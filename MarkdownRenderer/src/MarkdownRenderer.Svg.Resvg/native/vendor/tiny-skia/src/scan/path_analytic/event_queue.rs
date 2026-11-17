// Copyright (c) JitHub contributors. Licensed under MIT.
//
//! Sweep event queue for the analytic rasterizer.
//!
//! Vertex and clip events are all known before the sweep starts, so they are
//! sorted once and consumed from the end of a vector. Only crossing events,
//! which the sweep discovers as it advances, live in a binary heap. Taking
//! the greater head of the two stores yields exactly the sequence a single
//! heap of every event would pop, because `Event` has a total order and no
//! two distinct queued events compare equal.
//!
//! Fixed events are stored as three words whose unsigned lexicographic order
//! is `Event`'s order: the coordinates map through the integer image of
//! `f64::total_cmp`, and the kind keeps `event_key`'s order. The encoding is
//! lossless, so a popped event is bit-identical to the one pushed, and the
//! one-time sort compares integers instead of floats and enums.

use alloc::collections::BinaryHeap;
use alloc::vec::Vec;

use super::{CoverageError, Event, EventKind, Limits};

const KIND_SHIFT: u32 = 62;
const ID_MASK: u64 = (1 << KIND_SHIFT) - 1;
const START: u64 = 0;
const END: u64 = 1;
const CLIP: u64 = 3;

/// A start, end, or clip event. Every word is complemented because `Event`
/// orders the earliest event greatest, for the max-heap.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
struct FixedEvent([u64; 3]);

impl FixedEvent {
    fn encode(event: Event) -> Option<Self> {
        let kind = match event.kind {
            EventKind::Start(id) => (START << KIND_SHIFT) | fixed_id(id)?,
            EventKind::End(id) => (END << KIND_SHIFT) | fixed_id(id)?,
            EventKind::Clip => CLIP << KIND_SHIFT,
            EventKind::Cross(..) => return None,
        };
        Some(Self([
            !total_order_word(event.y),
            !total_order_word(event.x),
            !kind,
        ]))
    }

    fn decode(self) -> Event {
        let [y, x, kind] = self.0.map(|word| !word);
        let id = (kind & ID_MASK) as usize;
        Event {
            y: from_total_order_word(y),
            x: from_total_order_word(x),
            kind: match kind >> KIND_SHIFT {
                START => EventKind::Start(id),
                END => EventKind::End(id),
                _ => EventKind::Clip,
            },
        }
    }
}

fn fixed_id(id: usize) -> Option<u64> {
    u64::try_from(id).ok().filter(|&id| id <= ID_MASK)
}

/// The unsigned word whose order is `f64::total_cmp`'s order, as the standard
/// library defines it, with the sign moved into unsigned order.
fn total_order_word(value: f64) -> u64 {
    let bits = value.to_bits() as i64;
    let ordered = bits ^ (((bits >> 63) as u64) >> 1) as i64;
    (ordered as u64) ^ (1 << 63)
}

fn from_total_order_word(word: u64) -> f64 {
    // The magnitude flip keeps the sign bit, so it is its own inverse.
    let ordered = (word ^ (1 << 63)) as i64;
    f64::from_bits((ordered ^ (((ordered >> 63) as u64) >> 1) as i64) as u64)
}

pub(super) struct EventQueue {
    /// Start, end, and clip events in ascending order, so the next event to
    /// process is last.
    fixed: Vec<FixedEvent>,
    /// Crossing events scheduled while the sweep runs.
    crossings: BinaryHeap<Event>,
}

impl EventQueue {
    pub(super) fn new() -> Self {
        Self {
            fixed: Vec::new(),
            crossings: BinaryHeap::new(),
        }
    }

    /// Reserve exact storage for `count` fixed events. The caller admits
    /// `capacity()` events' bytes against its budgets after this.
    pub(super) fn try_reserve_fixed(&mut self, count: usize) -> Result<(), CoverageError> {
        self.fixed
            .try_reserve_exact(count)
            .map_err(|_| CoverageError::AllocationFailure)
    }

