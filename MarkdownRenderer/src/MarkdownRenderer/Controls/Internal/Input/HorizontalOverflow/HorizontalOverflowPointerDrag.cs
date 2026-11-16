using System;
using Windows.Foundation;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using MarkdownRenderer.Controls.Internal.Input.Gestures;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;

/// <summary>
/// Pointer scrolling of a horizontally overflowing block, such as a wide
/// table or code block: a mouse or pen drag on its scroll bar, a touch pan
/// across it, and a horizontal or Shift wheel over it.
/// </summary>
/// <remarks>
/// A precise pointer must press the scroll track and is captured at once. A
/// touch contact stays pending without capture until it moves far enough
/// horizontally, so an ancestor ScrollViewer can still take a vertical pan.
/// </remarks>
internal sealed class HorizontalOverflowPointerDrag
{
    private readonly IHorizontalOverflowPointerHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly PublishedLayout _published;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;

    private uint _pointerId;
    private LayoutSnapshot? _snapshot;
    private IHorizontalOverflowBox? _target;
    private Point _pressPoint;
    private double _initialOffset;
    private double _thumbGrabOffset;
    private bool _captured;
    private bool _thumbDrag;

    internal HorizontalOverflowPointerDrag(
        IHorizontalOverflowPointerHost host,
        MarkdownViewSurface surface,
        PublishedLayout published,
        TouchTapSuppressionPolicy touchTapSuppression)
    {
        _host = host;
        _surface = surface;
        _published = published;
        _touchTapSuppression = touchTapSuppression;
    }

    /// <summary>The pointer that owns the drag, pending or captured, or 0.</summary>
    internal uint PointerId => _pointerId;

    /// <summary>True once the drag holds the canvas pointer capture.</summary>
    internal bool IsCaptured => _captured;

    /// <summary>Scrolls the block under a horizontal or Shift wheel.</summary>
    internal void ProcessWheel(ref PointerInput e)
    {
        if (_published.Snapshot is not { } snapshot || _surface.Canvas is null)
            return;

        var point = e.Point;
        bool horizontalWheel = point.Properties.IsHorizontalMouseWheel;
        bool shift = (e.Modifiers & VirtualKeyModifiers.Shift) != 0;
        if (!horizontalWheel && !shift)
            return;

        if (!snapshot.TryGetHorizontalOverflow(point.Position, out var target) || target is null)
            return;

        int wheelDelta = point.Properties.MouseWheelDelta;
        if (wheelDelta == 0)
            return;

        double oldOffset = target.HorizontalOffset;
        double delta = -(wheelDelta / 120.0) * 64.0;
        if (target.IsRightToLeft)
            delta = -delta;

        if (!snapshot.TryScrollHorizontal(target, delta))
            return;

        _host.RefreshHorizontalOverflowVisualState(target, oldOffset);
        e.Handled = true;
    }

