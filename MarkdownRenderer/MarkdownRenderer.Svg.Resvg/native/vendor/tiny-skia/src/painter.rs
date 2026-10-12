// Copyright 2006 The Android Open Source Project
// Copyright 2020 Yevhenii Reizner
//
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

use crate::*;

use crate::blitter::Blitter;
use tiny_skia_path::{PathStroker, Scalar, SCALAR_MAX};

use crate::geom::ScreenIntRect;
use crate::mask::SubMaskRef;
use crate::pipeline::{RasterPipelineBlitter, RasterPipelineBuilder};
use crate::pixmap::SubPixmapMut;
use crate::scan;

use crate::geom::IntSizeExt;
#[cfg(all(not(feature = "std"), feature = "no-std-float"))]
use tiny_skia_path::NoStdFloat;

fn fill_path_with_root_clip(
    path: &Path,
    fill_rule: FillRule,
    clip: &ScreenIntRect,
    blitter: &mut dyn crate::blitter::Blitter,
    root_clip: Option<[f32; 4]>,
    anti_alias: bool,
    exact_stroke_rectangle: bool,
) {
    if anti_alias {
        if exact_stroke_rectangle {
            scan::path_aa::fill_stroke_outline_with_root_clip(
                path, fill_rule, clip, blitter, root_clip,
            );
        } else if let Some(root_clip) = root_clip {
            scan::path_aa::fill_path_with_root_clip(
                path,
                fill_rule,
                clip,
                blitter,
                Some(root_clip),
            );
        } else {
            scan::path_aa::fill_path(path, fill_rule, clip, blitter);
        }
    } else if let Some(root_clip) = root_clip {
        let mut clipped = crate::blitter::FractionalClipBlitter::new(blitter, root_clip);
        scan::path::fill_path(path, fill_rule, clip, &mut clipped);
    } else {
        scan::path::fill_path(path, fill_rule, clip, blitter);
    }
}

/// A path filling rule.
#[derive(Copy, Clone, Default, PartialEq, Debug)]
pub enum FillRule {
    /// Specifies that "inside" is computed by a non-zero sum of signed edge crossings.
    #[default]
    Winding,
    /// Specifies that "inside" is computed by an odd number of edge crossings.
    EvenOdd,
}

/// Controls how a shape should be painted.
#[derive(Clone, PartialEq, Debug)]
pub struct Paint<'a> {
    /// A paint shader.
    ///
    /// Default: black color
    pub shader: Shader<'a>,

    /// Paint blending mode.
    ///
    /// Default: SourceOver
    pub blend_mode: BlendMode,

    /// Enables anti-aliased painting.
    ///
    /// Default: true
    pub anti_alias: bool,

    /// Colorspace for blending.
    ///
    /// This enables gamma correction during the blend operation.  While skia supports
    /// full color-space conversions, we only support a few (simple) cases.  Note that
    /// any color space other than Linear will force using the high-quality pipeline.
    ///
    /// Default: Linear
    pub colorspace: ColorSpace,

    /// Forces the high quality/precision rendering pipeline.
    ///
    /// `tiny-skia`, just like Skia, has two rendering pipelines:
    /// one uses `f32` and another one uses `u16`. `u16` one is usually way faster,
    /// but less precise. Which can lead to slight differences.
    ///
    /// By default, `tiny-skia` will choose the pipeline automatically,
    /// depending on a blending mode and other parameters.
    /// But you can force the high quality one using this flag.
    ///
    /// This feature is especially useful during testing.
    ///
    /// Unlike high quality pipeline, the low quality one doesn't support all
    /// rendering stages, therefore we cannot force it like hq one.
    ///
    /// Default: false
    pub force_hq_pipeline: bool,
}

impl Default for Paint<'_> {
    fn default() -> Self {
        Paint {
            shader: Shader::SolidColor(Color::BLACK),
            blend_mode: BlendMode::default(),
            anti_alias: true,
            colorspace: ColorSpace::default(),
            force_hq_pipeline: false,
        }
    }
}

impl Paint<'_> {
    /// Sets a paint source to a solid color.
    pub fn set_color(&mut self, color: Color) {
        self.shader = Shader::SolidColor(color);
    }

    /// Sets a paint source to a solid color.
    ///
    /// `self.shader = Shader::SolidColor(Color::from_rgba8(50, 127, 150, 200));` shorthand.
    pub fn set_color_rgba8(&mut self, r: u8, g: u8, b: u8, a: u8) {
        self.set_color(Color::from_rgba8(r, g, b, a))
    }

    /// Checks that the paint source is a solid color.
    pub fn is_solid_color(&self) -> bool {
        matches!(self.shader, Shader::SolidColor(_))
    }
}

impl Pixmap {
    /// Draws a filled rectangle onto the pixmap.
    ///
    /// See [`PixmapMut::fill_rect`](struct.PixmapMut.html#method.fill_rect) for details.
    pub fn fill_rect(
        &mut self,
        rect: Rect,
        paint: &Paint,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        self.as_mut().fill_rect(rect, paint, transform, mask);
    }

    /// Draws a filled path onto the pixmap.
    ///
    /// See [`PixmapMut::fill_path`](struct.PixmapMut.html#method.fill_path) for details.
    pub fn fill_path(
        &mut self,
        path: &Path,
        paint: &Paint,
        fill_rule: FillRule,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        self.as_mut()
            .fill_path(path, paint, fill_rule, transform, mask);
    }

    /// Strokes a path.
    ///
    /// See [`PixmapMut::stroke_path`](struct.PixmapMut.html#method.stroke_path) for details.
    pub fn stroke_path(
        &mut self,
        path: &Path,
        paint: &Paint,
        stroke: &Stroke,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        self.as_mut()
            .stroke_path(path, paint, stroke, transform, mask);
    }

