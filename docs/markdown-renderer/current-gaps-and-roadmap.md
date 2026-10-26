# Current gaps and roadmap

MarkdownRenderer is a preview, not a completed 1.0 release.

The active work follows the [correctness goal and phase plan](correctness-plan.md),
adopted October 12, 2026. Browser-parity, comparative performance, and package-size
targets below are historical; they no longer block that goal. Distribution work
and future optimization remain separate from the correctness phases.

For the October 12 implementation, current failing gates, audit history, local
evidence, and proposed Rust direction, read the
[implementation checkpoint](implementation-checkpoint-2026-10-12.md). Results
below retain their historical scope and do not qualify later candidates.

## October 25 correctness closure

The correctness goal is closed except for environment-gated items; read the
[October 25 closure](correctness-closure-2026-10-25.md).

- **Phase 1 is closed.** The full live batch passes at actual 100%, 150% and
  200% display scaling, in Light, Dark and High Contrast, in both viewport modes.
  Every known defect, P1-001 to P1-041, is fixed with a regression test.
- **Phase 2:** 30 of 32 feature rows pass. Two rows wait on ARM64 hardware
  (Mermaid and resvg workers).
- **Phase 3 is complete:** all 500 frozen READMEs were evaluated, and every
  renderer defect found was fixed.
- **Current-source suites** pass 6,553 tests with no failures or skips, plus the
  official CommonMark/GFM corpus (1,322/1,322).

Still open within correctness:

- P1-042, a one-off Mermaid scroll that did not recur;
- live tooling limits for native touch cancellation, touch capture loss and
  clipboard isolation, which are covered offline instead;
- the browser-parity SVG pixel suite, which could not run because its Edge
  oracle fails on the qualification machine. It was never a correctness gate.

The 1.0 gates below are distribution and release work and remain open.

## October 14 correctness checkpoint (historical)

- All 500 frozen September 28 README bodies are acquired and integrity-verified.
  After the disclosure-parser repair, all 500 pass the headless parse/source-map
  checks without exceptions. One document retains two correctly classified
  unsupported-math fallbacks. This is not a full-page image/layout/selection or
  navigation audit; those evaluations remain required.
- The current pinned official CommonMark/GFM run and its offline rerun each pass
  1,322/1,322 examples with zero exceptions or waivers. The Release/x64 native
  renderer suite passes 3,041/3,041 before the final additional container-exit
  regression; the detailed plan retains each receipt and its exact scope.
- Mermaid's entity text, decoded literal tags, Markdown wrapping and self-loop
  labels have reviewed fixes and rebuilt x64/x86/ARM64 payloads. Its full feature
  suite is still red: 174 pass and eight strict regressions fail for authored
  Unicode/YAML collisions with internal entity placeholders. No complete-feature
  certification is claimed.
- Phase 1 still needs live interaction qualification and genuine 150%/200% DPI
  checks. The recorded Sandbox guest reports 96 DPI/1.0 despite the host's 150%;
  viewer zoom is not DPI evidence. Physical testing remains paused. All three
  correctness phases remain open; see the [active plan](correctness-plan.md).

## Implemented direction

- immutable `MarkdownEngineBuilder` -> `MarkdownEngine` ->
  `MarkdownDocument` model;
- explicit `MarkdownScrollView` and `MarkdownDocumentView` viewport ownership;
- lean base package and optional GFM, GitHub, HTML, Math, Mermaid, resvg, and
  TextMate packs;
- Fluent default behavior with GitHub-specific behavior opt-in;
- rendered-text plus `CF_HTML` default copy and explicit Copy Markdown;
- parser-independent declarative extension and stable host-service boundaries.

## Implemented capabilities, not release certification

- `MarkdownRenderer.Html` implements a bounded native safe-HTML subset parser
  and painter, including inline tags and cross-block scopes. It is not a browser.
- `MarkdownRenderer.Math` implements CSharpMath-based TeX typesetting into
  immutable vector scenes. Invalid input retains source fallback; only dollar
  delimiters are enabled for 1.0.
- `MarkdownRenderer.Mermaid` includes the selected-RID Merman engine, validated
  MMIR scenes, budget enforcement and original-code fallback. ELK is excluded.
- The declarative WinUI bridge consumes text, code, images, links, lists, tables,
  containers, hosted elements and registered custom scene primitives, including
  inline output. Unsupported fragments retain atomic built-in fallback.
