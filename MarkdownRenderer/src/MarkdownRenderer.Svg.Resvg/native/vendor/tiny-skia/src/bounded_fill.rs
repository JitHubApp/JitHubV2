// Copyright 2026 JitHub contributors.
// SPDX-License-Identifier: MIT

//! Bounded adapter for the unchanged non-antialiased scan converter.

use crate::geom::{IntSizeExt, ScreenIntRect};
use crate::painter::{is_too_big_for_math, DrawTiler};
use crate::pipeline::RasterPipelineBlitter;
use crate::{
    AnalyticRasterError, FillRule, GradientStop, Mask, Paint, Path, PixmapMut, Shader, Transform,
};

/// Replays an already admitted analytic command list without allocating. Check
/// every command before the first blit so a scanner invariant failure cannot
/// partially modify either an RGBA destination or an alpha-only mask.
pub(crate) fn replay_analytic_spans(
    spans: &[crate::scan::path_analytic::CoverageSpan],
    clip: ScreenIntRect,
    blitter: &mut dyn crate::blitter::Blitter,
) -> Result<(), AnalyticRasterError> {
    for span in spans {
        let right = i64::from(span.x) + i64::from(span.width);
        if i64::from(span.x) < i64::from(clip.left())
            || i64::from(span.y) < i64::from(clip.top())
            || span.width == 0
            || i64::from(span.y) >= i64::from(clip.bottom())
            || right > i64::from(clip.right())
        {
            return Err(AnalyticRasterError::NumericRange);
        }
    }

    for span in spans {
        if span.alpha == 0 {
            continue;
        }
        let x = span.x as u32;
        let y = span.y as u32;
        let width = crate::LengthU32::new(span.width).unwrap();
        if span.alpha == crate::ALPHA_U8_OPAQUE {
            blitter.blit_h(x, y, width);
        } else {
            // The inline alpha-mask context carries only two pixels. RLE
            // forwarding preserves longer partially covered boundary spans.
            const RUN_CHUNK: u32 = 100;
            let mut alpha_run = [span.alpha; RUN_CHUNK as usize];
            let mut runs = [None; RUN_CHUNK as usize + 1];
            let mut start = x;
            let mut remaining = span.width;
            while remaining != 0 {
                let count = remaining.min(RUN_CHUNK);
                alpha_run.fill(span.alpha);
                runs.fill(None);
                runs[0] = core::num::NonZeroU16::new(count as u16);
                blitter.blit_anti_h(start, y, &mut alpha_run, &mut runs);
                start += count;
                remaining -= count;
            }
        }
    }
    Ok(())
}

pub(crate) fn path_error(error: tiny_skia_path::PathAllocationError) -> AnalyticRasterError {
    use tiny_skia_path::PathAllocationError;
    match error {
        PathAllocationError::AllocationFailed => AnalyticRasterError::AllocationFailure,
        PathAllocationError::InvalidGeometry => AnalyticRasterError::InvalidCoordinate,
        PathAllocationError::CapacityOverflow => AnalyticRasterError::NumericRange,
        PathAllocationError::BudgetExceeded | PathAllocationError::ComplexityLimit => {
            AnalyticRasterError::ActiveWorkLimit
        }
    }
}

