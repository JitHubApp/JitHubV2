using System;
using System.Collections.Generic;
using MarkdownRenderer.Document;
using static MarkdownRenderer.Accessibility.MarkdownTextSpanProjection;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Maps between rendered document positions and offsets in a semantic
/// document's accessible text, through its ordered text spans and their
/// (block, inline) position index.
/// </summary>
internal sealed class MarkdownSemanticPositionMap
{
    private readonly int _textLength;
    private readonly IReadOnlyList<MarkdownTextSpan> _spans;
    private readonly IReadOnlyDictionary<(int BlockIndex, int InlineIndex), int> _spanByPosition;

    internal MarkdownSemanticPositionMap(int textLength, IReadOnlyList<MarkdownTextSpan> spans)
    {
        _textLength = textLength;
        _spans = spans;
        int mappableSpanCount = 0;
        bool hasPreviousMappedKey = false;
        (int BlockIndex, int InlineIndex) previousMappedKey = default;
        for (int i = 0; i < spans.Count; i++)
        {
            MarkdownTextSpan span = spans[i];
            (int BlockIndex, int InlineIndex) key;
            if (span.InlineBox is { } inline && span.InlineRun is { } run)
            {
                key = (inline.BlockIndex, run.InlineIndex);
            }
            else if (span.VectorSceneBox is { } vector)
            {
                key = (vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex));
            }
            else
            {
                hasPreviousMappedKey = false;
                continue;
            }

            if (!hasPreviousMappedKey || key != previousMappedKey)
            {
                mappableSpanCount++;
            }
            previousMappedKey = key;
            hasPreviousMappedKey = true;
        }

