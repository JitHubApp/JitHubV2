using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.ExceptionServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Paints an inline container's chrome and measured text layout: the text
/// itself at its device-snapped origin (through the script-baseline
/// renderer when superscript or subscript runs are present), the heading
/// rule, the selected-text foreground and the focused or hovered link
/// foreground.
/// </summary>
internal static class InlineTextPainter
{
    private static void DrawTextLayoutAtFinalOrigin(
        InlineContainerBox box,
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        float finalOriginX,
        float finalOriginY,
        Color color,
        int foregroundOverrideStart = -1,
        int foregroundOverrideLength = 0)
    {
        if (box.ScriptBaselineGeometry.Count != 0)
        {
            DrawTextLayoutWithScriptBaselines(
                box,
                drawingSession,
                layout,
                finalOriginX,
                finalOriginY,
                color,
                foregroundOverrideStart,
                foregroundOverrideLength);
            return;
        }

        Matrix3x2 originalTransform = drawingSession.Transform;
        if (!box.Context.PlacementTransform.TryGetPlacementFreeCanvasTransform(
                originalTransform,
                out Matrix3x2 placementFreeTransform))
        {
            drawingSession.DrawTextLayout(layout, finalOriginX, finalOriginY, color);
            return;
        }

        // Placement-only world translations perturb DirectWrite's glyph
        // rasterization even when the final logical origin is identical.
        // Draw text with the caller's exact matrix at the final snapped
        // coordinate; other box geometry retains its local-coordinate path.
        PlacementAwareTextDrawing.DrawAtPlacementFreeTransform(
            drawingSession,
            layout,
            finalOriginX,
            finalOriginY,
            color,
            originalTransform,
            placementFreeTransform);
    }

    private static void DrawTextLayoutWithScriptBaselines(
        InlineContainerBox box,
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        float finalOriginX,
        float finalOriginY,
        Color color,
        int foregroundOverrideStart,
        int foregroundOverrideLength)
    {
        ElementStyle containerStyle = box.GetContainerStyle();
        // CanvasTextLayout.DrawTextLayout's brush overload supplies a fallback
        // for glyph runs whose callback brush is null. Recreate that behavior
        // here: primary text inherits the paragraph foreground, while the
        // selection clone inherits the selection foreground. A link-state
        // overlay may additionally override only its own source range.
        Color defaultForegroundColor = ReferenceEquals(layout, box.TextLayout)
            ? containerStyle.Foreground
            : color;
        using var defaultForeground = new CanvasSolidColorBrush(
            box.Context.ResourceCreator,
            defaultForegroundColor);
        bool useForegroundOverride = ReferenceEquals(layout, box.TextLayout) && foregroundOverrideLength > 0;
        using CanvasSolidColorBrush? foregroundOverride = useForegroundOverride
            ? new CanvasSolidColorBrush(box.Context.ResourceCreator, color)
            : null;
        using var renderer = new ScriptBaselineTextRenderer(
            drawingSession,
            box.ScriptBaselineGeometry,
            defaultForeground,
            foregroundOverride,
            foregroundOverrideStart,
            foregroundOverrideLength);

        Matrix3x2 originalTransform = drawingSession.Transform;
        if (!box.Context.PlacementTransform.TryGetPlacementFreeCanvasTransform(
                originalTransform,
                out Matrix3x2 placementFreeTransform) ||
            originalTransform == placementFreeTransform)
        {
            layout.DrawToTextRenderer(renderer, finalOriginX, finalOriginY);
            return;
        }

        Exception? drawFailure = null;
        try
        {
            drawingSession.Transform = placementFreeTransform;
            layout.DrawToTextRenderer(renderer, finalOriginX, finalOriginY);
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
                throw new AggregateException("Script text drawing and canvas-transform restoration both failed.", drawFailure, restoreFailure);
            ExceptionDispatchInfo.Capture(drawFailure).Throw();
        }

        if (restoreFailure is not null)
            ExceptionDispatchInfo.Capture(restoreFailure).Throw();
    }

