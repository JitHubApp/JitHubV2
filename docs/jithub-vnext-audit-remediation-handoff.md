# JitHub vNext Audit Remediation Handoff

Status date: August 19, 2026

Branch: `codex/vnext-full-audit-remediation`

Base: `1010c15cc8c41d7af143ffa1ba41e3464933c858` (`origin/main` when the branch was created)

Handoff commit: the commit containing this document

## Purpose

This is the durable recovery record for the full vNext convergence work. It is
written so a new agent can clone or switch to the branch and continue without
the prior conversation. The detailed finding history remains in
`docs/jithub-vnext-full-product-audit.md`; this document records current
architecture, shipped behavior, verification, remaining work, environment
constraints, and exact restart commands.

Do not infer unfinished work from deleted legacy files. This branch replaces
large portions of the original application with canonical vNext owners. Do not
restore retired pages, view models, converters, fake tabs, or duplicate data
paths merely because they existed on `main`.

## Current Truth

- The source implementation for the recorded full-product audit is present.
- All audit tracker rows were implemented and page-specific tests/probes passed.
- Eight findings from the last data/security/performance review and the final
  first-frame selection repairs are implemented and verified with focused tests,
  the complete Debug/Release matrices, and live native probes.
- The canonical exact ten-iteration Warm run passes all 55 evaluations without
  changing budgets. Cached-selection p95 is `36.06ms` for My Issues, `33.01ms`
  for My Pull Requests, `42.67ms` for Gists, `34.75ms` for Repository Code,
  `29.64ms` for Repository Issues, `31.91ms` for Repository Pull Requests, and
  `37.52ms` for Repository Commits. Startup p95 is `1149.77ms` against `1500ms`.
  `artifacts/performance/vnext-publication-full-eight.json` is controlling.
- Two final review passes covered WinUI lifecycle/accessibility/concurrency and
  data/security/performance/resource ownership. Their actionable findings were
  fixed and focused validation was rerun.
- A prior post-review full-matrix rerun recorded one My Pull Requests host-input
  outlier and emitted a FlaUI COM-wrapper finalizer fault during shutdown after
  writing its report. Two isolated reruns passed, and the August 13 exact full
  matrix then passed `55/55` across all 80 measured launches with a clean process
  exit. The outlier and shutdown fault are retained as superseded history, not
  active release risk.
- The recovered batch passed consolidated publication review and is ready to be
  committed as one intentional remediation set. Generated outputs remain ignored
  evidence and must not enter the commit.
- The August 19 desktop cleanup is implemented and verified: normal Visual
  Studio launches no longer package pseudo-localized strings, duplicate
  profile/settings rail routes are removed, affected headers and navigation rows
  are aligned, the Stars category color picker has a default swatch preview, and
  Home quick actions are uniform global routes. A Visual Studio x64 Debug solution
  build and all 2,555 WinUI tests pass from this source state.
- Shared CheckBox colors now target WinUI's indicator fill and stroke resources,
  leaving the full label row transparent. The New Repository and sign-out dialogs
  were visually verified in dark theme, and New Repository was also verified in
  light theme, for both unchecked and checked states. The Visual Studio x64 Debug
  app build and 19 focused control-catalog/palette tests pass after this repair.
- Repository issue and pull-request detail headers now use paired expanded and
  compact visual trees animated by Community Toolkit Labs `TransitionHelper`.
  Scrolling down hides the expanded chrome; returning within 8 DIP of the top or
  accumulating 64 DIP of upward travel restores it. A subsequent 24 DIP downward
  move can hide it again. Reduced-motion mode resets directly to the target state.
- Pull-request scroll ownership is re-elected for Conversation, Files, Commits,
  Reviews, and Timeline, so the shy-header behavior follows every section rather
  than only the conversation. The compact overlay uses the transient acrylic
  brush while section content scrolls beneath it, with a scrollable content inset
  preventing first-frame occlusion at compact widths.
- Issue and pull-request content surfaces translate through the header height
  delta during each morph. The translation is cleared in the same forced layout
  pass that commits the new `Auto` row height, preventing the old expanded row
  from flashing as an empty band before content moves into the reclaimed space.
  Reversing an in-flight morph retargets the content from its current position.
- Repository issue and pull-request lists now keep only state tabs, search, and
  the primary create action visible at rest. Scope/sort/order controls live in a
  localized, accessible filter flyout with full-height native ComboBoxes. State
  tabs align with search and card content, and list identity chips use a small
  rounded-rectangle radius instead of a pill silhouette.
- The issue and pull-request on-demand comment composers use a bounded 440-DIP
  surface inside a 460-DIP flyout presenter, preventing right-edge clipping.
  Accent button foregrounds are explicit, and comment Markdown now inherits the
  same inset surface contract as issue/PR body Markdown.
- Content dialogs now use stable responsive width and height metrics owned by
  the shared presenter. Editor dialogs use an 840x720 preferred surface that
  contracts with the window, while their content scrolls instead of resizing
  the dialog when switching between Markdown Write and Preview modes.
