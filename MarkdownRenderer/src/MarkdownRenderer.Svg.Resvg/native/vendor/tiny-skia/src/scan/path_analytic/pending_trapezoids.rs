// Copyright (c) JitHub contributors. Licensed under MIT.
//
//! Fill-pair trapezoids that stay open across sweep bands.
//!
//! Between two sweep events every fill pair is a trapezoid bounded by two
//! straight edges. A pair whose two edges are unchanged in the next band is
//! the same trapezoid continued, so its area over both bands is one taller
//! trapezoid between the same lines. Flattened curves create a band for
//! every vertex of every curve in a sweep component; without merging, each
//! such band re-integrates every other glyph's pairs as thin slivers. This
//! accumulator keeps each pair open from the band where it appeared and
//! integrates it once, when it closes, plus once per completed pixel row.
//!
//! The integrated region is exactly the union of the per-band slivers, so
//! coverage is the same exact area; only the floating-point grouping of
//! the sum differs. Rows are completed before the caller flushes them, and
//! every row is visited in increasing order, as the dense row reducer needs.

use alloc::vec::Vec;

use super::{
    ClipRect, CoverageAccumulator, CoverageError, Edge, Limits, accumulate_trapezoid,
    ensure_vec_capacity, vec_capacity_bytes,
};

#[derive(Clone, Copy, Debug, PartialEq)]
struct OpenPair {
    left: usize,
    right: usize,
    /// Ordinate up to which this pair's area has already been integrated.
    since: f64,
}

/// Shared inputs for integrating trapezoid area into one coverage owner.
pub(super) struct AreaSink<'a> {
    pub(super) edges: &'a [Edge],
    pub(super) clip: ClipRect,
    pub(super) coverage: &'a mut CoverageAccumulator,
    pub(super) partial: &'a mut u64,
    pub(super) limits: Limits,
    pub(super) base_bytes: usize,
}

#[derive(Default)]
pub(super) struct PendingTrapezoids {
    open: Vec<OpenPair>,
    /// End of the last band; open pairs are integrated at most up to here.
    last_end: Option<f64>,
}

impl PendingTrapezoids {
    pub(super) fn capacity_bytes(&self) -> usize {
        vec_capacity_bytes(&self.open)
    }

    /// Start the band `[y0, end]` whose fill pairs are `pairs`, in active
    /// order. Pairs that do not continue are integrated up to the end of the
    /// previous band. `base_bytes` covers every live byte except the
    /// coverage accumulator, including this owner's current capacity.
    pub(super) fn begin_band(
        &mut self,
        pairs: &[(usize, usize)],
        y0: f64,
        end: f64,
        sink: &mut AreaSink<'_>,
    ) -> Result<(), CoverageError> {
        // A sliver between two bands (from same-ordinate event grouping) is
        // not part of either band, so nothing may integrate across it.
        let contiguous = self
            .last_end
            .is_some_and(|last| last.to_bits() == y0.to_bits());
        let same_shape = contiguous && self.open.len() == pairs.len();
        let last_end = self.last_end.unwrap_or(y0);
        integrate_rows(&mut self.open, last_end, sink, |index, pair| {
            !same_shape || (pair.left, pair.right) != pairs[index]
        })?;

        if same_shape {
            for (open, &(left, right)) in self.open.iter_mut().zip(pairs) {
                if (open.left, open.right) != (left, right) {
                    *open = OpenPair {
                        left,
                        right,
                        since: y0,
                    };
                }
            }
        } else {
            self.open.clear();
            let own_bytes = self.capacity_bytes();
            let other_live = sink
                .base_bytes
                .saturating_sub(own_bytes)
                .saturating_add(sink.coverage.live_bytes());
            ensure_vec_capacity(&mut self.open, pairs.len(), other_live, sink.limits)?;
            sink.base_bytes = sink
                .base_bytes
                .saturating_sub(own_bytes)
                .saturating_add(self.capacity_bytes());
            self.open
                .extend(pairs.iter().map(|&(left, right)| OpenPair {
                    left,
                    right,
                    since: y0,
                }));
        }
        self.last_end = Some(end);
        Ok(())
    }

    /// Integrate every open pair up to `until`, so rows before it can be
    /// flushed. `until` must not pass the current band's end.
    pub(super) fn complete_until(
        &mut self,
        until: f64,
        sink: &mut AreaSink<'_>,
    ) -> Result<(), CoverageError> {
        integrate_rows(&mut self.open, until, sink, |_, _| true)
    }

    /// Integrate every open pair to the end of the last band.
    pub(super) fn finish(&mut self, sink: &mut AreaSink<'_>) -> Result<(), CoverageError> {
        if let Some(last_end) = self.last_end {
            integrate_rows(&mut self.open, last_end, sink, |_, _| true)?;
        }
        self.open.clear();
        Ok(())
    }
}

/// Integrate the selected `pairs` from their `since` ordinates up to
/// `until` and advance their `since`. Rows are visited in increasing order
/// across all selected pairs, as the dense row reducer requires.
fn integrate_rows(
    pairs: &mut [OpenPair],
    until: f64,
    sink: &mut AreaSink<'_>,
    selected: impl Fn(usize, &OpenPair) -> bool,
) -> Result<(), CoverageError> {
    let Some(first) = pairs
        .iter()
        .enumerate()
        .filter(|&(index, pair)| pair.since < until && selected(index, pair))
        .map(|(_, pair)| pair.since)
        .reduce(f64::min)
    else {
        return Ok(());
    };
    let row_start = first.floor() as i32;
    let row_end = until.ceil() as i32;
    for row in row_start..row_end {
        let row_top = row as f64;
        let yb = until.min(row_top + 1.0);
        let mut began = false;
        for (index, pair) in pairs.iter().enumerate() {
            let ya = pair.since.max(row_top);
            if yb <= ya || !selected(index, pair) {
                continue;
            }
            if !began && sink.coverage.is_dense() {
                sink.coverage
                    .begin_dense_row(row, sink.limits, sink.base_bytes)?;
            }
            began = true;
            accumulate_trapezoid(
                sink.edges[pair.left],
                sink.edges[pair.right],
                ya,
                yb,
                sink.clip,
                row,
                sink.coverage,
                sink.partial,
                sink.limits,
                sink.base_bytes,
            )?;
        }
    }
    for (index, pair) in pairs.iter_mut().enumerate() {
        if selected(index, pair) {
            pair.since = pair.since.max(until);
        }
    }
    Ok(())
}
