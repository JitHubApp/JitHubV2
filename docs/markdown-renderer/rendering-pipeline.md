# Rendering pipeline

The rendering pipeline turns markdown source into native layout boxes and paints
those boxes through Win2D.

## Pipeline stages

1. `MarkdownRendererControl.Markdown` changes.
2. `RequestRebuild()` cancels any prior rebuild.
3. `MarkdownExtensionRegistry.BuildPipeline()` creates a Markdig pipeline.
4. `MarkdigParser` fixes forgiving data URIs and parses the markdown.
5. `LayoutBuilder` walks the Markdig AST and builds `BlockBox` objects.
6. Each block measures itself for the available width.
7. Blocks are arranged vertically into a `LayoutSnapshot`.
8. The snapshot is committed on the UI thread.
9. `CanvasVirtualControl` paints invalidated regions.
10. The XAML overlay hosts embeds, selection rectangles, and focus visuals.

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
- strikethrough through emphasis extras;
- links;
- autolinks;
- line breaks;
- standalone image paragraphs;
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

## Block dispatch

`LayoutBuilder.BuildBlock` handles built-in Markdig block types first through
custom extensions, then core node types:

- `HeadingBlock` -> `InlineContainerBox` with heading element key;
- `ParagraphBlock` -> paragraph or promoted `ImageBox`;
- `FencedCodeBlock` / `CodeBlock` -> code `InlineContainerBox`;
- `QuoteBlock` -> `StackBox` with quote styling;
- `ListBlock` -> `StackBox` containing `ListItemBox` children;
- `ThematicBreakBlock` -> `ThematicBreakBox`;
- `ContainerBlock` -> generic `StackBox`;
- custom registered Markdig node -> custom renderer output.

## Inline dispatch

The inline builder handles:

- `LiteralInline` -> `TextRun`;
- `CodeInline` -> `CodeInlineRun`;
- `EmphasisInline` -> `EmphasisRun`, `StrongRun`, or `StrikethroughRun`;
- `LinkInline` -> `LinkRun` or alt-text fallback for inline images;
- `LineBreakInline` -> `LineBreakRun`;
- `AutolinkInline` -> `LinkRun`;
- `HtmlInline` -> raw tag text fallback;
- GFM footnote links -> superscript internal `LinkRun`;
- unknown container inline -> flattened text fallback.

## Known rendering limitations

- Inline images inside text currently render as alt text, not images.
- Raw HTML is not rendered as HTML.
- HTML blocks are dropped by fallback behavior.
- Definition lists, math, abbreviations, subscript/superscript, and diagrams need
  future extensions.
- Generic attributes are parsed by GFM setup but not applied to layout or styles.
- Table alignment is not fully reflected in layout/styling yet.

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

