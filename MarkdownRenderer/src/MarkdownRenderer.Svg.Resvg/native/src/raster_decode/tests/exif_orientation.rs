// Copyright (c) JitHub contributors. Licensed under MIT.

//! EXIF orientation parsing.

use crate::raster_decode::exif::parse_exif_orientation;
use crate::raster_decode::tests::support::tiff_orientation;

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
