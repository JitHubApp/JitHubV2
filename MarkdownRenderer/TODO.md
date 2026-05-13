# MarkdownRenderer — Maturity Gap TODO

This file tracks the work between the current state and a fully production-mature,
open-source-ready control. Items are grouped by area and roughly ordered by priority
within each group.

Legend: 🔴 blocks release · 🟠 must fix before 1.0 · 🟡 v1.1 candidate

---

## Accessibility

> The biggest gap — currently ~60% complete. Screen reader support is non-negotiable
> for a production control.

- 🔴 **Implement `ITextProvider` / `ITextRangeProvider`**
  Narrator and other AT cannot query text by character offset, expand selection
  by word/line/paragraph, or drive Ctrl+F search. Add the UIA Text pattern to
  `MarkdownAutomationPeer`. (`Accessibility/MarkdownAutomationPeer.cs`)

- 🔴 **Expose heading levels in `MarkdownBlockPeer`**
  Override `GetHeadingLevelCore()` so Narrator announces "Heading 2" instead of
  treating H1–H6 identically to paragraphs. (`Accessibility/MarkdownBlockPeer.cs`)

- 🔴 **Implement `ITableProvider` / `ITableItemProvider` for tables**
  Table cells have no UIA table role. Screen readers cannot navigate rows/columns
  or understand header relationships. Add table peers with row/column/header
  associations. (`Accessibility/`)

- 🟠 **Add List / ListItem UIA control types**
  List blocks are plain text to UIA. Narrator cannot announce "list with N items".
  Add UIA `List` and `ListItem` control types to the list/list-item block peers.

- 🟡 **Expose code block language hint in UIA**
  The fenced code info string (e.g. `typescript`) is available from Markdig but
  discarded at render time. Expose it as a UIA `HelpText` or custom property.

---

## Rendering

> ~85% complete. Inline images and some Markdig extensions are missing.

- 🔴 **Fix inline image rendering**
  `LayoutBuilder.cs:333–339` falls back to alt-text for images embedded in text.
  Only standalone image paragraphs become `ImageBox`. Inline `![alt](url)` inside
  paragraphs must also render as an image, not alt-text.

- 🟠 **Fix HTML block / inline rendering**
  HTML inline renders the raw tag string (e.g. `<br>`). HTML blocks are silently
  dropped. Implement sanitized plain-text rendering or a safe HTML sub-renderer.

- 🟡 **Add definition list renderer**
  Markdig supports definition lists; no renderer exists. Add a GFM/extension
  renderer for `<dl>/<dt>/<dd>`.

- 🟡 **Add math / LaTeX block support**
  Wire `UseMathematics()` into the Markdig pipeline and add a `MathBox` renderer.
  Initial implementation can render the raw LaTeX as styled code with a note that
  a math engine can be injected via `IMarkdownEmbedFactory`.

- 🟡 **Add abbreviations extension**
  Wire `UseAbbreviations()` and render abbreviations with a tooltip/title
  affordance (hover shows full term).

- 🟡 **Apply generic attributes to styled elements**
  `UseGenericAttributes()` is parsed but `id`/`class` attributes are never applied.
  Apply `id` for fragment-link targets and `class` as an `ElementKey` alias for
  custom theming.

---

## Theming & Styling

> ~75% complete. Dynamic invalidation and coverage gaps are the main issues.

- 🟠 **Incremental theme invalidation — no re-parse on theme change**
  A theme change triggers a full `RequestRebuild()` that re-parses the markdown.
  The AST is unchanged — only text metrics need rebuilding. Add a restyle-only
  path that recreates `CanvasTextLayout` objects without re-parsing.
  (`Controls/MarkdownRendererControl.cs:654`)

- 🟠 **Auto-invalidate when `Theme.Overrides` is mutated**
  Setting `Theme.Overrides[key] = style` after first render is silently ignored
  until `Invalidate()` is called manually. Wire `ObservableDictionary` change
  notification (or replace with a proper API) to trigger restyle automatically.

- 🟡 **Per-element link hover / focus color styling**
  Hovering a link only changes the cursor. Add hover and focus color to
  `ElementStyle` and apply it in the link hit-test / pointer-over path.

- 🟡 **List nesting depth indent styles**
  All nesting levels use the same indent. Add per-depth indent scaling or
  `ElementStyle` overrides for nested lists.

- 🟡 **Code block and blockquote border styling**
  Only a solid `AccentBar` on the left is supported. Add full border radius,
  background, and padding properties to `ElementStyle`.