    private static void DrawHeadingBottomBorder(
        InlineContainerBox box,
        CanvasDrawingSession ds,
        Color color,
        float thickness)
    {
        if (!float.IsFinite(thickness) || thickness <= 0)
            return;

        Thickness margin = box.Margin;
        float left = (float)(box.Bounds.X + margin.Left);
        float right = (float)(box.Bounds.Right - margin.Right);
        float bottom = (float)(box.Bounds.Bottom - margin.Bottom);
        if (!float.IsFinite(left) || !float.IsFinite(right) ||
            !float.IsFinite(bottom) || right <= left)
        {
            return;
        }

        float scale = (float)box.Context.RasterizationScale;
        if (!float.IsFinite(scale) || scale <= 0)
            scale = 1;
        // Align the outside edge to a device-pixel boundary, then center the
        // stroke inside its reserved final padding band. This keeps 1-DIP
        // heading rules crisp at integral DPI scales.
        float snappedBottom = MathF.Round(bottom * scale) / scale;
        float centerY = snappedBottom - thickness / 2f;
        ds.DrawLine(left, centerY, right, centerY, color, thickness);
    }
    internal static void Paint(InlineContainerBox box, CanvasDrawingSession ds, Rect viewport)
    {
        if (box.TextLayout is null) return;
        var style = box.GetContainerStyle();

        if (box.DrawContainerChrome && style.Background is { } bg)
        {
            var margin = box.Margin;
            var rect = new Rect(
                box.Bounds.X + margin.Left,
                box.Bounds.Y + margin.Top,
                box.Bounds.Width - margin.Left - margin.Right,
                box.Bounds.Height - margin.Top - margin.Bottom);
            ds.FillRoundedRectangle(rect, style.CornerRadius, style.CornerRadius, bg);
        }

        bool drawHeadingBottomBorder =
            style.HeadingBorderBottomOnly &&
            (box.ElementKey is MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2) &&
            style.BorderBrush is not null &&
            float.IsFinite(style.BorderThickness) &&
            style.BorderThickness > 0;
        if (box.DrawContainerChrome && !drawHeadingBottomBorder &&
            style.BorderBrush is { } border && style.BorderThickness > 0)
        {
            var margin = box.Margin;
            var rect = new Rect(
                box.Bounds.X + margin.Left,
                box.Bounds.Y + margin.Top,
                box.Bounds.Width - margin.Left - margin.Right,
                box.Bounds.Height - margin.Top - margin.Bottom);
            float nnset = style.BorderThickness / 2f;
            ds.DrawRoundedRectangle(
                new Rect(
                    rect.X + nnset,
                    rect.Y + nnset,
                    Math.Max(0, rect.Width - style.BorderThickness),
                    Math.Max(0, rect.Height - style.BorderThickness)),
                style.CornerRadius,
                style.CornerRadius,
                border,
                style.BorderThickness);
        }
        if (drawHeadingBottomBorder && style.BorderBrush is { } headingBorder)
            DrawHeadingBottomBorder(box, ds, headingBorder, style.BorderThickness);

        // Hover state is intentionally NOT applied to the text layout —
        // see HoveredRun docs for why.  Link hover affordance is conveyed by
        // the cursor shape change in MarkdownRendererControl.OnPointerMoved.
        // Snap to integer pixels at the draw site.  See GetSnappedOrigin
        // for the rationale; the same snapped origin is used for hit-test,
        // selection rects and embed placement so they stay in sync.
        var (sx, sy) = box.GetSnappedOrigin(style, out float textOriginX, out float textOriginY);
        InlineDecorationPainter.DrawRunBackgrounds(box, ds, sx, sy);
        DrawTextLayoutAtFinalOrigin(box, ds, box.TextLayout, textOriginX, textOriginY, style.Foreground);
        InlineObjectPainter.DrawInlineImages(box, ds, sx, sy, viewport);
        InlineObjectPainter.DrawInlineVectorScenes(box, ds, sx, sy, viewport);

        InlineDecorationPainter.DrawDecorations(box, ds, sx, sy);
    }
    internal static void PaintSelectionForeground(InlineContainerBox box, CanvasDrawingSession ds, DocumentRange range, Color color, Rect viewport)
    {
        if (box.TextLayout is null) return;

        var normalized = range.Normalized();
        box.EnsureBuffer();
        int from = InlineRangeGeometry.ToBufferIndex(box, normalized.Start);
        int to = InlineRangeGeometry.ToBufferIndex(box, normalized.End);
        if (to <= from)
            return;

        var visibleSelectionRects = new List<Rect>();
        foreach (var rect in box.GetRangeRects(normalized))
        {
            if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
                rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
            {
                continue;
            }

            visibleSelectionRects.Add(rect);
        }

        if (visibleSelectionRects.Count == 0) return;

        var style = box.GetContainerStyle();
        var (sx, sy) = box.GetSnappedOrigin(style, out float textOriginX, out float textOriginY);
        var layout = box.EnsureSelectionLayout(color, style);
        // Draw a color-neutral copy of the full layout through the exact
        // selected character regions. Masking non-selected glyphs with a
        // transparent text color is unreliable on some Win2D/DirectWrite
        // paths: the later selected-color range can remain transparent and
        // leave an opaque highlight with no readable glyphs. Geometry clips
        // preserve the same native ordering and work for wrapped selections.
        foreach (var rect in visibleSelectionRects)
        {
            NativePaintSessionResources.LayerLease layer = box.Context.RenderResources.PaintSessions.CreateLayer(ds, 1.0f, rect);
            try
            {
                DrawTextLayoutAtFinalOrigin(box, ds, layout, textOriginX, textOriginY, color);
            }
            catch (Exception primary)
            {
                layer.ClosePreservingPrimary(primary);
                throw;
            }
            layer.Close();
        }
        InlineDecorationPainter.DrawSelectedDecorations(box, ds, sx, sy, color, from, to - from);
        InlineObjectPainter.DrawSelectedInlineImages(box, ds, sx, sy, viewport, normalized, color);
        InlineObjectPainter.DrawSelectedInlineVectorScenes(box, ds, sx, sy, viewport, normalized, color);
    }
    internal static void PaintLinkStateForeground(InlineContainerBox box, CanvasDrawingSession ds, LinkRun link, bool focused, Rect viewport)
    {
        if (box.TextLayout is null || link.TextLength == 0)
            return;

        var style = box.GetContainerStyle();
        var (sx, sy) = box.GetSnappedOrigin(style, out float textOriginX, out float textOriginY);
        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (!ReferenceEquals(run, link))
            {
                cumulative += len;
                continue;
            }

            var runStyle = box.GetRunStyle(run);
            var color = focused
                ? runStyle.FocusForeground ?? runStyle.HoverForeground ?? runStyle.Foreground
                : runStyle.HoverForeground ?? runStyle.Foreground;
            var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, cumulative, len);
            if (regions is null)
                return;

