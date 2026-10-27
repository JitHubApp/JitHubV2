# Tech stack and decisions

| Technology | Role |
| --- | --- |
| .NET | Immutable engine/document model and host contracts. |
| Markdig | Internal CommonMark/GFM parsing implementation. |
| WinUI / Windows App SDK | Native views, input, theme resources, hosted elements, and UI Automation. |
| Win2D / DirectWrite | Native paint, shaping, measurement, and hit testing. |
| resvg 0.48.1 | Optional isolated static-SVG rendering provider. |
| TextMate | Optional syntax-highlighting integration with separate grammar packs. |

A Rust core was considered and deferred as a separate future project. The
implementation retains its .NET document/WinUI
host and isolated Rust rendering/decoding workers.

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
Mermaid, resvg, TextMate, and grammar payloads require explicit package choices.
The default presentation follows WinUI/Fluent resources; GitHub behavior is not
silently imposed on every app.

resvg is kept out of the base package because architecture-specific workers are
a real size and deployment decision. TextMate grammar resources are split for
the same reason. Math and Mermaid provide bounded native implementations with
accessible, atomic source fallbacks; their physical-device and release-evidence
matrices remain separate 1.0 gates.

## Out-of-process SVG and image worker

`MarkdownRenderer.Svg.Resvg` decodes SVG and raster images in a separate worker
process (`MarkdownRenderer.Svg.Resvg.Worker.exe`), not in the host process. The
input is untrusted: any image a document links to. Running the decoder in its
own process lets the host contain failures that an in-process library or WinRT
component cannot:

- The worker runs in a Job Object with a commit-memory limit, so an oversized or
  decompression-bomb image ends the worker instead of exhausting the app.
- Each request has a deadline. A stalled or looping decode is ended with
  `TerminateProcess` and a fresh worker replaces it; a stuck thread inside the
  host process cannot be stopped safely.
- A native abort, stack overflow or memory fault ends only the worker
  (`DieOnUnhandledException`, `KillOnJobClose`), and the image shows its
  accessible fallback.

An out-of-process WinRT server would still be an executable, would need
activation registration (reg-free manifests for unpackaged hosts) and would not
provide these limits by itself. The worker uses a small private pipe protocol,
so hosts register nothing.

The cost is one executable per architecture (x86, x64, ARM64) and one process
startup per persistent worker. The executables are committed so that .NET
builds and packing don't need Rust. CI rebuilds all three from the vendored
source and fails if any differs from the committed binary, and every build
checks `RESVG_WORKER_IDENTITY.json` against the sources and binaries.

## Extensibility and AOT

Extensions register exact syntax-kind strings and emit declarative semantic
content. This avoids reflection-based discovery and prevents public API coupling
to Markdig node classes or viewer layout objects. Trim/AOT and package-matrix
validation are still required 1.0 gates, not completed guarantees.
