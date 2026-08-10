# JitHub vNext Interface Plan

This document is the canonical plan for the next generation of JitHub's
interface, telemetry, cache, and performance work. It has been revised around
the new shell and Home direction: one combined nav/repository rail, one content
frame, a responsive Home widget board, and shell-owned modal overlays.

The project remains phased work. We should not turn the rest of the app into one
large rewrite.

## Current baseline

The vNext foundation now has a clear product shape:

- A single-frame app shell.
- A combined app navigation and repository rail.
- Global command search as the primary cross-app search and command surface.
- Home as a two-rail widget board with customizable widgets.
- Shell-owned modal overlays for app dialogs.
- Stale-first data surfaces that preserve visible content during refresh.
- UI automation for shell, Home, command search, widget board, and customize
  behavior.

See [JitHub vNext Shell And Home Baseline](jithub-vnext-shell-home-baseline.md)
for the detailed shell/Home contract.

See [JitHub vNext Quiet UI Updates](jithub-vnext-quiet-ui-updates.md) for the
keyed diffing and no-flash refresh foundation that remaining page work should
build on.

## Product principles

JitHub vNext should feel like a production-grade native GitHub client for
developers who live in issues, pull requests, commits, code, notifications, and
repository context all day.

The rest of the work should follow these principles:

- Build in dependency order. Shared telemetry, cache, shell, navigation, modal,
  and widget infrastructure come before page-specific redesigns.
- Preserve native Windows behavior. Use WinUI primitives, app design tokens,
  accessible controls, keyboard navigation, and stable focus handling.
- Avoid browser metaphors. Do not make primary pages feel like websites, do not
  use visible refresh buttons as the normal data model, and avoid browser
  link-out icons unless a control truly opens an external browser.
- Optimize perceived performance first. Cached data should render immediately,
  network refresh should happen in the background, and page chrome should avoid
  full blank/loading swaps.
- Make dense workflows calm. The interface should support high information
  density without visual noise, layout jumps, clipped text, or unclear selected
  states.
- Treat telemetry as a product feature. Feature usage, cache health, and
  perceived latency should be measurable while avoiding user, repository, and
  content identifiers.
- Build page by page. Every major page gets an implementation spec and visual QA
  before work moves to the next page.

## Reference concepts

The supplied concept images are directional references, not literal
implementation specs. The parts we are carrying forward are:

- Dense dark native shell.
- Combined navigation and repository rail.
- Centered command search with shortcut badges.
- Two-rail Home dashboard with compact widgets.
- Detail pages that use list/detail/inspector structure where it fits.
- Compact controls, segmented filters, icon actions, and native hover/pressed
  states.
- Serif content headings, sans-serif UI chrome, and mono code/diff/technical
  surfaces.
- Dark green and black surface system with restrained borders, subtle depth,
  crisp selected states, and colored semantic accents.

Before implementation of any page, create a page-specific design spec that locks
layout, visible copy, responsive behavior, data states, keyboard behavior,
automation ids, and visual QA expectations.

## Architecture layers

| Layer | Responsibility | Current direction |
| --- | --- | --- |
| Presentation | App shell, page surfaces, design tokens, reusable XAML primitives, widget cards, list/detail layouts, inspector panels, markdown/code/diff surfaces. | Extend the shell/Home token language into page primitives. Avoid page-local chrome that competes with the shell. |
| Interaction | Keyboard shortcuts, command search, selection stability, focus management, modal behavior, item traversal. | Use `Ctrl+K` for global command search, shell nav for page routing, shell modal overlays for dialogs, and page-owned traversal for dense lists. |
| Data access | GitHub REST client, optional GraphQL reads, auth boundaries, request queue, pagination, rate-limit handling. | Keep REST as the default. Add GraphQL only where it materially reduces fan-out. Keep OAuth and current API version until a dedicated auth/API phase. |
| Cache | Stale-first query cache, SQLite metadata, payload storage, TTL policy, invalidation, background refresh. | Every redesigned page should use stale-first data and preserve existing rows while refreshing. |
| Telemetry | Microsoft Store custom events, local diagnostics, privacy rules, event taxonomy, performance timing. | Track shell/page feature usage and latency buckets with sanitized properties only. |
| Feature/page | Shell, Home, Settings, My Issues, repository issues, PRs, commits, code, notifications, stars, gists, profile, explore. | Shell/Home are the baseline. Remaining pages should be rebuilt against that baseline one page family at a time. |
| Verification | Unit tests, view-model tests, UI automation, performance budgets, visual QA. | Each page phase must add view-model coverage, UI automation for primary states, and screenshot review at common desktop sizes. |

