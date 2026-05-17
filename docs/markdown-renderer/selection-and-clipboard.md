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
| `MarkdownClipboardWriter` | Writes selected source markdown plus an HTML clipboard format. |
| `MarkdownCopyOptions` | Lets callers opt into rendered plain-text copy while preserving the default source-markdown behavior. |

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
using `MarkdownSourceMap.Slice(range)` in `MarkdownClipboardWriter`. The same
copy operation also places a `CF_HTML` payload on the clipboard for paste targets
that prefer formatted content.

Callers that need semantic rendered text can opt in through the public copy API:

```csharp
MarkdownView.CopySelectionToClipboard(new MarkdownCopyOptions
{
    PlainTextMode = MarkdownPlainTextCopyMode.RenderedText,
});
```

`IncludeHtml` defaults to `true` and can be disabled for consumers that need a
plain-text-only clipboard operation.

## Current coverage

Implemented:

- text hit-testing through `CanvasTextLayout` metrics;
- selection across text blocks;
- word and block expansion;
- source-accurate plain-text copy plus formatted HTML clipboard data;
- opt-in rendered plain-text copy through `MarkdownCopyOptions`;
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

Hosted WinUI controls participate visually, interactively, and as atomic
selection slots:

1. treat each embed as an atomic document slot;
2. make `EmbedBox.HitTest()` return a real `DocumentPosition`;
3. have `EmbedBox.GetSelectionRects()` return the embed bounds when selected;
4. use a transparent drag shield above embeds only during selection drag so WinUI
   child controls do not steal pointer-move events;
5. copy the original markdown source span for the embed.

Normal click behavior should continue to go to the hosted control. The drag
shield should only activate after a selection drag starts.

## Manual smoke

Before a release, verify source text, rendered text, and HTML clipboard formats
in Notepad, Word, Outlook, browser text fields, and any host app that consumes
the control.
