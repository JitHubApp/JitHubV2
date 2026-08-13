# JitHub vNext Full Product Audit

Status: tracked remediation implemented, independently rereviewed, and verified

Audit date: July 16, 2026; closure reverified August 13, 2026

Scope: current vNext worktree, authenticated preview data, public preview data,
dark/light themes, static architecture review, and responsive WinUI automation

This is the canonical defect and product-maturity tracker for the current JitHub
vNext application. It records the original findings, their production
remediation, and the evidence used to close them. It intentionally preserves
the history of the convergence work rather than presenting one large rewrite.

## Severity And Status

| Level | Meaning |
| --- | --- |
| P0 | Release blocker: crash, inaccessible core route, data-risk issue, security issue, or unusable responsive state. |
| P1 | Core workflow, performance, architecture, or accessibility defect that materially damages the product. |
| P2 | Important feature, consistency, polish, or test-coverage gap. |
| P3 | Cleanup, deferred capability, or developer-only issue. |

Historical findings remain in the document for traceability. A finding is
closed only when its row is explicitly marked `Verified` with implementation,
test, runtime, responsive, accessibility, and review evidence appropriate to
its risk.

## Executive Verdict

JitHub now has a real identity and several genuinely strong native surfaces. The
combined shell/repository rail, Home widget board, adaptive issue/PR/commit
workspaces, wrapped virtualized commit diff, native Markdown renderer, profile
identity workspace, and Stars library are worth preserving. They establish a
distinctive dense, calm, Windows-native GitHub client rather than a GitHub web
page in a desktop frame.

The convergence phase is complete for every finding recorded by this audit.
Reachable surfaces now share the account-partitioned cache/query architecture,
automatic pagination and truthful scope, keyed no-flash updates, coordinated
responsive layout, localized Fluent controls, identifier-free telemetry,
keyboard/UI Automation contracts, and bounded background work. Settings and
all canonical workspaces pass the audited snap widths. The final gates include
Debug and Release test/build matrices, native process lifecycle coverage,
performance budgets, High Contrast and pseudo-localization probes, dialog and
keyboard matrices, hostile-content security gates, and real-host Markdown
shutdown/selection coverage.

This closure is deliberately scoped to the recorded audit. New capabilities or
future regressions should be tracked as new work rather than reopening resolved
historical descriptions without new evidence.

## Product Identity To Preserve

- One single-frame shell with a combined navigation/repository rail.
- The dark green/black surface hierarchy, restrained borders, serif content
  headings, sans-serif UI chrome, and monospaced technical content.
- Compact Fluent controls with clear hover, pressed, selected, focus, disabled,
  loading, empty, offline, and failure states.
- Detail-first `AdaptiveWorkspace` behavior and animated edge drawers.
- Cached content first, invisible background refresh, no browser-style refresh
  workflow, no full-page blanking, and no selection/scroll reset.
- Native Markdown and commit diff surfaces with selectable text.
- A fixed app workspace whose structural width is owned by the page, never by
  the currently selected mode or child content.
- Internal navigation for GitHub users, repositories, issues, PRs, commits, and
  README links; browser launch only for genuinely external targets.

## Audit Method

The audit combined four forms of evidence:

1. Route and feature inventory across every reachable page, shell command,
   dialog, view model, GitHub data service, cache, and automation probe.
2. Static review of XAML structure, bindings, responsive states, UI Automation
   metadata, cancellation, pagination, cache usage, collection updates,
   telemetry, localization, and package health.
3. Real WinUI/FlaUI interaction probes request `1366x900`, `1180x800`,
   `900x700`, `760x650`, and `640x600` where a probe exists. The harness records
   the native bounds Windows actually grants, drives breakpoint assertions from
   those bounds, and includes both actual and requested dimensions in artifact
   names whenever the desktop work area constrains a request.
4. Dark/light visual comparison and manual screenshot inspection.

At closure, the WinUI suite passes 2,542 tests in both Debug and Release, the
Markdown renderer suite passes 335 tests in both configurations, and the
renderer pixel suite passes all 87 cases in both configurations. WinUI,
Automation, PerformanceGate, and Web build warning-free in Debug and Release;
the Release product build also passes all 60 embedded release/security gates.
These automated checks supplement, rather than replace, the live native
interaction and screenshot evidence below.

## Release Gates

The rows below retain their original priorities and areas for history. Every
gate is now verified by its recorded closure evidence.

| ID | Pri | Area | Finding | Evidence / Required Outcome |
| --- | --- | --- | --- | --- |
| AUD-SET-001 | P0 | Settings | **Verified 2026-07-17.** Settings now uses page-owned wide and compact layouts with stable content width and section-local scrolling at every audited width. | Closed after responsive UI automation, screenshot review, and independent review. |
| AUD-AUT-001 | P1 | Core interaction | **Verified 2026-07-28.** The current Home acceptance probe exercises every exposed View-all action at wide and compact widths; the sole truthful Notifications action is clickable and routes successfully. | Closed after a fresh live probe, artifact review, and independent review. |
| AUD-AUT-002 | P1 | Repo Issues | **Verified 2026-07-17.** The dedicated Issues probe now strictly verifies drawer existence and containment, then exercises filtering, row selection, scroll preservation, inspector access, and comment preview. | Closed after passing responsive automation and independent review. |
| AUD-AUT-003 | P0 | Stars | **Verified 2026-07-17.** Selection mode now survives repeated on/off cycles without closing or destabilizing the Stars workspace. | Closed after repeated-cycle UI automation and independent review. |
| AUD-AUT-004 | P1 | Stars categories | **Verified 2026-07-17.** Category create/rename/delete automation now drives deterministic dialogs and removes test-owned categories before and after each run. | Closed after isolated automation and independent review. |
| AUD-SRC-001 | P1 | Search | **Verified 2026-07-17.** Visible submit, Enter, and suggestion invocation now converge on the canonical cached search workspace through one shell state machine. | Closed after direct automation and independent review. |
| AUD-DEP-001 | P0 | Dependency security | **Verified 2026-07-17.** `SQLitePCLRaw.lib.e_sqlite3` now resolves to 3.0.3; the independent reviewer confirmed a clean direct/transitive vulnerability scan and passing cache tests. | Closed after independent review. |

## Cross-Cutting Findings

### Data, Cache, Pagination, And Quiet Updates

| ID | Pri | Finding | Required Outcome |
| --- | --- | --- | --- |
| AUD-DAT-001 | P1 | **Verified 2026-07-28.** Reachable REST and GraphQL reads use the shared account-partitioned query architecture or documented authentication/raw-content adapters; page-owned direct clients are mutation-only. | Closed after 236 broad architecture tests, 80 foundation tests, a zero-warning Debug build, and independent review of PR reactions, Profile GraphQL, and the no-allowlist conformance gate. |
| AUD-DAT-002 | P1 | **Verified 2026-07-28.** Canonical paged surfaces automatically continue within documented API limits, expose complete/partial/API-limited scope, preserve keyed rows and cached tails through failed or partial refreshes, and prevent detail tails from crossing item identities. | Closed after 232 focused data-integrity tests and independent end-to-end review of Issues, PRs, commits, My work, Gists, Profile, Stars, Notifications, repository search/manage/index, and GitHub's 300-event public-activity boundary. |
| AUD-DAT-003 | P1 | **Verified 2026-07-28.** Repo Issues now routes all reachable reads through `IGitHubIssueQueryService`, with independently keyed/tagged stale-first sections for list, detail, comments, timeline, reactions, and inspector metadata; direct client calls remain mutations only. | Closed after 54 focused Issue/Phase 0 tests, a sequential zero-warning Debug build, and independent read-path review. |
| AUD-DAT-004 | P1 | **Verified 2026-07-28.** Repo Code tree, directory, and blob reads use `IGitHubRepoCodeQueryService` over the shared query transport, with account partitioning, validators, dedupe, priority lanes, tags, Settings-visible accounting, and lazy expansion for truncated trees. SHA-addressed immutable file content retains its dedicated bounded blob store. | Closed after 224 focused query/cache/paging tests, a successful isolated Debug product build, and independent read-path review. |
| AUD-DAT-005 | P1 | **Verified 2026-07-28.** Repo Manage publishes cached pages immediately, merges network pages incrementally by key, preserves cached tails through incomplete reconciliation, and shares account-partitioned index state with Shell and Profile. | Closed after cancellation/reactivation tests, 110 focused repository tests, fresh live repository-library automation, a zero-warning build, and independent review. |
| AUD-DAT-006 | P1 | **Verified 2026-07-28.** Dashboard, My Issues/PRs, Gists, Profile, and Stars preserve row identity through keyed reconciliation; remaining collection clears are limited to identity/account changes, selected-section resets, or static layout reconstruction. | Closed after 88 focused tests, a zero-warning Debug build, and independent review of every remaining reachable collection-clear site. |
| AUD-DAT-007 | P2 | **Verified 2026-07-28.** Repo Code navigation owns and observes ref, tree, file, README, back, and forward work with request generations and cancellation; stale completions cannot replace the current file and failures preserve readable content. | Closed after focused concurrency tests, five-width live automation, and independent review. |
| AUD-DAT-008 | P2 | **Verified 2026-07-28.** Local diagnostics uses one bounded ordered writer, linear-time trimming, and an explicit shutdown drain. A real native close probe persisted all 64 ordered burst events plus the terminal marker and exited with code `0`. | Closed after adversarial tests, persisted NDJSON/hash evidence, live shutdown automation, and independent review. |
| AUD-DAT-009 | P2 | **Reverified 2026-08-10.** The cache registry reports ownership, paths, caps, TTLs, partitioning, size, clear semantics, and health across query, payload, image, code, diagnostics, Gists, and durable Stars stores. Coordinated clear uses a durable transaction marker and safely recovers ambiguous commits and orphaned staged data. Corrupt payload generations and unsafe payload identities are quarantined without reading or deleting outside the owned cache root. | Closed after focused cache corruption, traversal, clear-recovery, and registry tests plus the full 2,526-test Debug/Release matrices. |
| AUD-DAT-010 | P1 | **Verified 2026-07-17.** Remote avatar/content resolution now uses one bounded account-aware image pipeline with cancellation, data-URI reuse, partitioned cache identity, owned-byte safety, Settings size reporting, and clear behavior. | Closed after targeted cache/image tests and independent review. |

