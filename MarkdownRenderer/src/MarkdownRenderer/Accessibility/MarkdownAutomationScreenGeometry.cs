using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Converts between document coordinates and the client-pixel coordinates UIA
/// providers report for the markdown control.
/// </summary>
internal sealed class MarkdownAutomationScreenGeometry
{
    private readonly MarkdownRendererControl _owner;
    private readonly IMarkdownAutomationClipSource _clip;

    internal MarkdownAutomationScreenGeometry(MarkdownRendererControl owner, IMarkdownAutomationClipSource clip)
    {
        _owner = owner;
        _clip = clip;
    }

    internal Rect GetScreenRectForDocumentRect(Rect docRect)
    {
        // A clipped semantic span has no screen geometry. Rect.Empty uses
        // negative-infinite dimensions, which cannot be passed to Rect's
        // constructor when translating it to client coordinates.
        if (docRect.IsEmpty) return Rect.Empty;

        var ownerScreen = GetOwnerScreenBounds();
        if (ownerScreen.Width <= 0 || ownerScreen.Height <= 0) return default;

        double scale = _owner.XamlRoot?.RasterizationScale ?? 1.0;
        return new Rect(
            ownerScreen.X + docRect.X * scale,
            ownerScreen.Y + (docRect.Y - _owner.AutomationHost.ScrollOffsetY) * scale,
            docRect.Width * scale,
            docRect.Height * scale);
    }

    internal bool TryScreenPointToDocumentPoint(
        Point screenLocation,
        out Point documentPoint)
    {
        var ownerScreen = GetOwnerScreenBounds();
        if (ownerScreen.Width <= 0 || ownerScreen.Height <= 0)
        {
            documentPoint = default;
            return false;
        }

        double scale = _owner.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;
        documentPoint = new Point(
            (screenLocation.X - ownerScreen.X) / scale,
            (screenLocation.Y - ownerScreen.Y) / scale + _owner.AutomationHost.ScrollOffsetY);
        return true;
    }

    internal Rect GetVisibleScreenBounds()
    {
        Rect ownerBounds = GetOwnerScreenBounds();
        Rect clippedBounds = _clip.GetClippedBoundingRectangle();
        if (ownerBounds.Width <= 0 || ownerBounds.Height <= 0 ||
            clippedBounds.Width <= 0 || clippedBounds.Height <= 0)
        {
            return default;
        }

        double left = System.Math.Max(ownerBounds.Left, clippedBounds.Left);
        double top = System.Math.Max(ownerBounds.Top, clippedBounds.Top);
        double right = System.Math.Min(ownerBounds.Right, clippedBounds.Right);
        double bottom = System.Math.Min(ownerBounds.Bottom, clippedBounds.Bottom);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : default;
    }

    internal bool IsScreenRectOffscreen(Rect screenRect)
    {
        Rect viewport = GetVisibleScreenBounds();
        return screenRect.Width <= 0 ||
               screenRect.Height <= 0 ||
               viewport.Width <= 0 ||
               viewport.Height <= 0 ||
               screenRect.Right <= viewport.Left ||
               screenRect.Left >= viewport.Right ||
               screenRect.Bottom <= viewport.Top ||
               screenRect.Top >= viewport.Bottom;
    }

    private Rect GetOwnerScreenBounds()
    {
        if (_owner.XamlRoot is { Content: UIElement root })
        {
            // Peer bounds are clipped by ancestor ScrollViewers, and a plain
            // XAML root need not have a peer at all. Keep the unclipped origin
            // in the same client coordinate space as GetBoundingRectangleCore.
            // WinUI's UIA wrappers translate provider rectangles to screen
            // coordinates, and screen points to client coordinates, themselves.
            // Converting to screen here would apply the window origin twice.
            GeneralTransform transform = _owner.TransformToVisual(root);
            Point topLeft = transform.TransformPoint(new Point());
            Point topRight = transform.TransformPoint(new Point(_owner.ActualWidth, 0));
            Point bottomLeft = transform.TransformPoint(new Point(0, _owner.ActualHeight));
            Point bottomRight = transform.TransformPoint(new Point(_owner.ActualWidth, _owner.ActualHeight));
            AccessibilityRect ownerBounds = AccessibilityGeometry.NormalizeTransformedBounds(
                new AccessibilityPoint(topLeft.X, topLeft.Y),
                new AccessibilityPoint(topRight.X, topRight.Y),
                new AccessibilityPoint(bottomLeft.X, bottomLeft.Y),
                new AccessibilityPoint(bottomRight.X, bottomRight.Y));
            // TransformToVisual returns DIPs. Desktop WinUI providers use
            // rasterized client pixels, so scale once without a screen offset.
            double scale = _owner.XamlRoot.RasterizationScale;
            return new Rect(
                ownerBounds.X * scale, ownerBounds.Y * scale,
                ownerBounds.Width * scale, ownerBounds.Height * scale);
        }

        return _clip.GetClippedBoundingRectangle();
    }
}
