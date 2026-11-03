using System;

namespace MarkdownRenderer.Document;

/// <summary>
/// Projects rendered-text offsets within one source-map entry onto offsets
/// within that entry's source span.
/// </summary>
internal static class SourceMapEntryOffsets
{
    /// <summary>
    /// Maps a rendered-text offset within an entry to a source offset within the
    /// entry's source span. Exact when render-length matches span-length;
    /// otherwise proportional.
    /// </summary>
    internal static int ProjectStartOffset(MarkdownSourceMap.Entry entry, int renderedOffset)
    {
        if (entry.SourcePrefixLength == 0 && entry.SourceSuffixLength == 0)
            return entry.Projection?.ProjectStart(renderedOffset)
                ?? ProjectOffset(entry, renderedOffset);

        if (renderedOffset <= 0)
            return 0;

        return entry.SourcePrefixLength + ProjectContentStartOffset(entry, renderedOffset);
    }

    internal static int ProjectEndOffset(MarkdownSourceMap.Entry entry, int renderedOffset)
    {
        if (entry.SourcePrefixLength == 0 && entry.SourceSuffixLength == 0)
            return entry.Projection?.ProjectEnd(renderedOffset)
                ?? ProjectOffset(entry, renderedOffset);

        if (renderedOffset >= entry.RenderedLength)
            return entry.Span.Length;

        return entry.SourcePrefixLength + ProjectContentEndOffset(entry, renderedOffset);
    }

    private static int ProjectContentStartOffset(MarkdownSourceMap.Entry entry, int renderedOffset)
        => entry.Projection?.ProjectStart(renderedOffset)
            ?? ProjectAffixedContentOffset(entry, renderedOffset);

    private static int ProjectContentEndOffset(MarkdownSourceMap.Entry entry, int renderedOffset)
        => entry.Projection?.ProjectEnd(renderedOffset)
            ?? ProjectAffixedContentOffset(entry, renderedOffset);

    private static int ProjectAffixedContentOffset(MarkdownSourceMap.Entry entry, int renderedOffset)
    {
        int contentLength = GetContentSourceLength(entry);
        if (entry.RenderedLength <= 0)
            return 0;

        int contentOffset = entry.RenderedLength == contentLength
            ? renderedOffset
            : (int)Math.Round(renderedOffset * (double)contentLength / Math.Max(1, entry.RenderedLength));
        return Math.Clamp(contentOffset, 0, contentLength);
    }

    private static int GetContentSourceLength(MarkdownSourceMap.Entry entry)
    {
        int prefixLength = Math.Clamp(entry.SourcePrefixLength, 0, entry.Span.Length);
        int suffixLength = Math.Clamp(entry.SourceSuffixLength, 0, entry.Span.Length - prefixLength);
        return Math.Max(0, entry.Span.Length - prefixLength - suffixLength);
    }

    private static int ProjectOffset(MarkdownSourceMap.Entry entry, int renderedOffset)
    {
        if (entry.RenderedLength <= 0)
            return 0;
        if (entry.RenderedLength == entry.Span.Length)
            return renderedOffset;

        double scale = (double)entry.Span.Length / Math.Max(1, entry.RenderedLength);
        return (int)Math.Round(renderedOffset * scale);
    }
}
