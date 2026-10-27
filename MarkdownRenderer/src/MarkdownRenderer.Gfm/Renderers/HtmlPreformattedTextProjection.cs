using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Builds the displayed text and exact source projection for a safe-HTML
/// preformatted element. SafeHtmlElement.SourceLength describes only its
/// opening tag, so the source envelope is derived from parser-owned child and
/// closing-tag spans instead of treating that opening tag as the whole block.
/// </summary>
internal sealed class HtmlPreformattedTextProjection
{
    private HtmlPreformattedTextProjection(
        string text,
        SourceSpan sourceSpan,
        MarkdownSourceMap.SourceProjection? sourceProjection)
    {
        Text = text;
        SourceSpan = sourceSpan;
        SourceProjection = sourceProjection;
    }

    internal string Text { get; }

    internal SourceSpan SourceSpan { get; }

    internal MarkdownSourceMap.SourceProjection? SourceProjection { get; }

    internal static HtmlPreformattedTextProjection? Create(
        SafeHtmlElement pre,
        MarkdownLayoutContext context,
        int sourceOffset)
    {
        ArgumentNullException.ThrowIfNull(pre);
        ArgumentNullException.ThrowIfNull(context);

        var work = new WorkCounter(context);
        int sourceEnd = GetElementSourceEnd(pre, work);
        if (sourceEnd <= pre.SourceStart)
            return null;

        int sourceStartInInput = checked(sourceOffset + pre.SourceStart);
        int sourceLengthInInput = checked(sourceEnd - pre.SourceStart);
        if (!TryMapSafeHtmlRange(context, sourceStartInInput, sourceLengthInInput, out SourceSpan sourceSpan))
            throw new InvalidOperationException("The safe-HTML source projection does not cover a preformatted element.");

        var builder = new ProjectionBuilder(context, sourceOffset, sourceSpan, work);
        AppendDescendantText(pre, builder, work);

        return builder.Build();
    }

    private static int GetElementSourceEnd(SafeHtmlNode node, WorkCounter work)
    {
        work.Checkpoint();
        int end = checked(node.SourceStart + node.SourceLength);
        if (node is not SafeHtmlElement element)
            return end;

        if (element.RawClosingTag.Length > 0)
            end = Math.Max(end, checked(element.RawClosingStart + element.RawClosingTag.Length));
        if (element.RawTrailingMarkup.Length > 0)
            end = Math.Max(end, checked(element.RawTrailingStart + element.RawTrailingMarkup.Length));

        foreach (SafeHtmlNode child in element.Children)
            end = Math.Max(end, GetElementSourceEnd(child, work));

        return end;
    }

    private static void AppendDescendantText(
        SafeHtmlElement parent,
        ProjectionBuilder builder,
        WorkCounter work)
    {
        foreach (SafeHtmlNode child in parent.Children)
        {
            work.Checkpoint();
            if (child is SafeHtmlText text)
            {
                builder.AppendHtmlText(text);
            }
            else if (child is SafeHtmlElement element && !SafeHtmlParser.IsSuppressedElement(element.Name))
            {
                AppendDescendantText(element, builder, work);
            }
        }
    }

    private static bool TryMapSafeHtmlRange(
        MarkdownLayoutContext context,
        int inputStart,
        int inputLength,
        out SourceSpan sourceSpan)
    {
        if (context.SafeHtmlSourceProjection is { } projection)
            return projection.TryMapRange(inputStart, inputLength, out sourceSpan);

        sourceSpan = new SourceSpan(inputStart, inputLength);
        return true;
    }

    private sealed class ProjectionBuilder
    {
        private readonly MarkdownLayoutContext _context;
        private readonly int _sourceOffset;
        private readonly SourceSpan _sourceSpan;
        private readonly MarkdownSourceProjection? _safeHtmlProjection;
        private readonly WorkCounter _work;
        private readonly StringBuilder _text;
        private readonly List<MarkdownSourceMap.ProjectionSegment> _segments = [];
        private SourceSpan? _pendingCarriageReturn;

        internal ProjectionBuilder(
            MarkdownLayoutContext context,
            int sourceOffset,
            SourceSpan sourceSpan,
            WorkCounter work)
        {
            _context = context;
            _sourceOffset = sourceOffset;
            _sourceSpan = sourceSpan;
            _safeHtmlProjection = context.SafeHtmlSourceProjection;
            _work = work;
            _text = new StringBuilder(Math.Min(sourceSpan.Length, 64 * 1024));
        }

