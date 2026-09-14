# Release checklist

MarkdownRenderer remains a preview. A package build is evidence for one gate,
not proof that the full 1.0 bar has passed.

Checked entries record automated evidence completed in this worktree. Unchecked
entries remain release gates; a checked automated result never substitutes for
the physical, assistive-technology, packaging, signing, or publishing work named
below.

## API and documentation

- [x] Run the stable public-API/reference-surface baseline tests; they pass and
  guard the intended exported surface.
- [ ] Approve the immutable engine/document API and explicit viewport types.
- [ ] Confirm `MarkdownRendererControl` and its builder `Build()` path remain
  obsolete compatibility surface.
- [ ] Give the automated public surface its final human compatibility review;
  test success is not API approval or 1.0 certification.
- [ ] Check every README and quick start for `MarkdownScrollView` versus
  `MarkdownDocumentView`, lean package composition, Fluent defaults, GitHub
  opt-in, and rendered-text plus `CF_HTML` default copy.
- [ ] Describe implemented safe HTML, native vector Math, Mermaid and declarative
  adapters accurately; record syntax limitations and unverified release gates
  separately from implementation status.

## Package matrix

- [ ] Inspect `MarkdownRenderer.Core` and the lean `MarkdownRenderer` package.
- [ ] Inspect each selected GFM, GitHub, HTML, Math, Mermaid, ThorVG, TextMate, and
  grammar pack independently.
- [ ] Confirm `MarkdownRenderer.All` is the only deliberate all-feature/all-payload
  dependency path.
- [ ] Verify README, license, icon, repository metadata, symbols, source link, XML
  documentation, lock files, and dependency closure.
- [ ] When ThorVG is selected, validate the x86, x64, and ARM64 RID-native DLLs and
  matching PE machine types. The base package must not contain those assets.
- [ ] Confirm the lean TextMate integration does not silently contain the complete
  grammar set. Run `eng/Invoke-TextMateReleaseEvidence.ps1` and retain its
  contract-test, provenance, reproducibility, SBOM/notice, selected-RID size,
  managed-architecture, trim, single-file, and NativeAOT evidence.

## Correctness and conformance

- [x] Run the offline official CommonMark 0.31.2 and GFM 0.29 suites. The retained
  [report](../../MarkdownRenderer/artifacts/conformance/report-fix-all-official-20260910-postreview.json)
  records 1,322/1,322 passing examples, zero failures, and zero exceptions at its
  stated verification level.
- [x] Reject stale or unexplained conformance exceptions; the recorded official
  run required none.
- [ ] Validate immutable source, diagnostic, semantic-query, and half-open UTF-16
  source-map invariants.
- [ ] Test normal Copy as rendered text plus `CF_HTML`, and Copy Markdown as the
  explicit source path.

## Native and app validation

- [x] Build the canonical x64 Release configuration and run the x64 live sample
  automation. The recorded run passed 32/32 scenarios, and the retained
  [disposal evidence](../../MarkdownRenderer/artifacts/live-final/disposal-app-release-matrix-postreview-20260910.json)
  confirms renderer disposal on close rather than only process disappearance.
- [ ] Physically execute and install the supported x86 and ARM64 combinations;
  cross-builds and PE-machine inspection are not execution evidence.
- [ ] Install the produced x64 packages into a clean consumer environment; the
  completed developer-machine x64 build/live run is not clean-install evidence.
- [ ] Run trim and Native AOT analysis for every promised package combination;
  package-specific console evidence is not the complete installed-app matrix.
- [x] Retain the prior passing x64
  [absolute performance baseline](../../MarkdownRenderer/artifacts/live-final/performance-baseline-fixed-20260910.json)
  as historical absolute-budget evidence for first view, scrolling, allocation,
  cancellation, lifecycle, and memory behavior.
