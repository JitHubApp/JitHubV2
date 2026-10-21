// Copyright (c) JitHub contributors. Licensed under MIT.

//! Bounded static WebP decoding for the isolated raster worker operation.
//!
//! The mapped source and destination are host-owned. Temporary decoded pixels,
//! the WebP decoder ledger, and a conservative ICC parser/transform reservation
//! share the caller's working-byte ceiling. The worker Job Object commit limit
//! remains the outer process guard for allocator and runtime overhead.

use std::io::Cursor;
use std::sync::atomic::{AtomicU8, Ordering};

use super::{
    align64, mapping_length, Reject, Request, Response, SharedMapping, HARD_MAX_OUTPUT_BYTES,
    KIND_RASTER_DECODE, PIXEL_BGRA, PIXEL_RGBA, PROGRESS_AREA_BYTES,
};
use image_webp::{DecodingError, WebPDecoder, WebPMetadataRanges};
use moxcms::{ColorProfile, Layout, ParsingOptions, Transform8BitExecutor, TransformOptions};
use resvg::tiny_skia::{
    BlendMode, ColorU8, FilterQuality, PixmapMut, PixmapPaint, PixmapRef, PixmapRowRing, Transform,
};
use sha2::{Digest, Sha256};

const MAX_SOURCE_BYTES: usize = 32 * 1024 * 1024;
const MAX_DIMENSION: u32 = 16_384;
const MAX_SOURCE_PIXELS: u64 = 40_000_000;
const MAX_ICC_PROFILE_BYTES: usize = 32 * 1024 * 1024;
const MAX_ICC_TAG_COUNT: usize = 128;
const MAX_ICC_TRANSFORM_DATA_BYTES: usize = 4 * 1024 * 1024;
const MAX_ICC_CLUT_GRID_POINTS: usize = 262_144;
const MAX_ICC_CLUT_STORAGE_BYTES: usize = 2 * 1024 * 1024;
const MAX_ICC_TRC_ELEMENTS: usize = 65_536;
const ICC_TRANSFORM_FIXED_RESERVATION: usize = 2 * 1024 * 1024;
const ICC_ALLOCATION_MULTIPLIER: usize = 6;
const ALLOCATION_BOOKKEEPING_BYTES: usize = 64;
const PROGRESS_MAPPED: u8 = 1;
const PROGRESS_HASHED: u8 = 2;
const PROGRESS_HEADER_VALIDATED: u8 = 3;
const PROGRESS_DECODED: u8 = 4;
const PROGRESS_COLOR_CONVERTED: u8 = 5;
const PROGRESS_RASTERIZED: u8 = 6;
const PROGRESS_COMPLETE: u8 = 7;

#[derive(Clone, Copy)]
struct DecodePlan {
    source_width: u32,
    source_height: u32,
    orientation: u32,
    intrinsic_width: u32,
    intrinsic_height: u32,
    target_width: u32,
    target_height: u32,
    output_length: usize,
    mapping_length: usize,
    working_limit: usize,
    pixel_format: u8,
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

fn validate_request(request: &Request) -> Result<DecodePlan, Reject> {
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

fn hard_working_limit() -> usize {
    #[cfg(target_pointer_width = "32")]
    {
        32 * 1024 * 1024
    }
    #[cfg(target_pointer_width = "64")]
    {
        64 * 1024 * 1024
    }
}

fn oriented_dimensions(width: u32, height: u32, orientation: u32) -> (u32, u32) {
    if (5..=8).contains(&orientation) {
        (height, width)
    } else {
        (width, height)
    }
}

fn decode_and_render(
    request: &Request,
    plan: &DecodePlan,
    source: &[u8],
    output: &mut [u8],
    progress: &AtomicU8,
) -> Result<Response, Reject> {
    let source_hash: [u8; 32] = Sha256::digest(source).into();
    if source_hash != request.hash {
        return Err(Reject::Worker("raster source hash mismatch"));
    }
    progress.store(PROGRESS_HASHED, Ordering::Release);

    let mapping_bytes = plan.mapping_length;
    let decoder_limit = plan
        .working_limit
        .checked_sub(mapping_bytes)
        .ok_or(Reject::Resource("raster working limit exceeded"))?;
    let mut decoder =
        WebPDecoder::new_static_bounded_with_metadata(Cursor::new(source), decoder_limit)
            .map_err(map_decode_error)?;

    let (actual_width, actual_height) = decoder.dimensions();
    if (actual_width, actual_height) != (plan.source_width, plan.source_height) {
        return Err(Reject::Unsupported(
            "WebP dimensions do not match admitted source",
        ));
    }
    let metadata = decoder.bounded_metadata_ranges().ok_or(Reject::Worker(
        "bounded WebP decoder did not expose metadata ranges",
    ))?;
    validate_metadata_ranges(&metadata, source.len())?;
    let icc = metadata
        .icc_profile
        .as_ref()
        .map(|range| &source[range.clone()]);
    let actual_orientation = match metadata.exif_metadata.as_ref() {
        Some(range) => parse_exif_orientation(&source[range.clone()])?,
        None => 1,
    };
    if actual_orientation != plan.orientation {
        return Err(Reject::Unsupported(
            "WebP orientation does not match admitted source",
        ));
    }
    // The row API streams VP8L (with or without alpha) and opaque VP8. VP8X
    // alpha reconstruction still materializes a full frame, so it must not
    // be selected as a lower-memory fallback.
    let can_stream_rows = !decoder.is_lossy() || !decoder.has_alpha();
    let icc_plan = icc.map(preflight_icc_profile).transpose()?;
    progress.store(PROGRESS_HEADER_VALIDATED, Ordering::Release);

    let direct_output = plan.orientation == 1
        && plan.source_width == plan.target_width
        && plan.source_height == plan.target_height;
    let direct_decode = direct_output && icc.is_none();
    let direct_color_output = direct_output && icc.is_some();
    let rgba_length = rgba_length(plan.source_width, plan.source_height)?;

    let decoded_length = if direct_decode { 0 } else { rgba_length };
    let converted_length = if icc.is_some() && !direct_color_output {
        rgba_length
    } else {
        0
    };
    let requested_scratch_charge = requested_allocation_charge(decoded_length)?
        .checked_add(requested_allocation_charge(converted_length)?)
        .ok_or(Reject::Resource("raster scratch accounting overflow"))?;
    let cms_reservation = icc_plan.as_ref().map(|plan| plan.reservation).unwrap_or(0);
    let requested_reservation = requested_scratch_charge
        .checked_add(cms_reservation)
        .ok_or(Reject::Resource("raster scratch accounting overflow"))?;
    let decoder_used_before = decoder.bounded_allocation_bytes().ok_or(Reject::Worker(
        "bounded WebP decoder did not report its allocation ledger",
    ))?;
    let full_decoder_budget = admit_scratch_budget(
        plan.working_limit,
        mapping_bytes,
        requested_reservation,
        decoder_used_before,
    );

    if full_decoder_budget.is_err() && can_stream_rows {
        return decode_streamed_rows(
            request,
            plan,
            output,
            progress,
            &mut decoder,
            icc,
            icc_plan.as_ref(),
            mapping_bytes,
        );
    }

    let preadmitted_decoder_budget = full_decoder_budget?;
    decoder
        .lower_bounded_allocation_limit(preadmitted_decoder_budget)
        .map_err(map_decode_error)?;

    // Requested scratch capacities and the decoder's existing ledger are
    // admitted before allocating either pixel buffer. After allocation, charge
    // the actual Vec capacities (which may exceed the requested lengths) and
    // tighten the decoder ledger again before it performs pixel work.
    let mut decoded = if direct_decode {
        None
    } else {
        Some(allocate_zeroed(rgba_length)?)
    };
    let mut color_converted = if icc.is_some() && !direct_color_output {
        Some(allocate_zeroed(rgba_length)?)
    } else {
        None
    };
    let scratch_charge = decoded
        .as_ref()
        .map(allocation_charge)
        .transpose()?
        .unwrap_or(0)
        .checked_add(
            color_converted
                .as_ref()
                .map(allocation_charge)
                .transpose()?
                .unwrap_or(0),
        )
        .ok_or(Reject::Resource("raster scratch accounting overflow"))?;
    let decoder_budget = plan
        .working_limit
        .checked_sub(mapping_bytes)
        .and_then(|remaining| remaining.checked_sub(scratch_charge))
        .and_then(|remaining| remaining.checked_sub(cms_reservation))
        .ok_or(Reject::Resource("raster scratch exceeds working limit"))?;
    let decoder_used_after_scratch = decoder.bounded_allocation_bytes().ok_or(Reject::Worker(
        "bounded WebP decoder did not report its allocation ledger",
    ))?;
    if decoder_used_after_scratch > decoder_budget {
        return Err(Reject::Resource(
            "WebP decoder allocations exceed raster working limit",
        ));
    }
    decoder
        .lower_bounded_allocation_limit(decoder_budget)
        .map_err(map_decode_error)?;

    let icc_transform = match (icc, icc_plan.as_ref()) {
        (Some(profile), Some(plan)) => Some(create_icc_transform(profile, plan)?),
        (None, None) => None,
        _ => return Err(Reject::Worker("ICC preflight state mismatch")),
    };

    let decode_result = {
        let decode_destination = decoded.as_deref_mut().unwrap_or_else(|| &mut output[..]);
        decoder.read_static_rgba_bounded_with_metadata(decode_destination)
    };
    if let Err(error) = decode_result {
        // The decoder can discover its full-frame working set only while
        // decoding. If the bounded ledger refuses that allocation, release
        // every full-frame buffer and rebuild from the immutable mapped
        // source before trying the lower-memory row path. Do not retry
        // malformed/unsupported data or any non-budget failure.
        if matches!(&error, DecodingError::MemoryLimitExceeded) && can_stream_rows {
            drop(icc_transform);
            drop(color_converted);
            drop(decoded);
            drop(decoder);
            output.fill(0);
            let mut retry_decoder = reopen_verified_decoder(
                source,
                plan,
                icc,
                plan.orientation,
                decoder_limit,
            )?;
            return decode_streamed_rows(
                request,
                plan,
                output,
                progress,
                &mut retry_decoder,
                icc,
                icc_plan.as_ref(),
                mapping_bytes,
            );
        }
        return Err(map_decode_error(error));
    }
    progress.store(PROGRESS_DECODED, Ordering::Release);

    if direct_decode {
        // The decoder wrote straight RGBA directly into the caller's output
        // mapping. No source scratch buffer aliases this slice.
        premultiply_rgba_in_place(output);
        progress.store(PROGRESS_COLOR_CONVERTED, Ordering::Release);
        if plan.pixel_format == PIXEL_BGRA {
            swap_red_blue(output);
        }
    } else if direct_color_output {
        let source_pixels = decoded
            .as_deref()
            .ok_or(Reject::Worker("missing ICC source buffer"))?;
        apply_icc_transform(
            icc_transform
                .as_ref()
                .ok_or(Reject::Worker("missing ICC transform"))?,
            source_pixels,
            output,
        )?;
        premultiply_rgba_in_place(output);
        progress.store(PROGRESS_COLOR_CONVERTED, Ordering::Release);
        if plan.pixel_format == PIXEL_BGRA {
            swap_red_blue(output);
        }
    } else {
        let decoded_pixels = decoded
            .as_deref_mut()
            .ok_or(Reject::Worker("missing decoded WebP source buffer"))?;
        let raster_source = if icc.is_some() {
            let destination = color_converted
                .as_deref_mut()
                .ok_or(Reject::Worker("missing ICC destination buffer"))?;
            apply_icc_transform(
                icc_transform
                    .as_ref()
                    .ok_or(Reject::Worker("missing ICC transform"))?,
                decoded_pixels,
                destination,
            )?;
            premultiply_rgba_in_place(destination);
            destination
        } else {
            premultiply_rgba_in_place(decoded_pixels);
            decoded_pixels
        };
        progress.store(PROGRESS_COLOR_CONVERTED, Ordering::Release);
        rasterize_oriented(
            raster_source,
            plan.source_width,
            plan.source_height,
            plan.orientation,
            plan.target_width,
            plan.target_height,
            output,
        )?;
        if plan.pixel_format == PIXEL_BGRA {
            swap_red_blue(output);
        }
    }

    let decoder_used = decoder.bounded_allocation_bytes().ok_or(Reject::Worker(
        "bounded WebP decoder did not report its allocation ledger",
    ))?;
    let peak_used = plan
        .mapping_length
        .checked_add(scratch_charge)
        .and_then(|total| total.checked_add(cms_reservation))
        .and_then(|total| total.checked_add(decoder_used))
        .ok_or(Reject::Resource("raster working accounting overflow"))?;
    if peak_used > plan.working_limit {
        return Err(Reject::Resource("raster working limit exceeded"));
    }

    progress.store(PROGRESS_RASTERIZED, Ordering::Release);
    let metadata = super::Metadata {
        width: f64::from(plan.intrinsic_width),
        height: f64::from(plan.intrinsic_height),
        ..super::Metadata::default()
    };
    let response = Response::ok(
        request,
        metadata,
        request.output_length,
        plan.target_width,
        plan.target_height,
    );
    progress.store(PROGRESS_COMPLETE, Ordering::Release);
    Ok(response)
}

fn reopen_verified_decoder<'a>(
    source: &'a [u8],
    plan: &DecodePlan,
    expected_icc: Option<&[u8]>,
    expected_orientation: u32,
    decoder_limit: usize,
) -> Result<WebPDecoder<Cursor<&'a [u8]>>, Reject> {
    let decoder = WebPDecoder::new_static_bounded_with_metadata(Cursor::new(source), decoder_limit)
        .map_err(map_decode_error)?;
    if decoder.dimensions() != (plan.source_width, plan.source_height) {
        return Err(Reject::Worker("WebP dimensions changed during bounded retry"));
    }
    let metadata = decoder.bounded_metadata_ranges().ok_or(Reject::Worker(
        "bounded WebP decoder did not expose metadata ranges during retry",
    ))?;
    validate_metadata_ranges(&metadata, source.len())?;
    let actual_icc = metadata
        .icc_profile
        .as_ref()
        .map(|range| &source[range.clone()]);
    let actual_orientation = match metadata.exif_metadata.as_ref() {
        Some(range) => parse_exif_orientation(&source[range.clone()])?,
        None => 1,
    };
    if actual_icc != expected_icc || actual_orientation != expected_orientation {
        return Err(Reject::Worker("WebP metadata changed during bounded retry"));
    }
    Ok(decoder)
}