        internal void AppendHtmlText(SafeHtmlText node)
        {
            string raw = node.RawText;
            int cursor = 0;
            while (cursor < raw.Length)
            {
                _work.Checkpoint();
                if (raw[cursor] != '&')
                {
                    int nextAmpersand = FindNext(raw, '&', cursor);
                    int identityEnd = nextAmpersand < 0 ? raw.Length : nextAmpersand;
                    AppendIdentity(node, cursor, identityEnd - cursor);
                    cursor = identityEnd;
                    continue;
                }

                int delimiter = cursor + 1;
                while (delimiter < raw.Length && raw[delimiter] is not ('&' or ';'))
                {
                    _work.Checkpoint();
                    delimiter++;
                }

                if (delimiter >= raw.Length)
                {
                    AppendIdentity(node, cursor, raw.Length - cursor);
                    break;
                }

                if (raw[delimiter] == '&')
                {
                    // The candidate before this ampersand cannot be an entity
                    // understood by WebUtility.HtmlDecode. Emit it literally
                    // and reconsider this exact parser-owned source position.
                    AppendIdentity(node, cursor, delimiter - cursor);
                    cursor = delimiter;
                    continue;
                }

                int candidateLength = delimiter - cursor + 1;
                string encoded = raw.Substring(cursor, candidateLength);
                string decoded = WebUtility.HtmlDecode(encoded) ?? string.Empty;
                if (decoded.Length == 0 || decoded.AsSpan().SequenceEqual(encoded.AsSpan()))
                {
                    AppendIdentity(node, cursor, candidateLength);
                }
                else
                {
                    int localStart = checked(node.SourceStart + cursor);
                    if (!TryMap(localStart, candidateLength, out SourceSpan source))
                        throw new InvalidOperationException("The safe-HTML source projection does not cover an entity reference.");
                    AppendDecoded(decoded, source, forceAtomic: true);
                }

                cursor = delimiter + 1;
            }
        }

        internal HtmlPreformattedTextProjection? Build()
        {
            FlushPendingCarriageReturn();
            if (_text.Length == 0)
                return new HtmlPreformattedTextProjection(string.Empty, _sourceSpan, null);

            MarkdownSourceMap.SourceProjection? projection = MarkdownSourceMap.SourceProjection.TryCreateExactSegments(
                _text.Length,
                _sourceSpan.Length,
                _segments,
                includeSourcePrefix: true,
                includeSourceSuffix: true);
            if (projection is null)
                throw new InvalidOperationException("The parser-derived preformatted source projection is invalid.");

            return new HtmlPreformattedTextProjection(_text.ToString(), _sourceSpan, projection);
        }

        private int FindNext(string value, char target, int start)
        {
            for (int index = start; index < value.Length; index++)
            {
                _work.Checkpoint();
                if (value[index] == target)
                    return index;
            }

            return -1;
        }

        private void AppendIdentity(SafeHtmlText node, int rawOffset, int rawLength)
        {
            if (rawLength <= 0)
                return;

            int localStart = checked(node.SourceStart + rawOffset);
            int localEnd = checked(localStart + rawLength);
            int cursor = localStart;
            while (cursor < localEnd)
            {
                _work.Checkpoint();
                int segmentEnd = Math.Min(localEnd, checked(cursor + 4096));
                if (_safeHtmlProjection is not null)
                {
                    segmentEnd = Math.Min(
                        segmentEnd,
                        _safeHtmlProjection.GetNextSegmentEnd(cursor, segmentEnd));
                }
                if (segmentEnd <= cursor)
                    throw new InvalidOperationException("The safe-HTML source projection has a gap in text content.");

                int partLength = segmentEnd - cursor;
                int nodeOffset = rawOffset + (cursor - localStart);
                if (!TryMap(cursor, partLength, out SourceSpan source))
                    throw new InvalidOperationException("The safe-HTML source projection does not cover text content.");
                AppendDecoded(node.RawText.Substring(nodeOffset, partLength), source, forceAtomic: false);
                cursor = segmentEnd;
            }
        }

