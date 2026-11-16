// Copyright 2026 JitHub contributors.
// SPDX-License-Identifier: MIT

//! Analytic alpha-mask drawing with the same request budget as RGBA drawing.

use crate::bounded_fill::replay_analytic_spans;
use crate::geom::IntSizeExt;
use crate::pipeline::RasterPipelineBlitter;
use crate::scan::path_analytic;
use crate::{AnalyticRasterError, FillRule, Mask, Path, Transform};

impl Mask {
    /// Analytically fills a transformed rectangle, including its temporary path
    /// storage in the same byte limit as the mask coverage operation.
    pub fn fill_rect_analytic_bounded(
        &mut self,
        rect: crate::Rect,
        transform: Transform,
        max_total_bytes: usize,
    ) -> Result<(), AnalyticRasterError> {
        let path = crate::PathBuilder::from_rect_bounded(rect, max_total_bytes)
            .map_err(crate::bounded_fill::path_error)?;
        let remaining = max_total_bytes
            .checked_sub(path.retained_bytes())
            .ok_or(AnalyticRasterError::ActiveWorkLimit)?;
        self.fill_path_analytic_bounded(path.as_path(), FillRule::Winding, transform, remaining)
    }

    /// Fills an alpha mask with analytically computed path coverage.
    ///
    /// The caller owns and accounts for the source path and mask pixels.
    /// All transformed/flattened geometry, scan state and private coverage
    /// commands fit within `max_total_bytes`. No path clone, RGBA intermediary
    /// or tile-sized mask is created. Validation and budget failures leave the
    /// mask unchanged; successful drawing composites over its existing data.
    pub fn fill_path_analytic_bounded(
        &mut self,
        path: &Path,
        fill_rule: FillRule,
        transform: Transform,
        max_total_bytes: usize,
    ) -> Result<(), AnalyticRasterError> {
        if !transform.is_finite() {
            return Err(AnalyticRasterError::InvalidCoordinate);
        }
        let clip = self.size().to_screen_int_rect(0, 0);
        let spans = path_analytic::rasterize_path(
            path,
            match fill_rule {
                FillRule::EvenOdd => path_analytic::FillRule::EvenOdd,
                FillRule::Winding => path_analytic::FillRule::NonZero,
            },
            path_analytic::ClipRect {
                left: 0.0,
                top: 0.0,
                right: f64::from(clip.right()),
                bottom: f64::from(clip.bottom()),
            },
            transform,
            [0.0, 0.0],
            max_total_bytes,
        )?;
        let mut destination = self.as_subpixmap();
        let mut blitter = RasterPipelineBlitter::new_mask(&mut destination)
            .ok_or(AnalyticRasterError::InvalidClip)?;
        // This fixed white-to-A8 pipeline uses inline stages and no shader
        // storage. Keep that invariant explicit if its implementation changes.
        if blitter.analytic_heap_bytes() != 0 {
            return Err(AnalyticRasterError::ActiveWorkLimit);
        }
        replay_analytic_spans(&spans, clip, &mut blitter)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Color, Paint, PathBuilder, Pixmap, Rect};

    #[test]
    fn analytic_mask_matches_rgba_coverage_for_curves_transforms_and_fill_rules() {
        let mut builder = PathBuilder::new();
        builder.push_circle(13.0, 14.0, 10.25);
        builder.push_circle(15.0, 14.0, 5.5);
        let path = builder.finish().unwrap();
        for rule in [FillRule::Winding, FillRule::EvenOdd] {
            for transform in [
                Transform::identity(),
                Transform::from_row(1.2, 0.15, -0.1, 0.8, 3.0, 4.0),
                Transform::from_row(-1.0, 0.0, 0.0, 1.0, 28.0, -0.25),
            ] {
                for previous in [0, 37, 128, 255] {
                    let mut mask = Mask::new(40, 40).unwrap();
                    mask.data_mut().fill(previous);
                    let mut rgba = Pixmap::new(40, 40).unwrap();
                    rgba.fill(Color::from_rgba8(255, 255, 255, previous));
                    let mut paint = Paint::default();
                    paint.set_color(Color::WHITE);
                    mask.fill_path_analytic_bounded(&path, rule, transform, 1024 * 1024)
                        .unwrap();
                    rgba.as_mut()
                        .fill_path_analytic_bounded(
                            &path,
                            &paint,
                            rule,
                            transform,
                            None,
                            1024 * 1024,
                        )
                        .unwrap();
                    for (alpha, pixel) in mask.data().iter().zip(rgba.pixels()) {
                        assert_eq!(*alpha, pixel.alpha(), "{rule:?} {transform:?} {previous}");
                    }
                }
            }
        }
    }

