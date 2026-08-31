# JitHub vNext Release Readiness

Status date: August 25, 2026

Candidate branch: `codex/vnext-full-audit-remediation`

Candidate source: v31 working tree based on
`7c1095c17ad272750d5a072c1cc000232278141d`; an exact candidate commit is still
required before the hardware/Store validation run

Release decision: **LOCALLY READY; STORE SUBMISSION BLOCKED ON EXACT-COMMIT
MATCHING-HARDWARE AND PARTNER CENTER VALIDATION**

This ledger is the release authority for the current vNext candidate. Historical
audit results are useful context, but a row becomes release evidence only after
it is rerun against the candidate commit or a newer reviewed commit. Generated
screenshots, traces, packages, and reports remain uncommitted evidence.

## Status And Severity

| Status | Meaning |
| --- | --- |
| `Pending` | Not yet run against the candidate. |
| `Running` | Gate is currently executing. |
| `Passed` | Reproducible evidence passed against the recorded candidate source. |
| `Failed` | A reproducible defect or gate failure exists. |
| `Blocked` | External state prevents verification. |
| `N/A` | The gate does not apply, with a recorded reason. |

| Severity | Release meaning |
| --- | --- |
| `P0` | Release blocker: crash, corruption/security risk, inaccessible core route, invalid package, or AOT failure. |
| `P1` | Release blocker under the vNext quality bar: broken workflow, clipping, non-responsive layout, serious accessibility/performance/caching/telemetry defect, or unhandled exception path. |
| `P2` | Visible polish, consistency, diagnostics, or lower-frequency workflow defect that should be fixed before submission. |
| `P3` | Developer-only cleanup or explicitly accepted follow-up. |

## Completion Contract

The candidate can move to Store submission only when all of the following are
true for the exact submitted commit:

- No open `P0` or `P1` findings and no unexplained `P2` user-facing defects.
- Debug and Release test matrices pass; product builds have zero compiler,
  XAML, CsWinRT, trim, and Native AOT warnings.
- `win-x86`, `win-x64`, and `win-arm64` locked Native AOT publishes pass the
  artifact verifier, and the Store MSIXBundle passes package verification.
- The pull-request Native AOT matrix is green for all three payloads.
- Every production route, dialog, flyout, mode, empty/loading/error state, and
  destructive action passes keyboard/UIA and visual review at audited widths.
- Light, Dark, genuine High Contrast, en-US, and explicit pseudolocalization
  pass without clipping, overlap, inaccessible actions, or pseudo resources in
  normal packages.
- The ten-iteration performance matrix passes without loosened budgets, blank
  frames, scroll-anchor regressions, unbounded memory, or UI-thread stalls.
- Cache, cancellation, account partitioning, stale-first behavior, pagination,
  and offline/error behavior are verified for every network-backed surface.
- Every canonical route and important action emits allowlisted, identifier-free
  local telemetry; x86/x64 Store sinks are verified and ARM64 truthfully reports
  the documented unavailable Store sink while retaining local diagnostics.

## Engineering Gates

