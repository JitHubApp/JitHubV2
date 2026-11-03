using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Owns device-dependent resources for one immutable vector scene. Both inline
/// formulas and block diagrams use this implementation so their paint behavior
/// cannot drift.
/// </summary>

/// <summary>
/// Draws the styled vector primitives (rectangles, paths, ellipses and text)
/// of one scene command with resolved semantic paint roles.
/// </summary>
internal sealed class VectorScenePrimitivePainter
{
    private readonly VectorSceneDrawing _host;

    internal VectorScenePrimitivePainter(VectorSceneDrawing host) => _host = host;

    internal void DrawRectangle(
        MarkdownVectorScene scene,
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        Rect rect = VectorSceneNativeFactory.ToRect(command.Rectangle);
        MarkdownVectorPaintRole fillRole = _host.ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? VectorSceneDrawing.InferFillRole(command, style, VectorScenePaintResolver.GetSemanticRole(scene, command)),
            highContrast);
        if (VectorScenePaintResolver.TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillRectangle(rect, fill);
        if (style.StrokeWidth > 0 && VectorScenePaintResolver.TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                _host.ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawRectangle(rect, stroke, style.StrokeWidth, strokeStyle);
    }

    internal void DrawPath(
        MarkdownVectorScene scene,
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        CanvasGeometry? geometry,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        if (geometry is null)
            return;
        MarkdownVectorPaintRole fillRole = _host.ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? VectorSceneDrawing.InferFillRole(command, style, VectorScenePaintResolver.GetSemanticRole(scene, command)),
            highContrast);
        if (VectorScenePaintResolver.TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillGeometry(geometry, fill);
        if (style.StrokeWidth > 0 && VectorScenePaintResolver.TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                _host.ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawGeometry(geometry, stroke, style.StrokeWidth, strokeStyle);
    }

    internal void DrawEllipse(
        MarkdownVectorScene scene,
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        MarkdownVectorRectangle rectangle = command.Rectangle;
        float centerX = rectangle.X + rectangle.Width / 2f;
        float centerY = rectangle.Y + rectangle.Height / 2f;
        float radiusX = rectangle.Width / 2f;
        float radiusY = rectangle.Height / 2f;
        MarkdownVectorPaintRole fillRole = _host.ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? VectorSceneDrawing.InferFillRole(command, style, VectorScenePaintResolver.GetSemanticRole(scene, command)),
            highContrast);
        if (VectorScenePaintResolver.TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillEllipse(centerX, centerY, radiusX, radiusY, fill);
        if (style.StrokeWidth > 0 && VectorScenePaintResolver.TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                _host.ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawEllipse(centerX, centerY, radiusX, radiusY, stroke, style.StrokeWidth, strokeStyle);
    }

    internal void DrawText(
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasTextLayout? layout,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        uint? textArgb = style.FillArgb ?? style.StrokeArgb;
        MarkdownVectorPaintRole declaredRole = style.FillArgb is not null || style.StrokeArgb is null
            ? style.FillRole
            : style.StrokeRole;
        MarkdownVectorPaintRole highContrastRole = style.FillArgb is not null || style.StrokeArgb is null
            ? style.HighContrastFillRole ?? MarkdownVectorPaintRole.Foreground
            : style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        if (layout is null || !VectorScenePaintResolver.TryResolveColor(textArgb, style.Opacity,
                palette, _host.ResolvePaintRole(
                    command,
                    declaredRole,
                    textArgb,
                    highContrastRole,
                    highContrast),
                highContrast, foregroundOverride, out Color color))
        {
            return;
        }

        Vector2 origin = VectorSceneNativeFactory.ResolveTextOrigin(command, layout);
        ds.DrawTextLayout(layout, origin.X, origin.Y, color);
    }
}
