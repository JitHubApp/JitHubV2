using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

[CollectionDefinition("WIC raster memory qualification", DisableParallelization = true)]
public sealed class WicRasterMemoryQualificationCollection
{
    public const string CollectionName = "WIC raster memory qualification";
}

/// <summary>
/// Diagnostic-only WIC qualification. Sampled private bytes are evidence from
/// this test process, not a production memory guarantee or a WIC peak bound.
/// </summary>
[Collection(WicRasterMemoryQualificationCollection.CollectionName)]
public sealed class WicRasterDecodeMemoryQualificationTests(ITestOutputHelper output)
{
    private const int PreviewLongEdgePixels = 1024;
    private const int CandidateTileEdgePixels = 1024;
    private const int PrivateBytesSampleIntervalMilliseconds = 2;
    // This is a test-host guard, not a production decoder memory guarantee.
    // Keep a smaller guard on 32-bit processes, whose virtual-address headroom
    // is materially lower even for the same accepted source bytes.
    private static readonly long HarnessOnlyPrivateByteGrowthCeiling =
        (IntPtr.Size == 4 ? 256L : 512L) * 1024 * 1024;
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task BuiltInAcceptedStillCodecs_ReportSerialAndTwoWayDecodeEvidence()
    {
        List<DecodeFixture> fixtures = await CreateCodecFixturesAsync();
        Dictionary<string, DecodeProbeResult> serialResults = new(StringComparer.Ordinal);

        output.WriteLine(
            "WIC qualification coverage: generated BMP, GIF, ICO/PNG, JPEG, PNG, WebP. " +
            "UNQUALIFIED: AVIF is accepted by the source-header policy, but this " +
            "repository has no self-owned deterministic fixture or local encoder " +
            "for it, and the built-in Windows BitmapEncoder API cannot generate it. " +
            "Qualifying that codec requires an approved fixture or encoder; no " +
            "production memory guarantee is asserted.");
        output.WriteLine(
            $"Harness limits: source <= {RasterImageResourceBudget.MaxInputBytes} bytes, " +
            $"source <= {RasterImageResourceBudget.MaxDownsampleSourcePixels:N0} pixels, " +
            $"preview long edge <= {PreviewLongEdgePixels} px, " +
            $"candidate tile edge <= {CandidateTileEdgePixels} px, " +
            $"sample interval={PrivateBytesSampleIntervalMilliseconds} ms, " +
            $"diagnostic growth ceiling={HarnessOnlyPrivateByteGrowthCeiling:N0} bytes.");

        foreach (DecodeFixture fixture in fixtures)
        {
            ValidateFixture(fixture);
            (DecodeProbeResult result, PrivateBytesReport memory) = await MeasurePrivateBytesAsync(
                $"serial/{fixture.Name}",
                token => ProbeAsync(fixture, token));
            serialResults.Add(fixture.Name, result);
            WriteMemoryReport(memory);
            WriteProbeReport("serial", result);
        }

        // Keep concurrency bounded to exactly two decoders. Pairing differently
        // sized formats also exposes codec-specific behavior without turning
        // this diagnostic test into an unbounded image storm.
        for (int firstIndex = 0; firstIndex < fixtures.Count; firstIndex += 2)
        {
            DecodeFixture first = fixtures[firstIndex];
            DecodeFixture? second = firstIndex + 1 < fixtures.Count ? fixtures[firstIndex + 1] : null;
            string pairName = second is null ? first.Name : $"{first.Name}+{second.Name}";

            (DecodeProbeResult[] results, PrivateBytesReport memory) = await MeasurePrivateBytesAsync(
                $"two-way/{pairName}",
                token => ProbeBatchAsync(first, second, token));

            WriteMemoryReport(memory);
            foreach (DecodeProbeResult result in results)
            {
                WriteProbeReport("two-way", result);
                Assert.Equal(serialResults[result.Name], result);
            }
        }
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task JpegExifOrientations_ReportOrientedOutputAndCandidateTileDimensions()
    {
        const int sourceWidth = 1600;
        const int sourceHeight = 800;
        byte[] pixels = CreatePixels(sourceWidth, sourceHeight, limitedPalette: false);

        for (ushort orientation = 1; orientation <= 8; orientation++)
        {
            byte[] bytes = await EncodeAsync(
                BitmapEncoder.JpegEncoderId,
                sourceWidth,
                sourceHeight,
                pixels,
                orientation);
            var fixture = new DecodeFixture(
                $"JPEG-exif-{orientation}",
                "JPEG",
                bytes,
                sourceWidth,
                sourceHeight,
                orientation);
            ValidateFixture(fixture);
            Assert.Equal(orientation, await ReadExifOrientationAsync(bytes));

            DecodeProbeResult result = await ProbeAsync(fixture);
            output.WriteLine(
                $"WIC EXIF orientation={orientation}; source={sourceWidth}x{sourceHeight}; " +
                $"oriented={result.OrientedWidth}x{result.OrientedHeight}; " +
                $"preview={result.PreviewWidth}x{result.PreviewHeight}; " +
                $"bottom-right tile={result.TileX},{result.TileY} " +
                $"{result.TileWidth}x{result.TileHeight}; sourceBytes={bytes.Length}.");

            bool swapsAxes = orientation is >= 5 and <= 8;
            Assert.Equal(swapsAxes ? sourceHeight : sourceWidth, result.OrientedWidth);
            Assert.Equal(swapsAxes ? sourceWidth : sourceHeight, result.OrientedHeight);
            Assert.InRange(result.PreviewWidth, 1, PreviewLongEdgePixels);
            Assert.InRange(result.PreviewHeight, 1, PreviewLongEdgePixels);
            Assert.InRange(result.TileWidth, 1, CandidateTileEdgePixels);
            Assert.InRange(result.TileHeight, 1, CandidateTileEdgePixels);
        }
    }

    [Fact]
    [Trait("Category", "NativeMemoryQualification")]
    public async Task TwoDistinctNearInputLimitBmps_ReportConcurrentDecodeEvidence()
    {
        DecodeFixture first = await CreateEncodedFixtureAsync(
            "BMP-near-input-limit-first",
            "BMP",
            BitmapEncoder.BmpEncoderId,
            4092,
            2048,
            limitedPalette: false);
        DecodeFixture second = await CreateEncodedFixtureAsync(
            "BMP-near-input-limit-second",
            "BMP",
            BitmapEncoder.BmpEncoderId,
            4088,
            2048,
            limitedPalette: false);
        ValidateFixture(first);
        ValidateFixture(second);
        Assert.False(first.Bytes.AsSpan().SequenceEqual(second.Bytes));

        (DecodeProbeResult[] results, PrivateBytesReport memory) = await MeasurePrivateBytesAsync(
            "two-way/distinct-near-limit-BMPs",
            token => ProbeBatchAsync(first, second, token));
        Assert.Equal(2, results.Length);
        WriteMemoryReport(memory);
        foreach (DecodeProbeResult result in results)
            WriteProbeReport("two-way/distinct-near-limit-BMPs", result);
        output.WriteLine(
            $"Architecture={(IntPtr.Size == 4 ? "x86" : "64-bit")}; " +
            $"test-host-only sampled growth ceiling={HarnessOnlyPrivateByteGrowthCeiling:N0} bytes. " +
            "This does not establish a transient peak or production memory bound.");
    }

    private static async Task<ushort> ReadExifOrientationAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        var properties = await decoder.BitmapProperties.GetPropertiesAsync(
            ["System.Photo.Orientation"]);
        Assert.True(properties.ContainsKey("System.Photo.Orientation"));
        return Assert.IsType<ushort>(properties["System.Photo.Orientation"].Value);
    }

