using System.Collections.Generic;
using MarkdownRenderer.Controls.Internal.Rendering.Tiles;
using Xunit;

namespace MarkdownRenderer.Tests.Controls.Internal.Rendering.Tiles;

public class DocumentTileCacheTests
{
    [Fact]
    public void NeedsPaint_UntilTheAssignedTileIsMarkedCurrent()
    {
        var cache = new DocumentTileCache<Tile>();
        var tile = new Tile();

        Assert.True(cache.NeedsPaint(3));
        cache.Assign(3, tile);
        Assert.True(cache.NeedsPaint(3));
        cache.MarkCurrent(3);

        Assert.False(cache.NeedsPaint(3));
        Assert.Same(tile, cache.Find(3));
    }

    [Fact]
    public void Assign_RejectsASecondTileForTheSameIndex()
    {
        var cache = new DocumentTileCache<Tile>();
        var tile = new Tile();
        cache.Assign(1, tile);

        cache.Assign(1, tile);
        Assert.Throws<InvalidOperationException>(() => cache.Assign(1, new Tile()));
    }

    [Fact]
    public void MarkStale_KeepsTheTileButRequiresARepaint()
    {
        DocumentTileCache<Tile> cache = CurrentTiles(0, 1, 2, 3, 4);

        cache.MarkStale(1, 2);

        Assert.False(cache.NeedsPaint(0));
        Assert.True(cache.NeedsPaint(1));
        Assert.True(cache.NeedsPaint(2));
        Assert.False(cache.NeedsPaint(3));
        Assert.NotNull(cache.Find(1));
    }

    [Fact]
    public void MarkStale_WithoutALastIndexMarksEveryTileBelow()
    {
        DocumentTileCache<Tile> cache = CurrentTiles(0, 1, 2, 3);

        cache.MarkStale(2);

        Assert.False(cache.NeedsPaint(1));
        Assert.True(cache.NeedsPaint(2));
        Assert.True(cache.NeedsPaint(3));
    }

    [Fact]
    public void MarkAllStale_RequiresEveryTileToRepaint()
    {
        DocumentTileCache<Tile> cache = CurrentTiles(0, 1);

        cache.MarkAllStale();

        Assert.True(cache.NeedsPaint(0));
        Assert.True(cache.NeedsPaint(1));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Evict_HidesUnplannedTilesAndKeepsBoundedSpares()
    {
        DocumentTileCache<Tile> cache = CurrentTiles(0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var plan = new DocumentTilePlan();
        Assert.True(DocumentTileGrid.TryCreate(800, 1, out DocumentTileGrid grid, tilePixelHeight: 100));
        plan.Update(grid, 0, 200, 10_000, velocity: 0, maximumTiles: 3);
        var hidden = new List<Tile>();
        var released = new List<Tile>();

        cache.Evict(plan, hidden.Add, released.Add);

        Assert.Equal(3, cache.Count);
        Assert.Equal(7, hidden.Count);
        Assert.Equal(DocumentTileCache<Tile>.MaximumSpares, cache.SpareCount);
        Assert.Equal(7 - DocumentTileCache<Tile>.MaximumSpares, released.Count);
        Assert.Null(cache.Find(5));
        Assert.NotNull(cache.TakeSpare());
    }

    [Fact]
    public void Evict_ReleasesEverythingWhenNothingIsPlanned()
    {
        DocumentTileCache<Tile> cache = CurrentTiles(0, 1, 2);
        var plan = new DocumentTilePlan();
        Assert.True(DocumentTileGrid.TryCreate(800, 1, out DocumentTileGrid grid, tilePixelHeight: 100));
        plan.Update(grid, 0, 100, 10_000, velocity: 0, maximumTiles: 1);
        cache.Evict(plan, _ => { }, _ => { });
        Assert.Equal(2, cache.SpareCount);
        plan.Update(grid, -9000, 100, 10_000, velocity: 0, maximumTiles: 1, offscreenReach: 100);
        var released = new List<Tile>();

        cache.Evict(plan, _ => { }, released.Add);

        Assert.Equal(3, released.Count);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.SpareCount);
    }

    [Fact]
    public void ReleaseSpares_KeepsAssignedTiles()
    {
        DocumentTileCache<Tile> cache = CurrentTiles(0, 1, 2);
        var plan = new DocumentTilePlan();
        Assert.True(DocumentTileGrid.TryCreate(800, 1, out DocumentTileGrid grid, tilePixelHeight: 100));
        plan.Update(grid, 0, 100, 10_000, velocity: 0, maximumTiles: 1);
        cache.Evict(plan, _ => { }, _ => { });
        var released = new List<Tile>();

        cache.ReleaseSpares(released.Add);

        Assert.Equal(2, released.Count);
        Assert.Equal(1, cache.Count);
        Assert.False(cache.NeedsPaint(0));
    }

    [Fact]
    public void Clear_ReleasesAssignedTilesAndSpares()
    {
        DocumentTileCache<Tile> cache = CurrentTiles(0, 1, 2);
        var plan = new DocumentTilePlan();
        Assert.True(DocumentTileGrid.TryCreate(800, 1, out DocumentTileGrid grid, tilePixelHeight: 100));
        plan.Update(grid, 0, 100, 10_000, velocity: 0, maximumTiles: 1);
        cache.Evict(plan, _ => { }, _ => { });
        var released = new List<Tile>();

        cache.Clear(released.Add);

        Assert.Equal(3, released.Count);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.SpareCount);
    }

    private static DocumentTileCache<Tile> CurrentTiles(params int[] indices)
    {
        var cache = new DocumentTileCache<Tile>();
        foreach (int index in indices)
        {
            cache.Assign(index, new Tile());
            cache.MarkCurrent(index);
        }

        return cache;
    }

    private sealed class Tile;
}