### Responsive Layout And Native App Behavior

| ID | Pri | Finding | Required Outcome |
| --- | --- | --- | --- |
| AUD-RSP-001 | P1 | **Verified 2026-07-28.** Shell and repository workspaces share one window/content-width coordinator that preserves the inspector, app rail, inner-list collapse order across Issues, PRs, and Commits without revealing a pane while the window narrows. | Closed after 62 focused responsive/telemetry tests and independent review of the runtime Shell and `AdaptiveWorkspace` breakpoint paths. |
| AUD-RSP-002 | P1 | **Verified 2026-07-28.** Repo Code now uses `AdaptiveWorkspace`; the file tree becomes an animated leading drawer at constrained widths while the readable primary pane remains visible. | Closed after five-width live automation, focus/light-dismiss/keyboard verification, screenshot review, and independent review. |
| AUD-RSP-003 | P1 | **Verified 2026-07-28.** Settings switches to compact Fluent section navigation below 820px, keeps structural width stable, and scrolls only the selected content region. | Closed after five-width dark/light live automation, focused keyboard/dialog tests, a zero-warning isolated build, and independent review. |
| AUD-RSP-004 | P2 | **Verified 2026-07-28.** Compact repository chrome consolidates overlapping repository commands into one named overflow, while Issue, PR, and Code expose distinct, labeled page/file action domains with stable IDs, names, tooltips, and non-overlapping menus. | Closed after 93 focused action/accessibility/responsive tests and independent review. |
| AUD-RSP-005 | P2 | **Verified 2026-07-28.** Repo Code prioritizes the current filename, moves low-priority commands into one named compact overflow, and preserves back, forward, tree, breadcrumb, and primary file actions at `640px`. | Closed after five-width screenshots, keyboard automation, and independent review. |
| AUD-RSP-006 | P2 | **Verified 2026-08-06.** Canonical workspaces share stable header, margin, and compact-action primitives; secondary content reflows without shifting the shell or page width. | Closed after focused contracts, independent review, a five-width live responsive probe, and visual inspection of wide, medium, and compact screenshots. |

### Performance And Resource Use

| ID | Pri | Finding | Required Outcome |
| --- | --- | --- | --- |
| AUD-PERF-001 | P1 | **Reverified 2026-08-13.** Repeatable gates cover warm, cold, offline, and large-account startup; cached route and selection; first/settled content; scroll frames; memory; UI-thread stalls; and blanking. Selection now presents a lightweight coherent header/path in the input frame and defers heavy Markdown, diff, tree, and body hydration until after render. The exact ten-iteration eight-route Warm matrix passes all 55 evaluations with unchanged budgets. | Closed against `vnext-publication-full-eight.json`: startup p95 `1149.77ms`/`1500ms`; cached-selection p95 My Issues `36.06ms`, My PRs `33.01ms`, Gists `42.67ms`, Repo Code `34.75ms`, Repo Issues `29.64ms`, Repo PRs `31.91ms`, and Repo Commits `37.52ms`, each against `50ms`. |
| AUD-PERF-002 | P1 | **Verified 2026-07-28.** Canonical reads inherit stale-first caching, validators and `304` reuse, request dedupe, account partitioning, tags, cancellation, priority lanes, background refresh, and rate-limit retry semantics; Settings reports and clears the shared cache. | Closed with the same 316-test architecture/foundation verification and independent inspection of HTTP-200 GraphQL rate-limit propagation and source ownership gates. |
| AUD-PERF-003 | P2 | **Verified 2026-07-28.** Predictive work uses one adaptive policy for network, power, memory, per-resource GitHub rate limits, route/account cancellation, request-queue abandonment, and priority promotion; Issue, PR, and Commit hover/focus/dwell/neighbor work is bounded and shutdown-coordinated. | Closed after 113 focused adversarial tests, 30 fresh-process cancellation/promotion stress runs, a zero-warning build, and a third independent review of queue, focus-container, rate-limit-bucket, and foreground-wins behavior. |
| AUD-PERF-004 | P2 | **Verified 2026-07-28.** Application activation, Shell initialization/search, Commit prefetch, Stars work, Profile GraphQL refresh, Notifications, Repo Code reconciliation, query/cache maintenance, and diagnostics use one coordinator with account/route cancellation, bounded shutdown drain, and identifier-free fault observation. | Closed after 104 focused lifecycle/reliability tests, the 1,311-test Debug suite, a zero-warning build, and independent review. |
| AUD-PERF-005 | P1 | **Verified 2026-08-10.** Stars synchronization coalesces rapid requests by account, preserves pending force-full intent when an owner is cancelled or abandoned, completes each owned batch exactly once, and prevents disposal from losing later work. | Closed after coordinator ownership/cancellation tests, Stars service regressions, live Stars library and selection probes, and warning-free Debug/Release builds. |

### Accessibility, Keyboard, And Automation

| ID | Pri | Finding | Required Outcome |
| --- | --- | --- | --- |
| AUD-ACC-001 | P1 | **Verified 2026-08-05.** Canonical product XAML and generated/recycled controls expose stable, scoped automation IDs and meaningful accessible names, including simultaneous identityless PR review replies. | Closed after 226 focused accessibility checks, live recycling/recreation probes, warning-free product/automation builds, screenshot review, and independent accessibility review. |
| AUD-ACC-002 | P1 | **Verified 2026-07-28.** Generated Repo tree, Stars, and Gists rows expose concise user-facing UIA names derived from their visible content and state instead of raw CLR model names. | Closed after focused contract tests, live automation guards for all three surfaces, and independent accessibility review. |
| AUD-ACC-003 | P1 | **Verified 2026-07-17.** The contribution graph exposes one keyboard-focusable Calendar peer with per-day accessible summaries, visible tooltips, and arrow/Home/End day navigation across the fitted 53-week graph. | Closed after live UIA keyboard automation, unit coverage, screenshot review, and independent review. |
| AUD-ACC-004 | P2 | **Verified 2026-08-05.** Shell, Profile, Settings, repository workspaces, repeated PR reply controls, and compact command surfaces expose explicit accessible names independent of tooltip text. | Closed after canonical name contracts, live PR reply and compact Repo Code probes, screenshot review, warning-free builds, and independent accessibility review. |
| AUD-ACC-005 | P2 | **Verified 2026-08-07.** Live keyboard matrices cover `Tab`/`Shift+Tab`, arrow traversal, `Space`/`Enter`, `Esc`, context-menu key, `Ctrl+C`, focus return, lists, mode selectors, drawers, dialogs, Markdown links, and commit-diff search. | Closed with `keyboard-accessibility-matrix-final`, `keyboard-commit-diff-search-final`, focused Markdown host matrices, and independent accessibility review. |
| AUD-ACC-006 | P2 | **Verified 2026-08-07.** Representative reachable pages and dialogs pass live High Contrast visual and behavior probes; hardcoded contributor foreground was removed; semantic surface, interaction, selection, and focus colors are contract-tested against system roles. | Closed with `high-contrast-live-final-v3`, palette/resource tests, screenshot review, and independent WinUI accessibility review. |

### Styling, Localization, Telemetry, And Architecture

