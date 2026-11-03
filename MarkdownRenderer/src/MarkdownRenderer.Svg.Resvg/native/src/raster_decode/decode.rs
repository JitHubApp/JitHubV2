// Copyright (c) JitHub contributors. Licensed under MIT.

//! Full-frame decode with a bounded fall back to streamed rows.

use crate::metadata::Metadata;
use crate::protocol::PIXEL_BGRA;
use crate::raster_decode::exif::parse_exif_orientation;
use crate::raster_decode::icc_preflight::preflight_icc_profile;
use crate::raster_decode::icc_transform::{apply_icc_transform, create_icc_transform};
use crate::raster_decode::pixels::{premultiply_rgba_in_place, rasterize_oriented, swap_red_blue};
use crate::raster_decode::scratch::{
    admit_scratch_budget, allocate_zeroed, allocation_charge, requested_allocation_charge,
    rgba_length,
};
use crate::raster_decode::streamed_rows::decode_streamed_rows;
use crate::raster_decode::{
    DecodePlan, PROGRESS_COLOR_CONVERTED, PROGRESS_COMPLETE, PROGRESS_DECODED, PROGRESS_HASHED,
    PROGRESS_HEADER_VALIDATED, PROGRESS_RASTERIZED, map_decode_error,
};
use crate::reject::Reject;
use crate::request::Request;
use crate::response::Response;
use image_webp::{DecodingError, WebPDecoder, WebPMetadataRanges};
use sha2::{Digest, Sha256};
use std::io::Cursor;
use std::sync::atomic::{AtomicU8, Ordering};

pub(crate) fn decode_and_render(
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
    // The row API streams every supported static image: VP8L, opaque VP8,
    // and lossy VP8X alpha, which charges one reconstructed alpha byte per
    // pixel instead of a full RGBA frame (P3-014).
    let can_stream_rows = true;
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
            let mut retry_decoder =
                reopen_verified_decoder(source, plan, icc, plan.orientation, decoder_limit)?;
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
    let metadata = Metadata {
        width: f64::from(plan.intrinsic_width),
        height: f64::from(plan.intrinsic_height),
        ..Metadata::default()
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

pub(crate) fn reopen_verified_decoder<'a>(
    source: &'a [u8],
    plan: &DecodePlan,
    expected_icc: Option<&[u8]>,
    expected_orientation: u32,
    decoder_limit: usize,
) -> Result<WebPDecoder<Cursor<&'a [u8]>>, Reject> {
    let decoder = WebPDecoder::new_static_bounded_with_metadata(Cursor::new(source), decoder_limit)
        .map_err(map_decode_error)?;
    if decoder.dimensions() != (plan.source_width, plan.source_height) {
        return Err(Reject::Worker(
            "WebP dimensions changed during bounded retry",
        ));
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

pub(crate) fn validate_metadata_ranges(
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
