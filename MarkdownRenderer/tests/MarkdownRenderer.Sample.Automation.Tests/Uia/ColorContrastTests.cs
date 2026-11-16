using System.Drawing;
using MarkdownRenderer.Sample.Automation.Uia;
using Xunit;

namespace MarkdownRenderer.Sample.Automation.Tests.Uia;

public sealed class ColorContrastTests
{
    [Fact]
    public void BlackOnWhiteIsTwentyOneToOne()
    {
        Assert.Equal(21d, ColorContrast.Ratio(Color.Black, Color.White), precision: 6);
        Assert.Equal(21d, ColorContrast.Ratio(Color.White, Color.Black), precision: 6);
    }

    [Fact]
    public void IdenticalColorsAreOneToOne()
        => Assert.Equal(1d, ColorContrast.Ratio(Color.FromArgb(0, 255, 255), Color.FromArgb(0, 255, 255)), precision: 6);

    [Fact]
    public void RelativeLuminanceWeightsGreenMost()
    {
        Assert.Equal(0.2126d, ColorContrast.RelativeLuminance(Color.FromArgb(255, 0, 0)), precision: 6);
        Assert.Equal(0.7152d, ColorContrast.RelativeLuminance(Color.FromArgb(0, 255, 0)), precision: 6);
        Assert.Equal(0.0722d, ColorContrast.RelativeLuminance(Color.FromArgb(0, 0, 255)), precision: 6);
    }
}
