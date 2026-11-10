using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;

namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// Paints the atomic inline objects of an inline container, its raster
/// images and vector scenes, clipped to the viewport, and their
/// selected-state overlays.
/// </summary>
internal static class InlineObjectPainter
{
    internal static void DrawInlineImages(InlineContainerBox box, CanvasDrawingSession ds, float baseX, float baseY, Rect viewport)
    {
        if (box.TextLayout is null) return;

        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (run is InlineImageRun image && len > 0)
            {
            var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, cumulative, len);
                if (regions is null) { cumulative += len; continue; }
                foreach (var r in regions)
                {
                    Rect rect = InlineObjectGeometry.GetInlineImageDocumentRect(box, r, image, baseX, baseY);
                    image.Image.SetInlineBounds(rect);
                    image.Image.PaintInline(ds, rect, viewport);
                    break;
                }
            }

            cumulative += len;
        }
    }

    internal static void DrawSelectedInlineImages(
        InlineContainerBox box,
        CanvasDrawingSession ds,
        float baseX,
        float baseY,
        Rect viewport,
        DocumentRange range,
        Color selectionForeground)
    {
        if (box.TextLayout is null) return;

        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (run is InlineImageRun image && len > 0 && SelectionIntersectsRun(box, range, run, len))
            {
                var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, cumulative, len);
                if (regions is null) { cumulative += len; continue; }
                foreach (var r in regions)
                {
                    Rect rect = InlineObjectGeometry.GetInlineImageDocumentRect(box, r, image, baseX, baseY);
                    image.Image.SetInlineBounds(rect);
                    image.Image.PaintInlineSelectionForeground(ds, rect, viewport, selectionForeground);
                    break;
                }
            }

            cumulative += len;
        }
    }

    internal static void DrawInlineVectorScenes(
        InlineContainerBox box,
        CanvasDrawingSession drawingSession,
        float baseX,
        float baseY,
        Rect viewport)
    {
        foreach (InlineRun run in box.RunList)
        {
            if (run is not InlineVectorSceneRun vector)
                continue;

            Rect local = vector.LocalBounds;
            var bounds = new Rect(
                baseX + local.X,
                baseY + local.Y,
                local.Width,
                local.Height);
            if (!InlineDecorationPainter.Intersects(bounds, viewport))
                continue;
            vector.Paint(
                box.Context.PlacementTransform,
                drawingSession,
                baseX,
                baseY,
                box.GetRunStyle(vector).Foreground,
                box.Context.ThemeSnapshot.IsHighContrast);
        }
    }

    internal static void DrawSelectedInlineVectorScenes(
        InlineContainerBox box,
        CanvasDrawingSession drawingSession,
        float baseX,
        float baseY,
        Rect viewport,
        DocumentRange range,
        Color selectionForeground)
    {
        foreach (InlineRun run in box.RunList)
        {
            if (run is not InlineVectorSceneRun vector ||
                !SelectionIntersectsRun(box, range, run, run.TextLength))
            {
                continue;
            }

            Rect local = vector.LocalBounds;
            var bounds = new Rect(
                baseX + local.X,
                baseY + local.Y,
                local.Width,
                local.Height);
            if (!InlineDecorationPainter.Intersects(bounds, viewport))
                continue;
            vector.Paint(
                box.Context.PlacementTransform,
                drawingSession,
                baseX,
                baseY,
                selectionForeground,
                highContrast: true,
                foregroundOverride: selectionForeground);
        }
    }

    private static bool SelectionIntersectsRun(InlineContainerBox box, DocumentRange range, InlineRun run, int length)
    {
        var start = new DocumentPosition(box.BlockIndex, run.InlineIndex, 0);
        var end = new DocumentPosition(box.BlockIndex, run.InlineIndex, length);
        return end > range.Start && start < range.End;
    }
}
