using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how an SVG image box sizes and paints its raster: measured DIPs
/// and exact rasterization scale, fractional viewports and last tiles,
/// tile replacement regions, tile clip failure, and failure categories.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgRendererTests
{
    private readonly MarkdownRenderResources _renderResources = new();

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
            CreateContext(_renderResources, renderer, rasterizationScale), _renderResources.Images,
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
        var first = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, string.Empty);
        var second = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, string.Empty);
        var repeat = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, string.Empty);
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
        var image = new ImageBox(CreateContext(_renderResources, renderer, scale), _renderResources.Images,
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
        var image = new ImageBox(CreateContext(_renderResources, renderer, scale), _renderResources.Images,
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
            await WaitUntilAsync(() => SvgTileCacheTestOperations.HasAllTiles(image, tileIndex, 0, tileIndex, 0));
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
            var previous = new ImageBox(CreateContext(_renderResources, renderer, rasterizationScale: 0.5), _renderResources.Images, source, string.Empty);
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

        var image = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, string.Empty);
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
            await WaitUntilAsync(() => SvgTileCacheTestOperations.HasAllTiles(image, 0, 0, 0, 0));
            Assert.False(SvgTileCacheTestOperations.HasAllTiles(image, 0, 0, 1, 0));
            Color[] partial = PaintVisible();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < 1024; x++)
                    Assert.Equal(background, partial[y * 2048 + x]);
            Assert.NotEqual(background, partial[4 * 2048 + 1536]);
            if (previousWholeBitmap)
                Assert.Equal(Color.FromArgb(255, 255, 255, 255), partial[4 * 2048 + 1536]);

            renderer.ReleaseSecondVisibleTile.TrySetResult();
            await WaitUntilAsync(() => SvgTileCacheTestOperations.HasAllTiles(image, 0, 0, 1, 0));
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
        var host = new TestSvgTileCacheHost();
        var current = new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>();
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        Color background = Color.FromArgb(255, 31, 81, 121);
        Color previous = Color.FromArgb(255, 255, 255, 255);
        try
        {
            // The previous good grid becomes the cache's fallback when a plan
            // with another identity is accepted over it.
            SvgTileRenderPlan previousPlan = TestSvgTileCacheHost.Plan(
                "previous-grid", columns * tilePixels, rows * tilePixels);
            host.Accept(previousPlan);
            // Deliberately reverse insertion order. Forty replacement tiles
            // exercise the pooled coverage buffer, with holes in every row.
            for (int index = columns * rows - 1; index >= 0; index--)
            {
                int x = index % columns;
                int y = index / columns;
                var key = (x * tilePixels, y * tilePixels, tilePixels, tilePixels);
                host.PublishTile(previousPlan, key, _renderResources.Images.BitmapCache.CreateUncachedLease(
                    CanvasBitmap.CreateFromColors(device, [previous], 1, 1)));
                if ((x + y) % 2 == 0)
                    current.Add(key, _renderResources.Images.BitmapCache.CreateUncachedLease(
                        CanvasBitmap.CreateFromColors(device, [Color.FromArgb(0, 0, 0, 0)], 1, 1)));
            }
            host.Accept(TestSvgTileCacheHost.Plan("replacement-grid", columns * tilePixels, rows * tilePixels));
            var destination = new Rect(0, 0, columns * displayTile, rows * displayTile);
            using var target = new CanvasRenderTarget(device,
                columns * displayTile, rows * displayTile, 96);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(background);
                SvgTilePainter.PaintLayer(drawing, _renderResources.PaintSessions, current, destination, destination,
                    columns * tilePixels, rows * tilePixels, null, host.Cache, useFallbackTiles: true);
                Assert.Equal(CanvasAntialiasing.Antialiased, drawing.Antialiasing);
            }
            Color[] pixels = target.GetPixelColors();
            for (int y = 0; y < rows * displayTile; y++)
                for (int x = 0; x < columns * displayTile; x++)
                    Assert.Equal((x / displayTile + y / displayTile) % 2 == 0 ? background : previous,
                        pixels[y * columns * displayTile + x]);
            Assert.Equal(0, host.GapFillCount);
        }
        finally
        {
            foreach (SharedCanvasBitmapCache.Lease lease in current.Values)
                lease.Dispose();
            // The fallback grid is owned by the cache; its detach path
            // releases those native bitmap leases.
            host.ReleaseAll();
            Assert.Empty(host.RetirementFailures);
        }
    }

    [Theory]
    [InlineData(CanvasAntialiasing.Antialiased)]
    [InlineData(CanvasAntialiasing.Aliased)]
    public async Task TileClipFactoryFailureRestoresDrawingModeAndDoesNotQueueRendering(CanvasAntialiasing antialiasing)
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(20_000, 8, 2500));
        var retirement = new NativeResourceRetirement(createTimer: false);
        var injected = new InvalidOperationException("injected tile clip factory failure");
        var resources = new MarkdownRenderResources(retirement, afterPaintLayerReservation: () => throw injected);
        var image = new ImageBox(CreateContext(resources, renderer), resources.Images,
            CreateSvgDataUri(Guid.NewGuid().ToString("N")), string.Empty);
        try
        {
            image.Measure(20_000);
            await WaitForLoadAsync(image);
            image.Measure(20_000);
            image.Arrange(0, 0, 20_000);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 64, 8, 96);
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            drawing.Antialiasing = antialiasing;
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
}
