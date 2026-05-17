# MarkdownRenderer documentation

`MarkdownRenderer` is a native WinUI markdown rendering control built for Windows
11 applications. It parses markdown with Markdig, lays the document out into an
internal box tree, paints text and graphics through Win2D/DirectWrite, and hosts
real WinUI controls for interactive markdown extensions.

This documentation set is structured for a docs website. Start with the quick
start if you are integrating the control, use the public API and supported
markdown pages as references, and use the operations pages when preparing a
release or diagnosing an app integration.

## Start here

| Need | Read |
| --- | --- |
| Add the control to an app | [Quick start](quick-start.md) |
| See what markdown syntax is supported | [Supported markdown](supported-markdown.md) |
| Learn the control and query APIs | [Public API](public-api.md) |
| Customize color, spacing, borders, and links | [Theming and customization](theming-and-customization.md) |
| Host native WinUI controls from markdown | [Native integration and hosted controls](native-integration-and-hosted-controls.md) |
| Debug SVGs, selection, clipboard, or device loss | [Troubleshooting](troubleshooting.md) |
| Prepare packages for release | [Release checklist](release-checklist.md) |

For a full generated-site table of contents, see [Summary](SUMMARY.md).

## Documentation map

| Document | Purpose |
| --- | --- |
| [Overview and philosophy](overview-and-philosophy.md) | Product goals, non-goals, and design principles. |
| [Quick start](quick-start.md) | Install, create, configure, query, theme, copy, and embed. |
| [Supported markdown](supported-markdown.md) | Core, GFM, Markdown Extra, extension-sample, and out-of-scope syntax. |
| [Public API](public-api.md) | Consumer APIs, document facade, clipboard options, themes, and extension contracts. |
| [Samples](samples.md) | Sample app pages and automation coverage. |
| [Architecture](architecture.md) | High-level systems and object ownership. |
| [Tech stack and decisions](tech-stack-and-decisions.md) | Libraries used, why they were chosen, and rejected alternatives. |
| [Rendering pipeline](rendering-pipeline.md) | Parse, layout, paint, images, SVG, GFM, and Markdown Extra rendering. |
| [Theming and customization](theming-and-customization.md) | Theme objects, element keys, style composition, and dynamic theme behavior. |
| [Selection and clipboard](selection-and-clipboard.md) | DOM-like selection model, source-accurate copy, HTML clipboard, and embed selection. |
| [Accessibility](accessibility.md) | UI Automation peers, TextPattern support, roles, and release validation. |
| [Native integration and hosted controls](native-integration-and-hosted-controls.md) | WinUI overlay embeds, virtualization, focus, and interaction rules. |
| [Performance and memory](performance-and-memory.md) | Threading, invalidation, lazy layout, caching, image loading, and scale boundaries. |
| [Extensibility API](extensibility-api.md) | Custom Markdig renderers, pipeline configuration, and embed factories. |
| [Images, SVG, and assets](images-svg-and-assets.md) | Bitmap loading, SVG rasterization through ThorVG, caching, and runtime assets. |
| [Testing and diagnostics](testing-and-diagnostics.md) | Unit tests, UI automation, pixel tests, and shake logging. |
| [Packaging and distribution](packaging-and-distribution.md) | Project structure, package metadata, native assets, and versioning. |
| [Release checklist](release-checklist.md) | Build matrix, package inspection, manual smoke, and publish rehearsal. |
| [Roadmap](current-gaps-and-roadmap.md) | Deferred features and release-readiness tracking. |

## Maturity snapshot

The renderer is functional and has a broad feature set: headings, paragraphs,
lists, code, block quotes, images, SVG, GFM tables, task lists, footnotes,
alerts, Markdown Extra definition lists/abbreviations/figures, emphasis extras,
keyboard link navigation, hosted WinUI controls, source-preserving copy with
HTML plus opt-in rendered text, RTL flow, lazy image loading, embed
virtualization, and UI automation coverage.

Raw HTML and LaTeX/math are intentionally tracked outside the 1.0
non-HTML/non-LaTeX plan. The remaining maturity work is broader
release-validation smoke and NuGet publishing rehearsal. Packaging metadata,
public quick starts, XML docs, document queries, and x86/x64/ARM64 SVG assets
are in place. See [Roadmap](current-gaps-and-roadmap.md).

## Key namespaces and projects

| Project | Role |
| --- | --- |
| `MarkdownRenderer` | Core control, layout, theming, selection, images, accessibility, and hosted control support. |
| `MarkdownRenderer.Gfm` | Optional GitHub-flavored markdown extension registration and renderers. |
| `MarkdownRenderer.Sample` | WinUI sample app for manual and automated verification. |
| `MarkdownRenderer.Sample.Automation` | FlaUI-based UI automation probes. |
| `MarkdownRenderer.Tests` | Unit tests for parsing, layout, theming, images, SVG helpers, selection, and regressions. |
| `MarkdownRenderer.PixelTests` | SVG/pixel compliance infrastructure. |

## Documentation conventions

- Code snippets assume a WinUI 3 app and package references unless a page says it
  is showing project-reference development.
- Public API examples avoid implementation-only layout/rendering types.
- Pages call out raw HTML, LaTeX/math, and built-in diagram rendering as separate
  tracks so the 1.0 support boundary is explicit.
