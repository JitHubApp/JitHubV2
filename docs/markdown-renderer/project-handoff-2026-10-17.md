# MarkdownRenderer project handoff and correctness status

Checkpoint date: October 17, 2026, America/Los_Angeles. Some receipts are dated
October 18 in UTC; those are the same local working session, not later work.

MarkdownRenderer has an implemented native document engine, viewer and optional
feature packs, but it remains a preview. **None of the three correctness phases
is complete.** The highest-priority blocker is the intermittent native crash
tracked as P1-032. Passing component tests and a focused resize replay do not
close that defect or establish release readiness.

This is the current handoff. The [correctness plan](correctness-plan.md) contains
the frozen cases, individual defect records and detailed chronology. The
[October 12 checkpoint](implementation-checkpoint-2026-10-12.md) and
[progressive performance plan](progressive-performance-plan.md) preserve the
earlier performance program; their unfinished performance and browser-parity
targets are not acceptance criteria for the active correctness goal.

## Goal and boundaries

Make the existing MarkdownRenderer reliably correct within its documented
feature set. Supported Markdown must parse correctly, render the intended
content, and support accurate text selection and copying. Loading, navigation,
cancellation, replacement and disposal must leave no crashes, hangs, stale
content or broken interactions.

The feature set is frozen. Browser appearance, pixel parity, browser-relative
performance, additional syntax and a Rust rewrite are separate future projects.
Native presentation is acceptable when content and meaning are preserved.
Existing resource limits, image policies, accessibility semantics, AOT-friendly
registration and owned/borrowed lifetime contracts still apply. Removing a
claimed capability or substituting a fallback cannot hide a supported-content
defect.

Complete means that every required case passes and no known functional defect
remains in the phase's agreed scope. It does not mean proving that no unknown
bug can ever exist. Test counts, successful builds and historical results are
evidence at their stated scope, not substitutes for phase acceptance.

## Phases and current progress

| Phase | Required outcome | Current state |
| --- | --- | --- |
| 1 Core document correctness | Dependable CommonMark/GFM reader; supported HTML/image flow; exact source/selection/copy; mouse, keyboard and touch; Unicode/RTL; both viewport modes; resize, navigation and lifecycle; optional-feature smoke. All 15 frozen case groups and known defects must pass, including actual 100%, 150% and 200% scaling. | Open. There are 33 tracked Phase 1 defect entries, many with code repairs and regression evidence. P1-032 remains critical and open. Current full live interaction, cancellation/lifecycle and high-DPI qualification are incomplete. |
| 2 Complete existing feature correctness | Validate the frozen 32-case matrix for HTML, Math, Mermaid, SVG/raster images, highlighting, configuration, accessibility, hosted controls, selection/copy, failures and lifecycle. | Open. Substantial component repairs exist, including 17 tracked feature-defect entries. Required current-candidate actual-view and configuration evidence is not consolidated into a passing matrix. |
| 3 Real world correctness audit | Evaluate all 500 frozen READMEs for content completeness, supported features, usable layout, selection/copy and exception-free navigation; resolve every renderer defect found. | Open. All 500 source bodies are acquired and verified; previous headless runs parse all 500. The complete current-candidate page and interaction audit has not run. |

Close Phase 1 before expanding exhaustive feature certification. Then close
Phase 2, followed by Phase 3. Add fixtures only to cover a genuine defect; do
not continuously enlarge an otherwise completed phase.

## Implemented capabilities and architecture

