using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MarkdownRenderer.Layout.Boxes;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SvgSourceMetadataPerformanceTests(ITestOutputHelper output)
{
    private const int ImagePayloadCount = 182;
    private const int MeasurementIterations = 12;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void OnePassMetadataExtractionReducesAllocationsForKatexShapedImageSvg()
    {
        Fixture fixture = CreateKatexShapedFixture();
        Assert.Equal(ImagePayloadCount, fixture.UniquePayloadCount);
        Assert.Equal(890, fixture.Width);
        Assert.Equal(158, fixture.Height);

        // Warm each real implementation on the exact same immutable bytes so
        // regex/JIT startup is excluded from the comparison.
        SvgTitleExtractor.Metadata warmTitle = default;
        (double Width, double Height) warmSize = default;
        SvgSourceMetadata warmMetadata = default;
        for (int index = 0; index < 3; index++)
        {
            warmTitle = SvgTitleExtractor.Extract(fixture.Bytes);
            warmSize = SvgIntrinsics.TryExtractIntrinsicSize(fixture.Bytes);
            warmMetadata = SvgSourceMetadata.Extract(fixture.Bytes, CancellationToken.None);
        }

        Assert.Equal("KaTeX fixture title", warmTitle.Title);
        Assert.Equal("182 unique embedded PNGs", warmTitle.Desc);
        Assert.Equal((890d, 158d), warmSize);
        Assert.Equal(warmTitle.Title, warmMetadata.Title);
        Assert.Equal(warmTitle.Desc, warmMetadata.Desc);
        Assert.Equal(warmSize.Width, warmMetadata.Geometry.IntrinsicWidth);
        Assert.Equal(warmSize.Height, warmMetadata.Geometry.IntrinsicHeight);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var legacyTimer = new Stopwatch();
        SvgTitleExtractor.Metadata legacyTitle = default;
        (double Width, double Height) legacySize = default;
        long legacyBefore = GC.GetAllocatedBytesForCurrentThread();
        legacyTimer.Start();
        for (int index = 0; index < MeasurementIterations; index++)
        {
            legacyTitle = SvgTitleExtractor.Extract(fixture.Bytes);
            legacySize = SvgIntrinsics.TryExtractIntrinsicSize(fixture.Bytes);
        }
        legacyTimer.Stop();
        long legacyAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - legacyBefore;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var onePassTimer = new Stopwatch();
        SvgSourceMetadata onePass = default;
        long onePassBefore = GC.GetAllocatedBytesForCurrentThread();
        onePassTimer.Start();
        for (int index = 0; index < MeasurementIterations; index++)
            onePass = SvgSourceMetadata.Extract(fixture.Bytes, CancellationToken.None);
        onePassTimer.Stop();
        long onePassAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - onePassBefore;

        // Retain final values through the measurements; timing is reported as
        // diagnostic evidence only, never as a release/Edge latency gate.
        GC.KeepAlive(legacyTitle);
        GC.KeepAlive(legacySize);
        GC.KeepAlive(onePass);
        GC.KeepAlive(warmMetadata);
        GC.KeepAlive(fixture.Bytes);

        Assert.Equal(legacyTitle.Title, onePass.Title);
        Assert.Equal(legacyTitle.Desc, onePass.Desc);
        Assert.Equal(legacySize.Width, onePass.Geometry.IntrinsicWidth);
        Assert.Equal(legacySize.Height, onePass.Geometry.IntrinsicHeight);

        double allocationRatio = (double)onePassAllocatedBytes / legacyAllocatedBytes;
        _output.WriteLine(
            $"SVG metadata diagnostic only: bytes={fixture.Bytes.Length}, payloads={fixture.UniquePayloadCount}, " +
            $"iterations={MeasurementIterations}, legacyAllocatedBytes={legacyAllocatedBytes}, " +
            $"onePassAllocatedBytes={onePassAllocatedBytes}, onePassToLegacyRatio={allocationRatio:F3}, " +
            $"legacyElapsedMs={legacyTimer.Elapsed.TotalMilliseconds:F2}, " +
            $"onePassElapsedMs={onePassTimer.Elapsed.TotalMilliseconds:F2}.");

        // Require a material allocation reduction while leaving wall-clock
        // timing informational; machine load must not make this test flaky.
        Assert.True(
            onePassAllocatedBytes <= legacyAllocatedBytes * 0.70d,
            $"Expected at least a 30% managed-allocation reduction; one-pass used {onePassAllocatedBytes} bytes " +
            $"versus {legacyAllocatedBytes} bytes in the retained title+geometry helpers.");
    }

    private static Fixture CreateKatexShapedFixture()
    {
        var builder = new StringBuilder(32 * 1024);
        builder.Append("<svg xmlns='").Append(SvgNamespace)
            .Append("' width='890' height='158' viewBox='0 0 890 158' role='img'>")
            .Append("<title>KaTeX fixture title</title>")
            .Append("<desc>182 unique embedded PNGs</desc>")
            .Append("<defs><path id='glyph' d='M0 0h4v10h-4z'/></defs><g aria-hidden='true'>");

        var uniquePayloads = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < ImagePayloadCount; index++)
        {
            int x = index % 890;
            int y = (index * 7) % 158;
            string payload = Convert.ToBase64String(CreateOnePixelPng(index));
            if (!uniquePayloads.Add(payload))
                throw new InvalidOperationException("Fixture PNG payloads must be unique.");

            builder.Append("<g transform='translate(").Append(x).Append(' ').Append(y).Append(")'>")
                .Append("<use href='#glyph'/><image width='1' height='1' href='data:image/png;base64,")
                .Append(payload)
                .Append("'/></g>");
        }

        builder.Append("</g></svg>");
        return new Fixture(Encoding.UTF8.GetBytes(builder.ToString()), uniquePayloads.Count, 890, 158);
    }

    private static byte[] CreateOnePixelPng(int seed)
    {
        using var png = new MemoryStream(96);
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), 1);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(png, "IHDR", header);

        byte[] scanline = [
            0,
            (byte)seed,
            (byte)(seed >> 8),
            (byte)(seed >> 16),
            255,
        ];
        using var zlib = new MemoryStream(32);
        zlib.WriteByte(0x78);
        zlib.WriteByte(0x01);
        zlib.WriteByte(0x01); // Final, uncompressed DEFLATE block with byte alignment.
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(length[..2], (ushort)scanline.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(length.Slice(2, 2), unchecked((ushort)~scanline.Length));
        zlib.Write(length);
        zlib.Write(scanline);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, ComputeAdler32(scanline));
        zlib.Write(adler);
        WritePngChunk(png, "IDAT", zlib.ToArray());
        WritePngChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes)
            crc = UpdateCrc32(crc, value);
        foreach (byte value in data)
            crc = UpdateCrc32(crc, value);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }

    private static uint ComputeAdler32(ReadOnlySpan<byte> data)
    {
        uint first = 1;
        uint second = 0;
        foreach (byte value in data)
        {
            first = (first + value) % 65521;
            second = (second + first) % 65521;
        }
        return (second << 16) | first;
    }

    private readonly record struct Fixture(byte[] Bytes, int UniquePayloadCount, int Width, int Height);
}
