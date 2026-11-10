using System;
using System.Numerics;
using System.Runtime.ExceptionServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Placement;

namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

/// <summary>
/// Paints one prepared vector scene's command stream into a drawing session:
/// viewport and placement transforms, group transforms, LIFO clip layers and
/// the styled primitives. The caller's transform is always restored, and an
/// uncertain native clip depth quarantines the owning drawing.
/// </summary>
internal sealed class VectorSceneCommandPainter
{
    private readonly VectorSceneDrawing _host;

    internal VectorSceneCommandPainter(VectorSceneDrawing host) => _host = host;

    internal void Paint(
        LayoutPlacementTransformContext placement,
        CanvasDrawingSession drawingSession,
        MarkdownVectorScene scene,
        VectorScenePaintSlots slots,
        Rect destination,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        if (destination.Width <= 0 || destination.Height <= 0 ||
            scene.Viewport.Width <= 0 || scene.Viewport.Height <= 0)
        {
            return;
        }

        Matrix3x2 callerTransform = drawingSession.Transform;
        bool hasPlacement = placement.TryGetPlacementFreeCanvasTransform(
            callerTransform,
            out Matrix3x2 placementFreeCallerTransform);
        if (hasPlacement)
        {
            placement.GetCurrentTranslation(
                out float placementX,
                out float placementY);
            destination = new Rect(
                destination.X + placementX,
                destination.Y + placementY,
                destination.Width,
                destination.Height);
        }

        VectorViewportTransform viewportTransform = VectorViewportTransform.Create(
            scene.Viewport,
            destination);
        if (!float.IsFinite(viewportTransform.Scale) || viewportTransform.Scale <= 0)
            return;
        Matrix3x2 originalTransform = callerTransform;
        Matrix3x2 sceneBaseTransform = hasPlacement
            ? placementFreeCallerTransform
            : callerTransform;

        int clipDepth = 0;
        Exception? paintFailure = null;
        bool clipPopFailed = false;
        try
        {
            drawingSession.Transform = viewportTransform.Matrix * sceneBaseTransform;
            PaintCommands(drawingSession, scene, slots, palette, highContrast, foregroundOverride,
                ref clipDepth, ref clipPopFailed);
        }
        catch (Exception exception)
        {
            paintFailure = exception;
        }

        Exception? clipCleanupFailure = UnwindClipStack(drawingSession, slots.ClipStack, clipDepth, clipPopFailed);

        Exception? transformRestoreFailure = null;
        try
        {
            drawingSession.Transform = originalTransform;
        }
        catch (Exception exception)
        {
            transformRestoreFailure = exception;
        }

        ThrowPaintFailures(paintFailure, clipCleanupFailure, transformRestoreFailure);
    }

