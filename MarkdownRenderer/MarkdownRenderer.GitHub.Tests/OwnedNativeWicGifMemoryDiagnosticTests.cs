using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Separates native GIF decode/retire cost from Win2D bitmap allocation and
/// fractional composition. Process-memory observations are diagnostic only;
/// they are not hard bounds on WIC scratch, private bytes, or device memory.
/// Run each fact in its own test process when comparing allocation profiles.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class OwnedNativeWicGifMemoryDiagnosticTests(ITestOutputHelper output)
{
    private const int CycleCount = 100;
    private const int WarmupCount = 11;
    private const int FixtureCount = 11;
    private const int InterleaveStride = 7;
    private const double DisplayWidth = 8;
    private const long MaximumOutputPixels = 512;

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task DirectNativeGifDecodeAndLeaseRetirement_ReportsProcessAllocationObservations()
    {
        GifCase[] cases = [];
        MarkdownImagePreparationAdmission decodeAdmission = MarkdownImagePreparationAdmission.Shared;
        BitmapOutputAdmission outputAdmission = BitmapOutputAdmission.Shared;
        NativeResourceRetirement retirement = NativeResourceRetirement.Shared;
        await WaitForSharedQuiescenceAsync(decodeAdmission, retirement);
        BitmapOutputAdmission.Snapshot outputBaseline = outputAdmission.GetSnapshot();
        long retainedBitmapBaseline = SharedCanvasBitmapCache.RetainedBytesForTests;
        long livePixelBaseline = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
        long cacheFailuresBaseline = SharedCanvasBitmapCache.CacheDisposalFailureCountForTests;
        RetirementSnapshot retirementBaseline = ReadRetirement(retirement);

        try
        {
            cases = await CreateCasesAsync(device: null);
            await WaitForAdmissionIdleAsync(decodeAdmission);
            Assert.Equal(0, decodeAdmission.ActiveCount);
            Assert.Equal(0, decodeAdmission.ActiveBytes);
            Assert.Equal(0, decodeAdmission.PendingRequests);

            // Warm every native graph exactly once before the sampled interval.
            foreach (GifCase gifCase in cases)
                await DecodeAndAssertAsync(gifCase, decodeAdmission);

            await WaitForAdmissionIdleAsync(decodeAdmission);
            AssertGlobalLedgersUnchanged(
                decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);

            using Process process = Process.GetCurrentProcess();
            MemoryPoint baseline = Capture(process);
            MemoryPoint peak = baseline;
            var rows = new StringBuilder(CycleCount * 100);
            rows.AppendLine("cycle,fixture,privateBytesAfterRetirement,handlesAfterRetirement,totalAllocatedBytes,managedHeapBytes,gen0,gen1,gen2");

            for (int cycle = 0; cycle < CycleCount; cycle++)
            {
                GifCase gifCase = cases[(cycle * InterleaveStride) % cases.Length];
                await DecodeAndAssertAsync(gifCase, decodeAdmission);
                AssertGlobalLedgersUnchanged(
                    decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                    livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);

                MemoryPoint point = Capture(process);
                peak = MemoryPoint.Max(peak, point);
                rows.Append(cycle + 1).Append(',')
                    .Append(gifCase.Name).Append(',')
                    .Append(point.PrivateBytes).Append(',')
                    .Append(point.HandleCount).Append(',')
                    .Append(point.TotalAllocatedBytes).Append(',')
                    .Append(point.ManagedHeapBytes).Append(',')
                    .Append(point.Gen0).Append(',')
                    .Append(point.Gen1).Append(',')
                    .Append(point.Gen2).AppendLine();
            }

            await WaitForAdmissionIdleAsync(decodeAdmission);
            AssertGlobalLedgersUnchanged(
                decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);
            MemoryPoint final = Capture(process);
            output.WriteLine(
                $"Direct owned-native GIF decode diagnostic: warmups={WarmupCount}; cycles={CycleCount}; " +
                $"variants={cases.Length}; privateBytesBaseline={baseline.PrivateBytes}; " +
                $"privateBytesPeakAfterRetirement={peak.PrivateBytes}; privateBytesFinal={final.PrivateBytes}; " +
                $"handlesBaseline={baseline.HandleCount}; handlesPeakAfterRetirement={peak.HandleCount}; " +
                $"handlesFinal={final.HandleCount}; totalAllocatedBaseline={baseline.TotalAllocatedBytes}; " +
                $"totalAllocatedFinal={final.TotalAllocatedBytes}; managedHeapBaseline={baseline.ManagedHeapBytes}; " +
                $"managedHeapFinal={final.ManagedHeapBytes}; " +
                $"collectionsDelta={final.Gen0 - baseline.Gen0}/{final.Gen1 - baseline.Gen1}/{final.Gen2 - baseline.Gen2}. " +
                "No forced GC was used. Samples are process-wide observations after lease disposal, " +
                "not a hard bound or release-qualified memory claim.");
            output.WriteLine(rows.ToString());
        }
        finally
        {
            await WaitForAdmissionIdleAsync(decodeAdmission);
            AssertGlobalLedgersUnchanged(
                decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);
            ClearCases(cases);
        }
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task WarpBitmapCreationAndFractionalCompositionAlone_ReportsProcessAllocationObservations()
    {
        GifCase[] cases = [];
        MarkdownImagePreparationAdmission decodeAdmission = MarkdownImagePreparationAdmission.Shared;
        BitmapOutputAdmission outputAdmission = BitmapOutputAdmission.Shared;
        NativeResourceRetirement retirement = NativeResourceRetirement.Shared;
        await WaitForSharedQuiescenceAsync(decodeAdmission, retirement);
        BitmapOutputAdmission.Snapshot outputBaseline = outputAdmission.GetSnapshot();
        long retainedBitmapBaseline = SharedCanvasBitmapCache.RetainedBytesForTests;
        long livePixelBaseline = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
        long cacheFailuresBaseline = SharedCanvasBitmapCache.CacheDisposalFailureCountForTests;
        RetirementSnapshot retirementBaseline = ReadRetirement(retirement);

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer,
            "The GPU-allocation control must use WARP and must not silently fall back to hardware.");

        try
        {
            // WinRT frame decode and expected Win2D output are prepared before
            // the measured window. No decoder, ImageBox, session, or cache is
            // involved in the sampled cycles.
            cases = await CreateCasesAsync(device);
            await WaitForAdmissionIdleAsync(decodeAdmission);
            Assert.Equal(0, decodeAdmission.ActiveCount);
            Assert.Equal(0, decodeAdmission.ActiveBytes);
            Assert.Equal(0, decodeAdmission.PendingRequests);

            foreach (GifCase gifCase in cases)
                RenderWarpCycleAndAssert(device, gifCase);

            AssertGlobalLedgersUnchanged(
                decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);

            using Process process = Process.GetCurrentProcess();
            MemoryPoint baseline = Capture(process);
            MemoryPoint peak = baseline;
            var rows = new StringBuilder(CycleCount * 100);
            rows.AppendLine("cycle,fixture,privateBytesAfterRetirement,handlesAfterRetirement,totalAllocatedBytes,managedHeapBytes,gen0,gen1,gen2");

            for (int cycle = 0; cycle < CycleCount; cycle++)
            {
                GifCase gifCase = cases[(cycle * InterleaveStride) % cases.Length];
                RenderWarpCycleAndAssert(device, gifCase);
                AssertGlobalLedgersUnchanged(
                    decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                    livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);

                MemoryPoint point = Capture(process);
                peak = MemoryPoint.Max(peak, point);
                rows.Append(cycle + 1).Append(',')
                    .Append(gifCase.Name).Append(',')
                    .Append(point.PrivateBytes).Append(',')
                    .Append(point.HandleCount).Append(',')
                    .Append(point.TotalAllocatedBytes).Append(',')
                    .Append(point.ManagedHeapBytes).Append(',')
                    .Append(point.Gen0).Append(',')
                    .Append(point.Gen1).Append(',')
                    .Append(point.Gen2).AppendLine();
            }

            await WaitForAdmissionIdleAsync(decodeAdmission);
            AssertGlobalLedgersUnchanged(
                decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);
            MemoryPoint final = Capture(process);
            output.WriteLine(
                $"Win2D-only WARP GIF allocation diagnostic: warmups={WarmupCount}; cycles={CycleCount}; " +
                $"variants={cases.Length}; privateBytesBaseline={baseline.PrivateBytes}; " +
                $"privateBytesPeakAfterRetirement={peak.PrivateBytes}; privateBytesFinal={final.PrivateBytes}; " +
                $"handlesBaseline={baseline.HandleCount}; handlesPeakAfterRetirement={peak.HandleCount}; " +
                $"handlesFinal={final.HandleCount}; totalAllocatedBaseline={baseline.TotalAllocatedBytes}; " +
                $"totalAllocatedFinal={final.TotalAllocatedBytes}; managedHeapBaseline={baseline.ManagedHeapBytes}; " +
                $"managedHeapFinal={final.ManagedHeapBytes}; " +
                $"collectionsDelta={final.Gen0 - baseline.Gen0}/{final.Gen1 - baseline.Gen1}/{final.Gen2 - baseline.Gen2}. " +
                "This control uses precomputed WinRT frame bytes and expected output; it performs no native WIC " +
                "decode, ImageBox work, or bitmap-cache access. No forced GC was used, and these process-wide " +
                "observations are not a hard bound or release-qualified memory claim.");
            output.WriteLine(rows.ToString());
        }
        finally
        {
            AssertGlobalLedgersUnchanged(
                decodeAdmission, outputAdmission, outputBaseline, retainedBitmapBaseline,
                livePixelBaseline, cacheFailuresBaseline, retirement, retirementBaseline);
            ClearCases(cases);
        }
    }

    private static async Task<GifCase[]> CreateCasesAsync(CanvasDevice? device)
    {
        List<OwnedNativeWicGifFixtureFactory.Fixture> fixtures =
            OwnedNativeWicGifFixtureFactory.CreateCoreMatrix()
                .Append(OwnedNativeWicGifFixtureFactory.CreateOverFrameLimitStaticPreview())
                .ToList();
        Assert.Equal(10, fixtures.Count);

        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        try
        {
            fixtures.Add(OwnedNativeWicGifFixtureFactory.EmbedIccProfile(fixtures[0], profile) with
            {
                Name = "gct-full-frame-icc",
            });
        }
        finally
        {
            Array.Clear(profile);
        }
        Assert.Equal(FixtureCount, fixtures.Count);

        var result = new List<GifCase>(fixtures.Count);
        try
        {
            var sizing = new RasterImageSizingRequest(
                DisplayWidth, rasterizationScale: 1, requestedWidth: null, requestedHeight: null,
                maximumPixels: MaximumOutputPixels);
            foreach (OwnedNativeWicGifFixtureFactory.Fixture fixture in fixtures)
            {
                RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Bytes);
                Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
                    $"{fixture.Name}: source is not admitted for a static first-frame preview: {budget.Reason}");
                Assert.Equal("GIF", budget.Format);
                Assert.Equal(fixture.CanvasWidth, budget.Width);
                Assert.Equal(fixture.CanvasHeight, budget.Height);
                Assert.Equal(fixture.FrameCount, budget.FrameCount);
                Assert.Equal((fixture.FirstFrame.Left, fixture.FirstFrame.Top,
                        fixture.FirstFrame.Width, fixture.FirstFrame.Height),
                    (budget.FirstFrame!.Value.Left, budget.FirstFrame.Value.Top,
                        budget.FirstFrame.Value.Width, budget.FirstFrame.Value.Height));
                Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(
                    fixture.Bytes, budget, sizing, out RasterGifFirstFrameDecodePlan plan), fixture.Name);

                byte[] framePixels = await DecodeWinRtFrameZeroBytesAsync(fixture.Bytes, plan);
                byte[]? canvasPixels = device is null ? null : RenderWarp(device, plan, framePixels);
                result.Add(new GifCase(fixture, budget, plan, framePixels, canvasPixels));
            }

            Assert.Equal(FixtureCount, result.Count);
            return result.ToArray();
        }
        catch
        {
            ClearCases(result);
            foreach (OwnedNativeWicGifFixtureFactory.Fixture fixture in fixtures)
                ClearFixture(fixture);
            throw;
        }
    }

    private static async Task DecodeAndAssertAsync(
        GifCase gifCase,
        MarkdownImagePreparationAdmission admission)
    {
        IDisposable reservation = await admission.EnterAsync(
            new object(), gifCase.EstimatedAdmissionBytes, exclusive: true, CancellationToken.None);
        var trackedCredit = new CountingCredit(reservation);
        RasterDecodedPixelLease? decoded = null;
        bool decodeWasScheduled = false;
        try
        {
            Task<RasterDecodedPixelLease> decodeTask = Task.Run(
                () => RasterNativeWicDecoder.DecodeGifFirstFrame(
                    gifCase.Source, gifCase.Plan, trackedCredit, CancellationToken.None),
                CancellationToken.None);
            decodeWasScheduled = true;

            decoded = await decodeTask.ConfigureAwait(false);
            Assert.Equal(gifCase.Plan.DecodeWidth, decoded.Width);
            Assert.Equal(gifCase.Plan.DecodeHeight, decoded.Height);
            Assert.Equal(gifCase.FramePixels.Length, decoded.Pixels.Length);
            Assert.True(gifCase.FramePixels.AsSpan().SequenceEqual(decoded.Pixels),
                $"{gifCase.Name}: native frame-zero PBGRA bytes differ from the WinRT oracle.");
            Assert.Equal(0, trackedCredit.DisposeCount);
            Assert.Equal(1, admission.ActiveCount);
            Assert.Equal(admission.CapacityBytes, admission.ActiveBytes);

            ReadOnlyMemory<byte> borrowedPixels = decoded.Pixels;
            decoded.Dispose();
            decoded = null;
            Assert.Equal(1, trackedCredit.DisposeCount);
            Assert.Equal(0, admission.ActiveCount);
            Assert.Equal(0, admission.ActiveBytes);
            foreach (byte value in borrowedPixels.Span)
                Assert.Equal((byte)0, value);
        }
        finally
        {
            decoded?.Dispose();
            if (!decodeWasScheduled)
                trackedCredit.Dispose();
        }

        await WaitForAdmissionIdleAsync(admission);
    }

    private static void RenderWarpCycleAndAssert(CanvasDevice device, GifCase gifCase)
    {
        byte[] expected = gifCase.CanvasPixels
            ?? throw new InvalidOperationException($"{gifCase.Name} has no WARP canvas oracle.");
        byte[] actual = RenderWarp(device, gifCase.Plan, gifCase.FramePixels);
        try
        {
            Assert.Equal(expected, actual);
        }
        finally
        {
            Array.Clear(actual);
        }
    }

    private static byte[] RenderWarp(
        CanvasDevice device,
        RasterGifFirstFrameDecodePlan plan,
        byte[] framePixels)
    {
        using CanvasBitmap frame = CanvasBitmap.CreateFromBytes(
            device,
            framePixels,
            plan.DecodeWidth,
            plan.DecodeHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96,
            CanvasAlphaMode.Premultiplied);

        if (!plan.RequiresComposition)
            return frame.GetPixelBytes();

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
                frame,
                destination,
                new Rect(0, 0, frame.Size.Width, frame.Size.Height),
                1f,
                CanvasImageInterpolation.Linear);
        }
        return target.GetPixelBytes();
    }

    private static async Task<byte[]> DecodeWinRtFrameZeroBytesAsync(
        byte[] source,
        RasterGifFirstFrameDecodePlan plan)
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

        Assert.Equal((uint)plan.FrameCount, decoder.FrameCount);
        Assert.Equal((uint)plan.FrameWidth, decoder.PixelWidth);
        Assert.Equal((uint)plan.FrameHeight, decoder.PixelHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = checked((uint)plan.DecodeWidth),
            ScaledHeight = checked((uint)plan.DecodeHeight),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        IAsyncOperation<SoftwareBitmap> bitmapOperation = decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        SoftwareBitmap? bitmap = null;
        try
        {
            bitmap = await bitmapOperation;
            Assert.Equal(plan.DecodeWidth, bitmap.PixelWidth);
            Assert.Equal(plan.DecodeHeight, bitmap.PixelHeight);
            var buffer = new Windows.Storage.Streams.Buffer(
                checked((uint)(plan.DecodeWidth * plan.DecodeHeight * 4)))
            {
                Length = checked((uint)(plan.DecodeWidth * plan.DecodeHeight * 4)),
            };
            bitmap.CopyToBuffer(buffer);
            return buffer.ToArray();
        }
        finally
        {
            try
            {
                bitmapOperation.Close();
            }
            finally
            {
                bitmap?.Dispose();
            }
        }
    }

    private static void AssertGlobalLedgersUnchanged(
        MarkdownImagePreparationAdmission decodeAdmission,
        BitmapOutputAdmission outputAdmission,
        BitmapOutputAdmission.Snapshot outputBaseline,
        long retainedBitmapBaseline,
        long livePixelBaseline,
        long cacheFailuresBaseline,
        NativeResourceRetirement retirement,
        RetirementSnapshot retirementBaseline)
    {
        Assert.Equal(0, decodeAdmission.ActiveCount);
        Assert.Equal(0, decodeAdmission.ActiveBytes);
        Assert.Equal(0, decodeAdmission.PendingRequests);

        BitmapOutputAdmission.Snapshot output = outputAdmission.GetSnapshot();
        Assert.Equal(outputBaseline.ChargedBytes, output.ChargedBytes);
        Assert.Equal(outputBaseline.ActiveBytes, output.ActiveBytes);
        Assert.Equal(outputBaseline.UncertainBytes, output.UncertainBytes);
        Assert.Equal(outputBaseline.ActiveReservationCount, output.ActiveReservationCount);
        Assert.Equal(outputBaseline.UncertainReservationCount, output.UncertainReservationCount);
        Assert.Equal(retainedBitmapBaseline, SharedCanvasBitmapCache.RetainedBytesForTests);
        Assert.Equal(livePixelBaseline, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        Assert.Equal(cacheFailuresBaseline, SharedCanvasBitmapCache.CacheDisposalFailureCountForTests);
        Assert.Equal(retirementBaseline, ReadRetirement(retirement));
    }

    private static RetirementSnapshot ReadRetirement(NativeResourceRetirement retirement) => new(
        retirement.PendingRecordCount,
        retirement.PendingResourceCount,
        retirement.QuarantinedResourceCount);

    private static async Task WaitForAdmissionIdleAsync(MarkdownImagePreparationAdmission admission)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (admission.ActiveCount != 0 || admission.ActiveBytes != 0 || admission.PendingRequests != 0)
            await Task.Delay(5, timeout.Token).ConfigureAwait(false);
    }

    private static async Task WaitForSharedQuiescenceAsync(
        MarkdownImagePreparationAdmission admission,
        NativeResourceRetirement retirement)
    {
        // The shared retirement clock may still be draining a retryable tail
        // published by an earlier suite test. Observe its public counters until
        // those unrelated records/resources and image admissions have drained;
        // never invoke DrainOnce or alter the shared retirement schedule here.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int consecutiveIdleSamples = 0;
        while (consecutiveIdleSamples < 2)
        {
            bool idle = retirement.PendingRecordCount == 0 && retirement.PendingResourceCount == 0 &&
                        admission.ActiveCount == 0 && admission.ActiveBytes == 0 && admission.PendingRequests == 0;
            consecutiveIdleSamples = idle ? consecutiveIdleSamples + 1 : 0;
            if (consecutiveIdleSamples == 2)
                return;
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    private static MemoryPoint Capture(Process process)
    {
        process.Refresh();
        return new MemoryPoint(
            process.PrivateMemorySize64,
            process.HandleCount,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    private static void ClearCases(IEnumerable<GifCase> cases)
    {
        foreach (GifCase gifCase in cases)
        {
            Array.Clear(gifCase.FramePixels);
            if (gifCase.CanvasPixels is not null)
                Array.Clear(gifCase.CanvasPixels);
            ClearFixture(gifCase.SourceFixture);
        }
    }

    private static void ClearFixture(OwnedNativeWicGifFixtureFactory.Fixture fixture)
    {
        Array.Clear(fixture.Bytes);
        if (fixture.GlobalPalette is { } globalPalette)
            Array.Clear(globalPalette);
        foreach (OwnedNativeWicGifFixtureFactory.Frame frame in fixture.Frames)
        {
            Array.Clear(frame.PixelIndices);
            if (frame.LocalPalette is { } localPalette)
                Array.Clear(localPalette);
        }
    }

    private sealed class GifCase(
        OwnedNativeWicGifFixtureFactory.Fixture fixture,
        RasterImageBudgetResult budget,
        RasterGifFirstFrameDecodePlan plan,
        byte[] framePixels,
        byte[]? canvasPixels)
    {
        internal OwnedNativeWicGifFixtureFactory.Fixture SourceFixture { get; } = fixture;
        internal string Name => SourceFixture.Name;
        internal byte[] Source => SourceFixture.Bytes;
        internal RasterImageBudgetResult Budget { get; } = budget;
        internal RasterGifFirstFrameDecodePlan Plan { get; } = plan;
        internal byte[] FramePixels { get; } = framePixels;
        internal byte[]? CanvasPixels { get; } = canvasPixels;
        internal long EstimatedAdmissionBytes => checked(
            Source.Length + Budget.DecodedBytes + Plan.FrameOutputPixels * 12);
    }

    private sealed class CountingCredit(IDisposable inner) : IDisposable
    {
        private IDisposable? _inner = inner;
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            Interlocked.Exchange(ref _inner, null)?.Dispose();
        }
    }

    private readonly record struct MemoryPoint(
        long PrivateBytes,
        int HandleCount,
        long TotalAllocatedBytes,
        long ManagedHeapBytes,
        int Gen0,
        int Gen1,
        int Gen2)
    {
        internal static MemoryPoint Max(MemoryPoint left, MemoryPoint right) => new(
            System.Math.Max(left.PrivateBytes, right.PrivateBytes),
            System.Math.Max(left.HandleCount, right.HandleCount),
            right.TotalAllocatedBytes,
            System.Math.Max(left.ManagedHeapBytes, right.ManagedHeapBytes),
            right.Gen0,
            right.Gen1,
            right.Gen2);
    }

    private readonly record struct RetirementSnapshot(
        int PendingRecords,
        long PendingResources,
        long QuarantinedResources);
}
