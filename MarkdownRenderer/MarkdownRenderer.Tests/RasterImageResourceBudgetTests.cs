using System.Buffers.Binary;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class RasterImageResourceBudgetTests
{
    [Fact]
    public void Validate_AcceptsNarrowTallReadmeScreenshotWithinDecodedBudget()
    {
        byte[] png = CreatePngHeader(width: 1464, height: 9187);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(png);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(1464, result.Width);
        Assert.Equal(9187, result.Height);
        Assert.InRange(result.DecodedBytes, 1, RasterImageResourceBudget.MaxDecodedBytes);
    }

    [Fact]
    public void Validate_StillRejectsDimensionBombBeyondGpuLimit()
    {
        byte[] png = CreatePngHeader(width: RasterImageResourceBudget.MaxDimension + 1, height: 1);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(png);

        Assert.False(result.Accepted);
        Assert.False(result.CanRenderStaticPreview);
        Assert.Contains("dimensions", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AcceptsDecodablePngTrailerAfterIend()
    {
        byte[] png = CreatePngWithTrailingEncoderDebris(width: 1015, height: 379);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(png);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal("PNG", result.Format);
        Assert.Equal(1015, result.Width);
        Assert.Equal(379, result.Height);
    }

    [Fact]
    public void Validate_ReadsStillAvifSpatialExtentsFromBoundedBmffBoxes()
    {
        byte[] avif = CreateAvifHeader(width: 1920, height: 1080);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(avif);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal("AVIF", result.Format);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), checked((uint)height));
        return bytes;
    }

    private static byte[] CreateAvifHeader(int width, int height)
    {
        byte[] bytes = new byte[72];
        WriteBoxHeader(bytes, 0, 24, "ftyp"u8);
        "avif"u8.CopyTo(bytes.AsSpan(8));
        "mif1"u8.CopyTo(bytes.AsSpan(16));
        "avif"u8.CopyTo(bytes.AsSpan(20));

        WriteBoxHeader(bytes, 24, 48, "meta"u8);
        WriteBoxHeader(bytes, 36, 36, "iprp"u8);
        WriteBoxHeader(bytes, 44, 28, "ipco"u8);
        WriteBoxHeader(bytes, 52, 20, "ispe"u8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(64), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(68), checked((uint)height));
        return bytes;
    }

    private static byte[] CreatePngWithTrailingEncoderDebris(int width, int height)
    {
        byte[] bytes = new byte[57];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), checked((uint)height));
        bytes[24] = 8;
        bytes[25] = 6;
        "IEND"u8.CopyTo(bytes.AsSpan(37));
        // A large-looking value after the valid end marker must not be parsed
        // as a PNG chunk length.
        bytes.AsSpan(45).Fill(0xFF);
        return bytes;
    }

    private static void WriteBoxHeader(byte[] bytes, int offset, int size, ReadOnlySpan<byte> type)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), checked((uint)size));
        type.CopyTo(bytes.AsSpan(offset + 4));
    }
}
