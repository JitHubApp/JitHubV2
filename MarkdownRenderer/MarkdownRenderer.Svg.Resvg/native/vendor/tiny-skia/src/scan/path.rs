// Copyright 2006 The Android Open Source Project
// Copyright 2020 Yevhenii Reizner
//
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

use core::convert::TryFrom;

use tiny_skia_path::SaturateCast;

use crate::{FillRule, IntRect, LengthU32, Path, Rect};

use crate::blitter::Blitter;
use crate::edge::{Edge, LineEdge};
use crate::edge_builder::{BasicEdgeBuilder, ShiftedIntRect};
use crate::fixed_point::{fdot16, fdot6, FDot16};
use crate::geom::{IntRectExt, ScreenIntRect};

#[cfg(all(not(feature = "std"), feature = "no-std-float"))]
use tiny_skia_path::NoStdFloat;

pub fn fill_path(
    path: &Path,
    fill_rule: FillRule,
    clip: &ScreenIntRect,
    blitter: &mut dyn Blitter,
) {
    let ir = match conservative_round_to_int(&path.bounds()) {
        Some(v) => v,
        None => return,
    };

    let path_contained_in_clip = if let Some(bounds) = ir.to_screen_int_rect() {
        clip.contains(&bounds)
    } else {
        // If bounds cannot be converted into ScreenIntRect,
        // the path is out of clip.
        false
    };

    // TODO: SkScanClipper

    fill_path_impl(
        path,
        fill_rule,
        clip,
        ir.y(),
        ir.bottom(),
        0,
        path_contained_in_clip,
        blitter,
    );
}

// MIT-licensed bounded admission extension, JitHub contributors, 2026.
// Rasterization and tie ordering are identical to the legacy non-AA scanner.
// Every fallible operation finishes before the first destination write.
pub(crate) fn fill_path_bounded(
    path: &Path,
    fill_rule: FillRule,
    clip: &ScreenIntRect,
    blitter: &mut dyn Blitter,
    max_bytes: usize,
) -> Result<(), crate::AnalyticRasterError> {
    let ir = conservative_round_to_int(&path.bounds())
        .ok_or(crate::AnalyticRasterError::NumericRange)?;
    let contained = ir
        .to_screen_int_rect()
        .is_some_and(|bounds| clip.contains(&bounds));
    let shifted_clip =
        ShiftedIntRect::new(clip, 0).ok_or(crate::AnalyticRasterError::InvalidClip)?;
    let edge_clip = if contained { None } else { Some(&shifted_clip) };
    let Some(mut edges) = BasicEdgeBuilder::build_edges_bounded(path, edge_clip, 0, max_bytes)?
    else {
        return Ok(());
    };
    let start_y = ir.y().max(clip.top() as i32).max(0) as u32;
    let stop_y = ir.bottom().min(clip.bottom() as i32).max(0) as u32;
    if start_y >= stop_y {
        return Ok(());
    }

    // The original stable sort can allocate a temporary edge buffer. Sorting
    // by the original ordinal as a final key gives the exact same order using
    // the allocation-free sort; the unused link field is scratch until linking.
    for (index, edge) in edges.iter_mut().enumerate() {
        edge.prev = Some(index as u32);
    }
    edges.sort_unstable_by(|a, b| {
        a.first_y
            .cmp(&b.first_y)
            .then_with(|| a.x.cmp(&b.x))
            .then_with(|| a.prev.cmp(&b.prev))
    });
    for (index, edge) in edges.iter_mut().enumerate() {
        edge.prev = Some(index as u32);
        edge.next = Some(index as u32 + 2);
    }
    // Capacity for both sentinels was admitted by build_edges_bounded.
    debug_assert!(edges.capacity() - edges.len() >= 2);
    edges.insert(
        0,
        Edge::Line(LineEdge {
            prev: None,
            next: Some(1),
            x: i32::MIN,
            first_y: i32::MIN,
            ..LineEdge::default()
        }),
    );
    edges.push(Edge::Line(LineEdge {
        prev: Some(edges.len() as u32 - 1),
        next: None,
        first_y: i32::MAX,
        ..LineEdge::default()
    }));
    walk_edges(
        fill_rule,
        start_y,
        stop_y,
        clip.right(),
        &mut edges,
        blitter,
    );
    Ok(())
}

