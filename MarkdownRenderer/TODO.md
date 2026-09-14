# MarkdownRenderer — Maturity Gap TODO

This file tracks the work between the current state and a fully production-mature,
open-source-ready control. Items are grouped by area and roughly ordered by priority
within each group.

Implementation ledger, not release certification. The authoritative outstanding
validation gates are in [the release checklist](../docs/markdown-renderer/release-checklist.md).
Completed entries may retain historical problem descriptions for context.

Legend: 🔴 blocks release · 🟠 must fix before 1.0 · 🟡 v1.1 candidate

## Automated evidence recorded

- ✅ The offline official CommonMark 0.31.2 and GFM 0.29 run passed all
  1,322 examples with no exceptions. Retain the
  [conformance report](artifacts/conformance/report-fix-all-official-20260910-postreview.json)
  with the release evidence.
- ✅ Stable public-API baseline tests pass and guard the intended exported
  surface. Formal API approval is still a release decision, not an automated
  test result.
- ✅ The canonical x64 Release build and x64 live sample automation pass;
  the 32/32 live scenarios include observed renderer disposal rather than only
  process exit. Retain the
  [disposal evidence](artifacts/live-final/disposal-app-release-matrix-postreview-20260910.json).
  This is not physical x86 or ARM64 evidence.
- ✅ A prior x64 absolute performance baseline met the documented latency,
  allocation, scrolling, cancellation, lifecycle, and memory budgets. The
  schema-10 [quick smoke](artifacts/performance/performance-quick-schema10-deployment-bound-r3-20260910.json)
  passes its Williams schedule, pinned-thread, cache-proof, monotonic chronology,
  residual-envelope, GC-hierarchy, active-topology, full-runtime-output identity,
  and frozen-runtime structural validation. The first full schema-10
  [absolute baseline](artifacts/performance/performance-baseline-schema10-deployment-bound-r1-20260910.json)
  is retained but failed viewport stationarity, the 1 MiB cache-disabled absolute
  budget, warm-scroll cadence/stall, and refresh qualification (240 Hz configured,
  117.72 Hz observed). It is failed diagnostic evidence, not release evidence. A
  passing full baseline/candidate, true different-build cross-revision comparison,
  and counterbalanced execution remain open; schema-8 and schema-9 r8 reports are
  methodology evidence only.

---

## Accessibility

> The foundational UIA surface is now implemented. Remaining accessibility work
> is deeper OS/Narrator smoke coverage, keyboard edge cases, and broader real
> Windows contrast-theme / system-language validation.

- ✅ **Implement `ITextProvider` / `ITextRangeProvider`**
  Narrator and other AT cannot query text by character offset, expand selection
  by word/line/paragraph, or drive Ctrl+F search. Add the UIA Text pattern to
  `MarkdownAutomationPeer`. (`Accessibility/MarkdownAutomationPeer.cs`)

- ✅ **Implement `ITextProvider.RangeFromChild`**
  Child peers now map back to text ranges through WinUI peer/provider identity,
  with deterministic UI automation coverage for hyperlinks, images, and hosted
  WinUI embedded controls.

- ✅ **Expose core `ITextRangeProvider` text attributes**
  Ranges now expose read-only/hidden/active/culture/flow attributes plus
  font family, font size, font weight, foreground/background color, underline,
  strikethrough, style id/name, and superscript. UI automation verifies link,
  code, body, and high-contrast color attributes.

- ✅ **Expose heading levels in `MarkdownBlockPeer`**
  Override `GetHeadingLevelCore()` so Narrator announces "Heading 2" instead of
  treating H1–H6 identically to paragraphs. (`Accessibility/MarkdownBlockPeer.cs`)

- ✅ **Implement `ITableProvider` / `ITableItemProvider` for tables**
  Table cells have no UIA table role. Screen readers cannot navigate rows/columns
  or understand header relationships. Add table peers with row/column/header
  associations. (`Accessibility/`)

- ✅ **Add List / ListItem UIA control types**
  List blocks are plain text to UIA. Narrator cannot announce "list with N items".
  Add UIA `List` and `ListItem` control types to the list/list-item block peers.

- ✅ **Expose code block language hint in UIA**
  The fenced code info string (e.g. `typescript`) is available from Markdig but
  discarded at render time. Expose it as a UIA `HelpText` or custom property.

- 🟠 **Manual Narrator + real Windows theme smoke**
  CI now covers forced RTL and forced high-contrast palettes deterministically,
  but a release-quality pass still needs manual/optional smoke across Narrator,
  every built-in Windows contrast theme, a customized contrast theme, and real
  system language changes.

---

## Rendering

> Native rendering includes opt-in safe HTML, Math and Mermaid. Implementation
> coverage does not close the physical-device and release-validation matrix.

- ✅ **Fix inline image rendering**
  `LayoutBuilder.cs:333–339` falls back to alt-text for images embedded in text.
  Only standalone image paragraphs become `ImageBox`. Inline `![alt](url)` inside
  paragraphs must also render as an image, not alt-text.