    /// Draws a `Pixmap` on top of the current `Pixmap`.
    ///
    /// See [`PixmapMut::draw_pixmap`](struct.PixmapMut.html#method.draw_pixmap) for details.
    pub fn draw_pixmap(
        &mut self,
        x: i32,
        y: i32,
        pixmap: PixmapRef,
        paint: &PixmapPaint,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        self.as_mut()
            .draw_pixmap(x, y, pixmap, paint, transform, mask);
    }

    /// Applies a masks.
    ///
    /// See [`PixmapMut::apply_mask`](struct.PixmapMut.html#method.apply_mask) for details.
    pub fn apply_mask(&mut self, mask: &Mask) {
        self.as_mut().apply_mask(mask);
    }
}

impl PixmapMut<'_> {
    /// Returns the inclusive source-row range touched by a bicubic sample for
    /// one row of a scale-transformed draw.
    ///
    /// The calculation intentionally mirrors the pipeline's f32 inverse
    /// transform, four successive `+ 1.0` tap positions, and Pad clamp. A row
    /// streaming caller can use this to keep every required source row in its
    /// ring before invoking `draw_pixmap_row_from_ring`.
    pub fn required_bicubic_source_y_range(
        source_height: u32,
        row_y: u32,
        target_height: u32,
        transform: Transform,
    ) -> Option<(u32, u32)> {
        if source_height == 0
            || target_height == 0
            || row_y >= target_height
            || transform.sx <= 0.0
            || transform.sy <= 0.0
            || transform.kx != 0.0
            || transform.ky != 0.0
            || transform.tx != 0.0
            || transform.ty != 0.0
            || !transform.is_finite()
        {
            return None;
        }

        let inverse = transform.invert()?;
        let center_y = (row_y as f32 + 0.5) * inverse.sy;
        if !center_y.is_finite() {
            return None;
        }

        // This is the same exclusive-to-inclusive conversion used by
        // highp::gather_ix. Source dimensions are bounded well below f32's
        // integer precision limit by Pixmap construction.
        let maximum_y = f32::from_bits((source_height as f32).to_bits().checked_sub(1)?);
        let mut sample_y = center_y + -1.5;
        let mut minimum = u32::MAX;
        let mut maximum = 0;
        for _ in 0..4 {
            let clamped = sample_y.max(0.0).min(maximum_y);
            let index = clamped.trunc() as u32;
            minimum = minimum.min(index);
            maximum = maximum.max(index);
            sample_y += 1.0;
        }
        Some((minimum, maximum))
    }

    // TODO: accept NonZeroRect?
    /// Draws a filled rectangle onto the pixmap.
    ///
    /// This function is usually slower than filling a rectangular path,
    /// but it produces better results. Mainly it doesn't suffer from weird
    /// clipping of horizontal/vertical edges.
    ///
    /// Used mainly to render a pixmap onto a pixmap.
    pub fn fill_rect(
        &mut self,
        rect: Rect,
        paint: &Paint,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        if transform.is_identity() && paint.anti_alias && self.root_clip().is_some() {
            let path = PathBuilder::from_rect(rect);
            self.fill_path(&path, paint, FillRule::Winding, transform, mask);
            return;
        }

        // TODO: we probably can use tiler for rect too
        if transform.is_identity() && !DrawTiler::required(self.width(), self.height()) {
            // TODO: ignore rects outside the pixmap

            let clip = self.size().to_screen_int_rect(0, 0);

            let mask = mask.map(|mask| mask.as_submask());
            let mut subpix = self.as_subpixmap();
            let root_clip = subpix.root_clip;
            let mut blitter = match RasterPipelineBlitter::new(paint, mask, &mut subpix) {
                Some(v) => v,
                None => return, // nothing to do, all good
            };

            if paint.anti_alias {
                scan::fill_rect_aa(&rect, &clip, &mut blitter);
            } else if let Some(root_clip) = root_clip {
                let mut clipped =
                    crate::blitter::FractionalClipBlitter::new(&mut blitter, root_clip);
                scan::fill_rect(&rect, &clip, &mut clipped);
            } else {
                scan::fill_rect(&rect, &clip, &mut blitter);
            }
        } else {
            let path = PathBuilder::from_rect(rect);
            self.fill_path(&path, paint, FillRule::Winding, transform, mask);
        }
    }

    /// Draws a filled path onto the pixmap.
    pub fn fill_path(
        &mut self,
        path: &Path,
        paint: &Paint,
        fill_rule: FillRule,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        self.fill_path_impl(path, paint, fill_rule, transform, mask, false);
    }

    /// Analytically fills a path using exact area coverage subject to a
    /// request-local byte ceiling. Unlike [`fill_path`](Self::fill_path),
    /// this method is fallible and never falls back to the legacy scanner.
    /// The destination is untouched if geometry validation or budget
    /// admission fails.
    pub fn fill_path_analytic_bounded(
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
        let clone_shader = !transform.is_identity();
        let allowance = paint.shader.analytic_heap_allowance(clone_shader);
        let scan_budget = max_total_bytes
            .checked_sub(allowance)
            .ok_or(AnalyticRasterError::ActiveWorkLimit)?;

        let transformed_paint = if clone_shader {
            let mut cloned = paint.clone();
            cloned.shader.transform(transform);
            Some(cloned)
        } else {
            None
        };
        let active_paint = transformed_paint.as_ref().unwrap_or(paint);
        let clip = self.size().to_screen_int_rect(0, 0);
        let mask = mask.map(|mask| mask.as_submask());
        let mut subpix = self.as_subpixmap();
        let root_clip = subpix.root_clip;
        let mut blitter = match RasterPipelineBlitter::new(active_paint, mask, &mut subpix) {
            Some(blitter) => blitter,
            None => return Ok(()),
        };

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
        let pipeline_allowance = allowance.saturating_sub(clone_bytes);
        if blitter.analytic_heap_bytes() > pipeline_allowance {
            return Err(AnalyticRasterError::ActiveWorkLimit);
        }

        let mut analytic_clip = scan::path_analytic::ClipRect {
            left: f64::from(clip.left()),
            top: f64::from(clip.top()),
            right: f64::from(clip.right()),
            bottom: f64::from(clip.bottom()),
        };
        if let Some(root_clip) = root_clip {
            if root_clip.iter().any(|value| !value.is_finite())
                || root_clip[0] > root_clip[2]
                || root_clip[1] > root_clip[3]
            {
                return Err(AnalyticRasterError::InvalidClip);
            }
            analytic_clip.left = analytic_clip.left.max(f64::from(root_clip[0]));
            analytic_clip.top = analytic_clip.top.max(f64::from(root_clip[1]));
            analytic_clip.right = analytic_clip.right.min(f64::from(root_clip[2]));
            analytic_clip.bottom = analytic_clip.bottom.min(f64::from(root_clip[3]));
        }

        let analytic_rule = match fill_rule {
            FillRule::EvenOdd => scan::path_analytic::FillRule::EvenOdd,
            FillRule::Winding => scan::path_analytic::FillRule::NonZero,
        };
        let spans = scan::path_analytic::rasterize_path(
            path,
            analytic_rule,
            analytic_clip,
            transform,
            [0.0, 0.0],
            scan_budget,
        )
        .map_err(AnalyticRasterError::from)?;

        crate::bounded_fill::replay_analytic_spans(&spans, clip, &mut blitter)
    }

    fn fill_stroke_outline(
        &mut self,
        path: &Path,
        paint: &Paint,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        self.fill_path_impl(path, paint, FillRule::Winding, transform, mask, true);
    }

    fn fill_path_impl(
        &mut self,
        path: &Path,
        paint: &Paint,
        fill_rule: FillRule,
        transform: Transform,
        mask: Option<&Mask>,
        exact_stroke_rectangle: bool,
    ) {
        if transform.is_identity() {
            // This is sort of similar to SkDraw::drawPath

            // Skip empty paths and horizontal/vertical lines.
            let path_bounds = path.bounds();
            if path_bounds.width().is_nearly_zero() || path_bounds.height().is_nearly_zero() {
                log::warn!("empty paths and horizontal/vertical lines cannot be filled");
                return;
            }

            if is_too_big_for_math(path) {
                log::warn!("path coordinates are too big");
                return;
            }

            // TODO: ignore paths outside the pixmap

            if let Some(tiler) = DrawTiler::new(self.width(), self.height()) {
                let mut path = path.clone(); // TODO: avoid cloning
                let mut paint = paint.clone();

                for tile in tiler {
                    let ts = Transform::from_translate(-(tile.x() as f32), -(tile.y() as f32));
                    path = match path.transform(ts) {
                        Some(v) => v,
                        None => {
                            log::warn!("path transformation failed");
                            return;
                        }
                    };
                    paint.shader.transform(ts);

                    let clip_rect = tile.size().to_screen_int_rect(0, 0);
                    let mut subpix = match self.subpixmap(tile.to_int_rect()) {
                        Some(v) => v,
                        None => continue, // technically unreachable
                    };

                    let submask = mask.and_then(|mask| mask.submask(tile.to_int_rect()));
                    let root_clip = subpix.root_clip;
                    let mut blitter = match RasterPipelineBlitter::new(&paint, submask, &mut subpix)
                    {
                        Some(v) => v,
                        None => continue, // nothing to do, all good
                    };

                    // We're ignoring "errors" here, because `fill_path` will return `None`
                    // when rendering a tile that doesn't have a path on it.
                    // Which is not an error in this case.
                    fill_path_with_root_clip(
                        &path,
                        fill_rule,
                        &clip_rect,
                        &mut blitter,
                        root_clip,
                        paint.anti_alias,
                        exact_stroke_rectangle,
                    );

                    let ts = Transform::from_translate(tile.x() as f32, tile.y() as f32);
                    path = match path.transform(ts) {
                        Some(v) => v,
                        None => return, // technically unreachable
                    };
                    paint.shader.transform(ts);
                }
            } else {
                let clip_rect = self.size().to_screen_int_rect(0, 0);
                let submask = mask.map(|mask| mask.as_submask());
                let mut subpix = self.as_subpixmap();
                let root_clip = subpix.root_clip;
                let mut blitter = match RasterPipelineBlitter::new(paint, submask, &mut subpix) {
                    Some(v) => v,
                    None => return, // nothing to do, all good
                };

                fill_path_with_root_clip(
                    path,
                    fill_rule,
                    &clip_rect,
                    &mut blitter,
                    root_clip,
                    paint.anti_alias,
                    exact_stroke_rectangle,
                );
            }
        } else {
            let path = match path.clone().transform(transform) {
                Some(v) => v,
                None => {
                    log::warn!("path transformation failed");
                    return;
                }
            };

            let mut paint = paint.clone();
            paint.shader.transform(transform);

            self.fill_path_impl(
                &path,
                &paint,
                fill_rule,
                Transform::identity(),
                mask,
                exact_stroke_rectangle,
            )
        }
    }

    /// Strokes a path.
    ///
    /// Stroking is implemented using two separate algorithms:
    ///
    /// 1. If a stroke width is wider than 1px (after applying the transformation),
    ///    a path will be converted into a stroked path and then filled using `fill_path`.
    ///    Which means that we have to allocate a separate `Path`, that can be 2-3x larger
    ///    then the original path.
    /// 2. If a stroke width is thinner than 1px (after applying the transformation),
    ///    we will use hairline stroking, which doesn't involve a separate path allocation.
    ///
    /// Also, if a `stroke` has a dash array, then path will be converted into
    /// a dashed path first and then stroked. Which means a yet another allocation.
    pub fn stroke_path(
        &mut self,
        path: &Path,
        paint: &Paint,
        stroke: &Stroke,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        if stroke.width < 0.0 {
            log::warn!("negative stroke width isn't allowed");
            return;
        }

        let res_scale = PathStroker::compute_resolution_scale(&transform);

        let dash_path;
        let path = if let Some(ref dash) = stroke.dash {
            dash_path = match path.dash(dash, res_scale) {
                Some(v) => v,
                None => {
                    log::warn!("path dashing failed");
                    return;
                }
            };
            &dash_path
        } else {
            path
        };

        if let Some(coverage) = treat_as_hairline(paint, stroke, transform) {
            let mut paint = paint.clone();
            if coverage == 1.0 {
                // No changes to the `paint`.
            } else if paint.blend_mode.should_pre_scale_coverage() {
                // This is the old technique, which we preserve for now so
                // we don't change previous results (testing)
                // the new way seems fine, its just (a tiny bit) different.
                let scale = (coverage * 256.0) as i32;
                let new_alpha = (255 * scale) >> 8;
                paint.shader.apply_opacity(new_alpha as f32 / 255.0);
            }

            if let Some(tiler) = DrawTiler::new(self.width(), self.height()) {
                let mut path = path.clone(); // TODO: avoid cloning
                let mut paint = paint.clone();

                if !transform.is_identity() {
                    paint.shader.transform(transform);
                    path = match path.transform(transform) {
                        Some(v) => v,
                        None => {
                            log::warn!("path transformation failed");
                            return;
                        }
                    };
                }

                for tile in tiler {
                    let ts = Transform::from_translate(-(tile.x() as f32), -(tile.y() as f32));
                    path = match path.transform(ts) {
                        Some(v) => v,
                        None => {
                            log::warn!("path transformation failed");
                            return;
                        }
                    };
                    paint.shader.transform(ts);

                    let mut subpix = match self.subpixmap(tile.to_int_rect()) {
                        Some(v) => v,
                        None => continue, // technically unreachable
                    };
                    let submask = mask.and_then(|mask| mask.submask(tile.to_int_rect()));

                    // We're ignoring "errors" here, because `stroke_hairline` will return `None`
                    // when rendering a tile that doesn't have a path on it.
                    // Which is not an error in this case.
                    Self::stroke_hairline(&path, &paint, stroke.line_cap, submask, &mut subpix);

                    let ts = Transform::from_translate(tile.x() as f32, tile.y() as f32);
                    path = match path.transform(ts) {
                        Some(v) => v,
                        None => return,
                    };
                    paint.shader.transform(ts);
                }
            } else {
                let subpix = &mut self.as_subpixmap();
                let submask = mask.map(|mask| mask.as_submask());
                if !transform.is_identity() {
                    paint.shader.transform(transform);

                    // TODO: avoid clone
                    let path = match path.clone().transform(transform) {
                        Some(v) => v,
                        None => {
                            log::warn!("path transformation failed");
                            return;
                        }
                    };

                    Self::stroke_hairline(&path, &paint, stroke.line_cap, submask, subpix);
                } else {
                    Self::stroke_hairline(path, &paint, stroke.line_cap, submask, subpix);
                }
            }
        } else {
            let path = match path.stroke(stroke, res_scale) {
                Some(v) => v,
                None => {
                    log::warn!("path stroking failed");
                    return;
                }
            };

            self.fill_stroke_outline(&path, paint, transform, mask);
        }
    }

    /// A stroking for paths with subpixel/hairline width.
    fn stroke_hairline(
        path: &Path,
        paint: &Paint,
        line_cap: LineCap,
        mask: Option<SubMaskRef>,
        pixmap: &mut SubPixmapMut,
    ) {
        let clip = pixmap.size.to_screen_int_rect(0, 0);
        let root_clip = pixmap.root_clip;
        let mut blitter = match RasterPipelineBlitter::new(paint, mask, pixmap) {
            Some(v) => v,
            None => return, // nothing to do, all good
        };
        if paint.anti_alias {
            if let Some(root_clip) = root_clip {
                let mut clipped =
                    crate::blitter::FractionalClipBlitter::new(&mut blitter, root_clip);
                scan::hairline_aa::stroke_path(path, line_cap, &clip, &mut clipped);
            } else {
                scan::hairline_aa::stroke_path(path, line_cap, &clip, &mut blitter);
            }
        } else if let Some(root_clip) = root_clip {
            let mut clipped = crate::blitter::FractionalClipBlitter::new(&mut blitter, root_clip);
            scan::hairline::stroke_path(path, line_cap, &clip, &mut clipped);
        } else {
            scan::hairline::stroke_path(path, line_cap, &clip, &mut blitter);
        }
    }

    /// Draws a `Pixmap` on top of the current `Pixmap`.
    ///
    /// The same as filling a rectangle with a `pixmap` pattern.
    pub fn draw_pixmap(
        &mut self,
        x: i32,
        y: i32,
        pixmap: PixmapRef,
        paint: &PixmapPaint,
        transform: Transform,
        mask: Option<&Mask>,
    ) {
        let rect = pixmap.size().to_int_rect(x, y).to_rect();

        // TODO: SkSpriteBlitter
        // TODO: partially clipped
        // TODO: clipped out

        // Translate pattern as well as bounds.
        let patt_transform = Transform::from_translate(x as f32, y as f32);

        let paint = Paint {
            shader: Pattern::new(
                pixmap,
                SpreadMode::Pad, // Pad, otherwise we will get weird borders overlap.
                paint.quality,
                paint.opacity,
                patt_transform,
            ),
            blend_mode: paint.blend_mode,
            anti_alias: false,        // Skia doesn't use it too.
            force_hq_pipeline: false, // Pattern will use hq anyway.
            colorspace: ColorSpace::default(),
        };

        self.fill_rect(rect, &paint, transform, mask);
    }

    /// Draws one destination row from a modulo-indexed row ring using the same
    /// pixmap shader pipeline as [`draw_pixmap`](Self::draw_pixmap).
    ///
    /// `self` must be a one-row target buffer. `target_height` is the logical
    /// height of the full destination, and `row_y` is its global row index.
    /// The transform must be the exact scale that maps the source dimensions
    /// to `(self.width(), target_height)`. The row-ring source is sampled using
    /// its full logical dimensions while its backing storage remains bounded
    /// to the retained rows. The temporary full-image pipeline view is created
    /// and consumed here, so it cannot escape into APIs that assume complete
    /// backing storage. Returns `false` when the dimensions/transform do not
    /// satisfy these constraints or the sampler cannot be constructed.
    pub fn draw_pixmap_row_from_ring(
        &mut self,
        row_y: u32,
        target_height: u32,
        ring: PixmapRowRing,
        paint: &PixmapPaint,
        transform: Transform,
    ) -> bool {
        let pixmap = PixmapRef::from_row_ring(ring);
        let target_width = self.width();
        if self.height() != 1 || target_width == 0 || target_height == 0 || row_y >= target_height {
            return false;
        }

        let scale_x = target_width as f32 / pixmap.width() as f32;
        let scale_y = target_height as f32 / pixmap.height() as f32;
        if !scale_x.is_finite()
            || !scale_y.is_finite()
            || transform != Transform::from_scale(scale_x, scale_y)
        {
            return false;
        }

        // A regular draw_pixmap starts with a transparent destination. The
        // streaming caller reuses this one-row allocation, so restore that
        // initial condition before SourceOver compositing each row.
        self.data_mut().fill(0);

        let mut shader = Pattern::new(
            pixmap,
            SpreadMode::Pad,
            paint.quality,
            paint.opacity,
            Transform::identity(),
        );
        shader.transform(transform);
        let active_paint = Paint {
            shader,
            blend_mode: paint.blend_mode,
            anti_alias: false,
            force_hq_pipeline: false,
            colorspace: ColorSpace::default(),
        };

        let size = match tiny_skia_path::IntSize::from_wh(target_width, target_height) {
            Some(size) => size,
            None => return false,
        };
        let mut destination = SubPixmapMut {
            size,
            real_width: target_width as usize,
            data: self.data_mut(),
            row_origin: row_y,
            root_clip: None,
        };
        let mut blitter = match RasterPipelineBlitter::new(&active_paint, None, &mut destination) {
            Some(blitter) => blitter,
            None => return false,
        };
        let Some(width) = core::num::NonZeroU32::new(target_width) else {
            return false;
        };
        blitter.blit_h(0, row_y, width);
        true
    }

    /// Applies a masks.
    ///
    /// When a `Mask` is passed to drawing methods, it will be used to mask-out
    /// content we're about to draw.
    /// This method masks-out an already drawn content.
    /// It's not as fast, but can be useful when a mask is not available during drawing.
    ///
    /// This method is similar to filling the whole pixmap with an another,
    /// mask-like pixmap using the `DestinationOut` blend mode.
    ///
    /// `Mask` must have the same size as `Pixmap`. No transform or offset are allowed.
    pub fn apply_mask(&mut self, mask: &Mask) {
        if self.size() != mask.size() {
            log::warn!("Pixmap and Mask are expected to have the same size");
            return;
        }

        // Just a dummy.
        let pixmap_src = PixmapRef::from_bytes(&[0, 0, 0, 0], 1, 1).unwrap();

        let mut p = RasterPipelineBuilder::new();
        p.push(pipeline::Stage::LoadMaskU8);
        p.push(pipeline::Stage::LoadDestination);
        p.push(pipeline::Stage::DestinationIn);
        p.push(pipeline::Stage::Store);
        let mut p = p.compile();
        let rect = self.size().to_screen_int_rect(0, 0);
        p.run(
            &rect,
            pipeline::AAMaskCtx::default(),
            mask.as_submask().mask_ctx(),
            pixmap_src,
            &mut self.as_subpixmap(),
        );
    }
}

