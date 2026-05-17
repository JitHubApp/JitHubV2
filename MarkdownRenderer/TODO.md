# MarkdownRenderer — Maturity Gap TODO

This file tracks the work between the current state and a fully production-mature,
open-source-ready control. Items are grouped by area and roughly ordered by priority
within each group.

Legend: 🔴 blocks release · 🟠 must fix before 1.0 · 🟡 v1.1 candidate

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

> 1.0-ready for the non-HTML/non-LaTeX scope. Raw HTML and LaTeX/math are tracked
> separately and intentionally excluded from this release plan.

- ✅ **Fix inline image rendering**
  `LayoutBuilder.cs:333–339` falls back to alt-text for images embedded in text.
  Only standalone image paragraphs become `ImageBox`. Inline `![alt](url)` inside
  paragraphs must also render as an image, not alt-text.

- ↪️ **Fix HTML block / inline rendering** *(tracked outside 1.0 non-HTML plan)*
  HTML inline renders the raw tag string (e.g. `<br>`). HTML blocks are silently
  dropped. Implement sanitized plain-text rendering or a safe HTML sub-renderer.

- ✅ **Add definition list renderer**
  Markdig supports definition lists; no renderer exists. Add a GFM/extension
  renderer for `<dl>/<dt>/<dd>`.

- ↪️ **Add math / LaTeX block support** *(tracked outside 1.0 non-LaTeX plan)*
  Wire `UseMathematics()` into the Markdig pipeline and add a `MathBox` renderer.
  Initial implementation can render the raw LaTeX as styled code with a note that
  a math engine can be injected via `IMarkdownEmbedFactory`.

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
  Mermaid/diagram support is sample/documentation only through
  `IMarkdownEmbedFactory`; no built-in diagram engine is shipped.

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

> 1.0-ready. Core selection, embed selection, drag auto-scroll, HTML clipboard,
> and opt-in rendered plain-text copy are implemented. Remaining work is manual
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
  Copy always writes raw markdown source to the clipboard. Add a `CF_HTML`
  clipboard format path and optionally a rendered plain-text path.

- ✅ **Add opt-in rendered plain-text copy**
  `MarkdownCopyOptions` and `CopySelectionToClipboard(MarkdownCopyOptions?)`
  preserve source-markdown defaults while allowing rendered semantic text.

---

## Performance

> 1.0-ready for known non-HTML/non-LaTeX workloads. Core async pipeline, lazy
> large-document layout, cancellation, safe hot-path pooling, code-block
> segmentation, and embed/image virtualization are implemented.

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

---

## Packaging & Public API

> Complete. Core and GFM package metadata, XML docs, quick-start APIs, document
> queries, public-surface cleanup, and x86/x64/ARM64 ThorVG assets are in place.

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

- **Raw HTML policy** — intentionally out of scope for this plan.
- **LaTeX/math support** — intentionally out of scope for this plan.
- **Manual release smoke** — Narrator, real Windows contrast themes, customized
  contrast theme, system language/RTL, graphics-device reset, and x86/x64/ARM64
  sample launch still need human verification before shipping.
