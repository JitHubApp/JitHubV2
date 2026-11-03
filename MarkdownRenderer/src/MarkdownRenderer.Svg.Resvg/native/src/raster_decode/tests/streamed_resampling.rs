// Copyright (c) JitHub contributors. Licensed under MIT.

//! Streamed-row decode parity with the full-frame path.

use crate::protocol::{KIND_RASTER_DECODE, PIXEL_RGBA, PROGRESS_AREA_BYTES};
use crate::raster_decode::DecodePlan;
use crate::raster_decode::decode::decode_and_render;
use crate::raster_decode::pixels::{premultiply_rgba_in_place, rasterize_oriented};
use crate::raster_decode::scratch::{
    admit_scratch_budget, requested_allocation_charge, rgba_length,
};
use crate::raster_decode::streamed_rows::render_ready_streamed_rows;
use crate::request::Request;
use crate::shared_mapping::align64;
use image_webp::WebPDecoder;
use resvg::tiny_skia::{ColorU8, Transform};
use sha2::{Digest, Sha256};
use std::io::Cursor;
use std::sync::atomic::AtomicU8;

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
            assert_eq!(
                streamed, expected,
                "orientation={orientation}, intermediate={intermediate_width}x{intermediate_height}"
            );
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
    let encoded: &[u8] = include_bytes!("../../../tests/fixtures/opaque-vp8-256.webp");
    assert_eq!(&encoded[12..16], b"VP8 ");

    let source_length = encoded.len() as u32;
    let output_length = rgba_length(TARGET_WIDTH, TARGET_HEIGHT).unwrap();
    let mapping_bytes = align64(encoded.len())
        .checked_add(output_length)
        .and_then(|length| length.checked_add(PROGRESS_AREA_BYTES))
        .unwrap();
    assert!(mapping_bytes < WORKING_LIMIT);
    assert!(
        requested_allocation_charge(rgba_length(SOURCE_WIDTH, SOURCE_HEIGHT).unwrap()).unwrap()
            > WORKING_LIMIT - mapping_bytes
    );

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
    let full_scratch =
        requested_allocation_charge(rgba_length(SOURCE_WIDTH, SOURCE_HEIGHT).unwrap()).unwrap();
    assert!(
        admit_scratch_budget(
            WORKING_LIMIT,
            mapping_bytes,
            full_scratch,
            preflight_decoder.bounded_allocation_bytes().unwrap(),
        )
        .is_err()
    );

    let mut actual = vec![0; output_length];
    decode_and_render(&request, &plan, encoded, &mut actual, &AtomicU8::new(0)).unwrap();

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
