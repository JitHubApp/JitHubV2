using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Diagnostic-only qualification of the admitted ImageBox path for unusually
/// large static rasters. Sampled private bytes are evidence from this test
/// process; they are not a native peak or an RSS bound.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class NearMaximumRasterWicDecodeQualificationTests(ITestOutputHelper output)
{
    private const int DisplayWidth = 256;
    private const int SampleIntervalMilliseconds = 2;
    private const string PinnedWebpSha256 =
        "6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85";
    private static readonly TimeSpan WorkloadTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task NearFortyMegapixelPng_ReportsCappedImageBoxDecodeAndLeaseEvidence()
    {
        // Build the complete valid fixture before measuring the process. The
        // existing deterministic PNG writer compresses a constant RGBA image,
        // so the source remains small while WIC still sees all source pixels.
        const int sourceWidth = 6_324;
        const int sourceHeight = 6_324;
        byte[] source = DisplaySizedRasterDecodeTests.CreatePng(sourceWidth, sourceHeight);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview, budget.Reason);
        Assert.Equal("PNG", budget.Format);
        Assert.Equal(sourceWidth, budget.Width);
        Assert.Equal(sourceHeight, budget.Height);
        Assert.Equal(39_992_976L, budget.TotalPixels);
        Assert.Equal(budget.TotalPixels * 4, budget.DecodedBytes);
        Assert.InRange(source.Length, 1, RasterImageResourceBudget.MaxInputBytes);

        await RunImageBoxQualificationAsync(source, "image/png", budget);
    }

    [Trait("Requires", "WicWebpDecoder")]
    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task NearSeventeenMegapixelWebp_ReportsCappedImageBoxDecodeAndLeaseEvidence()
    {
        byte[] source = ReadPinnedOversizedWebpFixture();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.False(budget.Accepted);
        Assert.True(budget.CanRenderStaticPreview, budget.Reason);
        Assert.Equal("WEBP", budget.Format);
        Assert.Equal(2_048, budget.Width);
        Assert.Equal(8_193, budget.Height);
        Assert.Equal(16_779_264L, budget.TotalPixels);
        Assert.Equal(budget.TotalPixels * 4, budget.DecodedBytes);

        await RunImageBoxQualificationAsync(source, "image/webp", budget);
    }

    private async Task RunImageBoxQualificationAsync(
        byte[] source,
        string contentType,
        RasterImageBudgetResult budget)
    {
        RasterImageSizingRequest sizingRequest = CreateImageBoxSizingRequest();
        (int expectedWidth, int expectedHeight) = sizingRequest.GetSize(budget.Width, budget.Height);
        Assert.InRange(expectedWidth, 1, DisplayWidth);
        Assert.InRange(expectedWidth, 1, budget.Width);
        Assert.InRange(expectedHeight, 1, budget.Height);
        Assert.InRange((long)expectedWidth * expectedHeight, 1, sizingRequest.MaximumPixels);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        AdmissionState before = CaptureAdmissionState();
        output.WriteLine(
            $"ImageBox WIC qualification fixture: format={budget.Format}; contentType={contentType}; " +
            $"source={budget.Width}x{budget.Height} ({budget.TotalPixels:N0} pixels, " +
            $"{budget.DecodedBytes:N0} nominal decoded bytes); compressedSource={source.Length:N0} bytes; " +
            $"policy=existing static-preview ceiling {RasterImageResourceBudget.MaxDownsampleSourcePixels:N0} pixels; " +
            $"ImageBox display={DisplayWidth}px at scale 1; plannedOutput={expectedWidth}x{expectedHeight}. " +
            "The PNG source is generated in memory and the pinned WebP is embedded; this test performs no network I/O.");

#if MARKDOWN_LAYOUT_DIAGNOSTICS
        RasterNativeWicDiagnosticSnapshot nativeBeforeSerial = RasterNativeWicDiagnostics.GetSnapshot();
#endif
        (ProbeResult serial, MemoryReport serialMemory) = await MeasurePrivateBytesAsync(
            $"serial/ImageBox-{budget.Format}-{budget.TotalPixels:N0}-pixels",
            token => LoadOneAsync(source, contentType, token));
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        AssertAndReportNativeDecodeCount("serial", nativeBeforeSerial, expected: 1);
#endif
        WriteMemoryReport(serialMemory);
        WriteProbeReport(serial, budget, expectedWidth, expectedHeight);
        AssertPlannedOutput(serial, expectedWidth, expectedHeight);
        await AssertAdmissionsDrainedToAsync(before);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
        RasterNativeWicDiagnosticSnapshot nativeBeforePair = RasterNativeWicDiagnostics.GetSnapshot();
#endif
        (PairResult pair, MemoryReport pairMemory) = await MeasurePrivateBytesAsync(
            $"two-request-admission-barrier/ImageBox-{budget.Format}-{budget.TotalPixels:N0}-pixels",
            token => LoadTwoAsync(source, contentType, before.Preparation, token));
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        AssertAndReportNativeDecodeCount("two-way", nativeBeforePair, expected: 2);
#endif
        WriteMemoryReport(pairMemory);
        Assert.Equal(2, pair.Outputs.Length);
        foreach (ProbeResult result in pair.Outputs)
        {
            WriteProbeReport(result, budget, expectedWidth, expectedHeight);
            AssertPlannedOutput(result, expectedWidth, expectedHeight);
        }

        // Static-preview requests are admitted exclusively because a scaled
        // WIC output does not prove bounded codec intermediates. Both actual
        // ImageBoxes are therefore queued behind a test-owned exclusive lease
        // before it is released. This proves concurrent demand deterministically
        // without treating the barrier lease as decoder work or native overlap.
        output.WriteLine(
            $"ImageBox queued-demand barrier: queuedImageDemands={pair.QueuedImageDemandsAtBarrier}; " +
            $"pendingRequestsBeforeRelease={pair.PendingRequestsBeforeBarrierRelease}; " +
            $"baselinePendingRequests={before.Preparation.PendingRequests}. " +
            "The test-owned exclusive lease is admission-barrier evidence only, not a native decode or concurrency measurement.");
        Assert.Equal(2, pair.QueuedImageDemandsAtBarrier);
        Assert.Equal(
            before.Preparation.PendingRequests + 2,
            pair.PendingRequestsBeforeBarrierRelease);
        await AssertAdmissionsDrainedToAsync(before);

        output.WriteLine(
            "Interpretation: elapsed time and 2 ms sampled private-byte peaks are diagnostic observations only; " +
            "sampling can miss short-lived allocations, and process private bytes include the test host/runtime. " +
            "Post-retirement and post-test-only-GC samples help distinguish retained from collectable process state, " +
            "but are not attributed decoder measurements or a native peak bound. " +
            "Admission values are logical scheduling/output leases, not measured WIC/native memory.");
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private void AssertAndReportNativeDecodeCount(
        string label, RasterNativeWicDiagnosticSnapshot before, long expected)
    {
        // This collection excludes other tests while measuring process state.
        // Count the actual ImageBox branch; capped output alone cannot establish
        // whether the owned native graph or the projected WinRT decoder ran.
        RasterNativeWicDiagnosticSnapshot after = RasterNativeWicDiagnostics.GetSnapshot();
        long started = after.NativeStarted - before.NativeStarted;
        long completed = after.PixelLeaseCompleted - before.PixelLeaseCompleted;
        output.WriteLine($"ImageBox native WIC branch {label}: nativeStarted={started}; pixelLeaseCompleted={completed}.");
        Assert.Equal(expected, started);
        Assert.Equal(expected, completed);
    }
#endif

    private static byte[] ReadPinnedOversizedWebpFixture()
    {
        Assembly assembly = typeof(NearMaximumRasterWicDecodeQualificationTests).Assembly;
        string resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("Fixtures.oversized-static.webp", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{resourceName}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] source = buffer.ToArray();
        Assert.Equal(PinnedWebpSha256, Convert.ToHexString(SHA256.HashData(source)));
        return source;
    }

    private static RasterImageSizingRequest CreateImageBoxSizingRequest()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownPerformanceOptions options = session.Options;
        long maximumOutputPixels = System.Math.Min(
            RasterImageResourceBudget.MaxPixelsPerFrame,
            options.MaxRasterOutputPixels);
        if (options.ReleaseOffscreenImageResources)
        {
            maximumOutputPixels = System.Math.Min(
                maximumOutputPixels,
                BitmapOutputAdmission.Shared.MaximumSingleOutputBytes / 4);
        }

        return new RasterImageSizingRequest(
            DisplayWidth,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: maximumOutputPixels);
    }

    private static async Task<ProbeResult> LoadOneAsync(
        byte[] source,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var image = CreateImage(source, contentType, session, "serial");
        try
        {
            image.Measure(DisplayWidth);
            await WaitForLoadAsync(image, cancellationToken).ConfigureAwait(false);
            return CaptureResult("serial", image);
        }
        finally
        {
            image.Dispose();
            await WaitForImageDrainAsync(image).ConfigureAwait(false);
        }
    }

    private static async Task<PairResult> LoadTwoAsync(
        byte[] source,
        string contentType,
        PreparationSnapshot baseline,
        CancellationToken cancellationToken)
    {
        using var firstSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using var secondSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var first = CreateImage(source, contentType, firstSession, "pair-first");
        var second = CreateImage(source, contentType, secondSession, "pair-second");
        IDisposable? startBarrier = null;
        try
        {
            // The guard consumes the shared admission capacity, not a decoder
            // slot owned by either ImageBox. It makes the two real requests
            // observable in the wait queues before either can start.
            startBarrier = await MarkdownImagePreparationAdmission.Shared.EnterAsync(
                    new object(), estimatedBytes: 0, exclusive: true, cancellationToken: cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);

            first.Measure(DisplayWidth);
            Task firstLoad = StartLoadAsync(first, cancellationToken);

            MarkdownImagePreparationAdmission preparation = MarkdownImagePreparationAdmission.Shared;
            await WaitUntilAsync(
                () => preparation.PendingRequests >= baseline.PendingRequests + 1,
                cancellationToken).ConfigureAwait(false);

            second.Measure(DisplayWidth);
            Task secondLoad = StartLoadAsync(second, cancellationToken);
            await WaitUntilAsync(
                () => preparation.PendingRequests >= baseline.PendingRequests + 2,
                cancellationToken).ConfigureAwait(false);
            int pendingRequestsBeforeBarrierRelease = preparation.PendingRequests;
            int queuedImageDemandsAtBarrier = pendingRequestsBeforeBarrierRelease - baseline.PendingRequests;

            startBarrier.Dispose();
            startBarrier = null;

            await Task.WhenAll(firstLoad, secondLoad).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PairResult(
                [CaptureResult("two-way/first", first), CaptureResult("two-way/second", second)],
                queuedImageDemandsAtBarrier,
                pendingRequestsBeforeBarrierRelease);
        }
        finally
        {
            startBarrier?.Dispose();
            first.Dispose();
            second.Dispose();
            await Task.WhenAll(WaitForImageDrainAsync(first), WaitForImageDrainAsync(second))
                .ConfigureAwait(false);
        }
    }

    private static ImageBox CreateImage(
        byte[] source,
        string contentType,
        MarkdownPerformanceSession session,
        string id)
    {
        var resolver = new UnkeyedResolver(source, contentType);
        return new ImageBox(
            DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
            $"https://qualification.example/{id}.{GetExtension(contentType)}",
            string.Empty);
    }

    private static string GetExtension(string contentType) => contentType switch
    {
        "image/png" => "png",
        "image/webp" => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };

    private static Task StartLoadAsync(ImageBox image, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        image.LoadCompleted += (_, args) =>
        {
            if (!args.IsBitmapOutputRecoverySignal)
                completed.TrySetResult();
        };
        image.EnsureLoading();
        return completed.Task.WaitAsync(cancellationToken);
    }

    private static async Task WaitForLoadAsync(ImageBox image, CancellationToken cancellationToken)
    {
        await StartLoadAsync(image, cancellationToken).ConfigureAwait(false);
        Assert.NotNull(image.Bitmap);
    }

    private static ProbeResult CaptureResult(string name, ImageBox image)
    {
        var pixels = image.Bitmap?.SizeInPixels ?? throw new InvalidOperationException(
            $"The {name} large raster did not publish a bitmap.");
        return new ProbeResult(name, checked((int)pixels.Width), checked((int)pixels.Height));
    }

    private static void AssertPlannedOutput(ProbeResult result, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(expectedWidth, result.OutputWidth);
        Assert.Equal(expectedHeight, result.OutputHeight);
    }

    private static async Task WaitForImageDrainAsync(ImageBox image)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(
            () => image.ImageLifetimeDisposedForTests && image.ActiveImageWorkCountForTests == 0,
            timeout.Token).ConfigureAwait(false);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
    }

    private static AdmissionState CaptureAdmissionState() => new(
        CapturePreparationSnapshot(),
        BitmapOutputAdmission.Shared.GetSnapshot(),
        SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests,
        SharedCanvasBitmapCache.RetainedBytesForTests);

    private static PreparationSnapshot CapturePreparationSnapshot()
    {
        MarkdownImagePreparationAdmission preparation = MarkdownImagePreparationAdmission.Shared;
        return new PreparationSnapshot(
            preparation.ActiveBytes,
            preparation.ActiveCount,
            preparation.PendingRequests);
    }

    private static async Task AssertAdmissionsDrainedToAsync(AdmissionState before)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() =>
        {
            PreparationSnapshot preparation = CapturePreparationSnapshot();
            BitmapOutputAdmission.Snapshot outputs = BitmapOutputAdmission.Shared.GetSnapshot();
            return preparation.ActiveCount == before.Preparation.ActiveCount &&
                preparation.ActiveBytes == before.Preparation.ActiveBytes &&
                preparation.PendingRequests == before.Preparation.PendingRequests &&
                outputs.ActiveReservationCount == before.Outputs.ActiveReservationCount &&
                outputs.ActiveBytes == before.Outputs.ActiveBytes &&
                SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests == before.LiveBitmapWeightBytes &&
                SharedCanvasBitmapCache.RetainedBytesForTests == before.RetainedBitmapBytes;
        }, timeout.Token).ConfigureAwait(false);

        PreparationSnapshot finalPreparation = CapturePreparationSnapshot();
        BitmapOutputAdmission.Snapshot finalOutputs = BitmapOutputAdmission.Shared.GetSnapshot();
        Assert.Equal(before.Preparation.ActiveBytes, finalPreparation.ActiveBytes);
        Assert.Equal(before.Preparation.ActiveCount, finalPreparation.ActiveCount);
        Assert.Equal(before.Preparation.PendingRequests, finalPreparation.PendingRequests);
        Assert.Equal(before.Outputs.ActiveBytes, finalOutputs.ActiveBytes);
        Assert.Equal(before.Outputs.ActiveReservationCount, finalOutputs.ActiveReservationCount);
        Assert.Equal(before.LiveBitmapWeightBytes, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        Assert.Equal(before.RetainedBitmapBytes, SharedCanvasBitmapCache.RetainedBytesForTests);
    }

    private static async Task<(T Result, MemoryReport Report)> MeasurePrivateBytesAsync<T>(
        string label,
        Func<CancellationToken, Task<T>> workload)
    {
        using Process process = Process.GetCurrentProcess();
        // Timer and workload-completion samples share this Process observer;
        // Refresh/read must be one serialized operation on its mutable cache.
        object sampleGate = new();
        process.Refresh();
        long baselinePrivateBytes = process.PrivateMemorySize64;
        long sampledPeakPrivateBytes = baselinePrivateBytes;
        int maximumPreparationActiveCount = MarkdownImagePreparationAdmission.Shared.ActiveCount;
        int maximumPendingRequests = MarkdownImagePreparationAdmission.Shared.PendingRequests;
        BitmapOutputAdmission.Snapshot initialOutputs = BitmapOutputAdmission.Shared.GetSnapshot();
        long maximumOutputActiveBytes = initialOutputs.ActiveBytes;
        int maximumOutputReservations = initialOutputs.ActiveReservationCount;
        long maximumLiveBitmapWeightBytes = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
        using var stopSampling = new CancellationTokenSource();
        using var workloadTimeout = new CancellationTokenSource(WorkloadTimeout);

        void Sample()
        {
            long currentBytes;
            lock (sampleGate)
            {
                process.Refresh();
                currentBytes = process.PrivateMemorySize64;
            }
            UpdateMaximum(ref sampledPeakPrivateBytes, currentBytes);
            UpdateMaximum(ref maximumPreparationActiveCount,
                MarkdownImagePreparationAdmission.Shared.ActiveCount);
            UpdateMaximum(ref maximumPendingRequests,
                MarkdownImagePreparationAdmission.Shared.PendingRequests);
            BitmapOutputAdmission.Snapshot outputs = BitmapOutputAdmission.Shared.GetSnapshot();
            UpdateMaximum(ref maximumOutputActiveBytes, outputs.ActiveBytes);
            UpdateMaximum(ref maximumOutputReservations, outputs.ActiveReservationCount);
            UpdateMaximum(ref maximumLiveBitmapWeightBytes,
                SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        }

        async Task SampleUntilStoppedAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SampleIntervalMilliseconds));
            try
            {
                while (await timer.WaitForNextTickAsync(stopSampling.Token).ConfigureAwait(false))
                    Sample();
            }
            catch (OperationCanceledException) when (stopSampling.IsCancellationRequested)
            {
            }
        }

        Task sampler = SampleUntilStoppedAsync();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            T result = await workload(workloadTimeout.Token).ConfigureAwait(false);
            Sample();
            stopSampling.Cancel();
            await sampler.ConfigureAwait(false);
            stopwatch.Stop();
            long privateBytesAfterLogicalRetirement = ReadCurrentPrivateBytes(process);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long privateBytesAfterTestOnlyGc = ReadCurrentPrivateBytes(process);
            return (result, new MemoryReport(
                label,
                baselinePrivateBytes,
                Interlocked.Read(ref sampledPeakPrivateBytes),
                stopwatch.Elapsed,
                Interlocked.CompareExchange(ref maximumPreparationActiveCount, 0, 0),
                Interlocked.CompareExchange(ref maximumPendingRequests, 0, 0),
                Interlocked.Read(ref maximumOutputActiveBytes),
                Interlocked.CompareExchange(ref maximumOutputReservations, 0, 0),
                Interlocked.Read(ref maximumLiveBitmapWeightBytes),
                SampleIntervalMilliseconds,
                privateBytesAfterLogicalRetirement,
                privateBytesAfterTestOnlyGc));
        }
        finally
        {
            stopSampling.Cancel();
            await sampler.ConfigureAwait(false);
        }
    }

    private static long ReadCurrentPrivateBytes(Process process)
    {
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long observed;
        do
        {
            observed = Interlocked.Read(ref target);
            if (value <= observed)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, observed) != observed);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (value <= observed)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, observed) != observed);
    }

    private void WriteMemoryReport(MemoryReport report)
    {
        bool includesTestBarrier = report.Label.StartsWith(
            "two-request-admission-barrier/", StringComparison.Ordinal);
        string activeCountLabel = includesTestBarrier
            ? "maxPreparationActiveIncludingTestBarrier"
            : "maxPreparationActive";
        string pendingCountLabel = includesTestBarrier
            ? "maxPendingPreparationIncludingQueuedDemandBarrier"
            : "maxPendingPreparation";
        output.WriteLine(
            $"WIC memory {report.Label}: baselinePrivateBytes={report.BaselinePrivateBytes:N0}; " +
            $"sampledPeakPrivateBytes={report.SampledPeakPrivateBytes:N0}; " +
            $"sampledGrowthBytes={System.Math.Max(0, report.SampledPeakPrivateBytes - report.BaselinePrivateBytes):N0}; " +
            $"privateBytesAfterLogicalRetirement={report.PrivateBytesAfterLogicalRetirement:N0}; " +
            $"privateBytesAfterTestOnlyGc={report.PrivateBytesAfterTestOnlyGc:N0}; " +
            $"sampleInterval={report.SampleIntervalMilliseconds} ms; " +
            $"elapsedLoadAndLogicalRetirement={report.ElapsedLoadAndLogicalRetirement.TotalMilliseconds:F1} ms; " +
            $"{activeCountLabel}={report.MaximumPreparationActiveCount}; " +
            $"{pendingCountLabel}={report.MaximumPendingRequests}; " +
            $"maxOutputActiveBytes={report.MaximumOutputActiveBytes:N0}; " +
            $"maxOutputReservations={report.MaximumOutputReservations}; " +
            $"maxLiveBitmapWeightBytes={report.MaximumLiveBitmapWeightBytes:N0}.");
    }

    private void WriteProbeReport(
        ProbeResult result,
        RasterImageBudgetResult budget,
        int expectedWidth,
        int expectedHeight) => output.WriteLine(
        $"ImageBox output {result.Name}: source={budget.Width}x{budget.Height} " +
        $"({budget.TotalPixels:N0} pixels); displayWidth={DisplayWidth}px@1x; " +
        $"RasterImageSizingRequest={expectedWidth}x{expectedHeight}; decoded bitmap={result.OutputWidth}x{result.OutputHeight}.");

    private sealed class UnkeyedResolver(byte[] bytes, string contentType) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(bytes, contentType)));
    }

    private sealed record ProbeResult(string Name, int OutputWidth, int OutputHeight);

    private sealed record PairResult(
        ProbeResult[] Outputs,
        int QueuedImageDemandsAtBarrier,
        int PendingRequestsBeforeBarrierRelease);

    private sealed record MemoryReport(
        string Label,
        long BaselinePrivateBytes,
        long SampledPeakPrivateBytes,
        TimeSpan ElapsedLoadAndLogicalRetirement,
        int MaximumPreparationActiveCount,
        int MaximumPendingRequests,
        long MaximumOutputActiveBytes,
        int MaximumOutputReservations,
        long MaximumLiveBitmapWeightBytes,
        int SampleIntervalMilliseconds,
        long PrivateBytesAfterLogicalRetirement,
        long PrivateBytesAfterTestOnlyGc);

    private sealed record AdmissionState(
        PreparationSnapshot Preparation,
        BitmapOutputAdmission.Snapshot Outputs,
        long LiveBitmapWeightBytes,
        long RetainedBitmapBytes);

    private sealed record PreparationSnapshot(long ActiveBytes, int ActiveCount, int PendingRequests);
}
