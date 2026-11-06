// Copyright (c) JitHub contributors. Licensed under MIT.

//! Bounded rasterization of an attached tree into the host output mapping.

use crate::metadata::Metadata;
use crate::protocol::{FLAG_HAS_TILE, PIXEL_BGRA};
use crate::reject::Reject;
use crate::request::Request;
use resvg::{tiny_skia, usvg};
use std::cell::RefCell;

pub(crate) fn render(
    tree: &usvg::Tree,
    metadata: Metadata,
    request: &Request,
    output: &mut [u8],
    width: u32,
    height: u32,
    raster_cache: &RefCell<resvg::RasterImageCache>,
) -> Result<(), Reject> {
    if metadata.embedded_bytes > request.max_embedded_bytes
        || metadata.embedded_pixels > request.max_embedded_pixels
    {
        return Err(Reject::Resource("embedded image budget exceeded"));
    }
    let has_tile = request.flags & FLAG_HAS_TILE != 0;
    if has_tile && metadata.filter_primitives != 0 && metadata.filter_tiling_unbounded {
        return Err(Reject::Unsupported(
            "filter extent cannot be bounded for tiled rendering",
        ));
    }
    let tree_size = tree.size();
    // Preserve the established single-precision operation order whenever the
    // allocated target is the complete physical viewport. Fractional targets
    // use the exact host viewport for scale while retaining the separately
    // bounded integer pixmap allocation.
    let (scale_x, scale_y) = match request.physical_viewport {
        Some((width, height))
            if width != f64::from(request.target_width)
                || height != f64::from(request.target_height) =>
        {
            (
                (width / f64::from(tree_size.width())) as f32,
                (height / f64::from(tree_size.height())) as f32,
            )
        }
        _ => (
            request.target_width as f32 / tree_size.width(),
            request.target_height as f32 / tree_size.height(),
        ),
    };
    let gutter_x = if has_tile && metadata.filter_primitives != 0 {
        filter_extent_pixels(metadata.filter_gutter_x, f64::from(scale_x))?
    } else {
        0
    };
    let gutter_y = if has_tile && metadata.filter_primitives != 0 {
        filter_extent_pixels(metadata.filter_gutter_y, f64::from(scale_y))?
    } else {
        0
    };
    let left_gutter = gutter_x.min(request.tile_x.max(0) as u32);
    let top_gutter = gutter_y.min(request.tile_y.max(0) as u32);
    let right_edge = u64::from(request.tile_x.max(0) as u32).saturating_add(u64::from(width));
    let bottom_edge = u64::from(request.tile_y.max(0) as u32).saturating_add(u64::from(height));
    let right_gutter = gutter_x.min(
        u64::from(request.target_width)
            .saturating_sub(right_edge)
            .try_into()
            .unwrap_or(u32::MAX),
    );
    let bottom_gutter = gutter_y.min(
        u64::from(request.target_height)
            .saturating_sub(bottom_edge)
            .try_into()
            .unwrap_or(u32::MAX),
    );
    let render_width = width
        .checked_add(left_gutter)
        .and_then(|value| value.checked_add(right_gutter))
        .ok_or(Reject::Resource("tile gutter overflow"))?;
    let render_height = height
        .checked_add(top_gutter)
        .and_then(|value| value.checked_add(bottom_gutter))
        .ok_or(Reject::Resource("tile gutter overflow"))?;
    let filter_multiplier = u64::from(metadata.filter_primitives).saturating_add(1);
    let estimated_filter = u64::from(render_width)
        .saturating_mul(u64::from(render_height))
        .saturating_mul(4)
        .saturating_mul(filter_multiplier);
    if estimated_filter > request.max_filter_bytes {
        return Err(Reject::Resource("filter intermediate budget exceeded"));
    }

    let tile_x = if has_tile {
        request.tile_x.saturating_sub(left_gutter as i32)
    } else {
        0
    };
    let tile_y = if has_tile {
        request.tile_y.saturating_sub(top_gutter as i32)
    } else {
        0
    };
    let transform = tiny_skia::Transform::from_row(
        scale_x,
        0.0,
        0.0,
        scale_y,
        -(tile_x as f32),
        -(tile_y as f32),
    );
    let max_decoded_bytes = request.max_embedded_pixels.saturating_mul(4);
    if gutter_x == 0 && gutter_y == 0 {
        let mut pixmap = tiny_skia::PixmapMut::from_bytes(output, width, height)
            .ok_or(Reject::Worker("invalid output buffer"))?;
        set_physical_viewport_clip(&mut pixmap, request, tile_x, tile_y)?;
        resvg::render_with_resource_limits(
            tree,
            transform,
            &mut pixmap,
            raster_cache,
            max_decoded_bytes,
            request.max_filter_bytes,
        )
        .map_err(map_render_error)?;
    } else {
        let render_length = u64::from(render_width)
            .checked_mul(u64::from(render_height))
            .and_then(|pixels| pixels.checked_mul(4))
            .and_then(|bytes| usize::try_from(bytes).ok())
            .ok_or(Reject::Resource("tile gutter allocation overflow"))?;
        let mut guttered = Vec::new();
        guttered
            .try_reserve_exact(render_length)
            .map_err(|_| Reject::Resource("tile gutter allocation failed"))?;
        let intermediate_bytes = request
            .max_filter_bytes
            .checked_sub(guttered.capacity() as u64)
            .ok_or(Reject::Resource("tile gutter intermediate budget exceeded"))?;
        guttered.resize(render_length, 0);
        let mut pixmap =
            tiny_skia::PixmapMut::from_bytes(&mut guttered, render_width, render_height)
                .ok_or(Reject::Worker("invalid guttered output buffer"))?;
        set_physical_viewport_clip(&mut pixmap, request, tile_x, tile_y)?;
        resvg::render_with_resource_limits(
            tree,
            transform,
            &mut pixmap,
            raster_cache,
            max_decoded_bytes,
            intermediate_bytes,
        )
        .map_err(map_render_error)?;
        let source_stride = usize::try_from(render_width)
            .ok()
            .and_then(|value| value.checked_mul(4))
            .ok_or(Reject::Resource("tile source stride overflow"))?;
        let destination_stride = usize::try_from(width)
            .ok()
            .and_then(|value| value.checked_mul(4))
            .ok_or(Reject::Resource("tile destination stride overflow"))?;
        let source_x = usize::try_from(left_gutter)
            .ok()
            .and_then(|value| value.checked_mul(4))
            .ok_or(Reject::Resource("tile crop offset overflow"))?;
        let source_y = usize::try_from(top_gutter)
            .map_err(|_| Reject::Resource("tile crop offset overflow"))?;
        for row in
            0..usize::try_from(height).map_err(|_| Reject::Resource("tile height overflow"))?
        {
            let source_start = (source_y + row)
                .checked_mul(source_stride)
                .and_then(|value| value.checked_add(source_x))
                .ok_or(Reject::Resource("tile crop overflow"))?;
            let destination_start = row
                .checked_mul(destination_stride)
                .ok_or(Reject::Resource("tile crop overflow"))?;
            output[destination_start..destination_start + destination_stride]
                .copy_from_slice(&guttered[source_start..source_start + destination_stride]);
        }
    }
    if request.pixel_format == PIXEL_BGRA {
        for pixel in output.chunks_exact_mut(4) {
            pixel.swap(0, 2);
        }
    }
    Ok(())
}

