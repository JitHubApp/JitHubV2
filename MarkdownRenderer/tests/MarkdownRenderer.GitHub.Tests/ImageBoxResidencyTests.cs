using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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

// Cache/admission state is process-wide; a parallel cache trim or live-output
// reservation can legitimately invalidate this fixture's warm-constructor proof.
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ImageBoxResidencyTests
{
    [Fact]
    public async Task ReleasedRasterKeepsGeometryAndAccessibilityAndReusesDocumentSource()
    {
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                PrefetchDocumentImages = false,
                ReleaseOffscreenImageResources = true,
            });
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(320, 160),
            $"image-residency-{Guid.NewGuid():N}");
        using var documentScope = session.OpenDocument(
            resolver,
            new MarkdownImageResolveContext(BaseUri: null));
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(documentScope, session, scale: 1),
            "https://images.example/residency.png",
            "A retained image description");

        try
        {
            image.Measure(200);
            await LoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Equal("A retained image description", image.Alt);
            Assert.Equal(1, resolver.ResolveCount);
            float presentedHeight = image.Measure(200);
            var presentedDestination = image.AutomationRenderDestination;

            Task drained = ObserveResidencyDrainAsync(image);
            image.SuspendResources();
            await drained.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(image.TryReleaseSuspendedResources());
            Assert.Null(image.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Equal("A retained image description", image.Alt);
            Assert.False(image.HasBitmapForAutomation);
            Assert.Equal(presentedHeight, image.Measure(200), precision: 3);
            Assert.Equal(
                (presentedDestination.X, presentedDestination.Y,
                    presentedDestination.Width, presentedDestination.Height),
                (image.AutomationRenderDestination.X, image.AutomationRenderDestination.Y,
                    image.AutomationRenderDestination.Width, image.AutomationRenderDestination.Height));

            Assert.True(image.TryResumeResources());
            await LoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, resolver.ResolveCount);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.True(image.HasBitmapForAutomation);
            Assert.Equal(presentedHeight, image.Measure(200), precision: 3);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task SuspendedBoxWaitsForLateResolverBeforeReleasingOrPublishing()
    {
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                PrefetchDocumentImages = false,
                ReleaseOffscreenImageResources = true,
            });
        var resolver = new LateResolver();
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/late.png",
            "Late image");
        int publications = 0;
        image.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);

        try
        {
            image.Measure(200);
            image.EnsureLoading();
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task drained = ObserveResidencyDrainAsync(image);
            image.SuspendResources();
            Assert.False(image.TryReleaseSuspendedResources());
            Assert.Null(image.Bitmap);

            resolver.Complete(DisplaySizedRasterDecodeTests.CreatePng(320, 160));
            await drained.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, image.ActiveImageWorkCountForTests);
            Assert.True(image.TryReleaseSuspendedResources());
            Assert.Null(image.Bitmap);
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Equal(MarkdownImageAccessibilityState.Loading, image.AccessibilityState);
        }
        finally
        {
            resolver.Complete(DisplaySizedRasterDecodeTests.CreatePng(1, 1));
            image.Dispose();
        }
    }

    [Fact]
    public async Task QueuedPublicationIsDroppedAndItsLeaseDrainsBeforeRelease()
    {
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                ReleaseOffscreenImageResources = true,
            });
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(
                new UnusedResolver(), session, scale: 1),
            "https://images.example/queued.png",
            "Queued image");
        var queued = new TaskCompletionSource<Action>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int publications = 0;
        int dropped = 0;

        try
        {
            image.PublishOnUiThread(
                () => Interlocked.Increment(ref publications),
                () => Interlocked.Increment(ref dropped),
                callback =>
                {
                    queued.TrySetResult(callback);
                    return true;
                });

            Action callback = await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(image.ActiveImageWorkCountForTests > 0);
            Task drained = ObserveResidencyDrainAsync(image);
            image.SuspendResources();
            Assert.False(image.TryReleaseSuspendedResources());

            callback();
            await drained.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.Equal(1, Volatile.Read(ref dropped));
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
            Assert.True(image.TryReleaseSuspendedResources());
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task ReleasedSvgWaitsForDocumentDisposeAndReusesWarmExactBitmap()
    {
        using var session = CreateResidencySession();
        var renderer = new ResidencySvgRenderer(blockDispose: true);
        var image = new ImageBox(
            CreateContext(null, session, renderer),
            CreateSvgDataUri("blocked-dispose-" + Guid.NewGuid().ToString("N")),
            "SVG alternative text");

        try
        {
            image.Measure(80);
            await LoadAsync(image);
            await renderer.Document.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(image.UsesSvgTilesForAutomation);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
            Assert.Equal(1, renderer.Document.DisposeCount);
            Assert.Equal(1, renderer.LastRasterOwner!.DisposeCount);
            Assert.Equal("Residency title", image.SvgTitle);
            Assert.Equal("Provider description", image.SvgDesc);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            float presentedHeight = image.Measure(80);
            var presentedDestination = image.AutomationRenderDestination;

            // The document retirement lease is held by the blocked provider
            // Dispose call. Register while that work is known to be active so
            // the drained signal cannot have raced ahead of this observer.
            Assert.True(image.ActiveImageWorkCountForTests > 0);
            Task documentDisposalDrained = ObserveResidencyDrainAsync(image);
            image.SuspendResources();
            Assert.False(image.TryReleaseSuspendedResources());
            Assert.NotNull(image.Bitmap);
            Assert.False(renderer.Document.DisposeCompleted.Task.IsCompleted);
            Assert.False(image.TryResumeResources());
            Assert.Equal("Residency title", image.SvgTitle);
            Assert.Equal("Provider description", image.SvgDesc);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Equal(presentedHeight, image.Measure(80), precision: 3);
            Assert.Equal(
                (presentedDestination.X, presentedDestination.Y,
                    presentedDestination.Width, presentedDestination.Height),
                (image.AutomationRenderDestination.X, image.AutomationRenderDestination.Y,
                    image.AutomationRenderDestination.Width, image.AutomationRenderDestination.Height));

            renderer.Document.ReleaseDispose();
            await renderer.Document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await documentDisposalDrained.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(image.TryReleaseSuspendedResources());
            Assert.Equal(1, renderer.Document.DisposeCount);
            Assert.Null(image.Bitmap);
            Assert.True(image.TryResumeResources());

            await LoadAsync(image);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Equal("Residency title", image.SvgTitle);
            Assert.Equal("Provider description", image.SvgDesc);
            Assert.Equal(presentedHeight, image.Measure(80), precision: 3);
        }
        finally
        {
            renderer.Document.ReleaseDispose();
            image.Dispose();
        }
    }

    [Fact]
    public async Task LegacyNonTiledSvgRetiresDocumentAfterBitmapPublication()
    {
        var renderer = new ResidencySvgRenderer();
        var image = new ImageBox(
            CreateContext(null, session: null, renderer),
            CreateSvgDataUri("legacy-retirement-" + Guid.NewGuid().ToString("N")),
            "Legacy SVG");

        try
        {
            image.Measure(80);
            await LoadAsync(image);
            await renderer.Document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(image.UsesSvgTilesForAutomation);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
            Assert.Equal(1, renderer.Document.DisposeCount);
            Assert.Equal(1, renderer.LastRasterOwner!.DisposeCount);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task TiledSvgKeepsDocumentUntilProgressiveSuspensionReleasesIt()
    {
        using var session = CreateResidencySession();
        var renderer = new ResidencySvgRenderer(
            info: new MarkdownSvgDocumentInfo(
                IntrinsicWidthDips: 20_000,
                IntrinsicHeightDips: 4_000,
                IntrinsicAspectRatio: 5,
                Description: "Large provider document"));
        var image = new ImageBox(
            CreateContext(null, session, renderer),
            CreateSvgDataUri("tiled-retirement-" + Guid.NewGuid().ToString("N")),
            "Tiled SVG");

        try
        {
            image.Measure(20_000);
            await LoadAsync(image);
            // LoadCompleted is published before its owning work lease drains.
            // Wait for retirement before requesting a tile, then model the
            // host's paint-only response to admission recovery below.
            await WaitForWorkCountAsync(image, expected: 0);

            Assert.True(image.UsesSvgTilesForAutomation);
            Assert.Equal(0, renderer.Document.DisposeCount);

            BitmapOutputAdmission.Snapshot admissionBeforeTilePaint =
                BitmapOutputAdmission.Shared.GetSnapshot();
            long cacheBytesBeforeTilePaint = SharedCanvasBitmapCache.RetainedBytesForTests;
            TaskCompletionSource repaintPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoadCompleted(object? sender, LoadCompletedEventArgs args)
            {
                // The production control treats these as a source-free hint and
                // invalidates its canvas. This fixture only signals the test
                // loop; it never paints from inside the callback.
                if (!args.LayoutInvalidated || args.IsBitmapOutputRecoverySignal)
                    Volatile.Read(ref repaintPulse).TrySetResult();
            }

            using var renderDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var target = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(),
                256,
                256,
                96);
            image.LoadCompleted += OnLoadCompleted;
            try
            {
                Task renderStarted = renderer.Document.RenderStarted.Task;
                while (!renderStarted.IsCompleted)
                {
                    await WaitForWorkCountAsync(image, expected: 0).WaitAsync(renderDeadline.Token);
                    if (renderStarted.IsCompleted)
                        break;

                    // Re-arm before painting so a recovery raised during or
                    // immediately after Paint cannot be lost between frames.
                    var nextRepaintPulse = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    Interlocked.Exchange(ref repaintPulse, nextRepaintPulse);

                    using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
                        image.Paint(drawingSession, new Rect(0, 0, 256, 256));

                    if (renderStarted.IsCompleted)
                        break;
                    await Task.WhenAny(renderStarted, nextRepaintPulse.Task)
                        .WaitAsync(renderDeadline.Token);
                }
            }
            catch (OperationCanceledException exception) when (renderDeadline.IsCancellationRequested)
            {
                BitmapOutputAdmission.Snapshot admission = BitmapOutputAdmission.Shared.GetSnapshot();
                throw new TimeoutException(
                    $"Visible SVG tile render did not start. " +
                    $"beforePaint(admissionCharged={admissionBeforeTilePaint.ChargedBytes}, " +
                    $"active={admissionBeforeTilePaint.ActiveBytes}, " +
                    $"waiters={admissionBeforeTilePaint.WaiterCount}, " +
                    $"capacityVersion={admissionBeforeTilePaint.CapacityVersion}, " +
                    $"cacheRetained={cacheBytesBeforeTilePaint}); " +
                    $"after(admissionCharged={admission.ChargedBytes}, active={admission.ActiveBytes}, " +
                    $"uncertain={admission.UncertainBytes}, capacity={admission.CapacityBytes}, " +
                    $"visibleLimit={admission.VisibleInitialLimitBytes}, waiters={admission.WaiterCount}, " +
                    $"offered={admission.OfferedBytes}, capacityVersion={admission.CapacityVersion}); " +
                    $"cache(retained={SharedCanvasBitmapCache.RetainedBytesForTests}, " +
                    $"pixelLedger={SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests}, " +
                    $"uncertainPixelLedger={SharedCanvasBitmapCache.UncertainBitmapDisposePixelWeightBytesForTests}); " +
                    $"image(work={image.ActiveImageWorkCountForTests}, " +
                    $"needsRecovery={image.NeedsBitmapOutputRecovery}, " +
                    $"svgTiles={image.UsesSvgTilesForAutomation}, raster={image.SvgRasterPixelSize}, " +
                    $"accessibility={image.AccessibilityState}); " +
                    $"provider(open={renderer.OpenCount}, render={renderer.RenderCount}, " +
                    $"dispose={renderer.Document.DisposeCount}).",
                    exception);
            }
            finally
            {
                image.LoadCompleted -= OnLoadCompleted;
            }
            await WaitForWorkCountAsync(image, expected: 0);
            Assert.True(renderer.Document.RenderCount > 0);
            Assert.Equal(0, renderer.Document.DisposeCount);

            image.SuspendResources();
            await WaitForWorkCountAsync(image, expected: 0);
            Assert.False(image.TryReleaseSuspendedResources());
            await renderer.Document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForWorkCountAsync(image, expected: 0);

            Assert.Equal(1, renderer.Document.DisposeCount);
            Assert.True(image.TryReleaseSuspendedResources());
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task PromptSvgDocumentRetirementFailureDoesNotDiscardPublishedBitmap()
    {
        var renderer = new ResidencySvgRenderer(throwOnDispose: true);
        var image = new ImageBox(
            CreateContext(null, session: null, renderer),
            CreateSvgDataUri("dispose-failure-" + Guid.NewGuid().ToString("N")),
            "SVG with provider cleanup failure");

        try
        {
            image.Measure(80);
            await LoadAsync(image);
            await renderer.Document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(image.UsesSvgTilesForAutomation);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Equal(1, renderer.Document.DisposeCount);

            image.Dispose();
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("test provider document disposal failed", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            image.Dispose();
            try
            {
                await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException) when (renderer.Document.DisposeCount > 0)
            {
                // This test's provider cleanup failure is the expected
                // retirement result; its assertion above observes the fault.
            }
        }
    }

    [Fact]
    public async Task CanceledLateSvgRenderRetainsDocumentAndDisposesUnclaimedRasterOnce()
    {
        using var session = CreateResidencySession();
        var renderer = new ResidencySvgRenderer(blockRender: true);
        var image = new ImageBox(
            CreateContext(null, session, renderer),
            CreateSvgDataUri("late-render-" + Guid.NewGuid().ToString("N")),
            "Late SVG");
        int publications = 0;
        image.LoadCompleted += (_, _) => Interlocked.Increment(ref publications);

        try
        {
            image.Measure(80);
            image.EnsureLoading();
            await renderer.Document.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task drain = ObserveResidencyDrainAsync(image);
            image.SuspendResources();
            Assert.False(image.TryReleaseSuspendedResources());
            await renderer.Document.RenderCancellationObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            // The state predicate proves the canceled caller and cancellation
            // callbacks have drained, leaving only the provider render whose
            // ignored cancellation must still own the document/raster lifetime.
            // Its native-use lease and detached result-cleanup barrier are
            // independent tracked work while RenderAsync is held.
            await WaitForWorkCountAsync(image, expected: 2);
            Assert.Equal(1, renderer.RenderCount);
            Assert.Equal(0, renderer.Document.DisposeCount);
            Assert.Equal(0, renderer.LastRasterOwner?.DisposeCount ?? 0);
            Assert.Null(image.Bitmap);
            Assert.Equal(0, Volatile.Read(ref publications));
            Assert.False(image.TryResumeResources());

            renderer.Document.ReleaseRender();
            await drain.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, renderer.LastRasterOwner!.DisposeCount);
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
            if (renderer.Document.DisposeCount == 0)
            {
                Task releaseDrained = ObserveResidencyDrainAsync(image);
                Assert.False(image.TryReleaseSuspendedResources());
                await renderer.Document.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await releaseDrained.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.True(image.TryReleaseSuspendedResources());
            Assert.Equal(1, renderer.RenderCount);
            Assert.Equal(1, renderer.Document.DisposeCount);
            Assert.Equal(1, renderer.LastRasterOwner.DisposeCount);
            Assert.True(image.TryResumeResources());
        }
        finally
        {
            renderer.Document.ReleaseRender();
            renderer.Document.ReleaseDispose();
            image.Dispose();
        }
    }

    [Fact]
    public async Task RetiredSvgDocumentDrainsItsOwnUseWhileUnrelatedDocumentRemainsActive()
    {
        var image = new ImageBox(
            CreateContext(null, session: null),
            CreateSvgDataUri("per-document-use-" + Guid.NewGuid().ToString("N")),
            "Per-document SVG lifetime");
        var oldDocument = new ResidencySvgDocument(blockRender: false, blockDispose: false);
        var newDocument = new ResidencySvgDocument(blockRender: false, blockDispose: false);
        IDisposable? queuedOldUse = null;
        IDisposable? nativeOldUse = null;
        IDisposable? newDocumentUse = null;

        try
        {
            Assert.True((bool)(InvokePrivate(image, "TryAdoptSvgDocument", oldDocument) ?? false));
            queuedOldUse = Assert.IsAssignableFrom<IDisposable>(
                InvokePrivate(image, "TryAcquireSvgDocumentUse", oldDocument));

            // Replacement retires the old document while an already-admitted
            // tile remains queued before provider admission.
            Assert.True((bool)(InvokePrivate(image, "TryAdoptSvgDocument", newDocument) ?? false));
            newDocumentUse = Assert.IsAssignableFrom<IDisposable>(
                InvokePrivate(image, "TryAcquireSvgDocumentUse", newDocument));

            // Model that queued tile entering native RenderAsync after the old
            // document has been retired. The child use must be allowed only
            // because the queued parent still owns the old document.
            nativeOldUse = Assert.IsAssignableFrom<IDisposable>(
                InvokePrivate(image, "TryAcquireNestedSvgDocumentUse", queuedOldUse));
            queuedOldUse.Dispose();
            queuedOldUse = null;
            Assert.Equal(0, oldDocument.DisposeCount);

            nativeOldUse.Dispose();
            nativeOldUse = null;
            await oldDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The new document is still independently leased, but that must
            // not delay cleanup of the retired document.
            Assert.Equal(1, oldDocument.DisposeCount);
            Assert.Equal(0, newDocument.DisposeCount);

            _ = InvokePrivate(image, "ReleaseSvgDocument", newDocument);
            Assert.Equal(0, newDocument.DisposeCount);
            newDocumentUse.Dispose();
            newDocumentUse = null;
            await newDocument.DisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, newDocument.DisposeCount);
        }
        finally
        {
            queuedOldUse?.Dispose();
            nativeOldUse?.Dispose();
            newDocumentUse?.Dispose();
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task LegacySessionSuspensionDoesNotDisableImageLoading()
    {
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { ReleaseOffscreenImageResources = false });
        var resolver = new CountingResolver(
            DisplaySizedRasterDecodeTests.CreatePng(32, 16),
            $"legacy-residency-{Guid.NewGuid():N}");
        var image = new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            "https://images.example/legacy.png",
            "Legacy image");

        try
        {
            image.SuspendResources();
            image.Measure(80);
            await LoadAsync(image);

            Assert.NotNull(image.Bitmap);
            Assert.Equal(1, resolver.ResolveCount);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.False(image.TryReleaseSuspendedResources());
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task PercentEncodedBuiltInPngLoadsWithoutUtf8ByteReplacement()
    {
        byte[] bytes = DisplaySizedRasterDecodeTests.CreatePng(83, 27);
        string source = "data:image/png," +
            string.Concat(Array.ConvertAll(bytes, value => $"%{value:X2}"));
        var image = new ImageBox(CreateContext(null, session: null), source, "Percent-encoded PNG");
        try
        {
            image.Measure(100);
            await LoadAsync(image);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            Assert.Equal(83u, image.Bitmap.SizeInPixels.Width);
            Assert.Equal(27u, image.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task WarmConstructorCacheCanReleaseSynchronouslyWithoutDrainEvent()
    {
        byte[] bytes = DisplaySizedRasterDecodeTests.CreatePng(64, 32);
        string source = "data:image/png;base64," + Convert.ToBase64String(bytes);
        // Use one resource creator for both halves of this device-cache test.
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var warm = new ImageBox(CreateContext(null, session: null, device: device), source, "Warm cache image");
        try
        {
            warm.Measure(80);
            await LoadAsync(warm);
            Assert.NotNull(warm.Bitmap);
            Assert.True(MarkdownBuiltInImageSourcePolicy.TryResolve(source, null, out var builtIn));
            CanvasDevice observedDevice = warm.Context.ResourceCreator.Device;
            bool cached = SharedCanvasBitmapCache.TryAcquire(observedDevice, builtIn.CacheKey, out var cacheProbe);
            cacheProbe?.Dispose();
            Assert.True(cached,
                $"Warm bitmap was not cached under its built-in source key; creator/device same={ReferenceEquals(device, observedDevice)}, device getter stable={ReferenceEquals(observedDevice, warm.Context.ResourceCreator.Device)}, cache bytes={SharedCanvasBitmapCache.RetainedBytesForTests}.");
        }
        finally
        {
            warm.Dispose();
        }

        // This tests synchronous release of an eligible source-sized warm
        // lease. Display-sized consumers intentionally defer until measured.
        using var session = CreateResidencySession(displaySized: false);
        var image = new ImageBox(CreateContext(null, session, device: device), source, "Warm cache image");
        int drainEvents = 0;
        image.ResidencyWorkDrained += (_, _) => Interlocked.Increment(ref drainEvents);
        try
        {
            Assert.NotNull(image.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);

            image.SuspendResources();

            Assert.True(image.TryReleaseSuspendedResources());
            Assert.Null(image.Bitmap);
            Assert.Equal(0, Volatile.Read(ref drainEvents));
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);

            Assert.True(image.TryResumeResources());
            await LoadAsync(image);
            Assert.NotNull(image.Bitmap);
            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
        }
        finally
        {
            image.Dispose();
        }
    }

    private static async Task LoadAsync(ImageBox image)
    {
        if (image.Bitmap is not null)
            return; // Constructor-acquired warm leases do not raise LoadCompleted.
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completed.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            image.EnsureLoading();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static Task ObserveResidencyDrainAsync(ImageBox image)
    {
        var drained = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => drained.TrySetResult();
        image.ResidencyWorkDrained += handler;
        _ = drained.Task.ContinueWith(
            static (_, state) =>
            {
                var (box, callback) = ((ImageBox, EventHandler))state!;
                box.ResidencyWorkDrained -= callback;
            },
            (image, handler),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return drained.Task;
    }

    private static async Task WaitForWorkCountAsync(ImageBox image, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (image.ActiveImageWorkCountForTests != expected)
            await Task.Delay(1, timeout.Token);
    }

    private static object? InvokePrivate(ImageBox image, string methodName, params object?[] arguments)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(typeof(ImageBox).FullName, methodName);
        return method.Invoke(image, arguments);
    }

    private static MarkdownPerformanceSession CreateResidencySession(bool displaySized = true) =>
        new(MarkdownPerformanceOptions.Progressive with
        {
            PrefetchDocumentImages = false,
            ReleaseOffscreenImageResources = true,
            UseDisplaySizedRasterDecode = displaySized,
        });

    private static MarkdownLayoutContext CreateContext(
        IMarkdownImageResolver? resolver,
        MarkdownPerformanceSession? session,
        IMarkdownSvgRenderer? svgRenderer = null,
        CanvasDevice? device = null)
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
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            device ?? CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = resolver,
            PerformanceSession = session,
            PerformanceDocumentOwner = session is null ? null : new object(),
            SvgRenderer = svgRenderer,
            RasterizationScale = 1,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static string CreateSvgDataUri(string marker)
    {
        string svg =
            "<svg xmlns='http://www.w3.org/2000/svg' width='80' height='40'>" +
            $"<title>Residency title</title><desc>{marker}</desc>" +
            "<rect width='80' height='40' fill='#2a6'/></svg>";
        return "data:image/svg+xml," + Uri.EscapeDataString(svg);
    }

    private sealed class CountingResolver(byte[] bytes, string cacheKey) : IMarkdownImageResolver
    {
        private int _resolveCount;
        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(
                    bytes,
                    "image/png",
                    CacheKey: cacheKey)));
        }
    }

    private sealed class UnusedResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Unavailable);
    }

    private sealed class LateResolver : IMarkdownImageResolver
    {
        private readonly TaskCompletionSource<MarkdownImageResolution> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return new ValueTask<MarkdownImageResolution>(_completion.Task);
        }

        internal void Complete(byte[] bytes) => _completion.TrySetResult(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                bytes,
                "image/png",
                CacheKey: "late-image-residency-test")));
    }

    private sealed class ResidencySvgRenderer : IMarkdownSvgRenderer
    {
        private int _openCount;

        internal ResidencySvgRenderer(
            bool blockRender = false,
            bool blockDispose = false,
            MarkdownSvgDocumentInfo? info = null,
            bool throwOnDispose = false)
        {
            Document = new ResidencySvgDocument(
                blockRender,
                blockDispose,
                info ?? new MarkdownSvgDocumentInfo(
                    IntrinsicWidthDips: 80,
                    IntrinsicHeightDips: 40,
                    IntrinsicAspectRatio: 2,
                    Description: "Provider description"),
                throwOnDispose);
        }

        internal ResidencySvgDocument Document { get; }
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int RenderCount => Document.RenderCount;
        internal CountingMemoryOwner? LastRasterOwner => Document.LastRasterOwner;

        public long CacheGeneration => 0;

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
            return ValueTask.FromResult<IMarkdownSvgDocument>(Document);
        }
    }

    private sealed class ResidencySvgDocument(
        bool blockRender,
        bool blockDispose,
        MarkdownSvgDocumentInfo? info = null,
        bool throwOnDispose = false) : IMarkdownSvgDocument
    {
        private readonly ManualResetEventSlim _releaseDispose = new(initialState: false);
        private int _renderCount;
        private int _disposeCount;

        internal TaskCompletionSource RenderStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RenderCancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRenderTask { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal CountingMemoryOwner? LastRasterOwner { get; private set; }
        internal int RenderCount => Volatile.Read(ref _renderCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public MarkdownSvgDocumentInfo Info { get; } = info ?? new(
            IntrinsicWidthDips: 80,
            IntrinsicHeightDips: 40,
            IntrinsicAspectRatio: 2,
            Description: "Provider description");

        internal void ReleaseRender() => ReleaseRenderTask.TrySetResult();
        internal void ReleaseDispose() => _releaseDispose.Set();

        public async ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renderCount);
            RenderStarted.TrySetResult();
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((ResidencySvgDocument)state!).RenderCancellationObserved.TrySetResult(),
                this);
            if (blockRender)
                await ReleaseRenderTask.Task.ConfigureAwait(false);

            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int length = checked(width * height * 4);
            var owner = new CountingMemoryOwner(length);
            LastRasterOwner = owner;
            return new MarkdownSvgRaster(
                owner,
                length,
                width,
                height,
                checked(width * 4),
                request.PixelFormat);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            DisposeStarted.TrySetResult();
            if (blockDispose)
                _releaseDispose.Wait();
            DisposeCompleted.TrySetResult();
            if (throwOnDispose)
            {
                throw new InvalidOperationException("test provider document disposal failed");
            }
        }
    }

    private sealed class CountingMemoryOwner(int length) : IMemoryOwner<byte>
    {
        private IMemoryOwner<byte>? _inner = MemoryPool<byte>.Shared.Rent(length);
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public Memory<byte> Memory =>
            Volatile.Read(ref _inner)?.Memory ?? throw new ObjectDisposedException(nameof(CountingMemoryOwner));

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Interlocked.Exchange(ref _inner, null)?.Dispose();
        }
    }
}
