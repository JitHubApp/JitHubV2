# Rendering pipeline

The rendering pipeline turns markdown source into native layout boxes and paints
those boxes through Win2D.

## Pipeline stages

1. `MarkdownRendererControl.Markdown` changes.
2. `RequestRebuild()` cancels any prior rebuild.
3. `MarkdownExtensionRegistry.BuildPipeline()` creates a Markdig pipeline.
4. `MarkdigParser` fixes forgiving data URIs and parses the markdown.
5. `LayoutBuilder` walks the Markdig AST and builds `BlockBox` objects.
6. Small documents measure every block; large documents measure only the initial
   viewport band and keep estimates for the rest.
7. Blocks are arranged vertically into a `LayoutSnapshot`.
8. The snapshot is committed on the UI thread.
9. `CanvasVirtualControl` paints invalidated regions.
10. The XAML overlay hosts embeds, selection rectangles, and focus visuals.

Large-document snapshots extend measured bands on scroll. The source map and
cheap block tree are complete from the start, while native text layouts and
inline image/embed geometry are created as their top-level blocks approach the
viewport.

## Core markdown support

Implemented in the core project:

- headings H1-H6;
- paragraphs;
- fenced and indented code blocks;
- block quotes;
- ordered and unordered lists;
- ordered-list start numbers;
- thematic breaks;
- inline literal text;
- inline code;
- emphasis and strong emphasis;
- emphasis extras: strikethrough, subscript, superscript, inserted text, and
  marked text when the pipeline enables them;
- links;
- autolinks;
- line breaks;
- standalone image paragraphs;
- inline image cells;
- footnote forward links when GFM footnotes are enabled.

## GitHub-flavored markdown support

`MarkdownRenderer.Gfm` registers:

- pipe tables;
- task lists;
- autolinks;
- emphasis extras;
- footnotes;
- emoji and smiley parsing;
- generic attributes;
- custom renderers for tables, task-list items, alerts, and footnote groups.

Call:

```csharp
var registry = new MarkdownExtensionRegistry()
    .UseGitHubFlavoredMarkdown();

renderer.ExtensionRegistry = registry;
```

`MarkdownRenderer.Gfm` also provides `UseMarkdownExtra()` for opt-in non-GFM
Markdig extras: definition lists, abbreviations, and figures. It registers
native renderers for definition lists and figures while keeping the strict GFM
helper unchanged.

## Block dispatch

`LayoutBuilder.BuildBlock` handles built-in Markdig block types first through
custom extensions, then core node types:

- `HeadingBlock` -> `InlineContainerBox` with heading element key;
- `ParagraphBlock` -> paragraph or promoted `ImageBox`;
- `FencedCodeBlock` / `CodeBlock` -> code `InlineContainerBox`, or a segmented
  `StackBox` when the block is too large for one monolithic text layout;
- `QuoteBlock` -> `StackBox` with quote styling;
- `ListBlock` -> `StackBox` containing `ListItemBox` children;
- `ThematicBreakBlock` -> `ThematicBreakBox`;
- `ContainerBlock` -> generic `StackBox`;
- custom registered Markdig node -> custom renderer output.

## Inline dispatch

The inline builder handles:

- `LiteralInline` -> `TextRun`;
- `CodeInline` -> `CodeInlineRun`;
- `EmphasisInline` -> `EmphasisRun`, `StrongRun`, `StrikethroughRun`,
  `SubscriptRun`, `SuperscriptRun`, `InsertedRun`, or `MarkedRun`;
- `LinkInline` -> `LinkRun` or atomic inline image cell;
- `LineBreakInline` -> `LineBreakRun`;
- `AutolinkInline` -> `LinkRun`;
- `HtmlInline` -> raw tag text fallback;
- `AbbreviationInline` -> `AbbreviationRun` with accessible expansion text;
- GFM footnote links -> superscript internal `LinkRun`;
- unknown container inline -> flattened text fallback.

## Known rendering limitations

- Raw HTML is not rendered as HTML.
- HTML blocks are intentionally outside the native renderer's 1.0 support scope.
- LaTeX/math is intentionally out of scope for the 1.0 non-HTML/non-LaTeX plan.
- Mermaid/diagram support is sample/documentation only through
  `IMarkdownEmbedFactory`; no built-in diagram engine is shipped.

## Painting

Blocks implement `Paint(CanvasDrawingSession ds, Rect viewport)`. The virtual
canvas requests paint for invalidated regions, and each box decides whether it
intersects the viewport.

Text-heavy boxes use DirectWrite text layouts through Win2D. Images paint
`CanvasBitmap`. Hosted WinUI controls do not paint on the canvas; they are placed
on the overlay.

## Image loading

`ImageBox` loads images asynchronously. Until load completion, it displays an alt
text placeholder. When load completes:

- if intrinsic size changed, the control rebuilds layout;
- otherwise it repaints the affected region.

Images are lazy-loaded when their bounds enter the viewport plus an overscan band.
