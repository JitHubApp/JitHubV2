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

The same shared service can also be registered explicitly as a static WebP
decoder, independently of SVG registration:

```csharp
MarkdownScrollView view = new MarkdownRendererControlBuilder()
    .WithResvgSvgRenderer(svg)
    .WithRasterDecoder(svg)
    .WithMarkdown(markdown)
    .BuildScrollView();
```

`IMarkdownRasterDecoder` is engine-neutral. Controls check `SupportsFormat`
before submitting a bounded, display-sized request. This provider advertises
only static WebP; it does not take over animated images or formats it does not
advertise. Image URL policy and source admission still belong to the host.
Decoder selection is explicit: a selected provider's failure does not trigger
an installed-codec or in-process fallback. Failures use `MarkdownRasterException`
and its typed reason; controls keep their accessible unavailable-image behavior.

For direct decoding, keep the request's encoded memory alive and unchanged
until `DecodeAsync` completes, including cancellation/failure. Dispose the
returned `MarkdownRaster` after GPU upload. It owns exact-size, oriented,
premultiplied sRGB pixels and can outlive the service; never access its memory
after or concurrently with disposal. `MaximumWorkingBytes` can lower the
provider's immutable ceiling, not raise it, and is not a process-RSS guarantee.
Controls borrow this service and never dispose the shared decoder.

Dispose each document when its last render is no longer needed. Renderer
shutdown also cancels retained document handles and in-flight opens: synchronous
`Dispose` initiates cleanup and stops workers, while `DisposeAsync` waits for
actual open/render work to leave its source buffers. An earlier synchronous
shutdown does not skip that asynchronous drain. Handles become unusable after
renderer shutdown; cancellation during worker admission does not quarantine
otherwise valid SVG content. Healthy renderers track documents weakly rather
than retaining their source buffers. This is lifetime cleanup, not an aggregate
source-memory quota or an eviction/reacquisition policy.

The provider accepts the bounded static subset implemented by the pinned resvg
release. Scripts, event handlers, animation, `foreignObject`, DTD/entities,
external references/fonts/styles, and over-budget content fail atomically with
a typed `MarkdownSvgException`.

Worker binaries are RID assets for `win-x86`, `win-x64`, and `win-arm64`.
Production packaging must Authenticode-sign all three executables before NuGet
packing. The project deliberately fails `Pack` if an architecture is absent.

Build workers with `eng/Build-ResvgWorker.ps1`. It pins Rust 1.96.0's bundled
`rust-lld`, remaps checkout and Cargo paths, and enables reproducible PE output.
CI rebuilds all three architectures and compares the complete executable payload after normalizing
only standard PE timestamp/checksum and CodeView identity fields; executable
sections, imports, resources, and linker version must match byte-for-byte.

Ordinary project builds and packing also verify `RESVG_WORKER_IDENTITY.json`
against the current native sources, build recipe, protocol versions, and all
three checked-in worker payloads. They do not silently rebuild Rust or copy a
worker from a different architecture. After changing native inputs, build each
target with `eng/Build-ResvgWorker.ps1`, preserve the prior runtime payload, copy
the fresh executable to that target's `runtimes/<rid>/native/` directory, then
run the same build with `-RefreshIdentity`. This records that target only after
the fresh output reproduces the runtime executable. Repeat for all three
targets before building the managed projects. This unsigned source identity is
not a substitute for the separate release signing and license checks.