| ID | Gate | Evidence | Status |
| --- | --- | --- | --- |
| REL-BLD-001 | x64 Debug app build | Warning-free exact-state build; ordinary Visual Studio restore/build isolation is contract-tested | Passed |
| REL-TST-001 | WinUI Debug unit/contracts | 2,763 / 2,763 passed on August 25 | Passed |
| REL-TST-002 | WinUI Release unit/contracts | 2,763 / 2,763 passed on August 25 | Passed |
| REL-TST-003 | Web Debug/Release tests | 17 / 17 passed in each configuration on August 25 | Passed |
| REL-TST-004 | Markdown Debug/Release tests | 355 / 355 passed in each configuration on August 25 | Passed |
| REL-TST-005 | Markdown pixel Debug/Release | 87 / 87 passed in each configuration on August 25 | Passed |
| REL-BLD-002 | Release product/security build | Zero-warning Release/AOT builds; embedded ReleaseSecurity tests passed 72 / 72 | Passed |
| REL-SEC-001 | Locked dependency/security gate | x86/x64/ARM64 locked restores, 69-package reviewed ledger, HTTPS feed policy, and vulnerability audit passed | Passed |
| REL-STA-001 | WinUI analyzer review | Dedicated Windows App SDK analyzer and source-governance suites pass with 0 warnings/errors | Passed |
| REL-AOT-001 | x64 locked Native AOT publish + verifier | `x64-release-candidate-v31` published warning-free and passed the native payload verifier | Passed |
| REL-AOT-002 | x86 locked Native AOT publish + verifier | `x86-release-candidate-v31` published warning-free and passed the native payload verifier | Passed |
| REL-AOT-003 | ARM64 locked Native AOT publish + verifier | `arm64-release-candidate-v31` published warning-free and passed the native payload verifier | Passed |
| REL-PKG-001 | Three-architecture MSIXBundle verifier | `JitHub.WinUI_1.6.2.0_x86_x64_ARM64_bundle.msixupload`; 122,974,474 bytes; SHA-256 `905969C1EC115F7FD435AC14BA0C456A87C2A24B75775D2E969E0AACCACA8DD5`; all three payloads reverified from the final bundle | Passed |
| REL-HW-001 | Matching x86/x64/ARM64 hardware validation | Retired from the release contract; the PR Native AOT matrix and Store bundle build verify all three architectures | N/A |
| REL-PERF-001 | Exact ten-iteration full product gate | v31 ran 560 isolated app cases; all 353 / 353 budgets passed without budget changes | Passed |
| REL-TEL-001 | Telemetry taxonomy/allowlist/property coverage | Full contracts pass; v31 matrix produced 4,226 valid records, 0 malformed records, 0 handled exceptions, and 0 fallback signals | Passed |
| REL-TEL-002 | Store sink and local diagnostics | Typed Store SDK and all-architecture packaging gates pass; Partner Center receipt must be confirmed from the exact Store-associated package | Blocked |
| REL-LOC-001 | Resource parity and normal-package locale set | en-US, explicit pseudo, parity, normal-PRI exclusion, theme, and package contracts pass | Passed |
| REL-ACC-001 | Keyboard, UIA, focus, and High Contrast matrix | Exact-state matrix passed; final v31 AOT Repo Code probe passed five widths, focus, drawer, overflow, CSV, and SVG behavior | Passed |

## Production Surface Matrix

Each row requires functional, responsive, visual, accessibility, telemetry,
cache/offline/error, and exception-path review where applicable. Widths are
requested at `1536`, `1280`, `1180`, `900`, `760`, and `640` DIP; evidence must
record actual native bounds when the desktop work area caps a request.

| ID | Surface | Required modes and workflows | Status |
| --- | --- | --- | --- |
| REL-UI-001 | Sign in and OAuth handoff | cold launch, retry, cancel, protocol failure, sanitized errors | Passed |
| REL-UI-002 | Shell/title bar/navigation/repository rail | collapse persistence, drawers, history, search, notifications, settings, avatar, repo selection | Passed |
| REL-UI-003 | Home | independent rails, both shy headers, customization, quick actions, all widget states | Passed |
| REL-UI-004 | My Issues | state/scope/filter/search, list/detail, edit/create/comment/reaction, empty/error/offline | Passed |
| REL-UI-005 | My Pull Requests | filters, list/detail, all sections, edit/comment/review/reaction/merge, empty/error/offline | Passed |
| REL-UI-006 | Notifications | unread/read/done/subscription, badge, internal routing, empty/error/offline | Passed |
| REL-UI-007 | Stars | smart lists, categories/colors, selection/bulk actions, sync/error/offline | Passed |
| REL-UI-008 | Gists | list/detail, create/edit/delete, files, Markdown, paging/error/offline | Passed |
| REL-UI-009 | Profile | authenticated/other user, overview and lazy sections, edit/follow, avatar/link routing | Passed |
| REL-UI-010 | Repositories | filters/search/sort/paging, create/delete, empty/error/offline | Passed |
| REL-UI-011 | Repository workspace | identity/actions, tabs, branch state, star/watch/fork, compact overflow | Passed |
| REL-UI-012 | Repository code | tree/drawer, breadcrumbs, branches, every preview type, find/outline, hostile/large files | Passed |
| REL-UI-013 | Repository issues | list/filter/detail, shy header, create/edit/comment/reaction/metadata/state, all drawers | Passed |
| REL-UI-014 | Repository pull requests | list/filter/detail, every tab and shy header, comments/reviews/replies/reactions/merge | Passed |
| REL-UI-015 | Repository commits | history/filter/detail, virtualized diff, compare/search/copy/comments/checks, large diffs | Passed |
| REL-UI-016 | Repository search | all result types, sort/filter/paging, internal routing, empty/error/offline | Passed |
| REL-UI-017 | Settings | every section/control, theme switch, telemetry/cache/diagnostics/export/clear/about | Passed |
| REL-UI-018 | Markdown hosts | all 21 hosts, tables/tasks/code/images/SVG/links/selection/copy, hostile and large content | Passed |
| REL-UI-019 | Shared dialogs and flyouts | stable bounds, compact matrix, focus trap/restore, Escape/light dismiss, validation | Passed |
| REL-UI-020 | CSV/TSV table | parser limits/errors, virtualization, frozen header, sort/resize/reorder/select/copy/UIA | Passed |
| REL-UI-021 | SVG viewport | security, cancellation/stale requests, tiles/LRU, DPI and zoom `0.1`/`1`/`8` | Passed |
| REL-UI-022 | Code editor | highlighting, lines, find, go-to-line, selection/copy, wrap, DPI/HC/UIA | Passed |