#[allow(clippy::too_many_arguments)] fn decode_streamed_rows(
    request: &Request,
    plan: &DecodePlan,
    output: &mut [u8],
    progress: &AtomicU8,
    decoder: &mut WebPDecoder<Cursor<&[u8]>>,
    icc: Option<&[u8]>,
    icc_plan: Option<&IccProfilePlan>,
    mapping_bytes: usize,
) -> Result<Response, Reject> {
    if decoder.is_lossy() && decoder.has_alpha() {
        return Err(Reject::Resource(
            "lossy WebP alpha frame exceeds bounded full-frame working memory",
        ));
    }

    let transposed = plan.orientation >= 5;
    let (intermediate_width, intermediate_height) = if transposed {
        (plan.target_height, plan.target_width)
    } else {
        (plan.target_width, plan.target_height)
    };
    let source_row_bytes = rgba_length(plan.source_width, 1)?;
    let needs_resize = intermediate_width != plan.source_width
        || intermediate_height != plan.source_height;
    let ring_height = if needs_resize {
        plan.source_height.min(4)
    } else {
        0
    };
    let ring_length = source_row_bytes
        .checked_mul(ring_height as usize)
        .ok_or(Reject::Resource("raster row ring size overflow"))?;
    let output_row_length = rgba_length(intermediate_width, 1)?;
    let requested_scratch_charge = requested_allocation_charge(ring_length)?
        .checked_add(requested_allocation_charge(output_row_length)?)
        .ok_or(Reject::Resource("raster row scratch accounting overflow"))?;
    let cms_reservation = icc_plan.map(|icc| icc.reservation).unwrap_or(0);
    let decoder_used_before = decoder.bounded_allocation_bytes().ok_or(Reject::Worker(
        "bounded WebP decoder did not report its allocation ledger",
    ))?;
    let _preadmitted_decoder_budget = admit_scratch_budget(
        plan.working_limit,
        mapping_bytes,
        requested_scratch_charge
            .checked_add(cms_reservation)
            .ok_or(Reject::Resource("raster row scratch accounting overflow"))?,
        decoder_used_before,
    )?;

    let mut row_ring = if needs_resize {
        allocate_zeroed(ring_length)?
    } else {
        Vec::new()
    };
    let mut output_row = allocate_zeroed(output_row_length)?;
    let ring_charge = if needs_resize {
        allocation_charge(&row_ring)?
    } else {
        0
    };
    let scratch_charge = ring_charge
        .checked_add(allocation_charge(&output_row)?)
        .ok_or(Reject::Resource("raster row scratch accounting overflow"))?;
    let decoder_budget = plan
        .working_limit
        .checked_sub(mapping_bytes)
        .and_then(|remaining| remaining.checked_sub(scratch_charge))
        .and_then(|remaining| remaining.checked_sub(cms_reservation))
        .ok_or(Reject::Resource("raster row scratch exceeds working limit"))?;
    let decoder_used_after_scratch = decoder.bounded_allocation_bytes().ok_or(Reject::Worker(
        "bounded WebP decoder did not report its allocation ledger",
    ))?;
    if decoder_used_after_scratch > decoder_budget {
        return Err(Reject::Resource(
            "WebP decoder allocations exceed raster working limit",
        ));
    }
    decoder
        .lower_bounded_allocation_limit(decoder_budget)
        .map_err(map_decode_error)?;

    let icc_transform = match (icc, icc_plan) {
        (Some(profile), Some(plan)) => Some(create_icc_transform(profile, plan)?),
        (None, None) => None,
        _ => return Err(Reject::Worker("ICC preflight state mismatch")),
    };
    let transform = Transform::from_scale(
        intermediate_width as f32 / plan.source_width as f32,
        intermediate_height as f32 / plan.source_height as f32,
    );
    if !transform.is_finite() {
        return Err(Reject::Resource("WebP resize transform overflow"));
    }

    let mut next_source_row = 0_u32;
    let mut next_output_row = 0_u32;
    output.fill(0);
    decoder
        .read_static_rgba_rows_bounded_with_metadata(|row_y, rgba_row| {
            if row_y != next_source_row || rgba_row.len() != source_row_bytes {
                return Err(Reject::Worker("WebP row decoder returned invalid row order"));
            }
            next_source_row = next_source_row
                .checked_add(1)
                .ok_or(Reject::Resource("WebP row index overflow"))?;

            if needs_resize {
                // Drain rows whose taps are resident before overwriting the
                // modulo slot for the next source row.
                if row_y > 0 {
                    render_ready_streamed_rows(
                        plan,
                        intermediate_width,
                        intermediate_height,
                        row_y - 1,
                        &mut next_output_row,
                        ring_height,
                        &row_ring,
                        &mut output_row,
                        transform,
                        output,
                    )?;
                }

                let ring_slot = (row_y % ring_height) as usize * source_row_bytes;
                let ring_row = &mut row_ring[ring_slot..ring_slot + source_row_bytes];
                if let Some(transform) = icc_transform.as_ref() {
                    apply_icc_transform(transform, rgba_row, ring_row)?;
                } else {
                    ring_row.copy_from_slice(rgba_row);
                }
                premultiply_rgba_in_place(ring_row);
                render_ready_streamed_rows(
                    plan,
                    intermediate_width,
                    intermediate_height,
                    row_y,
                    &mut next_output_row,
                    ring_height,
                    &row_ring,
                    &mut output_row,
                    transform,
                    output,
                )?;
            } else {
                // With no resize the legacy path copies the decoded raster
                // before applying EXIF flips/transposition; do the same
                // mapping directly from each decoder row.
                if let Some(transform) = icc_transform.as_ref() {
                    apply_icc_transform(transform, rgba_row, &mut output_row)?;
                } else {
                    output_row.copy_from_slice(rgba_row);
                }
                premultiply_rgba_in_place(&mut output_row);
                if plan.pixel_format == PIXEL_BGRA {
                    swap_red_blue(&mut output_row);
                }
                scatter_unscaled_oriented_row(plan, row_y, &output_row, output)?;
            }
            Ok::<(), Reject>(())
        })
        .map_err(|error| match error {
            image_webp::RowDecodeError::Decoder(error) => map_decode_error(error),
            image_webp::RowDecodeError::Consumer(error) => error,
        })?;

    if next_source_row != plan.source_height {
        return Err(Reject::Worker("WebP row decoder returned an incomplete image"));
    }
    while needs_resize && next_output_row < intermediate_height {
        render_streamed_output_row(
            plan,
            intermediate_width,
            intermediate_height,
            next_output_row,
            ring_height,
            &row_ring,
            &mut output_row,
            transform,
            output,
        )?;
        next_output_row += 1;
    }
    progress.store(PROGRESS_DECODED, Ordering::Release);
    progress.store(PROGRESS_COLOR_CONVERTED, Ordering::Release);

    let decoder_used = decoder.bounded_allocation_bytes().ok_or(Reject::Worker(
        "bounded WebP decoder did not report its allocation ledger",
    ))?;
    let peak_used = mapping_bytes
        .checked_add(scratch_charge)
        .and_then(|total| total.checked_add(cms_reservation))
        .and_then(|total| total.checked_add(decoder_used))
        .ok_or(Reject::Resource("raster working accounting overflow"))?;
    if peak_used > plan.working_limit {
        return Err(Reject::Resource("raster working limit exceeded"));
    }

    progress.store(PROGRESS_RASTERIZED, Ordering::Release);
    let metadata = super::Metadata {
        width: f64::from(plan.intrinsic_width),
        height: f64::from(plan.intrinsic_height),
        ..super::Metadata::default()
    };
    let response = Response::ok(
        request,
        metadata,
        request.output_length,
        plan.target_width,
        plan.target_height,
    );
    progress.store(PROGRESS_COMPLETE, Ordering::Release);
    Ok(response)
}

#[allow(clippy::too_many_arguments)] fn render_ready_streamed_rows(
    plan: &DecodePlan,
    intermediate_width: u32,
    intermediate_height: u32,
    available_source_row: u32,
    next_output_row: &mut u32,
    ring_height: u32,
    row_ring: &[u8],
    output_row: &mut [u8],
    transform: Transform,
    output: &mut [u8],
) -> Result<(), Reject> {
    while *next_output_row < intermediate_height {
        let (minimum, maximum) = PixmapMut::required_bicubic_source_y_range(
            plan.source_height,
            *next_output_row,
            intermediate_height,
            transform,
        )
        .ok_or(Reject::Worker("invalid bicubic row sampling geometry"))?;
        if maximum > available_source_row {
            break;
        }
        if maximum.saturating_sub(minimum) >= ring_height
            || available_source_row.saturating_sub(minimum) >= ring_height
        {
            return Err(Reject::Worker("WebP source row ring lost a required sample"));
        }
        render_streamed_output_row(
            plan,
            intermediate_width,
            intermediate_height,
            *next_output_row,
            ring_height,
            row_ring,
            output_row,
            transform,
            output,
        )?;
        *next_output_row += 1;
    }
    Ok(())
}

