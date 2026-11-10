using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using Xunit;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Puts an <see cref="SvgTileCache"/> into a state through the same
/// operations the image box uses: a publication transition, admitted tile
/// work, and a published tile.
/// </summary>
internal static class SvgTileCacheTestOperations
{
    /// <summary>
    /// Swaps <paramref name="cache"/> to <paramref name="plan"/>'s grid as a
    /// publication commit does; <paramref name="accept"/> installs the plan as
    /// the host's accepted plan between preparing and applying the transition.
    /// </summary>
    internal static void Adopt(
        SvgTileCache cache,
        SvgTileRenderPlan? plan,
        Action<SvgTileRenderPlan?> accept)
    {
        SvgTileCacheTransition transition = cache.PrepareSvgTileCacheTransition(plan)
            ?? throw new Xunit.Sdk.XunitException("A tile-cache transition is already staged.");
        accept(plan);
        Assert.True(cache.ApplySvgTileCacheTransition(transition));
        cache.RetireSvgTileCacheTransition(transition);
    }

    /// <summary>Admits render work for <paramref name="key"/>, optionally claiming its failure.</summary>
    internal static SvgTileWorkOwner AdmitWork(
        SvgTileCache cache,
        SvgTileRenderPlan plan,
        SvgTileKey key,
        RetirableCancellationTokenSource cancellation,
        bool claimFailure = false)
    {
        var owner = new SvgTileWorkOwner(plan, cancellation, plan.Demand);
        Assert.True(cache.TryAdmitTileWork(plan, key, owner));
        if (claimFailure)
            Assert.True(cache.TryClaimFailedSvgTile(plan, key, owner));
        return owner;
    }

    /// <summary>Publishes <paramref name="lease"/> for <paramref name="key"/> through admitted, completed tile work.</summary>
    internal static void PublishTile(
        SvgTileCache cache,
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SharedCanvasBitmapCache.Lease lease)
    {
        using var cancellation = new RetirableCancellationTokenSource();
        _ = AdmitWork(cache, plan, key, cancellation);
        SvgTilePublicationOwnership ownership = default;
        SharedCanvasBitmapCache.Lease? previous = null;
        bool transferred = false;
        Assert.True(cache.TryPublishTile(
            plan, key, cancellation, rendererCurrent: true, lease, ref ownership, ref previous, ref transferred));
        Assert.True(transferred);
        previous?.Dispose();
        cache.CompleteSvgTileWork(key, cancellation);
    }

    /// <summary>Whether every tile in the range is resident for the image's current raster size.</summary>
    internal static bool HasAllTiles(ImageBox image, int firstTileX, int firstTileY, int lastTileX, int lastTileY)
    {
        (int width, int height) = image.SvgRasterPixelSize;
        return image.SvgTileCache.HasAllTiles(firstTileX, firstTileY, lastTileX, lastTileY, width, height);
    }
}
