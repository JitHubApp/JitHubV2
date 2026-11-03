using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Paints what shows through a hole in an SVG tile grid, inside a clip the
/// <see cref="SvgTilePainter"/> has already pushed.
/// </summary>
internal interface ISvgTileGapFill
{
    /// <summary>
    /// Fills <paramref name="gap"/> of the image at <paramref name="destination"/>;
    /// <paramref name="useFallbackTiles"/> is false while painting the fallback
    /// grid itself.
    /// </summary>
    void FillSvgTileGap(CanvasDrawingSession ds, Rect destination, Rect gap, bool useFallbackTiles);
}