    private void PaintCommands(
        CanvasDrawingSession drawingSession,
        MarkdownVectorScene scene,
        VectorScenePaintSlots slots,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride,
        ref int clipDepth,
        ref bool clipPopFailed)
    {
        Matrix3x2[] transformStack = slots.TransformStack;
        int transformDepth = 0;
        int geometryIndex = 0;
        int textIndex = 0;
        int strokeIndex = 0;
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            MarkdownVectorCommand command = scene.Commands[i];
            MarkdownVectorPaintStyle? style = command.Style;
            // Consume slots independently of draw visibility, opacity,
            // semantic overrides or the current switch branch. This is the
            // same immutable command order used by preparation, requiring
            // neither command-sized lookup maps nor paint allocations.
            CanvasGeometry? geometry = VectorSceneResourceShapes.RequiresGeometry(command.Kind)
                ? slots.Geometries[geometryIndex++] : null;
            CanvasTextLayout? textLayout = VectorSceneResourceShapes.RequiresTextLayout(command)
                ? slots.TextLayouts[textIndex++] : null;
            CanvasStrokeStyle? strokeStyle = style is not null && VectorSceneResourceShapes.RequiresStrokeStyle(style)
                ? slots.StrokeStyles[strokeIndex++] : null;
            switch (command.Kind)
            {
                case MarkdownVectorCommandKind.BeginGroup:
                    transformStack[transformDepth++] = drawingSession.Transform;
                    drawingSession.Transform = VectorSceneNativeFactory.ToMatrix(command.Transform) * drawingSession.Transform;
                    break;
                case MarkdownVectorCommandKind.EndGroup:
                    if (transformDepth > 0)
                        drawingSession.Transform = transformStack[--transformDepth];
                    break;
                case MarkdownVectorCommandKind.BeginClip:
                    if (geometry is { } clip)
                        PushClip(drawingSession, clip, slots.ClipStack, ref clipDepth, ref clipPopFailed);
                    break;
                case MarkdownVectorCommandKind.EndClip:
                    if (clipDepth > 0)
                        PopClip(slots.ClipStack, ref clipDepth, ref clipPopFailed);
                    break;
                default:
                    PaintPrimitive(drawingSession, scene, command, style, geometry, textLayout, strokeStyle,
                        palette, highContrast, foregroundOverride);
                    break;
            }
        }
    }

    private void PushClip(
        CanvasDrawingSession drawingSession,
        CanvasGeometry clip,
        CanvasActiveLayer?[] clipStack,
        ref int clipDepth,
        ref bool clipPopFailed)
    {
        CanvasActiveLayer layer;
        try
        {
            layer = _host.ClipLayerFactory is { } factory
                ? factory(drawingSession, clip)
                : drawingSession.CreateLayer(1f, clip);
        }
        catch
        {
            // A native factory can fail after an uncertain
            // push. No returned wrapper can describe that
            // depth, so never close known ancestors beneath
            // it. Publish depth only after factory success.
            clipPopFailed = true;
            throw;
        }
        clipStack[clipDepth] = layer;
        clipDepth++;
    }

    private void PopClip(CanvasActiveLayer?[] clipStack, ref int clipDepth, ref bool clipPopFailed)
    {
        int activeClipIndex = clipDepth - 1;
        CanvasActiveLayer? activeLayer = clipStack[activeClipIndex];
        try
        {
            if (activeLayer is not null)
                _host.NativeDisposer(activeLayer);
        }
        catch
        {
            // A failed pop leaves the native stack's exact
            // depth unknown. Do not retry it, or pop an
            // ancestor underneath it, during unwind.
            clipPopFailed = true;
            throw;
        }
        clipStack[activeClipIndex] = null;
        clipDepth = activeClipIndex;
    }

    private void PaintPrimitive(
        CanvasDrawingSession drawingSession,
        MarkdownVectorScene scene,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle? style,
        CanvasGeometry? geometry,
        CanvasTextLayout? textLayout,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        VectorScenePrimitivePainter primitives = _host.Primitives;
        switch (command.Kind)
        {
            case MarkdownVectorCommandKind.FillPath:
                if (geometry is { } fillGeometry)
                    drawingSession.FillGeometry(fillGeometry, VectorScenePaintResolver.ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride));
                break;
            case MarkdownVectorCommandKind.StrokePath:
                if (geometry is { } strokeGeometry)
                    drawingSession.DrawGeometry(strokeGeometry, VectorScenePaintResolver.ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                break;
            case MarkdownVectorCommandKind.FillRectangle:
                drawingSession.FillRectangle(VectorSceneNativeFactory.ToRect(command.Rectangle), VectorScenePaintResolver.ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride));
                break;
            case MarkdownVectorCommandKind.StrokeRectangle:
                drawingSession.DrawRectangle(VectorSceneNativeFactory.ToRect(command.Rectangle), VectorScenePaintResolver.ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                break;
            case MarkdownVectorCommandKind.StrokeLine:
                drawingSession.DrawLine(command.Start.X, command.Start.Y, command.End.X, command.End.Y,
                    VectorScenePaintResolver.ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                break;
            case MarkdownVectorCommandKind.DrawRectangle when style is not null:
                primitives.DrawRectangle(scene, drawingSession, command, style, strokeStyle, palette, highContrast, foregroundOverride);
                break;
            case MarkdownVectorCommandKind.DrawPath when style is not null:
                primitives.DrawPath(scene, drawingSession, command, geometry, style, strokeStyle, palette, highContrast, foregroundOverride);
                break;
            case MarkdownVectorCommandKind.DrawLine when style is not null:
                if (style.StrokeWidth > 0 && VectorScenePaintResolver.TryResolveColor(style.StrokeArgb, style.Opacity,
                        palette, _host.ResolvePaintRole(
                            command,
                            style.StrokeRole,
                            style.StrokeArgb,
                            style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                            highContrast),
                        highContrast, foregroundOverride, out Color lineColor))
                {
                    drawingSession.DrawLine(command.Start.X, command.Start.Y, command.End.X, command.End.Y,
                        lineColor, style.StrokeWidth, strokeStyle);
                }
                break;
            case MarkdownVectorCommandKind.DrawEllipse when style is not null:
                primitives.DrawEllipse(scene, drawingSession, command, style, strokeStyle, palette, highContrast, foregroundOverride);
                break;
            case MarkdownVectorCommandKind.DrawText when style is not null:
                primitives.DrawText(drawingSession, command, style, textLayout, palette, highContrast, foregroundOverride);
                break;
        }
    }

    // Pops the clip layers still open after the pass, in LIFO order. A pop
    // that already failed during the pass leaves the depth uncertain, so the
    // drawing is quarantined instead of unwound.
    private Exception? UnwindClipStack(
        CanvasDrawingSession drawingSession,
        CanvasActiveLayer?[] clipStack,
        int clipDepth,
        bool clipPopFailed)
    {
        if (clipPopFailed)
        {
            _host.MarkClipCleanupFailed(drawingSession);
            return null;
        }

        try
        {
            while (clipDepth > 0)
            {
                int activeClipIndex = clipDepth - 1;
                if (clipStack[activeClipIndex] is { } layer)
                    _host.NativeDisposer(layer);
                clipStack[activeClipIndex] = null;
                clipDepth = activeClipIndex;
            }
        }
        catch (Exception exception)
        {
            // Unwind must obey the same LIFO rule as EndClip. The first
            // failed pop retains the remaining stack for quarantine.
            _host.MarkClipCleanupFailed(drawingSession);
            return exception;
        }
        return null;
    }

    /// <summary>
    /// Rethrows the paint pass's failures in precedence order: the paint
    /// failure first, aggregated with any clip-unwind or transform-restore
    /// failure that followed it, then cleanup failures on their own.
    /// </summary>
    private static void ThrowPaintFailures(
        Exception? paintFailure,
        Exception? clipCleanupFailure,
        Exception? transformRestoreFailure)
    {
        if (paintFailure is not null)
        {
            if (clipCleanupFailure is not null && transformRestoreFailure is not null)
            {
                throw new AggregateException(
                    "Vector painting and cleanup both failed.",
                    paintFailure,
                    clipCleanupFailure,
                    transformRestoreFailure);
            }
            if (clipCleanupFailure is not null)
                throw new AggregateException("Vector painting and clip cleanup both failed.", paintFailure, clipCleanupFailure);
            if (transformRestoreFailure is not null)
                throw new AggregateException("Vector painting and transform restoration both failed.", paintFailure, transformRestoreFailure);

            ExceptionDispatchInfo.Capture(paintFailure).Throw();
        }

        if (clipCleanupFailure is not null)
        {
            if (transformRestoreFailure is not null)
            {
                throw new AggregateException(
                    "Vector clip cleanup and transform restoration both failed.",
                    clipCleanupFailure,
                    transformRestoreFailure);
            }

            ExceptionDispatchInfo.Capture(clipCleanupFailure).Throw();
        }

        if (transformRestoreFailure is not null)
            ExceptionDispatchInfo.Capture(transformRestoreFailure).Throw();
    }
}
