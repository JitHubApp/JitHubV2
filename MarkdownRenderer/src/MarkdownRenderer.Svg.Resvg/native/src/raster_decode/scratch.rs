// Copyright (c) JitHub contributors. Licensed under MIT.

//! Scratch allocation and working-byte admission.

use crate::reject::Reject;

pub(crate) const ALLOCATION_BOOKKEEPING_BYTES: usize = 64;

pub(crate) fn rgba_length(width: u32, height: u32) -> Result<usize, Reject> {
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

pub(crate) fn allocate_zeroed(length: usize) -> Result<Vec<u8>, Reject> {
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(length)
        .map_err(|_| Reject::Resource("raster scratch allocation failed"))?;
    bytes.resize(length, 0);
    Ok(bytes)
}

pub(crate) fn allocation_charge(bytes: &Vec<u8>) -> Result<usize, Reject> {
    bytes
        .capacity()
        .checked_add(ALLOCATION_BOOKKEEPING_BYTES)
        .ok_or(Reject::Resource("raster scratch accounting overflow"))
}

pub(crate) fn requested_allocation_charge(length: usize) -> Result<usize, Reject> {
    if length == 0 {
        return Ok(0);
    }
    length
        .checked_add(ALLOCATION_BOOKKEEPING_BYTES)
        .ok_or(Reject::Resource("raster scratch accounting overflow"))
}

pub(crate) fn admit_scratch_budget(
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
