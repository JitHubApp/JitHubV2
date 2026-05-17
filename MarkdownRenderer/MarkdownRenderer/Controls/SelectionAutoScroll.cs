using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Pure selection-drag auto-scroll math. Kept independent of WinUI so unit
/// tests can cover edge bands without constructing a ScrollViewer.
/// </summary>
internal static class SelectionAutoScroll
{
    public const double EdgeThresholdPx = 48.0;
    public const double MaxStepPx = 36.0;

    public static double ComputeDelta(
        double pointerY,
        double viewportTop,
        double viewportHeight,
        double edgeThreshold = EdgeThresholdPx,
        double maxStep = MaxStepPx)
    {
        if (viewportHeight <= 0 || edgeThreshold <= 0 || maxStep <= 0)
            return 0;

        double viewportBottom = viewportTop + viewportHeight;
        if (pointerY < viewportTop + edgeThreshold)
        {
            double pressure = Math.Clamp((viewportTop + edgeThreshold - pointerY) / edgeThreshold, 0, 1);
            return -Math.Max(1, pressure * maxStep);
        }

        if (pointerY > viewportBottom - edgeThreshold)
        {
            double pressure = Math.Clamp((pointerY - (viewportBottom - edgeThreshold)) / edgeThreshold, 0, 1);
            return Math.Max(1, pressure * maxStep);
        }

        return 0;
    }

    public static double ClampPointToViewport(double pointerY, double viewportTop, double viewportHeight)
    {
        if (viewportHeight <= 0)
            return pointerY;

        double bottom = viewportTop + viewportHeight;
        return Math.Clamp(pointerY, viewportTop, Math.Max(viewportTop, bottom - 1));
    }
}
