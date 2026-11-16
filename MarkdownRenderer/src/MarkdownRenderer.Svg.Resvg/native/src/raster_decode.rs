// Copyright (c) JitHub contributors. Licensed under MIT.

//! Bounded static WebP decoding for the isolated raster worker operation.
//!
//! The mapped source and destination are host-owned. Temporary decoded pixels,
//! the WebP decoder ledger, and a conservative ICC parser/transform reservation
//! share the caller's working-byte ceiling. The worker Job Object commit limit
//! remains the outer process guard for allocator and runtime overhead.

mod decode;
mod exif;
mod icc_preflight;
mod icc_transform;
mod pixels;
mod scratch;
mod streamed_rows;

#[cfg(test)]
mod tests;

use crate::limits::HARD_MAX_OUTPUT_BYTES;
use crate::protocol::{KIND_RASTER_DECODE, PIXEL_BGRA, PIXEL_RGBA, PROGRESS_AREA_BYTES};
use crate::raster_decode::decode::decode_and_render;
use crate::reject::Reject;
use crate::request::Request;
use crate::response::Response;
use crate::shared_mapping::{SharedMapping, align64, mapping_length};
use image_webp::DecodingError;
use std::sync::atomic::{AtomicU8, Ordering};

pub(crate) const MAX_SOURCE_BYTES: usize = 32 * 1024 * 1024;

pub(crate) const MAX_DIMENSION: u32 = 16_384;

pub(crate) const MAX_SOURCE_PIXELS: u64 = 40_000_000;

pub(crate) const PROGRESS_MAPPED: u8 = 1;

pub(crate) const PROGRESS_HASHED: u8 = 2;

pub(crate) const PROGRESS_HEADER_VALIDATED: u8 = 3;

pub(crate) const PROGRESS_DECODED: u8 = 4;

pub(crate) const PROGRESS_COLOR_CONVERTED: u8 = 5;

pub(crate) const PROGRESS_RASTERIZED: u8 = 6;

pub(crate) const PROGRESS_COMPLETE: u8 = 7;

#[derive(Clone, Copy)]
pub(crate) struct DecodePlan {
    pub(crate) source_width: u32,
    pub(crate) source_height: u32,
    pub(crate) orientation: u32,
    pub(crate) intrinsic_width: u32,
    pub(crate) intrinsic_height: u32,
    pub(crate) target_width: u32,
    pub(crate) target_height: u32,
    pub(crate) output_length: usize,
    pub(crate) mapping_length: usize,
    pub(crate) working_limit: usize,
    pub(crate) pixel_format: u8,
}

pub(super) fn process_request(request: &Request) -> Result<Response, Reject> {
    let plan = validate_request(request)?;
    let mut mapping = SharedMapping::open(request.mapping_handle, plan.mapping_length)?;
    let bytes = mapping.as_mut_slice();
    let source_aligned = align64(request.source_length as usize);
    let (source_region, output_and_trailer) = bytes.split_at_mut(source_aligned);
    let (output, trailer) = output_and_trailer.split_at_mut(plan.output_length);
    if trailer.len() != PROGRESS_AREA_BYTES {
        return Err(Reject::Worker("raster mapping layout mismatch"));
    }

    // This byte is disjoint from both mapped payload slices and is deliberately
    // at the end of the mapped operation-owned region.
    let progress = unsafe { AtomicU8::from_ptr(trailer.as_mut_ptr()) };
    progress.store(PROGRESS_MAPPED, Ordering::Release);
    output.fill(0);

    let source = &source_region[..request.source_length as usize];
    let result = decode_and_render(request, &plan, source, output, progress);
    if result.is_err() {
        // A failure response must never expose a partially initialized raster.
        output.fill(0);
    }
    result
}