            var lineMetrics = box.TryGetLineMetrics();
            foreach (var r in regions)
            {
                var rect = new Rect(
                    sx + r.LayoutBounds.X,
                    sy + r.LayoutBounds.Y,
                    r.LayoutBounds.Width,
                    r.LayoutBounds.Height);
                if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
                    rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
                {
                    continue;
                }

                NativePaintSessionResources.LayerLease layer = box.Context.RenderResources.PaintSessions.CreateLayer(ds, 1.0f, rect);
                try
                {
                    DrawTextLayoutAtFinalOrigin(
                        box,
                        ds,
                        box.TextLayout,
                        textOriginX,
                        textOriginY,
                        color,
                        foregroundOverrideStart: cumulative,
                        foregroundOverrideLength: len);
                    InlineDecorationPainter.DrawRunDecorations(ds, sx, sy, lineMetrics, r, run, runStyle, color);
                }
                catch (Exception primary)
                {
                    layer.ClosePreservingPrimary(primary);
                    throw;
                }
                layer.Close();
            }

            InlineDecorationPainter.DrawLinkInlineSpanDecorations(
                box,
                ds,
                sx,
                sy,
                link,
                cumulative,
                runStyle,
                color,
                selectedStart: null,
                selectedEnd: null,
                viewport: viewport,
                lineMetrics: lineMetrics);

            return;
        }
    }
}
