using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;

namespace MarkdownRenderer.Layout.Internal.Vectors;

/// <summary>
/// Paints the text that stands in for a vector scene whose command graph was
/// retired (the detached fallback) or whose native resources were refused
/// (the capacity fallback), clipped to the visible scene viewport.
/// </summary>
internal sealed class VectorSceneFallbackPainter
{
    private readonly VectorSceneBox _host;

    internal VectorSceneFallbackPainter(VectorSceneBox host) => _host = host;

    internal void PaintDetachedFallback(CanvasDrawingSession drawingSession, Color foreground)
    {
        if (_host.NativeAdmission is not null)
        {
            PaintCapacityFallback(drawingSession, foreground);
            return;
        }

        CanvasTextLayout? layout = _host.DetachedFallbackLayout;
        if (layout is null)
            return;

        Rect clip = VectorViewportTransform.Intersect(_host.ContentBounds, _host.HorizontalViewportBounds);
        if (clip.Width <= 0 || clip.Height <= 0)
            return;

        Rect textBounds = layout.LayoutBounds;
        float left = (float)_host.ContentBounds.X;
        float top = (float)(_host.ContentBounds.Y + Math.Max(0, (_host.ContentBounds.Height - textBounds.Height) / 2d));
        NativePaintSessionResources.LayerLease fallbackLayer =
            _host.LayoutContext.RenderResources.PaintSessions.CreateLayer(drawingSession, 1f, clip);
        try
        {
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(
                _host.LayoutContext.PlacementTransform,
                drawingSession,
                layout,
                left,
                top,
                foreground);
        }
        catch (Exception primary)
        {
            fallbackLayer.ClosePreservingPrimary(primary);
            throw;
        }
        fallbackLayer.Close();
    }

    internal void PaintCapacityFallback(
        CanvasDrawingSession drawingSession,
        Color foreground,
        bool clipAlreadyApplied = false)
    {
        if (!VectorSceneCapacityFallback.TryGetFormat(
                _host.NativeAdmission,
                _host.LayoutContext.FlowDirection,
                out CanvasTextFormat format))
        {
            return;
        }

        string text = _host.CapacityFallbackText;
        if (text.Length == 0)
            return;

        Rect clip = VectorViewportTransform.Intersect(_host.ContentBounds, _host.HorizontalViewportBounds);
        if (clip.Width <= 0 || clip.Height <= 0)
            return;

        if (clipAlreadyApplied)
        {
            VectorSceneCapacityFallback.DrawTextWithScale(
                drawingSession,
                text,
                _host.ContentBounds,
                foreground,
                format,
                _host.LayoutContext.ThemeSnapshot.TextScaleFactor);
            return;
        }

        NativePaintSessionResources.LayerLease fallbackLayer =
            _host.LayoutContext.RenderResources.PaintSessions.CreateLayer(drawingSession, 1f, clip);
        try
        {
            VectorSceneCapacityFallback.DrawTextWithScale(
                drawingSession,
                text,
                _host.ContentBounds,
                foreground,
                format,
                _host.LayoutContext.ThemeSnapshot.TextScaleFactor);
        }
        catch (Exception primary)
        {
            fallbackLayer.ClosePreservingPrimary(primary);
            throw;
        }
        fallbackLayer.Close();
    }
}
