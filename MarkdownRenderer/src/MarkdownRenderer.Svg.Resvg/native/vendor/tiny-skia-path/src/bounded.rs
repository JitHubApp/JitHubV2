// SPDX-License-Identifier: MIT
// Copyright 2026 JitHub contributors.

//! Fallible path operations with a request-scoped allocation budget.
//!
//! The legacy path builder/stroker APIs remain unchanged. Bounded operations
//! use this ledger to admit every retained `Vec` capacity and the old+new
//! capacity overlap while a vector grows.

use alloc::sync::Arc;
use alloc::vec::Vec;
use core::sync::atomic::{AtomicUsize, Ordering};

use crate::{Path, PathBuilder, PathVerb};

const BUDGET_METADATA_BYTES: usize = 128;

/// Why a bounded path operation could not produce a complete path.
#[derive(Copy, Clone, Debug, Eq, PartialEq)]
pub enum PathAllocationError {
    /// The operation's retained and transient path capacities exceed its byte budget.
    BudgetExceeded,
    /// A fallible vector allocation failed.
    AllocationFailed,
    /// A capacity calculation overflowed `usize`.
    CapacityOverflow,
    /// The path operation could not produce valid geometry.
    InvalidGeometry,
    /// The existing bounded dash complexity ceiling was exceeded.
    ComplexityLimit,
}

#[derive(Debug)]
pub(crate) struct AllocationBudget {
    limit_bytes: usize,
    live_bytes: AtomicUsize,
    peak_bytes: AtomicUsize,
}

impl AllocationBudget {
    pub(crate) fn new(limit_bytes: usize) -> Result<Arc<Self>, PathAllocationError> {
        if limit_bytes < BUDGET_METADATA_BYTES {
            return Err(PathAllocationError::BudgetExceeded);
        }
        Ok(Arc::new(Self {
            limit_bytes,
            live_bytes: AtomicUsize::new(BUDGET_METADATA_BYTES),
            peak_bytes: AtomicUsize::new(BUDGET_METADATA_BYTES),
        }))
    }

    pub(crate) fn live_bytes(&self) -> usize {
        self.live_bytes.load(Ordering::Acquire)
    }

    pub(crate) fn peak_bytes(&self) -> usize {
        self.peak_bytes.load(Ordering::Acquire)
    }

    fn begin_growth(&self, new_capacity_bytes: usize) -> Result<(), PathAllocationError> {
        let mut current = self.live_bytes.load(Ordering::Acquire);
        loop {
            let next = current
                .checked_add(new_capacity_bytes)
                .ok_or(PathAllocationError::CapacityOverflow)?;
            if next > self.limit_bytes {
                return Err(PathAllocationError::BudgetExceeded);
            }

            match self.live_bytes.compare_exchange_weak(
                current,
                next,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => {
                    self.update_peak(next);
                    return Ok(());
                }
                Err(observed) => current = observed,
            }
        }
    }

    fn finish_growth(
        &self,
        old_capacity_bytes: usize,
        requested_new_bytes: usize,
        actual_new_bytes: usize,
    ) -> Result<(), PathAllocationError> {
        let reserved = old_capacity_bytes
            .checked_add(requested_new_bytes)
            .ok_or(PathAllocationError::CapacityOverflow)?;
        let mut current = self.live_bytes.load(Ordering::Acquire);
        loop {
            if current < reserved {
                debug_assert!(false, "allocation ledger underflow");
                return Err(PathAllocationError::CapacityOverflow);
            }
            let next = current
                .checked_sub(reserved)
                .and_then(|other_live| other_live.checked_add(actual_new_bytes))
                .ok_or(PathAllocationError::CapacityOverflow)?;
            match self.live_bytes.compare_exchange_weak(
                current,
                next,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => {
                    self.update_peak(next);
                    if next > self.limit_bytes {
                        return Err(PathAllocationError::BudgetExceeded);
                    }
                    return Ok(());
                }
                Err(observed) => current = observed,
            }
        }
    }