| ID | Pri | Finding | Required Outcome |
| --- | --- | --- | --- |
| AUD-STY-001 | P1 | **Verified 2026-07-29.** All 15 canonical workspaces now adopt the shared control catalog for interactive rows, status/empty states, dialogs, and responsive headers; the duplicate legacy workspace row template is removed. | Closed after 28 focused catalog/theme/High Contrast checks, a warning-free product build, the 2,098-test full suite, and fresh independent review. |
| AUD-STY-003 | P1 | **Verified 2026-08-06.** Light now uses a neutral Windows canvas with a crisp semantic surface hierarchy, restrained green accents, measured contrast, and distinct interaction states; dark has matching layered/state semantics; High Contrast maps hover, pressed, selected, focus, foreground, and surface roles to meaningful system-color pairs. WinUI-owned platform overrides have one owner in `WinUIResourceBridge.xaml`, with effective-graph duplicate validation. Closed after 12 focused palette/resource tests, warning-free product and automation builds, fresh light/dark Settings runtime screenshots, XAML load verification, and independent WinUI design review. |
| AUD-STY-004 | P2 | **Verified 2026-07-28.** Navigation, controls, prose, review/status copy, and unavailable/empty diff states use UI/body typography; mono remains limited to code, diffs, paths, branches, SHAs, issue/PR numbers, usernames, stats, and deliberate compact metadata. | Closed after focused static typography coverage, a zero-warning Debug build, and independent source review. |
| AUD-STY-002 | P2 | **Reverified 2026-08-10.** Every reachable product XAML literal and accessibility fallback has a stable `x:Uid` resource owner; generated/runtime copy is resource-backed; the complete `qps-ploc` catalog is packaged while incomplete human translations remain excluded; and fresh wide, snapped, and compact pseudo-localized screenshots cover representative pages without clipping or width shift. | Closed with localization/resource contract tests, the full 2,526-test matrices, long-string probes, and independent localization review. |
| AUD-TEL-001 | P1 | **Verified 2026-08-06.** Every canonical route now owns opened/loaded/action/error/performance signals; Issue/PR scheduled and direct prefetch paths emit exact-once terminal outcomes across success, failure, cancellation, policy rejection, account cancellation, shutdown, and disposal; My Issues/My PR dwell work is covered; and dimension-specific sanitization rejects invalid taxonomy pairs and identifier-bearing properties. | Closed after 132 focused telemetry/lifecycle tests, a warning-free Debug build, and fresh independent telemetry/privacy review. |
| AUD-TEL-002 | P2 | **Verified 2026-07-28.** Obsolete shell-tab taxonomy is rejected; PR, commit, and Stars events have reachable runtime paths; and commit prefetch attempts exactly one safe completion event for every started success, failure, or cancellation without telemetry faults affecting navigation. | Closed after 43 behavioral tests and fresh independent telemetry review. |
| AUD-ARC-001 | P1 | **Verified 2026-07-28.** Every route, including the application shell, has one documented owning page/view model; retired duplicate issue, pull-request, commit, search, and repository-code surfaces and their dependency registrations are removed. | Closed after route-ownership and retired-path contract coverage plus independent review. |
| AUD-ARC-002 | P2 | **Verified 2026-07-28.** Profile's stable workspace, identity rail, mode surfaces, templates, and list structure live in XAML; growing collections are keyed and virtualized, with code-behind limited to event/dialog coordination and retained mode scroll state. | Closed after 44 focused tests, five-width screenshot review, edit/README interaction review, and an independent WinUI architecture pass. |
| AUD-BLD-001 | P2 | **Verified 2026-07-17.** The stale publish-profile reference is removed; Debug, Release, and clean packaged MSIX/MSIXBundle builds complete warning-free. Independent review confirmed the build and packaging configuration. | Closed after independent review. |
| AUD-TST-001 | P1 | **Verified 2026-07-17.** Automation now verifies exact process/root/native-window identity, isolates local data per run, requires JitHub foreground ownership, rejects foreign overlays, and validates captured pixels before saving. | Closed after clean screenshot review and independent review. |
| AUD-TST-002 | P2 | **Verified 2026-07-17.** The suspect self-comparison and blocking waits are repaired, analyzer warnings are resolved, and the Markdown test project treats warnings as errors. | Closed after 303 warning-free tests and independent review. |

### Security And Privacy

| ID | Pri | Finding | Required Outcome |
| --- | --- | --- | --- |
| AUD-SEC-001 | P1 | **Verified 2026-07-17.** Baseline OAuth is limited to `user repo notifications`; `delete_repo` is requested only after explicit destructive confirmation. URI, scope-header, launcher, rejection, failure, and upgraded-token retry paths are covered by tests. Independent review confirmed the implementation. | Closed after independent review. |
| AUD-SEC-002 | P2 | **Verified 2026-07-28.** Markdown remote images require HTTPS; third-party, metered, and offline requests are cache-only without consent; redirects and resolved addresses are revalidated; and encoded plus decoded image budgets cover SVG, raster, animated, and nested ICO payloads. | Closed after 159 focused security/renderer tests across hostile redirects, tracking hosts, malformed/oversized images, concealed ICO payloads, Debug/Release security gates, and independent review. |
| AUD-SEC-003 | P2 | **Verified 2026-07-28.** Exact OAuth state validation, credential/account isolation, telemetry redaction, external-link classification, account-partitioned caches, and hostile Markdown/URL inputs are covered by the release security gate. | Closed after focused abuse tests, release-gate execution, and independent security review. |
| AUD-SEC-004 | P2 | **Verified 2026-07-28.** Sign-out explicitly offers retention or account-data deletion and discloses retained diagnostics. Deletion cancels and drains account work, journals progress durably, clears every account-partitioned query/image/file/Stars/navigation/index store, and removes credentials only after all stores succeed; interrupted cleanup resumes before session restoration. | Closed after 81 focused reliability tests, 45 Release security tests, a zero-warning x64 Debug build, late-write/cross-account recovery coverage, and independent security review. |
| AUD-SEC-005 | P1 | **Verified 2026-08-10.** The web callback no longer returns GitHub access tokens from a GET URL. It creates a two-minute, one-time, state-and-verifier-bound handoff, returns all sensitive responses with `no-store`, requires AES-GCM-protected Redis storage in production, and enables forwarded headers only for explicitly configured exact proxies or CIDR networks. | Closed after handoff replay/expiry/state/verifier/backend tests, forwarded-header trust-policy tests, 17/17 web tests in Debug and Release, warning-free web builds, and a clean transitive vulnerability scan. |

### Final Independent-Review Hardening

| ID | Pri | Finding | Closure evidence |
| --- | --- | --- | --- |
| AUD-FIN-001 | P0 | **Verified 2026-08-10.** Pending OAuth credentials are visible only while account identity is unresolved. Positive account partitions never fall back to a pending token, and stale account IDs without credentials are cleared before restoration. | Auth partition and callback-transition tests in Debug and Release. |
| AUD-FIN-002 | P1 | **Verified 2026-08-10.** Profile edit/follow/unfollow mutations participate in the account mutation lane, account-work quiescence, application task coordination, and page cancellation. | Profile query/view-model lifecycle tests and warning-free product builds. |
| AUD-FIN-003 | P0 | **Verified 2026-08-10.** Repository-file cache keys and index entries are canonicalized and confined to the owned root for read, write, enumerate, and delete. Traversal, reserved names, malformed entries, duplicates, overflow, and reparse-point escapes are rejected or repaired. | Adversarial cache tests plus the full Debug/Release matrices. |
| AUD-FIN-004 | P1 | **Verified 2026-08-10.** Shell repository Code/Commit prefetch is latest-wins, cancellable, bounded, eviction-aware, and drained at shutdown instead of running as detached work. | Coordinator/cache concurrency tests and app lifecycle coverage. |
| AUD-FIN-005 | P1 | **Verified 2026-08-10.** UI-facing failures pass through one localized safe-error boundary. Raw exceptions, transport bodies, API messages, URLs, repository data, and content are retained only in Debug diagnostics, never presented as status/dialog copy. | Source-presentation contract tests and updated failure-path assertions. |
| AUD-FIN-006 | P1 | **Verified 2026-08-10.** Cold Issues and comments publish page one immediately, reconcile later pages by key, and load issue body before comment pagination completes. Cached rows, selection, and viewport remain stable through progressive refresh. | Progressive-loading service/view-model tests and the passing two-page six-width Issues probe. |
| AUD-FIN-007 | P0 | **Verified 2026-08-10.** Production OAuth accepts only the exact configured callback origin. Development permits only explicitly documented loopback callbacks; forwarded `Host`/`Referer` values are not trust inputs, and the auth service revalidates the redirect independently. | 14 auth tests and 17 web callback tests in Debug and Release. |
| AUD-FIN-008 | P0 | **Verified 2026-08-10.** Every project has a lock file; package versions and feeds are policy-checked; unapproved prereleases/floating versions are rejected; locked `x64` restore and direct/transitive NuGet audit run during every Release product build. | `eng/Verify-DependencySecurity.ps1`, 60 embedded Release security tests, and a clean final vulnerability scan. |

## Data And Pagination Matrix

