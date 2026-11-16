using System;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Layout.Internal.Boxes;

/// <summary>
/// Paint-only background geometry for the selection adorner. This is kept
/// separate from <see cref="BlockBox.GetSelectionRects"/>, whose rectangles
/// also define interaction and endpoint geometry.
/// </summary>
internal readonly record struct SelectionBackgroundGeometry(Rect Bounds, float Opacity)
{
    internal void Paint(CanvasDrawingSession drawingSession, Color color, bool highContrast)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);

        float opacity = highContrast ? 1f : Opacity;
        if (!float.IsFinite(opacity))
            return;
        opacity = Math.Clamp(opacity, 0f, 1f);
        if (opacity <= 0f || Bounds.IsEmpty)
            return;

        if (opacity < 1f)
        {
            byte alpha = (byte)Math.Clamp(
                MathF.Round(color.A * opacity),
                0f,
                255f);
            color = Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        drawingSession.FillRectangle(Bounds, color);
    }
}
