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

The lean `MarkdownRenderer` package does not include ThorVG native binaries.
Install `MarkdownRenderer.Svg.ThorVG` when the app wants native SVG
rasterization. That feature pack supplies architecture-specific assets for x86,
x64, and ARM64 and routes rasterized output through the normal bitmap paint path.

When the ThorVG pack is selected, release validation must inspect the matching
RID-native DLL, PE machine type, package placement, device-loss recovery, theme
color handling, DPI changes, and malformed/untrusted SVG limits. Without the
optional capability, the viewer must retain an accessible placeholder/fallback
rather than assuming the native DLL exists.

## Accessibility

Explicit markdown alt text is the primary accessible name. SVG title and
description metadata can provide fallback context when the optional rasterizer
is active. Applications should still provide meaningful alt text because a
preview may run without the SVG capability.
