// Copyright 2006 The Android Open Source Project
// Copyright 2020 Yevhenii Reizner
//
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

use crate::geom::ScreenIntRect;

use crate::alpha_runs::AlphaRun;
use crate::color::{AlphaU8, ALPHA_U8_OPAQUE};
use crate::LengthU32;
use core::num::NonZeroU16;

/// Mask is used to describe alpha bitmaps.
pub struct Mask {
    pub image: [u8; 2],
    pub bounds: ScreenIntRect,
    pub row_bytes: u32,
    /// Repeats `image[0]` over the complete bounds instead of indexing it.
    pub repeat_alpha: bool,
}

/// Blitter is responsible for actually writing pixels into memory.
///
/// Besides efficiency, they handle clipping and antialiasing.
/// An object that implements Blitter contains all the context needed to generate pixels
/// for the destination and how src/generated pixels map to the destination.
/// The coordinates passed to the `blit_*` calls are in destination pixel space.
pub trait Blitter {
    /// Blits a horizontal run of one or more pixels.
    fn blit_h(&mut self, _x: u32, _y: u32, _width: LengthU32) {
        unreachable!()
    }

    /// Blits a horizontal run of antialiased pixels.
    ///
    /// runs[] is a *sparse* zero-terminated run-length encoding of spans of constant alpha values.
    ///
    /// The runs[] and antialias[] work together to represent long runs of pixels with the same
    /// alphas. The runs[] contains the number of pixels with the same alpha, and antialias[]
    /// contain the coverage value for that number of pixels. The runs[] (and antialias[]) are
    /// encoded in a clever way. The runs array is zero terminated, and has enough entries for
    /// each pixel plus one, in most cases some of the entries will not contain valid data. An entry
    /// in the runs array contains the number of pixels (np) that have the same alpha value. The
    /// next np value is found np entries away. For example, if runs[0] = 7, then the next valid
    /// entry will by at runs[7]. The runs array and antialias[] are coupled by index. So, if the
    /// np entry is at runs[45] = 12 then the alpha value can be found at antialias[45] = 0x88.
    /// This would mean to use an alpha value of 0x88 for the next 12 pixels starting at pixel 45.
    fn blit_anti_h(
        &mut self,
        _x: u32,
        _y: u32,
        _antialias: &mut [AlphaU8],
        _runs: &mut [AlphaRun],
    ) {
        unreachable!()
    }

    /// Blits a vertical run of pixels with a constant alpha value.
    fn blit_v(&mut self, _x: u32, _y: u32, _height: LengthU32, _alpha: AlphaU8) {
        unreachable!()
    }

    fn blit_anti_h2(&mut self, _x: u32, _y: u32, _alpha0: AlphaU8, _alpha1: AlphaU8) {
        unreachable!()
    }

    fn blit_anti_v2(&mut self, _x: u32, _y: u32, _alpha0: AlphaU8, _alpha1: AlphaU8) {
        unreachable!()
    }

    /// Blits a solid rectangle one or more pixels wide.
    fn blit_rect(&mut self, _rect: &ScreenIntRect) {
        unreachable!()
    }

    /// Blits a pattern of pixels defined by a rectangle-clipped mask.
    fn blit_mask(&mut self, _mask: &Mask, _clip: &ScreenIntRect) {
        unreachable!()
    }
}

/// Applies an outer fractional destination clip to blitter coverage. AA path
/// fills use the scan converter's geometric clip instead; this adapter covers
/// non-AA primitives, hairlines, masks, and already-rasterized composites.
pub(crate) struct FractionalClipBlitter<'a> {
    inner: &'a mut dyn Blitter,
    bounds: [f32; 4],
    use_alpha_run_coverage: bool,
}

impl<'a> FractionalClipBlitter<'a> {
    pub(crate) fn new(inner: &'a mut dyn Blitter, bounds: [f32; 4]) -> Self {
        Self {
            inner,
            bounds,
            use_alpha_run_coverage: false,
        }
    }

    /// Adapts fractional root coverage for alpha runs emitted by the exact
    /// fixed-point stroke-rectangle rasterizer. Unlike raster-composition
    /// masks, these runs are already geometric coverage and must stay in the
    /// native scalar-coverage path when the fractional clip is applied.
    pub(crate) fn for_alpha_run_coverage(inner: &'a mut dyn Blitter, bounds: [f32; 4]) -> Self {
        Self {
            inner,
            bounds,
            use_alpha_run_coverage: true,
        }
    }

    fn overlap(start: f64, end: f64, clip_start: f64, clip_end: f64) -> f64 {
        (end.min(clip_end) - start.max(clip_start)).clamp(0.0, 1.0)
    }