- [x] Implement the schema-10 measurement and external-gate contract: six-trial
  first-viewport Hodges-Lehmann estimates, ordered Theil-Sen drift and warmup-
  boundary eligibility, per-trial and boundary-endpoint residual envelopes,
  consistency-scaled MAD dispersion, monotonic UTC trial chronology, active-native-
  processor evidence, physically valid GC deltas, frozen absolute
  noise floors combined with the +5% latency / +2% allocation limits, raw scroll
  evidence, five 40-sample cancellation trials with distinct warmups, eight
  source-lookup warmups, independent refresh qualification, and a canonical digest
  over the complete private runtime output with five critical artifacts also named
  explicitly. Every absolute gate remains mandatory. The retained
  [deployment-bound quick smoke](../../MarkdownRenderer/artifacts/performance/performance-quick-schema10-deployment-bound-r3-20260910.json)
  passes the Williams schedule, cache-proof, pinned-thread, and frozen-runtime
  structural validation but is explicitly non-gating.
- [x] Preserve the identical-binary schema-8
  [baseline](../../MarkdownRenderer/artifacts/performance/performance-baseline-optimized-r7-schema8-20260910.json),
  [candidate A](../../MarkdownRenderer/artifacts/performance/performance-candidate-a-optimized-r7-schema8-20260910.json),
  and [candidate B](../../MarkdownRenderer/artifacts/performance/performance-candidate-b-optimized-r7-schema8-20260910.json)
  as methodology evidence. The failed candidates motivated later contracts and are not
  passing regression or release evidence.
- [x] Preserve the schema-9 r8 baseline/candidate-A/candidate-B reports. Their
  ordered trial vectors exposed settling that order-invariant MAD did not reject;
  they are diagnostic inputs to schema 10, not release evidence.
- [x] Retain the first full schema-10
  [absolute baseline attempt](../../MarkdownRenderer/artifacts/performance/performance-baseline-schema10-deployment-bound-r1-20260910.json).
  Its deployment/runtime evidence is valid, but it failed all six viewport
  stationarity checks, the 1 MiB cache-disabled absolute budget, warm-scroll
  cadence/stall, and refresh qualification (240 Hz configured, 117.72 Hz
  observed); it is failed diagnostic evidence.
- [ ] Obtain and retain a passing full schema-10 same-machine baseline/candidate
  run on a qualified, stable display cadence. Neither the failed absolute
  baseline nor the schema-10 quick smoke proves the release regression budget.
- [ ] Compare separately built reference and candidate revisions with schema 10.
  A same-binary comparison demonstrates repeatability only; a true different-build
  cross-revision result is still required.
- [x] Implement fixed `R1, C1, C2, R2` counterbalanced orchestration with an ABBA
  contrast as its sole relative gate and pair/span/drift diagnostics kept
  non-gating.
- [ ] Run and retain that counterbalanced protocol against separately built
  reference and candidate revisions.
- [ ] Exercise actual graphics-device loss/recovery and review ETW traces on
  representative hardware; simulated invalidation and unit tests are not this gate.
- [ ] Run Narrator, keyboard, every built-in and a customized Windows contrast
  theme, text scaling, real system-language changes, mixed RTL/LTR,
  link, image, table, code, and hosted-element smoke.
- [ ] Run `eng/Test-TouchSelectionSampleUi.ps1` on physical x86, x64, and ARM64
  processes at 100%, 150%, and 200% display scaling. Pass
  `-ExpectedArchitecture` and `-ExpectedDpiScale` so a mislabeled run fails.
- [ ] At every required display scale, repeat the touch run with a Windows system
  contrast theme enabled and pass `-RequireSystemHighContrast`. Forced sample
  colors and 200% text scaling are supplemental coverage, not DPI/contrast evidence.
- [ ] Collect the run directories beneath one evidence root and run
  `eng/Test-TouchSelectionReleaseMatrix.ps1`. The validator requires all passing
  interaction results, normal and system-contrast evidence at every scale, all
  supported process architectures, and the Light/Dark/contrast/RTL screenshots.
- [ ] Verify unavailable/invalid optional capabilities fall back safely in the
  final produced-package matrix.

## Publishing

- [ ] Rehearse signed NuGet publishing to a staging feed.
- [ ] Install only from the produced packages into a clean consumer app.
- [ ] Complete release signing and the approved staging/production publish steps.
- [ ] Archive exact commands, package hashes, test reports, approved exceptions,
  and manual evidence.
- [ ] Publish 1.0 only after every required gate is closed; otherwise publish a
  preview with explicit limitations.
