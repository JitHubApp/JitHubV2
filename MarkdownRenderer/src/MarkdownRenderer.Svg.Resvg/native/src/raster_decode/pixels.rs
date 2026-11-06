// Copyright (c) JitHub contributors. Licensed under MIT.

//! Pixel conversions and full-frame EXIF orientation.

use crate::raster_decode::scratch::rgba_length;
use crate::reject::Reject;
use resvg::tiny_skia::{
    BlendMode, ColorU8, FilterQuality, PixmapMut, PixmapPaint, PixmapRef, Transform,
};

pub(crate) fn premultiply_rgba_in_place(rgba: &mut [u8]) {
    for pixel in rgba.chunks_exact_mut(4) {
        let premultiplied =
            ColorU8::from_rgba(pixel[0], pixel[1], pixel[2], pixel[3]).premultiply();
        pixel[0] = premultiplied.red();
        pixel[1] = premultiplied.green();
        pixel[2] = premultiplied.blue();
        pixel[3] = premultiplied.alpha();
    }
}

pub(crate) fn swap_red_blue(bytes: &mut [u8]) {
    for pixel in bytes.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
}

pub(crate) fn rasterize_oriented(
    source: &mut [u8],
    source_width: u32,
    source_height: u32,
    orientation: u32,
    target_width: u32,
    target_height: u32,
    output: &mut [u8],
) -> Result<(), Reject> {
    if source.len() != rgba_length(source_width, source_height)?
        || output.len() != rgba_length(target_width, target_height)?
    {
        return Err(Reject::Resource("raster buffer geometry mismatch"));
    }
    if !(1..=8).contains(&orientation) {
        return Err(Reject::Unsupported("invalid WebP orientation"));
    }

    let transposed = orientation >= 5;
    let (intermediate_width, intermediate_height) = if transposed {
        (target_height, target_width)
    } else {
        (target_width, target_height)
    };
    let intermediate_length = rgba_length(intermediate_width, intermediate_height)?;

    if source_width == intermediate_width && source_height == intermediate_height {
        // Avoid resampling at native resolution. Besides preserving lossless
        // pixels exactly, this keeps EXIF flips from passing negative scales
        // through the bicubic sampler.
        output.copy_from_slice(source);
    } else {
        let scale_x = intermediate_width as f32 / source_width as f32;
        let scale_y = intermediate_height as f32 / source_height as f32;
        let transform = Transform::from_scale(scale_x, scale_y);
        if !scale_x.is_finite() || !scale_y.is_finite() || !transform.is_finite() {
            return Err(Reject::Resource("WebP resize transform overflow"));
        }

        output.fill(0);
        {
            let source_pixmap = PixmapRef::from_bytes(source, source_width, source_height)
                .ok_or(Reject::Resource("invalid premultiplied WebP source raster"))?;
            let mut destination =
                PixmapMut::from_bytes(output, intermediate_width, intermediate_height)
                    .ok_or(Reject::Resource("invalid WebP output raster"))?;
            destination.draw_pixmap(
                0,
                0,
                source_pixmap,
                &PixmapPaint {
                    opacity: 1.0,
                    blend_mode: BlendMode::SourceOver,
                    quality: FilterQuality::Bicubic,
                },
                transform,
                None,
            );
        }
    }

    if transposed {
        transpose_pixels_in_place(output, intermediate_width, intermediate_height, source)?;
    } else if output.len() != intermediate_length {
        return Err(Reject::Resource("WebP intermediate raster mismatch"));
    }

    match orientation {
        1 | 5 => {}
        2 | 6 => flip_horizontal(output, target_width, target_height),
        3 | 7 => {
            flip_horizontal(output, target_width, target_height);
            flip_vertical(output, target_width, target_height);
        }
        4 | 8 => flip_vertical(output, target_width, target_height),
        _ => unreachable!(),
    }
    Ok(())
}

pub(crate) fn transpose_pixels_in_place(
    pixels: &mut [u8],
    width: u32,
    height: u32,
    scratch: &mut [u8],
) -> Result<(), Reject> {
    let pixel_count = usize::try_from(width)
        .ok()
        .and_then(|width| {
            usize::try_from(height)
                .ok()
                .and_then(|height| width.checked_mul(height))
        })
        .ok_or(Reject::Resource("WebP transpose dimensions overflow"))?;
    let expected_bytes = pixel_count
        .checked_mul(4)
        .ok_or(Reject::Resource("WebP transpose size overflow"))?;
    let visited_bytes = pixel_count
        .checked_add(7)
        .and_then(|count| count.checked_div(8))
        .ok_or(Reject::Resource("WebP transpose scratch overflow"))?;
    if pixels.len() != expected_bytes || scratch.len() < visited_bytes || width == 0 || height == 0
    {
        return Err(Reject::Resource("WebP transpose buffer mismatch"));
    }

    // The decoded source is no longer needed after the positive-scale draw, so
    // its already-admitted storage supplies the visited bitmap without another
    // frame-sized allocation.
    let visited = &mut scratch[..visited_bytes];
    visited.fill(0);
    for start in 0..pixel_count {
        if bitmap_contains(visited, start) {
            continue;
        }
        let mut carried = [0_u8; 4];
        let start_byte = start * 4;
        carried.copy_from_slice(&pixels[start_byte..start_byte + 4]);
        let mut current = start;
        loop {
            bitmap_insert(visited, current);
            let next = (current % width as usize) * height as usize + current / width as usize;
            let next_byte = next * 4;
            if next == start {
                pixels[next_byte..next_byte + 4].copy_from_slice(&carried);
                break;
            }
            let mut displaced = [0_u8; 4];
            displaced.copy_from_slice(&pixels[next_byte..next_byte + 4]);
            pixels[next_byte..next_byte + 4].copy_from_slice(&carried);
            carried = displaced;
            current = next;
        }
    }
    Ok(())
}

pub(crate) fn bitmap_contains(bitmap: &[u8], index: usize) -> bool {
    bitmap[index / 8] & (1 << (index % 8)) != 0
}

pub(crate) fn bitmap_insert(bitmap: &mut [u8], index: usize) {
    bitmap[index / 8] |= 1 << (index % 8);
}

pub(crate) fn flip_horizontal(pixels: &mut [u8], width: u32, height: u32) {
    let width = width as usize;
    let height = height as usize;
    for y in 0..height {
        for x in 0..width / 2 {
            let left = (y * width + x) * 4;
            let right = (y * width + (width - 1 - x)) * 4;
            for channel in 0..4 {
                pixels.swap(left + channel, right + channel);
            }
        }
    }
}

pub(crate) fn flip_vertical(pixels: &mut [u8], width: u32, height: u32) {
    let width = width as usize;
    let height = height as usize;
    for y in 0..height / 2 {
        for x in 0..width {
            let top = (y * width + x) * 4;
            let bottom = ((height - 1 - y) * width + x) * 4;
            for channel in 0..4 {
                pixels.swap(top + channel, bottom + channel);
            }
        }
    }
}
