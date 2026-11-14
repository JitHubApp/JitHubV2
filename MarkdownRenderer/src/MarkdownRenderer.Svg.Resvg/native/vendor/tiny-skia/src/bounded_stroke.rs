// Copyright 2026 JitHub contributors.
// SPDX-License-Identifier: MIT

//! Request-bounded stroke drawing. The fixed-point hairline scanner uses only
//! stack scratch; transformed paths and shader pipelines still need admission.

use crate::bounded_fill::path_error;
use crate::geom::{IntSizeExt, ScreenIntRect};
use crate::painter::{is_too_big_for_math, DrawTiler};
use crate::pipeline::RasterPipelineBlitter;
use crate::{
    AnalyticRasterError, FillRule, GradientStop, LineCap, Mask, Paint, Path, PathStroker,
    PixmapMut, Shader, Stroke, Transform,
};

impl PixmapMut<'_> {
    /// Draws a stroke while admitting dashing, stroke expansion, transformed
    /// geometry and scan storage against one request-local byte ceiling.
    ///
    /// The source path, caller-owned paint and destination are borrowed, not
    /// included in this limit. The caller must already account for those.
    /// Any error invalidates the complete output, including preceding tiles.
    pub fn stroke_path_bounded(
        &mut self,
        path: &Path,
        paint: &Paint,
        stroke: &Stroke,
        transform: Transform,
        mask: Option<&Mask>,
        max_bytes: usize,
    ) -> Result<(), AnalyticRasterError> {
        if !transform.is_finite() || !stroke.width.is_finite() || stroke.width < 0.0 {
            return Err(AnalyticRasterError::InvalidCoordinate);
        }
        if mask.is_some_and(|mask| mask.width() != self.width() || mask.height() != self.height()) {
            return Err(AnalyticRasterError::InvalidClip);
        }
        let scale = PathStroker::compute_resolution_scale(&transform);
        if let Some(coverage) = crate::painter::treat_as_hairline(paint, stroke, transform) {
            let dashed;
            let mut remaining = max_bytes;
            let path = if let Some(dash) = &stroke.dash {
                let Some(value) = path
                    .dash_bounded(dash, scale, remaining)
                    .map_err(path_error)?
                else {
                    return Ok(());
                };
                dashed = value;
                remaining = remaining
                    .checked_sub(dashed.retained_bytes())
                    .ok_or(AnalyticRasterError::ActiveWorkLimit)?;
                dashed.as_path()
            } else {
                path
            };
            return self.stroke_hairline_bounded(
                path,
                paint,
                stroke.line_cap,
                coverage,
                transform,
                mask,
                remaining,
            );
        }
        let Some(outline) = path
            .stroke_bounded(stroke, scale, max_bytes)
            .map_err(path_error)?
        else {
            return Ok(());
        };
        // Expansion scratch has retired, but the completed outline remains
        // live throughout scan conversion and must not be lent out again.
        let remaining = max_bytes
            .checked_sub(outline.retained_bytes())
            .ok_or(AnalyticRasterError::ActiveWorkLimit)?;
        if paint.anti_alias {
            self.fill_path_analytic_bounded(
                outline.as_path(),
                paint,
                FillRule::Winding,
                transform,
                mask,
                remaining,
            )
        } else {
            self.fill_path_non_aa_bounded(
                outline.as_path(),
                paint,
                FillRule::Winding,
                transform,
                mask,
                remaining,
            )
        }
    }

    pub(crate) fn stroke_hairline_bounded(
        &mut self,
        path: &Path,
        paint: &Paint,
        line_cap: LineCap,
        coverage: f32,
        transform: Transform,
        mask: Option<&Mask>,
        max_bytes: usize,
    ) -> Result<(), AnalyticRasterError> {
        if !transform.is_finite() || !coverage.is_finite() || !(0.0..=1.0).contains(&coverage) {
            return Err(AnalyticRasterError::InvalidCoordinate);
        }
        if mask.is_some_and(|mask| mask.width() != self.width() || mask.height() != self.height()) {
            return Err(AnalyticRasterError::InvalidClip);
        }
        if let Some(tiles) = DrawTiler::new(self.width(), self.height()) {
            for tile in tiles {
                self.stroke_hairline_tile(
                    path, paint, line_cap, coverage, transform, mask, tile, max_bytes,
                )?;
            }
        } else {
            let tile = self.size().to_screen_int_rect(0, 0);
            self.stroke_hairline_tile(
                path, paint, line_cap, coverage, transform, mask, tile, max_bytes,
            )?;
        }
        Ok(())
    }

    fn stroke_hairline_tile(
        &mut self,
        path: &Path,
        paint: &Paint,
        line_cap: LineCap,
        coverage: f32,
        transform: Transform,
        mask: Option<&Mask>,
        tile: ScreenIntRect,
        max_bytes: usize,
    ) -> Result<(), AnalyticRasterError> {
        // Transform the original for each tile, never translate the preceding
        // tile back and forth (which accumulates coordinate rounding drift).
        let transform =
            Transform::from_translate(-(tile.x() as f32), -(tile.y() as f32)).pre_concat(transform);
        let adjust_opacity = coverage != 1.0 && paint.blend_mode.should_pre_scale_coverage();
        let clone_shader = !transform.is_identity() || adjust_opacity;
        let allowance = paint.shader.analytic_heap_allowance(clone_shader);
        let remaining = max_bytes
            .checked_sub(allowance)
            .ok_or(AnalyticRasterError::ActiveWorkLimit)?;
        let transformed_path;
        let path = if !transform.is_identity() {
            transformed_path = path
                .transform_bounded(transform, remaining)
                .map_err(path_error)?;
            transformed_path.as_path()
        } else {
            path
        };
        if is_too_big_for_math(path) {
            return Err(AnalyticRasterError::NumericRange);
        }
        let transformed_paint = if clone_shader {
            let mut copy = paint.clone();
            if adjust_opacity {
                // Preserve the renderer's established subpixel-stroke opacity.
                let scale = (coverage * 256.0) as i32;
                let new_alpha = (255 * scale) >> 8;
                copy.shader.apply_opacity(new_alpha as f32 / 255.0);
            }
            copy.shader.transform(transform);
            Some(copy)
        } else {
            None
        };
        let active_paint = transformed_paint.as_ref().unwrap_or(paint);
        let clone_bytes = if clone_shader {
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
        let mut subpix = self
            .subpixmap(tile.to_int_rect())
            .ok_or(AnalyticRasterError::InvalidClip)?;
        let root_clip = subpix.root_clip;
        let submask = mask.and_then(|mask| mask.submask(tile.to_int_rect()));
        let Some(mut blitter) = RasterPipelineBlitter::new(active_paint, submask, &mut subpix)
        else {
            return Ok(());
        };
        if blitter.analytic_heap_bytes() > allowance.saturating_sub(clone_bytes) {
            return Err(AnalyticRasterError::ActiveWorkLimit);
        }
        // These converters perform bounded subdivision in fixed-size stack
        // arrays. No extra path, full-surface mask, or heap scanline is created.
        let scanner = if paint.anti_alias {
            crate::scan::hairline_aa::stroke_path
        } else {
            crate::scan::hairline::stroke_path
        };
        if let Some(root_clip) = root_clip {
            let mut clipped = crate::blitter::FractionalClipBlitter::new(&mut blitter, root_clip);
            scanner(path, line_cap, &clip, &mut clipped);
        } else {
            scanner(path, line_cap, &clip, &mut blitter);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{PathBuilder, Pixmap, Stroke};

    #[test]
    fn bounded_hairlines_preserve_caps_curves_transforms_and_fractional_clips() {
        for aa in [false, true] {
            for width in [0.0, 0.375, 0.875] {
                for cap in [LineCap::Butt, LineCap::Round, LineCap::Square] {
                    for transform in [
                        Transform::identity(),
                        Transform::from_translate(0.25, -0.125),
                    ] {
                        let mut pb = PathBuilder::new();
                        pb.move_to(1.125, 2.5);
                        pb.cubic_to(12.0, -4.0, 27.5, 39.0, 30.5, 9.875);
                        pb.line_to(7.0, 26.0);
                        let path = pb.finish().unwrap();
                        let paint = Paint {
                            anti_alias: aa,
                            ..Paint::default()
                        };
                        let stroke = Stroke {
                            width,
                            line_cap: cap,
                            ..Stroke::default()
                        };
                        let Some(coverage) =
                            crate::painter::treat_as_hairline(&paint, &stroke, transform)
                        else {
                            continue;
                        };
                        let mut expected = Pixmap::new(32, 32).unwrap();
                        let mut actual = Pixmap::new(32, 32).unwrap();
                        {
                            let mut expected = expected.as_mut();
                            let mut actual = actual.as_mut();
                            expected.set_root_clip([0.375, 0.25, 30.5, 30.75]);
                            actual.set_root_clip([0.375, 0.25, 30.5, 30.75]);
                            expected.stroke_path(&path, &paint, &stroke, transform, None);
                            actual
                                .stroke_hairline_bounded(
                                    &path,
                                    &paint,
                                    cap,
                                    coverage,
                                    transform,
                                    None,
                                    1024 * 1024,
                                )
                                .unwrap();
                        }
                        assert_eq!(
                            actual.data(),
                            expected.data(),
                            "aa={aa} width={width} cap={cap:?} transform={transform:?}"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn bounded_hairline_rejects_transform_allocation_before_output() {
        let mut pb = PathBuilder::new();
        pb.move_to(1.0, 1.0);
        pb.line_to(6.0, 6.0);
        let path = pb.finish().unwrap();
        let mut output = Pixmap::new(8, 8).unwrap();
        assert_eq!(
            output.as_mut().stroke_hairline_bounded(
                &path,
                &Paint::default(),
                LineCap::Butt,
                1.0,
                Transform::from_translate(0.25, 0.0),
                None,
                0
            ),
            Err(AnalyticRasterError::ActiveWorkLimit)
        );
        assert!(output.data().iter().all(|value| *value == 0));
    }

    #[test]
    fn bounded_hairlines_preserve_large_tile_boundaries_and_gradient_masks() {
        for (width, height) in [(16400, 4), (4, 16400), (32, 32)] {
            let mut pb = PathBuilder::new();
            pb.move_to(0.0, 0.0);
            pb.line_to(width as f32, height as f32);
            let path = pb.finish().unwrap();
            let stops = (0..24)
                .map(|i| {
                    crate::GradientStop::new(
                        i as f32 / 23.0,
                        crate::Color::from_rgba8(i * 9, 190, 75, 180),
                    )
                })
                .collect();
            let paint = Paint {
                shader: crate::LinearGradient::new(
                    (0.0, 0.0).into(),
                    (width as f32, height as f32).into(),
                    stops,
                    crate::SpreadMode::Pad,
                    Transform::identity(),
                )
                .unwrap(),
                ..Paint::default()
            };
            let stroke = Stroke {
                width: 0.5,
                ..Stroke::default()
            };
            let transform = Transform::from_translate(0.25, 0.125);
            let coverage = crate::painter::treat_as_hairline(&paint, &stroke, transform).unwrap();
            let mut mask = Mask::new(width, height).unwrap();
            mask.data_mut().fill(137);
            let mut expected = Pixmap::new(width, height).unwrap();
            let mut actual = Pixmap::new(width, height).unwrap();
            expected.stroke_path(&path, &paint, &stroke, transform, Some(&mask));
            actual
                .as_mut()
                .stroke_hairline_bounded(
                    &path,
                    &paint,
                    stroke.line_cap,
                    coverage,
                    transform,
                    Some(&mask),
                    1024 * 1024,
                )
                .unwrap();
            assert_eq!(actual.data(), expected.data(), "size={width}x{height}");
        }
    }

    #[test]
    fn all_bounded_drawing_rejects_mismatched_masks_without_writing_pixels() {
        let path = PathBuilder::from_rect(crate::Rect::from_xywh(1.0, 1.0, 4.0, 4.0).unwrap());
        let mask = Mask::new(4, 4).unwrap();
        let mut output = Pixmap::new(8, 8).unwrap();
        let mut target = output.as_mut();
        assert_eq!(
            target.fill_path_analytic_bounded(
                &path,
                &Paint::default(),
                FillRule::Winding,
                Transform::identity(),
                Some(&mask),
                1024 * 1024
            ),
            Err(AnalyticRasterError::InvalidClip)
        );
        assert_eq!(
            target.fill_path_non_aa_bounded(
                &path,
                &Paint::default(),
                FillRule::Winding,
                Transform::identity(),
                Some(&mask),
                1024 * 1024
            ),
            Err(AnalyticRasterError::InvalidClip)
        );
        assert_eq!(
            target.stroke_path_bounded(
                &path,
                &Paint::default(),
                &Stroke::default(),
                Transform::identity(),
                Some(&mask),
                1024 * 1024
            ),
            Err(AnalyticRasterError::InvalidClip)
        );
        assert!(output.data().iter().all(|value| *value == 0));
    }

    #[test]
    fn bounded_stroke_expansion_preserves_legacy_geometry_across_joins_and_dash_phases() {
        let mut seed = 0x95ad_3321u32;
        let mut coordinate = || {
            seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
            (seed >> 16) as f32 / 1024.0 - 10.0
        };
        for index in 0..64 {
            let mut pb = PathBuilder::new();
            pb.move_to(coordinate(), coordinate());
            pb.line_to(coordinate(), coordinate());
            pb.quad_to(coordinate(), coordinate(), coordinate(), coordinate());
            pb.cubic_to(
                coordinate(),
                coordinate(),
                coordinate(),
                coordinate(),
                coordinate(),
                coordinate(),
            );
            if index % 2 == 0 {
                pb.close();
            }
            let path = pb.finish().unwrap();
            for join in [
                crate::LineJoin::Miter,
                crate::LineJoin::MiterClip,
                crate::LineJoin::Round,
                crate::LineJoin::Bevel,
            ] {
                for phase in [None, Some(-3.25), Some(1.125)] {
                    let stroke = Stroke {
                        width: 3.375,
                        line_cap: [LineCap::Butt, LineCap::Round, LineCap::Square][index % 3],
                        line_join: join,
                        dash: phase.map(|phase| {
                            crate::StrokeDash::new(alloc::vec![2.5, 1.0], phase).unwrap()
                        }),
                        ..Stroke::default()
                    };
                    let dashed = stroke.dash.as_ref().and_then(|dash| path.dash(dash, 1.75));
                    let expected = if stroke.dash.is_some() && dashed.is_none() {
                        None
                    } else {
                        dashed.as_ref().unwrap_or(&path).stroke(&stroke, 1.75)
                    };
                    let actual = path.stroke_bounded(&stroke, 1.75, 8 * 1024 * 1024).unwrap();
                    assert_eq!(
                        actual.as_ref().map(|path| path.as_path()),
                        expected.as_ref(),
                        "index={index} join={join:?} phase={phase:?}"
                    );
                    if let Some(path) = actual {
                        assert!(path.retained_bytes() <= path.peak_bytes());
                        assert!(path.peak_bytes() <= 8 * 1024 * 1024);
                    }
                }
            }
        }
    }
}
