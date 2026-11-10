using MarkdownRenderer.GitHub.Tests.Performance;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg;

/// <summary>
/// Checks tiled SVG output: relayout keeps the previous bitmap until the
/// scaled replacement arrives, oversized output uses visible physical
/// pixel tiles, and visible ranges stay loading until every tile is
/// present.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgTiledOutputTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task RelayoutKeepsPreviousDeviceBitmapUntilScaledReplacementArrives()
    {
        var renderer = new RecordingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        string source = CreateSvgDataUri(Guid.NewGuid().ToString("N"));

        var first = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "transition");
        first.Measure(80);
        await WaitForLoadAsync(first);
        first.Dispose();

        int rendersBeforeScaleChange = renderer.RenderRequests.Count;
        var scaled = new ImageBox(
            CreateContext(_renderResources, renderer, rasterizationScale: 2), _renderResources.Images,
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
            CreateContext(_renderResources, renderer, rasterizationScale: 4), _renderResources.Images,
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
            CreateContext(_renderResources, renderer, rasterizationScale: 4), _renderResources.Images,
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
            await WaitUntilAsync(() => SvgTileCacheTestOperations.HasAllTiles(image, 0, 0, 0, 0));
            Assert.False(SvgTileCacheTestOperations.HasAllTiles(image, 0, 0, 1, 0));
            Assert.False(SvgTileCacheTestOperations.HasAllTiles(image, -1, 0, 0, 0));
            Assert.False(SvgTileCacheTestOperations.HasAllTiles(image, 2, 0, 1, 0));

            renderer.ReleaseSecondVisibleTile.TrySetResult();
            await WaitUntilAsync(() => SvgTileCacheTestOperations.HasAllTiles(image, 0, 0, 1, 0));
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
            CreateContext(_renderResources, renderer), _renderResources.Images,
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
}
