// Copyright (c) JitHub contributors. Licensed under MIT.

//! ICC profile preflight.

use crate::raster_decode::icc_preflight::{ICC_TRANSFORM_FIXED_RESERVATION, MAX_ICC_TAG_COUNT, preflight_icc_profile, read_icc_u32};
use crate::raster_decode::icc_transform::{apply_icc_transform, create_icc_transform};
use crate::raster_decode::tests::support::{build_icc_profile, icc_gamma_curve, icc_xyz, minimal_abm_matrix_profile, minimal_abm_profile, minimal_abm_profile_tag, minimal_matrix_profile, minimal_mft2_profile};
use crate::reject::Reject;

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
