using System;

namespace JitHub.Services.CodeViewer;

internal readonly record struct ViewportZoomTarget(
    double HorizontalOffset,
    double VerticalOffset);

internal static class ViewportZoomAnchor
{
    public static ViewportZoomTarget PreserveCenter(
        double horizontalOffset,
        double verticalOffset,
        double viewportWidth,
        double viewportHeight,
        double currentZoomFactor,
        double targetZoomFactor)
    {
        double currentZoom = IsPositiveFinite(currentZoomFactor) ? currentZoomFactor : 1;
        double targetZoom = IsPositiveFinite(targetZoomFactor) ? targetZoomFactor : currentZoom;
        double width = IsPositiveFinite(viewportWidth) ? viewportWidth : 0;
        double height = IsPositiveFinite(viewportHeight) ? viewportHeight : 0;
        double currentHorizontalOffset = IsNonNegativeFinite(horizontalOffset) ? horizontalOffset : 0;
        double currentVerticalOffset = IsNonNegativeFinite(verticalOffset) ? verticalOffset : 0;

        double contentCenterX = (currentHorizontalOffset + (width / 2)) / currentZoom;
        double contentCenterY = (currentVerticalOffset + (height / 2)) / currentZoom;

        return new ViewportZoomTarget(
            Math.Max(0, (contentCenterX * targetZoom) - (width / 2)),
            Math.Max(0, (contentCenterY * targetZoom) - (height / 2)));
    }

    private static bool IsPositiveFinite(double value) =>
        double.IsFinite(value) && value > 0;

    private static bool IsNonNegativeFinite(double value) =>
        double.IsFinite(value) && value >= 0;
}
