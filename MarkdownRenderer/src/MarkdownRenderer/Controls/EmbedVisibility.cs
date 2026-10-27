namespace MarkdownRenderer.Controls;

/// <summary>
/// Pure-logic helpers describing the realize/derealize hysteresis bands
/// used by <see cref="MarkdownRendererControl"/> for embed virtualization.
/// Lives in its own file (no WinUI/Win2D dependencies) so it can be
/// exercised by unit tests.
/// </summary>
public static class EmbedVisibility
{
    /// <summary>
    /// Returns true if the rect spanning [<paramref name="planTop"/>,
    /// <paramref name="planBottom"/>] intersects the realize band
    /// (viewport ± <paramref name="overscan"/>).
    /// </summary>
    public static bool IsInRealizeBand(double planTop, double planBottom,
        double viewportTop, double viewportBottom, double overscan)
    {
        double t = viewportTop - overscan;
        double b = viewportBottom + overscan;
        return planBottom >= t && planTop <= b;
    }

    /// <summary>
    /// Returns true if the rect lies within the larger derealize band
    /// (viewport ± <paramref name="derealizeOverscan"/>). When a plan is
    /// outside the realize band but still inside this band, it is kept
    /// realized to avoid thrashing near the boundary.
    /// </summary>
    public static bool IsInDerealizeBand(double planTop, double planBottom,
        double viewportTop, double viewportBottom, double derealizeOverscan)
    {
        double t = viewportTop - derealizeOverscan;
        double b = viewportBottom + derealizeOverscan;
        return planBottom >= t && planTop <= b;
    }
}
