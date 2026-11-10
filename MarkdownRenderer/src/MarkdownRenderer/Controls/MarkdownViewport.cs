using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.LazyLayout;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The part of the document a renderer currently shows, and the mapping
/// between canvas and viewport coordinates.
/// </summary>
/// <remarks>
/// A renderer that owns its scroll viewer reads the viewport from it. One
/// that follows an ancestor's scrolling instead records the effective
/// viewport that ancestor reports. Viewport coordinates survive a layout
/// change that moves the canvas, so input queued while layout is pending is
/// stored in them and mapped back to the canvas on replay.
/// </remarks>
internal sealed class MarkdownViewport
{
    private readonly FrameworkElement _owner;
    private readonly MarkdownViewSurface _surface;
    private Rect _external;
    private bool _hasExternal;

    /// <param name="owner">The renderer, whose size is the fallback viewport.</param>
    /// <param name="surface">The visual tree whose canvas and scroll viewer are mapped.</param>
    public MarkdownViewport(FrameworkElement owner, MarkdownViewSurface surface)
    {
        _owner = owner;
        _surface = surface;
    }

    /// <summary>
    /// Records an ancestor's effective viewport. Returns false and keeps the
    /// previous viewport when <paramref name="viewport"/> is empty or not
    /// finite.
    /// </summary>
    /// <param name="viewport">The effective viewport, in renderer coordinates.</param>
    /// <param name="materiallyChanged">
    /// True when any edge moved by at least half a DIP, or this is the first
    /// viewport recorded.
    /// </param>
    public bool TryAcceptExternal(Rect viewport, out bool materiallyChanged)
    {
        materiallyChanged = false;
        if (!double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) ||
            !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height <= 0)
        {
            return false;
        }

        materiallyChanged = !_hasExternal ||
            Math.Abs(_external.X - viewport.X) >= 0.5 ||
            Math.Abs(_external.Y - viewport.Y) >= 0.5 ||
            Math.Abs(_external.Width - viewport.Width) >= 0.5 ||
            Math.Abs(_external.Height - viewport.Height) >= 0.5;
        _external = viewport;
        _hasExternal = true;
        return true;
    }

    /// <summary>
    /// The visible document band. Returns false, with an estimate based on
    /// the renderer's size, when no viewport is known yet.
    /// </summary>
    public bool TryGet(out double top, out double height, out double width)
    {
        if (_surface.Scroll is { } scroll)
        {
            top = scroll.VerticalOffset;
            height = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : Math.Max(_owner.ActualHeight, 1);
            width = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : Math.Max(_owner.ActualWidth, 1);
            return true;
        }

        if (_hasExternal)
        {
            top = Math.Max(0, _external.Top);
            height = Math.Max(1, _external.Height);
            width = Math.Max(1, _external.Width);
            return true;
        }

        top = 0;
        height = Math.Max(_owner.ActualHeight, 600);
        width = Math.Max(_owner.ActualWidth, 1);
        return false;
    }

    /// <summary>
    /// The visible document band as a rectangle at the document's left edge.
    /// Returns false, with the same estimate as <see cref="TryGet"/>, when no
    /// viewport is known yet.
    /// </summary>
    internal bool TryGetRect(out Rect rect)
    {
        bool hasViewport = TryGet(out double top, out double height, out double width);
        rect = new Rect(0, top, width, height);
        return hasViewport;
    }

    /// <summary>The band the current viewport makes visible, with no overscan.</summary>
    internal LazyLayoutBand GetVisibleBand()
    {
        TryGet(out double top, out double height, out _);
        return LazyLayoutBand.FromVisibleViewport(top, height);
    }

    public bool TryMapCanvasToViewport(Point canvasPoint, out Point viewportPoint)
    {
        viewportPoint = default;
        if (_surface.Canvas is null)
            return false;
        return TryMapPoint(canvasPoint, canvasToViewport: true, out viewportPoint);
    }

    public bool TryMapViewportToCanvas(Point viewportPoint, out Point canvasPoint)
    {
        canvasPoint = default;
        if (_surface.Canvas is null || !double.IsFinite(viewportPoint.X) || !double.IsFinite(viewportPoint.Y))
            return false;
        return TryMapPoint(viewportPoint, canvasToViewport: false, out canvasPoint);
    }

    public bool TryMapCanvasContactToViewport(TouchContactGeometry contact, out TouchContactGeometry viewportContact)
        => TryMapContact(contact, canvasToViewport: true, out viewportContact);

    public bool TryMapViewportContactToCanvas(TouchContactGeometry contact, out TouchContactGeometry canvasContact)
        => TryMapContact(contact, canvasToViewport: false, out canvasContact);

    // Maps all four corners, so the result is the axis-aligned bounds of the
    // contact under any transform between the two spaces.
    private bool TryMapContact(TouchContactGeometry contact, bool canvasToViewport, out TouchContactGeometry mapped)
    {
        mapped = default;
        if (_surface.Canvas is null || !double.IsFinite(contact.X) || !double.IsFinite(contact.Y) ||
            !double.IsFinite(contact.Width) || !double.IsFinite(contact.Height) ||
            contact.Width < 0 || contact.Height < 0)
        {
            return false;
        }

        Point p1 = new(contact.X, contact.Y);
        Point p2 = new(contact.X + contact.Width, contact.Y);
        Point p3 = new(contact.X, contact.Y + contact.Height);
        Point p4 = new(contact.X + contact.Width, contact.Y + contact.Height);
        if (!TryMapPoint(p1, canvasToViewport, out p1) ||
            !TryMapPoint(p2, canvasToViewport, out p2) ||
            !TryMapPoint(p3, canvasToViewport, out p3) ||
            !TryMapPoint(p4, canvasToViewport, out p4))
        {
            return false;
        }

        double left = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        double top = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        double right = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        double bottom = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(right) || !double.IsFinite(bottom))
        {
            return false;
        }
        mapped = new TouchContactGeometry(left, top, right - left, bottom - top);
        return double.IsFinite(mapped.Width) && double.IsFinite(mapped.Height);
    }

    // The canvas must exist. Transforms throw while an element is leaving
    // the tree, so any failure maps to nothing.
    private bool TryMapPoint(Point point, bool canvasToViewport, out Point mapped)
    {
        mapped = default;
        try
        {
            if (_surface.Scroll is { } scroll)
            {
                mapped = canvasToViewport
                    ? _surface.Canvas!.TransformToVisual(scroll).TransformPoint(point)
                    : scroll.TransformToVisual(_surface.Canvas!).TransformPoint(point);
            }
            else if (_hasExternal)
            {
                mapped = canvasToViewport
                    ? new Point(point.X - _external.X, point.Y - _external.Y)
                    : new Point(point.X + _external.X, point.Y + _external.Y);
            }
            else
            {
                mapped = canvasToViewport
                    ? _surface.Canvas!.TransformToVisual(_owner).TransformPoint(point)
                    : _owner.TransformToVisual(_surface.Canvas!).TransformPoint(point);
            }
            return double.IsFinite(mapped.X) && double.IsFinite(mapped.Y);
        }
        catch (Exception)
        {
            mapped = default;
            return false;
        }
    }
}
