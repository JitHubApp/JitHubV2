using System;
using System.IO;
using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Runtime smoke tests for the ThorVG native rasterizer. These confirm that
/// the thorvg.dll deploys to the test output directory, the P/Invoke
/// signatures resolve, and a trivial SVG round-trips through the engine to
/// a non-empty BGRA buffer with sensible pixel content.
/// </summary>
public class ThorVgRasterizerTests
{
    private static byte[] Svg(string body) => Encoding.UTF8.GetBytes(body);

    [Fact]
    public void Rasterize_RedSquare_ProducesRedPixels()
    {
        var svg = Svg("<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'>" +
                      "<rect width='64' height='64' fill='#ff0000'/></svg>");

        var r = ThorVgRasterizer.Rasterize(svg, 64, 64);
        Assert.NotNull(r);
        var raster = r.Value;
        Assert.Equal(64, raster.WidthPx);
        Assert.Equal(64, raster.HeightPx);
        Assert.Equal(64 * 64 * 4, raster.Bgra.Length);

        // Sample center pixel (BGRA premultiplied = on little-endian Windows,
        // ARGB8888 uint32 reads back as bytes B,G,R,A in memory).
        int idx = ((32 * 64) + 32) * 4;
        byte b = raster.Bgra[idx + 0];
        byte g = raster.Bgra[idx + 1];
        byte rch = raster.Bgra[idx + 2];
        byte a = raster.Bgra[idx + 3];

        Assert.True(a >= 200, $"alpha should be opaque, got {a}");
        Assert.True(rch >= 200, $"red channel should be saturated, got {rch}");
        Assert.True(b <= 40, $"blue channel should be ~0, got {b}");
        Assert.True(g <= 40, $"green channel should be ~0, got {g}");
    }

    [Fact]
    public void Rasterize_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ThorVgRasterizer.Rasterize(Array.Empty<byte>(), 32, 32));
        Assert.Null(ThorVgRasterizer.Rasterize(Svg("<svg/>"), 0, 32));
        Assert.Null(ThorVgRasterizer.Rasterize(Svg("<svg/>"), 32, 0));
    }

    [Fact]
    public void Rasterize_MalformedSvg_ReturnsNull()
    {
        var r = ThorVgRasterizer.Rasterize(Svg("not actually svg at all"), 32, 32);
        Assert.Null(r);
    }

    [Fact]
    public void Rasterize_GradientWithFilter_DoesNotCrash()
    {
        // Exercises features the prior Win2D path couldn't handle:
        // linear gradient + Gaussian-blur filter. Just asserts we get a
        // non-null result with the right shape — pixel comparison lives in
        // the PixelTests suite.
        var svg = Svg(@"<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'>
  <defs>
    <linearGradient id='g'>
      <stop offset='0%' stop-color='#ff0000'/>
      <stop offset='100%' stop-color='#0000ff'/>
    </linearGradient>
    <filter id='f'><feGaussianBlur stdDeviation='2'/></filter>
  </defs>
  <rect width='64' height='64' fill='url(#g)' filter='url(#f)'/>
</svg>");
        var r = ThorVgRasterizer.Rasterize(svg, 64, 64);
        Assert.NotNull(r);
        Assert.Equal(64 * 64 * 4, r!.Value.Bgra.Length);
    }
}
