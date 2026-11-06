using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Pure selection-drag auto-scroll math. Kept independent of WinUI so unit
/// tests can cover edge bands without constructing a ScrollViewer.
/// </summary>
internal static class SelectionAutoScroll
{
    // Pointer and viewport coordinates are XAML device-independent pixels.
    public const double EdgeThresholdDip = 48.0;
    public const double MaximumVelocityDipPerSecond = 2160.0;
    public const double MinimumVelocityDipPerSecond = 60.0;
    public const double MaximumFrameDurationSeconds = 1.0 / 15.0;

    public static double ComputeVelocity(
        double pointerY,
        double viewportTop,
        double viewportHeight,
        double edgeThreshold = EdgeThresholdDip,
        double maximumVelocity = MaximumVelocityDipPerSecond)
    {
        double signedPressure = ComputeSignedPressure(
            pointerY,
            viewportTop,
            viewportHeight,
            edgeThreshold);
        if (signedPressure == 0 || !double.IsFinite(maximumVelocity) || maximumVelocity <= 0)
            return 0;

        double speed = Math.Max(
            Math.Min(MinimumVelocityDipPerSecond, maximumVelocity),
            Math.Abs(signedPressure) * maximumVelocity);
        return Math.CopySign(speed, signedPressure);
    }

    private static double ComputeSignedPressure(
        double pointerY,
        double viewportTop,
        double viewportHeight,
        double edgeThreshold)
    {
        if (!double.IsFinite(pointerY) || !double.IsFinite(viewportTop) ||
            !double.IsFinite(viewportHeight) || !double.IsFinite(edgeThreshold) ||
            viewportHeight <= 0 || edgeThreshold <= 0)
            return 0;

        double viewportBottom = viewportTop + viewportHeight;
        double topPressure = Math.Clamp(
            (viewportTop + edgeThreshold - pointerY) / edgeThreshold,
            0,
            1);
        double bottomPressure = Math.Clamp(
            (pointerY - (viewportBottom - edgeThreshold)) / edgeThreshold,
            0,
            1);

        // On very short viewports the edge bands overlap. Resolve the stronger
        // pressure instead of always preferring the top band.
        double pressure = Math.Max(topPressure, bottomPressure);
        if (pressure <= 0 || Math.Abs(topPressure - bottomPressure) < 0.0001)
            return 0;

        return topPressure > bottomPressure ? -pressure : pressure;
    }

    public static double ComputeFrameDelta(
        double pointerY,
        double viewportTop,
        double viewportHeight,
        double elapsedSeconds,
        double edgeThreshold = EdgeThresholdDip,
        double maximumVelocity = MaximumVelocityDipPerSecond)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            return 0;

        double boundedDuration = Math.Min(elapsedSeconds, MaximumFrameDurationSeconds);
        return ComputeVelocity(
            pointerY,
            viewportTop,
            viewportHeight,
            edgeThreshold,
            maximumVelocity) * boundedDuration;
    }

    public static double ComputeDirectionalDelta(
        double pointerY,
        double viewportTop,
        double viewportHeight,
        double previousPointerViewportY,
        double edgeThreshold = EdgeThresholdDip,
        double maximumStep = 36.0)
    {
        double signedPressure = ComputeSignedPressure(
            pointerY,
            viewportTop,
            viewportHeight,
            edgeThreshold);
        if (signedPressure == 0 || !double.IsFinite(maximumStep) || maximumStep <= 0 ||
            double.IsNaN(previousPointerViewportY))
            return 0;

        double delta = Math.CopySign(Math.Max(1, Math.Abs(signedPressure) * maximumStep), signedPressure);

        double pointerViewportY = pointerY - viewportTop;
        double movement = pointerViewportY - previousPointerViewportY;
        bool outsideTop = pointerViewportY < 0;
        bool outsideBottom = pointerViewportY >= viewportHeight;

        // Edge bands can overlap in very short editor previews. Only scroll
        // toward an edge while the pointer is moving toward (or already beyond)
        // that edge; a downward selection that starts near the top must not
        // walk the document backward under the pointer.
        if (delta < 0 && !outsideTop && movement >= 0)
            return 0;
        if (delta > 0 && !outsideBottom && movement <= 0)
            return 0;

        return delta;
    }

    public static double ClampPointToViewport(double pointerY, double viewportTop, double viewportHeight)
    {
        if (viewportHeight <= 0)
            return pointerY;

        double bottom = viewportTop + viewportHeight;
        return Math.Clamp(pointerY, viewportTop, Math.Max(viewportTop, bottom - 1));
    }
}
