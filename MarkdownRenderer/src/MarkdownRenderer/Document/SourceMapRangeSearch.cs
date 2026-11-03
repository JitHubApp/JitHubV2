using System;

namespace MarkdownRenderer.Document;

/// <summary>
/// Searches a document-ordered source-map entry array for the entries a
/// rendered range selects.
/// </summary>
internal static class SourceMapRangeSearch
{
    internal static bool TryFindFirstHit(
        MarkdownSourceMap.Entry[] entries,
        int searchStart,
        int searchEnd,
        DocumentRange range,
        out int entryIndex,
        out int projectedOffset)
    {
        for (int i = searchStart; i < searchEnd; i++)
        {
            if (!TryGetSelectedOffsets(entries[i], range, out int from, out _))
                continue;

            entryIndex = i;
            projectedOffset = SourceMapEntryOffsets.ProjectStartOffset(entries[i], from);
            return true;
        }

        entryIndex = -1;
        projectedOffset = 0;
        return false;
    }

    internal static bool TryFindLastHit(
        MarkdownSourceMap.Entry[] entries,
        int searchStart,
        int searchEnd,
        DocumentRange range,
        out int entryIndex,
        out int projectedOffset)
    {
        for (int i = searchEnd - 1; i >= searchStart; i--)
        {
            if (!TryGetSelectedOffsets(entries[i], range, out _, out int to))
                continue;

            entryIndex = i;
            projectedOffset = SourceMapEntryOffsets.ProjectEndOffset(entries[i], to);
            return true;
        }

        entryIndex = -1;
        projectedOffset = 0;
        return false;
    }

    private static bool TryGetSelectedOffsets(MarkdownSourceMap.Entry entry, DocumentRange range, out int from, out int to)
    {
        var startPosition = new DocumentPosition(entry.BlockIndex, entry.InlineIndex, 0);
        var endPosition = new DocumentPosition(entry.BlockIndex, entry.InlineIndex, entry.RenderedLength);
        if (endPosition <= range.Start || startPosition >= range.End)
        {
            from = 0;
            to = 0;
            return false;
        }

        from = 0;
        to = entry.RenderedLength;
        if (range.Start.BlockIndex == entry.BlockIndex && range.Start.InlineIndex == entry.InlineIndex)
            from = Math.Max(0, range.Start.CharacterOffset);
        if (range.End.BlockIndex == entry.BlockIndex && range.End.InlineIndex == entry.InlineIndex)
            to = Math.Min(entry.RenderedLength, range.End.CharacterOffset);

        return to > from;
    }

    internal static int LowerBound(MarkdownSourceMap.Entry[] entries, int blockIndex, int inlineIndex)
    {
        int low = 0;
        int high = entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (CompareDocumentKey(entries[middle], blockIndex, inlineIndex) < 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    internal static int UpperBound(MarkdownSourceMap.Entry[] entries, int blockIndex, int inlineIndex)
    {
        int low = 0;
        int high = entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (CompareDocumentKey(entries[middle], blockIndex, inlineIndex) <= 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static int CompareDocumentKey(MarkdownSourceMap.Entry entry, int blockIndex, int inlineIndex)
    {
        int comparison = entry.BlockIndex.CompareTo(blockIndex);
        return comparison != 0 ? comparison : entry.InlineIndex.CompareTo(inlineIndex);
    }
}
