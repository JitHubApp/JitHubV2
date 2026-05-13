# Packaging and distribution

The project is structured like a library but is not fully NuGet-ready yet.

## Current project structure

```text
MarkdownRenderer/
  MarkdownRenderer/                  core control
  MarkdownRenderer.Gfm/              GFM extension package candidate
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

The core project includes `native\win-x64\thorvg.dll` as content for x64-like
builds. Explicit x86 and ARM64 builds do not include a native ThorVG binary.

This is acceptable for current development but incomplete for a production NuGet.

## AOT and trimming

The projects enable:

- `IsTrimmable`;
- `IsAotCompatible`;
- trim analyzer;
- single-file analyzer;
- AOT analyzer;
- selected IL warnings as errors.

Custom renderer dispatch is designed to avoid reflection-heavy discovery.

## NuGet readiness gaps

Before packaging:

- add `PackageId`;
- add `Description`;
- add `Authors`;
- add `PackageTags`;
- add `PackageLicenseExpression`;
- add `RepositoryUrl`;
- add `PackageProjectUrl`;
- add package icon;
- decide whether `MarkdownRenderer.Gfm` ships as a separate package;
- ship native SVG assets for x64, ARM64, and x86 or document unsupported
  architectures clearly;
- remove or justify `CS1591` suppression after XML docs are added;
- create a versioning and breaking-change policy;
- include samples and extension-author docs.

## Suggested package split

| Package | Contents |
| --- | --- |
| `MarkdownRenderer` | Core control, base rendering, theming, selection, images, SVG, hosted controls. |
| `MarkdownRenderer.Gfm` | GFM pipeline helper and GFM renderers. |
| `MarkdownRenderer.Samples` | Optional sample package or repository-only samples. |

Keeping GFM separate lets the core remain minimal while still making GFM easy to
opt into.

## Bundle size considerations

Primary bundle-size contributors:

- Windows App SDK dependencies;
- Win2D;
- Markdig;
- ThorVG native DLL;
- future native binaries for ARM64/x86 if added.

The control should avoid adding broad general-purpose dependencies. New features
should prefer small, optional packages or extension points.