- Issue comments, pull-request conversation comments, review threads, and review
  replies share one permission-aware interaction bar. Reactions render inline
  with native emoji and counts; available actions include quote reply, copy link,
  copy Markdown, edit, issue-comment pin/unpin, minimize/unminimize, and delete.
  Review replies are visually integrated into their parent discussion and local
  minimization state is retained across the REST refresh that follows a GraphQL
  mutation.
- Generated outputs are ignored and may be present locally after recovery. They
  are evidence only and must remain uncommitted.

## Product Invariants

These are design and architecture requirements, not suggestions:

- JitHub is a Windows native productivity app, not a website.
- The shell is a single-frame workspace with one combined navigation/repository
  rail. Do not reintroduce visible workspace tabs.
- Structural pane widths are owned by the page/workspace, never by selected
  content. Mode changes must not resize a page.
- Keep identity, navigation, commands, and structural rails fixed where useful;
  scroll the active content region instead of making the entire page a website.
- Responsive layouts must preserve all actions through inline panes, compact
  menus, or animated edge drawers without permanent top-row waste.
- Cached data renders first. Refresh happens quietly in the background and must
  not blank content, recreate keyed rows, reset selection, or move scroll
  anchors.
- There are no browser-style manual refresh workflows on normal pages.
- Telemetry is identifier-free. Never send login, repository, query, title,
  branch, URL, path, Markdown, code, commit message, category name, or content.
- User-facing errors are localized and sanitized. Raw exception/API/transport
  text belongs only in diagnostics.
- GitHub links route internally when JitHub owns the destination. External
  browser launch is explicit and secondary.
- Use Fluent/WinUI controls and the app control catalog before inventing custom
  interaction semantics. Custom controls must implement hover, pressed, focus,
  disabled, keyboard, UI Automation, theme, and High Contrast behavior.

The repository-level version of these rules is in `AGENTS.md`.

## Architecture Delivered

### Shell And Routing

- `ShellPage` and `ShellPageViewModel` own the single-frame shell, combined
  nav/repository rail, global command search, active route, repository context,
  compact rail drawer, notifications, and title-bar profile/settings entries.
- Canonical routes cover Home, My Issues, My Pull Requests, Stars, Gists,
  Notifications, Profile, Settings, Search, Repository Management, and repo
  Code/Issues/Pull Requests/Commits.
- Repository route preparation is coordinated by
  `RepositoryRoutePrefetchCoordinator` and
  `RepoCodeNavigationPreparationCache`. Code/commit prefetch is latest-wins,
  cancellable, bounded, eviction-aware, and drained during shutdown.
- Route ownership and deduplication rules are documented in
  `docs/jithub-vnext-route-ownership.md`.

### Responsive Workspace

- `AdaptiveWorkspace` provides leading, primary, and trailing panes with inline,
  left-drawer, right-drawer, and hidden placements.
- `SlideDrawerAnimator` handles reversible, interruption-safe open/close motion,
  light dismiss, Escape, focus restoration, and stable toggle locations.
- Shell and page breakpoints are coordinated through
  `Services/Layout/ShellResponsiveLayout.cs` and the adaptive layout policy.
- The final Issues regression fix reserves a 32-pixel workspace structural
  inset when determining when the app rail may remain inline. At an actual
  `1280x672` desktop-limited window, the app rail now collapses before the issue
  list, preserving the required inspector -> app rail -> list collapse order.
- Requested UI test dimensions may be capped by the current desktop work area.
  The harness records actual bounds; the final run observed a maximum of
  `1280x672` for larger requests.

### Data, Cache, And Quiet Updates

- Phase 0 infrastructure lives under `Services/Phase0`:
  - REST transport with conditional requests, ETag/Last-Modified, 304 reuse,
    rate-limit metadata, retry delay, and request priority lanes.
  - Account-partitioned stale-first query service and SQLite metadata store.
  - Payload/image stores, TTLs, tags, invalidation, size caps, LRU cleanup, and
    in-flight request deduplication.
  - Cache registry and settings diagnostics/clear operations.
- Page facades under `Dashboard`, `Me`, `Issues`, `PullRequests`, `Commits`,
  `CodeViewer`, `Profile`, `Repositories`, `Stars`, `Gists`, and `Notifications`
  route production reads through the cache/query architecture.
- Lists use keyed reconciliation instead of `Clear()`/repopulate. Viewport
  anchors, selected rows, drafts, and active sections are preserved.
- Issues and comments load progressively: page one is published immediately,
  issue body is available before comment pagination completes, later pages
  reconcile by key, and cached content stays visible on section failure.
- Issue, PR, commit, and code navigation caches support dwell, neighbor,
  hover/focus, and route-intent prefetch at background priority.
- `ApplicationTaskCoordinator`, account-work quiescence, and cancellation scopes
  prevent detached work from crossing sign-out, account change, page disposal,
  or app shutdown.
- Repository-file cache identities and index entries are canonicalized and
  confined to the owned root. Traversal, malformed/reserved names, overflow,
  duplicate entries, and reparse-point escapes are rejected or repaired.

