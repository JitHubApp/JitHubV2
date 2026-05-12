using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public class SvgFeatureScannerTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void SimpleSvg_ClassifiesAsWin2D()
    {
        var svg = B("""<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="red"/></svg>""");
        Assert.Equal(SvgRenderTier.Win2D, SvgFeatureScanner.Classify(svg));
    }

    [Fact]
    public void SvgWithGradient_ClassifiesAsWin2D()
    {
        var svg = B("""<svg xmlns="http://www.w3.org/2000/svg"><defs><linearGradient id="g"><stop offset="0%" stop-color="red"/></linearGradient></defs><rect fill="url(#g)"/></svg>""");
        Assert.Equal(SvgRenderTier.Win2D, SvgFeatureScanner.Classify(svg));
    }

    [Theory]
    [InlineData("<filter id=\"f\">", SvgRenderTier.Skia)]
    [InlineData("<mask id=\"m\">", SvgRenderTier.Skia)]
    [InlineData("<clipPath id=\"c\">", SvgRenderTier.Skia)]
    [InlineData("<pattern id=\"p\">", SvgRenderTier.Skia)]
    [InlineData("<foreignObject>", SvgRenderTier.Skia)]
    [InlineData("<animate attributeName=\"x\"/>", SvgRenderTier.Skia)]
    [InlineData("<style>.a{fill:red}</style>", SvgRenderTier.Skia)]
    [InlineData("<feGaussianBlur stdDeviation=\"3\"/>", SvgRenderTier.Skia)]
    [InlineData("<feDropShadow/>", SvgRenderTier.Skia)]
    [InlineData("filter=\"url(#blur)\"", SvgRenderTier.Skia)]
    [InlineData("clip-path=\"url(#c)\"", SvgRenderTier.Skia)]
    public void IncompatibleFeature_RoutesToSkia(string snippet, SvgRenderTier expected)
    {
        var svg = B($"<svg xmlns=\"http://www.w3.org/2000/svg\">{snippet}</svg>");
        Assert.Equal(expected, SvgFeatureScanner.Classify(svg));
    }

    [Fact]
    public void Empty_DefaultsToWin2D()
    {
        Assert.Equal(SvgRenderTier.Win2D, SvgFeatureScanner.Classify(System.ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Case_Insensitive()
    {
        var svg = B("<SVG><FILTER id=\"x\"></FILTER></SVG>");
        Assert.Equal(SvgRenderTier.Skia, SvgFeatureScanner.Classify(svg));
    }
}
