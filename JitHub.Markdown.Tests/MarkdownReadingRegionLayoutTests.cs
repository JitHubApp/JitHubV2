using Microsoft.UI.Xaml;
using Xunit;

namespace JitHub.Markdown.Tests;

public sealed class MarkdownReadingRegionLayoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnmeasuredOrTooNarrowHost_StretchesWithoutAWidthLimit(double actualWidth)
    {
        MarkdownReadingRegionSize region = MarkdownReadingRegionLayout.Compute(
            actualWidth, 20, 20, 880, HorizontalAlignment.Left);

        Assert.True(double.IsNaN(region.Width));
        Assert.Equal(double.PositiveInfinity, region.MaxWidth);
        Assert.Equal(HorizontalAlignment.Stretch, region.HorizontalAlignment);
    }

    [Fact]
    public void WideHost_ClampsTheMeasureAndCentersAStretchedRegion()
    {
        MarkdownReadingRegionSize region = MarkdownReadingRegionLayout.Compute(
            1400, 24, 24, 880, HorizontalAlignment.Stretch);

        Assert.Equal(new MarkdownReadingRegionSize(880, 880, HorizontalAlignment.Center), region);
    }

    [Fact]
    public void WideHost_KeepsAnExplicitAlignment()
    {
        MarkdownReadingRegionSize region = MarkdownReadingRegionLayout.Compute(
            1400, 24, 24, 880, HorizontalAlignment.Left);

        Assert.Equal(HorizontalAlignment.Left, region.HorizontalAlignment);
    }

    [Fact]
    public void NarrowerThanTheMeasure_StretchesUpToTheLimit()
    {
        MarkdownReadingRegionSize region = MarkdownReadingRegionLayout.Compute(
            600, 24, 24, 880, HorizontalAlignment.Center);

        Assert.True(double.IsNaN(region.Width));
        Assert.Equal(880, region.MaxWidth);
        Assert.Equal(HorizontalAlignment.Stretch, region.HorizontalAlignment);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NoMeasureLimit_StretchesAcrossTheHost(double contentMaxWidth)
    {
        MarkdownReadingRegionSize region = MarkdownReadingRegionLayout.Compute(
            1400, 24, 24, contentMaxWidth, HorizontalAlignment.Center);

        Assert.True(double.IsNaN(region.Width));
        Assert.Equal(double.PositiveInfinity, region.MaxWidth);
        Assert.Equal(HorizontalAlignment.Stretch, region.HorizontalAlignment);
    }
}