- ✅ **Implement bounded safe HTML block / inline rendering**
  The optional HTML pack includes the native subset parser/painter. Configured
  limits also apply to inline tags and cross-block scopes; browser HTML is out of scope.

- ✅ **Add definition list renderer**
  Markdig supports definition lists; no renderer exists. Add a GFM/extension
  renderer for `<dl>/<dt>/<dd>`.

- ✅ **Add native Math support**
  The optional Math pack typesets dollar-delimited TeX into immutable vector
  scenes using CSharpMath, with bounded processing and invalid-source fallback.

- ✅ **Add abbreviations extension**
  Wire `UseAbbreviations()` and render abbreviations with a tooltip/title
  affordance (hover shows full term).

- ✅ **Finish emphasis extras**
  Subscript, superscript, inserted text, and marked text now map to distinct
  inline runs with style keys, source maps, UIA text attributes, and clipboard
  behavior.

- ✅ **Add figure renderer where Markdig produces figures**
  `MarkdownRenderer.Gfm.UseMarkdownExtra()` registers figure/caption rendering
  without changing ordinary image behavior.

- ✅ **Document diagram extension pattern**
  The optional Mermaid pack includes the selected-RID Merman engine and MMIR
  adapter. Hosted controls remain available for custom interactive diagrams.

- ✅ **Apply generic attributes to styled elements**
  `UseGenericAttributes()` is parsed but `id`/`class` attributes are never applied.
  Generic `id` attributes now register fragment targets and `class`/`id` values
  participate in theme override alias composition.

---

## Theming & Styling

> Implementation complete for the tracked theming and styling gaps. Release
> validation still needs manual visual smoke across real contrast themes and any
> app-specific theme presets consumers want to add.

- ✅ **Incremental theme invalidation — no re-parse on theme change**
  A theme change triggers a full `RequestRebuild()` that re-parses the markdown.
  The AST is unchanged — only text metrics need rebuilding. Add a restyle-only
  path that recreates `CanvasTextLayout` objects without re-parsing.
  Theme rebuilds now reuse the cached normalized-source AST while recreating
  layout/text metrics from a fresh `ThemeSnapshot`.

- ✅ **Auto-invalidate when `Theme.Overrides` is mutated**
  Setting `Theme.Overrides[key] = style` after first render is silently ignored
  until `Invalidate()` is called manually. Wire `ObservableDictionary` change
  notification (or replace with a proper API) to trigger restyle automatically.
  Direct `add`, `remove`, `clear`, and indexer assignments now bump revision and
  raise `Changed`.

- ✅ **Per-element link hover / focus color styling**
  Hovering a link only changes the cursor. Add hover and focus color to
  `ElementStyle` and apply it in the link hit-test / pointer-over path. Hover
  and keyboard focus now paint on the overlay without mutating base text layouts.

- ✅ **List nesting depth indent styles**
  All nesting levels use the same indent. Add per-depth indent scaling or
  `ElementStyle` overrides for nested lists. List depth keys and `NestedListIndent`
  now feed both normal and task-list marker gutters.

- ✅ **Code block and blockquote border styling**
  Only a solid `AccentBar` on the left is supported. Add full border radius,
  background, and padding properties to `ElementStyle`.

- ✅ **Table cell alignment styling**
  GFM column alignment (left/center/right) is parsed and passed through to
  `TableBox` cell layout.

- ✅ **Style composition / context-aware variants**
  No "Link inside Blockquote" vs. "Link in body" variant. Add context-key layering
  or style composition so element styles can vary by nesting context.

---

## Text Selection

> Core selection, embed selection, drag auto-scroll, HTML clipboard,
> and rendered plain-text copy are implemented. Remaining work is manual
> release smoke across target paste apps.

- ✅ **Make embedded WinUI elements participatory in selection**
  Task-list checkboxes and custom `EmbedBox` elements are skipped by selection —
  the range jumps over them. Add a selection-range slot for embedded elements so
  they are included in the selection span.

- ✅ **Auto-scroll viewport during selection drag**
  Dragging the selection pointer beyond the top or bottom of the viewport does not
  scroll. Add auto-scroll to `OnPointerMoved` when the pointer exits the scroll
  viewport bounds.

- ✅ **Copy-as-HTML / copy formatted text**
  Normal Copy writes rendered text and `CF_HTML`; Copy Markdown preserves source.

- ✅ **Make rendered plain-text copy the default**
  `MarkdownCopyOptions` and `CopySelectionToClipboard(MarkdownCopyOptions?)`
  default to rendered semantic text plus HTML, with explicit source-markdown copy.

---

## Performance

