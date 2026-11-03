using System;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Scrolls the viewport while a selection drag nears its top or bottom
/// edge, and clamps the drag point to the visible document.
/// </summary>
/// <remarks>
/// Text drags and touch handle drags share this state: only one of them
/// owns the selection at a time, and both measure edge pressure from the
/// pointer's last viewport-relative position.
/// </remarks>
internal sealed class SelectionDragAutoScroll
{
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private double _lastPointerViewportY = double.NaN;

    /// <param name="surface">The renderer's scroll viewer and canvas.</param>
    /// <param name="viewport">The visible document band.</param>
    internal SelectionDragAutoScroll(MarkdownViewSurface surface, MarkdownViewport viewport)
    {
        _surface = surface;
        _viewport = viewport;
    }

    /// <summary>Records where a drag started, relative to the viewport top.</summary>
    internal void TrackPointer(double pointerViewportY) => _lastPointerViewportY = pointerViewportY;

    /// <summary>Forgets the drag's last pointer position.</summary>
    internal void Reset() => _lastPointerViewportY = double.NaN;

    /// <summary>
    /// Scrolls toward an edge the drag is pressing on, then returns the drag
    /// point clamped to the canvas width and the visible band.
    /// </summary>
    /// <param name="point">The drag point in canvas coordinates.</param>
    /// <param name="sustained">
    /// True on a frame tick with a stationary contact, which scrolls at a
    /// steady rate; false on a pointer move, which scrolls only when moving
    /// toward the edge.
    /// </param>
    /// <param name="elapsedSeconds">Time since the previous frame tick.</param>
    internal Point PrepareDragPoint(
        Point point,
        bool sustained = false,
        double elapsedSeconds = 0)
    {
        if (_surface.Canvas is null)
            return point;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
        double pointerViewportY = point.Y - viewportTop;
        double delta = sustained
            ? SelectionAutoScroll.ComputeFrameDelta(
                point.Y,
                viewportTop,
                viewportHeight,
                elapsedSeconds)
            : SelectionAutoScroll.ComputeDirectionalDelta(
                point.Y,
                viewportTop,
                viewportHeight,
                _lastPointerViewportY);
        _lastPointerViewportY = pointerViewportY;
        if (Math.Abs(delta) > 0.0001)
        {
            double maxOffset = Math.Max(0, _surface.Canvas.ActualHeight - viewportHeight);
            double target = Math.Clamp(viewportTop + delta, 0, maxOffset);
            if (Math.Abs(target - viewportTop) > 0.0001)
            {
                if (_surface.Scroll is not null)
                    _surface.Scroll.ChangeView(null, target, null, disableAnimation: true);
                else
                    _surface.BringIntoExternalViewport(new Rect(0, target, 1, 1), animate: false);
                viewportTop = target;
            }
        }

        double x = Math.Clamp(point.X, 0, Math.Max(0, _surface.Canvas.ActualWidth - 1));
        double y = SelectionAutoScroll.ClampPointToViewport(point.Y, viewportTop, viewportHeight);
        return new Point(x, y);
    }
}
