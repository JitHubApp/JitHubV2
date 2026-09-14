# MarkdownRenderer.Svg.ThorVG

Optional native SVG support for `MarkdownRenderer.WinUI`. The package contains one
ThorVG software-rendering DLL for each supported Windows RID (`win-x86`, `win-x64`,
and `win-arm64`). NuGet selects the consuming application's matching asset; the lean
`MarkdownRenderer` package contains no native bytes.

Install this package when markdown images should render broad, self-contained SVG
content. Images continue to use the native WinUI image surface and UI Automation
semantics. If the native asset is absent, the wrong architecture, invalid, or rejects
the SVG, rendering falls back atomically to the image's accessible alt/error state.

The renderer does not execute script, perform SVG-originated network or filesystem
fetches, or host a browser engine. Resource budgets are applied before native parsing.
The pinned ThorVG 1.1.1 build supports static SVG geometry, paths, text, gradients,
patterns, masks, clipping, reuse, transforms, and Gaussian blur. Unsupported filter
primitives are rejected before native rendering so the viewer uses its atomic,
accessible image fallback instead of displaying silently incomplete artwork.

`ThorVgFeature.TryGetLoadedNativeVersion()` can be used for deployment diagnostics.
`ThorVgFeature.Rasterize(...)` exposes the same safe software-rasterization path for
headless validation and advanced hosts.