- Package-specific test, provenance, architecture and console trim/AOT evidence
  exists. This does not establish every supported installed-app combination.

## Automated evidence completed

- The offline official CommonMark 0.31.2 and GFM 0.29 suites pass 1,322/1,322
  examples with zero exceptions. See the retained
  [conformance report](../../MarkdownRenderer/artifacts/conformance/report-fix-all-official-20260910-postreview.json).
- Stable public-API baseline tests pass and guard the intended exported surface.
  They do not replace the final human API-approval decision.
- The canonical x64 Release build and the x64 live sample automation pass. The
  live run completed 32/32 scenarios and recorded renderer disposal on close;
  retain the
  [post-review disposal evidence](../../MarkdownRenderer/artifacts/live-final/disposal-app-release-matrix-postreview-20260910.json).
- A prior x64 absolute performance baseline passed the documented first-view,
  scroll, allocation, cancellation, lifecycle, and memory budgets. Schema 10
  implements six-trial first-viewport Hodges-Lehmann estimates, a balanced
  Williams condition schedule, ordered Theil-Sen drift and warmup-boundary gates,
  per-trial and boundary-endpoint residual envelopes, monotonic UTC chronology,
  active-native-processor/cache-path proof, valid GC hierarchy, consistency-scaled
  MAD dispersion, a frozen non-tiered runtimeconfig, a canonical digest over the
  complete private runtime output, metric-specific noise floors, raw scroll
  evidence, five 40-sample cancellation trials with distinct warmups, eight
  source-lookup warmups, and independent observed-refresh qualification. Its
  [deployment-bound quick smoke](../../MarkdownRenderer/artifacts/performance/performance-quick-schema10-deployment-bound-r3-20260910.json)
  passes structural validation but is non-gating. The retained first full
  schema-10 [absolute baseline](../../MarkdownRenderer/artifacts/performance/performance-baseline-schema10-deployment-bound-r1-20260910.json)
  has sound deployment/runtime evidence but failed all six first-viewport
  stationarity checks, the 1 MiB cache-disabled absolute budget, warm-scroll
  cadence/stall, and refresh qualification (240 Hz configured, 117.72 Hz
  observed). It is failed diagnostic evidence, not release evidence. The failed
  identical-binary schema-8
  [candidate A](../../MarkdownRenderer/artifacts/performance/performance-candidate-a-optimized-r7-schema8-20260910.json)
  and [candidate B](../../MarkdownRenderer/artifacts/performance/performance-candidate-b-optimized-r7-schema8-20260910.json)
  are retained as evidence for the methodology change, not as passing release
  reports. Retained schema-9 r8 reports additionally expose the nonstationarity
  holes that schema 10 closes. Schema 11 adds generation/source-matched raw
  UI-publication samples and an independently checked ≤2 ms measured maximum.
  Schema 12 additionally requires measurement-scoped foreground, visible, and
  unobstructed evidence. The retained schema-10 reports cannot qualify a
  schema-12 candidate. No passing full schema-12 candidate or true
  cross-revision performance certification is claimed here.

## 1.0 gates still open

- final human API approval, even though the automated compatibility baseline is
  now green;
- trim and Native AOT validation for every promised package combination;
- physical x86 and ARM64 execution and install validation, plus clean install
  from produced x64 packages; the completed x64 developer build/live run is not
  package-install evidence for other architectures;
- manual accessibility and localization smoke with Narrator, every built-in and
  a customized Windows contrast theme, system languages, mixed RTL/LTR, text
  scaling, keyboard navigation, and hosted elements;
- actual graphics-device loss/recovery and ETW review on representative hardware;
- a passing full schema-12 same-machine baseline/candidate run, a true different-
  build cross-revision comparison, and retained regression reports; counterbalanced
  multi-run execution remains an external release-validation step; the fixed
  ABBA orchestration itself is implemented;
- signed NuGet publishing rehearsal, clean-consumer installation, package
  contents review, signing, staging publish, production publish, and reproducible
  release evidence.

Track evidence separately from implementation. In particular, ARM64 cross-builds
are not physical execution; forced palettes are not manual Narrator/contrast
validation; console trim/AOT evidence is not every installed-app package
combination; and an absolute baseline or candidate-only microbenchmark does not
prove a final cross-revision performance budget. A schema-10 quick smoke likewise
does not prove release budgets or regression behavior. See
[Release checklist](release-checklist.md).
