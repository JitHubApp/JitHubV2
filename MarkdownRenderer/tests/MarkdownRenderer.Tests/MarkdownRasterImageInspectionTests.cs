using System.Buffers.Binary;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownRasterImageInspectionTests
{
    public static TheoryData<string, MarkdownRasterFormat?> Images => new()
    {
        { "png", MarkdownRasterFormat.Png },
        { "webp", MarkdownRasterFormat.WebP },
        { "gif-animated", MarkdownRasterFormat.Gif },
        { "png-oversized", null },
        { "garbage", null },
    };

    [Theory]
    [MemberData(nameof(Images))]
    public void Inspect_ReportsWhatTheViewsDecodeBudgetDecides(string image, MarkdownRasterFormat? format)
    {
        byte[] bytes = Create(image);
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(bytes);
        bool hasDisplaySize = RasterIntrinsicMetadata.TryReadValidated(
            bytes, budget, out RasterIntrinsicMetadata displaySize, out int orientation);

        MarkdownRasterImageInspection inspection = MarkdownRasterImageInspection.Inspect(bytes);

        Assert.Equal(budget.Accepted, inspection.IsAccepted);
        Assert.Equal(budget.CanRenderStaticPreview, inspection.CanRenderStaticPreview);
        Assert.Equal(budget.Accepted ? null : budget.Reason, inspection.RejectionReason);
        Assert.Equal(format, inspection.Format);
        Assert.Equal(budget.Width, inspection.PixelWidth);
        Assert.Equal(budget.Height, inspection.PixelHeight);
        Assert.Equal(budget.FrameCount, inspection.FrameCount);
        Assert.Equal(budget.DecodedBytes, inspection.DecodedBytes);
        Assert.Equal(budget.FirstFrame is not null, inspection.HasFirstFrameRegion);
        Assert.Equal(hasDisplaySize ? displaySize.Width : 0, inspection.DisplayWidth);
        Assert.Equal(hasDisplaySize ? displaySize.Height : 0, inspection.DisplayHeight);
        Assert.Equal(hasDisplaySize ? orientation : 1, inspection.Orientation);
    }

    [Fact]
    public void Inspect_AcceptsABoundedStaticImageWithoutOrientation()
    {
        MarkdownRasterImageInspection inspection = MarkdownRasterImageInspection.Inspect(Create("png"));

        Assert.True(inspection.IsAccepted);
        Assert.Null(inspection.RejectionReason);
        Assert.Equal((1024, 768), (inspection.PixelWidth, inspection.PixelHeight));
        Assert.Equal((1024, 768), (inspection.DisplayWidth, inspection.DisplayHeight));
        Assert.Equal(1, inspection.Orientation);
        Assert.Equal(1, inspection.FrameCount);
    }

    [Fact]
    public void Inspect_RejectsUnrecognizedBytesWithAReason()
    {
        MarkdownRasterImageInspection inspection = MarkdownRasterImageInspection.Inspect(Create("garbage"));

        Assert.False(inspection.IsAccepted);
        Assert.False(inspection.CanRenderStaticPreview);
        Assert.False(string.IsNullOrWhiteSpace(inspection.RejectionReason));
        Assert.Null(inspection.Format);
        Assert.Equal(0, inspection.DisplayWidth);
    }

    [Fact]
    public void Inspect_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MarkdownRasterImageInspection.Inspect(Create("png"), cancellation.Token));
    }

    private static byte[] Create(string image) => image switch
    {
        "png" => CreatePng(1024, 768),
        "png-oversized" => CreatePng(RasterImageResourceBudget.MaxDimension + 1, 4),
        "webp" => CreateLossyWebP(320, 200),
        "gif-animated" => CreateGif(16, 16, frameCount: RasterImageResourceBudget.MaxFrameCount + 1),
        "garbage" => [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16],
        _ => throw new ArgumentOutOfRangeException(nameof(image)),
    };

    // Signature, IHDR and IEND: a complete header-only PNG.
    private static byte[] CreatePng(int width, int height)
    {
        byte[] bytes = new byte[45];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), unchecked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), unchecked((uint)height));
        bytes[24] = 8;
        bytes[25] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33), 0);
        "IEND"u8.CopyTo(bytes.AsSpan(37));
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
}
