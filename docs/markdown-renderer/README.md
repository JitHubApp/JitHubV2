# MarkdownRenderer documentation

`MarkdownRenderer` is a native WinUI markdown rendering control built for Windows
11 applications. It parses markdown with Markdig, lays the document out into an
internal box tree, paints text and graphics through Win2D/DirectWrite, and hosts
real WinUI controls for interactive markdown extensions.

This documentation set captures the current implementation, the reasoning behind
major decisions, the public API shape, known gaps, and the path to a mature
library.

## Documentation map

| Document | Purpose |
| --- | --- |
| [Overview and philosophy](overview-and-philosophy.md) | Product goals, non-goals, and design principles. |
| [Quick start](quick-start.md) | How to place the control in a WinUI app and enable GFM. |
| [Architecture](architecture.md) | High-level systems and object ownership. |
| [Tech stack and decisions](tech-stack-and-decisions.md) | Libraries used, why they were chosen, and rejected alternatives. |
| [Rendering pipeline](rendering-pipeline.md) | Parse, layout, paint, images, SVG, and GFM rendering. |
| [Theming and customization](theming-and-customization.md) | Theme objects, element keys, dynamic theme behavior, and styling gaps. |
| [Selection and clipboard](selection-and-clipboard.md) | DOM-like selection model, source-accurate copy, and embed-selection roadmap. |
| [Accessibility](accessibility.md) | UI Automation peers, current support, and compliance gaps. |
| [Native integration and hosted controls](native-integration-and-hosted-controls.md) | WinUI overlay embeds, virtualization, focus, and interaction rules. |
| [Performance and memory](performance-and-memory.md) | Threading, invalidation, caching, image loading, and scale limits. |
| [Extensibility API](extensibility-api.md) | Custom Markdig renderers, pipeline configuration, and embed factories. |
| [Images, SVG, and assets](images-svg-and-assets.md) | Bitmap loading, SVG rasterization through ThorVG, caching, and fallbacks. |
| [Testing and diagnostics](testing-and-diagnostics.md) | Unit tests, UI automation, pixel tests, and shake logging. |
| [Packaging and distribution](packaging-and-distribution.md) | Project structure, AOT/trimming posture, NuGet readiness, and bundle size. |
| [Current gaps and roadmap](current-gaps-and-roadmap.md) | What is left before production maturity. |

## Current maturity snapshot

The renderer is functional and has a broad feature set: headings, paragraphs,
lists, code, block quotes, images, SVG, GFM tables, task lists, footnotes, alerts,
keyboard link navigation, hosted WinUI controls, source-preserving copy, RTL flow,
lazy image loading, embed virtualization, and UI automation coverage.

The biggest maturity gaps are accessibility depth, public documentation, packaging
polish, incremental restyling, inline image rendering, and very-large-document
layout strategy. See [Current gaps and roadmap](current-gaps-and-roadmap.md).

## Key namespaces and projects

| Project | Role |
| --- | --- |
| `MarkdownRenderer` | Core control, layout, theming, selection, images, accessibility, and hosted control support. |
| `MarkdownRenderer.Gfm` | Optional GitHub-flavored markdown extension registration and renderers. |
| `MarkdownRenderer.Sample` | WinUI sample app for manual and automated verification. |
| `MarkdownRenderer.Sample.Automation` | FlaUI-based UI automation probes. |
| `MarkdownRenderer.Tests` | Unit tests for parsing, layout, theming, images, SVG helpers, selection, and regressions. |
| `MarkdownRenderer.PixelTests` | SVG/pixel compliance infrastructure. |

