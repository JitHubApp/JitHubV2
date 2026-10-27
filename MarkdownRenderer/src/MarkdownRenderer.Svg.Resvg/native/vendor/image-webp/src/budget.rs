//! Conservative, request-local accounting for allocations made by the bounded decoder path.

use std::cell::Cell;
use std::mem::size_of;

use crate::decoder::DecodingError;

/// A conservative ledger for decoder-owned working allocations.
///
/// Charges are monotonic: a reallocation charges the new buffer while the previous capacity is
/// still live, and a failed allocation is not refunded. The ledger covers requested `Vec`
/// capacities plus a small per-allocation bookkeeping allowance. It is not an RSS guarantee;
/// allocator metadata and caller-owned input/output buffers are outside this crate-level ledger.
#[derive(Debug)]
pub(crate) struct DecodeBudget {
    limit: Cell<usize>,
    used: Cell<usize>,
    failed: Cell<bool>,
}

impl DecodeBudget {
    pub(crate) const fn new(limit: usize) -> Self {
        Self {
            limit: Cell::new(limit),
            used: Cell::new(0),
            failed: Cell::new(false),
        }
    }

    pub(crate) fn used(&self) -> usize {
        self.used.get()
    }

    pub(crate) fn lower_limit(&self, new_limit: usize) -> Result<(), DecodingError> {
        let current_limit = self.limit.get();
        if new_limit > current_limit {
            return Err(DecodingError::InvalidParameter(
                "bounded decoder allocation limit may only be lowered".to_owned(),
            ));
        }
        if new_limit < self.used.get() {
            self.failed.set(true);
            return Err(DecodingError::MemoryLimitExceeded);
        }
        self.limit.set(new_limit);
        Ok(())
    }

    pub(crate) fn mark_failed(&self) {
        self.failed.set(true);
    }

    pub(crate) fn charge(&self, bytes: usize) -> Result<(), DecodingError> {
        if self.failed.get() {
            return Err(DecodingError::MemoryLimitExceeded);
        }
        let Some(next) = self.used.get().checked_add(bytes) else {
            self.failed.set(true);
            return Err(DecodingError::MemoryLimitExceeded);
        };
        if next > self.limit.get() {
            self.failed.set(true);
            return Err(DecodingError::MemoryLimitExceeded);
        }
        self.used.set(next);
        Ok(())
    }

    fn charge_vec_capacity<T>(&self, capacity: usize) -> Result<(), DecodingError> {
        if capacity == 0 {
            return Ok(());
        }
        let payload = capacity
            .checked_mul(size_of::<T>())
            .ok_or(DecodingError::MemoryLimitExceeded)?;
        // Account a conservative fixed allocator/control allowance per buffer. This is deliberately
        // retained in the monotonic ledger even after a temporary vector is dropped.
        self.charge(
            payload
                .checked_add(64)
                .ok_or(DecodingError::MemoryLimitExceeded)?,
        )
    }

    pub(crate) fn vec_with_len<T: Clone>(
        &self,
        len: usize,
        value: T,
    ) -> Result<Vec<T>, DecodingError> {
        let mut values = Vec::new();
        if len != 0 {
            self.charge_vec_capacity::<T>(len)?;
            values
                .try_reserve_exact(len)
                .map_err(|_| self.allocation_failed())?;
            self.charge_capacity_rounding::<T>(values.capacity(), len)?;
            values.resize(len, value);
        }
        Ok(values)
    }

    pub(crate) fn vec_with_capacity<T>(&self, capacity: usize) -> Result<Vec<T>, DecodingError> {
        let mut values = Vec::new();
        self.reserve_vec(&mut values, capacity)?;
        Ok(values)
    }

    pub(crate) fn reserve_vec<T>(
        &self,
        values: &mut Vec<T>,
        requested_capacity: usize,
    ) -> Result<(), DecodingError> {
        if requested_capacity <= values.capacity() {
            return Ok(());
        }
        self.charge_vec_capacity::<T>(requested_capacity)?;
        values
            .try_reserve_exact(requested_capacity.saturating_sub(values.len()))
            .map_err(|_| self.allocation_failed())?;
        if values.capacity() < requested_capacity {
            return Err(self.allocation_failed());
        }
        self.charge_capacity_rounding::<T>(values.capacity(), requested_capacity)?;
        Ok(())
    }

    pub(crate) fn push_vec<T>(&self, values: &mut Vec<T>, value: T) -> Result<(), DecodingError> {
        if values.len() == values.capacity() {
            let next_capacity = if values.capacity() == 0 {
                4
            } else {
                values
                    .capacity()
                    .checked_mul(2)
                    .ok_or(DecodingError::MemoryLimitExceeded)?
            };
            self.reserve_vec(values, next_capacity)?;
        }
        values.push(value);
        Ok(())
    }

    fn charge_capacity_rounding<T>(
        &self,
        actual_capacity: usize,
        requested_capacity: usize,
    ) -> Result<(), DecodingError> {
        if actual_capacity <= requested_capacity {
            return Ok(());
        }
        let extra = actual_capacity
            .checked_sub(requested_capacity)
            .and_then(|count| count.checked_mul(size_of::<T>()))
            .ok_or(DecodingError::MemoryLimitExceeded)?;
        self.charge(extra)
    }

    fn allocation_failed(&self) -> DecodingError {
        self.failed.set(true);
        DecodingError::AllocationFailed
    }
}

#[cfg(test)]
mod tests {
    use super::DecodeBudget;
    use crate::decoder::DecodingError;

    #[test]
    fn vector_capacity_is_charged_before_reserve_and_growth_is_monotonic() {
        let budget = DecodeBudget::new(4096);
        let mut values = Vec::<u32>::new();
        budget.reserve_vec(&mut values, 8).unwrap();
        assert!(values.capacity() >= 8);
        let first_charge = budget.used();
        assert!(first_charge >= 8 * std::mem::size_of::<u32>());

        budget.push_vec(&mut values, 7).unwrap();
        assert_eq!(budget.used(), first_charge);

        budget.reserve_vec(&mut values, 16).unwrap();
        assert!(budget.used() > first_charge);
    }

    #[test]
    fn budget_rejects_growth_before_vector_changes() {
        let budget = DecodeBudget::new(80);
        let mut values = Vec::<u64>::new();
        assert!(matches!(
            budget.reserve_vec(&mut values, 3),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert_eq!(values.capacity(), 0);
        assert_eq!(budget.used(), 0);
        assert!(matches!(
            budget.reserve_vec(&mut values, 1),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert_eq!(values.capacity(), 0);
    }

    #[test]
    fn allocation_limit_is_monotone_and_cannot_drop_below_charged_use() {
        let budget = DecodeBudget::new(1024);
        budget.charge(512).unwrap();

        budget.lower_limit(512).unwrap();
        assert!(matches!(
            budget.lower_limit(513),
            Err(DecodingError::InvalidParameter(_))
        ));
        assert!(matches!(
            budget.lower_limit(511),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert!(matches!(budget.charge(1), Err(DecodingError::MemoryLimitExceeded)));
    }
}