    fn scaled_alpha(alpha: AlphaU8, coverage: f64) -> AlphaU8 {
        (f64::from(alpha) * coverage.clamp(0.0, 1.0)).round() as AlphaU8
    }

    fn emit_run(&mut self, x: u32, y: u32, width: u32, alpha: AlphaU8) {
        if width == 0 || alpha == 0 {
            return;
        }

        if alpha == ALPHA_U8_OPAQUE {
            self.inner.blit_h(x, y, LengthU32::new(width).unwrap());
        } else if self.use_alpha_run_coverage {
            // RasterPipeline's alpha-mask context stores at most two pixels,
            // so a repeated-alpha mask is not a valid way to cover a long run.
            // Forward the scalar coverage through the native RLE interface in
            // bounded chunks instead. As with the scan converter's caller,
            // reseed both mutable buffers because wrapper blitters may mutate
            // them in place.
            const RUN_CHUNK: u32 = 100;
            let mut alpha_run = [0; RUN_CHUNK as usize];
            let mut runs = [None; RUN_CHUNK as usize + 1];
            let mut start = x;
            let mut remaining = width;
            while remaining != 0 {
                let count = remaining.min(RUN_CHUNK);
                alpha_run.fill(alpha);
                runs.fill(None);
                runs[0] = NonZeroU16::new(count as u16);
                runs[count as usize] = None;
                self.inner.blit_anti_h(start, y, &mut alpha_run, &mut runs);
                start = start.saturating_add(count);
                remaining -= count;
            }
        } else {
            let bounds = ScreenIntRect::from_xywh_safe(
                x,
                y,
                LengthU32::new(width).unwrap(),
                LengthU32::new(1).unwrap(),
            );
            let mask = Mask {
                image: [alpha, alpha],
                bounds,
                row_bytes: 0,
                repeat_alpha: true,
            };
            self.inner.blit_mask(&mask, &bounds);
        }
    }

    fn emit_span_with_y_coverage(
        &mut self,
        x: u32,
        y: u32,
        width: u32,
        alpha: AlphaU8,
        coverage_y: f64,
    ) {
        if width == 0 || alpha == 0 {
            return;
        }

        let x0 = f64::from(x);
        let x1 = x0 + f64::from(width);
        let clip_left = f64::from(self.bounds[0]);
        let clip_right = f64::from(self.bounds[2]);
        let start = x0.max(clip_left);
        let stop = x1.min(clip_right);
        if coverage_y <= 0.0 || stop <= start {
            return;
        }

        let first = start.floor() as u32;
        let last = (stop.ceil() as u32).saturating_sub(1);
        if first == last {
            let coverage_x = Self::overlap(f64::from(first), f64::from(first + 1), start, stop);
            self.emit_run(
                first,
                y,
                1,
                Self::scaled_alpha(alpha, coverage_x * coverage_y),
            );
            return;
        }

        let full_start = start.ceil() as u32;
        let full_end = stop.floor() as u32;
        if start < f64::from(full_start) {
            let coverage_x = f64::from(full_start) - start;
            self.emit_run(
                first,
                y,
                1,
                Self::scaled_alpha(alpha, coverage_x * coverage_y),
            );
        }

        if full_end > full_start {
            self.emit_run(
                full_start,
                y,
                full_end - full_start,
                Self::scaled_alpha(alpha, coverage_y),
            );
        }

        if stop > f64::from(full_end) {
            let coverage_x = stop - f64::from(full_end);
            self.emit_run(
                full_end,
                y,
                1,
                Self::scaled_alpha(alpha, coverage_x * coverage_y),
            );
        }
    }

    fn emit_span(&mut self, x: u32, y: u32, width: u32, alpha: AlphaU8) {
        let y0 = f64::from(y);
        let coverage_y = Self::overlap(
            y0,
            y0 + 1.0,
            f64::from(self.bounds[1]),
            f64::from(self.bounds[3]),
        );
        self.emit_span_with_y_coverage(x, y, width, alpha, coverage_y);
    }