### Telemetry And Diagnostics

- Store custom events are best-effort behind a sink abstraction; local NDJSON
  diagnostics remain available without Store runtime support.
- Event names are canonical and allowlisted. Property keys and values are
  centrally sanitized; identifier-bearing or content-bearing fields are
  rejected.
- Performance traces use duration/cache/result buckets rather than user data.
- Settings exposes telemetry availability, future-send toggles, cache sizes,
  schema status, diagnostics export, and separately confirmed clear operations.
- Diagnostics, query cache, images, code cache, Gists, and durable Stars data
  have explicit ownership and clear semantics. Durable user-created Stars
  categories are not evicted with ordinary network cache.

### Authentication And Security

- OAuth uses explicit state and verifier handling, exact protocol identity, and
  one-time handoff semantics. Production accepts only the configured callback
  origin; development accepts only documented loopback callbacks.
- The web callback no longer returns access tokens in a GET URL. It issues a
  two-minute, one-time, state-and-verifier-bound handoff with `no-store`.
- Production handoff persistence requires AES-GCM-protected Redis storage.
- Forwarded headers are trusted only from configured exact proxy addresses or
  CIDR networks. Forwarded Host and Referer are not redirect trust inputs.
- OAuth defaults to `user repo notifications`; destructive `delete_repo` scope
  is requested only after explicit user confirmation.
- Pending credentials are available only while account identity is unresolved.
  A positive account partition cannot fall back to a pending token, and stale
  account IDs without credentials are cleared before restoration.
- Profile edit/follow/unfollow uses the account mutation lane and participates
  in page/app cancellation and account quiescence.
- Markdown remote content, links, images, SVGs, redirects, private-network
  destinations, and graphics resource budgets have explicit security policies.
- Package restore is locked. `Directory.Build.props` enables lock files and
  NuGet audit; `eng/Verify-DependencySecurity.ps1` enforces HTTPS feeds, approved
  stable versions, x64 locked restore, and direct/transitive vulnerability
  checks during Release product builds.

### Error Boundary And Localization

- `Helpers/UserFacingError.cs` is the central safe presentation boundary.
  Raw exception messages, API response text, URLs, repo/user data, and content
  are not presented in status text or dialogs.
- UI copy and accessibility fallbacks are resource-backed through stable
  `x:Uid` ownership and `LocalizedResourceText`.
- The complete `qps-ploc` catalog remains available for long-string testing, but
  normal builds exclude it from PRI packaging. Opt in only for localization
  automation with `-p:EnablePseudoLocalization=true`; incomplete human
  translations are likewise not exposed as product languages.
- Light, dark, and High Contrast resources bridge through the foundation tokens
  and control catalog.

### Native Markdown Renderer

- The renderer is a native WinUI control, not a WebView.
- It supports selectable text across lines, Ctrl+C, a consistent `Copy` context
  action, selection preservation on right-click, and scrollbar drag exclusion.
- Markdown forms retain write/preview behavior for issues, PRs, reviews,
  comments, commit comments, and Gists.
- SVG text sizing, intrinsic size, lifecycle cancellation, graphics-device
  failures, image caching, remote-image policy, accessibility geometry, and
  shutdown behavior are hardened and covered by dedicated tests.
- Hosted renderer callbacks are guarded during close/disposal so app shutdown
  does not surface unhandled debugger exceptions.

## Product Surfaces Delivered

### Home

- Fixed shell plus centered two-rail widget board.
- Main rail: recent activity, recent/pinned repositories, quick actions.
- Side rail: overview, recommended repositories, notifications.
- Wide recent-repository previews allow four entries when four are available.
  The five quick actions use one uniform row and route to repository creation,
  search, repository management, My Issues, and My Pull Requests without
  requiring an active repository.
- Compact widths use an animated right drawer; widgets have capped previews and
  native internal routes rather than internal scroll regions.
- Widget visibility, order, rail placement, and reset persist through
  `DashboardWidgetLayout.v1`, including corrupt-layout recovery.

### Settings

- Page-owned wide/compact layout with stable width and section-local scrolling.
- Appearance uses concept-aligned theme cards with accent-colored icons.
- Behavior, Account, Privacy, Data & Cache, Diagnostics, and About are integrated
  into one view model.
- Cache/diagnostic snapshot, export, clear, telemetry state, version, theme,
  developer mode, contributor photos/intros/social links, and confirmation/error
  behavior are present.
- Header-to-workspace spacing is compact in both wide and narrow layouts.

### My Issues And Repository Issues

- Shared adaptive list/detail/inspector workspace with native segmented filters,
  feature-rich filter controls, clickable rows, hover/pressed/selected states,
  selectable Markdown, comment write/preview, metadata actions, and close/reopen.
- Cached-first selection, progressive comments, predictive prefetch, keyed
  refresh, and scroll-anchor preservation prevent list flashes and jumps.
- Compact controls occupy existing title/action rows; drawers align opener and
  closer locations and slide within the repository workspace boundary.
