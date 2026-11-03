// Copyright (c) JitHub contributors. Licensed under MIT.

//! TIFF and ICC fixture builders for raster decode tests.

pub(crate) fn tiff_orientation(little_endian: bool, orientation: u16) -> Vec<u8> {
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

pub(crate) fn minimal_matrix_profile(version: u32, include_mluc: bool) -> Vec<u8> {
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

pub(crate) fn minimal_mft2_profile() -> Vec<u8> {
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

pub(crate) fn minimal_abm_profile(tag_type: &[u8; 4], tag_signature: [u8; 4]) -> Vec<u8> {
    build_icc_profile(
        0x0430_0000,
        vec![
            (tag_signature, minimal_abm_profile_tag(tag_type)),
            (*b"wtpt", icc_xyz(0.9642, 1.0, 0.8249)),
        ],
    )
}

pub(crate) fn minimal_abm_profile_tag(tag_type: &[u8; 4]) -> Vec<u8> {
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

pub(crate) fn minimal_abm_matrix_profile(tag_type: &[u8; 4], tag_signature: [u8; 4]) -> Vec<u8> {
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

pub(crate) fn build_icc_profile(version: u32, tags: Vec<([u8; 4], Vec<u8>)>) -> Vec<u8> {
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

pub(crate) fn icc_xyz(x: f32, y: f32, z: f32) -> Vec<u8> {
    let mut tag = Vec::with_capacity(20);
    tag.extend_from_slice(b"XYZ ");
    tag.extend_from_slice(&[0; 4]);
    for value in [x, y, z] {
        tag.extend_from_slice(&((value * 65536.0).round() as i32).to_be_bytes());
    }
    tag
}

pub(crate) fn icc_gamma_curve() -> Vec<u8> {
    let mut curve = vec![0_u8; 14];
    curve[..4].copy_from_slice(b"curv");
    curve[8..12].copy_from_slice(&1_u32.to_be_bytes());
    curve[12..14].copy_from_slice(&256_u16.to_be_bytes());
    curve
}

pub(crate) fn icc_shared_mluc() -> Vec<u8> {
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

pub(crate) fn push_u16(target: &mut Vec<u8>, value: u16, little_endian: bool) {
    let bytes = if little_endian {
        value.to_le_bytes()
    } else {
        value.to_be_bytes()
    };
    target.extend_from_slice(&bytes);
}

pub(crate) fn push_u32(target: &mut Vec<u8>, value: u32, little_endian: bool) {
    let bytes = if little_endian {
        value.to_le_bytes()
    } else {
        value.to_be_bytes()
    };
    target.extend_from_slice(&bytes);
}