| Area | Implementation and ownership |
| --- | --- |
| Parsing and documents | Markdig-backed CommonMark/GFM, explicit GitHub README profiles, immutable engines/documents, bounded admission/cache, diagnostics, cancellation and half-open UTF-16 source spans. |
| Native views | `MarkdownScrollView` owns its viewport. `MarkdownDocumentView` observes ancestor-owned scrolling. Win2D/DirectWrite supplies native text measurement, shaping, paint and hit testing; WinUI supplies interaction and hosted controls. |
| Markdown features | Headings, paragraphs, lists, native task checkboxes, quotations, rules, links, tables, fenced/inline code, optional alerts, footnotes, emoji, attributes and Markdown Extra. |
| Safe HTML | Explicit bounded native subset, inline/cross-block formatting, supported tables/images, source mapping and documented inert/literal/omitted fallbacks. It is not a browser DOM or unrestricted CSS engine. |
| Math | CSharpMath-based inline/block vector scenes, bounded TeX processing, diagnostics, accessible source fallback and atomic selection. Documented dollar delimiters are supported; backslash delimiters remain literal. |
| Mermaid | Selected-RID Merman native engine, validated MMIR scenes, native labels/links, semantic colors/accessibility and exact code fallback. Reviewed source patches and their provenance are versioned. |
| SVG and raster images | Explicit resvg 0.48.1 provider using isolated Rust workers and shared-memory transactions; bounded WebP decoding uses the same worker infrastructure. The host controls external image admission. Nested SVG network/file access remains prohibited. |
| Resource preparation | Asynchronous preparation, bounded caches/admission, opt-in shared performance sessions, prefetch, display-sized preparation, stale-generation rejection and lease-based retirement. Implemented optimization is retained, without making browser-speed claims. |
| Interaction and clipboard | Mouse/keyboard/pen/touch selection, word selection, native menus and handles, rendered text plus `CF_HTML` for normal Copy, exact selected source for Copy Markdown, atomic image/vector selection. |
| Accessibility and customization | Semantic text/UIA ranges, source relationships, hosted-control semantics, Fluent/theme resources, semantic selection brushes, localization hooks and explicit extension registration. |
| Packaging and integration | Lean Core/WinUI packages plus optional feature packs, static/AOT-friendly registration, pinned native sources/locks, provenance and license inventories. JitHub owns shared services; views borrow them and retire only their document-specific work. |

Painting, hit testing, selection and UI Automation must agree on committed
geometry. Layout/read/paint leases and snapshot generations prevent stale or
retiring geometry from being published or disposed while in use. Async document,
image and scene results require generation/identity checks before publication.

`SvgRenderer`, `RasterDecoder` and `PerformanceSession` are borrowed services.
The host disposes the shared instances. Controls cancel and dispose their own
document handles, not the shared provider. Optional providers are registered
explicitly; there is no reflection-based discovery or in-process SVG fallback.

The .NET document/WinUI core remains in place. Rust already implements native
image processing and Mermaid dependencies; that is not a completed Rust rewrite
of the whole renderer. Such a migration is outside these phases.

See [architecture](architecture.md), [rendering pipeline](rendering-pipeline.md),
[public API](public-api.md), [supported syntax](supported-markdown.md),
[selection and clipboard](selection-and-clipboard.md),
[accessibility](accessibility.md) and [package composition](packaging-and-distribution.md)
for the full consumer and implementation contracts.

## Repairs and investigations preserved

The detailed plan records each failure before repair, its regression and its
remaining qualification. The principal work groups are:

- Text/layout: superscript/subscript phrase baselines, ancestor viewport
  coordinates, absent geometry handling, image/table containment, HTML table
  captions, HTML whitespace/entities, disclosure nesting/closing spans and
  Unicode placeholder preservation.
- Source/copy: exact summary-image and long code/HTML-pre projections, preserved
  authored terminal blank lines, rich code whitespace and linked styles,
  structured table copy, grapheme-safe code chunks and correct long NoWrap rows.
- Selection: keycap/emoji/combining boundaries, touch contact ownership and tap
  suppression, deferred terminal cancellation, replacement/relayout provenance,
  native cancellation flags, handle restoration, final-object endpoints and
  visible-artwork selection boundaries.
- Embedded content: readable content-bounded Math/Mermaid selection rather than
  opaque viewport-wide paint, supported source-copy boundaries, SVG forbidden
  content admission, bounded raster first-canvas behavior and x86 worker startup.
- Mermaid: source-owned entity/placeholder expansion across diagram families,
  literal-versus-formatting classification, label whitespace, repeated semantic
  owners, relationship captions, hidden menus and root accessible descriptions.
- Test infrastructure: exact guest identity/payload checks, isolated output
  routing, UTF-8 and PowerShell-version compatibility, correct empty-array
  handling, independent expected source, touch target geometry and failure
  accounting. A harness repair is recorded as a harness repair, not a product fix.

These groups summarize implementation work; they do not declare every related
live interaction or architecture combination passed.

## Verified results and their limits

