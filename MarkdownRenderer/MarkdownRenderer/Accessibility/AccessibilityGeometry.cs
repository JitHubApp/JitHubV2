using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Accessibility;

internal readonly record struct AccessibilityPoint(double X, double Y);

internal readonly record struct AccessibilityRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

internal static class AccessibilityGeometry
{
    public static bool TryCoercePointToNearestRect(
        AccessibilityPoint point,
        IEnumerable<AccessibilityRect> rects,
        out AccessibilityPoint coercedPoint)
    {
        AccessibilityRect best = default;
        double bestScore = double.PositiveInfinity;

        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            double x = Math.Clamp(point.X, rect.Left, rect.Right);
            double y = Math.Clamp(point.Y, rect.Top, rect.Bottom);
            double dx = point.X - x;
            double dy = point.Y - y;
            double score = dx * dx + dy * dy;
            if (score >= bestScore)
                continue;

            bestScore = score;
            best = rect;
        }

        if (!double.IsFinite(bestScore))
        {
            coercedPoint = point;
            return false;
        }

        coercedPoint = new AccessibilityPoint(
            Math.Clamp(point.X, best.Left, best.Right),
            Math.Clamp(point.Y, best.Top, best.Bottom));
        return true;
    }

    public static double[] BuildVisibleBoundingRectangles(
        int start,
        int end,
        IEnumerable<AccessibilityRect> screenRects,
        AccessibilityRect visibleBounds)
    {
        if (start == end || visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
            return Array.Empty<double>();

        var values = new List<double>();
        foreach (var screen in screenRects)
        {
            AccessibilityRect clipped = ClipToVisibleBounds(screen, visibleBounds);
            if (clipped.Width <= 0 || clipped.Height <= 0) continue;
            values.Add(clipped.X);
            values.Add(clipped.Y);
            values.Add(clipped.Width);
            values.Add(clipped.Height);
        }

        return values.ToArray();
    }

    private static AccessibilityRect ClipToVisibleBounds(
        AccessibilityRect screenRect,
        AccessibilityRect visibleBounds)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0 ||
            visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
        {
            return default;
        }

        double left = Math.Max(screenRect.Left, visibleBounds.Left);
        double top = Math.Max(screenRect.Top, visibleBounds.Top);
        double right = Math.Min(screenRect.Right, visibleBounds.Right);
        double bottom = Math.Min(screenRect.Bottom, visibleBounds.Bottom);
        return right > left && bottom > top
            ? new AccessibilityRect(left, top, right - left, bottom - top)
            : default;
    }
}
