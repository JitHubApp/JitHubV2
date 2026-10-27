using System.Buffers;
using System.Diagnostics.Tracing;
using System.Reflection;
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

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class BitmapOutputDeferralTests
{
    [Fact]
    public async Task RasterOutputDeferralDrainsSourceWorkAndRetriesAfterCapacityReturns()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        using var session = CreateProgressiveSession();
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(2, 1),
            "bitmap-output-raster-" + Guid.NewGuid().ToString("N"),
            "image/png");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
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
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
            Assert.False(completed.Task.IsCompleted);
            Assert.Equal(1, resolver.ResolveCount);

            ReleaseReservations(blockers);
            LoadCompletedEventArgs result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

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
            Assert.Equal(0, GetField<int>(image, "_bitmapOutputDeferred"));
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

        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        var items = new List<RasterStormItem>(imageCount);
        MarkdownPerformanceSession? session = null;
        RasterAdmissionTrace? rasterTrace = null;

        try
        {
            ReleaseReservations(blockers);
            await WaitUntilAsync(() => admission.GetSnapshot().ChargedBytes == 0);
            BitmapOutputAdmission.Snapshot empty = admission.GetSnapshot();
            Assert.True(bytesPerOutput * imageCount > empty.CapacityBytes,
                "The fixture must request more live raster bytes than the process-wide ledger can admit.");
            Assert.True(bytesPerOutput * imageCount > empty.SpeculativeLimitBytes,
                "The fixture must exceed the background/speculative tier as well as total capacity.");

            session = CreateProgressiveSession();
            rasterTrace = new RasterAdmissionTrace(admission);
            byte[] png = DisplaySizedRasterDecodeTests.CreatePng(outputWidth, outputHeight);
            for (int index = 0; index < imageCount; index++)
            {
                var resolver = new CountingResolver(
                    png,
                    $"bitmap-output-storm-{index}-{Guid.NewGuid():N}",
                    "image/png");
                var image = new ImageBox(
                    DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
                    $"https://images.example/storm/{index}.png",
                    $"storm image {index}");
                var item = new RasterStormItem(image, resolver);
                items.Add(item);

                image.Measure(outputWidth);
                image.EnsureLoading();
            }

            await WaitUntilAsync(
                () => items.All(IsSettled),
                TimeSpan.FromMinutes(2));

            RasterStormItem[] initiallyDeferred = items
                .Where(item => GetField<int>(item.Image, "_bitmapOutputDeferred") != 0)
                .ToArray();
            RasterStormItem[] initiallyAdmitted = items
                .Where(item => item.Image.Bitmap is not null)
                .ToArray();
            Assert.NotEmpty(initiallyDeferred);
            Assert.NotEmpty(initiallyAdmitted);
            Assert.All(initiallyDeferred, item =>
            {
                Assert.Equal(0, item.Image.ActiveImageWorkCountForTests);
                Assert.Null(item.Image.Bitmap);
                Assert.False(GetField<bool>(item.Image, "_loadFailed"));
                Assert.Equal(MarkdownImageAccessibilityState.Loading, item.Image.AccessibilityState);
            });
            Assert.All(initiallyAdmitted, item =>
            {
                Assert.Equal(MarkdownImageAccessibilityState.Loaded, item.Image.AccessibilityState);
                Assert.Equal((uint)outputWidth, item.Image.Bitmap!.SizeInPixels.Width);
                Assert.Equal((uint)outputHeight, item.Image.Bitmap.SizeInPixels.Height);
            });

            // Release the first admitted cohort as offscreen residency work.
            // Their cache-only references are then trimmed so capacity can
            // advance the source-free denied cohort.
            foreach (RasterStormItem item in initiallyAdmitted)
                item.Image.SuspendResources();

            await WaitUntilAsync(
                () => initiallyAdmitted.All(item => item.Image.ActiveImageWorkCountForTests == 0),
                TimeSpan.FromSeconds(30));
            Assert.All(initiallyAdmitted, item => Assert.True(item.Image.TryReleaseSuspendedResources()));
            Assert.All(initiallyAdmitted, item => Assert.Null(item.Image.Bitmap));
            SharedCanvasBitmapCache.Clear();

            await Task.WhenAll(items.Select(item => item.Published.Task))
                .WaitAsync(TimeSpan.FromMinutes(2));
            await WaitUntilAsync(
                () => items.All(item => item.SuccessfulPublications > 0 &&
                    item.Image.ActiveImageWorkCountForTests == 0),
                TimeSpan.FromMinutes(2));

            Assert.All(items, item =>
            {
                Assert.True(item.Resolver.ResolveCount >= 1);
                Assert.True(item.SuccessfulPublications > 0);
                Assert.False(GetField<bool>(item.Image, "_loadFailed"));
                Assert.Equal((uint)outputWidth, item.FirstPublishedWidth);
                Assert.Equal((uint)outputHeight, item.FirstPublishedHeight);
            });
            Assert.Contains(initiallyDeferred, item => item.Resolver.ResolveCount >= 2);
            Assert.True(rasterTrace!.WicPixelDecodeCount >= imageCount);
            Assert.True(rasterTrace.Win2DBitmapUploadCount >= imageCount);
            Assert.Equal(0, rasterTrace.CapacityViolationCount);
            Assert.True(rasterTrace.MaximumChargedBytes <= empty.CapacityBytes);

            // Explicitly release the final wave too; successful publication
            // and cleanup are both part of this exercise.
            foreach (RasterStormItem item in items)
                item.Image.SuspendResources();
            await WaitUntilAsync(
                () => items.All(item => item.Image.ActiveImageWorkCountForTests == 0),
                TimeSpan.FromSeconds(30));
            Assert.All(items, item => Assert.True(item.Image.TryReleaseSuspendedResources()));
            SharedCanvasBitmapCache.Clear();
            await WaitUntilAsync(() => admission.GetSnapshot().ChargedBytes == 0);
            Assert.All(items, item => Assert.Null(item.Image.Bitmap));

            bool IsSettled(RasterStormItem item) => item.Resolver.ResolveCount > 0 &&
                (item.Image.Bitmap is not null ||
                 (GetField<int>(item.Image, "_bitmapOutputDeferred") != 0 &&
                  GetField(item.Image, "_bitmapOutputWaiter") is not null &&
                  item.Image.ActiveImageWorkCountForTests == 0));
        }
        finally
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
                    try { SharedCanvasBitmapCache.Clear(); }
                    finally { ReleaseReservations(blockers); }
                }
            }
        }
    }

    [Fact]
    public async Task FullSvgOutputDeferralDoesNotRenderUntilCapacityReturns()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
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
            CreateSvgContext(resolver, renderer, session, rasterizationScale: 1),
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
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
            Assert.Equal(0, renderer.RenderCount);
            Assert.False(completed.Task.IsCompleted);
            Assert.Null(image.SvgFailureReason);

            ReleaseReservations(blockers);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);

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
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
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
            CreateSvgContext(resolver, renderer, session, rasterizationScale: 4),
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
                using (CanvasDrawingSession drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                    image.Paint(drawing, new Rect(0, 0, 256, 256));
                }

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
                Assert.Equal(0, image.ActiveImageWorkCountForTests);
                Assert.Null(GetField(image, "_bitmapOutputWaiter"));
                Assert.Equal(0, GetField<int>(image, "_bitmapOutputDeferred"));

                // The capacity notification only invalidates the accepted tile
                // tree. Rendering resumes on a later paint; without one, no
                // source-free waiter or active work should remain stranded.
                using (CanvasDrawingSession drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                    image.Paint(drawing, new Rect(0, 0, 256, 256));
                }

                await WaitUntilAsync(() => renderer.RenderCount > 0);
                await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
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
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        List<BitmapOutputAdmission.Reservation> blockers = await SaturateAdmissionAsync(admission);
        using var session = CreateProgressiveSession();
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(2, 1),
            "bitmap-output-cancel-" + Guid.NewGuid().ToString("N"),
            "image/png");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/admission-cancel.png",
            "cancel waiter");

        try
        {
            image.Measure(2);
            image.EnsureLoading();
            await WaitForDeferredAndDrainedAsync(image, () => resolver.ResolveCount);
            Assert.NotNull(GetField(image, "_bitmapOutputWaiter"));
            Assert.Equal(0, image.ActiveImageWorkCountForTests);

            if (cancelSession)
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            else
                image.Dispose();

            await WaitUntilAsync(() =>
                GetField(image, "_bitmapOutputWaiter") is null &&
                GetField(image, "_bitmapOutputWaiterOwner") is null &&
                GetField<int>(image, "_bitmapOutputDeferred") == 0 &&
                image.ActiveImageWorkCountForTests == 0);
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
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
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
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
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
            Assert.Equal(0, GetField<int>(image, "_bitmapOutputDeferred"));
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
        SharedCanvasBitmapCache.Clear();
        await WaitUntilAsync(() => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        long maximumPixels = admission.MaximumSingleOutputBytes / 4;
        int side = checked((int)System.Math.Sqrt(maximumPixels) + 1);
        long oversizedBytes = checked((long)side * side * 4);
        Assert.True(oversizedBytes > admission.MaximumSingleOutputBytes);
        Assert.True(oversizedBytes < SharedCanvasBitmapCache.BudgetBytesForTests);

        long livePixelsBefore = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
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
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/oversized-legacy-cache.png",
            "oversized cached legacy bitmap");
        SharedCanvasBitmapCache.Lease? survivor = null;
        bool cacheSeeded = false;

        try
        {
            CanvasDevice device = CanvasDevice.GetSharedDevice();
            CanvasBitmap createdBitmap = CanvasBitmap.CreateFromBytes(
                device,
                new byte[checked((int)oversizedBytes)],
                side,
                side,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            CanvasBitmap? unownedBitmap = createdBitmap;
            SharedCanvasBitmapCache.Lease seed;
            try
            {
                seed = SharedCanvasBitmapCache.StoreAndAcquire(device, cacheKey, createdBitmap);
                unownedBitmap = null;
                cacheSeeded = true;
            }
            finally
            {
                unownedBitmap?.Dispose();
            }
            seed.Dispose(); // Leave the oversized, unadmitted Entry cache-only.

            Assert.True(SharedCanvasBitmapCache.TryAcquire(device, cacheKey, out survivor));
            SharedCanvasBitmapCache.Lease liveSurvivor =
                Assert.IsType<SharedCanvasBitmapCache.Lease>(survivor);
            Assert.False(liveSurvivor.IsBitmapOutputAdmitted);
            Assert.Equal(livePixelsBefore + oversizedBytes,
                SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);

            bool admitted = rejectionPath switch
            {
                "admission" => InvokeCachedAdmission(image, liveSurvivor),
                "source-sized" => InvokeSourceSizedCacheLookup(image, cacheKey),
                "raster-sized" => InvokeRasterSizedCacheLookup(image, device, cacheKey),
                _ => throw new ArgumentOutOfRangeException(nameof(rejectionPath)),
            };
            Assert.False(admitted);

            await WaitUntilAsync(() =>
            {
                if (!SharedCanvasBitmapCache.TryAcquire(device, cacheKey, out var probe))
                    return true;
                probe?.Dispose();
                return false;
            });

            // Cache.Clear has dropped only its own reference. The legacy lease
            // remains usable, and the opted-in governor never charged it.
            Assert.False(liveSurvivor.IsBitmapOutputAdmitted);
            Assert.Equal((uint)side, liveSurvivor.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)side, liveSurvivor.Bitmap.SizeInPixels.Height);
            Assert.Equal(admittedBytesBefore, admission.GetSnapshot().ChargedBytes);
            Assert.Equal(livePixelsBefore + oversizedBytes,
                SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        }
        finally
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
                        SharedCanvasBitmapCache.Clear();
                }
            }
        }

        await WaitUntilAsync(() =>
            SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests == livePixelsBefore);
        Assert.Equal(admittedBytesBefore, admission.GetSnapshot().ChargedBytes);
    }

    private static bool InvokeCachedAdmission(
        ImageBox image,
        SharedCanvasBitmapCache.Lease lease)
    {
        MethodInfo method = typeof(ImageBox).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "TryAdmitCachedBitmapOutput" &&
                candidate.GetParameters().Length == 2 &&
                candidate.GetParameters()[0].ParameterType == typeof(SharedCanvasBitmapCache.Lease));
        return (bool)method.Invoke(image, [lease, null])!;
    }

    private static bool InvokeSourceSizedCacheLookup(ImageBox image, string cacheKey)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "TryAcquireSourceSizedRasterBitmap",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(image, [cacheKey, null, null])!;
    }

    private static bool InvokeRasterSizedCacheLookup(
        ImageBox image,
        CanvasDevice device,
        string cacheKey)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            "TryAcquireRasterBitmap",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = new RasterImageSizingRequest(
            availableWidth: 1,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: 1);
        return (bool)method.Invoke(image, [device, cacheKey, request, false, null, null])!;
    }

    private static MarkdownPerformanceSession CreateProgressiveSession() =>
        new(MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 0 });

    private static MarkdownLayoutContext CreateSvgContext(
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

    private static async Task<List<BitmapOutputAdmission.Reservation>> SaturateAdmissionAsync(
        BitmapOutputAdmission admission)
    {
        // Cached admitted bitmaps can keep charges alive even when no image is
        // actively painting. Clear them first so the blockers below, rather
        // than an incidental prior test cache, define the full-ledger setup.
        SharedCanvasBitmapCache.Clear();
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

    private static async Task WaitForDeferredAndDrainedAsync(
        ImageBox image,
        Func<int>? resolverCount = null)
    {
        bool IsDeferredAndDrained() =>
            GetField<int>(image, "_bitmapOutputDeferred") != 0 &&
            GetField(image, "_bitmapOutputWaiter") is not null &&
            image.ActiveImageWorkCountForTests == 0;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!IsDeferredAndDrained())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            BitmapOutputAdmission.Snapshot capacity = BitmapOutputAdmission.Shared.GetSnapshot();
            string state =
                $"deferred={GetField<int>(image, "_bitmapOutputDeferred")}, " +
                $"owner={GetField(image, "_bitmapOutputWaiterOwner") is not null}, " +
                $"waiter={GetField(image, "_bitmapOutputWaiter") is not null}, " +
                $"registering={GetField<int>(image, "_bitmapOutputRegistering")}, " +
                $"registrationNeeded={GetField<int>(image, "_bitmapOutputRegistrationNeeded")}, " +
                $"registrationFailed={GetField<int>(image, "_bitmapOutputRegistrationFailed")}, " +
                $"capacityReady={GetField<int>(image, "_bitmapOutputCapacityReady")}, " +
                $"retryQueued={GetField<int>(image, "_bitmapOutputRetryQueued")}, " +
                $"retryInProgress={GetField<int>(image, "_bitmapOutputRetryInProgress")}, " +
                $"retryOffer={GetField<int>(image, "_bitmapOutputRetryOfferPending")}, " +
                $"needsTrim={GetField<int>(image, "_bitmapOutputNeedsTrim")}, " +
                $"requiredBytes={GetField<long>(image, "_bitmapOutputRequiredBytes")}, " +
                $"activeWork={image.ActiveImageWorkCountForTests}, " +
                $"loadStarted={GetField<bool>(image, "_loadStarted")}, " +
                $"loadFailed={GetField<bool>(image, "_loadFailed")}, " +
                $"disposed={GetField<bool>(image, "_disposed")}, " +
                $"suspended={GetField<bool>(image, "_resourcesSuspended")}, " +
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

    private static object? GetField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner);

    private static T GetField<T>(object owner, string name) =>
        Assert.IsType<T>(GetField(owner, name));

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

    private sealed class RasterAdmissionTrace(BitmapOutputAdmission admission) : EventListener
    {
        private long _maximumChargedBytes;
        private int _wicPixelDecodeCount;
        private int _win2DBitmapUploadCount;
        private int _capacityViolationCount;

        internal long MaximumChargedBytes => Interlocked.Read(ref _maximumChargedBytes);
        internal int WicPixelDecodeCount => Volatile.Read(ref _wicPixelDecodeCount);
        internal int Win2DBitmapUploadCount => Volatile.Read(ref _win2DBitmapUploadCount);
        internal int CapacityViolationCount => Volatile.Read(ref _capacityViolationCount);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer-RasterPreparation")
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != "MarkdownRenderer-RasterPreparation" ||
                eventData.EventId != 2 ||
                eventData.Payload is not { Count: 3 } payload ||
                payload[1] is not long stage)
            {
                return;
            }

            if (stage == (long)MarkdownRasterPreparationStage.WicPixelDecode)
                Interlocked.Increment(ref _wicPixelDecodeCount);
            else if (stage == (long)MarkdownRasterPreparationStage.Win2DBitmapUpload)
                Interlocked.Increment(ref _win2DBitmapUploadCount);
            else if (stage != (long)MarkdownRasterPreparationStage.Win2DDirectLoad &&
                     stage != (long)MarkdownRasterPreparationStage.CachePublication)
                return;

            BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
            long observed;
            while (snapshot.ChargedBytes > (observed = Interlocked.Read(ref _maximumChargedBytes)) &&
                   Interlocked.CompareExchange(
                       ref _maximumChargedBytes,
                       snapshot.ChargedBytes,
                       observed) != observed)
            {
            }

            if (snapshot.ChargedBytes > snapshot.CapacityBytes)
                Interlocked.Increment(ref _capacityViolationCount);
        }
    }

    private sealed class CountingSvgRenderer(MarkdownSvgDocumentInfo info) : IMarkdownSvgRenderer
    {
        private int _openCount;
        private int _renderCount;

        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int RenderCount => Volatile.Read(ref _renderCount);

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            return ValueTask.FromResult<IMarkdownSvgDocument>(new CountingSvgDocument(this, info));
        }

        private sealed class CountingSvgDocument(
            CountingSvgRenderer renderer,
            MarkdownSvgDocumentInfo info) : IMarkdownSvgDocument
        {
            public MarkdownSvgDocumentInfo Info { get; } = info;

            public ValueTask<MarkdownSvgRaster> RenderAsync(
                MarkdownSvgRenderRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref renderer._renderCount);
                int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
                int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
                int stride = checked(width * 4);
                int length = checked(stride * height);
                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
                owner.Memory.Span[..length].Clear();
                return ValueTask.FromResult(new MarkdownSvgRaster(
                    owner,
                    length,
                    width,
                    height,
                    stride,
                    request.PixelFormat));
            }

            public void Dispose() { }
        }
    }
}