    private static async Task<List<DecodeFixture>> CreateCodecFixturesAsync()
    {
        var fixtures = new List<DecodeFixture>(6)
        {
            await CreateEncodedFixtureAsync(
                "PNG-oversized-static",
                "PNG",
                BitmapEncoder.PngEncoderId,
                2048,
                8193,
                limitedPalette: false),
            await CreateEncodedFixtureAsync(
                "JPEG-oversized-static-exif-6",
                "JPEG",
                BitmapEncoder.JpegEncoderId,
                2048,
                8193,
                limitedPalette: false,
                orientation: 6),
            await CreateEncodedFixtureAsync(
                "BMP-near-input-limit",
                "BMP",
                BitmapEncoder.BmpEncoderId,
                4092,
                2048,
                limitedPalette: false),
            await CreateEncodedFixtureAsync(
                "GIF-static",
                "GIF",
                BitmapEncoder.GifEncoderId,
                2048,
                4096,
                limitedPalette: true),
        };

        DecodeFixture iconPng = await CreateEncodedFixtureAsync(
            "ICO-payload",
            "PNG",
            BitmapEncoder.PngEncoderId,
            256,
            256,
            limitedPalette: false);
        fixtures.Add(new DecodeFixture(
            "ICO-static",
            "ICO",
            WrapPngAsIcon(iconPng.Bytes, 256, 256),
            256,
            256,
            ExifOrientation: null));
        using Stream webp = typeof(WicRasterDecodeMemoryQualificationTests).Assembly
            .GetManifestResourceStream("MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp")!;
        Assert.NotNull(webp);
        using var webpBytes = new MemoryStream();
        await webp.CopyToAsync(webpBytes);
        byte[] encodedWebp = webpBytes.ToArray();
        Assert.Equal(
            "6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(encodedWebp)));
        fixtures.Add(new DecodeFixture(
            "WebP-oversized-static",
            "WEBP",
            encodedWebp,
            2048,
            8193,
            ExifOrientation: null));
        return fixtures;
    }

    private static async Task<DecodeFixture> CreateEncodedFixtureAsync(
        string name,
        string format,
        Guid encoderId,
        int width,
        int height,
        bool limitedPalette,
        ushort? orientation = null)
    {
        byte[] pixels = CreatePixels(width, height, limitedPalette);
        byte[] bytes = await EncodeAsync(encoderId, width, height, pixels, orientation);
        return new DecodeFixture(name, format, bytes, width, height, orientation);
    }

    private static async Task<byte[]> EncodeAsync(
        Guid encoderId,
        int width,
        int height,
        byte[] pixels,
        ushort? orientation)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            checked((uint)width),
            checked((uint)height),
            96,
            96,
            pixels);

