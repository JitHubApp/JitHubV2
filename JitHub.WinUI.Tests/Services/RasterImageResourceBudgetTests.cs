using System.Buffers.Binary;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RasterImageResourceBudgetTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_AcceptsBoundedPngHeader()
    {
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(CreatePng(1024, 768));

        Assert.True(result.Accepted);
        Assert.Equal("PNG", result.Format);
        Assert.Equal(1024 * 768, result.TotalPixels);
        Assert.Equal(3L * 1024 * 1024, result.DecodedBytes);
    }

    [Theory]
    [Trait("Category", "ReleaseSecurity")]
    [InlineData(100_000, 1)]
    [InlineData(8192, 8192)]
    public void Validate_RejectsTinyCompressedPngWithHostileDimensions(int width, int height)
    {
        byte[] compressedBombHeader = CreatePng(width, height);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(compressedBombHeader);

        Assert.True(compressedBombHeader.Length < 64);
        Assert.False(result.Accepted);
        Assert.Contains("budget", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_RejectsApngWhoseFramesExceedDecodedMemoryBudget()
    {
        byte[] hostileApng = CreatePng(4096, 4096, frameCount: 2);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(hostileApng);

        Assert.False(result.Accepted);
        Assert.Contains("decoded-memory", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_RejectsGifFrameBomb()
    {
        byte[] frameBomb = CreateGif(width: 1, height: 1, frameCount: RasterImageResourceBudget.MaxFrameCount + 1);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(frameBomb);

        Assert.False(result.Accepted);
        Assert.Contains("frame count", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_RejectsAnimatedWebPFrameBomb()
    {
        byte[] frameBomb = CreateAnimatedWebP(32, 32, RasterImageResourceBudget.MaxFrameCount + 1);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(frameBomb);

        Assert.False(result.Accepted);
        Assert.Contains("frame count", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_RecognizesLossyWebPByteSignature()
    {
        byte[] webP = CreateLossyWebP(640, 480);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(webP);

        Assert.True(result.Accepted);
        Assert.Equal("WEBP", result.Format);
        Assert.Equal(640, result.Width);
        Assert.Equal(480, result.Height);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_RejectsHostileJpegAndBmpDimensions()
    {
        RasterImageBudgetResult jpeg = RasterImageResourceBudget.Validate(CreateJpeg(9000, 1));
        RasterImageBudgetResult bmp = RasterImageResourceBudget.Validate(CreateBmp(9000, 1));

        Assert.False(jpeg.Accepted);
        Assert.False(bmp.Accepted);
        Assert.Contains("budget", jpeg.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("budget", bmp.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_AcceptsBoundedIcoAfterInspectingEmbeddedPng()
    {
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateIco([(16, 16, CompletePng(CreatePng(16, 16)))]));

        Assert.True(result.Accepted);
        Assert.Equal("ICO", result.Format);
        Assert.Equal(16, result.Width);
        Assert.Equal(16, result.Height);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_RejectsIcoDirectoryThatHidesOversizedEmbeddedImages()
    {
        byte[] hiddenPng = CreateIco([(1, 1, CompletePng(CreatePng(9000, 1)))]);
        byte[] hiddenDib = CreateIco([(1, 1, CreateIconDib(9000, 1))]);

        Assert.False(RasterImageResourceBudget.Validate(hiddenPng).Accepted);
        Assert.False(RasterImageResourceBudget.Validate(hiddenDib).Accepted);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_RejectsMalformedIcoPayloadRanges()
    {
        byte[] payload = CompletePng(CreatePng(16, 16));
        byte[] truncated = CreateIco([(16, 16, payload)]);
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(14), (uint)(payload.Length + 1));

        byte[] overflow = CreateIco([(16, 16, payload)]);
        BinaryPrimitives.WriteUInt32LittleEndian(overflow.AsSpan(18), uint.MaxValue);

        byte[] overlap = CreateIco([(16, 16, payload), (16, 16, payload)]);
        uint firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(overlap.AsSpan(18));
        BinaryPrimitives.WriteUInt32LittleEndian(overlap.AsSpan(34), firstOffset);

        Assert.False(RasterImageResourceBudget.Validate(truncated).Accepted);
        Assert.False(RasterImageResourceBudget.Validate(overflow).Accepted);
        Assert.False(RasterImageResourceBudget.Validate(overlap).Accepted);

        byte[] truncatedNestedPng = CreateIco([(16, 16, payload[..^1])]);
        Assert.False(RasterImageResourceBudget.Validate(truncatedNestedPng).Accepted);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void Validate_LeavesSvgToTheExistingSvgPolicy()
    {
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'/>"u8);

        Assert.False(result.Accepted);
        Assert.Contains("unsupported", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreatePng(int width, int height, int? frameCount = null)
    {
        int length = 33 + (frameCount.HasValue ? 20 : 0);
        byte[] bytes = new byte[length];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), unchecked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), unchecked((uint)height));
        bytes[24] = 8;
        bytes[25] = 6;
        if (frameCount.HasValue)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33), 8);
            "acTL"u8.CopyTo(bytes.AsSpan(37));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(41), (uint)frameCount.Value);
        }

        return bytes;
    }

    private static byte[] CreateGif(int width, int height, int frameCount)
    {
        List<byte> bytes = [.. "GIF89a"u8.ToArray()];
        bytes.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0]);
        for (int index = 0; index < frameCount; index++)
        {
            bytes.AddRange([
                0x2C,
                0, 0, 0, 0,
                (byte)width, (byte)(width >> 8),
                (byte)height, (byte)(height >> 8),
                0,
                2,
                1, 0,
                0]);
        }

        bytes.Add(0x3B);
        return [.. bytes];
    }

    private static byte[] CreateAnimatedWebP(int width, int height, int frameCount)
    {
        byte[] bytes = new byte[30 + (frameCount * 8)];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        bytes[20] = 0x02;
        WriteUInt24(bytes, 24, width - 1);
        WriteUInt24(bytes, 27, height - 1);
        int offset = 30;
        for (int index = 0; index < frameCount; index++)
        {
            "ANMF"u8.CopyTo(bytes.AsSpan(offset));
            offset += 8;
        }

        return bytes;
    }

    private static byte[] CreateLossyWebP(int width, int height)
    {
        byte[] bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8 "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10);
        new byte[] { 0x9D, 0x01, 0x2A }.CopyTo(bytes, 23);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), (ushort)height);
        return bytes;
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        byte[] bytes = new byte[11];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 7);
        bytes[6] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(7), checked((ushort)height));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9), checked((ushort)width));
        return bytes;
    }

    private static byte[] CreateBmp(int width, int height)
    {
        byte[] bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        return bytes;
    }

    private static byte[] CreateIconDib(int width, int height)
    {
        const int bitsPerPixel = 32;
        int colorStride = checked((((width * bitsPerPixel) + 31) / 32) * 4);
        int maskStride = checked(((width + 31) / 32) * 4);
        byte[] bytes = new byte[checked(40 + ((colorStride + maskStride) * height))];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), checked(height * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), bitsPerPixel);
        return bytes;
    }

    private static byte[] CreateIco(params (int Width, int Height, byte[] Payload)[] images)
    {
        int directoryLength = checked(6 + (images.Length * 16));
        int totalLength = checked(directoryLength + images.Sum(image => image.Payload.Length));
        byte[] bytes = new byte[totalLength];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)images.Length));
        int payloadOffset = directoryLength;
        for (int index = 0; index < images.Length; index++)
        {
            (int width, int height, byte[] payload) = images[index];
            int entryOffset = 6 + (index * 16);
            bytes[entryOffset] = width == 256 ? (byte)0 : checked((byte)width);
            bytes[entryOffset + 1] = height == 256 ? (byte)0 : checked((byte)height);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryOffset + 4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryOffset + 6), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 8), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 12), (uint)payloadOffset);
            payload.CopyTo(bytes, payloadOffset);
            payloadOffset += payload.Length;
        }

        return bytes;
    }

    private static byte[] CompletePng(byte[] png)
    {
        byte[] completed = new byte[png.Length + 12];
        png.CopyTo(completed, 0);
        "IEND"u8.CopyTo(completed.AsSpan(png.Length + 4));
        return completed;
    }

    private static void WriteUInt24(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
    }
}
