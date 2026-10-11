using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Test-only proof that a bounded WebP preview can be produced with uniquely-owned native WIC
/// interfaces and explicitly retired without relying on WinRT RCW finalization. This does not
/// qualify other codecs, arbitrary ICC profiles, EXIF orientations, GIF composition, or codec peak memory.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed partial class OwnedNativeWicLifetimeQualificationTests
{
    private readonly ITestOutputHelper _output;

    private const int SourceWidth = 2048;
    private const int SourceHeight = 8193;
    private const int OutputWidth = 256;
    private const int OutputHeight = 1025;
    private const int RepeatedDecodeCount = 8;
    private const int NativeRetirementWarmupCount = 8;
    private const int NativeRetirementCycleCount = 100;
    private const int ExpectedOwnedInterfaceCount = 9;
    private const int PrivateBytesSampleIntervalMilliseconds = 2;

    public OwnedNativeWicLifetimeQualificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task OversizedStaticWebp_UniqueNativeWicColorManagedGraphMatchesWinRtExactly()
    {
        byte[] source = await ReadPinnedWebpFixtureAsync();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.Equal(SourceWidth, budget.Width);
        Assert.Equal(SourceHeight, budget.Height);
        Assert.False(ContainsRiffChunk(source, "ICCP"));

        // The exact-pixel comparison is deliberately limited to this ICCP-free, orientation-1
        // VP8L fixture. Native WIC explicitly assigns the fixture's default EXIF color space (1,
        // sRGB), runs IWICColorTransform into PBGRA8, Fant-scales, and converts the final result to
        // PBGRA8. This is a narrow pipeline hypothesis, not a claim about WinRT's hidden working
        // format or arbitrary profile/orientation equivalence.
        await VerifyExactPreviewPixelsAsync(source);
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task OversizedStaticWebp_UniqueNativeWicGraphReportsLogicalRetirement()
    {
        byte[] source = await ReadPinnedWebpFixtureAsync();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.Equal(SourceWidth, budget.Width);
        Assert.Equal(SourceHeight, budget.Height);
        Assert.False(ContainsRiffChunk(source, "ICCP"));

        // No forced collection occurs between these native decode/retire iterations. Private-byte
        // sampling is diagnostic only; it can miss a transient peak and is not a codec memory cap.
        DecodeMeasurement measurement = await MeasureRepeatedNativeLifetimeAsync(source);
        _output.WriteLine(
            $"Owned native WIC WebP qualification: fixtureBytes={source.Length:N0}; " +
            $"source={SourceWidth}x{SourceHeight}; output={OutputWidth}x{OutputHeight} PBGRA8; " +
            $"frameColorContextCount={measurement.FrameColorContextCount}; " +
            $"repeatCount={measurement.RepeatCount}; elapsed={measurement.Elapsed.TotalMilliseconds:F1} ms; " +
            $"baselinePrivateBytes={measurement.BaselinePrivateBytes:N0}; " +
            $"sampledPeakPrivateBytes={measurement.SampledPeakPrivateBytes:N0}; " +
            $"privateBytesAfterLogicalRetirement={measurement.PrivateBytesAfterLogicalRetirement:N0}; " +
            $"handleCountBaseline={measurement.HandleCountBaseline}; " +
            $"handleCountAfterLogicalRetirement={measurement.HandleCountAfterLogicalRetirement}; " +
            $"sampleInterval={PrivateBytesSampleIntervalMilliseconds} ms. These process-wide samples " +
            "are observations, not bounds or release performance claims. The test covers one VP8L " +
            "fixture with no ICCP chunk and orientation 1 only; it does not qualify arbitrary ICC " +
            "profiles, EXIF orientation, GIF frame composition, other codecs, browser parity, or " +
            "transient decoder peak memory. The separate exact-pixel oracle must pass before this " +
            "native pipeline can be considered a fidelity candidate.");
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task OversizedStaticWebp_OneHundredOwnedNativeWicCyclesReportPerRetirementCustody()
    {
        byte[] source = await ReadPinnedWebpFixtureAsync();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.Equal(SourceWidth, budget.Width);
        Assert.Equal(SourceHeight, budget.Height);

        // One worker owns all COM initialization, native calls, and uninitialization. A single
        // reusable output buffer prevents this diagnostic from allocating 100 large managed
        // result arrays. No explicit or test-only GC occurs in this run.
        NativeRetirementRun run = await Task.Run(() => RunNativeRetirementCycles(source));

        Assert.Equal(NativeRetirementCycleCount, run.Cycles.Length);
        Assert.Equal(NativeRetirementCycleCount, run.PrivateBytesAfterRetirement.Length);
        Assert.Equal(NativeRetirementCycleCount, run.HandleCountAfterRetirement.Length);
        for (int i = 0; i < NativeRetirementCycleCount; i++)
        {
            WicGraphRetirement cycle = run.Cycles[i];
            Assert.Equal(OutputWidth, cycle.OutputWidth);
            Assert.Equal(OutputHeight, cycle.OutputHeight);
            Assert.Equal(ExpectedOwnedInterfaceCount, cycle.Retirement.AdoptedOwnedInterfaceCount);
            Assert.Equal(
                cycle.Retirement.AdoptedOwnedInterfaceCount,
                cycle.Retirement.SuccessfullyFinalReleasedOwnedInterfaceCount);
            Assert.Equal(0, cycle.Retirement.OutstandingQueryInterfaceReferences);
            Assert.True(cycle.Retirement.SourcePinReleased);
            Assert.True(cycle.Retirement.GraphRetired);
            Assert.False(cycle.Retirement.Quarantined);
        }

        _output.WriteLine(
            $"Owned native WIC repeated-retirement diagnostic: fixtureBytes={source.Length:N0}; " +
            $"source={SourceWidth}x{SourceHeight}; output={OutputWidth}x{OutputHeight}; " +
            $"warmups={NativeRetirementWarmupCount}; cycles={run.Cycles.Length}; " +
            $"ownedInterfacesPerCycle={ExpectedOwnedInterfaceCount}; allGraphsRetired=true; " +
            $"allInterfacesFinalReleased=true; allSourcePinsReleased=true; " +
            $"outstandingQueryReferences=0; quarantinedGraphs=0; elapsed={run.Elapsed.TotalMilliseconds:F1} ms; " +
            $"privateBytesBeforeCycles={run.PrivateBytesBeforeCycles:N0}; " +
            $"privateBytesAfterCycles={run.PrivateBytesAfterCycles:N0}; " +
            $"privateBytesPeakAtRetirement={run.MaximumPrivateBytesAtRetirement:N0}; " +
            $"handlesBeforeCycles={run.HandlesBeforeCycles}; " +
            $"handlesAfterCycles={run.HandlesAfterCycles}; " +
            $"handlesMaximumAtRetirement={run.MaximumHandleCountAtRetirement}; " +
            $"privateBytesAfterEachRetirement=[{string.Join(",", run.PrivateBytesAfterRetirement)}]; " +
            $"handlesAfterEachRetirement=[{string.Join(",", run.HandleCountAfterRetirement)}]. " +
            "Process bytes and handle counts are observational diagnostics only, not asserted memory or " +
            "resource ceilings; repeat this filtered Fact in separate fresh testhost processes before " +
            "drawing conclusions from process-wide measurements.");
    }

    private static async Task<byte[]> ReadPinnedWebpFixtureAsync()
    {
        using Stream fixture = typeof(OwnedNativeWicLifetimeQualificationTests).Assembly
            .GetManifestResourceStream("MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp")
            ?? throw new InvalidOperationException("The pinned oversized static WebP fixture is missing.");
        using var bytes = new MemoryStream();
        await fixture.CopyToAsync(bytes).ConfigureAwait(false);
        byte[] source = bytes.ToArray();

        Assert.Equal(51_112, source.Length);
        Assert.Equal(
            "6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85",
            Convert.ToHexString(SHA256.HashData(source)));
        Assert.Equal("WEBP", RasterImageResourceBudget.Validate(source).Format);
        Assert.False(ContainsRiffChunk(source, "ICCP"));
        return source;
    }

    private static async Task<(int Width, int Height, byte[] Pixels)> DecodeWithWinRtAsync(byte[] source)
    {
        using var backing = new MemoryStream(source, writable: false);
        using IRandomAccessStream stream = backing.AsRandomAccessStream();
        IAsyncOperation<BitmapDecoder> decoderOperation = BitmapDecoder.CreateAsync(stream);
        BitmapDecoder decoder;
        try
        {
            decoder = await decoderOperation.AsTask().ConfigureAwait(false);
        }
        finally
        {
            // The await reaches a terminal state before Close is called, including when it faults.
            decoderOperation.Close();
        }
        Assert.Equal(SourceWidth, checked((int)decoder.PixelWidth));
        Assert.Equal(SourceHeight, checked((int)decoder.PixelHeight));

        var transform = new BitmapTransform
        {
            ScaledWidth = OutputWidth,
            ScaledHeight = OutputHeight,
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
            try
            {
                bitmap = await bitmapOperation.AsTask().ConfigureAwait(false);
            }
            finally
            {
                bitmapOperation.Close();
            }

            SoftwareBitmap completedBitmap = bitmap
                ?? throw new InvalidOperationException("The completed WIC operation returned no SoftwareBitmap.");
            return (
                checked((int)completedBitmap.PixelWidth),
                checked((int)completedBitmap.PixelHeight),
                CopyPremultipliedBgraBytes(completedBitmap));
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private async Task VerifyExactPreviewPixelsAsync(byte[] source)
    {
        (int Width, int Height, byte[] Pixels) reference = await DecodeWithWinRtAsync(source);
        NativeDecodeResult native = await Task.Run(() => DecodeWithOwnedNativeWic(source)).ConfigureAwait(false);
        try
        {
            Assert.Equal(OutputWidth, reference.Width);
            Assert.Equal(OutputHeight, reference.Height);
            Assert.Equal(OutputWidth, native.Width);
            Assert.Equal(OutputHeight, native.Height);
            PixelDifference difference = ComparePixels(reference.Pixels, native.Pixels);
            _output.WriteLine(
                $"Native WIC color-managed pixel oracle: frameColorContextCount={native.FrameColorContextCount}; " +
                $"differingBytes={difference.Count}; maximumChannelDelta={difference.MaximumChannelDelta}; " +
                $"firstDifferenceByte={difference.FirstIndex}; pixel={difference.FirstX},{difference.FirstY}; " +
                $"channel={difference.FirstChannel}; WinRT={difference.FirstReferenceByte}; " +
                $"native={difference.FirstNativeByte}.");
            Assert.True(
                difference.Count == 0,
                $"Native WIC output must match the existing WinRT preview byte-for-byte for the pinned " +
                $"fixture. Differing bytes: {difference.Count:N0}; first differing byte: {difference.FirstIndex} " +
                $"(pixel {difference.FirstX},{difference.FirstY}, channel {difference.FirstChannel}: " +
                $"WinRT={difference.FirstReferenceByte}, native={difference.FirstNativeByte}); " +
                $"maximum channel delta={difference.MaximumChannelDelta}; " +
                $"frameColorContextCount={native.FrameColorContextCount}.");
        }
        finally
        {
            Array.Clear(reference.Pixels);
            Array.Clear(native.Pixels);
        }
    }

    private static NativeDecodeResult DecodeWithOwnedNativeWic(byte[] source)
        => DecodeWithOwnedNativeWic(source, reusableOutput: null);

    private static NativeDecodeResult DecodeWithOwnedNativeWic(byte[] source, byte[]? reusableOutput)
    {
        var graph = new OwnedWicGraph(source);
        NativeDecodeResult result;
        try
        {
            graph.InitializeApartment();
            graph.CreateFactory();
            graph.CreateBorrowingStream();
            graph.CreateDecoder();
            graph.ReadFrameAndSourceSize();
            graph.CreateColorManagedTransform();
            graph.CreateFantScaler();
            graph.CreatePremultipliedConverter();
            result = graph.CopyPixels(reusableOutput);
        }
        finally
        {
            // This executes inside the same fixed source-pin lifetime held by OwnedWicGraph.
            graph.Dispose();
        }

        return new NativeDecodeResult(
            result.Width,
            result.Height,
            result.Pixels,
            result.FrameColorContextCount,
            graph.GetRetirement());
    }

    private static NativeRetirementRun RunNativeRetirementCycles(byte[] source)
    {
        byte[] reusableOutput = new byte[checked(checked(OutputWidth * OutputHeight) * 4)];
        for (int i = 0; i < NativeRetirementWarmupCount; i++)
        {
            NativeDecodeResult warmup = DecodeWithOwnedNativeWic(source, reusableOutput);
            Assert.Equal(OutputWidth, warmup.Width);
            Assert.Equal(OutputHeight, warmup.Height);
            AssertRetirementCustody(warmup.Retirement);
        }

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long privateBytesBeforeCycles = process.PrivateMemorySize64;
        int handlesBeforeCycles = process.HandleCount;
        var privateBytesAfterRetirement = new long[NativeRetirementCycleCount];
        var handleCountAfterRetirement = new int[NativeRetirementCycleCount];
        var cycles = new WicGraphRetirement[NativeRetirementCycleCount];
        long maximumPrivateBytesAtRetirement = privateBytesBeforeCycles;
        int maximumHandleCountAtRetirement = handlesBeforeCycles;
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < NativeRetirementCycleCount; i++)
        {
            NativeDecodeResult result = DecodeWithOwnedNativeWic(source, reusableOutput);
            Assert.Equal(OutputWidth, result.Width);
            Assert.Equal(OutputHeight, result.Height);
            cycles[i] = new WicGraphRetirement(result.Width, result.Height, result.Retirement);

            process.Refresh();
            long privateBytes = process.PrivateMemorySize64;
            int handleCount = process.HandleCount;
            privateBytesAfterRetirement[i] = privateBytes;
            handleCountAfterRetirement[i] = handleCount;
            maximumPrivateBytesAtRetirement = System.Math.Max(maximumPrivateBytesAtRetirement, privateBytes);
            maximumHandleCountAtRetirement = System.Math.Max(maximumHandleCountAtRetirement, handleCount);
        }
        stopwatch.Stop();
        process.Refresh();
        long privateBytesAfterCycles = process.PrivateMemorySize64;
        int handlesAfterCycles = process.HandleCount;
        Array.Clear(reusableOutput);

        return new NativeRetirementRun(
            cycles,
            privateBytesAfterRetirement,
            handleCountAfterRetirement,
            privateBytesBeforeCycles,
            privateBytesAfterCycles,
            maximumPrivateBytesAtRetirement,
            handlesBeforeCycles,
            handlesAfterCycles,
            maximumHandleCountAtRetirement,
            stopwatch.Elapsed);
    }

    private static void AssertRetirementCustody(WicGraphRetirementInfo retirement)
    {
        Assert.Equal(ExpectedOwnedInterfaceCount, retirement.AdoptedOwnedInterfaceCount);
        Assert.Equal(
            retirement.AdoptedOwnedInterfaceCount,
            retirement.SuccessfullyFinalReleasedOwnedInterfaceCount);
        Assert.Equal(0, retirement.OutstandingQueryInterfaceReferences);
        Assert.True(retirement.SourcePinReleased);
        Assert.True(retirement.GraphRetired);
        Assert.False(retirement.Quarantined);
    }

    private async Task<DecodeMeasurement> MeasureRepeatedNativeLifetimeAsync(byte[] source)
    {
        // Warm codec dispatch before taking the comparison baseline. The output is explicitly
        // cleared and released; no GC is requested before or during the repeated workload.
        NativeDecodeResult warmup = await Task.Run(() => DecodeWithOwnedNativeWic(source)).ConfigureAwait(false);

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        using var sampler = new PrivateBytesSampler(process.PrivateMemorySize64);
        process.Refresh();
        long baselinePrivateBytes = process.PrivateMemorySize64;
        int handleCountBaseline = process.HandleCount;
        int frameColorContextCount = warmup.FrameColorContextCount;
        Array.Clear(warmup.Pixels);
        sampler.SampleNow();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < RepeatedDecodeCount; i++)
            {
                NativeDecodeResult result = await Task.Run(() => DecodeWithOwnedNativeWic(source))
                    .ConfigureAwait(false);
                Assert.Equal(OutputWidth, result.Width);
                Assert.Equal(OutputHeight, result.Height);
                frameColorContextCount = result.FrameColorContextCount;
                sampler.SampleNow();
                Array.Clear(result.Pixels);
            }
        }
        finally
        {
            stopwatch.Stop();
            await sampler.StopAsync().ConfigureAwait(false);
        }

        process.Refresh();
        return new DecodeMeasurement(
            RepeatedDecodeCount,
            stopwatch.Elapsed,
            baselinePrivateBytes,
            sampler.PeakPrivateBytes,
            process.PrivateMemorySize64,
            handleCountBaseline,
            process.HandleCount,
            frameColorContextCount);
    }

    private static byte[] CopyPremultipliedBgraBytes(SoftwareBitmap bitmap)
    {
        int byteCount = checked(checked((int)bitmap.PixelWidth) * checked((int)bitmap.PixelHeight) * 4);
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)byteCount))
        {
            Length = checked((uint)byteCount),
        };
        bitmap.CopyToBuffer(buffer);

        byte[] result = new byte[byteCount];
        using DataReader reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(result);
        return result;
    }

    private static PixelDifference ComparePixels(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
            return new PixelDifference(
                System.Math.Abs(left.Length - right.Length),
                System.Math.Min(left.Length, right.Length),
                byte.MaxValue,
                0,
                0,
                -1,
                -1,
                "length");
        int count = 0;
        int first = -1;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                if (first < 0)
                    first = i;
                count++;
            }
        }
        if (first < 0)
            return new PixelDifference(0, -1, 0, 0, 0);

        int maxDelta = 0;
        for (int i = 0; i < left.Length; i++)
            maxDelta = System.Math.Max(maxDelta, System.Math.Abs(left[i] - right[i]));
        int pixelIndex = first / 4;
        return new PixelDifference(
            count,
            first,
            maxDelta,
            left[first],
            right[first],
            pixelIndex % OutputWidth,
            pixelIndex / OutputWidth,
            ChannelName(first & 3));
    }

    private static string ChannelName(int channel) => channel switch
    {
        0 => "B",
        1 => "G",
        2 => "R",
        3 => "A",
        _ => "?",
    };

    private static bool ContainsRiffChunk(byte[] bytes, string expectedFourCc)
    {
        Assert.True(bytes.Length >= 12);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal((uint)(bytes.Length - 8), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));

        int offset = 12;
        while (offset < bytes.Length)
        {
            Assert.True(offset <= bytes.Length - 8, "The WebP fixture has a truncated RIFF chunk header.");
            string fourCc = Encoding.ASCII.GetString(bytes, offset, 4);
            uint payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            long nextOffset = checked((long)offset + 8 + payloadLength + (payloadLength & 1));
            Assert.True(nextOffset <= bytes.Length, $"WebP chunk {fourCc} exceeds the fixture boundary.");
            if (string.Equals(fourCc, expectedFourCc, StringComparison.Ordinal))
                return true;
            offset = checked((int)nextOffset);
        }

        Assert.Equal(bytes.Length, offset);
        return false;
    }

    private sealed record DecodeMeasurement(
        int RepeatCount,
        TimeSpan Elapsed,
        long BaselinePrivateBytes,
        long SampledPeakPrivateBytes,
        long PrivateBytesAfterLogicalRetirement,
        int HandleCountBaseline,
        int HandleCountAfterLogicalRetirement,
        int FrameColorContextCount);

    private sealed record NativeRetirementRun(
        WicGraphRetirement[] Cycles,
        long[] PrivateBytesAfterRetirement,
        int[] HandleCountAfterRetirement,
        long PrivateBytesBeforeCycles,
        long PrivateBytesAfterCycles,
        long MaximumPrivateBytesAtRetirement,
        int HandlesBeforeCycles,
        int HandlesAfterCycles,
        int MaximumHandleCountAtRetirement,
        TimeSpan Elapsed);

    private readonly record struct WicGraphRetirement(
        int OutputWidth,
        int OutputHeight,
        WicGraphRetirementInfo Retirement);

    private readonly record struct WicGraphRetirementInfo(
        int AdoptedOwnedInterfaceCount,
        int SuccessfullyFinalReleasedOwnedInterfaceCount,
        int OutstandingQueryInterfaceReferences,
        bool SourcePinReleased,
        bool GraphRetired,
        bool Quarantined);

    private readonly record struct PixelDifference(
        int Count,
        int FirstIndex,
        int MaximumChannelDelta,
        byte FirstReferenceByte,
        byte FirstNativeByte,
        int FirstX = -1,
        int FirstY = -1,
        string FirstChannel = "none");

    private sealed class PrivateBytesSampler : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _samplingTask;
        private readonly object _sampleGate = new();
        private long _peakPrivateBytes;

        internal PrivateBytesSampler(long baselinePrivateBytes)
        {
            _peakPrivateBytes = baselinePrivateBytes;
            _samplingTask = SampleUntilStoppedAsync(_stop.Token);
        }

        internal long PeakPrivateBytes => Interlocked.Read(ref _peakPrivateBytes);

        internal void SampleNow()
        {
            long current;
            lock (_sampleGate)
            {
                _process.Refresh();
                current = _process.PrivateMemorySize64;
            }
            Record(current);
        }

        internal async Task StopAsync()
        {
            if (!_stop.IsCancellationRequested)
                _stop.Cancel();
            await _samplingTask.ConfigureAwait(false);
            SampleNow();
        }

        public void Dispose()
        {
            if (!_stop.IsCancellationRequested)
                _stop.Cancel();
            _samplingTask.GetAwaiter().GetResult();
            _stop.Dispose();
            _process.Dispose();
        }

        private async Task SampleUntilStoppedAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PrivateBytesSampleIntervalMilliseconds));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    long current;
                    lock (_sampleGate)
                    {
                        _process.Refresh();
                        current = _process.PrivateMemorySize64;
                    }
                    Record(current);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }

        private void Record(long current)
        {
            long observed;
            do
            {
                observed = Interlocked.Read(ref _peakPrivateBytes);
                if (current <= observed)
                    return;
            }
            while (Interlocked.CompareExchange(ref _peakPrivateBytes, current, observed) != observed);
        }
    }

    private readonly record struct NativeDecodeResult(
        int Width,
        int Height,
        byte[] Pixels,
        int FrameColorContextCount,
        WicGraphRetirementInfo Retirement);

    /// <summary>
    /// Owns every unique native interface and the source pin. If final release fails, the graph
    /// is deliberately rooted and its source remains pinned; a failed test must not turn a borrowed
    /// WIC stream into a use-after-free. No decoder/bitmap is created through WinRT here.
    /// </summary>
    private sealed unsafe class OwnedWicGraph : IDisposable
    {
        private static readonly object QuarantineGate = new();
        private static readonly List<OwnedWicGraph> QuarantinedGraphs = [];

        private static readonly Guid ClsidWicImagingFactory2 = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
        private static readonly Guid IidWicImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
        private static readonly Guid IidIStream = new("0000000c-0000-0000-c000-000000000046");
        private static readonly Guid IidWicBitmapSource = new("00000120-a8f2-4877-ba0a-fd2b6645fb94");
        private static readonly Guid IidWicColorContext = new("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d");
        private static readonly Guid IidWicColorTransform = new("b66f034f-d0e2-40ab-b436-6de39e321a94");
        private static readonly Guid WicPixelFormat32bppPbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

        private readonly byte[] _source;
        private readonly StrategyBasedComWrappers _wrappers = new();
        private GCHandle _sourcePin;
        private ComObject? _factoryObject;
        private ComObject? _streamObject;
        private ComObject? _decoderObject;
        private ComObject? _frameObject;
        private ComObject? _sourceColorContextObject;
        private ComObject? _destinationColorContextObject;
        private ComObject? _colorTransformObject;
        private ComObject? _scalerObject;
        private ComObject? _converterObject;
        private IWICImagingFactory? _factory;
        private IWICStream? _stream;
        private IWICBitmapDecoder? _decoder;
        private IWICBitmapFrameDecode? _frame;
        private IWICColorContext? _sourceColorContext;
        private IWICColorContext? _destinationColorContext;
        private IWICColorTransform? _colorTransform;
        private IWICBitmapScaler? _scaler;
        private IWICFormatConverter? _converter;
        private int _sourceWidth;
        private int _sourceHeight;
        private int _frameColorContextCount;
        private int _adoptedOwnedInterfaceCount;
        private int _successfullyFinalReleasedOwnedInterfaceCount;
        private int _outstandingQueryInterfaceReferences;
        private bool _apartmentInitialized;
        private bool _sourcePinReleased;
        private bool _quarantined;
        private bool _retired;

        internal OwnedWicGraph(byte[] source)
        {
            _source = source;
            _sourcePin = GCHandle.Alloc(source, GCHandleType.Pinned);
        }

        internal void InitializeApartment()
        {
            // All native WIC calls and final releases stay on this one MTA thread.
            int hr = NativeMethods.CoInitializeEx(0, 0);
            if (hr < 0)
                ThrowForHResult(hr, "CoInitializeEx(COINIT_MULTITHREADED)");
            _apartmentInitialized = true; // S_OK and S_FALSE both require CoUninitialize.
        }

        internal void CreateFactory()
        {
            nint native = 0;
            Guid clsid = ClsidWicImagingFactory2;
            Guid iid = IidWicImagingFactory;
            int hr = NativeMethods.CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &native);
            if (native != 0)
                _factory = Adopt<IWICImagingFactory>(ref native, ref _factoryObject);
            ThrowForHResult(hr, "CoCreateInstance(CLSID_WICImagingFactory2)");
            if (_factory is null)
                throw new InvalidOperationException("CoCreateInstance returned no WIC factory.");
        }

        internal void CreateBorrowingStream()
        {
            IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateStream(&native);
            if (native != 0)
                _stream = Adopt<IWICStream>(ref native, ref _streamObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateStream");
            IWICStream stream = Require(_stream, "stream");
            byte* bytes = (byte*)_sourcePin.AddrOfPinnedObject();
            hr = stream.InitializeFromMemory(bytes, checked((uint)_source.Length));
            ThrowForHResult(hr, "IWICStream.InitializeFromMemory");
        }

        internal void CreateDecoder()
        {
            IWICImagingFactory factory = Require(_factory, "factory");
            nint streamPointer = QueryOwnedInterface(Require(_streamObject, "stream owner"), IidIStream);
            nint native = 0;
            int hr;
            try
            {
                hr = factory.CreateDecoderFromStream(
                    streamPointer,
                    0,
                    WicDecodeMetadataCacheOnDemand,
                    &native);
                if (native != 0)
                    _decoder = Adopt<IWICBitmapDecoder>(ref native, ref _decoderObject);
            }
            finally
            {
                ReleaseOwnedPointer(ref streamPointer);
            }

            ThrowForHResult(hr, "IWICImagingFactory.CreateDecoderFromStream");
            if (_decoder is null)
                throw new InvalidOperationException("WIC returned no bitmap decoder.");
        }

        internal void ReadFrameAndSourceSize()
        {
            IWICBitmapDecoder decoder = Require(_decoder, "decoder");
            uint frameCount = 0;
            int hr = decoder.GetFrameCount(&frameCount);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetFrameCount");
            if (frameCount != 1)
                throw new InvalidOperationException($"Pinned static fixture unexpectedly has {frameCount} frames.");

            nint native = 0;
            hr = decoder.GetFrame(0, &native);
            if (native != 0)
                _frame = Adopt<IWICBitmapFrameDecode>(ref native, ref _frameObject);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetFrame");
            IWICBitmapFrameDecode frame = Require(_frame, "frame");
            uint width = 0;
            uint height = 0;
            hr = frame.GetSize(&width, &height);
            ThrowForHResult(hr, "IWICBitmapSource.GetSize");
            _sourceWidth = checked((int)width);
            _sourceHeight = checked((int)height);
            if (_sourceWidth != SourceWidth || _sourceHeight != SourceHeight)
                throw new InvalidOperationException(
                    $"Native WIC decoded {width}x{height}; expected {SourceWidth}x{SourceHeight}.");

            uint colorContextCount = 0;
            hr = frame.GetColorContexts(0, null, &colorContextCount);
            ThrowForHResult(hr, "IWICBitmapFrameDecode.GetColorContexts(count)");
            _frameColorContextCount = checked((int)colorContextCount);
        }

        internal void CreateColorManagedTransform()
        {
            IWICImagingFactory factory = Require(_factory, "factory");
            IWICBitmapFrameDecode frame = Require(_frame, "frame");
            CreateSourceColorContext(factory, frame);
            CreateSrgbDestinationContext(factory);

            nint transformNative = 0;
            int hr = factory.CreateColorTransformer(&transformNative);
            if (transformNative != 0)
                _colorTransform = Adopt<IWICColorTransform>(ref transformNative, ref _colorTransformObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorTransformer");

            IWICColorTransform colorTransform = Require(_colorTransform, "color transform");
            Guid destinationFormat = WicPixelFormat32bppPbgra;
            nint framePointer = 0;
            nint sourceContextPointer = 0;
            nint destinationContextPointer = 0;
            try
            {
                // Acquire each independently-owned QueryInterface result inside the protected
                // region. A failure on a later QI must not leak earlier references and leave the
                // graph/pinned source alive without its explicit owner.
                framePointer = QueryOwnedInterface(
                    Require(_frameObject, "frame owner"),
                    IidWicBitmapSource);
                sourceContextPointer = QueryOwnedInterface(
                    Require(_sourceColorContextObject, "source color context owner"),
                    IidWicColorContext);
                destinationContextPointer = QueryOwnedInterface(
                    Require(_destinationColorContextObject, "destination color context owner"),
                    IidWicColorContext);
                hr = colorTransform.Initialize(
                    framePointer,
                    sourceContextPointer,
                    destinationContextPointer,
                    &destinationFormat);
            }
            finally
            {
                ReleaseOwnedPointer(ref destinationContextPointer);
                ReleaseOwnedPointer(ref sourceContextPointer);
                ReleaseOwnedPointer(ref framePointer);
            }
            ThrowForHResult(hr, "IWICColorTransform.Initialize(sRGB,32bppPBGRA)");
        }

        private void CreateSourceColorContext(IWICImagingFactory factory, IWICBitmapFrameDecode frame)
        {
            nint sourceNative = 0;
            int hr = factory.CreateColorContext(&sourceNative);
            if (sourceNative != 0)
                _sourceColorContext = Adopt<IWICColorContext>(ref sourceNative, ref _sourceColorContextObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorContext(source)");

            IWICColorContext sourceContext = Require(_sourceColorContext, "source color context");
            if (_frameColorContextCount == 0)
            {
                // The pinned WebP fixture has no embedded color profile. WIC's EXIF color-space
                // value 1 is sRGB, matching the default color-space assumption for this fixture.
                hr = sourceContext.InitializeFromExifColorSpace(1);
                ThrowForHResult(hr, "IWICColorContext.InitializeFromExifColorSpace(sRGB default)");
                return;
            }

            // WIC requires caller-created color-context objects to be supplied for initialization.
            // This fixture is pinned ICCP-free, but retain the native profile path in the proof so
            // a changed fixture fails loudly instead of silently interpreting its profile as sRGB.
            if (_frameColorContextCount != 1)
                throw new InvalidOperationException(
                    $"The pinned single-image proof unexpectedly has {_frameColorContextCount} source color contexts.");

            nint sourceContextPointer = QueryOwnedInterface(
                Require(_sourceColorContextObject, "source color context owner"), IidWicColorContext);
            try
            {
                uint actualCount = 0;
                hr = frame.GetColorContexts(1, &sourceContextPointer, &actualCount);
                ThrowForHResult(hr, "IWICBitmapFrameDecode.GetColorContexts(profile)");
                if (actualCount != 1)
                    throw new InvalidOperationException(
                        $"WIC reported {actualCount} profile contexts after the count query reported one.");
            }
            finally
            {
                ReleaseOwnedPointer(ref sourceContextPointer);
            }
        }

        private void CreateSrgbDestinationContext(IWICImagingFactory factory)
        {
            nint destinationNative = 0;
            int hr = factory.CreateColorContext(&destinationNative);
            if (destinationNative != 0)
                _destinationColorContext = Adopt<IWICColorContext>(
                    ref destinationNative,
                    ref _destinationColorContextObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorContext(destination)");
            hr = Require(_destinationColorContext, "destination color context")
                .InitializeFromExifColorSpace(1);
            ThrowForHResult(hr, "IWICColorContext.InitializeFromExifColorSpace(sRGB destination)");
        }

        internal void CreateFantScaler()
        {
            IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateBitmapScaler(&native);
            if (native != 0)
                _scaler = Adopt<IWICBitmapScaler>(ref native, ref _scalerObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateBitmapScaler");

            IWICBitmapScaler scaler = Require(_scaler, "scaler");
            nint sourcePointer = QueryOwnedInterface(
                Require(_colorTransformObject, "color transform owner"),
                IidWicBitmapSource);
            try
            {
                hr = scaler.Initialize(
                    sourcePointer,
                    OutputWidth,
                    OutputHeight,
                    WicBitmapInterpolationModeFant);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICBitmapScaler.Initialize(color-managed Fant)");
        }

        internal void CreatePremultipliedConverter()
        {
            IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateFormatConverter(&native);
            if (native != 0)
                _converter = Adopt<IWICFormatConverter>(ref native, ref _converterObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateFormatConverter");

            IWICFormatConverter converter = Require(_converter, "converter");
            Guid targetFormat = WicPixelFormat32bppPbgra;
            nint sourcePointer = QueryOwnedInterface(Require(_scalerObject, "scaler owner"), IidWicBitmapSource);
            try
            {
                hr = converter.Initialize(
                    sourcePointer,
                    &targetFormat,
                    WicBitmapDitherTypeNone,
                    0,
                    0,
                    WicBitmapPaletteTypeCustom);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICFormatConverter.Initialize(final PBGRA)");
        }

        internal NativeDecodeResult CopyPixels(byte[]? reusableOutput)
        {
            IWICFormatConverter converter = Require(_converter, "converter");
            int expectedByteCount = checked(checked(OutputWidth * OutputHeight) * 4);
            byte[] pixels = reusableOutput ?? new byte[expectedByteCount];
            if (pixels.Length != expectedByteCount)
                throw new ArgumentException(
                    $"The reusable native WIC output buffer has {pixels.Length} bytes; expected {expectedByteCount}.",
                    nameof(reusableOutput));
            fixed (byte* destination = pixels)
            {
                int hr = converter.CopyPixels(
                    rect: 0,
                    stride: checked(OutputWidth * 4),
                    bufferSize: checked((uint)pixels.Length),
                    buffer: destination);
                ThrowForHResult(hr, "IWICBitmapSource.CopyPixels(final PBGRA)");
            }
            return new NativeDecodeResult(
                OutputWidth,
                OutputHeight,
                pixels,
                _frameColorContextCount,
                default);
        }

        internal WicGraphRetirementInfo GetRetirement() => new(
            _adoptedOwnedInterfaceCount,
            _successfullyFinalReleasedOwnedInterfaceCount,
            _outstandingQueryInterfaceReferences,
            _sourcePinReleased,
            _retired,
            _quarantined);

        public void Dispose()
        {
            if (_retired)
                return;

            if (_outstandingQueryInterfaceReferences != 0)
            {
                _quarantined = true;
                lock (QuarantineGate)
                    QuarantinedGraphs.Add(this);
                throw new InvalidOperationException(
                    $"Cannot retire a WIC graph with {_outstandingQueryInterfaceReferences} outstanding " +
                    "QueryInterface references. Its source pin and dependency graph are retained.");
            }

            Exception? failure = null;
            if (!TryFinalRelease(ref _converterObject, ref _converter, ref failure) ||
                !TryFinalRelease(ref _scalerObject, ref _scaler, ref failure) ||
                !TryFinalRelease(ref _colorTransformObject, ref _colorTransform, ref failure) ||
                !TryFinalRelease(ref _destinationColorContextObject, ref _destinationColorContext, ref failure) ||
                !TryFinalRelease(ref _sourceColorContextObject, ref _sourceColorContext, ref failure) ||
                !TryFinalRelease(ref _frameObject, ref _frame, ref failure) ||
                !TryFinalRelease(ref _decoderObject, ref _decoder, ref failure) ||
                !TryFinalRelease(ref _streamObject, ref _stream, ref failure) ||
                !TryFinalRelease(ref _factoryObject, ref _factory, ref failure))
            {
                _quarantined = true;
                lock (QuarantineGate)
                    QuarantinedGraphs.Add(this);
                throw new InvalidOperationException(
                    "A unique native WIC interface could not be retired. Its source pin and remaining " +
                    "dependent graph are retained for the lifetime of this test process.",
                    failure);
            }

            if (_sourcePin.IsAllocated)
            {
                _sourcePin.Free();
                _sourcePinReleased = true;
            }
            if (_apartmentInitialized)
            {
                NativeMethods.CoUninitialize();
                _apartmentInitialized = false;
            }
            _retired = true;
        }

        private T Adopt<T>(ref nint native, ref ComObject? slot)
            where T : class
        {
            if (native == 0)
                throw new InvalidOperationException("WIC returned a null COM interface pointer.");
            if (slot is not null)
                throw new InvalidOperationException("A WIC graph slot was assigned twice.");

            nint ownedResultReference = native;
            ComObject? unrecordedWrapper = null;
            try
            {
                unrecordedWrapper = (ComObject)_wrappers.GetOrCreateObjectForComInstance(
                    ownedResultReference,
                    CreateObjectFlags.UniqueInstance);
                slot = unrecordedWrapper;
                unrecordedWrapper = null; // The graph slot owns it before any further operation.
                _adoptedOwnedInterfaceCount++;

                T typed = (T)(object)slot!;
                // GetOrCreateObjectForComInstance gives the unique wrapper its own reference.
                // Release only this method's individually-owned out reference, never a WinRT RCW.
                Marshal.Release(ownedResultReference);
                native = 0;
                return typed;
            }
            catch
            {
                if (unrecordedWrapper is not null)
                    unrecordedWrapper.FinalRelease();
                if (native != 0)
                {
                    Marshal.Release(native);
                    native = 0;
                }
                throw;
            }
        }

        private bool TryFinalRelease<T>(ref ComObject? owner, ref T? typed, ref Exception? failure)
            where T : class
        {
            if (owner is null)
            {
                typed = null;
                return true;
            }

            try
            {
                owner.FinalRelease();
                owner = null;
                typed = null;
                _successfullyFinalReleasedOwnedInterfaceCount++;
                return true;
            }
            catch (Exception exception)
            {
                failure = exception;
                return false;
            }
        }

        private nint QueryOwnedInterface(ComObject owner, Guid iid)
        {
            bool found = ComWrappers.TryGetComInstance(owner, out nint instance);
            if (!found || instance == 0)
            {
                if (instance != 0)
                    Marshal.Release(instance);
                throw new InvalidOperationException("The unique WIC wrapper no longer exposes its COM instance.");
            }

            try
            {
                int hr = Marshal.QueryInterface(instance, in iid, out nint queried);
                if (hr < 0)
                {
                    if (queried != 0)
                        Marshal.Release(queried);
                    ThrowForHResult(hr, $"QueryInterface({iid})");
                }
                if (queried == 0)
                    throw new InvalidOperationException($"QueryInterface({iid}) returned a null interface pointer.");
                _outstandingQueryInterfaceReferences++;
                return queried;
            }
            finally
            {
                // TryGetComInstance returns an owned reference; release that reference separately
                // from the owned QueryInterface result returned to the caller.
                Marshal.Release(instance);
            }
        }

        private void ReleaseOwnedPointer(ref nint pointer)
        {
            nint value = pointer;
            pointer = 0;
            if (value != 0)
            {
                Marshal.Release(value);
                _outstandingQueryInterfaceReferences--;
            }
        }

        private static T Require<T>(T? value, string description) where T : class =>
            value ?? throw new InvalidOperationException($"The WIC {description} was not created.");

        private static void ThrowForHResult(int hr, string operation)
        {
            if (hr < 0)
                throw new COMException($"{operation} failed with HRESULT 0x{hr:X8}.", hr);
        }
    }

    private const uint ClsctxInprocServer = 0x1;
    private const int WicDecodeMetadataCacheOnDemand = 0;
    private const int WicBitmapInterpolationModeFant = 3;
    private const int WicBitmapDitherTypeNone = 0;
    private const int WicBitmapPaletteTypeCustom = 0;

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename(char* filename, Guid* vendor, uint access, int options, nint* decoder);
        [PreserveSig] int CreateDecoderFromStream(nint stream, nint vendor, int options, nint* decoder);
        [PreserveSig] int CreateDecoderFromFileHandle(nuint fileHandle, nint vendor, int options, nint* decoder);
        [PreserveSig] int CreateComponentInfo(Guid* clsid, nint* componentInfo);
        [PreserveSig] int CreateDecoder(Guid* containerFormat, nint vendor, nint* decoder);
        [PreserveSig] int CreateEncoder(Guid* containerFormat, nint vendor, nint* encoder);
        [PreserveSig] int CreatePalette(nint* palette);
        [PreserveSig] int CreateFormatConverter(nint* converter);
        [PreserveSig] int CreateBitmapScaler(nint* scaler);
        [PreserveSig] int CreateBitmapClipper(nint* clipper);
        [PreserveSig] int CreateBitmapFlipRotator(nint* flipRotator);
        [PreserveSig] int CreateStream(nint* stream);
        [PreserveSig] int CreateColorContext(nint* colorContext);
        [PreserveSig] int CreateColorTransformer(nint* colorTransform);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("135ff860-22b7-4ddf-b0f6-218f4f299a43")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICStream : IStreamAbi
    {
        [PreserveSig] int InitializeFromIStream(nint stream);
        [PreserveSig] int InitializeFromFilename(char* filename, uint access);
        [PreserveSig] int InitializeFromMemory(byte* buffer, uint bufferSize);
        [PreserveSig] int InitializeFromIStreamRegion(nint stream, ulong offset, ulong maximumSize);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("0000000c-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IStreamAbi
    {
        [PreserveSig] int Read(void* buffer, uint count, uint* read);
        [PreserveSig] int Write(void* buffer, uint count, uint* written);
        [PreserveSig] int Seek(long move, uint origin, ulong* newPosition);
        [PreserveSig] int SetSize(ulong size);
        [PreserveSig] int CopyTo(nint target, ulong count, ulong* read, ulong* written);
        [PreserveSig] int Commit(uint flags);
        [PreserveSig] int Revert();
        [PreserveSig] int LockRegion(ulong offset, ulong count, uint lockType);
        [PreserveSig] int UnlockRegion(ulong offset, ulong count, uint lockType);
        [PreserveSig] int Stat(nint stat, uint flags);
        [PreserveSig] int Clone(nint* stream);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapDecoder
    {
        [PreserveSig] int QueryCapability(nint stream, uint* capability);
        [PreserveSig] int Initialize(nint stream, int options);
        [PreserveSig] int GetContainerFormat(Guid* format);
        [PreserveSig] int GetDecoderInfo(nint* decoderInfo);
        [PreserveSig] int CopyPalette(nint palette);
        [PreserveSig] int GetMetadataQueryReader(nint* reader);
        [PreserveSig] int GetPreview(nint* preview);
        [PreserveSig] int GetColorContexts(uint count, nint* contexts, uint* actualCount);
        [PreserveSig] int GetThumbnail(nint* thumbnail);
        [PreserveSig] int GetFrameCount(uint* count);
        [PreserveSig] int GetFrame(uint index, nint* frame);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapSource
    {
        [PreserveSig] int GetSize(uint* width, uint* height);
        [PreserveSig] int GetPixelFormat(Guid* pixelFormat);
        [PreserveSig] int GetResolution(double* dpiX, double* dpiY);
        [PreserveSig] int CopyPalette(nint palette);
        [PreserveSig] int CopyPixels(nint rect, uint stride, uint bufferSize, byte* buffer);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("3b16811b-6a43-4ec9-a813-3d930c13b940")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapFrameDecode : IWICBitmapSource
    {
        [PreserveSig] int GetMetadataQueryReader(nint* reader);
        [PreserveSig] int GetColorContexts(uint count, nint* contexts, uint* actualCount);
        [PreserveSig] int GetThumbnail(nint* thumbnail);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICBitmapScaler : IWICBitmapSource
    {
        [PreserveSig] int Initialize(nint source, uint width, uint height, int interpolationMode);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICFormatConverter : IWICBitmapSource
    {
        [PreserveSig]
        int Initialize(
            nint source,
            Guid* destinationFormat,
            int dither,
            nint palette,
            double alphaThresholdPercent,
            int paletteTranslate);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICColorContext
    {
        [PreserveSig] int InitializeFromFilename(char* filename);
        [PreserveSig] int InitializeFromMemory(byte* buffer, uint bufferSize);
        [PreserveSig] int InitializeFromExifColorSpace(uint value);
        [PreserveSig] int GetType(uint* type);
        [PreserveSig] int GetProfileBytes(uint bufferSize, byte* buffer, uint* actualSize);
        [PreserveSig] int GetExifColorSpace(uint* value);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
    [Guid("b66f034f-d0e2-40ab-b436-6de39e321a94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe partial interface IWICColorTransform : IWICBitmapSource
    {
        [PreserveSig]
        int Initialize(
            nint source,
            nint sourceColorContext,
            nint destinationColorContext,
            Guid* destinationFormat);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx")]
        internal static partial int CoInitializeEx(nint reserved, uint coInitialize);

        [LibraryImport("ole32.dll", EntryPoint = "CoUninitialize")]
        internal static partial void CoUninitialize();

        [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
        internal static unsafe partial int CoCreateInstance(
            Guid* classId,
            nint outer,
            uint classContext,
            Guid* interfaceId,
            nint* instance);
    }
}
