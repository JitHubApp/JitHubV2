using System;
using System.Collections.Generic;
using System.Threading;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout;

/// <summary>
/// A compact, piecewise mapping from a transformed Markdown fragment back to
/// the document that authored it. Segments are UTF-16 ranges, matching both
/// Markdig spans and the public source-map contract.
/// </summary>
internal sealed class MarkdownSourceProjection
{
    private readonly Segment[] _segments;
    private Dictionary<HtmlBlock, int>? _htmlBlockStarts;

    internal MarkdownSourceProjection(Segment[] segments, int localLength)
    {
        _segments = segments;
        LocalLength = localLength;
    }

    internal int LocalLength { get; }

    internal bool TryMapRange(int localStart, int localLength, out SourceSpan sourceSpan)
    {
        sourceSpan = SourceSpan.Empty;
        if (localStart < 0 || localLength < 0 || localStart > LocalLength ||
            localLength > LocalLength - localStart)
        {
            return false;
        }

        if (localLength == 0)
        {
            if (!TryMapBoundary(localStart, preferFollowing: true, out int point))
                return false;
            sourceSpan = new SourceSpan(point, 0);
            return true;
        }

        if (!TryFindSegment(localStart, preferFollowing: true, out _) ||
            !TryFindSegment(localStart + localLength, preferFollowing: false, out _) ||
            !TryMapBoundary(localStart, preferFollowing: true, out int sourceStart) ||
            !TryMapBoundary(localStart + localLength, preferFollowing: false, out int sourceEnd) ||
            sourceEnd < sourceStart)
        {
            return false;
        }

        sourceSpan = new SourceSpan(sourceStart, sourceEnd - sourceStart);
        return true;
    }

    internal SourceSpan MapRangeOrEmpty(int localStart, int localLength)
        => TryMapRange(localStart, localLength, out SourceSpan span) ? span : SourceSpan.Empty;

    internal int GetNextSegmentEnd(int localStart, int limit)
    {
        if (localStart < 0 || limit < localStart || limit > LocalLength ||
            !TryFindSegment(localStart, preferFollowing: true, out Segment segment))
        {
            return localStart;
        }

        return Math.Min(limit, segment.LocalEnd);
    }

    internal void ApplyTo(Markdig.Syntax.MarkdownDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        _htmlBlockStarts = new Dictionary<HtmlBlock, int>(ReferenceEqualityComparer.Instance);
        int workItems = 0;
        ApplyBlock(document, cancellationToken, ref workItems);
    }

    internal bool TryGetHtmlBlockProjection(
        HtmlBlock block,
        string htmlSource,
        CancellationToken cancellationToken,
        out MarkdownSourceProjection projection)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(htmlSource);
        projection = null!;
        if (_htmlBlockStarts is not { } htmlBlockStarts ||
            !htmlBlockStarts.ContainsKey(block))
        {
            return false;
        }

        var builder = new MarkdownSourceProjectionBuilder();
        int htmlOffset = 0;
        IReadOnlyList<StringLine> lines = block.Lines.Lines;
        int lineCount = block.Lines.Count;
        if (lineCount > lines.Count)
            return false;

        for (int index = 0; index < lineCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StringLine line = lines[index];
            string lineText = line.Slice.ToString();
            if (lineText.Length > htmlSource.Length - htmlOffset ||
                !htmlSource.AsSpan(htmlOffset, lineText.Length).SequenceEqual(lineText.AsSpan()))
            {
                return false;
            }

            if (lineText.Length > 0)
                builder.AppendProjection(lineText, this, line.Slice.Start, cancellationToken);

            htmlOffset += lineText.Length;
            if (index + 1 < lineCount)
            {
                StringLine nextLine = lines[index + 1];
                if (nextLine.Slice.ToString().Length == 0 && htmlOffset == htmlSource.Length)
                    break;

                if (htmlOffset >= htmlSource.Length || htmlSource[htmlOffset] != '\n')
                    return false;

                int separatorOffset = line.Slice.Start + line.Slice.Length;
                builder.AppendProjection("\n", this, separatorOffset, cancellationToken);
                htmlOffset++;
            }
        }

        if (htmlOffset != htmlSource.Length)
            return false;

