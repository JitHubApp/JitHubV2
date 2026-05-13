# Architecture

`MarkdownRendererControl` is a WinUI `UserControl` with two coordinated visual
layers:

1. a `CanvasVirtualControl` for Win2D/DirectWrite painting;
2. a transparent XAML `Canvas` overlay for hosted WinUI embeds, selection
   rectangles, and focus rings.

The control owns parsing, layout, paint invalidation, selection, hosted control
realization, image loading, keyboard navigation, and UI Automation peers.

## High-level flow

```text
Markdown string
  -> Markdig pipeline
  -> Markdig AST
  -> LayoutBuilder
  -> BlockBox / InlineRun tree
  -> LayoutSnapshot
  -> CanvasVirtualControl paint regions
  -> XAML overlay for selection, focus, and hosted controls
```

## Main object responsibilities

| Type | Responsibility |
| --- | --- |
| `MarkdownRendererControl` | Public WinUI control, dependency properties, rebuild pipeline, input, scrolling, overlays, and event surface. |
| `MarkdigParser` | Runs Markdig parsing and data-URI fixing. |
| `MarkdownExtensionRegistry` | Holds Markdig pipeline customizations and AOT-safe node renderer registrations. |
| `LayoutBuilder` | Converts Markdig AST nodes into layout boxes and inline runs. |
| `LayoutSnapshot` | Immutable-ish committed layout tree plus source map and footnote metadata. |
| `BlockBox` | Base class for block layout, paint, hit-test, selection rects, and disposal. |
| `InlineContainerBox` | Text-heavy block for paragraphs, headings, list markers, code blocks, table cells, etc. |
| `ImageBox` | Async bitmap/SVG image loading, measurement, painting, caption, and image cache integration. |
| `EmbedBox` | Reserves space for a hosted block-level WinUI `FrameworkElement`. |
| `SelectionController` | Owns the active `DocumentRange` and yields highlight rectangles. |
| `MarkdownSourceMap` | Maps rendered positions back to markdown source spans for source-accurate copy. |
| `MarkdownAutomationPeer` | UI Automation root peer for document traversal. |

## Rebuild pipeline

`RequestRebuild()` cancels any in-flight pipeline and starts a new async rebuild.
The rebuild:

1. builds a Markdig pipeline from `ExtensionRegistry`;
2. parses markdown through `MarkdigParser`;
3. creates a `MarkdownSourceMap`;
4. resolves a `ThemeSnapshot` against the current WinUI theme;
5. builds a layout tree with `LayoutBuilder`;
6. swaps the committed `LayoutSnapshot`;
7. updates canvas and overlay dimensions;
8. restores scroll anchor when possible;
9. rebuilds embed and image plans;
10. invalidates the virtual canvas.

The current pipeline re-parses markdown for theme changes. That is correct but
wasteful; the roadmap includes a restyle-only path.

## Layout model

The layout tree is a hierarchy of `BlockBox` objects. Leaf text blocks hold
`InlineRun` instances. Containers such as lists, block quotes, and tables recurse
through child boxes.

Every selectable/rendered box receives a `BlockIndex`. Source spans are recorded
against `(blockIndex, inlineIndex, characterOffset)` ranges, allowing selection to
copy the exact source markdown that produced a rendered visual range.

## Visual layering

```text
ScrollViewer
  Grid
    CanvasVirtualControl        // DirectWrite/Win2D text, rules, tables, images
    Canvas overlay
      selection rectangles      // pooled, low z-index
      hosted WinUI embeds       // buttons, checkboxes, custom controls
      focus ring                // keyboard navigation
```

Selection is intentionally drawn on the overlay. Repainting DirectWrite text on
every pointer move caused visible text shake at fractional DPI. Overlay
rectangles avoid canvas invalidation during drag.

## Ownership and lifetime

- The committed `LayoutSnapshot` owns native text layout resources.
- Old snapshots are disposed after an atomic swap.
- In-flight rebuilds are cancelled when new input arrives.
- Hosted controls are realized only near the viewport and derealized outside a
  wider overscan band.
- `ImageBox` instances unsubscribe load-completion handlers on unload to avoid
  zombie rebuilds.
- Cursor objects are cached and disposed on unload.

