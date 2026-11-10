using System.Threading;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using Xunit;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Tile-grid transitions and tile-work currency of <see cref="SvgTileCache"/>,
/// driven through its own operations over a box-free host.
/// </summary>
public sealed class SvgTileCacheTests
{
    private const int TileSize = SvgTileCache.SvgTileSizePixels;

    [Fact]
    public void TileCacheReplacementIsPreparedBeforeTheAcceptedPlanAndAppliedAtomically()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan oldPlan = TestSvgTileCacheHost.Plan("svg-cache-prefix:transition-old", 2048, 1536, 51);
        SvgTileRenderPlan newPlan = TestSvgTileCacheHost.Plan("svg-cache-prefix:transition-new", 3072, 1536, 52);
        SvgTileKey oldKey = (0, 0, 1024, 1024);
        var oldLease = new SharedCanvasBitmapCache.Lease(null!);
        host.Accept(oldPlan);
        host.PublishTile(oldPlan, oldKey, oldLease);

        SvgTileCacheTransition transition = host.Cache.PrepareSvgTileCacheTransition(newPlan)
            ?? throw new Xunit.Sdk.XunitException("Could not prepare SVG tile cache transition.");

        SvgTileCacheSnapshot staged = host.Cache.CaptureSnapshot();
        Assert.Same(oldLease, Assert.Single(staged.Tiles).Value);
        Assert.Null(staged.FallbackTiles);
        Assert.Equal(oldPlan.IdentityPrefix, staged.IdentityPrefix);
        Assert.True(staged.HasPendingTransition);
        Assert.False(host.Cache.IsCurrentSvgTilePlan(oldPlan));

        host.AcceptedSvgTilePlan = newPlan;
        Assert.True(host.Cache.ApplySvgTileCacheTransition(transition));
        host.Cache.RetireSvgTileCacheTransition(transition);

