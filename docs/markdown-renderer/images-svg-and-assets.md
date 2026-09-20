# Images, SVG, and assets

Bitmap images use the viewer's asynchronous resolver, cache, lazy-loading, and
accessibility paths. Applications control repository-relative and remote image
policy through `IMarkdownImageResolver`/`IImageResolver`, base URI, document
identity, and third-party remote-image consent.

## Bitmap behavior

Standalone and inline images preserve alt text and source ranges. Loading begins
near the effective viewport. Intrinsic-size changes can trigger relayout, while
pixel-only updates repaint the affected area. Cache identity must include the
document/repository security context so resolved assets are not reused across an
incompatible trust boundary.

## SVG is optional

The lean `MarkdownRenderer` package does not include an SVG engine. Install
`MarkdownRenderer.Svg.Resvg` and explicitly assign one shared
`ResvgMarkdownSvgRenderer` to `SvgRenderer`, or register it with
`WithResvgSvgRenderer(...)`. The control borrows the renderer; the host owns and
disposes it after all document surfaces have stopped using it.

The optional package runs pinned resvg 0.48.1 in an architecture-specific,
job-contained worker for x86, x64, and ARM64. Source and premultiplied raster
bytes travel through shared memory; the pipe carries only fixed-size protocol
messages. SVG parsing and rendering never execute inside the app process and
there is no in-process fallback.

The admitted subset is static and self-contained. Scripts, event handlers,
animation, `foreignObject`, DTD/entities, external CSS/fonts, and nested
network/file references fail as typed, accessible placeholders. Bounded data-URI
PNG, JPEG, GIF, WebP, and nested SVG images are allowed and deduplicated by
content. Remote top-level markdown images still obey the host's ordinary image
policy before any SVG bytes reach the provider.

Release validation inspects every RID worker, protocol and resource ceilings,
package placement and signatures, crash/hang containment, device-loss recovery,
theme dependencies, DPI changes, package size, and deterministic Edge-relative
fidelity. Without a registered provider, SVGs remain accessible fallbacks.

## Accessibility

Explicit markdown/HTML alt text is the accessible name; an explicitly empty alt
keeps the image decorative. The root SVG `<desc>` is exposed as help text. SVGs
remain atomic images for selection and UI Automation, so inner SVG text is not
duplicated as document text. Applications should still provide meaningful alt
text because a preview may run without the optional capability.