> Core async pipeline, lazy large-document layout, cancellation, safe hot-path
> pooling, code-block segmentation, and embed/image virtualization are
> implemented. The schema-10 evidence contract adds a balanced six-condition
> Williams schedule, six-trial first-viewport Hodges-Lehmann estimates, MAD,
> Theil-Sen drift, boundary and residual-envelope checks, monotonic UTC chronology,
> cache-path and active-processor proof, metric-specific noise floors,
> independently qualified refresh rate, raw scroll evidence, five 40-sample
> cancellation trials, eight source-lookup warmups, and a canonical digest over the
> complete private runtime output. Automated absolute-budget evidence has passed
> once and the deployment-bound schema-10 quick smoke is green. The retained first
> full schema-10 baseline failed stationarity, one first-viewport absolute budget,
> warm-scroll cadence/stall, and refresh qualification; a passing full same-machine
> baseline/candidate and different-build comparison are still pending.

- ✅ **Lazy / viewport-relative layout for large documents**
  All block bounds are computed before first paint. For large documents (10K+
  lines) add a streaming measure path that only arranges blocks near the viewport
  and extends on scroll. (`Layout/LayoutBuilder.cs:24–42`)

- ✅ **Document and guard `IMarkdownEmbedFactory` thread safety**
  `CanCreate()` and `MeasureHeight()` run on the layout thread. Calling WinUI APIs
  from there can deadlock. Add XML doc warnings, runtime thread assertions, and a
  developer best-practices guide with a worked example.

- ✅ **Avoid unsafe pooling for layout/native/UI state**
  Large re-layouts allocate many short-lived `InlineRun`/`BlockBox` instances, but
  these objects carry source-map identity, native text layouts, image events, and
  hosted UI references. Keep them unpooled; only pool proven pure managed helpers.

- ✅ **Reduce safe managed hot-path allocation**
  Inline text-buffer construction now uses a small thread-local `StringBuilder`
  pool. Native text layouts, image boxes, and hosted controls remain unpooled.

- ✅ **Segment huge code blocks**
  Very large fenced/indented code blocks are split above the monolithic text
  layout threshold so one pasted block cannot force a single enormous DirectWrite
  layout.

- ✅ **Make regression evidence robust to measured machine noise**
  Schema 10 recomputes raw trial evidence, uses the Hodges-Lehmann location of six
  first-viewport trial p95s (five for the other repeated metrics), rejects
  excessive consistency-scaled MAD, ordered drift, boundary movement, and isolated
  residuals as inconclusive, and gates
  each stable increase against `max(relative allowance, absolute noise floor)`.
  The +5% latency and +2% allocation limits remain frozen, and every absolute
  performance budget remains mandatory. Cancellation now records five trials of
  40 supersessions with one distinct warmup per trial; source lookup uses eight
  fixed warmup passes. This implementation item does not close the outstanding
  full-run or true cross-revision release gates.

---

## Packaging & Public API

> Implementation complete. Core and optional-pack metadata, XML docs,
> quick-start APIs, document queries, public-surface cleanup, and
> x86/x64/ARM64 native assets are in place. Final release validation remains a
> separate gate.

- ✅ **Add XML documentation to all public surface**
  `CS1591` is enabled for core and GFM; builds pass with `-warnaserror:CS1591`.

- ✅ **Add quick-start static helpers / fluent builder**
  Core exposes `MarkdownRendererControl.CreateDefault()` and
  `MarkdownRendererControlBuilder`; GFM exposes `GfmMarkdownRenderer.CreateDefault()`
  plus `MarkdownRendererControlBuilder.UseGitHubFlavoredMarkdown()`.

- ✅ **ARM64 SVG native asset support**
  ThorVG ships for x64 and ARM64, default repo builds copy the selected DLL to
  the output root, and the runtime resolver probes app-root, project-reference,
  and RID-native layouts.

- ✅ **x86 SVG native asset support**
  ThorVG ships and is PE-validated for x86, x64, and ARM64.

- ✅ **Clean up and stabilise public API surface**
  Layout snapshots and concrete renderer boxes are internal implementation
  details. `MarkdownRendererControl.Document` exposes a stable facade with
  `GetHeadings()`, `GetLinks()`, `GetCodeBlocks()`, and `GetImages()`.

- ✅ **Add NuGet package metadata**
  Core and GFM include package IDs, descriptions, authors, MIT license metadata,
  repository/project URLs, tags, README, and icon assets.

---

## Open bugs / tech debt

Remaining tracked debt for 1.0 is validation-oriented rather than known code
blockers:

- ✅ **Raw HTML policy** — `MarkdownRenderer.Html` provides an opt-in,
  non-executable safe-HTML subset with bounded parsing and explicit link/image
  policy.
- ✅ **LaTeX/math support** — `MarkdownRenderer.Math` provides bounded native
  inline and display math with diagnostics and literal-code fallback for invalid
  input.
- **Manual and external release gates** — physical x86 and ARM64 execution and
  install; Narrator; real and customized Windows contrast themes; system
  language, mixed RTL/LTR, and text scaling; actual graphics-device loss plus
  ETW review; every promised trim/Native AOT package combination; clean package
  install; signing; staging publish; and production publishing still require
  separate evidence before shipping. The completed x64 developer-machine build
  and live automation do not substitute for those gates.