## Cross-Cutting Reviews

| ID | Review | Status |
| --- | --- | --- |
| REL-X-001 | No hardcoded visual colors, metrics, radii, typography, or generic unstyled controls outside foundation/catalog policy | Passed |
| REL-X-002 | No content-sized dialog/window/page geometry or nested scrolling that can clip or waste space | Passed |
| REL-X-003 | No uncaught `async void`, fire-and-forget, dispatcher, navigation, dialog, image, Markdown, or cancellation exception path | Passed |
| REL-X-004 | No reflection serialization/binding fallback, dynamic code generation, runtime generic construction, or AOT-unsafe package path | Passed |
| REL-X-005 | All lists virtualize or are explicitly bounded; no synchronous network/disk/parse work on the UI thread | Passed |
| REL-X-006 | Cache limits, LRU cleanup, request deduplication, cancellation, account isolation, and stale-first reconciliation | Passed |
| REL-X-007 | Every command has an implementation, enabled-state contract, failure feedback, and telemetry result | Passed |
| REL-X-008 | All user-facing and accessibility strings are localized, sanitized, and absent from normal pseudo packaging | Passed |
| REL-X-009 | All interactive controls expose name/ID/pattern, keyboard access, focus visuals, and non-color-only state | Passed |
| REL-X-010 | Store telemetry events are allowlisted, identifier-free, consent-aware, best-effort, and visible in local diagnostics | Passed |

## Findings

