# Supported markdown

Support is compositional. Installing the lean `MarkdownRenderer` package does
not implicitly enable every syntax or native payload.

| Area | Package/profile | Current status |
| --- | --- | --- |
| CommonMark document/viewer baseline | `MarkdownRenderer` | Native headings, paragraphs, emphasis, code, quotes, lists, rules, links, images, selection, queries, and accessibility. Full conformance certification is still a 1.0 gate. |
| Strict GFM 0.29 | `MarkdownRenderer.Gfm` + `UseGitHubFlavoredMarkdown()` | Tables, task lists, strikethrough, autolinks, and related native rendering. |
| Markdown Extra | `MarkdownRenderer.Gfm` + `UseMarkdownExtra()` | Separate opt-in for definitions, abbreviations, figures, and extra inline forms. |
| GitHub README profile | `MarkdownRenderer.GitHub` + `UseGitHubReadme()` | GFM plus alerts, footnotes, emoji, generic attributes, and safe-HTML composition. The GitHub profile/theme is opt-in; Fluent remains the base visual default. |
| Safe HTML | `MarkdownRenderer.Html` | Bounded native subset parser and painter, including inline tags and cross-block scopes. No script, CSS layout, browser DOM, or direct I/O capability. |
| Math | `MarkdownRenderer.Math` | CSharpMath-based native vector typesetting, dollar delimiters, immutable scenes, accessibility, and lossless invalid-source fallback. |
| Mermaid | `MarkdownRenderer.Mermaid` | Selected-RID Merman engine, validated MMIR vector scenes, bounded processing and fallback. ELK is excluded. |
| SVG | `MarkdownRenderer.Svg.ThorVG` | Optional native ThorVG 1.1.1 rasterization payload for supported Windows architectures. Static geometry, text, gradients, patterns, masks, clips, reuse, transforms, and Gaussian blur are supported; unsupported filter primitives use the atomic accessible fallback. |
| Syntax highlighting | `MarkdownRenderer.SyntaxHighlighting.TextMate` | Optional provider integration. Choose a separate grammar pack or provide your own grammar source. |

## Configure profiles

```csharp
using MarkdownEngine commonMark = new MarkdownEngineBuilder().Build();

using MarkdownEngine gfm = new MarkdownEngineBuilder()
    .UseGitHubFlavoredMarkdown()
    .Build();

using MarkdownEngine githubReadme = new MarkdownEngineBuilder()
    .UseGitHubReadme()
    .Build();
```

Profiles and extension sets are immutable after `Build()`. A view should use the
same engine that produced its assigned `MarkdownDocument`.

## Fallback policy

Unsupported, disabled, unavailable, timed-out, or invalid optional content must
degrade to safe semantic text or original source content. Engine implementation
does not imply full syntax parity or completion of the release-validation matrix.
