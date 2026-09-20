# Rendering pipeline

## Stages

1. Configure and freeze a `MarkdownEngine` with `MarkdownEngineBuilder`.
2. Parse source asynchronously into an immutable `MarkdownDocument`.
3. Assign the document to `MarkdownScrollView` or `MarkdownDocumentView`.
4. Resolve the current Fluent environment, application style sheet, images,
   code-highlighting service, and enabled feature-pack capabilities.
5. Build and measure private native layout for the effective viewport.
6. Commit a view snapshot on the UI thread and paint through Win2D/DirectWrite.
7. Realize WinUI interaction elements only where the effective viewport needs
   them.

Assigning `Markdown` asks the view's engine to perform steps 2-7 for convenience.
Assigning a previously parsed `Document` skips repeated parse ownership and makes
sharing explicit.

## Feature composition

The default engine uses the CommonMark profile. Optional packs add profiles or
services without changing the base package's dependency footprint:

- `MarkdownRenderer.Gfm` for strict GFM and opt-in Markdown Extra;
- `MarkdownRenderer.GitHub` for the GitHub README profile;
- `MarkdownRenderer.Html` for bounded native safe-HTML rendering;
- `MarkdownRenderer.Math` and `MarkdownRenderer.Mermaid` for bounded native
  processing, vector scenes and fallbacks;
- `MarkdownRenderer.Svg.Resvg` for isolated static SVG rasterization;
- `MarkdownRenderer.SyntaxHighlighting.TextMate` plus a selected grammar pack.

Math uses CSharpMath-based typesetting, while Mermaid uses a selected-RID Merman
engine and validated MMIR scenes. Safe HTML uses a restricted native parser and
painter. No browser or JavaScript runtime is introduced by these packs.

## Source and diagnostics

Source ranges use half-open UTF-16 offsets. The immutable document retains
semantic query results, diagnostics, and source-map entries regardless of which
visual bands are currently realized. Render or capability failures should be
reported through diagnostics/events and fall back to usable text or source
content instead of executing browser behavior.