fn scatter_unscaled_oriented_row(
    plan: &DecodePlan,
    source_y: u32,
    source_row: &[u8],
    output: &mut [u8],
) -> Result<(), Reject> {
    if source_row.len() != rgba_length(plan.source_width, 1)? {
        return Err(Reject::Worker("invalid unscaled WebP source row"));
    }
    for source_x in 0..plan.source_width {
        let (destination_x, destination_y) = match plan.orientation {
            1 => (source_x, source_y),
            2 => (plan.source_width - 1 - source_x, source_y),
            3 => (
                plan.source_width - 1 - source_x,
                plan.source_height - 1 - source_y,
            ),
            4 => (source_x, plan.source_height - 1 - source_y),
            5 => (source_y, source_x),
            6 => (plan.target_width - 1 - source_y, source_x),
            7 => (
                plan.target_width - 1 - source_y,
                plan.target_height - 1 - source_x,
            ),
            8 => (source_y, plan.target_height - 1 - source_x),
            _ => return Err(Reject::Unsupported("invalid WebP orientation")),
        };
        let source_offset = source_x as usize * 4;
        let destination_offset = (destination_y as usize)
            .checked_mul(plan.target_width as usize)
            .and_then(|value| value.checked_add(destination_x as usize))
            .and_then(|value| value.checked_mul(4))
            .ok_or(Reject::Resource("WebP output pixel index overflow"))?;
        let destination = output
            .get_mut(destination_offset..destination_offset + 4)
            .ok_or(Reject::Worker("WebP output pixel mapping is out of bounds"))?;
        destination.copy_from_slice(&source_row[source_offset..source_offset + 4]);
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)] fn render_streamed_output_row(
    plan: &DecodePlan,
    intermediate_width: u32,
    intermediate_height: u32,
    output_y: u32,
    ring_height: u32,
    row_ring: &[u8],
    output_row: &mut [u8],
    transform: Transform,
    output: &mut [u8],
) -> Result<(), Reject> {
    let source = PixmapRowRing::from_bytes(
        row_ring,
        plan.source_width,
        plan.source_height,
        ring_height,
    )
    .ok_or(Reject::Resource("invalid WebP source row ring"))?;
    let mut row = PixmapMut::from_bytes(output_row, intermediate_width, 1)
        .ok_or(Reject::Resource("invalid WebP output row"))?;
    let paint = PixmapPaint {
        opacity: 1.0,
        blend_mode: BlendMode::SourceOver,
        quality: FilterQuality::Bicubic,
    };
    if !row.draw_pixmap_row_from_ring(output_y, intermediate_height, source, &paint, transform) {
        return Err(Reject::Resource("WebP row sampler rejected its geometry"));
    }

    let transposed = plan.orientation >= 5;
    let target_width = plan.target_width as usize;
    let intermediate_width = intermediate_width as usize;
    for x in 0..intermediate_width {
        let (destination_x, destination_y) = match plan.orientation {
            1 => (x as u32, output_y),
            2 => (intermediate_width as u32 - 1 - x as u32, output_y),
            3 => (
                intermediate_width as u32 - 1 - x as u32,
                intermediate_height - 1 - output_y,
            ),
            4 => (x as u32, intermediate_height - 1 - output_y),
            5 => (output_y, x as u32),
            6 => (plan.target_width - 1 - output_y, x as u32),
            7 => (
                plan.target_width - 1 - output_y,
                plan.target_height - 1 - x as u32,
            ),
            8 => (output_y, plan.target_height - 1 - x as u32),
            _ => return Err(Reject::Unsupported("invalid WebP orientation")),
        };
        if transposed && destination_y >= plan.target_height {
            return Err(Reject::Worker("WebP orientation row mapping overflow"));
        }
        let source_offset = x * 4;
        let destination_offset = ((destination_y as usize)
            .checked_mul(target_width)
            .and_then(|value| value.checked_add(destination_x as usize))
            .and_then(|value| value.checked_mul(4)))
        .ok_or(Reject::Resource("WebP output pixel index overflow"))?;
        if destination_offset + 4 > output.len() || source_offset + 4 > output_row.len() {
            return Err(Reject::Worker("WebP output pixel mapping is out of bounds"));
        }
        output[destination_offset..destination_offset + 4]
            .copy_from_slice(&output_row[source_offset..source_offset + 4]);
        if plan.pixel_format == PIXEL_BGRA {
            output[destination_offset..destination_offset + 4].swap(0, 2);
        }
    }
    Ok(())
}

fn validate_metadata_ranges(
    metadata: &WebPMetadataRanges,
    source_length: usize,
) -> Result<(), Reject> {
    for range in [
        metadata.icc_profile.as_ref(),
        metadata.exif_metadata.as_ref(),
    ]
    .into_iter()
    .flatten()
    {
        if range.start >= range.end || range.end > source_length {
            return Err(Reject::Unsupported("invalid WebP metadata range"));
        }
    }
    Ok(())
}

#[derive(Clone, Copy, Default)]
struct IccTagEntry {
    signature: u32,
    offset: usize,
    length: usize,
}

struct IccProfilePlan {
    entries: [IccTagEntry; MAX_ICC_TAG_COUNT],
    entry_count: usize,
    compact_size: usize,
    reservation: usize,
}

fn preflight_icc_profile(profile: &[u8]) -> Result<IccProfilePlan, Reject> {
    if profile.len() < 132 || profile.len() > MAX_ICC_PROFILE_BYTES {
        return Err(Reject::Unsupported("invalid embedded ICC profile size"));
    }
    let declared_size = read_icc_u32(profile, 0)? as usize;
    if declared_size != profile.len()
        || !matches!(profile[8], 2 | 4)
        || &profile[16..20] != b"RGB "
        || (&profile[20..24] != b"XYZ " && &profile[20..24] != b"Lab ")
        || &profile[36..40] != b"acsp"
    {
        return Err(Reject::Unsupported(
            "invalid or unsupported embedded ICC profile",
        ));
    }

    let tag_count = read_icc_u32(profile, 128)? as usize;
    if tag_count == 0 || tag_count > MAX_ICC_TAG_COUNT {
        return Err(Reject::Unsupported("embedded ICC tag count exceeds limit"));
    }
    let table_end = 132usize
        .checked_add(
            tag_count
                .checked_mul(12)
                .ok_or(Reject::Resource("embedded ICC tag table size overflow"))?,
        )
        .ok_or(Reject::Resource("embedded ICC tag table size overflow"))?;
    if table_end > profile.len() {
        return Err(Reject::Unsupported("truncated embedded ICC tag table"));
    }

    let mut entries = [IccTagEntry::default(); MAX_ICC_TAG_COUNT];
    let mut entry_count = 0usize;
    let mut transform_data_bytes = 0usize;
    let mut expanded_curve_bytes = 0usize;
    for index in 0..tag_count {
        let record = 132 + index * 12;
        let signature = read_icc_u32(profile, record)?;
        let offset = read_icc_u32(profile, record + 4)? as usize;
        let length = read_icc_u32(profile, record + 8)? as usize;

        // Duplicate signatures are invalid ICC structure. Checking them in a
        // bounded table avoids relying on the parser's duplicate-tag behavior.
        for previous in 0..index {
            if read_icc_u32(profile, 132 + previous * 12)? == signature {
                return Err(Reject::Unsupported("duplicate embedded ICC tag"));
            }
        }
        let end = offset
            .checked_add(length)
            .ok_or(Reject::Resource("embedded ICC tag range overflow"))?;
        if length < 8 || offset < table_end || offset % 4 != 0 || end > profile.len() {
            return Err(Reject::Unsupported("invalid embedded ICC tag range"));
        }

        if is_unsupported_rendering_transform_tag(signature) {
            // moxcms does not implement the ICC v4 D2B/B2D tag families. Do
            // not strip them and accidentally fall back to a different
            // matrix/LUT transform that may also be present in the profile.
            return Err(Reject::Unsupported(
                "embedded ICC contains an unsupported rendering transform",
            ));
        }

        if !is_rendering_icc_tag(signature) {
            // The CMS does not need descriptive text, device strings, gamut
            // previews, or measurement metadata to convert RGB pixels. Those
            // tags are omitted from the compact parser input to prevent MLUC
            // shared-offset records from amplifying into unbounded Strings.
            continue;
        }

        let tag = &profile[offset..end];
        expanded_curve_bytes = expanded_curve_bytes
            .checked_add(validate_rendering_icc_tag(signature, tag)?)
            .ok_or(Reject::Resource("embedded ICC curve workspace overflow"))?;
        transform_data_bytes = transform_data_bytes
            .checked_add(length)
            .ok_or(Reject::Resource("embedded ICC transform data overflow"))?;
        if transform_data_bytes > MAX_ICC_TRANSFORM_DATA_BYTES {
            return Err(Reject::Resource(
                "embedded ICC transform data exceeds limit",
            ));
        }
        entries[entry_count] = IccTagEntry {
            signature,
            offset,
            length,
        };
        entry_count += 1;
    }

    if entry_count == 0 {
        return Err(Reject::Unsupported(
            "embedded ICC has no RGB transform tags",
        ));
    }

    let mut compact_size = align4_checked(
        132usize
            .checked_add(
                entry_count
                    .checked_mul(12)
                    .ok_or(Reject::Resource("embedded ICC compact size overflow"))?,
            )
            .ok_or(Reject::Resource("embedded ICC compact size overflow"))?,
    )?;
    for entry in entries.iter().take(entry_count) {
        compact_size = align4_checked(compact_size)?
            .checked_add(entry.length)
            .ok_or(Reject::Resource("embedded ICC compact size overflow"))?;
    }
    if compact_size > MAX_ICC_PROFILE_BYTES || compact_size > u32::MAX as usize {
        return Err(Reject::Resource(
            "embedded ICC compact profile exceeds limit",
        ));
    }

    // moxcms materializes decoded curve/CLUT values and may build fixed RGB
    // interpolation tables. Bound the parser's copied values by the total
    // admitted transform-tag bytes (including aliases copied as separate
    // entries) and each nested mAB/mBA curve-chain occurrence. The latter is
    // counted per parser occurrence because ICC offsets may legally alias the
    // same three curves across A/M/B stages. A fixed allowance covers the
    // documented 33^3 RGB tables and per-channel 8-bit inverse tables. The
    // worker Job Object remains the process-wide commit guard; this reservation
    // is not an RSS measurement.
    let reservation = compact_size
        .checked_add(
            transform_data_bytes
                .checked_mul(ICC_ALLOCATION_MULTIPLIER)
                .ok_or(Reject::Resource("embedded ICC workspace overflow"))?,
        )
        .and_then(|value| {
            expanded_curve_bytes
                .checked_mul(ICC_ALLOCATION_MULTIPLIER)
                .and_then(|curve_bytes| value.checked_add(curve_bytes))
        })
        .and_then(|value| value.checked_add(ICC_TRANSFORM_FIXED_RESERVATION))
        .and_then(|value| value.checked_add(ALLOCATION_BOOKKEEPING_BYTES))
        .ok_or(Reject::Resource("embedded ICC workspace overflow"))?;

    Ok(IccProfilePlan {
        entries,
        entry_count,
        compact_size,
        reservation,
    })
}

fn is_rendering_icc_tag(signature: u32) -> bool {
    [
        *b"rXYZ", *b"gXYZ", *b"bXYZ", *b"rTRC", *b"gTRC", *b"bTRC", *b"wtpt", *b"bkpt", *b"chad",
        *b"cicp", *b"A2B0", *b"A2B1", *b"A2B2", *b"B2A0", *b"B2A1", *b"B2A2",
    ]
    .iter()
    .any(|candidate| u32::from_be_bytes(*candidate) == signature)
}

fn is_unsupported_rendering_transform_tag(signature: u32) -> bool {
    [
        *b"D2B0", *b"D2B1", *b"D2B2", *b"D2B3", *b"B2D0", *b"B2D1", *b"B2D2", *b"B2D3",
    ]
    .iter()
    .any(|candidate| u32::from_be_bytes(*candidate) == signature)
}

