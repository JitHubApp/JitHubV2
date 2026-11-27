using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using Xunit;

namespace MarkdownRenderer.Tests.Layout.Internal.LazyLayout;

public class LazyBandRealizationOrderTests
{
    [Fact]
    public void Plan_StartsWithTheVisibleBand()
    {
        IReadOnlyList<LazyLayoutBand> parts = LazyBandRealizationOrder.Plan(
            new LazyLayoutBand(0, 5000), new LazyLayoutBand(1000, 1800), chunkHeight: 800);

        Assert.Equal(1000, parts[0].Top);
        Assert.Equal(1800, parts[0].Bottom);
    }

    [Fact]
    public void Plan_MeasuresTheWiderSideFirstNearestTheViewport()
    {
        IReadOnlyList<LazyLayoutBand> parts = LazyBandRealizationOrder.Plan(
            new LazyLayoutBand(600, 5000), new LazyLayoutBand(1000, 1800), chunkHeight: 800);

        // Below is wider (3200 DIPs), so it comes first, downward from the viewport.
        Assert.Equal(new[] { 1800.0, 2600, 3400, 4200 }, parts.Skip(1).Take(4).Select(part => part.Top));
        Assert.Equal(5000, parts[4].Bottom);
        // Then the narrower side above, upward from the viewport.
        Assert.Equal(1000, parts[5].Bottom);
        Assert.Equal(600, parts[5].Top);
        Assert.Equal(6, parts.Count);
    }

    [Fact]
    public void Plan_LeadsUpwardWhenTheBandExtendsMostlyAbove()
    {
        IReadOnlyList<LazyLayoutBand> parts = LazyBandRealizationOrder.Plan(
            new LazyLayoutBand(0, 2200), new LazyLayoutBand(1800, 2000), chunkHeight: 256);

        Assert.Equal(1800, parts[1].Bottom);
        Assert.True(parts[1].Top < parts[1].Bottom);
        Assert.True(parts[2].Bottom <= parts[1].Top);
        Assert.Equal(2000, parts[^1].Top);
        Assert.Equal(2200, parts[^1].Bottom);
    }

    [Fact]
    public void Plan_CoversTheWholeBandWithoutOverlap()
    {
        var band = new LazyLayoutBand(120, 9876);
        IReadOnlyList<LazyLayoutBand> parts = LazyBandRealizationOrder.Plan(
            band, new LazyLayoutBand(3000, 3900), chunkHeight: 700);

        double covered = parts.Sum(part => part.Bottom - part.Top);
        Assert.Equal(band.Bottom - band.Top, covered, precision: 6);
        Assert.All(parts, part => Assert.True(part.Bottom - part.Top <= 900));
    }

    [Fact]
    public void Plan_ClampsAVisibleBandOutsideTheRealizationBand()
    {
        IReadOnlyList<LazyLayoutBand> parts = LazyBandRealizationOrder.Plan(
            new LazyLayoutBand(5000, 9000), new LazyLayoutBand(1000, 1800), chunkHeight: 1000);

        Assert.Equal(5000, parts[0].Top);
        Assert.Equal(5000, parts[0].Bottom);
        Assert.Equal(5000, parts[1].Top);
        Assert.Equal(9000, parts[^1].Bottom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void Plan_UsesTheMinimumChunkForInvalidHeights(double chunkHeight)
    {
        IReadOnlyList<LazyLayoutBand> parts = LazyBandRealizationOrder.Plan(
            new LazyLayoutBand(0, 1024), new LazyLayoutBand(0, 0), chunkHeight);

        Assert.All(parts.Skip(1), part =>
            Assert.Equal(LazyBandRealizationOrder.MinimumChunkHeight, part.Bottom - part.Top));
    }
}