| Surface | Current read path | Current limit | Cache/refresh assessment | Required convergence |
| --- | --- | --- | --- | --- |
| Shell repository rail/search | Shared pilot/query architecture | Preview plus authoritative account index | Cached rows render first and refresh silently without blanking. | Verified; scope is explicit and ordinary refresh is automatic. |
| Home | Dashboard query facade | Intentionally capped widget previews | Sections reconcile by key and View-all routes to canonical destinations. | Verified; preview scope is a product choice, not hidden truncation. |
| Repository workspace chrome | Shared repository query facade | Repository/ref/action state | Reads are cached and cancellable; mutations are optimistic and recoverable. | Verified across repository workspaces. |
| My Issues / My PRs | Me query facade | Automatic paged results and detail sections | Cached tails, selection, and scroll survive partial refreshes. | Verified with truthful complete/partial state. |
| Repo Issues | Issue query facade | Automatic list and section pagination | List, detail, comments, timeline, reactions, and inspector are independently cached. | Verified stale-first and section-isolated. |
| Repo Pull Requests | PR query facade | Automatic list and section pagination | Keyed updates and cached sections preserve detail and drafts. | Verified, including responsive/prefetch paths. |
| Repo Commits | Commit query facade | Automatic history and section pagination | Diff/search viewport remains stable while cached detail refreshes. | Verified with wrapped virtualized diff gates. |
| Repo Code | Repo Code query service plus bounded blob store | Lazy complete tree and immutable blob content | Shared metadata cache, validators, cancellation, and stale-completion protection. | Verified in `AdaptiveWorkspace`. |
| Stars | Durable SQLite library plus star query service | Progressive full synchronization at 100/page | Account-partitioned FTS/category data and keyed rows survive offline/partial sync. | Verified, including lifecycle and category automation. |
| Gists | Cached Me query facade and native detail | Automatic pagination | Keyed stale-first rows preserve visible content. | Verified with explicit API scope. |
| Profile | Profile REST/GraphQL query service | Identity-first lazy sections | Cached identity renders immediately; active sections load independently. | Verified identity-focused workspace. |
| Search results | Canonical cached search workspace | Automatic paged results | Stable error, filter, scope, and keyboard behavior. | Verified through all shell entry paths. |
| Repository management | Shared account repository index | Incremental complete reconciliation | Cached pages publish immediately and network pages merge by key. | Verified warm/offline/large-account behavior. |

## Page-By-Page Findings

### Shell And Command Search

Strengths: stable single-frame shell, combined rail, compact title bar, responsive
rail drawer, cached repository suggestions, keyboard command search, and a clear
visual identity.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-SHL-001 | P1 | **Verified 2026-07-17.** Search button submission, Enter submission, and suggestion invocation share one route/state machine and dismiss the shell popup deterministically. | Closed after direct automation and independent review. |
| AUD-SHL-002 | P1 | **Reverified 2026-08-10.** The `286px` shell rail collapses before repository leading panes, including the structural frame/padding inset, and recovered width is capped so an already-collapsed inspector cannot reappear during narrowing. | Closed after 26 breakpoint tests and the passing six-width My Issues/Repo Issues runtime matrix at the desktop's actual native bounds. |
| AUD-SHL-003 | P2 | **Verified 2026-07-28.** Repository synchronization is silent and automatic; the only visible retry command is conditioned on an actionable rail error. | Closed after source-contract tests and independent review. |
| AUD-SHL-004 | P2 | **Verified 2026-07-28.** Notifications is a canonical shell route, command-search destination, and truthful Home View-all target. | Closed after live routing automation and independent review. |
| AUD-SHL-005 | P2 | **Verified 2026-08-07.** Fresh live shell navigation and hover probes audit Back/Forward, every rail route, top actions, profile/footer, search, repository filters/items, Home customize, and the compact drawer for accessible name, tooltip, hover/focus behavior, disabled/error behavior, click routing, focus return, and identifier-free telemetry contracts. | Closed with `final-shell-audit-current`, keyboard coverage, telemetry contract tests, and independent shell review. |
| AUD-SHL-006 | P1 | **Verified 2026-07-28.** The single-frame shell provides accessible Back/Forward commands, Alt shortcuts, mouse navigation buttons, route identity, frame reuse, and per-page focus/selection/scroll restoration. | Closed after history tests, live navigation automation, and independent review. |
| AUD-SHL-007 | P2 | **Verified 2026-07-28.** The account footer derives truthful display name/login and labels fallback state as a GitHub account; the fake Pro entitlement is gone. | Closed after contract tests and independent review. |

### Home Dashboard

Strengths: concept-aligned two-rail board, fixed-height preview widgets, compact
side drawer, customizable persisted layout, internal activity links, and strong
wide-screen composition.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-HOM-001 | P1 | **Verified 2026-07-28.** Every currently exposed View-all action passes direct wide and compact UI automation with reliable clickable-point and destination assertions. | Closed after fresh live automation and independent review. |
| AUD-HOM-002 | P1 | **Verified 2026-07-28.** Semantically false widget destinations were removed; only Notifications exposes View all, and it opens the canonical inbox workspace. | Closed after source-policy tests, live routing, and independent review. |
| AUD-HOM-003 | P1 | **Verified 2026-07-28.** Dashboard data and preview collections use keyed in-place snapshots, retaining realized rows and preventing refresh blanking; structural widget rebuilds are limited to explicit layout customization. | Closed after no-blanking contracts and independent review. |
| AUD-HOM-004 | P2 | **Verified 2026-07-28.** Recommended repositories are derived from real recent/starred/language signals and expose concise, truthful offline/no-signal empty states without masquerading as Trending. | Closed after service/state review and independent review. |
| AUD-HOM-005 | P2 | **Verified 2026-07-28.** Widget layouts recover corrupt/old/duplicate state deterministically, and rapid drawer/customize cycles have live automation coverage. | Closed after migration tests, rapid-cycle artifacts, and independent review. |

### Notifications And Explore

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-NOT-001 | P1 | **Verified 2026-07-28.** Notifications has a canonical virtualized workspace with unread/all/participating filters, automatic paging and polling, optimistic read/unread and mark-all-read mutations, and thread subscription/mute controls. Successful remote mutations remain authoritative when local cache invalidation fails. | Closed after mutation/failure-path review, 50 focused tests, five-width artifact review, and independent review. |
| AUD-NOT-002 | P1 | **Verified 2026-07-28.** Notification destinations preserve specific internal routes for issues, pull requests, and commits, use truthful type-specific GitHub destinations for supported external surfaces, and route Check Suite notifications to repository Actions without misusing the suite ID as a commit SHA. | Closed after destination-policy review, focused routing tests, and independent review. |
| AUD-NOT-003 | P2 | **Verified 2026-07-28.** Notifications, Shell, and Home share account-partitioned unread state; Home rows use keyed in-place updates and immediately project optimistic read-state changes without stale refresh regression. | Closed after shared-state concurrency review, keyed-update tests, and independent review. |
| AUD-EXP-001 | P1 | **Verified 2026-07-28.** The shell route is visibly labeled `Search` and focuses the command search surface; the misleading `Explore` label is absent. | Closed after focused source-contract tests and independent review. |

### Settings And Diagnostics

Strengths: useful privacy controls, cache diagnostics, separate Stars library
clear action, confirmations, NDJSON export, contributor detail, and concept-led
theme cards.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-SET-002 | P1 | **Reverified 2026-08-10.** The adaptive section rail/compact selector keeps navigation fixed while only the selected section content scrolls. Section changes reset that local scroll position after layout, so controls in a newly selected short section never remain unrealized below an inherited offset. | Closed after five-width dark/light automation, section-switch regression coverage, and screenshot review. |
| AUD-SET-003 | P1 | **Verified 2026-07-17.** Headings/actions reflow without clipping at every audited width; contributor biographies wrap completely and social actions remain visible at `640px`. | Closed after five-width screenshots, automation, and independent review. |
| AUD-SET-004 | P1 | **Verified 2026-07-17.** Settings section items, theme cards, toggles, data actions, contributor links, and confirmation dialogs expose stable UIA ids and meaningful names. | Closed after accessibility automation and independent review. |
| AUD-SET-005 | P2 | **Verified 2026-07-17.** Redundant captions and persistent diagnostic prose were removed; state text appears only when actionable. Independent review found no remaining issue. |
| AUD-SET-006 | P2 | **Verified 2026-07-17.** Snapshots refresh automatically and failures expose contextual retry without a browser-like Refresh command. Independent review found no remaining issue. |
| AUD-SET-007 | P2 | **Verified 2026-07-17.** Unpackaged builds report a truthful development/assembly version instead of `0.0.0.0`. Independent review found no remaining issue. |
| AUD-SET-008 | P2 | **Verified 2026-07-17.** Sparse categories were consolidated into useful settings groups while preserving diagnostics/data depth. Independent review found no remaining issue. |
| AUD-SET-009 | P2 | **Verified 2026-07-17.** Contributor dialogs use theme resources with no hardcoded black foreground and pass light, dark, and High Contrast review. | Closed after theme verification and independent review. |

### My Issues

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-MIS-001 | P1 | **Verified 2026-07-28.** My Issues automatically pages work items and comments in 100-item batches, reconciles rows by stable key, preserves selection and scroll anchors, and retains published rows with explicit partial status when a later page fails. Closed after 43 focused tests, a zero-warning product build, and independent review. |
| AUD-MIS-002 | P2 | **Verified 2026-07-28.** Persistent cache diagnostics were removed; saved/stale state appears only with an actionable refresh failure. Independent review found no remaining issue. |
| AUD-MIS-003 | P2 | **Verified 2026-07-28.** The selected-item action uses native `Open issue` wording, native iconography, and internal shell routing. Closed after live interaction and independent review. |
| AUD-MIS-004 | P2 | **Verified 2026-07-28.** Assigned/Created/Mentioned and Open/Closed/All use native segmented controls with compact ComboBox fallbacks selected from available width and measured localized labels. Closed after long-label layout tests and independent review. |

