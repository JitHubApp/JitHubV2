using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class RasterPreparationOutputEstimateTests
{
    [Fact]
    public async Task TwoDisplaySizedPreviewsFitWithoutDroppingSourceSurfaceCharge()
    {
        const int width = 4096;
        const int height = 2048;
        const int sourceBytes = 4096;
        var sizing = new RasterImageSizingRequest(800, 1, null, null, 16_777_216);
        long outputPixels = sizing.GetMaximumOrientedOutputPixels(width, height, maySwapAxes: true);
        Assert.Equal(1_280_000, outputPixels); // 800x1600 after an EXIF axis swap.
        long sourceSurfaceBytes = (long)width * height * 4;
        long weight = sourceBytes + sourceSurfaceBytes + outputPixels * 8;
        var admission = new MarkdownImagePreparationAdmission(128L * 1024 * 1024, 2);
        using IDisposable first = await admission.EnterAsync(new object(), weight, false, CancellationToken.None);
        Task<IDisposable> pendingSecond = admission.EnterAsync(new object(), weight, false, CancellationToken.None).AsTask();
        Assert.True(pendingSecond.IsCompletedSuccessfully);
        using IDisposable second = await pendingSecond;
        Assert.Equal(2, admission.ActiveCount);
        Assert.Equal(weight * 2, admission.ActiveBytes);
        Assert.True(sourceSurfaceBytes * 2 < admission.ActiveBytes);
        long previousWeight = sourceBytes + sourceSurfaceBytes + (long)width * height * 8;
        Assert.True(previousWeight * 2 > admission.CapacityBytes);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(1.75d)]
    [InlineData(2d)]
    [InlineData(2.25d)]
    [InlineData(3d)]
    [InlineData(4d)]
    public void BothOrientationPlansAreCoveredAtEveryScale(double scale)
    {
        var sizing = new RasterImageSizingRequest(800, scale, null, null, 9_000_000);
        long weight = sizing.GetMaximumOrientedOutputPixels(4096, 2048, maySwapAxes: true);
        var raw = sizing.GetSize(4096, 2048);
        var rotated = sizing.GetSize(2048, 4096);
        Assert.True(weight >= (long)raw.Width * raw.Height);
        Assert.True(weight >= (long)rotated.Width * rotated.Height);
        Assert.InRange(weight, 1, sizing.MaximumPixels);
        Assert.Equal(weight, sizing.GetMaximumOrientedOutputPixels(2048, 4096, maySwapAxes: true));
    }

    [Fact]
    public void GifCanvasDoesNotChargeAnImpossibleExifRotation()
    {
        var sizing = new RasterImageSizingRequest(800, 1, null, null, 16_777_216);
        Assert.Equal(320_000, sizing.GetMaximumOrientedOutputPixels(4096, 2048, maySwapAxes: false));
    }

    [Fact]
    public void AuthoredSizesAndPixelCeilingStillBoundBothAxisPlans()
    {
        var sizing = new RasterImageSizingRequest(800, 4,
            new SafeHtmlLength(1600, false), new SafeHtmlLength(900, false), 512_000);
        long weight = sizing.GetMaximumOrientedOutputPixels(8000, 5000, maySwapAxes: true);
        Assert.InRange(weight, 1, 512_000);
        var raw = sizing.GetSize(8000, 5000);
        var rotated = sizing.GetSize(5000, 8000);
        Assert.Equal(System.Math.Max((long)raw.Width * raw.Height, (long)rotated.Width * rotated.Height), weight);
    }

    [Theory]
    [InlineData(1, 40_000_000)]
    [InlineData(40_000_000, 1)]
    [InlineData(8000, 5000)]
    public void ThinAndNearPreviewLimitSourcesStayBounded(int width, int height)
    {
        var sizing = new RasterImageSizingRequest(800, 4, null, null, 512_000);
        Assert.InRange(sizing.GetMaximumOrientedOutputPixels(width, height, true), 1, 512_000);
    }

    [Fact]
    public void InvalidDimensionsAndDefaultSizingCannotProduceAdmissionWeights()
    {
        var sizing = new RasterImageSizingRequest(800, 1, null, null, 512_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => sizing.GetMaximumOrientedOutputPixels(0, 2048, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => sizing.GetMaximumOrientedOutputPixels(4096, -1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            default(RasterImageSizingRequest).GetMaximumOrientedOutputPixels(4096, 2048, true));
    }
}
