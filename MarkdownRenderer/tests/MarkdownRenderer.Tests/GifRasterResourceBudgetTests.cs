using System.Buffers.Binary;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class GifRasterResourceBudgetTests
{
    [Fact]
    public void Validate_UsesLogicalScreenAndPreservesFirstImageRectangle()
    {
        byte[] gif = CreateGif(
            logicalWidth: 4,
            logicalHeight: 4,
            left: 1,
            top: 1,
            frameWidth: 2,
            frameHeight: 3);

        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(gif);

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal("GIF", result.Format);
        Assert.Equal((4, 4), (result.Width, result.Height));
        Assert.Equal(1, result.FrameCount);
        Assert.Equal(16L, result.TotalPixels);
        Assert.Equal(64L, result.DecodedBytes);
        Assert.True(result.FirstFrame.HasValue);
        Assert.Equal((1, 1, 2, 3), result.FirstFrame.Value);
    }

    [Fact]
    public void WithDimensions_PreservesLogicalFrameRectangleAndAdmissionTotals()
    {
        RasterImageBudgetResult source = RasterImageResourceBudget.Validate(
            CreateGif(6, 4, left: 1, top: 0, frameWidth: 2, frameHeight: 3));

        RasterImageBudgetResult oriented = source.WithDimensions(source.Height, source.Width);

        Assert.True(oriented.Accepted, oriented.Reason);
        Assert.Equal((4, 6), (oriented.Width, oriented.Height));
        Assert.Equal(source.TotalPixels, oriented.TotalPixels);
        Assert.Equal(source.DecodedBytes, oriented.DecodedBytes);
        Assert.Equal(source.FirstFrame, oriented.FirstFrame);
    }

    [Fact]
    public void Validate_AcceptsFrameEndingExactlyAtLogicalRightAndBottomEdges()
    {
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateGif(4, 4, left: 2, top: 1, frameWidth: 2, frameHeight: 3));

        Assert.True(result.Accepted, result.Reason);
        Assert.True(result.FirstFrame.HasValue);
        Assert.Equal((2, 1, 2, 3), result.FirstFrame.Value);
    }

    [Fact]
    public void Validate_ChargesEveryFrameAreaWhileKeepingTheFirstFrameRectangle()
    {
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateGif(2, 3, left: 0, top: 0, frameWidth: 2, frameHeight: 3, frameCount: 2));

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(2, result.FrameCount);
        Assert.Equal(12L, result.TotalPixels);
        Assert.Equal(48L, result.DecodedBytes);
        Assert.True(result.FirstFrame.HasValue);
        Assert.Equal((0, 0, 2, 3), result.FirstFrame.Value);
    }

    [Theory]
    [InlineData(1, 1, 0, 0, 2, 3)]
    [InlineData(4, 4, 3, 1, 2, 3)]
    [InlineData(4, 4, 1, 3, 2, 3)]
    [InlineData(4, 4, 0, 0, 0, 3)]
    [InlineData(4, 4, 0, 0, 2, 0)]
    public void Validate_RejectsFramesThatAreEmptyOrEscapeTheLogicalScreen(
        int logicalWidth,
        int logicalHeight,
        int left,
        int top,
        int frameWidth,
        int frameHeight)
    {
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateGif(logicalWidth, logicalHeight, left, top, frameWidth, frameHeight));

        Assert.False(result.Accepted);
        Assert.False(result.CanRenderStaticPreview);
    }

    [Fact]
    public void Validate_ClipsLaterFrameBeyondTheCanvasButRejectsAnEmptyLaterFrame()
    {
        // Browsers clip a later frame to the logical canvas, and only the first
        // frame is drawn, so an overhanging later frame does not reject the image; its accounting counts only the visible area.
        byte[] overhangingSecondFrame = CreateGif(
            4, 4, left: 1, top: 1, frameWidth: 2, frameHeight: 3,
            frameCount: 2,
            secondFrameLeft: 3);

        RasterImageBudgetResult clipped = RasterImageResourceBudget.Validate(overhangingSecondFrame);

        Assert.Equal("GIF", clipped.Format);
        Assert.Equal(2, clipped.FrameCount);
        Assert.True(clipped.Accepted || clipped.CanRenderStaticPreview, clipped.Reason);

        // A zero-sized later descriptor is still malformed.
        byte[] emptySecondFrame = (byte[])overhangingSecondFrame.Clone();
        const int secondDescriptor = 48 + 9;
        BinaryPrimitives.WriteUInt16LittleEndian(emptySecondFrame.AsSpan(secondDescriptor + 4, 2), 0);

        RasterImageBudgetResult rejected = RasterImageResourceBudget.Validate(emptySecondFrame);

        Assert.False(rejected.Accepted);
        Assert.False(rejected.CanRenderStaticPreview);
    }

    [Fact]
    public void Validate_RejectsEveryTruncatedPrefixOfTheCompleteFixture()
    {
        byte[] valid = CreateGif(4, 4, left: 1, top: 1, frameWidth: 2, frameHeight: 3);

        for (int length = 0; length < valid.Length; length++)
        {
            RasterImageBudgetResult result = RasterImageResourceBudget.Validate(valid.AsSpan(0, length));

            Assert.False(result.Accepted, $"Truncated prefix of {length} bytes was admitted.");
            Assert.False(result.CanRenderStaticPreview, $"Truncated prefix of {length} bytes became a preview.");
        }
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(16_385, 1)]
    [InlineData(1, 16_385)]
    [InlineData(10_000, 10_000)]
    public void Validate_RejectsZeroOversizedAndOverBudgetLogicalScreens(int logicalWidth, int logicalHeight)
    {
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateGif(logicalWidth, logicalHeight, left: 0, top: 0, frameWidth: 1, frameHeight: 1));

        Assert.False(result.Accepted);
        Assert.False(result.CanRenderStaticPreview);
    }

    [Fact]
    public void Validate_UsesBoundedStaticPreviewForTwentyFiveMillionPixelLogicalScreen()
    {
        // This is a budget-header fixture, not a native decoder input: the
        // tiny compressed frame remains unchanged while its logical canvas
        // metadata exercises the large-source preview policy.
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateGif(5_000, 5_000, left: 0, top: 0, frameWidth: 2, frameHeight: 3));

        Assert.False(result.Accepted);
        Assert.True(result.CanRenderStaticPreview);
        Assert.Equal("GIF", result.Format);
        Assert.Equal((5_000, 5_000), (result.Width, result.Height));
        Assert.Equal(25_000_000L, result.TotalPixels);
        Assert.Equal(100_000_000L, result.DecodedBytes);
        Assert.True(result.FirstFrame.HasValue);
        Assert.Equal((0, 0, 2, 3), result.FirstFrame.Value);
    }

    [Fact]
    public void Validate_PreservesFirstRectangleWhenFrameCountRequiresStaticPreview()
    {
        // Repeated small data blocks exercise bounded structural accounting;
        // no claim is made that this synthetic stream is a native visual fixture.
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateGif(2, 3, left: 0, top: 0, frameWidth: 2, frameHeight: 3, frameCount: 61));

        Assert.False(result.Accepted);
        Assert.True(result.CanRenderStaticPreview);
        Assert.Equal("GIF", result.Format);
        Assert.Equal(61, result.FrameCount);
        Assert.Equal(366L, result.TotalPixels);
        Assert.Equal(1_464L, result.DecodedBytes);
        Assert.True(result.FirstFrame.HasValue);
        Assert.Equal((0, 0, 2, 3), result.FirstFrame.Value);
    }

    [Fact]
    public void Validate_UsesStaticPreviewWhenAggregateFrameBytesExceedTheDecodedBudget()
    {
        // Header-only dimensions are intentionally larger than the payload's
        // encoded pixel content. This validates budget decisions, not WIC decode.
        RasterImageBudgetResult result = RasterImageResourceBudget.Validate(
            CreateGif(4_096, 4_096, left: 0, top: 0,
                frameWidth: 4_096, frameHeight: 4_096, frameCount: 2));

        Assert.False(result.Accepted);
        Assert.True(result.CanRenderStaticPreview);
        Assert.Equal(2, result.FrameCount);
        Assert.Equal(33_554_432L, result.TotalPixels);
        Assert.Equal(134_217_728L, result.DecodedBytes);
        Assert.True(result.FirstFrame.HasValue);
        Assert.Equal((0, 0, 4_096, 4_096), result.FirstFrame.Value);
    }

    private static byte[] CreateGif(
        int logicalWidth,
        int logicalHeight,
        int left,
        int top,
        int frameWidth,
        int frameHeight,
        int frameCount = 1,
        int? secondFrameLeft = null)
    {
        byte[] singleFrame = Convert.FromBase64String(
            "R0lGODlhAgADAHAAACH5BAEAAAEALAAAAAACAAMAgf8AAAAAAAAAAAAAAAIChF8AOw==");
        BinaryPrimitives.WriteUInt16LittleEndian(singleFrame.AsSpan(6, 2), checked((ushort)logicalWidth));
        BinaryPrimitives.WriteUInt16LittleEndian(singleFrame.AsSpan(8, 2), checked((ushort)logicalHeight));
        BinaryPrimitives.WriteUInt16LittleEndian(singleFrame.AsSpan(22, 2), checked((ushort)left));
        BinaryPrimitives.WriteUInt16LittleEndian(singleFrame.AsSpan(24, 2), checked((ushort)top));
        BinaryPrimitives.WriteUInt16LittleEndian(singleFrame.AsSpan(26, 2), checked((ushort)frameWidth));
        BinaryPrimitives.WriteUInt16LittleEndian(singleFrame.AsSpan(28, 2), checked((ushort)frameHeight));

        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        if (frameCount == 1)
            return singleFrame;

        // Duplicate the complete GCE + image descriptor + local palette + data
        // block. The base fixture has no global table. Larger dimensions are
        // structural budget cases and are not native decoder pixel fixtures.
        const int prefixLength = 48;
        const int frameBlockLength = 35;
        byte[] multipleFrames = new byte[checked(prefixLength +
            (frameBlockLength * (frameCount - 1)) + 1)];
        singleFrame.AsSpan(0, prefixLength).CopyTo(multipleFrames);
        for (int frameIndex = 1; frameIndex < frameCount; frameIndex++)
        {
            int blockOffset = prefixLength + ((frameIndex - 1) * frameBlockLength);
            singleFrame.AsSpan(13, frameBlockLength)
                .CopyTo(multipleFrames.AsSpan(blockOffset, frameBlockLength));
            if (frameIndex == 1 && secondFrameLeft is { } laterLeft)
            {
                // The duplicated GCE is eight bytes; its descriptor follows.
                BinaryPrimitives.WriteUInt16LittleEndian(
                    multipleFrames.AsSpan(blockOffset + 9, 2), checked((ushort)laterLeft));
            }
        }

        multipleFrames[^1] = 0x3B;
        return multipleFrames;
    }
}