// Conservative rounding function, which effectively nudges the int-rect to be slightly larger
// than Rect::round() might have produced. This is a safety-net for the scan-converter, which
// inspects the returned int-rect, and may disable clipping (for speed) if it thinks all of the
// edges will fit inside the clip's bounds. The scan-converter introduces slight numeric errors
// due to accumulated += of the slope, so this function is used to return a conservatively large
// int-bounds, and thus we will only disable clipping if we're sure the edges will stay in-bounds.
fn conservative_round_to_int(src: &Rect) -> Option<IntRect> {
    // We must use `from_ltrb`, otherwise rounding will be incorrect.
    IntRect::from_ltrb(
        round_down_to_int(src.left()),
        round_down_to_int(src.top()),
        round_up_to_int(src.right()),
        round_up_to_int(src.bottom()),
    )
}

#[cfg(test)]
mod bounded_tests {
    use super::*;
    use alloc::vec::Vec;

    #[derive(Default)]
    struct Spans(Vec<(u32, u32, u32)>);
    impl Blitter for Spans {
        fn blit_h(&mut self, x: u32, y: u32, width: LengthU32) {
            self.0.push((x, y, width.get()));
        }
    }

    #[test]
    fn bounded_non_aa_matches_legacy_ties_curves_clipping_and_fill_rules() {
        let clip = ScreenIntRect::from_xywh(0, 0, 64, 64).unwrap();
        let mut seed = 0x50a7_1234u32;
        let mut coordinate = || {
            seed = seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            (seed >> 16) as f32 / 1024.0 - 10.0
        };
        for _ in 0..128 {
            let mut builder = crate::PathBuilder::new();
            for _ in 0..8 {
                // Shared first vertices deliberately create equal sort keys.
                builder.move_to(12.0, 8.0);
                builder.line_to(coordinate(), coordinate());
                builder.quad_to(coordinate(), coordinate(), coordinate(), coordinate());
                builder.cubic_to(
                    coordinate(),
                    coordinate(),
                    coordinate(),
                    coordinate(),
                    coordinate(),
                    coordinate(),
                );
                builder.close();
            }
            let path = builder.finish().unwrap();
            for rule in [FillRule::Winding, FillRule::EvenOdd] {
                let mut legacy = Spans::default();
                let mut bounded = Spans::default();
                fill_path(&path, rule, &clip, &mut legacy);
                assert_eq!(
                    fill_path_bounded(&path, rule, &clip, &mut bounded, 1024 * 1024),
                    Ok(())
                );
                assert_eq!(bounded.0, legacy.0);
            }
        }
    }

    #[test]
    fn bounded_non_aa_rejects_before_blitting_and_charges_growth_and_sentinels() {
        let clip = ScreenIntRect::from_xywh(0, 0, 64, 64).unwrap();
        let path = crate::PathBuilder::from_rect(Rect::from_xywh(1.0, 1.0, 10.0, 10.0).unwrap());
        let unit = core::mem::size_of::<Edge>();
        for budget in [0, unit, 5 * unit] {
            let mut spans = Spans::default();
            assert_eq!(
                fill_path_bounded(&path, FillRule::Winding, &clip, &mut spans, budget),
                Err(crate::AnalyticRasterError::ActiveWorkLimit)
            );
            assert!(spans.0.is_empty());
        }
        // Two geometric edges plus two sentinels are retained. During growth,
        // the old two-edge allocation and new four-edge allocation coexist.
        let mut spans = Spans::default();
        assert_eq!(
            fill_path_bounded(&path, FillRule::Winding, &clip, &mut spans, 6 * unit),
            Ok(())
        );
        assert_eq!(spans.0.len(), 10);
    }
}

// Bias used for conservative rounding of float rects to int rects, to nudge the irects a little
// larger, so we don't "think" a path's bounds are inside a clip, when (due to numeric drift in
// the scan-converter) we might walk beyond the predicted limits.
//
// This value has been determined trial and error: pick the smallest value (after the 0.5) that
// fixes any problematic cases (e.g. crbug.com/844457)
// NOTE: cubics appear to be the main reason for needing this slop. If we could (perhaps) have a
// more accurate walker for cubics, we may be able to reduce this fudge factor.
const CONSERVATIVE_ROUND_BIAS: f64 = 0.5 + 1.5 / fdot6::ONE as f64;

// Round the value down. This is used to round the top and left of a rectangle,
// and corresponds to the way the scan converter treats the top and left edges.
// It has a slight bias to make the "rounded" int smaller than a normal round, to create a more
// conservative int-bounds (larger) from a float rect.
fn round_down_to_int(x: f32) -> i32 {
    let mut xx = x as f64;
    xx -= CONSERVATIVE_ROUND_BIAS;
    i32::saturate_from(xx.ceil())
}

