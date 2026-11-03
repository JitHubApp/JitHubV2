// Copyright (c) JitHub contributors. Licensed under MIT.

//! Row-streamed decode, bicubic resampling, and orientation for low working budgets.

use crate::metadata::Metadata;
use crate::protocol::PIXEL_BGRA;
use crate::raster_decode::icc_preflight::IccProfilePlan;
use crate::raster_decode::icc_transform::{apply_icc_transform, create_icc_transform};
use crate::raster_decode::pixels::{premultiply_rgba_in_place, swap_red_blue};
use crate::raster_decode::scratch::{
    admit_scratch_budget, allocate_zeroed, allocation_charge, requested_allocation_charge,
    rgba_length,
};
use crate::raster_decode::{
    DecodePlan, PROGRESS_COLOR_CONVERTED, PROGRESS_COMPLETE, PROGRESS_DECODED, PROGRESS_RASTERIZED,
    map_decode_error,
};
use crate::reject::Reject;
use crate::request::Request;
use crate::response::Response;
use image_webp::WebPDecoder;
use resvg::tiny_skia::{
    BlendMode, FilterQuality, PixmapMut, PixmapPaint, PixmapRowRing, Transform,
};
use std::io::Cursor;
use std::sync::atomic::{AtomicU8, Ordering};

#[allow(clippy::too_many_arguments)]
pub(crate) fn decode_streamed_rows(
    request: &Request,
    plan: &DecodePlan,
    output: &mut [u8],
    progress: &AtomicU8,
    decoder: &mut WebPDecoder<Cursor<&[u8]>>,
    icc: Option<&[u8]>,
    icc_plan: Option<&IccProfilePlan>,
    mapping_bytes: usize,
) -> Result<Response, Reject> {
    let transposed = plan.orientation >= 5;
    let (intermediate_width, intermediate_height) = if transposed {
        (plan.target_height, plan.target_width)
    } else {
        (plan.target_width, plan.target_height)
    };
    let source_row_bytes = rgba_length(plan.source_width, 1)?;
    let needs_resize =
        intermediate_width != plan.source_width || intermediate_height != plan.source_height;
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
                return Err(Reject::Worker(
                    "WebP row decoder returned invalid row order",
                ));
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
        return Err(Reject::Worker(
            "WebP row decoder returned an incomplete image",
        ));
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

#[allow(clippy::too_many_arguments)]
pub(crate) fn render_ready_streamed_rows(
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
            return Err(Reject::Worker(
                "WebP source row ring lost a required sample",
            ));
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

pub(crate) fn scatter_unscaled_oriented_row(
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

#[allow(clippy::too_many_arguments)]
pub(crate) fn render_streamed_output_row(
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
    let source =
        PixmapRowRing::from_bytes(row_ring, plan.source_width, plan.source_height, ring_height)
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
