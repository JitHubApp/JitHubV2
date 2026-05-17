# Quick start

This page shows the recommended consumer shape for the packaged renderer. For a
complete feature matrix, see [Supported markdown](supported-markdown.md).

## Add the project

Install the core package and, for the recommended GitHub-flavored markdown path,
the GFM package:

```powershell
dotnet add package MarkdownRenderer
dotnet add package MarkdownRenderer.Gfm
```

When developing from this repository, reference the projects directly:

```xml
<ProjectReference Include="..\MarkdownRenderer\MarkdownRenderer.csproj" />
<ProjectReference Include="..\MarkdownRenderer.Gfm\MarkdownRenderer.Gfm.csproj" />
```

The library targets:

- `net10.0-windows10.0.26100.0`
- minimum Windows platform `10.0.19041.0`
- WinUI / Windows App SDK

## Create the control in XAML

```xml
<Page
    x:Class="Sample.MarkdownPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:md="using:MarkdownRenderer.Controls">

    <md:MarkdownRendererControl
        x:Name="MarkdownView"
        IsSelectionEnabled="True" />
</Page>
```

## Create a GFM renderer

```csharp
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Theming;

var view = GfmMarkdownRenderer.CreateDefault("""
# Hello MarkdownRenderer

- [x] Task lists
- Tables
- Footnotes[^1]

[^1]: Footnote definitions are rendered and linked.
""");
```

The fluent builder exposes the same setup path:

```csharp
var view = new MarkdownRendererControlBuilder()
    .UseGitHubFlavoredMarkdown()
    .UseMarkdownExtra()
    .WithMarkdown(markdownSource)
    .WithTheme(new MarkdownTheme())
    .WithSelectionEnabled(true)
    .Build();
```

Core CommonMark-only setup stays in the core package:

```csharp
var view = MarkdownRendererControl.CreateDefault(markdownSource);
```

## Query the parsed document

```csharp
foreach (var heading in view.Document.GetHeadings())
{
    var level = heading.Level;
    var text = heading.DisplayText;
    var sourceSpan = heading.SourceSpan;
}

var links = view.Document.GetLinks();
var codeBlocks = view.Document.GetCodeBlocks();
var images = view.Document.GetImages();
var footnotes = view.Document.GetFootnotes();
var definitions = view.Document.GetDefinitionItems();
var abbreviations = view.Document.GetAbbreviations();
var fragments = view.Document.GetFragments();
```

`UseGitHubFlavoredMarkdown()` stays strict to GFM. Call `UseMarkdownExtra()`
when you also want definition lists, abbreviations, and figure/caption nodes.
Raw HTML and LaTeX/math are not enabled by these helpers.

## Copy selection

Keyboard and context-menu copy preserve the markdown source as the plain-text
clipboard payload and add an HTML payload for formatted paste targets. Apps that
want rendered semantic plain text can opt in explicitly:

```csharp
using MarkdownRenderer.Selection;

MarkdownView.CopySelectionToClipboard(new MarkdownCopyOptions
{
    PlainTextMode = MarkdownPlainTextCopyMode.RenderedText,
});
```

## Handle links

```csharp
MarkdownView.LinkClick += (_, e) =>
{
    // External links are surfaced here. Internal footnote fragments are handled
    // by the control.
    var url = e.Url;
};
```

## Apply theme overrides

```csharp
using MarkdownRenderer.Theming;
using Microsoft.UI;
using Microsoft.UI.Text;

var theme = new MarkdownTheme
{
    AccentColor = Colors.DeepSkyBlue,
};

theme.Overrides[MarkdownElementKeys.Link] = new ElementStyleOverride
{
    Foreground = Colors.DeepSkyBlue,
    Underline = false,
};

theme.Overrides[MarkdownElementKeys.Heading1] = new ElementStyleOverride
{
    FontSize = 28,
    FontWeight = FontWeights.SemiBold,
};

MarkdownView.Theme = theme;

// Direct override mutations invalidate the assigned theme automatically.
```

## Host a WinUI control for a markdown block

```csharp
using Markdig.Syntax;
using MarkdownRenderer.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

public sealed class DemoEmbedFactory : IMarkdownEmbedFactory
{
    public bool CanCreate(Block block)
        => block is FencedCodeBlock fenced && fenced.Info == "button";

    public float MeasureHeight(Block block, float availableWidth)
        => 40;

    public FrameworkElement CreateBlock(Block block)
        => new Button { Content = "Native WinUI Button" };
}

MarkdownView.EmbedFactory = new DemoEmbedFactory();
```

Important: `CanCreate` and `MeasureHeight` run on the background layout thread.
They must not touch WinUI objects. `CreateBlock` runs on the UI thread.

This is also the recommended shape for Mermaid or diagram support: recognize a
fenced code block whose info string is `mermaid` in `CanCreate`, return a cheap
measured height in `MeasureHeight`, and host your chosen renderer from
`CreateBlock`.

## Next steps

- [Public API](public-api.md) for the full consumer surface.
- [Theming and customization](theming-and-customization.md) for style keys and
  override composition.
- [Extensibility API](extensibility-api.md) for custom renderers and hosted
  controls.
- [Troubleshooting](troubleshooting.md) for SVG, clipboard, graphics-device, and
  embed-threading issues.
