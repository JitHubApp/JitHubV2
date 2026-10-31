#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Deterministic managed-allocation diagnostic for combining host SVG
/// validation, title/description extraction, and intrinsic geometry parsing.
/// Its timing output is observational only, not a release-latency claim.
/// </summary>
public sealed class SvgAdmissionMetadataPerformanceTests(ITestOutputHelper output)
{
    private const int UniqueImageCount = 182;
    private const int Iterations = 8;

    [Fact]
    public void CombinedValidationMetadataReducesManagedAllocationsOnKatexShapedInput()
    {
        byte[] fixture = SvgStaticSnapshot.Create(
            CreateKatexShapedFixture(),
            CancellationToken.None);

        // Warm parser/JIT/code paths symmetrically using the exact same source.
        for (int index = 0; index < 2; index++)
        {
            RunLegacyPipeline(fixture);
            RunCombinedPipeline(fixture);
        }

        (long legacyBytes, TimeSpan legacyElapsed) = Measure(() => RunLegacyPipeline(fixture));
        (long combinedBytes, TimeSpan combinedElapsed) = Measure(() => RunCombinedPipeline(fixture));
        GC.KeepAlive(fixture);

        double allocationRatio = combinedBytes / (double)legacyBytes;
        output.WriteLine(
            $"SVG admission metadata diagnostic: bytes={fixture.Length}; uniquePngs={UniqueImageCount}; iterations={Iterations}; " +
            $"legacyAllocated={legacyBytes}; combinedAllocated={combinedBytes}; ratio={allocationRatio:F3}; " +
            $"legacyElapsedMs={legacyElapsed.TotalMilliseconds:F2}; combinedElapsedMs={combinedElapsed.TotalMilliseconds:F2}. " +
            "Elapsed time is diagnostic only and is not an Edge-relative release benchmark.");

        Assert.True(legacyBytes > 0);
        Assert.True(
            combinedBytes <= legacyBytes * 0.70,
            $"Combined validation/metadata allocated {combinedBytes} bytes versus {legacyBytes} bytes " +
            $"for the retained three-helper pipeline (ratio {allocationRatio:F3}); the target is at least 30% lower.");
    }

    private static (long AllocatedBytes, TimeSpan Elapsed) Measure(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < Iterations; index++)
            action();
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (allocated, stopwatch.Elapsed);
    }

    private static void RunLegacyPipeline(byte[] source)
    {
        SvgResourceBudgetResult validation = SvgResourceBudget.Validate(source, CancellationToken.None);
        if (!validation.Accepted)
            throw new InvalidOperationException($"Legacy fixture was rejected: {validation.Reason}");

        SvgTitleExtractor.Metadata text = SvgTitleExtractor.Extract(source);
        (double width, double height) = SvgIntrinsics.TryExtractIntrinsicSize(source);
        if (text.Title != "KaTeX-shaped asset" || width != 890 || height != 158)
            throw new InvalidOperationException("Legacy helpers returned unexpected fixture metadata.");

        GC.KeepAlive(text);
        GC.KeepAlive(width);
        GC.KeepAlive(height);
        GC.KeepAlive(validation);
    }

    private static void RunCombinedPipeline(byte[] source)
    {
        SvgResourceBudgetResult validation = SvgResourceBudget.ValidateWithMetadata(
            source,
            CancellationToken.None,
            out MarkdownSvgSourceInfo? metadataInfo);
        SvgSourceMetadata metadata = metadataInfo?.Metadata ?? default;
        if (!validation.Accepted)
            throw new InvalidOperationException($"Combined fixture was rejected: {validation.Reason}");
        if (metadata.Title != "KaTeX-shaped asset" ||
            metadata.Geometry.IntrinsicWidth != 890 ||
            metadata.Geometry.IntrinsicHeight != 158)
        {
            throw new InvalidOperationException("Combined helper returned unexpected fixture metadata.");
        }

        GC.KeepAlive(metadata);
        GC.KeepAlive(validation);
    }

    private static byte[] CreateKatexShapedFixture()
    {
        StringBuilder source = new(48_000);
        HashSet<string> uniqueImagePayloads = new(StringComparer.Ordinal);
        source.Append("<svg xmlns='http://www.w3.org/2000/svg' width='890' height='158' viewBox='0 0 890 158'>")
            .Append("<title>KaTeX-shaped asset</title><defs><clipPath id='clip'><rect width='890' height='158'/></clipPath></defs>")
            .Append("<g clip-path='url(#clip)'>");

        for (int index = 0; index < UniqueImageCount; index++)
        {
            byte[] png = CreateOnePixelPng(
                (byte)((index * 31) % 256),
                (byte)((index * 67) % 256),
                (byte)((index * 97) % 256),
                metadataSeed: index);
            string dataUri = Convert.ToBase64String(png);
            if (!uniqueImagePayloads.Add(dataUri))
                throw new InvalidOperationException($"Fixture PNG payload {index} was not unique.");
            int x = index % 22;
            int y = index / 22;
            source.Append("<image x='").Append(x).Append("' y='").Append(y)
                .Append("' width='1' height='1' href='data:image/png;base64,")
                .Append(dataUri).Append("'/>");
        }

        if (uniqueImagePayloads.Count != UniqueImageCount)
            throw new InvalidOperationException($"Fixture produced {uniqueImagePayloads.Count} unique PNG payloads.");
        source.Append("</g></svg>");
        return Encoding.UTF8.GetBytes(source.ToString());
    }

    private static byte[] CreateOnePixelPng(byte red, byte green, byte blue, int metadataSeed)
    {
        using MemoryStream output = new();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        byte[] metadata = Encoding.ASCII.GetBytes(
            "Comment\0fixture-" + metadataSeed.ToString("D3", CultureInfo.InvariantCulture));
        WritePngChunk(output, "tEXt", metadata);

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write([0, red, green, blue, byte.MaxValue]);
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream destination, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        destination.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);

        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcInput));
        destination.Write(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
#endif