- Repository issue comments now open in a focused flyout on demand instead of
  permanently consuming the bottom of the conversation. The detail header
  condenses after meaningful vertical scrolling and restores at the top, with
  reduced-motion support and cancellable transitions.

### My Pull Requests And Repository Pull Requests

- Uses the Issues foundation with PR conversation, commits, reviews, timeline,
  reviewers, metadata, reactions, edit, close/reopen, comment/reply, and merge.
- Section caches fail independently; selection, drafts, reply drafts, active
  section, and list anchor persist through refresh.
- Pull-request identity projections are stable and no longer bind form state to
  the wrong nested view model.
- Repository pull-request comments use the same on-demand focused composer. At
  medium/wide widths, scrolling condenses title, metadata, actions, and tabs into
  one title/section row; returning to the top restores the expanded header.
  Compact widths keep a full-width section selector below the condensed title so
  all PR sections remain reachable without crowding or overlap.

### Repository Commits

- Adaptive commit history/detail/inspector workspace with branch/path/author/date
  and local message filters, compare mode, status/checks, provenance, associated
  PRs/issues, comments, and fast keyboard traversal.
- The diff is one continuous ItemsRepeater-based virtualized unified stream with
  file headers, hunks, lines, unavailable placeholders, wrapped text, stable row
  keys, file filtering, text search, next/previous match, and selectable text.
- There is no horizontal diff scrollbar, split-mode dead UI, manual `show more`
  paging, or UI-thread parsing. Previous/cached content remains visible while a
  new diff is built off-thread.

### Repository Code

- Adaptive tree/detail workspace with stable resizable leading pane, compact
  drawer, repository identity, branch search, breadcrumb navigation, file
  preview types, code find, symbol outline, F6 traversal, copyable links, and
  GitHub-aware internal routing.
- Tree and file reads are cached/prefetched; large parsing and preview work is
  budgeted away from the UI thread.
- SVG and remote previews apply resource, redirect, and network-address policy.

### Profile

- One profile page serves authenticated and other users.
- Fixed identity/action rail and stable mode navigation avoid whole-page website
  scrolling and width changes.
- Profile-focused immediate content includes identity, facts, contribution
  heatmap, profile README, organizations, and API-backed highlights.
- Secondary repositories, public stars, followers/following, and activity are
  section-lazy and cache-isolated; authenticated users route to canonical account
  libraries rather than contradictory previews.
- Authenticated users can edit REST-supported fields. Other users can follow or
  unfollow. Facts, people, organizations, repositories, and README links route to
  useful destinations.

### Stars

- Canonical account-level Stars library with a fixed categories pane and
  virtualized repository list.
- Smart lists, durable many-to-many local categories, FTS search, filter chips,
  sort, dense styled rows, context and hover actions, native checkbox selection,
  multi-select category assignment, bulk unstar confirmation, and single-action
  Undo are present.
- `application/vnd.github.star+json` timestamps are progressively synchronized
  at 100 items per page. Incomplete reconciliation never prunes local rows.
- User-created categories live in a separate account-partitioned SQLite store
  and have a separately confirmed Settings clear operation.
- Smart-list icon/text rows are vertically aligned, category titles are
  left-aligned, and category editing defaults to a full-width color selector
  that previews each color with a swatch and hex value.

### Notifications, Gists, Search, And Repository Management

- Notifications: virtualized unread/all/participating inbox, search, polling,
  automatic paging, optimistic read/unread, mark-all-read, subscription/mute,
  internal routing, and baseline-aligned result counts.
- Gists: cached library/detail, filters, native Markdown/code editing, multi-file
  create/update/delete, persistence, keyboard/context actions, and compact mode.
- Search: stale-first repository suggestions and canonical results workspace;
  submit, Enter, and suggestion invocation converge on one route.
- Repository Management: canonical account repository library with truthful
  public/private/source/fork/archived scopes, search/filter/sort, pagination,
  create/fork/delete actions, and no contradictory count taxonomy.

### Repository Chrome And Actions

- Repository pages show owner/name identity and sections for Code, Issues, Pull
  Requests, and Commits.
- Branch, watch, star, fork, compact overflow, and responsive identity/actions
  share one state model and preserve last known state on refresh failures.
- Star mutations notify the Stars library immediately.

## Test And Tooling Inventory

- `JitHub.WinUI.Tests`: service, policy, architecture, view-model, XAML/source
  contracts, security, responsiveness, telemetry, performance-budget, cache,
  pagination, and no-flash tests.
- `JitHub.Web.Tests`: OAuth callback, one-time handoff, redirect policy,
  forwarded-header trust, expiry/replay, and backend behavior.
- `MarkdownRenderer.Tests`: parser/layout/lifecycle/selection/accessibility/image,
  SVG, security, and native packaging tests.
- `MarkdownRenderer.PixelTests`: browser-grounded SVG/pixel comparison suite.
- `JitHub.WinUI.Automation`: FlaUI native probes, hover/click/keyboard behavior,
  responsive screenshots, dialogs, themes, pseudo-localization, High Contrast,
  auth lifecycle, Markdown lifecycle, and workspace-specific acceptance flows.
