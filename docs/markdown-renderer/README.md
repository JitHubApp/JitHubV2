# MarkdownRenderer documentation

MarkdownRenderer is a native WinUI markdown viewer with immutable parsing,
explicit viewport ownership, Fluent defaults, and opt-in feature packs. It is a
preview: the Math and Mermaid engines are implemented, while full 1.0
certification and the remaining release gates are unfinished.

## Start here

| Need | Read |
| --- | --- |
| Install and render a document | [Quick start](quick-start.md) |
| Choose `MarkdownScrollView` or `MarkdownDocumentView` | [Architecture](architecture.md) |
| Review supported consumer types | [Public API](public-api.md) |
| Choose optional packages | [Packaging and distribution](packaging-and-distribution.md) |
| Review syntax and feature status | [Supported markdown](supported-markdown.md) |
| Author a declarative extension | [Extensibility API](extensibility-api.md) |
| Customize Fluent styling | [Theming and customization](theming-and-customization.md) |
| Understand normal Copy vs Copy Markdown | [Selection and clipboard](selection-and-clipboard.md) |
| Track unfinished work | [Current gaps and roadmap](current-gaps-and-roadmap.md) |
| Review the proposed opt-in performance work | [Progressive performance plan](progressive-performance-plan.md) |

For the complete documentation table of contents, see [Summary](SUMMARY.md).

## Package model

`MarkdownRenderer.Core` contains immutable engines and documents without WinUI.
The `MarkdownRenderer` convenience package is deliberately lean: Core plus the
native WinUI viewer. GFM, the GitHub README profile, safe HTML, Math, Mermaid,
resvg SVG rasterization, TextMate integration, and grammar resources are
optional packages. `MarkdownRenderer.All` is an explicit opt-in to every feature
and payload.

## Recommended API shape

```csharp
using MarkdownEngine engine = new MarkdownEngineBuilder().Build();
MarkdownDocument document = await engine.ParseAsync(source);

var view = new MarkdownScrollView
{
    Engine = engine,
    Document = document,
};
```

Use `MarkdownDocumentView` inside an ancestor-owned scroll surface.
`MarkdownRendererControl` is obsolete compatibility surface.

The base presentation follows WinUI/Fluent resources. The GitHub profile and any
GitHub-themed presentation are opt-in through `MarkdownRenderer.GitHub`.

Normal Copy writes rendered semantic text and `CF_HTML`. Exact source markdown
is available through the explicit Copy Markdown path.

## Preview boundary

Safe HTML, native vector Math, Mermaid and declarative viewer adapters are
implemented. This is not release certification: conformance, API, AOT,
accessibility, architecture, packaging, and
publishing gates must pass before 1.0.
