using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class DirectionalLazyLayoutBandTests
{
    [Fact]
    public void DownwardReadingPreparesTwoViewportsAheadAndOneBehind()
    {
        LazyLayoutBand band = LazyLayoutBand.FromDirectionalViewport(
            viewportTop: 3000,
            viewportHeight: 800,
            lookAheadViewports: 2,
            velocityPixelsPerSecond: 0);

        Assert.Equal(2200, band.Top);
        Assert.Equal(5400, band.Bottom);
    }

    [Fact]
    public void UpwardFlingReversesTheLeadingBand()
    {
        LazyLayoutBand band = LazyLayoutBand.FromDirectionalViewport(
            viewportTop: 3000,
            viewportHeight: 800,
            lookAheadViewports: 2,
            velocityPixelsPerSecond: -4000);

        Assert.True(band.Top < 2200);
        Assert.Equal(4600, band.Bottom);
    }

    [Fact]
    public void ExtremeInputsStayFiniteAndBounded()
    {
        LazyLayoutBand band = LazyLayoutBand.FromDirectionalViewport(
            double.NaN, double.PositiveInfinity, 99, double.NegativeInfinity);

        Assert.True(double.IsFinite(band.Top));
        Assert.True(double.IsFinite(band.Bottom));
        Assert.InRange(band.Bottom - band.Top, 1, 7000);
    }
}