### Repository Workspace Chrome And Actions

Strengths: the `owner/name` identity, native Code/Issues/Pull Requests/Commits
navigation, branch picker, and compact Watch/Star/Fork actions give every
repository page a coherent shared context.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-RWC-001 | P1 | **Verified 2026-07-28.** Repository identity, branches, star state, and watch state use the shared stale-first repository query facade with validators, cancellation, independent section state, and cached content retained during refresh. | Closed after 81 focused tests, a clean app build, and independent source review. |
| AUD-RWC-002 | P1 | **Verified 2026-07-28.** Fork readiness uses bounded cancellation-aware polling with backoff, rate-limit handling, visible progress, and recoverable timeout/failure state instead of unbounded silent polling. | Closed after focused policy/service tests, build verification, and independent review. |
| AUD-RWC-003 | P1 | **Verified 2026-07-28.** Star/watch use optimistic state with rollback and non-negative counts, invalidate only action-state tags, retain branch/identity caches, and publish canonical Stars changes consumed by shell, Dashboard, Profile, and the library. | Closed after 96 focused tests, live mutation/failure coverage, and fresh independent review. |
| AUD-RWC-004 | P2 | **Verified 2026-07-28.** Branch, star, and watch refresh failures preserve the last known state and expose non-blocking unavailable/retry feedback without substituting empty or false values. | Closed after failure-path tests, recoverable-state artifact review, and independent review. |
| AUD-RWC-005 | P2 | **Verified 2026-07-28.** Repository identity, sections, branch selection, and actions collapse into one coordinated accessible compact menu; all five widths, keyboard operation, branch search, and page-two branch selection pass. | Closed after fresh five-width automation, open-flyout artifact review, and independent review. |
| AUD-RWC-006 | P2 | **Verified 2026-07-28.** Live repository-action automation covers Watch/Unwatch keyboard paths, Star/Unstar/Undo and library handoff, searchable branch selection, compact overflow, fork success/timeout/failure/rate-limit recovery, rollback, persistence, and rapid route overlap. | Closed after 110 focused tests, fresh interaction probes, screenshot review, a zero-warning build, and independent review. |

### Repository Issues

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-RIS-001 | P1 | **Verified 2026-07-28.** Repository issue list/detail/prefetch reads use the persistent Phase 0 facade, while the in-memory navigation cache remains the predictive handoff layer. | Closed after focused tests, a sequential Debug build, and independent architecture review. |
| AUD-RIS-002 | P1 | **Verified 2026-07-28.** Issue lists, comments, and timeline events paginate automatically; stale short pages receive authoritative refresh, later-page failures remain explicit partial state, and cached visible tails survive incomplete refreshes. | Closed after 11 focused regression tests, an isolated zero-warning Debug build, and independent review. |
| AUD-RIS-003 | P1 | **Verified 2026-08-05.** The dedicated responsive Issues probe now validates settled drawer edges, opener/closer alignment within `1.5px`, light dismiss, `Esc`, focus containment and restoration, and scroll-anchor stability across My Issues and repository Issues. Closed after 61 focused checks, a clean isolated six-width live run, screenshot review, and independent review. |
| AUD-RIS-004 | P2 | **Verified 2026-07-28.** Repository Issues uses the coordinated `760px` collapse point so list and inspector leave the reading area before title, primary action, and comment content become cramped. | Closed after five-width live automation, detail/action bounds assertions, screenshot review, and independent review. |
| AUD-RIS-005 | P2 | **Verified 2026-07-28.** Filters, edit and metadata actions, reactions, state/comment controls, responsive drawer controls, and compact overflow commands expose distinct stable IDs and meaningful accessible names. Closed after focused accessibility contracts and independent source review. |
| AUD-RIS-006 | P1 | **Verified 2026-08-06.** Repository Issues models create, edit, metadata, state, comment, and reaction capabilities from authentication, repository permissions, authorship, lock/archive state, and current capability denials. Ordinary `403` failures degrade only the rejected current capability, rate-limit `403` responses remain transient failures, authoritative refresh recovers permissions, stale failures are suppressed, and deleted authors remain passive and null-safe. Closed after 27 focused regression tests, a zero-warning product build, and fresh independent review. |

### My Pull Requests

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-MPR-001 | P1 | **Verified 2026-07-28.** My Pull Requests automatically pages the list, conversation, commits, reviews, review comments, and timeline; later-page failures preserve cached and already-published rows. Closed after focused paging/failure tests, a zero-warning build, and independent review. |
| AUD-MPR-002 | P2 | **Verified 2026-07-28.** My Pull Requests consumes the canonical `PullRequestNavigationSnapshot`, exposes the native section selector and explicitly named actions, and routes repository navigation internally through the shell. Closed after focused parity tests and independent review. |
| AUD-MPR-003 | P2 | **Verified 2026-07-28.** My Pull Requests keeps implementation/cache prose out of persistent chrome and reports only contextual section errors while preserving visible content. Independent review found no remaining issue. |

### Repository Pull Requests

Strengths: good `AdaptiveWorkspace` migration, cached-first selection,
predictive prefetch, keyed detail sections, Markdown comment/reply parity,
metadata editing, reactions, close/reopen, and merge actions.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-RPR-001 | P1 | **Verified 2026-07-28.** Repository pull requests and every requested detail collection auto-page in 100-item batches, retain partial loaded rows on interruption, and stop safely on short or duplicate pages. Closed after 37 focused PR tests, clean product and automation builds, and independent pagination review. |
| AUD-RPR-002 | P1 | **Verified 2026-07-28.** Pull requests now include a native Files diff workspace and typed Comment, Approve, and Request Changes review submission with validation and tested endpoint payloads. Closed after 60 focused PR tests, clean product and automation builds, and independent review. |
| AUD-RPR-003 | P2 | **Verified 2026-07-28.** Non-wide pull request layouts use one compact action overflow, flexible title sizing, and distinct accessible names for each action. Closed after focused responsive/accessibility tests and independent review. |
| AUD-RPR-004 | P2 | **Verified 2026-07-28.** Conversation keeps the full composer fixed below its scrolling content, while narrow and compact modes expose a persistent Comment action that immediately opens the Markdown composer. Closed after responsive source review, focused PR tests, and independent UX verification. |
| AUD-RPR-005 | P2 | **Verified 2026-07-28.** Pull request actions and prefetch emit allowlisted, identifier-free, best-effort telemetry; telemetry and prefetch failures cannot escape into user workflows. Closed after focused fault-isolation tests and independent review. |
| AUD-RPR-006 | P1 | **Verified 2026-07-28.** Pull request actions are driven by refreshed repository/viewer permissions, mergeability, branch protection, and repository-enabled merge methods; endpoint-specific denials recover after authoritative refresh, and target-aware guards prevent delayed failures from affecting a newly selected PR. Closed after 22 focused reliability/policy tests, a zero-warning build, and independent review. |

### Repository Commits

Strengths: wrapped unified diff, continuous XAML virtualization, no horizontal
scrollbar, multi-line selection/copy, search/filter, compare, checks, comments,
inspector data, cache, cancellation, and neighbor/dwell prefetch.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-COM-001 | P1 | **Verified 2026-07-28.** Commit history, comments, check runs, and associated pull requests auto-page in 100-item batches while detail sections remain independently cached. | Closed after multi-page service tests and independent review. |
| AUD-COM-002 | P1 | **Verified 2026-07-28.** Commit history, detail, and inspector are inline at normal wide desktop width; narrowing collapses inspector first, app rail second, and commit history last. | Closed after five-profile breakpoint coverage and independent runtime review. |
| AUD-COM-003 | P2 | **Verified 2026-07-28.** Commit date filtering uses native `CalendarDatePicker` controls with clearable Since/Until chips. | Closed after XAML contract coverage and independent review. |
| AUD-COM-004 | P2 | **Verified 2026-07-28.** Commit-scoped repository navigation is explicitly named `Browse files`, with one accessible action surface and no duplicate compact overflow. | Closed after control contract coverage and independent review. |
| AUD-COM-005 | P2 | **Verified 2026-07-28.** A deterministic large-commit gate enforces selection input, first visible rows, search indexing, dispatcher stall, scroll frame rate, and working-set budgets. | Closed after a live pass at 12.1ms input, 395.3ms first rows, 148.5ms search, 47.4ms maximum dispatcher stall, 237.6fps scrolling, and 292MiB working set plus independent review. |
| AUD-COM-006 | P2 | **Verified 2026-07-28.** Hover, dwell, neighbor, and handoff prefetches plus comment, copy-SHA, and browse-files actions emit canonical identifier-free telemetry with outcome and duration classifications. | Closed after focused prefetch/action/sanitizer tests and independent review. |

### Repository Code

