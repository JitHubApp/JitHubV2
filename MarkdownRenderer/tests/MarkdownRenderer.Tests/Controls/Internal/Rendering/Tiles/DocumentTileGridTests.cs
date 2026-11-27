using MarkdownRenderer.Controls.Internal.Rendering.Tiles;
using Xunit;

namespace MarkdownRenderer.Tests.Controls.Internal.Rendering.Tiles;

public class DocumentTileGridTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(800, 0)]
    [InlineData(800, double.PositiveInfinity)]
    public void TryCreate_RejectsUnusableWidthOrScale(double width, double scale)
    {
        Assert.False(DocumentTileGrid.TryCreate(width, scale, out _));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void Tiles_StartOnWholeDevicePixelsAndMeetWithoutSeams(double scale)
    {
        Assert.True(DocumentTileGrid.TryCreate(800.4, scale, out DocumentTileGrid grid));

        for (int index = 0; index < 50; index++)
        {
            double top = grid.TopOf(index);
            double devicePixelTop = top * scale;
            Assert.Equal(Math.Round(devicePixelTop), devicePixelTop, 6);
            Assert.Equal(grid.TopOf(index + 1), top + grid.TileHeight, 6);
            Assert.Equal(index, grid.IndexAt(top + grid.TileHeight / 2));
        }
    }

    [Fact]
    public void PixelWidth_CoversTheDocumentWidthAndIsCapped()
    {
        Assert.True(DocumentTileGrid.TryCreate(800.4, 1.5, out DocumentTileGrid grid));
        Assert.Equal(1201, grid.PixelWidth);
        Assert.Equal(1201L * DocumentTileGrid.DefaultTilePixelHeight * 4, grid.TileBytes);

        Assert.True(DocumentTileGrid.TryCreate(1_000_000, 2, out DocumentTileGrid wide));
        Assert.Equal(DocumentTileGrid.MaximumPixelWidth, wide.PixelWidth);
    }

    [Theory]
    [InlineData(10.0, 1.0, 128)]
    [InlineData(128.0, 1.0, 128)]
    [InlineData(128.5, 1.0, 256)]
    [InlineData(150.0, 1.5, 256)]
    [InlineData(400.0, 1.25, 512)]
    [InlineData(5000.0, 1.0, 512)]
    [InlineData(0.0, 1.0, 512)]
    [InlineData(double.NaN, 1.0, 512)]
    public void PixelHeightFor_FitsAShortDocumentInOneSteppedTile(double documentHeight, double scale, int expected)
    {
        Assert.Equal(expected, DocumentTileGrid.PixelHeightFor(documentHeight, scale));
    }

    [Fact]
    public void PixelHeightFor_ShortDocumentNeedsOneTile()
    {
        int pixelHeight = DocumentTileGrid.PixelHeightFor(150, 1.5);
        Assert.True(DocumentTileGrid.TryCreate(800, 1.5, out DocumentTileGrid grid, pixelHeight));

        Assert.Equal(1, grid.CountFor(150));
    }

    [Fact]
    public void IndexAt_ClampsAboveTheDocumentAndRejectsNonFiniteY()
    {
        Assert.True(DocumentTileGrid.TryCreate(800, 1, out DocumentTileGrid grid, tilePixelHeight: 100));

        Assert.Equal(0, grid.IndexAt(-250));
        Assert.Equal(0, grid.IndexAt(double.NaN));
        Assert.Equal(2, grid.IndexAt(299.9));
        Assert.Equal(3, grid.IndexAt(300));
    }

    [Fact]
    public void CountFor_CoversAPartialLastTile()
    {
        Assert.True(DocumentTileGrid.TryCreate(800, 2, out DocumentTileGrid grid, tilePixelHeight: 100));

        Assert.Equal(0, grid.CountFor(0));
        Assert.Equal(0, grid.CountFor(double.NaN));
        Assert.Equal(1, grid.CountFor(50));
        Assert.Equal(2, grid.CountFor(50.5));
    }

    [Fact]
    public void HasSameRaster_ChangesWithWidthOrScale()
    {
        Assert.True(DocumentTileGrid.TryCreate(800, 1.5, out DocumentTileGrid grid));
        Assert.True(DocumentTileGrid.TryCreate(800, 1.5, out DocumentTileGrid same));
        Assert.True(DocumentTileGrid.TryCreate(801, 1.5, out DocumentTileGrid wider));
        Assert.True(DocumentTileGrid.TryCreate(800, 2, out DocumentTileGrid sharper));

        Assert.True(grid.HasSameRaster(same));
        Assert.False(grid.HasSameRaster(wider));
        Assert.False(grid.HasSameRaster(sharper));
    }
}
