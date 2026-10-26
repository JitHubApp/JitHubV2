# MarkdownRenderer documentation

MarkdownRenderer is a native WinUI markdown viewer with immutable parsing,
explicit viewport ownership, Fluent defaults, and opt-in feature packs. Its
correctness goal closed on October 25, 2026, except for two feature rows that
need ARM64 hardware. It is still a preview: the 1.0 distribution and
release gates are unfinished.

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
| Review the correctness closure, candidates and evidence hashes | [October 25 correctness closure](correctness-closure-2026-10-25.md) |
| Review the correctness cases and defect history | [Correctness goal and phase plan](correctness-plan.md) |
| Understand the browser-free Phase 3 audit input | [Phase 3 correctness input design](phase3-correctness-input-design.md) |
| Inspect portable receipts and the frozen audit manifest | [October 17 evidence index](evidence/2026-10-17/README.md) |

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
implemented.

- Phase 1 (core correctness) is closed.
- Phase 3 (the 500-README audit) is complete.
- Phase 2 (feature correctness) passes 30 of 32 rows. The other 2 need ARM64
  hardware.

The separate distribution checklist covers API, AOT, architecture, packaging
and publishing. Historical browser-parity and performance targets are not
correctness gates. Correctness closure is not release certification.
