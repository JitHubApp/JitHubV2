// Copyright (c) JitHub contributors. Licensed under MIT.

//! Full-frame orientation mapping and transposition.

use crate::raster_decode::oriented_dimensions;
use crate::raster_decode::pixels::{
    premultiply_rgba_in_place, rasterize_oriented, transpose_pixels_in_place,
};
use crate::reject::Reject;
use image_webp::{ColorType, WebPDecoder, WebPEncoder, WebPMetadataRanges};
use resvg::tiny_skia::{BlendMode, FilterQuality, PixmapMut, PixmapPaint, PixmapRef, Transform};
use std::io::Cursor;

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
            10, 0, 0, 255, 20, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255, 50, 0, 0, 255, 60, 0, 0,
            255,
        ],
        &[
            20, 0, 0, 255, 10, 0, 0, 255, 40, 0, 0, 255, 30, 0, 0, 255, 60, 0, 0, 255, 50, 0, 0,
            255,
        ],
        &[
            60, 0, 0, 255, 50, 0, 0, 255, 40, 0, 0, 255, 30, 0, 0, 255, 20, 0, 0, 255, 10, 0, 0,
            255,
        ],
        &[
            50, 0, 0, 255, 60, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255, 10, 0, 0, 255, 20, 0, 0,
            255,
        ],
        &[
            10, 0, 0, 255, 30, 0, 0, 255, 50, 0, 0, 255, 20, 0, 0, 255, 40, 0, 0, 255, 60, 0, 0,
            255,
        ],
        &[
            50, 0, 0, 255, 30, 0, 0, 255, 10, 0, 0, 255, 60, 0, 0, 255, 40, 0, 0, 255, 20, 0, 0,
            255,
        ],
        &[
            60, 0, 0, 255, 40, 0, 0, 255, 20, 0, 0, 255, 50, 0, 0, 255, 30, 0, 0, 255, 10, 0, 0,
            255,
        ],
        &[
            20, 0, 0, 255, 40, 0, 0, 255, 60, 0, 0, 255, 10, 0, 0, 255, 30, 0, 0, 255, 50, 0, 0,
            255,
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
            let mut resized =
                PixmapMut::from_bytes(&mut intermediate, intermediate_width, intermediate_height)
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
        [
            1, 0, 0, 255, 4, 0, 0, 255, 2, 0, 0, 255, 5, 0, 0, 255, 3, 0, 0, 255, 6, 0, 0, 255,
        ]
    );

    let mut pixels_3x3 = [
        1, 0, 0, 255, 2, 0, 0, 255, 3, 0, 0, 255, 4, 0, 0, 255, 5, 0, 0, 255, 6, 0, 0, 255, 7, 0,
        0, 255, 8, 0, 0, 255, 9, 0, 0, 255,
    ];
    let mut scratch_3x3 = [0_u8; 2];
    transpose_pixels_in_place(&mut pixels_3x3, 3, 3, &mut scratch_3x3).unwrap();
    assert_eq!(
        pixels_3x3,
        [
            1, 0, 0, 255, 4, 0, 0, 255, 7, 0, 0, 255, 2, 0, 0, 255, 5, 0, 0, 255, 8, 0, 0, 255, 3,
            0, 0, 255, 6, 0, 0, 255, 9, 0, 0, 255,
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
