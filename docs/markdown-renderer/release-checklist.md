# Release checklist

MarkdownRenderer remains a preview. A package build is evidence for one gate,
not proof that the full 1.0 bar has passed. Browser-performance and size targets
are tracked separately from correctness.

Checked entries are automated checks that already run. Unchecked entries remain
release gates; an automated result never substitutes for the physical,
assistive-technology, packaging, signing, or publishing work named below.

## API and documentation

- [x] Run the stable public-API/reference-surface baseline tests; they guard the
  intended exported surface.
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
- [ ] Inspect each selected GFM, GitHub, HTML, Math, Mermaid, resvg, TextMate, and
  grammar pack independently.
- [ ] Confirm `MarkdownRenderer.All` is the only deliberate all-feature/all-payload
  dependency path.
- [ ] Verify README, license, icon, repository metadata, symbols, source link, XML
  documentation, lock files, and dependency closure.
- [ ] When resvg is selected, validate the signed x86, x64, and ARM64 RID workers,
  worker isolation/fault recovery, package size, SBOM/license closure, and
  matching PE machine types. Run
  `eng/Invoke-SvgResvgReleaseEvidence.ps1 -Mode Release` with every required
  evidence input and keep its output. The base package must not contain those
  assets.
- [ ] Confirm the lean TextMate integration does not silently contain the complete
  grammar set. Run `eng/Invoke-TextMateReleaseEvidence.ps1` and keep its
  contract-test, provenance, reproducibility, SBOM/notice, selected-RID size,
  managed-architecture, trim, single-file, and NativeAOT output.

## Correctness and conformance

- [ ] Run the `MarkdownRenderer.Conformance` harness over the official
  CommonMark 0.31.2 and GFM 0.29 examples (`--suite all`) and confirm every
  example passes.
- [x] Keep `conformance-exceptions.v1.json` empty; any new entry needs an issue
  link and is reported as stale once the example passes.
- [ ] Validate immutable source, diagnostic, semantic-query, and half-open UTF-16
  source-map invariants.
- [ ] Test normal Copy as rendered text plus `CF_HTML`, and Copy Markdown as the
  explicit source path.

## Native and app validation

- [x] Build the x64 Release configuration and consume the renderer from
  JitHub WinUI in CI.
- [ ] Run the x64 live sample automation and Sandbox UI runners, and confirm
  renderer disposal on close.
- [ ] Physically execute and install the supported x86 and ARM64 combinations;
  cross-builds and PE-machine inspection are not execution evidence.
- [ ] Install the produced x64 packages into a clean consumer environment; a
  developer-machine build is not clean-install evidence.
- [ ] Run trim and Native AOT analysis for every promised package combination;
  package-specific console smokes are not the complete installed-app matrix.
- [x] Run the benchmark and allocation gates (`eng/Test-MarkdownBenchmarkGates.ps1`)
  and the external performance-gate fixture tests in CI.
- [ ] Obtain a passing full schema-12 same-machine baseline/candidate harness
  run on a qualified, stable foreground display cadence. A quick run does not
  prove the release regression budget.
- [ ] Compare separately built reference and candidate revisions with schema 12.
  A same-binary comparison shows repeatability only.
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
- [ ] Collect the run directories beneath one root and run
  `eng/Test-TouchSelectionReleaseMatrix.ps1`. The validator requires all passing
  interaction results, normal and system-contrast results at every scale, all
  supported process architectures, and the Light/Dark/contrast/RTL screenshots.
- [ ] Verify unavailable/invalid optional capabilities fall back safely in the
  final produced-package matrix.

## Publishing

- [ ] Rehearse signed NuGet publishing to a staging feed.
- [ ] Install only from the produced packages into a clean consumer app.
- [ ] Complete release signing and the approved staging/production publish steps.
- [ ] Archive exact commands, package hashes, test reports, approved exceptions,
  and manual results.
- [ ] Publish 1.0 only after every required gate is closed; otherwise publish a
  preview with explicit limitations.
