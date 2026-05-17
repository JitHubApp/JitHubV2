using MarkdownRenderer.Layout;
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