    /// <summary>
    /// Starts a drag when the press lands on a horizontally overflowing
    /// block. Returns true when the press belongs to the block, including a
    /// touch contact that is still pending.
    /// </summary>
    internal bool TryBegin(ref PointerInput e)
    {
        if (_published.Snapshot is not { } snapshot || _surface.Canvas is null)
            return false;

        var point = e.Point;
        if (!point.IsInContact ||
            !snapshot.TryGetHorizontalOverflow(point.Position, out var target) ||
            target is null)
        {
            return false;
        }

        Rect track = target.HorizontalScrollTrackBounds;
        bool precise = PointerInputClassification.IsPrecisePointer(e);
        if (precise && !track.Contains(point.Position))
            return false;

        _pointerId = e.Pointer.PointerId;
        _snapshot = snapshot;
        _target = target;
        _pressPoint = point.Position;
        _initialOffset = target.HorizontalOffset;
        _captured = false;
        _thumbDrag = precise;

        if (!precise)
        {
            // A touch contact starts in a pending state. Do not capture or mark
            // handled until horizontal movement wins the gesture threshold, so
            // the ancestor ScrollViewer remains free to own vertical panning.
            return true;
        }

        Rect thumb = target.HorizontalScrollThumbBounds;
        double oldOffset = target.HorizontalOffset;
        if (!thumb.Contains(point.Position))
        {
            SetHorizontalOffsetFromTrackPoint(snapshot, target, point.Position.X, centerThumb: true);
            thumb = target.HorizontalScrollThumbBounds;
        }
        _thumbGrabOffset = Math.Clamp(
            point.Position.X - thumb.Left,
            0,
            Math.Max(0, thumb.Width));

        if (!PointerInputClassification.CapturePointerForInput(_surface.Canvas, e))
        {
            Clear();
            e.ResumeRebuildAfterInteraction = true;
            return false;
        }

        _captured = true;
        if (Math.Abs(target.HorizontalOffset - oldOffset) > 0.1)
            _host.RefreshHorizontalOverflowVisualState(target, oldOffset);
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Scrolls the block for a move of the drag's pointer. Returns true when
    /// the move belongs to the drag.
    /// </summary>
    internal bool TryHandleMoved(ref PointerInput e, Point point)
    {
        if (_pointerId == 0 ||
            _pointerId != e.Pointer.PointerId ||
            _snapshot is not { } snapshot ||
            _target is not { } target ||
            !ReferenceEquals(snapshot, _published.Snapshot))
        {
            return false;
        }

        if (_thumbDrag)
        {
            double oldOffset = target.HorizontalOffset;
            SetHorizontalOffsetFromTrackPoint(
                snapshot,
                target,
                point.X - _thumbGrabOffset,
                centerThumb: false);
            if (Math.Abs(target.HorizontalOffset - oldOffset) > 0.1)
                _host.RefreshHorizontalOverflowVisualState(target, oldOffset);
            e.Handled = true;
            return true;
        }

        double dx = point.X - _pressPoint.X;
        double dy = point.Y - _pressPoint.Y;
        if (!_captured)
        {
            MarkdownPanGestureDecision decision =
                MarkdownPointerGesturePolicy.ClassifyHorizontalPan(dx, dy);
            if (decision == MarkdownPanGestureDecision.Pending)
                return false;

            if (decision == MarkdownPanGestureDecision.YieldToAncestor)
            {
                Clear();
                e.ResumeRebuildAfterInteraction = true;
                return false;
            }

            if (_surface.Canvas is null || !PointerInputClassification.CapturePointerForInput(_surface.Canvas, e))
            {
                Clear();
                e.ResumeRebuildAfterInteraction = true;
                return false;
            }
            _captured = true;
            _touchTapSuppression.SuppressTouchTap(e.TouchTapGestureEpoch, point, Environment.TickCount64);
        }

        double oldTouchOffset = target.HorizontalOffset;
        double requested = target.IsRightToLeft
            ? _initialOffset + dx
            : _initialOffset - dx;
        if (snapshot.TrySetHorizontalOffset(target, requested))
            _host.RefreshHorizontalOverflowVisualState(target, oldTouchOffset);
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Ends the drag on its pointer's release. Returns true when the drag had
    /// captured the pointer, so the release must not reach text selection.
    /// </summary>
    internal bool TryHandleReleased(ref PointerInput e)
    {
        if (_pointerId == 0 ||
            _pointerId != e.Pointer.PointerId)
        {
            return false;
        }

        bool captured = _captured;
        if (captured && _surface.Canvas is not null)
            _touchTapSuppression.SuppressTouchTap(e.TouchTapGestureEpoch, e.Point.Position, Environment.TickCount64);
        Clear();
        e.ResumeRebuildAfterInteraction = true;
        if (captured && _surface.Canvas is not null)
        {
            e.Handled = true;
            e.ReleaseCanvasCaptureAfterInteraction = true;
        }
        return captured;
    }

    /// <summary>
    /// Ends the drag on its pointer's cancel or capture loss. Returns true
    /// when the drag had captured the pointer.
    /// </summary>
    internal bool TryHandleCanceled(ref PointerInput e)
    {
        if (_pointerId == 0 ||
            _pointerId != e.Pointer.PointerId)
        {
            return false;
        }

        bool captured = _captured;
        Clear();
        e.ResumeRebuildAfterInteraction = true;
        if (captured)
        {
            e.Handled = true;
        }
        return captured;
    }

    /// <summary>Forgets the drag without scrolling.</summary>
    internal void Clear()
    {
        _pointerId = 0;
        _snapshot = null;
        _target = null;
        _pressPoint = default;
        _initialOffset = 0;
        _thumbGrabOffset = 0;
        _captured = false;
        _thumbDrag = false;
    }

    private static void SetHorizontalOffsetFromTrackPoint(
        LayoutSnapshot snapshot,
        IHorizontalOverflowBox target,
        double x,
        bool centerThumb)
    {
        Rect track = target.HorizontalScrollTrackBounds;
        Rect thumb = target.HorizontalScrollThumbBounds;
        if (track.IsEmpty || thumb.IsEmpty)
            return;

        double thumbLeft = centerThumb ? x - thumb.Width / 2.0 : x;
        double travel = Math.Max(0, track.Width - thumb.Width);
        if (travel <= 0)
            return;

        double physicalFraction = Math.Clamp((thumbLeft - track.Left) / travel, 0, 1);
        double logicalFraction = target.IsRightToLeft
            ? 1.0 - physicalFraction
            : physicalFraction;
        double maximum = Math.Max(0, target.HorizontalExtent - target.HorizontalViewport);
        snapshot.TrySetHorizontalOffset(target, logicalFraction * maximum);
    }
}
