using MarkdownRenderer.Layout;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.Tests;

public class LazyLayoutBandTests
{
    [Fact]
    public void FromViewport_ExpandsByOverscanAndClampsTop()
    {
        var band = LazyLayoutBand.FromViewport(viewportTop: 100, viewportHeight: 600, overscan: 240);

        Assert.Equal(0, band.Top);
        Assert.Equal(940, band.Bottom);
    }

    [Fact]
    public void FromViewport_NormalizesInvalidInputs()
    {
        var band = LazyLayoutBand.FromViewport(double.NaN, viewportHeight: -1, overscan: double.PositiveInfinity);

        Assert.Equal(0, band.Top);
        Assert.Equal(1, band.Bottom);
    }

    [Theory]
    [InlineData(549.9, 611)]
    [InlineData(549.9, 602.09999)]
    [InlineData(0.1, 611.1)]
    [InlineData(0.7, 611.7)]
    [InlineData(549.9, 611.1)]
    [InlineData(549.9, 611.3)]
    [InlineData(9876.54321, 407.33333)]
    [InlineData(16_777_217, 600.3)]
    public void VisibleViewportCoversBothRawAndProjectedEdges(double top, double height)
    {
        LazyLayoutBand raw = LazyLayoutBand.FromViewport(top, height, overscan: 0);
        var rect = new Rect(0, top, 1, height);
        LazyLayoutBand projected = LazyLayoutBand.FromViewport(rect.Top, rect.Height, overscan: 0);

        LazyLayoutBand visible = LazyLayoutBand.FromVisibleViewport(top, height);

        // The exact union admits all displayed geometry, without an arbitrary
        // epsilon, whole-pixel overscan, or a relaxed readiness comparison.
        Assert.Equal(Math.Min(raw.Top, projected.Top), visible.Top);
        Assert.Equal(Math.Max(raw.Bottom, Math.Max(projected.Bottom, rect.Bottom)), visible.Bottom);
        Assert.True(visible.Top <= raw.Top && visible.Top <= projected.Top);
        Assert.True(visible.Bottom >= raw.Bottom && visible.Bottom >= projected.Bottom);
        Assert.True(visible.Bottom >= rect.Bottom);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(549.5, 611.25)]
    public void ExactVisibleViewportDoesNotAddOverscan(double top, double height)
    {
        LazyLayoutBand visible = LazyLayoutBand.FromVisibleViewport(top, height);
        Assert.Equal(top, visible.Top);
        Assert.Equal(top + height, visible.Bottom);
    }

    [Theory]
    [InlineData(double.NaN, -1)]
    [InlineData(double.PositiveInfinity, 611)]
    [InlineData(549.9, double.PositiveInfinity)]
    [InlineData(549.9, 0)]
    [InlineData(549.9, -1)]
    [InlineData(1e100, 611)]
    [InlineData(-1e100, 611)]
    [InlineData(549.9, double.Epsilon)]
    [InlineData(3e38, 3e38)]
    public void InvalidOrUnprojectableVisibleViewportKeepsExistingNormalization(double top, double height)
    {
        LazyLayoutBand expected = LazyLayoutBand.FromViewport(top, height, overscan: 0);
        LazyLayoutBand visible = LazyLayoutBand.FromVisibleViewport(top, height);
        Assert.Equal(expected.Top, visible.Top);
        Assert.Equal(expected.Bottom, visible.Bottom);
    }

    [Fact]
    public void WarmVisibleViewportCalculationDoesNotAllocate()
    {
        for (int index = 0; index < 100; index++)
            _ = LazyLayoutBand.FromVisibleViewport(549.9 + index, 611.3);

        long before = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0;
        for (int index = 0; index < 1_000; index++)
        {
            LazyLayoutBand band = LazyLayoutBand.FromVisibleViewport(549.9 + index, 611.3);
            checksum += band.Bottom - band.Top;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 611_000);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Intersects_IncludesTouchingEdges()
    {
        var band = new LazyLayoutBand(100, 200);

        Assert.True(band.Intersects(50, 100));
        Assert.True(band.Intersects(200, 250));
        Assert.False(band.Intersects(0, 99));
        Assert.False(band.Intersects(201, 300));
    }
}
