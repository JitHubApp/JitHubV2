using System.Buffers.Binary;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class PrefetchedRasterMetadataTests
{
    [Theory]
    [InlineData(1, false, 640, 360)]
    [InlineData(2, true, 640, 360)]
    [InlineData(3, false, 640, 360)]
    [InlineData(4, true, 640, 360)]
    [InlineData(5, false, 360, 640)]
    [InlineData(6, false, 360, 640)]
    [InlineData(7, true, 360, 640)]
    [InlineData(8, true, 360, 640)]
    public void JpegExifOrientationProducesDisplayAxes(
        int orientation,
        bool bigEndian,
        int expectedWidth,
        int expectedHeight)
    {
        byte[] bytes = CreateJpeg(640, 360, [CreateTiff(orientation, bigEndian)]);

        Assert.True(RasterIntrinsicMetadata.TryRead(bytes, out RasterIntrinsicMetadata result));
        Assert.Equal(new RasterIntrinsicMetadata(expectedWidth, expectedHeight), result);
    }

    [Fact]
    public void PngAndWebPExifOrientationProducesDisplayAxes()
    {
        byte[] tiff = CreateTiff(orientation: 7, bigEndian: false);

        Assert.True(RasterIntrinsicMetadata.TryRead(CreatePng(800, 450, [tiff]), out var png));
        Assert.Equal(new RasterIntrinsicMetadata(450, 800), png);

        Assert.True(RasterIntrinsicMetadata.TryRead(CreateWebP(800, 450, [tiff]), out var webp));
        Assert.Equal(new RasterIntrinsicMetadata(450, 800), webp);
    }

    [Fact]
    public void RasterWithoutOrientationUsesValidatedAxesAndGifUsesLogicalCanvas()
    {
        Assert.True(RasterIntrinsicMetadata.TryRead(CreateBmp(320, 180), out var bmp));
        Assert.Equal(new RasterIntrinsicMetadata(320, 180), bmp);

        Assert.True(RasterIntrinsicMetadata.TryRead(CreateGif(640, 480, 24, 16), out var gif));
        Assert.Equal(new RasterIntrinsicMetadata(640, 480), gif);
    }

    [Fact]
    public void AmbiguousAndUnsupportedContainerMetadataProducesNoHint()
    {
        Assert.False(RasterIntrinsicMetadata.TryRead(CreateWebP(400, 200, [], animated: true), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(CreatePng(400, 200, [], includeEnd: false), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(CreateAvif(400, 200), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(new byte[32], out _));
    }

    [Fact]
    public void MalformedOrDuplicateExifNeverProducesAProvisionalHint()
    {
        byte[] invalidValue = CreateTiff(orientation: 9, bigEndian: false);
        byte[] duplicateTags = CreateTiffEntries([6, 8], bigEndian: false);
        byte[] invalidOffset = CreateTiff(orientation: 6, bigEndian: false, ifdOffset: uint.MaxValue);
        byte[] invalidType = CreateTiff(orientation: 6, bigEndian: false);
        WriteUInt16(invalidType, 12, 4, bigEndian: false);
        byte[] invalidNextIfd = CreateTiff(orientation: 6, bigEndian: false);
        WriteUInt32(invalidNextIfd, 22, uint.MaxValue, bigEndian: false);

        Assert.False(RasterIntrinsicMetadata.TryRead(CreateJpeg(640, 360, [invalidValue]), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(CreateJpeg(640, 360, [duplicateTags]), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(CreateJpeg(640, 360, [invalidOffset]), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(CreateJpeg(640, 360, [invalidType]), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(CreateJpeg(640, 360, [invalidNextIfd]), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(
            CreateJpeg(640, 360, [CreateTiff(6, false), CreateTiff(6, false)]), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(
            CreatePng(640, 360, [CreateTiff(6, false), CreateTiff(6, false)]), out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(
            CreateWebP(640, 360, [CreateTiff(6, false), CreateTiff(6, false)]), out _));
    }

    [Fact]
    public void TruncatedExifAndRasterDimensionBombAreRejectedForHints()
    {
        byte[] truncatedTiff = CreateTiff(orientation: 6, bigEndian: false)[..^3];
        byte[] truncatedExif = CreateJpeg(640, 360, [truncatedTiff]);
        byte[] bomb = CreatePng(RasterImageResourceBudget.MaxDimension + 1, 1, []);

        Assert.False(RasterIntrinsicMetadata.TryRead(truncatedExif, out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(bomb, out _));
        Assert.False(RasterIntrinsicMetadata.TryRead(new byte[RasterImageResourceBudget.MaxInputBytes + 1], out _));
    }

    [Fact]
    public void MetadataParserPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RasterIntrinsicMetadata.TryRead(CreatePng(640, 360, []), out _, cancellation.Token));
    }

    private static byte[] CreateTiff(int orientation, bool bigEndian, uint ifdOffset = 8) =>
        CreateTiffEntries([orientation], bigEndian, ifdOffset);

    private static byte[] CreateTiffEntries(int[] orientations, bool bigEndian, uint ifdOffset = 8)
    {
        byte[] bytes = new byte[checked(8 + 2 + orientations.Length * 12 + 4)];
        if (bigEndian)
            "MM"u8.CopyTo(bytes);
        else
            "II"u8.CopyTo(bytes);
        WriteUInt16(bytes, 2, 42, bigEndian);
        WriteUInt32(bytes, 4, ifdOffset, bigEndian);
        if (ifdOffset != 8)
            return bytes;

        WriteUInt16(bytes, 8, checked((ushort)orientations.Length), bigEndian);
        for (int i = 0; i < orientations.Length; i++)
        {
            int entry = 10 + i * 12;
            WriteUInt16(bytes, entry, 0x0112, bigEndian);
            WriteUInt16(bytes, entry + 2, 3, bigEndian);
            WriteUInt32(bytes, entry + 4, 1, bigEndian);
            WriteUInt16(bytes, entry + 8, checked((ushort)orientations[i]), bigEndian);
        }
        return bytes;
    }

    private static byte[] CreateJpeg(int width, int height, byte[][] exifBlocks)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0xFF, 0xD8 });
        foreach (byte[] tiff in exifBlocks)
        {
            byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        stream.Write(new byte[] { 0xFF, 0xE1 });
            WriteUInt16BigEndian(stream, checked((ushort)(payload.Length + 2)));
            stream.Write(payload);
        }

        // Baseline SOF0 with three component descriptors.
        stream.Write(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
        WriteUInt16BigEndian(stream, checked((ushort)height));
        WriteUInt16BigEndian(stream, checked((ushort)width));
        stream.Write(new byte[] { 0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00 });
        // The metadata scanner stops at SOS; no entropy-coded payload is needed.
        stream.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00 });
        return stream.ToArray();
    }

    private static byte[] CreatePng(
        int width,
        int height,
        byte[][] exifBlocks,
        bool includeEnd = true)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), checked((uint)height));
        ihdr[8] = 8;
        ihdr[9] = 6;
        WritePngChunk(stream, "IHDR"u8, ihdr);
        foreach (byte[] exif in exifBlocks)
            WritePngChunk(stream, "eXIf"u8, exif);
        WritePngChunk(stream, "IDAT"u8, []);
        if (includeEnd)
            WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static byte[] CreateWebP(int width, int height, byte[][] exifBlocks, bool animated = false)
    {
        using var body = new MemoryStream();
        byte[] extendedHeader = new byte[10];
        if (animated)
            extendedHeader[0] = 0x02;
        WriteUInt24LittleEndian(extendedHeader.AsSpan(4), checked((uint)(width - 1)));
        WriteUInt24LittleEndian(extendedHeader.AsSpan(7), checked((uint)(height - 1)));
        WriteWebPChunk(body, "VP8X"u8, extendedHeader);
        WriteWebPChunk(body, "VP8 "u8, new byte[10]);
        foreach (byte[] tiff in exifBlocks)
        {
            byte[] exif = [.. "Exif\0\0"u8.ToArray(), .. tiff];
            WriteWebPChunk(body, "EXIF"u8, exif);
        }
        if (animated)
            WriteWebPChunk(body, "ANMF"u8, new byte[16]);

        byte[] chunks = body.ToArray();
        byte[] bytes = new byte[12 + chunks.Length];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)(bytes.Length - 8)));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        chunks.CopyTo(bytes, 12);
        return bytes;
    }

    private static byte[] CreateBmp(int width, int height)
    {
        byte[] bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        return bytes;
    }

    private static byte[] CreateGif(int canvasWidth, int canvasHeight, int frameWidth, int frameHeight)
    {
        byte[] bytes = new byte[28];
        "GIF89a"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), checked((ushort)canvasWidth));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), checked((ushort)canvasHeight));
        bytes[13] = 0x2C;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), checked((ushort)frameWidth));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), checked((ushort)frameHeight));
        bytes[23] = 2; // LZW minimum code size
        bytes[24] = 1;
        bytes[25] = 0x44;
        bytes[26] = 0;
        bytes[27] = 0x3B;
        return bytes;
    }

    private static byte[] CreateAvif(int width, int height)
    {
        byte[] bytes = new byte[72];
        WriteBmffBox(bytes, 0, 24, "ftyp"u8);
        "avif"u8.CopyTo(bytes.AsSpan(8));
        "mif1"u8.CopyTo(bytes.AsSpan(16));
        "avif"u8.CopyTo(bytes.AsSpan(20));
        WriteBmffBox(bytes, 24, 48, "meta"u8);
        WriteBmffBox(bytes, 36, 36, "iprp"u8);
        WriteBmffBox(bytes, 44, 28, "ipco"u8);
        WriteBmffBox(bytes, 52, 20, "ispe"u8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(64), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(68), checked((uint)height));
        return bytes;
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
        type.CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(payload);
        stream.Write(new byte[4]); // CRC is outside this bounded geometry parser's trust.
    }

    private static void WriteWebPChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        type.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
            stream.WriteByte(0);
    }

    private static void WriteBmffBox(byte[] bytes, int offset, int length, ReadOnlySpan<byte> type)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), checked((uint)length));
        type.CopyTo(bytes.AsSpan(offset + 4));
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    private static void WriteUInt24LittleEndian(Span<byte> bytes, uint value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
    }
}
