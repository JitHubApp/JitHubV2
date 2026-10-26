using System.Buffers.Binary;
using System.Diagnostics;
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
/// Diagnostic-only comparison of direct await versus explicit close of completed operations
/// for a bounded WebP preview. Private-byte sampling is not a codec peak bound.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class WicWebpOperationLifetimeQualificationTests(ITestOutputHelper output)
{
    private const int OutputWidth = 256;
    private const int OutputHeight = 1025;
    private const int PrivateBytesSampleIntervalMilliseconds = 2;

    [Trait("Requires", "WicWebpDecoder")]
    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task OversizedStaticWebp_ReportsTerminalOperationCloseComparison()
    {
        byte[] source = await ReadPinnedWebpFixtureAsync();
        Assert.Equal("WEBP", RasterImageResourceBudget.Validate(source).Format);
        Assert.False(ContainsRiffChunk(source, "ICCP"));

        DecodeMeasurement? directAwait = null;
        DecodeMeasurement? closeAfterTerminal = null;
        try
        {
            directAwait = await DecodeAsync(
                source,
                closeCompletedOperations: false,
                label: "direct-await");
            closeAfterTerminal = await DecodeAsync(
                source,
                closeCompletedOperations: true,
                label: "close-after-terminal");

            WriteMeasurement(directAwait);
            WriteMeasurement(closeAfterTerminal);
            Assert.Equal(OutputWidth, directAwait.Width);
            Assert.Equal(OutputHeight, directAwait.Height);
            Assert.Equal(OutputWidth, closeAfterTerminal.Width);
            Assert.Equal(OutputHeight, closeAfterTerminal.Height);

            Assert.True(
                directAwait.Pixels.AsSpan().SequenceEqual(closeAfterTerminal.Pixels),
                "Closing completed WinRT operations must not change decoded pixels.");
            output.WriteLine(
                $"WIC WebP operation-lifetime comparison: fixture={source.Length:N0} bytes; " +
                $"source=2048x8193; output={OutputWidth}x{OutputHeight}; " +
                $"outputBytes={directAwait.Pixels.Length:N0}; exactPixelsMatch=true; " +
                "both decodes use ColorManageToSRgb, RespectExifOrientation, and identical Fant-scaled dimensions. " +
                "The fixture contains VP8L and no ICCP profile. This is diagnostic evidence only, not a " +
                "production codec-memory bound or release-latency result. The second baseline includes the " +
                "first output copy retained for byte comparison.");
        }
        finally
        {
            if (directAwait is not null)
                Array.Clear(directAwait.Pixels);
            if (closeAfterTerminal is not null)
                Array.Clear(closeAfterTerminal.Pixels);
            directAwait = null;
            closeAfterTerminal = null;
            source = [];

            ForceFullCollection();
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            output.WriteLine(
                $"WIC WebP post-test-only-GC report: privateBytesAfterDecodeObjectsAndOutputCopiesReleased=" +
                $"{process.PrivateMemorySize64:N0}; full collection/finalizers/full collection completed. " +
                "This process-wide value is diagnostic only and is not a WIC or native-memory guarantee.");
        }
    }

    private static async Task<byte[]> ReadPinnedWebpFixtureAsync()
    {
        using Stream fixture = typeof(WicWebpOperationLifetimeQualificationTests).Assembly
            .GetManifestResourceStream("MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp")
            ?? throw new InvalidOperationException("The pinned oversized static WebP fixture is missing.");
        using var bytes = new MemoryStream();
        await fixture.CopyToAsync(bytes).ConfigureAwait(false);
        byte[] source = bytes.ToArray();

        Assert.Equal(51_112, source.Length);
        Assert.Equal(
            "6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85",
            Convert.ToHexString(SHA256.HashData(source)));
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview, budget.Reason);
        Assert.Equal(2048, budget.Width);
        Assert.Equal(8193, budget.Height);
        Assert.False(ContainsRiffChunk(source, "ICCP"));
        return source;
    }

    private static async Task<DecodeMeasurement> DecodeAsync(
        byte[] source,
        bool closeCompletedOperations,
        string label)
    {
        // Isolate each sample from managed garbage left by the preceding decode.
        ForceFullCollection();

        using Process process = Process.GetCurrentProcess();
        // Process.Refresh invalidates shared cached data; timer and final
        // samples must read it as one operation on this observer instance.
        object sampleGate = new();
        process.Refresh();
        long baselinePrivateBytes = process.PrivateMemorySize64;
        long sampledPeakPrivateBytes = baselinePrivateBytes;
        long logicalCleanupPrivateBytes;
        int width;
        int height;
        TimeSpan elapsed;
        byte[] pixels;

        void SamplePrivateBytes()
        {
            long current;
            lock (sampleGate)
            {
                process.Refresh();
                current = process.PrivateMemorySize64;
            }
            long observed;
            do
            {
                observed = Interlocked.Read(ref sampledPeakPrivateBytes);
                if (current <= observed)
                    return;
            }
            while (Interlocked.CompareExchange(ref sampledPeakPrivateBytes, current, observed) != observed);
        }

        async Task SampleUntilStoppedAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromMilliseconds(PrivateBytesSampleIntervalMilliseconds));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    SamplePrivateBytes();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }

        using (var memoryStream = new MemoryStream(source, writable: false))
        {
            using IRandomAccessStream stream = memoryStream.AsRandomAccessStream();
            IAsyncOperation<BitmapDecoder> decoderOperation = BitmapDecoder.CreateAsync(stream);
            BitmapDecoder decoder = await decoderOperation.AsTask().ConfigureAwait(false);
            if (closeCompletedOperations)
                decoderOperation.Close();

            Assert.Equal(2048u, decoder.PixelWidth);
            Assert.Equal(8193u, decoder.PixelHeight);

            var transform = new BitmapTransform
            {
                ScaledWidth = OutputWidth,
                ScaledHeight = OutputHeight,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            using var stopSampling = new CancellationTokenSource();
            Task sampler = SampleUntilStoppedAsync(stopSampling.Token);
            var stopwatch = Stopwatch.StartNew();
            SoftwareBitmap? decoded = null;
            IAsyncOperation<SoftwareBitmap>? decodeOperation = null;
            bool decodeCompleted = false;
            bool decodeOperationClosed = false;
            try
            {
                decodeOperation = decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                decoded = await decodeOperation.AsTask().ConfigureAwait(false);
                decodeCompleted = true;
                if (closeCompletedOperations)
                {
                    decodeOperation.Close();
                    decodeOperationClosed = true;
                }
                SamplePrivateBytes();
                stopwatch.Stop();

                width = checked((int)decoded.PixelWidth);
                height = checked((int)decoded.PixelHeight);
                Assert.Equal(OutputWidth, width);
                Assert.Equal(OutputHeight, height);
                pixels = CopyPremultipliedBgraBytes(decoded);
            }
            finally
            {
                // Stop sampling before cleanup and explicit test-only GC.
                stopSampling.Cancel();
                await sampler.ConfigureAwait(false);
                if (closeCompletedOperations && decodeCompleted && !decodeOperationClosed)
                    decodeOperation!.Close();
                decoded?.Dispose();
            }

            elapsed = stopwatch.Elapsed;
        }
        process.Refresh();
        logicalCleanupPrivateBytes = process.PrivateMemorySize64;
        return new DecodeMeasurement(
            label,
            width,
            height,
            baselinePrivateBytes,
            Interlocked.Read(ref sampledPeakPrivateBytes),
            logicalCleanupPrivateBytes,
            elapsed,
            pixels);
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

    private static bool ContainsRiffChunk(byte[] bytes, string expectedFourCc)
    {
        Assert.True(bytes.Length >= 12);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));

        int offset = 12;
        while (offset < bytes.Length)
        {
            Assert.True(offset <= bytes.Length - 8, "The WebP fixture has a truncated RIFF chunk header.");
            string fourCc = Encoding.ASCII.GetString(bytes, offset, 4);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            long nextOffset = checked((long)offset + 8 + payloadLength + (payloadLength & 1));
            Assert.True(nextOffset <= bytes.Length, $"WebP chunk {fourCc} exceeds the fixture boundary.");
            if (string.Equals(fourCc, expectedFourCc, StringComparison.Ordinal))
                return true;
            offset = checked((int)nextOffset);
        }

        Assert.Equal(bytes.Length, offset);
        return false;
    }

    private void WriteMeasurement(DecodeMeasurement measurement)
    {
        long growth = System.Math.Max(
            0,
            measurement.SampledPeakPrivateBytes - measurement.BaselinePrivateBytes);
        output.WriteLine(
            $"WIC WebP decode {measurement.Label}: elapsed={measurement.Elapsed.TotalMilliseconds:F1} ms; " +
            $"baselinePrivateBytes={measurement.BaselinePrivateBytes:N0}; " +
            $"sampledPeakPrivateBytes={measurement.SampledPeakPrivateBytes:N0}; " +
            $"logicalCleanupPrivateBytes={measurement.LogicalCleanupPrivateBytes:N0}; " +
            $"sampledGrowthBytes={growth:N0}; sampleInterval={PrivateBytesSampleIntervalMilliseconds} ms; " +
            $"output={measurement.Width}x{measurement.Height} BGRA8-premultiplied " +
            $"({measurement.Pixels.Length:N0} bytes). Sampling may miss transient codec/native allocations; " +
            "elapsed time and process private bytes are diagnostic only.");
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record DecodeMeasurement(
        string Label,
        int Width,
        int Height,
        long BaselinePrivateBytes,
        long SampledPeakPrivateBytes,
        long LogicalCleanupPrivateBytes,
        TimeSpan Elapsed,
        byte[] Pixels);
}