    fn abort_growth(&self, requested_new_bytes: usize) {
        let previous = self
            .live_bytes
            .fetch_sub(requested_new_bytes, Ordering::AcqRel);
        debug_assert!(previous >= requested_new_bytes);
    }

    pub(crate) fn release(&self, bytes: usize) {
        if bytes == 0 {
            return;
        }
        let previous = self.live_bytes.fetch_sub(bytes, Ordering::AcqRel);
        debug_assert!(previous >= bytes);
    }

    fn update_peak(&self, candidate: usize) {
        let mut peak = self.peak_bytes.load(Ordering::Acquire);
        while candidate > peak {
            match self.peak_bytes.compare_exchange_weak(
                peak,
                candidate,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => break,
                Err(observed) => peak = observed,
            }
        }
    }
}

pub(crate) fn capacity_bytes<T>(capacity: usize) -> Result<usize, PathAllocationError> {
    capacity
        .checked_mul(core::mem::size_of::<T>())
        .ok_or(PathAllocationError::CapacityOverflow)
}

/// Releases the ledger's retained-capacity credit after its allocation owner
/// has been dropped. Keep this as the final struct field after owned buffers.
#[derive(Debug)]
pub(crate) struct AllocationLease {
    budget: Option<Arc<AllocationBudget>>,
    bytes: usize,
}

impl AllocationLease {
    pub(crate) fn empty() -> Self {
        Self {
            budget: None,
            bytes: 0,
        }
    }

    pub(crate) fn attach(budget: Arc<AllocationBudget>) -> Self {
        Self {
            budget: Some(budget),
            bytes: 0,
        }
    }

    pub(crate) fn set_bytes(&mut self, bytes: usize) {
        self.bytes = bytes;
    }

    pub(crate) fn disarm(&mut self) {
        self.bytes = 0;
    }
}

impl Default for AllocationLease {
    fn default() -> Self {
        Self::empty()
    }
}

impl Drop for AllocationLease {
    fn drop(&mut self) {
        if let Some(budget) = &self.budget {
            budget.release(self.bytes);
        }
    }
}

/// Reserve vector capacity without exceeding the shared operation budget.
/// Growth accounts for the old allocation and requested replacement at once.
pub(crate) fn reserve_vec<T>(
    values: &mut Vec<T>,
    additional: usize,
    budget: &Arc<AllocationBudget>,
) -> Result<(), PathAllocationError> {
    let needed = values
        .len()
        .checked_add(additional)
        .ok_or(PathAllocationError::CapacityOverflow)?;
    if needed <= values.capacity() {
        return Ok(());
    }

    let old_capacity_bytes = capacity_bytes::<T>(values.capacity())?;
    let doubled = values
        .capacity()
        .max(4)
        .checked_mul(2)
        .ok_or(PathAllocationError::CapacityOverflow)?;
    let preferred_capacity = needed.max(doubled);
    let preferred_bytes = capacity_bytes::<T>(preferred_capacity)?;

    let requested_capacity = match budget.live_bytes().checked_add(preferred_bytes) {
        Some(total) if total <= budget.limit_bytes => preferred_capacity,
        _ => needed,
    };
    let requested_new_bytes = capacity_bytes::<T>(requested_capacity)?;
    budget.begin_growth(requested_new_bytes)?;

    let reserve_result = values.try_reserve_exact(requested_capacity - values.len());
    if reserve_result.is_err() {
        budget.abort_growth(requested_new_bytes);
        return Err(PathAllocationError::AllocationFailed);
    }

    let actual_new_bytes = capacity_bytes::<T>(values.capacity())?;
    budget.finish_growth(old_capacity_bytes, requested_new_bytes, actual_new_bytes)
}

/// A path whose vector storage remains charged to a request-scoped budget.
///
/// The path is only exposed by reference so the caller cannot detach storage
/// from the lease while it is still live.
#[derive(Debug)]
pub struct BoundedPath {
    path: Path,
    lease: AllocationLease,
    retained_bytes: usize,
}

