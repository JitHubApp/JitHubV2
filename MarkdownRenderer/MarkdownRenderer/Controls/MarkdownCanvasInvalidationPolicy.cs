using Windows.Foundation;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Produces finite, non-empty invalidation rectangles accepted by
/// <c>CanvasVirtualControl.Invalidate(Rect)</c>.
/// </summary>
internal static class MarkdownCanvasInvalidationPolicy
{
    internal static bool TryClipToCanvas(
        Rect requested,
        double canvasWidth,
        double canvasHeight,
        out Rect clipped)
    {
        clipped = default;
        if (!double.IsFinite(requested.X) ||
            !double.IsFinite(requested.Y) ||
            !double.IsFinite(requested.Width) ||
            !double.IsFinite(requested.Height) ||
            !double.IsFinite(canvasWidth) ||
            !double.IsFinite(canvasHeight) ||
            requested.Width <= 0 ||
            requested.Height <= 0 ||
            canvasWidth <= 0 ||
            canvasHeight <= 0)
        {
            return false;
        }

        double left = System.Math.Clamp(requested.X, 0, canvasWidth);
        double top = System.Math.Clamp(requested.Y, 0, canvasHeight);
        double right = System.Math.Clamp(requested.X + requested.Width, 0, canvasWidth);
        double bottom = System.Math.Clamp(requested.Y + requested.Height, 0, canvasHeight);
        if (!double.IsFinite(right) ||
            !double.IsFinite(bottom) ||
            right <= left ||
            bottom <= top)
        {
            return false;
        }

        clipped = new Rect(left, top, right - left, bottom - top);
        return true;
    }
}
