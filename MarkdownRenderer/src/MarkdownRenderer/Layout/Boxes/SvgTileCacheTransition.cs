using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Images;
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
    CancellationTokenSource[] work,
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
    internal readonly CancellationTokenSource[] Work = work;
    internal readonly bool RestoreFallback = restoreFallback;
    internal readonly string? RestoredFallbackIdentityPrefix = restoredFallbackIdentityPrefix;
    internal readonly int RestoredFallbackWidthPixels = restoredFallbackWidthPixels;
    internal readonly int RestoredFallbackHeightPixels = restoredFallbackHeightPixels;
    internal readonly MarkdownSvgPhysicalViewport? PreviousPhysicalViewport = previousPhysicalViewport;
    internal readonly MarkdownSvgPhysicalViewport? RestoredFallbackPhysicalViewport = restoredFallbackPhysicalViewport;
}
