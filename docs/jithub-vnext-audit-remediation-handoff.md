# JitHub vNext Audit Remediation Handoff

Status date: August 10, 2026

Branch: `codex/vnext-full-audit-remediation`

Base: `1010c15cc8c41d7af143ffa1ba41e3464933c858` (`origin/main` when the branch was created)

Handoff commit: the commit containing this document

## Purpose

This is the durable recovery record for the full vNext convergence work. It is
written so a new agent can clone or switch to the branch and continue without
the prior conversation. The detailed finding history remains in
`docs/jithub-vnext-full-product-audit.md`; this document records current
architecture, shipped behavior, verification, unresolved work, environment
constraints, and exact restart commands.

Do not infer unfinished work from deleted legacy files. This branch replaces
large portions of the original application with canonical vNext owners. Do not
restore retired pages, view models, converters, fake tabs, or duplicate data
paths merely because they existed on `main`.

## Current Truth

- The source implementation for the recorded full-product audit is present.
- All audit tracker rows were implemented and page-specific tests/probes passed.
- Eight findings from the last data/security/performance review were implemented
  and verified with focused tests plus the complete Debug/Release matrices.
- One fresh combined performance rerun is incomplete because the automation
  route-input bridge timed out on Profile iteration 8. This is the only known
  unfinished verification task, not a known product data or UI failure.
- A fresh pair of independent read-only reviews has not yet run after the final
  eight hardening changes. Do that after repairing/rerunning the combined gate.
- Build, test, and screenshot output was intentionally removed after the system
  drive ran out of space. The branch contains source, tests, lock files, scripts,
  and documentation, but no claim that local binaries or artifact directories
  still exist.

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
  compact rail drawer, profile footer, notifications, and settings entry.
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
- The complete `qps-ploc` catalog is packaged for long-string testing; incomplete
  human translations are not exposed as product languages.
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

### My Issues And Repository Issues

- Shared adaptive list/detail/inspector workspace with native segmented filters,
  feature-rich filter controls, clickable rows, hover/pressed/selected states,
  selectable Markdown, comment write/preview, metadata actions, and close/reopen.
- Cached-first selection, progressive comments, predictive prefetch, keyed
  refresh, and scroll-anchor preservation prevent list flashes and jumps.
- Compact controls occupy existing title/action rows; drawers align opener and
  closer locations and slide within the repository workspace boundary.

### My Pull Requests And Repository Pull Requests

- Uses the Issues foundation with PR conversation, commits, reviews, timeline,
  reviewers, metadata, reactions, edit, close/reopen, comment/reply, and merge.
- Section caches fail independently; selection, drafts, reply drafts, active
  section, and list anchor persist through refresh.
- Pull-request identity projections are stable and no longer bind form state to
  the wrong nested view model.

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

### Notifications, Gists, Search, And Repository Management

- Notifications: virtualized unread/all/participating inbox, search, polling,
  automatic paging, optimistic read/unread, mark-all-read, subscription/mute,
  and internal routing.
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

These results were completed before generated outputs were deleted:

| Target | Result |
| --- | --- |
| `JitHub.WinUI.Tests` Debug | 2,526 / 2,526 passed |
| `JitHub.WinUI.Tests` Release | 2,526 / 2,526 passed |
| `MarkdownRenderer.Tests` Debug | 335 / 335 passed |
| `MarkdownRenderer.Tests` Release | 335 / 335 passed |
| `MarkdownRenderer.PixelTests` Debug | 82 / 82 passed |
| `MarkdownRenderer.PixelTests` Release | 82 / 82 passed |
| `JitHub.Web.Tests` Debug | 17 / 17 passed |
| `JitHub.Web.Tests` Release | 17 / 17 passed |
| `JitHub.WinUI` Debug | warning-free build |
| `JitHub.WinUI` Release | warning-free build; 60 / 60 embedded security gates |
| Automation / PerformanceGate / Web | warning-free Debug and Release builds |
| Dependency policy and audit | passed; no direct/transitive vulnerabilities |

Latest native acceptance results before cleanup:

- `shell-responsive`: passed, rerun clean after an unrelated Windows low-disk
  dialog contaminated the first screenshots.
- `settings-responsive`: passed all five requested widths in light/dark. Genuine
  OS High Contrast was not enabled in that session, so the harness truthfully
  recorded the conditional skip rather than a false pass.
- `profile-responsive`: passed all widths and the edit dialog.
- `issues-responsive-workspace`: passed My Issues and Repository Issues at all
  six requested widths after the coordinated breakpoint fix.
- Screenshots were manually reviewed for clipping, overlap, width shift, active
  navigation, drawer behavior, and stable content.

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

## Open Work

### 1. Repair And Rerun The Combined Performance Gate

The attempted ten-iteration warm run covered
`profile,my_issues,repo_code,repo_commits`.

- My Issues warm-up and all 10 iterations completed.
- Profile completed through iteration 7.
- Profile iteration 8 failed with:

```text
TimeoutException: The performance route input did not commit 'settings'.
```

The failure originates in
`JitHub.WinUI.PerformanceGate/ProductPerformanceRouteProbe.cs`:

- `NavigateRouteAndWait` near line 277.
- `CommitTextValue` near line 1155.

The corresponding tiny automation bridge is in:

- `JitHub.WinUI/Views/Pages/ShellPage.xaml` near the performance bridge.
- `JitHub.WinUI/Views/Pages/ShellPage.xaml.cs`,
  `ProductPerformanceNavigateButton_Click` near line 377.

`CommitTextValue` currently sets the bridge TextBox and waits two seconds. The
next agent should reproduce Profile in isolation, then harden the bridge by
reacquiring the UIA element and retrying a bounded commit/acknowledgement. Keep
the bridge test-only, deterministic, and non-blocking. Do not loosen product
performance budgets or turn a failed route acknowledgement into a pass.

No partial report was retained after storage cleanup.

### 2. Run Two Fresh Independent Read-Only Reviews

After the performance rerun passes, request two independent reviews:

1. Data/security/performance: account partitioning, cancellation, cache path
   confinement, OAuth redirect/handoff, dependency gate, pagination, prefetch,
   and the performance bridge.
2. WinUI/accessibility/localization/error presentation: responsive pane order,
   keyboard/focus/UIA behavior, High Contrast/pseudo-localization, safe error
   copy, and control-catalog consistency.

The earlier WinUI/accessibility reviewer was clean. The later
data/security/performance reviewer reported eight findings, all now implemented:

1. Pending OAuth token/account partition isolation.
2. Profile mutation lifecycle coordination.
3. Repository-file cache path confinement.
4. Shell Code/Commit prefetch cancellation and draining.
5. Central localized safe error presentation.
6. Progressive Issues/comment loading.
7. Exact production OAuth redirect allowlist.
8. Locked dependency and Release vulnerability policy.

Do not declare a new final closure until both rereviews report no actionable
finding and the combined performance run passes.

## Next-Session Runbook

### 1. Confirm Source State

```powershell
git switch codex/vnext-full-audit-remediation
git status --short --branch
Get-PSDrive C
```

The expected branch should be clean after this handoff commit. Confirm at least
6 GB free before rebuilding all configurations and generating screenshots.

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

### 6. Repair And Run The Performance Gate

Build the app and gate, then run at least the affected warm fixture:

```powershell
pwsh -NoProfile -File eng\Invoke-ProductPerformanceGate.ps1 `
  -AppPath $app `
  -Configuration Debug `
  -Iterations 10 `
  -Fixtures Warm `
  -Routes profile,my_issues,repo_code,repo_commits `
  -OutputPath artifacts\performance\vnext-handoff-rerun.json `
  -ArtifactsPath artifacts\performance\vnext-handoff-rerun `
  -SkipBuild
```

The gate intentionally requires at least 10 iterations. Do not bypass that
validation. After a passing affected-route run, run the full plan or the CI gate
configuration before final release closure.

### 7. Final Review And Audit Update

- Run the two independent reviews described above.
- Fix every actionable finding and rerun focused plus affected full tests.
- Update `docs/jithub-vnext-full-product-audit.md` with fresh evidence.
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