        // Standalone images and other atomic-only documents can have many
        // semantic spans but no position-index entries. Size from consecutive
        // distinct mapped keys rather than the full span count; the ordered
        // fill/TryAdd loop below still preserves first-wins behavior for every
        // duplicate, including nonconsecutive duplicates.
        var spanByPosition = new Dictionary<(int BlockIndex, int InlineIndex), int>(mappableSpanCount);
        for (int i = 0; i < spans.Count; i++)
        {
            MarkdownTextSpan span = spans[i];
            if (span.InlineBox is { } inline && span.InlineRun is { } run)
                spanByPosition.TryAdd((inline.BlockIndex, run.InlineIndex), i);
            else if (span.VectorSceneBox is { } vector)
                spanByPosition.TryAdd((vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex)), i);
        }
        _spanByPosition = spanByPosition;
    }

    /// <summary>
    /// The first text span index for each mapped (block, inline) document
    /// position; sized from the distinct consecutive mapped keys.
    /// </summary>
    internal IReadOnlyDictionary<(int BlockIndex, int InlineIndex), int> SpanIndexByPosition => _spanByPosition;

    public int TextOffsetFromDocumentPosition(
        DocumentPosition position,
        IReadOnlyDictionary<int, MarkdownSemanticNode> nodeByInlineBlockIndex)
    {
        if (_spanByPosition.TryGetValue((position.BlockIndex, position.InlineIndex), out int indexedSpanIndex))
        {
            MarkdownTextSpan indexedSpan = _spans[indexedSpanIndex];
            if (indexedSpan.InlineRun is { } indexedRun)
            {
                return Math.Clamp(
                    indexedSpan.TextStart + ProjectRenderedOffsetToText(
                        indexedRun,
                        position.CharacterOffset,
                        indexedSpan.TextEnd - indexedSpan.TextStart),
                    indexedSpan.TextStart,
                    indexedSpan.TextEnd);
            }

            return position.CharacterOffset <= 0 ? indexedSpan.TextStart : indexedSpan.TextEnd;
        }

        // Non-run positions (images/embeds) and deliberately unbounded
        // selection sentinels are uncommon. Preserve the compatibility
        // fallback without putting normal copy/UIA lookups on a linear path.
        int trailingInlineTextEnd = -1;
        foreach (var span in _spans)
        {
            if (span.InlineBox is { } icb && icb.BlockIndex == position.BlockIndex)
            {
                if (span.InlineRun is { } run)
                {
                    if (position.InlineIndex < run.InlineIndex)
                        return span.TextStart;
                    if (position.InlineIndex > run.InlineIndex)
                    {
                        trailingInlineTextEnd = span.TextEnd;
                        continue;
                    }

                    return Math.Clamp(
                        span.TextStart + ProjectRenderedOffsetToText(run, position.CharacterOffset, span.TextEnd - span.TextStart),
                        span.TextStart,
                        span.TextEnd);
                }

                return Math.Clamp(span.TextStart + icb.GetBufferCharOffset(position), span.TextStart, span.TextEnd);
            }
        }

        // A decorative image/spacer has no semantic text span of its own. If
        // it follows the final semantic run in this box, map it to the end of
        // that preceding text rather than falling through to the first span.
        if (trailingInlineTextEnd >= 0)
            return trailingInlineTextEnd;

        foreach (var span in _spans)
        {
            bool matchesBlockImage = span.ImageBox?.BlockIndex == position.BlockIndex;
            bool matchesBlockEmbed = span.EmbedBox?.BlockIndex == position.BlockIndex;
            bool matchesHostedElement = span.HostedElementBox?.BlockIndex == position.BlockIndex;
            if (matchesBlockImage || matchesBlockEmbed || matchesHostedElement)
            {
                // These block objects publish explicit before/after endpoints
                // through PositionFromSpanEnd, but do not use the normal
                // position index. Keep the compatibility scan endpoint-aware
                // so a complete atomic selection includes its semantic text.
                return position.CharacterOffset <= 0 ? span.TextStart : span.TextEnd;
            }

            if (span.InlineBox?.BlockIndex == position.BlockIndex ||
                (span.VectorSceneBox?.BlockIndex == position.BlockIndex &&
                 GetVectorDocumentInlineIndex(span.VectorSemanticIndex) == position.InlineIndex))
            {
                return span.TextStart;
            }
        }

        // A box containing only omitted decorative objects still has its
        // zero-length semantic block node. Use that boundary so a spacer-only
        // paragraph between other paragraphs does not map to document end.
        if (nodeByInlineBlockIndex.TryGetValue(position.BlockIndex, out MarkdownSemanticNode? inlineNode))
            return inlineNode.TextStart;

        return position.BlockIndex <= 0 ? 0 : _textLength;
    }

    public bool TryGetDocumentRange(int textStart, int textEnd, out DocumentRange range)
    {
        var start = PositionFromTextOffset(textStart);
        var end = PositionFromTextOffset(textEnd);
        if (start is { } s && end is { } e)
        {
            range = new DocumentRange(s, e);
            return true;
        }

        range = DocumentRange.Empty;
        return false;
    }

    public DocumentPosition? PositionFromTextOffset(int textOffset)
    {
        textOffset = Math.Clamp(textOffset, 0, _textLength);
        if (_spans.Count == 0)
            return DocumentPosition.Zero;

        int spanIndex = GetTextSpanStartIndex(textOffset);
        if (spanIndex >= _spans.Count)
            return PositionFromSpanEnd(_spans[^1]);

        MarkdownTextSpan span = _spans[spanIndex];
        if (textOffset < span.TextStart)
        {
            return spanIndex > 0
                ? PositionFromSpanEnd(_spans[spanIndex - 1])
                : PositionFromSpanEnd(span) with { CharacterOffset = 0 };
        }

        if (span.InlineBox is { } icb)
        {
            if (span.InlineRun is { } run)
            {
                int accessibleOffset = Math.Clamp(textOffset - span.TextStart, 0, Math.Max(0, span.TextEnd - span.TextStart));
                int renderedOffset = ProjectTextOffsetToRendered(run, accessibleOffset, span.TextEnd - span.TextStart);
                return new DocumentPosition(icb.BlockIndex, run.InlineIndex, renderedOffset);
            }

            int bufferOffset = Math.Clamp(textOffset - span.TextStart, 0, Math.Max(0, span.TextEnd - span.TextStart));
            return icb.GetPositionFromBufferOffset(bufferOffset);
        }

        if (span.ImageBox is { } image)
            return new DocumentPosition(image.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.EmbedBox is { } embed)
            return new DocumentPosition(embed.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.HostedElementBox is { } hosted)
            return new DocumentPosition(hosted.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.VectorSceneBox is { } vector)
            return new DocumentPosition(
                vector.BlockIndex,
                GetVectorDocumentInlineIndex(span.VectorSemanticIndex),
                textOffset <= span.TextStart ? 0 : 1);

        return DocumentPosition.Zero;
    }

    /// <summary>Finds the first text span whose end is after the requested offset.</summary>
    internal int GetTextSpanStartIndex(int textOffset)
    {
        int low = 0;
        int high = _spans.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_spans[middle].TextEnd <= textOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