pub(crate) fn map_render_error(error: resvg::RenderError) -> Reject {
    match error {
        resvg::RenderError::ResourceLimit => {
            Reject::Resource("render intermediate budget exceeded")
        }
        resvg::RenderError::AllocationFailure => {
            Reject::Resource("render intermediate allocation failed")
        }
        resvg::RenderError::InvalidGeometry => {
            Reject::Resource("render geometry cannot be represented")
        }
        resvg::RenderError::ImageDecode => Reject::Resource("embedded raster image decode failed"),
    }
}

pub(crate) fn set_physical_viewport_clip(
    pixmap: &mut tiny_skia::PixmapMut<'_>,
    request: &Request,
    tile_x: i32,
    tile_y: i32,
) -> Result<(), Reject> {
    let Some((viewport_width, viewport_height)) = request.physical_viewport else {
        return Ok(());
    };
    if viewport_width == f64::from(request.target_width)
        && viewport_height == f64::from(request.target_height)
    {
        return Ok(());
    }

    let tile_x = f64::from(tile_x);
    let tile_y = f64::from(tile_y);
    let bounds = [
        (-tile_x) as f32,
        (-tile_y) as f32,
        (viewport_width - tile_x) as f32,
        (viewport_height - tile_y) as f32,
    ];
    if !pixmap.set_root_clip(bounds) {
        return Err(Reject::Worker("invalid physical viewport clip"));
    }
    Ok(())
}

pub(crate) fn filter_extent_pixels(user_units: f64, scale: f64) -> Result<u32, Reject> {
    let pixels = (user_units * scale.abs() + 2.0).ceil();
    if !pixels.is_finite() || pixels < 0.0 || pixels > f64::from(u32::MAX) {
        return Err(Reject::Resource("filter gutter exceeds addressable output"));
    }
    Ok(pixels as u32)
}
