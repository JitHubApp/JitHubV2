// Copyright (c) JitHub contributors. Licensed under MIT.

//! ICC profile preflight: tag tables, LUT/curve bounds, and transform reservation.

use crate::raster_decode::scratch::ALLOCATION_BOOKKEEPING_BYTES;
use crate::reject::Reject;

pub(crate) const MAX_ICC_PROFILE_BYTES: usize = 32 * 1024 * 1024;

pub(crate) const MAX_ICC_TAG_COUNT: usize = 128;

pub(crate) const MAX_ICC_TRANSFORM_DATA_BYTES: usize = 4 * 1024 * 1024;

pub(crate) const MAX_ICC_CLUT_GRID_POINTS: usize = 262_144;

pub(crate) const MAX_ICC_CLUT_STORAGE_BYTES: usize = 2 * 1024 * 1024;

pub(crate) const MAX_ICC_TRC_ELEMENTS: usize = 65_536;

pub(crate) const ICC_TRANSFORM_FIXED_RESERVATION: usize = 2 * 1024 * 1024;

pub(crate) const ICC_ALLOCATION_MULTIPLIER: usize = 6;

#[derive(Clone, Copy, Default)]
pub(crate) struct IccTagEntry {
    pub(crate) signature: u32,
    pub(crate) offset: usize,
    pub(crate) length: usize,
}

pub(crate) struct IccProfilePlan {
    pub(crate) entries: [IccTagEntry; MAX_ICC_TAG_COUNT],
    pub(crate) entry_count: usize,
    pub(crate) compact_size: usize,
    pub(crate) reservation: usize,
}

pub(crate) fn preflight_icc_profile(profile: &[u8]) -> Result<IccProfilePlan, Reject> {
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

pub(crate) fn is_rendering_icc_tag(signature: u32) -> bool {
    [
        *b"rXYZ", *b"gXYZ", *b"bXYZ", *b"rTRC", *b"gTRC", *b"bTRC", *b"wtpt", *b"bkpt", *b"chad",
        *b"cicp", *b"A2B0", *b"A2B1", *b"A2B2", *b"B2A0", *b"B2A1", *b"B2A2",
    ]
    .iter()
    .any(|candidate| u32::from_be_bytes(*candidate) == signature)
}

pub(crate) fn is_unsupported_rendering_transform_tag(signature: u32) -> bool {
    [
        *b"D2B0", *b"D2B1", *b"D2B2", *b"D2B3", *b"B2D0", *b"B2D1", *b"B2D2", *b"B2D3",
    ]
    .iter()
    .any(|candidate| u32::from_be_bytes(*candidate) == signature)
}

pub(crate) fn validate_rendering_icc_tag(signature: u32, tag: &[u8]) -> Result<usize, Reject> {
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

pub(crate) fn validate_icc_lut_tag(tag: &[u8]) -> Result<usize, Reject> {
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
            return Err(Reject::Unsupported(
                "unaligned embedded ICC LUT stage offset",
            ));
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

pub(crate) fn validate_icc_curve_chain(
    tag: &[u8],
    offset: usize,
    count: usize,
) -> Result<usize, Reject> {
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

pub(crate) fn validate_icc_curve(tag: &[u8], offset: usize) -> Result<usize, Reject> {
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

pub(crate) fn read_icc_u32(bytes: &[u8], offset: usize) -> Result<u32, Reject> {
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

pub(crate) fn align4_checked(value: usize) -> Result<usize, Reject> {
    value
        .checked_add(3)
        .map(|rounded| rounded & !3)
        .ok_or(Reject::Resource("embedded ICC alignment overflow"))
}