    /// Append one start, end, or clip event inside the reserved storage.
    /// Fixed events are counted against the same event limit as crossings.
    pub(super) fn push_fixed(
        &mut self,
        event: Event,
        event_count: &mut usize,
        limits: Limits,
    ) -> Result<(), CoverageError> {
        let next_count = event_count
            .checked_add(1)
            .ok_or(CoverageError::EventLimit)?;
        if next_count > limits.max_events {
            return Err(CoverageError::EventLimit);
        }
        let encoded = FixedEvent::encode(event).ok_or(CoverageError::EdgeLimit)?;
        if self.fixed.len() == self.fixed.capacity() {
            // Growing here would bypass the caller's byte admission.
            return Err(CoverageError::ActiveWorkLimit);
        }
        self.fixed.push(encoded);
        *event_count = next_count;
        Ok(())
    }

    /// Order the fixed events once, after the last `push_fixed` and before
    /// the first `peek` or `pop`. The sort is in place and allocation-free.
    pub(super) fn sort_fixed(&mut self) {
        self.fixed.sort_unstable();
    }

    pub(super) fn crossings_mut(&mut self) -> &mut BinaryHeap<Event> {
        &mut self.crossings
    }

    /// Live capacity of both stores in `Event`-sized units, rounded up, for
    /// live-byte accounting.
    pub(super) fn capacity(&self) -> usize {
        self.fixed_capacity()
            .saturating_add(self.crossings.capacity())
    }

    /// Live capacity of the fixed store in `Event`-sized units, rounded up.
    /// Crossing-heap growth is admitted against every other live byte,
    /// including this store.
    pub(super) fn fixed_capacity(&self) -> usize {
        self.fixed
            .capacity()
            .saturating_mul(core::mem::size_of::<FixedEvent>())
            .div_ceil(core::mem::size_of::<Event>())
    }

    pub(super) fn peek(&self) -> Option<Event> {
        let fixed = self.fixed.last().map(|fixed| fixed.decode());
        match (fixed, self.crossings.peek()) {
            (Some(fixed), Some(&crossing)) => Some(if crossing > fixed { crossing } else { fixed }),
            (fixed, crossing) => fixed.or(crossing.copied()),
        }
    }