- `JitHub.WinUI.PerformanceGate`: repeatable warm/cold/offline/large-account route
  timing, first/settled content, interaction latency, blanking, memory, UI-thread
  stalls, and scroll-anchor checks.
- `.github/workflows/product-performance-gate.yml`: CI performance-gate wiring.

## Last Successful Verification

These results were rerun sequentially from the recovered final source state on
2026-08-12, 2026-08-13, and 2026-08-19. Generated outputs remain evidence only
and are not commit content.

| Target | Result |
| --- | --- |
| `JitHub.WinUI.Tests` Debug | 2,555 / 2,555 passed |
| `JitHub.WinUI.Tests` Release | 2,542 / 2,542 passed |
| `MarkdownRenderer.Tests` Debug | 335 / 335 passed |
| `MarkdownRenderer.Tests` Release | 335 / 335 passed |
| `MarkdownRenderer.PixelTests` Debug | 87 / 87 passed |
| `MarkdownRenderer.PixelTests` Release | 87 / 87 passed |
| `JitHub.Web.Tests` Debug | 17 / 17 passed |
| `JitHub.Web.Tests` Release | 17 / 17 passed |
| `JitHub.WinUI` Debug | warning-free build |
| `JitHub.WinUI` Release | warning-free build; 60 / 60 embedded security gates |
| `JitHub.WinUI.Automation` | warning-free x64 Debug and Release builds |
| `JitHub.WinUI.PerformanceGate` | warning-free x64 Debug and Release builds |
| `JitHub.Web` | warning-free Debug and Release builds |
| Dependency policy and audit | passed; no direct/transitive vulnerabilities |
| Eight-route warm performance gate | 55 / 55 route/fixture/metric budgets passed |
| Post-review My Pull Requests reruns | 8 / 8 passed twice; cached-selection p95 `42.10ms` and `49.50ms` |
| Publication eight-route warm performance gate | 55 / 55 passed; 4,970 measurements; clean exit |

The August 19 UI cleanup also passed a warning-free x64 Debug build of
`JitHub.slnx` with Visual Studio Community MSBuild 18.9.1. Live `winapp` review
covered Home quick actions and recent repositories, shell and Stars alignment,
the collapsed and expanded category color picker, notification count alignment,
and wide/narrow Settings spacing.

The August 19 issue/PR vertical-space pass also completed a warning-free x64
Debug app build and 45 focused contracts. Live `winapp` checks covered issue and
PR resting layouts at 1180x800, shy-header activation/restoration at 1180x450,
focused comment flyouts, and the PR compact workspace at 760x650 in light theme.
The issue conversation grew from roughly 319 to 513 DIP at 1180x800. The PR
conversation now measures roughly 436 DIP at the same fixture, materially larger
than its persistent-composer baseline.

The August 19 directional shy-header pass completed another warning-free x64
Debug app build and the full 2,546-test WinUI suite. Live native review verified
the 240ms forward/220ms reverse morph, acrylic content underlay, restoration after
a large upward scroll away from the top, and the Conversation, Files, Commits,
Reviews, and Timeline PR sections. The PR responsive probe passed in
`.codex-artifacts/shy-header-final`. The final My Issues/Repository Issues probe
passed all six requested widths in `.codex-artifacts/shy-header-issue-final-v3`,
including aligned list/inspector drawer controls at wide and compact placements.
The 760x650 and 1536x816 Repository Issues screenshots were manually reviewed at
original resolution after the final placement-aware inspector inset correction.

The follow-up morph-reflow repair also passed a warning-free x64 Debug app build,
all 2,546 WinUI tests, `pull-requests-responsive-workspace`, and
`issues-responsive-workspace`. Live captures taken approximately 80ms into the
shrink verified that PR and issue content was already rendered beneath the
transitioning acrylic header, including a 485-DIP-high issue viewport. Artifacts
are in `.codex-artifacts/shy-header-reflow-pr` and
`.codex-artifacts/shy-header-reflow-issues`.

The August 19 issue/PR list-density pass completed another warning-free x64 Debug
app build, a warning-free Debug automation build, and all 2,555 WinUI tests.
Native light/dark review covered resting issue and PR lists, both advanced-filter
flyouts, both enabled on-demand comment composers, accent contrast, and parent-
matched Markdown surfaces. `pull-requests-responsive-workspace` passed at all
five requested widths in `.codex-artifacts/density-pr-probe-final`, and
`issues-responsive-workspace` passed My Issues and Repository Issues at all six
requested widths in `.codex-artifacts/density-issues-probe-final`. Windows capped
the largest requested windows to the current `1536x816` work area; both lifecycle
logs record `status=passed`, clean app exit code 0, and automation exit code 0.

The August 20 dialog and comment-interaction pass completed a warning-free x64
Debug app build and all 2,567 WinUI tests. Live light-theme review verified that
an issue editor keeps identical bounds between Write and Preview, PR conversation
and review reactions show native emoji with counts, the eight-reaction picker is
usable, nested review replies render as an integrated thread, and a normal reply
offers Hide without simultaneously offering Unhide. The preview app was closed
after verification.

