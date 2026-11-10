using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// These ownership checks observe the test's own preparation gate. Run them
// without unrelated image decodes and drain asynchronous retirement between
// cases so the process raster-decode admission starts from a quiet baseline.
[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class RasterDecoderImageBoxTests : IAsyncLifetime
{
    private readonly MarkdownRenderResources _renderResources = new();

    public Task InitializeAsync() => WaitForAdmissionIdleAsync();

    public Task DisposeAsync() => WaitForAdmissionIdleAsync();

    private Task WaitForAdmissionIdleAsync() =>
        DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
            _renderResources.Images.Preparation.ActiveCount == 0 &&
            _renderResources.Images.Preparation.ActiveBytes == 0 &&
            _renderResources.Images.Preparation.PendingRequests == 0);

    [Fact]
    public async Task RegisteredProviderGetsExactDisplayPlanAndPixelOwnerIsDisposedAfterUpload()
    {
        byte[] source = MinimalWebPFixture.Create(640, 360);
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var image = CreateImage(source, decoder, scale: 1);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, decoder.Calls);
            MarkdownRasterDecodeRequest request = Assert.IsType<MarkdownRasterDecodeRequest>(decoder.LastRequest);
            var sizing = new RasterImageSizingRequest(200, 1, null, null,
                RasterImageResourceBudget.MaxPixelsPerFrame);
            Assert.True(RasterImageDecodePlan.TryCreate(
                source, RasterImageResourceBudget.Validate(source), sizing, out RasterImageDecodePlan plan));
            Assert.Equal(MarkdownRasterFormat.WebP, request.Format);
            Assert.Equal(plan.OutputWidth, request.TargetWidthPixels);
            Assert.Equal(plan.OutputHeight, request.TargetHeightPixels);
            Assert.Equal(source, request.EncodedBytes.ToArray());
            Assert.Equal(IntPtr.Size == 4 ? 32L * 1024 * 1024 : 64L * 1024 * 1024,
                request.MaximumWorkingBytes);
            Assert.Equal(MarkdownRasterPixelFormat.Bgra8Premultiplied, request.PixelFormat);
            Assert.Equal((uint)plan.OutputWidth, image.Bitmap!.SizeInPixels.Width);
            Assert.Equal((uint)plan.OutputHeight, image.Bitmap.SizeInPixels.Height);
            Assert.True(decoder.LastOwner!.IsDisposed,
                "The provider-owned pixel mapping must retire immediately after synchronous Win2D upload.");
        }
        finally
        {
            image.Dispose();
        }

        Assert.Equal(0, decoder.DisposeCount);
    }

    [Fact]
    public async Task ProviderHandlesAdmittedOversizedStaticPreviewAtDisplayDimensions()
    {
        const int sourceWidth = 2048;
        const int sourceHeight = 8193;
        byte[] source = MinimalWebPFixture.Create(sourceWidth, sourceHeight);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview);

        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                UseDisplaySizedRasterDecode = false,
                MaxRasterOutputPixels = 65_536,
            });
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var image = CreateImage(source, decoder, scale: 1, session: session);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            MarkdownRasterDecodeRequest request = Assert.IsType<MarkdownRasterDecodeRequest>(decoder.LastRequest);
            var sizing = new RasterImageSizingRequest(200, 1, null, null, 65_536);
            Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out RasterImageDecodePlan plan));
            Assert.Equal(plan.OutputWidth, request.TargetWidthPixels);
            Assert.Equal(plan.OutputHeight, request.TargetHeightPixels);
            Assert.True((long)request.TargetWidthPixels * request.TargetHeightPixels <= 65_536);
            Assert.NotEqual((int)budget.Width, request.TargetWidthPixels);
            Assert.NotEqual((int)budget.Height, request.TargetHeightPixels);
            Assert.True(decoder.LastOwner!.IsDisposed);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task UnsupportedProviderFormatLeavesExistingPlatformDecodePathAlone()
    {
        byte[] source = DisplaySizedRasterDecodeTests.CreatePng(32, 16);
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var image = CreateImage(source, decoder, scale: 1);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(0, decoder.Calls);
            Assert.Equal(32u, image.Bitmap!.SizeInPixels.Width);
            Assert.Equal(16u, image.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Theory]
    [InlineData(InvalidProviderOutput.Null)]
    [InlineData(InvalidProviderOutput.WrongGeometry)]
    [InlineData(InvalidProviderOutput.WrongPixelFormat)]
    public async Task InvalidProviderOutputFailsClosedAndReleasesAnyOwner(InvalidProviderOutput output)
    {
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP) { InvalidOutput = output };
        var image = CreateImage(MinimalWebPFixture.Create(64, 32), decoder, scale: 1);
        try
        {
            image.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);

            Assert.Null(image.Bitmap);
            Assert.Equal(1, decoder.Calls);
            if (output == InvalidProviderOutput.Null)
                Assert.Null(decoder.LastOwner);
            else
                Assert.True(decoder.LastOwner!.IsDisposed);
            await WaitForAdmissionIdleAsync();
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task SelectedProviderFailureDoesNotFallBackAndTypedCancellationIsNotPublishedAsFailure()
    {
        var failedDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            Failure = new MarkdownRasterException(MarkdownRasterFailureReason.UnsupportedContent),
        };
        var failedImage = CreateImage(MinimalWebPFixture.Create(64, 32), failedDecoder, scale: 1);
        try
        {
            failedImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(failedImage);
            Assert.Null(failedImage.Bitmap);
            Assert.Equal(1, failedDecoder.Calls);
        }
        finally
        {
            failedImage.Dispose();
        }

        int unavailable = 0;
        var canceledDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            Failure = new MarkdownRasterException(MarkdownRasterFailureReason.Canceled),
        };
        var canceledImage = CreateImage(
            MinimalWebPFixture.Create(64, 32), canceledDecoder, scale: 1,
            imageUnavailable: (_, _, _) => Interlocked.Increment(ref unavailable));
        try
        {
            canceledImage.Measure(200);
            canceledImage.EnsureLoading();
            await canceledDecoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAdmissionIdleAsync();

            Assert.Null(canceledImage.Bitmap);
            Assert.Equal(0, Volatile.Read(ref unavailable));
            Assert.Equal(1, canceledDecoder.Calls);
        }
        finally
        {
            canceledImage.Dispose();
        }
    }

    [Fact]
    public async Task RasterDecodeFailureIsPartitionedByProviderIdentity()
    {
        byte[] source = MinimalWebPFixture.Create(64, 32);
        string cacheKey = $"raster-provider-retry-{Guid.NewGuid():N}";
        var firstDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            Failure = new MarkdownRasterException(MarkdownRasterFailureReason.UnsupportedContent),
        };

        var firstImage = CreateImage(source, firstDecoder, scale: 1, cacheKey: cacheKey);
        try
        {
            firstImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(firstImage);

            Assert.Null(firstImage.Bitmap);
            Assert.Equal(1, firstDecoder.Calls);
        }
        finally
        {
            firstImage.Dispose();
        }

        // The same failed provider remains negatively cached for this exact
        // source/format, avoiding repeated deterministic decoder work.
        var repeatedImage = CreateImage(source, firstDecoder, scale: 1, cacheKey: cacheKey);
        try
        {
            repeatedImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(repeatedImage);

            Assert.Null(repeatedImage.Bitmap);
            Assert.Equal(1, firstDecoder.Calls);
        }
        finally
        {
            repeatedImage.Dispose();
        }

        // Installing a different provider must not inherit the first
        // provider's failure for an otherwise identical resolved asset.
        var replacementDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP);
        var replacementImage = CreateImage(source, replacementDecoder, scale: 1, cacheKey: cacheKey);
        try
        {
            replacementImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(replacementImage);

            Assert.NotNull(replacementImage.Bitmap);
            Assert.Equal(1, replacementDecoder.Calls);
        }
        finally
        {
            replacementImage.Dispose();
        }

        Assert.Equal(0, firstDecoder.DisposeCount);
        Assert.Equal(0, replacementDecoder.DisposeCount);
    }

    [Fact]
    public async Task ResourceLimitFailureCanRetrySameProviderAtSmallerDisplaySize()
    {
        byte[] source = MinimalWebPFixture.Create(640, 360);
        string cacheKey = $"raster-provider-smaller-retry-{Guid.NewGuid():N}";
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            FailAboveTargetWidth = 300,
        };

        var oversizedImage = CreateImage(source, decoder, scale: 2, cacheKey: cacheKey);
        try
        {
            oversizedImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(oversizedImage);

            Assert.NotNull(decoder.LastRequest);
            Assert.Equal((400, 225),
                (decoder.LastRequest!.TargetWidthPixels, decoder.LastRequest.TargetHeightPixels));
            Assert.Null(oversizedImage.Bitmap);
            Assert.Equal(1, decoder.Calls);
        }
        finally
        {
            oversizedImage.Dispose();
        }

        var smallerImage = CreateImage(source, decoder, scale: 1, cacheKey: cacheKey);
        try
        {
            smallerImage.Measure(200);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(smallerImage);

            Assert.NotNull(decoder.LastRequest);
            Assert.Equal((200, 113),
                (decoder.LastRequest!.TargetWidthPixels, decoder.LastRequest.TargetHeightPixels));
            Assert.NotNull(smallerImage.Bitmap);
            Assert.Equal(2, decoder.Calls);
        }
        finally
        {
            smallerImage.Dispose();
        }
    }

    [Fact]
    public async Task StaleGenerationDisposesLateProviderPixelsAndReleasesAdmission()
    {
        var decoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            PendingResult = new TaskCompletionSource<MarkdownRaster>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var image = CreateImage(MinimalWebPFixture.Create(64, 32), decoder, scale: 1);
        image.Measure(200);
        try
        {
            image.EnsureLoading();
            await decoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, _renderResources.Images.Preparation.ActiveCount);
            Assert.True(_renderResources.Images.Preparation.ActiveBytes > 0);

            image.Dispose();
            MarkdownRaster lateRaster = decoder.CreateRaster(decoder.LastRequest!);
            decoder.PendingResult!.SetResult(lateRaster);
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => decoder.LastOwner!.IsDisposed);
            await WaitForAdmissionIdleAsync();
            Assert.Null(image.Bitmap);
        }
        finally
        {
            image.Dispose();
            decoder.PendingResult!.TrySetCanceled();
        }
    }

    [Fact]
    public async Task LateRasterFromReplacedDocumentCannotChangeCurrentCachedLocalImage()
    {
        // Warm a valid local PNG before starting the blocked old-document decode.
        // The replacement ImageBox can then acquire that admitted bitmap while
        // the old request is still in flight, independent of decode concurrency.
        byte[] replacementBytes = DisplaySizedRasterDecodeTests.CreatePng(71, 37);
        string replacementSource = "data:image/png;base64," + Convert.ToBase64String(replacementBytes);
        await WarmLocalImageAsync(replacementSource);

        var oldDecoder = new FakeRasterDecoder(MarkdownRasterFormat.WebP)
        {
            PendingResult = new TaskCompletionSource<MarkdownRaster>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var oldNotifications = new LateImageNotifications();
        ImageBox oldImage = CreateCountedImage(oldDecoder, oldNotifications);
        ImageBox? currentImage = null;

        try
        {
            oldImage.Measure(71);
            oldImage.EnsureLoading();
            await oldDecoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(_renderResources.Images.Preparation.ActiveCount > 0);
            Assert.True(_renderResources.Images.Preparation.ActiveBytes > 0);

            // Replacing the document retires the old image box before the
            // uncooperative decoder completes. The replacement is an admitted
            // local source (a warm device-cache lease), not a network fixture.
            oldImage.Dispose();
            currentImage = new ImageBox(
                DisplaySizedRasterDecodeTests.CreateContext(_renderResources, resolver: null!, session: null, scale: 1), _renderResources.Images,
                replacementSource,
                "Current document local image");
            currentImage.Measure(71);
            Assert.NotNull(currentImage.Bitmap);
            var currentBitmap = currentImage.Bitmap!;
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, currentImage.AccessibilityState);
            Assert.Equal(71u, currentBitmap.SizeInPixels.Width);
            Assert.Equal(37u, currentBitmap.SizeInPixels.Height);

            MarkdownRaster lateRaster = oldDecoder.CreateRaster(oldDecoder.LastRequest!);
            TrackingMemoryOwner oldOwner = Assert.IsType<TrackingMemoryOwner>(oldDecoder.LastOwner);
            Assert.True(oldDecoder.PendingResult!.TrySetResult(lateRaster));

            await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => oldOwner.IsDisposed);
            await WaitForAdmissionIdleAsync();

            Assert.Equal(1, oldOwner.DisposeCount);
            Assert.Null(oldImage.Bitmap);
            Assert.Same(currentBitmap, currentImage.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, currentImage.AccessibilityState);
            Assert.Equal(0, Volatile.Read(ref oldNotifications.Unavailable));
            Assert.Equal(0, Volatile.Read(ref oldNotifications.Publications));
            Assert.Equal(0, _renderResources.Images.Preparation.PendingRequests);
        }
        finally
        {
            oldImage.Dispose();
            currentImage?.Dispose();
            if (oldDecoder.PendingResult is { } pending && !pending.Task.IsCompleted)
            {
                if (oldDecoder.LastRequest is { } request)
                    pending.TrySetResult(oldDecoder.CreateRaster(request));
                else
                    pending.TrySetCanceled();
            }
            await WaitForAdmissionIdleAsync();
        }
    }

    private async Task WarmLocalImageAsync(string replacementSource)
    {
        var warmImage = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(_renderResources, resolver: null!, session: null, scale: 1), _renderResources.Images,
            replacementSource,
            "Current document local image");
        try
        {
            warmImage.Measure(71);
            if (warmImage.Bitmap is null)
                await DisplaySizedRasterDecodeTests.WaitForLoadAsync(warmImage);
            Assert.NotNull(warmImage.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, warmImage.AccessibilityState);
        }
        finally
        {
            warmImage.Dispose();
        }
    }

    private ImageBox CreateCountedImage(FakeRasterDecoder decoder, LateImageNotifications notifications)
    {
        ImageBox image = CreateImage(
            MinimalWebPFixture.Create(64, 32),
            decoder,
            scale: 1,
            imageUnavailable: (_, _, _) => Interlocked.Increment(ref notifications.Unavailable));
        image.LoadCompleted += (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                Interlocked.Increment(ref notifications.Publications);
        };
        return image;
    }

    private sealed class LateImageNotifications
    {
        internal int Unavailable;
        internal int Publications;
    }

    private ImageBox CreateImage(
        byte[] source,
        IMarkdownRasterDecoder? decoder,
        double scale,
        MarkdownPerformanceSession? session = null,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable = null,
        string? cacheKey = null)
    {
        var resolver = new RasterResolver(source, cacheKey ?? $"raster-provider-{Guid.NewGuid():N}");
        MarkdownLayoutContext context = DisplaySizedRasterDecodeTests.CreateContext(_renderResources, 
            resolver, session, scale, imageUnavailable: imageUnavailable, rasterDecoder: decoder);
        return new ImageBox(context, context.ImageResources, "https://images.example/asset", string.Empty);
    }

    private sealed class RasterResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/webp", CacheKey: cacheKey)));
    }

    public enum InvalidProviderOutput
    {
        None,
        Null,
        WrongGeometry,
        WrongPixelFormat,
    }
}