#[cfg(test)]
mod fractional_root_clip_tests {
    use super::*;

    fn alpha_at(pixmap: &Pixmap, x: u32, y: u32) -> u8 {
        pixmap.pixel(x, y).unwrap().alpha()
    }

    fn solid_paint(anti_alias: bool) -> Paint<'static> {
        let mut paint = Paint::default();
        paint.set_color_rgba8(255, 255, 255, 255);
        paint.anti_alias = anti_alias;
        paint
    }

    #[test]
    fn non_aa_rect_uses_area_coverage_at_fractional_root_edges() {
        let mut pixmap = Pixmap::new(2, 2).unwrap();
        {
            let mut target = pixmap.as_mut();
            assert!(target.set_root_clip([0.0, 0.0, 1.25, 1.25]));
            target.fill_rect(
                Rect::from_xywh(0.0, 0.0, 10.0, 10.0).unwrap(),
                &solid_paint(false),
                Transform::identity(),
                None,
            );
        }

        assert_eq!(alpha_at(&pixmap, 0, 0), 255);
        assert_eq!(alpha_at(&pixmap, 1, 0), 64);
        assert_eq!(alpha_at(&pixmap, 0, 1), 64);
        assert_eq!(alpha_at(&pixmap, 1, 1), 16);
    }

    #[test]
    fn aa_path_intersects_fractional_root_clip_before_coverage_reduction() {
        let mut pixmap = Pixmap::new(2, 2).unwrap();
        let path = PathBuilder::from_rect(Rect::from_xywh(0.0, 0.0, 10.0, 10.0).unwrap());
        {
            let mut target = pixmap.as_mut();
            assert!(target.set_root_clip([0.0, 0.0, 1.25, 1.25]));
            target.fill_path(
                &path,
                &solid_paint(true),
                FillRule::Winding,
                Transform::identity(),
                None,
            );
        }

        assert_eq!(alpha_at(&pixmap, 0, 0), 255);
        assert_eq!(alpha_at(&pixmap, 1, 0), 64);
        assert_eq!(alpha_at(&pixmap, 0, 1), 64);
        assert_eq!(alpha_at(&pixmap, 1, 1), 16);
    }

    #[test]
    fn aa_path_intersection_does_not_paint_outside_fractional_clip() {
        let mut builder = PathBuilder::new();
        builder.move_to(0.0, 0.0);
        builder.line_to(2.0, 0.0);
        builder.line_to(0.0, 2.0);
        builder.close();
        let path = builder.finish().unwrap();
        let mut pixmap = Pixmap::new(2, 2).unwrap();
        {
            let mut target = pixmap.as_mut();
            assert!(target.set_root_clip([0.0, 0.0, 1.25, 1.25]));
            target.fill_path(
                &path,
                &solid_paint(true),
                FillRule::Winding,
                Transform::identity(),
                None,
            );
        }

        assert!(alpha_at(&pixmap, 1, 0) > 0);
        assert_eq!(alpha_at(&pixmap, 1, 1), 0);
        let clipped_triangle_coverage = alpha_at(&pixmap, 0, 1);
        assert!(clipped_triangle_coverage > 0);
        assert!(clipped_triangle_coverage < 255);
    }

    #[test]
    fn analytic_long_fractional_span_uses_scalar_coverage_for_all_pixels() {
        let path = PathBuilder::from_rect(Rect::from_xywh(10.0, 10.5, 200.0, 1.0).unwrap());
        let mut pixmap = Pixmap::new(220, 13).unwrap();
        {
            let mut target = pixmap.as_mut();
            target
                .fill_path_analytic_bounded(
                    &path,
                    &solid_paint(true),
                    FillRule::Winding,
                    Transform::identity(),
                    None,
                    4 * 1024 * 1024,
                )
                .unwrap();
        }

        for y in [10, 11] {
            for x in 10..210 {
                assert_eq!(alpha_at(&pixmap, x, y), 128, "x={x} y={y}");
            }
        }
    }

    #[test]
    fn transformed_non_aa_path_respects_tile_local_fractional_clip() {
        let path = PathBuilder::from_rect(Rect::from_xywh(0.0, 0.0, 10.0, 1.0).unwrap());
        let mut pixmap = Pixmap::new(2, 1).unwrap();
        {
            let mut target = pixmap.as_mut();
            // The target starts at physical x=1. The true viewport ends at
            // x=1.25, so only the first quarter of this tile's first pixel is
            // inside the root viewport.
            assert!(target.set_root_clip([-1.0, 0.0, 0.25, 1.0]));
            target.fill_path(
                &path,
                &solid_paint(false),
                FillRule::Winding,
                Transform::from_translate(-1.0, 0.0),
                None,
            );
        }

        assert_eq!(alpha_at(&pixmap, 0, 0), 64);
        assert_eq!(alpha_at(&pixmap, 1, 0), 0);
    }

    #[test]
    fn raster_composition_receives_fractional_root_clip() {
        let mut source = Pixmap::new(10, 10).unwrap();
        source.fill(Color::WHITE);
        let mut target = Pixmap::new(2, 2).unwrap();
        {
            let mut output = target.as_mut();
            assert!(output.set_root_clip([0.0, 0.0, 1.25, 1.25]));
            output.draw_pixmap(
                0,
                0,
                source.as_ref(),
                &PixmapPaint::default(),
                Transform::identity(),
                None,
            );
        }

        assert_eq!(alpha_at(&target, 0, 0), 255);
        assert_eq!(alpha_at(&target, 1, 0), 64);
        assert_eq!(alpha_at(&target, 0, 1), 64);
        assert_eq!(alpha_at(&target, 1, 1), 16);
    }

    #[test]
    fn translucent_raster_composition_preserves_fractional_edge_coverage() {
        let mut source = Pixmap::new(10, 10).unwrap();
        source.fill(Color::WHITE);
        let mut target = Pixmap::new(2, 2).unwrap();
        let mut paint = PixmapPaint::default();
        paint.opacity = 0.5;
        {
            let mut output = target.as_mut();
            assert!(output.set_root_clip([0.0, 0.0, 1.25, 1.25]));
            output.draw_pixmap(0, 0, source.as_ref(), &paint, Transform::identity(), None);
        }

        assert_eq!(alpha_at(&target, 0, 0), 128);
        assert_eq!(alpha_at(&target, 1, 0), 32);
        assert_eq!(alpha_at(&target, 0, 1), 32);
        assert_eq!(alpha_at(&target, 1, 1), 8);
    }

    #[test]
    fn transformed_raster_composition_respects_fractional_root_clip() {
        let mut source = Pixmap::new(10, 10).unwrap();
        source.fill(Color::WHITE);
        let mut target = Pixmap::new(2, 2).unwrap();
        {
            let mut output = target.as_mut();
            assert!(output.set_root_clip([0.0, 0.0, 1.25, 1.25]));
            output.draw_pixmap(
                0,
                0,
                source.as_ref(),
                &PixmapPaint::default(),
                Transform::from_translate(-0.25, -0.25),
                None,
            );
        }

        assert_eq!(alpha_at(&target, 0, 0), 255);
        assert_eq!(alpha_at(&target, 1, 0), 64);
        assert_eq!(alpha_at(&target, 0, 1), 64);
        assert_eq!(alpha_at(&target, 1, 1), 16);
    }

    #[test]
    fn absent_root_clip_preserves_integer_rect_pixels() {
        let rect = Rect::from_xywh(0.0, 0.0, 2.0, 2.0).unwrap();
        let mut unconfigured = Pixmap::new(2, 2).unwrap();
        unconfigured.fill_rect(rect, &solid_paint(true), Transform::identity(), None);

        let mut explicitly_full = Pixmap::new(2, 2).unwrap();
        {
            let mut target = explicitly_full.as_mut();
            assert!(target.set_root_clip([0.0, 0.0, 2.0, 2.0]));
            target.fill_rect(rect, &solid_paint(true), Transform::identity(), None);
        }

        assert_eq!(unconfigured.data(), explicitly_full.data());
    }
}

