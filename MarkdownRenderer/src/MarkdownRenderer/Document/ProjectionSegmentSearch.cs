using System;

namespace MarkdownRenderer.Document;

/// <summary>
/// Locates and maps rendered boundaries within an exact segment projection.
/// </summary>
internal static class ProjectionSegmentSearch
{
    internal static int FindSegmentForStart(MarkdownSourceMap.ProjectionSegment[] segments, int renderedOffset)
    {
        int low = 0;
        int high = segments.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (segments[middle].RenderedStart <= renderedOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low - 1;
    }

    internal static int FindSegmentForEnd(MarkdownSourceMap.ProjectionSegment[] segments, int renderedOffset)
    {
        int low = 0;
        int high = segments.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (segments[middle].RenderedStart < renderedOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return Math.Max(0, low - 1);
    }

    internal static int MapSegmentStartBoundary(MarkdownSourceMap.ProjectionSegment segment, int renderedDelta)
    {
        if (segment.IsAtomic)
            return segment.SourceStart;

        if (segment.SourceLength == segment.RenderedLength)
            return segment.SourceStart + renderedDelta;
        return segment.SourceStart;
    }

    internal static int MapSegmentEndBoundary(MarkdownSourceMap.ProjectionSegment segment, int renderedDelta)
    {
        if (segment.IsAtomic)
            return GetSegmentSourceEnd(segment);

        if (segment.SourceLength == segment.RenderedLength)
            return segment.SourceStart + renderedDelta;
        return GetSegmentSourceEnd(segment);
    }

    internal static int GetSegmentSourceEnd(MarkdownSourceMap.ProjectionSegment segment)
        => segment.SourceStart + segment.SourceLength;
}
