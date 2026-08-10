# JitHub vNext Quiet UI Updates

This document defines the foundation we need to remove refresh flashes across
JitHub. Data can refresh aggressively, but the visible UI should update only the
smallest changed pieces while preserving focus, scroll position, selection, and
layout stability.

## Problem

Many WinUI pages currently bind directly to refreshed collections or detail
models. When a refresh replaces an entire collection or selected detail object,
the UI can blank, flash, jump, lose scroll position, or replay layout work that
the user can see.

The vNext data model should behave more like a keyed UI runtime:

- Keep stable identity for every visible row, card, widget, and detail section.
- Apply diffs instead of clearing and replacing collections.
- Update row-level properties only when values actually changed.
- Preserve scroll anchors and selection through refresh.
- Avoid showing loading placeholders when stale data already exists.

## Core Contracts

| Contract | Requirement |
| --- | --- |
| Stable identity | Every repeated item exposes a stable key such as issue id, PR id, repo id, notification id, gist id, or cache key. |
| Snapshot input | Services return immutable snapshots from cache/network. View models project snapshots into stable UI nodes. |
| Keyed collections | Pages use keyed observable collections that apply add, remove, move, and update operations without `Clear()` during refresh. |
| Row updates | Row view models expose `ApplySnapshot` and raise property changes only for changed values. |
| Refresh states | Cached data remains visible while refresh runs. A refresh error updates status, not the primary content. |
| Scroll anchors | Before applying list diffs, capture the first visible item key and offset where possible; restore after the diff. |
| Selection anchors | Selection is tracked by item key. If the selected item still exists after refresh, keep it selected. |
| Layout stability | Preview widgets and list rows keep stable height unless the user performs an explicit expand action. |

## Proposed Building Blocks

| Type | Purpose |
| --- | --- |
| `IStableItem` | Common interface for view items with `StableKey`. |
| `KeyedObservableCollection<TItem, TSnapshot>` | Applies keyed diffs to an observable collection without full replacement. |
| `IViewItemProjector<TSnapshot, TItem>` | Creates or updates a view item from a service snapshot. |
| `CollectionDiffOptions` | Controls move behavior, max reorder distance, and whether missing items are removed immediately or deferred. |
| `IRefreshSession` | Captures selection, scroll anchor, start time, cache state, and refresh result for one UI refresh. |
| `QuietRefreshController` | Runs refresh work, applies diffs on the UI thread in one batch, and reports telemetry. |
| `StableListViewBehavior` | Page-level behavior for preserving scroll/focus anchors around collection diffs. |
| `ListViewScrollAnchor` | Captures a WinUI list's scroll offset before selection/diff work and restores it across delayed layout passes. |

## Diff Rules

| Case | Behavior |
| --- | --- |
| Same key, same values | Do nothing. Do not raise property changes. |
| Same key, changed values | Update only changed properties on the existing row view model. |
| New key | Insert the new row at its target index. |
| Missing key | Remove only if the snapshot is authoritative for that query. For partial refresh failures, keep stale rows. |
| Reordered key | Move existing row when the target index changed enough to matter. Avoid churn from tiny timestamp jitter. |
| Selected item changed | Update detail fields in place if the selected key is the same. Do not reselect the item. |

## Page Migration Order

| Phase | Scope | Notes |
| --- | --- | --- |
| 1 | My Issues and repository Issues | Highest visible pain today. Migrate list rows, detail body, comments, filters, and selection. |
| 2 | Pull Requests | Reuse issue/PR work item projections and detail/comment diffing. |
| 3 | Home widgets | Apply keyed diffing to activity, repositories, recommendations, notifications, and metrics. |
| 4 | Shell repository rail and command search | Keep repository rail and suggestions stable during background refresh. |
| 5 | Commits and code surfaces | Preserve file diff scroll position and selected file while commit data refreshes. |

## Current Implementation Slice

The first no-flash slice is in place for issue selection:

- My Issues uses keyed row view items for cached list refreshes.
- Repository Issues preserves visible `GitHubIssue` object identity when detail
  refreshes return fresher issue data.
- My Issues and Repository Issues capture and restore `ListView` scroll anchors
  around item-click selection so WinUI selection/detail layout work does not pull
  the list away from the user's reading position.
- `JitHub.WinUI.Automation` includes scroll-click probes for both issue pages.
  The probes scroll the list, click the last visible issue, assert the selected
  row stays within a small movement tolerance, and capture before/after
  screenshots.

## Telemetry

Add privacy-safe events and metrics:

| Name | Type | Properties |
| --- | --- | --- |
| `ui.diff.applied` | Event | `page`, `section`, `result`, `duration_bucket` |
| `ui.diff.items_changed` | Metric | `page`, `section`, `cache_state` |
| `ui.refresh.preserved_selection` | Event | `page`, `section`, `result` |
| `ui.refresh.preserved_scroll` | Event | `page`, `section`, `result` |

Never log item keys, repo names, usernames, query text, titles, paths, branches,
markdown, code, or URLs.

## Acceptance Criteria

- Refreshing cached issue lists does not flash, blank, or reset scroll.
- Selecting an already-loaded issue updates the detail pane in under 50 ms
  perceived time.
- A background refresh never replaces the entire collection when keyed diffs can
  express the change.
- Refresh failures preserve stale visible rows and show status only.
- No page uses `ObservableCollection.Clear()` as the normal refresh path for
  cached query results.
- UI automation covers refresh while hovering, refresh while selected, and
  refresh after scrolling.

## Implementation Notes

Start with a small, page-local implementation for Issues only, then promote the
stable collection and refresh controller into shared primitives once the shape
holds up. The shared layer should be boring and test-heavy: most value comes
from making the default page implementation difficult to misuse.