The August 20 theme-token and control-catalog pass completed a warning-free x64
Debug app build and all 2,573 WinUI tests. Semantic popup, reaction, smoke, and
transparent tokens now have aligned Default, Light, Dark, and High Contrast
definitions; app XAML outside the palette contains no literal visual colors.
Shared interactive WinUI controls are governed by app-owned implicit or keyed
styles, view structure references styles statically, and the obsolete parallel
`WinUICommonColor.xaml` palette was removed. Reaction chips, the launcher, and the
eight-reaction popup now share quiet token-driven color, typography, geometry,
and states. Live Light and Dark Design Lab review also caught and fixed two
runtime-only defects: a
missing `DefaultListViewStyle` resource reference that blocked activation and an
unconstrained settings-card icon slot that expanded a bitmap across the viewport.
The latter is now fixed at the app metric token and covered by a regression
contract. Evidence data roots are `.codex-artifacts/token-audit-design-light-final`
and `.codex-artifacts/token-audit-design-dark-final`; the preview app was closed
after verification.

An August 20 Visual Studio runtime follow-up removed unpublished Windows App SDK
style-key dependencies from the app-owned implicit `SelectorBar`,
`SelectorBarItem`, and `TreeView` styles. Their platform templates now resolve
normally through each control's `DefaultStyleKey`, while JitHub's semantic-token
setters remain in effect. A governance test guards all three keys. The x64 Debug
app build is warning-free, all 2,574 WinUI tests pass, and a debugger-attached
Light `repo-code` launch was visually verified with the repository tabs, file
tree, and README content rendered. The verification app was closed afterward.

The August 20 issue/PR body-reaction follow-up moved body reactions out of the
title and inspector areas and removed both legacy checkbox dialogs. Issue and
pull-request bodies now use the same permission-aware `CommentInteractionBar`
as comments, with inline native emoji/count chips, the shared eight-reaction
picker, and Quote reply, Copy link, Copy Markdown, and Edit actions. Body targets
intentionally omit comment-only pin, hide, and delete commands. Focused contracts
passed 556 tests; the final x64 Debug app build is warning-free and all 2,574
WinUI tests pass. Live review covered an enabled Light issue fixture and Dark PR
fixture from `.codex-artifacts/body-reaction-issue-light-direct` and
`.codex-artifacts/body-reaction-pr-dark-direct`; the verification app was closed
afterward.

Latest native acceptance results in `artifacts/final-independent-review`:

- `shell-responsive`: passed.
- `settings-responsive`: passed all five requested widths in light/dark. Genuine
  OS High Contrast was not enabled in that session, so the harness truthfully
  recorded the conditional skip rather than a false pass.
- `profile-responsive`: passed all widths and the edit dialog.
- `issues-responsive-workspace`: passed My Issues and Repository Issues at all
  six requested widths after the coordinated breakpoint fix.
- `pull-requests-responsive-workspace`: passed all five requested widths,
  section changes, list/detail behavior, and scroll stability.
- `repo-code-responsive-workspace`: passed all five widths, drawer/focus
  behavior, overflow access, and breadcrumb routing.
- `commits-virtualized-diff`: passed wrapped virtualized diff, search,
  selection/context copy, compare, and compact/wide layouts.
- Screenshots were manually reviewed through seven contact sheets and targeted
  original-resolution compact states for clipping, overlap, width shift, active
  navigation, drawer behavior, and stable content. Windows capped the largest
  requested windows at `1280x672`; assertions used actual native bounds.

## Storage Incident And Cleanup

The worktree exhausted the system drive during the final convergence run.
Consequences and recovery:

- `LocalizedResourceText.cs` was truncated during the zero-space event. It was
  restored to the enhanced localized implementation and the full tests/builds
  above subsequently passed.
- The worktree contained roughly 2.6 GB of `bin`, `obj`, specialized `obj-*`,
  `.vs`, `.tools`, and generated screenshot/performance output. These were
  removed without touching source, tests, lock files, docs, or tracked editor
  assets.
- The cleanup recovered roughly 2.1 GB, leaving about 18.3 GB free at handoff.
- `MarkdownRenderer/artifacts/svg-pixel-diffs` contains tracked baseline files;
  running the pixel harness can rewrite `diff-stats.txt`. Those run-produced
  deltas were restored before commit. Inspect that directory after future pixel
  runs and do not commit incidental statistics changes unless intentionally
  updating baselines.
- `.gitignore` now covers `.tools`, specialized security/auth/restore `obj-*`
  directories, test artifacts, and Markdown generated artifacts.
- Build outputs are intentionally absent. Run builds sequentially and clean
  outputs again after verification if disk pressure returns.

Never run a broad destructive cleanup against a computed path. Use explicit
worktree-relative generated targets and verify they resolve inside this
worktree.

## Final Hardening Progress

The automation and production hardening below is implemented and closed by the
newest exact combined-performance evidence.

- The automation bridge now uses bounded, acknowledged input commits and
  reacquires transient UIA providers instead of accepting an unacknowledged
  route transition.
