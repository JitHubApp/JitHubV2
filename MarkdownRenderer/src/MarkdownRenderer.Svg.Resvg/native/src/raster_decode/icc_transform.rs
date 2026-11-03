// Copyright (c) JitHub contributors. Licensed under MIT.

//! Creating and applying a preflighted ICC transform.

use crate::raster_decode::icc_preflight::{
    IccProfilePlan, MAX_ICC_CLUT_GRID_POINTS, MAX_ICC_TRC_ELEMENTS, align4_checked,
};
use crate::reject::Reject;
use moxcms::{ColorProfile, Layout, ParsingOptions, Transform8BitExecutor, TransformOptions};

pub(crate) fn create_icc_transform(
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

pub(crate) fn apply_icc_transform(
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