## Data and telemetry contracts

| Contract | Rule |
| --- | --- |
| Stale-first reads | Cached content appears first. Background refresh updates in place. Failed refresh does not blank existing content. |
| Section isolation | One failed section should not poison an entire page or shell surface. |
| User partitioning | Cache data is partitioned by authenticated GitHub user id. |
| Conditional reads | Use validators and pagination where GitHub supports them. |
| Mutations | Mutations should patch or invalidate affected query keys. |
| Store telemetry | Store custom events are best-effort and non-blocking. |
| Local diagnostics | Local diagnostics remain useful even when Store APIs are unavailable. |
| Privacy | Never log repo names, usernames, query text, URLs, issue/PR titles, notification titles, branches, markdown, code, commit messages, or tokens. |

## Implemented foundation

| Area | Status | Notes |
| --- | --- | --- |
| Phase 0 foundations | Implemented foundation | Telemetry/cache/query infrastructure exists and should continue to be expanded as pages migrate. |
| Settings and diagnostics | Implemented first user-facing surface | Settings exposes diagnostics/cache controls and app preferences. Further polish can be handled as small follow-ups. |
| Shell | New baseline | Single-frame shell with combined nav/repository rail, command search, Stars and Gists routes, and no visible workspace tabs. |
| Home | New baseline | Two-rail widget board, compact side drawer, widget customization, stale-first dashboard data, and shell-owned modal customization. |
| Automation | Active baseline | Shell/Home probes cover responsive layout, nav, hover states, search, repo click, widget board, customization, and view-all routes. |

Implemented does not mean frozen. It means future phases should treat this as
the platform and make targeted improvements rather than reopening the shell
model.

## Remaining page strategy

The rest of vNext should be built in two kinds of phases:

- Personal/global work pages: My Issues, My Pull Requests, Stars, Gists,
  Notifications, Explore, Profile.
- Repository work pages: repository overview/code, repository issues,
  repository pull requests, commits, diffs, files, inspectors.

The shell nav currently routes to personal/global surfaces. Repository-specific
work starts from a selected repository and should be reachable through
repository content, command search, and contextual actions.

## Revised phased roadmap

| Phase | Name | Scope | Outcome |
| --- | --- | --- | --- |
| 0 | Telemetry and cache architecture | Shared telemetry, diagnostics, cache, request transport, stale-first query service. | Foundation for fast cached pages and privacy-safe product analytics. |
| 1 | Settings and diagnostics | Settings redesign, diagnostics export/clear, cache controls, telemetry preferences. | Users can inspect and manage the new foundations. |
| 2 | Shell foundation | Combined rail, single content frame, command search, shell routing, Stars/Gists entry points. | The app has a stable native shell and no visible workspace tabs. |
| 3 | Home widget board | Two-rail Home, responsive side drawer, widgets, customization, shell modal behavior. | Home is the baseline for dense native UI, preview widgets, and customization. |
| 4 | My Issues | Personal issue inbox from GitHub Search API, filters, cached rows, detail preview or route to repository issue detail. | The Issues nav becomes a real daily-work page for assigned or involved issues. |
| 5 | Repository Issues | Repository issue list/detail/inspector, comments, labels, assignees, milestone, close/reopen, inline links. | First full list/detail/inspector workspace and template for PRs. |
| 6 | My Pull Requests | Personal PR inbox, review/status filters, cached rows, route into repository PR detail. | Pull Requests nav becomes a real personal work queue. |
| 7 | Repository Pull Requests | PR conversation/detail/inspector, reviews, changed files, checks summary, merge/update actions. | PR work reaches feature parity with high-performance navigation. |
| 8 | Commits and diffs | Repository commits page, commit detail, changed files, unified/split diff, file filter, neighbor prefetch. | Commit traversal and diff reading match the vNext density/performance bar. |
| 9 | Code and repository overview | Repository overview, file tree, code browser, breadcrumbs, README/markdown/code preview, branch handling. | Repository browsing shares shell/cache/design behavior with the rest of the app. |
| 10 | Notifications | Full notifications page, notification badges, read/unread state, polling headers, notification routing. | Notifications become a first-class native workflow. |
| 11 | Stars, Gists, Explore, Profile polish | Finish lower-depth pages, profile details, search/explore results, Stars/Gists polish, empty/error states. | All shell nav items feel production-ready. |
| 12 | Release polish | Light/high-contrast QA, localization-sensitive strings, Store screenshots, performance pass, bug bash. | vNext is ready for packaging and preview/release channels. |

