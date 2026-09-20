# MarkdownRenderer.Svg.Resvg

Optional engine-neutral static SVG rendering for MarkdownRenderer, backed by
resvg 0.48.1 in a persistent out-of-process Windows worker.

```csharp
await using var svg = new ResvgMarkdownSvgRenderer();
await svg.WarmUpAsync();

MarkdownScrollView view = new MarkdownRendererControlBuilder()
    .WithResvgSvgRenderer(svg)
    .WithMarkdown(markdown)
    .BuildScrollView();
```

The caller owns the shared renderer. Controls borrow it and dispose only their
document handles. SVG bytes and raster pixels cross the worker boundary through
private shared memory; a versioned fixed-size named-pipe protocol carries only
control data. There is no in-process native fallback.

The provider accepts the bounded static subset implemented by the pinned resvg
release. Scripts, event handlers, animation, `foreignObject`, DTD/entities,
external references/fonts/styles, and over-budget content fail atomically with
a typed `MarkdownSvgException`.

Worker binaries are RID assets for `win-x86`, `win-x64`, and `win-arm64`.
Production packaging must Authenticode-sign all three executables before NuGet
packing. The project deliberately fails `Pack` if an architecture is absent.
