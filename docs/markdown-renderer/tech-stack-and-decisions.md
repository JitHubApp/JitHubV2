# Tech stack and decisions

| Technology | Role |
| --- | --- |
| .NET | Immutable engine/document model and host contracts. |
| Markdig | Internal CommonMark/GFM parsing implementation. |
| WinUI / Windows App SDK | Native views, input, theme resources, hosted elements, and UI Automation. |
| Win2D / DirectWrite | Native paint, shaping, measurement, and hit testing. |
| ThorVG | Optional native SVG rasterization feature pack. |
| TextMate | Optional syntax-highlighting integration with separate grammar packs. |

## Native viewer instead of WebView

The viewer does not depend on browser DOM, script, or CSS layout. This keeps
focus, theme, DPI, input, packaging, and UI Automation in the native WinUI model.
It also means HTML must be an explicitly bounded native subset rather than an
implicit browser capability.

## Immutable boundary

`MarkdownEngineBuilder` freezes profiles and declarative extensions into a
thread-safe engine. `ParseAsync` returns immutable documents that preserve
half-open UTF-16 source spans, diagnostics, and semantic queries. Views consume
documents without exposing their private layout or drawing implementation.

## Optional capabilities

The base `MarkdownRenderer` package stays lean. GitHub behavior, safe HTML, Math,
Mermaid, ThorVG, TextMate, and grammar payloads require explicit package choices.
The default presentation follows WinUI/Fluent resources; GitHub behavior is not
silently imposed on every app.

ThorVG is kept out of the base package because native architecture payloads are
a real size and deployment decision. TextMate grammar resources are split for
the same reason. Math and Mermaid provide bounded native implementations with
accessible, atomic source fallbacks; their physical-device and release-evidence
matrices remain separate 1.0 gates.

## Extensibility and AOT

Extensions register exact syntax-kind strings and emit declarative semantic
content. This avoids reflection-based discovery and prevents public API coupling
to Markdig node classes or viewer layout objects. Trim/AOT and package-matrix
validation are still required 1.0 gates, not completed guarantees.