Phases 0-3 are the foundation now. The next highest-leverage work is Phase 4
and Phase 5 because issues are the clearest place to prove the dense
list/detail/inspector model.

## Phase 4 proposal: My Issues

| Area | Spec |
| --- | --- |
| Data | Use GitHub Search issues/PRs endpoint with `is:issue is:open assignee:{login}` and optional local filter chips. |
| Layout | Single page in shell content frame. Dense list with status, repo, labels, comments, age, and updated time. |
| Detail behavior | Start with route-to-detail or lightweight inline preview. Do not overbuild the full repository issue workspace here. |
| Cache | Stale-first search results. Refresh in background. Preserve cached rows on error. |
| Actions | Open issue, open repository, copy link, optional mark/read state only if sourced safely. |
| Telemetry | `my_issues.opened`, `my_issues.filter.changed`, `my_issues.item.opened`, `my_issues.refresh.completed`. |
| Automation | Nav click, cached rows, empty state, error state, filter chips, item click route, responsive screenshot. |

## Phase 5 proposal: Repository Issues

| Area | Spec |
| --- | --- |
| Data | Repository issues, issue detail, comments, labels, assignees, milestones, reactions, linked references where available. |
| Layout | Three-part workspace: virtualized issue list, detail conversation, inspector. |
| Navigation | Repository issue routes come from repo context, command search, Home activity links, My Issues rows, and inline links. |
| Performance | Neighbor issue prefetch. Cached selected issue detail should swap under 50 ms perceived time. |
| Editing | Comment composer, close/reopen, labels, assignees, milestone actions. Keep destructive actions confirmed. |
| Inspector | Status, assignees, labels, milestone, linked items, digest/summary if locally available. |
| Automation | List/detail/inspector visual QA, arrow traversal, comment composer, labels, close/reopen confirmation, refresh without blanking. |

## Phase 6 proposal: My Pull Requests

| Area | Spec |
| --- | --- |
| Data | GitHub Search issues/PRs endpoint with `is:pr is:open involves:{login}` and filters for authored, review requested, mentioned, merged/closed. |
| Layout | Personal PR queue in the shell content frame. Rows show repo, title, status, review/check summary where cheaply available. |
| Route | Clicking a row opens repository PR detail. |
| Cache | Stale-first rows with independent refresh errors. |
| Automation | Nav click, filter chips, cached rows, empty/error state, row route, responsive screenshots. |

## Phase 7 proposal: Repository Pull Requests

| Area | Spec |
| --- | --- |
| Data | PR detail, conversation, reviews, review comments, commits, changed files, labels, assignees, reviewers, checks summary. |
| Layout | Reuse repository issue workspace primitives with PR-specific tabs or segmented subviews inside the content area. |
| Actions | Review, comment, assign, label, request reviewers, update branch, merge, close/reopen where permissions allow. |
| Performance | Preserve selected PR, selected subview, scroll positions, and draft comments during refresh. |
| Automation | Conversation, files, checks, merge disabled/enabled states, review/comment flow, refresh stability. |