pub(crate) fn treat_as_hairline(paint: &Paint, stroke: &Stroke, mut ts: Transform) -> Option<f32> {
    fn fast_len(p: Point) -> f32 {
        let mut x = p.x.abs();
        let mut y = p.y.abs();
        if x < y {
            core::mem::swap(&mut x, &mut y);
        }

        x + y.half()
    }

    debug_assert!(stroke.width >= 0.0);

    if stroke.width == 0.0 {
        return Some(1.0);
    }

    if !paint.anti_alias {
        return None;
    }

    // We don't care about translate.
    ts.tx = 0.0;
    ts.ty = 0.0;

    // We need to try to fake a thick-stroke with a modulated hairline.
    let mut points = [
        Point::from_xy(stroke.width, 0.0),
        Point::from_xy(0.0, stroke.width),
    ];
    ts.map_points(&mut points);

    let len0 = fast_len(points[0]);
    let len1 = fast_len(points[1]);

    if len0 <= 1.0 && len1 <= 1.0 {
        return Some(len0.ave(len1));
    }

    None
}

/// Sometimes in the drawing pipeline, we have to perform math on path coordinates, even after
/// the path is in device-coordinates. Tessellation and clipping are two examples. Usually this
/// is pretty modest, but it can involve subtracting/adding coordinates, or multiplying by
/// small constants (e.g. 2,3,4). To try to preflight issues where these optionations could turn
/// finite path values into infinities (or NaNs), we allow the upper drawing code to reject
/// the path if its bounds (in device coordinates) is too close to max float.
pub(crate) fn is_too_big_for_math(path: &Path) -> bool {
    // This value is just a guess. smaller is safer, but we don't want to reject largish paths
    // that we don't have to.
    const SCALE_DOWN_TO_ALLOW_FOR_SMALL_MULTIPLIES: f32 = 0.25;
    const MAX: f32 = SCALAR_MAX * SCALE_DOWN_TO_ALLOW_FOR_SMALL_MULTIPLIES;

    let b = path.bounds();

    // use ! expression so we return true if bounds contains NaN
    !(b.left() >= -MAX && b.top() >= -MAX && b.right() <= MAX && b.bottom() <= MAX)
}