impl BoundedPath {
    pub(crate) fn new(
        path: Path,
        budget: Arc<AllocationBudget>,
    ) -> Result<Self, PathAllocationError> {
        let path_bytes = path.allocation_bytes()?;
        let retained_bytes = match path_bytes.checked_add(BUDGET_METADATA_BYTES) {
            Some(bytes) => bytes,
            None => {
                drop(path);
                budget.release(path_bytes);
                return Err(PathAllocationError::CapacityOverflow);
            }
        };
        Ok(Self {
            path,
            lease: AllocationLease {
                budget: Some(budget),
                bytes: path_bytes,
            },
            retained_bytes,
        })
    }

    /// Borrows the completed path while its allocation lease remains alive.
    pub fn as_path(&self) -> &Path {
        &self.path
    }

    /// Returns the path storage retained by this lease, in bytes of Vec capacity.
    pub fn retained_bytes(&self) -> usize {
        self.retained_bytes
    }

    /// Returns the maximum simultaneously admitted Vec capacity during creation.
    pub fn peak_bytes(&self) -> usize {
        self.lease
            .budget
            .as_ref()
            .map_or(BUDGET_METADATA_BYTES, |budget| budget.peak_bytes())
    }

    /// Applies a transform in place without allocating a second path.
    pub fn transform_in_place(
        mut self,
        transform: crate::Transform,
    ) -> Result<Self, PathAllocationError> {
        if !transform.is_identity() {
            transform.map_points(&mut self.path.points);
            self.path.bounds = crate::Rect::from_points(&self.path.points)
                .ok_or(PathAllocationError::InvalidGeometry)?;
        }
        Ok(self)
    }
}

impl Path {
    pub(crate) fn allocation_bytes(&self) -> Result<usize, PathAllocationError> {
        capacity_bytes::<PathVerb>(self.verbs.capacity())?
            .checked_add(capacity_bytes::<crate::Point>(self.points.capacity())?)
            .ok_or(PathAllocationError::CapacityOverflow)
    }

    /// Clones this path under a request-local allocation limit.
    pub fn clone_bounded(&self, max_bytes: usize) -> Result<BoundedPath, PathAllocationError> {
        let budget = AllocationBudget::new(max_bytes)?;
        let mut verbs = Vec::new();
        if let Err(error) = reserve_vec(&mut verbs, self.verbs.len(), &budget) {
            let bytes = capacity_bytes::<PathVerb>(verbs.capacity()).unwrap_or(usize::MAX);
            drop(verbs);
            budget.release(bytes);
            return Err(error);
        }
        verbs.extend_from_slice(&self.verbs);

        let mut points = Vec::new();
        if let Err(error) = reserve_vec(&mut points, self.points.len(), &budget) {
            let verb_bytes = capacity_bytes::<PathVerb>(verbs.capacity()).unwrap_or(usize::MAX);
            let point_bytes =
                capacity_bytes::<crate::Point>(points.capacity()).unwrap_or(usize::MAX);
            drop(points);
            drop(verbs);
            budget.release(verb_bytes.saturating_add(point_bytes));
            return Err(error);
        }
        points.extend_from_slice(&self.points);

        let path = Path {
            bounds: self.bounds,
            verbs,
            points,
        };
        BoundedPath::new(path, budget)
    }

    /// Clones and transforms this path under a request-local allocation limit.
    pub fn transform_bounded(
        &self,
        transform: crate::Transform,
        max_bytes: usize,
    ) -> Result<BoundedPath, PathAllocationError> {
        self.clone_bounded(max_bytes)?.transform_in_place(transform)
    }
}

