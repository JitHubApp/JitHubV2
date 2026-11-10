using System;
using Microsoft.UI.Input;
using MarkdownRenderer.Layout.Internal.LazyLayout;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Computes the bounded document band a touch or pen gesture must hit-test,
/// including its contact probes.
/// </summary>
internal static class GestureHitTestBands
{
    internal static bool TryCreateGestureHitTestBand(
        Windows.Foundation.Point gestureCanvas,
        double rasterizationScale,
        bool includeContact,
        TouchContactGeometry capturedContact,
        out LazyLayoutBand band)
    {
        band = default;
        if (!double.IsFinite(gestureCanvas.X) || !double.IsFinite(gestureCanvas.Y))
            return false;

        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            rasterizationScale = 1;
        // The scale term covers one physical pixel, but a malformed caller
        // must not turn that term into a document-scale realization request.
        double radius = Math.Min(256, Math.Max(4, 1 / rasterizationScale));
        double top = gestureCanvas.Y - radius;
        double bottom = gestureCanvas.Y + radius;
        if (!double.IsFinite(top) || !double.IsFinite(bottom))
            return false;

        if (includeContact && IsBoundedGestureContact(gestureCanvas, capturedContact))
        {
            double contactTop = capturedContact.Y;
            double contactBottom = capturedContact.Y + capturedContact.Height;
            double centerY = capturedContact.CenterY;
            if (!double.IsFinite(contactBottom) || !double.IsFinite(centerY))
                return false;
            top = Math.Min(top, Math.Min(contactTop - 4, centerY - 4));
            bottom = Math.Max(bottom, Math.Max(contactBottom + 4, centerY + 4));
        }

        // HitTest samples outside the document are guaranteed misses. Clamp
        // the only in-document edge, and represent a wholly pre-document band
        // as empty so it cannot wait forever for the first block to measure.
        if (bottom < 0)
        {
            band = new LazyLayoutBand(0, 0);
            return true;
        }

        band = new LazyLayoutBand(Math.Max(0, top), Math.Max(0, bottom));
        return double.IsFinite(band.Top) && double.IsFinite(band.Bottom) &&
            band.Bottom >= band.Top;
    }

    internal static bool IsBoundedGestureContact(
        Windows.Foundation.Point gestureCanvas,
        TouchContactGeometry contact)
    {
        if (!double.IsFinite(contact.X) || !double.IsFinite(contact.Y) ||
            !double.IsFinite(contact.Width) || !double.IsFinite(contact.Height) ||
            contact.Width <= 0 || contact.Height <= 0 ||
            contact.Width > 256 || contact.Height > 256)
        {
            return false;
        }

        double right = contact.X + contact.Width;
        double bottom = contact.Y + contact.Height;
        double centerX = contact.CenterX;
        double centerY = contact.CenterY;
        if (!double.IsFinite(right) || !double.IsFinite(bottom) ||
            !double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            return false;
        }

        double dx = centerX - gestureCanvas.X;
        double dy = centerY - gestureCanvas.Y;
        return double.IsFinite(dx) && double.IsFinite(dy) &&
            dx * dx + dy * dy <= 256 * 256;
    }

    internal static bool IncludesContactProbes(PointerInput input) =>
        input.HasGestureContact &&
        (input.GestureDevice is PointerDeviceType.Touch or PointerDeviceType.Pen) &&
        (input.Kind is PointerInputKind.DoubleTap or PointerInputKind.Hold or PointerInputKind.RightTap);
}
