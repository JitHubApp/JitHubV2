using System.Collections.Generic;
using System.Threading;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One consistent read of an <see cref="SvgTileCache"/>, taken under its tile
/// lock: copies of the tile, fallback and in-flight maps, the grid and
/// fallback identities, and the viewport observation.
/// </summary>
internal readonly record struct SvgTileCacheSnapshot(
    IReadOnlyDictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> Tiles,
    IReadOnlyDictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? FallbackTiles,
    IReadOnlyDictionary<SvgTileKey, RetirableCancellationTokenSource> Work,
    string? IdentityPrefix,
    int WidthPixels,
    int HeightPixels,
    string? FallbackIdentityPrefix,
    int FallbackWidthPixels,
    int FallbackHeightPixels,
    bool HasRenderedTile,
    bool HasPendingTransition,
    int ViewportVisibility,
    bool VisibleRangeKnown,
    int FirstVisibleTileX,
    int FirstVisibleTileY,
    int LastVisibleTileX,
    int LastVisibleTileY,
    long ViewportObservationVersion);
