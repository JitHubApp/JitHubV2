using System;
using System.Collections.Generic;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using Windows.Foundation;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;

/// <summary>
/// The geometry rule that decides which tiles of an image box's previous SVG
/// tile grid stay resident as a fallback while the current grid fills in.
/// </summary>
internal static class SvgFallbackTileRetention
{
    /// <summary>
    /// Whether a previous-grid fallback tile must stay resident: it is kept
    /// while it may still cover a visible hole in the current grid, and also
    /// whenever the geometry is invalid and cannot prove it is replaced.
    /// </summary>
    internal static bool ShouldRetain(
        SvgTileKey key,
        int fallbackWidthPixels,
        int fallbackHeightPixels,
        int currentWidthPixels,
        int currentHeightPixels,
        Rect destination,
        Rect visible,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> currentTiles,
        MarkdownSvgPhysicalViewport? fallbackViewport = null,
        MarkdownSvgPhysicalViewport? currentViewport = null)
    {
        // Invalid geometry cannot establish that the previous good image is
        // invisible or replaced. Preserve it until a valid observation arrives.
        long keyRight = (long)key.X + key.Width;
        long keyBottom = (long)key.Y + key.Height;
        if (currentTiles is null ||
            fallbackWidthPixels <= 0 || fallbackHeightPixels <= 0 ||
            currentWidthPixels <= 0 || currentHeightPixels <= 0 ||
            key.X < 0 || key.Y < 0 || key.Width <= 0 || key.Height <= 0 ||
            keyRight > fallbackWidthPixels || keyBottom > fallbackHeightPixels ||
            !IsFiniteNonemptyRect(destination) || !IsFiniteNonemptyRect(visible))
        {
            return true;
        }

        double visibleLeft = Math.Max(destination.Left, visible.Left);
        double visibleTop = Math.Max(destination.Top, visible.Top);
        double visibleRight = Math.Min(destination.Right, visible.Right);
        double visibleBottom = Math.Min(destination.Bottom, visible.Bottom);
        if (visibleLeft >= visibleRight || visibleTop >= visibleBottom)
            return true;

        // The previous bitmap has its own physical grid. Divide first to avoid
        // overflow for extreme but finite DIP coordinates; keep boundary-touching
        // tiles within one old-grid tile of the clipped viewport.
        double fallbackWidth = fallbackViewport?.Width ?? fallbackWidthPixels;
        double fallbackHeight = fallbackViewport?.Height ?? fallbackHeightPixels;
        double currentWidth = currentViewport?.Width ?? currentWidthPixels;
        double currentHeight = currentViewport?.Height ?? currentHeightPixels;
        double bandLeft = (visibleLeft - destination.Left) / destination.Width * fallbackWidth - SvgTileCache.SvgTileSizePixels;
        double bandTop = (visibleTop - destination.Top) / destination.Height * fallbackHeight - SvgTileCache.SvgTileSizePixels;
        double bandRight = (visibleRight - destination.Left) / destination.Width * fallbackWidth + SvgTileCache.SvgTileSizePixels;
        double bandBottom = (visibleBottom - destination.Top) / destination.Height * fallbackHeight + SvgTileCache.SvgTileSizePixels;
        if (keyRight < bandLeft || key.X > bandRight ||
            keyBottom < bandTop || key.Y > bandBottom)
        {
            return false;
        }

        // Cover the entire old tile, not just its visible portion. Exact rational
        // floor/ceiling prevents DPI changes from hiding a one-pixel gap. All
        // products fit in Int64 because the admitted dimensions fit in Int32.
        long left = (long)key.X * currentWidthPixels / fallbackWidthPixels;
        long top = (long)key.Y * currentHeightPixels / fallbackHeightPixels;
        long rightProduct = keyRight * currentWidthPixels;
        long bottomProduct = keyBottom * currentHeightPixels;
        long right = rightProduct / fallbackWidthPixels + (rightProduct % fallbackWidthPixels == 0 ? 0 : 1);
        long bottom = bottomProduct / fallbackHeightPixels + (bottomProduct % fallbackHeightPixels == 0 ? 0 : 1);
        if (fallbackWidth != fallbackWidthPixels || fallbackHeight != fallbackHeightPixels ||
            currentWidth != currentWidthPixels || currentHeight != currentHeightPixels)
        {
            // Fractional viewports are independent of their integral allocation.
            // Bias one ULP outward: retention may be conservative, never hide a
            // still-uncovered edge during a resize or mixed-DPI transition.
            left = (long)Math.Clamp(Math.Floor(Math.BitDecrement(key.X / fallbackWidth * currentWidth)), 0, currentWidthPixels - 1);
            top = (long)Math.Clamp(Math.Floor(Math.BitDecrement(key.Y / fallbackHeight * currentHeight)), 0, currentHeightPixels - 1);
            right = (long)Math.Clamp(Math.Ceiling(Math.BitIncrement(Math.Min(keyRight, fallbackWidth) / fallbackWidth * currentWidth)), 1, currentWidthPixels);
            bottom = (long)Math.Clamp(Math.Ceiling(Math.BitIncrement(Math.Min(keyBottom, fallbackHeight) / fallbackHeight * currentHeight)), 1, currentHeightPixels);
        }
        return !AreCurrentTilesResident(
            left, top, right, bottom, currentWidthPixels, currentHeightPixels, currentTiles);
    }

    // Whether every current-grid tile overlapping the physical pixel span
    // [left, right) x [top, bottom) is resident.
    private static bool AreCurrentTilesResident(
        long left,
        long top,
        long right,
        long bottom,
        int currentWidthPixels,
        int currentHeightPixels,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> currentTiles)
    {
        int firstTileX = (int)(left / SvgTileCache.SvgTileSizePixels);
        int firstTileY = (int)(top / SvgTileCache.SvgTileSizePixels);
        int lastTileX = (int)((right - 1) / SvgTileCache.SvgTileSizePixels);
        int lastTileY = (int)((bottom - 1) / SvgTileCache.SvgTileSizePixels);
        long requiredTileCount = ((long)lastTileX - firstTileX + 1) * ((long)lastTileY - firstTileY + 1);
        if (requiredTileCount > currentTiles.Count)
            return false;

        // Enumeration work is bounded by resident entries, not the SVG's area.
        // Values remain opaque: dictionary membership is the published lease
        // contract, so this geometry check makes no native calls or allocations.
        for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            int y = tileY * SvgTileCache.SvgTileSizePixels;
            for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
            {
                int x = tileX * SvgTileCache.SvgTileSizePixels;
                SvgTileKey currentKey = (
                    x, y,
                    Math.Min(SvgTileCache.SvgTileSizePixels, currentWidthPixels - x),
                    Math.Min(SvgTileCache.SvgTileSizePixels, currentHeightPixels - y));
                if (!currentTiles.ContainsKey(currentKey))
                    return false;
            }
        }
        return true;
    }

    private static bool IsFiniteNonemptyRect(Rect rectangle) =>
        double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) &&
        double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) &&
        rectangle.Width > 0 && rectangle.Height > 0 &&
        double.IsFinite(rectangle.Right) && double.IsFinite(rectangle.Bottom);
}
