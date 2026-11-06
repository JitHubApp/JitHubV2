using System;
using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Projects one semantic text span between accessible text offsets, rendered
/// document positions and layout rectangles.
/// </summary>
internal static class MarkdownTextSpanProjection
{
    internal static IEnumerable<Rect> GetDocumentRectsForSpan(
        MarkdownTextSpan span,
        int start,
        int end,
        bool logicalInlineCoordinates = false)
    {
        start = Math.Clamp(start, span.TextStart, span.TextEnd);
        end = Math.Clamp(end, start, span.TextEnd);

        if (span.InlineBox is { } icb)
        {
            DocumentPosition startPos;
            DocumentPosition endPos;
            if (span.InlineRun is { } run)
            {
                int textLength = span.TextEnd - span.TextStart;
                startPos = new DocumentPosition(
                    icb.BlockIndex,
                    run.InlineIndex,
                    ProjectTextOffsetToRendered(run, start - span.TextStart, textLength));
                endPos = new DocumentPosition(
                    icb.BlockIndex,
                    run.InlineIndex,
                    ProjectTextOffsetToRendered(run, end - span.TextStart, textLength));
                if (end > start && endPos.CharacterOffset == startPos.CharacterOffset && run.RenderedLength > 0)
                    endPos = endPos with { CharacterOffset = run.RenderedLength };
            }
            else
            {
                startPos = icb.GetPositionFromBufferOffset(start - span.TextStart);
                endPos = icb.GetPositionFromBufferOffset(end - span.TextStart);
            }

            IEnumerable<Rect> inlineRects = logicalInlineCoordinates
                ? icb.GetLogicalRangeRects(new DocumentRange(startPos, endPos))
                : icb.GetRangeRects(new DocumentRange(startPos, endPos));
            foreach (var rect in inlineRects)
                yield return rect;
        }
        else if (span.ImageBox is { } image)
        {
            yield return image.Bounds;
        }
        else if (span.EmbedBox is { } embed)
        {
            yield return embed.Bounds;
        }
        else if (span.HostedElementBox is { } hosted)
        {
            yield return hosted.Bounds;
        }
        else if (span.VectorSceneBox is { } vector)
        {
            yield return span.VectorSemanticIndex >= 0
                ? vector.GetVisibleSemanticBounds(span.VectorSemanticIndex)
                : vector.VisibleContentBounds;
        }
    }

    internal static DocumentPosition PositionFromSpanEnd(MarkdownTextSpan span)
    {
        if (span.InlineBox is { } icb)
        {
            if (span.InlineRun is { } run)
                return new DocumentPosition(icb.BlockIndex, run.InlineIndex, run.RenderedLength);
            return icb.GetPositionFromBufferOffset(span.TextEnd - span.TextStart);
        }
        if (span.ImageBox is { } image)
            return new DocumentPosition(image.BlockIndex, 0, 1);
        if (span.EmbedBox is { } embed)
            return new DocumentPosition(embed.BlockIndex, 0, 1);
        if (span.HostedElementBox is { } hosted)
            return new DocumentPosition(hosted.BlockIndex, 0, 1);
        if (span.VectorSceneBox is { } vector)
            return new DocumentPosition(vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex), 1);
        return DocumentPosition.Zero;
    }

    // The negative vector-semantic index is a metadata sentinel for a whole,
    // unstructured scene, not a public document coordinate. Atomic source-map
    // and selection positions for that scene use the zero-based document slot.
    internal static int GetVectorDocumentInlineIndex(int vectorSemanticIndex) =>
        vectorSemanticIndex < 0 ? 0 : vectorSemanticIndex;

    internal static int ProjectRenderedOffsetToText(InlineRun run, int renderedOffset, int textLength)
    {
        if (textLength <= 0 || run.RenderedLength <= 0)
            return 0;

        renderedOffset = Math.Clamp(renderedOffset, 0, run.RenderedLength);
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun)
            return renderedOffset <= 0 ? 0 : textLength;

        if (run.RenderedLength == textLength)
            return renderedOffset;

        return Math.Clamp((int)Math.Round(renderedOffset * (double)textLength / run.RenderedLength), 0, textLength);
    }

    internal static int ProjectTextOffsetToRendered(InlineRun run, int textOffset, int textLength)
    {
        if (run.RenderedLength <= 0)
            return 0;

        textOffset = Math.Clamp(textOffset, 0, Math.Max(0, textLength));
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun)
            return textOffset <= 0 ? 0 : run.RenderedLength;

        if (run.RenderedLength == textLength)
            return textOffset;

        if (textLength <= 0)
            return 0;

        return Math.Clamp((int)Math.Round(textOffset * (double)run.RenderedLength / textLength), 0, run.RenderedLength);
    }
}