| Result | Evidence and qualification |
| --- | --- |
| Latest renderer regression suites | R6 passes 3,195/3,195 in both Debug and Release, zero failed/skipped. All 3,192 previous R5 test identities are retained, with only three new lifetime source-order contracts added. |
| Lifetime source contracts | The three new checks fail before production edits; the post-change focused run passes 20/20. They check source ordering, not actual reentrant XAML assignment or native heap behavior. |
| Sample build | R6 Release/x64 JIT build has zero warnings/errors and contains the same renderer assembly as the tested Release output. This is not a current whole-app NativeAOT publication. |
| Latest focused Sandbox replay | 2 PASS, 0 FAIL, 60 UNVERIFIED; expected exit code 2 and `complete=false`. Exact `hold` selection and both UIA 44-by-44-DIP handles survive narrow/restored width at actual 100% DPI. All three screenshots are individually reviewed. Only the owned viewport and that sequence are exercised. |
| Earlier broad sample run | R3 ends 47 PASS, 7 FAIL, 8 UNVERIFIED. It covers all 22 sample smoke pages and substantial mouse/keyboard/Unicode/clipboard behavior, but suffers the native crash and other failures. Later component fixes do not retroactively pass that run. |
| Original-option crash replay | Retained R3 payload ends 38 PASS, 5 FAIL, 19 UNVERIFIED and reproduces P1-032 before C14 resize. Its last successful action is source replacement. Detection timing does not prove which earlier operation corrupted ownership. |
| Official syntax conformance | Earlier pinned CommonMark 0.31.2/GFM 0.29 coverage passes 1,322 examples. Preserve its revision identity and rerun required current-source gates rather than assuming historical success certifies every subsequent change. |
| Mermaid component milestone | Final recorded x64 public suites pass 259/259 in both configurations; product-native tests pass 54/54 and the source gate 117/117. All three RID binaries are built/PE-verified; x86/ARM64 Mermaid runtime execution and live matrix coverage are separate. |
| Isolated image provider milestone | Prior final provider suite passes 725/725 and recorded x86/x64 AOT smoke runs pass startup/render/lifecycle checks. ARM64 publication is not ARM64 execution, and provider smoke is not current whole-app release qualification. |

The [portable evidence index](evidence/2026-10-17/README.md) preserves selected
receipts and their provenance. Raw result files, screenshots and dumps remain
separate from these summaries. Test counts cannot be turned into an overall
completion percentage: component success and complete live acceptance measure
different things.

## Critical open crash and review findings

P1-032 is reproduced native heap corruption around selected-document
relayout/replacement. The retained diagnostic identifies double-free detection
during Win2D `CanvasImageSource` destruction from XAML image-peer cleanup. It
does not identify the first invalid release, establish an upstream cause or
prove a production fix.

At the user's request, the current investigation switched to managed source
review, regression tests and normal Sandbox interactions. The prepared native
debugger batch was never started; its unused runner hook/helper files were
removed. Existing diagnostic history is preserved, not retried or disguised.

The R6 candidate closes drawing sessions before committing geometry or changing
handles, roots image-source owners across XAML attachment/detachment, and checks
source/request/snapshot versions before publishing. Its focused no-crash replay
does not reproduce the full preceding state history. P1-032 remains open.

Independent review also identifies follow-ups:

1. A failing XAML source detach can interrupt one-shot unload cleanup.
2. Direct release before the guarded device-recovery handler can throw and
   prevent recovery from being scheduled.
3. Caught device loss may need notification on the actual failed Win2D device,
   rather than rebuilding against the same canvas device.
4. Source-order checks do not execute both possible reentrant setter mutation
   orderings. The restored observer also has null handle entries; its values are
   not substituted for the separate current UIA/screenshot evidence.

These are review findings and test gaps, not demonstrated causes of P1-032.
The precise high-DPI handle issue is also open: at actual 150%, one earlier UIA
handle is 65 by 66 physical pixels although the intended size is 44 by 44 DIPs.
Blind enlargement is not an accepted diagnosis. Pair actual layout, transforms,
clipping and UIA bounds at the same scale before changing sizing.

## Top 500 audit status

The frozen September 28 manifest identifies exactly 500 repositories and their
commit, README path, blob identity and size. Its SHA-256 is
`615c56723fb16d17744af2549615a4de32b1fc299a1759727cc86b70edacac72`.
A byte-preserved [manifest](evidence/2026-10-17/top-repositories.json) is included
so a fresh clone can retain the membership without querying a new popularity
ranking.