pub(crate) fn validate_request(request: &Request) -> Result<DecodePlan, Reject> {
    if request.kind != KIND_RASTER_DECODE
        || request.flags != 0
        || request.tile_x != 0
        || request.tile_y != 0
        || request.tile_width != 0
        || request.tile_height != 0
        || request.max_xml_depth != 0
        || request.max_nested_svg_depth != 0
        || request.max_structural_cost != 0
        || request.max_embedded_bytes != 0
        || request.max_embedded_pixels != 0
        || request.max_output_bytes != 0
        || request.max_filter_bytes != 0
        || request.max_cache_bytes != 0
        || request.font_generation != 0
        || request.color_scheme != 0
        || request.semantic_color != 0
        || request.document_id != 0
        || !request.locale.is_empty()
        || request.physical_viewport.is_some()
    {
        return Err(Reject::Worker("malformed raster decode request"));
    }

    let source_length = usize::try_from(request.source_length)
        .map_err(|_| Reject::Resource("raster source length overflow"))?;
    if source_length == 0 || source_length > MAX_SOURCE_BYTES {
        return Err(Reject::Resource("raster source exceeds limit"));
    }
    if request.source_width == 0
        || request.source_height == 0
        || request.source_width > MAX_DIMENSION
        || request.source_height > MAX_DIMENSION
        || u64::from(request.source_width) * u64::from(request.source_height) > MAX_SOURCE_PIXELS
    {
        return Err(Reject::Resource("raster source dimensions exceed limit"));
    }
    if !(1..=8).contains(&request.orientation) {
        return Err(Reject::Unsupported("invalid raster orientation"));
    }

    let (intrinsic_width, intrinsic_height) = oriented_dimensions(
        request.source_width,
        request.source_height,
        request.orientation,
    );
    if request.target_width == 0
        || request.target_height == 0
        || request.target_width > MAX_DIMENSION
        || request.target_height > MAX_DIMENSION
        || request.target_width > intrinsic_width
        || request.target_height > intrinsic_height
    {
        return Err(Reject::Resource("raster output dimensions exceed limit"));
    }
    if request.pixel_format != PIXEL_RGBA && request.pixel_format != PIXEL_BGRA {
        return Err(Reject::Worker("invalid raster output pixel format"));
    }

    let output_length_u64 = u64::from(request.target_width)
        .checked_mul(u64::from(request.target_height))
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or(Reject::Resource("raster output size overflow"))?;
    if output_length_u64 != u64::from(request.output_length)
        || output_length_u64 > HARD_MAX_OUTPUT_BYTES
    {
        return Err(Reject::Resource("raster output size exceeds limit"));
    }
    let output_length = usize::try_from(output_length_u64)
        .map_err(|_| Reject::Resource("raster output size overflow"))?;
    let mapped_length = mapping_length(request)?;
    let working_limit = usize::try_from(request.working_bytes)
        .map_err(|_| Reject::Resource("raster working limit overflow"))?;
    let hard_limit = hard_working_limit();
    if working_limit > hard_limit {
        return Err(Reject::Worker(
            "host attempted to raise raster working limit",
        ));
    }
    if mapped_length > working_limit {
        return Err(Reject::Resource("raster mapping exceeds working limit"));
    }

    Ok(DecodePlan {
        source_width: request.source_width,
        source_height: request.source_height,
        orientation: request.orientation,
        intrinsic_width,
        intrinsic_height,
        target_width: request.target_width,
        target_height: request.target_height,
        output_length,
        mapping_length: mapped_length,
        working_limit,
        pixel_format: request.pixel_format,
    })
}

pub(crate) fn hard_working_limit() -> usize {
    #[cfg(target_pointer_width = "32")]
    {
        32 * 1024 * 1024
    }
    #[cfg(target_pointer_width = "64")]
    {
        64 * 1024 * 1024
    }
}

pub(crate) fn oriented_dimensions(width: u32, height: u32, orientation: u32) -> (u32, u32) {
    if (5..=8).contains(&orientation) {
        (height, width)
    } else {
        (width, height)
    }
}

pub(crate) fn map_decode_error(error: DecodingError) -> Reject {
    match error {
        DecodingError::ImageTooLarge
        | DecodingError::MemoryLimitExceeded
        | DecodingError::AllocationFailed => {
            Reject::Resource("WebP decoder resource limit exceeded")
        }
        DecodingError::UnsupportedFeature(_) => Reject::Unsupported("unsupported WebP feature"),
        _ => Reject::Unsupported("invalid or unsupported WebP image"),
    }
}
