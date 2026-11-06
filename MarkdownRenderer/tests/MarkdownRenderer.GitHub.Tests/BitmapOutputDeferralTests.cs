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
using Windows.Graphics.DirectX;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <remarks>
/// This file is longer than 500 lines because every case drives the same
/// saturated bitmap output ledger through a real <see cref="ImageBox"/>, and
/// they share the saturation, drained-deferral wait, storm and disposal
/// helpers that keep each case's cleanup ordered.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class BitmapOutputDeferralTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task RasterOutputDeferralDrainsSourceWorkAndRetriesAfterCapacityReturns()
    {
        BitmapOutputAdmission admission = _renderResources.Images.BitmapOutput;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        using var session = CreateProgressiveSession();
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(2, 1),
            "bitmap-output-raster-" + Guid.NewGuid().ToString("N"),
            "image/png");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(_renderResources, resolver, session, scale: 1), _renderResources.Images,
            "https://images.example/admission-raster.png",
            "admitted raster");
        var completed = new TaskCompletionSource<LoadCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completed.TrySetResult(args);
        };
        image.LoadCompleted += handler;

        try
        {
            image.Measure(2);
            image.EnsureLoading();
            await WaitForDeferredAndDrainedAsync(image, () => resolver.ResolveCount);

            Assert.Null(image.Bitmap);
            Assert.Equal(0, image.GetWorkState().ActiveWorkCount);
            Assert.False(completed.Task.IsCompleted);
            Assert.Equal(1, resolver.ResolveCount);

            ReleaseReservations(blockers);
            LoadCompletedEventArgs result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);

            Assert.True(result.LayoutInvalidated);
            CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
            Assert.Equal(2u, bitmap.SizeInPixels.Width);
            Assert.Equal(1u, bitmap.SizeInPixels.Height);
            using SharedCanvasBitmapCache.Lease lease =
                Assert.IsType<SharedCanvasBitmapCache.Lease>(image.AcquireBitmapLease());
            Assert.True(lease.IsBitmapOutputAdmitted);
            Assert.True(resolver.ResolveCount >= 2,
                "The byte-source reservation was drained before the source-free waiter re-resolved the image.");
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.False(image.OutputDeferral.GetState().Deferred);
        }
        finally
        {
            image.LoadCompleted -= handler;
            await DisposeImageAsync(image);
            ReleaseReservations(blockers);
        }
    }

    [Fact]
    public async Task RasterStormStaysWithinSharedLedgerAndAdvancesAfterResidencyRelease()
    {
        const int imageCount = 72;
        const int outputWidth = 512;
        const int outputHeight = 512;
        const long bytesPerOutput = (long)outputWidth * outputHeight * 4;

        BitmapOutputAdmission admission = _renderResources.Images.BitmapOutput;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        var items = new List<RasterStormItem>(imageCount);
        MarkdownPerformanceSession? session = null;
        RasterPreparationChargeTrace? rasterTrace = null;

        try
        {
            ReleaseReservations(blockers);
            await WaitUntilAsync(() => admission.GetSnapshot().ChargedBytes == 0);
            BitmapOutputAdmission.Snapshot empty = admission.GetSnapshot();
            Assert.True(bytesPerOutput * imageCount > empty.CapacityBytes,
                "The fixture must request more live raster bytes than the scope's ledger can admit.");
            Assert.True(bytesPerOutput * imageCount > empty.SpeculativeLimitBytes,
                "The fixture must exceed the background/speculative tier as well as total capacity.");

            session = CreateProgressiveSession();
            rasterTrace = new RasterPreparationChargeTrace(admission);
            StartRasterStorm(items, session, imageCount, outputWidth, outputHeight);

            await WaitUntilAsync(
                () => items.All(IsStormItemSettled),
                TimeSpan.FromMinutes(2));

            (RasterStormItem[] initiallyDeferred, RasterStormItem[] initiallyAdmitted) =
                AssertInitialStormSplitsIntoDeferredAndAdmitted(items, outputWidth, outputHeight);

            // Release the first admitted cohort as offscreen residency work.
            // Their cache-only references are then trimmed so capacity can
            // advance the source-free denied cohort.
            await SuspendAndReleaseStormResourcesAsync(initiallyAdmitted);
            Assert.All(initiallyAdmitted, item => Assert.Null(item.Image.Bitmap));
            _renderResources.Images.BitmapCache.Clear();

            await Task.WhenAll(items.Select(item => item.Published.Task))
                .WaitAsync(TimeSpan.FromMinutes(2));
            await WaitUntilAsync(
                () => items.All(item => item.SuccessfulPublications > 0 &&
                    item.Image.GetWorkState().ActiveWorkCount == 0),
                TimeSpan.FromMinutes(2));

            AssertStormPublishedWithinLedger(
                items, initiallyDeferred, rasterTrace, empty, imageCount, outputWidth, outputHeight);

            // Explicitly release the final wave too; successful publication
            // and cleanup are both part of this exercise.
            await SuspendAndReleaseStormResourcesAsync(items);
            _renderResources.Images.BitmapCache.Clear();
            await WaitUntilAsync(() => admission.GetSnapshot().ChargedBytes == 0);
            Assert.All(items, item => Assert.Null(item.Image.Bitmap));
        }
        finally
        {
            await DisposeRasterStormAsync(items, rasterTrace, session, blockers);
        }
    }

    private void StartRasterStorm(
        List<RasterStormItem> items,
        MarkdownPerformanceSession session,
        int imageCount,
        int outputWidth,
        int outputHeight)
    {
        byte[] png = DisplaySizedRasterDecodeTests.CreatePng(outputWidth, outputHeight);
        for (int index = 0; index < imageCount; index++)
        {
            var resolver = new CountingResolver(
                png,
                $"bitmap-output-storm-{index}-{Guid.NewGuid():N}",
                "image/png");
            var image = new ImageBox(
                DisplaySizedRasterDecodeTests.CreateContext(_renderResources, resolver, session, scale: 1), _renderResources.Images,
                $"https://images.example/storm/{index}.png",
                $"storm image {index}");
            var item = new RasterStormItem(image, resolver);
            items.Add(item);

            image.Measure(outputWidth);
            image.EnsureLoading();
        }
    }

    private static bool IsStormItemSettled(RasterStormItem item) => item.Resolver.ResolveCount > 0 &&
        (item.Image.Bitmap is not null ||
         (item.Image.OutputDeferral.GetState().Deferred &&
          item.Image.OutputDeferral.GetState().HasWaiter &&
          item.Image.GetWorkState().ActiveWorkCount == 0));

    private static (RasterStormItem[] Deferred, RasterStormItem[] Admitted)
        AssertInitialStormSplitsIntoDeferredAndAdmitted(
            List<RasterStormItem> items,
            int outputWidth,
            int outputHeight)
    {
        RasterStormItem[] initiallyDeferred = items
            .Where(item => item.Image.OutputDeferral.GetState().Deferred)
            .ToArray();
        RasterStormItem[] initiallyAdmitted = items
            .Where(item => item.Image.Bitmap is not null)
            .ToArray();
        Assert.NotEmpty(initiallyDeferred);
        Assert.NotEmpty(initiallyAdmitted);
        Assert.All(initiallyDeferred, item =>
        {
            Assert.Equal(0, item.Image.GetWorkState().ActiveWorkCount);
            Assert.Null(item.Image.Bitmap);
            Assert.False(item.Image.LoadState.IsFailed);
            Assert.Equal(MarkdownImageAccessibilityState.Loading, item.Image.AccessibilityState);
        });
        Assert.All(initiallyAdmitted, item =>
        {
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, item.Image.AccessibilityState);
            Assert.Equal((uint)outputWidth, item.Image.Bitmap!.SizeInPixels.Width);
            Assert.Equal((uint)outputHeight, item.Image.Bitmap.SizeInPixels.Height);
        });
        return (initiallyDeferred, initiallyAdmitted);
    }

    private static async Task SuspendAndReleaseStormResourcesAsync(IReadOnlyCollection<RasterStormItem> items)
    {
        foreach (RasterStormItem item in items)
            item.Image.SuspendResources();
        await WaitUntilAsync(
            () => items.All(item => item.Image.GetWorkState().ActiveWorkCount == 0),
            TimeSpan.FromSeconds(30));
        Assert.All(items, item => Assert.True(item.Image.TryReleaseSuspendedResources()));
    }

    private static void AssertStormPublishedWithinLedger(
        List<RasterStormItem> items,
        RasterStormItem[] initiallyDeferred,
        RasterPreparationChargeTrace rasterTrace,
        BitmapOutputAdmission.Snapshot empty,
        int imageCount,
        int outputWidth,
        int outputHeight)
    {
        Assert.All(items, item =>
        {
            Assert.True(item.Resolver.ResolveCount >= 1);
            Assert.True(item.SuccessfulPublications > 0);
            Assert.False(item.Image.LoadState.IsFailed);
            Assert.Equal((uint)outputWidth, item.FirstPublishedWidth);
            Assert.Equal((uint)outputHeight, item.FirstPublishedHeight);
        });
        Assert.Contains(initiallyDeferred, item => item.Resolver.ResolveCount >= 2);
        Assert.True(rasterTrace.WicPixelDecodeCount >= imageCount);
        Assert.True(rasterTrace.Win2DBitmapUploadCount >= imageCount);
        Assert.Equal(0, rasterTrace.CapacityViolationCount);
        Assert.True(rasterTrace.MaximumChargedBytes <= empty.CapacityBytes);
    }

    private async Task DisposeRasterStormAsync(
        List<RasterStormItem> items,
        RasterPreparationChargeTrace? rasterTrace,
        MarkdownPerformanceSession? session,
        List<BitmapOutputAdmission.Reservation> blockers)
    {
        foreach (RasterStormItem item in items)
            item.Detach();
        try
        {
            foreach (RasterStormItem item in items)
                await DisposeImageAsync(item.Image);
        }
        finally
        {
            try
            {
                rasterTrace?.Dispose();
                if (session is not null)
                    await session.DisposeAsync();
            }
            finally
            {
                try { _renderResources.Images.BitmapCache.Clear(); }
                finally { ReleaseReservations(blockers); }
            }
        }
    }

    [Fact]
    public async Task FullSvgOutputDeferralDoesNotRenderUntilCapacityReturns()
    {
        BitmapOutputAdmission admission = _renderResources.Images.BitmapOutput;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        using var session = CreateProgressiveSession();
        var renderer = new CountingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2));
        var resolver = new CountingResolver(
            CreateSvg("full-svg-" + Guid.NewGuid().ToString("N")),
            "svg-content:" + Guid.NewGuid().ToString("N"),
            "image/svg+xml");
        var image = new ImageBox(
            CreateSvgContext(resolver, renderer, session, rasterizationScale: 1), _renderResources.Images,
            "https://images.example/admission-full.svg",
            "full SVG");
        var completed = new TaskCompletionSource<LoadCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completed.TrySetResult(args);
        };
        image.LoadCompleted += handler;

        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await WaitForDeferredAndDrainedAsync(image, () => resolver.ResolveCount);

            Assert.Null(image.Bitmap);
            Assert.Equal(0, image.GetWorkState().ActiveWorkCount);
            Assert.Equal(0, renderer.RenderCount);
            Assert.False(completed.Task.IsCompleted);
            Assert.Null(image.SvgFailureReason);

            ReleaseReservations(blockers);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);

            Assert.IsType<CanvasBitmap>(image.Bitmap);
            using SharedCanvasBitmapCache.Lease lease =
                Assert.IsType<SharedCanvasBitmapCache.Lease>(image.AcquireBitmapLease());
            Assert.True(lease.IsBitmapOutputAdmitted);
            Assert.Equal(1, renderer.RenderCount);
            Assert.True(renderer.OpenCount >= 1);
            Assert.Null(image.SvgFailureReason);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
        }
        finally
        {
            image.LoadCompleted -= handler;
            await DisposeImageAsync(image);
            ReleaseReservations(blockers);
        }
    }

    [Fact]
    public async Task TiledSvgOutputDeferralRetainsAcceptedTreeAndInvalidatesForRetry()
    {
        BitmapOutputAdmission admission = _renderResources.Images.BitmapOutput;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        using var session = CreateProgressiveSession();
        var renderer = new CountingSvgRenderer(new MarkdownSvgDocumentInfo(
            IntrinsicWidthDips: 5_000,
            IntrinsicHeightDips: 1_000,
            IntrinsicAspectRatio: 5));
        var resolver = new CountingResolver(
            CreateSvg("tiled-svg-" + Guid.NewGuid().ToString("N"), 5_000, 1_000),
            "svg-content:" + Guid.NewGuid().ToString("N"),
            "image/svg+xml");
        var image = new ImageBox(
            CreateSvgContext(resolver, renderer, session, rasterizationScale: 4), _renderResources.Images,
            "https://images.example/admission-tiled.svg",
            "tiled SVG");

        try
        {
            image.Measure(5_000);
            await WaitForLoadAsync(image);
            Assert.True(image.UsesSvgTilesForAutomation);
            Assert.Equal((20_000, 4_000), image.SvgRasterPixelSize);
            Assert.Equal(0, renderer.RenderCount);

            var tileWake = new TaskCompletionSource<LoadCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<LoadCompletedEventArgs> handler = (_, args) =>
            {
                if (!args.IsBitmapOutputRecoverySignal)
                    tileWake.TrySetResult(args);
            };
            image.LoadCompleted += handler;
            try
            {
                using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 256, 256, 96);
                PaintTileViewport(image, target);

                await WaitForDeferredAndDrainedAsync(image, () => resolver.ResolveCount);
                Assert.Equal(0, renderer.RenderCount);
                Assert.True(image.UsesSvgTilesForAutomation);
                Assert.Equal((20_000, 4_000), image.SvgRasterPixelSize);
                Assert.Null(image.SvgFailureReason);

                ReleaseReservations(blockers);
                LoadCompletedEventArgs wake = await tileWake.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(wake.LayoutInvalidated);
                Assert.True(image.UsesSvgTilesForAutomation);
                Assert.Equal((20_000, 4_000), image.SvgRasterPixelSize);
                Assert.Equal(0, renderer.RenderCount);
                Assert.Equal(0, image.GetWorkState().ActiveWorkCount);
                Assert.False(image.OutputDeferral.GetState().HasWaiter);
                Assert.False(image.OutputDeferral.GetState().Deferred);

                // The capacity notification only invalidates the accepted tile
                // tree. Rendering resumes on a later paint; without one, no
                // source-free waiter or active work should remain stranded.
                PaintTileViewport(image, target);

                await WaitUntilAsync(() => renderer.RenderCount > 0);
                await WaitUntilAsync(() => image.GetWorkState().ActiveWorkCount == 0);
                Assert.True(image.UsesSvgTilesForAutomation);
                Assert.Null(image.SvgFailureReason);
            }
            finally
            {
                image.LoadCompleted -= handler;
            }
        }
        finally
        {
            await DisposeImageAsync(image);
            ReleaseReservations(blockers);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingImageOrSessionRemovesTheSourceFreeCapacityWaiter(bool cancelSession)
    {
        BitmapOutputAdmission admission = _renderResources.Images.BitmapOutput;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        using var session = CreateProgressiveSession();
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(2, 1),
            "bitmap-output-cancel-" + Guid.NewGuid().ToString("N"),
            "image/png");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(_renderResources, resolver, session, scale: 1), _renderResources.Images,
            "https://images.example/admission-cancel.png",
            "cancel waiter");

        try
        {
            image.Measure(2);
            image.EnsureLoading();
            await WaitForDeferredAndDrainedAsync(image, () => resolver.ResolveCount);
            Assert.True(image.OutputDeferral.GetState().HasWaiter);
            Assert.Equal(0, image.GetWorkState().ActiveWorkCount);

            if (cancelSession)
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            else
                image.Dispose();

            await WaitUntilAsync(() =>
                !image.OutputDeferral.GetState().HasWaiter &&
                !image.OutputDeferral.GetState().HasWaiterOwner &&
                !image.OutputDeferral.GetState().Deferred &&
                image.GetWorkState().ActiveWorkCount == 0);
            ReleaseReservations(blockers);
            Assert.Null(image.Bitmap);
        }
        finally
        {
            await DisposeImageAsync(image);
            ReleaseReservations(blockers);
        }
    }

    [Fact]
    public async Task DisabledOffscreenReleasePolicyKeepsLegacyBitmapLoadOutsideSharedLedger()
    {
        BitmapOutputAdmission admission = _renderResources.Images.BitmapOutput;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                ReleaseOffscreenImageResources = false,
                SourceCacheBudgetBytes = 0,
            });
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(1, 1),
            "bitmap-output-legacy-" + Guid.NewGuid().ToString("N"),
            "image/png");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(_renderResources, resolver, session, scale: 1), _renderResources.Images,
            "https://images.example/admission-legacy.png",
            "legacy policy");

        try
        {
            image.Measure(1);
            await WaitForLoadAsync(image);

            CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
            Assert.Equal(1u, bitmap.SizeInPixels.Width);
            Assert.Equal(1u, bitmap.SizeInPixels.Height);
            using SharedCanvasBitmapCache.Lease lease =
                Assert.IsType<SharedCanvasBitmapCache.Lease>(image.AcquireBitmapLease());
            Assert.False(lease.IsBitmapOutputAdmitted);
            Assert.False(image.OutputDeferral.GetState().Deferred);
        }
        finally
        {
            await DisposeImageAsync(image);
            ReleaseReservations(blockers);
        }
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("source-sized")]
    [InlineData("raster-sized")]
    public async Task OversizedLegacyCacheRejectionTrimsCacheOnlyOnWorkerAndPreservesLease(string rejectionPath)
    {
        _renderResources.Images.BitmapCache.Clear();
        await WaitUntilAsync(() => _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes == 0);
        BitmapOutputAdmission admission = _renderResources.Images.BitmapOutput;
        long maximumPixels = admission.MaximumSingleOutputBytes / 4;
        int side = checked((int)System.Math.Sqrt(maximumPixels) + 1);
        long oversizedBytes = checked((long)side * side * 4);
        Assert.True(oversizedBytes > admission.MaximumSingleOutputBytes);
        Assert.True(oversizedBytes < _renderResources.Images.BitmapCache.GetSnapshot().BudgetBytes);

        long livePixelsBefore = _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes;
        long admittedBytesBefore = admission.GetSnapshot().ChargedBytes;
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                SourceCacheBudgetBytes = 0,
                UseDisplaySizedRasterDecode = false,
            });
        string cacheKey = $"oversized-legacy-cache-{Guid.NewGuid():N}";
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(1, 1),
            cacheKey,
            "image/png");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(_renderResources, resolver, session, scale: 1), _renderResources.Images,
            "https://images.example/oversized-legacy-cache.png",
            "oversized cached legacy bitmap");
        SharedCanvasBitmapCache.Lease? survivor = null;
        bool cacheSeeded = false;

        try
        {
            CanvasDevice device = CanvasDevice.GetSharedDevice();
            SharedCanvasBitmapCache.Lease seed = StoreOversizedBitmap(device, cacheKey, side, oversizedBytes);
            cacheSeeded = true;
            seed.Dispose(); // Leave the oversized, unadmitted Entry cache-only.

            Assert.True(_renderResources.Images.BitmapCache.TryAcquire(device, cacheKey, out survivor));
            SharedCanvasBitmapCache.Lease liveSurvivor =
                Assert.IsType<SharedCanvasBitmapCache.Lease>(survivor);
            Assert.False(liveSurvivor.IsBitmapOutputAdmitted);
            Assert.Equal(livePixelsBefore + oversizedBytes,
                _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);

            bool admitted = InvokeRejectionPath(rejectionPath, image, liveSurvivor, device, cacheKey);
            Assert.False(admitted);

            await WaitUntilCacheKeyEvictedAsync(device, cacheKey);

            // Cache.Clear has dropped only its own reference. The legacy lease
            // remains usable, and the opted-in governor never charged it.
            Assert.False(liveSurvivor.IsBitmapOutputAdmitted);
            Assert.Equal((uint)side, liveSurvivor.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)side, liveSurvivor.Bitmap.SizeInPixels.Height);
            Assert.Equal(admittedBytesBefore, admission.GetSnapshot().ChargedBytes);
            Assert.Equal(livePixelsBefore + oversizedBytes,
                _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
        }
        finally
        {
            await DisposeOversizedCacheFixtureAsync(survivor, image, cacheSeeded);
        }

        await WaitUntilAsync(() =>
            _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes == livePixelsBefore);
        Assert.Equal(admittedBytesBefore, admission.GetSnapshot().ChargedBytes);
    }

    private SharedCanvasBitmapCache.Lease StoreOversizedBitmap(
        CanvasDevice device,
        string cacheKey,
        int side,
        long oversizedBytes)
    {
        CanvasBitmap createdBitmap = CanvasBitmap.CreateFromBytes(
            device,
            new byte[checked((int)oversizedBytes)],
            side,
            side,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        CanvasBitmap? unownedBitmap = createdBitmap;
        try
        {
            SharedCanvasBitmapCache.Lease seed =
                _renderResources.Images.BitmapCache.StoreAndAcquire(device, cacheKey, createdBitmap);
            unownedBitmap = null;
            return seed;
        }
        finally
        {
            unownedBitmap?.Dispose();
        }
    }

    private static bool InvokeRejectionPath(
        string rejectionPath,
        ImageBox image,
        SharedCanvasBitmapCache.Lease liveSurvivor,
        CanvasDevice device,
        string cacheKey) => rejectionPath switch
    {
        "admission" => InvokeCachedAdmission(image, liveSurvivor),
        "source-sized" => InvokeSourceSizedCacheLookup(image, cacheKey),
        "raster-sized" => InvokeRasterSizedCacheLookup(image, device, cacheKey),
        _ => throw new ArgumentOutOfRangeException(nameof(rejectionPath)),
    };

    private Task WaitUntilCacheKeyEvictedAsync(CanvasDevice device, string cacheKey) =>
        WaitUntilAsync(() =>
        {
            if (!_renderResources.Images.BitmapCache.TryAcquire(device, cacheKey, out var probe))
                return true;
            probe?.Dispose();
            return false;
        });

    private async Task DisposeOversizedCacheFixtureAsync(
        SharedCanvasBitmapCache.Lease? survivor,
        ImageBox image,
        bool cacheSeeded)
    {
        try
        {
            survivor?.Dispose();
        }
        finally
        {
            try
            {
                await DisposeImageAsync(image);
            }
            finally
            {
                if (cacheSeeded)
                    _renderResources.Images.BitmapCache.Clear();
            }
        }
    }

    private static bool InvokeCachedAdmission(
        ImageBox image,
        SharedCanvasBitmapCache.Lease lease)
    {
        return image.OutputAdmission.TryAdmitCachedBitmapOutput(lease, expectedWork: null);
    }

    private static bool InvokeSourceSizedCacheLookup(ImageBox image, string cacheKey)
    {
        return image.OutputAdmission.TryAcquireSourceSizedRasterBitmap(cacheKey, out _);
    }

    private static bool InvokeRasterSizedCacheLookup(
        ImageBox image,
        CanvasDevice device,
        string cacheKey)
    {
        var request = new RasterImageSizingRequest(
            availableWidth: 1,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: 1);
        return image.OutputAdmission.TryAcquireRasterBitmap(device, cacheKey, request, transformed: false, out _);
    }

    private static void PaintTileViewport(ImageBox image, CanvasRenderTarget target)
    {
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        drawing.Clear(Color.FromArgb(0, 0, 0, 0));
        image.Paint(drawing, new Rect(0, 0, 256, 256));
    }

    private static MarkdownPerformanceSession CreateProgressiveSession() =>
        new(MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 0 });

    private MarkdownLayoutContext CreateSvgContext(
        IMarkdownImageResolver resolver,
        IMarkdownSvgRenderer renderer,
        MarkdownPerformanceSession session,
        double rasterizationScale)
    {
        var style = new ElementStyle { Foreground = Color.FromArgb(255, 12, 34, 56) };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ImageCaption] = style,
            [MarkdownElementKeys.ListMarker] = style,
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
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
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
            PerformanceSession = session,
            PerformanceDocumentOwner = new object(),
            RasterizationScale = rasterizationScale,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static byte[] CreateSvg(string title, int width = 80, int height = 40) =>
        Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}'>" +
            $"<title>{title}</title><rect width='{width}' height='{height}' fill='#2a6'/></svg>");

    private async Task<List<BitmapOutputAdmission.Reservation>> SaturateAdmissionAsync(
        BitmapOutputAdmission admission)
    {
        // Cached admitted bitmaps can keep charges alive even when no image is
        // actively painting. Clear them first so the blockers below, rather
        // than an incidental prior test cache, define the full-ledger setup.
        _renderResources.Images.BitmapCache.Clear();
        await WaitUntilAsync(() => admission.GetSnapshot().ChargedBytes == 0);

        var reservations = new List<BitmapOutputAdmission.Reservation>();
        long reservationBytes = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
                long remaining = snapshot.CapacityBytes - snapshot.ChargedBytes;
                if (remaining <= 0)
                {
                    Assert.Equal(snapshot.CapacityBytes, snapshot.ChargedBytes);
                    Assert.Equal(snapshot.CapacityBytes, reservationBytes);
                    return reservations;
                }

                long bytes = System.Math.Min(remaining, snapshot.MaximumSingleOutputBytes);
                if (admission.TryReserve(
                        bytes,
                        isVisible: true,
                        isReplacement: true,
                        out BitmapOutputAdmission.Reservation? reservation,
                        out _))
                {
                    BitmapOutputAdmission.Reservation admitted =
                        Assert.IsType<BitmapOutputAdmission.Reservation>(reservation);
                    reservations.Add(admitted);
                    reservationBytes = checked(reservationBytes + admitted.ReservedBytes);
                    continue;
                }

                await Task.Delay(1, timeout.Token);
            }
        }
        catch
        {
            ReleaseReservations(reservations);
            throw;
        }
    }

    private static void ReleaseReservations(List<BitmapOutputAdmission.Reservation> reservations)
    {
        for (int index = reservations.Count - 1; index >= 0; index--)
            reservations[index].Dispose();
        reservations.Clear();
    }

    private async Task WaitForDeferredAndDrainedAsync(
        ImageBox image,
        Func<int>? resolverCount = null)
    {
        bool IsDeferredAndDrained() =>
            image.OutputDeferral.GetState().Deferred &&
            image.OutputDeferral.GetState().HasWaiter &&
            image.GetWorkState().ActiveWorkCount == 0;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!IsDeferredAndDrained())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            BitmapOutputAdmission.Snapshot capacity = _renderResources.Images.BitmapOutput.GetSnapshot();
            string state =
                $"{image.OutputDeferral.GetState()}, " +
                $"activeWork={image.GetWorkState().ActiveWorkCount}, " +
                $"loadStarted={image.LoadState.IsStarted}, " +
                $"loadFailed={image.LoadState.IsFailed}, " +
                $"disposed={image.Lifetime.IsDisposed}, " +
                $"suspended={image.Lifetime.IsSuspended}, " +
                $"resolverCount={resolverCount?.Invoke().ToString() ?? "n/a"}, " +
                $"bitmap={image.Bitmap is not null}, " +
                $"charged={capacity.ChargedBytes}/{capacity.CapacityBytes}, " +
                $"capacityVersion={capacity.CapacityVersion}";
            throw new Xunit.Sdk.XunitException(
                $"The image did not reach a drained bitmap-output deferral within 30 seconds. State: {state}");
        }
    }

    private static async Task WaitForLoadAsync(ImageBox image)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completion.TrySetResult();
        };
        image.LoadCompleted += handler;
        try
        {
            image.EnsureLoading();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        await WaitUntilAsync(condition, TimeSpan.FromSeconds(30));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private static async Task DisposeImageAsync(ImageBox image)
    {
        image.Dispose();
        try
        {
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch when (image.RetirementTask.IsFaulted)
        {
            _ = image.RetirementTask.Exception;
        }
    }

    private sealed class CountingResolver(byte[] bytes, string cacheKey, string mediaType)
        : IMarkdownImageResolver
    {
        private int _resolveCount;
        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, mediaType, CacheKey: cacheKey)));
        }
    }

    private sealed class RasterStormItem
    {
        private int _successfulPublications;
        private readonly object _publicationSync = new();
        private uint _firstPublishedWidth;
        private uint _firstPublishedHeight;

        internal RasterStormItem(ImageBox image, CountingResolver resolver)
        {
            Image = image;
            Resolver = resolver;
            Published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Handler = (_, args) =>
            {
                if (!args.LayoutInvalidated)
                    return;
                CanvasBitmap? bitmap = Image.Bitmap;
                if (bitmap is not null)
                {
                    var size = bitmap.SizeInPixels;
                    lock (_publicationSync)
                    {
                        if (_firstPublishedWidth == 0 || _firstPublishedHeight == 0)
                        {
                            _firstPublishedWidth = size.Width;
                            _firstPublishedHeight = size.Height;
                        }
                    }
                }
                Interlocked.Increment(ref _successfulPublications);
                Published.TrySetResult();
            };
            Image.LoadCompleted += Handler;
        }

        internal ImageBox Image { get; }
        internal CountingResolver Resolver { get; }
        internal TaskCompletionSource Published { get; }
        internal EventHandler<LoadCompletedEventArgs> Handler { get; }
        internal int SuccessfulPublications => Volatile.Read(ref _successfulPublications);
        internal uint FirstPublishedWidth
        {
            get { lock (_publicationSync) return _firstPublishedWidth; }
        }
        internal uint FirstPublishedHeight
        {
            get { lock (_publicationSync) return _firstPublishedHeight; }
        }

        internal void Detach() => Image.LoadCompleted -= Handler;
    }
}
