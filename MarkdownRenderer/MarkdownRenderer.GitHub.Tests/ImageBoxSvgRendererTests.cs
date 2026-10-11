using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgRendererTests
{
    [Fact]
    public void UnavailableEventArgsPreserveOptionalSvgFailureCategory()
    {
        var ordinary = new MarkdownImageUnavailableEventArgs(
            "image.png", MarkdownImageUnavailableReason.Unavailable);
        var svg = new MarkdownImageUnavailableEventArgs(
            "image.svg", MarkdownImageUnavailableReason.Unavailable,
            MarkdownSvgFailureReason.Timeout);

        Assert.Null(ordinary.SvgFailureReason);
        Assert.Equal(MarkdownSvgFailureReason.Timeout, svg.SvgFailureReason);
    }

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
            var viewport = Assert.IsType<MarkdownSvgPhysicalViewport>(request.PhysicalViewport);
            Assert.Equal(125 * rasterizationScale, viewport.Width);
            Assert.Equal(62.5 * rasterizationScale, viewport.Height);
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
    public async Task FractionalViewportsWithEqualAllocationDoNotShareGpuRaster()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            // Power-of-two intrinsic dimensions isolate the cache-key contract
            // from an unrelated float division/multiplication rounding step.
            IntrinsicWidthDips: 256, IntrinsicHeightDips: 128, IntrinsicAspectRatio: 2));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));
        var first = new ImageBox(CreateContext(renderer), source, string.Empty);
        var second = new ImageBox(CreateContext(renderer), source, string.Empty);
        var repeat = new ImageBox(CreateContext(renderer), source, string.Empty);
        try
        {
            first.Measure(125.125f);
            await WaitForLoadAsync(first);
            second.Measure(125.25f);
            await WaitForLoadAsync(second);
            MarkdownSvgRenderRequest[] requests = renderer.RenderRequests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal(requests[0].TargetWidthPixels, requests[1].TargetWidthPixels);
            Assert.Equal(requests[0].TargetHeightPixels, requests[1].TargetHeightPixels);
            Assert.NotEqual(requests[0].PhysicalViewport, requests[1].PhysicalViewport);
            Assert.Equal(new MarkdownSvgPhysicalViewport(125.125, 62.5625), requests[0].PhysicalViewport);
            Assert.Equal(new MarkdownSvgPhysicalViewport(125.25, 62.625), requests[1].PhysicalViewport);
            repeat.Measure(125.125f);
            await WaitForLoadAsync(repeat);
            Assert.Equal(2, renderer.RenderRequests.Count);
            Assert.Same(first.Bitmap, repeat.Bitmap);
            Assert.NotSame(first.Bitmap, second.Bitmap);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            repeat.Dispose();
            await Task.WhenAll(first.RetirementTask, second.RetirementTask, repeat.RetirementTask)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.25)]
    public async Task FractionalViewportPaintingDoesNotMultiplyProviderEdgeCoverage(double scale)
    {
        var renderer = new RecordingSvgRenderer(
            new MarkdownSvgDocumentInfo(7, 5, 7d / 5),
            fillPixels: static (pixels, _, _) =>
            {
                Span<byte> bytes = pixels.Span;
                for (int offset = 0; offset < bytes.Length; offset += 4)
                {
                    // Already antialiased premultiplied pixels must reach
                    // composition unchanged, including the partial last pixel.
                    bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = bytes[offset + 3] = 128;
                }
            });
        var image = new ImageBox(CreateContext(renderer, scale),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")), string.Empty);
        try
        {
            image.Measure(7);
            await WaitForLoadAsync(image);
            image.Measure(7);
            image.Arrange(0, 0, 7);
            CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
            Rect destination = image.AutomationRenderDestination;
            int pixelWidth = checked((int)System.Math.Ceiling(destination.Width * scale));
            int pixelHeight = checked((int)System.Math.Ceiling(destination.Height * scale));
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), pixelWidth, pixelHeight, 96);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                    -(float)destination.X, -(float)destination.Y) *
                    System.Numerics.Matrix3x2.CreateScale((float)scale);
                image.Paint(drawing, destination);
            }
            Assert.Equal(bitmap.GetPixelBytes(), target.GetPixelBytes());
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.25)]
    public async Task FractionalLastTilePaintingDoesNotMultiplyProviderEdgeCoverage(double scale)
    {
        const int width = 16_385;
        const int height = 5;
        var renderer = new RecordingSvgRenderer(
            new MarkdownSvgDocumentInfo(width, height, (double)width / height),
            fillPixels: static (pixels, _, _) => pixels.Span.Fill(128));
        var image = new ImageBox(CreateContext(renderer, scale),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")), string.Empty);
        try
        {
            image.Measure(width);
            await WaitForLoadAsync(image);
            image.Measure(width);
            image.Arrange(0, 0, width);
            Assert.True(image.UsesSvgTilesForAutomation);
            Rect destination = image.AutomationRenderDestination;
            int firstPixel = checked((int)((width - 1) * scale));
            int tileIndex = firstPixel / 1024;
            int pixelWidth = checked((int)System.Math.Ceiling(width * scale)) - firstPixel;
            int pixelHeight = checked((int)System.Math.Ceiling(height * scale));
            Rect visible = new(destination.X + width - 1, destination.Y, 1, height);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), pixelWidth, pixelHeight, 96);

            void PaintLastTile()
            {
                using CanvasDrawingSession drawing = target.CreateDrawingSession();
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = System.Numerics.Matrix3x2.CreateScale((float)scale) *
                    System.Numerics.Matrix3x2.CreateTranslation(
                        -(float)(destination.X * scale) - firstPixel, -(float)(destination.Y * scale));
                image.Paint(drawing, visible);
            }

            PaintLastTile();
            await WaitUntilAsync(() => image.HasAllSvgTilesForAutomation(tileIndex, 0, tileIndex, 0));
            PaintLastTile();
            Assert.Contains(renderer.RenderRequests, request =>
                request.TileRegion is { } tile && tile.X == firstPixel &&
                tile.Width == pixelWidth && tile.Height == pixelHeight &&
                request.PhysicalViewport == new MarkdownSvgPhysicalViewport(width * scale, height * scale));
            Assert.All(target.GetPixelBytes(), value => Assert.Equal((byte)128, value));
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransparentReadyTilesReplaceOnlyTheirOwnLoadingOrPreviousBitmapRegions(bool previousWholeBitmap)
    {
        const int width = 20_000;
        const int height = 8;
        var renderer = new GatedTileSvgRenderer(
            new MarkdownSvgDocumentInfo(width, height, (double)width / height),
            opaqueWholeBitmap: true);
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));
        if (previousWholeBitmap)
        {
            var previous = new ImageBox(CreateContext(renderer, rasterizationScale: 0.5), source, string.Empty);
            try
            {
                previous.Measure(width);
                await WaitForLoadAsync(previous);
                Assert.NotNull(previous.Bitmap);
            }
            finally
            {
                previous.Dispose();
                await previous.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        var image = new ImageBox(CreateContext(renderer), source, string.Empty);
        try
        {
            if (previousWholeBitmap)
                Assert.NotNull(image.Bitmap);
            image.Measure(width);
            await WaitForLoadAsync(image);
            image.Measure(width);
            image.Arrange(0, 0, width);
            Assert.True(image.UsesSvgTilesForAutomation);
            Rect destination = image.AutomationRenderDestination;
            var visible = new Rect(destination.X, destination.Y, 2048, height);
            Color background = Color.FromArgb(255, 31, 81, 121);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 2048, height, 96);

            Color[] PaintVisible()
            {
                using (CanvasDrawingSession drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(background);
                    drawing.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                        -(float)destination.X, -(float)destination.Y);
                    var transform = drawing.Transform;
                    image.Paint(drawing, visible);
                    Assert.Equal(CanvasAntialiasing.Antialiased, drawing.Antialiasing);
                    Assert.Equal(transform, drawing.Transform);
                }
                return target.GetPixelColors();
            }

            PaintVisible();
            await renderer.SecondVisibleTileStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.HasAllSvgTilesForAutomation(0, 0, 0, 0));
            Assert.False(image.HasAllSvgTilesForAutomation(0, 0, 1, 0));
            Color[] partial = PaintVisible();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < 1024; x++)
                    Assert.Equal(background, partial[y * 2048 + x]);
            Assert.NotEqual(background, partial[4 * 2048 + 1536]);
            if (previousWholeBitmap)
                Assert.Equal(Color.FromArgb(255, 255, 255, 255), partial[4 * 2048 + 1536]);

            renderer.ReleaseSecondVisibleTile.TrySetResult();
            await WaitUntilAsync(() => image.HasAllSvgTilesForAutomation(0, 0, 1, 0));
            Assert.All(PaintVisible(), pixel => Assert.Equal(background, pixel));
        }
        finally
        {
            renderer.ReleaseSecondVisibleTile.TrySetResult();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void SparseOutOfOrderReplacementTilesCoverOnlyTheirPixelsAcrossMultipleRows()
    {
        const int columns = 20;
        const int rows = 4;
        const int tilePixels = 1024;
        const int displayTile = 32;
        var image = new ImageBox(CreateContext(renderer: null), string.Empty, string.Empty);
        var current = new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>();
        var fallback = new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>();
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        Color background = Color.FromArgb(255, 31, 81, 121);
        Color previous = Color.FromArgb(255, 255, 255, 255);
        try
        {
            // Deliberately reverse insertion order. Forty replacement tiles
            // exercise the pooled coverage buffer, with holes in every row.
            for (int index = columns * rows - 1; index >= 0; index--)
            {
                int x = index % columns;
                int y = index / columns;
                var key = (x * tilePixels, y * tilePixels, tilePixels, tilePixels);
                fallback.Add(key, SharedCanvasBitmapCache.CreateUncachedLease(
                    CanvasBitmap.CreateFromColors(device, [previous], 1, 1)));
                if ((x + y) % 2 == 0)
                    current.Add(key, SharedCanvasBitmapCache.CreateUncachedLease(
                        CanvasBitmap.CreateFromColors(device, [Color.FromArgb(0, 0, 0, 0)], 1, 1)));
            }
            void Set(string name, object value) => typeof(ImageBox)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(image, value);
            Set("_svgFallbackTiles", fallback);
            Set("_svgFallbackWidthPixels", columns * tilePixels);
            Set("_svgFallbackHeightPixels", rows * tilePixels);
            var paint = typeof(ImageBox).GetMethod("PaintSvgTileLayerNoLock",
                BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<
                Action<CanvasDrawingSession,
                    Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>,
                    Rect, Rect, int, int, MarkdownSvgPhysicalViewport?, bool>>(image);
            var destination = new Rect(0, 0, columns * displayTile, rows * displayTile);
            using var target = new CanvasRenderTarget(device,
                columns * displayTile, rows * displayTile, 96);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(background);
                paint(drawing, current, destination, destination,
                    columns * tilePixels, rows * tilePixels, null, true);
                Assert.Equal(CanvasAntialiasing.Antialiased, drawing.Antialiasing);
            }
            Color[] pixels = target.GetPixelColors();
            for (int y = 0; y < rows * displayTile; y++)
                for (int x = 0; x < columns * displayTile; x++)
                    Assert.Equal((x / displayTile + y / displayTile) % 2 == 0 ? background : previous,
                        pixels[y * columns * displayTile + x]);
        }
        finally
        {
            foreach (SharedCanvasBitmapCache.Lease lease in current.Values)
                lease.Dispose();
            // The fallback dictionary was transferred to the ImageBox. Its
            // normal disposal path must release those native bitmap leases.
            image.Dispose();
            foreach (SharedCanvasBitmapCache.Lease lease in fallback.Values)
                lease.Dispose();
        }
    }

    [Theory]
    [InlineData(CanvasAntialiasing.Antialiased)]
    [InlineData(CanvasAntialiasing.Aliased)]
    public async Task TileClipFactoryFailureRestoresDrawingModeAndDoesNotQueueRendering(CanvasAntialiasing antialiasing)
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(20_000, 8, 2500));
        var image = new ImageBox(CreateContext(renderer),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")), string.Empty);
        try
        {
            image.Measure(20_000);
            await WaitForLoadAsync(image);
            image.Measure(20_000);
            image.Arrange(0, 0, 20_000);
            var retirement = new NativeResourceRetirement(createTimer: false);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 64, 8, 96);
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            drawing.Antialiasing = antialiasing;
            var injected = new InvalidOperationException("injected tile clip factory failure");
            NativePaintSessionResources.RegisterTestOwner(drawing, retirement,
                afterLayerReservation: () => throw injected);
            Rect destination = image.AutomationRenderDestination;
            Assert.Same(injected, Assert.Throws<InvalidOperationException>(() =>
                image.Paint(drawing, new Rect(destination.X, destination.Y, 64, 8))));
            Assert.Equal(antialiasing, drawing.Antialiasing);
            Assert.Empty(renderer.RenderRequests);
            Assert.Equal(0, retirement.QuarantinedResourceCount);
            retirement.DrainOnce();
            Assert.Equal(0, retirement.PendingRecordCount);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
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
    public async Task ResolverOwnedSvgCacheKeyCannotSatisfyBuiltInLocalFallback()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MarkdownRendererSvgAlias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "built-in.svg");
        string builtInUri = new Uri(path).AbsoluteUri;
        string resolverSource = "https://resolver.test/" + Guid.NewGuid().ToString("N") + ".svg";
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        ImageBox? resolverImage = null;
        ImageBox? builtInImage = null;

        try
        {
            await File.WriteAllTextAsync(path, CreateSvgMarkup("Built-in B"));
            var asset = new MarkdownImageAsset(
                CreateSvgBytes("Resolver A"),
                ContentType: "image/svg+xml",
                ResolvedUri: new Uri(resolverSource),
                CacheKey: builtInUri);
            resolverImage = new ImageBox(
                CreateContext(
                    renderer,
                    imageResolver: new FixedImageResolver(asset)),
                resolverSource,
                "Resolver-owned SVG");

            resolverImage.Measure(80);
            await WaitForLoadAsync(resolverImage);
            Assert.NotNull(resolverImage.Bitmap);
            Assert.Equal("Resolver A", resolverImage.SvgTitle);
            int opensAfterResolverLoad = renderer.OpenCount;
            Assert.Equal(1, opensAfterResolverLoad);

            builtInImage = new ImageBox(
                CreateContext(renderer, imageResolver: new NotHandledImageResolver()),
                builtInUri,
                "Built-in SVG");
            builtInImage.Measure(80);
            await WaitForLoadAsync(builtInImage);

            Assert.NotNull(builtInImage.Bitmap);
            Assert.Equal("Built-in B", builtInImage.SvgTitle);
            Assert.Equal(opensAfterResolverLoad + 1, renderer.OpenCount);
        }
        finally
        {
            builtInImage?.Dispose();
            resolverImage?.Dispose();
            try
            {
                await Task.WhenAll(builtInImage?.RetirementTask ?? Task.CompletedTask,
                    resolverImage?.RetirementTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                RemoveProcessSvgCacheEntry(builtInUri);
                File.Delete(path);
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public async Task ResolverSvgContentHitStillReusesThePartitionedContentEntryWithoutSourceAlias()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string source = "https://resolver.test/" + suffix + ".svg";
        string partitionedCacheKey = "github:private-repository:" + suffix;
        var asset = new MarkdownImageAsset(
            CreateSvgBytes("Partitioned content"),
            ContentType: "image/svg+xml",
            ResolvedUri: new Uri(source),
            CacheKey: partitionedCacheKey);
        var resolver = new FixedImageResolver(asset);
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        ImageBox? first = null;
        ImageBox? second = null;

        try
        {
            first = new ImageBox(
                CreateContext(renderer, imageResolver: resolver),
                source,
                "First partitioned SVG");
            first.Measure(80);
            await WaitForLoadAsync(first);

            Assert.NotNull(first.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Null(GetProcessSvgCacheEntry(partitionedCacheKey));

            first.Dispose();
            await first.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            first = null;
            int opensAfterFirstLoad = renderer.OpenCount;

            second = new ImageBox(
                CreateContext(renderer, imageResolver: resolver),
                source,
                "Second partitioned SVG");
            second.Measure(80);
            await WaitForLoadAsync(second);

            Assert.NotNull(second.Bitmap);
            Assert.Equal(opensAfterFirstLoad, renderer.OpenCount);
            Assert.Null(GetProcessSvgCacheEntry(partitionedCacheKey));
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
            try
            {
                await Task.WhenAll(second?.RetirementTask ?? Task.CompletedTask,
                    first?.RetirementTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { RemoveProcessSvgCacheEntry(partitionedCacheKey); }
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
    public async Task UnkeyedResolverSvgDoesNotShareSourceOrDeviceBitmapAcrossBoxes()
    {
        string source = $"https://resolver.test/unkeyed-{Guid.NewGuid():N}.svg";
        var resolver = new FixedImageResolver(new MarkdownImageAsset(
            CreateSvgBytes("Unkeyed source"), "image/svg+xml",
            ResolvedUri: new Uri(source), CacheKey: string.Empty));
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(80, 40));
        var first = new ImageBox(CreateContext(renderer, imageResolver: resolver), source, "First unkeyed SVG");
        var second = new ImageBox(CreateContext(renderer, imageResolver: resolver), source, "Second unkeyed SVG");
        try
        {
            first.Measure(80);
            await WaitForLoadAsync(first);
            Assert.NotNull(first.Bitmap);
            int opens = renderer.OpenCount;
            int renders = renderer.RenderRequests.Count;
            Assert.Equal(1, opens);
            Assert.Equal(1, renders);

            second.Measure(80);
            await WaitForLoadAsync(second);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(opens + 1, renderer.OpenCount);
            Assert.Equal(renders + 1, renderer.RenderRequests.Count);
            Assert.NotSame(first.Bitmap, second.Bitmap);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            await Task.WhenAll(first.RetirementTask, second.RetirementTask)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DistinctSvgBoxesCanOpenConcurrently()
    {
        var renderer = new ConcurrentOpenSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        var first = new ImageBox(
            CreateContext(renderer),
            CreateSvgDataUri("parallel-first-" + Guid.NewGuid().ToString("N")),
            "first independent SVG");
        var second = new ImageBox(
            CreateContext(renderer),
            CreateSvgDataUri("parallel-second-" + Guid.NewGuid().ToString("N")),
            "second independent SVG");

        try
        {
            first.Measure(80);
            second.Measure(80);
            Task firstLoad = WaitForLoadAsync(first);
            Task secondLoad = WaitForLoadAsync(second);

            bool twoOpensStarted = await Task.WhenAny(
                    renderer.TwoOpensStarted.Task,
                    Task.Delay(TimeSpan.FromSeconds(5))) == renderer.TwoOpensStarted.Task;
            Assert.True(
                twoOpensStarted,
                $"Expected two distinct SVG opens to overlap; opens={renderer.OpenCount}, " +
                $"maximum concurrent={renderer.MaximumConcurrentOpens}.");
            Assert.Equal(2, renderer.MaximumConcurrentOpens);
            renderer.ReleaseOpens.TrySetResult();

            await Task.WhenAll(firstLoad, secondLoad);
            Assert.NotNull(first.Bitmap);
            Assert.NotNull(second.Bitmap);
            Assert.Equal(2, renderer.OpenCount);
        }
        finally
        {
            renderer.ReleaseOpens.TrySetResult();
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
    public async Task RuntimeOpenResourceLimitDoesNotPoisonSameRendererAndGeneration()
    {
        var renderer = new TransientResourceLimitSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            TransientResourceLimitStage.Open);
        string source = CreateSvgDataUri($"runtime-open-limit-{Guid.NewGuid():N}");
        var rejected = new ImageBox(CreateContext(renderer), source, "runtime open limit");

        try
        {
            rejected.Measure(80);
            await WaitForLoadAsync(rejected);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, rejected.SvgFailureReason);
            Assert.Null(rejected.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(0, renderer.RenderCount);
        }
        finally
        {
            rejected.Dispose();
        }

        var recovered = new ImageBox(CreateContext(renderer), source, "runtime open retry");
        try
        {
            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.Null(recovered.SvgFailureReason);
            Assert.NotNull(recovered.Bitmap);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(2, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
        }
        finally
        {
            recovered.Dispose();
        }
    }

    [Fact]
    public async Task RuntimeFirstRenderResourceLimitDoesNotPoisonSameRendererAndGeneration()
    {
        var renderer = new TransientResourceLimitSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            TransientResourceLimitStage.Render);
        string source = CreateSvgDataUri($"runtime-render-limit-{Guid.NewGuid():N}");
        var rejected = new ImageBox(CreateContext(renderer), source, "runtime render limit");

        try
        {
            rejected.Measure(80);
            await WaitForLoadAsync(rejected);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, rejected.SvgFailureReason);
            Assert.Null(rejected.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
        }
        finally
        {
            rejected.Dispose();
        }

        var recovered = new ImageBox(CreateContext(renderer), source, "runtime render retry");
        try
        {
            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.Null(recovered.SvgFailureReason);
            Assert.NotNull(recovered.Bitmap);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(2, renderer.OpenCount);
            Assert.Equal(2, renderer.RenderCount);
        }
        finally
        {
            recovered.Dispose();
        }
    }

    [Fact]
    public async Task ExplicitPrepareSourceResourceLimitRemainsCachedAcrossImageBoxes()
    {
        var renderer = new TransientResourceLimitSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            TransientResourceLimitStage.PrepareSource);
        string source = CreateSvgDataUri($"source-budget-limit-{Guid.NewGuid():N}");
        var rejected = new ImageBox(CreateContext(renderer), source, "source budget limit");

        try
        {
            rejected.Measure(80);
            await WaitForLoadAsync(rejected);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, rejected.SvgFailureReason);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
        }
        finally
        {
            rejected.Dispose();
        }

        // The provider admits this second preparation. The same renderer,
        // generation and source must still observe the earlier deterministic
        // content rejection before an OpenAsync call can begin.
        var cached = new ImageBox(CreateContext(renderer), source, "cached source budget limit");
        try
        {
            cached.Measure(80);
            await WaitForLoadAsync(cached);

            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, cached.SvgFailureReason);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Null(cached.Bitmap);
        }
        finally
        {
            cached.Dispose();
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
            image.ActiveImageWorkCountForTests == 0 &&
            image.ImageLifetimeDisposedForTests);
    }

    [Fact]
    public async Task SessionShutdownDropsAcceptedQueuedImagePublicationExactlyOnce()
    {
        await using var session = CreateSingleSlotCpuPreparationSession();
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80, IntrinsicHeightDips: 40, IntrinsicAspectRatio: 2));
        var image = new ImageBox(
            CreateContext(renderer, performanceSession: session), "", "queued publication");
        Action? delivery = null;
        int published = 0;
        int dropped = 0;
        try
        {
            image.PublishOnUiThread(
                () => Interlocked.Increment(ref published),
                () => Interlocked.Increment(ref dropped),
                callback =>
                {
                    delivery = callback;
                    return true;
                });
            Assert.Equal(1, image.ActiveImageWorkCountForTests);

            await session.DisposeAsync();
            Assert.Equal(0, Volatile.Read(ref dropped));
            Action callback = Assert.IsType<Action>(delivery);
            callback();
            callback();

            Assert.Equal(0, Volatile.Read(ref published));
            Assert.Equal(1, Volatile.Read(ref dropped));
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
        }
        finally
        {
            delivery?.Invoke();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SvgPreparationWaitsForSharedProcessAdmissionAcrossSessionsAndLegacyControls()
    {
        MarkdownImagePreparationAdmission gate = MarkdownImagePreparationAdmission.Shared;
        using IDisposable blocker = await gate.EnterAsync(
            new object(), 1, exclusive: true, CancellationToken.None);
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: false);
        await using var firstSession = CreateSingleSlotCpuPreparationSession();
        await using var secondSession = CreateSingleSlotCpuPreparationSession();
        var images = new[]
        {
            new ImageBox(
                CreateContext(renderer, performanceSession: firstSession),
                CreateSvgDataUri("process-preparation-session-one"),
                "session one"),
            new ImageBox(
                CreateContext(renderer, performanceSession: secondSession),
                CreateSvgDataUri("process-preparation-session-two"),
                "session two"),
            new ImageBox(
                CreateContext(renderer),
                CreateSvgDataUri("process-preparation-without-session"),
                "legacy control"),
        };
        Task[] loads = [];

        try
        {
            foreach (ImageBox image in images)
                image.Measure(80);
            loads = images.Select(WaitForLoadAsync).ToArray();

            await WaitUntilAsync(() => gate.PendingRequests == images.Length);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(1, gate.ActiveCount);
            Assert.Equal(gate.CapacityBytes, gate.ActiveBytes);

            blocker.Dispose();
            await Task.WhenAll(loads).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(images.Length, renderer.PrepareSourceCount);
            Assert.All(images, image => Assert.NotNull(image.Bitmap));
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
            Assert.Equal(0, gate.PendingRequests);
        }
        finally
        {
            blocker.Dispose();
            renderer.ReleaseOpenRequests();
            foreach (ImageBox image in images)
                image.Dispose();
            foreach (Task load in loads)
                await ObserveTaskForCleanupAsync(load);
            await WaitUntilAsync(() => images.All(image => image.ActiveImageWorkCountForTests == 0));
        }
    }

    [Fact]
    public async Task SvgPreparationUsesThreeTimesSourceLengthAsItsSchedulingWeight()
    {
        MarkdownImagePreparationAdmission gate = MarkdownImagePreparationAdmission.Shared;
        string dataUri = CreateSvgDataUri("process-preparation-weight");
        string sourceText = Uri.UnescapeDataString(dataUri[(dataUri.IndexOf(',') + 1)..]);
        long sourceLength = Encoding.UTF8.GetByteCount(sourceText);
        long heldWeight = gate.CapacityBytes - (sourceLength * 2);
        Assert.True(heldWeight > 0);

        using IDisposable blocker = await gate.EnterAsync(
            new object(), heldWeight, exclusive: false, CancellationToken.None);
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: false);
        var image = new ImageBox(CreateContext(renderer), dataUri, "weighted SVG");
        Task load = Task.CompletedTask;

        try
        {
            image.Measure(80);
            load = WaitForLoadAsync(image);

            // One source-sized estimate would fit in the remaining budget;
            // the required 3x transient-work estimate must wait.
            await WaitUntilAsync(() => gate.PendingRequests == 1);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(1, gate.ActiveCount);
            Assert.Equal(heldWeight, gate.ActiveBytes);

            blocker.Dispose();
            await load.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
        }
        finally
        {
            blocker.Dispose();
            renderer.ReleaseOpenRequests();
            image.Dispose();
            await ObserveTaskForCleanupAsync(load);
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
        }
    }

    [Fact]
    public async Task RetiringQueuedSvgSessionRemovesItsProcessPreparationRequest()
    {
        MarkdownImagePreparationAdmission gate = MarkdownImagePreparationAdmission.Shared;
        using IDisposable blocker = await gate.EnterAsync(
            new object(), 1, exclusive: true, CancellationToken.None);
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: false);
        await using var retiringSession = CreateSingleSlotCpuPreparationSession();
        var retired = new ImageBox(
            CreateContext(renderer, performanceSession: retiringSession),
            CreateSvgDataUri("process-preparation-retired"),
            "retired SVG");
        var survivor = new ImageBox(
            CreateContext(renderer),
            CreateSvgDataUri("process-preparation-survivor"),
            "surviving SVG");
        int retiredPublications = 0;
        retired.LoadCompleted += (_, _) => Interlocked.Increment(ref retiredPublications);
        Task survivorLoad = Task.CompletedTask;

        try
        {
            retired.Measure(80);
            survivor.Measure(80);
            retired.EnsureLoading();
            survivorLoad = WaitForLoadAsync(survivor);

            await WaitUntilAsync(() => gate.PendingRequests == 2);
            Assert.Equal(0, renderer.PrepareSourceCount);

            await retiringSession.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => gate.PendingRequests == 1 &&
                retired.ActiveImageWorkCountForTests == 0);
            Assert.Equal(0, renderer.PrepareSourceCount);
            Assert.Equal(0, Volatile.Read(ref retiredPublications));

            blocker.Dispose();
            await survivorLoad.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(survivor.Bitmap);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.ActiveBytes);
            Assert.Equal(0, gate.PendingRequests);
        }
        finally
        {
            blocker.Dispose();
            renderer.ReleaseOpenRequests();
            retired.Dispose();
            survivor.Dispose();
            await ObserveTaskForCleanupAsync(survivorLoad);
            await WaitUntilAsync(() => retired.ActiveImageWorkCountForTests == 0 &&
                survivor.ActiveImageWorkCountForTests == 0);
        }
    }

    [Fact]
    public async Task SvgPreparationReleasesProcessAdmissionBeforeBlockedProviderOpens()
    {
        MarkdownImagePreparationAdmission gate = MarkdownImagePreparationAdmission.Shared;
        using var renderer = new ProcessPreparationSvgRenderer(holdOpens: true);
        var images = new[]
        {
            new ImageBox(CreateContext(renderer), CreateSvgDataUri("provider-block-first"), "first"),
            new ImageBox(CreateContext(renderer), CreateSvgDataUri("provider-block-second"), "second"),
            new ImageBox(CreateContext(renderer), CreateSvgDataUri("provider-block-third"), "third"),
        };
        Task[] loads = [];

        try
        {
            foreach (ImageBox image in images.Take(2))
                image.Measure(80);
            loads = images.Take(2).Select(WaitForLoadAsync).ToArray();

            await renderer.TwoOpensStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.PendingRequests);

            images[2].Measure(80);
            loads = [.. loads, WaitForLoadAsync(images[2])];
            await renderer.ThreeOpensStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // All three provider opens remain blocked, yet a third source has
            // prepared and reached OpenAsync: the process gate was released
            // before provider admission/waiting.
            Assert.Equal(3, renderer.PrepareSourceCount);
            Assert.Equal(3, renderer.OpenCount);
            Assert.Equal(0, gate.ActiveCount);
            Assert.Equal(0, gate.PendingRequests);

            renderer.ReleaseOpenRequests();
            await Task.WhenAll(loads).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.All(images, image => Assert.NotNull(image.Bitmap));
        }
        finally
        {
            renderer.ReleaseOpenRequests();
            foreach (ImageBox image in images)
                image.Dispose();
            foreach (Task load in loads)
                await ObserveTaskForCleanupAsync(load);
            await WaitUntilAsync(() => images.All(image => image.ActiveImageWorkCountForTests == 0));
        }
    }

    [Fact]
    public async Task CpuPreparationAdmissionSerializesPreflightButIsReleasedBeforeProviderOpen()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstPreparation: true,
            blockFirstOpen: true);
        await using var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var first = new ImageBox(
            CreateContext(renderer, performanceSession: session, performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-admission-first-" + Guid.NewGuid().ToString("N")),
            "first admitted SVG");
        var second = new ImageBox(
            CreateContext(renderer, performanceSession: session, performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-admission-second-" + Guid.NewGuid().ToString("N")),
            "queued SVG");
        Task firstLoad = Task.CompletedTask;
        Task secondLoad = Task.CompletedTask;

        try
        {
            first.Measure(80);
            second.Measure(80);
            firstLoad = WaitForLoadAsync(first);
            await renderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            secondLoad = WaitForLoadAsync(second);
            await WaitUntilAsync(() => session.PendingCpuPreparations == 1);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.False(renderer.SecondPreparationStarted.Task.IsCompleted);

            renderer.ReleaseFirstPreparation.Set();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The first provider OpenAsync remains deliberately blocked. The
            // second synchronous prepare must nevertheless enter, proving the
            // CPU lease ends after host preflight and before provider awaits.
            await renderer.SecondPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.False(firstLoad.IsCompleted);

            await secondLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(second.Bitmap);
            renderer.ReleaseFirstOpen.TrySetResult();
            await firstLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(first.Bitmap);
        }
        finally
        {
            renderer.ReleaseFirstPreparation.Set();
            renderer.ReleaseFirstOpen.TrySetResult();
            await ObserveTaskForCleanupAsync(firstLoad);
            await ObserveTaskForCleanupAsync(secondLoad);
            first.Dispose();
            second.Dispose();
            await WaitUntilAsync(() =>
                first.ActiveImageWorkCountForTests == 0 &&
                second.ActiveImageWorkCountForTests == 0);
        }
    }

    [Fact]
    public async Task RetiringImageCancelsQueuedCpuPreparationWithoutPublishingAndDrainsIt()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstPreparation: true);
        await using var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var first = new ImageBox(
            CreateContext(renderer, performanceSession: session, performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-retire-first-" + Guid.NewGuid().ToString("N")),
            "admitted SVG");
        var queued = new ImageBox(
            CreateContext(renderer, performanceSession: session, performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-retire-queued-" + Guid.NewGuid().ToString("N")),
            "retired queued SVG");
        int queuedPublications = 0;
        queued.LoadCompleted += (_, _) => Interlocked.Increment(ref queuedPublications);
        Task firstLoad = Task.CompletedTask;

        try
        {
            first.Measure(80);
            queued.Measure(80);
            firstLoad = WaitForLoadAsync(first);
            await renderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            queued.EnsureLoading();
            await WaitUntilAsync(() => session.PendingCpuPreparations == 1);
            Assert.Equal(1, renderer.PrepareSourceCount);

            queued.Dispose();
            await WaitUntilAsync(() =>
                queued.ActiveImageWorkCountForTests == 0 &&
                queued.ImageLifetimeDisposedForTests);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, Volatile.Read(ref queuedPublications));
            Assert.Null(queued.SvgFailureReason);

            renderer.ReleaseFirstPreparation.Set();
            await firstLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(first.Bitmap);
        }
        finally
        {
            renderer.ReleaseFirstPreparation.Set();
            await ObserveTaskForCleanupAsync(firstLoad);
            first.Dispose();
            queued.Dispose();
            await WaitUntilAsync(() =>
                first.ActiveImageWorkCountForTests == 0 &&
                queued.ActiveImageWorkCountForTests == 0);
        }
    }

    [Fact]
    public async Task SessionShutdownCancelsQueuedAndActiveSvgPreparationAndWaitsForActiveDrain()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstPreparation: true,
            holdFirstPreparationAfterCancellation: true);
        var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var unavailable = new ConcurrentQueue<MarkdownSvgFailureReason?>();
        var first = new ImageBox(
            CreateContext(
                renderer,
                imageUnavailable: (_, _, failure) => unavailable.Enqueue(failure),
                performanceSession: session,
                performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-shutdown-first-" + Guid.NewGuid().ToString("N")),
            "active SVG");
        var queued = new ImageBox(
            CreateContext(
                renderer,
                imageUnavailable: (_, _, failure) => unavailable.Enqueue(failure),
                performanceSession: session,
                performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-shutdown-queued-" + Guid.NewGuid().ToString("N")),
            "queued SVG");
        int firstPublications = 0;
        int queuedPublications = 0;
        first.LoadCompleted += (_, _) => Interlocked.Increment(ref firstPublications);
        queued.LoadCompleted += (_, _) => Interlocked.Increment(ref queuedPublications);

        try
        {
            first.Measure(80);
            queued.Measure(80);
            first.EnsureLoading();
            await renderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            queued.EnsureLoading();
            await WaitUntilAsync(() => session.PendingCpuPreparations == 1);

            Task shutdown = session.DisposeAsync().AsTask();
            await renderer.FirstPreparationCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => queued.ActiveImageWorkCountForTests == 0);

            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(0, Volatile.Read(ref firstPublications));
            Assert.Equal(0, Volatile.Read(ref queuedPublications));
            Assert.Empty(unavailable);
            Assert.Null(first.SvgFailureReason);
            Assert.Null(queued.SvgFailureReason);

            renderer.ReleaseFirstPreparationAfterCancellation.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => first.ActiveImageWorkCountForTests == 0);
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(0, renderer.OpenCount);
            Assert.Equal(0, Volatile.Read(ref firstPublications));
            Assert.Empty(unavailable);
            Assert.Null(first.SvgFailureReason);
        }
        finally
        {
            renderer.ReleaseFirstPreparation.Set();
            renderer.ReleaseFirstPreparationAfterCancellation.Set();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            first.Dispose();
            queued.Dispose();
            await WaitUntilAsync(() =>
                first.ActiveImageWorkCountForTests == 0 &&
                queued.ActiveImageWorkCountForTests == 0);
        }
    }

    [Fact]
    public async Task SessionShutdownCancelsProviderOpenAfterCpuPreparationWithoutPublishingRetry()
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            blockFirstOpen: true);
        await using var session = CreateSingleSlotCpuPreparationSession();
        var unavailable = new ConcurrentQueue<MarkdownSvgFailureReason?>();
        var image = new ImageBox(
            CreateContext(
                renderer,
                imageUnavailable: (_, _, failure) => unavailable.Enqueue(failure),
                performanceSession: session),
            CreateSvgDataUri("cpu-shutdown-open-" + Guid.NewGuid().ToString("N")),
            "SVG awaiting provider open");
        int publications = 0;
        image.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);

        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, renderer.PrepareSourceCount);
            Assert.Equal(1, renderer.OpenCount);

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Empty(unavailable);
            Assert.Null(image.SvgFailureReason);
            Assert.Null(image.Bitmap);
        }
        finally
        {
            renderer.ReleaseFirstOpen.TrySetResult();
            image.Dispose();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
        }
    }

    [Fact]
    public async Task SessionShutdownCancelsSaturatedProviderAdmissionWithoutWaitingForOtherImages()
    {
        var info = new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        var blockers = new List<(ImageBox Image, CpuAdmissionSvgRenderer Renderer)>();
        using var queuedRenderer = new CpuAdmissionSvgRenderer(info);
        await using var session = CreateSingleSlotCpuPreparationSession();
        var queued = new ImageBox(
            CreateContext(queuedRenderer, performanceSession: session),
            CreateSvgDataUri("provider-admission-queued-" + Guid.NewGuid().ToString("N")),
            "SVG waiting for provider admission");
        int publications = 0;
        queued.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);

        try
        {
            // Hold all sixteen global provider slots with unrelated images.
            // Each first OpenAsync signals only after that slot was acquired.
            for (int i = 0; i < 16; i++)
            {
                var renderer = new CpuAdmissionSvgRenderer(info, blockFirstOpen: true);
                var image = new ImageBox(
                    CreateContext(renderer),
                    CreateSvgDataUri("provider-admission-blocker-" + Guid.NewGuid().ToString("N")),
                    "unrelated admitted SVG");
                blockers.Add((image, renderer));
                image.Measure(80);
                image.EnsureLoading();
                await renderer.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            queued.Measure(80);
            queued.EnsureLoading();
            await queuedRenderer.FirstPreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Acquiring this CPU slot proves the queued image released its
            // synchronous preparation slot; the provider gate is still full.
            using (await session.EnterCpuPreparationAsync(new object(), false, CancellationToken.None))
            {
                Assert.Equal(0, queuedRenderer.OpenCount);
                Assert.True(queued.ActiveImageWorkCountForTests > 0);
            }

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => queued.ActiveImageWorkCountForTests == 0);

            Assert.Equal(0, queuedRenderer.OpenCount);
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Null(queued.SvgFailureReason);
            Assert.Null(queued.Bitmap);
            Assert.All(blockers, blocker => Assert.False(blocker.Renderer.ReleaseFirstOpen.Task.IsCompleted));
        }
        finally
        {
            foreach (var blocker in blockers)
                blocker.Renderer.ReleaseFirstOpen.TrySetResult();
            queued.Dispose();
            foreach (var blocker in blockers)
                blocker.Image.Dispose();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() =>
                queued.ActiveImageWorkCountForTests == 0 &&
                blockers.All(blocker => blocker.Image.ActiveImageWorkCountForTests == 0));
            foreach (var blocker in blockers)
                blocker.Renderer.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrThrowingProviderPreparationReturnsCpuSlot(bool providerThrows)
    {
        using var renderer = new CpuAdmissionSvgRenderer(
            new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 80,
                IntrinsicHeightDips: 40,
                IntrinsicAspectRatio: 2),
            firstPreparationFailure: providerThrows
                ? CpuAdmissionSvgRenderer.FirstPreparationFailure.Throw
                : CpuAdmissionSvgRenderer.FirstPreparationFailure.Reject);
        await using var session = CreateSingleSlotCpuPreparationSession();
        object documentOwner = new();
        var failed = new ImageBox(
            CreateContext(renderer, performanceSession: session, performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-failure-first-" + Guid.NewGuid().ToString("N")),
            "provider failure");
        var recovered = new ImageBox(
            CreateContext(renderer, performanceSession: session, performanceDocumentOwner: documentOwner),
            CreateSvgDataUri("cpu-failure-recovered-" + Guid.NewGuid().ToString("N")),
            "subsequent provider request");

        try
        {
            failed.Measure(80);
            await WaitForLoadAsync(failed);
            Assert.NotNull(failed.SvgFailureReason);

            recovered.Measure(80);
            await WaitForLoadAsync(recovered);

            Assert.Null(recovered.SvgFailureReason);
            Assert.NotNull(recovered.Bitmap);
            Assert.Equal(2, renderer.PrepareSourceCount);
            Assert.Equal(1, renderer.OpenCount);
        }
        finally
        {
            failed.Dispose();
            recovered.Dispose();
            await session.DisposeAsync();
            await WaitUntilAsync(() =>
                failed.ActiveImageWorkCountForTests == 0 &&
                recovered.ActiveImageWorkCountForTests == 0);
        }
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

            Assert.True(image.UsesSvgTilesForAutomation);
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

    [Fact]
    public async Task VisibleSvgTileRangeRemainsLoadingUntilEveryVisibleTileIsPresent()
    {
        var renderer = new GatedTileSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 5000,
            IntrinsicHeightDips: 1000,
            IntrinsicAspectRatio: 5));
        var image = new ImageBox(
            CreateContext(renderer, rasterizationScale: 4),
            CreateSvgDataUri(Guid.NewGuid().ToString("N")),
            "partially rendered diagram");

        try
        {
            image.Measure(5000);
            await WaitForLoadAsync(image);
            await WaitUntilAsync(() => image.UsesSvgTilesForAutomation && image.SvgRasterPixelSize == (20_000, 4_000));

            using var target = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(),
                600,
                256,
                96);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                image.Paint(drawingSession, new Rect(0, 0, 600, 256));
            }

            await renderer.SecondVisibleTileStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => image.HasAllSvgTilesForAutomation(0, 0, 0, 0));
            Assert.False(image.HasAllSvgTilesForAutomation(0, 0, 1, 0));
            Assert.False(image.HasAllSvgTilesForAutomation(-1, 0, 0, 0));
            Assert.False(image.HasAllSvgTilesForAutomation(2, 0, 1, 0));

            renderer.ReleaseSecondVisibleTile.TrySetResult();
            await WaitUntilAsync(() => image.HasAllSvgTilesForAutomation(0, 0, 1, 0));
        }
        finally
        {
            renderer.ReleaseSecondVisibleTile.TrySetResult();
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

            Assert.True(image.UsesSvgTilesForAutomation);
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
        var unavailable = new ConcurrentQueue<MarkdownSvgFailureReason?>();
        var image = new ImageBox(
            CreateContext(renderer: null, imageUnavailable: (_, _, svgReason) => unavailable.Enqueue(svgReason)),
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
            Assert.Equal(MarkdownSvgFailureReason.ArchitectureMismatch, Assert.Single(unavailable));
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

    [Fact]
    public async Task UnkeyedHostRasterLeaseKeepsClipboardBitmapAliveAfterImageDisposal()
    {
        const string source = "https://example.test/private.png";
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII=");
        var resolver = new FixedImageResolver(new MarkdownImageAsset(
            png,
            ContentType: "image/png",
            ResolvedUri: new Uri(source),
            CacheKey: string.Empty));
        var image = new ImageBox(
            CreateContext(renderer: null, imageResolver: resolver),
            source,
            "Private image");
        SharedCanvasBitmapCache.Lease? clipboardLease = null;

        try
        {
            image.Measure(96);
            await WaitForLoadAsync(image);
            Assert.NotNull(image.Bitmap);

            clipboardLease = image.AcquireBitmapLease();
            Assert.NotNull(clipboardLease);
            image.Dispose();

            Assert.Equal(1u, clipboardLease.Bitmap.SizeInPixels.Width);
            Assert.Equal(1u, clipboardLease.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            clipboardLease?.Dispose();
            image.Dispose();
        }
    }

    [Fact]
    public async Task ProgressiveVisibleImageResolutionStartsOffTheCommitThread()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII=");
        var resolver = new ThreadRecordingImageResolver(new MarkdownImageAsset(
            png, "image/png", CacheKey: $"off-thread-{Guid.NewGuid():N}"));
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var image = new ImageBox(
            CreateContext(renderer: null, imageResolver: resolver, performanceSession: session),
            "https://example.test/visible.png",
            "Visible image");

        try
        {
            int commitThread = Environment.CurrentManagedThreadId;
            await WaitForLoadAsync(image);
            Assert.NotEqual(commitThread, resolver.InvokedOnThreadId);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task DefaultVisibleImageResolutionKeepsTheExistingCallerThreadContract()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII=");
        var resolver = new ThreadRecordingImageResolver(new MarkdownImageAsset(
            png, "image/png", CacheKey: $"caller-thread-{Guid.NewGuid():N}"));
        var image = new ImageBox(
            CreateContext(renderer: null, imageResolver: resolver),
            "https://example.test/default.png",
            "Default image");

        try
        {
            int callerThread = Environment.CurrentManagedThreadId;
            await WaitForLoadAsync(image);
            Assert.Equal(callerThread, resolver.InvokedOnThreadId);
            Assert.NotNull(image.Bitmap);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task SvgSourcePreparationRunsInlineOffUiAfterConditionalDispatch()
    {
        var backgroundRenderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 10,
            IntrinsicHeightDips: 10,
            IntrinsicAspectRatio: 1,
            Description: null,
            HasText: false,
            UsesCurrentColor: false,
            UsesColorScheme: false));
        int callerThread = Environment.CurrentManagedThreadId;
        await ImageBox.RunSvgCoreOnBackgroundIfNeeded(
            dispatcherHasThreadAccess: false,
            () =>
            {
                backgroundRenderer.PrepareSource([1], CancellationToken.None);
                return Task.CompletedTask;
            });
        Assert.Equal(callerThread, backgroundRenderer.PrepareSourceThreadId);

        var dispatcherRenderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 10,
            IntrinsicHeightDips: 10,
            IntrinsicAspectRatio: 1,
            Description: null,
            HasText: false,
            UsesCurrentColor: false,
            UsesColorScheme: false));
        var prepareSourceThreadId = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherCallerCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatcherCallerThreadId = -1;
        var dispatcherCaller = new Thread(() =>
        {
            dispatcherCallerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                ImageBox.RunSvgCoreOnBackgroundIfNeeded(
                    dispatcherHasThreadAccess: true,
                    () =>
                    {
                        dispatcherRenderer.PrepareSource([1], CancellationToken.None);
                        prepareSourceThreadId.TrySetResult(
                            dispatcherRenderer.PrepareSourceThreadId);
                        return Task.CompletedTask;
                    }).GetAwaiter().GetResult();
                dispatcherCallerCompleted.TrySetResult();
            }
            catch (Exception exception)
            {
                dispatcherCallerCompleted.TrySetException(exception);
            }
        });
        dispatcherCaller.Start();

        int prepareSourceThread = await prepareSourceThreadId.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await dispatcherCallerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(dispatcherCaller.Join(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(dispatcherCallerThreadId, prepareSourceThread);
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

    private static async Task ObserveTaskForCleanupAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // A cleanup wait must observe eventual faults/timeouts without
            // hiding the assertion that originally caused test unwinding.
        }
    }

    private static MarkdownPerformanceSession CreateSingleSlotCpuPreparationSession() =>
        new(MarkdownPerformanceOptions.Progressive with
        {
            MaxConcurrentCpuPreparations = 1,
        });

    private static MarkdownLayoutContext CreateContext(
        IMarkdownSvgRenderer? renderer,
        double rasterizationScale = 1,
        IMarkdownImageResolver? imageResolver = null,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable = null,
        MarkdownPerformanceSession? performanceSession = null,
        object? performanceDocumentOwner = null)
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
            PerformanceSession = performanceSession,
            PerformanceDocumentOwner = performanceDocumentOwner,
            ImageUnavailable = imageUnavailable,
            SvgRenderer = renderer,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = rasterizationScale,
        };
    }

    private static string CreateSvgDataUri(string marker)
    {
        string svg = CreateSvgMarkup(marker);
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    private static string CreateSvgMarkup(string title) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' width='200' height='100'>" +
        $"<title>{title}</title><rect width='200' height='100'/></svg>";

    private static byte[] CreateSvgBytes(string title) =>
        Encoding.UTF8.GetBytes(CreateSvgMarkup(title));

    private static object? GetProcessSvgCacheEntry(string key)
    {
        object cache = typeof(ImageBox)
            .GetField("_svgCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        object?[] arguments = [key, null];
        bool found = Assert.IsType<bool>(cache.GetType()
            .GetMethod("TryGetValue", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(cache, arguments));
        return found ? arguments[1] : null;
    }

    private static void RemoveProcessSvgCacheEntry(string key)
    {
        object cache = typeof(ImageBox)
            .GetField("_svgCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        _ = cache.GetType()
            .GetMethod("Remove", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(cache, [key]);
    }

    private sealed class RecordingSvgRenderer(
        MarkdownSvgDocumentInfo info,
        TimeSpan? openDelay = null,
        Action<Memory<byte>, int, int>? fillPixels = null)
        : IMarkdownSvgRenderer
    {
        internal MarkdownSvgSourcePreparation? SourcePreparationOverride { get; init; }
        private int _prepareSourceThreadId;
        private int _openCount;
        private long _cacheGeneration;

        internal int PrepareSourceThreadId => Volatile.Read(ref _prepareSourceThreadId);

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _prepareSourceThreadId, Environment.CurrentManagedThreadId);
            return SourcePreparationOverride ?? MarkdownSvgSourcePreparation.Admit(source);
        }

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
            return new RecordingSvgDocument(info, RenderRequests, fillPixels);
        }
    }

    private enum TransientResourceLimitStage
    {
        PrepareSource,
        Open,
        Render,
    }

    private sealed class TransientResourceLimitSvgRenderer(
        MarkdownSvgDocumentInfo info,
        TransientResourceLimitStage failureStage) : IMarkdownSvgRenderer
    {
        private int _prepareSourceCount;
        private int _openCount;
        private int _renderCount;

        internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int RenderCount => Volatile.Read(ref _renderCount);
        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref _prepareSourceCount);
            if (failureStage == TransientResourceLimitStage.PrepareSource && ordinal == 1)
            {
                return MarkdownSvgSourcePreparation.Reject(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The test provider rejected an oversized SVG source.");
            }

            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref _openCount);
            if (failureStage == TransientResourceLimitStage.Open && ordinal == 1)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The test worker queue was temporarily full during OpenAsync.");
            }

            return ValueTask.FromResult<IMarkdownSvgDocument>(
                new TransientResourceLimitSvgDocument(info, this));
        }

        internal int RecordRenderAndShouldFail()
        {
            int ordinal = Interlocked.Increment(ref _renderCount);
            if (failureStage == TransientResourceLimitStage.Render && ordinal == 1)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The test worker queue was temporarily full during RenderAsync.");
            }

            return ordinal;
        }
    }

    private sealed class TransientResourceLimitSvgDocument(
        MarkdownSvgDocumentInfo info,
        TransientResourceLimitSvgRenderer owner) : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = owner.RecordRenderAndShouldFail();

            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int length = checked(width * height * 4);
            IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
            memory.Memory.Span[..length].Clear();
            return ValueTask.FromResult(new MarkdownSvgRaster(
                memory,
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

    private sealed class CpuAdmissionSvgRenderer : IMarkdownSvgRenderer, IDisposable
    {
        internal enum FirstPreparationFailure
        {
            None,
            Reject,
            Throw,
        }

        private readonly MarkdownSvgDocumentInfo _info;
        private readonly bool _blockFirstPreparation;
        private readonly bool _holdFirstPreparationAfterCancellation;
        private readonly bool _blockFirstOpen;
        private readonly FirstPreparationFailure _firstPreparationFailure;
        private int _prepareSourceCount;
        private int _openCount;

        internal CpuAdmissionSvgRenderer(
            MarkdownSvgDocumentInfo info,
            bool blockFirstPreparation = false,
            bool holdFirstPreparationAfterCancellation = false,
            bool blockFirstOpen = false,
            FirstPreparationFailure firstPreparationFailure = FirstPreparationFailure.None)
        {
            _info = info;
            _blockFirstPreparation = blockFirstPreparation;
            _holdFirstPreparationAfterCancellation = holdFirstPreparationAfterCancellation;
            _blockFirstOpen = blockFirstOpen;
            _firstPreparationFailure = firstPreparationFailure;
        }

        internal ManualResetEventSlim ReleaseFirstPreparation { get; } = new(initialState: false);

        internal ManualResetEventSlim ReleaseFirstPreparationAfterCancellation { get; } = new(initialState: false);

        internal TaskCompletionSource FirstPreparationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondPreparationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstPreparationCancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstOpenStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstOpen { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);

        internal int OpenCount => Volatile.Read(ref _openCount);

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            int ordinal = Interlocked.Increment(ref _prepareSourceCount);
            if (ordinal == 1)
            {
                FirstPreparationStarted.TrySetResult();
                if (_blockFirstPreparation)
                {
                    try
                    {
                        if (!ReleaseFirstPreparation.Wait(TimeSpan.FromSeconds(15), cancellationToken))
                            throw new TimeoutException("The test did not release the first SVG preparation gate.");
                    }
                    catch (OperationCanceledException)
                    {
                        FirstPreparationCancellationObserved.TrySetResult();
                        if (_holdFirstPreparationAfterCancellation)
                            ReleaseFirstPreparationAfterCancellation.Wait();
                        throw;
                    }
                }

                if (_firstPreparationFailure == FirstPreparationFailure.Reject)
                {
                    return MarkdownSvgSourcePreparation.Reject(
                        MarkdownSvgFailureReason.UnsupportedContent,
                        "Rejected by the CPU admission test renderer.");
                }
                if (_firstPreparationFailure == FirstPreparationFailure.Throw)
                    throw new InvalidOperationException("Thrown by the CPU admission test renderer.");
            }
            else if (ordinal == 2)
            {
                SecondPreparationStarted.TrySetResult();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ordinal = Interlocked.Increment(ref _openCount);
            if (ordinal == 1 && _blockFirstOpen)
            {
                FirstOpenStarted.TrySetResult();
                await ReleaseFirstOpen.Task.WaitAsync(cancellationToken);
            }

            return new RecordingSvgDocument(
                _info,
                new ConcurrentQueue<MarkdownSvgRenderRequest>());
        }

        public void Dispose()
        {
            ReleaseFirstPreparation.Set();
            ReleaseFirstPreparationAfterCancellation.Set();
            ReleaseFirstOpen.TrySetResult();
            ReleaseFirstPreparation.Dispose();
            ReleaseFirstPreparationAfterCancellation.Dispose();
        }
    }

    private sealed class ProcessPreparationSvgRenderer(bool holdOpens) : IMarkdownSvgRenderer, IDisposable
    {
        private readonly MarkdownSvgDocumentInfo _info = new(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2);
        private readonly TaskCompletionSource _releaseOpenRequests = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prepareSourceCount;
        private int _openCount;

        internal TaskCompletionSource TwoOpensStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ThreeOpensStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int PrepareSourceCount => Volatile.Read(ref _prepareSourceCount);

        internal int OpenCount => Volatile.Read(ref _openCount);

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _prepareSourceCount);
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Interlocked.Increment(ref _openCount);
            if (count >= 2)
                TwoOpensStarted.TrySetResult();
            if (count >= 3)
                ThreeOpensStarted.TrySetResult();
            if (holdOpens)
                await _releaseOpenRequests.Task.WaitAsync(cancellationToken);

            return new RecordingSvgDocument(
                _info,
                new ConcurrentQueue<MarkdownSvgRenderRequest>());
        }

        internal void ReleaseOpenRequests() => _releaseOpenRequests.TrySetResult();

        public void Dispose() => ReleaseOpenRequests();
    }

    private sealed class ConcurrentOpenSvgRenderer(MarkdownSvgDocumentInfo info)
        : IMarkdownSvgRenderer
    {
        private int _openCount;
        private int _activeOpenCount;
        private int _maximumConcurrentOpens;

        internal TaskCompletionSource TwoOpensStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseOpens { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int OpenCount => Volatile.Read(ref _openCount);

        internal int MaximumConcurrentOpens => Volatile.Read(ref _maximumConcurrentOpens);

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public async ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            int active = Interlocked.Increment(ref _activeOpenCount);
            int observedMaximum;
            while (active > (observedMaximum = Volatile.Read(ref _maximumConcurrentOpens)) &&
                   Interlocked.CompareExchange(
                       ref _maximumConcurrentOpens,
                       active,
                       observedMaximum) != observedMaximum)
            {
            }

            if (active >= 2)
                TwoOpensStarted.TrySetResult();

            try
            {
                await ReleaseOpens.Task.WaitAsync(cancellationToken);
                return new RecordingSvgDocument(info, new ConcurrentQueue<MarkdownSvgRenderRequest>());
            }
            finally
            {
                Interlocked.Decrement(ref _activeOpenCount);
            }
        }
    }

    private sealed class GatedTileSvgRenderer(
        MarkdownSvgDocumentInfo info, bool opaqueWholeBitmap = false) : IMarkdownSvgRenderer
    {
        internal bool OpaqueWholeBitmap { get; } = opaqueWholeBitmap;
        internal TaskCompletionSource SecondVisibleTileStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseSecondVisibleTile { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IMarkdownSvgDocument>(new GatedTileSvgDocument(this, info));
        }
    }

    private sealed class GatedTileSvgDocument(
        GatedTileSvgRenderer owner,
        MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;

        public async ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.TileRegion is { X: 1024, Y: 0 })
            {
                owner.SecondVisibleTileStarted.TrySetResult();
                await owner.ReleaseSecondVisibleTile.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int length = checked(width * height * 4);
            IMemoryOwner<byte> memory = MemoryPool<byte>.Shared.Rent(length);
            memory.Memory.Span[..length].Fill(
                request.TileRegion is null && owner.OpaqueWholeBitmap ? (byte)255 : (byte)0);
            return new MarkdownSvgRaster(
                memory,
                length,
                width,
                height,
                checked(width * 4),
                request.PixelFormat);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingSvgDocument(
        MarkdownSvgDocumentInfo info,
        ConcurrentQueue<MarkdownSvgRenderRequest> requests,
        Action<Memory<byte>, int, int>? fillPixels = null)
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
            fillPixels?.Invoke(owner.Memory[..length], width, height);
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

    private sealed class NotHandledImageResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(MarkdownImageResolution.NotHandled);
        }
    }

    private sealed class ThreadRecordingImageResolver(MarkdownImageAsset asset) : IMarkdownImageResolver
    {
        internal int InvokedOnThreadId { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvokedOnThreadId = Environment.CurrentManagedThreadId;
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(asset));
        }
    }
}
