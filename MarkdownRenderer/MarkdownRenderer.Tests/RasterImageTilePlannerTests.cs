using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class RasterImageTilePlannerTests
{
    [Fact]
    public void TryCreate_UsesOneTileForImagesWithinTheHardTileEdge()
    {
        Assert.True(RasterImageTilePlanner.TryCreate(1024, 1024, out RasterImageTilePlan plan));

        Assert.Equal(1024, plan.Width);
        Assert.Equal(1024, plan.Height);
        Assert.Equal(1, plan.Count);
        Assert.Equal(new RasterImageTile(0, 0, 1024, 1024), Assert.Single(plan.Tiles.ToArray()));
    }

    [Fact]
    public void TryCreate_SplitsNonMultipleDimensionsIntoExactRowMajorBounds()
    {
        Assert.True(RasterImageTilePlanner.TryCreate(1025, 1025, out RasterImageTilePlan plan));

        Assert.Equal(
        new[]
        {
            new RasterImageTile(0, 0, 1024, 1024),
            new RasterImageTile(1024, 0, 1, 1024),
            new RasterImageTile(0, 1024, 1024, 1),
            new RasterImageTile(1024, 1024, 1, 1),
        },
        plan.Tiles.ToArray());
    }

    [Theory]
    [InlineData(1, 16_384)]
    [InlineData(16_384, 1)]
    [InlineData(16_384, 16_384)]
    [InlineData(3_071, 5_123)]
    public void TryCreate_CoversEveryPixelExactlyOnceWithoutOversizedTiles(int width, int height)
    {
        Assert.True(RasterImageTilePlanner.TryCreate(width, height, out RasterImageTilePlan plan));

        RasterImageTile[] tiles = plan.Tiles.ToArray();
        Assert.Equal(plan.Count, tiles.Length);
        Assert.InRange(plan.Count, 1, RasterImageTilePlanner.MaxTileCount);
        Assert.Equal((long)width * height, tiles.Sum(tile => (long)tile.Width * tile.Height));

        foreach (RasterImageTile tile in tiles)
        {
            Assert.InRange(tile.X, 0, width - 1);
            Assert.InRange(tile.Y, 0, height - 1);
            Assert.InRange(tile.Width, 1, RasterImageTilePlanner.TileEdgePixels);
            Assert.InRange(tile.Height, 1, RasterImageTilePlanner.TileEdgePixels);
            Assert.InRange(tile.Right, 1, width);
            Assert.InRange(tile.Bottom, 1, height);
        }

        for (int leftIndex = 0; leftIndex < tiles.Length; leftIndex++)
        {
            RasterImageTile left = tiles[leftIndex];
            for (int rightIndex = leftIndex + 1; rightIndex < tiles.Length; rightIndex++)
            {
                RasterImageTile right = tiles[rightIndex];
                bool overlaps = left.X < right.Right && right.X < left.Right &&
                                left.Y < right.Bottom && right.Y < left.Bottom;
                Assert.False(overlaps, $"Tiles {left} and {right} overlap.");
            }
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(16_385, 1)]
    [InlineData(1, 16_385)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void TryCreate_RejectsInvalidDimensionsBeforeAllocating(int width, int height)
    {
        Assert.False(RasterImageTilePlanner.TryCreate(width, height, out RasterImageTilePlan plan));

        Assert.Equal(0, plan.Count);
        Assert.Empty(plan.Tiles.ToArray());
    }
}
