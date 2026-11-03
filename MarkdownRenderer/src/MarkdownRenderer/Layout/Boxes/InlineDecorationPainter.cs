using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Paints the run-level decorations of an inline container: inline-code and
/// highlight backgrounds, underlines and strikethroughs (including nested
/// link spans), and inline image and vector-scene content, for both normal
/// and selected passes.
/// </summary>
internal static class InlineDecorationPainter
{

    internal static bool Intersects(Rect left, Rect right) =>
        left.Right >= right.Left && left.Left <= right.Right &&
        left.Bottom >= right.Top && left.Top <= right.Bottom;

    internal static void DrawDecorations(InlineContainerBox box, CanvasDrawingSession ds, float baseX, float baseY)
        => DrawDecorations(box, ds, baseX, baseY, null);

    private static void DrawDecorations(InlineContainerBox box, CanvasDrawingSession ds, float baseX, float baseY, Color? overrideColor)
    {
        if (box.TextLayout is null) return;

        int cumulative = 0;
        CanvasLineMetrics[]? lineMetrics = null;
        bool lineMetricsAttempted = false;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (len == 0) { continue; }
            if (run is InlineEmbedRun or InlineImageRun or InlineVectorSceneRun or InlineSpacerRun) { cumulative += len; continue; }
            // Superscript link runs (footnote citation markers ¹²³…) should not
            // have an underline drawn at the normal line baseline — the small
            // Unicode glyphs sit high in the line and the baseline underline ends
            // up drawn ~10 px below them, looking completely detached.  Skip
            // decorations for these runs entirely; their link appearance is already
            // communicated by the accent foreground color.
            if (run is LinkRun { IsSuperscript: true }) { cumulative += len; continue; }

            var rs = box.GetRunStyle(run);
            var decorationColor = overrideColor ?? rs.Foreground;

            if (rs.Underline || rs.Strikethrough)
            {
                if (!lineMetricsAttempted)
                {
                    lineMetrics = box.TryGetLineMetrics();
                    lineMetricsAttempted = true;
                }
                var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, cumulative, len);
                if (regions is null) { cumulative += len; continue; } // Win2D can return null on DirectWrite errors
                foreach (var r in regions)
                {
                    DrawRunDecorations(ds, baseX, baseY, lineMetrics, r, run, rs, decorationColor);
                }
            }
            if (run is LinkRun { InlineStyleMap: not null } styledLink)
            {
                if (!lineMetricsAttempted)
                {
                    lineMetrics = box.TryGetLineMetrics();
                    lineMetricsAttempted = true;
                }
                DrawLinkInlineSpanDecorations(
                    box,
                    ds,
                    baseX,
                    baseY,
                    styledLink,
                    cumulative,
                    rs,
                    decorationColor,
                    selectedStart: null,
                    viewport: null,
                    selectedEnd: null,
                    lineMetrics: lineMetrics);
            }
            cumulative += len;
        }
    }

    internal static void DrawSelectedDecorations(InlineContainerBox box, CanvasDrawingSession ds, float baseX, float baseY, Color color, int selectedStart, int selectedLength)
    {
        if (box.TextLayout is null || selectedLength <= 0)
            return;

        int selectedEnd = selectedStart + selectedLength;
        int cumulative = 0;
        CanvasLineMetrics[]? lineMetrics = null;
        bool lineMetricsAttempted = false;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (len == 0)
                continue;

            int runStart = cumulative;
            int runEnd = cumulative + len;
            cumulative = runEnd;

            if (runEnd <= selectedStart || runStart >= selectedEnd)
                continue;
            if (run is InlineEmbedRun or InlineImageRun or InlineVectorSceneRun or InlineSpacerRun)
                continue;
            if (run is LinkRun { IsSuperscript: true })
                continue;

            var rs = box.GetRunStyle(run);
            if ((rs.Underline || rs.Strikethrough) && !lineMetricsAttempted)
            {
                lineMetrics = box.TryGetLineMetrics();
                lineMetricsAttempted = true;
            }

            if (rs.Underline || rs.Strikethrough)
            {
                int segmentStart = Math.Max(runStart, selectedStart);
                int segmentLength = Math.Min(runEnd, selectedEnd) - segmentStart;
                var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, segmentStart, segmentLength);
                if (regions is not null)
                {
                    foreach (var r in regions)
                        DrawRunDecorations(ds, baseX, baseY, lineMetrics, r, run, rs, color);
                }
            }

            if (run is LinkRun { InlineStyleMap: not null } styledLink)
            {
                if (!lineMetricsAttempted)
                {
                    lineMetrics = box.TryGetLineMetrics();
                    lineMetricsAttempted = true;
                }
                DrawLinkInlineSpanDecorations(
                    box,
                    ds,
                    baseX,
                    baseY,
                    styledLink,
                    runStart,
                    rs,
                    color,
                    selectedStart,
                    Math.Min(runEnd, selectedEnd),
                    viewport: null,
                    lineMetrics: lineMetrics);
            }
        }
    }

    internal static void DrawRunDecorations(
        CanvasDrawingSession ds,
        float baseX,
        float baseY,
        CanvasLineMetrics[]? lineMetrics,
        CanvasTextLayoutRegion region,
        InlineRun run,
        ElementStyle style,
        Color color)
        => DrawRunDecorations(
            ds,
            baseX,
            baseY,
            lineMetrics,
            region,
            run,
            style,
            color,
            style.Underline,
            style.Strikethrough);

    private static void DrawRunDecorations(
        CanvasDrawingSession ds,
        float baseX,
        float baseY,
        CanvasLineMetrics[]? lineMetrics,
        CanvasTextLayoutRegion region,
        InlineRun run,
        ElementStyle style,
        Color color,
        bool underline,
        bool strikethrough)
    {
        if (!underline && !strikethrough)
            return;
        if (run is LinkRun { IsSuperscript: true })
            return;

        var lb = region.LayoutBounds;
        var lm = InlineContainerBox.FindLineMetrics(lineMetrics, region);
        float baselineFromTop = lm.Baseline > 0 ? lm.Baseline : (float)lb.Height * 0.80f;
        float xHeight = baselineFromTop * 0.50f;
        float strikeY = (float)(baseY + lb.Y + (baselineFromTop - xHeight * 0.5f));
        float underlineY = (float)(baseY + lb.Y + baselineFromTop + 1.5f);

        if (underline)
        {
            if (run is AbbreviationRun)
                DrawDottedUnderline(ds, (float)(baseX + lb.X), (float)(baseX + lb.X + lb.Width), underlineY, style.AccentBar ?? color);
            else
                ds.DrawLine((float)(baseX + lb.X), underlineY, (float)(baseX + lb.X + lb.Width),
                    underlineY, color, 1.0f);
        }

        if (strikethrough)
        {
            ds.DrawLine((float)(baseX + lb.X), strikeY, (float)(baseX + lb.X + lb.Width),
                strikeY, color, 1.0f);
        }
    }

    private static void DrawDottedUnderline(CanvasDrawingSession ds, float startX, float endX, float y, Color color)
    {
        const float dot = 1.25f;
        const float gap = 2.25f;
        for (float x = startX; x < endX; x += dot + gap)
        {
            float x2 = MathF.Min(x + dot, endX);
            ds.DrawLine(x, y, x2, y, color, 1.0f);
        }
    }

    internal static void DrawRunBackgrounds(InlineContainerBox box, CanvasDrawingSession ds, float baseX, float baseY)
    {
        if (box.TextLayout is null) return;

        int cumulative = 0;
        foreach (var run in box.RunList)
        {
            int len = run.TextLength;
            if (len == 0) { continue; }
            if (run is InlineEmbedRun or InlineImageRun or InlineVectorSceneRun or InlineSpacerRun) { cumulative += len; continue; }

            var rs = box.GetRunStyle(run);
            bool hasRunSpecificStyle = box.HasRunSpecificStyle(run);
            bool drawRunBg = hasRunSpecificStyle && rs.Background is { };
            if (drawRunBg)
            {
                DrawInlineStyleBackground(
                    box,
                    ds,
                    baseX,
                    baseY,
                    cumulative,
                    len,
                    rs,
                    hasInlineCodePadding: run is CodeInlineRun);
            }

            if (run is LinkRun { InlineStyleMap: not null } styledLink)
            {
                LinkInlineStyleMap map = styledLink.InlineStyleMap!;
                ElementStyle[] spanStyles = box.GetLinkInlineSpanStyles(styledLink);
                for (int index = 0; index < map.Count; index++)
                {
                    ElementStyle spanStyle = spanStyles[index];
                    if (spanStyle.Background is not { } background || background == rs.Background)
                        continue;
                    LinkInlineStyleSpan span = map.GetSpan(index);
                    DrawInlineStyleBackground(
                        box,
                        ds,
                        baseX,
                        baseY,
                        cumulative + span.Start,
                        span.Length,
                        spanStyle,
                        hasInlineCodePadding: InlineCodePaddingLayout.HasInlineCodeStylePath(span.Path));
                }
            }

            cumulative += len;
        }
    }

    private static void DrawInlineStyleBackground(
        InlineContainerBox box,
        CanvasDrawingSession ds,
        float baseX,
        float baseY,
        int start,
        int length,
        ElementStyle style,
        bool hasInlineCodePadding = false)
    {
        if (box.TextLayout is null || style.Background is not { } background)
            return;

        var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, start, length);
        if (regions is null)
            return;

        foreach (var region in regions)
        {
            var bounds = region.LayoutBounds;
            double backgroundTop;
            double backgroundHeight;
            if (hasInlineCodePadding)
            {
                // SetCharacterSpacing already reserves the two physical
                // horizontal padding advances in these character regions.
                // Vertical inline padding is paint-only: it expands the chip
                // without changing DirectWrite's line height or text offsets.
                double topPadding = InlineCodePaddingLayout.ToInlineCodeAdvance(style.Padding.Top);
                double bottomPadding = InlineCodePaddingLayout.ToInlineCodeAdvance(style.Padding.Bottom);
                backgroundTop = bounds.Y + bounds.Height * 0.05 - topPadding;
                backgroundHeight = bounds.Height * 0.90 + topPadding + bottomPadding;
            }
            else
            {
                // Preserve the existing non-code inline background geometry.
                backgroundTop = bounds.Y + bounds.Height * 0.05;
                backgroundHeight = bounds.Height * 0.90;
            }
            ds.FillRoundedRectangle(
                new Rect(
                    baseX + bounds.X - (hasInlineCodePadding ? 0 : 2),
                    baseY + backgroundTop,
                    bounds.Width + (hasInlineCodePadding ? 0 : 4),
                    backgroundHeight),
                style.CornerRadius,
                style.CornerRadius,
                background);
        }
    }

    internal static void DrawLinkInlineSpanDecorations(
        InlineContainerBox box,
        CanvasDrawingSession ds,
        float baseX,
        float baseY,
        LinkRun link,
        int runStart,
        ElementStyle linkStyle,
        Color color,
        int? selectedStart,
        int? selectedEnd,
        Rect? viewport,
        CanvasLineMetrics[]? lineMetrics = null)
    {
        if (box.TextLayout is null || link.InlineStyleMap is not { } map || link.IsSuperscript)
            return;

        ElementStyle[] spanStyles = box.GetLinkInlineSpanStyles(link);
        for (int index = 0; index < map.Count; index++)
        {
            LinkInlineStyleSpan span = map.GetSpan(index);
            int spanStart = runStart + span.Start;
            int spanLength = span.Length;
            bool underline = spanStyles[index].Underline && !linkStyle.Underline;
            bool strikethrough = spanStyles[index].Strikethrough && !linkStyle.Strikethrough;
            if ((!underline && !strikethrough) || spanLength <= 0)
                continue;

            if (selectedStart is int selectionStart && selectedEnd is int selectionEnd)
            {
                int clippedStart = Math.Max(spanStart, selectionStart);
                int clippedEnd = Math.Min(spanStart + spanLength, selectionEnd);
                spanStart = clippedStart;
                spanLength = clippedEnd - clippedStart;
                if (spanLength <= 0)
                    continue;
            }

            var regions = InlineRangeGeometry.GetAdjustedCharacterRegions(box, box.TextLayout, spanStart, spanLength);
            if (regions is null)
                continue;
            lineMetrics ??= box.TryGetLineMetrics();
            foreach (var region in regions)
            {
                var bounds = region.LayoutBounds;
                if (viewport is { } clip)
                {
                    Rect placed = new(baseX + bounds.X, baseY + bounds.Y, bounds.Width, bounds.Height);
                    if (!Intersects(placed, clip))
                        continue;
                }

                DrawRunDecorations(
                    ds,
                    baseX,
                    baseY,
                    lineMetrics,
                    region,
                    link,
                    spanStyles[index],
                    color,
                    underline,
                    strikethrough);
            }
        }
    }
}
