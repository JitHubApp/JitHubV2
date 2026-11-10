using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Paints the measured top-level roots that intersect a viewport, including
/// the selection foreground pass, under the snapshot paint lease.
/// </summary>
internal sealed class SnapshotPainter
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;

    internal SnapshotPainter(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery)
    {
        _geometry = geometry;
        _recovery = recovery;
    }

    internal void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long inlineBytes = 0;
        long tableBytes = 0;
        long codeBytes = 0;
        long otherBytes = 0;
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return;

            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            var range = _geometry.FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
            for (int i = range.Start; i < range.End; i++)
            {
                int n = placements.GetBlockOrdinal(i);
                var b = _geometry.Blocks[n];
                LayoutRootPlacement placement = placements.GetPlacement(n);
                if (placement.Bottom < viewport.Top || placement.Top > viewport.Bottom) continue;
                if (!placement.Measured) continue;
                long before = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                Matrix3x2 originalTransform = ds.Transform;
                using LayoutPlacementTransformContext.Scope placementScope =
                    _geometry.PlacementTransform.Enter(
                        placement.TranslationX, placement.TranslationY, originalTransform);
                try
                {
                    ds.Transform = Matrix3x2.CreateTranslation(
                        placement.TranslationX,
                        placement.TranslationY) * originalTransform;
                    b.Paint(ds, placements.ToPhysicalViewport(n, viewport));
                }
                finally
                {
                    ds.Transform = originalTransform;
                }
                if (measure)
                {
                    long allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
                    switch (b)
                    {
                        case InlineContainerBox:
                            inlineBytes += allocated;
                            break;
                        case TableBox:
                            tableBytes += allocated;
                            break;
                        case CodeBlockBox:
                            codeBytes += allocated;
                            break;
                        default:
                            otherBytes += allocated;
                            break;
                    }
                }
            }
        }
        if (measure)
        {
            performance.SnapshotPaintAllocationBreakdown(
                MarkdownPerformanceEventSource.GetLogicalFrameId(),
                inlineBytes,
                tableBytes,
                codeBytes,
                otherBytes);
        }
    }

    internal void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return;

            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            var visible = _geometry.FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
            for (int i = visible.Start; i < visible.End; i++)
            {
                int n = placements.GetBlockOrdinal(i);
                var b = _geometry.Blocks[n];
                LayoutRootPlacement placement = placements.GetPlacement(n);
                if (placement.Bottom < viewport.Top || placement.Top > viewport.Bottom) continue;
                if (!placement.Measured) continue;
                Matrix3x2 originalTransform = ds.Transform;
                using LayoutPlacementTransformContext.Scope placementScope =
                    _geometry.PlacementTransform.Enter(
                        placement.TranslationX, placement.TranslationY, originalTransform);
                try
                {
                    ds.Transform = Matrix3x2.CreateTranslation(
                        placement.TranslationX,
                        placement.TranslationY) * originalTransform;
                    PaintSelectionForeground(
                        b, ds, range, color, placements.ToPhysicalViewport(n, viewport));
                }
                finally
                {
                    ds.Transform = originalTransform;
                }
            }
        }
    }

    private static void PaintSelectionForeground(BlockBox box, CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        box.PaintSelectionForeground(ds, range, color, viewport);
    }
}
