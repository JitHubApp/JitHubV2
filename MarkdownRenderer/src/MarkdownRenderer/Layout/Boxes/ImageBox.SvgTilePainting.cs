using System;
using System.Buffers;
using System.Collections.Generic;
using MarkdownRenderer.Images;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class ImageBox
{
    // A transparent replacement tile is not an overlay. Paint older content
    // only in holes in the new tile grid, otherwise loading chrome or an old
    // theme shows through fully rendered transparent pixels indefinitely.
    // The caller holds _svgTileLock throughout both coverage and drawing.
    private void PaintSvgTileLayerNoLock(
        CanvasDrawingSession ds,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> tiles,
        Rect destination, Rect viewport, int widthPixels, int heightPixels,
        MarkdownSvgPhysicalViewport? physicalViewport, bool useFallbackTiles)
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
                PaintSvgTileGap(ds, destination,
                    visible.Left, y, visible.Right, rowTop, useFallbackTiles);
                double x = visible.Left;
                do
                {
                    Rect covered = coverage[index++];
                    PaintSvgTileGap(ds, destination,
                        x, rowTop, covered.Left, rowBottom, useFallbackTiles);
                    x = covered.Right;
                }
                while (index < count && coverage[index].Top == rowTop);

                PaintSvgTileGap(ds, destination,
                    x, rowTop, visible.Right, rowBottom, useFallbackTiles);
                y = rowBottom;
            }
            PaintSvgTileGap(ds, destination,
                visible.Left, y, visible.Right, visible.Bottom, useFallbackTiles);

            DrawSvgTileSetNoLock(ds, tiles, destination, viewport,
                widthPixels, heightPixels, physicalViewport);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<Rect>.Shared.Return(rented);
        }
    }

    private void PaintSvgTileGap(CanvasDrawingSession ds, Rect destination,
        double left, double top, double right, double bottom, bool useFallbackTiles)
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
                clip = NativePaintSessionResources.CreateLayer(ds, 1f, gap);
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
            if (useFallbackTiles && _svgFallbackTiles is { Count: > 0 } fallback &&
                _svgFallbackWidthPixels > 0 && _svgFallbackHeightPixels > 0)
            {
                PaintSvgTileLayerNoLock(ds, fallback, destination, gap,
                    _svgFallbackWidthPixels, _svgFallbackHeightPixels,
                    _svgFallbackPhysicalViewport, useFallbackTiles: false);
            }
            else if (_bitmap is { } previousBitmap)
            {
                PaintWholeBitmap(ds, previousBitmap, destination);
            }
            else
            {
                PaintPlaceholder(ds, destination);
            }
        }
        catch (Exception primary)
        {
            clip.ClosePreservingPrimary(primary);
            throw;
        }
        clip.Close();
    }
}