    fn emit_vertical(&mut self, x: u32, y: u32, height: u32, alpha: AlphaU8) {
        if height == 0 || alpha == 0 {
            return;
        }

        let x_coverage = Self::overlap(
            f64::from(x),
            f64::from(x) + 1.0,
            f64::from(self.bounds[0]),
            f64::from(self.bounds[2]),
        );
        let start = f64::from(y).max(f64::from(self.bounds[1]));
        let stop = (f64::from(y) + f64::from(height)).min(f64::from(self.bounds[3]));
        if x_coverage == 0.0 || stop <= start {
            return;
        }

        let first_row = start.floor() as u32;
        let last_row = (stop.ceil() as u32).saturating_sub(1);
        if first_row == last_row {
            self.inner.blit_v(
                x,
                first_row,
                LengthU32::new(1).unwrap(),
                Self::scaled_alpha(alpha, (stop - start) * x_coverage),
            );
            return;
        }

        let first_full = start.ceil() as u32;
        let full_end = stop.floor() as u32;
        if start < f64::from(first_full) {
            self.inner.blit_v(
                x,
                first_row,
                LengthU32::new(1).unwrap(),
                Self::scaled_alpha(alpha, (f64::from(first_full) - start) * x_coverage),
            );
        }
        if full_end > first_full {
            self.inner.blit_v(
                x,
                first_full,
                LengthU32::new(full_end - first_full).unwrap(),
                Self::scaled_alpha(alpha, x_coverage),
            );
        }
        if stop > f64::from(full_end) && full_end >= first_full {
            self.inner.blit_v(
                x,
                full_end,
                LengthU32::new(1).unwrap(),
                Self::scaled_alpha(alpha, (stop - f64::from(full_end)) * x_coverage),
            );
        }
    }

    fn emit_single_mask(&mut self, x: u32, y: u32, alpha: AlphaU8) {
        let coverage_x = Self::overlap(
            f64::from(x),
            f64::from(x) + 1.0,
            f64::from(self.bounds[0]),
            f64::from(self.bounds[2]),
        );
        let coverage_y = Self::overlap(
            f64::from(y),
            f64::from(y) + 1.0,
            f64::from(self.bounds[1]),
            f64::from(self.bounds[3]),
        );
        let alpha = Self::scaled_alpha(alpha, coverage_x * coverage_y);
        if alpha == 0 {
            return;
        }

        self.inner.blit_v(x, y, LengthU32::new(1).unwrap(), alpha);
    }
}

#[cfg(test)]
mod fractional_clip_tests {
    use super::*;
    use alloc::vec;
    use alloc::vec::Vec;

    #[derive(Default)]
    struct CapturedRun {
        x: u32,
        y: u32,
        width: u32,
        alpha: AlphaU8,
    }

    #[derive(Default)]
    struct RunCapture(Vec<CapturedRun>);

    impl Blitter for RunCapture {
        fn blit_anti_h(
            &mut self,
            mut x: u32,
            y: u32,
            alpha: &mut [AlphaU8],
            runs: &mut [AlphaRun],
        ) {
            let mut offset = 0_usize;
            while let Some(run) = runs[offset] {
                let count = u32::from(run.get());
                self.0.push(CapturedRun {
                    x,
                    y,
                    width: count,
                    alpha: alpha[offset],
                });
                x = x.saturating_add(count);
                offset += usize::from(run.get());
            }
        }
    }

    #[test]
    fn fractional_clip_preserves_long_fractional_alpha_runs() {
        for width in [1_u32, 2, 4, 100, 101, 1_001] {
            let mut capture = RunCapture::default();
            let mut clipped = FractionalClipBlitter::for_alpha_run_coverage(
                &mut capture,
                [2.25, 0.25, 2_000.5, 1.0],
            );
            let mut alpha = vec![128; width as usize];
            let mut runs = vec![None; width as usize + 1];
            runs[0] = NonZeroU16::new(width as u16);
            runs[width as usize] = None;

            clipped.blit_anti_h(5, 0, &mut alpha, &mut runs);

            let mut next_x = 5;
            let mut total_width = 0;
            assert!(!capture.0.is_empty(), "width {width} produced no runs");
            for run in capture.0 {
                assert_eq!(run.x, next_x, "width {width} has a gap or overlap");
                assert_eq!(run.y, 0);
                assert!(run.width <= 100, "run exceeded the bounded chunk");
                assert_eq!(run.alpha, 96, "wrong fractional coverage for width {width}");
                next_x += run.width;
                total_width += run.width;
            }
            assert_eq!(total_width, width);
        }
    }
}