    pub(super) fn pop(&mut self) -> Option<Event> {
        let fixed = self.fixed.last().map(|fixed| fixed.decode());
        match (fixed, self.crossings.peek()) {
            (Some(fixed), Some(crossing)) if *crossing > fixed => self.crossings.pop(),
            (Some(fixed), _) => {
                self.fixed.pop();
                Some(fixed)
            }
            (None, _) => self.crossings.pop(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(y: f64, x: f64, kind: EventKind) -> Event {
        Event { y, x, kind }
    }

    #[test]
    fn merged_stores_pop_in_single_heap_order() {
        let mut state = 0x5eed_1234_9876_abcdu64;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        for _ in 0..64 {
            let mut fixed = Vec::new();
            let mut crossings = Vec::new();
            for id in 0..48usize {
                // Few distinct ordinates and abscissae force kind/id ties.
                let y = (next() % 7) as f64 * 0.25 - 0.5;
                let x = (next() % 5) as f64 - 2.0;
                fixed.push(event(y, x, EventKind::Start(id)));
                fixed.push(event(y + (next() % 3) as f64, x, EventKind::End(id)));
                if next() % 3 == 0 {
                    crossings.push(event(y, x, EventKind::Cross(id, (id + 1) % 48)));
                }
            }
            fixed.push(event(-0.0, f64::NEG_INFINITY, EventKind::Clip));
            fixed.push(event(4.0, f64::NEG_INFINITY, EventKind::Clip));

            let mut reference: BinaryHeap<Event> = fixed.iter().copied().collect();
            let mut queue = EventQueue::new();
            queue.try_reserve_fixed(fixed.len()).unwrap();
            let mut count = 0;
            for &e in &fixed {
                queue.push_fixed(e, &mut count, Limits::default()).unwrap();
            }
            queue.sort_fixed();
            // Interleave crossing pushes with pops, as the sweep does.
            let mut pending = crossings.into_iter();
            loop {
                if let Some(crossing) = pending.next() {
                    queue.crossings_mut().push(crossing);
                    reference.push(crossing);
                }
                assert_eq!(queue.peek().as_ref(), reference.peek());
                let popped = queue.pop();
                assert_eq!(popped, reference.pop());
                if popped.is_none() && pending.len() == 0 {
                    break;
                }
            }
            assert_eq!(count, fixed.len());
        }
    }

    #[test]
    fn fixed_encoding_is_lossless_and_orders_exactly_like_events() {
        let values = [
            f64::NEG_INFINITY,
            -1.0e300,
            -1.5,
            -f64::MIN_POSITIVE,
            -0.0,
            0.0,
            f64::MIN_POSITIVE,
            1.0,
            1.0 + f64::EPSILON,
            f64::MAX,
            f64::INFINITY,
            f64::NAN,
            -f64::NAN,
        ];
        let mut encoded = Vec::new();
        for (index, &y) in values.iter().enumerate() {
            for &x in &values {
                for kind in [
                    EventKind::Start(index),
                    EventKind::End(index),
                    EventKind::Start(ID_MASK as usize),
                    EventKind::Clip,
                ] {
                    let original = event(y, x, kind);
                    let fixed = FixedEvent::encode(original).unwrap();
                    let decoded = fixed.decode();
                    assert_eq!(decoded.y.to_bits(), y.to_bits());
                    assert_eq!(decoded.x.to_bits(), x.to_bits());
                    assert_eq!(decoded.kind, kind);
                    encoded.push((original, fixed));
                }
            }
        }
        for &(a, fixed_a) in &encoded {
            for &(b, fixed_b) in &encoded {
                assert_eq!(fixed_a.cmp(&fixed_b), a.cmp(&b));
            }
        }
        assert!(FixedEvent::encode(event(0.0, 0.0, EventKind::Cross(0, 1))).is_none());
        assert!(
            FixedEvent::encode(event(0.0, 0.0, EventKind::Start(ID_MASK as usize + 1))).is_none()
        );
    }

    #[test]
    fn fixed_events_never_grow_past_their_admitted_reservation() {
        let mut queue = EventQueue::new();
        queue.try_reserve_fixed(1).unwrap();
        let capacity = queue.fixed.capacity();
        let mut count = 0;
        for id in 0..capacity {
            queue
                .push_fixed(
                    event(0.0, 0.0, EventKind::Start(id)),
                    &mut count,
                    Limits::default(),
                )
                .unwrap();
        }
        assert_eq!(
            queue.push_fixed(
                event(0.0, 0.0, EventKind::Start(capacity)),
                &mut count,
                Limits::default()
            ),
            Err(CoverageError::ActiveWorkLimit)
        );
        assert_eq!(queue.fixed.capacity(), capacity);
        assert!(queue.fixed_capacity() <= capacity);
        assert_eq!(count, capacity);
    }

    #[test]
    fn fixed_events_share_the_event_limit() {
        let mut queue = EventQueue::new();
        queue.try_reserve_fixed(4).unwrap();
        let limits = Limits {
            max_events: 2,
            ..Limits::default()
        };
        let mut count = 1;
        queue
            .push_fixed(event(0.0, 0.0, EventKind::Start(0)), &mut count, limits)
            .unwrap();
        assert_eq!(
            queue.push_fixed(event(0.0, 0.0, EventKind::Start(1)), &mut count, limits),
            Err(CoverageError::EventLimit)
        );
        assert_eq!(count, 2);
    }
}
