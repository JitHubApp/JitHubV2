using System;
using MarkdownRenderer.Layout;

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
        // P1-036: a hit-tested candidate is a caret position. On a grapheme
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

internal readonly struct TouchSelectionEndpointAdjustment
{
    public TouchSelectionEndpointAdjustment(int Offset, bool IsStartEndpoint)
    {
        this.Offset = Offset;
        this.IsStartEndpoint = IsStartEndpoint;
    }

    public int Offset { get; init; }
    public bool IsStartEndpoint { get; init; }
}

/// <summary>
/// Maps a half-open selection boundary to the character side owned by that
/// selected range. DirectWrite then resolves the visual caret edge, including
/// run-level bidi reordering.
/// </summary>
internal static class TouchSelectionCaretPolicy
{
    public static TouchSelectionCaretQuery Resolve(
        int textLength,
        int offset,
        bool rangeStart)
    {
        if (textLength <= 0)
            return default;

        offset = Math.Clamp(offset, 0, textLength);
        if (rangeStart)
        {
            return offset < textLength
                ? new TouchSelectionCaretQuery(offset, TrailingSideOfCharacter: false)
                : new TouchSelectionCaretQuery(textLength - 1, TrailingSideOfCharacter: true);
        }

        return offset > 0
            ? new TouchSelectionCaretQuery(offset - 1, TrailingSideOfCharacter: true)
            : new TouchSelectionCaretQuery(0, TrailingSideOfCharacter: false);
    }

    public static double ResolveVisualX(
        double caretX,
        double layoutWidth,
        bool paragraphRightToLeft)
    {
        if (!double.IsFinite(caretX) || !double.IsFinite(layoutWidth) || layoutWidth < 0)
            return double.NaN;

        return paragraphRightToLeft
            ? layoutWidth - caretX
            : caretX;
    }
}

internal readonly struct TouchSelectionCaretQuery
{
    public TouchSelectionCaretQuery(int CharacterIndex, bool TrailingSideOfCharacter)
    {
        this.CharacterIndex = CharacterIndex;
        this.TrailingSideOfCharacter = TrailingSideOfCharacter;
    }

    public int CharacterIndex { get; init; }
    public bool TrailingSideOfCharacter { get; init; }
}
