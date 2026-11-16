using System;
using System.Buffers;
using System.Collections.Generic;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;

/// <summary>
/// Paints a grid of SVG tiles. Holes in the grid are clipped and handed to an
/// <see cref="ISvgTileGapFill"/>, so transparent tiles never overlay older
/// content.
/// </summary>
internal static class SvgTilePainter
{
    // A transparent replacement tile is not an overlay. Paint older content
    // only in holes in the new tile grid, otherwise loading chrome or an old
    // theme shows through fully rendered transparent pixels indefinitely.
    // The caller holds its tile lock throughout both coverage and drawing.
    internal static void PaintLayer(
        CanvasDrawingSession ds,
        NativePaintSessionResources paintSessions,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> tiles,
        Rect destination, Rect viewport, int widthPixels, int heightPixels,
        MarkdownSvgPhysicalViewport? physicalViewport, ISvgTileGapFill fill, bool useFallbackTiles)
    {
        Rect visible = IntersectRects(destination, viewport);
        if (visible.Width <= 0 || visible.Height <= 0)
            return;

        Rect[]? rented = null;
        Span<Rect> coverage = tiles.Count <= 32
            ? stackalloc Rect[tiles.Count]
            : (rented = ArrayPool<Rect>.Shared.Rent(tiles.Count)).AsSpan(0, tiles.Count);
        try
        {
            int count = 0;
            if (widthPixels > 0 && heightPixels > 0)
            {
                double scaleX = destination.Width / (physicalViewport?.Width ?? widthPixels);
                double scaleY = destination.Height / (physicalViewport?.Height ?? heightPixels);
                foreach (SvgTileKey tile in tiles.Keys)
                {
                    Rect covered = IntersectRects(visible, new Rect(
                        destination.X + tile.X * scaleX, destination.Y + tile.Y * scaleY,
                        tile.Width * scaleX, tile.Height * scaleY));
                    if (covered.Width > 0 && covered.Height > 0)
                        coverage[count++] = covered;
                }
            }

            // Canonical tiles form disjoint rows. Sweep only resident tiles,
            // not every cell of a potentially enormous virtual image. The
            // ordinary <=32-tile path allocates no managed coverage storage.
            coverage = coverage[..count];
            coverage.Sort(static (a, b) =>
            {
                int row = a.Top.CompareTo(b.Top);
                return row != 0 ? row : a.Left.CompareTo(b.Left);
            });
            double y = visible.Top;
            int index = 0;
            while (index < count)
            {
                double rowTop = coverage[index].Top;
                double rowBottom = coverage[index].Bottom;
                PaintGap(ds, paintSessions, destination,
                    visible.Left, y, visible.Right, rowTop, fill, useFallbackTiles);
                double x = visible.Left;
                do
                {
                    Rect covered = coverage[index++];
                    PaintGap(ds, paintSessions, destination,
                        x, rowTop, covered.Left, rowBottom, fill, useFallbackTiles);
                    x = covered.Right;
                }
                while (index < count && coverage[index].Top == rowTop);

                PaintGap(ds, paintSessions, destination,
                    x, rowTop, visible.Right, rowBottom, fill, useFallbackTiles);
                y = rowBottom;
            }
            PaintGap(ds, paintSessions, destination,
                visible.Left, y, visible.Right, visible.Bottom, fill, useFallbackTiles);

            DrawTileSet(ds, tiles, destination, viewport,
                widthPixels, heightPixels, physicalViewport);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<Rect>.Shared.Return(rented);
        }
    }

    private static void PaintGap(CanvasDrawingSession ds, NativePaintSessionResources paintSessions, Rect destination,
        double left, double top, double right, double bottom, ISvgTileGapFill fill, bool useFallbackTiles)
    {
        // Adjacent physical edges can differ by one floating-point unit after
        // DIP mapping. Do not construct an invalid negative-sized WinRT Rect.
        if (right <= left || bottom <= top)
            return;
        Rect gap = new(left, top, right - left, bottom - top);

        // This is a pixel-ownership clip, not SVG geometry to antialias again.
        // Restore the caller's drawing mode before painting text or artwork.
        CanvasAntialiasing originalAntialiasing = ds.Antialiasing;
        NativePaintSessionResources.LayerLease clip = default;
        try
        {
            try
            {
                ds.Antialiasing = CanvasAntialiasing.Aliased;
                clip = paintSessions.CreateLayer(ds, 1f, gap);
            }
            catch (Exception creation)
            {
                try { ds.Antialiasing = originalAntialiasing; }
                catch (Exception restore)
                {
                    throw new AggregateException(
                        "The tile clip failed and its drawing mode could not be restored.", creation, restore);
                }
                throw;
            }
            // Keep restoration inside the lease's failure scope as well:
            // device loss here must not abandon an already-pushed clip.
            ds.Antialiasing = originalAntialiasing;
            fill.FillSvgTileGap(ds, paintSessions, destination, gap, useFallbackTiles);
        }
        catch (Exception primary)
        {
            clip.ClosePreservingPrimary(primary);
            throw;
        }
        clip.Close();
    }

    internal static void DrawTileSet(
        CanvasDrawingSession ds,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> tiles,
        Rect destination,
        Rect viewport,
        int widthPixels,
        int heightPixels,
        MarkdownSvgPhysicalViewport? physicalViewport)
    {
        if (widthPixels <= 0 || heightPixels <= 0)
            return;

        double physicalWidth = physicalViewport?.Width ?? widthPixels;
        double physicalHeight = physicalViewport?.Height ?? heightPixels;
        double scaleX = destination.Width / physicalWidth;
        double scaleY = destination.Height / physicalHeight;
        foreach (KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> tile in tiles)
        {
            Rect tileDestination = new(
                destination.X + tile.Key.X * scaleX,
                destination.Y + tile.Key.Y * scaleY,
                tile.Key.Width * scaleX,
                tile.Key.Height * scaleY);
            if (tileDestination.Right < viewport.Left ||
                tileDestination.Left > viewport.Right ||
                tileDestination.Bottom < viewport.Top ||
                tileDestination.Top > viewport.Bottom)
            {
                continue;
            }

            // As with whole rasters, the worker owns antialiasing at the outer
            // fractional viewport. Do not multiply the final tile's coverage.
            ds.DrawImage(tile.Value.Bitmap, tileDestination);
        }
    }

    internal static Rect IntersectRects(Rect left, Rect right)
    {
        double x = Math.Max(left.Left, right.Left);
        double y = Math.Max(left.Top, right.Top);
        double width = Math.Max(0, Math.Min(left.Right, right.Right) - x);
        double height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y);
        return new Rect(x, y, width, height);
    }
}