fn validate_rendering_icc_tag(signature: u32, tag: &[u8]) -> Result<usize, Reject> {
    let is_lut = [*b"A2B0", *b"A2B1", *b"A2B2", *b"B2A0", *b"B2A1", *b"B2A2"]
        .iter()
        .any(|candidate| u32::from_be_bytes(*candidate) == signature);
    if is_lut {
        return validate_icc_lut_tag(tag);
    }

    if [*b"rTRC", *b"gTRC", *b"bTRC"]
        .iter()
        .any(|candidate| u32::from_be_bytes(*candidate) == signature)
    {
        return validate_icc_curve(tag, 0);
    }
    let expected_type: &[u8; 4] = if signature == u32::from_be_bytes(*b"chad") {
        b"sf32"
    } else if signature == u32::from_be_bytes(*b"cicp") {
        b"cicp"
    } else {
        b"XYZ "
    };
    if tag.get(..4) != Some(expected_type.as_slice()) {
        return Err(Reject::Unsupported("unsupported embedded ICC tag type"));
    }
    let minimum_length = if expected_type == b"sf32" {
        44
    } else if expected_type == b"cicp" {
        12
    } else {
        20
    };
    if tag.len() < minimum_length {
        return Err(Reject::Unsupported("truncated embedded ICC color tag"));
    }
    Ok(0)
}

fn validate_icc_lut_tag(tag: &[u8]) -> Result<usize, Reject> {
    if tag.len() < 48 {
        return Err(Reject::Unsupported("truncated embedded ICC LUT"));
    }
    let kind = &tag[..4];
    if kind == b"mft1" || kind == b"mft2" {
        if tag[8] != 3 || tag[9] != 3 {
            return Err(Reject::Unsupported(
                "embedded ICC LUT must have three input and output channels",
            ));
        }
        let grid = usize::from(tag[10]);
        let grid_points = grid
            .checked_mul(grid)
            .and_then(|value| value.checked_mul(grid))
            .ok_or(Reject::Resource("embedded ICC CLUT size overflow"))?;
        if grid < 2 || grid_points > MAX_ICC_CLUT_GRID_POINTS {
            return Err(Reject::Resource("embedded ICC CLUT exceeds limit"));
        }
        let (header_bytes, input_entries, output_entries, sample_bytes) = if kind == b"mft1" {
            (48usize, 256usize, 256usize, 1usize)
        } else {
            if tag.len() < 52 {
                return Err(Reject::Unsupported("truncated embedded ICC mft2 LUT"));
            }
            let input = usize::from(u16::from_be_bytes([tag[48], tag[49]]));
            let output = usize::from(u16::from_be_bytes([tag[50], tag[51]]));
            if !(2..=4096).contains(&input) || !(2..=4096).contains(&output) {
                return Err(Reject::Unsupported("invalid embedded ICC mft2 table size"));
            }
            (52usize, input, output, 2usize)
        };
        let input_bytes = input_entries
            .checked_mul(3)
            .and_then(|value| value.checked_mul(sample_bytes))
            .ok_or(Reject::Resource("embedded ICC table size overflow"))?;
        let clut_bytes = grid_points
            .checked_mul(3)
            .and_then(|value| value.checked_mul(sample_bytes))
            .ok_or(Reject::Resource("embedded ICC CLUT size overflow"))?;
        let output_bytes = output_entries
            .checked_mul(3)
            .and_then(|value| value.checked_mul(sample_bytes))
            .ok_or(Reject::Resource("embedded ICC table size overflow"))?;
        let clut_storage = input_bytes
            .checked_add(clut_bytes)
            .and_then(|value| value.checked_add(output_bytes))
            .ok_or(Reject::Resource("embedded ICC LUT storage overflow"))?;
        if clut_bytes > MAX_ICC_CLUT_STORAGE_BYTES
            || header_bytes
                .checked_add(clut_storage)
                .is_none_or(|required| required > tag.len())
        {
            return Err(Reject::Unsupported(
                "truncated or oversized embedded ICC LUT",
            ));
        }
        return Ok(0);
    }

    if kind != b"mAB " && kind != b"mBA " {
        return Err(Reject::Unsupported("unsupported embedded ICC LUT type"));
    }
    if tag[8] != 3 || tag[9] != 3 {
        return Err(Reject::Unsupported(
            "embedded ICC LUT must have three input and output channels",
        ));
    }
    let offsets = [
        read_icc_u32(tag, 12)? as usize,
        read_icc_u32(tag, 16)? as usize,
        read_icc_u32(tag, 20)? as usize,
        read_icc_u32(tag, 24)? as usize,
        read_icc_u32(tag, 28)? as usize,
    ];
    for (index, offset) in offsets.iter().copied().enumerate() {
        // The mAB/mBA fixed header is 32 bytes. In particular, a B-curve
        // chain may begin immediately after it. The matrix stage, when
        // present, occupies 48 bytes at its own offset.
        if offset != 0 && (offset < 32 || offset >= tag.len()) {
            return Err(Reject::Unsupported("invalid embedded ICC LUT stage offset"));
        }
        if offset != 0 && offset % 4 != 0 {
            return Err(Reject::Unsupported("unaligned embedded ICC LUT stage offset"));
        }
        if index == 1 && offset != 0 && offset.checked_add(48).is_none_or(|end| end > tag.len()) {
            return Err(Reject::Unsupported("truncated embedded ICC matrix stage"));
        }
    }
    // ICC mAB/mBA stages are ordered B, matrix, M, CLUT, A. The B curves
    // are required; matrix and M must occur together, as must CLUT and A.
    // Rejecting incomplete stage groups prevents moxcms from interpreting a
    // structurally different fallback than the profile author supplied.
    if offsets[0] == 0
        || (offsets[1] == 0) != (offsets[2] == 0)
        || (offsets[3] == 0) != (offsets[4] == 0)
    {
        return Err(Reject::Unsupported(
            "incomplete embedded ICC LUT stage group",
        ));
    }
    let clut_offset = offsets[3];
    if clut_offset != 0 {
        let header_end = clut_offset
            .checked_add(20)
            .ok_or(Reject::Resource("embedded ICC CLUT header overflow"))?;
        if header_end > tag.len() {
            return Err(Reject::Unsupported("truncated embedded ICC CLUT header"));
        }
        let mut grid_points = 1usize;
        for dimension in &tag[clut_offset..clut_offset + 3] {
            let dimension = usize::from(*dimension);
            if dimension < 2 {
                return Err(Reject::Unsupported("invalid embedded ICC CLUT grid"));
            }
            grid_points = grid_points
                .checked_mul(dimension)
                .ok_or(Reject::Resource("embedded ICC CLUT size overflow"))?;
        }
        if grid_points > MAX_ICC_CLUT_GRID_POINTS {
            return Err(Reject::Resource("embedded ICC CLUT exceeds limit"));
        }
        let entry_bytes = usize::from(tag[clut_offset + 16]);
        if !matches!(entry_bytes, 1 | 2) {
            return Err(Reject::Unsupported("invalid embedded ICC CLUT entry size"));
        }
        let clut_bytes = grid_points
            .checked_mul(3)
            .and_then(|value| value.checked_mul(entry_bytes))
            .ok_or(Reject::Resource("embedded ICC CLUT size overflow"))?;
        if clut_bytes > MAX_ICC_CLUT_STORAGE_BYTES
            || header_end
                .checked_add(clut_bytes)
                .is_none_or(|end| end > tag.len())
        {
            return Err(Reject::Unsupported(
                "truncated or oversized embedded ICC CLUT",
            ));
        }
    }
    let mut expanded_curve_bytes = 0usize;
    // mAB/mBA offset order is B, matrix, M, CLUT, A. Count every stage's
    // nested curves independently, including aliased offsets, because the
    // parser materializes each stage separately.
    for offset in [offsets[4], offsets[2], offsets[0]] {
        if offset != 0 {
            expanded_curve_bytes = expanded_curve_bytes
                .checked_add(validate_icc_curve_chain(tag, offset, 3)?)
                .ok_or(Reject::Resource("embedded ICC curve workspace overflow"))?;
        }
    }
    Ok(expanded_curve_bytes)
}

fn validate_icc_curve_chain(tag: &[u8], offset: usize, count: usize) -> Result<usize, Reject> {
    let mut cursor = offset;
    let mut payload_bytes = 0usize;
    for _ in 0..count {
        let curve_length = validate_icc_curve(tag, cursor)?;
        payload_bytes = payload_bytes
            .checked_add(curve_length)
            .ok_or(Reject::Resource("embedded ICC curve workspace overflow"))?;
        cursor = cursor
            .checked_add(curve_length)
            .ok_or(Reject::Resource("embedded ICC curve offset overflow"))?;
        cursor = align4_checked(cursor)?;
        if cursor > tag.len() {
            return Err(Reject::Unsupported("truncated embedded ICC curve set"));
        }
    }
    Ok(payload_bytes)
}

fn validate_icc_curve(tag: &[u8], offset: usize) -> Result<usize, Reject> {
    let header = tag
        .get(
            offset
                ..offset
                    .checked_add(12)
                    .ok_or(Reject::Resource("embedded ICC curve offset overflow"))?,
        )
        .ok_or(Reject::Unsupported("truncated embedded ICC curve"))?;
    let length = if &header[..4] == b"curv" {
        let count = read_icc_u32(tag, offset + 8)? as usize;
        if count > MAX_ICC_TRC_ELEMENTS {
            return Err(Reject::Resource("embedded ICC TRC exceeds limit"));
        }
        12usize
            .checked_add(
                count
                    .checked_mul(2)
                    .ok_or(Reject::Resource("embedded ICC curve size overflow"))?,
            )
            .ok_or(Reject::Resource("embedded ICC curve size overflow"))?
    } else if &header[..4] == b"para" {
        let function = u16::from_be_bytes([header[8], header[9]]) as usize;
        let parameter_count =
            [1usize, 3, 4, 5, 7]
                .get(function)
                .copied()
                .ok_or(Reject::Unsupported(
                    "unsupported embedded ICC parametric TRC",
                ))?;
        12usize
            .checked_add(
                parameter_count
                    .checked_mul(4)
                    .ok_or(Reject::Resource("embedded ICC curve size overflow"))?,
            )
            .ok_or(Reject::Resource("embedded ICC curve size overflow"))?
    } else {
        return Err(Reject::Unsupported("unsupported embedded ICC curve type"));
    };
    let end = offset
        .checked_add(length)
        .ok_or(Reject::Resource("embedded ICC curve range overflow"))?;
    if end > tag.len() {
        return Err(Reject::Unsupported("truncated embedded ICC curve data"));
    }
    Ok(length)
}

fn read_icc_u32(bytes: &[u8], offset: usize) -> Result<u32, Reject> {
    let raw = bytes
        .get(
            offset
                ..offset
                    .checked_add(4)
                    .ok_or(Reject::Resource("embedded ICC offset overflow"))?,
        )
        .ok_or(Reject::Unsupported("truncated embedded ICC structure"))?;
    Ok(u32::from_be_bytes([raw[0], raw[1], raw[2], raw[3]]))
}

fn align4_checked(value: usize) -> Result<usize, Reject> {
    value
        .checked_add(3)
        .map(|rounded| rounded & !3)
        .ok_or(Reject::Resource("embedded ICC alignment overflow"))
}

