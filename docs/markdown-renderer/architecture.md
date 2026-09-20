# Architecture

MarkdownRenderer separates parse ownership from view ownership.

```text
MarkdownEngineBuilder
  -> immutable MarkdownEngine
  -> ParseAsync(source)
  -> immutable MarkdownDocument
  -> MarkdownScrollView or MarkdownDocumentView
  -> native Win2D/DirectWrite paint plus a WinUI interaction layer
```

## Public layers

| Layer | Responsibility |
| --- | --- |
| `MarkdownRenderer.Core` | Profiles, immutable engines/documents, diagnostics, UTF-16 source maps, and declarative extensions. |
| `MarkdownRenderer` | Native WinUI views, Fluent defaults, selection, accessibility, images, host services, and rendering. |
| Feature packs | GFM, GitHub README behavior, safe HTML, Math, Mermaid, isolated resvg SVG, and TextMate syntax highlighting. |
| Host application | Viewport composition, URI/image policy, commands, localization, optional hosted elements, and selected feature packs. |

## Viewport ownership

`MarkdownScrollView` creates and owns its vertical viewport. It is appropriate
for a standalone document surface. `MarkdownDocumentView` creates no internal
vertical `ScrollViewer`; it observes the effective viewport supplied by the page
or workspace shell. This avoids nested scrolling and lets a native app keep page
layout stable.

## Immutable configuration

`MarkdownEngineBuilder` is mutable only while configuration is being assembled.
`Build()` creates a frozen engine containing a profile, extension set, and parse
cache budget. The engine can parse concurrently. A resulting
`MarkdownDocument` preserves source text, semantic queries, diagnostics, and
half-open UTF-16 source ranges and can be shared by several views.
Dispose ordinary engines when their application scope ends. Only the built-in
process-wide shared engines ignore disposal; derived engines own independent
feature-pack resources and are always disposable.

## Declarative extension boundary

An `IMarkdownExtension` configures `MarkdownExtensionBuilder` with features and
exact syntax-kind renderers. A renderer receives a `MarkdownSyntaxNode` and emits
`MarkdownContent`. The native view translates that content into its private
layout and paint representation. This boundary keeps extensions deterministic,
AOT-friendly, and independent of Win2D implementation details.

## Native view internals

The viewer paints document content through Win2D/DirectWrite and uses WinUI for
input, focus visuals, commands, and viewport-realized hosted elements. Long
documents extend measured regions around the effective viewport, while immutable
document semantics remain available independently of visual realization.

Internal layout, paint, and compatibility adapter types are not public
extension contracts and should not appear in application code.
