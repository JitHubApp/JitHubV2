using Xunit;

namespace MarkdownRenderer.Pixel.Tests;

public sealed class BrowserCaptureBoundsTests
{
    [Theory]
    [InlineData(7, 1.25, 8)]
    [InlineData(5, 1.25, 6)]
    [InlineData(7, 1.75, 8)]
    [InlineData(5, 2.25, 6)]
    [InlineData(890, 1.25, 891)]
    [InlineData(16, 1.25, 16)]
    [InlineData(7, 2, 7)]
    public void IntegralCaptureIncludesTheEntirePhysicalAllocation(int css, double scale, int expected)
    {
        Assert.Equal(expected, PersistentEdgeSvgRasterizer.CaptureCssExtent(css, scale));
        Assert.True(expected * scale >= Math.Ceiling(css * scale));
    }

    [Fact]
    public void ExtractionPreservesEveryRequiredPixelIncludingPartialEdges()
    {
        byte[] source = new byte[10 * 8 * 4];
        for (int i = 0; i < 80; i++)
        {
            source[i * 4] = (byte)i;
            source[i * 4 + 1] = (byte)(255 - i);
            source[i * 4 + 2] = 123;
            source[i * 4 + 3] = (byte)(64 + i);
        }
        byte[] png = PixelComparer.EncodeRgbaAsPng(source, 10, 8);
        byte[] cropped = PersistentEdgeSvgRasterizer.ExtractRequiredCapture(png, 7, 5, 1.25);
        var (pixels, width, height) = PixelComparer.LoadPngAsRgba(cropped);
        Assert.Equal((9, 7), (width, height));
        Assert.Equal(PixelComparer.CropToRequiredBounds(source, 10, 8, 9, 7), pixels);
    }

    [Fact]
    public void ExactCaptureIsReturnedWithoutASecondRasterAllocation()
    {
        byte[] png = PixelComparer.EncodeRgbaAsPng(new byte[9 * 7 * 4], 9, 7);
        Assert.Same(png, PersistentEdgeSvgRasterizer.ExtractRequiredCapture(png, 7, 5, 1.25));
    }

    [Fact]
    public void ExtractionRejectsMissingOuterPixelsRatherThanPaddingOrShrinking()
    {
        byte[] png = PixelComparer.EncodeRgbaAsPng(new byte[9 * 6 * 4], 9, 6);
        Assert.Throws<InvalidDataException>(() =>
            PersistentEdgeSvgRasterizer.ExtractRequiredCapture(png, 7, 5, 1.25));
    }
}