| Stage | Completed work | Remaining distinction |
| --- | --- | --- |
| Raw Markdown acquisition | 500/500 verified bodies, 18,023,117 bytes, checked against frozen size/Git blob identity. The Next.js symlink is resolved with same-commit tree proof, not by changing the manifest. | Source bodies and assets are not all included in this Git checkpoint. They must be separately retained or reacquired from the pinned identities. |
| Headless engine | Prior matching engine runs parse 500/500 with no parse/source invariant/disposal exceptions and produce 19 Mermaid scenes. | Rank 340 retains two classified unsupported TeX diagnostics. The runner keeps its nonzero diagnostic exit. No layout, image load, selection, clipboard or navigation is tested. This is not a new R6-bound run. |
| Existing offline input preflight | Five ranks have complete compatible schema-2 page/assets inputs: 1, 59, 60, 203, 318. The other 495 are missing required input material, with zero integrity failures in the preflight. | The current automation still requires the earlier capture format. Decoupling native correctness inputs from unnecessary browser-capture provenance is an open methodology improvement, not an implemented change. |
| Historical rendering | Earlier 500-document rendering audits exist; the plan records one ordinary-web result of 378 pass/122 fail and a later same-byte ten-case rerun of 9 pass/1 fail. | These use different revisions/criteria and cannot qualify the new frozen current-candidate interaction audit. Old pixel/performance failures are not automatically correctness failures. |
| Current complete page audit | No document has completed every required current-candidate content/layout/resource/selection/copy/navigation check under the new rubric. | All 500 still need full acceptance; this does not mean that no READMEs have ever been rendered or tested. |

Upstream unavailability, unsupported syntax, renderer defects, missing test
inputs and harness failures are distinct outcomes. Record the actual source
occurrence, resolved URI and fetch/decode result before modifying image routing.
Do not turn a missing audit input or an upstream image failure into a renderer
pass, and do not relabel an unsupported formula as successfully rendered math.

## Testing methodology

Use a regression that fails before the repair and passes afterward. Preserve
the failing result and the exact source/binary identity. Retain old test
identities and assertions; compare old/new test sets when counts change.

Geometry expectations must be independently calculated or measured. Painting,
hit testing and UIA agreeing with one another is insufficient if all consume
the same wrong coordinates. Exercise source spans in UTF-16, authored CR/LF
behavior, Unicode graphemes, RTL, partial selections and final embedded objects.
Check plain text, rich HTML and Markdown clipboard representations separately.

Run UI validation in Windows Sandbox through WinApp CLI 0.7+. Keep the guest
epoch, process, HWND and host/guest payload identity together. Inspect every
captured PNG in addition to UIA assertions. A stopped guest or dead process
invalidates its old identifiers; never fall back silently to the host desktop.
Use structured PASS/FAIL/UNVERIFIED outcomes and preserve failed runs. Follow
the WinUI workflow's bounded repair/rerun rule rather than silently looping.

Guest zoom is not actual Windows DPI. Actual 100%, 150% and 200%, both viewport
modes and required theme/High Contrast states need their own evidence. Prior
physical-setting approvals are not a reason to alter a new machine silently.
Host interaction/display changes and another native-debugging campaign are not
part of this documentation checkpoint. Scheduled continuation remains canceled.

Builds execute on the host; approved UI runs execute in the guest. Release JIT,
NativeAOT publish, architecture cross-build, PE inspection and physical runtime
execution are different claims. Keep trim/AOT warnings and API baselines
enabled; do not infer current full-app certification from console smoke tests.

## Challenges and execution lessons

The viewer is a custom document engine rather than a thin Markdown-to-native
text adapter. Parsing, source maps, lazy layout, graphics resources, selection,
accessibility and async publication have coupled correctness boundaries.
Native graphics corruption can be detected long after the corrupting action,
making a short successful replay inconclusive.

Scope also expanded into browser fidelity, performance research and elaborate
measurement infrastructure. The correctness-only reset removes those goals from
the active acceptance criteria. Some work still carries old capture dependencies;
those should be examined before investing in another large harness redesign.

Execution spread across optional-feature repairs, tooling and local experiments
without closing a dependable core milestone. Passing micro/component tests and
more receipts were too often easier to report than a completed actual-app gate.
Future progress reports should lead with closed acceptance cases, remaining
product defects and whether the tested changes are actually committed/pushed.

