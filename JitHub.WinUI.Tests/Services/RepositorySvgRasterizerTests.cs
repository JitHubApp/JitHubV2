using System.Text;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositorySvgRasterizerTests
{
    private readonly RepositorySvgRasterizer _rasterizer = new();

    [Fact]
    public void RasterizeTile_PreservesTransparentBgraPixels()
    {
        using RepositorySvgDocument document = Load(
            "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'>" +
            "<rect x='1' y='1' width='2' height='2' fill='#ff0000'/></svg>");

        RepositorySvgTile tile = _rasterizer.RasterizeTile(
            document,
            new RepositorySvgTileRequest(0, 0, 8, 8, 2),
            CancellationToken.None);

        Assert.Equal(8 * 8 * 4, tile.ByteCount);
        Assert.Equal(0, AlphaAt(tile, 0, 0));
        Assert.Equal(255, AlphaAt(tile, 4, 4));
        Assert.Equal(0, BlueAt(tile, 4, 4));
        Assert.Equal(0, GreenAt(tile, 4, 4));
        Assert.Equal(255, RedAt(tile, 4, 4));
    }

    [Theory]
    [InlineData(0.1f, 1)]
    [InlineData(1f, 10)]
    [InlineData(8f, 80)]
    public void RasterizeTile_SupportsRequiredZoomScales(float scale, int edge)
    {
        using RepositorySvgDocument document = Load(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
            "<rect width='10' height='10' fill='#246bce'/></svg>");

        RepositorySvgTile tile = _rasterizer.RasterizeTile(
            document,
            new RepositorySvgTileRequest(0, 0, edge, edge, scale),
            CancellationToken.None);

        Assert.Contains(tile.BgraPixels.Where((_, index) => index % 4 == 3), alpha => alpha != 0);
    }

    [Fact]
    public void LoadAndRasterize_SupportsGradientsClippingAndUse()
    {
        using RepositorySvgDocument document = Load("""
            <svg xmlns="http://www.w3.org/2000/svg" width="32" height="24">
              <defs>
                <linearGradient id="paint"><stop stop-color="#00a86b"/><stop offset="1" stop-color="#246bce"/></linearGradient>
                <clipPath id="clip"><circle cx="16" cy="12" r="10"/></clipPath>
                <rect id="shape" width="32" height="24" fill="url(#paint)"/>
              </defs>
              <use href="#shape" clip-path="url(#clip)"/>
            </svg>
            """);

        RepositorySvgTile tile = _rasterizer.RasterizeTile(
            document,
            new RepositorySvgTileRequest(0, 0, 64, 48, 2),
            CancellationToken.None);

        Assert.Equal(0, AlphaAt(tile, 0, 0));
        Assert.True(AlphaAt(tile, 32, 24) > 200);
    }

    [Fact]
    public void Load_RejectsExternalResources()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>" +
            "<image href='https://example.com/tracker.png'/></svg>");

        Assert.Null(_rasterizer.Load(bytes, CancellationToken.None));
    }

    [Fact]
    public void Load_ObservesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _rasterizer.Load(
            Encoding.UTF8.GetBytes(
                "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>"),
            cancellation.Token));
    }

    [Fact]
    public void RasterizeTile_ObservesCancellation()
    {
        using RepositorySvgDocument document = Load(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _rasterizer.RasterizeTile(
            document,
            new RepositorySvgTileRequest(0, 0, 10, 10, 1),
            cancellation.Token));
    }

    private RepositorySvgDocument Load(string svg)
    {
        RepositorySvgDocument? document = _rasterizer.Load(
            Encoding.UTF8.GetBytes(svg),
            CancellationToken.None);
        return Assert.IsType<RepositorySvgDocument>(document);
    }

    private static byte BlueAt(RepositorySvgTile tile, int x, int y) =>
        tile.BgraPixels[PixelOffset(tile, x, y)];

    private static byte GreenAt(RepositorySvgTile tile, int x, int y) =>
        tile.BgraPixels[PixelOffset(tile, x, y) + 1];

    private static byte RedAt(RepositorySvgTile tile, int x, int y) =>
        tile.BgraPixels[PixelOffset(tile, x, y) + 2];

    private static byte AlphaAt(RepositorySvgTile tile, int x, int y) =>
        tile.BgraPixels[PixelOffset(tile, x, y) + 3];

    private static int PixelOffset(RepositorySvgTile tile, int x, int y) =>
        ((y * tile.PixelWidth) + x) * 4;
}