Strengths: native tree, resizable separator, back/forward breadcrumb, rich/plain
README modes, embedded code editor, file blob memory/disk cache, and useful
binary/image fallback architecture.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-COD-001 | P1 | **Verified 2026-07-28.** Repo Code uses `AdaptiveWorkspace`; the tree becomes an animated leading drawer before it consumes the reading area and the file detail remains primary. | Closed after five-width drawer automation and independent review. |
| AUD-COD-007 | P1 | **Verified 2026-07-17.** Removed the fixed `700px` minimum height; the Code workspace now adapts to the available frame through `640x600` without placing content outside the app. Closed after compact screenshot review, focused tests, clean builds, and independent review. |
| AUD-COD-002 | P1 | **Verified 2026-07-28.** Tree, directory, and blob reads use the Phase 0 query transport with validators, tags, dedupe, mutable/ref versus 30-day immutable-SHA policy, while the dedicated repository-file cache is registered, measured, displayed, and independently clearable in Settings. | Closed after cache/transport tests, clean builds, live route review, and independent rereview. |
| AUD-COD-003 | P1 | **Verified 2026-07-17.** Branch/ref controls, file tree rows, breadcrumb actions, copy/open controls, repository actions, and compact overflow expose stable ids and meaningful names with live keyboard invocation coverage. |
| AUD-COD-004 | P2 | **Verified 2026-07-28.** Branch/ref refresh keeps the previous tree and file readable, updates keyed nodes in place, and reports contextual failure without a full-workspace loading overlay. | Closed after stale/failure tests, live automation, and independent review. |
| AUD-COD-005 | P2 | **Verified 2026-07-28.** Repo Code supports find next/previous, background symbol outline navigation, copyable current-line GitHub links, native tree invocation, and F6 tree/detail traversal across inline and drawer layouts. | Closed after 150 focused tests, five-width live automation, performance evidence, and independent rereview. |
| AUD-COD-006 | P2 | **Verified 2026-07-28.** Authoritative request generations prevent stale tree/file loads from overwriting newer navigation; cache and offline failures retain the last readable content. | Closed after overlap/cancellation tests and independent review. |

### Native Markdown Renderer And Composer

Strengths: custom selectable Markdown, native link handling, themed code and
comment surfaces, SVG/image handling, lazy image loading, preview composition,
and a substantial dedicated unit suite. This is central app infrastructure and
must remain visually consistent across Issues, PRs, Profile, README, commit
comments, and future Gists.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-MD-001 | P1 | **Verified 2026-08-07.** App-level lifecycle automation opens all 21 real Markdown host compositions in isolated Debug and Release processes, exercises active text, images, and SVGs, closes through the native window Close command, and requires clean process exit with zero dispatcher, graphics-device, or unhandled-exception diagnostics. The compact-only PR composer passes the same host lifecycle gate. | Closed with the complete `markdown-lifecycle-full-final-debug-v9` and `markdown-lifecycle-full-final-release-v1` matrices plus independent Markdown review. |
| AUD-MD-002 | P1 | **Verified 2026-08-06.** Every Markdown surface declares a canonical host kind and stable instance identity through `MarkdownHostContract`; comment cards, issue/PR bodies, README/profile README, preview mode, light/dark/High Contrast, and nested SVG treatment share the same semantic surface and automation contract. Closed after 68 focused app/host/security checks, the 321-test renderer suite, a warning-free Release build and release gates, and fresh independent Markdown review. |
| AUD-MD-003 | P2 | **Reverified 2026-08-09.** The real-host fixture covers large documents, tables, task lists, deep quotes, code blocks, relative, blocked, malformed, inline, and animated images, hostile SVG/HTML, internal/external link routes, repeated relayout, scroll stability, and memory budgets in Debug and Release. | Closed with complete 567-case lifecycle manifests in both configurations, 335 renderer tests per configuration, 82 pixel tests per configuration, release/security gates, and independent fixture review. |
| AUD-MD-004 | P2 | **Verified 2026-08-07.** Every real host exposes ordered TextPattern content and passes physical cross-line selection, `Ctrl+C`, context-menu Copy without selection loss, keyboard link focus, internal/external routing, and representative 100%, 150%, and 200% text-scale behavior. | Closed with all-host Debug/Release matrices, focused final-binary issue and compact composer runs, accessibility contracts, and independent UIA review. |
| AUD-MD-005 | P2 | **Verified 2026-07-17.** The renderer test suite is warning-clean and remains behaviorally meaningful under warnings-as-errors. | Closed after 303 passing tests and independent review. |

### Stars Library

Strengths: canonical two-pane native workspace, durable account-partitioned
SQLite/FTS index, progressive full sync, custom many-to-many categories,
filters/sort/search, automatic incremental rows, native Checkbox selection,
bulk actions, hover unstar, Undo, responsive category drawer, and shell/profile
integration.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-STA-001 | P0 | **Verified 2026-07-17.** Repeated selection-mode on/off soak coverage passes without page closure, state loss, or lifecycle failure. Closed after independent review. |
| AUD-STA-002 | P1 | **Verified 2026-07-17.** Category dialogs dismiss deterministically, automation fixtures are isolated, and duplicate automation categories are cleaned before and after each run. Closed after independent review. |
| AUD-STA-003 | P1 | **Verified 2026-07-17.** Stars smart lists, categories, repositories, selection checkboxes, row actions, filters, and compact drawer controls expose stable UIA identities and survive repeated interaction cycles. | Closed after live UIA automation, screenshot review, and independent review. |
| AUD-STA-004 | P2 | **Verified 2026-07-28.** Category create, update, reorder, and delete emit canonical identifier-free telemetry, and committed SQLite mutations remain successful even if telemetry throws. | Closed after a fault-injected mutation matrix and independent review. |
| AUD-STA-005 | P2 | **Verified 2026-07-17.** One real-SQLite account fixture covers remote import, offline unstar, durable replay, full reconciliation, Undo, category membership/deletion/reorder, and reopen; UI automation covers drag assignment and relaunch persistence. |
| AUD-STA-006 | P1 | **Verified 2026-08-10.** Background sync request ownership is account-partitioned and batch-safe: a cancelled or abandoned force-full request remains pending for the next owner, while rapid incremental requests coalesce without duplicate visible resets. | Closed after focused coordinator tests plus fresh `stars-library` and `stars-selection-mode` live probes. |

### Gists

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-GIS-001 | P1 | **Verified 2026-07-28.** Remote create/update/delete success is an irreversible commit boundary; local projection, durability, telemetry, cancellation, scheduling, and reconciliation failures cannot expose a retryable mutation and instead use a non-blocking durability warning while reconciliation continues. Successful delete clears stale errors. | Closed after 94 focused tests, a clean app build, five-width artifact review, mutation-failure source review, and an independent post-fix verification pass. |
| AUD-GIS-002 | P1 | **Verified 2026-07-28.** Gists automatically page beyond 30, reconcile conservatively, and update keyed rows without clearing or flashing the visible list. | Closed after restart/offline service tests, live five-width automation, and independent review. |
| AUD-GIS-003 | P2 | **Verified 2026-07-28.** Healthy-state implementation captions/count prose were removed and the fixed workspace uses dense rows and available width intentionally. | Closed after five-width screenshot review and independent review. |
| AUD-GIS-004 | P2 | **Verified 2026-07-28.** The native workspace provides local search, visibility filtering, sorting, and a contextual New gist action. | Closed after UI automation and independent review. |
| AUD-GIS-005 | P2 | **Verified 2026-07-28.** Gist rows, files, editor actions, and context commands expose stable UIA ids and visible-title accessible names, including keyboard context-menu paths. | Closed after UIA automation and independent review. |
| AUD-GIS-006 | P2 | **Verified 2026-07-28.** Gists uses a distinct braces glyph in shell navigation and commands rather than the Issues glyph. | Closed after shell inspection and independent review. |

### Profile

Strengths: shared authenticated/public profile page, cached identity-first load,
lazy section reads, native contribution heatmap, Markdown README, REST profile
edit, follow/unfollow, organizations/highlights, and fixed identity/content
workspace.

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-PRO-001 | P1 | **Verified 2026-07-28.** Authenticated Repositories, Stars, and Gists route to their canonical workspaces; public repositories/stars use visible profile modes, and Followers/Following have a fixed titled people surface with a visible Back action. | Closed after navigation-policy tests, source routing review, and five-width workspace review. |
| AUD-PRO-002 | P1 | **Verified 2026-07-28.** Repository, star, activity, follower, and following modes use virtualized `ListView` collections with stable keyed observable rows; the former `MainRail.Children` rebuild path is gone. | Closed after collection contract tests, retained-row binding review, and mode screenshot review. |
| AUD-PRO-003 | P1 | **Verified 2026-07-28.** Overview loads cached identity, contribution, README, and bounded pinned data only; repositories, public stars, activity, followers, and following page lazily inside explicit full modes. | Closed after paging/query tests and first-render source review. |
| AUD-PRO-004 | P1 | **Verified 2026-07-17.** Contribution days are inspectable through the focusable Calendar peer, per-day name updates, tooltips, and arrow/Home/End navigation without horizontal scrolling. |
| AUD-PRO-005 | P2 | **Verified 2026-07-28.** Profile labels public destinations as `Public repositories`, `Public stars`, and `Public activity`; authenticated Stars exposes and routes to the canonical Stars library instead of a misleading preview. | Closed after authenticated/public policy tests and XAML/source review. |
| AUD-PRO-006 | P2 | **Verified 2026-07-28.** Website, email, and Twitter facts use validated HTTPS/mailto actions with native keyboard/context/copy behavior and stable UIA identities; passive facts remain truthful. Responsive Profile automation passes all five widths plus navigation Back and focus restoration. | Closed after 70 focused tests, clean builds, live profile probes, and fresh independent review. |
| AUD-PRO-007 | P2 | **Verified 2026-07-28.** Markdown user, repository, issue, and pull-request links route through the internal shell; unsupported GitHub routes and external links retain explicit browser launch behavior. | Closed after 71 focused profile/navigation tests and independent source-policy review. |
| AUD-PRO-008 | P1 | **Verified 2026-08-05.** The shared Avatar now provides native hover, pressed, keyboard, and accessibility behavior with stable identities and internal Profile routing; unavailable and bot identities remain passive, and Back restores focus after virtualized realization. Closed after 67 focused checks, clean product and automation builds, and independent review. |
| AUD-PRO-009 | P1 | **Reverified 2026-08-10.** The authenticated profile editor remains centered and bounded at short desktop heights, owns exactly one field scroll region, and keeps every REST-supported field reachable without clipping the fixed dialog commands. The responsive probe now reaches Profile through the production Shell and records actual native window bounds. | Closed after the shell-hosted live five-width Profile probe, direct lower-field reachability assertions, `artifacts/review-fixes/profile-responsive` screenshot review, focused dialog/viewport contracts, and warning-free product/automation builds. |