- 🟡 **Table cell alignment styling**
  GFM column alignment (left/center/right) is parsed but not applied to cell
  rendering in `TableBox`.

- 🟡 **Style composition / context-aware variants**
  No "Link inside Blockquote" vs. "Link in body" variant. Add context-key layering
  or style composition so element styles can vary by nesting context.

---

## Text Selection

> ~80% complete. Core selection works; edge cases and copy variants are missing.

- 🟠 **Make embedded WinUI elements participatory in selection**
  Task-list checkboxes and custom `EmbedBox` elements are skipped by selection —
  the range jumps over them. Add a selection-range slot for embedded elements so
  they are included in the selection span.

- 🟠 **Auto-scroll viewport during selection drag**
  Dragging the selection pointer beyond the top or bottom of the viewport does not
  scroll. Add auto-scroll to `OnPointerMoved` when the pointer exits the scroll
  viewport bounds.

- 🟡 **Copy-as-HTML / copy formatted text**
  Copy always writes raw markdown source to the clipboard. Add a `CF_HTML`
  clipboard format path and optionally a rendered plain-text path.

---

## Performance

> ~85% complete. Core async pipeline is solid; large-document and GC gaps remain.

- 🟠 **Lazy / viewport-relative layout for large documents**
  All block bounds are computed before first paint. For large documents (10K+
  lines) add a streaming measure path that only arranges blocks near the viewport
  and extends on scroll. (`Layout/LayoutBuilder.cs:24–42`)

- 🟠 **Document and guard `IMarkdownEmbedFactory` thread safety**
  `CanCreate()` and `MeasureHeight()` run on the layout thread. Calling WinUI APIs
  from there can deadlock. Add XML doc warnings, runtime thread assertions, and a
  developer best-practices guide with a worked example.

- 🟡 **Object pooling for `InlineRun` / `BlockBox` allocation**
  Large re-layouts allocate many short-lived `InlineRun`/`BlockBox` instances. Add
  a pool or recycling strategy to reduce GC pressure on rapid re-layouts or rapid
  markdown updates.

---

## Packaging & Public API

> ~70% complete. No docs, sparse API, no NuGet metadata.

- 🔴 **Add XML documentation to all public surface**
  `CS1591` is actively suppressed; the generated `.xml` file is empty. Add
  `/// <summary>` to every public type, property, method, and interface. Include
  thread-safety warnings on extension interfaces.

- 🟠 **Add quick-start static helpers / fluent builder**
  Consumers must manually wire parser + layout builder + registry + theme. Add
  `MarkdownRendererControl.CreateDefault()` and a fluent builder. Make GFM the
  obvious default (it should not require an explicit call).

- 🟠 **ARM64 / x86 SVG fallback visibility**
  Only the x64 ThorVG DLL is shipped. ARM64/x86 builds silently show empty space
  or alt-text when SVG rendering fails. Add a visible "SVG not supported on this
  architecture" placeholder and evaluate shipping ARM64 ThorVG.

- 🟠 **Clean up and stabilise public API surface**
  `LayoutSnapshot` exposes a raw `Blocks` list with no query helpers. Add
  `GetHeadings()`, `GetLinks()`, `GetCodeBlocks()` convenience methods. Add a
  `MarkdownDocument` facade to hide layout-internal complexity from consumers.

- 🟡 **Add NuGet package metadata**
  No license, repo URL, icon, or package description in `.csproj`. Add
  `PackageLicenseExpression`, `RepositoryUrl`, `PackageIcon`, `Description`,
  `Authors`, `PackageTags`.

---

## Open bugs / tech debt

These are design compromises in the current code that will surface as user-facing
bugs at scale:

- **Theme snapshot not auto-invalidated** — `Theme.Overrides[key] = style` is
  silently ignored until `Invalidate()` is called.
- **Footnote order heuristic** — `FootnoteRenderer.cs:28–45` manually guesses
  order when Markdig doesn't assign `Order > 0`; can fail with repeated citations.
- **Scroll anchor loss** — if all visible blocks scroll off-screen simultaneously
  the read anchor is lost and position may jump.
- **Pointer-capture state** — the left-pointer-capture flag
  (`MarkdownRendererControl.cs:90–92`) is implicit state; could misbehave if
  pointer events arrive out-of-order.
- **Missing stress-test coverage**: mixed RTL/LTR runs, 100K-word documents,
  rapid theme switching, concurrent selection + scroll + theme changes.
