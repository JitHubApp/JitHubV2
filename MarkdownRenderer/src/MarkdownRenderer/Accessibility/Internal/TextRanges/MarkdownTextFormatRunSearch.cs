using System;

namespace MarkdownRenderer.Accessibility.Internal.TextRanges;

/// <summary>Binary searches over sorted format runs and run boundaries.</summary>
internal static class MarkdownTextFormatRunSearch
{
    internal static int FindFormatRunIndex(
        MarkdownTextFormatCache cache,
        int offset,
        int documentLength)
    {
        int low = 0;
        int high = cache.RunCount - 1;
        while (low <= high)
        {
            int i = low + ((high - low) / 2);
            MarkdownTextStyleRun run = cache.GetRun(i);
            if (ContainsHalfOpenOffset(
                    offset,
                    run.Start,
                    run.End,
                    i == cache.RunCount - 1,
                    documentLength))
                return i;
            if (offset < run.Start)
                high = i - 1;
            else
                low = i + 1;
        }

        return Math.Clamp(low, 0, cache.RunCount - 1);
    }

    internal static bool ContainsHalfOpenOffset(
        int offset,
        int start,
        int end,
        bool isFinalRun,
        int documentLength) =>
        offset >= start &&
        (offset < end ||
         (isFinalRun && offset == documentLength && offset == end));

    internal static int MoveAcrossSortedBoundaries(
        int[] boundaries,
        int current,
        int count,
        out int moved)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        moved = 0;
        if (boundaries.Length == 0 || count == 0)
            return current;

        int found = Array.BinarySearch(boundaries, current);
        int baseIndex;
        if (count > 0)
            baseIndex = found >= 0 ? found : Math.Max(-1, ~found - 1);
        else
            baseIndex = found >= 0 ? found : Math.Min(boundaries.Length, ~found);

        int targetIndex = (int)Math.Clamp(
            (long)baseIndex + count,
            0L,
            boundaries.Length - 1L);
        moved = targetIndex - baseIndex;
        return boundaries[targetIndex];
    }

    internal static bool SpanIntersects(int spanStart, int spanEnd, int rangeStart, int rangeEnd, bool collapsed)
    {
        if (collapsed)
            return rangeStart >= spanStart && rangeStart <= spanEnd;

        return spanEnd > rangeStart && spanStart < rangeEnd;
    }
}
