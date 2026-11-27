using System.Linq;
using MarkdownRenderer.Controls.Internal.Rendering.Tiles;
using Xunit;

namespace MarkdownRenderer.Tests.Controls.Internal.Rendering.Tiles;

public class DocumentTilePlanTests
{
    // 100 DIP tiles make the arithmetic below readable.
    private static DocumentTileGrid Grid()
    {
        Assert.True(DocumentTileGrid.TryCreate(800, 1, out DocumentTileGrid grid, tilePixelHeight: 100));
        return grid;
    }

    [Fact]
    public void Update_PaintsVisibleTilesFirstInOrder()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), viewportTop: 1050, viewportHeight: 300, documentHeight: 10_000, velocity: 0, maximumTiles: 10);

        Assert.Equal(10, plan.FirstVisible);
        Assert.Equal(13, plan.LastVisible);
        Assert.Equal([10, 11, 12, 13], plan.Order.Take(4));
    }

    [Fact]
    public void Update_AtRestAlternatesBelowAndAbove()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 1000, 200, 10_000, velocity: 0, maximumTiles: 6);

        Assert.Equal([10, 11, 12, 9, 13, 8], plan.Order);
        Assert.Equal(8, plan.First);
        Assert.Equal(13, plan.Last);
    }

    [Fact]
    public void Update_FastUpwardScrollFavoursTilesAbove()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 1000, 200, 10_000, velocity: -4000, maximumTiles: 10);

        Assert.Equal([10, 11, 9, 8, 7, 12, 6, 5, 4, 13], plan.Order);
    }

    [Fact]
    public void Update_FastDownwardScrollFavoursTilesBelow()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 1000, 200, 10_000, velocity: DocumentTilePlan.DirectionalSpeed, maximumTiles: 6);

        Assert.Equal([10, 11, 12, 13, 14, 9], plan.Order);
    }

    [Fact]
    public void Update_UsesTheOtherSideAtTheDocumentEdge()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 900, 100, documentHeight: 1000, velocity: 4000, maximumTiles: 4);

        Assert.Equal([9, 8, 7, 6], plan.Order);
        Assert.True(plan.Contains(6));
        Assert.False(plan.Contains(10));
    }

    [Fact]
    public void Update_NeverPlansFewerThanTheVisibleTiles()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 0, 450, 10_000, velocity: 0, maximumTiles: 1);

        Assert.Equal([0, 1, 2, 3, 4], plan.Order);
    }

    [Fact]
    public void Update_PlansAContiguousRun()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 2000, 300, 10_000, velocity: 2500, maximumTiles: 17);

        int[] sorted = [.. plan.Order.Order()];
        Assert.Equal(Enumerable.Range(plan.First, plan.Last - plan.First + 1), sorted);
    }

    [Fact]
    public void Update_PlansTheNearestTilesOfADocumentAboveTheViewport()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 5000, 400, documentHeight: 1000, velocity: 0, maximumTiles: 3);

        Assert.Equal([9, 8, 7], plan.Order);
        Assert.Equal(7, plan.First);
        Assert.Equal(9, plan.Last);
        Assert.False(plan.IsVisible(9));
    }

    [Fact]
    public void Update_PlansOnlyTilesWithinReachOfADocumentBelowTheViewport()
    {
        var plan = new DocumentTilePlan();

        // The viewport ends 150 DIPs above the document; 400 DIPs of reach
        // cover the document's first 250 DIPs.
        plan.Update(Grid(), -650, 500, documentHeight: 1000, velocity: 0, maximumTiles: 8, offscreenReach: 400);

        Assert.Equal([0, 1, 2], plan.Order);
        Assert.Equal(0, plan.First);
        Assert.Equal(2, plan.Last);
        Assert.False(plan.IsVisible(0));
    }

    [Fact]
    public void Update_PlansOnlyTilesWithinReachOfADocumentAboveTheViewport()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 1100, 500, documentHeight: 1000, velocity: 0, maximumTiles: 8, offscreenReach: 250);

        Assert.Equal([9, 8], plan.Order);
        Assert.Equal(8, plan.First);
        Assert.Equal(9, plan.Last);
    }

    [Theory]
    [InlineData(-5000.0)]
    [InlineData(6000.0)]
    public void Update_PlansNothingForADocumentOutOfReach(double viewportTop)
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), viewportTop, 500, documentHeight: 1000, velocity: 0, maximumTiles: 8, offscreenReach: 2000);

        Assert.Empty(plan.Order);
        Assert.False(plan.Contains(0));
        Assert.False(plan.Contains(9));
    }

    [Fact]
    public void Update_PlansNothingWithoutADocumentOrViewport()
    {
        var plan = new DocumentTilePlan();

        plan.Update(Grid(), 0, 400, documentHeight: 0, velocity: 0, maximumTiles: 8);
        Assert.Empty(plan.Order);
        Assert.False(plan.Contains(0));

        plan.Update(Grid(), double.NaN, 400, documentHeight: 1000, velocity: 0, maximumTiles: 8);
        Assert.Empty(plan.Order);
        Assert.False(plan.IsVisible(0));
    }
}
