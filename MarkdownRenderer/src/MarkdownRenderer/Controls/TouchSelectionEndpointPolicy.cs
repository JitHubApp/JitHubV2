using System;
using MarkdownRenderer.Layout.Internal.Text;

namespace MarkdownRenderer.Controls;

/// <summary>Logical endpoint adjustment shared by touch and keyboard handles.</summary>
internal static class TouchSelectionEndpointPolicy
{
    public static TouchSelectionEndpointAdjustment Resolve(
        TextElementBoundaryIndex boundaries,
        int fixedOffset,
        int rawCandidateOffset,
        bool startEndpoint,
        bool allowCrossing,
        bool candidateIsBoundary = false)
    {
        (int clusterStart, int clusterEnd) = boundaries.FindBoundaries(rawCandidateOffset);
        // A hit-tested candidate is a caret position. On a grapheme
        // boundary it is the endpoint for either role; FindBoundaries returns
        // the following grapheme there, so taking its end would overshoot by
        // one grapheme. Only a mid-grapheme caret snaps to the side its role owns.
        bool onGraphemeBoundary = rawCandidateOffset == clusterStart;
        int offset = candidateIsBoundary
            ? Math.Clamp(rawCandidateOffset, 0, boundaries.TextLength)
            : onGraphemeBoundary ? clusterStart
            : startEndpoint ? clusterStart : clusterEnd;
        bool effectiveStart = startEndpoint;

        if (allowCrossing && startEndpoint && offset > fixedOffset)
        {
            effectiveStart = false;
            if (!candidateIsBoundary && !onGraphemeBoundary)
                offset = clusterEnd;
        }
        else if (allowCrossing && !startEndpoint && offset < fixedOffset)
        {
            effectiveStart = true;
            if (!candidateIsBoundary && !onGraphemeBoundary)
                offset = clusterStart;
        }
        else if (!allowCrossing)
        {
            offset = startEndpoint
                ? Math.Min(offset, boundaries.FindPreviousStart(fixedOffset))
                : Math.Max(offset, boundaries.FindNextStart(fixedOffset));
        }

        return new TouchSelectionEndpointAdjustment(
            Math.Clamp(offset, 0, boundaries.TextLength),
            effectiveStart);
    }
}
