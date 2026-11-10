using System;
using System.Collections.Generic;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.SourceProjection;

/// <summary>Builds source projections alongside the transformed fragment text.</summary>
internal sealed class MarkdownSourceProjectionBuilder
{
    private readonly List<MarkdownSourceProjection.Segment> _segments = [];
    private int _localLength;

    internal int Length => _localLength;

    internal void AppendDirect(string value, int sourceStart)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            return;

        AppendMapped(value, new SourceSpan(sourceStart, value.Length));
    }

    internal void AppendMapped(string value, SourceSpan sourceSpan)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            return;
        if (sourceSpan.Start < 0 || sourceSpan.Length < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSpan));

        var segment = new MarkdownSourceProjection.Segment(
            _localLength,
            value.Length,
            sourceSpan.Start,
            sourceSpan.Length);
        if (_segments.Count > 0 && CanMerge(_segments[^1], segment))
        {
            MarkdownSourceProjection.Segment previous = _segments[^1];
            _segments[^1] = previous with
            {
                LocalLength = previous.LocalLength + segment.LocalLength,
                SourceLength = previous.SourceLength + segment.SourceLength,
            };
        }
        else
        {
            _segments.Add(segment);
        }

        _localLength = checked(_localLength + value.Length);
    }

    internal void AppendProjection(
        string value,
        MarkdownSourceProjection projection,
        int projectionStart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(projection);
        if (projectionStart < 0 || projectionStart > projection.LocalLength ||
            value.Length > projection.LocalLength - projectionStart)
        {
            throw new ArgumentOutOfRangeException(nameof(projectionStart));
        }

        int projectionEnd = projectionStart + value.Length;
        int copied = 0;
        while (copied < value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int localStart = projectionStart + copied;
            int segmentEnd = Math.Min(projectionEnd, checked(localStart + 4096));
            segmentEnd = projection.GetNextSegmentEnd(localStart, segmentEnd);
            int length = segmentEnd - localStart;
            if (length <= 0 || !projection.TryMapRange(localStart, length, out SourceSpan sourceSpan))
                throw new InvalidOperationException("The source projection does not cover the appended fragment.");

            AppendMapped(value.Substring(copied, length), sourceSpan);
            copied += length;
        }
    }

    internal MarkdownSourceProjection Build()
        => new(_segments.ToArray(), _localLength);

    private static bool CanMerge(
        MarkdownSourceProjection.Segment previous,
        MarkdownSourceProjection.Segment next)
        => previous.LocalEnd == next.LocalStart &&
           previous.SourceEnd == next.SourceStart &&
           previous.LocalLength == previous.SourceLength &&
           next.LocalLength == next.SourceLength;
}