fn create_icc_transform(
    source_profile: &[u8],
    plan: &IccProfilePlan,
) -> Result<std::sync::Arc<Transform8BitExecutor>, Reject> {
    let mut compact = Vec::new();
    compact
        .try_reserve_exact(plan.compact_size)
        .map_err(|_| Reject::Resource("ICC compact profile allocation failed"))?;
    compact.resize(plan.compact_size, 0);
    compact[..128].copy_from_slice(&source_profile[..128]);
    compact[0..4].copy_from_slice(&(plan.compact_size as u32).to_be_bytes());
    compact[128..132].copy_from_slice(&(plan.entry_count as u32).to_be_bytes());

    let mut data_offset = align4_checked(132 + plan.entry_count * 12)?;
    for (index, entry) in plan.entries.iter().take(plan.entry_count).enumerate() {
        data_offset = align4_checked(data_offset)?;
        let record = 132 + index * 12;
        compact[record..record + 4].copy_from_slice(&entry.signature.to_be_bytes());
        compact[record + 4..record + 8].copy_from_slice(&(data_offset as u32).to_be_bytes());
        compact[record + 8..record + 12].copy_from_slice(&(entry.length as u32).to_be_bytes());
        let data_end = data_offset
            .checked_add(entry.length)
            .ok_or(Reject::Resource("ICC compact profile offset overflow"))?;
        compact[data_offset..data_end]
            .copy_from_slice(&source_profile[entry.offset..entry.offset + entry.length]);
        data_offset = data_end;
    }
    if data_offset != plan.compact_size {
        return Err(Reject::Worker("ICC compact profile size mismatch"));
    }

    let parsing_options = ParsingOptions {
        max_profile_size: plan
            .compact_size
            .checked_add(1)
            .ok_or(Reject::Resource("ICC parser limit overflow"))?,
        max_allowed_clut_size: MAX_ICC_CLUT_GRID_POINTS,
        max_allowed_trc_size: MAX_ICC_TRC_ELEMENTS,
    };
    let parsed = ColorProfile::new_from_slice_with_options(&compact, parsing_options)
        .map_err(|_| Reject::Unsupported("embedded ICC profile is invalid or unsupported"))?;
    if parsed.color_space != moxcms::DataColorSpace::Rgb {
        return Err(Reject::Unsupported("embedded ICC profile is not RGB"));
    }
    let srgb = ColorProfile::new_srgb();
    parsed
        .create_transform_8bit(
            Layout::Rgba,
            &srgb,
            Layout::Rgba,
            TransformOptions::default(),
        )
        .map_err(|_| Reject::Unsupported("embedded ICC transform is unsupported"))
}

fn apply_icc_transform(
    transform: &std::sync::Arc<Transform8BitExecutor>,
    source_rgba: &[u8],
    destination_rgba: &mut [u8],
) -> Result<(), Reject> {
    if source_rgba.len() != destination_rgba.len() || source_rgba.len() % 4 != 0 {
        return Err(Reject::Resource("ICC transform buffer geometry mismatch"));
    }
    transform
        .transform(source_rgba, destination_rgba)
        .map_err(|_| Reject::Unsupported("embedded ICC color conversion failed"))
}

fn rgba_length(width: u32, height: u32) -> Result<usize, Reject> {
    usize::try_from(width)
        .ok()
        .and_then(|width| {
            usize::try_from(height)
                .ok()
                .and_then(|height| width.checked_mul(height))
        })
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or(Reject::Resource("raster pixel buffer size overflow"))
}

fn allocate_zeroed(length: usize) -> Result<Vec<u8>, Reject> {
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(length)
        .map_err(|_| Reject::Resource("raster scratch allocation failed"))?;
    bytes.resize(length, 0);
    Ok(bytes)
}

fn allocation_charge(bytes: &Vec<u8>) -> Result<usize, Reject> {
    bytes
        .capacity()
        .checked_add(ALLOCATION_BOOKKEEPING_BYTES)
        .ok_or(Reject::Resource("raster scratch accounting overflow"))
}

fn requested_allocation_charge(length: usize) -> Result<usize, Reject> {
    if length == 0 {
        return Ok(0);
    }
    length
        .checked_add(ALLOCATION_BOOKKEEPING_BYTES)
        .ok_or(Reject::Resource("raster scratch accounting overflow"))
}

fn admit_scratch_budget(
    working_limit: usize,
    mapping_bytes: usize,
    requested_scratch_charge: usize,
    decoder_used: usize,
) -> Result<usize, Reject> {
    let decoder_budget = working_limit
        .checked_sub(mapping_bytes)
        .and_then(|remaining| remaining.checked_sub(requested_scratch_charge))
        .ok_or(Reject::Resource("raster scratch exceeds working limit"))?;
    if decoder_used > decoder_budget {
        return Err(Reject::Resource(
            "WebP decoder allocations exceed raster working limit",
        ));
    }
    Ok(decoder_budget)
}

fn premultiply_rgba_in_place(rgba: &mut [u8]) {
    for pixel in rgba.chunks_exact_mut(4) {
        let premultiplied =
            ColorU8::from_rgba(pixel[0], pixel[1], pixel[2], pixel[3]).premultiply();
        pixel[0] = premultiplied.red();
        pixel[1] = premultiplied.green();
        pixel[2] = premultiplied.blue();
        pixel[3] = premultiplied.alpha();
    }
}

fn swap_red_blue(bytes: &mut [u8]) {
    for pixel in bytes.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
}

