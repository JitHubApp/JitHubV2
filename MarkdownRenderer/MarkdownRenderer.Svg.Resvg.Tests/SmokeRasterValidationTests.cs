using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SmokeRasterValidationTests
{
    [Xunit.Fact]
    public void InvalidStorageReturnsAFailureWithoutReadingOutOfBounds()
    {
        Assert.Equal(SmokeRasterFailure.Dimensions, SmokeRasterValidation.CheckRich([], 128, 64).Failures);
        Assert.Equal(SmokeRasterFailure.Dimensions, SmokeRasterValidation.CheckRich(new byte[128 * 64 * 4], 127, 64).Failures);
    }

    [Xunit.Theory]
    [Xunit.InlineData(77, 119, true)]
    [Xunit.InlineData(122, 127, false)]
    [Xunit.InlineData(128, -1, false)]
    [Xunit.InlineData(31, 119, false)]
    [Xunit.InlineData(100, 119, false)]
    public void RtlBoundsRetainTheOriginalFidelityGate(int left, int right, bool valid)
    {
        byte[] pixels = CreateRaster(left, right);
        SmokeRasterResult result = SmokeRasterValidation.CheckRich(pixels, 128, 64);
        Assert.Equal(valid ? SmokeRasterFailure.None : SmokeRasterFailure.RtlBounds, result.Failures);
        Assert.Equal(left, result.TextLeft);
        Assert.Equal(right, result.TextRight);
    }

    [Xunit.Fact]
    public void ReportsAllMissingRegionsWithoutThrowingOrAllocatingPerRender()
    {
        byte[] pixels = new byte[128 * 64 * 4];
        _ = SmokeRasterValidation.CheckRich(pixels, 128, 64);
        long before = GC.GetAllocatedBytesForCurrentThread();
        SmokeRasterResult result = default;
        for (int i = 0; i < 100; i++)
            result = SmokeRasterValidation.CheckRich(pixels, 128, 64);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(SmokeRasterFailure.FirstEmbeddedImage | SmokeRasterFailure.RepeatedEmbeddedImage |
            SmokeRasterFailure.RtlBounds, result.Failures);
    }

    [Xunit.Fact]
    public void ManyImagesChecksEveryUniqueResourceWithoutPerRenderAllocation()
    {
        const int width = 890;
        const int height = 158;
        byte[] pixels = new byte[width * height * 4];
        for (int image = 0; image < 182; image++)
        {
            int offset = (((image / 26 * 22 + 8) * width) + image % 26 * 34 + 8) * 4;
            pixels[offset] = (byte)(image + 1);
            pixels[offset + 1] = (byte)(255 - image);
            pixels[offset + 2] = (byte)(image * 37 % 256);
            pixels[offset + 3] = 255;
        }
        Assert.Equal(SmokeRasterFailure.None, SmokeRasterValidation.CheckManyImages(pixels, width, height));
        long before = GC.GetAllocatedBytesForCurrentThread();
        SmokeRasterFailure result = default;
        for (int repeat = 0; repeat < 100; repeat++)
            result |= SmokeRasterValidation.CheckManyImages(pixels, width, height);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(SmokeRasterFailure.None, result);

        // No resource may silently disappear, including the last avatar.
        for (int image = 0; image < 182; image++)
        {
            int alpha = (((image / 26 * 22 + 8) * width) + image % 26 * 34 + 8) * 4 + 3;
            pixels[alpha] = 0;
            Assert.Equal(SmokeRasterFailure.UniqueEmbeddedImages,
                SmokeRasterValidation.CheckManyImages(pixels, width, height));
            pixels[alpha] = 255;
        }
        Assert.Equal(SmokeRasterFailure.Dimensions, SmokeRasterValidation.CheckManyImages([], width, height));
        Assert.Equal(SmokeRasterFailure.Dimensions, SmokeRasterValidation.CheckManyImages(pixels, width - 1, height));
    }

    private static byte[] CreateRaster(int left, int right)
    {
        byte[] pixels = new byte[128 * 64 * 4];
        foreach (int x in new[] { 4, 20 })
        {
            int offset = ((4 * 128) + x) * 4;
            pixels[offset] = 240;
            pixels[offset + 1] = 24;
            pixels[offset + 2] = 40;
            pixels[offset + 3] = 255;
        }
        for (int x = left; x <= right; x++)
            pixels[((40 * 128) + x) * 4 + 3] = 255;
        return pixels;
    }
}
