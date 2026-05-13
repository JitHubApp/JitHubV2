# Overview and philosophy

`MarkdownRenderer` exists to provide a markdown experience that feels like a
native Windows 11 control rather than a web view embedded in a WinUI app.

The target experience is:

- native Windows typography, colors, spacing, focus visuals, cursors, and theme
  behavior;
- DirectWrite-quality text rendering through Win2D;
- GitHub-flavored markdown support via Markdig extensions;
- DOM-like text selection and source-preserving copy;
- real WinUI controls embedded inside markdown blocks or inline flows;
- good accessibility and RTL support;
- high throughput without blocking paint during parse, layout, scrolling, click,
  hover, or selection operations;
- minimal dependencies and a standalone project named `MarkdownRenderer`.

## Design principles

### Native first

The control is intentionally not a WebView. Native rendering gives the host app
better integration with WinUI theme resources, focus behavior, input routing,
high-DPI rendering, UI Automation, app packaging, and memory policy.

### Source markdown remains the document of record

The renderer lays out visual boxes, but the markdown source remains important.
Selection copy is source-preserving: copying selected rendered text should copy
the markdown source range that produced it, not a lossy rendered string.

### Paint should be cheap and predictable

Text is painted through `CanvasVirtualControl` regional invalidation. Selection is
drawn on a XAML overlay, not by repainting DirectWrite glyphs during every drag
move. Hover state must not mutate `CanvasTextLayout` or invalidate canvas tiles
unless a real visual paint change is required.

### Extensibility should be explicit and AOT-safe

Custom markdown renderers are registered by concrete Markdig node type in
`MarkdownExtensionRegistry`. The registry uses direct dictionary lookup instead
of reflection-heavy discovery so the library remains compatible with trimming
and Native AOT analysis.

### Hosted controls are real controls

When markdown needs a button, checkbox, text box, or app-specific widget, the
renderer hosts a real `FrameworkElement` on a transparent overlay. These controls
participate in normal WinUI input and accessibility behavior, while the renderer
owns their layout slot and virtualization lifecycle.

### Accessibility is part of the architecture, not an afterthought

The current implementation exposes a UIA `Document` peer, block peers, link invoke
peers, accessible image text, and keyboard navigation. Full maturity still needs
Text pattern, table pattern, list roles, heading-level exposure, and richer range
navigation.

## Non-goals

- A full HTML engine. Raw HTML is not currently rendered as HTML and should be
  treated as a future feature requiring a clear sanitization policy.
- Cross-platform rendering. The library is Windows/WinUI-specific.
- CSS compatibility. The theme model is native and object-based, not CSS.
- Browser-perfect markdown behavior at the cost of native behavior. GitHub
  compatibility is important, but native app behavior wins when tradeoffs exist.