fn rasterize_oriented(
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

fn transpose_pixels_in_place(
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

fn bitmap_contains(bitmap: &[u8], index: usize) -> bool {
    bitmap[index / 8] & (1 << (index % 8)) != 0
}

fn bitmap_insert(bitmap: &mut [u8], index: usize) {
    bitmap[index / 8] |= 1 << (index % 8);
}

fn flip_horizontal(pixels: &mut [u8], width: u32, height: u32) {
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

fn flip_vertical(pixels: &mut [u8], width: u32, height: u32) {
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

fn parse_exif_orientation(exif: &[u8]) -> Result<u32, Reject> {
    let tiff = exif.strip_prefix(b"Exif\0\0").unwrap_or(exif);
    if tiff.len() < 8 {
        return Err(Reject::Unsupported("invalid WebP EXIF metadata"));
    }
    let little_endian = if &tiff[..2] == b"II" {
        true
    } else if &tiff[..2] == b"MM" {
        false
    } else {
        return Err(Reject::Unsupported("invalid WebP EXIF byte order"));
    };
    if read_tiff_u16(tiff, 2, little_endian) != Some(42) {
        return Err(Reject::Unsupported("invalid WebP EXIF signature"));
    }
    let ifd_offset = read_tiff_u32(tiff, 4, little_endian)
        .and_then(|offset| usize::try_from(offset).ok())
        .ok_or(Reject::Unsupported("invalid WebP EXIF IFD offset"))?;
    let mut state = TiffState {
        visited_offsets: [0; 4],
        visited_count: 0,
        orientation_count: 0,
        orientation: 1,
    };
    if !validate_tiff_ifd(tiff, ifd_offset, little_endian, 0, &mut state) {
        return Err(Reject::Unsupported("invalid WebP EXIF directory"));
    }
    Ok(state.orientation)
}

struct TiffState {
    visited_offsets: [usize; 4],
    visited_count: usize,
    orientation_count: usize,
    orientation: u32,
}

fn validate_tiff_ifd(
    data: &[u8],
    offset: usize,
    little_endian: bool,
    depth: usize,
    state: &mut TiffState,
) -> bool {
    if depth >= state.visited_offsets.len()
        || offset > data.len().saturating_sub(2)
        || state.visited_count >= state.visited_offsets.len()
    {
        return false;
    }
    if state.visited_offsets[..state.visited_count].contains(&offset) {
        return false;
    }
    state.visited_offsets[state.visited_count] = offset;
    state.visited_count += 1;

    let Some(entry_count) = read_tiff_u16(data, offset, little_endian).map(usize::from) else {
        return false;
    };
    let Some(entries_bytes) = entry_count.checked_mul(12) else {
        return false;
    };
    let Some(entries_start) = offset.checked_add(2) else {
        return false;
    };
    let Some(next_ifd_position) = entries_start.checked_add(entries_bytes) else {
        return false;
    };
    if next_ifd_position > data.len().saturating_sub(4) {
        return false;
    }

    let mut has_thumbnail_offset = false;
    let mut has_thumbnail_length = false;
    let mut thumbnail_offset = 0u32;
    let mut thumbnail_length = 0u32;
    for index in 0..entry_count {
        let Some(entry) = entries_start.checked_add(index * 12) else {
            return false;
        };
        let (Some(tag), Some(kind), Some(count)) = (
            read_tiff_u16(data, entry, little_endian),
            read_tiff_u16(data, entry + 2, little_endian),
            read_tiff_u32(data, entry + 4, little_endian),
        ) else {
            return false;
        };
        let Some(type_width) = tiff_type_width(kind) else {
            return false;
        };
        if count == 0 {
            return false;
        }
        let value_bytes = u64::from(count) * type_width as u64;
        if value_bytes > i32::MAX as u64 {
            return false;
        }
        if value_bytes > 4 {
            let Some(value_offset) = read_tiff_u32(data, entry + 8, little_endian)
                .and_then(|value| usize::try_from(value).ok())
            else {
                return false;
            };
            if value_offset > data.len() || value_bytes > (data.len() - value_offset) as u64 {
                return false;
            }
        }

        if tag == 0x0112 {
            state.orientation_count += 1;
            if depth != 0 || state.orientation_count != 1 || kind != 3 || count != 1 {
                return false;
            }
            let Some(value) = read_tiff_u16(data, entry + 8, little_endian) else {
                return false;
            };
            if !(1..=8).contains(&value) {
                return false;
            }
            state.orientation = u32::from(value);
        } else if is_ifd_pointer_tag(tag) {
            if !matches!(kind, 4 | 13) || count != 1 {
                return false;
            }
            let Some(child_offset) = read_tiff_u32(data, entry + 8, little_endian) else {
                return false;
            };
            if child_offset != 0 {
                let Ok(child_offset) = usize::try_from(child_offset) else {
                    return false;
                };
                if !validate_tiff_ifd(data, child_offset, little_endian, depth + 1, state) {
                    return false;
                }
            }
        } else if tag == 0x0201 {
            if kind != 4 || count != 1 {
                return false;
            }
            has_thumbnail_offset = true;
            let Some(value) = read_tiff_u32(data, entry + 8, little_endian) else {
                return false;
            };
            thumbnail_offset = value;
        } else if tag == 0x0202 {
            if kind != 4 || count != 1 {
                return false;
            }
            has_thumbnail_length = true;
            let Some(value) = read_tiff_u32(data, entry + 8, little_endian) else {
                return false;
            };
            thumbnail_length = value;
        }
    }

    if has_thumbnail_offset != has_thumbnail_length
        || (has_thumbnail_offset
            && (thumbnail_offset as u64 > data.len() as u64
                || u64::from(thumbnail_length) > data.len() as u64 - u64::from(thumbnail_offset)))
    {
        return false;
    }
    let Some(next_ifd) = read_tiff_u32(data, next_ifd_position, little_endian) else {
        return false;
    };
    if next_ifd != 0 {
        let Ok(next_ifd) = usize::try_from(next_ifd) else {
            return false;
        };
        if !validate_tiff_ifd(data, next_ifd, little_endian, depth + 1, state) {
            return false;
        }
    }
    true
}

fn tiff_type_width(kind: u16) -> Option<u32> {
    match kind {
        1 | 2 | 6 | 7 => Some(1),
        3 | 8 => Some(2),
        4 | 9 | 11 | 13 => Some(4),
        5 | 10 | 12 => Some(8),
        _ => None,
    }
}

fn is_ifd_pointer_tag(tag: u16) -> bool {
    matches!(tag, 0x014A | 0x8769 | 0x8825 | 0xA005)
}

fn read_tiff_u16(data: &[u8], offset: usize, little_endian: bool) -> Option<u16> {
    let bytes: [u8; 2] = data.get(offset..offset.checked_add(2)?)?.try_into().ok()?;
    Some(if little_endian {
        u16::from_le_bytes(bytes)
    } else {
        u16::from_be_bytes(bytes)
    })
}

fn read_tiff_u32(data: &[u8], offset: usize, little_endian: bool) -> Option<u32> {
    let bytes: [u8; 4] = data.get(offset..offset.checked_add(4)?)?.try_into().ok()?;
    Some(if little_endian {
        u32::from_le_bytes(bytes)
    } else {
        u32::from_be_bytes(bytes)
    })
}

fn map_decode_error(error: DecodingError) -> Reject {
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

#[cfg(test)]
mod tests {
    use super::*;
    use image_webp::{ColorType, WebPEncoder};

    #[test]
    fn exif_orientation_accepts_both_endian_encodings_and_optional_prefix() {
        assert_eq!(
            parse_exif_orientation(&tiff_orientation(true, 6)).unwrap(),
            6
        );
        let mut big_endian = tiff_orientation(false, 8);
        assert_eq!(parse_exif_orientation(&big_endian).unwrap(), 8);
        let mut prefixed = b"Exif\0\0".to_vec();
        prefixed.extend_from_slice(&tiff_orientation(true, 5));
        assert_eq!(parse_exif_orientation(&prefixed).unwrap(), 5);
        big_endian.clear();
        assert!(parse_exif_orientation(&big_endian).is_err());
    }

    #[test]
    fn exif_orientation_rejects_duplicate_invalid_and_out_of_bounds_tags() {
        let mut invalid_orientation = tiff_orientation(true, 6);
        invalid_orientation[18..20].copy_from_slice(&9u16.to_le_bytes());
        assert!(parse_exif_orientation(&invalid_orientation).is_err());

        let mut duplicate = tiff_orientation(true, 1);
        duplicate[8..10].copy_from_slice(&2u16.to_le_bytes());
        duplicate.resize(38, 0);
        duplicate[22..24].copy_from_slice(&0x0112u16.to_le_bytes());
        duplicate[24..26].copy_from_slice(&3u16.to_le_bytes());
        duplicate[26..30].copy_from_slice(&1u32.to_le_bytes());
        duplicate[30..32].copy_from_slice(&1u16.to_le_bytes());
        assert!(parse_exif_orientation(&duplicate).is_err());

        let mut bad_offset = tiff_orientation(true, 1);
        bad_offset[4..8].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(parse_exif_orientation(&bad_offset).is_err());
    }

    #[test]
    fn scratch_is_admitted_before_full_source_pixel_buffers_are_allocated() {
        let source_rgba = rgba_length(2048, 8193).unwrap();
        let requested_scratch = requested_allocation_charge(source_rgba)
            .unwrap()
            .checked_add(requested_allocation_charge(source_rgba).unwrap())
            .unwrap();

        // The mapping + source decode + ICC destination exceed the worker cap.
        // This path rejects on size arithmetic, before allocate_zeroed runs.
        assert!(matches!(
            admit_scratch_budget(64 * 1024 * 1024, 2 * 1024 * 1024, requested_scratch, 0,),
            Err(Reject::Resource(_))
        ));
    }

    #[test]
    fn decoder_ledger_is_included_before_scratch_allocation() {
        let requested_scratch = requested_allocation_charge(1024).unwrap();
        assert!(matches!(
            admit_scratch_budget(4096, 1024, requested_scratch, 1985),
            Err(Reject::Resource(_))
        ));
        assert_eq!(
            admit_scratch_budget(4096, 1024, requested_scratch, 1984).unwrap(),
            1984
        );
    }

    #[test]
    fn streamed_resampling_matches_full_raster_for_all_exif_orientations() {
        let source_width = 9;
        let source_height = 11;
        let mut source = vec![0_u8; rgba_length(source_width, source_height).unwrap()];
        for y in 0..source_height {
            for x in 0..source_width {
                let offset = ((y * source_width + x) * 4) as usize;
                let alpha = 72 + ((x * 17 + y * 11) % 184) as u8;
                let color = ColorU8::from_rgba(
                    ((x * 29 + y * 13) % 256) as u8,
                    ((x * 7 + y * 31) % 256) as u8,
                    ((x * 23 + y * 5) % 256) as u8,
                    alpha,
                )
                .premultiply();
                source[offset..offset + 4].copy_from_slice(&[
                    color.red(),
                    color.green(),
                    color.blue(),
                    color.alpha(),
                ]);
            }
        }

        for orientation in 1..=8 {
            let transposed = orientation >= 5;
            for (intermediate_width, intermediate_height) in [(7, 8), (1, 3), (8, 11)] {
                let (target_width, target_height) = if transposed {
                    (intermediate_height, intermediate_width)
                } else {
                    (intermediate_width, intermediate_height)
                };
                let plan = DecodePlan {
                    source_width,
                    source_height,
                    orientation,
                    intrinsic_width: target_width,
                    intrinsic_height: target_height,
                    target_width,
                    target_height,
                    output_length: rgba_length(target_width, target_height).unwrap(),
                    mapping_length: 0,
                    working_limit: 64 * 1024 * 1024,
                    pixel_format: PIXEL_RGBA,
                };
                let scale = Transform::from_scale(
                    intermediate_width as f32 / source_width as f32,
                    intermediate_height as f32 / source_height as f32,
                );
                let ring_height = source_height.min(4);
                let row_bytes = rgba_length(source_width, 1).unwrap();
                let mut ring = vec![0; row_bytes * ring_height as usize];
                let mut output_row = vec![0; rgba_length(intermediate_width, 1).unwrap()];
                let mut streamed = vec![0; plan.output_length];
                let mut next_output_row = 0;

                for source_y in 0..source_height {
                    if source_y > 0 {
                        render_ready_streamed_rows(
                            &plan,
                            intermediate_width,
                            intermediate_height,
                            source_y - 1,
                            &mut next_output_row,
                            ring_height,
                            &ring,
                            &mut output_row,
                            scale,
                            &mut streamed,
                        )
                        .unwrap();
                    }
                    let ring_offset = (source_y % ring_height) as usize * row_bytes;
                    let source_offset = source_y as usize * row_bytes;
                    ring[ring_offset..ring_offset + row_bytes]
                        .copy_from_slice(&source[source_offset..source_offset + row_bytes]);
                    render_ready_streamed_rows(
                        &plan,
                        intermediate_width,
                        intermediate_height,
                        source_y,
                        &mut next_output_row,
                        ring_height,
                        &ring,
                        &mut output_row,
                        scale,
                        &mut streamed,
                    )
                    .unwrap();
                }
                assert_eq!(next_output_row, intermediate_height);

                let mut full_source = source.clone();
                let mut expected = vec![0; plan.output_length];
                rasterize_oriented(
                    &mut full_source,
                    source_width,
                    source_height,
                    orientation,
                    target_width,
                    target_height,
                    &mut expected,
                )
                .unwrap();
                assert_eq!(streamed, expected, "orientation={orientation}, intermediate={intermediate_width}x{intermediate_height}");
            }
        }
    }

    #[test]
    fn low_budget_opaque_vp8_dispatch_matches_full_frame_resize() {
        const SOURCE_WIDTH: u32 = 256;
        const SOURCE_HEIGHT: u32 = 256;
        const TARGET_WIDTH: u32 = 64;
        const TARGET_HEIGHT: u32 = 64;
        const WORKING_LIMIT: usize = 200 * 1024;

        // Generated with the test-only libwebp encoder at quality 75.0 from
        // the seeded LCG pattern documented by the fixture generator. Keeping
        // encoded bytes here avoids adding an encoder to the worker's
        // production dependency graph.
        let encoded: &[u8] = include_bytes!("../tests/fixtures/opaque-vp8-256.webp");
        assert_eq!(&encoded[12..16], b"VP8 ");

        let source_length = encoded.len() as u32;
        let output_length = rgba_length(TARGET_WIDTH, TARGET_HEIGHT).unwrap();
        let mapping_bytes = align64(encoded.len())
            .checked_add(output_length)
            .and_then(|length| length.checked_add(PROGRESS_AREA_BYTES))
            .unwrap();
        assert!(mapping_bytes < WORKING_LIMIT);
        assert!(requested_allocation_charge(rgba_length(SOURCE_WIDTH, SOURCE_HEIGHT).unwrap())
            .unwrap()
            > WORKING_LIMIT - mapping_bytes);

        let plan = DecodePlan {
            source_width: SOURCE_WIDTH,
            source_height: SOURCE_HEIGHT,
            orientation: 1,
            intrinsic_width: TARGET_WIDTH,
            intrinsic_height: TARGET_HEIGHT,
            target_width: TARGET_WIDTH,
            target_height: TARGET_HEIGHT,
            output_length,
            mapping_length: mapping_bytes,
            working_limit: WORKING_LIMIT,
            pixel_format: PIXEL_RGBA,
        };
        let request = Request {
            kind: KIND_RASTER_DECODE,
            request_id: 1,
            nonce_low: 2,
            nonce_high: 3,
            flags: 0,
            source_length,
            output_length: output_length as u32,
            target_width: TARGET_WIDTH,
            target_height: TARGET_HEIGHT,
            tile_x: 0,
            tile_y: 0,
            tile_width: 0,
            tile_height: 0,
            max_xml_depth: 0,
            max_nested_svg_depth: 0,
            max_structural_cost: 0,
            max_embedded_bytes: 0,
            max_embedded_pixels: 0,
            max_output_bytes: 0,
            max_filter_bytes: 0,
            max_cache_bytes: 0,
            font_generation: 0,
            color_scheme: 0,
            pixel_format: PIXEL_RGBA,
            semantic_color: 0,
            document_id: 0,
            hash: Sha256::digest(encoded).into(),
            locale: String::new(),
            mapping_handle: 0,
            physical_viewport: None,
            working_bytes: WORKING_LIMIT as u64,
            source_width: SOURCE_WIDTH,
            source_height: SOURCE_HEIGHT,
            orientation: 1,
        };

        // Confirm the fixture is eligible for the bounded opaque-VP8 row API,
        // and that full-frame scratch cannot pass the same worker limit.
        let mut preflight_decoder =
            WebPDecoder::new_static_bounded_with_metadata(Cursor::new(encoded), 4 * 1024 * 1024)
                .unwrap();
        assert!(preflight_decoder.is_lossy());
        assert!(!preflight_decoder.has_alpha());
        let full_scratch = requested_allocation_charge(rgba_length(SOURCE_WIDTH, SOURCE_HEIGHT).unwrap())
            .unwrap();
        assert!(admit_scratch_budget(
            WORKING_LIMIT,
            mapping_bytes,
            full_scratch,
            preflight_decoder.bounded_allocation_bytes().unwrap(),
        )
        .is_err());

        let mut actual = vec![0; output_length];
        decode_and_render(
            &request,
            &plan,
            encoded,
            &mut actual,
            &AtomicU8::new(0),
        )
        .unwrap();

        let mut full_decoder =
            WebPDecoder::new_static_bounded_with_metadata(Cursor::new(encoded), 8 * 1024 * 1024)
                .unwrap();
        let mut full_source = vec![0; rgba_length(SOURCE_WIDTH, SOURCE_HEIGHT).unwrap()];
        full_decoder
            .read_static_rgba_bounded_with_metadata(&mut full_source)
            .unwrap();
        premultiply_rgba_in_place(&mut full_source);
        let mut expected = vec![0; output_length];
        rasterize_oriented(
            &mut full_source,
            SOURCE_WIDTH,
            SOURCE_HEIGHT,
            1,
            TARGET_WIDTH,
            TARGET_HEIGHT,
            &mut expected,
        )
        .unwrap();
        assert_eq!(actual, expected);
    }

    #[test]
    fn icc_preflight_compacts_non_rendering_mluc_and_bounds_transform_memory() {
        let profile = minimal_matrix_profile(0x0400_0000, true);
        let plan = preflight_icc_profile(&profile).unwrap();
        assert!(plan.compact_size < profile.len());
        assert!(plan.reservation >= plan.compact_size + ICC_TRANSFORM_FIXED_RESERVATION);

        let transform = create_icc_transform(&profile, &plan).unwrap();
        let source = [128, 90, 40, 127, 12, 200, 70, 255];
        let mut destination = [0; 8];
        apply_icc_transform(&transform, &source, &mut destination).unwrap();
        assert_eq!(destination[3], source[3]);
        assert_eq!(destination[7], source[7]);
    }

    #[test]
    fn icc_preflight_admits_v2_v4_matrix_and_mft2_rgb_profiles() {
        for version in [0x0200_0000, 0x0430_0000] {
            let profile = minimal_matrix_profile(version, false);
            assert!(preflight_icc_profile(&profile).is_ok());
        }

        let profile = minimal_mft2_profile();
        let plan = preflight_icc_profile(&profile).unwrap();
        let transform = create_icc_transform(&profile, &plan).unwrap();
        let source = [0, 64, 255, 93];
        let mut destination = [0; 4];
        apply_icc_transform(&transform, &source, &mut destination).unwrap();
        assert_eq!(destination[3], 93);
    }

    #[test]
    fn icc_preflight_admits_mab_and_mba_curve_chains_at_header_boundary() {
        for (tag_type, signature) in [(b"mAB ", *b"A2B0"), (b"mBA ", *b"B2A0")] {
            let profile = minimal_abm_profile(tag_type, signature);
            let plan = preflight_icc_profile(&profile).unwrap();
            assert!(plan.reservation > plan.compact_size);
            let profile = minimal_abm_matrix_profile(tag_type, signature);
            assert!(preflight_icc_profile(&profile).is_ok());
        }
    }

    #[test]
    fn icc_preflight_rejects_incomplete_or_unaligned_mab_mba_stage_groups() {
        for (tag_type, signature) in [(b"mAB ", *b"A2B0"), (b"mBA ", *b"B2A0")] {
            // Matrix without its required M curves.
            let mut profile = minimal_abm_matrix_profile(tag_type, signature);
            let tag_offset = read_icc_u32(&profile, 136).unwrap() as usize;
            profile[tag_offset + 20..tag_offset + 24].fill(0);
            assert!(matches!(
                preflight_icc_profile(&profile),
                Err(Reject::Unsupported(_))
            ));

            // CLUT without its required A curves.
            let mut invalid_clut_tag = minimal_abm_profile_tag(tag_type);
            invalid_clut_tag.resize(128, 0);
            let mut profile = build_icc_profile(
                0x0430_0000,
                vec![
                    (signature, invalid_clut_tag),
                    (*b"wtpt", icc_xyz(0.9642, 1.0, 0.8249)),
                ],
            );
            let tag_offset = read_icc_u32(&profile, 136).unwrap() as usize;
            profile[tag_offset + 24..tag_offset + 28].copy_from_slice(&80_u32.to_be_bytes());
            assert!(matches!(
                preflight_icc_profile(&profile),
                Err(Reject::Unsupported(_))
            ));

            // B curves at an otherwise-valid location must be four-byte aligned.
            let mut profile = minimal_abm_profile(tag_type, signature);
            let tag_offset = read_icc_u32(&profile, 136).unwrap() as usize;
            profile[tag_offset + 12..tag_offset + 16].copy_from_slice(&33_u32.to_be_bytes());
            assert!(matches!(
                preflight_icc_profile(&profile),
                Err(Reject::Unsupported(_))
            ));
        }
    }

    #[test]
    fn icc_preflight_rejects_non_rgb_lut_channels_and_oversized_cluts() {
        let mut profile = minimal_mft2_profile();
        let tag_offset = read_icc_u32(&profile, 136).unwrap() as usize;
        profile[tag_offset + 8] = 4;
        assert!(matches!(
            preflight_icc_profile(&profile),
            Err(Reject::Unsupported(_))
        ));

        let mut profile = minimal_mft2_profile();
        let tag_offset = read_icc_u32(&profile, 136).unwrap() as usize;
        profile[tag_offset + 10] = 255;
        assert!(matches!(
            preflight_icc_profile(&profile),
            Err(Reject::Resource(_))
        ));
    }

    #[test]
    fn icc_preflight_rejects_duplicate_tags_and_profile_table_amplification() {
        let mut profile = minimal_matrix_profile(0x0400_0000, false);
        // The second table record repeats the first tag signature. The parser
        // must reject it before allocating duplicate channel structures.
        let first_signature = [profile[132], profile[133], profile[134], profile[135]];
        profile[144..148].copy_from_slice(&first_signature);
        assert!(matches!(
            preflight_icc_profile(&profile),
            Err(Reject::Unsupported(_))
        ));

        let mut profile = minimal_matrix_profile(0x0400_0000, false);
        let tag_count = read_icc_u32(&profile, 128).unwrap();
        profile[128..132].copy_from_slice(&((MAX_ICC_TAG_COUNT as u32) + 1).to_be_bytes());
        assert!(matches!(
            preflight_icc_profile(&profile),
            Err(Reject::Unsupported(_))
        ));
        profile[128..132].copy_from_slice(&tag_count.to_be_bytes());
    }

    #[test]
    fn icc_preflight_rejects_unhandled_v4_transform_even_with_matrix_fallback() {
        let unsupported_transform = b"D2B0\0\0\0\0".to_vec();
        let mut tags = vec![
            (*b"rXYZ", icc_xyz(0.4361, 0.2225, 0.0139)),
            (*b"gXYZ", icc_xyz(0.3851, 0.7169, 0.0971)),
            (*b"bXYZ", icc_xyz(0.1431, 0.0606, 0.7142)),
            (*b"rTRC", icc_gamma_curve()),
            (*b"gTRC", icc_gamma_curve()),
            (*b"bTRC", icc_gamma_curve()),
            (*b"wtpt", icc_xyz(0.9642, 1.0, 0.8249)),
        ];
        tags.push((*b"D2B0", unsupported_transform));
        let profile = build_icc_profile(0x0430_0000, tags);
        assert!(matches!(
            preflight_icc_profile(&profile),
            Err(Reject::Unsupported(_))
        ));
    }

    #[test]
    fn all_exif_orientations_rasterize_exactly_without_axis_drift() {
        let source_width = 2;
        let source_height = 3;
        let source = [
            10, 0, 0, 255, 20, 0, 0, 255, // row 0
            30, 0, 0, 255, 40, 0, 0, 255, // row 1
            50, 0, 0, 255, 60, 0, 0, 255, // row 2
        ];
        let expected: [&[u8]; 8] = [
            &[
                10, 0, 0, 255, 20, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255, 50, 0, 0, 255, 60, 0,
                0, 255,
            ],
            &[
                20, 0, 0, 255, 10, 0, 0, 255, 40, 0, 0, 255, 30, 0, 0, 255, 60, 0, 0, 255, 50, 0,
                0, 255,
            ],
            &[
                60, 0, 0, 255, 50, 0, 0, 255, 40, 0, 0, 255, 30, 0, 0, 255, 20, 0, 0, 255, 10, 0,
                0, 255,
            ],
            &[
                50, 0, 0, 255, 60, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255, 10, 0, 0, 255, 20, 0,
                0, 255,
            ],
            &[
                10, 0, 0, 255, 30, 0, 0, 255, 50, 0, 0, 255, 20, 0, 0, 255, 40, 0, 0, 255, 60, 0,
                0, 255,
            ],
            &[
                50, 0, 0, 255, 30, 0, 0, 255, 10, 0, 0, 255, 60, 0, 0, 255, 40, 0, 0, 255, 20, 0,
                0, 255,
            ],
            &[
                60, 0, 0, 255, 40, 0, 0, 255, 20, 0, 0, 255, 50, 0, 0, 255, 30, 0, 0, 255, 10, 0,
                0, 255,
            ],
            &[
                20, 0, 0, 255, 40, 0, 0, 255, 60, 0, 0, 255, 10, 0, 0, 255, 30, 0, 0, 255, 50, 0,
                0, 255,
            ],
        ];

        for orientation in 1..=8 {
            let (width, height) = oriented_dimensions(source_width, source_height, orientation);
            let mut output = vec![0; (width * height * 4) as usize];
            let mut source_pixels = source;
            rasterize_oriented(
                &mut source_pixels,
                source_width,
                source_height,
                orientation,
                width,
                height,
                &mut output,
            )
            .unwrap();
            assert_eq!(
                output.as_slice(),
                expected[(orientation - 1) as usize],
                "orientation={orientation}"
            );
        }
    }

    #[test]
    fn scaled_exif_orientations_match_positive_resize_then_pixel_mapping() {
        let source_width = 4;
        let source_height = 3;
        let mut source = Vec::with_capacity((source_width * source_height * 4) as usize);
        for index in 0..source_width * source_height {
            source.extend_from_slice(&[
                (index * 17) as u8,
                (index * 11 + 3) as u8,
                (240 - index * 9) as u8,
                255,
            ]);
        }

        for orientation in 1..=8 {
            let (target_width, target_height) = if orientation < 5 { (3, 2) } else { (2, 3) };
            let (intermediate_width, intermediate_height) = if orientation < 5 {
                (target_width, target_height)
            } else {
                (target_height, target_width)
            };
            let mut intermediate = vec![0; (intermediate_width * intermediate_height * 4) as usize];
            {
                let source_pixmap =
                    PixmapRef::from_bytes(&source, source_width, source_height).unwrap();
                let mut resized = PixmapMut::from_bytes(
                    &mut intermediate,
                    intermediate_width,
                    intermediate_height,
                )
                .unwrap();
                resized.draw_pixmap(
                    0,
                    0,
                    source_pixmap,
                    &PixmapPaint {
                        opacity: 1.0,
                        blend_mode: BlendMode::SourceOver,
                        quality: FilterQuality::Bicubic,
                    },
                    Transform::from_scale(
                        intermediate_width as f32 / source_width as f32,
                        intermediate_height as f32 / source_height as f32,
                    ),
                    None,
                );
            }

            let mut expected = vec![0; intermediate.len()];
            for y in 0..intermediate_height {
                for x in 0..intermediate_width {
                    let (destination_x, destination_y) = match orientation {
                        1 => (x, y),
                        2 => (intermediate_width - 1 - x, y),
                        3 => (intermediate_width - 1 - x, intermediate_height - 1 - y),
                        4 => (x, intermediate_height - 1 - y),
                        5 => (y, x),
                        6 => (intermediate_height - 1 - y, x),
                        7 => (intermediate_height - 1 - y, intermediate_width - 1 - x),
                        8 => (y, intermediate_width - 1 - x),
                        _ => unreachable!(),
                    };
                    let from = ((y * intermediate_width + x) * 4) as usize;
                    let to = ((destination_y * target_width + destination_x) * 4) as usize;
                    expected[to..to + 4].copy_from_slice(&intermediate[from..from + 4]);
                }
            }

            let mut source_pixels = source.clone();
            let mut actual = vec![0; expected.len()];
            rasterize_oriented(
                &mut source_pixels,
                source_width,
                source_height,
                orientation,
                target_width,
                target_height,
                &mut actual,
            )
            .unwrap();
            assert_eq!(actual, expected, "orientation={orientation}");
        }
    }

    #[test]
    fn in_place_transpose_uses_bounded_rounded_bitset_and_rejects_short_scratch() {
        let mut pixels = [
            1, 0, 0, 255, 2, 0, 0, 255, 3, 0, 0, 255, 4, 0, 0, 255, 5, 0, 0, 255, 6, 0, 0, 255,
        ];
        let mut scratch = [0_u8; 1];
        transpose_pixels_in_place(&mut pixels, 3, 2, &mut scratch).unwrap();
        assert_eq!(
            pixels,
            [1, 0, 0, 255, 4, 0, 0, 255, 2, 0, 0, 255, 5, 0, 0, 255, 3, 0, 0, 255, 6, 0, 0, 255,]
        );

        let mut pixels_3x3 = [
            1, 0, 0, 255, 2, 0, 0, 255, 3, 0, 0, 255, 4, 0, 0, 255, 5, 0, 0, 255, 6, 0, 0, 255, 7,
            0, 0, 255, 8, 0, 0, 255, 9, 0, 0, 255,
        ];
        let mut scratch_3x3 = [0_u8; 2];
        transpose_pixels_in_place(&mut pixels_3x3, 3, 3, &mut scratch_3x3).unwrap();
        assert_eq!(
            pixels_3x3,
            [
                1, 0, 0, 255, 4, 0, 0, 255, 7, 0, 0, 255, 2, 0, 0, 255, 5, 0, 0, 255, 8, 0, 0, 255,
                3, 0, 0, 255, 6, 0, 0, 255, 9, 0, 0, 255,
            ]
        );

        let mut unchanged = [1_u8; 24];
        let before = unchanged;
        assert!(matches!(
            transpose_pixels_in_place(&mut unchanged, 3, 2, &mut []),
            Err(Reject::Resource(_))
        ));
        assert_eq!(unchanged, before);
    }

    #[test]
    fn encoded_lossless_rgba_fixture_decodes_premultiplies_and_maps_all_orientations() {
        let straight_rgba = [
            220, 20, 40, 255, // A
            100, 180, 30, 128, // B
            40, 70, 230, 64, // C
            250, 150, 10, 200, // D
            10, 240, 120, 180, // E
            130, 40, 210, 90, // F
        ];
        let mut encoded = Vec::new();
        WebPEncoder::new(&mut encoded)
            .encode(&straight_rgba, 2, 3, ColorType::Rgba8)
            .unwrap();
        let fixture_hex = encoded
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>();
        println!("static_webp_vp8l_2x3_fixture_hex={fixture_hex}");

        let mut decoder =
            WebPDecoder::new_static_bounded_with_metadata(Cursor::new(encoded), 2 * 1024 * 1024)
                .unwrap();
        assert_eq!(decoder.dimensions(), (2, 3));
        assert_eq!(
            decoder.bounded_metadata_ranges(),
            Some(WebPMetadataRanges {
                icc_profile: None,
                exif_metadata: None,
            })
        );
        let mut decoded = vec![0; straight_rgba.len()];
        decoder
            .read_static_rgba_bounded_with_metadata(&mut decoded)
            .unwrap();
        assert_eq!(decoded, straight_rgba);
        premultiply_rgba_in_place(&mut decoded);
        let expected_premultiplied = [
            220, 20, 40, 255, // A
            50, 90, 15, 128, // B
            10, 18, 58, 64, // C
            196, 118, 8, 200, // D
            7, 169, 85, 180, // E
            46, 14, 74, 90, // F
        ];
        assert_eq!(decoded, expected_premultiplied);

        let expected_indices: [&[usize; 6]; 8] = [
            &[0, 1, 2, 3, 4, 5],
            &[1, 0, 3, 2, 5, 4],
            &[5, 4, 3, 2, 1, 0],
            &[4, 5, 2, 3, 0, 1],
            &[0, 2, 4, 1, 3, 5],
            &[4, 2, 0, 5, 3, 1],
            &[5, 3, 1, 4, 2, 0],
            &[1, 3, 5, 0, 2, 4],
        ];
        for orientation in 1..=8 {
            let (width, height) = oriented_dimensions(2, 3, orientation);
            let mut actual = vec![0; (width * height * 4) as usize];
            let mut source_pixels = decoded.clone();
            rasterize_oriented(
                &mut source_pixels,
                2,
                3,
                orientation,
                width,
                height,
                &mut actual,
            )
            .unwrap();
            let mut expected = Vec::with_capacity(actual.len());
            for &index in expected_indices[(orientation - 1) as usize] {
                expected.extend_from_slice(&expected_premultiplied[index * 4..index * 4 + 4]);
            }
            assert_eq!(actual, expected, "orientation={orientation}");
        }
    }

    fn tiff_orientation(little_endian: bool, orientation: u16) -> Vec<u8> {
        let mut tiff = Vec::with_capacity(26);
        tiff.extend_from_slice(if little_endian { b"II" } else { b"MM" });
        push_u16(&mut tiff, 42, little_endian);
        push_u32(&mut tiff, 8, little_endian);
        push_u16(&mut tiff, 1, little_endian);
        push_u16(&mut tiff, 0x0112, little_endian);
        push_u16(&mut tiff, 3, little_endian);
        push_u32(&mut tiff, 1, little_endian);
        push_u16(&mut tiff, orientation, little_endian);
        push_u16(&mut tiff, 0, little_endian);
        push_u32(&mut tiff, 0, little_endian);
        tiff
    }

    fn minimal_matrix_profile(version: u32, include_mluc: bool) -> Vec<u8> {
        let mut tags = vec![
            (*b"rXYZ", icc_xyz(0.4361, 0.2225, 0.0139)),
            (*b"gXYZ", icc_xyz(0.3851, 0.7169, 0.0971)),
            (*b"bXYZ", icc_xyz(0.1431, 0.0606, 0.7142)),
            (*b"rTRC", icc_gamma_curve()),
            (*b"gTRC", icc_gamma_curve()),
            (*b"bTRC", icc_gamma_curve()),
            (*b"wtpt", icc_xyz(0.9642, 1.0, 0.8249)),
        ];
        if include_mluc {
            tags.push((*b"desc", icc_shared_mluc()));
        }
        build_icc_profile(version, tags)
    }

    fn minimal_mft2_profile() -> Vec<u8> {
        let mut tag = vec![0_u8; 124];
        tag[..4].copy_from_slice(b"mft2");
        tag[8] = 3;
        tag[9] = 3;
        tag[10] = 2;
        for diagonal in [0usize, 4, 8] {
            let offset = 12 + diagonal * 4;
            tag[offset..offset + 4].copy_from_slice(&65536_i32.to_be_bytes());
        }
        tag[48..50].copy_from_slice(&2_u16.to_be_bytes());
        tag[50..52].copy_from_slice(&2_u16.to_be_bytes());
        let mut offset = 52;
        for _channel in 0..3 {
            tag[offset..offset + 2].copy_from_slice(&0_u16.to_be_bytes());
            tag[offset + 2..offset + 4].copy_from_slice(&u16::MAX.to_be_bytes());
            offset += 4;
        }
        for red in [0_u16, u16::MAX] {
            for green in [0_u16, u16::MAX] {
                for blue in [0_u16, u16::MAX] {
                    for value in [red, green, blue] {
                        tag[offset..offset + 2].copy_from_slice(&value.to_be_bytes());
                        offset += 2;
                    }
                }
            }
        }
        for _channel in 0..3 {
            tag[offset..offset + 2].copy_from_slice(&0_u16.to_be_bytes());
            tag[offset + 2..offset + 4].copy_from_slice(&u16::MAX.to_be_bytes());
            offset += 4;
        }
        assert_eq!(offset, tag.len());
        build_icc_profile(
            0x0430_0000,
            vec![(*b"A2B0", tag), (*b"wtpt", icc_xyz(0.9642, 1.0, 0.8249))],
        )
    }

    fn minimal_abm_profile(tag_type: &[u8; 4], tag_signature: [u8; 4]) -> Vec<u8> {
        build_icc_profile(
            0x0430_0000,
            vec![
                (tag_signature, minimal_abm_profile_tag(tag_type)),
                (*b"wtpt", icc_xyz(0.9642, 1.0, 0.8249)),
            ],
        )
    }

    fn minimal_abm_profile_tag(tag_type: &[u8; 4]) -> Vec<u8> {
        let mut tag = vec![0_u8; 80];
        tag[..4].copy_from_slice(tag_type);
        tag[8] = 3;
        tag[9] = 3;
        // B curves are allowed immediately after the 32-byte mAB/mBA header.
        tag[12..16].copy_from_slice(&32_u32.to_be_bytes());
        for index in 0..3 {
            let offset = 32 + index * 16;
            tag[offset..offset + 4].copy_from_slice(b"curv");
            tag[offset + 8..offset + 12].copy_from_slice(&1_u32.to_be_bytes());
            tag[offset + 12..offset + 14].copy_from_slice(&256_u16.to_be_bytes());
        }
        tag
    }

    fn minimal_abm_matrix_profile(tag_type: &[u8; 4], tag_signature: [u8; 4]) -> Vec<u8> {
        let mut tag = vec![0_u8; 176];
        tag[..4].copy_from_slice(tag_type);
        tag[8] = 3;
        tag[9] = 3;
        // Required stage groups: B curves, matrix+M curves. The M curve
        // chain may begin at the 32-byte header boundary, then the matrix
        // block and B curves follow at aligned offsets.
        tag[12..16].copy_from_slice(&128_u32.to_be_bytes());
        tag[16..20].copy_from_slice(&80_u32.to_be_bytes());
        tag[20..24].copy_from_slice(&32_u32.to_be_bytes());
        for offset in [32usize, 128] {
            for index in 0..3 {
                let curve = offset + index * 16;
                tag[curve..curve + 4].copy_from_slice(b"curv");
                tag[curve + 8..curve + 12].copy_from_slice(&1_u32.to_be_bytes());
                tag[curve + 12..curve + 14].copy_from_slice(&256_u16.to_be_bytes());
            }
        }
        for diagonal in [0usize, 4, 8] {
            let offset = 80 + diagonal * 4;
            tag[offset..offset + 4].copy_from_slice(&65536_i32.to_be_bytes());
        }
        build_icc_profile(
            0x0430_0000,
            vec![
                (tag_signature, tag),
                (*b"wtpt", icc_xyz(0.9642, 1.0, 0.8249)),
            ],
        )
    }

    fn build_icc_profile(version: u32, tags: Vec<([u8; 4], Vec<u8>)>) -> Vec<u8> {
        let table_end = 132 + tags.len() * 12;
        let mut profile = vec![0_u8; (table_end + 3) & !3];
        profile[8..12].copy_from_slice(&version.to_be_bytes());
        profile[12..16].copy_from_slice(b"mntr");
        profile[16..20].copy_from_slice(b"RGB ");
        profile[20..24].copy_from_slice(b"XYZ ");
        profile[24..36].copy_from_slice(&[0x07, 0xe8, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0]);
        profile[36..40].copy_from_slice(b"acsp");
        profile[40..44].copy_from_slice(b"APPL");
        profile[128..132].copy_from_slice(&(tags.len() as u32).to_be_bytes());

        for (index, (signature, data)) in tags.into_iter().enumerate() {
            while profile.len() % 4 != 0 {
                profile.push(0);
            }
            let offset = profile.len();
            profile.extend_from_slice(&data);
            let record = 132 + index * 12;
            profile[record..record + 4].copy_from_slice(&signature);
            profile[record + 4..record + 8].copy_from_slice(&(offset as u32).to_be_bytes());
            profile[record + 8..record + 12].copy_from_slice(&(data.len() as u32).to_be_bytes());
        }
        let length = profile.len() as u32;
        profile[0..4].copy_from_slice(&length.to_be_bytes());
        profile
    }

    fn icc_xyz(x: f32, y: f32, z: f32) -> Vec<u8> {
        let mut tag = Vec::with_capacity(20);
        tag.extend_from_slice(b"XYZ ");
        tag.extend_from_slice(&[0; 4]);
        for value in [x, y, z] {
            tag.extend_from_slice(&((value * 65536.0).round() as i32).to_be_bytes());
        }
        tag
    }

    fn icc_gamma_curve() -> Vec<u8> {
        let mut curve = vec![0_u8; 14];
        curve[..4].copy_from_slice(b"curv");
        curve[8..12].copy_from_slice(&1_u32.to_be_bytes());
        curve[12..14].copy_from_slice(&256_u16.to_be_bytes());
        curve
    }

    fn icc_shared_mluc() -> Vec<u8> {
        let mut tag = vec![0_u8; 42];
        tag[..4].copy_from_slice(b"mluc");
        tag[8..12].copy_from_slice(&2_u32.to_be_bytes());
        tag[12..16].copy_from_slice(&12_u32.to_be_bytes());
        for (record, (language, country)) in
            [(b"en", b"US"), (b"fr", b"FR")].into_iter().enumerate()
        {
            let offset = 16 + record * 12;
            tag[offset..offset + 2].copy_from_slice(language);
            tag[offset + 2..offset + 4].copy_from_slice(country);
            tag[offset + 4..offset + 8].copy_from_slice(&2_u32.to_be_bytes());
            tag[offset + 8..offset + 12].copy_from_slice(&40_u32.to_be_bytes());
        }
        tag[40..42].copy_from_slice(&[0, b'X']);
        tag
    }

    fn push_u16(target: &mut Vec<u8>, value: u16, little_endian: bool) {
        let bytes = if little_endian {
            value.to_le_bytes()
        } else {
            value.to_be_bytes()
        };
        target.extend_from_slice(&bytes);
    }

    fn push_u32(target: &mut Vec<u8>, value: u32, little_endian: bool) {
        let bytes = if little_endian {
            value.to_le_bytes()
        } else {
            value.to_be_bytes()
        };
        target.extend_from_slice(&bytes);
    }
}