// Round the value up. This is used to round the right and bottom of a rectangle.
// It has a slight bias to make the "rounded" int smaller than a normal round, to create a more
// conservative int-bounds (larger) from a float rect.
fn round_up_to_int(x: f32) -> i32 {
    let mut xx = x as f64;
    xx += CONSERVATIVE_ROUND_BIAS;
    i32::saturate_from(xx.floor())
}

pub fn fill_path_impl(
    path: &Path,
    fill_rule: FillRule,
    clip_rect: &ScreenIntRect,
    mut start_y: i32,
    mut stop_y: i32,
    shift_edges_up: i32,
    path_contained_in_clip: bool,
    blitter: &mut dyn Blitter,
) {
    let shifted_clip = match ShiftedIntRect::new(clip_rect, shift_edges_up) {
        Some(v) => v,
        None => return,
    };

    let clip = if path_contained_in_clip {
        None
    } else {
        Some(&shifted_clip)
    };
    let mut edges = match BasicEdgeBuilder::build_edges(path, clip, shift_edges_up) {
        Some(v) => v,
        None => return, // no edges to render, just return
    };

    edges.sort_by(|a, b| {
        let mut value_a = a.as_line().first_y;
        let mut value_b = b.as_line().first_y;

        if value_a == value_b {
            value_a = a.as_line().x;
            value_b = b.as_line().x;
        }

        value_a.cmp(&value_b)
    });

    for i in 0..edges.len() {
        // 0 will be set later, so start with 1.
        edges[i].prev = Some(i as u32 + 0);
        edges[i].next = Some(i as u32 + 2);
    }

    const EDGE_HEAD_Y: i32 = i32::MIN;
    const EDGE_TAIL_Y: i32 = i32::MAX;

    edges.insert(
        0,
        Edge::Line(LineEdge {
            prev: None,
            next: Some(1),
            x: i32::MIN,
            first_y: EDGE_HEAD_Y,
            ..LineEdge::default()
        }),
    );

    edges.push(Edge::Line(LineEdge {
        prev: Some(edges.len() as u32 - 1),
        next: None,
        first_y: EDGE_TAIL_Y,
        ..LineEdge::default()
    }));

    start_y <<= shift_edges_up;
    stop_y <<= shift_edges_up;

    let top = shifted_clip.shifted().y() as i32;
    if !path_contained_in_clip && start_y < top {
        start_y = top;
    }

    let bottom = shifted_clip.shifted().bottom() as i32;
    if !path_contained_in_clip && stop_y > bottom {
        stop_y = bottom;
    }

    let start_y = match u32::try_from(start_y) {
        Ok(v) => v,
        Err(_) => return,
    };
    let stop_y = match u32::try_from(stop_y) {
        Ok(v) => v,
        Err(_) => return,
    };

    // TODO: walk_simple_edges

    walk_edges(
        fill_rule,
        start_y,
        stop_y,
        shifted_clip.shifted().right(),
        &mut edges,
        blitter,
    );
}

// TODO: simplify!
fn walk_edges(
    fill_rule: FillRule,
    start_y: u32,
    stop_y: u32,
    right_clip: u32,
    edges: &mut [Edge],
    blitter: &mut dyn Blitter,
) {
    let mut curr_y = start_y;
    let winding_mask = if fill_rule == FillRule::EvenOdd {
        1
    } else {
        -1
    };

    loop {
        let mut w = 0i32;
        let mut left = 0u32;
        let mut prev_x = edges[0].x;

        let mut curr_idx = edges[0].next.unwrap() as usize;
        while edges[curr_idx].first_y <= curr_y as i32 {
            debug_assert!(edges[curr_idx].last_y >= curr_y as i32);

            let x = fdot16::round_to_i32(edges[curr_idx].x) as u32; // TODO: check

            if (w & winding_mask) == 0 {
                // we're starting interval
                left = x;
            }

            w += i32::from(edges[curr_idx].winding);

            if (w & winding_mask) == 0 {
                // we finished an interval
                if let Some(width) = LengthU32::new(x - left) {
                    blitter.blit_h(left, curr_y, width);
                }
            }

            let next_idx = edges[curr_idx].next.unwrap();
            let new_x;

            if edges[curr_idx].last_y == curr_y as i32 {
                // are we done with this edge?
                match &mut edges[curr_idx] {
                    Edge::Line(_) => {
                        remove_edge(curr_idx, edges);
                    }
                    Edge::Quadratic(ref mut quad) => {
                        if quad.curve_count > 0 && quad.update() {
                            new_x = quad.line.x;

                            if new_x < prev_x {
                                // ripple current edge backwards until it is x-sorted
                                backward_insert_edge_based_on_x(curr_idx, edges);
                            } else {
                                prev_x = new_x;
                            }
                        } else {
                            remove_edge(curr_idx, edges);
                        }
                    }
                    Edge::Cubic(ref mut cubic) => {
                        if cubic.curve_count < 0 && cubic.update() {
                            debug_assert!(cubic.line.first_y == curr_y as i32 + 1);

                            new_x = cubic.line.x;

                            if new_x < prev_x {
                                // ripple current edge backwards until it is x-sorted
                                backward_insert_edge_based_on_x(curr_idx, edges);
                            } else {
                                prev_x = new_x;
                            }
                        } else {
                            remove_edge(curr_idx, edges);
                        }
                    }
                }
            } else {
                debug_assert!(edges[curr_idx].last_y > curr_y as i32);
                new_x = edges[curr_idx].x + edges[curr_idx].dx;
                edges[curr_idx].x = new_x;

                if new_x < prev_x {
                    // ripple current edge backwards until it is x-sorted
                    backward_insert_edge_based_on_x(curr_idx, edges);
                } else {
                    prev_x = new_x;
                }
            }

            curr_idx = next_idx as usize;
        }

        if (w & winding_mask) != 0 {
            // was our right-edge culled away?
            if let Some(width) = LengthU32::new(right_clip - left) {
                blitter.blit_h(left, curr_y, width);
            }
        }

        curr_y += 1;
        if curr_y >= stop_y {
            break;
        }

        // now current edge points to the first edge with a Yint larger than curr_y
        insert_new_edges(curr_idx, curr_y as i32, edges);
    }
}

