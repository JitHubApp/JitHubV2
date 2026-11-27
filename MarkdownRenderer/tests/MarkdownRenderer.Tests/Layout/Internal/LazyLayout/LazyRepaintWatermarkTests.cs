using System.Threading.Tasks;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using Xunit;

namespace MarkdownRenderer.Tests.Layout.Internal.LazyLayout;

public class LazyRepaintWatermarkTests
{
    [Fact]
    public void Take_ReturnsInfinityWhenNothingChanged()
    {
        var watermark = new LazyRepaintWatermark();

        Assert.Equal(double.PositiveInfinity, watermark.Take());
    }

    [Fact]
    public void Take_ReturnsTheLowestTopAndResets()
    {
        var watermark = new LazyRepaintWatermark();
        watermark.Lower(900);
        watermark.Lower(300);
        watermark.Lower(700);

        Assert.Equal(300, watermark.Take());
        Assert.Equal(double.PositiveInfinity, watermark.Take());
    }

    [Fact]
    public void Lower_TreatsNaNAsTheTopOfTheDocument()
    {
        var watermark = new LazyRepaintWatermark();
        watermark.Lower(double.NaN);

        Assert.Equal(0, watermark.Take());
    }

    [Fact]
    public void Lower_KeepsTheMinimumAcrossThreads()
    {
        var watermark = new LazyRepaintWatermark();
        Parallel.For(1, 10_001, value => watermark.Lower(value));

        Assert.Equal(1, watermark.Take());
    }
}
