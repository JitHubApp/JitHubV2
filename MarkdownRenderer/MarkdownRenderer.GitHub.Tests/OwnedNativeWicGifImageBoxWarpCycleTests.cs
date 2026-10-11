using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
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
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Repeats the shipping GIF ImageBox path through WARP, output publication,
/// source-cache reuse, and logical retirement. Process memory/handle values are
/// diagnostic observations only; they are not codec or device-memory bounds.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class OwnedNativeWicGifImageBoxWarpCycleTests(ITestOutputHelper output)
{
    private const int CycleCount = 100;
    private const float DisplayWidth = 8f;
    private const long RasterPixelLimit = 512;

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task ImageBoxWarpCyclesFirstFrameMatrixAndDrainsEachRetiredOutput()
    {
        OwnedGifCycleFixture[] fixtures = [];
        MarkdownPerformanceSession? session = null;
        IMarkdownPerformanceDocumentScope? documentScope = null;
        MarkdownImagePreparationAdmission processAdmission = MarkdownImagePreparationAdmission.Shared;
        BitmapOutputAdmission outputAdmission = BitmapOutputAdmission.Shared;
        long livePixelWeightBaseline = 0;
        long cacheRetainedBaseline = 0;
        bool sharedCacheTouched = false;
        bool globalBaselinesInitialized = false;

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer,
            "GIF cycle qualification must run on WARP without silently selecting hardware.");

        try
        {
            fixtures = CreateFixtureMatrix();
            SharedCanvasBitmapCache.Clear();
            sharedCacheTouched = true;
            await WaitUntilAsync(() => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
            await WaitForProcessAdmissionIdleAsync(processAdmission);
            livePixelWeightBaseline = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
            cacheRetainedBaseline = SharedCanvasBitmapCache.RetainedBytesForTests;
            globalBaselinesInitialized = true;

            // Build and retain all WinRT/WARP oracle pixels before the measured
            // window. No decoder/oracle work or forced collection occurs during
            // the 100-cycle sample.
            foreach (OwnedGifCycleFixture fixture in fixtures)
                await PrepareFixtureAsync(fixture, device);

            session = new MarkdownPerformanceSession(
                MarkdownPerformanceOptions.Progressive with
                {
                    UseDisplaySizedRasterDecode = true,
                    MaxRasterOutputPixels = RasterPixelLimit,
                });
            var resolver = new GifFixtureResolver(fixtures);
            documentScope = session.OpenDocument(
                resolver,
                new MarkdownImageResolveContext(BaseUri: null));
            object documentOwner = new();

            // Warm each source-cache entry and the ImageBox/WARP pipeline, then
            // retire every warm output before measuring process observations.
            foreach (OwnedGifCycleFixture fixture in fixtures)
            {
                await RunImageCycleAsync(
                    device, documentScope, session, documentOwner, fixture,
                    processAdmission, outputAdmission, livePixelWeightBaseline, cacheRetainedBaseline);
            }

            long sourceCacheBytes = session.GetSnapshot().SourceCacheBytes;
            Assert.True(sourceCacheBytes >= fixtures.Sum(static fixture => (long)fixture.Source.Length));
            Assert.Equal(fixtures.Length, resolver.TotalCalls);
            Assert.All(fixtures, fixture => Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri)));
            Assert.Equal(livePixelWeightBaseline, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(cacheRetainedBaseline, SharedCanvasBitmapCache.RetainedBytesForTests);
            await WaitForProcessAdmissionIdleAsync(processAdmission);

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long baselinePrivateBytes = process.PrivateMemorySize64;
            int baselineHandleCount = process.HandleCount;
            long maximumPrivateBytes = baselinePrivateBytes;
            int maximumHandleCount = baselineHandleCount;
            long baselineManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            GCMemoryInfo baselineGcInfo = GC.GetGCMemoryInfo();
            int[] baselineCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            long measuredCycleAllocatedBytes = 0;
            long measuredObserverAllocatedBytes = 0;
            var observations = new StringBuilder(
                "cycle,fixture,privateBytesAfterRetirement,handleCountAfterRetirement," +
                "cycleAllocatedBytes,observerAllocatedBytes,managedHeapBytes\n");
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot diagnosticsBeforeCycles = RasterNativeWicDiagnostics.GetSnapshot();
#endif

            // There are eleven variants including the 61-frame static preview
            // and ICC-bearing fixture. The coprime stride interleaves them.
            const int InterleaveStride = 7;
            Assert.Equal(1, GreatestCommonDivisor(InterleaveStride, fixtures.Length));
            for (int cycle = 0; cycle < CycleCount; cycle++)
            {
                OwnedGifCycleFixture fixture = fixtures[(cycle * InterleaveStride) % fixtures.Length];
                long cycleAllocationBefore = GC.GetTotalAllocatedBytes(precise: true);
                await RunImageCycleAsync(
                    device, documentScope, session, documentOwner, fixture,
                    processAdmission, outputAdmission, livePixelWeightBaseline, cacheRetainedBaseline);

                Assert.Equal(sourceCacheBytes, session.GetSnapshot().SourceCacheBytes);
                Assert.Equal(fixtures.Length, resolver.TotalCalls);
                Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri));
                long cycleAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - cycleAllocationBefore;
                long observerAllocationBefore = GC.GetTotalAllocatedBytes(precise: true);
                process.Refresh();
                long sampledPrivateBytes = process.PrivateMemorySize64;
                int sampledHandleCount = process.HandleCount;
                long observerAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - observerAllocationBefore;
                measuredCycleAllocatedBytes += cycleAllocatedBytes;
                measuredObserverAllocatedBytes += observerAllocatedBytes;
                maximumPrivateBytes = System.Math.Max(maximumPrivateBytes, sampledPrivateBytes);
                maximumHandleCount = System.Math.Max(maximumHandleCount, sampledHandleCount);
                observations.Append(cycle + 1).Append(',')
                    .Append(fixture.Name).Append(',')
                    .Append(sampledPrivateBytes).Append(',')
                    .Append(sampledHandleCount).Append(',')
                    .Append(cycleAllocatedBytes).Append(',')
                    .Append(observerAllocatedBytes).Append(',')
                    .Append(GC.GetTotalMemory(forceFullCollection: false)).AppendLine();
            }

            Assert.Equal(fixtures.Length, resolver.TotalCalls);
            Assert.All(fixtures, fixture => Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri)));
            Assert.Equal(livePixelWeightBaseline, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(cacheRetainedBaseline, SharedCanvasBitmapCache.RetainedBytesForTests);
            await WaitForProcessAdmissionIdleAsync(processAdmission);
            Assert.Equal(0, processAdmission.PendingRequests);

            process.Refresh();
            GCMemoryInfo finalGcInfo = GC.GetGCMemoryInfo();
            output.WriteLine(
                $"GIF ImageBox/WARP cycle diagnostic: warmups={fixtures.Length}; cycles={CycleCount}; " +
                $"variants={fixtures.Length}; sourceCacheBytes={sourceCacheBytes}; " +
                $"privateBytesBaseline={baselinePrivateBytes}; privateBytesPeakAtRetirement={maximumPrivateBytes}; " +
                $"privateBytesFinal={process.PrivateMemorySize64}; handleCountBaseline={baselineHandleCount}; " +
                $"handleCountPeakAtRetirement={maximumHandleCount}; handleCountFinal={process.HandleCount}. " +
                "Samples are process-wide observations after logical output retirement, not bounds on codec " +
                "intermediates, private bytes, RSS, or device memory. No forced GC is used.");
            output.WriteLine(
                $"Managed GIF cycle diagnostic: measuredCycleAllocatedBytes={measuredCycleAllocatedBytes}; " +
                $"measuredObserverAllocatedBytes={measuredObserverAllocatedBytes}; " +
                $"managedHeapBytesBaseline={baselineManagedHeapBytes}; " +
                $"managedHeapBytesFinal={GC.GetTotalMemory(forceFullCollection: false)}; " +
                $"gcCommittedBytesBaseline={baselineGcInfo.TotalCommittedBytes}; " +
                $"gcCommittedBytesFinal={finalGcInfo.TotalCommittedBytes}; " +
                $"gen0Collections={GC.CollectionCount(0) - baselineCollections[0]}; " +
                $"gen1Collections={GC.CollectionCount(1) - baselineCollections[1]}; " +
                $"gen2Collections={GC.CollectionCount(2) - baselineCollections[2]}. " +
                "Allocation intervals include testhost/background work; they do not prove native peak or retained leakage.");
            output.WriteLine(observations.ToString());
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot diagnosticsAfterCycles = RasterNativeWicDiagnostics.GetSnapshot();
            Assert.Equal(CycleCount,
                diagnosticsAfterCycles.NativeStarted - diagnosticsBeforeCycles.NativeStarted);
            Assert.Equal(CycleCount,
                diagnosticsAfterCycles.PixelLeaseCompleted - diagnosticsBeforeCycles.PixelLeaseCompleted);
            output.WriteLine(
                $"Diagnostic native GIF route deltas for measured cycles: started=" +
                $"{diagnosticsAfterCycles.NativeStarted - diagnosticsBeforeCycles.NativeStarted}; " +
                $"pixelLeases={diagnosticsAfterCycles.PixelLeaseCompleted - diagnosticsBeforeCycles.PixelLeaseCompleted}.");
#else
            output.WriteLine(
                "Native-route counters are diagnostics-build-only; default-build exact ImageBox/WARP output " +
                "and retirement assertions do not independently prove branch identity.");
#endif
        }
        finally
        {
            try
            {
                if (sharedCacheTouched)
                {
                    SharedCanvasBitmapCache.Clear();
                    if (globalBaselinesInitialized)
                    {
                        await WaitUntilAsync(() =>
                            SharedCanvasBitmapCache.RetainedBytesForTests == cacheRetainedBaseline &&
                            SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests == livePixelWeightBaseline);
                    }
                    else
                    {
                        await WaitUntilAsync(() => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
                    }
                }
            }
            finally
            {
                try
                {
                    documentScope?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (session is not null)
                            await session.DisposeAsync();
                    }
                    finally
                    {
                        await WaitForProcessAdmissionIdleAsync(processAdmission);
                        foreach (OwnedGifCycleFixture fixture in fixtures)
                        {
                            Array.Clear(fixture.Source);
                            if (fixture.ExpectedPixels is not null)
                                Array.Clear(fixture.ExpectedPixels);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task ImageBoxWarpCyclesReuseOneContextPerDocumentAndReportAllocationObservations()
    {
        OwnedGifCycleFixture[] fixtures = [];
        MarkdownPerformanceSession? session = null;
        IMarkdownPerformanceDocumentScope? documentScope = null;
        MarkdownLayoutContext? sharedContext = null;
        MarkdownImagePreparationAdmission processAdmission = MarkdownImagePreparationAdmission.Shared;
        BitmapOutputAdmission outputAdmission = BitmapOutputAdmission.Shared;
        long livePixelWeightBaseline = 0;
        long cacheRetainedBaseline = 0;
        bool sharedCacheTouched = false;
        bool globalBaselinesInitialized = false;

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer,
            "GIF context-reuse qualification must run on WARP without silently selecting hardware.");

        try
        {
            fixtures = CreateFixtureMatrix();
            SharedCanvasBitmapCache.Clear();
            sharedCacheTouched = true;
            await WaitUntilAsync(() => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
            await WaitForProcessAdmissionIdleAsync(processAdmission);
            livePixelWeightBaseline = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
            cacheRetainedBaseline = SharedCanvasBitmapCache.RetainedBytesForTests;
            globalBaselinesInitialized = true;

            foreach (OwnedGifCycleFixture fixture in fixtures)
                await PrepareFixtureAsync(fixture, device);

            session = new MarkdownPerformanceSession(
                MarkdownPerformanceOptions.Progressive with
                {
                    UseDisplaySizedRasterDecode = true,
                    MaxRasterOutputPixels = RasterPixelLimit,
                });
            var resolver = new GifFixtureResolver(fixtures);
            documentScope = session.OpenDocument(
                resolver,
                new MarkdownImageResolveContext(BaseUri: null));
            object documentOwner = new();
            sharedContext = CreateReusableContext(device, documentScope, session, documentOwner);
            Assert.Same(documentScope, sharedContext.ImageResolver);
            Assert.Same(session, sharedContext.PerformanceSession);
            Assert.Same(documentOwner, sharedContext.PerformanceDocumentOwner);

            // Exactly one warmup per fixture, matching the original cohort.
            foreach (OwnedGifCycleFixture fixture in fixtures)
            {
                await RunImageCycleAsync(
                    device, documentScope, session, documentOwner, fixture,
                    processAdmission, outputAdmission, livePixelWeightBaseline, cacheRetainedBaseline,
                    sharedContext);
            }

            long sourceCacheBytes = session.GetSnapshot().SourceCacheBytes;
            Assert.True(sourceCacheBytes >= fixtures.Sum(static fixture => (long)fixture.Source.Length));
            Assert.Equal(fixtures.Length, resolver.TotalCalls);
            Assert.All(fixtures, fixture => Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri)));
            Assert.Equal(livePixelWeightBaseline, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(cacheRetainedBaseline, SharedCanvasBitmapCache.RetainedBytesForTests);
            await WaitForProcessAdmissionIdleAsync(processAdmission);

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long baselinePrivateBytes = process.PrivateMemorySize64;
            int baselineHandleCount = process.HandleCount;
            long maximumPrivateBytes = baselinePrivateBytes;
            int maximumHandleCount = baselineHandleCount;
            long baselineManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            GCMemoryInfo baselineGcInfo = GC.GetGCMemoryInfo();
            int[] baselineCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            long measuredCycleAllocatedBytes = 0;
            long measuredObserverAllocatedBytes = 0;
            var observations = new StringBuilder(
                "cycle,fixture,privateBytesAfterRetirement,handleCountAfterRetirement," +
                "cycleAllocatedBytes,observerAllocatedBytes,managedHeapBytes\n");
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot diagnosticsBeforeCycles = RasterNativeWicDiagnostics.GetSnapshot();
#endif

            const int InterleaveStride = 7;
            Assert.Equal(1, GreatestCommonDivisor(InterleaveStride, fixtures.Length));
            for (int cycle = 0; cycle < CycleCount; cycle++)
            {
                OwnedGifCycleFixture fixture = fixtures[(cycle * InterleaveStride) % fixtures.Length];
                long cycleAllocationBefore = GC.GetTotalAllocatedBytes(precise: true);
                await RunImageCycleAsync(
                    device, documentScope, session, documentOwner, fixture,
                    processAdmission, outputAdmission, livePixelWeightBaseline, cacheRetainedBaseline,
                    sharedContext);

                Assert.Equal(sourceCacheBytes, session.GetSnapshot().SourceCacheBytes);
                Assert.Equal(fixtures.Length, resolver.TotalCalls);
                Assert.All(fixtures, candidate => Assert.Equal(1, resolver.GetCallCount(candidate.SourceUri)));
                long cycleAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - cycleAllocationBefore;
                long observerAllocationBefore = GC.GetTotalAllocatedBytes(precise: true);
                process.Refresh();
                long sampledPrivateBytes = process.PrivateMemorySize64;
                int sampledHandleCount = process.HandleCount;
                long observerAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - observerAllocationBefore;
                measuredCycleAllocatedBytes += cycleAllocatedBytes;
                measuredObserverAllocatedBytes += observerAllocatedBytes;
                maximumPrivateBytes = System.Math.Max(maximumPrivateBytes, sampledPrivateBytes);
                maximumHandleCount = System.Math.Max(maximumHandleCount, sampledHandleCount);
                observations.Append(cycle + 1).Append(',')
                    .Append(fixture.Name).Append(',')
                    .Append(sampledPrivateBytes).Append(',')
                    .Append(sampledHandleCount).Append(',')
                    .Append(cycleAllocatedBytes).Append(',')
                    .Append(observerAllocatedBytes).Append(',')
                    .Append(GC.GetTotalMemory(forceFullCollection: false)).AppendLine();
            }

            Assert.Equal(fixtures.Length, resolver.TotalCalls);
            Assert.All(fixtures, fixture => Assert.Equal(1, resolver.GetCallCount(fixture.SourceUri)));
            Assert.Equal(livePixelWeightBaseline, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(cacheRetainedBaseline, SharedCanvasBitmapCache.RetainedBytesForTests);
            await WaitForProcessAdmissionIdleAsync(processAdmission);
            Assert.Equal(0, processAdmission.PendingRequests);

            process.Refresh();
            GCMemoryInfo finalGcInfo = GC.GetGCMemoryInfo();
            output.WriteLine(
                $"GIF shared-context ImageBox/WARP diagnostic: warmups={fixtures.Length}; cycles={CycleCount}; " +
                $"variants={fixtures.Length}; sourceCacheBytes={sourceCacheBytes}; " +
                $"privateBytesBaseline={baselinePrivateBytes}; privateBytesPeakAtRetirement={maximumPrivateBytes}; " +
                $"privateBytesFinal={process.PrivateMemorySize64}; handleCountBaseline={baselineHandleCount}; " +
                $"handleCountPeakAtRetirement={maximumHandleCount}; handleCountFinal={process.HandleCount}. " +
                "Samples are process-wide observations after logical output retirement, not bounds on codec " +
                "intermediates, private bytes, RSS, or device memory. No forced GC is used.");
            output.WriteLine(
                $"Managed shared-context GIF diagnostic: measuredCycleAllocatedBytes={measuredCycleAllocatedBytes}; " +
                $"measuredObserverAllocatedBytes={measuredObserverAllocatedBytes}; " +
                $"managedHeapBytesBaseline={baselineManagedHeapBytes}; " +
                $"managedHeapBytesFinal={GC.GetTotalMemory(forceFullCollection: false)}; " +
                $"gcCommittedBytesBaseline={baselineGcInfo.TotalCommittedBytes}; " +
                $"gcCommittedBytesFinal={finalGcInfo.TotalCommittedBytes}; " +
                $"gen0Collections={GC.CollectionCount(0) - baselineCollections[0]}; " +
                $"gen1Collections={GC.CollectionCount(1) - baselineCollections[1]}; " +
                $"gen2Collections={GC.CollectionCount(2) - baselineCollections[2]}. " +
                "Allocation intervals include testhost/background work and do not prove native peak or retained leakage.");
            output.WriteLine(observations.ToString());
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot diagnosticsAfterCycles = RasterNativeWicDiagnostics.GetSnapshot();
            Assert.Equal(CycleCount,
                diagnosticsAfterCycles.NativeStarted - diagnosticsBeforeCycles.NativeStarted);
            Assert.Equal(CycleCount,
                diagnosticsAfterCycles.PixelLeaseCompleted - diagnosticsBeforeCycles.PixelLeaseCompleted);
            output.WriteLine(
                $"Diagnostic native GIF route deltas for shared-context measured cycles: started=" +
                $"{diagnosticsAfterCycles.NativeStarted - diagnosticsBeforeCycles.NativeStarted}; " +
                $"pixelLeases={diagnosticsAfterCycles.PixelLeaseCompleted - diagnosticsBeforeCycles.PixelLeaseCompleted}.");
#else
            output.WriteLine(
                "Native-route counters are diagnostics-build-only; default-build exact ImageBox/WARP output " +
                "and retirement assertions do not independently prove branch identity.");
#endif
        }
        finally
        {
            try
            {
                if (sharedCacheTouched)
                {
                    SharedCanvasBitmapCache.Clear();
                    if (globalBaselinesInitialized)
                    {
                        await WaitUntilAsync(() =>
                            SharedCanvasBitmapCache.RetainedBytesForTests == cacheRetainedBaseline &&
                            SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests == livePixelWeightBaseline);
                    }
                    else
                    {
                        await WaitUntilAsync(() => SharedCanvasBitmapCache.RetainedBytesForTests == 0);
                    }
                }
            }
            finally
            {
                try
                {
                    documentScope?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (session is not null)
                            await session.DisposeAsync();
                    }
                    finally
                    {
                        await WaitForProcessAdmissionIdleAsync(processAdmission);
                        foreach (OwnedGifCycleFixture fixture in fixtures)
                        {
                            Array.Clear(fixture.Source);
                            if (fixture.ExpectedPixels is not null)
                                Array.Clear(fixture.ExpectedPixels);
                        }
                    }
                }
            }
        }
    }

    private static OwnedGifCycleFixture[] CreateFixtureMatrix()
    {
        List<OwnedNativeWicGifFixtureFactory.Fixture> sources =
            OwnedNativeWicGifFixtureFactory.CreateCoreMatrix()
                .Append(OwnedNativeWicGifFixtureFactory.CreateOverFrameLimitStaticPreview())
                .ToList();
        Assert.Equal(10, sources.Count);

        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        try
        {
            sources.Add(OwnedNativeWicGifFixtureFactory.EmbedIccProfile(sources[0], profile) with
            {
                Name = "gct-full-frame-icc",
            });
        }
        finally
        {
            Array.Clear(profile);
        }

        Assert.Equal(11, sources.Count);
        return sources.Select(static source => new OwnedGifCycleFixture(source)).ToArray();
    }

    private static async Task PrepareFixtureAsync(OwnedGifCycleFixture fixture, CanvasDevice device)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Source);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
            $"{fixture.Name} is not admitted for static preview: {budget.Reason}");
        Assert.Equal("GIF", budget.Format);
        Assert.Equal(fixture.CanvasWidth, budget.Width);
        Assert.Equal(fixture.CanvasHeight, budget.Height);
        Assert.Equal(fixture.FrameCount, budget.FrameCount);
        Assert.Equal((fixture.SourceFixture.FirstFrame.Left, fixture.SourceFixture.FirstFrame.Top,
                fixture.SourceFixture.FirstFrame.Width, fixture.SourceFixture.FirstFrame.Height),
            (budget.FirstFrame!.Value.Left, budget.FirstFrame.Value.Top,
                budget.FirstFrame.Value.Width, budget.FirstFrame.Value.Height));

        var sizing = new RasterImageSizingRequest(
            DisplayWidth,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: RasterPixelLimit);
        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(
            fixture.Source, budget, sizing, out RasterGifFirstFrameDecodePlan plan), fixture.Name);
        fixture.Budget = budget;
        fixture.Plan = plan;
        fixture.ExpectedPixels = await DecodeLegacyImageBoxCanvasAsync(fixture.Source, budget, plan, device);
        Assert.InRange(checked((long)plan.OutputWidth * plan.OutputHeight), 1, RasterPixelLimit);
    }

    private static async Task RunImageCycleAsync(
        CanvasDevice device,
        IMarkdownPerformanceDocumentScope documentScope,
        MarkdownPerformanceSession session,
        object documentOwner,
        OwnedGifCycleFixture fixture,
        MarkdownImagePreparationAdmission processAdmission,
        BitmapOutputAdmission outputAdmission,
        long livePixelWeightBaseline,
        long cacheRetainedBaseline,
        MarkdownLayoutContext? sharedContext = null)
    {
        Assert.NotNull(fixture.ExpectedPixels);
        long outputChargeBaseline = outputAdmission.GetSnapshot().ChargedBytes;
        Assert.Equal(livePixelWeightBaseline, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        Assert.Equal(cacheRetainedBaseline, SharedCanvasBitmapCache.RetainedBytesForTests);
        await WaitForProcessAdmissionIdleAsync(processAdmission);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        RasterNativeWicDiagnosticSnapshot before = RasterNativeWicDiagnostics.GetSnapshot();
#endif
        ImageBox? image = null;
        CanvasBitmap? bitmap = null;
        try
        {
            image = sharedContext is null
                ? CreateImage(device, documentScope, session, documentOwner, fixture.SourceUri)
                : new ImageBox(sharedContext, fixture.SourceUri, "project-authored GIF first-frame fixture");
            if (sharedContext is not null)
                Assert.Same(sharedContext, image.Context);
            await LoadAndAssertGeometryAsync(image, fixture);
            bitmap = fixture.Plan.RequiresComposition
                ? Assert.IsType<CanvasRenderTarget>(image.Bitmap)
                : Assert.IsType<CanvasBitmap>(image.Bitmap);
            Assert.Equal((uint)fixture.Plan.OutputWidth, bitmap.SizeInPixels.Width);
            Assert.Equal((uint)fixture.Plan.OutputHeight, bitmap.SizeInPixels.Height);
            byte[] actualPixels = bitmap.GetPixelBytes();
            try
            {
                Assert.Equal(fixture.ExpectedPixels, actualPixels);
            }
            finally
            {
                Array.Clear(actualPixels);
            }

            Assert.Equal(MarkdownImageAccessibilityState.Loaded, image.AccessibilityState);
            await WaitForProcessAdmissionIdleAsync(processAdmission);
            long expectedOutputBytes = checked((long)fixture.Plan.OutputWidth * fixture.Plan.OutputHeight * 4);
            Assert.Equal(outputChargeBaseline + expectedOutputBytes, outputAdmission.GetSnapshot().ChargedBytes);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnosticSnapshot after = RasterNativeWicDiagnostics.GetSnapshot();
            Assert.Equal(before.NativeStarted + 1, after.NativeStarted);
            Assert.Equal(before.PixelLeaseCompleted + 1, after.PixelLeaseCompleted);
#endif
        }
        finally
        {
            try
            {
                bitmap = null;
                image?.Dispose();
                ImageBox? retiredImage = image;
                if (retiredImage is not null)
                {
                    Task retirement = retiredImage.RetirementTask;
                    await retirement.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    Assert.True(retirement.IsCompletedSuccessfully,
                        "The image box must finish terminal retirement before the cycle is sampled as retired.");
                    await WaitUntilAsync(() => retiredImage.ActiveImageWorkCountForTests == 0);
                }
            }
            finally
            {
                SharedCanvasBitmapCache.Clear();
                await WaitUntilAsync(() =>
                    processAdmission.ActiveCount == 0 &&
                    processAdmission.ActiveBytes == 0 &&
                    processAdmission.PendingRequests == 0 &&
                    outputAdmission.GetSnapshot().ChargedBytes == outputChargeBaseline &&
                    SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests == livePixelWeightBaseline &&
                    SharedCanvasBitmapCache.RetainedBytesForTests == cacheRetainedBaseline);
            }
        }
    }

    private static async Task<byte[]> DecodeLegacyImageBoxCanvasAsync(
        byte[] source,
        RasterImageBudgetResult budget,
        RasterGifFirstFrameDecodePlan plan,
        CanvasDevice device)
    {
        using SoftwareBitmap frame = await DecodeWinRtFirstFrameAsync(
            source, plan.DecodeWidth, plan.DecodeHeight, budget.FrameCount,
            plan.FrameWidth, plan.FrameHeight);
        using CanvasBitmap decodedFrame = CanvasBitmap.CreateFromSoftwareBitmap(device, frame);
        if (!plan.RequiresComposition)
            return decodedFrame.GetPixelBytes();

        using var target = new CanvasRenderTarget(device, plan.OutputWidth, plan.OutputHeight, 96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            var destination = new Rect(
                plan.FrameLeft * (double)plan.OutputWidth / plan.CanvasWidth,
                plan.FrameTop * (double)plan.OutputHeight / plan.CanvasHeight,
                plan.FrameWidth * (double)plan.OutputWidth / plan.CanvasWidth,
                plan.FrameHeight * (double)plan.OutputHeight / plan.CanvasHeight);
            drawing.DrawImage(
                decodedFrame,
                destination,
                new Rect(0, 0, decodedFrame.Size.Width, decodedFrame.Size.Height),
                1f,
                CanvasImageInterpolation.Linear);
        }
        return target.GetPixelBytes();
    }

    private static async Task<SoftwareBitmap> DecodeWinRtFirstFrameAsync(
        byte[] source,
        int width,
        int height,
        int expectedFrameCount,
        int expectedSourceWidth,
        int expectedSourceHeight)
    {
        using var backing = new MemoryStream(source, writable: false);
        using IRandomAccessStream stream = backing.AsRandomAccessStream();
        IAsyncOperation<BitmapDecoder> decoderOperation = BitmapDecoder.CreateAsync(stream);
        BitmapDecoder decoder;
        try
        {
            decoder = await decoderOperation;
        }
        finally
        {
            decoderOperation.Close();
        }

        Assert.Equal((uint)expectedFrameCount, decoder.FrameCount);
        Assert.Equal((uint)expectedSourceWidth, decoder.PixelWidth);
        Assert.Equal((uint)expectedSourceHeight, decoder.PixelHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = checked((uint)width),
            ScaledHeight = checked((uint)height),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        IAsyncOperation<SoftwareBitmap> bitmapOperation = decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        try
        {
            SoftwareBitmap bitmap = await bitmapOperation;
            Assert.Equal(width, bitmap.PixelWidth);
            Assert.Equal(height, bitmap.PixelHeight);
            return bitmap;
        }
        finally
        {
            bitmapOperation.Close();
        }
    }

    private static async Task LoadAndAssertGeometryAsync(ImageBox image, OwnedGifCycleFixture fixture)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completed.TrySetResult();
        };
        image.LoadCompleted += handler;
        try
        {
            image.Measure(DisplayWidth);
            image.EnsureLoading();
            if (image.Bitmap is null || image.ActiveImageWorkCountForTests != 0)
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            image.LoadCompleted -= handler;
        }

        await WaitUntilAsync(() => image.ActiveImageWorkCountForTests == 0);
        image.Measure(DisplayWidth);
        float expectedDisplayWidth = System.Math.Min(DisplayWidth, fixture.CanvasWidth);
        Assert.Equal(expectedDisplayWidth, image.MeasuredImageWidth, precision: 2);
        Assert.Equal(
            expectedDisplayWidth * fixture.CanvasHeight / fixture.CanvasWidth,
            image.MeasuredImageHeight,
            precision: 1);
        Assert.NotNull(image.Bitmap);
    }

    private static ImageBox CreateImage(
        CanvasDevice device,
        IMarkdownImageResolver documentScope,
        MarkdownPerformanceSession session,
        object documentOwner,
        string source)
    {
        var style = new ElementStyle();
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
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap("owned GIF ImageBox/WARP cycle qualification"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = documentScope,
            PerformanceSession = session,
            PerformanceDocumentOwner = documentOwner,
            RasterizationScale = 1,
            ImageCancellationToken = CancellationToken.None,
        };
        return new ImageBox(context, source, "project-authored GIF first-frame fixture");
    }

    private static MarkdownLayoutContext CreateReusableContext(
        CanvasDevice device,
        IMarkdownImageResolver documentScope,
        MarkdownPerformanceSession session,
        object documentOwner)
    {
        var style = new ElementStyle();
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
            device,
            theme,
            new MarkdownSourceMap("owned GIF shared-context cycle qualification"),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = documentScope,
            PerformanceSession = session,
            PerformanceDocumentOwner = documentOwner,
            RasterizationScale = 1,
            ImageCancellationToken = CancellationToken.None,
        };
    }

    private static async Task WaitForProcessAdmissionIdleAsync(MarkdownImagePreparationAdmission admission)
    {
        await WaitUntilAsync(() =>
            admission.ActiveCount == 0 && admission.ActiveBytes == 0 && admission.PendingRequests == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            int remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
    }

    private sealed class OwnedGifCycleFixture(OwnedNativeWicGifFixtureFactory.Fixture sourceFixture)
    {
        internal OwnedNativeWicGifFixtureFactory.Fixture SourceFixture { get; } = sourceFixture;
        internal string Name => SourceFixture.Name;
        internal byte[] Source => SourceFixture.Bytes;
        internal int CanvasWidth => SourceFixture.CanvasWidth;
        internal int CanvasHeight => SourceFixture.CanvasHeight;
        internal int FrameCount => SourceFixture.FrameCount;
        internal string SourceUri { get; } = $"https://images.example/owned-gif-cycle/{Guid.NewGuid():N}";
        internal string CacheKey { get; } = $"owned-gif-cycle:{Guid.NewGuid():N}";
        internal RasterImageBudgetResult Budget { get; set; } = null!;
        internal RasterGifFirstFrameDecodePlan Plan { get; set; }
        internal byte[]? ExpectedPixels { get; set; }
    }

    private sealed class GifFixtureResolver : IMarkdownImageResolver
    {
        private readonly Dictionary<string, OwnedGifCycleFixture> _fixtures;
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        internal GifFixtureResolver(IEnumerable<OwnedGifCycleFixture> fixtures) =>
            _fixtures = fixtures.ToDictionary(static fixture => fixture.SourceUri, StringComparer.Ordinal);

        internal int TotalCalls => _calls.Values.Sum();
        internal int GetCallCount(string source) => _calls.TryGetValue(source, out int count) ? count : 0;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fixtures.TryGetValue(source, out OwnedGifCycleFixture? fixture) || fixture is null)
                throw new InvalidOperationException("ImageBox requested an unregistered GIF fixture.");
            _calls.AddOrUpdate(source, 1, static (_, count) => checked(count + 1));
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(fixture.Source, "image/gif", CacheKey: fixture.CacheKey)));
        }
    }
}
