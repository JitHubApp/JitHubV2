using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public class SvgSkiaRasterizerTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Rasterize_SimpleRect_ProducesBuffer()
    {
        var svg = B("""<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="red"/></svg>""");
        var raster = SvgSkiaRasterizer.Rasterize(svg, 32, 32);
        Assert.NotNull(raster);
        Assert.Equal(32, raster!.Value.WidthPx);
        Assert.Equal(32, raster.Value.HeightPx);
        Assert.Equal(32 * 32 * 4, raster.Value.Bgra.Length);
        // Center pixel should be red-ish (BGRA premultiplied)
        int idx = (16 * 32 + 16) * 4;
        var b = raster.Value.Bgra[idx];
        var g = raster.Value.Bgra[idx + 1];
        var r = raster.Value.Bgra[idx + 2];
        var a = raster.Value.Bgra[idx + 3];
        Assert.True(r > 200, $"expected red channel >200, got R={r} G={g} B={b} A={a}");
    }

    [Fact]
    public void Rasterize_FilterEffect_DoesNotThrow()
    {
        // SVG with feGaussianBlur — the exact Win2D-incompatible feature
        // the Skia tier must handle.
        var svg = B(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\">" +
            "<defs><filter id=\"b\"><feGaussianBlur stdDeviation=\"3\"/></filter></defs>" +
            "<circle cx=\"32\" cy=\"32\" r=\"20\" fill=\"blue\" filter=\"url(#b)\"/>" +
            "</svg>");
        var raster = SvgSkiaRasterizer.Rasterize(svg, 64, 64);
        Assert.NotNull(raster);
        Assert.Equal(64 * 64 * 4, raster!.Value.Bgra.Length);
    }

    [Fact]
    public void Rasterize_InvalidBytes_ReturnsNull()
    {
        var raster = SvgSkiaRasterizer.Rasterize(B("not an svg"), 16, 16);
        Assert.Null(raster);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(16, 0)]
    [InlineData(-1, 16)]
    public void Rasterize_InvalidSize_ReturnsNull(int w, int h)
    {
        var svg = B("""<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"/>""");
        Assert.Null(SvgSkiaRasterizer.Rasterize(svg, w, h));
    }
}
