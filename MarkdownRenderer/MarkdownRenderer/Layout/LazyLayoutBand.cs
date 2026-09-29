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

    /// <summary>Bounds preparation ahead of reading direction while retaining one trailing viewport.</summary>
    public static LazyLayoutBand FromDirectionalViewport(
        double viewportTop,
        double viewportHeight,
        int lookAheadViewports,
        double velocityPixelsPerSecond)
    {
        if (!double.IsFinite(viewportTop)) viewportTop = 0;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0) viewportHeight = 1;
        if (!double.IsFinite(velocityPixelsPerSecond)) velocityPixelsPerSecond = 0;
        double trailing = Math.Clamp(viewportHeight, 400, 2400);
        double velocityBonus = Math.Min(viewportHeight, Math.Abs(velocityPixelsPerSecond) * 0.15);
        double leading = Math.Clamp(
            viewportHeight * Math.Clamp(lookAheadViewports, 0, 4) + velocityBonus,
            0,
            6000);
        if (velocityPixelsPerSecond < -100)
            (leading, trailing) = (trailing, leading);
        double top = Math.Max(0, viewportTop - trailing);
        return new LazyLayoutBand(top, Math.Max(top, viewportTop + viewportHeight + leading));
    }

    public bool Intersects(double top, double bottom)
        => bottom >= Top && top <= Bottom;
}