### Repository Search

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-SRC-002 | P1 | **Verified 2026-07-17.** Search has one canonical page view model; the legacy XAML-instantiated implementation and split ownership were removed. | Closed after architecture review and independent review. |
| AUD-SRC-003 | P1 | **Verified 2026-07-17.** Search is a fixed native workspace with supported GitHub sorts, owner/language/topic/visibility/fork/archive filters, result scope, cached page snapshots, automatic paging, and non-blanking error recovery. |
| AUD-SRC-004 | P1 | **Verified 2026-07-17.** Search debounces and cancels superseded requests, keeps prior rows visible, assigns page ownership, and applies keyed snapshots so refreshed page-one rows do not blank or leave stale entries. Closed after focused tests and independent review. |
| AUD-SRC-005 | P2 | **Verified 2026-07-17.** Search automation covers stable ids/names, row hover/pressed/selected states, mandatory keyboard traversal, internal navigation, and responsive screenshots. |

### Repository Management

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-REP-001 | P1 | **Verified 2026-07-28.** Repo Manage is the canonical account repository browser with shared index state, local search, visibility/fork/archive filters, sorting, open/create commands, and explicit selection mode. | Closed after 46 focused repository tests, an isolated app build, and independent review. |
| AUD-REP-002 | P1 | **Verified 2026-07-28.** Cached repository rows render first and paged reconciliation applies progressively by stable key without clearing the visible collection. | Closed after focused stale-first/progressive reconciliation tests and independent no-flash review. |
| AUD-REP-003 | P1 | **Verified 2026-07-28.** Delete is contextual to row/selection actions, requires explicit confirmation, and is no longer the page's primary framing. | Closed after focused interaction/source review and independent destructive-action review. |
| AUD-REP-004 | P2 | **Verified 2026-07-28.** Repository synchronization is automatic and non-blocking, retry appears only for actionable failures, and rows expose accessible state plus permission-aware context actions. | Closed after source-contract tests, live keyboard/context interaction, and independent review. |
| AUD-REP-005 | P2 | **Verified 2026-07-28.** Dedicated repository-library automation reaches the canonical page and covers search, filters, sort, hover, selection, context actions, cached reactivation, and all five responsive widths. | Closed after fresh live probes, screenshot review, lifecycle teardown verification, and independent review. |
| AUD-REP-006 | P1 | **Verified 2026-07-28.** Repository counts now use explicit `public repos`, `preview repositories`, `account repositories`, `indexed repositories`, and filtered `x of y` scopes; authenticated Profile routes to the canonical account library. | Closed after taxonomy tests, five-width artifact review, and independent source/routing review. |

### Login, Auth, And Activation

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-AUT-005 | P1 | **Verified 2026-07-17.** Debug uses the distinct `JitHub.WinUI.Debug` package and `jithub-dev` protocol, cleanup is repository-scoped, and a real callback activation targeted the current Debug build. | Closed after identity contract tests, live activation smoke, and independent review. |
| AUD-AUT-006 | P1 | **Verified 2026-07-17.** Login-launch failures are sanitized and surfaced through an in-app `InfoBar` while leaving sign-in available for retry. | Closed after unit/UI automation and independent review. |
| AUD-AUT-007 | P2 | **Verified 2026-07-17.** The sign-in button has stable UIA identity, an accessible name, and compiled bindings exercised against the live UIA tree. | Closed after UI automation and independent review. |
| AUD-AUT-008 | P2 | **Verified 2026-08-07.** Fresh isolated authentication lifecycle automation covers cancel, invalid state, expired token, notification-scope reconnect, offline launch, protocol reactivation, and multi-account routing, confirmation, and cleanup, with light/dark coverage where applicable. | Closed with `auth-lifecycle-final`, security/release gates, and independent authentication review. |
| AUD-AUT-009 | P2 | **Verified 2026-07-17.** Login automation launches isolated light, dark, and failure processes and validates deterministic theme-aware screenshots and live controls. | Closed after artifact inspection, a fresh automation run, and independent review. |

### Dialogs, Modals, Dev Surfaces, And Legacy Controls

| ID | Pri | Finding / action |
| --- | --- | --- |
| AUD-DLG-001 | P1 | **Verified 2026-08-07.** The compact dialog matrix covers the shell modal host, New Repository, issue/PR forms, metadata/reactions, delete confirmations, merge, profile edit, widget customization, and Stars categories at `900x700`, `760x650`, and `640x600`. | Closed with `final-dialog-matrix-current-v2`: 47 passed, 0 blocked, 0 failed, plus independent screenshot review. |
| AUD-DLG-002 | P2 | **Verified 2026-08-07.** Dialogs share semantic backgrounds and scrims, centered bounded geometry, focus trapping, explicit Esc/light-dismiss policy, busy/disabled states, validation, and post-close focus restoration at all audited compact widths. | Closed with the 47-case compact matrix, theme/High Contrast contracts, and independent WinUI design review. |
| AUD-DLG-003 | P2 | **Verified 2026-08-07.** Rapid open/close is deduplicated and repeated submission cannot create duplicate operations, stranded overlays, or stale focus restoration. | Closed with repeated-open/submit cases in `final-dialog-matrix-current-v2`, coordinator tests, and independent lifecycle review. |
| AUD-DLG-004 | P1 | **Verified 2026-07-17.** New Repository defaults to `No license`, explains the consequence, and omits `license_template` unless the user intentionally chooses a license. Closed after exact serializer/payload tests, clean builds, and independent review. |
| AUD-DEV-001 | P3 | **Verified 2026-07-28.** Design Lab remains developer-only: normal launch requires developer mode, isolated automation roots retain deliberate access, shell discovery is gated, and fake version content is absent. | Closed after route-policy tests and independent source review. |
| AUD-DEV-002 | P3 | **Verified 2026-07-28.** Dev Console and Design Lab are absent from normal production navigation unless Developer Mode is enabled, guarded again at execution, and the retired legacy issue/PR/commit routes and dependency islands are deleted. | Closed after route-policy tests, retired-path contracts, a current zero-warning build, and independent review. |

## Interaction Audit Matrix

| Surface | Interaction coverage | Closure result |
| --- | --- | --- |
| Shell navigation | Home, Issues, PRs, Notifications, Stars, Gists, Explore, Settings, history, profile, and repository routes | Passed live click, selected-state, focus-return, compact drawer, and telemetry probes. |
| Shell hover | Nav, top actions, search, repository filters/items, profile/footer, and Home actions | Passed normal, hover, focus, disabled/error, and invocation checks. |
| Command search | `Ctrl+K`, typing, keyboard traversal, visible submit, suggestion invoke, `Esc`, Explore focus | Passed through one canonical route/state machine. |
| Home board | Wide/compact, side drawer, customize persistence, and all truthful View-all actions | Passed responsive, keyboard, persistence, and destination probes. |
| Settings | Seven sections, local section scrolling, diagnostics/data actions, and dialogs | Passed wide-to-compact layout, stable-width, keyboard, theme, and action checks. |
| My Issues | Filters, automatic paging, row hover/click, compact drawers, detail, and scroll anchor | Passed stale-first, no-flash, keyboard, and responsive probes. |
| Repo Issues | Filters, rows, selection/prefetch, detail/Markdown, inspector, comment form, and drawers | Passed real interaction, no-flash, lifecycle, and five-width probes. |
| Repo PRs | Rows, sections, automatic paging, drawers, Markdown/replies, metadata, actions, and scroll anchor | Passed parity, accessibility, prefetch, and responsive probes. |
| Commits | Rows, sections, drawers, filters, virtualized wrapped diff, search, selection/copy, compare, and comments | Passed performance, keyboard, lifecycle, and responsive probes. |
| Code | Tree/drawer, navigation history, refs, file actions, cache behavior, keyboard, and responsive layout | Passed shared-query, stale-completion, action, and five-width probes. |
| Profile | Identity workspace, modes, edit/follow, contribution graph, README, repositories, and internal routing | Passed cached-first, keyboard, dialog, accessibility, and responsive probes. |
| Stars | Categories, drawer, search/filter/sort, keyboard, selection, drag assignment, unstar/undo, and persistence | Passed full lifecycle, offline, large-library, and category probes. |
| Gists | Automatic paged library, detail, Markdown, routing, wide/compact, and keyboard interaction | Passed native detail and shared cache/quiet-update contracts. |
| Search / Repo Manage | All shell entry paths, cached result/index display, paging, filters, actions, and offline state | Passed canonical routing and warm/offline/large-account gates. |
| Login/auth | Light/dark launch, callback/error states, reconnect, offline, protocol, and account cleanup | Passed isolated lifecycle and security automation. |
| New/edit/delete/merge/reaction dialogs | Unified shell-owned modal behavior at `900`, `760`, and `640` widths | 47 passed, 0 blocked, 0 failed. |