- Cached traversal uses real native pointer input and app-owned timestamps.
- Traversal completion is published only after `CompositionTarget.Rendered`,
  with stale, cancellation, timeout, and unload guards in the shared
  `ProductPerformanceRenderCommitter`.
- Repository Code publishes its lightweight selected row and breadcrumb first,
  waits for that frame, and only then hydrates the potentially large file body.
  This prevents a fast disk/network result from blocking the interaction frame.
- Repository Code file/tree reads use the shared account-partitioned L1/query
  cache, true `Prefetch` priority, latest-wins hover/focus prediction, and
  route/account-owned cancellation leases.
- The former controlling baseline,
  `vnext-handoff-full-eight-exact-final-v2.json`, passed 51 of 55 budgets and
  identified four cached-selection failures. It is retained as superseded
  evidence, not current status.
- `vnext-recovery-affected-final-2.json` passes all 29 affected evaluations.
- `vnext-recovery-full-eight-final-v2.json` passes all 55 exact evaluations and
  is the first recovered exact closure artifact.
- `vnext-recovery-full-eight-post-review-v2.json` passed 54 of 55 evaluations.
  Its only miss was a single My Pull Requests pre-handler input spike: `50.73ms`
  input and `6.76ms` render, producing `55.79ms` p95. The complete report was
  written before a shutdown-only FlaUI COM-wrapper finalizer fault.
- `vnext-recovery-my-pr-post-review.json` and
  `vnext-recovery-my-pr-post-review-repeat.json` immediately rechecked that
  route at ten iterations and passed all eight budgets (`42.10ms` and `49.50ms`
  cached-selection p95) with clean process exits.
- `vnext-publication-full-eight.json` is the final controlling artifact. It
  records 4,970 measurements, all 55 evaluations passing, cached-selection p95
  between `29.64ms` and `42.67ms`, startup p95 `1149.77ms`, and a clean runner
  exit after all 80 measured launches. It did not reproduce either the My Pull
  Requests host-input outlier or the FlaUI shutdown fault.
- Browser-backed SVG pixel tests create Edge suspended, assign it to a Windows
  Job Object before resume, and terminate/drain the entire process tree. The
  test verifies both launcher and descendant termination.

No performance budget was loosened and no fixture-specific timestamp shortcut
was introduced.

## Next-Session Runbook

### 1. Confirm Source State

```powershell
git switch codex/vnext-full-audit-remediation
git status --short --branch
Get-PSDrive C
```

Before the recovery batch is committed, expect the source changes described in
this handoff plus three new implementation files. Confirm at least 6 GB free
before rebuilding all configurations and generating screenshots.

### 2. Restore The Locked Graph Once

```powershell
dotnet restore JitHub.slnx --force-evaluate `
  -p:Platform=x64 `
  -p:RestoreLockedMode=false `
  -p:SkipReleaseSecurityGate=true
```

Do not change `JitHub.WinUI.Tests.csproj` to `AnyCPU`. Its default platform must
remain `x64`; changing it moves output and breaks source-path contract tests.

### 3. Run Core Tests Sequentially

```powershell
dotnet test JitHub.WinUI.Tests\JitHub.WinUI.Tests.csproj -c Debug --no-restore
dotnet test JitHub.WinUI.Tests\JitHub.WinUI.Tests.csproj -c Release --no-restore

dotnet test JitHub.Web.Tests\JitHub.Web.Tests.csproj -c Debug --no-restore
dotnet test JitHub.Web.Tests\JitHub.Web.Tests.csproj -c Release --no-restore

