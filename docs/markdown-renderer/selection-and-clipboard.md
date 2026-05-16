# Selection and clipboard

Selection is modeled as document positions over the renderer's layout tree. The
goal is DOM-like interaction: users should select arbitrary rendered markdown and
copy the markdown source that produced that rendered range.

## Main types

| Type | Purpose |
| --- | --- |
| `DocumentPosition` | A point in rendered markdown: block index, inline index, character offset. |
| `DocumentRange` | Start/end pair with normalization support. |
| `SelectionController` | Owns the active range and produces highlight rectangles. |
| `MarkdownSourceMap` | Maps rendered ranges back to source spans. |
| `MarkdownClipboardWriter` | Writes the selected source markdown to the system clipboard. |

## Interaction model

Pointer press hit-tests the layout snapshot. If text is hit:

- single click sets the selection anchor;
- double click expands to a word;
- triple click expands to a block/line;
- pointer move extends the range;
- pointer release finalizes the selection.

Keyboard handling includes link focus traversal and copy behavior. Right-click
context menu support includes selection actions such as Select All and Copy.

## Why selection is on the XAML overlay

Early selection implementations invalidated the Win2D canvas. At fractional DPI,
frequent regional repaint of DirectWrite text could cause visible glyph jitter.
The current design renders selection rectangles as pooled XAML `Rectangle`
elements on the overlay.

That means pointer-move selection updates:

- do not repaint DirectWrite text;
- do not call `_canvas.Invalidate()`;
- mutate existing overlay rectangles instead of rebuilding the visual tree.

## Source-preserving copy

The control does not copy rendered text by default. It copies the exact markdown
source slice mapped to the selected rendered range:

```text
Rendered selection:    bold word
Copied markdown:       **bold word**
```

This is implemented by recording `SourceSpan` values while building runs and then
using `MarkdownSourceMap.Slice(range)` in `MarkdownClipboardWriter`.

## Current coverage

Implemented:

- text hit-testing through `CanvasTextLayout` metrics;
- selection across text blocks;
- word and block expansion;
- source-accurate copy;
- selection adorner fill rectangles derived from WinUI's native
  `TextControlSelectionHighlightColor` resource, pre-composited over the
  renderer surface when the native brush is translucent so previously painted
  glyphs do not show through;
- selected glyph foreground overpainted from WinUI's native selected-text
  foreground resource on the same single viewport-sized Win2D adorner surface;
- no per-drag XAML child creation/resizing and no document-canvas invalidation
  while dragging selection;
- app-wide dismissal: pointer/focus movement outside the renderer clears the
  selection, and active selections are coordinated across renderer instances;
- selection automation that verifies real `sel-anchor`, `sel-extend`, and
  `sel-rect-phys` diagnostics;
- no canvas repaint during selection drag on Embeds sample.

## Embedded WinUI controls and selection

Hosted WinUI controls currently participate visually and interactively, but they
are not fully part of text selection. The intended design is:

1. treat each embed as an atomic document slot;
2. make `EmbedBox.HitTest()` return a real `DocumentPosition`;
3. have `EmbedBox.GetSelectionRects()` return the embed bounds when selected;
4. use a transparent drag shield above embeds only during selection drag so WinUI
   child controls do not steal pointer-move events;
5. copy the original markdown source span for the embed.

Normal click behavior should continue to go to the hosted control. The drag
shield should only activate after a selection drag starts.

## Selection gaps

- Embedded WinUI controls are not fully selectable yet.
- Dragging beyond the viewport does not auto-scroll.
- Copy-as-HTML and rendered plain-text copy are not implemented.
- Inline images are represented as text fallback, so image selection is not
  semantically correct yet.