        if (orientation is { } value)
        {
            var properties = new BitmapPropertySet();
            properties.Add(
                "System.Photo.Orientation",
                new BitmapTypedValue(value, PropertyType.UInt16));
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
        }

        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint streamLength = checked((uint)stream.Size);
        await reader.LoadAsync(streamLength);
        byte[] bytes = new byte[checked((int)streamLength)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static byte[] CreatePixels(int width, int height, bool limitedPalette)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = checked((y * width + x) * 4);
                if (limitedPalette)
                {
                    bool alternate = ((x >> 5) + (y >> 5)) % 2 != 0;
                    pixels[offset] = alternate ? (byte)220 : (byte)28;
                    pixels[offset + 1] = ((x >> 6) & 1) == 0 ? (byte)45 : (byte)205;
                    pixels[offset + 2] = ((y >> 6) & 1) == 0 ? (byte)210 : (byte)52;
                }
                else
                {
                    pixels[offset] = (byte)((x * 13 + y * 3) & 0xFF);
                    pixels[offset + 1] = (byte)((x * 5 + y * 11) & 0xFF);
                    pixels[offset + 2] = (byte)(((x >> 5) ^ (y >> 4)) & 0xFF);
                }

                pixels[offset + 3] = byte.MaxValue;
            }
        }

        return pixels;
    }

    private static byte[] WrapPngAsIcon(byte[] png, int width, int height)
    {
        const int directoryLength = 6 + 16;
        byte[] icon = new byte[checked(directoryLength + png.Length)];
        icon[2] = 1; // One image in an ICO directory.
        icon[4] = 1; // One directory entry.
        icon[6] = width == 256 ? (byte)0 : checked((byte)width);
        icon[7] = height == 256 ? (byte)0 : checked((byte)height);
        icon[10] = 1; // Color planes.
        icon[12] = 32; // Bits per pixel.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            icon.AsSpan(14, 4),
            checked((uint)png.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            icon.AsSpan(18, 4),
            checked((uint)directoryLength));
        png.CopyTo(icon, directoryLength);
        return icon;
    }

    private static void ValidateFixture(DecodeFixture fixture)
    {
        Assert.InRange(fixture.Bytes.Length, 1, RasterImageResourceBudget.MaxInputBytes);
        Assert.InRange(fixture.Width, 1, RasterImageResourceBudget.MaxDimension);
        Assert.InRange(fixture.Height, 1, RasterImageResourceBudget.MaxDimension);
        Assert.InRange(
            checked((long)fixture.Width * fixture.Height),
            1,
            RasterImageResourceBudget.MaxDownsampleSourcePixels);

        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(fixture.Bytes);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview,
            $"Generated {fixture.Format} fixture failed the unchanged input/resource budget: {budget.Reason}");
        Assert.Equal(fixture.Format, budget.Format);
    }

    private static async Task<DecodeProbeResult> ProbeAsync(
        DecodeFixture fixture,
        CancellationToken cancellationToken = default)
    {
        // Match ImageBox's admitted-source path. The source array is retained
        // for the operation, without an additional native stream-sized copy.
        using var sourceStream = new MemoryStream(fixture.Bytes, writable: false);
        using IRandomAccessStream stream = sourceStream.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

        Assert.Equal((uint)fixture.Width, decoder.PixelWidth);
        Assert.Equal((uint)fixture.Height, decoder.PixelHeight);
        int orientedWidth = checked((int)decoder.OrientedPixelWidth);
        int orientedHeight = checked((int)decoder.OrientedPixelHeight);
        bool swapsAxes = orientedWidth != fixture.Width;

        int previewOrientedWidth;
        int previewOrientedHeight;
        double scale = System.Math.Min(
            1d,
            PreviewLongEdgePixels / (double)System.Math.Max(orientedWidth, orientedHeight));
        previewOrientedWidth = System.Math.Clamp(
            checked((int)System.Math.Ceiling(orientedWidth * scale)),
            1,
            orientedWidth);
        previewOrientedHeight = System.Math.Clamp(
            checked((int)System.Math.Ceiling(orientedHeight * scale)),
            1,
            orientedHeight);

        var previewTransform = new BitmapTransform
        {
            ScaledWidth = checked((uint)(swapsAxes ? previewOrientedHeight : previewOrientedWidth)),
            ScaledHeight = checked((uint)(swapsAxes ? previewOrientedWidth : previewOrientedHeight)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        int previewWidth;
        int previewHeight;
        using (SoftwareBitmap preview = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            previewTransform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken))
        {
            Assert.Equal(previewOrientedWidth, checked((int)preview.PixelWidth));
            Assert.Equal(previewOrientedHeight, checked((int)preview.PixelHeight));
            previewWidth = checked((int)preview.PixelWidth);
            previewHeight = checked((int)preview.PixelHeight);
        }

        int tileX = System.Math.Max(0, orientedWidth - CandidateTileEdgePixels);
        int tileY = System.Math.Max(0, orientedHeight - CandidateTileEdgePixels);
        int tileWidth = System.Math.Min(CandidateTileEdgePixels, orientedWidth - tileX);
        int tileHeight = System.Math.Min(CandidateTileEdgePixels, orientedHeight - tileY);
        var tileTransform = new BitmapTransform
        {
            ScaledWidth = decoder.PixelWidth,
            ScaledHeight = decoder.PixelHeight,
            Bounds = new BitmapBounds
            {
                X = checked((uint)tileX),
                Y = checked((uint)tileY),
                Width = checked((uint)tileWidth),
                Height = checked((uint)tileHeight),
            },
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using SoftwareBitmap candidateTile = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            tileTransform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        Assert.Equal(tileWidth, checked((int)candidateTile.PixelWidth));
        Assert.Equal(tileHeight, checked((int)candidateTile.PixelHeight));

        return new DecodeProbeResult(
            fixture.Name,
            fixture.Format,
            fixture.ExifOrientation,
            fixture.Width,
            fixture.Height,
            fixture.Bytes.Length,
            orientedWidth,
            orientedHeight,
            previewWidth,
            previewHeight,
            tileX,
            tileY,
            checked((int)candidateTile.PixelWidth),
            checked((int)candidateTile.PixelHeight));
    }

    private static async Task<DecodeProbeResult[]> ProbeBatchAsync(
        DecodeFixture first,
        DecodeFixture? second,
        CancellationToken cancellationToken)
    {
        if (second is null)
            return [await ProbeAsync(first, cancellationToken)];

        return await Task.WhenAll(
            ProbeAsync(first, cancellationToken),
            ProbeAsync(second, cancellationToken));
    }

    private static async Task<(T Result, PrivateBytesReport Report)> MeasurePrivateBytesAsync<T>(
        string label,
        Func<CancellationToken, Task<T>> workload)
    {
        using Process process = Process.GetCurrentProcess();
        // Refresh invalidates Process's cached process information. The final
        // workload sample can overlap a timer tick, so both must serialize the
        // refresh/read pair on this observer instance. Do not swallow genuine
        // sampling failures or change the measured memory ceiling.
        object sampleGate = new();
        process.Refresh();
        long baseline = process.PrivateMemorySize64;
        long peak = baseline;
        using var stopSampling = new CancellationTokenSource();
        using var decodeTimeout = new CancellationTokenSource(DecodeTimeout);
        void Sample()
        {
            long current;
            lock (sampleGate)
            {
                process.Refresh();
                current = process.PrivateMemorySize64;
            }
            long previous;
            do
            {
                previous = Interlocked.Read(ref peak);
                if (current <= previous)
                    return;
            }
            while (Interlocked.CompareExchange(ref peak, current, previous) != previous);
        }

        async Task SampleUntilStoppedAsync()
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromMilliseconds(PrivateBytesSampleIntervalMilliseconds));
            try
            {
                while (await timer.WaitForNextTickAsync(stopSampling.Token))
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
            T result = await workload(decodeTimeout.Token);
            Sample();
            long sampledPeak = Interlocked.Read(ref peak);
            long growth = System.Math.Max(0, sampledPeak - baseline);
            Assert.True(
                growth <= HarnessOnlyPrivateByteGrowthCeiling,
                $"{label} sampled private-byte growth {growth:N0} exceeded the " +
                $"test-host-only safety ceiling {HarnessOnlyPrivateByteGrowthCeiling:N0}.");
            return (result, new PrivateBytesReport(
                label,
                baseline,
                sampledPeak,
                growth,
                stopwatch.Elapsed,
                PrivateBytesSampleIntervalMilliseconds));
        }
        finally
        {
            stopSampling.Cancel();
            await sampler;
        }
    }

    private void WriteMemoryReport(PrivateBytesReport report) => output.WriteLine(
        $"WIC memory {report.Label}: baselinePrivateBytes={report.BaselinePrivateBytes:N0}; " +
        $"sampledPeakPrivateBytes={report.SampledPeakPrivateBytes:N0}; " +
        $"sampledGrowthBytes={report.SampledGrowthBytes:N0}; " +
        $"interval={report.SampleIntervalMilliseconds} ms; elapsed={report.Elapsed.TotalMilliseconds:F1} ms. " +
        "Peak is sampled and may miss transient allocations.");

    private void WriteProbeReport(string mode, DecodeProbeResult result) => output.WriteLine(
        $"WIC output {mode}/{result.Name}: format={result.Format}; " +
        $"exif={result.ExifOrientation?.ToString() ?? "none"}; " +
        $"source={result.SourceWidth}x{result.SourceHeight} " +
        $"({result.SourceBytes:N0} bytes); oriented={result.OrientedWidth}x{result.OrientedHeight}; " +
        $"preview={result.PreviewWidth}x{result.PreviewHeight}; " +
        $"bottomRightTile=({result.TileX},{result.TileY}) " +
        $"{result.TileWidth}x{result.TileHeight}.");

    private sealed record DecodeFixture(
        string Name,
        string Format,
        byte[] Bytes,
        int Width,
        int Height,
        ushort? ExifOrientation);

    private sealed record DecodeProbeResult(
        string Name,
        string Format,
        ushort? ExifOrientation,
        int SourceWidth,
        int SourceHeight,
        int SourceBytes,
        int OrientedWidth,
        int OrientedHeight,
        int PreviewWidth,
        int PreviewHeight,
        int TileX,
        int TileY,
        int TileWidth,
        int TileHeight);

    private sealed record PrivateBytesReport(
        string Label,
        long BaselinePrivateBytes,
        long SampledPeakPrivateBytes,
        long SampledGrowthBytes,
        TimeSpan Elapsed,
        int SampleIntervalMilliseconds);
}