dotnet test MarkdownRenderer\MarkdownRenderer.Tests\MarkdownRenderer.Tests.csproj -c Debug --no-restore -p:Platform=x64
dotnet test MarkdownRenderer\MarkdownRenderer.Tests\MarkdownRenderer.Tests.csproj -c Release --no-restore -p:Platform=x64
dotnet test MarkdownRenderer\MarkdownRenderer.PixelTests\MarkdownRenderer.PixelTests.csproj -c Debug --no-restore -p:Platform=x64
dotnet test MarkdownRenderer\MarkdownRenderer.PixelTests\MarkdownRenderer.PixelTests.csproj -c Release --no-restore -p:Platform=x64
```

Run pixel tests sequentially; concurrent browser-backed pixel runs waste disk
and can race generated comparison statistics.

### 4. Run Product Builds

```powershell
dotnet build JitHub.WinUI\JitHub.WinUI.csproj -c Debug --no-restore
dotnet build JitHub.WinUI\JitHub.WinUI.csproj -c Release --no-restore
dotnet build JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj -c Debug --no-restore
dotnet build JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj -c Release --no-restore
dotnet build JitHub.WinUI.PerformanceGate\JitHub.WinUI.PerformanceGate.csproj -c Debug --no-restore
dotnet build JitHub.WinUI.PerformanceGate\JitHub.WinUI.PerformanceGate.csproj -c Release --no-restore
dotnet build JitHub.Web\JitHub.Web.csproj -c Debug --no-restore
dotnet build JitHub.Web\JitHub.Web.csproj -c Release --no-restore
```

The Release app build runs `eng/Verify-DependencySecurity.ps1`, including x64
locked restore and 60 security gates. The script itself can also be run directly:

```powershell
pwsh -NoProfile -File eng\Verify-DependencySecurity.ps1
```

### 5. Run Native Responsive Probes

After the Debug app and automation harness build:

```powershell
$app = (Get-ChildItem JitHub.WinUI\bin\x64\Debug -Recurse `
  -Filter JitHub.WinUI.exe | Where-Object FullName -NotMatch '\\AppX\\' |
  Select-Object -First 1).FullName

$probes = @(
  'shell-responsive',
  'settings-responsive',
  'profile-responsive',
  'issues-responsive-workspace',
  'pull-requests-responsive-workspace',
  'repo-code-responsive-workspace',
  'commits-virtualized-diff'
)

foreach ($probe in $probes) {
  dotnet run --project JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj `
    -c Debug --no-build -- `
    --app=$app `
    --probe=$probe `
    --out="artifacts\final-independent-review\$probe"
  if ($LASTEXITCODE -ne 0) { throw "$probe failed" }
}
```

Review every screenshot, not only the exit code. Confirm actual native bounds in
filenames/logs when Windows caps the request. Run shell, settings, profile, and
issues first because they exercise the shared responsive primitives.

### 6. Reverify The Performance Gate

Build the app and gate, then run the exact full Warm matrix when performance-
relevant source changes:

```powershell
pwsh -NoProfile -File eng\Invoke-ProductPerformanceGate.ps1 `
  -AppPath $app `
  -Configuration Debug `
  -Iterations 10 `
  -Fixtures Warm `
  -Routes gists,my_issues,my_pull_requests,profile,repo_code,repo_commits,repo_issues,repo_pull_requests `
  -OutputPath artifacts\performance\vnext-recovery-rerun.json `
  -ArtifactsPath artifacts\performance\vnext-recovery-rerun `
  -SkipBuild
```

The gate intentionally requires at least 10 iterations. Do not bypass that
validation or loosen the checked-in budgets.

### 7. Future Change Discipline

- Rerun focused tests for every behavioral change and the affected native probe.
- Repeat both review perspectives for cross-cutting or lifecycle changes.
- Update `docs/jithub-vnext-full-product-audit.md` only with fresh evidence.
- Keep generated screenshots/reports uncommitted; link their run names in docs
  only when the team intentionally retains them outside Git.

## Source Map For Final Hardening

| Concern | Principal locations |
| --- | --- |
| OAuth client/account partition | `JitHub.WinUI/Services/AuthService.cs`, `AuthCredentialStore.cs`, `AuthProtocolPolicy.cs`, auth tests |
| OAuth web handoff/redirect | `JitHub.Web/Services/OAuthHandoffStore.cs`, `OAuthRedirectUriPolicy.cs`, `RedisOAuthHandoffBackend.cs`, `ForwardedHeaderTrustPolicy.cs`, `JitHub.Web.Tests` |
| Profile mutation lifecycle | `JitHub.WinUI/Services/Profile`, `ProfilePageViewModel.cs`, profile tests |
| File cache confinement | `JitHub.WinUI/Services/CodeViewer/RepoFileCacheService.cs`, `RepoFileCacheKey.cs`, cache tests |
| Shell route prefetch | `RepositoryRoutePrefetchCoordinator.cs`, `RepoCodeNavigationPreparationCache.cs`, `ShellPageViewModel.cs` |
| Safe UI errors | `JitHub.WinUI/Helpers/UserFacingError.cs`, localized resources, presentation contract tests |
| Progressive Issues | `JitHub.WinUI/Services/Issues`, `RepoIssuePageViewModel.cs`, Issues page/panes, progressive tests |
| Dependency gate | `Directory.Build.props`, all `packages.lock.json`, `eng/Verify-DependencySecurity.ps1`, Release target in `JitHub.WinUI.csproj` |
| Responsive breakpoint | `JitHub.WinUI/Services/Layout/ShellResponsiveLayout.cs`, `AdaptiveWorkspaceLayoutTests.cs` |
| Performance bridge | `ProductPerformanceRouteProbe.cs`, `ShellPage.xaml`, `ShellPage.xaml.cs` |

## Commit Hygiene

- Do not commit `bin`, `obj`, `obj-*`, `.vs`, `.tools`, screenshots,
  performance reports, test result files, app packages, local databases, or
  user-specific app settings.
- `packages.lock.json` files are intentional source and must remain committed.
- `JitHub.WinUI/appsettings.json` is an intentional non-secret baseline; local
  `appsettings.development.json` remains ignored.
- Before every push, run `git diff --check`, inspect staged file sizes, and scan
  staged text for tokens, secrets, private keys, connection strings, and local
  absolute paths.
- Keep the complete convergence commit intact unless a later maintainer
  deliberately splits it with a verified history rewrite. This branch is the
  recovery point for the accumulated vNext implementation.