    #[test]
    fn analytic_mask_preserves_long_fractional_boundaries_without_tile_cloning() {
        for (width, height) in [(400, 20), (16400, 3), (3, 16400)] {
            let path = PathBuilder::from_rect(
                Rect::from_xywh(0.5, 0.5, width as f32 - 1.0, height as f32 - 1.0).unwrap(),
            );
            let mut mask = Mask::new(width, height).unwrap();
            mask.fill_path_analytic_bounded(
                &path,
                FillRule::Winding,
                Transform::identity(),
                4 * 1024 * 1024,
            )
            .unwrap();
            for y in 0..height {
                for x in 0..width {
                    let edges =
                        u32::from(x == 0 || x == width - 1) + u32::from(y == 0 || y == height - 1);
                    let expected = match edges {
                        0 => 255,
                        1 => 128,
                        _ => 64,
                    };
                    assert_eq!(
                        mask.data()[(y * width + x) as usize],
                        expected,
                        "{width}x{height} ({x},{y})"
                    );
                }
            }
        }
    }

    #[test]
    fn analytic_mask_rejection_is_transactional() {
        let path = PathBuilder::from_rect(Rect::from_xywh(1.0, 1.0, 5.0, 5.0).unwrap());
        for (transform, budget, expected) in [
            (
                Transform::identity(),
                0,
                AnalyticRasterError::ActiveWorkLimit,
            ),
            (
                Transform::from_translate(0.5, 0.5),
                0,
                AnalyticRasterError::ActiveWorkLimit,
            ),
            (
                Transform::from_translate(f32::INFINITY, 0.0),
                1024 * 1024,
                AnalyticRasterError::InvalidCoordinate,
            ),
        ] {
            let mut mask = Mask::new(8, 8).unwrap();
            mask.data_mut().fill(37);
            assert_eq!(
                mask.fill_path_analytic_bounded(&path, FillRule::Winding, transform, budget),
                Err(expected)
            );
            assert!(mask.data().iter().all(|value| *value == 37));
        }
    }

    #[test]
    fn analytic_mask_rectangle_admits_geometry_and_scan_storage_together() {
        let rect = Rect::from_xywh(0.5, 0.5, 6.0, 6.0).unwrap();
        let path = PathBuilder::from_rect_bounded(rect, 1024).unwrap();
        let transform = Transform::from_translate(0.25, 0.25);
        let mut mask = Mask::new(8, 8).unwrap();
        mask.data_mut().fill(37);
        for budget in [0, path.peak_bytes()] {
            assert_eq!(
                mask.fill_rect_analytic_bounded(rect, transform, budget),
                Err(AnalyticRasterError::ActiveWorkLimit)
            );
            assert!(mask.data().iter().all(|value| *value == 37));
        }
        let mut expected = mask.clone();
        expected
            .fill_path_analytic_bounded(path.as_path(), FillRule::Winding, transform, 1024 * 1024)
            .unwrap();
        mask.fill_rect_analytic_bounded(rect, transform, 1024 * 1024)
            .unwrap();
        assert_eq!(mask.data(), expected.data());
    }
}
