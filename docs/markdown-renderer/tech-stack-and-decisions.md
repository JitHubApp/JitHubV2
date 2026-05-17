# Tech stack and decisions

## Core technologies

| Technology | Use | Reason |
| --- | --- | --- |
| WinUI / Windows App SDK | Control, visual tree, input, theme, UIA, hosted controls | Native Windows 11 integration. |
| Markdig | Markdown parsing and extension pipeline | Mature, extensible CommonMark/GFM parser for .NET. |
| Win2D | Virtual canvas and drawing API | Efficient Direct2D/DirectWrite-backed rendering from WinUI. |
| DirectWrite via Win2D text layouts | Text shaping, metrics, hit testing | Native text quality, glyph metrics, RTL support path. |
| ThorVG native DLL | SVG rasterization | Broad SVG feature support through one raster path. |
| FlaUI | Sample app UI automation | End-to-end automation of WinUI behavior. |
| xUnit | Unit tests | Standard .NET test infrastructure. |

## Why not WebView?

A WebView would provide mature HTML/CSS/layout/selection quickly, but it would
also bring:

- larger process and memory footprint;
- browser-specific styling and focus behavior;
- less direct WinUI theme integration;
- harder native hosted-control composition;
- harder deterministic paint invalidation;
- reduced control over text selection/copy semantics;
- dependency on web accessibility semantics rather than UIA-native peers.

The library chooses native implementation complexity in exchange for control over
Windows integration and performance.

## Why Markdig?

Markdig provides:

- CommonMark-compatible parsing;
- GFM extensions such as pipe tables, task lists, autolinks, emphasis extras,
  footnotes, emoji, and generic attributes;
- AST access suitable for custom layout;
- an extensible pipeline that lets consumers opt into additional syntax.

The renderer does not use Markdig's HTML renderer. It consumes the AST and builds
native boxes instead.

## Why Win2D and DirectWrite?

Win2D provides a WinUI-friendly wrapper over Direct2D and DirectWrite. It supports
`CanvasVirtualControl`, which lets the renderer repaint only invalidated regions.
DirectWrite text layouts provide the text metrics needed for:

- line wrapping;
- hit testing;
- selection rectangles;
- baseline and font metrics;
- RTL and mixed-direction text support path.

## Why a XAML overlay?

Win2D paints pixels. It cannot host interactive WinUI controls or expose rich
control-specific UI Automation peers by itself. The overlay solves this by placing
real XAML elements above the paint surface while the renderer still owns their
layout slot.

The overlay is also used for selection and focus visuals because those visuals
can change every pointer move. Keeping them out of the DirectWrite canvas avoids
expensive text repaint and fractional-DPI glyph jitter.

## Why ThorVG for SVG?

The current design uses ThorVG as the single SVG rasterizer. SVG payloads are
converted to BGRA bitmaps and then painted through the same `CanvasBitmap` branch
as raster images.

Benefits:

- one image paint path;
- broad SVG support, including features beyond simple Win2D SVG;
- cached raster output for theme/DPI combinations;
- no runtime tier classifier.

The core package ships ThorVG native DLLs for x86, x64, and ARM64. Repo builds
copy the selected architecture's DLL to the output root, and NuGet packages place
all three assets under RID-native runtime folders.

## AOT and trimming posture

The projects enable trim, single-file, and AOT analyzers. Reflection-heavy plugin
discovery is avoided. `MarkdownExtensionRegistry` dispatches custom renderers by
concrete Markdig AST node type using a dictionary.

Public XML docs, package metadata, versioning policy, and cross-architecture
native asset packaging are part of the package contract and are validated during
release.
