// Copyright (c) JitHub contributors. Licensed under MIT.
//
//! Bounded analytic area coverage for filled vector paths.
//!
//! Fallible bounded analytic area coverage for polygonalized paths.
//!
//! Rasterization is transactional: geometry, sweep state, and the complete
//! span command list remain private until every validation and budget check
//! succeeds. A caller may then replay those spans through its normal blitter.

use alloc::collections::BinaryHeap;
use alloc::vec::Vec;
use core::cmp::Ordering;
use tiny_skia_path::{Path, PathSegment, Transform};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ClipRect {
    pub left: f64,
    pub top: f64,
    pub right: f64,
    pub bottom: f64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FillRule {
    EvenOdd,
    NonZero,
}

#[derive(Clone, Copy, Debug)]
pub struct Limits {
    pub max_edges: usize,
    pub max_events: usize,
    pub max_active_edge_visits: u64,
    pub max_partial_pixel_intersections: u64,
    pub max_coverage_events: usize,
    pub max_spans: usize,
    /// Conservative peak budget for private sweep and coverage state.
    pub max_intermediate_bytes: usize,
    /// Maximum returned span-command allocation, separate from the caller's bitmap.
    pub max_output_bytes: usize,
    /// Combined live peak (intermediate state plus unpublished span output).
    /// A host renderer can lower this to its remaining request-local budget.
    pub max_total_bytes: usize,
    pub max_coordinate_abs: f64,
    /// Maximum permitted deviation between an input curve and its flattened
    /// polyline, in physical pixels.
    pub curve_error_px: f64,
    pub max_curve_depth: u8,
}

impl Default for Limits {
    fn default() -> Self {
        Self {
            max_edges: 100_000,
            max_events: 1_000_000,
            max_active_edge_visits: 8_000_000,
            max_partial_pixel_intersections: 4_000_000,
            max_coverage_events: 250_000,
            max_spans: 2_000_000,
            max_intermediate_bytes: 128 * 1024 * 1024,
            max_output_bytes: 64 * 1024 * 1024,
            max_total_bytes: 192 * 1024 * 1024,
            max_coordinate_abs: 1.0e9,
            // A 1/1024 physical-pixel chord deviation keeps the geometric
            // approximation well below one alpha quantum for small glyphs.
            curve_error_px: 1.0 / 1024.0,
            max_curve_depth: 20,
        }
    }
}

/// A validation, complexity, or allocation failure while producing analytic coverage.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CoverageError {
    /// A path contains a non-finite or disallowed coordinate.
    InvalidCoordinate,
    /// The raster clip is invalid.
    InvalidClip,
    /// Flattened or clipped geometry exceeded the edge limit.
    EdgeLimit,
    /// The sweep exceeded its event limit.
    EventLimit,
    /// The bounded sweep or allocation budget was exceeded.
    ActiveWorkLimit,
    /// The rasterizer exceeded the partial-pixel intersection limit.
    PartialPixelLimit,
    /// Coverage command count or storage exceeded its limit.
    SpanLimit,
    /// Curve flattening exceeded its configured complexity limit.
    CurveLimit,
    /// A finite calculation could not be represented safely.
    NumericRange,
    /// A fallible allocation failed.
    AllocationFailure,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CoverageSpan {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub alpha: u8,
}

// Within this coordinate bound, the product used by `Edge::x_at` is safely
// representable for every sample inside a clipped edge: both coordinate
// deltas are at most 2e150, so their product is at most 4e300. Larger caller
// limits retain the per-band finiteness check for fail-closed behavior.
const MAX_COORDINATE_WITH_PROVEN_FINITE_INTERPOLATION: f64 = 1.0e150;

#[derive(Clone, Copy, Debug, Default)]
struct RasterUsage {
    edges: usize,
    processed_events: usize,
    active_work: u64,
    partial_intersections: u64,
    range_runs: usize,
    output_spans: usize,
}

impl RasterUsage {
    fn add(&mut self, other: Self) -> Result<(), CoverageError> {
        self.edges = self
            .edges
            .checked_add(other.edges)
            .ok_or(CoverageError::EdgeLimit)?;
        self.processed_events = self
            .processed_events
            .checked_add(other.processed_events)
            .ok_or(CoverageError::EventLimit)?;
        self.active_work = self
            .active_work
            .checked_add(other.active_work)
            .ok_or(CoverageError::ActiveWorkLimit)?;
        self.partial_intersections = self
            .partial_intersections
            .checked_add(other.partial_intersections)
            .ok_or(CoverageError::PartialPixelLimit)?;
        self.range_runs = self
            .range_runs
            .checked_add(other.range_runs)
            .ok_or(CoverageError::SpanLimit)?;
        self.output_spans = self
            .output_spans
            .checked_add(other.output_spans)
            .ok_or(CoverageError::SpanLimit)?;
        Ok(())
    }
}

#[derive(Clone, Copy, Debug)]
struct ContourBounds {
    original_index: usize,
    left_pixel: i64,
    right_pixel: i64,
    component: usize,
}

#[derive(Clone, Copy, Debug)]
struct Edge {
    a: Point,
    b: Point,
    winding: i32,
    id: usize,
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct CoverageEvent {
    y: i32,
    x: i32,
    range_delta: f64,
    partial_delta: f64,
}

#[derive(Clone, Copy, Default)]
struct DenseCell {
    range_delta: f64,
    partial: f64,
}

/// A bounded one-row accumulator used when the clipped path bounds are narrow
/// enough to make scanning the physical row cheaper than sorting sparse events.
/// The last cell stores the terminal range-difference value at x_origin+width.
struct DenseRow {
    x_origin: i32,
    width: usize,
    cells: Vec<DenseCell>,
    current_row: Option<i32>,
}

impl DenseRow {
    fn live_bytes(&self) -> usize {
        vec_capacity_bytes(&self.cells)
    }
}

struct CoverageAccumulator {
    events: Vec<CoverageEvent>,
    spans: Vec<CoverageSpan>,
    /// Number of difference events pending in rows that are not complete yet.
    event_count: usize,
    /// Cumulative full-coverage runs, including runs that quantize to zero.
    /// This uses the existing span-work ceiling, so flushing rows cannot
    /// reset the path's total coverage work.
    total_range_count: usize,
    /// Retained capacity of `events`; output capacity is charged separately.
    reserved_bytes: usize,
    /// Rows below this coordinate have already been reduced into spans.
    flushed_before_row: i64,
    /// Conservatively charged work spent sorting and reducing coverage keys.
    compaction_work: u64,
    /// Sweep visits already charged by the caller; compaction shares that cap.
    sweep_work: u64,
    /// Bounded pixel visits used to reduce dense rows.
    dense_work: u64,
    /// Present only when the clipped physical path width fits the request budget.
    dense: Option<DenseRow>,
}

impl Default for CoverageAccumulator {
    fn default() -> Self {
        Self {
            events: Vec::new(),
            spans: Vec::new(),
            event_count: 0,
            total_range_count: 0,
            reserved_bytes: 0,
            // No row has been flushed yet. Zero is a valid scanline for
            // translated paths and negative-coordinate clips.
            flushed_before_row: i64::MIN,
            compaction_work: 0,
            sweep_work: 0,
            dense_work: 0,
            dense: None,
        }
    }
}

impl CoverageAccumulator {
    fn live_bytes(&self) -> usize {
        self.reserved_bytes
            .saturating_add(self.dense.as_ref().map_or(0, DenseRow::live_bytes))
            .saturating_add(vec_capacity_bytes(&self.spans))
    }

    fn set_sweep_work(&mut self, sweep_work: u64, limits: Limits) -> Result<(), CoverageError> {
        if sweep_work
            .saturating_add(self.compaction_work)
            .saturating_add(self.dense_work)
            > limits.max_active_edge_visits
        {
            return Err(CoverageError::ActiveWorkLimit);
        }
        self.sweep_work = sweep_work;
        Ok(())
    }

    fn is_dense(&self) -> bool {
        self.dense.is_some()
    }

    /// Select dense row reduction only when its entire fixed buffer and a
    /// conservative scan-work estimate fit. Failure to admit this optional
    /// optimization leaves the sparse exact-area path available.
    fn select_dense(
        &mut self,
        x_origin: i32,
        width: usize,
        estimated_rows: usize,
        limits: Limits,
        base_bytes: usize,
    ) -> bool {
        if width == 0 || width.saturating_add(1) > limits.max_coverage_events {
            return false;
        }
        let Some(estimated_work) = (width as u64).checked_mul(estimated_rows as u64) else {
            return false;
        };
        if estimated_work > limits.max_active_edge_visits / 2 {
            return false;
        }

        let mut cells = Vec::<DenseCell>::new();
        let other_live = base_bytes
            .saturating_add(vec_capacity_bytes(&self.events))
            .saturating_add(vec_capacity_bytes(&self.spans));
        if ensure_vec_capacity(&mut cells, width + 1, other_live, limits).is_err() {
            return false;
        }
        cells.resize(width + 1, DenseCell::default());
        let live = other_live.saturating_add(vec_capacity_bytes(&cells));
        if live > limits.max_intermediate_bytes || live > limits.max_total_bytes {
            return false;
        }
        self.dense = Some(DenseRow {
            x_origin,
            width,
            cells,
            current_row: None,
        });
        true
    }

    fn begin_dense_row(
        &mut self,
        row: i32,
        limits: Limits,
        base_bytes: usize,
    ) -> Result<(), CoverageError> {
        let Some(current) = self.dense.as_ref().and_then(|dense| dense.current_row) else {
            if self.dense.is_none() {
                return Err(CoverageError::NumericRange);
            }
            self.dense
                .as_mut()
                .expect("dense row checked above")
                .current_row = Some(row);
            return Ok(());
        };
        if self.dense.is_none() {
            return Err(CoverageError::NumericRange);
        }
        if row < current {
            return Err(CoverageError::NumericRange);
        }
        if row > current {
            self.flush_dense_row(limits, base_bytes)?;
        }
        self.dense
            .as_mut()
            .expect("dense row checked above")
            .current_row = Some(row);
        Ok(())
    }

    fn flush_dense_row(&mut self, limits: Limits, base_bytes: usize) -> Result<(), CoverageError> {
        let Some(mut dense) = self.dense.take() else {
            return Ok(());
        };
        let Some(row) = dense.current_row.take() else {
            self.dense = Some(dense);
            return Ok(());
        };
        let row_work = u64::try_from(dense.width).map_err(|_| CoverageError::ActiveWorkLimit)?;
        let next_dense_work = self
            .dense_work
            .checked_add(row_work)
            .ok_or(CoverageError::ActiveWorkLimit)?;
        if self
            .sweep_work
            .saturating_add(self.compaction_work)
            .saturating_add(next_dense_work)
            > limits.max_active_edge_visits
        {
            self.dense = Some(dense);
            return Err(CoverageError::ActiveWorkLimit);
        }
        self.dense_work = next_dense_work;

        let mut range_coverage = 0.0f64;
        let mut run_start = None;
        let mut run_alpha = 0u8;
        let other_live_bytes = base_bytes
            .saturating_add(self.reserved_bytes)
            .saturating_add(dense.live_bytes());
        for index in 0..dense.width {
            let cell = &mut dense.cells[index];
            range_coverage += cell.range_delta;
            if !range_coverage.is_finite() {
                return Err(CoverageError::NumericRange);
            }
            let alpha =
                ((range_coverage + cell.partial).clamp(0.0, 1.0) * 255.0 + 0.5).floor() as u8;
            cell.range_delta = 0.0;
            cell.partial = 0.0;

            match (run_start, alpha) {
                (Some(_start), next_alpha) if next_alpha == run_alpha && next_alpha != 0 => {}
                (Some(start), _) => {
                    push_span(
                        &mut self.spans,
                        CoverageSpan {
                            x: dense.x_origin + start as i32,
                            y: row,
                            width: index.saturating_sub(start) as u32,
                            alpha: run_alpha,
                        },
                        limits,
                        other_live_bytes,
                    )?;
                    run_start = if alpha == 0 { None } else { Some(index) };
                    run_alpha = alpha;
                }
                (None, 0) => {}
                (None, next_alpha) => {
                    run_start = Some(index);
                    run_alpha = next_alpha;
                }
            }
        }
        let terminal = &mut dense.cells[dense.width];
        range_coverage += terminal.range_delta;
        terminal.range_delta = 0.0;
        terminal.partial = 0.0;
        if !range_coverage.is_finite() || range_coverage.abs() > 1.0e-9 {
            return Err(CoverageError::NumericRange);
        }
        if let Some(start) = run_start {
            push_span(
                &mut self.spans,
                CoverageSpan {
                    x: dense.x_origin + start as i32,
                    y: row,
                    width: dense.width.saturating_sub(start) as u32,
                    alpha: run_alpha,
                },
                limits,
                other_live_bytes,
            )?;
        }
        self.dense = Some(dense);
        Ok(())
    }

    /// Sort and combine identical breakpoints in place when the pending-event
    /// ceiling is reached. No second map or scratch allocation is needed.
    /// Each record touched by a compaction is charged to the same cumulative
    /// work ceiling as the geometry sweep; range and partial-pixel producers
    /// also retain their independent cumulative limits.
    fn compact_events(&mut self, limits: Limits) -> Result<(), CoverageError> {
        let len = self.events.len();
        if len <= 1 {
            self.event_count = len;
            return Ok(());
        }

        let record_count = u64::try_from(len).map_err(|_| CoverageError::ActiveWorkLimit)?;
        let work = self
            .compaction_work
            .checked_add(record_count)
            .ok_or(CoverageError::ActiveWorkLimit)?;
        if self.sweep_work.saturating_add(work) > limits.max_active_edge_visits {
            return Err(CoverageError::ActiveWorkLimit);
        }

        self.events
            .sort_unstable_by(|a, b| a.y.cmp(&b.y).then_with(|| a.x.cmp(&b.x)));
        let mut read = 0usize;
        let mut write = 0usize;
        while read < len {
            let y = self.events[read].y;
            let x = self.events[read].x;
            let mut range_delta = 0.0;
            let mut partial_delta = 0.0;
            while read < len && self.events[read].y == y && self.events[read].x == x {
                range_delta += self.events[read].range_delta;
                partial_delta += self.events[read].partial_delta;
                read += 1;
            }
            if !range_delta.is_finite() || !partial_delta.is_finite() {
                return Err(CoverageError::NumericRange);
            }
            if range_delta != 0.0 || partial_delta != 0.0 {
                self.events[write] = CoverageEvent {
                    y,
                    x,
                    range_delta,
                    partial_delta,
                };
                write += 1;
            }
        }
        self.events.truncate(write);
        self.event_count = write;
        self.reserved_bytes = vec_capacity_bytes(&self.events);
        self.compaction_work = work;
        Ok(())
    }

    fn reserve_event_capacity(
        &mut self,
        required_capacity: usize,
        limits: Limits,
        base_bytes: usize,
    ) -> Result<(), CoverageError> {
        let element_size = core::mem::size_of::<CoverageEvent>();
        let output_bytes = vec_capacity_bytes(&self.spans);
        let current_bytes = vec_capacity_bytes(&self.events);
        let current_live = base_bytes
            .saturating_add(current_bytes)
            .saturating_add(output_bytes);
        if current_live > limits.max_intermediate_bytes || current_live > limits.max_total_bytes {
            return Err(CoverageError::ActiveWorkLimit);
        }
        if required_capacity <= self.events.capacity() {
            return Ok(());
        }

        let capacity_limit = limits
            .max_intermediate_bytes
            .saturating_sub(base_bytes)
            .saturating_sub(output_bytes)
            .min(
                limits
                    .max_total_bytes
                    .saturating_sub(base_bytes)
                    .saturating_sub(output_bytes),
            )
            / element_size;
        let target_capacity = required_capacity
            .max(self.events.capacity().max(8).saturating_mul(2))
            .min(limits.max_coverage_events)
            .min(capacity_limit.saturating_sub(self.events.capacity()));
        if target_capacity < required_capacity {
            return Err(CoverageError::ActiveWorkLimit);
        }
        let old_bytes = current_bytes;
        self.events
            .try_reserve_exact(target_capacity.saturating_sub(self.events.len()))
            .map_err(|_| CoverageError::AllocationFailure)?;
        let new_bytes = vec_capacity_bytes(&self.events);
        let peak_bytes = base_bytes
            .saturating_add(old_bytes)
            .saturating_add(new_bytes)
            .saturating_add(output_bytes);
        if peak_bytes > limits.max_intermediate_bytes || peak_bytes > limits.max_total_bytes {
            return Err(CoverageError::ActiveWorkLimit);
        }
        self.reserved_bytes = new_bytes;
        Ok(())
    }

    fn add_event_pair(
        &mut self,
        events: [CoverageEvent; 2],
        limits: Limits,
        base_bytes: usize,
    ) -> Result<(), CoverageError> {
        let required_count = self
            .event_count
            .checked_add(2)
            .ok_or(CoverageError::SpanLimit)?;
        if required_count > limits.max_coverage_events {
            self.compact_events(limits)?;
        }
        let required_count = self
            .event_count
            .checked_add(2)
            .ok_or(CoverageError::SpanLimit)?;
        if required_count > limits.max_coverage_events {
            return Err(CoverageError::EventLimit);
        }

        self.reserve_event_capacity(required_count, limits, base_bytes)?;
        self.events.extend_from_slice(&events);
        self.event_count = required_count;
        self.reserved_bytes = vec_capacity_bytes(&self.events);
        Ok(())
    }

    /// Reduce rows that cannot receive any more events. Geometry events are
    /// monotonic between bands, but trapezoids within one band are emitted
    /// pair-major and may revisit earlier rows; defer sorting until the band
    /// boundary proves that a row is complete.
    fn flush_completed_rows(
        &mut self,
        before_row: i64,
        limits: Limits,
        base_bytes: usize,
    ) -> Result<(), CoverageError> {
        if before_row <= self.flushed_before_row {
            return Ok(());
        }
        if self.dense.is_some() {
            let should_flush = self
                .dense
                .as_ref()
                .and_then(|dense| dense.current_row)
                .is_some_and(|row| i64::from(row) < before_row);
            if should_flush {
                self.flush_dense_row(limits, base_bytes)?;
            }
            self.flushed_before_row = before_row;
            return Ok(());
        }
        if self.events.is_empty() {
            self.flushed_before_row = before_row;
            return Ok(());
        }

        self.events
            .sort_unstable_by(|a, b| a.y.cmp(&b.y).then_with(|| a.x.cmp(&b.x)));
        let complete_count = self
            .events
            .partition_point(|event| i64::from(event.y) < before_row);
        let mut index = 0usize;
        while index < complete_count {
            let y = self.events[index].y;
            let mut range_coverage = 0.0f64;
            let mut partial_coverage = 0.0f64;
            while index < complete_count && self.events[index].y == y {
                let x = self.events[index].x;
                while index < complete_count
                    && self.events[index].y == y
                    && self.events[index].x == x
                {
                    range_coverage += self.events[index].range_delta;
                    partial_coverage += self.events[index].partial_delta;
                    index += 1;
                }

                let Some(next_x) = self
                    .events
                    .get(index)
                    .filter(|event| event.y == y)
                    .map(|event| event.x)
                else {
                    if range_coverage.abs() > 1.0e-9 || partial_coverage.abs() > 1.0e-9 {
                        return Err(CoverageError::NumericRange);
                    }
                    break;
                };
                if !range_coverage.is_finite() || !partial_coverage.is_finite() || next_x <= x {
                    return Err(CoverageError::NumericRange);
                }
                let alpha = ((range_coverage + partial_coverage).clamp(0.0, 1.0) * 255.0 + 0.5)
                    .floor() as u8;
                if alpha != 0 {
                    let width = (next_x as i64 - x as i64) as u32;
                    let other_live_bytes = base_bytes.saturating_add(self.reserved_bytes);
                    push_span(
                        &mut self.spans,
                        CoverageSpan { x, y, width, alpha },
                        limits,
                        other_live_bytes,
                    )?;
                }
            }
        }

        if complete_count != 0 {
            let remaining = self.events.len() - complete_count;
            self.events.copy_within(complete_count.., 0);
            self.events.truncate(remaining);
        }
        self.event_count = self.events.len();
        self.flushed_before_row = before_row;
        Ok(())
    }

    fn add_range(
        &mut self,
        y: i32,
        start: i32,
        end: i32,
        amount: f64,
        limits: Limits,
        base_bytes: usize,
    ) -> Result<(), CoverageError> {
        if end <= start || amount <= 0.0 {
            return Ok(());
        }
        if i64::from(y) < self.flushed_before_row {
            return Err(CoverageError::NumericRange);
        }
        let total_range_count = self
            .total_range_count
            .checked_add(1)
            .ok_or(CoverageError::SpanLimit)?;
        if total_range_count > limits.max_spans {
            return Err(CoverageError::SpanLimit);
        }
        if let Some(dense) = self.dense.as_mut() {
            if dense.current_row != Some(y) {
                return Err(CoverageError::NumericRange);
            }
            let start_index = usize::try_from(i64::from(start) - i64::from(dense.x_origin))
                .map_err(|_| CoverageError::NumericRange)?;
            let end_index = usize::try_from(i64::from(end) - i64::from(dense.x_origin))
                .map_err(|_| CoverageError::NumericRange)?;
            if end_index > dense.width || start_index >= end_index {
                return Err(CoverageError::NumericRange);
            }
            let start_value = dense.cells[start_index].range_delta + amount;
            let end_value = dense.cells[end_index].range_delta - amount;
            if !start_value.is_finite() || !end_value.is_finite() {
                return Err(CoverageError::NumericRange);
            }
            dense.cells[start_index].range_delta = start_value;
            dense.cells[end_index].range_delta = end_value;
            self.total_range_count = total_range_count;
            return Ok(());
        }
        self.add_event_pair(
            [
                CoverageEvent {
                    y,
                    x: start,
                    range_delta: amount,
                    partial_delta: 0.0,
                },
                CoverageEvent {
                    y,
                    x: end,
                    range_delta: -amount,
                    partial_delta: 0.0,
                },
            ],
            limits,
            base_bytes,
        )?;
        self.total_range_count = total_range_count;
        Ok(())
    }

    fn add_partial(
        &mut self,
        y: i32,
        x: i32,
        amount: f64,
        limits: Limits,
        base_bytes: usize,
    ) -> Result<(), CoverageError> {
        if amount <= 0.0 {
            return Ok(());
        }
        let end = x.checked_add(1).ok_or(CoverageError::NumericRange)?;
        if i64::from(y) < self.flushed_before_row {
            return Err(CoverageError::NumericRange);
        }
        if let Some(dense) = self.dense.as_mut() {
            if dense.current_row != Some(y) {
                return Err(CoverageError::NumericRange);
            }
            let index = usize::try_from(i64::from(x) - i64::from(dense.x_origin))
                .map_err(|_| CoverageError::NumericRange)?;
            if index >= dense.width {
                return Err(CoverageError::NumericRange);
            }
            let value = dense.cells[index].partial + amount;
            if !value.is_finite() {
                return Err(CoverageError::NumericRange);
            }
            dense.cells[index].partial = value;
            return Ok(());
        }
        self.add_event_pair(
            [
                CoverageEvent {
                    y,
                    x,
                    range_delta: 0.0,
                    partial_delta: amount,
                },
                CoverageEvent {
                    y,
                    x: end,
                    range_delta: 0.0,
                    partial_delta: -amount,
                },
            ],
            limits,
            base_bytes,
        )
    }

    fn finish(
        mut self,
        limits: Limits,
        base_intermediate_bytes: usize,
    ) -> Result<Vec<CoverageSpan>, CoverageError> {
        self.flush_completed_rows(i64::MAX, limits, base_intermediate_bytes)?;
        Ok(self.spans)
    }
}

#[derive(Clone, Copy)]
enum PairSlot {
    Empty,
    Tombstone,
    Occupied((usize, usize)),
}

/// Request-local open-addressed set with explicit Vec capacity accounting.
/// Unlike std::collections::HashSet, its backing allocation and resize peak
/// are known before allocation and can be charged exactly to the caller.
#[derive(Default)]
struct ScheduledPairs {
    slots: Vec<PairSlot>,
    len: usize,
    tombstones: usize,
}

impl ScheduledPairs {
    fn capacity_bytes(&self) -> usize {
        self.slots
            .capacity()
            .saturating_mul(core::mem::size_of::<PairSlot>())
    }

    fn contains(&self, key: (usize, usize)) -> bool {
        self.find(key).is_some()
    }

    fn find(&self, key: (usize, usize)) -> Option<usize> {
        if self.slots.is_empty() {
            return None;
        }
        let mask = self.slots.len() - 1;
        let mut index = hash_pair(key) & mask;
        for _ in 0..self.slots.len() {
            match self.slots[index] {
                PairSlot::Empty => return None,
                PairSlot::Tombstone => {}
                PairSlot::Occupied(found) if found == key => return Some(index),
                PairSlot::Occupied(_) => {}
            }
            index = (index + 1) & mask;
        }
        None
    }

    fn insert(
        &mut self,
        key: (usize, usize),
        limits: Limits,
        other_live_bytes: usize,
    ) -> Result<bool, CoverageError> {
        if self.contains(key) {
            return Ok(false);
        }
        if self.len >= limits.max_events {
            return Err(CoverageError::EventLimit);
        }
        let next_len = self.len.checked_add(1).ok_or(CoverageError::EventLimit)?;
        let must_grow = self.slots.is_empty()
            || next_len.saturating_mul(10) >= self.slots.len().saturating_mul(7);
        let must_rehash = !must_grow && self.tombstones > self.slots.len() / 4;
        if must_grow || must_rehash {
            let target_len = if must_grow {
                self.slots
                    .len()
                    .max(8)
                    .checked_mul(2)
                    .ok_or(CoverageError::EventLimit)?
            } else {
                self.slots.len()
            };
            let slot_bytes = core::mem::size_of::<PairSlot>();
            let old_bytes = self.capacity_bytes();
            let new_bytes = target_len.saturating_mul(slot_bytes);
            let peak_bytes = other_live_bytes
                .saturating_add(old_bytes)
                .saturating_add(new_bytes);
            if peak_bytes > limits.max_intermediate_bytes || peak_bytes > limits.max_total_bytes {
                return Err(CoverageError::ActiveWorkLimit);
            }
            let mut replacement = Vec::new();
            replacement
                .try_reserve_exact(target_len)
                .map_err(|_| CoverageError::AllocationFailure)?;
            if other_live_bytes
                .saturating_add(old_bytes)
                .saturating_add(replacement.capacity().saturating_mul(slot_bytes))
                > limits.max_intermediate_bytes
                || other_live_bytes
                    .saturating_add(old_bytes)
                    .saturating_add(replacement.capacity().saturating_mul(slot_bytes))
                    > limits.max_total_bytes
            {
                return Err(CoverageError::ActiveWorkLimit);
            }
            replacement.resize(target_len, PairSlot::Empty);
            for slot in self.slots.iter().copied() {
                if let PairSlot::Occupied(pair) = slot {
                    insert_pair_raw(&mut replacement, pair);
                }
            }
            self.slots = replacement;
            self.tombstones = 0;
        }
        if !insert_pair_raw(&mut self.slots, key) {
            return Err(CoverageError::NumericRange);
        }
        self.len += 1;
        Ok(true)
    }

    fn remove(&mut self, key: (usize, usize)) -> bool {
        let Some(index) = self.find(key) else {
            return false;
        };
        self.slots[index] = PairSlot::Tombstone;
        self.len -= 1;
        self.tombstones += 1;
        true
    }
}

fn hash_pair(pair: (usize, usize)) -> usize {
    fn mix(mut value: u64) -> u64 {
        value = (value ^ (value >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        value = (value ^ (value >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        value ^ (value >> 31)
    }
    let left = mix(pair.0 as u64);
    let right = mix((pair.1 as u64).wrapping_add(0x9E37_79B9_7F4A_7C15));
    mix(left ^ right.rotate_left(23)) as usize
}

fn insert_pair_raw(slots: &mut [PairSlot], pair: (usize, usize)) -> bool {
    if slots.is_empty() {
        return false;
    }
    let mask = slots.len() - 1;
    let mut index = hash_pair(pair) & mask;
    let mut first_tombstone = None;
    for _ in 0..slots.len() {
        match slots[index] {
            PairSlot::Empty => {
                let target = first_tombstone.unwrap_or(index);
                slots[target] = PairSlot::Occupied(pair);
                return true;
            }
            PairSlot::Tombstone => {
                first_tombstone.get_or_insert(index);
            }
            PairSlot::Occupied(found) if found == pair => return false,
            PairSlot::Occupied(_) => {}
        }
        index = (index + 1) & mask;
    }
    if let Some(target) = first_tombstone {
        slots[target] = PairSlot::Occupied(pair);
        true
    } else {
        false
    }
}

#[derive(Default)]
struct SweepScratch {
    group: Vec<Event>,
    remove: Vec<usize>,
    add: Vec<usize>,
    cross_x: Vec<f64>,
    crossing_ids: Vec<CrossingEntry>,
    changed: Vec<usize>,
    reinsert: Vec<CrossingGroup>,
    candidates: Vec<(usize, usize)>,
    band_edges: Vec<usize>,
    band_stack: Vec<usize>,
    fill_pairs: Vec<(usize, usize)>,
}

#[derive(Clone, Copy)]
struct CrossingEntry {
    group: usize,
    edge: usize,
}

#[derive(Clone, Copy)]
struct CrossingGroup {
    x: f64,
    seed_lo: Option<usize>,
    seed_hi: Option<usize>,
}

impl SweepScratch {
    fn capacity_bytes(&self) -> usize {
        self.group.capacity() * core::mem::size_of::<Event>()
            + self.remove.capacity() * core::mem::size_of::<usize>()
            + self.add.capacity() * core::mem::size_of::<usize>()
            + self.cross_x.capacity() * core::mem::size_of::<f64>()
            + self.crossing_ids.capacity() * core::mem::size_of::<CrossingEntry>()
            + self.changed.capacity() * core::mem::size_of::<usize>()
            + self.reinsert.capacity() * core::mem::size_of::<CrossingGroup>()
            + self.candidates.capacity() * core::mem::size_of::<(usize, usize)>()
            + self.band_edges.capacity() * core::mem::size_of::<usize>()
            + self.band_stack.capacity() * core::mem::size_of::<usize>()
            + self.fill_pairs.capacity() * core::mem::size_of::<(usize, usize)>()
    }
}

fn ensure_vec_capacity<T>(
    values: &mut Vec<T>,
    required_len: usize,
    other_live_bytes: usize,
    limits: Limits,
) -> Result<(), CoverageError> {
    ensure_vec_capacity_with_output_limit(values, required_len, other_live_bytes, None, limits)
}

/// Reserve an internal vector while separately bounding the final capacity of
/// a span vector. `other_live_bytes` participates in both intermediate and
/// combined-live accounting; `max_output_bytes` limits this vector's retained
/// final capacity, not temporary component outputs that are separately
/// charged as live memory.
fn ensure_vec_capacity_with_output_limit<T>(
    values: &mut Vec<T>,
    required_len: usize,
    other_live_bytes: usize,
    output_limit_bytes: Option<usize>,
    limits: Limits,
) -> Result<(), CoverageError> {
    let element_size = core::mem::size_of::<T>();
    let current_bytes =
        other_live_bytes.saturating_add(values.capacity().saturating_mul(element_size));
    if current_bytes > limits.max_intermediate_bytes || current_bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    if required_len <= values.capacity() {
        if output_limit_bytes
            .is_some_and(|limit| values.capacity().saturating_mul(element_size) > limit)
        {
            return Err(CoverageError::SpanLimit);
        }
        return Ok(());
    }

    let output_capacity_limit = output_limit_bytes
        .map(|limit| limit / element_size.max(1))
        .unwrap_or(usize::MAX);
    if required_len > output_capacity_limit {
        return Err(CoverageError::SpanLimit);
    }

    let capacity_limit = if element_size == 0 {
        usize::MAX
    } else {
        limits
            .max_intermediate_bytes
            .saturating_sub(other_live_bytes)
            .min(limits.max_total_bytes.saturating_sub(other_live_bytes))
            / element_size
    };
    // During a moved Vec growth, the old and replacement allocations can both
    // be live while elements are copied. The other-live figure excludes this
    // vector, so reserve only capacity that fits alongside its old buffer.
    let replacement_capacity_limit = capacity_limit.saturating_sub(values.capacity());
    if required_len > replacement_capacity_limit {
        return Err(CoverageError::ActiveWorkLimit);
    }
    let doubled_capacity = values
        .capacity()
        .max(4)
        .saturating_mul(2)
        .min(replacement_capacity_limit)
        .min(output_capacity_limit);
    let projected_capacity = required_len.max(doubled_capacity);
    let old_capacity = values.capacity();
    let projected_bytes = other_live_bytes.saturating_add(
        old_capacity
            .saturating_add(projected_capacity)
            .saturating_mul(element_size),
    );
    if projected_bytes > limits.max_intermediate_bytes || projected_bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    // `try_reserve_exact` takes an additional count relative to len, not
    // capacity. Reserve the full admitted geometric target in one
    // allocation instead of degenerating into one-slot growth.
    let additional = projected_capacity.saturating_sub(values.len());
    values
        .try_reserve_exact(additional)
        .map_err(|_| CoverageError::AllocationFailure)?;
    let actual_peak_bytes = other_live_bytes.saturating_add(
        old_capacity
            .saturating_add(values.capacity())
            .saturating_mul(element_size),
    );
    if actual_peak_bytes > limits.max_intermediate_bytes
        || actual_peak_bytes > limits.max_total_bytes
    {
        return Err(CoverageError::ActiveWorkLimit);
    }
    if output_limit_bytes
        .is_some_and(|limit| values.capacity().saturating_mul(element_size) > limit)
    {
        return Err(CoverageError::SpanLimit);
    }
    Ok(())
}

fn vec_capacity_bytes<T>(values: &Vec<T>) -> usize {
    values.capacity().saturating_mul(core::mem::size_of::<T>())
}

fn push_span(
    spans: &mut Vec<CoverageSpan>,
    span: CoverageSpan,
    limits: Limits,
    other_live_bytes: usize,
) -> Result<(), CoverageError> {
    if spans.len() >= limits.max_spans {
        return Err(CoverageError::SpanLimit);
    }
    let element_size = core::mem::size_of::<CoverageSpan>();
    let max_elements = limits.max_output_bytes / element_size;
    let current_live_bytes = other_live_bytes.saturating_add(vec_capacity_bytes(spans));
    if current_live_bytes > limits.max_intermediate_bytes
        || current_live_bytes > limits.max_total_bytes
        || spans.capacity() > max_elements
    {
        return Err(CoverageError::ActiveWorkLimit);
    }
    if spans.len() >= max_elements {
        return Err(CoverageError::SpanLimit);
    }
    if spans.len() == spans.capacity() {
        let minimum_capacity = spans.len().checked_add(1).ok_or(CoverageError::SpanLimit)?;
        if minimum_capacity > max_elements {
            return Err(CoverageError::SpanLimit);
        }
        let total_capacity_limit = limits
            .max_intermediate_bytes
            .saturating_sub(other_live_bytes)
            .min(limits.max_total_bytes.saturating_sub(other_live_bytes))
            / element_size;
        let replacement_capacity_limit = total_capacity_limit.saturating_sub(spans.capacity());
        if minimum_capacity > replacement_capacity_limit {
            return Err(CoverageError::ActiveWorkLimit);
        }
        let doubled_capacity = spans
            .capacity()
            .max(4)
            .saturating_mul(2)
            .min(replacement_capacity_limit)
            .min(max_elements);
        let target_capacity = minimum_capacity.max(doubled_capacity);
        let old_capacity = spans.capacity();
        let peak_bytes = other_live_bytes.saturating_add(
            old_capacity
                .saturating_add(target_capacity)
                .saturating_mul(element_size),
        );
        if peak_bytes > limits.max_intermediate_bytes || peak_bytes > limits.max_total_bytes {
            return Err(CoverageError::SpanLimit);
        }
        spans
            .try_reserve_exact(target_capacity.saturating_sub(spans.len()))
            .map_err(|_| CoverageError::AllocationFailure)?;
        let actual_peak_bytes = other_live_bytes.saturating_add(
            old_capacity
                .saturating_add(spans.capacity())
                .saturating_mul(element_size),
        );
        if spans.capacity() > replacement_capacity_limit
            || spans.capacity() > max_elements
            || actual_peak_bytes > limits.max_intermediate_bytes
            || actual_peak_bytes > limits.max_total_bytes
        {
            return Err(CoverageError::SpanLimit);
        }
    }
    spans.push(span);
    Ok(())
}

fn push_event(
    heap: &mut BinaryHeap<Event>,
    event: Event,
    event_count: &mut usize,
    limits: Limits,
    other_live_bytes: usize,
) -> Result<(), CoverageError> {
    let next_count = event_count
        .checked_add(1)
        .ok_or(CoverageError::EventLimit)?;
    if next_count > limits.max_events {
        return Err(CoverageError::EventLimit);
    }
    if heap.len() == heap.capacity() {
        let required_capacity = heap.len().checked_add(1).ok_or(CoverageError::EventLimit)?;
        let element_size = core::mem::size_of::<Event>();
        let capacity_limit = limits
            .max_intermediate_bytes
            .saturating_sub(other_live_bytes)
            .min(limits.max_total_bytes.saturating_sub(other_live_bytes))
            / element_size;
        let old_capacity = heap.capacity();
        let replacement_capacity_limit = capacity_limit.saturating_sub(old_capacity);
        if required_capacity > replacement_capacity_limit {
            return Err(CoverageError::ActiveWorkLimit);
        }
        let doubled_capacity = heap.capacity().max(8).saturating_mul(2).min(capacity_limit);
        let target_capacity =
            required_capacity.max(doubled_capacity.min(replacement_capacity_limit));
        let peak_bytes = other_live_bytes.saturating_add(
            old_capacity
                .saturating_add(target_capacity)
                .saturating_mul(element_size),
        );
        if peak_bytes > limits.max_intermediate_bytes || peak_bytes > limits.max_total_bytes {
            return Err(CoverageError::ActiveWorkLimit);
        }
        heap.try_reserve_exact(target_capacity.saturating_sub(heap.len()))
            .map_err(|_| CoverageError::AllocationFailure)?;
        let actual_peak_bytes = other_live_bytes.saturating_add(
            old_capacity
                .saturating_add(heap.capacity())
                .saturating_mul(element_size),
        );
        if actual_peak_bytes > limits.max_intermediate_bytes
            || actual_peak_bytes > limits.max_total_bytes
        {
            return Err(CoverageError::ActiveWorkLimit);
        }
    }
    if other_live_bytes.saturating_add(
        heap.capacity()
            .saturating_mul(core::mem::size_of::<Event>()),
    ) > limits.max_intermediate_bytes
        || other_live_bytes.saturating_add(
            heap.capacity()
                .saturating_mul(core::mem::size_of::<Event>()),
        ) > limits.max_total_bytes
    {
        return Err(CoverageError::ActiveWorkLimit);
    }
    heap.push(event);
    *event_count = next_count;
    Ok(())
}

macro_rules! reserve_scratch {
    ($scratch:ident, $field:ident, $required:expr, $shared_bytes:expr, $limits:expr) => {{
        let field_bytes = vec_capacity_bytes(&$scratch.$field);
        let other_bytes =
            ($shared_bytes).saturating_add($scratch.capacity_bytes().saturating_sub(field_bytes));
        ensure_vec_capacity(&mut $scratch.$field, $required, other_bytes, $limits)
    }};
}

macro_rules! push_scratch {
    ($scratch:ident, $field:ident, $value:expr, $shared_bytes:expr, $limits:expr) => {{
        let required = $scratch.$field.len().saturating_add(1);
        // Scratch capacity was admitted when it grew. With fixed limits for
        // this sweep, reusing an already-admitted slot cannot increase live
        // bytes; every other growable owner independently includes the full
        // scratch capacity in its admission calculation.
        if required > $scratch.$field.capacity() {
            reserve_scratch!($scratch, $field, required, $shared_bytes, $limits)?;
        }
        $scratch.$field.push($value);
    }};
}

impl Edge {
    fn x_at(self, y: f64) -> f64 {
        self.a.x + (y - self.a.y) * (self.b.x - self.a.x) / (self.b.y - self.a.y)
    }

    fn slope(self) -> f64 {
        (self.b.x - self.a.x) / (self.b.y - self.a.y)
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct Event {
    y: f64,
    x: f64,
    kind: EventKind,
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum EventKind {
    Start(usize),
    End(usize),
    Cross(usize, usize),
    Clip,
}

impl Eq for Event {}

// BinaryHeap is a max-heap; reverse the deterministic y/x/kind order.
impl Ord for Event {
    fn cmp(&self, other: &Self) -> Ordering {
        other
            .y
            .total_cmp(&self.y)
            .then_with(|| other.x.total_cmp(&self.x))
            .then_with(|| event_key(other.kind).cmp(&event_key(self.kind)))
    }
}

impl PartialOrd for Event {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

fn event_key(kind: EventKind) -> (u8, usize, usize) {
    match kind {
        EventKind::Start(id) => (0, id, 0),
        EventKind::End(id) => (1, id, 0),
        EventKind::Cross(a, b) => (2, a.min(b), a.max(b)),
        EventKind::Clip => (3, 0, 0),
    }
}

#[derive(Clone, Debug)]
struct Node {
    edge: usize,
    priority: u64,
    left: Option<usize>,
    right: Option<usize>,
    parent: Option<usize>,
    size: usize,
}

/// Implicit treap: the in-order sequence is the active x-order. It avoids
/// O(E) vector insertion/deletion at each vertex or crossing event.
struct OrderTree {
    nodes: Vec<Node>,
    free: Vec<usize>,
    by_edge: Vec<Option<usize>>,
    root: Option<usize>,
}

impl OrderTree {
    fn try_new(edge_count: usize) -> Result<Self, CoverageError> {
        let mut nodes = Vec::new();
        nodes
            .try_reserve_exact(edge_count)
            .map_err(|_| CoverageError::AllocationFailure)?;
        let mut free = Vec::new();
        free.try_reserve_exact(edge_count)
            .map_err(|_| CoverageError::AllocationFailure)?;
        let mut by_edge = Vec::new();
        by_edge
            .try_reserve_exact(edge_count)
            .map_err(|_| CoverageError::AllocationFailure)?;
        by_edge.resize(edge_count, None);
        Ok(Self {
            nodes,
            free,
            by_edge,
            root: None,
        })
    }

    fn size(&self, node: Option<usize>) -> usize {
        node.map_or(0, |n| self.nodes[n].size)
    }

    fn fix(&mut self, node: usize) {
        self.nodes[node].size =
            1 + self.size(self.nodes[node].left) + self.size(self.nodes[node].right);
        if let Some(left) = self.nodes[node].left {
            self.nodes[left].parent = Some(node);
        }
        if let Some(right) = self.nodes[node].right {
            self.nodes[right].parent = Some(node);
        }
    }

    fn priority(edge: usize) -> u64 {
        let mut z = edge as u64 + 0x9E37_79B9_7F4A_7C15;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    fn merge(&mut self, a: Option<usize>, b: Option<usize>) -> Option<usize> {
        match (a, b) {
            (None, x) | (x, None) => {
                if let Some(n) = x {
                    self.nodes[n].parent = None;
                }
                x
            }
            (Some(a), Some(b)) => {
                if self.nodes[a].priority <= self.nodes[b].priority {
                    let ar = self.nodes[a].right.take();
                    let merged = self.merge(ar, Some(b));
                    self.nodes[a].right = merged;
                    self.fix(a);
                    self.nodes[a].parent = None;
                    Some(a)
                } else {
                    let bl = self.nodes[b].left.take();
                    let merged = self.merge(Some(a), bl);
                    self.nodes[b].left = merged;
                    self.fix(b);
                    self.nodes[b].parent = None;
                    Some(b)
                }
            }
        }
    }

    fn split(&mut self, root: Option<usize>, count: usize) -> (Option<usize>, Option<usize>) {
        let Some(root) = root else {
            return (None, None);
        };
        if self.size(self.nodes[root].left) >= count {
            let left = self.nodes[root].left.take();
            let (a, b) = self.split(left, count);
            self.nodes[root].left = b;
            self.fix(root);
            self.nodes[root].parent = None;
            if let Some(a) = a {
                self.nodes[a].parent = None;
            }
            (a, Some(root))
        } else {
            let right = self.nodes[root].right.take();
            let n = count - self.size(self.nodes[root].left) - 1;
            let (a, b) = self.split(right, n);
            self.nodes[root].right = a;
            self.fix(root);
            self.nodes[root].parent = None;
            if let Some(b) = b {
                self.nodes[b].parent = None;
            }
            (Some(root), b)
        }
    }

    fn rank_node(&self, node: usize) -> usize {
        let mut rank = self.size(self.nodes[node].left);
        let mut current = node;
        while let Some(parent) = self.nodes[current].parent {
            if self.nodes[parent].right == Some(current) {
                rank += 1 + self.size(self.nodes[parent].left);
            }
            current = parent;
        }
        rank
    }

    /// Return the active predecessor/successor directly from the treap's
    /// parent/child links. This is the same in-order neighborhood that
    /// `edge_at(rank +/- 1)` returns, without first computing a rank and then
    /// traversing from the root again for each neighbor.
    fn neighbors(&self, node: usize) -> (Option<usize>, Option<usize>) {
        let mut predecessor = self.nodes[node].left.map(|mut child| {
            while let Some(right) = self.nodes[child].right {
                child = right;
            }
            self.nodes[child].edge
        });
        let mut successor = self.nodes[node].right.map(|mut child| {
            while let Some(left) = self.nodes[child].left {
                child = left;
            }
            self.nodes[child].edge
        });

        let mut child = node;
        while predecessor.is_none() || successor.is_none() {
            let Some(parent) = self.nodes[child].parent else {
                break;
            };
            if predecessor.is_none() && self.nodes[parent].right == Some(child) {
                predecessor = Some(self.nodes[parent].edge);
            }
            if successor.is_none() && self.nodes[parent].left == Some(child) {
                successor = Some(self.nodes[parent].edge);
            }
            child = parent;
        }
        (predecessor, successor)
    }

    fn insert_at(&mut self, edge: usize, rank: usize) {
        let node = if let Some(node) = self.free.pop() {
            self.nodes[node] = Node {
                edge,
                priority: Self::priority(edge),
                left: None,
                right: None,
                parent: None,
                size: 1,
            };
            node
        } else {
            let node = self.nodes.len();
            self.nodes.push(Node {
                edge,
                priority: Self::priority(edge),
                left: None,
                right: None,
                parent: None,
                size: 1,
            });
            node
        };
        let root = self.root.take();
        let (a, b) = self.split(root, rank);
        let joined = self.merge(a, Some(node));
        self.root = self.merge(joined, b);
        self.by_edge[edge] = Some(node);
    }

    #[cfg(test)]
    fn remove(&mut self, edge: usize) -> Option<usize> {
        let node = self.by_edge[edge].take()?;
        let rank = self.rank_node(node);
        self.unlink_node(node);
        Some(rank)
    }

    /// Remove an edge when the caller does not need its in-order rank.
    /// Keeping this path avoids a redundant parent-chain rank walk in the
    /// analytic sweep, which already has the node from `by_edge`.
    fn remove_unranked(&mut self, edge: usize) -> bool {
        let Some(node) = self.by_edge[edge].take() else {
            return false;
        };
        self.unlink_node(node);
        true
    }

    fn unlink_node(&mut self, node: usize) {
        // The edge map already identifies the node. Unlink it in place,
        // preserving the unique ordered treap of its two children, instead
        // of searching/splitting the complete tree twice by rank.
        let parent = self.nodes[node].parent;
        let left = self.nodes[node].left.take();
        let right = self.nodes[node].right.take();
        let merged = self.merge(left, right);
        if let Some(parent) = parent {
            if self.nodes[parent].left == Some(node) {
                self.nodes[parent].left = merged;
            } else {
                debug_assert_eq!(self.nodes[parent].right, Some(node));
                self.nodes[parent].right = merged;
            }
        } else {
            self.root = merged;
        }
        if let Some(merged) = merged {
            self.nodes[merged].parent = parent;
        }
        let mut ancestor = parent;
        while let Some(current) = ancestor {
            self.fix(current);
            ancestor = self.nodes[current].parent;
        }
        self.nodes[node].left = None;
        self.nodes[node].right = None;
        self.nodes[node].parent = None;
        self.nodes[node].size = 1;
        self.free.push(node);
    }

    fn edge_at(&self, mut rank: usize) -> Option<usize> {
        let mut current = self.root?;
        loop {
            let left_size = self.size(self.nodes[current].left);
            match rank.cmp(&left_size) {
                Ordering::Less => current = self.nodes[current].left?,
                Ordering::Equal => return Some(self.nodes[current].edge),
                Ordering::Greater => {
                    rank -= left_size + 1;
                    current = self.nodes[current].right?;
                }
            }
        }
    }

    fn len(&self) -> usize {
        self.size(self.root)
    }

    fn capacity_bytes(&self) -> usize {
        self.nodes
            .capacity()
            .saturating_mul(core::mem::size_of::<Node>())
            .saturating_add(
                self.free
                    .capacity()
                    .saturating_mul(core::mem::size_of::<usize>()),
            )
            .saturating_add(
                self.by_edge
                    .capacity()
                    .saturating_mul(core::mem::size_of::<Option<usize>>()),
            )
    }

    fn copy_in_order(&self, out: &mut Vec<usize>, stack: &mut Vec<usize>) {
        stack.clear();
        let mut current = self.root;
        loop {
            while let Some(n) = current {
                stack.push(n);
                current = self.nodes[n].left;
            }
            let Some(n) = stack.pop() else {
                break;
            };
            out.push(self.nodes[n].edge);
            current = self.nodes[n].right;
        }
    }

    #[cfg(test)]
    fn in_order(&self) -> Vec<usize> {
        let mut out = Vec::with_capacity(self.len());
        let mut stack = Vec::new();
        self.copy_in_order(&mut out, &mut stack);
        out
    }
}

/// Rasterize closed linear contours with analytic area coverage.
///
/// The implementation uses a y sweep. Only adjacent active edges schedule
/// future intersections. Full active-edge traversals are charged to an
/// explicit budget before any span list is returned. The returned list is
/// private until success, so callers can preserve their previous bitmap on a
/// typed fallback.
#[cfg(test)]
pub fn rasterize(
    contours: &[Vec<Point>],
    fill_rule: FillRule,
    clip: ClipRect,
    limits: Limits,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    rasterize_with_base(contours, fill_rule, clip, limits, 0)
}

/// Rasterizes a tiny-skia path after transforming and adaptively flattening
/// its curves. `tile_origin` is subtracted after the supplied transform so the
/// same path can be rendered into bounded destination tiles without cloning
/// or rewriting its source path.
pub(crate) fn rasterize_path(
    path: &Path,
    fill_rule: FillRule,
    clip: ClipRect,
    transform: Transform,
    tile_origin: [f32; 2],
    max_total_bytes: usize,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    rasterize_path_mode(
        path,
        fill_rule,
        clip,
        transform,
        tile_origin,
        max_total_bytes,
        true,
    )
}

fn rasterize_path_mode(
    path: &Path,
    fill_rule: FillRule,
    clip: ClipRect,
    transform: Transform,
    tile_origin: [f32; 2],
    max_total_bytes: usize,
    cluster_x: bool,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    let mut limits = Limits::default();
    limits.max_total_bytes = max_total_bytes;
    limits.max_intermediate_bytes = limits.max_intermediate_bytes.min(max_total_bytes);
    limits.max_output_bytes = limits.max_output_bytes.min(max_total_bytes);

    validate_clip(clip)?;
    validate_raster_limits(limits)?;
    if !tile_origin[0].is_finite() || !tile_origin[1].is_finite() {
        return Err(CoverageError::InvalidCoordinate);
    }

    let mut segment_count = 0usize;
    let mut contour_count = 0usize;
    for segment in path.segments() {
        segment_count = segment_count
            .checked_add(1)
            .ok_or(CoverageError::EdgeLimit)?;
        if segment_count > limits.max_edges {
            return Err(CoverageError::EdgeLimit);
        }
        if matches!(segment, PathSegment::MoveTo(_)) {
            contour_count = contour_count
                .checked_add(1)
                .ok_or(CoverageError::EdgeLimit)?;
        }
    }
    if contour_count == 0 {
        return Ok(Vec::new());
    }

    let mut contours = new_contours(contour_count, limits)?;
    let outer_bytes = vec_capacity_bytes(&contours);

    let mut current: Option<Vec<Point>> = None;
    let mut finalized_point_bytes = 0usize;
    let mut all_point_count = 0usize;
    for segment in path.segments() {
        match segment {
            PathSegment::MoveTo(p) => {
                if let Some(contour) = current.take() {
                    finalized_point_bytes = finalized_point_bytes
                        .checked_add(vec_capacity_bytes(&contour))
                        .ok_or(CoverageError::ActiveWorkLimit)?;
                    contours.push(contour);
                }

                let mut contour = Vec::new();
                let base_bytes = outer_bytes
                    .checked_add(finalized_point_bytes)
                    .ok_or(CoverageError::ActiveWorkLimit)?;
                let point = map_path_point(p, transform, tile_origin, limits)?;
                if all_point_count >= limits.max_edges {
                    return Err(CoverageError::EdgeLimit);
                }
                ensure_vec_capacity(&mut contour, 1, base_bytes, limits)?;
                contour.push(point);
                all_point_count = all_point_count
                    .checked_add(1)
                    .ok_or(CoverageError::EdgeLimit)?;
                current = Some(contour);
            }
            PathSegment::LineTo(p) => {
                let contour = current.as_mut().ok_or(CoverageError::InvalidCoordinate)?;
                let point = map_path_point(p, transform, tile_origin, limits)?;
                let next_count = all_point_count
                    .checked_add(1)
                    .ok_or(CoverageError::EdgeLimit)?;
                if next_count > limits.max_edges {
                    return Err(CoverageError::EdgeLimit);
                }
                let base_bytes = outer_bytes
                    .checked_add(finalized_point_bytes)
                    .ok_or(CoverageError::ActiveWorkLimit)?;
                ensure_vec_capacity(contour, contour.len() + 1, base_bytes, limits)?;
                contour.push(point);
                all_point_count = next_count;
            }
            PathSegment::QuadTo(control, end) => {
                let contour = current.as_mut().ok_or(CoverageError::InvalidCoordinate)?;
                let start = *contour.last().ok_or(CoverageError::InvalidCoordinate)?;
                let control = map_path_point(control, transform, tile_origin, limits)?;
                let end = map_path_point(end, transform, tile_origin, limits)?;
                let base_bytes = outer_bytes
                    .checked_add(finalized_point_bytes)
                    .ok_or(CoverageError::ActiveWorkLimit)?;
                let curve_limits = limits_with_remaining(
                    limits,
                    base_bytes,
                    all_point_count.saturating_sub(contour.len()),
                )?;
                let before = contour.len();
                flatten_quadratic(start, control, end, curve_limits, contour)?;
                // The flatten helper enforces a per-contour edge bound. Account
                // its newly appended points against the path-wide bound here.
                let added = contour.len().saturating_sub(before);
                if added > limits.max_edges.saturating_sub(all_point_count) {
                    return Err(CoverageError::EdgeLimit);
                }
                all_point_count = all_point_count.saturating_add(added);
            }
            PathSegment::CubicTo(control1, control2, end) => {
                let contour = current.as_mut().ok_or(CoverageError::InvalidCoordinate)?;
                let start = *contour.last().ok_or(CoverageError::InvalidCoordinate)?;
                let control1 = map_path_point(control1, transform, tile_origin, limits)?;
                let control2 = map_path_point(control2, transform, tile_origin, limits)?;
                let end = map_path_point(end, transform, tile_origin, limits)?;
                let base_bytes = outer_bytes
                    .checked_add(finalized_point_bytes)
                    .ok_or(CoverageError::ActiveWorkLimit)?;
                let curve_limits = limits_with_remaining(
                    limits,
                    base_bytes,
                    all_point_count.saturating_sub(contour.len()),
                )?;
                let before = contour.len();
                flatten_cubic(start, control1, control2, end, curve_limits, contour)?;
                let added = contour.len().saturating_sub(before);
                if added > limits.max_edges.saturating_sub(all_point_count) {
                    return Err(CoverageError::EdgeLimit);
                }
                all_point_count = all_point_count.saturating_add(added);
            }
            PathSegment::Close => {
                if let Some(contour) = current.take() {
                    finalized_point_bytes = finalized_point_bytes
                        .checked_add(vec_capacity_bytes(&contour))
                        .ok_or(CoverageError::ActiveWorkLimit)?;
                    contours.push(contour);
                }
            }
        }
    }
    if let Some(contour) = current {
        finalized_point_bytes = finalized_point_bytes
            .checked_add(vec_capacity_bytes(&contour))
            .ok_or(CoverageError::ActiveWorkLimit)?;
        contours.push(contour);
    }
    let base_live_bytes = outer_bytes
        .checked_add(finalized_point_bytes)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if base_live_bytes > limits.max_intermediate_bytes || base_live_bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }

    if cluster_x {
        rasterize_x_clusters(contours, fill_rule, clip, limits, base_live_bytes)
    } else {
        rasterize_with_base(
            contours.as_slice(),
            fill_rule,
            clip,
            limits,
            base_live_bytes,
        )
    }
}

/// Admit even the outer contour table before asking the allocator for storage.
/// It remains live together with every flattened contour throughout the sweep.
fn new_contours(count: usize, limits: Limits) -> Result<Vec<Vec<Point>>, CoverageError> {
    let required_bytes = count
        .checked_mul(core::mem::size_of::<Vec<Point>>())
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if required_bytes > limits.max_intermediate_bytes || required_bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    let mut contours = Vec::new();
    contours
        .try_reserve_exact(count)
        .map_err(|_| CoverageError::AllocationFailure)?;
    let actual_bytes = vec_capacity_bytes(&contours);
    if actual_bytes > limits.max_intermediate_bytes || actual_bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    Ok(contours)
}

/// Separate whole closed contours only when their conservative physical
/// pixel-column support is disjoint. Each component is swept independently,
/// with one shared work/output ledger across components.
fn rasterize_x_clusters(
    mut contours: Vec<Vec<Point>>,
    fill_rule: FillRule,
    clip: ClipRect,
    limits: Limits,
    base_live_bytes: usize,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    validate_clip(clip)?;
    validate_raster_limits(limits)?;
    if contours.is_empty() {
        return Ok(Vec::new());
    }

    let coordinate_min = i32::MIN as f64;
    let coordinate_max = i32::MAX as f64;
    let clip = ClipRect {
        left: clip.left.max(coordinate_min),
        top: clip.top.max(coordinate_min),
        right: clip.right.min(coordinate_max),
        bottom: clip.bottom.min(coordinate_max),
    };
    if clip.right <= clip.left || clip.bottom <= clip.top {
        return Ok(Vec::new());
    }

    let descriptor_size = core::mem::size_of::<ContourBounds>();
    let Some(descriptor_bytes) = contours.len().checked_mul(descriptor_size) else {
        return rasterize_with_base(&contours, fill_rule, clip, limits, base_live_bytes);
    };
    if base_live_bytes
        .checked_add(descriptor_bytes)
        .map_or(true, |bytes| {
            bytes > limits.max_intermediate_bytes || bytes > limits.max_total_bytes
        })
    {
        return rasterize_with_base(&contours, fill_rule, clip, limits, base_live_bytes);
    }

    // Reject an excessive number of empty/tiny contours before the O(n)
    // counting pass. The input allocation was admitted by the caller, but
    // scanning a count that already exceeds the request work ceiling is not.
    let contour_work = match x_cluster_grouping_work(contours.len(), 0) {
        Ok(work) => work,
        Err(_) => return rasterize_with_base(&contours, fill_rule, clip, limits, base_live_bytes),
    };
    if contour_work > limits.max_active_edge_visits {
        return rasterize_with_base(&contours, fill_rule, clip, limits, base_live_bytes);
    }
    let Some(point_count) = contours
        .iter()
        .try_fold(0usize, |count, contour| count.checked_add(contour.len()))
    else {
        return Err(CoverageError::ActiveWorkLimit);
    };
    let mut grouping_work_spent =
        u64::try_from(contours.len()).map_err(|_| CoverageError::ActiveWorkLimit)?;
    let grouping_work = match x_cluster_grouping_work(contours.len(), point_count) {
        Ok(work) => work,
        Err(_) => {
            return rasterize_monolithic_after_work(
                &contours,
                fill_rule,
                clip,
                limits,
                base_live_bytes,
                grouping_work_spent,
            );
        }
    };
    if grouping_work > limits.max_active_edge_visits {
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            grouping_work_spent,
        );
    }

    let mut descriptors = Vec::new();
    if descriptors.try_reserve_exact(contours.len()).is_err() {
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            grouping_work_spent,
        );
    }
    if base_live_bytes.saturating_add(vec_capacity_bytes(&descriptors))
        > limits.max_intermediate_bytes
        || base_live_bytes.saturating_add(vec_capacity_bytes(&descriptors)) > limits.max_total_bytes
    {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            grouping_work_spent,
        );
    }

    let mut point_storage = 0usize;
    for (original_index, contour) in contours.iter().enumerate() {
        let mut min_x = f64::INFINITY;
        let mut max_x = f64::NEG_INFINITY;
        point_storage = point_storage
            .checked_add(vec_capacity_bytes(contour))
            .ok_or(CoverageError::ActiveWorkLimit)?;
        for point in contour {
            if !point.x.is_finite()
                || !point.y.is_finite()
                || point.x.abs() > limits.max_coordinate_abs
                || point.y.abs() > limits.max_coordinate_abs
            {
                return Err(CoverageError::InvalidCoordinate);
            }
            min_x = min_x.min(point.x);
            max_x = max_x.max(point.x);
        }
        if contour.is_empty() {
            continue;
        }
        let clipped_min = min_x.max(clip.left);
        let clipped_max = max_x.min(clip.right);
        if clipped_max <= clipped_min {
            // This entire closed contour is outside the clipped pixel support;
            // it contributes zero winding inside the clip.
            continue;
        }
        let left_pixel = clipped_min.floor() as i64;
        let right_pixel = clipped_max.ceil() as i64;
        if right_pixel <= left_pixel {
            continue;
        }
        descriptors.push(ContourBounds {
            original_index,
            left_pixel,
            right_pixel,
            component: 0,
        });
    }
    // This pass validates each contour and point and records retained point
    // capacities. Charge both its contour-level and point-level work now so
    // any later memory fallback cannot refund that accounting pass.
    let bounds_work = u64::try_from(contours.len())
        .ok()
        .and_then(|count| count.checked_mul(2))
        .and_then(|count| {
            u64::try_from(point_count)
                .ok()
                .and_then(|points| count.checked_add(points))
        })
        .ok_or(CoverageError::ActiveWorkLimit)?;
    grouping_work_spent = grouping_work_spent
        .checked_add(bounds_work)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if grouping_work_spent > limits.max_active_edge_visits {
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            grouping_work_spent,
        );
    }
    if descriptors.is_empty() {
        return Ok(Vec::new());
    }

    let sort_work = x_cluster_sort_work(contours.len())?;
    let first_sort_work = grouping_work_spent
        .checked_add(sort_work)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if first_sort_work > limits.max_active_edge_visits {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            grouping_work_spent,
        );
    }
    heap_sort_by(&mut descriptors, |a, b| {
        a.left_pixel
            .cmp(&b.left_pixel)
            .then_with(|| a.right_pixel.cmp(&b.right_pixel))
            .then_with(|| a.original_index.cmp(&b.original_index))
    });
    let mut component = 0usize;
    let mut component_right = descriptors[0].right_pixel;
    descriptors[0].component = component;
    let component_work =
        u64::try_from(contours.len()).map_err(|_| CoverageError::ActiveWorkLimit)?;
    let formed_work = first_sort_work
        .checked_add(component_work)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if formed_work > limits.max_active_edge_visits {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            first_sort_work,
        );
    }
    for descriptor in descriptors.iter_mut().skip(1) {
        if descriptor.left_pixel > component_right {
            component = component
                .checked_add(1)
                .ok_or(CoverageError::ActiveWorkLimit)?;
            component_right = descriptor.right_pixel;
        } else {
            component_right = component_right.max(descriptor.right_pixel);
        }
        descriptor.component = component;
    }
    grouping_work_spent = formed_work;
    let component_count = component
        .checked_add(1)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if component_count == 1 {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            grouping_work_spent,
        );
    }
    let second_sort_work = grouping_work_spent
        .checked_add(sort_work)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if second_sort_work > limits.max_active_edge_visits {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            grouping_work_spent,
        );
    }
    heap_sort_by(&mut descriptors, |a, b| {
        a.component
            .cmp(&b.component)
            .then_with(|| a.original_index.cmp(&b.original_index))
    });

    let Some(range_bytes) = component_count.checked_mul(core::mem::size_of::<(usize, usize)>())
    else {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    };
    let Some(ordered_bytes) = descriptors
        .len()
        .checked_mul(core::mem::size_of::<Vec<Point>>())
    else {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    };
    let preparation_peak = base_live_bytes
        .checked_add(vec_capacity_bytes(&descriptors))
        .and_then(|bytes| bytes.checked_add(range_bytes))
        .and_then(|bytes| bytes.checked_add(ordered_bytes));
    let Some(preparation_peak) = preparation_peak else {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    };
    if preparation_peak > limits.max_intermediate_bytes || preparation_peak > limits.max_total_bytes
    {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    }

    let mut ranges = Vec::<(usize, usize)>::new();
    if ranges.try_reserve_exact(component_count).is_err() {
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    }
    let mut ordered = Vec::<Vec<Point>>::new();
    if ordered.try_reserve_exact(descriptors.len()).is_err() {
        drop(ordered);
        drop(ranges);
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    }
    let actual_preparation_peak = base_live_bytes
        .saturating_add(vec_capacity_bytes(&descriptors))
        .saturating_add(vec_capacity_bytes(&ranges))
        .saturating_add(vec_capacity_bytes(&ordered));
    if actual_preparation_peak > limits.max_intermediate_bytes
        || actual_preparation_peak > limits.max_total_bytes
    {
        drop(ordered);
        drop(ranges);
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    }

    let reorder_work = u64::try_from(contours.len()).map_err(|_| CoverageError::ActiveWorkLimit)?;
    let full_grouping_work = second_sort_work
        .checked_add(reorder_work)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if full_grouping_work > limits.max_active_edge_visits {
        drop(ordered);
        drop(ranges);
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    }

    let persistent_bytes = vec_capacity_bytes(&ordered)
        .checked_add(point_storage)
        .and_then(|bytes| bytes.checked_add(vec_capacity_bytes(&ranges)))
        .ok_or(CoverageError::ActiveWorkLimit)?;
    if persistent_bytes > limits.max_intermediate_bytes || persistent_bytes > limits.max_total_bytes
    {
        drop(ordered);
        drop(ranges);
        drop(descriptors);
        return rasterize_monolithic_after_work(
            &contours,
            fill_rule,
            clip,
            limits,
            base_live_bytes,
            second_sort_work,
        );
    }

    let mut range_start = 0usize;
    for index in 0..descriptors.len() {
        if index != 0 && descriptors[index].component != descriptors[index - 1].component {
            ranges.push((range_start, index));
            range_start = index;
        }
        let original_index = descriptors[index].original_index;
        let contour = core::mem::replace(&mut contours[original_index], Vec::new());
        ordered.push(contour);
    }
    ranges.push((range_start, descriptors.len()));
    drop(contours);
    drop(descriptors);

    let mut spent = RasterUsage {
        active_work: full_grouping_work,
        ..RasterUsage::default()
    };
    let mut combined = Vec::new();
    for &(start, end) in &ranges {
        let prior_output_bytes = vec_capacity_bytes(&combined);
        let group_limits = limits_after_usage(limits, spent, prior_output_bytes)?;
        let mut group_usage = RasterUsage::default();
        let group_spans = rasterize_with_base_usage(
            &ordered[start..end],
            fill_rule,
            clip,
            group_limits,
            persistent_bytes,
            true,
            &mut group_usage,
        )?;
        spent.add(group_usage)?;
        if group_spans.is_empty() {
            continue;
        }

        let group_output_bytes = vec_capacity_bytes(&group_spans);
        let combined_len = combined
            .len()
            .checked_add(group_spans.len())
            .ok_or(CoverageError::SpanLimit)?;
        let merge_live = persistent_bytes
            .checked_add(group_output_bytes)
            .ok_or(CoverageError::ActiveWorkLimit)?;
        ensure_vec_capacity_with_output_limit(
            &mut combined,
            combined_len,
            merge_live,
            Some(limits.max_output_bytes),
            limits,
        )?;
        combined.extend(group_spans);
    }
    Ok(combined)
}

fn x_cluster_grouping_work(contour_count: usize, point_count: usize) -> Result<u64, CoverageError> {
    if contour_count == 0 {
        return Ok(0);
    }
    let count = u64::try_from(contour_count).map_err(|_| CoverageError::ActiveWorkLimit)?;
    let points = u64::try_from(point_count).map_err(|_| CoverageError::ActiveWorkLimit)?;
    let sort_work = x_cluster_sort_work(contour_count)?;
    // Two heapsorts plus five linear contour-level passes (counting, bounds
    // validation/capacity accounting, component formation, and reordering).
    // Point visits cover the bounds/validation pass.
    count
        .checked_mul(5)
        .and_then(|linear| linear.checked_add(points))
        .and_then(|work| {
            sort_work
                .checked_mul(2)
                .and_then(|sorts| work.checked_add(sorts))
        })
        .ok_or(CoverageError::ActiveWorkLimit)
}

fn x_cluster_sort_work(contour_count: usize) -> Result<u64, CoverageError> {
    bounded_sort_work(contour_count)
}

/// Conservative operation bound for the in-place heapsort below. Heapify
/// performs fewer than n downward steps in aggregate; extraction performs
/// fewer than n*ceil(log2(n)) steps. Each step uses at most two comparisons
/// and one swap, with fewer than n extra root swaps. The 8*n*levels+n charge
/// therefore bounds the implementation rather than relying on std sorting
/// constants or allocating auxiliary storage.
fn bounded_sort_work(len: usize) -> Result<u64, CoverageError> {
    if len <= 1 {
        return Ok(0);
    }
    let count = u64::try_from(len).map_err(|_| CoverageError::ActiveWorkLimit)?;
    let levels = usize::BITS as usize - (len - 1).leading_zeros() as usize;
    let levels = u64::try_from(levels).map_err(|_| CoverageError::ActiveWorkLimit)?;
    count
        .checked_mul(levels)
        .and_then(|work| work.checked_mul(8))
        .and_then(|work| work.checked_add(count))
        .ok_or(CoverageError::ActiveWorkLimit)
}

fn heap_sort_by<T>(values: &mut [T], mut compare: impl FnMut(&T, &T) -> Ordering) {
    fn sift_down<T>(
        values: &mut [T],
        mut root: usize,
        end: usize,
        compare: &mut impl FnMut(&T, &T) -> Ordering,
    ) {
        loop {
            let Some(left) = root.checked_mul(2).and_then(|index| index.checked_add(1)) else {
                break;
            };
            if left >= end {
                break;
            }
            let mut largest = left;
            let right = left + 1;
            if right < end && compare(&values[left], &values[right]) == Ordering::Less {
                largest = right;
            }
            if compare(&values[root], &values[largest]) != Ordering::Less {
                break;
            }
            values.swap(root, largest);
            root = largest;
        }
    }

    let len = values.len();
    if len < 2 {
        return;
    }
    for start in (0..len / 2).rev() {
        sift_down(values, start, len, &mut compare);
    }
    for end in (1..len).rev() {
        values.swap(0, end);
        sift_down(values, 0, end, &mut compare);
    }
}

fn rasterize_monolithic_after_work(
    contours: &[Vec<Point>],
    fill_rule: FillRule,
    clip: ClipRect,
    limits: Limits,
    base_live_bytes: usize,
    prior_work: u64,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    let remaining = limits_after_grouping_work(limits, prior_work)?;
    rasterize_with_base(contours, fill_rule, clip, remaining, base_live_bytes)
}

fn limits_after_grouping_work(
    mut limits: Limits,
    prior_work: u64,
) -> Result<Limits, CoverageError> {
    limits.max_active_edge_visits = limits
        .max_active_edge_visits
        .checked_sub(prior_work)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    Ok(limits)
}

fn limits_after_usage(
    original: Limits,
    spent: RasterUsage,
    prior_output_bytes: usize,
) -> Result<Limits, CoverageError> {
    let mut remaining = original;
    remaining.max_edges = remaining
        .max_edges
        .checked_sub(spent.edges)
        .ok_or(CoverageError::EdgeLimit)?;
    remaining.max_events = remaining
        .max_events
        .checked_sub(spent.processed_events)
        .ok_or(CoverageError::EventLimit)?;
    remaining.max_active_edge_visits = remaining
        .max_active_edge_visits
        .checked_sub(spent.active_work)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    remaining.max_partial_pixel_intersections = remaining
        .max_partial_pixel_intersections
        .checked_sub(spent.partial_intersections)
        .ok_or(CoverageError::PartialPixelLimit)?;
    remaining.max_spans = remaining
        .max_spans
        .checked_sub(spent.range_runs.max(spent.output_spans))
        .ok_or(CoverageError::SpanLimit)?;
    remaining.max_intermediate_bytes = remaining
        .max_intermediate_bytes
        .checked_sub(prior_output_bytes)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    remaining.max_output_bytes = remaining
        .max_output_bytes
        .checked_sub(prior_output_bytes)
        .ok_or(CoverageError::SpanLimit)?;
    remaining.max_total_bytes = remaining
        .max_total_bytes
        .checked_sub(prior_output_bytes)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    Ok(remaining)
}

#[cfg(test)]
mod tiny_path_tests {
    use super::*;
    use tiny_skia_path::PathBuilder;

    #[test]
    fn contour_table_is_admitted_before_allocation() {
        let exact_bytes = 3 * core::mem::size_of::<Vec<Point>>();
        let limits = Limits {
            max_intermediate_bytes: exact_bytes,
            max_total_bytes: exact_bytes,
            ..Limits::default()
        };
        let contours = new_contours(3, limits).unwrap();
        assert!(contours.is_empty());
        assert_eq!(vec_capacity_bytes(&contours), exact_bytes);
        for insufficient in [
            Limits {
                max_intermediate_bytes: exact_bytes - 1,
                ..limits
            },
            Limits {
                max_total_bytes: exact_bytes - 1,
                ..limits
            },
        ] {
            assert_eq!(
                new_contours(3, insufficient),
                Err(CoverageError::ActiveWorkLimit)
            );
        }
        // Overflow and a representable but unadmitted table must fail as a
        // budget rejection, not reach an allocation failure/allocator abort.
        assert_eq!(
            new_contours(usize::MAX, limits),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(
            new_contours(usize::MAX / core::mem::size_of::<Vec<Point>>(), limits),
            Err(CoverageError::ActiveWorkLimit)
        );
        let zero = Limits {
            max_intermediate_bytes: 0,
            max_total_bytes: 0,
            ..limits
        };
        assert_eq!(new_contours(0, zero).unwrap().capacity(), 0);
        assert_eq!(new_contours(1, zero), Err(CoverageError::ActiveWorkLimit));
    }

    fn path_with_quadratic() -> Path {
        let mut builder = PathBuilder::new();
        builder.move_to(1.0, 1.0);
        builder.quad_to(5.0, 9.0, 9.0, 1.0);
        builder.line_to(9.0, 10.0);
        builder.line_to(1.0, 10.0);
        builder.close();
        builder.finish().unwrap()
    }

    #[test]
    fn tiny_path_curves_flatten_and_translate_in_physical_space() {
        let path = path_with_quadratic();
        let spans = rasterize_path(
            &path,
            FillRule::NonZero,
            ClipRect {
                left: 0.0,
                top: 0.0,
                right: 12.0,
                bottom: 12.0,
            },
            Transform::from_scale(2.0, 2.0),
            [3.0, 4.0],
            4 * 1024 * 1024,
        )
        .unwrap();
        assert!(!spans.is_empty());
        assert!(spans.iter().all(|span| span.x >= 0 && span.y >= 0));

        let mut budget = 128;
        assert_eq!(
            rasterize_path(
                &path,
                FillRule::NonZero,
                ClipRect {
                    left: 0.0,
                    top: 0.0,
                    right: 12.0,
                    bottom: 12.0
                },
                Transform::identity(),
                [0.0, 0.0],
                budget,
            ),
            Err(CoverageError::ActiveWorkLimit),
        );
        budget = 4 * 1024 * 1024;
        let moved = rasterize_path(
            &path,
            FillRule::NonZero,
            ClipRect {
                left: 0.0,
                top: 0.0,
                right: 12.0,
                bottom: 12.0,
            },
            Transform::from_translate(2.0, 3.0),
            [0.0, 0.0],
            budget,
        )
        .unwrap();
        assert!(!moved.is_empty());
    }

    #[test]
    fn path_segment_and_curve_growth_is_bounded_before_rasterization() {
        let mut builder = PathBuilder::new();
        builder.move_to(0.0, 0.0);
        builder.cubic_to(0.0, 100.0, 100.0, -100.0, 100.0, 0.0);
        builder.line_to(100.0, 100.0);
        builder.line_to(0.0, 100.0);
        builder.close();
        let path = builder.finish().unwrap();
        let result = rasterize_path(
            &path,
            FillRule::EvenOdd,
            ClipRect {
                left: 0.0,
                top: 0.0,
                right: 128.0,
                bottom: 128.0,
            },
            Transform::identity(),
            [0.0, 0.0],
            4096,
        );
        assert!(matches!(
            result,
            Err(CoverageError::ActiveWorkLimit | CoverageError::EdgeLimit)
        ));

        let outside = rasterize_path(
            &path,
            FillRule::EvenOdd,
            ClipRect {
                left: 0.0,
                top: 0.0,
                right: 0.0,
                bottom: 128.0,
            },
            Transform::identity(),
            [0.0, 0.0],
            4096,
        );
        assert!(matches!(
            outside,
            Err(CoverageError::ActiveWorkLimit | CoverageError::EdgeLimit)
        ));
    }

    #[test]
    fn rejects_nonfinite_transform_and_massive_mapped_coordinates() {
        let mut builder = PathBuilder::new();
        builder.move_to(0.0, 0.0);
        builder.line_to(1.0, 0.0);
        builder.line_to(0.0, 1.0);
        builder.close();
        let path = builder.finish().unwrap();
        let result = rasterize_path(
            &path,
            FillRule::NonZero,
            ClipRect {
                left: 0.0,
                top: 0.0,
                right: 10.0,
                bottom: 10.0,
            },
            Transform::from_translate(f32::INFINITY, 0.0),
            [0.0, 0.0],
            1024 * 1024,
        );
        assert_eq!(result, Err(CoverageError::InvalidCoordinate));
    }
}

fn map_path_point(
    mut point: tiny_skia_path::Point,
    transform: Transform,
    tile_origin: [f32; 2],
    limits: Limits,
) -> Result<Point, CoverageError> {
    transform.map_point(&mut point);
    let mapped = Point {
        x: f64::from(point.x) - f64::from(tile_origin[0]),
        y: f64::from(point.y) - f64::from(tile_origin[1]),
    };
    if !mapped.x.is_finite()
        || !mapped.y.is_finite()
        || mapped.x.abs() > limits.max_coordinate_abs
        || mapped.y.abs() > limits.max_coordinate_abs
    {
        return Err(CoverageError::InvalidCoordinate);
    }
    Ok(mapped)
}

fn limits_with_remaining(
    limits: Limits,
    persistent_bytes: usize,
    prior_point_count: usize,
) -> Result<Limits, CoverageError> {
    let remaining = limits
        .max_total_bytes
        .checked_sub(persistent_bytes)
        .ok_or(CoverageError::ActiveWorkLimit)?;
    let mut result = limits;
    result.max_intermediate_bytes = result.max_intermediate_bytes.min(remaining);
    result.max_total_bytes = remaining;
    result.max_edges = result
        .max_edges
        .checked_sub(prior_point_count)
        .ok_or(CoverageError::EdgeLimit)?;
    Ok(result)
}

/// Rasterizes contours while accounting for caller-owned, still-live geometry
/// storage in both the intermediate and combined peak budgets.
fn rasterize_with_base(
    contours: &[Vec<Point>],
    fill_rule: FillRule,
    clip: ClipRect,
    limits: Limits,
    base_live_bytes: usize,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    rasterize_with_base_mode(contours, fill_rule, clip, limits, base_live_bytes, true)
}

fn dense_bounds(edges: &[Edge], clip: ClipRect) -> Option<(i32, usize, usize)> {
    let min_x = edges
        .iter()
        .flat_map(|edge| [edge.a.x, edge.b.x])
        .fold(f64::INFINITY, f64::min)
        .max(clip.left);
    let max_x = edges
        .iter()
        .flat_map(|edge| [edge.a.x, edge.b.x])
        .fold(f64::NEG_INFINITY, f64::max)
        .min(clip.right);
    let min_y = edges
        .iter()
        .flat_map(|edge| [edge.a.y, edge.b.y])
        .fold(f64::INFINITY, f64::min)
        .max(clip.top);
    let max_y = edges
        .iter()
        .flat_map(|edge| [edge.a.y, edge.b.y])
        .fold(f64::NEG_INFINITY, f64::max)
        .min(clip.bottom);
    if ![min_x, max_x, min_y, max_y]
        .iter()
        .all(|value| value.is_finite())
        || max_x <= min_x
        || max_y <= min_y
    {
        return None;
    }

    let x_origin = min_x.floor() as i64;
    let x_end = max_x.ceil() as i64;
    let y_start = min_y.floor() as i64;
    let y_end = max_y.ceil() as i64;
    let width = usize::try_from(x_end.checked_sub(x_origin)?).ok()?;
    let rows = usize::try_from(y_end.checked_sub(y_start)?).ok()?;
    if width == 0 || rows == 0 {
        return None;
    }

    Some((i32::try_from(x_origin).ok()?, width, rows))
}

fn rasterize_with_base_mode(
    contours: &[Vec<Point>],
    fill_rule: FillRule,
    clip: ClipRect,
    limits: Limits,
    base_live_bytes: usize,
    prefer_dense: bool,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    let mut usage = RasterUsage::default();
    rasterize_with_base_usage(
        contours,
        fill_rule,
        clip,
        limits,
        base_live_bytes,
        prefer_dense,
        &mut usage,
    )
}

fn rasterize_with_base_usage(
    contours: &[Vec<Point>],
    fill_rule: FillRule,
    clip: ClipRect,
    limits: Limits,
    base_live_bytes: usize,
    prefer_dense: bool,
    cumulative_usage: &mut RasterUsage,
) -> Result<Vec<CoverageSpan>, CoverageError> {
    validate_clip(clip)?;
    validate_raster_limits(limits)?;
    let coordinate_min = i32::MIN as f64;
    let coordinate_max = i32::MAX as f64;
    let clip = ClipRect {
        left: clip.left.max(coordinate_min),
        top: clip.top.max(coordinate_min),
        right: clip.right.min(coordinate_max),
        bottom: clip.bottom.min(coordinate_max),
    };
    if clip.right <= clip.left || clip.bottom <= clip.top {
        return Ok(Vec::new());
    }

    let edges = build_edges(contours, clip, limits, base_live_bytes)?;
    if edges.len() > limits.max_edges {
        return Err(CoverageError::EdgeLimit);
    }
    if edges.is_empty() {
        return Ok(Vec::new());
    }
    // Pre-admit edge and order-tree arrays. Reusable scratch buffers and the
    // event heap are charged separately by their actual capacities.
    let edge_bytes = edges
        .capacity()
        .saturating_mul(core::mem::size_of::<Edge>());
    let tree_bytes_upper = edges.len().saturating_mul(
        core::mem::size_of::<Node>()
            + core::mem::size_of::<usize>()
            + core::mem::size_of::<Option<usize>>(),
    );
    let fixed_upper = base_live_bytes
        .saturating_add(edge_bytes)
        .saturating_add(tree_bytes_upper);
    if fixed_upper > limits.max_intermediate_bytes || fixed_upper > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    let mut tree = OrderTree::try_new(edges.len())?;
    let fixed_bytes = base_live_bytes
        .saturating_add(edge_bytes)
        .saturating_add(tree.capacity_bytes());
    let mut scratch = SweepScratch::default();
    reserve_scratch!(scratch, band_edges, edges.len(), fixed_bytes, limits)?;
    reserve_scratch!(scratch, band_stack, edges.len(), fixed_bytes, limits)?;

    let mut heap = BinaryHeap::new();
    let mut scheduled = ScheduledPairs::default();
    let mut event_count = 0usize;

    // Pixel rows are rasterized inside each geometry slab. Keeping row
    // boundaries out of the event queue makes tall uncomplicated paths
    // O(active edges + covered rows), instead of revisiting every active edge
    // for every output row.
    let initial_event_capacity = edges.len().saturating_mul(2).saturating_add(2);
    let bytes_without_heap = estimate_intermediate_bytes(
        fixed_bytes,
        0,
        scheduled.capacity_bytes(),
        0,
        scratch.capacity_bytes(),
    );
    let initial_estimate = bytes_without_heap
        .saturating_add(initial_event_capacity.saturating_mul(core::mem::size_of::<Event>()));
    if initial_event_capacity > limits.max_events
        || initial_estimate > limits.max_intermediate_bytes
        || initial_estimate > limits.max_total_bytes
    {
        return Err(CoverageError::EventLimit);
    }
    heap.try_reserve_exact(initial_event_capacity)
        .map_err(|_| CoverageError::AllocationFailure)?;
    if bytes_without_heap.saturating_add(
        heap.capacity()
            .saturating_mul(core::mem::size_of::<Event>()),
    ) > limits.max_intermediate_bytes
        || bytes_without_heap.saturating_add(
            heap.capacity()
                .saturating_mul(core::mem::size_of::<Event>()),
        ) > limits.max_total_bytes
    {
        return Err(CoverageError::ActiveWorkLimit);
    }
    push_event(
        &mut heap,
        Event {
            y: clip.top,
            x: f64::NEG_INFINITY,
            kind: EventKind::Clip,
        },
        &mut event_count,
        limits,
        bytes_without_heap,
    )?;
    push_event(
        &mut heap,
        Event {
            y: clip.bottom,
            x: f64::NEG_INFINITY,
            kind: EventKind::Clip,
        },
        &mut event_count,
        limits,
        bytes_without_heap,
    )?;
    for edge in &edges {
        push_event(
            &mut heap,
            Event {
                y: edge.a.y,
                x: edge.a.x,
                kind: EventKind::Start(edge.id),
            },
            &mut event_count,
            limits,
            bytes_without_heap,
        )?;
        push_event(
            &mut heap,
            Event {
                y: edge.b.y,
                x: edge.b.x,
                kind: EventKind::End(edge.id),
            },
            &mut event_count,
            limits,
            bytes_without_heap,
        )?;
    }

    let mut coverage = CoverageAccumulator::default();
    if prefer_dense {
        if let Some((x_origin, width, rows)) = dense_bounds(&edges, clip) {
            let mut dense_base_bytes = estimate_intermediate_bytes(
                fixed_bytes,
                heap.capacity(),
                scheduled.capacity_bytes(),
                0,
                scratch.capacity_bytes(),
            );
            let can_try_dense = width != 0
                && width.saturating_add(1) <= limits.max_coverage_events
                && width
                    .checked_mul(rows)
                    .is_some_and(|work| (work as u64) <= limits.max_active_edge_visits / 2);
            if can_try_dense {
                let old_pair_bytes = vec_capacity_bytes(&scratch.fill_pairs);
                let pair_capacity = edges.len().saturating_add(1) / 2;
                let other_live = dense_base_bytes.saturating_sub(old_pair_bytes);
                if ensure_vec_capacity(&mut scratch.fill_pairs, pair_capacity, other_live, limits)
                    .is_ok()
                {
                    dense_base_bytes = dense_base_bytes
                        .saturating_sub(old_pair_bytes)
                        .saturating_add(vec_capacity_bytes(&scratch.fill_pairs));
                    if !coverage.select_dense(x_origin, width, rows, limits, dense_base_bytes) {
                        scratch.fill_pairs = Vec::new();
                    }
                }
            }
        }
    }
    let mut current_y = clip.top;
    let mut active_visits = 0u64;
    let mut partial_visits = 0u64;
    let mut processed_events = 0usize;
    while let Some(first) = heap.pop() {
        processed_events = processed_events
            .checked_add(1)
            .ok_or(CoverageError::EventLimit)?;
        if processed_events > limits.max_events {
            return Err(CoverageError::EventLimit);
        }
        if first.y.is_nan() {
            return Err(CoverageError::NumericRange);
        }
        let y = first.y;
        if y > current_y {
            let base_bytes = estimate_intermediate_bytes(
                fixed_bytes,
                heap.capacity(),
                scheduled.capacity_bytes(),
                0,
                scratch.capacity_bytes(),
            );
            if base_bytes.saturating_add(coverage.live_bytes()) > limits.max_intermediate_bytes
                || base_bytes.saturating_add(coverage.live_bytes()) > limits.max_total_bytes
            {
                return Err(CoverageError::ActiveWorkLimit);
            }
            scratch.band_edges.clear();
            tree.copy_in_order(&mut scratch.band_edges, &mut scratch.band_stack);
            active_visits = active_visits
                .checked_add(scratch.band_edges.len() as u64)
                .ok_or(CoverageError::ActiveWorkLimit)?;
            if active_visits > limits.max_active_edge_visits {
                return Err(CoverageError::ActiveWorkLimit);
            }
            coverage.set_sweep_work(active_visits, limits)?;
            accumulate_band(
                &edges,
                &scratch.band_edges,
                fill_rule,
                current_y,
                y,
                clip,
                &mut coverage,
                &mut partial_visits,
                &mut scratch.fill_pairs,
                limits,
                base_bytes,
            )?;
            coverage.flush_completed_rows(y.floor() as i64, limits, base_bytes)?;
        }

        scratch.group.clear();
        scratch.remove.clear();
        scratch.add.clear();
        scratch.cross_x.clear();
        scratch.crossing_ids.clear();
        scratch.changed.clear();
        scratch.reinsert.clear();
        scratch.candidates.clear();
        let shared_bytes = estimate_intermediate_bytes(
            fixed_bytes,
            heap.capacity(),
            scheduled.capacity_bytes(),
            coverage.live_bytes(),
            0,
        );
        push_scratch!(scratch, group, first, shared_bytes, limits);
        while heap.peek().map_or(false, |e| same_y(e.y, y)) {
            let event = heap.pop().expect("peeked event");
            push_scratch!(scratch, group, event, shared_bytes, limits);
            processed_events = processed_events
                .checked_add(1)
                .ok_or(CoverageError::EventLimit)?;
            if processed_events > limits.max_events {
                return Err(CoverageError::EventLimit);
            }
        }
        // `same_y` deliberately groups ordinates separated only by floating
        // point noise. An edge ending anywhere inside that group is not active
        // on the group's y+ side, even when the representative (first) event
        // has a slightly smaller ordinate than the edge endpoint.
        let group_max_y = scratch.group.iter().map(|event| event.y).fold(y, f64::max);
        for event_index in 0..scratch.group.len() {
            let e = scratch.group[event_index];
            match e.kind {
                EventKind::End(id) => {
                    push_scratch!(scratch, remove, id, shared_bytes, limits);
                }
                EventKind::Start(id) => {
                    push_scratch!(scratch, add, id, shared_bytes, limits);
                }
                EventKind::Cross(a, b) => {
                    scheduled.remove((a.min(b), a.max(b)));
                    if tree.by_edge[a].is_some()
                        && tree.by_edge[b].is_some()
                        && near(edges[a].x_at(y), e.x)
                        && near(edges[b].x_at(y), e.x)
                    {
                        push_scratch!(scratch, cross_x, e.x, shared_bytes, limits);
                        // Preserve the event's certified pair as seeds. The
                        // active tree may already contain a numerically
                        // ambiguous tie, so a binary lower bound alone can
                        // miss the very pair whose crossing it is processing.
                        push_scratch!(scratch, changed, a, shared_bytes, limits);
                        push_scratch!(scratch, changed, b, shared_bytes, limits);
                    }
                }
                EventKind::Clip => {}
            }
        }

        // Expand each crossing point to its full coincident active block. A
        // multi-edge crossing is handled as one tie, not pairwise swaps.
        scratch.cross_x.sort_unstable_by(f64::total_cmp);
        scratch.cross_x.dedup_by(|a, b| near(*a, *b));

        for cross_index in 0..scratch.cross_x.len() {
            let x = scratch.cross_x[cross_index];
            push_scratch!(
                scratch,
                reinsert,
                CrossingGroup {
                    x,
                    seed_lo: None,
                    seed_hi: None,
                },
                shared_bytes,
                limits
            );
        }

        // Seed each block from its scheduled crossing pair before expanding
        // to any other coincident active edges found around the event x.
        for seed_index in 0..scratch.changed.len() {
            let id = scratch.changed[seed_index];
            if !tree.by_edge[id].is_some() || edges[id].b.y <= group_max_y {
                continue;
            }
            if let Some(group) = nearest_crossing_group(&scratch.cross_x, edges[id].x_at(y)) {
                let seed = CrossingEntry { group, edge: id };
                record_crossing_seed_ranges(
                    core::slice::from_ref(&seed),
                    &mut scratch.reinsert,
                    &tree,
                    &mut active_visits,
                    limits,
                )?;
                push_scratch!(scratch, crossing_ids, seed, shared_bytes, limits);
            }
        }

        for cross_index in 0..scratch.cross_x.len() {
            let group = scratch.reinsert[cross_index];
            // Expand from the ranks of this group's certified event seeds.
            // A binary lower bound assumes the pre-crossing tree is already
            // ordered at the tie; that is exactly the assumption invalidated
            // by a numerically ambiguous crossing.
            let (Some(mut lo), Some(mut hi)) = (group.seed_lo, group.seed_hi) else {
                continue;
            };

            while lo > 0 {
                let id = tree.edge_at(lo - 1).ok_or(CoverageError::NumericRange)?;
                active_visits = active_visits
                    .checked_add(1)
                    .ok_or(CoverageError::ActiveWorkLimit)?;
                if active_visits > limits.max_active_edge_visits {
                    return Err(CoverageError::ActiveWorkLimit);
                }
                if nearest_crossing_group(&scratch.cross_x, edges[id].x_at(y)) != Some(cross_index)
                {
                    break;
                }
                lo -= 1;
            }
            while hi + 1 < tree.len() {
                let id = tree.edge_at(hi + 1).ok_or(CoverageError::NumericRange)?;
                active_visits = active_visits
                    .checked_add(1)
                    .ok_or(CoverageError::ActiveWorkLimit)?;
                if active_visits > limits.max_active_edge_visits {
                    return Err(CoverageError::ActiveWorkLimit);
                }
                if nearest_crossing_group(&scratch.cross_x, edges[id].x_at(y)) != Some(cross_index)
                {
                    break;
                }
                hi += 1;
            }

            for rank in lo..=hi {
                active_visits = active_visits
                    .checked_add(1)
                    .ok_or(CoverageError::ActiveWorkLimit)?;
                if active_visits > limits.max_active_edge_visits {
                    return Err(CoverageError::ActiveWorkLimit);
                }
                let id = tree.edge_at(rank).ok_or(CoverageError::NumericRange)?;
                if nearest_crossing_group(&scratch.cross_x, edges[id].x_at(y)) == Some(cross_index)
                {
                    // Edges ending at this ordinate must be removed, but must
                    // not be reinserted into the y+ active order. A crossing
                    // coincident with a shared polygon vertex can otherwise
                    // resurrect the incoming edge beyond its endpoint.
                    if edges[id].b.y <= group_max_y {
                        push_scratch!(scratch, remove, id, shared_bytes, limits);
                        continue;
                    }
                    push_scratch!(
                        scratch,
                        crossing_ids,
                        CrossingEntry {
                            group: cross_index,
                            edge: id,
                        },
                        shared_bytes,
                        limits
                    );
                }
            }
        }

        // A vertex can start on the same ordinate as a crossing. Include any
        // start at that crossing x in its block so f64 evaluation noise cannot
        // split a multi-edge tie into an arbitrary left/right order.
        let mut retained_starts = 0;
        for read_index in 0..scratch.add.len() {
            let id = scratch.add[read_index];
            if edges[id].b.y <= group_max_y {
                continue;
            }
            if let Some(group) = nearest_crossing_group(&scratch.cross_x, edges[id].x_at(y)) {
                push_scratch!(
                    scratch,
                    crossing_ids,
                    CrossingEntry { group, edge: id },
                    shared_bytes,
                    limits
                );
            } else {
                scratch.add[retained_starts] = id;
                retained_starts += 1;
            }
        }
        scratch.add.truncate(retained_starts);

        // Keep each crossing block together and order it by the side the sweep
        // enters (increasing y). The event x is the canonical shared x; do not
        // let a few ulps of independently evaluated x_at(y) reorder members.
        scratch.crossing_ids.sort_unstable_by(|a, b| {
            a.group
                .cmp(&b.group)
                .then_with(|| edges[a.edge].slope().total_cmp(&edges[b.edge].slope()))
                .then_with(|| a.edge.cmp(&b.edge))
        });
        scratch
            .crossing_ids
            .dedup_by(|a, b| a.group == b.group && a.edge == b.edge);
        for crossing_index in 0..scratch.crossing_ids.len() {
            let id = scratch.crossing_ids[crossing_index].edge;
            push_scratch!(scratch, remove, id, shared_bytes, limits);
        }
        scratch.remove.sort_unstable();
        scratch.remove.dedup();

        for remove_index in 0..scratch.remove.len() {
            let id = scratch.remove[remove_index];
            if let Some(node) = tree.by_edge[id] {
                let (before, after) = tree.neighbors(node);
                if let Some(neighbor) = before {
                    push_scratch!(scratch, changed, neighbor, shared_bytes, limits);
                }
                if let Some(neighbor) = after {
                    push_scratch!(scratch, changed, neighbor, shared_bytes, limits);
                }
                let removed = tree.remove_unranked(id);
                debug_assert!(removed);
            }
        }
        // Reinsert each crossing block at its canonical event x. Members are
        // already slope-ordered for the y+ side; inserting them at one shared
        // rank preserves that order even when x_at(y) differs by a few ulps.
        let mut crossing_cursor = 0;
        for group_index in 0..scratch.reinsert.len() {
            let group = scratch.reinsert[group_index];
            let start = crossing_cursor;
            while crossing_cursor < scratch.crossing_ids.len()
                && scratch.crossing_ids[crossing_cursor].group == group_index
            {
                crossing_cursor += 1;
            }
            if start == crossing_cursor {
                continue;
            }
            let mut rank = lower_bound_x(&tree, &edges, group.x, y);
            for crossing_index in start..crossing_cursor {
                let id = scratch.crossing_ids[crossing_index].edge;
                if tree.by_edge[id].is_some() {
                    continue;
                }
                tree.insert_at(id, rank);
                rank += 1;
                push_scratch!(scratch, changed, id, shared_bytes, limits);
            }
        }
        // Multiple starts/crossings at one y are combined in geometric order.
        scratch.add.sort_unstable();
        scratch.add.dedup();
        scratch.add.retain(|id| edges[*id].b.y > group_max_y);
        scratch
            .add
            .sort_unstable_by(|&a, &b| compare_at_y_above(&edges[a], &edges[b], y));

        // Find insertion ranks using current neighboring geometry. Insertions
        // are individually logarithmic in the order tree; same-y ties have a
        // deterministic slope/id order.
        for add_index in 0..scratch.add.len() {
            let id = scratch.add[add_index];
            if tree.by_edge[id].is_some() {
                continue;
            }
            let rank = insertion_rank(&tree, &edges, id, y, true);
            tree.insert_at(id, rank);
            push_scratch!(scratch, changed, id, shared_bytes, limits);
        }

        // Schedule only adjacent pairs touching the changed region. Existing
        // pairs keep their event; crossing-pair events are removed on pop.
        scratch.changed.sort_unstable();
        scratch.changed.dedup();
        for changed_index in 0..scratch.changed.len() {
            let id = scratch.changed[changed_index];
            let Some(node) = tree.by_edge[id] else {
                continue;
            };
            let (before, after) = tree.neighbors(node);
            if let Some(before) = before {
                push_scratch!(scratch, candidates, (before, id), shared_bytes, limits);
            }
            if let Some(after) = after {
                push_scratch!(scratch, candidates, (id, after), shared_bytes, limits);
            }
        }
        scratch.candidates.sort_unstable();
        scratch.candidates.dedup();
        for candidate_index in 0..scratch.candidates.len() {
            let (a, b) = scratch.candidates[candidate_index];
            if let Some((iy, ix)) =
                segment_intersection_after(edges[a], edges[b], group_max_y, clip)
            {
                let key = (a.min(b), a.max(b));
                if !scheduled.contains(key) {
                    let non_scheduled_bytes = estimate_intermediate_bytes(
                        fixed_bytes,
                        heap.capacity(),
                        0,
                        coverage.live_bytes(),
                        scratch.capacity_bytes(),
                    );
                    if scheduled.insert(key, limits, non_scheduled_bytes)? {
                        let non_heap_bytes = estimate_intermediate_bytes(
                            fixed_bytes,
                            0,
                            scheduled.capacity_bytes(),
                            coverage.live_bytes(),
                            scratch.capacity_bytes(),
                        );
                        push_event(
                            &mut heap,
                            Event {
                                y: iy,
                                x: ix,
                                kind: EventKind::Cross(a, b),
                            },
                            &mut event_count,
                            limits,
                            non_heap_bytes,
                        )?;
                    }
                }
            }
        }

        #[cfg(test)]
        {
            for rank in 0..tree.len().saturating_sub(1) {
                let a = tree.edge_at(rank).unwrap();
                let b = tree.edge_at(rank + 1).unwrap();
                let key = (a.min(b), a.max(b));
                if segment_intersection_after(edges[a], edges[b], group_max_y, clip).is_some() {
                    assert!(
                        scheduled.contains(key),
                        "adjacent future crossing not scheduled after y={y}: pair={key:?}, a={:?}->{:?}, b={:?}->{:?}, active={:?}",
                        edges[a].a,
                        edges[a].b,
                        edges[b].a,
                        edges[b].b,
                        tree.in_order()
                    );
                }
            }
        }
        // Continue from the upper edge of the collapsed event group. Using
        // its minimum ordinate can revisit a fraction of a pixel row after
        // that row has already been flushed as complete.
        current_y = group_max_y;
    }

    // Every queued event is consumed on a successful sweep. Keep the pushed
    // and processed ledgers tied together so cumulative component accounting
    // cannot accidentally refund stale or compacted events.
    if event_count != processed_events {
        return Err(CoverageError::EventLimit);
    }

    let finish_base_bytes = estimate_intermediate_bytes(
        fixed_bytes,
        heap.capacity(),
        scheduled.capacity_bytes(),
        0,
        scratch.capacity_bytes(),
    );
    let mut current_usage = RasterUsage {
        edges: edges.len(),
        processed_events,
        active_work: active_visits
            .checked_add(coverage.compaction_work)
            .and_then(|work| work.checked_add(coverage.dense_work))
            .ok_or(CoverageError::ActiveWorkLimit)?,
        partial_intersections: partial_visits,
        range_runs: coverage.total_range_count,
        output_spans: 0,
    };
    let spans = coverage.finish(limits, finish_base_bytes)?;
    current_usage.output_spans = spans.len();
    cumulative_usage.add(current_usage)?;
    Ok(spans)
}

fn lower_bound_x(tree: &OrderTree, edges: &[Edge], x: f64, y: f64) -> usize {
    // Search nodes once while accumulating their in-order rank. A binary
    // search over ranks would itself traverse the tree at every comparison.
    let mut rank = 0usize;
    let mut cursor = tree.root;
    while let Some(node_id) = cursor {
        let node = &tree.nodes[node_id];
        let edge = &edges[node.edge];
        if edge.x_at(y).total_cmp(&x) == Ordering::Less {
            rank += tree.size(node.left) + 1;
            cursor = node.right;
        } else {
            cursor = node.left;
        }
    }
    rank
}

#[cfg(test)]
mod order_tree_tests {
    use super::*;

    #[test]
    fn node_removal_preserves_rank_sequence_and_treap_invariants() {
        let mut tree = OrderTree::try_new(128).unwrap();
        let mut sequence = Vec::new();
        let mut state = 0x2388_1486_4400_f00du64;
        for iteration in 0..20_000 {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            let edge = (state as usize) % 128;
            if let Some(index) = sequence.iter().position(|&current| current == edge) {
                assert_eq!(tree.remove(edge), Some(index));
                sequence.remove(index);
                assert_eq!(tree.remove(edge), None);
            } else {
                let rank = ((state >> 8) as usize) % (sequence.len() + 1);
                sequence.insert(rank, edge);
                tree.insert_at(edge, rank);
            }
            assert_eq!(tree.in_order(), sequence, "iteration {iteration}");
            assert_eq!(tree.len(), sequence.len());
            if let Some(root) = tree.root {
                assert_eq!(tree.nodes[root].parent, None);
            }
            for (rank, &edge) in sequence.iter().enumerate() {
                let node_id = tree.by_edge[edge].unwrap();
                let node = &tree.nodes[node_id];
                assert_eq!(tree.edge_at(rank), Some(edge));
                assert_eq!(tree.rank_node(node_id), rank);
                assert_eq!(
                    tree.neighbors(node_id),
                    (
                        rank.checked_sub(1).map(|previous| sequence[previous]),
                        sequence.get(rank + 1).copied(),
                    ),
                    "neighbor lookup at iteration {iteration}, rank {rank}"
                );
                assert_eq!(node.size, 1 + tree.size(node.left) + tree.size(node.right));
                for child in [node.left, node.right].into_iter().flatten() {
                    assert_eq!(tree.nodes[child].parent, Some(node_id));
                    assert!(node.priority <= tree.nodes[child].priority);
                }
            }
        }
    }

    #[test]
    fn node_search_matches_linear_order_at_crossings_and_signed_zero() {
        let edges: Vec<Edge> = (0..128)
            .map(|id| {
                let x = (id / 4) as f64 - 16.0;
                let slope = (id % 4) as f64 - 1.5;
                Edge {
                    a: Point {
                        x: x - slope,
                        y: -1.0,
                    },
                    b: Point {
                        x: x + slope,
                        y: 1.0,
                    },
                    id,
                    winding: 1,
                }
            })
            .collect();
        for above in [false, true] {
            let mut sequence: Vec<_> = (0..edges.len()).collect();
            sequence.sort_unstable_by(|&a, &b| {
                if above {
                    compare_at_y_above(&edges[a], &edges[b], 0.0)
                } else {
                    compare_at_y_below(&edges[a], &edges[b], 0.0)
                }
            });
            let mut tree = OrderTree::try_new(edges.len()).unwrap();
            for &id in &sequence {
                tree.insert_at(id, tree.len());
            }
            for x in [-100.0, -16.0, -3.5, -0.0, 0.0, 0.5, 15.0, 100.0] {
                let expected = sequence
                    .partition_point(|&id| edges[id].x_at(0.0).total_cmp(&x) == Ordering::Less);
                assert_eq!(lower_bound_x(&tree, &edges, x, 0.0), expected);
            }
            for (rank, &id) in sequence.iter().enumerate() {
                assert_eq!(insertion_rank(&tree, &edges, id, 0.0, above), rank + 1);
            }
        }
    }

    #[test]
    fn unranked_removal_matches_ranked_in_order_removal() {
        let mut unranked = OrderTree::try_new(256).unwrap();
        let mut ranked = OrderTree::try_new(256).unwrap();
        let mut sequence = Vec::new();
        let mut state = 0xD1B5_4A32_D192_ED03u64;

        for edge in 0..256 {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            let rank = (state as usize) % (sequence.len() + 1);
            sequence.insert(rank, edge);
            unranked.insert_at(edge, rank);
            ranked.insert_at(edge, rank);
        }

        while !sequence.is_empty() {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            let rank = (state as usize) % sequence.len();
            let edge = sequence.remove(rank);
            assert_eq!(ranked.remove(edge), Some(rank));
            assert!(unranked.remove_unranked(edge));
            assert_eq!(ranked.in_order(), sequence);
            assert_eq!(unranked.in_order(), sequence);
            assert!(!unranked.remove_unranked(edge));
        }
    }
}

fn nearest_crossing_group(crossings: &[f64], x: f64) -> Option<usize> {
    let mut lo = 0usize;
    let mut hi = crossings.len();
    while lo < hi {
        let mid = lo + (hi - lo) / 2;
        if crossings[mid].total_cmp(&x) == Ordering::Less {
            lo = mid + 1;
        } else {
            hi = mid;
        }
    }

    let before = lo.checked_sub(1);
    let after = (lo < crossings.len()).then_some(lo);
    match (before, after) {
        (Some(before), Some(after)) => {
            let before_distance = (crossings[before] - x).abs();
            let after_distance = (crossings[after] - x).abs();
            let nearest = if before_distance <= after_distance {
                before
            } else {
                after
            };
            near(crossings[nearest], x).then_some(nearest)
        }
        (Some(index), None) | (None, Some(index)) => near(crossings[index], x).then_some(index),
        (None, None) => None,
    }
}

fn record_crossing_seed_ranges(
    seeds: &[CrossingEntry],
    groups: &mut [CrossingGroup],
    tree: &OrderTree,
    active_visits: &mut u64,
    limits: Limits,
) -> Result<(), CoverageError> {
    for seed in seeds {
        *active_visits = active_visits
            .checked_add(1)
            .ok_or(CoverageError::ActiveWorkLimit)?;
        if *active_visits > limits.max_active_edge_visits {
            return Err(CoverageError::ActiveWorkLimit);
        }

        let group = groups
            .get_mut(seed.group)
            .ok_or(CoverageError::NumericRange)?;
        let Some(node) = tree.by_edge.get(seed.edge).copied().flatten() else {
            continue;
        };
        let rank = tree.rank_node(node);
        group.seed_lo = Some(group.seed_lo.map_or(rank, |current| current.min(rank)));
        group.seed_hi = Some(group.seed_hi.map_or(rank, |current| current.max(rank)));
    }
    Ok(())
}

fn estimate_intermediate_bytes(
    fixed_bytes: usize,
    heap_capacity: usize,
    scheduled_bytes: usize,
    coverage_bytes: usize,
    scratch_bytes: usize,
) -> usize {
    fixed_bytes
        .saturating_add(heap_capacity.saturating_mul(core::mem::size_of::<Event>()))
        .saturating_add(scheduled_bytes)
        .saturating_add(coverage_bytes)
        .saturating_add(scratch_bytes)
}

fn validate_clip(c: ClipRect) -> Result<(), CoverageError> {
    if ![c.left, c.top, c.right, c.bottom]
        .iter()
        .all(|v| v.is_finite())
        || c.right < c.left
        || c.bottom < c.top
    {
        return Err(CoverageError::InvalidClip);
    }
    Ok(())
}

fn validate_raster_limits(limits: Limits) -> Result<(), CoverageError> {
    if !limits.max_coordinate_abs.is_finite()
        || limits.max_coordinate_abs <= 0.0
        || !limits.curve_error_px.is_finite()
        || limits.curve_error_px <= 0.0
    {
        return Err(CoverageError::InvalidCoordinate);
    }
    Ok(())
}

fn build_edges(
    contours: &[Vec<Point>],
    clip: ClipRect,
    limits: Limits,
    base_live_bytes: usize,
) -> Result<Vec<Edge>, CoverageError> {
    let mut edge_count = 0usize;
    for contour in contours {
        if contour.len() < 3 {
            continue;
        }
        for i in 0..contour.len() {
            let p = contour[i];
            let q = contour[(i + 1) % contour.len()];
            if clipped_edge(p, q, clip, limits, 0)?.is_some() {
                edge_count = edge_count.checked_add(1).ok_or(CoverageError::EdgeLimit)?;
            }
            if edge_count > limits.max_edges {
                return Err(CoverageError::EdgeLimit);
            }
        }
    }

    // Edge storage plus the order tree and reusable scan buffers are admitted
    // before allocating. The fixed 192-byte allowance per edge is a
    // conservative combined estimate for these structures.
    let edge_scratch_upper = edge_count.saturating_mul(192);
    if base_live_bytes.saturating_add(edge_scratch_upper) > limits.max_intermediate_bytes
        || base_live_bytes.saturating_add(edge_scratch_upper) > limits.max_total_bytes
    {
        return Err(CoverageError::ActiveWorkLimit);
    }
    let mut out = Vec::new();
    out.try_reserve_exact(edge_count)
        .map_err(|_| CoverageError::AllocationFailure)?;
    for contour in contours {
        if contour.len() < 3 {
            continue;
        }
        for i in 0..contour.len() {
            if let Some(mut edge) = clipped_edge(
                contour[i],
                contour[(i + 1) % contour.len()],
                clip,
                limits,
                out.len(),
            )? {
                edge.id = out.len();
                out.push(edge);
            }
        }
    }
    Ok(out)
}

fn clipped_edge(
    p: Point,
    q: Point,
    clip: ClipRect,
    limits: Limits,
    id: usize,
) -> Result<Option<Edge>, CoverageError> {
    if ![p.x, p.y, q.x, q.y].iter().all(|v| v.is_finite())
        || p.x.abs() > limits.max_coordinate_abs
        || p.y.abs() > limits.max_coordinate_abs
        || q.x.abs() > limits.max_coordinate_abs
        || q.y.abs() > limits.max_coordinate_abs
    {
        return Err(CoverageError::InvalidCoordinate);
    }
    if p.y == q.y {
        return Ok(None);
    }
    let winding = if p.y < q.y { 1 } else { -1 };
    let mut a = if p.y < q.y { p } else { q };
    let mut b = if p.y < q.y { q } else { p };
    if b.y <= clip.top || a.y >= clip.bottom {
        return Ok(None);
    }
    if a.y < clip.top {
        let t = (clip.top - a.y) / (b.y - a.y);
        a = Point {
            x: a.x + t * (b.x - a.x),
            y: clip.top,
        };
    }
    if b.y > clip.bottom {
        let t = (clip.bottom - a.y) / (b.y - a.y);
        b = Point {
            x: a.x + t * (b.x - a.x),
            y: clip.bottom,
        };
    }
    if ![a.x, a.y, b.x, b.y].iter().all(|v| v.is_finite()) {
        return Err(CoverageError::NumericRange);
    }
    Ok(Some(Edge { a, b, winding, id }))
}

fn same_y(a: f64, b: f64) -> bool {
    a.to_bits() == b.to_bits() || near(a, b)
}
fn near(a: f64, b: f64) -> bool {
    let scale = a.abs().max(b.abs()).max(1.0);
    (a - b).abs() <= f64::EPSILON * 32.0 * scale
}

fn compare_at_y_above(a: &Edge, b: &Edge, y: f64) -> Ordering {
    let ax = a.x_at(y);
    let bx = b.x_at(y);
    ax.total_cmp(&bx)
        .then_with(|| a.slope().total_cmp(&b.slope()))
        .then_with(|| a.id.cmp(&b.id))
}
fn compare_at_y_below(a: &Edge, b: &Edge, y: f64) -> Ordering {
    let ax = a.x_at(y);
    let bx = b.x_at(y);
    ax.total_cmp(&bx)
        .then_with(|| b.slope().total_cmp(&a.slope()))
        .then_with(|| a.id.cmp(&b.id))
}

fn insertion_rank(tree: &OrderTree, edges: &[Edge], edge_id: usize, y: f64, above: bool) -> usize {
    let mut rank = 0usize;
    let mut cursor = tree.root;
    while let Some(node_id) = cursor {
        let node = &tree.nodes[node_id];
        let other = &edges[node.edge];
        let ord = if above {
            compare_at_y_above(&edges[edge_id], other, y)
        } else {
            compare_at_y_below(&edges[edge_id], other, y)
        };
        if ord == Ordering::Less {
            cursor = node.left;
        } else {
            rank += tree.size(node.left) + 1;
            cursor = node.right;
        }
    }
    rank
}

fn segment_intersection_after(a: Edge, b: Edge, y_now: f64, clip: ClipRect) -> Option<(f64, f64)> {
    let rx = a.b.x - a.a.x;
    let ry = a.b.y - a.a.y;
    let sx = b.b.x - b.a.x;
    let sy = b.b.y - b.a.y;
    let den = rx * sy - ry * sx;
    if !den.is_finite()
        || den.abs() <= f64::EPSILON * (rx.abs() * sy.abs() + ry.abs() * sx.abs()).max(1.0)
    {
        return None;
    }
    let qpx = b.a.x - a.a.x;
    let qpy = b.a.y - a.a.y;
    let t = (qpx * sy - qpy * sx) / den;
    let u = (qpx * ry - qpy * rx) / den;
    if !t.is_finite() || !u.is_finite() || t < 0.0 || t > 1.0 || u < 0.0 || u > 1.0 {
        return None;
    }
    let iy = a.a.y + t * ry;
    let ix = a.a.x + t * rx;
    if !iy.is_finite() || !ix.is_finite() || iy <= y_now || iy < clip.top || iy > clip.bottom {
        return None;
    }
    Some((iy, ix))
}

fn accumulate_band(
    edges: &[Edge],
    order: &[usize],
    rule: FillRule,
    y0: f64,
    y1: f64,
    clip: ClipRect,
    coverage: &mut CoverageAccumulator,
    partial: &mut u64,
    fill_pairs: &mut Vec<(usize, usize)>,
    limits: Limits,
    base_bytes: usize,
) -> Result<(), CoverageError> {
    if y1 <= y0 {
        return Ok(());
    }
    let y0 = y0.max(clip.top);
    let y1 = y1.min(clip.bottom);
    if y1 <= y0 {
        return Ok(());
    }
    let sample_y = y0 * 0.5 + y1 * 0.5;
    let validate_sample_interpolation =
        limits.max_coordinate_abs > MAX_COORDINATE_WITH_PROVEN_FINITE_INTERPOLATION;
    let mut winding = 0i32;
    let mut parity = false;
    let mut left = None;
    let dense_mode = coverage.is_dense();
    if dense_mode {
        fill_pairs.clear();
    }
    for &id in order {
        let edge = edges[id];
        let filled_before = match rule {
            FillRule::EvenOdd => parity,
            FillRule::NonZero => winding != 0,
        };
        match rule {
            FillRule::EvenOdd => parity = !parity,
            FillRule::NonZero => {
                winding = winding
                    .checked_add(edge.winding)
                    .ok_or(CoverageError::NumericRange)?
            }
        }
        let filled_after = match rule {
            FillRule::EvenOdd => parity,
            FillRule::NonZero => winding != 0,
        };
        if !filled_before && filled_after {
            left = Some(edge);
        } else if filled_before && !filled_after {
            let Some(l) = left.take() else {
                return Err(CoverageError::NumericRange);
            };
            if dense_mode {
                if fill_pairs.len() >= fill_pairs.capacity() {
                    return Err(CoverageError::ActiveWorkLimit);
                }
                fill_pairs.push((l.id, edge.id));
            } else {
                accumulate_trapezoid_rows(
                    l, edge, y0, y1, clip, coverage, partial, limits, base_bytes,
                )?;
            }
        }
        // Intersections are sweep events, so the order and fill transitions
        // are constant throughout this open band. The sample value is used
        // only to reject numerically unstable ordering, not to choose spans.
        if validate_sample_interpolation && !edge.x_at(sample_y).is_finite() {
            return Err(CoverageError::NumericRange);
        }
    }
    if left.is_some() {
        return Err(CoverageError::NumericRange);
    }
    if dense_mode {
        // A large clip may contain long empty bands before or after the path.
        // Dense row work is admitted from the clipped geometry bounds, so do
        // not walk those empty rows here; the caller flushes any pending row
        // when the next sweep event proves it complete.
        if fill_pairs.is_empty() {
            return Ok(());
        }
        let row_start = y0.floor() as i32;
        let row_end = y1.ceil() as i32;
        for row in row_start..row_end {
            coverage.begin_dense_row(row, limits, base_bytes)?;
            let ya = y0.max(row as f64);
            let yb = y1.min(row as f64 + 1.0);
            if yb <= ya {
                continue;
            }
            for &(left_id, right_id) in fill_pairs.iter() {
                accumulate_trapezoid(
                    edges[left_id],
                    edges[right_id],
                    ya,
                    yb,
                    clip,
                    row,
                    coverage,
                    partial,
                    limits,
                    base_bytes,
                )?;
            }
        }
    }
    Ok(())
}

// Exposes the interpolation validation to the artifact's boundary tests. The
// coincident opposite-winding edges produce no pixels, so extreme coordinate
// cases exercise the production band walk without a large raster allocation.
#[cfg(test)]
pub(crate) fn probe_sample_interpolation_for_test(
    a: Point,
    b: Point,
    y0: f64,
    y1: f64,
    clip: ClipRect,
    rule: FillRule,
    max_coordinate_abs: f64,
) -> Result<(), CoverageError> {
    let band_y0 = y0.max(clip.top);
    let band_y1 = y1.min(clip.bottom);
    if band_y1 > band_y0 {
        // The production sweep only passes edges whose start event is at or
        // below this band and whose end event is at or above its next event.
        debug_assert!(a.y <= band_y0 && band_y1 <= b.y);
    }
    let edges = [
        Edge {
            a,
            b,
            winding: 1,
            id: 0,
        },
        Edge {
            a,
            b,
            winding: -1,
            id: 1,
        },
    ];
    let mut limits = Limits::default();
    limits.max_coordinate_abs = max_coordinate_abs;
    let mut coverage = CoverageAccumulator::default();
    let mut partial = 0;
    let mut fill_pairs = Vec::new();
    accumulate_band(
        &edges,
        &[0, 1],
        rule,
        y0,
        y1,
        clip,
        &mut coverage,
        &mut partial,
        &mut fill_pairs,
        limits,
        0,
    )
}

fn accumulate_trapezoid_rows(
    left: Edge,
    right: Edge,
    y0: f64,
    y1: f64,
    clip: ClipRect,
    coverage: &mut CoverageAccumulator,
    partial: &mut u64,
    limits: Limits,
    base_bytes: usize,
) -> Result<(), CoverageError> {
    let row_start = y0.floor() as i32;
    let row_end = y1.ceil() as i32;
    for row in row_start..row_end {
        let ya = y0.max(row as f64);
        let yb = y1.min(row as f64 + 1.0);
        if yb > ya {
            accumulate_trapezoid(
                left, right, ya, yb, clip, row, coverage, partial, limits, base_bytes,
            )?;
        }
    }
    Ok(())
}

fn accumulate_trapezoid(
    left: Edge,
    right: Edge,
    y0: f64,
    y1: f64,
    clip: ClipRect,
    row: i32,
    coverage: &mut CoverageAccumulator,
    partial: &mut u64,
    limits: Limits,
    base_bytes: usize,
) -> Result<(), CoverageError> {
    // Keep the exact trapezoid; clip polygon-to-cell below. Clamping the edge
    // vertices independently would bend a sloped boundary at the clip edge.
    let xl0 = left.x_at(y0);
    let xr0 = right.x_at(y0);
    let xl1 = left.x_at(y1);
    let xr1 = right.x_at(y1);
    if [xl0, xr0, xl1, xr1].iter().any(|v| !v.is_finite()) {
        return Err(CoverageError::NumericRange);
    }
    if xr0 <= xl0 && xr1 <= xl1 {
        return Ok(());
    }
    let polygon = [
        Point { x: xl0, y: y0 },
        Point { x: xr0, y: y0 },
        Point { x: xr1, y: y1 },
        Point { x: xl1, y: y1 },
    ];
    let minx = polygon
        .iter()
        .map(|p| p.x)
        .fold(f64::INFINITY, f64::min)
        .floor()
        .max(clip.left.floor());
    let maxx = polygon
        .iter()
        .map(|p| p.x)
        .fold(f64::NEG_INFINITY, f64::max)
        .ceil()
        .min(clip.right.ceil());
    let candidate_start = minx as i32;
    let candidate_end = maxx as i32;
    let full_start = xl0.max(xl1).max(clip.left).ceil() as i32;
    let full_end = xr0.min(xr1).min(clip.right).floor() as i32;
    coverage.add_range(row, full_start, full_end, y1 - y0, limits, base_bytes)?;
    for x in candidate_start..candidate_end {
        if x >= full_start && x < full_end {
            continue;
        }
        *partial = partial
            .checked_add(1)
            .ok_or(CoverageError::PartialPixelLimit)?;
        if *partial > limits.max_partial_pixel_intersections {
            return Err(CoverageError::PartialPixelLimit);
        }
        let cell = ClipRect {
            left: (x as f64).max(clip.left),
            top: y0.max(clip.top),
            right: ((x as f64) + 1.0).min(clip.right),
            bottom: y1.min(clip.bottom),
        };
        if cell.right <= cell.left || cell.bottom <= cell.top {
            continue;
        }
        // The sweep already clipped this band vertically. Its boundaries are
        // affine, so integrating two clamped horizontal widths gives the exact
        // cell area without clipping a polygon against four half-planes. Keep
        // the established polygon kernel for a numerically crossed endpoint;
        // that degenerate case is not an ordered trapezoid.
        let area = if xl0 <= xr0 && xl1 <= xr1 {
            trapezoid_cell_area(xl0, xr0, xl1, xr1, y1 - y0, cell.left, cell.right)?
        } else {
            polygon_rect_area(polygon, cell)?
        };
        coverage.add_partial(row, x, area, limits, base_bytes)?;
    }
    Ok(())
}

/// Average of a linearly interpolated value clamped to [0, width]. Integrate
/// the clipped triangles/rectangle analytically, rather than sampling coverage.
fn clamped_linear_mean(a: f64, b: f64, width: f64) -> f64 {
    let lo = a.min(b);
    let hi = a.max(b);
    if hi <= 0.0 {
        0.0
    } else if lo >= width {
        width
    } else if lo >= 0.0 && hi <= width {
        0.5 * lo + 0.5 * hi
    } else {
        // At least one endpoint is outside the interval and the other is on
        // the opposite side of a boundary, so the denominator is positive.
        let delta = hi - lo;
        if lo < 0.0 {
            if hi <= width {
                0.5 * hi * (hi / delta)
            } else {
                width * ((hi - 0.5 * width) / delta)
            }
        } else {
            let gap = width - lo;
            width - 0.5 * gap * (gap / delta)
        }
    }
}

fn trapezoid_cell_area(
    left0: f64,
    right0: f64,
    left1: f64,
    right1: f64,
    height: f64,
    cell_left: f64,
    cell_right: f64,
) -> Result<f64, CoverageError> {
    let width = cell_right - cell_left;
    let mean_right = clamped_linear_mean(right0 - cell_left, right1 - cell_left, width);
    let mean_left = clamped_linear_mean(left0 - cell_left, left1 - cell_left, width);
    let mean = mean_right - mean_left;
    // Monotonicity follows from the ordered endpoints. Tolerate only arithmetic
    // roundoff, not an inconsistent geometry or a meaningful negative area.
    let roundoff = 16.0 * f64::EPSILON * width;
    if !mean.is_finite() || mean < -roundoff || mean > width + roundoff {
        return Err(CoverageError::NumericRange);
    }
    Ok(mean.clamp(0.0, width) * height)
}

fn polygon_rect_area(polygon: [Point; 4], r: ClipRect) -> Result<f64, CoverageError> {
    // A convex quad clipped by four half-planes has at most eight vertices.
    // Fixed stack buffers avoid heap allocation for every partial pixel.
    let mut a = [Point { x: 0.0, y: 0.0 }; 8];
    let mut b = [Point { x: 0.0, y: 0.0 }; 8];
    a[..4].copy_from_slice(&polygon);
    let mut count = 4;
    for (axis, bound, keep_greater) in [
        (0usize, r.left, true),
        (0usize, r.right, false),
        (1usize, r.top, true),
        (1usize, r.bottom, false),
    ] {
        count = clip_polygon_fixed(&a[..count], &mut b, axis, bound, keep_greater)?;
        core::mem::swap(&mut a, &mut b);
        if count < 3 {
            return Ok(0.0);
        }
    }
    let mut sum = 0.0;
    for i in 0..count {
        let p = a[i];
        let q = a[(i + 1) % count];
        sum += p.x * q.y - q.x * p.y;
    }
    let area = sum.abs() * 0.5;
    if area.is_finite() {
        Ok(area)
    } else {
        Err(CoverageError::NumericRange)
    }
}

fn clip_polygon_fixed(
    polygon: &[Point],
    output: &mut [Point; 8],
    axis: usize,
    bound: f64,
    keep_greater: bool,
) -> Result<usize, CoverageError> {
    if polygon.is_empty() {
        return Ok(0);
    }
    let coord = |p: Point| if axis == 0 { p.x } else { p.y };
    let inside = |p: Point| {
        if keep_greater {
            coord(p) >= bound
        } else {
            coord(p) <= bound
        }
    };
    let mut count = 0usize;
    let mut previous = *polygon.last().unwrap();
    let mut previous_inside = inside(previous);
    for current in polygon.iter().copied() {
        let current_inside = inside(current);
        if current_inside != previous_inside {
            let denominator = coord(current) - coord(previous);
            if denominator != 0.0 {
                let t = (bound - coord(previous)) / denominator;
                let intersection = Point {
                    x: previous.x + t * (current.x - previous.x),
                    y: previous.y + t * (current.y - previous.y),
                };
                if !t.is_finite() || !intersection.x.is_finite() || !intersection.y.is_finite() {
                    return Err(CoverageError::NumericRange);
                }
                if count == output.len() {
                    return Err(CoverageError::NumericRange);
                }
                output[count] = intersection;
                count += 1;
            }
        }
        if current_inside {
            if count == output.len() {
                return Err(CoverageError::NumericRange);
            }
            output[count] = current;
            count += 1;
        }
        previous = current;
        previous_inside = current_inside;
    }
    Ok(count)
}

// Adaptive De Casteljau flattening helpers. Curve constructors append into a
// private vector and enforce both depth and final edge budget.
pub fn flatten_quadratic(
    p0: Point,
    p1: Point,
    p2: Point,
    limits: Limits,
    out: &mut Vec<Point>,
) -> Result<(), CoverageError> {
    validate_curve_points(&[p0, p1, p2], limits)?;
    let mut flattened = Vec::new();
    let mut stack = new_flatten_stack::<(Point, Point, Point, u8)>(out, limits)?;
    stack.push((p0, p1, p2, 0u8));
    ensure_flatten_budget(&flattened, &stack, out, limits)?;
    while let Some((a, b, c, depth)) = stack.pop() {
        if point_line_distance(b, a, c) <= limits.curve_error_px {
            push_flattened(&mut flattened, c, &stack, out, limits)?;
        } else if depth >= limits.max_curve_depth {
            return Err(CoverageError::CurveLimit);
        } else {
            let ab = mid(a, b);
            let bc = mid(b, c);
            let abc = mid(ab, bc);
            stack.push((abc, bc, c, depth + 1));
            stack.push((a, ab, abc, depth + 1));
            ensure_flatten_budget(&flattened, &stack, out, limits)?;
        }
    }
    drop(stack);
    reserve_flatten_output(out, &flattened, limits)?;
    out.extend(flattened);
    Ok(())
}

pub fn flatten_cubic(
    p0: Point,
    p1: Point,
    p2: Point,
    p3: Point,
    limits: Limits,
    out: &mut Vec<Point>,
) -> Result<(), CoverageError> {
    validate_curve_points(&[p0, p1, p2, p3], limits)?;
    let mut flattened = Vec::new();
    let mut stack = new_flatten_stack::<(Point, Point, Point, Point, u8)>(out, limits)?;
    stack.push((p0, p1, p2, p3, 0u8));
    ensure_flatten_budget(&flattened, &stack, out, limits)?;
    while let Some((a, b, c, d, depth)) = stack.pop() {
        let flat = point_line_distance(b, a, d).max(point_line_distance(c, a, d));
        if flat <= limits.curve_error_px {
            push_flattened(&mut flattened, d, &stack, out, limits)?;
        } else if depth >= limits.max_curve_depth {
            return Err(CoverageError::CurveLimit);
        } else {
            let ab = mid(a, b);
            let bc = mid(b, c);
            let cd = mid(c, d);
            let abc = mid(ab, bc);
            let bcd = mid(bc, cd);
            let abcd = mid(abc, bcd);
            stack.push((abcd, bcd, cd, d, depth + 1));
            stack.push((a, ab, abc, abcd, depth + 1));
            ensure_flatten_budget(&flattened, &stack, out, limits)?;
        }
    }
    drop(stack);
    reserve_flatten_output(out, &flattened, limits)?;
    out.extend(flattened);
    Ok(())
}

fn validate_curve_points(points: &[Point], limits: Limits) -> Result<(), CoverageError> {
    if !limits.max_coordinate_abs.is_finite() || limits.max_coordinate_abs <= 0.0 {
        return Err(CoverageError::InvalidCoordinate);
    }
    for point in points {
        if !point.x.is_finite()
            || !point.y.is_finite()
            || point.x.abs() > limits.max_coordinate_abs
            || point.y.abs() > limits.max_coordinate_abs
        {
            return Err(CoverageError::InvalidCoordinate);
        }
    }
    Ok(())
}

fn new_flatten_stack<T>(out: &Vec<Point>, limits: Limits) -> Result<Vec<T>, CoverageError> {
    let required = (limits.max_curve_depth as usize)
        .checked_add(1)
        .ok_or(CoverageError::CurveLimit)?;
    let bytes = out
        .capacity()
        .saturating_mul(core::mem::size_of::<Point>())
        .saturating_add(required.saturating_mul(core::mem::size_of::<T>()));
    if bytes > limits.max_intermediate_bytes || bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    let mut stack = Vec::new();
    stack
        .try_reserve_exact(required)
        .map_err(|_| CoverageError::AllocationFailure)?;
    if out
        .capacity()
        .saturating_mul(core::mem::size_of::<Point>())
        .saturating_add(stack.capacity().saturating_mul(core::mem::size_of::<T>()))
        > limits.max_intermediate_bytes
        || out
            .capacity()
            .saturating_mul(core::mem::size_of::<Point>())
            .saturating_add(stack.capacity().saturating_mul(core::mem::size_of::<T>()))
            > limits.max_total_bytes
    {
        return Err(CoverageError::ActiveWorkLimit);
    }
    Ok(stack)
}

fn push_flattened<T>(
    flattened: &mut Vec<Point>,
    point: Point,
    stack: &Vec<T>,
    out: &Vec<Point>,
    limits: Limits,
) -> Result<(), CoverageError> {
    if out
        .len()
        .checked_add(flattened.len())
        .and_then(|count| count.checked_add(1))
        .map_or(true, |count| count > limits.max_edges)
    {
        return Err(CoverageError::EdgeLimit);
    }
    let other_live_bytes = stack
        .capacity()
        .saturating_mul(core::mem::size_of::<T>())
        .saturating_add(out.capacity().saturating_mul(core::mem::size_of::<Point>()));
    ensure_vec_capacity(
        flattened,
        flattened
            .len()
            .checked_add(1)
            .ok_or(CoverageError::EdgeLimit)?,
        other_live_bytes,
        limits,
    )?;
    ensure_flatten_budget(flattened, stack, out, limits)?;
    flattened.push(point);
    Ok(())
}

fn ensure_flatten_budget<T>(
    flattened: &Vec<Point>,
    stack: &Vec<T>,
    out: &Vec<Point>,
    limits: Limits,
) -> Result<(), CoverageError> {
    let bytes = flattened
        .capacity()
        .saturating_mul(core::mem::size_of::<Point>())
        .saturating_add(stack.capacity().saturating_mul(core::mem::size_of::<T>()))
        .saturating_add(out.capacity().saturating_mul(core::mem::size_of::<Point>()));
    if bytes > limits.max_intermediate_bytes || bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    Ok(())
}

fn reserve_flatten_output(
    out: &mut Vec<Point>,
    flattened: &Vec<Point>,
    limits: Limits,
) -> Result<(), CoverageError> {
    let new_len = out
        .len()
        .checked_add(flattened.len())
        .ok_or(CoverageError::EdgeLimit)?;
    if new_len > limits.max_edges {
        return Err(CoverageError::EdgeLimit);
    }
    let element_size = core::mem::size_of::<Point>();
    let flattened_bytes = vec_capacity_bytes(flattened);
    let old_capacity = out.capacity();
    let old_bytes = vec_capacity_bytes(out);
    let current_bytes = old_bytes.saturating_add(flattened_bytes);
    if current_bytes > limits.max_intermediate_bytes || current_bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    if new_len <= out.capacity() {
        return Ok(());
    }
    let capacity_limit = limits
        .max_intermediate_bytes
        .min(limits.max_total_bytes)
        .saturating_sub(flattened_bytes)
        / element_size;
    let replacement_capacity_limit = capacity_limit.saturating_sub(old_capacity);
    if new_len > replacement_capacity_limit {
        return Err(CoverageError::ActiveWorkLimit);
    }
    let doubled_capacity = out
        .capacity()
        .max(4)
        .saturating_mul(2)
        .min(replacement_capacity_limit);
    let projected_capacity = new_len.max(doubled_capacity);
    let projected_bytes = old_capacity
        .saturating_add(projected_capacity)
        .saturating_mul(core::mem::size_of::<Point>())
        .saturating_add(flattened_bytes);
    if projected_bytes > limits.max_intermediate_bytes || projected_bytes > limits.max_total_bytes {
        return Err(CoverageError::ActiveWorkLimit);
    }
    out.try_reserve_exact(projected_capacity.saturating_sub(out.len()))
        .map_err(|_| CoverageError::AllocationFailure)?;
    let actual_peak_bytes = old_capacity
        .saturating_add(out.capacity())
        .saturating_mul(element_size)
        .saturating_add(flattened_bytes);
    if out.capacity() > replacement_capacity_limit
        || actual_peak_bytes > limits.max_intermediate_bytes
        || actual_peak_bytes > limits.max_total_bytes
    {
        return Err(CoverageError::ActiveWorkLimit);
    }
    Ok(())
}

fn mid(a: Point, b: Point) -> Point {
    Point {
        x: a.x * 0.5 + b.x * 0.5,
        y: a.y * 0.5 + b.y * 0.5,
    }
}
fn point_line_distance(p: Point, a: Point, b: Point) -> f64 {
    let dx = b.x - a.x;
    let dy = b.y - a.y;
    let den = dx.hypot(dy);
    if den == 0.0 {
        p.x.distance(a.x).hypot(p.y.distance(a.y))
    } else {
        (dy * p.x - dx * p.y + b.x * a.y - b.y * a.x).abs() / den
    }
}

trait Distance {
    fn distance(self, other: Self) -> f64;
}
impl Distance for f64 {
    fn distance(self, other: Self) -> f64 {
        (self - other).abs()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use alloc::vec;
    use tiny_skia_path::{LineCap, LineJoin, PathBuilder, PathStroker, Stroke};

    fn rect(x0: f64, y0: f64, x1: f64, y1: f64) -> Vec<Point> {
        vec![
            Point { x: x0, y: y0 },
            Point { x: x1, y: y0 },
            Point { x: x1, y: y1 },
            Point { x: x0, y: y1 },
        ]
    }
    fn clip(x0: f64, y0: f64, x1: f64, y1: f64) -> ClipRect {
        ClipRect {
            left: x0,
            top: y0,
            right: x1,
            bottom: y1,
        }
    }
    fn total(spans: &[CoverageSpan]) -> f64 {
        spans
            .iter()
            .map(|s| s.width as f64 * s.alpha as f64 / 255.0)
            .sum()
    }
    fn area_for(spans: &[CoverageSpan], x: i32, y: i32) -> f64 {
        spans
            .iter()
            .filter(|s| s.y == y && x >= s.x && x < s.x + s.width as i32)
            .map(|s| s.alpha as f64 / 255.0)
            .sum()
    }
    fn assert_same_pixels(a: &[CoverageSpan], b: &[CoverageSpan], r: ClipRect) {
        for y in r.top.floor() as i32..r.bottom.ceil() as i32 {
            for x in r.left.floor() as i32..r.right.ceil() as i32 {
                let alpha_a = (area_for(a, x, y) * 255.0).round() as i32;
                let alpha_b = (area_for(b, x, y) * 255.0).round() as i32;
                assert_eq!(alpha_a, alpha_b, "alpha differs at ({x}, {y})");
            }
        }
    }

    // Independent test oracle: clip a triangle to the pixel rectangle with
    // Sutherland-Hodgman, then use its shoelace area. This deliberately does
    // not call the analytic affine-clamp kernel under test.
    fn oracle_clip_half_plane(
        polygon: &[Point],
        axis: usize,
        bound: f64,
        keep_greater: bool,
    ) -> Vec<Point> {
        let coordinate = |point: Point| if axis == 0 { point.x } else { point.y };
        let inside = |point: Point| {
            if keep_greater {
                coordinate(point) >= bound
            } else {
                coordinate(point) <= bound
            }
        };
        let mut clipped = Vec::with_capacity(polygon.len() + 2);
        if polygon.is_empty() {
            return clipped;
        }
        for index in 0..polygon.len() {
            let a = polygon[index];
            let b = polygon[(index + 1) % polygon.len()];
            let a_inside = inside(a);
            let b_inside = inside(b);
            if a_inside && b_inside {
                clipped.push(b);
            } else if a_inside != b_inside {
                let a_value = coordinate(a);
                let b_value = coordinate(b);
                let t = (bound - a_value) / (b_value - a_value);
                clipped.push(Point {
                    x: a.x + t * (b.x - a.x),
                    y: a.y + t * (b.y - a.y),
                });
                if b_inside {
                    clipped.push(b);
                }
            }
        }
        clipped
    }

    fn oracle_triangle_pixel_area(triangle: [Point; 3], x: i32, y: i32) -> f64 {
        let mut polygon = triangle.to_vec();
        for (axis, bound, keep_greater) in [
            (0, x as f64, true),
            (0, x as f64 + 1.0, false),
            (1, y as f64, true),
            (1, y as f64 + 1.0, false),
        ] {
            polygon = oracle_clip_half_plane(&polygon, axis, bound, keep_greater);
            if polygon.len() < 3 {
                return 0.0;
            }
        }
        let mut twice_area = 0.0;
        for index in 0..polygon.len() {
            let a = polygon[index];
            let b = polygon[(index + 1) % polygon.len()];
            twice_area += a.x * b.y - b.x * a.y;
        }
        twice_area.abs() * 0.5
    }

    fn stroked_polyline_outline(scale: f32) -> Path {
        let mut source = PathBuilder::new();
        source.move_to(4.0, 56.0);
        source.line_to(24.0, 8.0);
        source.line_to(44.0, 40.0);
        source.line_to(60.0, 16.0);
        source.line_to(76.0, 56.0);
        let source = source.finish().unwrap();
        let outline = PathStroker::new()
            .stroke(
                &source,
                &Stroke {
                    width: 3.0,
                    line_cap: LineCap::Butt,
                    line_join: LineJoin::Miter,
                    ..Stroke::default()
                },
                1.0,
            )
            .unwrap();
        if scale == 1.0 {
            outline
        } else {
            outline
                .transform(Transform::from_scale(scale, scale))
                .unwrap()
        }
    }

    fn simultaneous_crossing_bowties() -> (Vec<Vec<Point>>, Path) {
        let contours = vec![
            vec![
                Point { x: 2.0, y: 2.0 },
                Point { x: 14.0, y: 14.0 },
                Point { x: 2.0, y: 14.0 },
                Point { x: 14.0, y: 2.0 },
            ],
            vec![
                Point { x: 2.0, y: 5.0 },
                Point { x: 14.0, y: 11.0 },
                Point { x: 5.0, y: 14.0 },
                Point { x: 11.0, y: 2.0 },
            ],
            vec![
                Point { x: 2.0, y: 11.0 },
                Point { x: 14.0, y: 5.0 },
                Point { x: 5.0, y: 2.0 },
                Point { x: 11.0, y: 14.0 },
            ],
        ];
        let mut builder = PathBuilder::new();
        for contour in &contours {
            builder.move_to(contour[0].x as f32, contour[0].y as f32);
            for point in &contour[1..] {
                builder.line_to(point.x as f32, point.y as f32);
            }
            builder.close();
        }
        (contours, builder.finish().unwrap())
    }

    fn outline_contains_nonzero(path: &Path, point: Point) -> bool {
        let mut winding = 0i32;
        let mut start = None;
        let mut previous = None;
        for segment in path.segments() {
            match segment {
                PathSegment::MoveTo(p) => {
                    if let (Some(prev), Some(first)) = (previous, start) {
                        add_winding_crossing(&mut winding, prev, first, point);
                    }
                    start = Some(Point {
                        x: f64::from(p.x),
                        y: f64::from(p.y),
                    });
                    previous = start;
                }
                PathSegment::LineTo(p) => {
                    let next = Point {
                        x: f64::from(p.x),
                        y: f64::from(p.y),
                    };
                    if let Some(prev) = previous {
                        add_winding_crossing(&mut winding, prev, next, point);
                    }
                    previous = Some(next);
                }
                PathSegment::Close => {
                    if let (Some(prev), Some(first)) = (previous, start) {
                        add_winding_crossing(&mut winding, prev, first, point);
                    }
                    start = None;
                    previous = None;
                }
                PathSegment::QuadTo(_, _) | PathSegment::CubicTo(_, _, _) => {
                    panic!("mitered line-only stroke outline unexpectedly contains a curve")
                }
            }
        }
        if let (Some(prev), Some(first)) = (previous, start) {
            add_winding_crossing(&mut winding, prev, first, point);
        }
        winding != 0
    }

    fn add_winding_crossing(winding: &mut i32, a: Point, b: Point, p: Point) {
        if a.y <= p.y && b.y > p.y {
            if (b.x - a.x) * (p.y - a.y) - (p.x - a.x) * (b.y - a.y) > 0.0 {
                *winding += 1;
            }
        } else if b.y <= p.y
            && a.y > p.y
            && (b.x - a.x) * (p.y - a.y) - (p.x - a.x) * (b.y - a.y) < 0.0
        {
            *winding -= 1;
        }
    }

    #[test]
    fn stroked_polyline_outline_does_not_fill_between_segments() {
        for scale in [1.0f32, 1.25, 3.0] {
            let outline = stroked_polyline_outline(scale);
            let outside = Point {
                x: 25.5 * f64::from(scale),
                y: 30.5 * f64::from(scale),
            };
            assert!(
                !outline_contains_nonzero(&outline, outside),
                "test point unexpectedly lies inside the stroked outline at scale {scale}"
            );
            let width = (80.0 * scale).ceil();
            let height = (60.0 * scale).ceil();
            let spans = rasterize_path(
                &outline,
                FillRule::NonZero,
                ClipRect {
                    left: 0.0,
                    top: 0.0,
                    right: f64::from(width),
                    bottom: f64::from(height),
                },
                Transform::identity(),
                [0.0, 0.0],
                Limits::default().max_total_bytes,
            )
            .unwrap();
            let pixel_x = (outside.x.floor() as i32).clamp(0, width as i32 - 1);
            let pixel_y = (outside.y.floor() as i32).clamp(0, height as i32 - 1);
            let alpha = area_for(&spans, pixel_x, pixel_y);
            assert!(
                alpha < 0.1,
                "analytic scan filled between stroked polyline segments at scale {scale}: alpha={alpha}"
            );
        }
    }

    #[test]
    fn treap_insert_remove_rank_smoke() {
        let mut t = OrderTree::try_new(8).unwrap();
        for id in 0..5 {
            t.insert_at(id, t.len());
        }
        assert_eq!(t.len(), 5);
        assert_eq!(t.in_order(), vec![0, 1, 2, 3, 4]);
        for id in 0..5 {
            let r = t.rank_node(t.by_edge[id].unwrap());
            assert_eq!(t.edge_at(r), Some(id));
        }
        t.remove(2);
        assert_eq!(t.len(), 4);
        assert_eq!(t.in_order(), vec![0, 1, 3, 4]);
        t.insert_at(2, 1);
        assert_eq!(t.in_order(), vec![0, 2, 1, 3, 4]);
    }

    #[test]
    fn axis_aligned_integer_rectangle_has_exact_area() {
        let s = rasterize(
            &[rect(1.0, 1.0, 4.0, 3.0)],
            FillRule::NonZero,
            clip(0.0, 0.0, 8.0, 8.0),
            Limits::default(),
        )
        .unwrap();
        assert!((total(&s) - 6.0).abs() < 0.02);
        assert_eq!(s.len(), 2);
    }
    #[test]
    fn fractional_rectangle_is_analytic_and_clipped() {
        let s = rasterize(
            &[rect(0.25, 0.25, 1.25, 1.25)],
            FillRule::NonZero,
            clip(0.0, 0.0, 4.0, 4.0),
            Limits::default(),
        )
        .unwrap();
        assert!((total(&s) - 1.0).abs() < 0.025);
        assert!((area_for(&s, 0, 0) - 0.5625).abs() < 0.02);
    }
    #[test]
    fn fractional_root_clip_contributes_exact_area() {
        let s = rasterize(
            &[rect(-10.0, -10.0, 10.0, 10.0)],
            FillRule::NonZero,
            clip(0.25, 0.5, 1.75, 2.0),
            Limits::default(),
        )
        .unwrap();
        assert!((total(&s) - 2.25).abs() < 0.04);
    }
    #[test]
    fn evenodd_hole_and_opposite_winding_nonzero_hole() {
        let outer = rect(0.0, 0.0, 6.0, 6.0);
        let mut inner = rect(1.0, 1.0, 5.0, 5.0);
        inner.reverse();
        for rule in [FillRule::EvenOdd, FillRule::NonZero] {
            let s = rasterize(
                &[outer.clone(), inner.clone()],
                rule,
                clip(0.0, 0.0, 8.0, 8.0),
                Limits::default(),
            )
            .unwrap();
            assert!((total(&s) - 20.0).abs() < 0.05);
        }
    }
    #[test]
    fn same_winding_nested_contour_is_filled_for_nonzero_and_hole_for_evenodd() {
        let outer = rect(0.0, 0.0, 6.0, 6.0);
        let inner = rect(1.0, 1.0, 5.0, 5.0);
        let nz = rasterize(
            &[outer.clone(), inner.clone()],
            FillRule::NonZero,
            clip(0.0, 0.0, 8.0, 8.0),
            Limits::default(),
        )
        .unwrap();
        let eo = rasterize(
            &[outer, inner],
            FillRule::EvenOdd,
            clip(0.0, 0.0, 8.0, 8.0),
            Limits::default(),
        )
        .unwrap();
        assert!((total(&nz) - 36.0).abs() < 0.05);
        assert!((total(&eo) - 20.0).abs() < 0.05);
    }

    #[test]
    fn dense_rows_match_sparse_events_for_holes_crossings_and_fractional_clips() {
        let mut opposite = rect(1.0, 1.0, 8.0, 8.0);
        opposite.reverse();
        let same = rect(2.25, 2.5, 6.75, 7.0);
        let bow_tie = vec![
            Point { x: -1.0, y: 0.25 },
            Point { x: 7.75, y: 8.5 },
            Point { x: 0.5, y: 8.25 },
            Point { x: 8.5, y: 0.5 },
        ];
        let cases = [
            (
                vec![rect(-2.0, -1.0, 9.0, 10.0), opposite.clone()],
                FillRule::EvenOdd,
            ),
            (
                vec![rect(-2.0, -1.0, 9.0, 10.0), opposite],
                FillRule::NonZero,
            ),
            (vec![rect(-2.0, -1.0, 9.0, 10.0), same], FillRule::EvenOdd),
            (vec![bow_tie], FillRule::NonZero),
        ];
        let clip_rect = clip(-0.375, -0.625, 9.25, 10.375);
        for (case_index, (contours, rule)) in cases.into_iter().enumerate() {
            let dense =
                rasterize_with_base_mode(&contours, rule, clip_rect, Limits::default(), 0, true)
                    .unwrap_or_else(|error| panic!("dense case {case_index} {rule:?}: {error:?}"));
            let sparse =
                rasterize_with_base_mode(&contours, rule, clip_rect, Limits::default(), 0, false)
                    .unwrap();
            assert_same_pixels(&dense, &sparse, clip_rect);
        }
    }

    #[test]
    fn dense_row_allocation_is_pre_admitted_and_large_width_uses_sparse_path() {
        let mut too_small = Limits::default();
        too_small.max_intermediate_bytes = 1024;
        too_small.max_total_bytes = 1024;
        let mut accumulator = CoverageAccumulator::default();
        assert!(!accumulator.select_dense(0, 100, 4, too_small, 1024));
        assert!(accumulator.dense.is_none());
        assert!(accumulator.events.is_empty());
        assert!(accumulator.spans.is_empty());

        let mut sparse_limits = Limits::default();
        sparse_limits.max_coverage_events = 64;
        let shape = [rect(1000.0, 1.0, 2000.0, 2.0)];
        let spans = rasterize_with_base_mode(
            &shape,
            FillRule::NonZero,
            clip(0.0, 0.0, 3000.0, 4.0),
            sparse_limits,
            0,
            true,
        )
        .unwrap();
        assert_eq!(
            spans,
            vec![CoverageSpan {
                x: 1000,
                y: 1,
                width: 1000,
                alpha: 255
            }]
        );

        let narrow_clip = clip(0.0, 0.0, 4.0, 4.0);
        let far_offscreen = [rect(-1_000_000.0, 1.0, 2.0, 3.0)];
        let edges = build_edges(&far_offscreen, narrow_clip, Limits::default(), 0).unwrap();
        assert_eq!(dense_bounds(&edges, narrow_clip), Some((0, 2, 2)));
        let dense = rasterize_with_base_mode(
            &far_offscreen,
            FillRule::NonZero,
            narrow_clip,
            Limits::default(),
            0,
            true,
        )
        .unwrap();
        let sparse = rasterize_with_base_mode(
            &far_offscreen,
            FillRule::NonZero,
            narrow_clip,
            Limits::default(),
            0,
            false,
        )
        .unwrap();
        assert_same_pixels(&dense, &sparse, narrow_clip);
    }

    #[test]
    fn dense_rows_skip_empty_clip_bands_before_distant_geometry() {
        let shape = [rect(1.0, 100_000.0, 3.0, 100_001.0)];
        let mut limits = Limits::default();
        limits.max_active_edge_visits = 32;
        let spans = rasterize_with_base_mode(
            &shape,
            FillRule::NonZero,
            clip(0.0, 0.0, 8.0, 100_002.0),
            limits,
            0,
            true,
        )
        .unwrap();
        assert_eq!(
            spans,
            vec![CoverageSpan {
                x: 1,
                y: 100_000,
                width: 2,
                alpha: 255,
            }]
        );
    }

    #[test]
    fn self_intersecting_bow_tie_is_split_at_crossing() {
        let bow = vec![
            Point { x: 0.0, y: 0.0 },
            Point { x: 4.0, y: 4.0 },
            Point { x: 0.0, y: 4.0 },
            Point { x: 4.0, y: 0.0 },
        ];
        let s = rasterize(
            &[bow],
            FillRule::EvenOdd,
            clip(0.0, 0.0, 5.0, 5.0),
            Limits::default(),
        )
        .unwrap();
        assert!((total(&s) - 8.0).abs() < 0.08);
    }

    #[test]
    fn circle_crossing_the_top_clip_boundary_is_valid_at_integer_scales() {
        // Match usvg's four-cubic conversion, including the tiny
        // trigonometric endpoint residuals that distinguish it from the
        // exact-cardinal PathBuilder circle.
        let mut centered = PathBuilder::new();
        centered.move_to(8.0, 0.0);
        centered.cubic_to(8.0, 4.418278, 4.418278, 8.0, 4.8985874e-16, 8.0);
        centered.cubic_to(-4.418278, 8.0, -8.0, 4.418278, -8.0, 9.797175e-16);
        centered.cubic_to(-8.0, -4.418278, -4.418278, -8.0, -1.4695761e-15, -8.0);
        centered.cubic_to(4.418278, -8.0, 8.0, -4.418278, 8.0, 0.0);
        centered.close();
        let centered = centered.finish().unwrap();

        let mut offset = PathBuilder::new();
        offset.move_to(16.0, 0.0);
        offset.cubic_to(16.0, 4.418278, 12.418278, 8.0, 8.0, 8.0);
        offset.cubic_to(3.581722, 8.0, 0.0, 4.418278, 0.0, 9.797175e-16);
        offset.cubic_to(0.0, -4.418278, 3.581722, -8.0, 8.0, -8.0);
        offset.cubic_to(12.418278, -8.0, 16.0, -4.418278, 16.0, 0.0);
        offset.close();
        let offset = offset.finish().unwrap();

        for (name, path) in [("centered", centered), ("offset", offset)] {
            for scale in [1.0, 4.0] {
                let dimension = 512.0 * scale;
                let spans = rasterize_path(
                    &path,
                    FillRule::NonZero,
                    clip(0.0, 0.0, dimension, dimension),
                    Transform::from_scale(scale as f32, scale as f32),
                    [0.0, 0.0],
                    16 * 1024 * 1024,
                )
                .unwrap_or_else(|error| {
                    panic!("{name} circle at top clip was rejected at {scale}x: {error:?}")
                });
                assert!(!spans.is_empty(), "{name} circle at {scale}x was blank");
            }
        }
    }

    #[test]
    fn same_group_started_edge_ending_at_crossing_is_not_reinserted() {
        let crossing = vec![
            Point { x: 1.0, y: 1.0 },
            Point { x: 9.0, y: 9.0 },
            Point { x: 1.0, y: 9.0 },
            Point { x: 9.0, y: 1.0 },
        ];
        let micro_edge = vec![
            Point { x: 5.0, y: 5.0 },
            Point {
                x: 5.000001,
                y: 5.0 + 4.0 * f64::EPSILON,
            },
            Point { x: 6.0, y: 8.0 },
        ];
        let collapsed_edge = vec![
            Point { x: 5.0, y: 5.0 },
            Point {
                x: 5.000001,
                y: 5.0,
            },
            Point { x: 6.0, y: 8.0 },
        ];
        let actual = rasterize(
            &[crossing.clone(), micro_edge],
            FillRule::NonZero,
            clip(0.0, 0.0, 10.0, 10.0),
            Limits::default(),
        )
        .unwrap();
        let expected = rasterize(
            &[crossing, collapsed_edge],
            FillRule::NonZero,
            clip(0.0, 0.0, 10.0, 10.0),
            Limits::default(),
        )
        .unwrap();
        assert_eq!(actual, expected);
    }

    #[test]
    fn simultaneous_multi_edge_crossings_preserve_nonzero_area() {
        let (contours, path) = simultaneous_crossing_bowties();
        let output = rasterize(
            &contours,
            FillRule::NonZero,
            clip(0.0, 0.0, 16.0, 16.0),
            Limits::default(),
        )
        .unwrap();

        // Independent midpoint integration over the original contours checks
        // the combined winding on both sides of the shared three-way crossing.
        const SAMPLES_PER_AXIS: usize = 256;
        let sample_step = 16.0 / SAMPLES_PER_AXIS as f64;
        let mut covered_samples = 0usize;
        for y in 0..SAMPLES_PER_AXIS {
            for x in 0..SAMPLES_PER_AXIS {
                let point = Point {
                    x: (x as f64 + 0.5) * sample_step,
                    y: (y as f64 + 0.5) * sample_step,
                };
                if outline_contains_nonzero(&path, point) {
                    covered_samples += 1;
                }
            }
        }
        let expected = covered_samples as f64 * sample_step * sample_step;
        let measured = total(&output);
        assert!(
            (measured - expected).abs() < 0.12,
            "simultaneous crossing area mismatch: measured={measured}, sampled={expected}"
        );
    }

    #[test]
    fn many_distinct_same_scanline_crossings_have_bounded_seed_work_and_correct_area() {
        const CROSSING_GROUPS: usize = 128;
        let contours: Vec<Vec<Point>> = (0..CROSSING_GROUPS)
            .map(|index| {
                let x = (index * 8) as f64;
                vec![
                    Point { x, y: 5.0 },
                    Point {
                        x: x + 4.0,
                        y: 25.0,
                    },
                    Point { x, y: 25.0 },
                    Point { x: x + 4.0, y: 5.0 },
                ]
            })
            .collect();
        let mut limits = Limits::default();
        // This admits the linear sweep and per-group neighborhood visits but
        // is far below the former G×S seed-list scan count.
        limits.max_active_edge_visits = 4096;
        let output = rasterize(
            &contours,
            FillRule::NonZero,
            clip(0.0, 0.0, (CROSSING_GROUPS * 8) as f64, 30.0),
            limits,
        )
        .unwrap();

        // The unquantized geometry is exactly 40 square pixels per bow tie,
        // but a sum of byte-quantized pixel coverages can differ from that
        // continuous area by as much as half an alpha quantum per partial
        // pixel. Compare every output pixel to an independent exact-area
        // oracle instead of hiding that quantization error in an arbitrary
        // aggregate tolerance.
        let mut expected_area = 0.0;
        for index in 0..CROSSING_GROUPS {
            let x0 = (index * 8) as f64;
            let lobes = [
                [
                    Point { x: x0, y: 5.0 },
                    Point {
                        x: x0 + 2.0,
                        y: 15.0,
                    },
                    Point {
                        x: x0 + 4.0,
                        y: 5.0,
                    },
                ],
                [
                    Point {
                        x: x0 + 2.0,
                        y: 15.0,
                    },
                    Point {
                        x: x0 + 4.0,
                        y: 25.0,
                    },
                    Point { x: x0, y: 25.0 },
                ],
            ];
            for y in 5..25 {
                for x in (index * 8) as i32..(index * 8 + 4) as i32 {
                    let expected_pixel = lobes
                        .iter()
                        .map(|triangle| oracle_triangle_pixel_area(*triangle, x, y))
                        .sum::<f64>();
                    expected_area += expected_pixel;
                    let actual_pixel = area_for(&output, x, y);
                    let quantization_bound = 0.5 / 255.0 + 1.0e-8;
                    assert!(
                        (actual_pixel - expected_pixel).abs() <= quantization_bound,
                        "distinct same-scanline crossing pixel mismatch at ({x},{y}): actual={actual_pixel}, exact={expected_pixel}"
                    );
                }
            }
        }
        assert!(
            (expected_area - CROSSING_GROUPS as f64 * 40.0).abs() < 1.0e-8,
            "independent bow-tie oracle area mismatch: {expected_area}"
        );
    }

    #[test]
    fn crossing_seed_ranges_charge_one_bounded_pass_over_many_groups() {
        const GROUP_COUNT: usize = 256;
        let seed_count = GROUP_COUNT * 2;
        let mut tree = OrderTree::try_new(seed_count).unwrap();
        for edge in 0..seed_count {
            tree.insert_at(edge, tree.len());
        }

        let mut groups: Vec<CrossingGroup> = (0..GROUP_COUNT)
            .map(|index| CrossingGroup {
                x: index as f64,
                seed_lo: None,
                seed_hi: None,
            })
            .collect();
        let mut seeds = Vec::new();
        seeds.try_reserve_exact(seed_count).unwrap();
        for group in 0..GROUP_COUNT {
            seeds.push(CrossingEntry {
                group,
                edge: group * 2,
            });
            seeds.push(CrossingEntry {
                group,
                edge: group * 2 + 1,
            });
        }

        let mut limits = Limits::default();
        limits.max_active_edge_visits = seed_count as u64;
        let mut visits = 0;
        record_crossing_seed_ranges(&seeds, &mut groups, &tree, &mut visits, limits).unwrap();
        assert_eq!(visits, seed_count as u64);
        for (index, group) in groups.iter().enumerate() {
            assert_eq!(group.seed_lo, Some(index * 2));
            assert_eq!(group.seed_hi, Some(index * 2 + 1));
        }

        let mut groups: Vec<CrossingGroup> = (0..GROUP_COUNT)
            .map(|index| CrossingGroup {
                x: index as f64,
                seed_lo: None,
                seed_hi: None,
            })
            .collect();
        limits.max_active_edge_visits -= 1;
        visits = 0;
        assert_eq!(
            record_crossing_seed_ranges(&seeds, &mut groups, &tree, &mut visits, limits),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(visits, seed_count as u64);
    }

    #[test]
    fn coincident_and_horizontal_edges_do_not_create_nan() {
        let r = rect(0.0, 0.0, 3.0, 3.0);
        let s = rasterize(
            &[r.clone(), r],
            FillRule::EvenOdd,
            clip(0.0, 0.0, 4.0, 4.0),
            Limits::default(),
        )
        .unwrap();
        assert!(s.is_empty());
    }
    #[test]
    fn integer_row_crossing_and_tie_is_stable() {
        let p = vec![
            Point { x: 0.0, y: 0.0 },
            Point { x: 4.0, y: 4.0 },
            Point { x: 0.0, y: 4.0 },
            Point { x: 4.0, y: 0.0 },
        ];
        let a = rasterize(
            &[p.clone()],
            FillRule::EvenOdd,
            clip(0.0, 0.0, 5.0, 5.0),
            Limits::default(),
        )
        .unwrap();
        let b = rasterize(
            &[p],
            FillRule::EvenOdd,
            clip(0.0, 0.0, 5.0, 5.0),
            Limits::default(),
        )
        .unwrap();
        assert_eq!(a, b);
    }
    #[test]
    fn negative_coordinates_are_clipped_without_truncation() {
        let s = rasterize(
            &[rect(-1.5, -1.5, 0.5, 0.5)],
            FillRule::NonZero,
            clip(0.0, 0.0, 3.0, 3.0),
            Limits::default(),
        )
        .unwrap();
        assert!((total(&s) - 0.25).abs() < 0.02);
    }
    #[test]
    fn massive_finite_coordinates_fail_before_emission() {
        let mut lim = Limits::default();
        lim.max_coordinate_abs = 1e6;
        let result = rasterize(
            &[rect(-1e12, -1.0, 1e12, 2.0)],
            FillRule::NonZero,
            clip(0.0, 0.0, 4.0, 4.0),
            lim,
        );
        assert_eq!(result, Err(CoverageError::InvalidCoordinate));
    }
    #[test]
    fn empty_clip_has_no_output() {
        let s = rasterize(
            &[rect(0.0, 0.0, 3.0, 3.0)],
            FillRule::NonZero,
            clip(2.0, 2.0, 2.0, 4.0),
            Limits::default(),
        )
        .unwrap();
        assert!(s.is_empty());
    }
    #[test]
    fn translation_by_integer_pixels_preserves_alpha_and_area() {
        let a = rasterize(
            &[rect(0.25, 0.5, 3.75, 2.25)],
            FillRule::NonZero,
            clip(-1.0, -1.0, 8.0, 8.0),
            Limits::default(),
        )
        .unwrap();
        let b = rasterize(
            &[rect(2.25, 3.5, 5.75, 5.25)],
            FillRule::NonZero,
            clip(1.0, 2.0, 10.0, 10.0),
            Limits::default(),
        )
        .unwrap();
        assert!((total(&a) - total(&b)).abs() < 0.03);
    }
    #[test]
    fn tiled_and_whole_clip_have_same_overlap_pixels() {
        let polygon = vec![
            Point { x: 0.2, y: 0.1 },
            Point { x: 5.7, y: 1.4 },
            Point { x: 4.8, y: 4.9 },
            Point { x: 0.1, y: 3.2 },
        ];
        let whole = rasterize(
            &[polygon.clone()],
            FillRule::NonZero,
            clip(0.0, 0.0, 6.0, 5.0),
            Limits::default(),
        )
        .unwrap();
        let left = rasterize(
            &[polygon.clone()],
            FillRule::NonZero,
            clip(0.0, 0.0, 3.0, 5.0),
            Limits::default(),
        )
        .unwrap();
        let right = rasterize(
            &[polygon],
            FillRule::NonZero,
            clip(3.0, 0.0, 6.0, 5.0),
            Limits::default(),
        )
        .unwrap();
        for y in 0..5 {
            for x in 0..6 {
                let combined = if x < 3 {
                    area_for(&left, x, y)
                } else {
                    area_for(&right, x, y)
                };
                assert!(
                    (area_for(&whole, x, y) - combined).abs() < 0.025,
                    "x={x} y={y}"
                );
            }
        }
    }
    #[test]
    fn curve_flattening_is_adaptive_and_bounded() {
        let mut out = vec![Point { x: 0.0, y: 0.0 }];
        flatten_cubic(
            Point { x: 0.0, y: 0.0 },
            Point { x: 0.0, y: 8.0 },
            Point { x: 8.0, y: 8.0 },
            Point { x: 8.0, y: 0.0 },
            Limits::default(),
            &mut out,
        )
        .unwrap();
        assert!(out.len() > 2);
        let mut lim = Limits::default();
        lim.max_curve_depth = 0;
        let mut tiny = vec![Point { x: 0.0, y: 0.0 }];
        assert_eq!(
            flatten_cubic(
                Point { x: 0.0, y: 0.0 },
                Point { x: 0.0, y: 8.0 },
                Point { x: 8.0, y: 8.0 },
                Point { x: 8.0, y: 0.0 },
                lim,
                &mut tiny
            ),
            Err(CoverageError::CurveLimit)
        );
    }

    #[test]
    fn small_circle_flatness_stays_below_alpha_fidelity_budget() {
        let center = Point { x: 8.0, y: 8.0 };
        let radius = 6.0;
        let k = 0.552_284_749_830_793_6 * radius;
        let p0 = Point {
            x: center.x + radius,
            y: center.y,
        };
        let mut contour = vec![p0];
        let arcs = [
            (
                p0,
                Point {
                    x: center.x + radius,
                    y: center.y + k,
                },
                Point {
                    x: center.x + k,
                    y: center.y + radius,
                },
                Point {
                    x: center.x,
                    y: center.y + radius,
                },
            ),
            (
                Point {
                    x: center.x,
                    y: center.y + radius,
                },
                Point {
                    x: center.x - k,
                    y: center.y + radius,
                },
                Point {
                    x: center.x - radius,
                    y: center.y + k,
                },
                Point {
                    x: center.x - radius,
                    y: center.y,
                },
            ),
            (
                Point {
                    x: center.x - radius,
                    y: center.y,
                },
                Point {
                    x: center.x - radius,
                    y: center.y - k,
                },
                Point {
                    x: center.x - k,
                    y: center.y - radius,
                },
                Point {
                    x: center.x,
                    y: center.y - radius,
                },
            ),
            (
                Point {
                    x: center.x,
                    y: center.y - radius,
                },
                Point {
                    x: center.x + k,
                    y: center.y - radius,
                },
                Point {
                    x: center.x + radius,
                    y: center.y - k,
                },
                p0,
            ),
        ];
        let mut limits = Limits::default();
        limits.curve_error_px = 1.0 / 1024.0;
        for (a, b, c, d) in arcs {
            flatten_cubic(a, b, c, d, limits, &mut contour).unwrap();
        }
        let output = rasterize(
            &[contour],
            FillRule::NonZero,
            clip(0.0, 0.0, 16.0, 16.0),
            limits,
        )
        .unwrap();
        let measured = total(&output);
        let expected = std::f64::consts::PI * radius * radius;
        assert!(
            (measured - expected).abs() / expected < 0.005,
            "measured={measured}, expected={expected}"
        );
    }

    #[test]
    fn deterministic_random_simple_polygons_match_shoelace_area() {
        let mut seed = 0xD1B5_4A32_D192_ED03u64;
        let mut random = || {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            (seed >> 11) as f64 / ((1u64 << 53) as f64)
        };
        for case in 0..80 {
            let count = 5 + case % 16;
            let mut points = Vec::with_capacity(count);
            for i in 0..count {
                let angle = std::f64::consts::TAU * i as f64 / count as f64;
                let radius = 5.0 + random() * 8.0;
                points.push(Point {
                    x: 16.0 + radius * angle.cos(),
                    y: 16.0 + radius * angle.sin(),
                });
            }
            let mut twice_area = 0.0;
            for i in 0..points.len() {
                let a = points[i];
                let b = points[(i + 1) % points.len()];
                twice_area += a.x * b.y - b.x * a.y;
            }
            let expected = twice_area.abs() * 0.5;
            let rendered = rasterize(
                &[points],
                FillRule::NonZero,
                clip(0.0, 0.0, 32.0, 32.0),
                Limits::default(),
            )
            .unwrap();
            let measured = total(&rendered);
            assert!(
                (measured - expected).abs() < 0.5,
                "case={case}, measured={measured}, expected={expected}"
            );
        }
    }

    #[test]
    fn full_opaque_spans_are_batched_without_partial_pixel_work() {
        let mut limits = Limits::default();
        limits.max_partial_pixel_intersections = 0;
        let spans = rasterize(
            &[rect(1.0, 0.0, 1000.0, 1.0)],
            FillRule::NonZero,
            clip(0.0, 0.0, 1024.0, 1.0),
            limits,
        )
        .unwrap();
        assert_eq!(
            spans,
            vec![CoverageSpan {
                x: 1,
                y: 0,
                width: 999,
                alpha: 255
            }]
        );
    }

    #[test]
    fn tall_rect_uses_geometry_events_not_one_event_per_output_row() {
        let mut limits = Limits::default();
        limits.max_events = 8;
        limits.max_active_edge_visits = 2;
        let spans = rasterize(
            &[rect(1.0, 0.0, 2.0, 10_000.0)],
            FillRule::NonZero,
            clip(0.0, 0.0, 4.0, 10_000.0),
            limits,
        )
        .unwrap();
        assert_eq!(spans.len(), 10_000);
        assert_eq!(total(&spans), 10_000.0);
    }

    #[test]
    fn coverage_event_budget_is_checked_before_first_vector_allocation() {
        let mut coverage = CoverageAccumulator::default();
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = 512;
        let error = coverage.add_range(0, 0, 4, 1.0, limits, 512).unwrap_err();
        assert_eq!(error, CoverageError::ActiveWorkLimit);
        assert!(coverage.events.is_empty());
        assert_eq!(coverage.event_count, 0);
        assert_eq!(coverage.reserved_bytes, 0);
        assert_eq!(coverage.events.capacity(), 0);
    }

    #[test]
    fn coverage_event_count_limit_is_checked_before_reservation() {
        let mut coverage = CoverageAccumulator::default();
        let mut limits = Limits::default();
        limits.max_coverage_events = 1;
        assert_eq!(
            coverage.add_range(0, 0, 4, 1.0, limits, 0),
            Err(CoverageError::EventLimit)
        );
        assert_eq!(coverage.events.capacity(), 0);
        assert_eq!(coverage.event_count, 0);
    }

    #[test]
    fn repeated_breakpoints_compact_in_place_before_the_pending_event_cap() {
        let mut coverage = CoverageAccumulator::default();
        let mut limits = Limits::default();
        limits.max_coverage_events = 8;

        for _ in 0..100 {
            coverage.add_range(0, 2, 9, 0.01, limits, 0).unwrap();
            assert!(coverage.event_count <= limits.max_coverage_events);
        }

        assert!(coverage.compaction_work > 0);
        assert!(coverage.event_count <= limits.max_coverage_events);
        let spans = coverage.finish(limits, 0).unwrap();
        assert_eq!(
            spans,
            vec![CoverageSpan {
                x: 2,
                y: 0,
                width: 7,
                alpha: 255,
            }]
        );
    }

    #[test]
    fn breakpoint_compaction_work_is_charged_before_sorting() {
        let mut coverage = CoverageAccumulator::default();
        let mut limits = Limits::default();
        limits.max_coverage_events = 2;
        limits.max_active_edge_visits = 1;
        coverage.add_range(0, 2, 9, 0.5, limits, 0).unwrap();
        let before = coverage.events.clone();

        assert_eq!(
            coverage.add_range(0, 2, 9, 0.5, limits, 0),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(coverage.events, before);
        assert_eq!(coverage.compaction_work, 0);
    }

    #[test]
    fn compacted_breakpoint_deltas_preserve_zero_crossing_reduction() {
        let mut coverage = CoverageAccumulator::default();
        let mut limits = Limits::default();
        limits.max_coverage_events = 4;
        coverage.add_range(0, 0, 2, 0.5, limits, 0).unwrap();
        coverage.add_range(0, 2, 4, 0.5, limits, 0).unwrap();
        coverage.add_range(0, 0, 2, 0.5, limits, 0).unwrap();

        // Compaction merges the duplicated start/end records, including the
        // opposite deltas at x=2, without materializing a dense row plane.
        assert!(coverage.event_count <= limits.max_coverage_events);
        let spans = coverage.finish(limits, 0).unwrap();
        assert!((total(&spans) - 3.0).abs() < 0.01);
        assert_eq!(spans.len(), 2);
        assert_eq!(spans[0].alpha, 255);
        assert_eq!(spans[1].alpha, 128);
    }

    #[test]
    fn band_events_can_arrive_pair_major_and_rows_flush_only_when_complete() {
        let mut coverage = CoverageAccumulator::default();
        let limits = Limits::default();

        // One trapezoid pair may cover a lower row before a later pair emits
        // events for an upper row in the same sweep band. The row-local reducer
        // must sort by (y, x), not assume add order is scanline order.
        coverage.add_range(1, 4, 6, 0.5, limits, 0).unwrap();
        coverage.add_range(0, 2, 5, 1.0, limits, 0).unwrap();
        coverage.flush_completed_rows(1, limits, 0).unwrap();

        assert_eq!(coverage.events.len(), 2);
        assert!(coverage.events.iter().all(|event| event.y == 1));
        assert_eq!(coverage.spans.len(), 1);
        assert_eq!(
            coverage.spans[0],
            CoverageSpan {
                x: 2,
                y: 0,
                width: 3,
                alpha: 255
            }
        );

        let spans = coverage.finish(limits, 0).unwrap();
        assert_eq!(
            spans,
            vec![
                CoverageSpan {
                    x: 2,
                    y: 0,
                    width: 3,
                    alpha: 255
                },
                CoverageSpan {
                    x: 4,
                    y: 1,
                    width: 2,
                    alpha: 128
                },
            ]
        );
    }

    #[test]
    fn row_flush_does_not_reset_cumulative_full_run_work_limit() {
        let mut coverage = CoverageAccumulator::default();
        let mut limits = Limits::default();
        limits.max_spans = 1;

        coverage.add_range(0, 0, 1, 1.0, limits, 0).unwrap();
        coverage.flush_completed_rows(1, limits, 0).unwrap();
        assert_eq!(coverage.event_count, 0);
        assert_eq!(coverage.total_range_count, 1);

        assert_eq!(
            coverage.add_range(1, 0, 1, 1.0, limits, 0),
            Err(CoverageError::SpanLimit)
        );
        assert_eq!(coverage.total_range_count, 1);
    }

    #[test]
    fn zero_coverage_full_runs_still_consume_cumulative_work_budget() {
        let mut coverage = CoverageAccumulator::default();
        let mut limits = Limits::default();
        limits.max_spans = 1;

        // These events quantize to no visible output, but remain scan work;
        // otherwise repeated flushes would bypass every cumulative bound.
        coverage.add_range(0, 0, 1, 1.0e-9, limits, 0).unwrap();
        assert_eq!(
            coverage.add_range(0, 2, 3, 1.0e-9, limits, 0),
            Err(CoverageError::SpanLimit)
        );
        assert_eq!(coverage.total_range_count, 1);
    }

    #[test]
    fn sparse_scratch_capacity_is_rejected_before_allocation() {
        let mut values = Vec::<u64>::new();
        let mut limits = Limits::default();
        let other_live = core::mem::size_of::<u64>() * 2;
        limits.max_intermediate_bytes = other_live;
        assert_eq!(
            ensure_vec_capacity(&mut values, 1, other_live, limits),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert!(values.is_empty());
        assert_eq!(values.capacity(), 0);
    }

    #[test]
    fn scratch_growth_admits_old_and_replacement_capacity_before_reserving() {
        let mut values = Vec::<u64>::new();
        values.try_reserve_exact(4).unwrap();
        values.extend([1, 2, 3, 4]);
        let old_capacity = values.capacity();
        let old_len = values.len();
        let mut limits = Limits::default();
        let old_bytes = old_capacity * core::mem::size_of::<u64>();
        limits.max_intermediate_bytes = old_bytes + old_bytes;
        limits.max_total_bytes = old_bytes + old_bytes;

        assert_eq!(
            ensure_vec_capacity(&mut values, old_len + 1, 0, limits),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(values.as_slice(), &[1, 2, 3, 4]);
        assert_eq!(values.capacity(), old_capacity);

        limits.max_intermediate_bytes = old_bytes + old_bytes + core::mem::size_of::<u64>();
        limits.max_total_bytes = limits.max_intermediate_bytes;
        ensure_vec_capacity(&mut values, old_len + 1, 0, limits).unwrap();
        assert!(old_capacity + values.capacity() <= limits.max_total_bytes / 8);
    }

    #[test]
    fn event_vector_growth_admits_old_and_replacement_capacity() {
        let mut coverage = CoverageAccumulator::default();
        coverage.events.try_reserve_exact(4).unwrap();
        coverage.events.extend((0..4).map(|index| CoverageEvent {
            y: index,
            x: 0,
            range_delta: 0.0,
            partial_delta: 0.0,
        }));
        coverage.event_count = coverage.events.len();
        let old_capacity = coverage.events.capacity();
        let old_len = coverage.events.len();
        let element_size = core::mem::size_of::<CoverageEvent>();
        let mut limits = Limits::default();
        let required_peak = (old_capacity + old_len + 1) * element_size;
        limits.max_intermediate_bytes = required_peak - 1;
        limits.max_total_bytes = required_peak - 1;

        assert_eq!(
            coverage.reserve_event_capacity(old_len + 1, limits, 0),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(coverage.events.len(), old_len);
        assert_eq!(coverage.events.capacity(), old_capacity);

        limits.max_intermediate_bytes = required_peak;
        limits.max_total_bytes = required_peak;
        coverage
            .reserve_event_capacity(old_len + 1, limits, 0)
            .unwrap();
        assert!(old_capacity + coverage.events.capacity() <= limits.max_total_bytes / element_size);
    }

    #[test]
    fn event_growth_reserves_retained_spans_against_intermediate_budget_before_allocation() {
        let mut coverage = CoverageAccumulator::default();
        coverage.events.try_reserve_exact(4).unwrap();
        coverage.events.extend((0..4).map(|index| CoverageEvent {
            y: index,
            x: 0,
            range_delta: 0.0,
            partial_delta: 0.0,
        }));
        coverage.event_count = coverage.events.len();
        coverage.spans.try_reserve_exact(4).unwrap();

        let old_event_capacity = coverage.events.capacity();
        let old_event_len = coverage.events.len();
        let output_bytes = vec_capacity_bytes(&coverage.spans);
        let event_size = core::mem::size_of::<CoverageEvent>();
        let required_peak = (old_event_capacity + old_event_len + 1) * event_size + output_bytes;
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = required_peak - 1;
        limits.max_total_bytes = required_peak * 2;
        limits.max_output_bytes = output_bytes;

        assert!(old_event_capacity * event_size + output_bytes < limits.max_intermediate_bytes);
        assert_eq!(
            coverage.reserve_event_capacity(old_event_len + 1, limits, 0),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(coverage.events.len(), old_event_len);
        assert_eq!(coverage.events.capacity(), old_event_capacity);
    }

    #[test]
    fn heap_growth_admits_old_and_replacement_capacity() {
        let mut heap = BinaryHeap::new();
        heap.try_reserve_exact(4).unwrap();
        assert_eq!(heap.capacity(), 4);
        for index in 0..heap.capacity() {
            heap.push(Event {
                y: index as f64,
                x: 0.0,
                kind: EventKind::Clip,
            });
        }
        let old_capacity = heap.capacity();
        let element_size = core::mem::size_of::<Event>();
        let mut event_count = 0;
        let mut limits = Limits::default();
        let required_peak = (old_capacity + old_capacity + 1) * element_size;
        limits.max_intermediate_bytes = required_peak - 1;
        limits.max_total_bytes = required_peak - 1;
        let event = Event {
            y: 10.0,
            x: 0.0,
            kind: EventKind::Clip,
        };

        assert_eq!(
            push_event(&mut heap, event, &mut event_count, limits, 0),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(heap.len(), old_capacity);
        assert_eq!(heap.capacity(), old_capacity);
        assert_eq!(event_count, 0);

        limits.max_intermediate_bytes = required_peak;
        limits.max_total_bytes = required_peak;
        push_event(&mut heap, event, &mut event_count, limits, 0).unwrap();
        assert_eq!(heap.len(), old_capacity + 1);
        assert!(old_capacity + heap.capacity() <= limits.max_total_bytes / element_size);
    }

    #[test]
    fn output_span_growth_admits_old_and_replacement_capacity() {
        let mut spans = Vec::new();
        spans.try_reserve_exact(4).unwrap();
        spans.extend((0..4).map(|index| CoverageSpan {
            x: index,
            y: 0,
            width: 1,
            alpha: 255,
        }));
        let old_capacity = spans.capacity();
        let element_size = core::mem::size_of::<CoverageSpan>();
        let mut limits = Limits::default();
        limits.max_output_bytes = 1024;
        let required_peak = (old_capacity + spans.len() + 1) * element_size;
        limits.max_intermediate_bytes = required_peak - 1;
        limits.max_total_bytes = required_peak - 1;
        let new_span = CoverageSpan {
            x: 5,
            y: 0,
            width: 1,
            alpha: 255,
        };

        assert_eq!(
            push_span(&mut spans, new_span, limits, 0),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(spans.len(), 4);
        assert_eq!(spans.capacity(), old_capacity);

        limits.max_intermediate_bytes = required_peak;
        limits.max_total_bytes = required_peak;
        push_span(&mut spans, new_span, limits, 0).unwrap();
        assert_eq!(spans.len(), 5);
        assert!(old_capacity + spans.capacity() <= limits.max_total_bytes / element_size);
    }

    #[test]
    fn preallocated_scratch_does_not_pay_for_hypothetical_growth() {
        let mut values = Vec::<u64>::new();
        values.try_reserve_exact(4).unwrap();
        let current_allocation = values.capacity() * core::mem::size_of::<u64>();
        let other_live = current_allocation;
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = current_allocation + other_live;
        limits.max_total_bytes = current_allocation + other_live;

        assert_eq!(
            ensure_vec_capacity(&mut values, 1, other_live, limits),
            Ok(())
        );
        assert_eq!(values.capacity(), 4);
    }

    #[test]
    fn flatten_output_reuses_admitted_capacity_without_growth() {
        let mut out = Vec::<Point>::new();
        out.try_reserve_exact(4).unwrap();
        let mut flattened = Vec::<Point>::new();
        flattened.try_reserve_exact(4).unwrap();
        let current_bytes = vec_capacity_bytes(&out) + vec_capacity_bytes(&flattened);
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = current_bytes;
        limits.max_total_bytes = current_bytes;

        assert_eq!(reserve_flatten_output(&mut out, &flattened, limits), Ok(()));
        assert_eq!(out.capacity(), 4);
    }

    #[test]
    fn flatten_output_growth_admits_old_and_replacement_capacity() {
        let mut out = Vec::<Point>::new();
        out.try_reserve_exact(4).unwrap();
        out.extend((0..4).map(|x| Point {
            x: x as f64,
            y: 0.0,
        }));
        let old_capacity = out.capacity();
        let old_len = out.len();
        let mut flattened = Vec::<Point>::new();
        flattened.try_reserve_exact(4).unwrap();
        flattened.extend((0..4).map(|x| Point {
            x: x as f64,
            y: 1.0,
        }));
        let element_size = core::mem::size_of::<Point>();
        let flattened_bytes = vec_capacity_bytes(&flattened);
        let needed_capacity = old_len + flattened.len();
        let minimum_peak = flattened_bytes + (old_capacity + needed_capacity) * element_size;
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = minimum_peak - 1;
        limits.max_total_bytes = minimum_peak - 1;

        assert_eq!(
            reserve_flatten_output(&mut out, &flattened, limits),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(out.len(), old_len);
        assert_eq!(out.capacity(), old_capacity);

        limits.max_intermediate_bytes = minimum_peak;
        limits.max_total_bytes = minimum_peak;
        reserve_flatten_output(&mut out, &flattened, limits).unwrap();
        assert_eq!(out.len(), old_len);
        assert!(old_capacity + out.capacity() <= (minimum_peak - flattened_bytes) / element_size);
    }

    #[test]
    fn sparse_scheduled_pair_capacity_is_rejected_before_allocation() {
        let mut scheduled = ScheduledPairs::default();
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = 16 * core::mem::size_of::<PairSlot>() - 1;
        assert_eq!(
            scheduled.insert((1, 2), limits, 0),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(scheduled.len, 0);
        assert_eq!(scheduled.slots.capacity(), 0);
    }

    #[test]
    fn combined_peak_budget_is_checked_before_first_output_span() {
        let mut spans = Vec::new();
        let mut limits = Limits::default();
        limits.max_output_bytes = 1024;
        let other_live = limits.max_total_bytes;
        assert_eq!(
            push_span(
                &mut spans,
                CoverageSpan {
                    x: 0,
                    y: 0,
                    width: 1,
                    alpha: 128
                },
                limits,
                other_live,
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert!(spans.is_empty());
        assert_eq!(spans.capacity(), 0);
    }

    #[test]
    fn combined_peak_budget_allows_exact_one_span_reservation() {
        let mut spans = Vec::new();
        let mut limits = Limits::default();
        limits.max_output_bytes = 1024;
        limits.max_intermediate_bytes = limits.max_total_bytes;
        let other_live = limits.max_total_bytes - core::mem::size_of::<CoverageSpan>();
        push_span(
            &mut spans,
            CoverageSpan {
                x: 0,
                y: 0,
                width: 1,
                alpha: 128,
            },
            limits,
            other_live,
        )
        .unwrap();
        assert_eq!(spans.len(), 1);
        assert!(
            other_live + spans.capacity() * core::mem::size_of::<CoverageSpan>()
                <= limits.max_total_bytes
        );
    }

    #[test]
    fn curve_stack_budget_is_checked_before_allocation_or_output_mutation() {
        let sentinel = Point { x: 42.0, y: 24.0 };
        let mut out = vec![sentinel];
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = 1;
        assert_eq!(
            flatten_quadratic(
                Point { x: 0.0, y: 0.0 },
                Point { x: 0.0, y: 4.0 },
                Point { x: 4.0, y: 0.0 },
                limits,
                &mut out,
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(out, vec![sentinel]);
    }

    #[test]
    fn output_byte_budget_is_checked_before_span_append() {
        let mut limits = Limits::default();
        limits.max_output_bytes = 0;
        let error = rasterize(
            &[rect(1.0, 1.0, 4.0, 3.0)],
            FillRule::NonZero,
            clip(0.0, 0.0, 8.0, 8.0),
            limits,
        )
        .unwrap_err();
        assert_eq!(error, CoverageError::SpanLimit);
    }

    #[test]
    fn near_coincident_boundaries_remain_bounded_and_finite() {
        let outer = rect(2.0, 1.0, 2.0 + 1.0e-9, 9.0);
        let output = rasterize(
            &[outer],
            FillRule::NonZero,
            clip(0.0, 0.0, 8.0, 10.0),
            Limits::default(),
        )
        .unwrap();
        assert!(output.iter().all(|span| span.alpha > 0));
        assert!(total(&output) < 1.0e-6);
    }
    #[test]
    fn event_and_span_budgets_fail_without_partial_result() {
        let mut lim = Limits::default();
        lim.max_spans = 0;
        assert_eq!(
            rasterize(
                &[rect(0.0, 0.0, 2.0, 2.0)],
                FillRule::NonZero,
                clip(0.0, 0.0, 4.0, 4.0),
                lim
            ),
            Err(CoverageError::SpanLimit)
        );
    }
    #[test]
    fn active_work_budget_is_typed() {
        let mut lim = Limits::default();
        lim.max_active_edge_visits = 0;
        assert_eq!(
            rasterize(
                &[rect(0.0, 0.0, 2.0, 2.0)],
                FillRule::NonZero,
                clip(0.0, 0.0, 4.0, 4.0),
                lim
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
    }
    #[test]
    fn affine_clamp_area_matches_independent_polygon_clipping() {
        let values = [
            -4.0, -1.0, -0.125, 0.0, 0.125, 0.5, 0.875, 1.0, 1.125, 2.0, 5.0,
        ];
        for &left0 in &values {
            for &right0 in values.iter().filter(|&&v| v >= left0) {
                for &left1 in &values {
                    for &right1 in values.iter().filter(|&&v| v >= left1) {
                        for height in [1.0, 0.25, 1.0e-8] {
                            for (cell_left, cell_right) in [(0.0, 1.0), (0.125, 0.875)] {
                                let actual = trapezoid_cell_area(
                                    left0, right0, left1, right1, height, cell_left, cell_right,
                                )
                                .unwrap();
                                let expected = polygon_rect_area(
                                    [
                                        Point { x: left0, y: 0.0 },
                                        Point { x: right0, y: 0.0 },
                                        Point {
                                            x: right1,
                                            y: height,
                                        },
                                        Point {
                                            x: left1,
                                            y: height,
                                        },
                                    ],
                                    ClipRect {
                                        left: cell_left,
                                        right: cell_right,
                                        top: 0.0,
                                        bottom: height,
                                    },
                                )
                                .unwrap();
                                assert!(
                                    (actual - expected).abs() <= height * 2.0e-14,
                                    "{left0},{right0},{left1},{right1} height={height}: {actual} != {expected}"
                                );
                                assert!(
                                    (0.0..=height * (cell_right - cell_left)).contains(&actual)
                                );
                            }
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn affine_clamp_area_is_translation_stable_and_matches_random_trapezoids() {
        let mut state = 0x14d7_a810_92b3_036fu64;
        let mut next = || {
            state = state
                .wrapping_mul(6364136223846793005)
                .wrapping_add(1442695040888963407);
            (state >> 32) as f64 / u32::MAX as f64 * 32.0 - 16.0
        };
        for _ in 0..10_000 {
            let a = next();
            let b = next();
            let c = next();
            let d = next();
            let (l0, r0, l1, r1) = (a.min(b), a.max(b), c.min(d), c.max(d));
            let actual = trapezoid_cell_area(l0, r0, l1, r1, 0.75, 0.0, 1.0).unwrap();
            let expected = polygon_rect_area(
                [
                    Point { x: l0, y: 0.0 },
                    Point { x: r0, y: 0.0 },
                    Point { x: r1, y: 0.75 },
                    Point { x: l1, y: 0.75 },
                ],
                ClipRect {
                    left: 0.0,
                    right: 1.0,
                    top: 0.0,
                    bottom: 0.75,
                },
            )
            .unwrap();
            assert!((actual - expected).abs() < 2.0e-13);
        }
        for origin in [-1.0e8, -4096.0, 0.0, 4096.0, 1.0e8] {
            let area = trapezoid_cell_area(
                origin - 0.5,
                origin + 0.5,
                origin + 0.5,
                origin + 1.5,
                0.125,
                origin,
                origin + 1.0,
            )
            .unwrap();
            assert!((area - 0.09375).abs() < 1.0e-14);
        }
    }

    #[test]
    fn curve_failure_does_not_modify_caller_buffer() {
        let mut lim = Limits::default();
        lim.max_curve_depth = 0;
        let sentinel = Point {
            x: 123.0,
            y: -456.0,
        };
        let mut out = vec![sentinel];
        assert_eq!(
            flatten_cubic(
                Point { x: 0.0, y: 0.0 },
                Point { x: 0.0, y: 8.0 },
                Point { x: 8.0, y: 8.0 },
                Point { x: 8.0, y: 0.0 },
                lim,
                &mut out
            ),
            Err(CoverageError::CurveLimit)
        );
        assert_eq!(out, vec![sentinel]);
    }
}

#[cfg(test)]
mod interpolation_guard_tests {
    use super::*;

    #[test]
    fn bounded_interpolation_guard_matches_boundary_expectations() {
        let cutoff = MAX_COORDINATE_WITH_PROVEN_FINITE_INTERPOLATION;
        let just_above_cutoff = f64::from_bits(cutoff.to_bits() + 1);
        let min_subnormal = f64::from_bits(1);
        let cases = [
            (
                "exact cutoff with opposite coordinate extremes",
                Point { x: -cutoff, y: -cutoff },
                Point { x: cutoff, y: cutoff },
                -1.0,
                1.0,
                ClipRect {
                    left: -1.0,
                    top: -1.0,
                    right: 1.0,
                    bottom: 1.0,
                },
                cutoff,
                Ok(()),
            ),
            (
                "next representable limit above cutoff retains runtime guard",
                Point { x: -cutoff, y: -1.0 },
                Point { x: cutoff, y: 1.0 },
                -0.5,
                0.5,
                ClipRect {
                    left: -1.0,
                    top: -1.0,
                    right: 1.0,
                    bottom: 1.0,
                },
                just_above_cutoff,
                Ok(()),
            ),
            (
                "adjacent near-horizontal ordinates",
                Point { x: -cutoff, y: 1.0 },
                Point {
                    x: cutoff,
                    y: f64::from_bits(1.0_f64.to_bits() + 1),
                },
                1.0,
                f64::from_bits(1.0_f64.to_bits() + 1),
                ClipRect {
                    left: -1.0,
                    top: 1.0,
                    right: 1.0,
                    bottom: f64::from_bits(1.0_f64.to_bits() + 1),
                },
                cutoff,
                Ok(()),
            ),
            (
                "subnormal delta and signed-zero start",
                Point { x: -32.0, y: -0.0 },
                Point { x: -16.0, y: min_subnormal },
                -0.0,
                min_subnormal,
                ClipRect {
                    left: -64.0,
                    top: -0.0,
                    right: 64.0,
                    bottom: min_subnormal,
                },
                64.0,
                Ok(()),
            ),
            (
                "clip-top rounding with sample at boundary",
                Point {
                    x: 0.0,
                    y: -2.0 * min_subnormal,
                },
                Point {
                    x: 1.0,
                    y: 2.0 * min_subnormal,
                },
                -min_subnormal,
                min_subnormal,
                ClipRect {
                    left: -1.0,
                    top: 0.0,
                    right: 2.0,
                    bottom: min_subnormal,
                },
                4.0,
                Ok(()),
            ),
            (
                "signed-zero clip boundary",
                Point { x: 0.0, y: -0.0 },
                Point { x: 0.5, y: 1.0 },
                -0.0,
                1.0,
                ClipRect {
                    left: -1.0,
                    top: 0.0,
                    right: 1.0,
                    bottom: 1.0,
                },
                2.0,
                Ok(()),
            ),
            (
                "finite clipped sample despite a large remote endpoint",
                Point { x: -1.0e200, y: 0.0 },
                Point { x: 1.0e200, y: 1.0e120 },
                0.0,
                1.0,
                ClipRect {
                    left: -1.0e200,
                    top: 0.0,
                    right: 1.0e200,
                    bottom: 1.0e120,
                },
                1.0e200,
                Ok(()),
            ),
            (
                "unsafe high-limit interpolation retains numeric failure",
                Point {
                    x: -1.0e200,
                    y: -1.0e120,
                },
                Point {
                    x: 1.0e200,
                    y: 1.0e120,
                },
                -1.0e120,
                1.0e120,
                ClipRect {
                    left: -1.0e200,
                    top: -1.0e120,
                    right: 1.0e200,
                    bottom: 1.0e120,
                },
                1.0e200,
                Err(CoverageError::NumericRange),
            ),
        ];

        for rule in [FillRule::EvenOdd, FillRule::NonZero] {
            for &(name, a, b, y0, y1, clip, max_coordinate_abs, expected) in &cases {
                assert_eq!(
                    probe_sample_interpolation_for_test(
                        a,
                        b,
                        y0,
                        y1,
                        clip,
                        rule,
                        max_coordinate_abs,
                    ),
                    expected,
                    "{name} with {rule:?}"
                );
            }
        }
    }
}

#[cfg(test)]
mod x_cluster_tests {
    use super::*;
    use alloc::vec;
    use std::collections::BTreeMap;

    fn rect(left: f64, top: f64, right: f64, bottom: f64, reverse: bool) -> Vec<Point> {
        let points = vec![
            Point { x: left, y: top },
            Point { x: right, y: top },
            Point {
                x: right,
                y: bottom,
            },
            Point { x: left, y: bottom },
        ];
        if reverse {
            points.into_iter().rev().collect()
        } else {
            points
        }
    }

    fn pixels(spans: &[CoverageSpan]) -> BTreeMap<(i32, i32), u8> {
        let mut result = BTreeMap::new();
        for span in spans {
            for offset in 0..span.width {
                let x = span.x + offset as i32;
                assert!(result.insert((x, span.y), span.alpha).is_none());
            }
        }
        result
    }

    fn grouped(
        contours: Vec<Vec<Point>>,
        rule: FillRule,
        clip: ClipRect,
        limits: Limits,
    ) -> Result<Vec<CoverageSpan>, CoverageError> {
        let outer = vec_capacity_bytes(&contours);
        let points = contours.iter().map(vec_capacity_bytes).sum::<usize>();
        rasterize_x_clusters(contours, rule, clip, limits, outer.saturating_add(points))
    }

    #[test]
    fn clustered_spans_match_monolithic_for_holes_bowties_and_touching_columns() {
        let contours = vec![
            rect(-12.25, -3.5, -5.0, 6.0, false),
            rect(-10.5, -1.0, -7.25, 3.75, true),
            // Zero signed shoelace area, but nonempty even-odd coverage.
            vec![
                Point { x: 0.0, y: 0.0 },
                Point { x: 4.0, y: 4.0 },
                Point { x: 0.0, y: 4.0 },
                Point { x: 4.0, y: 0.0 },
            ],
            // Touches the previous shape's pixel support at x=4 and must be
            // conservatively merged rather than split across a shared column.
            rect(4.0, -0.25, 8.25, 4.5, false),
            rect(20.0, -4.0, 27.0, 5.0, false),
            rect(21.0, -2.0, 26.0, 3.0, true),
        ];
        let clip = ClipRect {
            left: -13.25,
            top: -4.25,
            right: 28.5,
            bottom: 6.75,
        };
        for rule in [FillRule::EvenOdd, FillRule::NonZero] {
            let expected = rasterize(&contours, rule, clip, Limits::default()).unwrap();
            let actual = grouped(contours.clone(), rule, clip, Limits::default()).unwrap();
            assert_eq!(pixels(&actual), pixels(&expected), "rule {rule:?}");
        }
    }

    #[test]
    fn randomized_clustered_components_match_monolithic_pixel_coverage() {
        let mut state = 0x15b3_1d27_918f_2acdu64;
        let mut next = || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        let mut contours = Vec::new();
        for component in 0..18 {
            let origin = component as f64 * 12.0 - 24.0;
            for contour_index in 0..3 {
                let count = 3 + (next() as usize % 6);
                let mut contour = Vec::with_capacity(count);
                for point_index in 0..count {
                    let x = origin + (next() % 700) as f64 / 100.0;
                    let y = (next() % 900) as f64 / 100.0 - 4.5;
                    contour.push(Point { x, y });
                    if point_index == 1 && contour_index == 2 && component % 3 == 0 {
                        // Retain occasional collinear/repeated vertices and
                        // hostile self-touching contours in the randomized set.
                        contour.push(Point { x, y });
                    }
                }
                if contour_index == 1 {
                    contour.reverse();
                }
                contours.push(contour);
            }
        }
        let clip = ClipRect {
            left: -25.75,
            top: -5.0,
            right: 196.25,
            bottom: 6.25,
        };
        for rule in [FillRule::EvenOdd, FillRule::NonZero] {
            let expected = rasterize(&contours, rule, clip, Limits::default()).unwrap();
            let actual = grouped(contours.clone(), rule, clip, Limits::default()).unwrap();
            assert_eq!(pixels(&actual), pixels(&expected), "rule {rule:?}");
        }
    }

    #[test]
    fn clustering_preserves_overlapping_components_and_fractional_clip() {
        let contours = vec![
            rect(-8.5, -2.25, 2.5, 5.75, false),
            rect(-1.25, -1.5, 7.5, 4.25, true),
            rect(5.75, -3.0, 12.125, 6.0, false),
            // Clearly outside the clipped support; it cannot affect fill
            // winding anywhere in the destination and may be culled.
            rect(40.0, -10.0, 42.0, 10.0, false),
        ];
        let clip = ClipRect {
            left: -8.25,
            top: -2.5,
            right: 12.0,
            bottom: 5.25,
        };
        let expected = rasterize(&contours, FillRule::NonZero, clip, Limits::default()).unwrap();
        let actual = grouped(contours.clone(), FillRule::NonZero, clip, Limits::default()).unwrap();
        assert_eq!(pixels(&actual), pixels(&expected));
    }

    #[test]
    fn disjoint_x_components_with_overlapping_rows_preserve_pixel_map() {
        let contours = vec![
            rect(0.25, 0.25, 2.75, 4.25, false),
            rect(8.25, 0.25, 10.75, 4.25, false),
        ];
        let clip = ClipRect {
            left: 0.0,
            top: 0.0,
            right: 12.0,
            bottom: 5.0,
        };
        let expected = rasterize(&contours, FillRule::NonZero, clip, Limits::default()).unwrap();
        let actual = grouped(contours, FillRule::NonZero, clip, Limits::default()).unwrap();

        // X clusters may be concatenated in component order rather than
        // globally row-major. Their support columns are disjoint, so replay
        // order cannot change any composited pixel.
        assert_eq!(pixels(&actual), pixels(&expected));
    }

    #[test]
    fn grouping_admission_fails_before_component_buffers_are_allocated() {
        let contours = vec![rect(0.0, 0.0, 4.0, 4.0, false); 16];
        let base = vec_capacity_bytes(&contours)
            .saturating_add(contours.iter().map(vec_capacity_bytes).sum::<usize>());
        let limits = Limits {
            max_intermediate_bytes: base,
            max_total_bytes: base,
            ..Limits::default()
        };
        assert_eq!(
            rasterize_x_clusters(
                contours,
                FillRule::EvenOdd,
                ClipRect {
                    left: -1.0,
                    top: -1.0,
                    right: 5.0,
                    bottom: 5.0,
                },
                limits,
                base
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
    }

    #[test]
    fn clustered_work_and_output_budgets_are_shared_across_components() {
        let contours = vec![
            rect(0.0, 0.0, 3.0, 3.0, false),
            rect(8.0, 0.0, 11.0, 3.0, false),
        ];
        let clip = ClipRect {
            left: 0.0,
            top: 0.0,
            right: 12.0,
            bottom: 4.0,
        };
        let grouping_work = x_cluster_grouping_work(2, 8).unwrap();
        let mut limits = Limits::default();
        // The shared cap admits the grouping pass and either component by
        // itself, but not both components cumulatively. This low cap selects
        // the sparse path; each rectangle then consumes exactly two visits.
        limits.max_active_edge_visits = grouping_work + 2;
        let result = grouped(contours, FillRule::NonZero, clip, limits);
        assert_eq!(result, Err(CoverageError::ActiveWorkLimit));

        let contours = vec![
            rect(0.0, 0.0, 3.0, 3.0, false),
            rect(8.0, 0.0, 11.0, 3.0, false),
        ];
        let mut limits = Limits::default();
        limits.max_spans = 5;
        assert_eq!(
            grouped(contours, FillRule::NonZero, clip, limits),
            Err(CoverageError::SpanLimit)
        );
    }

    #[test]
    fn retained_component_output_reduces_later_intermediate_budget() {
        let limits = Limits {
            max_intermediate_bytes: 128,
            max_total_bytes: 256,
            max_output_bytes: 128,
            ..Limits::default()
        };
        let remaining = limits_after_usage(limits, RasterUsage::default(), 48).unwrap();
        assert_eq!(remaining.max_intermediate_bytes, 80);
        assert_eq!(remaining.max_total_bytes, 208);
        assert_eq!(remaining.max_output_bytes, 80);
        assert!(matches!(
            limits_after_usage(limits, RasterUsage::default(), 129),
            Err(CoverageError::ActiveWorkLimit)
        ));
    }

    #[test]
    fn empty_cluster_input_still_validates_original_raster_limits_and_clip() {
        let clip = ClipRect {
            left: 0.0,
            top: 0.0,
            right: 1.0,
            bottom: 1.0,
        };
        for invalid in [
            Limits {
                max_coordinate_abs: f64::NAN,
                ..Limits::default()
            },
            Limits {
                curve_error_px: f64::INFINITY,
                ..Limits::default()
            },
            Limits {
                curve_error_px: 0.0,
                ..Limits::default()
            },
        ] {
            assert_eq!(
                rasterize_x_clusters(Vec::new(), FillRule::NonZero, clip, invalid, 0),
                Err(CoverageError::InvalidCoordinate)
            );
        }
        assert_eq!(
            rasterize_x_clusters(
                Vec::new(),
                FillRule::NonZero,
                ClipRect {
                    right: f64::NAN,
                    ..clip
                },
                Limits::default(),
                0
            ),
            Err(CoverageError::InvalidClip)
        );
    }

    #[test]
    fn grouping_memory_preflight_falls_back_to_monolithic_for_offscreen_contours() {
        let contours = vec![rect(1.0, 100.0, 4.0, 104.0, false); 8];
        let base = vec_capacity_bytes(&contours)
            .saturating_add(contours.iter().map(vec_capacity_bytes).sum::<usize>());
        let limits = Limits {
            max_intermediate_bytes: base,
            max_total_bytes: base,
            ..Limits::default()
        };
        assert!(
            grouped(
                contours,
                FillRule::NonZero,
                ClipRect {
                    left: 0.0,
                    top: 0.0,
                    right: 10.0,
                    bottom: 10.0,
                },
                limits,
            )
            .unwrap()
            .is_empty()
        );
    }

    #[test]
    fn monolithic_fallback_receives_only_remaining_grouping_work_budget() {
        let limits = Limits {
            max_active_edge_visits: 10,
            ..Limits::default()
        };
        let remaining = limits_after_grouping_work(limits, 4).unwrap();
        assert_eq!(remaining.max_active_edge_visits, 6);
        assert!(matches!(
            limits_after_grouping_work(limits, 11),
            Err(CoverageError::ActiveWorkLimit)
        ));
    }

    #[test]
    fn merged_output_growth_respects_each_original_budget() {
        let mut spans: Vec<CoverageSpan> = Vec::new();
        let one_span = CoverageSpan {
            x: 0,
            y: 0,
            width: 1,
            alpha: 255,
        };
        let element_size = core::mem::size_of::<CoverageSpan>();
        let mut limits = Limits {
            max_intermediate_bytes: 1024,
            max_total_bytes: 2048,
            max_output_bytes: element_size * 8,
            ..Limits::default()
        };
        ensure_vec_capacity_with_output_limit(
            &mut spans,
            4,
            0,
            Some(limits.max_output_bytes),
            limits,
        )
        .unwrap();
        assert!(vec_capacity_bytes(&spans) <= limits.max_output_bytes);

        // A larger total ceiling must never raise the caller's smaller
        // intermediate ceiling for the retained result vector.
        spans = Vec::new();
        limits.max_intermediate_bytes = element_size;
        limits.max_total_bytes = element_size * 64;
        limits.max_output_bytes = element_size * 64;
        assert_eq!(
            ensure_vec_capacity_with_output_limit(
                &mut spans,
                2,
                0,
                Some(limits.max_output_bytes),
                limits,
            ),
            Err(CoverageError::ActiveWorkLimit)
        );

        // Output allocation is independently capped even when both live
        // memory ceilings have room.
        spans = Vec::new();
        limits.max_intermediate_bytes = element_size * 64;
        limits.max_total_bytes = element_size * 64;
        limits.max_output_bytes = element_size;
        assert_eq!(
            ensure_vec_capacity_with_output_limit(
                &mut spans,
                2,
                0,
                Some(limits.max_output_bytes),
                limits,
            ),
            Err(CoverageError::SpanLimit)
        );
        assert!(spans.is_empty());

        // Keep a concrete value in this test so it also type-checks the
        // output command representation used by the bounded merge path.
        assert_eq!(one_span.alpha, 255);
    }

    #[test]
    fn heapsort_matches_total_order_without_allocating_scratch() {
        let mut state = 0x25a4_9d13_8c07_31efu64;
        let mut next = || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            (state >> 32) as i32
        };
        for len in 0..300 {
            let mut actual: Vec<_> = (0..len)
                .map(|index| (next() % 31, (index % 7) as i32, index as i32))
                .collect();
            let mut expected = actual.clone();
            heap_sort_by(&mut actual, Ord::cmp);
            expected.sort_unstable();
            assert_eq!(actual, expected, "length {len}");
        }
    }
}

#[cfg(test)]
mod scratch_fast_path_admission_tests {
    use super::*;

    fn limits_at(bytes: usize) -> Limits {
        let mut limits = Limits::default();
        limits.max_intermediate_bytes = bytes;
        limits.max_total_bytes = bytes;
        limits.max_output_bytes = bytes;
        limits
    }

    fn event(index: usize) -> Event {
        Event {
            y: index as f64,
            x: 0.0,
            kind: EventKind::Clip,
        }
    }

    fn push_group(
        scratch: &mut SweepScratch,
        value: Event,
        shared_bytes: usize,
        limits: Limits,
    ) -> Result<(), CoverageError> {
        push_scratch!(scratch, group, value, shared_bytes, limits);
        Ok(())
    }

    #[test]
    fn admitted_scratch_capacity_can_be_reused_at_exact_live_limit() {
        let shared_bytes: usize = 37;
        let mut scratch = SweepScratch::default();
        let initial_limits = limits_at(1024 * 1024);
        reserve_scratch!(scratch, group, 1, shared_bytes, initial_limits).unwrap();
        let admitted_capacity = scratch.group.capacity();
        let admitted_bytes = shared_bytes + scratch.capacity_bytes();
        let exact_limits = limits_at(admitted_bytes);

        for index in 0..admitted_capacity {
            push_group(&mut scratch, event(index), shared_bytes, exact_limits).unwrap();
        }
        assert_eq!(scratch.group.len(), admitted_capacity);
        scratch.group.clear();
        push_group(&mut scratch, event(0), shared_bytes, exact_limits).unwrap();
        assert_eq!(scratch.group.len(), 1);
        assert_eq!(scratch.group.capacity(), admitted_capacity);
        assert_eq!(shared_bytes + scratch.capacity_bytes(), admitted_bytes);
    }

    #[test]
    fn scratch_capacity_growth_still_requires_the_full_replacement_peak() {
        let shared_bytes: usize = 53;
        let mut scratch = SweepScratch::default();
        let generous = limits_at(1024 * 1024);
        reserve_scratch!(scratch, group, 1, shared_bytes, generous).unwrap();
        for index in 0..scratch.group.capacity() {
            push_group(&mut scratch, event(index), shared_bytes, generous).unwrap();
        }

        let old_capacity = scratch.group.capacity();
        let element_size = core::mem::size_of::<Event>();
        let required_peak = shared_bytes + (old_capacity * 2 + 1) * element_size;
        let too_small = limits_at(required_peak - 1);
        assert_eq!(
            push_group(&mut scratch, event(old_capacity), shared_bytes, too_small),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(scratch.group.len(), old_capacity);
        assert_eq!(scratch.group.capacity(), old_capacity);

        let enough = limits_at(required_peak.saturating_add(old_capacity * element_size * 2));
        push_group(&mut scratch, event(old_capacity), shared_bytes, enough).unwrap();
        assert_eq!(scratch.group.len(), old_capacity + 1);
    }

    #[test]
    fn heap_scheduled_coverage_and_output_growth_still_charge_live_scratch() {
        let fixed_bytes: usize = 71;
        let mut scratch = SweepScratch::default();
        let generous = limits_at(1024 * 1024);
        reserve_scratch!(scratch, changed, 1, fixed_bytes, generous).unwrap();
        let other_live = fixed_bytes + scratch.capacity_bytes();

        let mut heap = BinaryHeap::new();
        heap.try_reserve_exact(4).unwrap();
        for index in 0..heap.capacity() {
            heap.push(event(index));
        }
        let heap_capacity = heap.capacity();
        let heap_size = core::mem::size_of::<Event>();
        let heap_growth_peak = other_live + (heap_capacity * 2 + 1) * heap_size;
        let heap_limits = limits_at(heap_growth_peak - 1);
        let mut event_count = heap.len();
        assert_eq!(
            push_event(
                &mut heap,
                event(9),
                &mut event_count,
                heap_limits,
                other_live
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(heap.capacity(), heap_capacity);

        let mut scheduled = ScheduledPairs::default();
        let slot_bytes = core::mem::size_of::<PairSlot>();
        let scheduled_limits = limits_at(other_live + 16 * slot_bytes - 1);
        assert_eq!(
            scheduled.insert((2, 7), scheduled_limits, other_live),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(scheduled.capacity_bytes(), 0);

        let mut coverage = CoverageAccumulator::default();
        let event_bytes = core::mem::size_of::<CoverageEvent>();
        let coverage_limits = limits_at(other_live + 2 * event_bytes - 1);
        assert_eq!(
            coverage.add_event_pair(
                [
                    CoverageEvent {
                        y: 1,
                        x: 1,
                        range_delta: 1.0,
                        partial_delta: 0.0,
                    },
                    CoverageEvent {
                        y: 1,
                        x: 4,
                        range_delta: -1.0,
                        partial_delta: 0.0,
                    },
                ],
                coverage_limits,
                other_live,
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(coverage.events.capacity(), 0);

        let mut spans = Vec::new();
        let span_bytes = core::mem::size_of::<CoverageSpan>();
        let output_limits = limits_at(other_live + span_bytes - 1);
        assert_eq!(
            push_span(
                &mut spans,
                CoverageSpan {
                    x: 1,
                    y: 1,
                    width: 1,
                    alpha: 255,
                },
                output_limits,
                other_live,
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(spans.capacity(), 0);
    }

    #[test]
    fn fixed_order_tree_capacity_is_bounded_by_the_pre_admitted_edge_count() {
        let edge_count = 48;
        let mut tree = OrderTree::try_new(edge_count).unwrap();
        let fixed_bytes = tree.capacity_bytes();
        assert!(
            fixed_bytes
                <= edge_count
                    * (core::mem::size_of::<Node>()
                        + core::mem::size_of::<usize>()
                        + core::mem::size_of::<Option<usize>>())
        );

        for edge in 0..edge_count {
            tree.insert_at(edge, tree.len());
            assert_eq!(tree.capacity_bytes(), fixed_bytes);
        }
        for edge in (0..edge_count).step_by(2) {
            tree.remove(edge);
            assert_eq!(tree.capacity_bytes(), fixed_bytes);
        }
        for edge in (0..edge_count).step_by(2) {
            tree.insert_at(edge, tree.len());
            assert_eq!(tree.capacity_bytes(), fixed_bytes);
        }
    }
}
