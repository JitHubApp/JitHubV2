using System;
using System.Collections.Generic;
using Markdig.Helpers;
using Markdig.Syntax;
using MarkdownSourceMap = MarkdownRenderer.Document.MarkdownSourceMap;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.SourceProjection;

namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

/// <summary>
/// Maps rendered code-block lines and chunks back to parser source ranges so
/// selection, copy and accessibility projections address the original text.
/// </summary>
internal sealed class CodeBlockSourceProjector
{
    private readonly MarkdownLayoutContext _context;

    internal CodeBlockSourceProjector(
        MarkdownLayoutContext context)
    {
        _context = context;
    }

    internal bool TryPrepareCodeBlockSourceMap(LeafBlock block, string text, out SourceSpan sourceSpan)
    {
        sourceSpan = SourceSpan.Empty;
        if (block.Span.Start < 0 || block.Span.Length <= 0)
            return false;

        IReadOnlyList<StringLine> lines = block.Lines.Lines;
        int lineCount = block.Lines.Count;
        if (lineCount <= 0 || lineCount > lines.Count)
            return false;

        MarkdownSourceProjection? transformedSource = _context.SourceProjection;
        if (!TryResolveBlockSourceSpan(block, out sourceSpan))
            return false;

        int renderedOffset = 0;
        string? lineSourceText = null;
        for (int index = 0; index < lineCount; index++)
        {
            if ((index & 0x3f) == 0)
                _context.CancellationToken.ThrowIfCancellationRequested();

            StringLine line = lines[index];
            StringSlice slice = line.Slice;
            string? sliceSource = slice.Text;
            int sliceStart = slice.Start;
            int sliceLength = slice.Length;
            if (lineSourceText is null)
            {
                lineSourceText = sliceSource;
                if (transformedSource is null &&
                    (sliceSource is null || !string.Equals(
                        sliceSource,
                        _context.SourceMap.SourceText,
                        StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            if (sliceSource is null ||
                !ReferenceEquals(lineSourceText, sliceSource) ||
                !IsRenderedLineContent(text, renderedOffset, sliceSource, sliceStart, sliceLength, sourceSpan))
            {
                return false;
            }

            renderedOffset += sliceLength;
            // Markdig's StringLineGroup.ToString emits separators only between
            // stored lines. The last line can carry NewLine metadata in the
            // original source (for example, the LF before a closing fence),
            // but that final terminator is not part of the rendered code text.
            int newlineLength = index + 1 < lineCount
                ? GetCodeBlockSourceNewlineLength(line.NewLine)
                : 0;
            if (newlineLength < 0)
                return false;
            if (newlineLength == 0)
                continue;

            int newlineStart = sliceStart + sliceLength;
            if (!IsRenderedLineNewline(text, renderedOffset, sliceSource, newlineStart, newlineLength, line.NewLine, sourceSpan))
                return false;

            renderedOffset++;
        }

        if (renderedOffset != text.Length)
            return false;

        return true;
    }

    // GitHubDetailsBlockRenderer calls MarkdownSourceProjection.ApplyTo
    // before building a transformed fragment. That rewrites AST node spans into
    // document coordinates, while Markdig's StringLine slices remain
    // fragment-local. Do not project block.Span a second time; only line
    // slices are still in the projection's local domain. Either way the block
    // span is checked against the document source text.
    private bool TryResolveBlockSourceSpan(LeafBlock block, out SourceSpan sourceSpan)
    {
        sourceSpan = SourceSpan.Empty;
        string sourceText = _context.SourceMap.SourceText;
        if (block.Span.Start > sourceText.Length ||
            block.Span.Length > sourceText.Length - block.Span.Start)
        {
            return false;
        }

        sourceSpan = new SourceSpan(block.Span.Start, block.Span.Length);
        return true;
    }

    // One stored line's slice must be in range, match the rendered text at the
    // current offset and map inside the block's source span.
    private bool IsRenderedLineContent(
        string text,
        int renderedOffset,
        string sliceSource,
        int sliceStart,
        int sliceLength,
        SourceSpan sourceSpan)
    {
        if (sliceStart < 0 || sliceLength < 0 ||
            sliceStart > sliceSource.Length || sliceLength > sliceSource.Length - sliceStart ||
            sliceLength > text.Length - renderedOffset ||
            !text.AsSpan(renderedOffset, sliceLength).SequenceEqual(sliceSource.AsSpan(sliceStart, sliceLength)))
        {
            return false;
        }

        return sliceLength == 0 ||
            (TryMapCodeBlockParserRange(sliceSource, sliceStart, sliceLength, out SourceSpan mappedLine) &&
             ContainsSourceRange(sourceSpan, mappedLine));
    }

    // A stored separator must be the authored terminator in source, a single
    // LF in the rendered text, and map inside the block's source span.
    private bool IsRenderedLineNewline(
        string text,
        int renderedOffset,
        string sliceSource,
        int newlineStart,
        int newlineLength,
        NewLine newline,
        SourceSpan sourceSpan)
        => IsExpectedSourceNewline(sliceSource, newlineStart, newline) &&
           renderedOffset < text.Length && text[renderedOffset] == '\n' &&
           TryMapCodeBlockParserRange(sliceSource, newlineStart, newlineLength, out SourceSpan mappedNewline) &&
           ContainsSourceRange(sourceSpan, mappedNewline);

    private bool TryMapCodeBlockParserRange(string parserSource, int localStart, int localLength, out SourceSpan sourceSpan)
    {
        sourceSpan = SourceSpan.Empty;
        if (localStart < 0 || localLength < 0 || localStart > parserSource.Length ||
            localLength > parserSource.Length - localStart)
        {
            return false;
        }

        if (_context.SourceProjection is { } projection)
            return projection.TryMapRange(localStart, localLength, out sourceSpan);

        string sourceText = _context.SourceMap.SourceText;
        if (localStart > sourceText.Length || localLength > sourceText.Length - localStart)
            return false;

        sourceSpan = new SourceSpan(localStart, localLength);
        return true;
    }

    internal MarkdownSourceMap.SourceProjection? TryCreateCodeBlockChunkProjection(
        LeafBlock block,
        string text,
        int chunkStart,
        int chunkLength,
        SourceSpan blockSourceSpan,
        bool includeSourcePrefix,
        bool includeSourceSuffix,
        ref CodeBlockSourceCursor cursor)
    {
        if (chunkStart < 0 || chunkLength <= 0 || chunkStart > text.Length || chunkLength > text.Length - chunkStart ||
            blockSourceSpan.IsEmpty)
        {
            return null;
        }

        int chunkEnd = chunkStart + chunkLength;
        IReadOnlyList<StringLine> lines = block.Lines.Lines;
        int lineCount = block.Lines.Count;
        if (lineCount <= 0 || lineCount > lines.Count)
            return null;

        var segments = new List<MarkdownSourceMap.ProjectionSegment>(Math.Min(lineCount, 260));
        while (cursor.LineIndex < lineCount && cursor.RenderedOffset < chunkEnd)
        {
            if ((cursor.LineIndex & 0x3f) == 0)
                _context.CancellationToken.ThrowIfCancellationRequested();

            bool? advanced = TryProjectChunkLine(
                segments,
                text,
                lines[cursor.LineIndex],
                hasFollowingLine: cursor.LineIndex + 1 < lineCount,
                chunkStart,
                chunkEnd,
                blockSourceSpan,
                ref cursor);
            if (advanced is null)
                return null;
            if (advanced == false)
                break;
        }

        if (segments.Count == 0)
            return null;

        return MarkdownSourceMap.SourceProjection.TryCreateExactSegments(
            chunkLength,
            blockSourceSpan.Length,
            segments,
            includeSourcePrefix,
            includeSourceSuffix);
    }

    // Projects one stored line's content and separator into the chunk's
    // segments. Returns null when the line cannot be projected exactly, true
    // when the cursor moved past the whole line, and false when the line
    // continues beyond the chunk and the next chunk resumes on it.
    private bool? TryProjectChunkLine(
        List<MarkdownSourceMap.ProjectionSegment> segments,
        string text,
        StringLine line,
        bool hasFollowingLine,
        int chunkStart,
        int chunkEnd,
        SourceSpan blockSourceSpan,
        ref CodeBlockSourceCursor cursor)
    {
        StringSlice slice = line.Slice;
        string? parserSource = slice.Text;
        cursor.ParserSource ??= parserSource;
        int contentStart = cursor.RenderedOffset;
        int contentLength = slice.Length;
        if (parserSource is null || !ReferenceEquals(cursor.ParserSource, parserSource) ||
            slice.Start < 0 || slice.Start > parserSource.Length || contentLength < 0 ||
            contentLength > parserSource.Length - slice.Start || contentStart > text.Length ||
            contentLength > text.Length - contentStart)
        {
            return null;
        }

        int contentEnd = contentStart + contentLength;
        if (!text.AsSpan(contentStart, contentLength).SequenceEqual(parserSource.AsSpan(slice.Start, contentLength)))
        {
            return null;
        }

        if (!TryAddChunkContentSegment(
                segments,
                parserSource,
                slice.Start,
                contentStart,
                contentEnd,
                chunkStart,
                chunkEnd,
                blockSourceSpan))
        {
            return null;
        }

        int sourceNewlineLength = hasFollowingLine
            ? GetCodeBlockSourceNewlineLength(line.NewLine)
            : 0;
        if (sourceNewlineLength < 0)
            return null;
        int renderedLineEnd = contentEnd;
        if (sourceNewlineLength > 0 && renderedLineEnd >= chunkStart && renderedLineEnd < chunkEnd &&
            !TryAddChunkNewlineSegment(
                segments,
                text,
                parserSource,
                slice.Start + contentLength,
                sourceNewlineLength,
                line.NewLine,
                renderedLineEnd,
                chunkStart,
                blockSourceSpan))
        {
            return null;
        }

        int renderedLineLength = contentLength + (sourceNewlineLength > 0 ? 1 : 0);
        int renderedLineEndWithNewline = contentStart + renderedLineLength;
        if (renderedLineEndWithNewline > chunkEnd)
            return false;

        cursor.LineIndex++;
        cursor.RenderedOffset = renderedLineEndWithNewline;
        return true;
    }

    // Adds the part of one line's content that falls inside the chunk.
    private bool TryAddChunkContentSegment(
        List<MarkdownSourceMap.ProjectionSegment> segments,
        string parserSource,
        int sliceStart,
        int contentStart,
        int contentEnd,
        int chunkStart,
        int chunkEnd,
        SourceSpan blockSourceSpan)
    {
        int selectedContentStart = Math.Max(contentStart, chunkStart);
        int selectedContentEnd = Math.Min(contentEnd, chunkEnd);
        if (selectedContentEnd <= selectedContentStart)
            return true;

        int parserStart = sliceStart + selectedContentStart - contentStart;
        int parserLength = selectedContentEnd - selectedContentStart;
        return TryAddCodeBlockProjectionSegment(
            segments,
            selectedContentStart - chunkStart,
            selectedContentEnd - selectedContentStart,
            parserSource,
            parserStart,
            parserLength,
            blockSourceSpan,
            allowAtomicLengthChange: false);
    }

    // Adds a line's rendered LF as one atomic segment over its authored terminator.
    private bool TryAddChunkNewlineSegment(
        List<MarkdownSourceMap.ProjectionSegment> segments,
        string text,
        string parserSource,
        int parserNewlineStart,
        int sourceNewlineLength,
        NewLine newline,
        int renderedLineEnd,
        int chunkStart,
        SourceSpan blockSourceSpan)
        => IsExpectedSourceNewline(parserSource, parserNewlineStart, newline) &&
           renderedLineEnd < text.Length && text[renderedLineEnd] == '\n' &&
           TryAddCodeBlockProjectionSegment(
               segments,
               renderedLineEnd - chunkStart,
               1,
               parserSource,
               parserNewlineStart,
               sourceNewlineLength,
               blockSourceSpan,
               allowAtomicLengthChange: true);

    private bool TryAddCodeBlockProjectionSegment(
        List<MarkdownSourceMap.ProjectionSegment> segments,
        int renderedStart,
        int renderedLength,
        string parserSource,
        int parserStart,
        int parserSourceLength,
        SourceSpan blockSourceSpan,
        bool allowAtomicLengthChange)
    {
        if (renderedLength <= 0 || parserSourceLength <= 0 ||
            !TryMapCodeBlockParserRange(parserSource, parserStart, parserSourceLength, out SourceSpan mapped) ||
            !ContainsSourceRange(blockSourceSpan, mapped) || mapped.IsEmpty)
        {
            return false;
        }

        if (allowAtomicLengthChange)
        {
            // Markdig emits one normalized LF for CR, LF, or CRLF source line
            // endings. Preserve the parser's complete authored terminator as
            // one atomic source range instead of treating its source width as
            // rendered text.
            if (renderedLength != 1 ||
                !TryMapCodeBlockParserRange(parserSource, parserStart, parserSourceLength, out mapped) ||
                !ContainsSourceRange(blockSourceSpan, mapped) || mapped.IsEmpty)
            {
                return false;
            }

            segments.Add(new MarkdownSourceMap.ProjectionSegment(
                renderedStart,
                1,
                SourceStart: mapped.Start - blockSourceSpan.Start,
                SourceLength: mapped.Length,
                IsAtomic: mapped.Length != 1));
            return true;
        }

        MarkdownSourceProjection? transformedSource = _context.SourceProjection;
        int parserEnd = parserStart + parserSourceLength;
        int current = parserStart;
        int renderedCurrent = renderedStart;
        while (current < parserEnd)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int pieceEnd = transformedSource is null
                ? parserEnd
                : transformedSource.GetNextSegmentEnd(current, parserEnd);
            if (pieceEnd <= current ||
                !TryMapCodeBlockParserRange(parserSource, current, pieceEnd - current, out mapped) ||
                !ContainsSourceRange(blockSourceSpan, mapped) || mapped.IsEmpty)
            {
                return false;
            }

            int pieceLength = pieceEnd - current;
            if (mapped.Length != pieceLength)
            {
                return false;
            }

            segments.Add(new MarkdownSourceMap.ProjectionSegment(
                renderedCurrent,
                pieceLength,
                SourceStart: mapped.Start - blockSourceSpan.Start,
                SourceLength: mapped.Length));
            current = pieceEnd;
            renderedCurrent += pieceLength;
        }

        return renderedCurrent == renderedStart + renderedLength;
    }

    private static bool ContainsSourceRange(SourceSpan outer, SourceSpan inner)
        => !inner.IsEmpty && inner.Start >= outer.Start && inner.End <= outer.End;

    private static int GetCodeBlockSourceNewlineLength(NewLine newline)
        => newline switch
        {
            NewLine.None => 0,
            NewLine.CarriageReturn or NewLine.LineFeed => 1,
            NewLine.CarriageReturnLineFeed => 2,
            _ => -1,
        };

    private static bool IsExpectedSourceNewline(string source, int offset, NewLine newline)
    {
        int length = GetCodeBlockSourceNewlineLength(newline);
        if (offset < 0 || length <= 0 || offset > source.Length || length > source.Length - offset)
            return false;

        return newline switch
        {
            NewLine.CarriageReturn => source[offset] == '\r',
            NewLine.LineFeed => source[offset] == '\n',
            NewLine.CarriageReturnLineFeed => source[offset] == '\r' && source[offset + 1] == '\n',
            _ => false,
        };
    }
}
