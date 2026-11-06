using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Places an inline container's atomic objects in document coordinates:
/// hosted-embed rectangles and inline image rectangles, including the CSS
/// middle alignment that uses the captured line baselines and x-heights
/// instead of re-reading line metrics on the paint path.
/// </summary>
internal static class InlineObjectGeometry
{
    internal static IEnumerable<(InlineEmbedRun Run, Rect Rect)> EnumerateEmbedRects(InlineContainerBox box)
    {
        if (box.TextLayout is null || box.InlineEmbedEntries is null) yield break;
        var style = box.GetContainerStyle();
        var (baseX, baseY) = box.GetSnappedOrigin(style);

        foreach (var (emb, offset) in box.InlineEmbedEntries)
        {
            int len = emb.TextLength;
            if (len > 0)
            {
                var regions = box.TextLayout.GetCharacterRegions(offset, len);
                if (regions is null) continue;
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    double cellY = lb.Y + (lb.Height - emb.DesiredHeight) / 2.0;
                    if (cellY < lb.Y) cellY = lb.Y;
                    var rect = new Rect(baseX + lb.X, baseY + cellY,
                        Math.Min(emb.DesiredWidth, lb.Width), emb.DesiredHeight);
                    yield return (emb, rect);
                    break;
                }
            }
        }
    }

    internal static Rect GetInlineImageDocumentRect(
        InlineContainerBox box,
        CanvasTextLayoutRegion region,
        InlineImageRun image,
        float baseX,
        float baseY)
    {
        Rect lineBounds = region.LayoutBounds;
        double top;
        if (box.Context.ThemeSnapshot.MiddleAlignInlineImages &&
            !image.Image.IsFailedDecorativeImageCollapsed &&
            TryGetCachedInlineImageXHeightDip(box, image, out float xHeightDip) &&
            TryGetCachedInlineImageLineBaseline(box, image, out float lineBaseline))
        {
            // CSS vertical-align: middle aligns the object's center with
            // x-middle: halfway from the alphabetic baseline to the top of
            // lowercase x. DirectWrite line metrics are relative to layout top.
            top = baseY + lineBounds.Y + lineBaseline -
                (image.DesiredHeight + xHeightDip) / 2d;
        }
        else
        {
            // Preserve the legacy baseline/centering behavior for default
            // Markdown, missing font metrics, and zero-size failed decoration.
            double cellY = lineBounds.Y + (lineBounds.Height - image.DesiredHeight) / 2d;
            if (cellY < lineBounds.Y)
                cellY = lineBounds.Y;
            top = baseY + cellY;
        }

        return new Rect(
            baseX + lineBounds.X,
            top,
            Math.Min(image.DesiredWidth, lineBounds.Width),
            image.DesiredHeight);
    }

    private static bool TryGetCachedInlineImageLineBaseline(InlineContainerBox box, InlineImageRun image, out float baseline)
    {
        baseline = 0f;
        float[]? baselines = box.InlineImageLineBaselineByRunIndex;
        if (baselines is null)
            return false;

        int runIndex = image.InlineIndex;
        if ((uint)runIndex >= (uint)baselines.Length)
            return false;

        float cached = baselines[runIndex];
        if (!float.IsFinite(cached) || cached < 0f)
            return false;

        baseline = cached;
        return true;
    }

    internal static float[] BuildInlineImageLineBaselines(InlineContainerBox box, CanvasLineMetrics[]? lineMetrics)
    {
        float[] baselinesByRun = new float[box.RunList.Count];
        Array.Fill(baselinesByRun, -1f);
        if (lineMetrics is not { Length: > 0 })
            return baselinesByRun;

        int lineIndex = 0;
        int lineStart = 0;
        int characterOffset = 0;
        int runIndex = 0;
        foreach (InlineRun run in box.RunList)
        {
            if ((runIndex & 63) == 0)
                box.Context.CancellationToken.ThrowIfCancellationRequested();

            if (run is InlineImageRun && run.TextLength > 0)
            {
                while (lineIndex + 1 < lineMetrics.Length)
                {
                    int lineEnd = lineStart + lineMetrics[lineIndex].CharacterCount;
                    if (characterOffset < lineEnd)
                        break;

                    lineStart = lineEnd;
                    lineIndex++;
                }

                CanvasLineMetrics line = lineMetrics[lineIndex];
                if (line.CharacterCount > 0 &&
                    float.IsFinite(line.Baseline) && line.Baseline >= 0f)
                {
                    baselinesByRun[runIndex] = line.Baseline;
                }
            }

            characterOffset += run.TextLength;
            runIndex++;
        }

        return baselinesByRun;
    }

    internal static bool TryGetCachedInlineImageXHeightDip(InlineContainerBox box, InlineImageRun image, out float xHeightDip)
    {
        xHeightDip = 0f;
        float[]? metrics = box.InlineImageXHeightByRunIndex;
        if (!box.Context.ThemeSnapshot.MiddleAlignInlineImages ||
            image.Image.IsFailedDecorativeImageCollapsed ||
            metrics is null)
        {
            return false;
        }

        int runIndex = image.InlineIndex;
        if ((uint)runIndex >= (uint)metrics.Length)
            return false;

        float cached = metrics[runIndex];
        if (!float.IsFinite(cached) || cached <= 0f)
            return false;

        xHeightDip = cached;
        return true;
    }
    internal static IEnumerable<(InlineImageRun Run, Rect Rect)> EnumerateInlineImageRects(InlineContainerBox box)
    {
        if (box.TextLayout is null || box.InlineImageEntries is null) yield break;
        var style = box.GetContainerStyle();
        var (baseX, baseY) = box.GetSnappedOrigin(style);

        foreach (var (image, offset) in box.InlineImageEntries)
        {
            int len = image.TextLength;
            if (len > 0)
            {
                var regions = box.TextLayout.GetCharacterRegions(offset, len);
                if (regions is null) continue;
                foreach (var r in regions)
                {
                    Rect rect = GetInlineImageDocumentRect(box, r, image, baseX, baseY);
                    image.Image.SetInlineBounds(rect);
                    yield return (image, rect);
                    break;
                }
            }
        }
    }
}