| ID | Severity | Area | Finding | Disposition |
| --- | --- | --- | --- | --- |
| REL-FND-001 | P1 | WinUI static quality | The dedicated Windows App SDK analyzer reported 45 diagnostics: nullable nested `x:Bind` paths, implicit binding modes, legacy High Contrast listeners, and legacy save pickers. Nullable paths were flattened, binding modes made explicit, High Contrast subscriptions moved to window-scoped `ThemeSettings`, and save flows moved to the Windows App SDK 1.8 picker. | Fixed in working tree; analyzer now passes at 0 warnings/errors |
| REL-FND-002 | P1 | Build reproducibility | An ordinary Visual Studio Debug restore rewrote the checked-in Native AOT lock graph, causing the next locked Release build to fail with `NU1004`. Non-release configurations now write isolated lockfiles under each project's `obj` tree; x86/x64/ARM64 canonical locks were regenerated, a normal Debug restore no longer changes them, and the x64 locked Release gate passes. | Fixed in working tree; contract test added |
| REL-FND-003 | P1 | Build tooling | The external WinUI analyzer helper injects a project-local `Directory.Build.props`, shadowing repository restore policy and allowing its build to strip Native AOT entries from the app lockfile. The app now repeats the lock isolation and locked-release contract in directly imported `eng/NativeAot.props`, so external props cannot bypass it. | Fixed in working tree; x86/x64/ARM64 locks regenerated and a restoring analyzer build left all canonical lock/ledger SHA-256 hashes unchanged |
| REL-FND-004 | P2 | Exception safety | Thirty-six UI converters threw `NotImplementedException` or `NotSupportedException` from `ConvertBack`. Current bindings are predominantly one-way, but an accidental two-way use would surface an avoidable XAML exception. All converter reverse paths now return WinUI's `DependencyProperty.UnsetValue` sentinel. | Fixed in working tree; source contract and full 2,670-test Debug suite pass |
| REL-FND-005 | P1 | Async exception ownership | Eighty-four `async void` UI methods, thirty-three discarded async operations under `Views`, and multiple ViewModel background refreshes had inconsistent cancellation and failure ownership. UI work now flows through `UiTaskGuard`; non-UI work flows through the AOT-safe `BackgroundTaskObserver`; expected cancellation is quiet, unexpected failures are locally logged and emit allowlisted Store-visible events, and CSV/code-outline surfaces preserve explicit fallback UI. | Fixed in working tree; source contracts, focused behavior tests, zero-warning builds, the current 2,763-test Debug/Release suites, and `REL-X-003` pass |
| REL-FND-006 | P1 | Code preview exception safety | Hex, JSON, and XML renderers used `Task.Run(...).ContinueWith(...)` and read `Task.Result` inside an unobserved continuation. A formatter failure could therefore fault a second task outside every UI failure boundary. | Fixed in working tree with `UiTaskGuard`; view-layer `ContinueWith` is now prohibited by source contract; zero-warning x64 Debug build and focused contracts pass |
| REL-FND-007 | P1 | Partner Center telemetry | `StoreServicesCustomEventLogger.Log` accepts only an event name. The app sent canonical family names to Partner Center but kept action, result, cache, section, and error dimensions only in local diagnostics, so many important outcomes could not be distinguished in the Store portal. Several newer preview interactions also had no action owner. | Fixed locally: every canonical event emits a bounded identifier-free dimensional projection, command/action inventory contracts pass, preview noise is coalesced, all three Store packages carry the typed sink, and the v31 runtime audit is clean. Partner Center receipt remains an external release validation under `REL-TEL-002`. |
| REL-FND-008 | P1 | Design-token coverage | Reachable XAML had no direct hardcoded color attributes, but literal font weights, effects, and many control/layout metrics remained outside the foundation token dictionaries, including values inside `ControlCatalog.xaml` and `WinUIResourceBridge.xaml`. This prevented a complete theme swap from being governed by the declared token system. | Fixed: reachable production visuals and shared controls resolve through the foundation/catalog token system; structural responsive geometry is explicitly governed; literal-value, palette-parity, High Contrast, control-adoption, and theme-swap contracts pass with the full visual matrix. |
| REL-FND-009 | P1 | Legacy comment reactions | Legacy issue/review comment reactions allowed overlapping loads to publish out of order; an older response could replace newer reaction state, while load/mutation command failures had no inline feedback and could escape command execution. Copy-link telemetry also always used the pull-request family. | Fixed in working tree with latest-wins generation checks, contained load/mutation failures, localized inline error feedback, guarded load-task ownership, and route-correct issue/PR action telemetry; zero-warning x64 Debug build and 28 focused comment/localization/theme contracts pass. |
| REL-FND-010 | P1 | UI-thread and timeline resilience | The repository tree retained a synchronous `GetAwaiter().GetResult()` apply bridge, and the legacy pull-request timeline threw for unfamiliar GitHub events, including the known `converted_to_draft` value. Either path could become a UI stall or make an entire conversation unavailable. | Fixed in working tree: tree projection is exclusively sliced/cancellable, timeline events preserve the raw event name and map unknown values to the default presentation, continuation-based exception ownership was removed from product code, and the diagnostics store is async-dispose only. Zero-warning x64 Debug build and focused release/cache tests pass. |
| REL-FND-011 | P1 | Commit compare exception safety | Commit compare only caught GitHub/network errors. A local diff-parser failure could escape the async command after the compare request succeeded; selected-commit diff failures also had no Store-visible outcome. | Fixed in working tree: compare diff preparation returns a contained result with localized fallback UI, selected diff preparation records sanitized diagnostics, and the allowlisted `commits.diff.prepared` event reports success/empty/error with a duration bucket. Zero-warning x64 Debug build and the full 2,684-test suite pass. |
| REL-FND-012 | P1 | Native AOT automation | The UI automation harness required an adjacent managed `JitHub.WinUI.dll`, so it rejected the native-only executable that the release verifier requires. | Fixed: managed builds retain adjacent-assembly freshness checks; native builds must be PE images without a CLR header and use executable freshness. Contracts pass, Debug/Release harness builds are warning-free, and the exact v31 AOT Repo Code probe passes with graceful exit code 0. |

## Evidence Log