impl PathBuilder {
    /// Creates a rectangle path while charging its retained and transient
    /// vector capacity to a request-scoped byte budget.
    ///
    /// The path uses the same clockwise corner sequence as [`PathBuilder::from_rect`].
    pub fn from_rect_bounded(
        rect: crate::Rect,
        max_bytes: usize,
    ) -> Result<BoundedPath, PathAllocationError> {
        let budget = AllocationBudget::new(max_bytes)?;
        let mut builder = PathBuilder::new();
        builder.set_budget(budget.clone());
        builder.move_to(rect.left(), rect.top());
        builder.line_to(rect.right(), rect.top());
        builder.line_to(rect.right(), rect.bottom());
        builder.line_to(rect.left(), rect.bottom());
        builder.close();

        if let Some(error) = builder.allocation_error() {
            return Err(error);
        }

        let path = builder
            .finish()
            .ok_or(PathAllocationError::InvalidGeometry)?;
        BoundedPath::new(path, budget)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::PathBuilder;

    fn sample_path() -> Path {
        let mut builder = PathBuilder::new();
        builder.move_to(1.0, 2.0);
        builder.line_to(7.0, 9.0);
        builder.finish().unwrap()
    }

    #[test]
    fn clone_bounded_charges_retained_path_and_budget_metadata() {
        let path = sample_path();
        let clone = path.clone_bounded(256).unwrap();
        assert_eq!(clone.as_path(), &path);
        assert!(clone.retained_bytes() >= BUDGET_METADATA_BYTES + path.allocation_bytes().unwrap());
        assert!(clone.peak_bytes() >= clone.retained_bytes());
    }

    #[test]
    fn clone_bounded_rejects_before_path_growth_exceeds_limit() {
        let path = sample_path();
        assert!(matches!(
            path.clone_bounded(BUDGET_METADATA_BYTES),
            Err(PathAllocationError::BudgetExceeded)
        ));
        assert!(matches!(
            path.clone_bounded(BUDGET_METADATA_BYTES - 1),
            Err(PathAllocationError::BudgetExceeded)
        ));
    }

    #[test]
    fn transform_bounded_reuses_the_admitted_path_allocation() {
        let path = sample_path();
        let retained = path.clone_bounded(256).unwrap().retained_bytes();
        let transform = crate::Transform::from_translate(10.0, -4.0);
        let transformed = path.transform_bounded(transform, 256).unwrap();

        assert_eq!(
            transformed.as_path().points()[0],
            crate::Point::from_xy(11.0, -2.0)
        );
        assert_eq!(transformed.retained_bytes(), retained);
        assert_eq!(transformed.peak_bytes(), transformed.retained_bytes());
    }

    #[test]
    fn bounded_builder_keeps_allocation_failure_sticky_and_releases_credit() {
        let budget = AllocationBudget::new(BUDGET_METADATA_BYTES + 16).unwrap();
        let mut builder = PathBuilder::new();
        builder.set_budget(budget.clone());
        builder.move_to(1.0, 2.0);
        builder.line_to(3.0, 4.0);

        assert_eq!(
            builder.allocation_error(),
            Some(PathAllocationError::BudgetExceeded)
        );
        assert!(builder.finish().is_none());
        assert_eq!(budget.live_bytes(), BUDGET_METADATA_BYTES);
    }

    #[test]
    fn bounded_rectangle_matches_legacy_path_geometry() {
        let rect = crate::Rect::from_xywh(3.25, -2.5, 17.0, 9.75).unwrap();
        let legacy = PathBuilder::from_rect(rect);
        let bounded = PathBuilder::from_rect_bounded(rect, 1024).unwrap();

        assert_eq!(bounded.as_path().verbs(), legacy.verbs());
        assert_eq!(bounded.as_path().points(), legacy.points());
        assert_eq!(bounded.as_path().bounds(), legacy.bounds());
        assert!(bounded.peak_bytes() <= 1024);
    }

    #[test]
    fn bounded_rectangle_rejects_zero_and_insufficient_budget() {
        let rect = crate::Rect::from_xywh(0.0, 0.0, 10.0, 10.0).unwrap();
        assert!(matches!(
            PathBuilder::from_rect_bounded(rect, 0),
            Err(PathAllocationError::BudgetExceeded)
        ));

        let minimum_budget = (1..=1024)
            .find(|budget| PathBuilder::from_rect_bounded(rect, *budget).is_ok())
            .unwrap();
        assert!(minimum_budget > BUDGET_METADATA_BYTES);
        assert!(PathBuilder::from_rect_bounded(rect, minimum_budget).is_ok());
        assert!(matches!(
            PathBuilder::from_rect_bounded(rect, minimum_budget - 1),
            Err(PathAllocationError::BudgetExceeded)
        ));
    }
}