impl PixmapMut<'_> {
    /// Fills a path without antialiasing, admitting transformed geometry,
    /// shader/pipeline storage and scan edges against one byte limit.
    ///
    /// Pixel rounding and edge ordering match the legacy non-AA scanner.
    /// Large destinations use its normal fixed-point-safe tiling. A failure
    /// invalidates the destination: a preceding tile may already be rendered,
    /// so callers must discard the complete output instead of publishing it.
    pub fn fill_path_non_aa_bounded(
        &mut self,
        path: &Path,
        paint: &Paint,
        fill_rule: FillRule,
        transform: Transform,
        mask: Option<&Mask>,
        max_total_bytes: usize,
    ) -> Result<(), AnalyticRasterError> {
        if !transform.is_finite() {
            return Err(AnalyticRasterError::InvalidCoordinate);
        }
        if mask.is_some_and(|mask| mask.width() != self.width() || mask.height() != self.height()) {
            return Err(AnalyticRasterError::InvalidClip);
        }
        if let Some(tiles) = DrawTiler::new(self.width(), self.height()) {
            for tile in tiles {
                self.fill_non_aa_tile(
                    path,
                    paint,
                    fill_rule,
                    transform,
                    mask,
                    tile,
                    max_total_bytes,
                )?;
            }
        } else {
            let tile = self.size().to_screen_int_rect(0, 0);
            self.fill_non_aa_tile(
                path,
                paint,
                fill_rule,
                transform,
                mask,
                tile,
                max_total_bytes,
            )?;
        }
        Ok(())
    }

    fn fill_non_aa_tile(
        &mut self,
        path: &Path,
        paint: &Paint,
        fill_rule: FillRule,
        transform: Transform,
        mask: Option<&Mask>,
        tile: ScreenIntRect,
        max_bytes: usize,
    ) -> Result<(), AnalyticRasterError> {
        let transform =
            Transform::from_translate(-(tile.x() as f32), -(tile.y() as f32)).pre_concat(transform);
        let needs_transform = !transform.is_identity();
        let allowance = paint.shader.analytic_heap_allowance(needs_transform);
        let mut remaining = max_bytes
            .checked_sub(allowance)
            .ok_or(AnalyticRasterError::ActiveWorkLimit)?;
        let transformed_path;
        let path = if needs_transform {
            transformed_path = path
                .transform_bounded(transform, remaining)
                .map_err(path_error)?;
            remaining = remaining
                .checked_sub(transformed_path.retained_bytes())
                .ok_or(AnalyticRasterError::ActiveWorkLimit)?;
            transformed_path.as_path()
        } else {
            path
        };
        if is_too_big_for_math(path) {
            return Err(AnalyticRasterError::NumericRange);
        }
        if path.bounds().width() == 0.0 || path.bounds().height() == 0.0 {
            return Ok(());
        }
        let transformed_paint = if needs_transform {
            let mut copy = paint.clone();
            copy.shader.transform(transform);
            Some(copy)
        } else {
            None
        };
        let active_paint = transformed_paint.as_ref().unwrap_or(paint);
        let clone_bytes = if needs_transform {
            match &paint.shader {
                Shader::LinearGradient(g) => g.base.analytic_stop_count(),
                Shader::RadialGradient(g) => g.base.analytic_stop_count(),
                Shader::SweepGradient(g) => g.base.analytic_stop_count(),
                Shader::SolidColor(_) | Shader::Pattern(_) => 0,
            }
            .saturating_mul(core::mem::size_of::<GradientStop>())
        } else {
            0
        };
        let clip = tile.size().to_screen_int_rect(0, 0);
        let mut subpixmap = self
            .subpixmap(tile.to_int_rect())
            .ok_or(AnalyticRasterError::InvalidClip)?;
        let root_clip = subpixmap.root_clip;
        let submask = mask.and_then(|mask| mask.submask(tile.to_int_rect()));
        let Some(mut blitter) = RasterPipelineBlitter::new(active_paint, submask, &mut subpixmap)
        else {
            return Ok(());
        };
        if blitter.analytic_heap_bytes() > allowance.saturating_sub(clone_bytes) {
            return Err(AnalyticRasterError::ActiveWorkLimit);
        }
        if let Some(root_clip) = root_clip {
            let mut clipped = crate::blitter::FractionalClipBlitter::new(&mut blitter, root_clip);
            crate::scan::path::fill_path_bounded(path, fill_rule, &clip, &mut clipped, remaining)
        } else {
            crate::scan::path::fill_path_bounded(path, fill_rule, &clip, &mut blitter, remaining)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Color, PathBuilder, Pixmap, Rect};

    #[test]
    fn analytic_replay_validates_later_spans_before_writing_earlier_spans() {
        use crate::scan::path_analytic::CoverageSpan;
        for invalid in [
            CoverageSpan {
                x: -1,
                y: 0,
                width: 1,
                alpha: 255,
            },
            CoverageSpan {
                x: 0,
                y: -1,
                width: 1,
                alpha: 255,
            },
            CoverageSpan {
                x: 0,
                y: 8,
                width: 1,
                alpha: 255,
            },
            CoverageSpan {
                x: 0,
                y: 0,
                width: 0,
                alpha: 255,
            },
            CoverageSpan {
                x: 7,
                y: 0,
                width: u32::MAX,
                alpha: 255,
            },
        ] {
            let mut mask = Mask::new(8, 8).unwrap();
            mask.data_mut().fill(37);
            let clip = mask.size().to_screen_int_rect(0, 0);
            let mut subpix = mask.as_subpixmap();
            let mut blitter = RasterPipelineBlitter::new_mask(&mut subpix).unwrap();
            assert_eq!(
                replay_analytic_spans(
                    &[
                        CoverageSpan {
                            x: 0,
                            y: 0,
                            width: 8,
                            alpha: 255
                        },
                        invalid
                    ],
                    clip,
                    &mut blitter,
                ),
                Err(AnalyticRasterError::NumericRange),
            );
            assert!(mask.data().iter().all(|value| *value == 37));
        }
    }

    #[test]
    fn bounded_non_aa_adapter_matches_legacy_at_tile_and_fractional_clip_boundaries() {
        for (width, height) in [(32, 32), (16400, 3), (3, 16400)] {
            let path = PathBuilder::from_rect(
                Rect::from_xywh(0.25, 0.25, width as f32 - 0.75, height as f32 - 0.75).unwrap(),
            );
            for transform in [Transform::identity(), Transform::from_translate(0.2, -0.1)] {
                for clip in [
                    None,
                    Some([0.6, 0.6, width as f32 - 0.1, height as f32 - 0.1]),
                ] {
                    let mut legacy = Pixmap::new(width, height).unwrap();
                    let mut bounded = Pixmap::new(width, height).unwrap();
                    let mut old = legacy.as_mut();
                    let mut new = bounded.as_mut();
                    if let Some(clip) = clip {
                        assert!(old.set_root_clip(clip));
                        assert!(new.set_root_clip(clip));
                    }
                    let paint = Paint {
                        anti_alias: false,
                        ..Paint::default()
                    };
                    old.fill_path(&path, &paint, FillRule::Winding, transform, None);
                    assert_eq!(
                        new.fill_path_non_aa_bounded(
                            &path,
                            &paint,
                            FillRule::Winding,
                            transform,
                            None,
                            1024 * 1024
                        ),
                        Ok(())
                    );
                    assert_eq!(
                        legacy.data(),
                        bounded.data(),
                        "{width}x{height}, {transform:?}, {clip:?}"
                    );
                }
            }
        }
    }

    #[test]
    fn non_aa_adapter_rejects_without_writing_first_tile_when_geometry_is_unadmitted() {
        let path = PathBuilder::from_rect(Rect::from_xywh(1.0, 1.0, 5.0, 5.0).unwrap());
        let mut output = Pixmap::new(8, 8).unwrap();
        output.fill(Color::TRANSPARENT);
        assert_eq!(
            output.as_mut().fill_path_non_aa_bounded(
                &path,
                &Paint::default(),
                FillRule::Winding,
                Transform::from_translate(0.5, 0.5),
                None,
                0
            ),
            Err(AnalyticRasterError::ActiveWorkLimit)
        );
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
    }

    #[test]
    fn non_aa_adapter_preserves_gradient_shader_transform_and_alpha_mask() {
        let path = PathBuilder::from_circle(10.0, 10.0, 8.0).unwrap();
        let mut stops = alloc::vec::Vec::new();
        for index in 0..24 {
            stops.push(GradientStop::new(
                index as f32 / 23.0,
                Color::from_rgba8(index * 10, 180, 90, 210),
            ));
        }
        let paint = Paint {
            shader: crate::LinearGradient::new(
                (0.0, 0.0).into(),
                (20.0, 20.0).into(),
                stops,
                crate::SpreadMode::Pad,
                Transform::identity(),
            )
            .unwrap(),
            anti_alias: false,
            ..Paint::default()
        };
        let mut mask = Mask::new(32, 32).unwrap();
        for (index, value) in mask.data_mut().iter_mut().enumerate() {
            *value = (index % 256) as u8;
        }
        let transform = Transform::from_row(1.2, 0.15, -0.1, 0.8, 3.0, 4.0);
        let mut legacy = Pixmap::new(32, 32).unwrap();
        let mut bounded = Pixmap::new(32, 32).unwrap();
        legacy.fill_path(&path, &paint, FillRule::Winding, transform, Some(&mask));
        assert_eq!(
            bounded.as_mut().fill_path_non_aa_bounded(
                &path,
                &paint,
                FillRule::Winding,
                transform,
                Some(&mask),
                1024 * 1024
            ),
            Ok(())
        );
        assert_eq!(bounded.data(), legacy.data());
        let mut rejected = Pixmap::new(32, 32).unwrap();
        assert_eq!(
            rejected.as_mut().fill_path_non_aa_bounded(
                &path,
                &paint,
                FillRule::Winding,
                transform,
                Some(&mask),
                0
            ),
            Err(AnalyticRasterError::ActiveWorkLimit)
        );
        assert!(rejected.pixels().iter().all(|pixel| pixel.alpha() == 0));
    }
}