        SvgTileCacheSnapshot applied = host.Cache.CaptureSnapshot();
        Assert.Empty(applied.Tiles);
        KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> fallback =
            Assert.Single(applied.FallbackTiles ?? throw new Xunit.Sdk.XunitException("No fallback grid."));
        Assert.Equal(oldKey, fallback.Key);
        Assert.Same(oldLease, fallback.Value);
        Assert.Equal(oldPlan.IdentityPrefix, applied.FallbackIdentityPrefix);
        Assert.Equal(oldPlan.FullWidthPixels, applied.FallbackWidthPixels);
        Assert.Equal(oldPlan.FullHeightPixels, applied.FallbackHeightPixels);
        Assert.Equal(newPlan.IdentityPrefix, applied.IdentityPrefix);
        Assert.Equal(newPlan.FullWidthPixels, applied.WidthPixels);
        Assert.Equal(newPlan.FullHeightPixels, applied.HeightPixels);
        Assert.Equal(-1, applied.ViewportVisibility);
        Assert.False(applied.HasPendingTransition);
        Assert.True(host.Cache.IsCurrentSvgTilePlan(newPlan));
        Assert.Empty(host.RetirementFailures);
        host.ReleaseAll();
    }

    [Fact]
    public void AbortedTileCacheTransitionPreservesTheAcceptedPlanAndCacheIdentity()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan oldPlan = TestSvgTileCacheHost.Plan("svg-cache-prefix:transition-abort-old", 2048, 1536, 53);
        SvgTileRenderPlan candidate = TestSvgTileCacheHost.Plan("svg-cache-prefix:transition-abort-new", 3072, 1536, 54);
        host.Accept(oldPlan);

        SvgTileCacheTransition transition = host.Cache.PrepareSvgTileCacheTransition(candidate)
            ?? throw new Xunit.Sdk.XunitException("Could not prepare SVG tile cache transition.");
        host.Cache.AbortSvgTileCacheTransition(transition);

        SvgTileCacheSnapshot snapshot = host.Cache.CaptureSnapshot();
        Assert.False(snapshot.HasPendingTransition);
        Assert.Same(oldPlan, host.AcceptedSvgTilePlan);
        Assert.Equal(oldPlan.IdentityPrefix, snapshot.IdentityPrefix);
        Assert.Equal(oldPlan.FullWidthPixels, snapshot.WidthPixels);
        Assert.Equal(oldPlan.FullHeightPixels, snapshot.HeightPixels);
        Assert.True(host.Cache.IsCurrentSvgTilePlan(oldPlan));
    }

    [Fact]
    public void FailedTiledPlanWithoutFallbackDetachesItsTileLeasesAndCacheIdentity()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan plan = TestSvgTileCacheHost.Plan(
            "svg-cache-prefix:terminal-failure-no-fallback", 2048, 1536, 58);
        host.Accept(plan);
        host.PublishTile(plan, (0, 0, TileSize, TileSize), new SharedCanvasBitmapCache.Lease(null!));
        host.Cache.MarkTileRendered();

        SvgTileCacheTransition transition = host.Cache.PrepareSvgTileFailureTransition(plan)
            ?? throw new Xunit.Sdk.XunitException("Could not prepare SVG tile failure transition.");
        host.AcceptedSvgTilePlan = null;
        Assert.True(host.Cache.ApplySvgTileCacheTransition(transition));
        host.Cache.RetireSvgTileCacheTransition(transition);

        SvgTileCacheSnapshot snapshot = host.Cache.CaptureSnapshot();
        Assert.Empty(snapshot.Tiles);
        Assert.Null(snapshot.FallbackTiles);
        Assert.Null(snapshot.IdentityPrefix);
        Assert.Equal(0, snapshot.WidthPixels);
        Assert.Equal(0, snapshot.HeightPixels);
        Assert.False(snapshot.HasRenderedTile);
        Assert.False(snapshot.HasPendingTransition);
        Assert.Empty(host.RetirementFailures);
    }

    [Fact]
    public void FailedTiledPlanRestoresFallbackIdentityWithoutRetainingFailedTiles()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan previousGood = TestSvgTileCacheHost.Plan("svg-cache-prefix:previous-good", 1024, 768, 58);
        SvgTileRenderPlan plan = TestSvgTileCacheHost.Plan(
            "svg-cache-prefix:terminal-failure-with-fallback", 2048, 1536, 59);
        SvgTileKey failedKey = (0, 0, TileSize, TileSize);
        SvgTileKey fallbackKey = (TileSize, 0, TileSize, TileSize);
        var failedLease = new SharedCanvasBitmapCache.Lease(null!);
        var fallbackLease = new SharedCanvasBitmapCache.Lease(null!);
        host.Accept(previousGood);
        host.PublishTile(previousGood, fallbackKey, fallbackLease);
        host.Accept(plan);
        host.PublishTile(plan, failedKey, failedLease);

        SvgTileCacheTransition transition = host.Cache.PrepareSvgTileFailureTransition(plan)
            ?? throw new Xunit.Sdk.XunitException("Could not prepare SVG tile failure transition.");
        host.AcceptedSvgTilePlan = null;
        Assert.True(host.Cache.ApplySvgTileCacheTransition(transition));
        host.Cache.RetireSvgTileCacheTransition(transition);

        SvgTileCacheSnapshot snapshot = host.Cache.CaptureSnapshot();
        KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> restored = Assert.Single(snapshot.Tiles);
        Assert.Equal(fallbackKey, restored.Key);
        Assert.Same(fallbackLease, restored.Value);
        Assert.NotSame(failedLease, restored.Value);
        Assert.Null(snapshot.FallbackTiles);
        Assert.Equal("svg-cache-prefix:previous-good", snapshot.IdentityPrefix);
        Assert.Equal(1024, snapshot.WidthPixels);
        Assert.Equal(768, snapshot.HeightPixels);
        Assert.True(snapshot.HasRenderedTile);
        Assert.Null(snapshot.FallbackIdentityPrefix);
        Assert.Empty(host.RetirementFailures);
        host.ReleaseAll();
    }

    [Fact]
    public void BitmapTransitionStagesAndDetachesAllOldTileIdentities()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan earlierPlan = TestSvgTileCacheHost.Plan("svg-cache-prefix:bitmap-transition-earlier", 2048, 2048, 54);
        SvgTileRenderPlan oldPlan = TestSvgTileCacheHost.Plan("svg-cache-prefix:bitmap-transition", 2048, 2048, 55);
        host.Accept(earlierPlan);
        host.PublishTile(earlierPlan, (1024, 0, 1024, 1024), new SharedCanvasBitmapCache.Lease(null!));
        host.Accept(oldPlan);
        host.PublishTile(oldPlan, (0, 0, 1024, 1024), new SharedCanvasBitmapCache.Lease(null!));
        Assert.NotNull(host.Cache.CaptureSnapshot().FallbackTiles);

        SvgTileCacheTransition transition = host.Cache.PrepareSvgTileCacheTransition(plan: null)
            ?? throw new Xunit.Sdk.XunitException("Could not prepare SVG tile cache transition.");
        host.AcceptedSvgTilePlan = null;
        Assert.True(host.Cache.ApplySvgTileCacheTransition(transition));
        host.Cache.RetireSvgTileCacheTransition(transition);

        SvgTileCacheSnapshot snapshot = host.Cache.CaptureSnapshot();
        Assert.Empty(snapshot.Tiles);
        Assert.Null(snapshot.FallbackTiles);
        Assert.Null(snapshot.IdentityPrefix);
        Assert.Equal(0, snapshot.WidthPixels);
        Assert.Equal(0, snapshot.HeightPixels);
        Assert.False(snapshot.HasPendingTransition);
        Assert.Empty(host.RetirementFailures);
    }

    [Fact]
    public void RemovedTileOwnerCannotPublishSuccess()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan plan = TestSvgTileCacheHost.Plan("svg-cache-prefix:removed-success-owner", 2048, 2048, 40);
        host.Accept(plan);
        using var work = new RetirableCancellationTokenSource();
        SvgTileKey key = (0, 0, TileSize, TileSize);
        _ = SvgTileCacheTestOperations.AdmitWork(host.Cache, plan, key, work);
        host.Cache.CompleteSvgTileWork(key, work);

        Assert.False(TryPublish(host.Cache, plan, key, work));
        Assert.Empty(host.Cache.CaptureSnapshot().Tiles);
    }

    [Fact]
    public void ReassignedTileOwnerCannotPublishSuccess()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan plan = TestSvgTileCacheHost.Plan("svg-cache-prefix:reassigned-success-owner", 2048, 2048, 42);
        host.Accept(plan);
        using var staleWork = new RetirableCancellationTokenSource();
        using var currentWork = new RetirableCancellationTokenSource();
        SvgTileKey key = (0, 0, TileSize, TileSize);
        _ = new SvgTileWorkOwner(plan, staleWork, plan.Demand);
        _ = SvgTileCacheTestOperations.AdmitWork(host.Cache, plan, key, currentWork);

        Assert.False(TryPublish(host.Cache, plan, key, staleWork));
        SvgTileCacheSnapshot snapshot = host.Cache.CaptureSnapshot();
        Assert.Empty(snapshot.Tiles);
        Assert.Same(currentWork, snapshot.Work[key]);
    }

    [Fact]
    public void ExactTileOwnerCanPublishSuccess()
    {
        var host = new TestSvgTileCacheHost();
        SvgTileRenderPlan plan = TestSvgTileCacheHost.Plan("svg-cache-prefix:current-success-owner", 2048, 2048, 44);
        host.Accept(plan);
        using var work = new RetirableCancellationTokenSource();
        SvgTileKey key = (0, 0, TileSize, TileSize);
        _ = SvgTileCacheTestOperations.AdmitWork(host.Cache, plan, key, work);

        Assert.True(TryPublish(host.Cache, plan, key, work));
        Assert.Single(host.Cache.CaptureSnapshot().Tiles);
        host.ReleaseAll();
    }

    private static bool TryPublish(
        SvgTileCache cache,
        SvgTileRenderPlan plan,
        SvgTileKey key,
        RetirableCancellationTokenSource work)
    {
        SvgTilePublicationOwnership ownership = default;
        SharedCanvasBitmapCache.Lease? previous = null;
        bool transferred = false;
        bool accepted = cache.TryPublishTile(
            plan,
            key,
            work,
            rendererCurrent: true,
            new SharedCanvasBitmapCache.Lease(null!),
            ref ownership,
            ref previous,
            ref transferred);
        Assert.Equal(accepted, transferred);
        Assert.Null(previous);
        return accepted;
    }
}