        projection = builder.Build();
        return true;
    }

    private void ApplyBlock(Block block, CancellationToken cancellationToken, ref int workItems)
    {
        if ((workItems++ & 0x3F) == 0)
            cancellationToken.ThrowIfCancellationRequested();

        if (block is HtmlBlock htmlBlock && htmlBlock.Span.Start >= 0)
            _htmlBlockStarts?.TryAdd(htmlBlock, htmlBlock.Span.Start);

        ApplySpan(block);
        if (block is ContainerBlock container)
        {
            foreach (Block child in container)
                ApplyBlock(child, cancellationToken, ref workItems);
        }

        if (block is LeafBlock { Inline: not null } leaf)
            ApplyInline(leaf.Inline, cancellationToken, ref workItems);
    }

    private void ApplyInline(ContainerInline container, CancellationToken cancellationToken, ref int workItems)
    {
        if ((workItems++ & 0x3F) == 0)
            cancellationToken.ThrowIfCancellationRequested();

        ApplySpan(container);
        if (container is LinkInline link)
        {
            ApplySpan(ref link.UrlSpan);
            ApplySpan(ref link.TitleSpan);
        }

        foreach (Inline inline in container)
        {
            if (inline is ContainerInline child)
                ApplyInline(child, cancellationToken, ref workItems);
            else
            {
                if ((workItems++ & 0x3F) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                ApplySpan(inline);
            }
        }
    }

    private void ApplySpan(MarkdownObject node)
    {
        if (!TryMapSpan(node.Span, out SourceSpan mapped))
            return;

        node.Span = new Markdig.Syntax.SourceSpan(mapped.Start, checked(mapped.End - 1));
    }

    private void ApplySpan(ref Markdig.Syntax.SourceSpan span)
    {
        if (TryMapSpan(span, out SourceSpan mapped))
            span = new Markdig.Syntax.SourceSpan(mapped.Start, checked(mapped.End - 1));
    }

    private bool TryMapSpan(Markdig.Syntax.SourceSpan span, out SourceSpan mapped)
    {
        mapped = SourceSpan.Empty;
        return !span.IsEmpty && span.Start >= 0 && span.End >= span.Start &&
            TryMapRange(span.Start, checked(span.End - span.Start + 1), out mapped) &&
            mapped.Length > 0;
    }

    private bool TryMapBoundary(int localOffset, bool preferFollowing, out int sourceOffset)
    {
        sourceOffset = 0;
        if (_segments.Length == 0)
            return false;

        if (localOffset == LocalLength)
        {
            Segment last = _segments[^1];
            sourceOffset = last.SourceEnd;
            return true;
        }

        if (!TryFindSegment(localOffset, preferFollowing, out Segment segment))
            return false;

        if (localOffset <= segment.LocalStart)
        {
            sourceOffset = segment.SourceStart;
            return true;
        }

        if (localOffset >= segment.LocalEnd)
        {
            sourceOffset = segment.SourceEnd;
            return true;
        }

        int localDelta = localOffset - segment.LocalStart;
        sourceOffset = segment.SourceLength == segment.LocalLength
            ? segment.SourceStart + localDelta
            : segment.SourceStart;
        return true;
    }

    private bool TryFindSegment(int localOffset, bool preferFollowing, out Segment segment)
    {
        int low = 0;
        int high = _segments.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            Segment candidate = _segments[middle];
            if (candidate.LocalStart < localOffset)
                low = middle + 1;
            else
                high = middle;
        }

        // At an exact segment boundary, the source may have a trimmed gap
        // between the previous segment's end and the next segment's start.
        // Endpoints must stay attached to the segment that owns the range,
        // rather than jumping across that omitted source text.
        if (low < _segments.Length && _segments[low].LocalStart == localOffset)
        {
            if (!preferFollowing && low > 0 && _segments[low - 1].LocalEnd == localOffset)
            {
                segment = _segments[low - 1];
                return true;
            }

            segment = _segments[low];
            return true;
        }

        int precedingIndex = low - 1;
        if (precedingIndex >= 0 && localOffset <= _segments[precedingIndex].LocalEnd)
        {
            segment = _segments[precedingIndex];
            return true;
        }

        segment = default;
        return false;
    }

    internal readonly record struct Segment(int LocalStart, int LocalLength, int SourceStart, int SourceLength)
    {
        internal int LocalEnd => LocalStart + LocalLength;

        internal int SourceEnd => SourceStart + SourceLength;
    }
}

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
