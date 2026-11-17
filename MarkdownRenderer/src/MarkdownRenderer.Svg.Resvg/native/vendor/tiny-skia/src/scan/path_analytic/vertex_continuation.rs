// Copyright (c) JitHub contributors. Licensed under MIT.
//
//! Constant-structure sweep update for a vertex where one active edge ends
//! and another edge starts.
//!
//! Curve flattening turns each outline into long y-monotone chains of very
//! short edges, so nearly every sweep event group is one such vertex. The
//! general group path removes the ending edge from the order treap, finds
//! the starting edge's rank with a treap search, and splits and merges the
//! treap to insert it. This module proves, per group, that the general path
//! would put the starting edge exactly where the ending edge was, and then
//! performs that same update in place.
//!
//! The proof has two parts. First, removing the ending edge's node replaces
//! it with the merge of its two subtrees, whose root path runs down the
//! right spine of its left subtree and the left spine of its right subtree,
//! interleaved by priority. A search for the starting edge in that tree
//! returns the ending edge's rank exactly when it would still descend toward
//! the removed node at every ancestor, go right at every node of that left
//! spine, and go left at every node of that right spine; any other turn
//! leaves the rank on one side of the slot. Checking those comparisons
//! decides the general path's rank without building the tree. Second, a
//! treap is the unique Cartesian tree of its in-order sequence and its
//! priorities, and edge priorities are distinct (a bijective mix of the edge
//! id). Writing the new edge and its priority into the old node and rotating
//! that node back into heap order therefore reproduces the general path's
//! tree node for node, including the reused node index.
//!
//! Only the pairs that the new edge forms with its two neighbors can have a
//! crossing that is not already scheduled; every other adjacent pair was
//! adjacent before this group, and its crossings were scheduled then.

use super::{Edge, Event, EventKind, OrderTree, compare_at_y_above};
use core::cmp::Ordering;

/// One active edge handed over to its successor at a shared event ordinate.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct VertexContinuation {
    pub(super) ended: usize,
    pub(super) started: usize,
}

/// Recognize a group of exactly one end event and one start event that the
/// general path would resolve by putting the starting edge at the ending
/// edge's rank. Returns that rank. `y` is the group's representative
/// ordinate, the one the general path's search compares at.
pub(super) fn find_vertex_continuation(
    group: &[Event],
    group_max_y: f64,
    y: f64,
    edges: &[Edge],
    tree: &OrderTree,
) -> Option<(VertexContinuation, usize)> {
    let [first, second] = group else {
        return None;
    };
    let (ended, started) = match (first.kind, second.kind) {
        (EventKind::End(ended), EventKind::Start(started))
        | (EventKind::Start(started), EventKind::End(ended)) => (ended, started),
        _ => return None,
    };
    // The general path only inserts a starting edge that extends past the
    // group, and only removes an ending edge that is active.
    if edges[started].b.y <= group_max_y || tree.by_edge[started].is_some() {
        return None;
    }
    let node = tree.by_edge[ended]?;

    // `insertion_rank` descends left exactly when this is true.
    let goes_left_of = |node: usize| {
        compare_at_y_above(&edges[started], &edges[tree.nodes[node].edge], y) == Ordering::Less
    };
    let mut cursor = tree.nodes[node].left;
    while let Some(spine) = cursor {
        if goes_left_of(spine) {
            return None;
        }
        cursor = tree.nodes[spine].right;
    }
    let mut cursor = tree.nodes[node].right;
    while let Some(spine) = cursor {
        if !goes_left_of(spine) {
            return None;
        }
        cursor = tree.nodes[spine].left;
    }
    let mut child = node;
    while let Some(parent) = tree.nodes[child].parent {
        if goes_left_of(parent) != (tree.nodes[parent].left == Some(child)) {
            return None;
        }
        child = parent;
    }
    Some((VertexContinuation { ended, started }, tree.rank_node(node)))
}

impl OrderTree {
    /// Hand `continuation.ended`'s node to `continuation.started` and restore
    /// treap priority order. The in-order sequence is unchanged except for
    /// that one entry.
    pub(super) fn continue_in_place(&mut self, continuation: VertexContinuation) {
        let Some(node) = self.by_edge[continuation.ended].take() else {
            debug_assert!(false, "a continued edge must be active");
            return;
        };
        self.nodes[node].edge = continuation.started;
        self.nodes[node].priority = Self::priority(continuation.started);
        self.by_edge[continuation.started] = Some(node);

        while let Some(parent) = self.nodes[node].parent {
            if self.nodes[node].priority >= self.nodes[parent].priority {
                break;
            }
            self.rotate_up(node);
        }
        loop {
            let left = self.nodes[node].left;
            let right = self.nodes[node].right;
            let child = match (left, right) {
                (Some(left), Some(right)) => {
                    if self.nodes[left].priority <= self.nodes[right].priority {
                        left
                    } else {
                        right
                    }
                }
                (Some(child), None) | (None, Some(child)) => child,
                (None, None) => break,
            };
            if self.nodes[child].priority >= self.nodes[node].priority {
                break;
            }
            self.rotate_up(child);
        }
    }

    /// Rotate `node` above its parent, preserving in-order sequence and the
    /// subtree sizes of every ancestor.
    fn rotate_up(&mut self, node: usize) {
        let Some(parent) = self.nodes[node].parent else {
            return;
        };
        let grandparent = self.nodes[parent].parent;
        if self.nodes[parent].left == Some(node) {
            self.nodes[parent].left = self.nodes[node].right;
            self.nodes[node].right = Some(parent);
        } else {
            self.nodes[parent].right = self.nodes[node].left;
            self.nodes[node].left = Some(parent);
        }
        // `fix` recomputes the size and re-parents both children.
        self.fix(parent);
        self.nodes[node].parent = grandparent;
        match grandparent {
            Some(grandparent) => {
                if self.nodes[grandparent].left == Some(parent) {
                    self.nodes[grandparent].left = Some(node);
                } else {
                    self.nodes[grandparent].right = Some(node);
                }
            }
            None => self.root = Some(node),
        }
        self.fix(node);
    }
}