fn remove_edge(curr_idx: usize, edges: &mut [Edge]) {
    let prev = edges[curr_idx].prev.unwrap();
    let next = edges[curr_idx].next.unwrap();

    edges[prev as usize].next = Some(next);
    edges[next as usize].prev = Some(prev);
}

fn backward_insert_edge_based_on_x(curr_idx: usize, edges: &mut [Edge]) {
    let x = edges[curr_idx].x;
    let mut prev_idx = edges[curr_idx].prev.unwrap() as usize;
    while prev_idx != 0 {
        if edges[prev_idx].x > x {
            prev_idx = edges[prev_idx].prev.unwrap() as usize;
        } else {
            break;
        }
    }

    let next_idx = edges[prev_idx].next.unwrap() as usize;
    if next_idx != curr_idx {
        remove_edge(curr_idx, edges);
        insert_edge_after(curr_idx, prev_idx, edges);
    }
}

fn insert_edge_after(curr_idx: usize, after_idx: usize, edges: &mut [Edge]) {
    edges[curr_idx].prev = Some(after_idx as u32);
    edges[curr_idx].next = edges[after_idx].next;

    let after_next_idx = edges[after_idx].next.unwrap() as usize;
    edges[after_next_idx].prev = Some(curr_idx as u32);
    edges[after_idx].next = Some(curr_idx as u32);
}

// Start from the right side, searching backwards for the point to begin the new edge list
// insertion, marching forwards from here. The implementation could have started from the left
// of the prior insertion, and search to the right, or with some additional caching, binary
// search the starting point. More work could be done to determine optimal new edge insertion.
fn backward_insert_start(mut prev_idx: usize, x: FDot16, edges: &mut [Edge]) -> usize {
    while let Some(prev) = edges[prev_idx].prev {
        prev_idx = prev as usize;
        if edges[prev_idx].x <= x {
            break;
        }
    }

    prev_idx
}

fn insert_new_edges(mut new_idx: usize, curr_y: i32, edges: &mut [Edge]) {
    if edges[new_idx].first_y != curr_y {
        return;
    }

    let prev_idx = edges[new_idx].prev.unwrap() as usize;
    if edges[prev_idx].x <= edges[new_idx].x {
        return;
    }

    // find first x pos to insert
    let mut start_idx = backward_insert_start(prev_idx, edges[new_idx].x, edges);
    // insert the lot, fixing up the links as we go
    loop {
        let next_idx = edges[new_idx].next.unwrap() as usize;
        let mut keep_edge = false;
        loop {
            let after_idx = edges[start_idx].next.unwrap() as usize;
            if after_idx == new_idx {
                keep_edge = true;
                break;
            }

            if edges[after_idx].x >= edges[new_idx].x {
                break;
            }

            start_idx = after_idx;
        }

        if !keep_edge {
            remove_edge(new_idx, edges);
            insert_edge_after(new_idx, start_idx, edges);
        }

        start_idx = new_idx;
        new_idx = next_idx;

        if edges[new_idx].first_y != curr_y {
            break;
        }
    }
}