## Responsive Audit Matrix

| Surface | 1366 | 1180 | 900 | 760 | 640 |
| --- | --- | --- | --- | --- | --- |
| Shell/Home | Two rails | Two rails | Compact side rail | Compact drawers | Compact drawers |
| Settings | Stable wide | Stable wide | Compact navigation | Compact navigation | Compact navigation |
| My Issues | Three pane | Adaptive | Adaptive | Detail-first drawers | Detail-first drawers |
| Repo Issues | Three pane | Coordinated collapse | Adaptive | Detail-first drawers | Detail-first drawers |
| Repo PRs | Three pane | Coordinated collapse | Adaptive | Detail-first drawers | Detail-first drawers |
| Commits | Three pane | Coordinated collapse | Adaptive | Detail-first drawers | Detail-first drawers |
| Code | Tree/detail | Tree/detail | Adaptive | Detail-first drawer | Detail-first drawer |
| Stars | Two pane | Two pane | Two pane | Category drawer | Category drawer |
| Profile | Identity/content | Identity/content | Adaptive | Compact identity | Compact identity |
| Gists | Library/detail | Library/detail | Adaptive | Compact | Compact |
| Search | Stable wide | Stable wide | Adaptive | Compact | Compact |
| Repo Manage | Stable wide | Stable wide | Adaptive | Compact | Compact |

## Verification Record

Final build and test matrix:

- `JitHub.WinUI.Tests`: 2,542/2,542 in Debug and 2,542/2,542 in Release.
- `MarkdownRenderer.Tests`: 335/335 in Debug and 335/335 in Release.
- `MarkdownRenderer.PixelTests`: 87/87 in Debug and 87/87 in Release.
- `JitHub.WinUI`: warning-free Debug and Release builds; Release includes
  60/60 embedded release/security gates.
- `JitHub.WinUI.Automation`: warning-free Debug and Release builds.
- `JitHub.Web`, `JitHub.Web.Tests`, and `JitHub.WinUI.PerformanceGate`:
  warning-free Debug and Release builds; web tests pass 17/17 in both configurations.
- Direct and transitive dependency vulnerability scans are clean for the
  product, tests, web callback app, automation harness, and Markdown renderer.
- Native screenshots and lifecycle logs were visually reviewed during the
  closure run. They are intentionally ephemeral and were removed on
  2026-08-10 after the worktree exhausted the system drive; regenerate them
  with the automation commands in
  `docs/jithub-vnext-audit-remediation-handoff.md` when fresh artifacts are
  required.

Final live evidence:

- Performance: latest exact evidence is
  `artifacts/performance/vnext-publication-full-eight.json`, which records 4,970
  measurements, all 55 final eight-route Warm budgets passing, and a clean exit
  after all 80 measured launches. The former 51/55
  `vnext-handoff-full-eight-exact-final-v2.json` report is retained only as the
  superseded baseline, alongside
  `artifacts/performance/notifications-large-report-v8.json`,
  plus the passing My Issues/My PR evaluations in
  `work-notifications-large-report-v2.json` (its earlier Notifications sample is
  superseded by v8),
  `commit-code-performance-report-v5.json`,
  `repo-manage-warm-report-v10.json`, `stars-warm-report-v7.json`, and
  `stars-manage-offline-report-v7.json`.
- Superseded post-review performance history: the complete eight-route rerun in
  `vnext-recovery-full-eight-post-review-v2.json` passed 54/55; its sole miss was
  a pre-handler My Pull Requests input spike (`50.73ms` input, `6.76ms` render,
  `55.79ms` p95). Immediate ten-iteration reruns in
  `vnext-recovery-my-pr-post-review.json` and
  `vnext-recovery-my-pr-post-review-repeat.json` passed all 8/8 route budgets at
  `42.10ms` and `49.50ms` cached-selection p95. The full runner emitted a
  shutdown-only FlaUI COM-wrapper finalizer fault after writing its report; the
  isolated reruns exited cleanly. The August 13 publication matrix subsequently
  passed `55/55` and exited cleanly, so neither anomaly remains active release
  risk.
- Dialogs: `artifacts/final-dialog-matrix-current-v2` records 47 passed,
  0 blocked, and 0 failed compact cases.
- Markdown lifecycle: `artifacts/markdown-lifecycle-full-final-debug-v9` and
  `artifacts/markdown-lifecycle-full-final-release-v1` each complete all
  567/567 real-host, theme, text-scale, and viewport cases with zero failures.
- Shell: `artifacts/final-shell-audit-release-post-lifecycle`.
- Authentication: `artifacts/auth-lifecycle-final`.
- Accessibility: `artifacts/keyboard-accessibility-matrix-final`,
  `artifacts/keyboard-commit-diff-search-final`, and
  `artifacts/high-contrast-live-final-v3`.
- Localization:
  `artifacts/final-localization-release-post-lifecycle/vnext-pseudo-localization-final-v2`
  plus focused long-string matrices.
- Settings: `artifacts/final-independent-review/settings-responsive`. The fresh
  run truthfully skipped the genuine Contrast palette because OS High Contrast
  was not active; historical `high-contrast-live-final-v3` remains the live
  High Contrast acceptance evidence.
- Profile: `artifacts/final-independent-review/profile-responsive` (production
  Shell navigation; constrained captures include actual and requested native bounds).
- Shell and responsive chrome:
  `artifacts/final-independent-review/shell-responsive`.
- Issues progressive/responsive workspace:
  `artifacts/final-independent-review/issues-responsive-workspace`.
- Pull Requests responsive workspace:
  `artifacts/final-independent-review/pull-requests-responsive-workspace`.
- Repository Code responsive workspace:
  `artifacts/final-independent-review/repo-code-responsive-workspace`.
- Commit virtualized diff:
  `artifacts/final-independent-review/commits-virtualized-diff`.
- Stars: `artifacts/final-workspaces-release-post-lifecycle/stars-library-final-v12`
  and `artifacts/final-workspaces-release-post-lifecycle/stars-categories-final-v5`,
  reverified by `artifacts/final-recovery-authored/stars-library` and
  `artifacts/final-recovery-authored/stars-selection-mode`.
- Pull-request generated identity stability:
  `artifacts/final-recovery-authored/pull-request-reply-identities`.
- Historical run names above remain traceability references only. Generated
  artifacts are not committed and are no longer present after the storage
  cleanup; fresh runs should write under `artifacts/final-independent-review`.

All page-specific acceptance probes named above passed. The automation bridge
requires explicit WinUI acknowledgement, and cached traversal completion is
measured after the rendered frame rather than at command dispatch. The recovered
exact-source matrix supersedes the 51/55 baseline and closes all four cached-
selection failures without budget changes.

## Completed Execution Order

1. Stabilized automation identity/state and closed all P0 interaction failures.
2. Rebuilt Settings responsively and closed dependency/build warnings.
3. Converged Search and Repository Management onto the vNext architecture.
4. Established shared pagination, cache, and quiet-update contracts across all
   canonical data surfaces.
5. Coordinated shell/workspace breakpoints and migrated Code to
   `AdaptiveWorkspace`.
6. Completed UIA names, keyboard behavior, localization, High Contrast, and
   telemetry route by route.
7. Added mature Notifications, native Gist detail, PR review, and canonical
   account repository browsing.
8. Removed retired legacy routes and dependency islands after parity was
   verified.

Independent review passes compared this tracker with the route inventory,
principal view models/services, current binaries, screenshots, UI Automation
evidence, security and performance gates, and native-layout principles. The
last data/security/performance review produced eight hardening findings; all
eight are implemented and covered by the verification record above. Fresh final
passes reviewed WinUI correctness/accessibility/concurrency and then data,
security, performance, and resource ownership; their actionable findings were
fixed before the final focused validation.

## Definition Of Audit Closure

This audit itself is complete when an independent reviewer has inspected the
tracker against the code, route inventory, screenshots, automation results, and
native-layout principles and reports no material unrecorded category or
user-facing surface. Individual findings close only after implementation,
tests, responsive screenshots, and accessibility verification are attached to
their ids.

That definition is satisfied and reverified as of August 13, 2026.
