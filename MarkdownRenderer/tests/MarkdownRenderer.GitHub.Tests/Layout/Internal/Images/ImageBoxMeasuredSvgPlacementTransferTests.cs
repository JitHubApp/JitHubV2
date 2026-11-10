using System.Buffers;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.GitHub.Tests.Performance;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images;

[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxMeasuredSvgPlacementTransferTests
{
    private readonly MarkdownRenderResources _renderResources = new();
    private const float Width = 320f;
    private const string SvgMarkup =
        "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'><text x='0' y='7'>x</text></svg>";
    [Fact]
    public async Task TransferMovesFullSvgPlacementWithoutChangingSourceIdentityOrProviderState()
    {
        var renderer = new TransferSvgRenderer(hasText: true);
        var resolver = new TransferSvgResolver(System.Text.Encoding.UTF8.GetBytes(SvgMarkup));
        MarkdownLayoutContext sourceContext = CreateSvgContext(renderer, resolver);
        var source = new ImageBox(sourceContext, sourceContext.ImageResources, resolver.Url, "Text SVG");
        MarkdownLayoutContext targetContext = CreateTargetContext(sourceContext);
        ImageBox? prepared = null;
        LayoutSnapshot? snapshot = null;
        byte[] sourceIdentityBytes = Array.Empty<byte>();

        try
        {
            await LoadAndMeasureAsync(source);
            sourceIdentityBytes = RetainSvgSourceIdentity(source);
            Assert.NotNull(source.Bitmap);
            Assert.DoesNotContain(renderer.Requests, request => request.TileRegion is not null);

            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedLease,
                out CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            Assert.NotNull(svgFence);
            PrepareAtDifferentOrigin(source, prepared);
            snapshot = CreateSnapshot(source, sourceContext.SourceMap);

            SharedCanvasBitmapCache.Lease sourceLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(source.Presentation.Lease);
            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);

            Assert.NotSame(sourceLease, prepared.Presentation.Lease);
            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.True(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    svgFence,
                    renderer.CacheGeneration,
                    Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.Same(sourceBitmap, prepared.Bitmap);
            Assert.Equal(preparedBounds, source.Bounds);
            Assert.Equal(sourceBounds, prepared.Bounds);
            Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
            GC.KeepAlive(sourceIdentityBytes);
        }
        finally
        {
            await RetireAsync(snapshot, source, targetContext, prepared);
        }
    }

    [Fact]
    public async Task TransferRejectsTextSvgAfterProviderFontGenerationChanges()
    {
        var renderer = new TransferSvgRenderer(hasText: true);
        var resolver = new TransferSvgResolver(System.Text.Encoding.UTF8.GetBytes(SvgMarkup));
        MarkdownLayoutContext sourceContext = CreateSvgContext(renderer, resolver);
        var source = new ImageBox(sourceContext, sourceContext.ImageResources, resolver.Url, "Text SVG");
        MarkdownLayoutContext targetContext = CreateTargetContext(sourceContext);
        ImageBox? prepared = null;
        LayoutSnapshot? snapshot = null;
        byte[] sourceIdentityBytes = Array.Empty<byte>();

        try
        {
            await LoadAndMeasureAsync(source);
            sourceIdentityBytes = RetainSvgSourceIdentity(source);
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedLease,
                out CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            Assert.NotNull(svgFence);
            PrepareAtDifferentOrigin(source, prepared);
            snapshot = CreateSnapshot(source, sourceContext.SourceMap);

            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);
            renderer.AdvanceGeneration();

            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.False(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    svgFence,
                    renderer.CacheGeneration,
                    Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.Same(sourceBitmap, prepared.Bitmap);
            Assert.Equal(sourceBounds, source.Bounds);
            Assert.Equal(preparedBounds, prepared.Bounds);
            Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
            GC.KeepAlive(sourceIdentityBytes);
        }
        finally
        {
            await RetireAsync(snapshot, source, targetContext, prepared);
        }
    }

    private static MarkdownLayoutContext CreateTargetContext(MarkdownLayoutContext sourceContext) =>
        sourceContext.CreateRasterPlacementContext(
            CancellationToken.None,
            sourceContext.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);

    private static async Task RetireAsync(
        LayoutSnapshot? snapshot,
        ImageBox source,
        MarkdownLayoutContext targetContext,
        ImageBox? prepared)
    {
        if (snapshot is not null)
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        else
        {
            source.Dispose();
            await source.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (prepared is not null)
        {
            targetContext.DiscardConstructionBlock(prepared);
            await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private MarkdownLayoutContext CreateSvgContext(
        TransferSvgRenderer renderer,
        TransferSvgResolver resolver)
    {
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
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
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            RenderResources = _renderResources,
            ImageResolver = resolver,
            SvgRenderer = renderer,
            ImagePreparationOwner = new object(),
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
        };
        return context;
    }

    private static async Task LoadAndMeasureAsync(ImageBox image)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completion.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            _ = image.Measure(Width);
            image.Arrange(5, 8, Width);
            image.EnsureLoading();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => image.GetWorkState().ActiveWorkCount == 0);
            Assert.NotNull(image.Bitmap);
            _ = image.Measure(Width);
            image.Arrange(5, 8, Width);
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static void PrepareAtDifferentOrigin(ImageBox source, ImageBox prepared)
    {
        _ = source.Measure(Width);
        source.Arrange(11, 17, Width);
        _ = prepared.Measure(Width);
        prepared.Arrange(29, 43, Width);
    }

    private static LayoutSnapshot CreateSnapshot(ImageBox source, MarkdownSourceMap sourceMap) =>
        new(new BlockBox[] { source }, sourceMap, Width, (float)source.Bounds.Height);

    private static byte[] RetainSvgSourceIdentity(ImageBox image)
    {
        WeakReference<byte[]> weak = Assert.IsType<WeakReference<byte[]>>(image.Svg.SourceIdentity);
        Assert.True(weak.TryGetTarget(out byte[]? source));
        return Assert.IsType<byte[]>(source);
    }

    private sealed class TransferSvgRenderer(bool hasText) : IMarkdownSvgRenderer
    {
        private long _cacheGeneration;
        private readonly MarkdownSvgDocumentInfo _info = new(
            IntrinsicWidthDips: 16,
            IntrinsicHeightDips: 8,
            IntrinsicAspectRatio: 2,
            Description: "Transfer test SVG",
            HasText: hasText);

        internal int OpenCount { get; private set; }
        internal int RenderCount { get; private set; }
        internal List<MarkdownSvgRenderRequest> Requests { get; } = new();
        public long CacheGeneration => Interlocked.Read(ref _cacheGeneration);
        internal void AdvanceGeneration() => Interlocked.Increment(ref _cacheGeneration);

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
            OpenCount++;
            return ValueTask.FromResult<IMarkdownSvgDocument>(new TransferSvgDocument(
                _info,
                Requests,
                () => RenderCount++));
        }
    }

    private sealed class TransferSvgDocument(
        MarkdownSvgDocumentInfo info,
        List<MarkdownSvgRenderRequest> requests,
        Action rendered) : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;
        public void Dispose() { }

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            rendered();
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int stride = checked(width * 4);
            int byteCount = checked(stride * height);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(byteCount);
            owner.Memory.Span[..byteCount].Clear();
            return ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                byteCount,
                width,
                height,
                stride,
                MarkdownSvgPixelFormat.Rgba8Premultiplied));
        }
    }
}
