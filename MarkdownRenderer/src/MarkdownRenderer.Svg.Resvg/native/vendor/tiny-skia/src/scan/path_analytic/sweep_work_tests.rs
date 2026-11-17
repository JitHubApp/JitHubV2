// Copyright (c) JitHub contributors. Licensed under MIT.
//
//! Work guards for flattened curves. Flattening turns every outline into
//! long chains of very short edges, so the sweep's cost per vertex decides
//! whether text-heavy documents render inside their deadline. These tests
//! pin the two properties that keep that cost low: a vertex where one edge
//! hands over to the next updates the active order in place, and fill pairs
//! that a vertex does not touch are integrated once per pixel row rather
//! than once per band.

use super::*;
use alloc::vec::Vec;

/// `rings` concentric circles of `vertices` points each, in one sweep
/// component, filled even-odd so every ring is its own fill pair.
fn concentric_rings(rings: usize, vertices: usize) -> Vec<Vec<Point>> {
    (0..rings)
        .map(|ring| {
            let radius = 20.0 + 9.0 * ring as f64;
            // An irrational phase keeps the left and right chains from
            // sharing vertex ordinates, as glyph outlines rarely do.
            let phase = 0.37 + 0.11 * ring as f64;
            (0..vertices)
                .map(|vertex| {
                    let angle = core::f64::consts::TAU * (vertex as f64 + phase) / vertices as f64;
                    Point {
                        x: 128.0 + radius * angle.cos(),
                        y: 128.0 + radius * angle.sin(),
                    }
                })
                .collect()
        })
        .collect()
}

fn usage_for(contours: &[Vec<Point>], prefer_dense: bool) -> RasterUsage {
    let clip = ClipRect {
        left: 0.0,
        top: 0.0,
        right: 256.0,
        bottom: 256.0,
    };
    let mut usage = RasterUsage::default();
    rasterize_with_base_usage(
        contours,
        FillRule::EvenOdd,
        clip,
        Limits::default(),
        0,
        prefer_dense,
        &mut usage,
    )
    .unwrap();
    usage
}

const RINGS: usize = 8;
const VERTICES: usize = 1024;

#[test]
fn chain_vertices_update_the_active_order_in_place() {
    for prefer_dense in [false, true] {
        let usage = usage_for(&concentric_rings(RINGS, VERTICES), prefer_dense);
        // Every vertex but each ring's top and bottom extremum hands one edge
        // to the next; those extrema and the two clip events are the only
        // groups that reorder. Without the in-place path every group does.
        assert!(usage.processed_events >= 2 * RINGS * VERTICES);
        assert!(
            usage.reordered_event_groups <= 2 * RINGS + 2,
            "{} of {} events reordered",
            usage.reordered_event_groups,
            usage.processed_events
        );
    }
}

#[test]
fn untouched_fill_pairs_are_integrated_once_per_row() {
    for prefer_dense in [false, true] {
        let usage = usage_for(&concentric_rings(RINGS, VERTICES), prefer_dense);
        // A vertex closes the one pair it touches, which visits a few cells
        // near that edge. Integrating every pair at every band instead
        // re-visits all other rings' boundary cells at each vertex, about
        // twelve partial cells per edge with eight rings.
        assert!(
            usage.partial_intersections <= 4 * usage.edges as u64,
            "{} partial-pixel visits for {} edges",
            usage.partial_intersections,
            usage.edges
        );
    }
}
