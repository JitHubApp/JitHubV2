using System;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Paints the common local horizontal-overflow affordance for native renderer
/// blocks. Color resolution stays in the immutable theme snapshot so the hot
/// paint path never consults WinUI resources.
/// </summary>
internal static class HorizontalOverflowVisual
{
    private const float Thickness = 4f;

    internal static void Paint(
        CanvasDrawingSession drawingSession,
        Rect track,
        Rect thumb,
        ThemeSnapshot themeSnapshot,
        Color fallback)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(themeSnapshot);
        if (track.IsEmpty || thumb.IsEmpty)
            return;

        (Color trackColor, Color thumbColor) =
            themeSnapshot.ResolveOverflowIndicatorColors(fallback);
        var visualTrack = new Rect(
            track.X,
            track.Y + (track.Height - Thickness) / 2f,
            track.Width,
            Thickness);
        var visualThumb = new Rect(
            thumb.X,
            thumb.Y + (thumb.Height - Thickness) / 2f,
            thumb.Width,
            Thickness);
        float radius = Thickness / 2f;
        drawingSession.FillRoundedRectangle(visualTrack, radius, radius, trackColor);
        drawingSession.FillRoundedRectangle(visualThumb, radius, radius, thumbColor);
    }
}
