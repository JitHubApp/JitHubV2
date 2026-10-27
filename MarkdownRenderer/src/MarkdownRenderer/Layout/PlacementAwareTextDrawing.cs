using System;
using System.Numerics;
using System.Runtime.ExceptionServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Draws root-local text at its immutable logical placement without allowing
/// a placement-only world translation to perturb DirectWrite rasterization.
/// Nested scene renderers can supply their already-composed placement-free
/// transform so viewport and authored group transforms remain intact.
/// </summary>
internal static class PlacementAwareTextDrawing
{
    internal static void DrawAtRootLocalOrigin(
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        float rootLocalX,
        float rootLocalY,
        Color color)
    {
        LayoutPlacementTransformContext.GetCurrentTranslation(
            out float translationX,
            out float translationY);
        Matrix3x2 originalTransform = drawingSession.Transform;
        if (!LayoutPlacementTransformContext.TryGetPlacementFreeCanvasTransform(
                originalTransform,
                out Matrix3x2 placementFreeTransform))
        {
            drawingSession.DrawTextLayout(layout, rootLocalX, rootLocalY, color);
            return;
        }

        DrawAtTransform(
            drawingSession,
            layout,
            rootLocalX + translationX,
            rootLocalY + translationY,
            color,
            originalTransform,
            placementFreeTransform);
    }

    internal static void DrawAtPlacementFreeTransform(
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        float x,
        float y,
        Color color,
        Matrix3x2 originalTransform,
        Matrix3x2 placementFreeTransform)
    {
        if (originalTransform == placementFreeTransform)
        {
            drawingSession.DrawTextLayout(layout, x, y, color);
            return;
        }

        DrawAtTransform(
            drawingSession,
            layout,
            x,
            y,
            color,
            originalTransform,
            placementFreeTransform);
    }

    private static void DrawAtTransform(
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        float x,
        float y,
        Color color,
        Matrix3x2 originalTransform,
        Matrix3x2 drawTransform)
    {
        Exception? drawFailure = null;
        try
        {
            drawingSession.Transform = drawTransform;
            drawingSession.DrawTextLayout(layout, x, y, color);
        }
        catch (Exception exception)
        {
            drawFailure = exception;
        }

        Exception? restoreFailure = null;
        try
        {
            drawingSession.Transform = originalTransform;
        }
        catch (Exception exception)
        {
            restoreFailure = exception;
        }

        if (drawFailure is not null)
        {
            if (restoreFailure is not null)
            {
                throw new AggregateException(
                    "Text drawing and canvas-transform restoration both failed.",
                    drawFailure,
                    restoreFailure);
            }

            ExceptionDispatchInfo.Capture(drawFailure).Throw();
        }

        if (restoreFailure is not null)
            ExceptionDispatchInfo.Capture(restoreFailure).Throw();
    }
}