Sandbox/compiler/DPI limitations and harness mistakes consumed additional
cycles. Treat them as tooling problems, not excuses to weaken product oracles.
Similarly, the product's cybersecurity-content notice interrupted one diagnostic
approach, but its exact trigger is unknown; no claim about an internal classifier
or workaround is made. The chosen alternative is ordinary application-level
source review and UI testing.

## Remaining work in priority order

1. Resolve P1-032 and the related detach/recovery exception-safety findings.
   Reproduce the full relevant state sequence on the current candidate; do not
   close the crash from another isolated resize pass.
2. Validate current handle dragging, final-image selection/source copy, actual
   cancellation and capture-loss/unload/disposal behavior in both viewport modes.
3. Finish actual 150%/200% DPI and required contrast checks. Resolve measured
   geometry discrepancies rather than changing dimensions speculatively.
4. Run the complete frozen Phase 1 sample/interaction and feature-smoke gates
   against one matching candidate, record outcomes and close Phase 1.
5. Qualify Phase 2's existing 32 cases without adding features or changing the
   documented support envelope to conceal failures.
6. Materialize the remaining frozen audit inputs, address the old capture-format
   coupling without discarding correctness checks, and run every Phase 3 page
   and interaction case. Fix renderer findings and classify external failures.

Do not substitute a Rust rewrite, a new feature campaign, another benchmark
system or repeated scheduled wakeups for these completion milestones.

## Repository state and continuation

The implementation is preserved in backup commit
`c013b9488ef5180fd87729cc512ffd15f3e665f9` on
[`codex/markdown-renderer-1-0-finalize`](https://github.com/JitHubApp/JitHubV2/tree/codex/markdown-renderer-1-0-finalize).
It was already on origin when this handoff was prepared. This documentation
checkpoint adds no renderer behavior or new live-test result. PR
[#99](https://github.com/JitHubApp/JitHubV2/pull/99) remains the existing review
vehicle; old CI and receipts must not be described as checks of a newer commit.

The backup contains the reviewed Mermaid patch chain and selected-RID native
payloads. The DLLs still contain temporary source-build path metadata; a future
distribution/reproducibility pass should consider path remapping and then repeat
the required source-bound validation. This checkpoint does not rebuild or alter
those binaries.

Start a continuation by reading this handoff, the active plan's defect table and
the selected evidence index. Check the actual branch/worktree status and current
PR head before changing anything. Do not reset concurrent edits. Hash the
candidate actually tested, not an older output directory. Reuse the existing
test tooling before adding another diagnostic framework.

Typical host-side component checks from the repository root are:

```powershell
dotnet test MarkdownRenderer/MarkdownRenderer.GitHub.Tests/MarkdownRenderer.GitHub.Tests.csproj -c Release -p:Platform=x64
dotnet test MarkdownRenderer/MarkdownRenderer.GitHub.Tests/MarkdownRenderer.GitHub.Tests.csproj -c Debug -p:Platform=x64
dotnet build MarkdownRenderer/MarkdownRenderer.Sample/MarkdownRenderer.Sample.csproj -c Release -p:Platform=x64
pwsh -NoProfile -File MarkdownRenderer/eng/Test-MarkdownCorrectnessUiHarness.ps1
pwsh -NoProfile -File MarkdownRenderer/eng/Test-MarkdownCorrectnessDiagnosticRouting.ps1
```

These are reproduction instructions, not new executions in this checkpoint.
For approved Sandbox runs, use the existing
`MarkdownRenderer/eng/Invoke-MarkdownCorrectnessUiTests.ps1` with a fresh result
directory and the freshly built sample. `-ResizeCrashDiagnosticOnly` intentionally
executes only identity and resize cases and leaves the rest UNVERIFIED.
Headless source acquisition and parsing commands are described in the evidence
index; neither launches or certifies the actual document view.

All findings and historical failed experiments remain available through the
[correctness chronology](correctness-plan.md), [historical performance plan](progressive-performance-plan.md),
native dependency provenance/patch ledgers and the source/test commit history.
See the [evidence retention instructions](evidence/2026-10-17/README.md#local-data-not-in-this-checkpoint)
before discarding local storage. A Git push of source and compact receipts is
not a backup of ignored raw artifacts, crash memory or fetched repository assets.