## Phase 8 proposal: Commits and diffs

| Area | Spec |
| --- | --- |
| Data | Repository commits, commit detail, file changes, diff payloads, branch refs. |
| Layout | Commit list plus detail/diff pane. File list beside unified/split diff surface. |
| Performance | Cache commit details and diff payloads. Prefetch neighboring commits and selected file diffs. |
| Controls | Branch selector, file filter, unified/split toggle, copy hash, browse files. |
| Automation | Commit selection traversal, diff mode switch, file filter, copy hash, responsive screenshots. |

## Phase 9 proposal: Code and repository overview

| Area | Spec |
| --- | --- |
| Data | Repository metadata, branches, tree, blobs, README, languages, releases where useful. |
| Layout | Repository overview and code browser that fit the new shell. No duplicated repository rail. |
| Cache | SHA-addressed tree/blob cache can use longer TTLs. Branch and metadata stay mutable. |
| Controls | Breadcrumbs, branch selector, file tree, file preview, markdown/code viewer. |
| Automation | Repo click route, branch change, folder navigation, file preview, markdown/code rendering, responsive screenshots. |

## Phase 10 proposal: Notifications

| Area | Spec |
| --- | --- |
| Data | GitHub notifications API with `Last-Modified` and `X-Poll-Interval`. |
| Layout | Notification inbox with unread/read filters, repo/source grouping, route targets, and compact rows. |
| Actions | Mark read, mark all read, open target, mute/unsubscribe where supported. |
| Cache | Stale-first notifications. Polling obeys GitHub headers. |
| Automation | Notification nav/badge, filters, mark read, route target, polling-safe refresh state. |

## Acceptance criteria

The relevant phase is complete only when these criteria are true:

- Cached page open has under 150 ms perceived time.
- Cached issue, PR, and commit traversal has under 50 ms perceived detail swap
  time where traversal exists.
- Background refresh never causes full-page blanking, selection loss, or draft
  loss.
- Layouts stay stable during loading, refresh, filter, mutation, and narrow
  window states.
- Text does not overlap or clip in common desktop, medium, and snapped sizes.
- Telemetry contains no repository names, usernames, URLs, issue titles, PR
  titles, commit messages, markdown, code, query text, branches, or tokens.
- Page dialogs use shell modal overlays or properly constrained WinUI dialogs.
- Keyboard navigation works for the primary workflow.
- Each phase includes unit/view-model coverage, UI automation, and screenshot
  review.
- Each page includes empty, loading-from-cache, refreshing, error, and
  unauthorized states.

## Implementation defaults

- Use the existing shell and Home baseline as the product source of truth.
- Use app token brushes and shared primitives before adding page-specific
  styling.
- Use Microsoft Store custom events plus local diagnostics.
- Use stale-first cache behavior, not full offline-first behavior.
- Keep the current OAuth token model unless a phase explicitly changes auth.
- Prefer REST for existing reads and mutations.
- Add GraphQL only for shallow aggregate reads where it materially reduces REST
  fan-out.
- Treat GitHub website-only concepts, such as Trending, honestly. Do not label
  locally inferred or search-based data as first-party GitHub Trending.
- Keep Projects hidden until a GraphQL ProjectV2 design and permissions plan is
  written.

## Open planning items

Resolve these before or during the next page phase:

- Decide whether My Issues opens a lightweight inline preview or routes directly
  to the repository issue workspace.
- Define the shared list/detail/inspector primitive before repository Issues.
- Decide how command search should rank personal issue/PR results once those
  pages exist.
- Decide which mutations are safe for Phase 5 and which should wait for PR/code
  parity.
- Add performance measurements for cached page open and cached item traversal to
  the automation harness.
- Define high-contrast visual QA expectations for shell, Home, and the first
  dense workspace.
