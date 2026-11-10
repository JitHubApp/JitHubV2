using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One pending swap of an image box's SVG tile cache: the expected and
/// replacement tiles, the leases it retires, and the state to restore if the
/// swap is abandoned.
/// </summary>
internal sealed class SvgTileCacheTransition(
    object token,
    SvgTileRenderPlan? previousPlan,
    SvgTileRenderPlan? nextPlan,
    Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> expectedTiles,
    Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? expectedFallbackTiles,
    string? previousIdentityPrefix,
    int previousWidthPixels,
    int previousHeightPixels,
    bool sameIdentity,
    Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? replacementTiles,
    SharedCanvasBitmapCache.Lease[]? retiredTiles,
    SharedCanvasBitmapCache.Lease[]? retiredFallbackTiles,
    RetirableCancellationTokenSource[] work,
    bool restoreFallback = false,
    string? restoredFallbackIdentityPrefix = null,
    int restoredFallbackWidthPixels = 0,
    int restoredFallbackHeightPixels = 0,
    MarkdownSvgPhysicalViewport? previousPhysicalViewport = null,
    MarkdownSvgPhysicalViewport? restoredFallbackPhysicalViewport = null)
{
    internal readonly object Token = token;
    internal readonly SvgTileRenderPlan? PreviousPlan = previousPlan;
    internal readonly SvgTileRenderPlan? NextPlan = nextPlan;
    internal readonly Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> ExpectedTiles = expectedTiles;
    internal readonly Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? ExpectedFallbackTiles = expectedFallbackTiles;
    internal readonly string? PreviousIdentityPrefix = previousIdentityPrefix;
    internal readonly int PreviousWidthPixels = previousWidthPixels;
    internal readonly int PreviousHeightPixels = previousHeightPixels;
    internal readonly bool SameIdentity = sameIdentity;
    internal readonly Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? ReplacementTiles = replacementTiles;
    internal readonly SharedCanvasBitmapCache.Lease[]? RetiredTiles = retiredTiles;
    internal readonly SharedCanvasBitmapCache.Lease[]? RetiredFallbackTiles = retiredFallbackTiles;
    internal readonly RetirableCancellationTokenSource[] Work = work;
    internal readonly bool RestoreFallback = restoreFallback;
    internal readonly string? RestoredFallbackIdentityPrefix = restoredFallbackIdentityPrefix;
    internal readonly int RestoredFallbackWidthPixels = restoredFallbackWidthPixels;
    internal readonly int RestoredFallbackHeightPixels = restoredFallbackHeightPixels;
    internal readonly MarkdownSvgPhysicalViewport? PreviousPhysicalViewport = previousPhysicalViewport;
    internal readonly MarkdownSvgPhysicalViewport? RestoredFallbackPhysicalViewport = restoredFallbackPhysicalViewport;

    /// <summary>
    /// Stages the swap for a newly accepted plan (or for detaching every tile
    /// when <paramref name="plan"/> is null) from the cache state read under
    /// its tile lock. Every array and dictionary the swap needs is allocated
    /// here, before any tile state changes.
    /// </summary>
    internal static SvgTileCacheTransition ForPublication(
        object token,
        SvgTileRenderPlan? previousPlan,
        SvgTileRenderPlan? plan,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> expectedTiles,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? expectedFallback,
        string? previousIdentityPrefix,
        int previousWidth,
        int previousHeight,
        MarkdownSvgPhysicalViewport? previousPhysicalViewport,
        Dictionary<SvgTileKey, RetirableCancellationTokenSource>.ValueCollection inFlightWork)
    {
        bool sameIdentity = plan is not null &&
            string.Equals(previousIdentityPrefix, plan.IdentityPrefix, StringComparison.Ordinal) &&
            previousWidth == plan.FullWidthPixels &&
            previousHeight == plan.FullHeightPixels &&
            Nullable.Equals(previousPhysicalViewport, plan.PhysicalViewport);

        SharedCanvasBitmapCache.Lease[]? retiredTiles = null;
        SharedCanvasBitmapCache.Lease[]? retiredFallback = null;
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? replacementTiles = null;
        if (plan is null)
        {
            retiredTiles = expectedTiles.Values.ToArray();
            if (expectedFallback is not null)
                retiredFallback = expectedFallback.Values.ToArray();
            replacementTiles = new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
        }
        else if (!sameIdentity)
        {
            if (expectedTiles.Count > 0)
            {
                if (expectedFallback is not null)
                    retiredFallback = expectedFallback.Values.ToArray();
                replacementTiles = new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
            }
            else if (expectedFallback is null)
            {
                replacementTiles = new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
            }
        }

        RetirableCancellationTokenSource[] work = inFlightWork.Count == 0
            ? Array.Empty<RetirableCancellationTokenSource>()
            : inFlightWork.ToArray();
        return new SvgTileCacheTransition(
            token,
            previousPlan,
            plan,
            expectedTiles,
            expectedFallback,
            previousIdentityPrefix,
            previousWidth,
            previousHeight,
            sameIdentity,
            replacementTiles,
            retiredTiles,
            retiredFallback,
            work,
            previousPhysicalViewport: previousPhysicalViewport);
    }

    /// <summary>
    /// Stages the swap that detaches <paramref name="plan"/> after a terminal
    /// tile failure, restoring the previous good grid when one is resident.
    /// </summary>
    internal static SvgTileCacheTransition ForFailure(
        object token,
        SvgTileRenderPlan plan,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> expectedTiles,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? expectedFallback,
        string? identityPrefix,
        int widthPixels,
        int heightPixels,
        MarkdownSvgPhysicalViewport? physicalViewport,
        string? fallbackIdentityPrefix,
        int fallbackWidthPixels,
        int fallbackHeightPixels,
        MarkdownSvgPhysicalViewport? fallbackPhysicalViewport,
        Dictionary<SvgTileKey, RetirableCancellationTokenSource>.ValueCollection inFlightWork)
    {
        bool restoreFallback = expectedFallback is { Count: > 0 };

        // Build every potentially allocating piece before the work
        // owner is invalidated. The publication token prevents new
        // tile admission while this transition is staged.
        SharedCanvasBitmapCache.Lease[] retiredTiles = expectedTiles.Count == 0
            ? Array.Empty<SharedCanvasBitmapCache.Lease>()
            : expectedTiles.Values.ToArray();
        SharedCanvasBitmapCache.Lease[]? retiredFallback = !restoreFallback &&
            expectedFallback is { Count: > 0 }
            ? expectedFallback.Values.ToArray()
            : null;
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> replacementTiles = restoreFallback
            ? expectedFallback!
            : new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
        RetirableCancellationTokenSource[] work = inFlightWork.Count == 0
            ? Array.Empty<RetirableCancellationTokenSource>()
            : inFlightWork.ToArray();

        return new SvgTileCacheTransition(
            token,
            plan,
            nextPlan: null,
            expectedTiles,
            expectedFallback,
            identityPrefix,
            widthPixels,
            heightPixels,
            sameIdentity: false,
            replacementTiles,
            retiredTiles,
            retiredFallback,
            work,
            restoreFallback,
            restoreFallback ? fallbackIdentityPrefix : null,
            restoreFallback ? fallbackWidthPixels : 0,
            restoreFallback ? fallbackHeightPixels : 0,
            previousPhysicalViewport: physicalViewport,
            restoredFallbackPhysicalViewport: restoreFallback ? fallbackPhysicalViewport : null);
    }
}
