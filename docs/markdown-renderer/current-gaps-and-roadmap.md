# Current gaps and roadmap

MarkdownRenderer is a preview, not a completed 1.0 release. Browser parity,
comparative performance, and package size are tracked separately from
correctness.

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
- Package-specific tests, provenance checks, architecture checks and console
  trim/AOT smokes exist. They do not cover every supported installed-app
  combination.

## Automated coverage

- The `MarkdownRenderer.Conformance` harness runs every official CommonMark
  0.31.2 and GFM 0.29 example and checks reference-HTML parity. Its exception
  list is empty. CI runs the harness's own tests, not the full corpus.
- Public-API baseline tests guard the exported surface. They do not replace the
  final human API approval.
- The Sandbox UI runners and `MarkdownRenderer.Sample.Automation` drive the x64
  sample live. Hosted CI does not run them.
- The performance harness and its gates check first-view, scroll, allocation,
  cancellation, lifecycle, and memory budgets. Hosted CI runs the benchmark and
  allocation gates; full harness runs need a qualified machine.

## Open correctness items

- Mermaid and resvg worker execution on ARM64 needs ARM64 hardware.
- Native touch cancellation, touch capture loss, and clipboard isolation are
  tooling limits in the live runner. Offline tests cover them instead.
- The browser-parity SVG pixel suite needs a working Edge oracle. It is not a
  correctness gate.

## 1.0 gates still open

- final human API approval;
- trim and Native AOT validation for every promised package combination;
- physical x86 and ARM64 execution and install validation, plus clean install
  from produced x64 packages;
- manual accessibility and localization smoke with Narrator, every built-in and
  a customized Windows contrast theme, system languages, mixed RTL/LTR, text
  scaling, keyboard navigation, and hosted elements;
- actual graphics-device loss/recovery and ETW review on representative hardware;
- a passing full schema-12 same-machine baseline/candidate run and a true
  cross-revision comparison between separately built revisions;
- signed NuGet publishing rehearsal, clean-consumer installation, package
  contents review, signing, staging publish, and production publish.

Keep evidence separate from implementation. ARM64 cross-builds are not physical
execution; forced palettes are not manual Narrator/contrast validation; console
trim/AOT smokes are not every installed-app package combination; and a quick
harness run or candidate-only microbenchmark does not prove a cross-revision
performance budget. See [Release checklist](release-checklist.md).