#[cfg(test)]
mod tests {
    use super::super::{Point, insertion_rank};
    use super::*;
    use alloc::vec::Vec;

    type NodeSnapshot = (
        usize,
        u64,
        Option<usize>,
        Option<usize>,
        Option<usize>,
        usize,
    );

    fn snapshot(tree: &OrderTree) -> Vec<NodeSnapshot> {
        tree.nodes
            .iter()
            .map(|n| (n.edge, n.priority, n.left, n.right, n.parent, n.size))
            .collect()
    }

    fn vertical(id: usize, x: f64) -> Edge {
        Edge {
            a: Point { x, y: 0.0 },
            b: Point { x, y: 1.0 },
            winding: 1,
            id,
        }
    }

    #[test]
    fn in_place_continuation_builds_the_general_paths_exact_treap() {
        let mut state = 0x0ddb_a11c_0ffe_e000_u64;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        for _ in 0..400 {
            let edge_count = 2 + (next() % 40) as usize;
            let mut general = OrderTree::try_new(edge_count * 2).unwrap();
            let mut in_place = OrderTree::try_new(edge_count * 2).unwrap();
            for edge in 0..edge_count {
                let rank = (next() as usize) % (general.len() + 1);
                general.insert_at(edge, rank);
                in_place.insert_at(edge, rank);
            }
            // Churn the free list so node reuse is exercised too.
            for _ in 0..(next() % 4) {
                let edge = (next() as usize) % edge_count;
                if let Some(rank) = general.remove(edge) {
                    in_place.remove(edge);
                    general.insert_at(edge, rank);
                    in_place.insert_at(edge, rank);
                }
            }
            for _ in 0..8 {
                let ended = (next() as usize) % (edge_count * 2);
                let started = (next() as usize) % (edge_count * 2);
                if general.by_edge[ended].is_none() || general.by_edge[started].is_some() {
                    continue;
                }
                let rank = general.remove(ended).unwrap();
                general.insert_at(started, rank);
                in_place.continue_in_place(VertexContinuation { ended, started });

                assert_eq!(in_place.root, general.root);
                assert_eq!(in_place.by_edge, general.by_edge);
                assert_eq!(in_place.free, general.free);
                assert_eq!(snapshot(&in_place), snapshot(&general));
            }
        }
    }

    #[test]
    fn continuation_is_found_exactly_when_the_search_returns_the_old_rank() {
        let mut state = 0x7e57_ab1e_5eed_0001u64;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        let mut found_count = 0;
        let mut rejected_count = 0;
        for _ in 0..2_000 {
            let active = 1 + (next() % 24) as usize;
            // Few distinct abscissae and slopes force ties, and shuffled
            // insertion ranks make the active order inconsistent at times.
            let mut edges: Vec<Edge> = (0..=active)
                .map(|id| {
                    let x = (next() % 9) as f64 - 4.0;
                    let dx = (next() % 5) as f64 - 2.0;
                    Edge {
                        a: Point { x, y: 0.0 },
                        b: Point { x: x + dx, y: 1.0 },
                        winding: 1,
                        id,
                    }
                })
                .collect();
            let started = active;
            edges[started].a.y = 0.0;
            let mut tree = OrderTree::try_new(active + 1).unwrap();
            let sorted = next() % 2 == 0;
            for id in 0..active {
                let rank = if sorted {
                    insertion_rank(&tree, &edges, id, 0.0, true)
                } else {
                    (next() as usize) % (tree.len() + 1)
                };
                tree.insert_at(id, rank);
            }
            let ended = (next() as usize) % active;
            let end = Event {
                y: 0.0,
                x: edges[ended].b.x,
                kind: EventKind::End(ended),
            };
            let start = Event {
                y: 0.0,
                x: edges[started].a.x,
                kind: EventKind::Start(started),
            };

            let found = find_vertex_continuation(&[start, end], 0.0, 0.0, &edges, &tree);
            let old_rank = tree.rank_node(tree.by_edge[ended].unwrap());
            let mut general = OrderTree::try_new(active + 1).unwrap();
            for (rank, id) in tree.in_order().into_iter().enumerate() {
                general.insert_at(id, rank);
            }
            // A treap is determined by its order and priorities, so this
            // rebuild is the same tree; remove from it as the general path would.
            general.remove(ended);
            let general_rank = insertion_rank(&general, &edges, started, 0.0, true);
            assert_eq!(found.is_some(), general_rank == old_rank);
            if let Some((continuation, rank)) = found {
                assert_eq!(continuation, VertexContinuation { ended, started });
                assert_eq!(rank, old_rank);
                found_count += 1;
            } else {
                rejected_count += 1;
            }
        }
        assert!(found_count > 100 && rejected_count > 100);

        // Other group shapes always take the general path.
        let edges = [vertical(0, 0.0), vertical(1, 1.0)];
        let mut tree = OrderTree::try_new(4).unwrap();
        tree.insert_at(0, 0);
        let end = Event {
            y: 0.0,
            x: 0.0,
            kind: EventKind::End(0),
        };
        assert!(find_vertex_continuation(&[end], 0.0, 0.0, &edges, &tree).is_none());
        assert!(find_vertex_continuation(&[end, end], 0.0, 0.0, &edges, &tree).is_none());
    }
}
