# Quick start

This page shows the expected consumer shape. Some packaging and helper APIs are
still maturing, so examples reflect the current project API.

## Add the project

Reference the core renderer and, if GitHub-flavored markdown is needed, the GFM
extension project:

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

## Enable GitHub-flavored markdown

```csharp
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Parsing;

var registry = new MarkdownExtensionRegistry()
    .UseGitHubFlavoredMarkdown();

MarkdownView.ExtensionRegistry = registry;
MarkdownView.Markdown = """
# Hello MarkdownRenderer

- [x] Task lists
- Tables
- Footnotes[^1]

[^1]: Footnote definitions are rendered and linked.
""";
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

// Required today when mutating Overrides after assignment. A future API should
// make this automatic.
theme.Invalidate();
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

