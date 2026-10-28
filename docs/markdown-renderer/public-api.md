# Public API

The supported API is organized around immutable parsing, explicit viewport
ownership, declarative extensions, and host services. Layout and painting types
are implementation details.

## Packages

| Package | Purpose |
| --- | --- |
| `MarkdownRenderer` | The viewer: immutable engines, documents, profiles, diagnostics, UTF-16 source maps, declarative extension contracts, the opt-in profiles and safe HTML listed below, the optional performance session, and the native WinUI views. |
| `MarkdownRenderer.Math` | CSharpMath-based TeX processor, immutable vector scenes, fallback and accessibility. |
| `MarkdownRenderer.Mermaid` | Selected-RID Merman engine, validated MMIR scenes, bounded processing and fallback. |
| `MarkdownRenderer.Svg.Resvg` | Optional isolated resvg static-SVG provider and RID workers. |
| `MarkdownRenderer.SyntaxHighlighting.TextMate` | Lean TextMate integration and provider contracts; grammar packs are separate. |
| `MarkdownRenderer.All` | Explicit, deliberately large meta-package for every feature and payload. |

Most apps should install `MarkdownRenderer` and only the packs they need.

The `MarkdownRenderer` assembly groups its opt-in areas by namespace:

| Namespace | Purpose |
| --- | --- |
| `MarkdownRenderer.Gfm` | Strict GFM 0.29 plus separately opt-in Markdown Extra helpers. |
| `MarkdownRenderer.GitHub` | Opt-in GitHub README profile: GFM plus alerts, footnotes, emoji, attributes, and safe HTML; versioned Primer visual comparison profiles. |
| `MarkdownRenderer.Html` | Bounded native safe-HTML subset parser/painter and immutable options. |
| `MarkdownRenderer.Performance` | Optional host-owned progressive preparation session; the WinUI controls borrow it through the engine-neutral session contract. |

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

`ICodeHighlighter` is the syntax-highlighting contract. Its revision and
callbacks can be queried concurrently for different blocks and controls;
implementations and captured state must be thread-safe and UI-context
independent. Cancellation arrives only through the `HighlightAsync` token.

## Views

| Type | Use when |
| --- | --- |
| `MarkdownScrollView` | The markdown component owns the vertical viewport. |
| `MarkdownDocumentView` | An ancestor page or workspace owns scrolling and supplies the effective viewport. |

Both views expose `Markdown`, `Document`, `Engine`, `StyleSheet`, `Theme`, image
policy, selection, code highlighting, commands, localization, and hosted-element
services through their shared abstract base, `MarkdownRendererControl`. Only
the two views derive from it; apps construct a view.

A view releases its Win2D resources when it is unloaded, its window closes, or
the app disposes it. `Dispose()` returns once teardown is done; some native
resources are freed shortly after. Apps that must know when they are gone, for
example before shutting down shared services, await `DisposeAsync()` on the UI
thread. It throws if teardown could not finish, and its task faults if a
resource could not be released. The `Disposed` event reports teardown that the
view's window started.

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

For the opt-in progressive image pipeline, create one `MarkdownPerformanceSession`
per account or security partition, and share it between
views. Controls borrow it; the host disposes it after those views stop using it:

```csharp
using MarkdownRenderer.Performance;

var performance = new MarkdownPerformanceSession(
    MarkdownPerformanceOptions.Progressive);
var view = new MarkdownRendererControlBuilder()
    .WithEngine(engine)
    .WithPerformanceSession(performance)
    .WithMarkdown(source)
    .BuildScrollView();

// ... keep the view alive while the host uses it ...
// After every view borrowing the session has unloaded:
await performance.DisposeAsync();
```

This currently shares bounded image-source retention, reserves fetch capacity
for visible images, decodes ordinary rasters to display size, and enables
directional viewport preparation and off-thread image reflow. A null session
keeps the established behavior. `GetSnapshot()` reports aggregate counters
without URLs or image bytes, and `Trim()` drops retained source bytes. See
[Current gaps and roadmap](current-gaps-and-roadmap.md) for the remaining 1.0 gates.

Lease-aware image resolvers that start detached fallback or hedge work can
probe their source admission for `IMarkdownImageSourceWorkTracker` and register
a source-free completion task before starting that work. The task must cover
actual operation completion and owned-result cleanup, not just cancellation.
The session includes it in asynchronous shutdown even when the branch never
reserved image bytes, observes faults without surfacing their messages, and
rejects registrations after the resolver call has returned.

## Themes and host services

The base visual design follows WinUI/Fluent resources, including light, dark,
high-contrast, text-scale, and flow-direction changes. The GitHub README profile
and GitHub-themed presentation are opt-in through `UseGitHubReadme()` and the
`MarkdownRenderer.GitHub` namespace; applications can further customize
roles through `MarkdownStyleSheet`, `MarkdownStyleRole`, and
`MarkdownResourceKeys`.

Stable host contracts include:

- `IImageResolver` / `IMarkdownImageResolver` for application image policy;
- `IMarkdownSvgRenderer` for an explicitly registered, borrowed SVG provider;
- `ICodeHighlighter` for asynchronous code highlighting;
- `IMarkdownStringProvider` for localization;
- `IMarkdownCommandProvider` for target-aware actions;
- `IMarkdownHostedElementFactory` for viewport-aware WinUI elements requested
  by declarative extensions.

For `IMarkdownSvgRenderer.OpenAsync`, keep the memory backing
`MarkdownSvgOpenRequest.SanitizedSvgBytes` alive and unchanged until the
asynchronous operation actually completes (also on failure or cancellation).
A pending `ValueTask` returned from the call is not completion. Providers snapshot
the input by completion; callers can then reuse their input memory. Controls
dispose their SVG document handles, not the shared provider. Resvg retains its
private source through admitted renders and cancellation callbacks before
document retirement releases it; this lifetime contract is not an aggregate
source-memory cap.

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

## Changing the public API

Every package that ships an assembly declares its public and protected surface
in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` next to its project
file, checked by
[`Microsoft.CodeAnalysis.PublicApiAnalyzers`](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md).
Nothing has shipped yet, so every entry is in `PublicAPI.Unshipped.txt`. The
build fails when a public symbol is not declared (RS0016), a declared symbol no
longer exists (RS0017), or an entry lacks its nullability annotations (RS0036);
`MarkdownRenderer/src/PublicApiAnalyzers.globalconfig` sets these severities.
It also fails when a shipping project lacks either file
(`MarkdownRenderer/src/Directory.Build.targets`).

To accept an intentional change, apply the RS0016 code fix in the IDE, or run
from the repository root:

```powershell
dotnet format analyzers MarkdownRenderer\src\MarkdownRenderer\MarkdownRenderer.csproj --diagnostics RS0016 --severity warn
```

For a removed or changed symbol, delete or edit its old line in
`PublicAPI.Unshipped.txt` by hand; RS0017 has no code fix. Review the diff of the
API files as a public compatibility decision.

`MarkdownRenderer.PublicApi.Tests` checks design rules the API files cannot
express: public signatures expose no third-party or renderer-internal types,
structs are readonly with default layout, classes are sealed or static, and
`IEquatable<T>` is paired with an `Equals(object)` override.

Once a version has shipped, binary compatibility beyond member signatures
(base types, sealing, attributes, enum storage) is checked by the .NET SDK's
package validation against that version; see the
[release checklist](release-checklist.md#api-and-documentation).

## Preview status

The packages are still previews. These implementations exist, but full 1.0 conformance, API,
accessibility, packaging, and release gates have not all passed.