        private bool TryMap(int localStart, int localLength, out SourceSpan source)
        {
            int inputStart = checked(_sourceOffset + localStart);
            return TryMapSafeHtmlRange(_context, inputStart, localLength, out source);
        }

        private void AppendDecoded(string value, SourceSpan source, bool forceAtomic)
        {
            bool atomic = forceAtomic || source.Length != value.Length;
            int cursor = 0;
            while (cursor < value.Length)
            {
                _work.Checkpoint();
                if (_pendingCarriageReturn is { } pending)
                {
                    if (value[cursor] == '\n')
                    {
                        SourceSpan lineFeedSource = GetSourceSlice(source, value.Length, cursor, 1, atomic);
                        AppendSegment("\n", Combine(pending, lineFeedSource), forceAtomic: true);
                        _pendingCarriageReturn = null;
                        cursor++;
                        continue;
                    }

                    AppendSegment("\n", pending, forceAtomic: true);
                    _pendingCarriageReturn = null;
                }

                int carriageReturn = value.IndexOf('\r', cursor);
                int plainEnd = carriageReturn < 0 ? value.Length : carriageReturn;
                if (plainEnd > cursor)
                {
                    int plainLength = plainEnd - cursor;
                    SourceSpan plainSource = GetSourceSlice(source, value.Length, cursor, plainLength, atomic);
                    AppendSegment(value.Substring(cursor, plainLength), plainSource, atomic);
                }

                if (carriageReturn < 0)
                    break;

                _pendingCarriageReturn = GetSourceSlice(source, value.Length, carriageReturn, 1, atomic);
                cursor = carriageReturn + 1;
            }
        }

        private static SourceSpan GetSourceSlice(
            SourceSpan source,
            int renderedLength,
            int renderedStart,
            int length,
            bool atomic)
        {
            if (atomic || source.Length != renderedLength)
                return source;

            return new SourceSpan(checked(source.Start + renderedStart), length);
        }

        private static SourceSpan Combine(SourceSpan first, SourceSpan second)
        {
            if (second.Start < first.Start || second.End < first.End)
                throw new InvalidOperationException("Preformatted source segments are not ordered.");

            return new SourceSpan(first.Start, second.End - first.Start);
        }

        private void FlushPendingCarriageReturn()
        {
            if (_pendingCarriageReturn is not { } pending)
                return;

            AppendSegment("\n", pending, forceAtomic: true);
            _pendingCarriageReturn = null;
        }

        private void AppendSegment(string value, SourceSpan source, bool forceAtomic)
        {
            if (value.Length == 0)
                return;

            int sourceStart = source.Start - _sourceSpan.Start;
            bool atomic = forceAtomic || source.Length != value.Length;
            if (sourceStart < 0 || source.Length <= 0 || sourceStart > _sourceSpan.Length ||
                source.Length > _sourceSpan.Length - sourceStart)
            {
                throw new InvalidOperationException("A preformatted text segment falls outside its source envelope.");
            }

            int renderedStart = _text.Length;
            int copied = 0;
            while (copied < value.Length)
            {
                _work.Checkpoint();
                int count = Math.Min(4096, value.Length - copied);
                _text.Append(value.AsSpan(copied, count));
                copied += count;
            }

            var next = new MarkdownSourceMap.ProjectionSegment(
                renderedStart,
                value.Length,
                sourceStart,
                source.Length,
                atomic);

            if (!atomic && _segments.Count > 0)
            {
                MarkdownSourceMap.ProjectionSegment previous = _segments[^1];
                if (!previous.IsAtomic &&
                    previous.RenderedStart + previous.RenderedLength == next.RenderedStart &&
                    previous.SourceStart + previous.SourceLength == next.SourceStart)
                {
                    _segments[^1] = previous with
                    {
                        RenderedLength = previous.RenderedLength + next.RenderedLength,
                        SourceLength = previous.SourceLength + next.SourceLength,
                    };
                    return;
                }
            }

            _segments.Add(next);
        }
    }

    private sealed class WorkCounter
    {
        private readonly MarkdownLayoutContext _context;
        private int _remaining;

        internal WorkCounter(MarkdownLayoutContext context)
        {
            _context = context;
            _remaining = 0;
        }

        internal void Checkpoint()
        {
            if (--_remaining <= 0)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                _remaining = 4096;
            }
        }
    }
}
