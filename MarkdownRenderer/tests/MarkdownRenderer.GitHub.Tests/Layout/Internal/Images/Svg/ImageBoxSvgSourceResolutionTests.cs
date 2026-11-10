using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Performance;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageBoxSvgRendererFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg;

/// <summary>
/// Checks how SVG image sources are resolved: host preflight of embedded
/// raster media types, resolved raster payloads, host raster leases, and
/// the thread visible resolution and source preparation run on.
/// </summary>
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxSvgSourceResolutionTests
{
    private readonly MarkdownRenderResources _renderResources = new();

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
        var image = new ImageBox(CreateContext(_renderResources, renderer), _renderResources.Images, source, "Organization avatar");

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
            CreateContext(_renderResources, renderer, imageResolver: resolver), _renderResources.Images,
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
            CreateContext(_renderResources, renderer: null, imageResolver: resolver), _renderResources.Images,
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
            CreateContext(_renderResources, renderer: null, imageResolver: resolver, performanceSession: session), _renderResources.Images,
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
            CreateContext(_renderResources, renderer: null, imageResolver: resolver), _renderResources.Images,
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
        await SvgWorkDispatch.RunOffDispatcher(
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
                SvgWorkDispatch.RunOffDispatcher(
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
