using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public class SvgIntrinsicsTests
{
    [Theory]
    [InlineData("foo.svg", true)]
    [InlineData("foo.SVG", true)]
    [InlineData("https://x.test/y.svg?cb=1", true)]
    [InlineData("https://x.test/y.svg#frag", true)]
    [InlineData("data:image/svg+xml;base64,AAA", true)]
    [InlineData("foo.png", false)]
    [InlineData("foo.svg.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeSvg_DetectsCorrectly(string? url, bool expected)
    {
        Assert.Equal(expected, SvgIntrinsics.LooksLikeSvg(url));
    }

    [Fact]
    public void ExtractIntrinsicSize_ExplicitWidthHeight()
    {
        var bytes = Bytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"80\"><rect/></svg>");
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(bytes);
        Assert.Equal(120, w);
        Assert.Equal(80, h);
    }

    [Fact]
    public void ExtractIntrinsicSize_StripsUnits()
    {
        var bytes = Bytes("<svg width=\"100px\" height=\"50px\"></svg>");
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(bytes);
        Assert.Equal(100, w);
        Assert.Equal(50, h);
    }

    [Fact]
    public void ExtractIntrinsicSize_FallsBackToViewBox()
    {
        var bytes = Bytes("<svg viewBox=\"0 0 256 64\"></svg>");
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(bytes);
        Assert.Equal(256, w);
        Assert.Equal(64, h);
    }

    [Fact]
    public void ExtractIntrinsicSize_NoSizeReturnsZero()
    {
        var bytes = Bytes("<svg></svg>");
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(bytes);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Fact]
    public void ExtractIntrinsicSize_PrefersExplicitOverViewBox()
    {
        var bytes = Bytes("<svg width=\"10\" height=\"20\" viewBox=\"0 0 999 999\"></svg>");
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(bytes);
        Assert.Equal(10, w);
        Assert.Equal(20, h);
    }

    [Fact]
    public void ExtractIntrinsicSize_DoesNotMatchSimilarlyNamedAttribute()
    {
        // "stroke-width" must not match the "width" lookup.
        var bytes = Bytes("<svg stroke-width=\"3\" viewBox=\"0 0 8 16\"></svg>");
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(bytes);
        Assert.Equal(8, w);
        Assert.Equal(16, h);
    }

    [Fact]
    public void ExtractIntrinsicSize_MalformedReturnsZero()
    {
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(Bytes("not xml at all"));
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Fact]
    public void ExtractIntrinsicSize_NullBytesReturnsZero()
    {
        var (w, h) = SvgIntrinsics.TryExtractIntrinsicSize(null);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