impl Blitter for FractionalClipBlitter<'_> {
    fn blit_h(&mut self, x: u32, y: u32, width: LengthU32) {
        self.emit_span(x, y, width.get(), ALPHA_U8_OPAQUE);
    }

    fn blit_anti_h(&mut self, x: u32, y: u32, alpha: &mut [AlphaU8], runs: &mut [AlphaRun]) {
        let mut offset = 0_usize;
        let mut start = x;
        while let Some(run) = runs[offset] {
            let count = u32::from(run.get());
            self.emit_span(start, y, count, alpha[offset]);
            start = start.saturating_add(count);
            offset += usize::from(run.get());
        }
    }

    fn blit_v(&mut self, x: u32, y: u32, height: LengthU32, alpha: AlphaU8) {
        self.emit_vertical(x, y, height.get(), alpha);
    }

    fn blit_anti_h2(&mut self, x: u32, y: u32, alpha0: AlphaU8, alpha1: AlphaU8) {
        self.emit_span(x, y, 1, alpha0);
        self.emit_span(x.saturating_add(1), y, 1, alpha1);
    }

    fn blit_anti_v2(&mut self, x: u32, y: u32, alpha0: AlphaU8, alpha1: AlphaU8) {
        self.emit_vertical(x, y, 1, alpha0);
        self.emit_vertical(x, y.saturating_add(1), 1, alpha1);
    }

    fn blit_rect(&mut self, rect: &ScreenIntRect) {
        let left = f64::from(rect.left()).max(f64::from(self.bounds[0]));
        let top = f64::from(rect.top()).max(f64::from(self.bounds[1]));
        let right = f64::from(rect.right()).min(f64::from(self.bounds[2]));
        let bottom = f64::from(rect.bottom()).min(f64::from(self.bounds[3]));
        if right <= left || bottom <= top {
            return;
        }

        let first_row = top.floor() as u32;
        let last_row = (bottom.ceil() as u32).saturating_sub(1);
        if first_row == last_row {
            self.emit_span_with_y_coverage(
                rect.left(),
                first_row,
                rect.width(),
                ALPHA_U8_OPAQUE,
                bottom - top,
            );
            return;
        }

        let first_column = left.floor() as u32;
        let last_column = (right.ceil() as u32).saturating_sub(1);
        if first_column == last_column {
            self.emit_vertical(first_column, rect.top(), rect.height(), ALPHA_U8_OPAQUE);
            return;
        }

        let full_left = left.ceil() as u32;
        let full_top = top.ceil() as u32;
        let full_right = right.floor() as u32;
        let full_bottom = bottom.floor() as u32;
        if full_right > full_left && full_bottom > full_top {
            let full = ScreenIntRect::from_xywh_safe(
                full_left,
                full_top,
                LengthU32::new(full_right - full_left).unwrap(),
                LengthU32::new(full_bottom - full_top).unwrap(),
            );
            self.inner.blit_rect(&full);
        }

        if left < f64::from(full_left) {
            let x = left.floor() as u32;
            let coverage_x = f64::from(full_left) - left;
            if x >= rect.left() && x < rect.right() && full_bottom > full_top {
                self.inner.blit_v(
                    x,
                    full_top,
                    LengthU32::new(full_bottom - full_top).unwrap(),
                    Self::scaled_alpha(ALPHA_U8_OPAQUE, coverage_x),
                );
            }
        }
        if right > f64::from(full_right) {
            let x = full_right;
            let coverage_x = right - f64::from(full_right);
            if x >= rect.left() && x < rect.right() && full_bottom > full_top {
                self.inner.blit_v(
                    x,
                    full_top,
                    LengthU32::new(full_bottom - full_top).unwrap(),
                    Self::scaled_alpha(ALPHA_U8_OPAQUE, coverage_x),
                );
            }
        }

        if top < f64::from(full_top) && first_row >= rect.top() && first_row < rect.bottom() {
            self.emit_span_with_y_coverage(
                rect.left(),
                first_row,
                rect.width(),
                ALPHA_U8_OPAQUE,
                f64::from(full_top) - top,
            );
        }
        if bottom > f64::from(full_bottom)
            && last_row != first_row
            && full_bottom >= rect.top()
            && full_bottom < rect.bottom()
        {
            self.emit_span_with_y_coverage(
                rect.left(),
                full_bottom,
                rect.width(),
                ALPHA_U8_OPAQUE,
                bottom - f64::from(full_bottom),
            );
        }
    }

    fn blit_mask(&mut self, mask: &Mask, clip: &ScreenIntRect) {
        let bounds = &mask.bounds;
        let left = bounds.left().max(clip.left());
        let top = bounds.top().max(clip.top());
        let right = bounds.right().min(clip.right());
        let bottom = bounds.bottom().min(clip.bottom());
        for y in top..bottom {
            for x in left..right {
                let index = if mask.row_bytes == 0 {
                    0
                } else {
                    let row = usize::try_from(y - bounds.top()).unwrap();
                    let column = usize::try_from(x - bounds.left()).unwrap();
                    row * mask.row_bytes as usize + column
                };
                if index < mask.image.len() {
                    self.emit_single_mask(x, y, mask.image[index]);
                }
            }
        }
    }
}
