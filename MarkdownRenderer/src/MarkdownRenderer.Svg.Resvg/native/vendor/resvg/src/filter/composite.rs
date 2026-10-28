// Copyright 2020 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use super::{ImageRef, ImageRefMut, f32_bound};
use rgb::RGBA8;
use usvg::ApproxZeroUlps;

/// Performs an arithmetic composition.
///
/// - `src1` and `src2` image pixels should have a **premultiplied alpha**.
/// - `dest` image pixels will have a **premultiplied alpha**.
///
/// All images share the filter region's origin. An input larger or smaller
/// than `dest` (a fractional region with an offset or blur input) is read
/// within its own bounds and is transparent outside them, as the other
/// composite operators draw it; `dest` is never indexed past its size.
pub fn arithmetic(
    k1: f32,
    k2: f32,
    k3: f32,
    k4: f32,
    src1: ImageRef,
    src2: ImageRef,
    dest: ImageRefMut,
) {
    let calc = |i1, i2, max| {
        let i1 = i1 as f32 / 255.0;
        let i2 = i2 as f32 / 255.0;
        let result = k1 * i1 * i2 + k2 * i1 + k3 * i2 + k4;
        f32_bound(0.0, result, max)
    };

    let pixel = |image: &ImageRef, x: u32, y: u32| -> RGBA8 {
        if x < image.width && y < image.height {
            image
                .data
                .get((image.width as usize) * (y as usize) + (x as usize))
                .copied()
                .unwrap_or_default()
        } else {
            RGBA8::default()
        }
    };

    let width = dest.width as usize;
    for y in 0..dest.height {
        for x in 0..dest.width {
            let index = width * (y as usize) + (x as usize);
            if index >= dest.data.len() {
                return;
            }

            let c1 = pixel(&src1, x, y);
            let c2 = pixel(&src2, x, y);
            let a = calc(c1.a, c2.a, 1.0);
            if a.approx_zero_ulps(4) {
                continue;
            }

            let r = (calc(c1.r, c2.r, a) * 255.0) as u8;
            let g = (calc(c1.g, c2.g, a) * 255.0) as u8;
            let b = (calc(c1.b, c2.b, a) * 255.0) as u8;
            let a = (a * 255.0) as u8;

            dest.data[index] = RGBA8 { r, g, b, a };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Phase 3 (P3-011): a Figma inner-shadow filter (feComposite arithmetic
    // k2=-1 k3=1) on a fractional region gave inputs larger than the region,
    // and the unchecked dest write aborted the worker process.
    #[test]
    fn arithmetic_with_inputs_larger_than_dest_writes_only_dest() {
        let src1 = vec![RGBA8 { r: 0, g: 0, b: 0, a: 255 }; 5 * 4];
        let src2 = vec![RGBA8 { r: 255, g: 255, b: 255, a: 255 }; 6 * 5];
        let mut dest = vec![RGBA8::default(); 3 * 2];
        arithmetic(
            0.0,
            0.0,
            1.0,
            0.0,
            ImageRef::new(5, 4, &src1),
            ImageRef::new(6, 5, &src2),
            ImageRefMut::new(3, 2, &mut dest),
        );
        // result = src2 within dest's 3x2 region; nothing is written past it.
        assert!(dest.iter().all(|pixel| *pixel == RGBA8 { r: 255, g: 255, b: 255, a: 255 }));
    }

    #[test]
    fn arithmetic_reads_inputs_smaller_than_dest_as_transparent() {
        let src1 = vec![RGBA8 { r: 0, g: 0, b: 0, a: 0 }; 2 * 2];
        let src2 = vec![RGBA8 { r: 255, g: 0, b: 0, a: 255 }; 2 * 2];
        let mut dest = vec![RGBA8::default(); 3 * 3];
        arithmetic(
            0.0,
            0.0,
            1.0,
            0.0,
            ImageRef::new(2, 2, &src1),
            ImageRef::new(2, 2, &src2),
            ImageRefMut::new(3, 3, &mut dest),
        );
        assert_eq!(dest[0], RGBA8 { r: 255, g: 0, b: 0, a: 255 });
        assert_eq!(dest[4], RGBA8 { r: 255, g: 0, b: 0, a: 255 });
        assert_eq!(dest[2], RGBA8::default());
        assert_eq!(dest[8], RGBA8::default());
    }
}