/// Splits the target pixmap into a list of tiles.
///
/// Skia/tiny-skia uses a lot of fixed-point math during path rendering.
/// Probably more for precision than performance.
/// And our fixed-point types are limited by 8192 and 32768.
/// Which means that we cannot render a path larger than 8192 onto a pixmap.
/// When pixmap is smaller than 8192, the path will be automatically clipped anyway,
/// but for large pixmaps we have to render in tiles.
pub(crate) struct DrawTiler {
    image_width: u32,
    image_height: u32,
    x_offset: u32,
    y_offset: u32,
    finished: bool,
}

impl DrawTiler {
    // 8K is 1 too big, since 8K << supersample == 32768 which is too big for Fixed.
    const MAX_DIMENSIONS: u32 = 8192 - 1;

    fn required(image_width: u32, image_height: u32) -> bool {
        image_width > Self::MAX_DIMENSIONS || image_height > Self::MAX_DIMENSIONS
    }

    pub(crate) fn new(image_width: u32, image_height: u32) -> Option<Self> {
        if Self::required(image_width, image_height) {
            Some(DrawTiler {
                image_width,
                image_height,
                x_offset: 0,
                y_offset: 0,
                finished: false,
            })
        } else {
            None
        }
    }
}

impl Iterator for DrawTiler {
    type Item = ScreenIntRect;

