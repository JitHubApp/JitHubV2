using System;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Finds selectable text under a touch or pen context gesture, probing the
/// contact rectangle when the gesture point itself misses text.
/// </summary>
internal sealed class TouchSelectionHitTest
{
    private readonly IPointerInteractionHost _host;

    /// <param name="host">Embed and code block action hit-testing.</param>
    internal TouchSelectionHitTest(IPointerInteractionHost host) => _host = host;

    /// <summary>
    /// Hit-tests <paramref name="gesturePoint"/>, then the centre and inset
    /// edges of <paramref name="capturedContact"/> when it bounds the point.
    /// </summary>
    internal bool TryHitTestTouchSelection(
        Point gesturePoint,
        LayoutSnapshot snapshot,
        TouchContactGeometry? capturedContact,
        out DocumentPosition position)
    {
        position = default;
        if (IsSelectableTouchPoint(gesturePoint, snapshot, out position))
            return true;

        if (capturedContact is not { } contact ||
            !GestureHitTestBands.IsBoundedGestureContact(gesturePoint, contact))
            return false;

        double left = contact.X;
        double top = contact.Y;
        double right = contact.X + contact.Width;
        double bottom = contact.Y + contact.Height;
        Point center = new(contact.CenterX, contact.CenterY);
        double insetX = Math.Min(4, contact.Width / 4);
        double insetY = Math.Min(4, contact.Height / 4);
        return IsSelectableTouchPoint(center, snapshot, out position) ||
               IsSelectableTouchPoint(new Point(left + insetX, center.Y), snapshot, out position) ||
               IsSelectableTouchPoint(new Point(right - insetX, center.Y), snapshot, out position) ||
               IsSelectableTouchPoint(new Point(center.X, top + insetY), snapshot, out position) ||
               IsSelectableTouchPoint(new Point(center.X, bottom - insetY), snapshot, out position);
    }

    /// <summary>
    /// True when <paramref name="point"/> hits selectable text that is not
    /// under a hosted embed or a code block action.
    /// </summary>
    internal bool IsSelectableTouchPoint(Point point, LayoutSnapshot snapshot, out DocumentPosition position)
    {
        position = default;
        return !_host.IsPointOverEmbed(point) &&
               !_host.IsPointOverCodeBlockAction(point) &&
               snapshot.HitTest(point, out position);
    }
}
