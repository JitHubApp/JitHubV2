# Quick start

`MarkdownRenderer` is the lean WinUI viewer package. It includes the native
CommonMark viewer and immutable document API; add only the feature packs your app
uses.

## Install

```powershell
dotnet add package MarkdownRenderer
```

Optional packages include `MarkdownRenderer.Gfm`, `MarkdownRenderer.GitHub`,
`MarkdownRenderer.Html`, `MarkdownRenderer.Math`, `MarkdownRenderer.Mermaid`,
`MarkdownRenderer.Svg.ThorVG`, and
`MarkdownRenderer.SyntaxHighlighting.TextMate`. Add
`MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common` for the curated
language set or `.Grammars.All` for the complete pinned corpus. The integration
package itself contains no grammar or native regular-expression payload.

For trim- and NativeAOT-safe syntax highlighting, construct the provider
explicitly:

```csharp
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

var provider = new CommonTextMateGrammarProvider();
var highlighter = new TextMateCodeBlockSyntaxHighlighter(
    provider,
    options: null,
    ownsProvider: true);
MarkdownView.UseTextMateSyntaxHighlighting(highlighter);
```

Dispose `highlighter` when the view/host lifetime ends; it cancels outstanding
work and then disposes the owned provider. Do not dispose `provider` separately
in this ownership mode. Constructors without `ownsProvider: true` borrow the
supplied provider instead; in that mode, keep the provider alive while the view
uses it and dispose it from the owning host/service afterward. Inputs outside
the configured code/line/span budgets render as ordinary unhighlighted code.

## Choose the viewport owner

Use `MarkdownScrollView` when the markdown viewer should own vertical scrolling:

```xml
<md:MarkdownScrollView
    x:Name="MarkdownView"
    Markdown="{x:Bind ViewModel.Markdown, Mode=OneWay}" />
```

Use `MarkdownDocumentView` when a page, workspace, or another ancestor already
owns the scroll viewport:

```xml
<ScrollViewer>
    <md:MarkdownDocumentView
        x:Name="MarkdownDocument"
        Markdown="{x:Bind ViewModel.Markdown, Mode=OneWay}" />
</ScrollViewer>
```

Do not nest `MarkdownScrollView` inside another vertical `ScrollViewer`.
`MarkdownRendererControl` remains only as an obsolete compatibility type.

## Parse once and reuse immutable documents

```csharp
using MarkdownRenderer;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;

using MarkdownEngine engine = new MarkdownEngineBuilder().Build();
MarkdownDocument document = await engine.ParseAsync(markdownSource);

var view = new MarkdownScrollView
{
    Engine = engine,
    Document = document,
};
```

`MarkdownEngine` has immutable configuration and thread-safe parse coordination.
Custom extension callbacks can run concurrently and must make their own captured
state thread-safe. `ParseAsync` returns an immutable
`MarkdownDocument`, which can be queried or shared by multiple views. Assigning
`Markdown` is still convenient for one-off content; assigning `Document` makes
parse ownership and reuse explicit.

## Opt into GFM or the GitHub README profile

Strict GFM:

```csharp
using MarkdownRenderer.Gfm;

using MarkdownEngine engine = new MarkdownEngineBuilder()
    .UseGitHubFlavoredMarkdown()
    .Build();

var view = new MarkdownScrollView().UseGitHubFlavoredMarkdown(engine);
view.Document = await engine.ParseAsync(markdownSource);
```

For GitHub README additions such as alerts, footnotes, emoji, generic attributes,
and the native safe-HTML subset, reference `MarkdownRenderer.GitHub` and use
`UseGitHubReadme()`. The base renderer follows WinUI/Fluent styling by default;
GitHub-specific parsing and presentation are opt-in.

The builder is also available when constructing a view in code:

```csharp
var view = new MarkdownRendererControlBuilder()
    .WithEngine(engine)
    .WithMarkdown(markdownSource)
    .WithSelectionEnabled(true)
    .BuildScrollView();
```

Call `BuildDocumentView()` instead when an ancestor owns scrolling. `Build()` is
obsolete for the same reason as the legacy control type.

## Query a document

```csharp
foreach (var heading in document.GetHeadings())
{
    Debug.WriteLine($"H{heading.Level}: {heading.DisplayText}");
}

var links = document.GetLinks();
var codeBlocks = document.GetCodeBlocks();
var images = document.GetImages();
var diagnostics = document.Diagnostics;
```

## Copy selection

Keyboard and context-menu copy write rendered semantic text as plain text and a
formatted `CF_HTML` payload by default. Exact source markdown is an explicit
alternative:

```csharp
using MarkdownRenderer.Selection;

MarkdownView.CopySelectionToClipboard(new MarkdownCopyOptions
{
    PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
    IncludeHtml = true,
});
```

`CopySelectionAsMarkdown()` is the convenience action for that source-copy path.

## Next steps

- [Public API](public-api.md)
- [Packages and distribution](packaging-and-distribution.md)
- [Supported markdown](supported-markdown.md)
- [Theming and customization](theming-and-customization.md)
- [Extensibility API](extensibility-api.md)
