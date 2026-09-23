using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageBoxSvgRendererTests
{
    [Theory]
    [InlineData(1.0, 125, 63)]
    [InlineData(1.25, 157, 79)]
    [InlineData(1.5, 188, 94)]
    [InlineData(2.0, 250, 125)]
    [InlineData(4.0, 500, 250)]
    public async Task RasterSizeUsesMeasuredDipsAndExactRasterizationScale(
        double rasterizationScale,
        int expectedWidth,
        int expectedHeight)
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 200,
            IntrinsicHeightDips: 100,
            IntrinsicAspectRatio: 2,
            Description: "Provider description",
            HasText: true,
            UsesCurrentColor: true,
            UsesColorScheme: true));
        var image = new ImageBox(
            CreateContext(renderer, rasterizationScale),
            CreateSvgDataUri($"sizing-{rasterizationScale}"),
            "Sizing example");

        try
        {
            image.Measure(125);
            await WaitForLoadAsync(image);

            MarkdownSvgRenderRequest request = Assert.Single(renderer.RenderRequests);
            Assert.True(
                image.Bitmap is not null,
                $"{image.SvgFailureReason}: {image.SvgDesc}");
            Assert.Equal(expectedWidth, request.TargetWidthPixels);
            Assert.Equal(expectedHeight, request.TargetHeightPixels);
            Assert.Equal((expectedWidth, expectedHeight), image.SvgRasterPixelSize);
            Assert.Equal("Provider description", image.SvgDesc);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Null(image.SvgFailureReason);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task ExactDeviceCacheHitDoesNotOpenOrRenderTheSvgAgain()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(renderer), source, "cached");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int opensAfterFirstRender = renderer.OpenCount;
        int rendersAfterFirstRender = renderer.RenderRequests.Count;
        var second = new ImageBox(CreateContext(renderer), source, "cached");
        try
        {
            second.Measure(80);
            await WaitForLoadAsync(second);

            Assert.NotNull(second.Bitmap);
            Assert.Equal(opensAfterFirstRender, renderer.OpenCount);
            Assert.Equal(rendersAfterFirstRender, renderer.RenderRequests.Count);
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentIdenticalSvgBoxesShareProviderOpenAndRaster()
    {
        var renderer = new RecordingSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            openDelay: TimeSpan.FromMilliseconds(150));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));
        var first = new ImageBox(CreateContext(renderer), source, "duplicate");
        var second = new ImageBox(CreateContext(renderer), source, "duplicate");

        try
        {
            first.Measure(80);
            second.Measure(80);

            await Task.WhenAll(
                WaitForLoadAsync(first),
                WaitForLoadAsync(second));

            Assert.NotNull(first.Bitmap);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Single(renderer.RenderRequests);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public async Task DeviceCacheDoesNotCrossRendererInstances()
    {
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        var firstRenderer = new RecordingSvgRenderer(info);
        var replacementRenderer = new RecordingSvgRenderer(info);
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(firstRenderer), source, "provider swap");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        var replacement = new ImageBox(
            CreateContext(replacementRenderer),
            source,
            "provider swap");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.Equal(1, replacementRenderer.OpenCount);
            Assert.Single(replacementRenderer.RenderRequests);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public async Task DeviceCacheIsNotUsedAfterProviderIsRemoved()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(renderer), source, "provider removed");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        var withoutProvider = new ImageBox(CreateContext(renderer: null), source, "provider removed");
        try
        {
            withoutProvider.Measure(80);
            await WaitForLoadAsync(withoutProvider);

            Assert.Null(withoutProvider.Bitmap);
            Assert.Equal(
                MarkdownSvgFailureReason.ArchitectureMismatch,
                withoutProvider.SvgFailureReason);
        }
        finally
        {
            withoutProvider.Dispose();
        }
    }

    [Fact]
    public async Task CacheGenerationDoesNotInvalidateNonTextSvgRaster()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2,
            HasText: false));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(renderer), source, "generation");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int opensBeforeInvalidation = renderer.OpenCount;
        int rendersBeforeInvalidation = renderer.RenderRequests.Count;
        renderer.AdvanceGeneration();

        var replacement = new ImageBox(CreateContext(renderer), source, "generation");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.Equal(opensBeforeInvalidation, renderer.OpenCount);
            Assert.Equal(rendersBeforeInvalidation, renderer.RenderRequests.Count);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public async Task CacheGenerationInvalidatesTextSvgRaster()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2,
            HasText: true));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(renderer), source, "generation");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int opensBeforeInvalidation = renderer.OpenCount;
        int rendersBeforeInvalidation = renderer.RenderRequests.Count;
        renderer.AdvanceGeneration();

        var replacement = new ImageBox(CreateContext(renderer), source, "generation");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.True(renderer.OpenCount > opensBeforeInvalidation);
            Assert.True(renderer.RenderRequests.Count > rendersBeforeInvalidation);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public async Task ProviderFailureDoesNotPoisonReplacementRenderer()
    {
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        var rejectingRenderer = new RecoveringSvgRenderer(info, reject: true);
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var rejected = new ImageBox(CreateContext(rejectingRenderer), source, "rejected");
        rejected.Measure(80);
        await WaitForLoadAsync(rejected);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejected.SvgFailureReason);
        rejected.Dispose();

        var replacementRenderer = new RecordingSvgRenderer(info);
        var replacement = new ImageBox(CreateContext(replacementRenderer), source, "replacement");
        try
        {
            replacement.Measure(80);
            await WaitForLoadAsync(replacement);

            Assert.Equal(1, replacementRenderer.OpenCount);
            Assert.Single(replacementRenderer.RenderRequests);
            Assert.NotNull(replacement.Bitmap);
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public async Task ProviderFailureDoesNotPoisonLaterGeneration()
    {
        var renderer = new RecoveringSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            reject: true);
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var rejected = new ImageBox(CreateContext(renderer), source, "generation zero");
        rejected.Measure(80);
        await WaitForLoadAsync(rejected);
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, rejected.SvgFailureReason);
        rejected.Dispose();

        renderer.EnableAndAdvanceGeneration();
        var recovered = new ImageBox(CreateContext(renderer), source, "generation one");
        try
        {
            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.NotNull(recovered.Bitmap);
            Assert.Null(recovered.SvgFailureReason);
            Assert.Single(renderer.RenderRequests);
        }
        finally
        {
            recovered.Dispose();
        }
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsAdmittedSvgWorkBeforeReleasingLifetime()
    {
        var renderer = new BlockingSvgRenderer();
        var image = new ImageBox(
            CreateContext(renderer),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")),
            "dispose race");
        image.Measure(80);
        image.EnsureLoading();
        await renderer.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        image.Dispose();

        await renderer.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() =>
            image.ActiveSvgWorkCountForTests == 0 &&
            image.SvgLifetimeDisposedForTests);
    }

    [Fact]
    public async Task RelayoutKeepsPreviousDeviceBitmapUntilScaledReplacementArrives()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(renderer), source, "transition");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int rendersBeforeScaleChange = renderer.RenderRequests.Count;
        var scaled = new ImageBox(
            CreateContext(renderer, rasterizationScale: 2),
            source,
            "transition");
        try
        {
            Assert.NotNull(scaled.Bitmap);
            scaled.Measure(80);
            await WaitForLoadAsync(scaled);

            Assert.True(renderer.RenderRequests.Count > rendersBeforeScaleChange);
            Assert.Equal((160, 80), scaled.SvgRasterPixelSize);
            Assert.NotNull(scaled.Bitmap);
        }
        finally
        {
            scaled.Dispose();
        }
    }

    [Fact]
    public async Task OversizedOutputDefersToVisiblePhysicalPixelTiles()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 5000,
            IntrinsicHeightDips: 1000,
            IntrinsicAspectRatio: 5));
        var image = new ImageBox(
            CreateContext(renderer, rasterizationScale: 4),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")),
            "large diagram");

        try
        {
            image.Measure(5000);
            await WaitForLoadAsync(image);

            Assert.True(image.UsesSvgTilesForTests);
            Assert.Empty(renderer.RenderRequests);
            Assert.Equal((20_000, 4_000), image.SvgRasterPixelSize);

            using var target = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(),
                256,
                256,
                96);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                image.Paint(drawingSession, new Rect(0, 0, 256, 256));
            }

            await WaitUntilAsync(() => !renderer.RenderRequests.IsEmpty);
            MarkdownSvgRenderRequest[] requests = renderer.RenderRequests.ToArray();
            Assert.InRange(requests.Length, 1, 32);
            Assert.Contains(requests, request => request.Priority == MarkdownSvgRenderPriority.Visible);
            Assert.All(requests, request =>
            {
                Assert.Equal(20_000, request.TargetWidthPixels);
                Assert.Equal(4_000, request.TargetHeightPixels);
                Assert.True(request.TileRegion.HasValue);
                MarkdownSvgTileRegion tile = request.TileRegion.Value;
                Assert.InRange(tile.Width, 1, 1024);
                Assert.InRange(tile.Height, 1, 1024);
            });
        }
        finally
        {
            image.Dispose();
        }
    }

    [Theory]
    [InlineData(1, 1_000_000, 1)]
    [InlineData(1_000_000, 1, 1_000_000)]
    public async Task DeviceOversizedDimensionUsesBoundedTiles(
        double intrinsicWidth,
        double intrinsicHeight,
        float availableWidth)
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: intrinsicWidth,
            IntrinsicHeightDips: intrinsicHeight,
            IntrinsicAspectRatio: intrinsicWidth / intrinsicHeight));
        var image = new ImageBox(
            CreateContext(renderer),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")),
            "extreme aspect ratio");

        try
        {
            image.Measure(availableWidth);
            await WaitForLoadAsync(image);

            Assert.True(image.UsesSvgTilesForTests);
            Assert.Empty(renderer.RenderRequests);

            using var target = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(),
                256,
                256,
                96);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                image.Paint(drawingSession, new Rect(0, 0, 256, 256));
            }

            await WaitUntilAsync(() => !renderer.RenderRequests.IsEmpty);
            Assert.All(renderer.RenderRequests, request =>
            {
                Assert.True(request.TileRegion.HasValue);
                MarkdownSvgTileRegion tile = request.TileRegion.Value;
                Assert.InRange(tile.Width, 1, 1024);
                Assert.InRange(tile.Height, 1, 1024);
            });
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task MissingProviderProducesTypedAccessibleFailure()
    {
        var image = new ImageBox(
            CreateContext(renderer: null),
            CreateSvgDataUri("missing-provider"),
            "Unavailable diagram");
        try
        {
            image.Measure(120);
            await WaitForLoadAsync(image);

            Assert.Equal(
                MarkdownSvgFailureReason.ArchitectureMismatch,
                image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
            Assert.Contains("No static SVG renderer", image.SvgDesc, StringComparison.Ordinal);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task BlockedNestedImageBecomesAccessibleFailureWithoutFirstChanceException()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2))
        {
            SourcePreparationOverride = MarkdownSvgSourcePreparation.Reject(
                MarkdownSvgFailureReason.UnsupportedContent,
                "The SVG was rejected by host preflight (external-image-reference)."),
        };
        string svg =
            "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='40'>" +
            "<image href='https://example.test/avatar.png' width='40' height='40'/>" +
            "</svg>";
        string source = "data:image/svg+xml," + Uri.EscapeDataString(svg);
        int firstChancePreflightExceptions = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception is MarkdownSvgException exception &&
                exception.Message.Contains(
                    "host preflight (external-image-reference)",
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref firstChancePreflightExceptions);
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        var image = new ImageBox(CreateContext(renderer), source, "Contributor avatars");
        try
        {
            image.Measure(120);
            await WaitForLoadAsync(image);

            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
            Assert.Contains("external-image-reference", image.SvgDesc, StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref firstChancePreflightExceptions));
        }
        finally
        {
            image.Dispose();
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }
    }

    [Fact]
    public async Task BrowserToleratedEmbeddedRasterMediaTypePassesHostPreflight()
    {
        const string png =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        string svg =
            "<svg xmlns='http://www.w3.org/2000/svg' width='64' height='56'>" +
            $"<image width='64' height='56' href='data:false;base64,{png}'/>" +
            "</svg>";
        string source = "data:image/svg+xml," + Uri.EscapeDataString(svg);
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 64,
            IntrinsicHeightDips: 56,
            IntrinsicAspectRatio: 64d / 56d));
        var image = new ImageBox(CreateContext(renderer), source, "Organization avatar");

        try
        {
            image.Measure(112);
            await WaitForLoadAsync(image);

            Assert.Equal(1, renderer.OpenCount);
            Assert.Null(image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task ResolvedRasterPayloadOverridesSvgSourceSuffix()
    {
        const string source =
            "https://opencollective.com/katex/organization/3/avatar.svg";
        byte[] transparentPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII=");
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 1,
            IntrinsicHeightDips: 1,
            IntrinsicAspectRatio: 1));
        var resolver = new FixedImageResolver(new MarkdownImageAsset(
            transparentPng,
            ContentType: "image/png",
            ResolvedUri: new Uri(source),
            CacheKey: $"opencollective-transparent-{Guid.NewGuid():N}"));
        var image = new ImageBox(
            CreateContext(renderer, imageResolver: resolver),
            source,
            "Unoccupied organization slot");

        try
        {
            image.Measure(96);
            await WaitForLoadAsync(image);

            Assert.Equal(0, renderer.OpenCount);
            Assert.NotNull(image.Bitmap);
            Assert.False(image.IsSvg);
            Assert.Null(image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
        }
        finally
        {
            image.Dispose();
        }
    }

    private static async Task WaitForLoadAsync(ImageBox image)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completion.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            image.EnsureLoading();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static MarkdownLayoutContext CreateContext(
        IMarkdownSvgRenderer? renderer,
        double rasterizationScale = 1,
        IMarkdownImageResolver? imageResolver = null)
    {
        var body = new ElementStyle
        {
            Foreground = Color.FromArgb(255, 12, 34, 56),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
            [MarkdownElementKeys.ListMarker] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: true,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = imageResolver,
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = rasterizationScale,
        };
    }

    private static string CreateSvgDataUri(string marker)
    {
        string svg =
            $"<svg xmlns='http://www.w3.org/2000/svg' width='200' height='100'>" +
            $"<title>{marker}</title><rect width='200' height='100'/></svg>";
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    private sealed class RecordingSvgRenderer(
        MarkdownSvgDocumentInfo info,
        TimeSpan? openDelay = null)
        : IMarkdownSvgRenderer
    {
        internal MarkdownSvgSourcePreparation? SourcePreparationOverride { get; init; }

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SourcePreparationOverride ?? MarkdownSvgSourcePreparation.Admit(source);
        }

        private int _openCount;
        private long _cacheGeneration;

        internal int OpenCount => Volatile.Read(ref _openCount);

        public long CacheGeneration => Volatile.Read(ref _cacheGeneration);

        internal ConcurrentQueue<MarkdownSvgRenderRequest> RenderRequests { get; } = new();

        internal void AdvanceGeneration() => Interlocked.Increment(ref _cacheGeneration);

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            if (openDelay is { } delay && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            return new RecordingSvgDocument(info, RenderRequests);
        }
    }

    private sealed class RecordingSvgDocument(
        MarkdownSvgDocumentInfo info,
        ConcurrentQueue<MarkdownSvgRenderRequest> requests)
        : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(request);
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int length = checked(width * height * 4);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
            owner.Memory.Span[..length].Clear();
            return ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                length,
                width,
                height,
                checked(width * 4),
                request.PixelFormat));
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecoveringSvgRenderer(
        MarkdownSvgDocumentInfo info,
        bool reject)
        : IMarkdownSvgRenderer
    {
        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        private long _cacheGeneration;
        private int _reject = reject ? 1 : 0;

        public long CacheGeneration => Volatile.Read(ref _cacheGeneration);

        internal ConcurrentQueue<MarkdownSvgRenderRequest> RenderRequests { get; } = new();

        internal void EnableAndAdvanceGeneration()
        {
            Volatile.Write(ref _reject, 0);
            Interlocked.Increment(ref _cacheGeneration);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _reject) != 0)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.UnsupportedContent,
                    "Rejected by the test provider.");
            }

            return ValueTask.FromResult<IMarkdownSvgDocument>(
                new RecordingSvgDocument(info, RenderRequests));
        }
    }

    private sealed class BlockingSvgRenderer : IMarkdownSvgRenderer
    {
        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        internal TaskCompletionSource Opened { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Canceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            Opened.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking renderer unexpectedly resumed.");
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FixedImageResolver(MarkdownImageAsset asset) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(asset));
        }
    }
}
