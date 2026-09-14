# Public API

The supported API is organized around immutable parsing, explicit viewport
ownership, declarative extensions, and host services. Layout and painting types
are implementation details.

## Packages

| Package | Purpose |
| --- | --- |
| `MarkdownRenderer.Core` | Immutable engines, documents, profiles, diagnostics, UTF-16 source maps, and declarative extension contracts; no WinUI or native payload. |
| `MarkdownRenderer` | Lean convenience package containing Core plus the native WinUI viewer. |
| `MarkdownRenderer.Gfm` | Strict GFM 0.29 plus separately opt-in Markdown Extra helpers. |
| `MarkdownRenderer.GitHub` | Opt-in GitHub README profile: GFM plus alerts, footnotes, emoji, attributes, and safe HTML. |
| `MarkdownRenderer.Html` | Bounded native safe-HTML subset parser/painter and immutable options. |
| `MarkdownRenderer.Math` | CSharpMath-based TeX processor, immutable vector scenes, fallback and accessibility. |
| `MarkdownRenderer.Mermaid` | Selected-RID Merman engine, validated MMIR scenes, bounded processing and fallback. |
| `MarkdownRenderer.Svg.ThorVG` | Optional native ThorVG SVG rasterizer assets. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate` | Lean TextMate integration and provider contracts; grammar packs are separate. |
| `MarkdownRenderer.All` | Explicit, deliberately large meta-package for every feature and payload. |

Most apps should install `MarkdownRenderer` and only the packs they need.

## Engines and documents

```csharp
using MarkdownEngine engine = new MarkdownEngineBuilder()
    .UseProfile(MarkdownProfiles.CommonMark)
    .WithParseCacheBudgetBytes(16 * 1024 * 1024)
    .WithParseLimits(new MarkdownParseLimits(
        maximumSourceLength: 4 * 1024 * 1024,
        maximumConcurrentParseCount: 4,
        maximumOutstandingParseCount: 16,
        maximumOutstandingSourceBytes: 64L * 1024 * 1024))
    .Build();

MarkdownDocument document = await engine.ParseAsync(source, cancellationToken);
```

`MarkdownEngine` configuration and `MarkdownDocument` values are immutable;
engine admission and caches are thread-safe. Custom extension callbacks may run
concurrently and remain responsible for the safety of captured services.
An engine admits bounded concurrent/outstanding unique work, deduplicates
same-source in-flight work, and reuses completed documents within its configured
cache budget. Dispose every ordinary engine. The built-in `MarkdownEngine.Default` and GFM/GitHub
shared engines explicitly ignore disposal so a consumer cannot poison a global
singleton. Every ordinary engine becomes unusable after disposal, even if it
owns no optional service. `UseExtension()` borrows stateless or externally owned
extension state. Stateful feature packs can use `UseOwnedExtensionFactory()` and
`MarkdownOwnedExtension` to store a reusable per-engine factory rather than a
one-shot service instance: a builder or frozen extension set can create fresh,
independently disposable engines before or after earlier engines are disposed.
Documents expose source text,
diagnostics, source-map entries, and stable queries such as `GetHeadings()`,
`GetLinks()`, `GetCodeBlocks()`, and `GetImages()`. Extension hosts can use
`GetBlockExtensionContent()` or `GetInlineExtensionContent()` to retrieve every
fragment associated with a source range; this preserves nested syntax nodes
whose half-open UTF-16 ranges are identical.

`ICodeHighlighter` is the canonical syntax-highlighting contract. Its revision
and callbacks can be queried concurrently for different blocks and controls;
implementations and captured state must be thread-safe and UI-context independent.
`ICodeBlockSyntaxHighlighter` remains an obsolete compatibility alias and is
adapted to the same single provider backing without sync-over-async blocking.

## Views

| Type | Use when |
| --- | --- |
| `MarkdownScrollView` | The markdown component owns the vertical viewport. |
| `MarkdownDocumentView` | An ancestor page or workspace owns scrolling and supplies the effective viewport. |

Both views expose `Markdown`, `Document`, `Engine`, `StyleSheet`, `Theme`, image
policy, selection, code highlighting, commands, localization, and hosted-element
services. `MarkdownRendererControl` is obsolete compatibility surface; new code
should not derive from or construct it.

GFM, GitHub README, Markdown Extra, and safe-HTML helpers freeze their native
presentation registrations into the engine. Parsed documents retain that
snapshot internally, so assigning a reusable document to a bare view preserves
the same tables, tasks, alerts, and safe-HTML policy without rebuilding registries.

```csharp
var view = new MarkdownRendererControlBuilder()
    .WithEngine(engine)
    .WithMarkdown(source)
    .WithSelectionEnabled(true)
    .BuildScrollView();
```

Use `BuildDocumentView()` for an ancestor-owned viewport. The builder's `Build()`
method is obsolete.

## Themes and host services

The base visual design follows WinUI/Fluent resources, including light, dark,
high-contrast, text-scale, and flow-direction changes. The GitHub profile and
GitHub-themed presentation are opt-in through `MarkdownRenderer.GitHub`;
applications can further customize
roles through `MarkdownStyleSheet`, `MarkdownStyleRole`, and
`MarkdownResourceKeys`.

Stable host contracts include:

- `IImageResolver` / `IMarkdownImageResolver` for application image policy;
- `ICodeHighlighter` for asynchronous code highlighting;
- `IMarkdownStringProvider` for localization;
- `IMarkdownCommandProvider` for target-aware actions;
- `IMarkdownHostedElementFactory` for viewport-aware WinUI elements requested
  by declarative extensions.

## Clipboard

`CopySelectionToClipboard()` uses `MarkdownCopyOptions.Default`: rendered
semantic text plus `CF_HTML`. Set `PlainTextMode` to `SourceMarkdown`, or call
`CopySelectionAsMarkdown()`, when exact markdown source is the desired explicit
action. `IncludeHtml` defaults to `true`.

## Extension boundary

Extensions implement `IMarkdownExtension` and configure a
`MarkdownExtensionBuilder`. They register exact, stable syntax-kind strings and
return declarative `MarkdownContent` through `MarkdownNodeRenderer` delegates.
They do not receive the viewer's internal layout tree or paint context.

The WinUI adapter consumes block and inline text, code, images, links, lists,
tables, containers, hosted elements and registered custom scene primitives.
Unsupported fragments retain atomic fallback to built-in rendering.

See [Extensibility API](extensibility-api.md) for the supported model.

## Preview status

The packages are still previews. These implementations exist, but full 1.0 conformance, API,
accessibility, packaging, and release gates have not all passed.
