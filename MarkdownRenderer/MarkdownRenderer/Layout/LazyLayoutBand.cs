using System;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Pure helper for viewport-relative lazy layout bands.
/// </summary>
internal readonly record struct LazyLayoutBand(double Top, double Bottom)
{
    public static LazyLayoutBand FromViewport(double viewportTop, double viewportHeight, double overscan)
    {
        if (double.IsNaN(viewportTop) || double.IsInfinity(viewportTop))
            viewportTop = 0;
        if (double.IsNaN(viewportHeight) || double.IsInfinity(viewportHeight) || viewportHeight <= 0)
            viewportHeight = 1;
        if (double.IsNaN(overscan) || double.IsInfinity(overscan) || overscan < 0)
            overscan = 0;

        double top = Math.Max(0, viewportTop - overscan);
        double bottom = Math.Max(top, viewportTop + viewportHeight + overscan);
        return new LazyLayoutBand(top, bottom);
    }

    public bool Intersects(double top, double bottom)
        => bottom >= Top && top <= Bottom;
}