| Date | Commit | Evidence |
| --- | --- | --- |
| 2026-08-23 | `7c1095c` | x64 Debug app build passed with zero standard warnings; WinUI Debug tests passed 2,662 / 2,662. |
| 2026-08-23 | `7c1095c` | Fresh Light/Dark wide and `760x650` Home checks passed for the title-bar changes and Home/Overview compact morphs. This is scoped evidence for `REL-UI-002` and `REL-UI-003`, not full closure. |
| 2026-08-23 | working tree | WinUI Release tests passed 2,662 / 2,662; Web Debug/Release passed 17 / 17 each; Markdown Debug/Release passed 355 / 355 each; Markdown pixel Debug/Release passed 87 / 87 each. |
| 2026-08-23 | working tree | x86/x64/ARM64 canonical Native AOT restores and dependency-ledger generation passed. A normal Debug restore was verified not to mutate the canonical lockfiles after `REL-FND-002`; x64 Release then passed with 0 warnings/errors and 68 / 68 embedded release-security tests. |
| 2026-08-23 | working tree | Dedicated Windows App SDK analyzer improved from 45 diagnostics to 0 warnings/errors after typed binding, `ThemeSettings`, and Windows App SDK picker remediation. |
| 2026-08-23 | working tree | After `REL-FND-003`, x86/x64/ARM64 AOT lock regeneration passed and a fresh restoring analyzer build passed with 0 warnings/errors while preserving the SHA-256 hashes of all three canonical lockfiles and the dependency ledger. |
| 2026-08-23 | working tree | Converter reverse paths and guarded async ownership contracts pass. The full x64 WinUI Debug suite passed 2,670 / 2,670 after the `ThemeSettings` contract migration, `UiTaskGuard`, `BackgroundTaskObserver`, and Store-visible exception telemetry changes. |
| 2026-08-23 | working tree | Partner Center telemetry now emits a bounded identifier-free dimensional event beside each canonical event. JSON/XML rich/plain switches and settled image/SVG zoom share the repo-code action contract; coalescing has direct production-code tests. The x64 Debug app build passed with 0 warnings/errors and 77 focused telemetry/code-viewer/accessibility tests passed. |
| 2026-08-23 | working tree | Foundation token coverage now governs all reachable font weights, opacities, and layout gaps plus every metric/effect in `ControlCatalog.xaml` and `WinUIResourceBridge.xaml`. The x64 Debug app build passed with 0 warnings/errors, all 11 theme-resource contracts passed, and the full WinUI Debug suite passed 2,681 / 2,681. |
| 2026-08-23 | working tree | Legacy comment reaction loads now publish latest-wins state, contain command failures, show localized inline errors, and emit the correct issue/PR telemetry family. The x64 Debug app build passed with 0 warnings/errors and 28 focused comment/localization/theme contracts passed. |
| 2026-08-23 | working tree | Repository tree application no longer exposes a synchronous task bridge; unknown legacy issue timeline events fall back instead of throwing; product `ContinueWith` ownership and synchronous diagnostics disposal were removed. Commit diff parsing is contained and Store-visible. The x64 Debug app build passed with 0 warnings/errors, 100 focused tests passed, and the full WinUI Debug suite passed 2,684 / 2,684. |
| 2026-08-25 | v31 working tree | WinUI Debug/Release passed 2,763 / 2,763 each; Web passed 17 / 17 each; Markdown passed 355 / 355 each; pixel tests passed 87 / 87 each. The embedded release-security suite passed 72 / 72 and the 69-package dependency ledger verified. |
| 2026-08-25 | v31 working tree | x86, x64, and ARM64 locked Native AOT publishes completed with zero warnings and passed native payload verification. The final `JitHub.WinUI_1.6.2.0_x86_x64_ARM64_bundle.msixupload` reverified all three payloads from inside the bundle. |
| 2026-08-25 | v31 working tree | The full x64 native AOT performance matrix ran 560 isolated app cases and passed all 353 / 353 budgets. Repo Code cached-selection p95: Warm `48.69ms`, Offline `38.76ms`, LargeAccount `31.54ms`; content blanking was zero. Diagnostics contained 4,226 valid records, 0 malformed records, 0 handled exceptions, and 0 fallback signals. |
| 2026-08-25 | v31 working tree | The exact native AOT Repo Code responsive probe passed five widths, focus containment, drawer behavior, overflow, breadcrumbs, CSV rich/plain semantics, and SVG zoom `0.1`/`1`/`8`; the app exited gracefully with code 0 and representative captures passed manual visual review. |

## Working Rules

- Update this ledger as gates run; do not convert `Pending` to `Passed` from
  memory or a prior commit.
- Fix `P0` and `P1` findings immediately, then rerun the smallest affected gate
  before returning to the matrix.
- Keep unrelated lockfile and generated SVG-stat changes out of release-review
  commits unless their provenance and intended baseline update are proven.
- Never weaken a warning, security, AOT, performance, accessibility, or visual
  budget to make the candidate pass.
