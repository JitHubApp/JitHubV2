#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using System.Security.Cryptography;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Diagnostic-only exact-pixel comparison for final WIC CopyPixels row bands.
/// The renderer's shipping decoder continues to use one full-surface copy.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class RasterNativeWicCopyBandEquivalenceTests(ITestOutputHelper output)
{
    private const int CopyBandRows = 3;
    private const int RequestedWidth = 19;
    private const int MaximumPixels = 300_000;
    private static readonly CancellationToken s_noCancellation = CancellationToken.None;

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public Task OversizedStaticWebp_FullCopy_ReportsDirectDecodePeak() =>
        ReportOversizedWebpPeakAsync(CopyMode.Full);

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public Task OversizedStaticWebp_Banded64_ReportsDirectDecodePeak() =>
        ReportOversizedWebpPeakAsync(CopyMode.Banded64);

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public Task OversizedStaticWebp_Banded16_ReportsDirectDecodePeak() =>
        ReportOversizedWebpPeakAsync(CopyMode.Banded16);

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task OversizedStaticWebp_FullAndBandedCopyMatchEveryPixel()
    {
        using Stream fixture = typeof(RasterNativeWicCopyBandEquivalenceTests).Assembly
            .GetManifestResourceStream("MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp")
            ?? throw new InvalidOperationException("The pinned oversized WebP fixture is missing.");
        using var backing = new MemoryStream();
        await fixture.CopyToAsync(backing);
        byte[] source = backing.ToArray();

        RasterImageDecodePlan plan = CreateStaticPlan(source, requestedWidth: 256, maximumPixels: MaximumPixels);
        Assert.Equal(256, plan.OutputWidth);
        Assert.Equal(1025, plan.OutputHeight);
        Assert.True(plan.OutputHeight > CopyBandRows * 2, "Fixture must require several row bands.");

        AssertStaticCopiesMatch(source, plan, copyBandRows: CopyBandRows);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task JpegExifOrientations_FullAndBandedCopyMatchEveryPixel(int orientation)
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(checked((ushort)orientation));
        AssertStaticCopiesMatch(source, CreateStaticPlan(source, RequestedWidth, MaximumPixels), copyBandRows: 3);
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task AlphaPng_FullAndBandedCopyMatchEveryPixel()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
            width: 37, height: 23, withAlphaEdges: true);
        AssertStaticCopiesMatch(source, CreateStaticPlan(source, RequestedWidth, MaximumPixels), copyBandRows: 3);
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task ProfiledAlphaPng_FullAndBandedCopyMatchEveryPixel()
    {
        byte[] source = await OwnedNativeWicIccFixtureFactory.CreatePatternedPngWithGamma18ProfileAsync(
            width: 37, height: 23, withAlphaEdges: true);
        AssertStaticCopiesMatch(source, CreateStaticPlan(source, RequestedWidth, MaximumPixels), copyBandRows: 3);
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public void GifFirstFrame_FullAndBandedCopyMatchEveryPixel()
    {
        OwnedNativeWicGifFixtureFactory.Fixture fixture = OwnedNativeWicGifFixtureFactory.CreateCoreMatrix()[0];
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Bytes);
        var sizing = CreateSizingRequest(requestedWidth: 3, maximumPixels: 64);
        Assert.True(RasterGifFirstFrameDecodePlan.TryCreate(fixture.Bytes, budget, sizing, out RasterGifFirstFrameDecodePlan plan));
        Assert.True(plan.OutputHeight > 1, "Fixture must require more than one output row.");
        AssertGifCopiesMatch(fixture.Bytes, plan, copyBandRows: 1);
    }

    private static RasterImageDecodePlan CreateStaticPlan(byte[] source, int requestedWidth, int maximumPixels)
    {
        Assert.True(RasterImageDecodePlan.TryCreate(
            source,
            RasterImageResourceBudget.Validate(source),
            CreateSizingRequest(requestedWidth, maximumPixels),
            out RasterImageDecodePlan plan));
        return plan;
    }

    private static RasterImageSizingRequest CreateSizingRequest(int requestedWidth, int maximumPixels) =>
        new(
            requestedWidth,
            rasterizationScale: 1d,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: maximumPixels);

    private async Task ReportOversizedWebpPeakAsync(CopyMode mode)
    {
        (byte[] source, RasterImageDecodePlan plan) = await LoadOversizedWebpAsync().ConfigureAwait(false);
        Assert.Equal(256, plan.OutputWidth);
        Assert.Equal(1025, plan.OutputHeight);
        string sourceHash = Convert.ToHexString(SHA256.HashData(source));

        var wallClock = new Stopwatch();
        var credit = new CountingDecodeCredit();
        LedgerSnapshot ledgerBefore = GetLedgerSnapshot();
        await using var sampler = new PrivateBytesSampler();
        sampler.Start();
        TimeSpan processCpuBefore = sampler.ReadProcessCpuTime();
        wallClock.Start();
        int outputWidth;
        int outputHeight;
        TimeSpan processCpu;
        string outputHash;
        using (RasterDecodedPixelLease lease = DecodeByMode(source, plan, credit, mode))
        {
            wallClock.Stop();
            processCpu = sampler.ReadProcessCpuTime() - processCpuBefore;
            outputWidth = lease.Width;
            outputHeight = lease.Height;
            outputHash = Convert.ToHexString(SHA256.HashData(lease.Pixels));
        }

        // Keep the sampler active through pixel-lease disposal so its terminal
        // private-byte sample includes native-graph retirement and CPU-buffer release.
        string report = await sampler.StopAndCaptureAsync().ConfigureAwait(false);
        output.WriteLine(
            $"Direct owned-WIC CopyPixels peak diagnostic: mode={mode}; requestedBandRows={GetBandRows(mode)}; " +
            $"dimensions={outputWidth}x{outputHeight}; sourceSha256={sourceHash}; outputSha256={outputHash}; " +
            $"processCpuMs={processCpu.TotalMilliseconds:F3}; wallMs={wallClock.Elapsed.TotalMilliseconds:F3}; " +
            report);

        Assert.Equal(plan.OutputWidth, outputWidth);
        Assert.Equal(plan.OutputHeight, outputHeight);
        Assert.Equal(1, credit.DisposeCount);
        Assert.Equal(ledgerBefore, GetLedgerSnapshot());
    }

    private static RasterDecodedPixelLease DecodeByMode(
        byte[] source,
        RasterImageDecodePlan plan,
        IDisposable credit,
        CopyMode mode) =>
        mode switch
        {
            CopyMode.Full => RasterNativeWicDecoder.Decode(source, plan, credit, s_noCancellation),
            CopyMode.Banded64 => RasterNativeWicDecoder.DecodeWithCopyBandsForDiagnostics(source, plan, credit, 64, s_noCancellation),
            CopyMode.Banded16 => RasterNativeWicDecoder.DecodeWithCopyBandsForDiagnostics(source, plan, credit, 16, s_noCancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static async Task<(byte[] Source, RasterImageDecodePlan Plan)> LoadOversizedWebpAsync()
    {
        using Stream fixture = typeof(RasterNativeWicCopyBandEquivalenceTests).Assembly
            .GetManifestResourceStream("MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp")
            ?? throw new InvalidOperationException("The pinned oversized WebP fixture is missing.");
        using var backing = new MemoryStream();
        await fixture.CopyToAsync(backing).ConfigureAwait(false);
        byte[] source = backing.ToArray();
        RasterImageDecodePlan plan = CreateStaticPlan(source, requestedWidth: 256, maximumPixels: MaximumPixels);
        return (source, plan);
    }

    private static void AssertStaticCopiesMatch(byte[] source, RasterImageDecodePlan plan, int copyBandRows)
    {
        var fullCredit = new CountingDecodeCredit();
        var bandedCredit = new CountingDecodeCredit();
        LedgerSnapshot before = GetLedgerSnapshot();
        using (RasterDecodedPixelLease full = RasterNativeWicDecoder.Decode(
                   source, plan, fullCredit, s_noCancellation))
        using (RasterDecodedPixelLease banded = RasterNativeWicDecoder.DecodeWithCopyBandsForDiagnostics(
                   source, plan, bandedCredit, copyBandRows, s_noCancellation))
        {
            Assert.Equal((full.Width, full.Height), (banded.Width, banded.Height));
            Assert.True(full.Pixels.AsSpan().SequenceEqual(banded.Pixels), "Row-banded final CopyPixels changed output bytes.");
        }

        Assert.Equal(1, fullCredit.DisposeCount);
        Assert.Equal(1, bandedCredit.DisposeCount);
        Assert.Equal(before, GetLedgerSnapshot());
    }

    private static void AssertGifCopiesMatch(byte[] source, RasterGifFirstFrameDecodePlan plan, int copyBandRows)
    {
        var fullCredit = new CountingDecodeCredit();
        var bandedCredit = new CountingDecodeCredit();
        LedgerSnapshot before = GetLedgerSnapshot();
        using (RasterDecodedPixelLease full = RasterNativeWicDecoder.DecodeGifFirstFrame(
                   source, plan, fullCredit, s_noCancellation))
        using (RasterDecodedPixelLease banded = RasterNativeWicDecoder.DecodeGifFirstFrameWithCopyBandsForDiagnostics(
                   source, plan, bandedCredit, copyBandRows, s_noCancellation))
        {
            Assert.Equal((full.Width, full.Height), (banded.Width, banded.Height));
            Assert.True(full.Pixels.AsSpan().SequenceEqual(banded.Pixels), "GIF row-banded final CopyPixels changed output bytes.");
        }

        Assert.Equal(1, fullCredit.DisposeCount);
        Assert.Equal(1, bandedCredit.DisposeCount);
        Assert.Equal(before, GetLedgerSnapshot());
    }

    private static LedgerSnapshot GetLedgerSnapshot()
    {
        NativeResourceRetirement retirement = NativeResourceRetirement.Shared;
        return new LedgerSnapshot(
            retirement.PendingRecordCount,
            retirement.PendingResourceCount,
            retirement.QuarantinedResourceCount);
    }

    private readonly record struct LedgerSnapshot(int PendingRecords, long PendingResources, long QuarantinedResources);

    private enum CopyMode
    {
        Full,
        Banded64,
        Banded16,
    }

    private static int GetBandRows(CopyMode mode) => mode switch
    {
        CopyMode.Full => 0,
        CopyMode.Banded64 => 64,
        CopyMode.Banded16 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private sealed class PrivateBytesSampler : IAsyncDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly object _processGate = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _samplingTask;
        private int _stopped;
        private long _peakPrivateBytes;

        internal PrivateBytesSampler()
        {
            BaselinePrivateBytes = SamplePrivateBytes();
            _peakPrivateBytes = BaselinePrivateBytes;
        }

        internal long BaselinePrivateBytes { get; }

        internal void Start()
        {
            if (_samplingTask is not null)
                throw new InvalidOperationException("The private-memory sampler can only start once.");
            _samplingTask = SampleEveryTwoMillisecondsAsync();
        }

        internal TimeSpan ReadProcessCpuTime()
        {
            lock (_processGate)
            {
                _process.Refresh();
                return _process.TotalProcessorTime;
            }
        }

        internal async Task<string> StopAndCaptureAsync()
        {
            await StopSamplingAsync().ConfigureAwait(false);
            long endPrivateBytes = SamplePrivateBytes();
            UpdatePeak(endPrivateBytes);
            long peak = Interlocked.Read(ref _peakPrivateBytes);
            return $"baselinePrivateBytes={BaselinePrivateBytes}; peakPrivateBytes={peak}; " +
                $"endPrivateBytes={endPrivateBytes}; peakDeltaBytes={System.Math.Max(0, peak - BaselinePrivateBytes)}";
        }

        public async ValueTask DisposeAsync()
        {
            await StopSamplingAsync().ConfigureAwait(false);
            _stop.Dispose();
            _process.Dispose();
        }

        private async Task SampleEveryTwoMillisecondsAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                    UpdatePeak(SamplePrivateBytes());
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task StopSamplingAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                _stop.Cancel();
            if (_samplingTask is not null)
                await _samplingTask.ConfigureAwait(false);
        }

        private long SamplePrivateBytes()
        {
            lock (_processGate)
            {
                _process.Refresh();
                return _process.PrivateMemorySize64;
            }
        }

        private void UpdatePeak(long sample)
        {
            long peak;
            while (sample > (peak = Interlocked.Read(ref _peakPrivateBytes)) &&
                Interlocked.CompareExchange(ref _peakPrivateBytes, sample, peak) != peak)
            {
            }
        }
    }

    private sealed class CountingDecodeCredit : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
#endif
