# Packaging and distribution

The renderer is packaged as two NuGet packages: `MarkdownRenderer` for the core
control and `MarkdownRenderer.Gfm` for GitHub-flavored markdown helpers and
renderers.

## Current project structure

```text
MarkdownRenderer/
  MarkdownRenderer/                  core control
  MarkdownRenderer.Gfm/              GFM extension package
  MarkdownRenderer.Sample/           WinUI sample app
  MarkdownRenderer.Sample.Automation/ UI automation
  MarkdownRenderer.Tests/            unit tests
  MarkdownRenderer.PixelTests/       SVG/pixel tests
```

## Target frameworks and platforms

Core and GFM projects target:

- `net10.0-windows10.0.26100.0`
- `TargetPlatformMinVersion` `10.0.19041.0`
- `Platforms`: `x86;x64;ARM64`
- `UseWinUI`: `true`

## Dependencies

Core package references:

- `Markdig`
- `Microsoft.Graphics.Win2D`
- `Microsoft.WindowsAppSDK`
- `Microsoft.Windows.SDK.BuildTools`

`MarkdownRenderer.Gfm` references the core project and uses Markdig extension
APIs.

## Native assets

The core project includes ThorVG native SVG rasterizer assets for:

- `native\win-x86\thorvg.dll`;
- `native\win-x64\thorvg.dll`;
- `native\win-arm64\thorvg.dll`.

The default repo build copies the selected architecture's `thorvg.dll` to the
output root, including app outputs that reference the renderer project. The core
project packs all three binaries under `runtimes\win-x86\native`,
`runtimes\win-x64\native`, and `runtimes\win-arm64\native`. Builds fail if a
selected architecture's native asset is missing.

## AOT and trimming

The projects enable:

- `IsTrimmable`;
- `IsAotCompatible`;
- trim analyzer;
- single-file analyzer;
- AOT analyzer;
- selected IL warnings as errors.

Custom renderer dispatch is designed to avoid reflection-heavy discovery.

## Package metadata

Both packages include package IDs, descriptions, author metadata, MIT license
expression, repository/project URLs, tags, README, and the existing repository
icon. XML documentation is generated with `CS1591` enabled.

## Package split

| Package | Contents |
| --- | --- |
| `MarkdownRenderer` | Core control, base rendering, theming, selection, images, SVG, hosted controls. |
| `MarkdownRenderer.Gfm` | GFM pipeline helper and GFM renderers. |
| `MarkdownRenderer.Samples` | Optional sample package or repository-only samples. |

Keeping GFM separate lets the core remain minimal while still making GFM easy to
opt into.

## Quick-start APIs

Core CommonMark:

```csharp
var control = MarkdownRendererControl.CreateDefault(markdownSource);
```

Recommended GFM:

```csharp
var control = GfmMarkdownRenderer.CreateDefault(markdownSource);
```

Fluent configuration:

```csharp
var control = new MarkdownRendererControlBuilder()
    .UseGitHubFlavoredMarkdown()
    .UseMarkdownExtra()
    .WithMarkdown(markdownSource)
    .WithTheme(theme)
    .WithEmbedFactory(embedFactory)
    .WithSelectionEnabled(true)
    .Build();
```

The committed parsed-document facade is available through `control.Document`
with `GetHeadings()`, `GetLinks()`, `GetCodeBlocks()`, `GetImages()`,
`GetFootnotes()`, `GetDefinitionItems()`, `GetAbbreviations()`, and
`GetFragments()`.

## Versioning policy

Before 1.0, source-breaking cleanup is allowed when it removes accidental public
internals or stabilizes the extension-author boundary. Starting at 1.0, public
APIs follow semantic versioning.

## Bundle size considerations

Primary bundle-size contributors:

- Windows App SDK dependencies;
- Win2D;
- Markdig;
- ThorVG native DLLs for x86, x64, and ARM64.

The control should avoid adding broad general-purpose dependencies. New features
should prefer small, optional packages or extension points.
