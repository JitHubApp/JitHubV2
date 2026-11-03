// Copyright (c) JitHub contributors. Licensed under MIT.

//! Scratch and decoder-ledger admission.

use crate::raster_decode::scratch::{
    admit_scratch_budget, requested_allocation_charge, rgba_length,
};
use crate::reject::Reject;

#[test]
fn scratch_is_admitted_before_full_source_pixel_buffers_are_allocated() {
    let source_rgba = rgba_length(2048, 8193).unwrap();
    let requested_scratch = requested_allocation_charge(source_rgba)
        .unwrap()
        .checked_add(requested_allocation_charge(source_rgba).unwrap())
        .unwrap();

    // The mapping + source decode + ICC destination exceed the worker cap.
    // This path rejects on size arithmetic, before allocate_zeroed runs.
    assert!(matches!(
        admit_scratch_budget(64 * 1024 * 1024, 2 * 1024 * 1024, requested_scratch, 0,),
        Err(Reject::Resource(_))
    ));
}

#[test]
fn decoder_ledger_is_included_before_scratch_allocation() {
    let requested_scratch = requested_allocation_charge(1024).unwrap();
    assert!(matches!(
        admit_scratch_budget(4096, 1024, requested_scratch, 1985),
        Err(Reject::Resource(_))
    ));
    assert_eq!(
        admit_scratch_budget(4096, 1024, requested_scratch, 1984).unwrap(),
        1984
    );
}