    fn next(&mut self) -> Option<Self::Item> {
        if self.finished {
            return None;
        }

        // TODO: iterate only over tiles that actually affected by the shape

        if self.x_offset < self.image_width && self.y_offset < self.image_height {
            let h = if self.y_offset < self.image_height {
                (self.image_height - self.y_offset).min(Self::MAX_DIMENSIONS)
            } else {
                self.image_height
            };

            let r = ScreenIntRect::from_xywh(
                self.x_offset,
                self.y_offset,
                (self.image_width - self.x_offset).min(Self::MAX_DIMENSIONS),
                h,
            );

            self.x_offset += Self::MAX_DIMENSIONS;
            if self.x_offset >= self.image_width {
                self.x_offset = 0;
                self.y_offset += Self::MAX_DIMENSIONS;
            }

            return r;
        }

        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use alloc::vec;
    const MAX_DIM: u32 = DrawTiler::MAX_DIMENSIONS;

    fn streamed_bicubic_draw_matches_full_draw(source_width: u32, source_height: u32, target_width: u32, target_height: u32) {
        let mut source = Pixmap::new(source_width, source_height).unwrap();
        for y in 0..source_height {
            for x in 0..source_width {
                let offset = ((y * source_width + x) * 4) as usize;
                let alpha = 96 + ((x * 13 + y * 7) % 160) as u8;
                let color = ColorU8::from_rgba(
                    ((x * 29 + y * 11) % 256) as u8,
                    ((x * 3 + y * 37) % 256) as u8,
                    ((x * 17 + y * 19) % 256) as u8,
                    alpha,
                )
                .premultiply();
                    source.data_mut()[offset..offset + 4].copy_from_slice(&[
                        color.red(),
                        color.green(),
                        color.blue(),
                        color.alpha(),
                    ]);
            }
        }

        let transform = Transform::from_scale(
            target_width as f32 / source_width as f32,
            target_height as f32 / source_height as f32,
        );
        let paint = PixmapPaint {
            opacity: 1.0,
            blend_mode: BlendMode::SourceOver,
            quality: FilterQuality::Bicubic,
        };
        let mut expected = Pixmap::new(target_width, target_height).unwrap();
        expected.draw_pixmap(0, 0, source.as_ref(), &paint, transform, None);

        let ring_height = source_height.min(4);
        let mut ring = vec![0; (source_width * ring_height * 4) as usize];
        let mut streamed = Pixmap::new(target_width, target_height).unwrap();
        let mut row = vec![0; (target_width * 4) as usize];
        let mut next_output_row = 0;

        for source_y in 0..source_height {
            if source_y > 0 {
                while next_output_row < target_height
                    && PixmapMut::required_bicubic_source_y_range(
                        source_height,
                        next_output_row,
                        target_height,
                        transform,
                    )
                    .unwrap()
                    .1
                        <= source_y - 1
                {
                    let mut destination = PixmapMut::from_bytes(&mut row, target_width, 1).unwrap();
                    let source_ring = PixmapRowRing::from_bytes(
                        &ring,
                        source_width,
                        source_height,
                        ring_height,
                    )
                    .unwrap();
                    assert!(destination.draw_pixmap_row_from_ring(
                        next_output_row,
                        target_height,
                        source_ring,
                        &paint,
                        transform,
                    ));
                    let offset = (next_output_row * target_width * 4) as usize;
                    streamed.data_mut()[offset..offset + row.len()].copy_from_slice(&row);
                    next_output_row += 1;
                }
            }

            let slot = (source_y % ring_height) as usize * (source_width * 4) as usize;
            let source_offset = (source_y * source_width * 4) as usize;
            ring[slot..slot + (source_width * 4) as usize]
                .copy_from_slice(&source.data()[source_offset..source_offset + (source_width * 4) as usize]);

            while next_output_row < target_height
                && PixmapMut::required_bicubic_source_y_range(
                    source_height,
                    next_output_row,
                    target_height,
                    transform,
                )
                .unwrap()
                .1
                    <= source_y
            {
                let (minimum, maximum) = PixmapMut::required_bicubic_source_y_range(
                    source_height,
                    next_output_row,
                    target_height,
                    transform,
                )
                .unwrap();
                assert!(maximum - minimum < ring_height);
                assert!(source_y - minimum < ring_height);
                let mut destination = PixmapMut::from_bytes(&mut row, target_width, 1).unwrap();
                let source_ring = PixmapRowRing::from_bytes(
                    &ring,
                    source_width,
                    source_height,
                    ring_height,
                )
                .unwrap();
                assert!(destination.draw_pixmap_row_from_ring(
                    next_output_row,
                    target_height,
                    source_ring,
                    &paint,
                    transform,
                ));
                let offset = (next_output_row * target_width * 4) as usize;
                streamed.data_mut()[offset..offset + row.len()].copy_from_slice(&row);
                next_output_row += 1;
            }
        }

        assert_eq!(next_output_row, target_height);
        assert_eq!(streamed.data(), expected.data());
    }

    #[test]
    fn skip() {
        assert!(DrawTiler::new(100, 500).is_none());
    }

    #[test]
    fn horizontal() {
        let mut iter = DrawTiler::new(10000, 500).unwrap();
        assert_eq!(iter.next(), ScreenIntRect::from_xywh(0, 0, MAX_DIM, 500));
        assert_eq!(
            iter.next(),
            ScreenIntRect::from_xywh(MAX_DIM, 0, 10000 - MAX_DIM, 500)
        );
        assert_eq!(iter.next(), None);
    }

    #[test]
    fn vertical() {
        let mut iter = DrawTiler::new(500, 10000).unwrap();
        assert_eq!(iter.next(), ScreenIntRect::from_xywh(0, 0, 500, MAX_DIM));
        assert_eq!(
            iter.next(),
            ScreenIntRect::from_xywh(0, MAX_DIM, 500, 10000 - MAX_DIM)
        );
        assert_eq!(iter.next(), None);
    }

    #[test]
    fn rect() {
        let mut iter = DrawTiler::new(10000, 10000).unwrap();
        // Row 1
        assert_eq!(
            iter.next(),
            ScreenIntRect::from_xywh(0, 0, MAX_DIM, MAX_DIM)
        );
        assert_eq!(
            iter.next(),
            ScreenIntRect::from_xywh(MAX_DIM, 0, 10000 - MAX_DIM, MAX_DIM)
        );
        // Row 2
        assert_eq!(
            iter.next(),
            ScreenIntRect::from_xywh(0, MAX_DIM, MAX_DIM, 10000 - MAX_DIM)
        );
        assert_eq!(
            iter.next(),
            ScreenIntRect::from_xywh(MAX_DIM, MAX_DIM, 10000 - MAX_DIM, 10000 - MAX_DIM)
        );
        assert_eq!(iter.next(), None);
    }

    #[test]
    fn streamed_bicubic_rows_match_full_draw_for_fractional_scales() {
        for (target_width, target_height) in [(7, 8), (1, 3), (8, 1), (7, 11), (9, 4)] {
            streamed_bicubic_draw_matches_full_draw(9, 11, target_width, target_height);
        }
    }

    #[test]
    fn pixmap_row_ring_rejects_invalid_backing_geometry() {
        let exact = [0; 3 * 2 * 4];
        let short = [0; 3 * 2 * 4 - 1];
        let long = [0; 3 * 2 * 4 + 1];

        assert!(PixmapRowRing::from_bytes(&exact, 3, 5, 2).is_some());
        assert!(PixmapRowRing::from_bytes(&short, 3, 5, 2).is_none());
        assert!(PixmapRowRing::from_bytes(&long, 3, 5, 2).is_none());
        assert!(PixmapRowRing::from_bytes(&exact, 3, 5, 0).is_none());
        assert!(PixmapRowRing::from_bytes(&exact, 3, 1, 2).is_none());
        assert!(PixmapRowRing::from_bytes(&exact, 0, 5, 2).is_none());
        assert!(PixmapRowRing::from_bytes(&[], u32::MAX, 5, 1).is_none());
    }
}
