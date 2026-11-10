using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Markdig.Helpers;
using Markdig.Syntax;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Maps an ordinary HtmlBlock's text back to its exact authored source lines,
/// so a block-quote or list prefix that Markdig removed is not mistaken for
/// a contiguous offset.
/// </summary>
internal static class HtmlBlockSourceProjection
{
    internal static bool TryCreateDirectHtmlBlockProjection(
        HtmlBlock htmlBlock,
        string htmlSource,
        string sourceText,
        CancellationToken cancellationToken,
        out MarkdownSourceProjection projection)
    {
        projection = null!;
        IReadOnlyList<StringLine> lines = htmlBlock.Lines.Lines;
        int lineCount = htmlBlock.Lines.Count;
        if (lineCount <= 0 || lineCount > lines.Count)
            return false;

        var builder = new MarkdownSourceProjectionBuilder();
        int localOffset = 0;
        for (int index = 0; index < lineCount; index++)
        {
            if ((index & 0x3f) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            StringLine line = lines[index];
            var slice = line.Slice;
            string lineText = slice.ToString();
            if (!ReferenceEquals(slice.Text, sourceText) ||
                slice.Start < 0 || slice.Length < 0 ||
                slice.Start > sourceText.Length || slice.Length > sourceText.Length - slice.Start ||
                lineText.Length != slice.Length || lineText.Length > htmlSource.Length - localOffset ||
                !lineText.AsSpan().SequenceEqual(htmlSource.AsSpan(localOffset, lineText.Length)) ||
                !sourceText.AsSpan(slice.Start, slice.Length).SequenceEqual(lineText.AsSpan()))
            {
                return false;
            }

            if (lineText.Length > 0)
                builder.AppendDirect(lineText, slice.Start);
            localOffset += lineText.Length;

            if (index + 1 >= lineCount)
                continue;

            StringLine nextLine = lines[index + 1];
            if (nextLine.Slice.Length == 0 && localOffset == htmlSource.Length)
                break;

            int newlineLength = line.NewLine switch
            {
                NewLine.CarriageReturn or NewLine.LineFeed => 1,
                NewLine.CarriageReturnLineFeed => 2,
                _ => 0,
            };
            int sourceNewlineStart = checked(slice.Start + slice.Length);
            if (newlineLength <= 0 || sourceNewlineStart > sourceText.Length ||
                newlineLength > sourceText.Length - sourceNewlineStart ||
                localOffset > htmlSource.Length || newlineLength > htmlSource.Length - localOffset ||
                !IsExpectedSourceNewline(sourceText, sourceNewlineStart, line.NewLine))
            {
                return false;
            }

            ReadOnlySpan<char> htmlNewline = htmlSource.AsSpan(localOffset, newlineLength);
            ReadOnlySpan<char> sourceNewline = sourceText.AsSpan(sourceNewlineStart, newlineLength);
            if (!htmlNewline.SequenceEqual(sourceNewline))
                return false;

            // Ordinary HtmlBlock source preserves its exact line ending. Keep
            // this parser-derived projection byte-for-character; preformatted
            // rendering performs the CR/LF normalization later, where it can
            // retain an atomic mapping back to the full authored newline.
            builder.AppendDirect(htmlSource.Substring(localOffset, newlineLength), sourceNewlineStart);
            localOffset += newlineLength;
        }

        if (localOffset != htmlSource.Length || builder.Length != htmlSource.Length)
            return false;

        projection = builder.Build();
        return true;
    }

    private static bool IsExpectedSourceNewline(string source, int offset, NewLine newline)
    {
        return newline switch
        {
            NewLine.CarriageReturn => source[offset] == '\r',
            NewLine.LineFeed => source[offset] == '\n',
            NewLine.CarriageReturnLineFeed => source[offset] == '\r' && source[offset + 1] == '\n',
            _ => false,
        };
    }
}
