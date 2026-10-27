using System;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace MarkdownRenderer.GitHub;

/// <summary>
/// Parses standalone GitHub-style details elements as Markdown containers rather
/// than opaque CommonMark HTML blocks.
/// </summary>
internal sealed class GitHubDetailsBlockParser : BlockParser
{
    internal GitHubDetailsBlockParser()
    {
        OpeningCharacters = ['<'];
    }

    public override BlockState TryOpen(BlockProcessor processor)
    {
        string rawLine = processor.Line.ToString();
        if (processor.IsCodeIndent ||
            !TryReadOpening(rawLine, out bool isOpen, out string remainder, out int remainderStart))
        {
            return BlockState.None;
        }

        var block = new GitHubDetailsBlock(this)
        {
            Column = processor.Column,
            Span = new Markdig.Syntax.SourceSpan(processor.Start, processor.Line.End),
            IsOpenByDefault = isOpen,
        };
        if (TryReadSummaryStart(remainder, out string summary, out int summaryTextStart, out bool summaryClosed))
        {
            block.HasSummary = true;
            block.IsReadingSummary = !summaryClosed;
            block.SummarySourceStart = checked(processor.Line.Start + remainderStart + summaryTextStart);
            block.AppendSummaryLine(
                summary,
                block.SummarySourceStart,
                new SourceSpan(processor.Line.Start, 0));
        }

        // TryContinue runs outer-to-inner. Keep each open details ancestor aware
        // of descendants so it can leave a child closing line for that child.
        for (ContainerBlock? ancestor = processor.GetCurrentContainerOpened();
             ancestor is not null;
             ancestor = ancestor.Parent)
        {
            if (ancestor is GitHubDetailsBlock detailsAncestor)
                detailsAncestor.AddOpenDetailsDescendant();
        }

        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var details = (GitHubDetailsBlock)block;
        StringSlice sourceLine = processor.Line;
        string rawLine = sourceLine.ToString();
        int leadingWhitespace = rawLine.Length - rawLine.TrimStart().Length;
        string line = rawLine.Trim();
        int lineSourceStart = checked(sourceLine.Start + leadingWhitespace);
        SourceSpan precedingLineBreak = GetPrecedingLineBreak(sourceLine);
        if (details.IsReadingSummary)
        {
            int closingStart = line.IndexOf("</summary", StringComparison.OrdinalIgnoreCase);
            if (closingStart >= 0)
            {
                details.AppendSummaryLine(line[..closingStart], lineSourceStart, precedingLineBreak);
                details.IsReadingSummary = false;
            }
            else
            {
                details.AppendSummaryLine(line, lineSourceStart, precedingLineBreak);
            }

            details.UpdateSpanEnd(processor.Line.End);
            return BlockState.ContinueDiscard;
        }

        if (IsDetailsClosingLine(line))
        {
            // A closing-looking source line inside a Markdown code block is
            // content, not a details delimiter. Fenced/active code is already
            // on the processor's open-block stack. For a new indented code
            // block, rewind the indentation scan so Markdig's block parser sees
            // the original four-space prefix after this container continues.
            if (processor.CurrentBlock is CodeBlock)
            {
                details.UpdateSpanEnd(processor.Line.End);
                return ContinuePreservingIndent(processor);
            }
            if (processor.IsCodeIndent)
            {
                details.UpdateSpanEnd(processor.Line.End);
                processor.GoToColumn(processor.ColumnBeforeIndent);
                return BlockState.Continue;
            }

            // Markdig visits the outer details parser before its children. Let
            // nested details containers see their own close before consuming it.
            if (details.OpenDetailsDescendantCount > 0)
                return ContinuePreservingIndent(processor);

            details.UpdateSpanEnd(processor.Line.End);
            return BlockState.BreakDiscard;
        }

        if (!details.HasSummary &&
            TryReadSummaryStart(line, out string summary, out int summaryTextStart, out bool summaryClosed))
        {
            details.HasSummary = true;
            details.IsReadingSummary = !summaryClosed;
            details.SummarySourceStart = checked(lineSourceStart + summaryTextStart);
            details.AppendSummaryLine(summary, details.SummarySourceStart, precedingLineBreak);
            details.UpdateSpanEnd(processor.Line.End);
            return BlockState.ContinueDiscard;
        }

        details.UpdateSpanEnd(processor.Line.End);
        return ContinuePreservingIndent(processor);
    }

    public override bool Close(BlockProcessor processor, Block block)
    {
        for (ContainerBlock? ancestor = block.Parent;
             ancestor is not null;
             ancestor = ancestor.Parent)
        {
            if (ancestor is GitHubDetailsBlock detailsAncestor)
                detailsAncestor.RemoveOpenDetailsDescendant();
        }

        return true;
    }

    private static bool TryReadOpening(
        string line,
        out bool isOpen,
        out string remainder,
        out int remainderStart)
    {
        isOpen = false;
        remainder = string.Empty;
        remainderStart = 0;
        int trimStart = line.Length - line.TrimStart().Length;
        int trimEnd = line.TrimEnd().Length;
        string trimmed = trimEnd > trimStart
            ? line.Substring(trimStart, trimEnd - trimStart)
            : string.Empty;
        if (!StartsWithTag(trimmed, "<details"))
        {
            return false;
        }

        int tagEnd = trimmed.IndexOf('>');
        if (tagEnd < "<details".Length)
        {
            return false;
        }

        string openingTag = trimmed[..(tagEnd + 1)];
        isOpen = HasBooleanAttribute(openingTag, "open");
        remainderStart = trimStart + tagEnd + 1;
        int remainderEnd = line.Length;
        while (remainderStart < remainderEnd && char.IsWhiteSpace(line[remainderStart]))
            remainderStart++;
        while (remainderEnd > remainderStart && char.IsWhiteSpace(line[remainderEnd - 1]))
            remainderEnd--;
        remainder = line[remainderStart..remainderEnd];
        if (remainder.Contains("</details", StringComparison.OrdinalIgnoreCase))
        {
            // Let the safe-HTML renderer handle compact single-line elements;
            // this block parser is intentionally for Markdown-bearing containers.
            return false;
        }
        return true;
    }

    private static bool TryReadSummaryStart(
        string line,
        out string summary,
        out int summaryStart,
        out bool isClosed)
    {
        summary = string.Empty;
        summaryStart = 0;
        isClosed = false;
        if (!line.StartsWith("<summary", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int openingEnd = line.IndexOf('>');
        int closingStart = line.IndexOf("</summary", StringComparison.OrdinalIgnoreCase);
        if (openingEnd < 0)
        {
            return false;
        }

        isClosed = closingStart > openingEnd;
        summaryStart = openingEnd + 1;
        summary = isClosed ? line[summaryStart..closingStart] : line[summaryStart..];
        return true;
    }

    private static SourceSpan GetPrecedingLineBreak(StringSlice line)
    {
        string? source = line.Text;
        int before = Math.Min(line.Start, source?.Length ?? 0);
        if (source is null || before <= 0)
            return new SourceSpan(Math.Max(0, before), 0);

        int lineFeed = source.LastIndexOf('\n', before - 1);
        int carriageReturn = source.LastIndexOf('\r', before - 1);
        int lineBreak = Math.Max(lineFeed, carriageReturn);
        if (lineBreak < 0)
            return new SourceSpan(before, 0);

        // LastIndexOf('\n') selects the LF half of CRLF. The projected
        // summary separator must retain both authored code units so source
        // mapping never points into only half of the line ending.
        if (source[lineBreak] == '\n' && lineBreak > 0 && source[lineBreak - 1] == '\r')
            lineBreak--;

        int length = source[lineBreak] == '\r' &&
            lineBreak + 1 < before &&
            source[lineBreak + 1] == '\n'
                ? 2
                : 1;
        return new SourceSpan(lineBreak, length);
    }

    private static bool HasBooleanAttribute(string tag, string attribute)
    {
        ReadOnlySpan<char> remaining = tag.AsSpan("<details".Length, tag.Length - "<details".Length - 1);
        foreach (Range range in remaining.SplitAny(" \t\r\n"))
        {
            ReadOnlySpan<char> token = remaining[range];
            int equals = token.IndexOf('=');
            if (equals >= 0)
            {
                token = token[..equals];
            }

            if (token.Equals(attribute, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDetailsClosingLine(string line)
    {
        // Preserve the existing behavior for a standalone closing tag. GitHub
        // READMEs commonly close inline formatting before the details element,
        // e.g. `</b></details>`; treating that line as body content leaves each
        // following sibling details block nested in the previous one.
        if (StartsWithTag(line, "</details"))
            return true;

        int detailsTagStart = line.IndexOf("</details", StringComparison.OrdinalIgnoreCase);
        if (detailsTagStart < 0 ||
            !StartsWithTag(line[detailsTagStart..], "</details") ||
            !IsOnlyClosingHtmlTags(line.AsSpan(0, detailsTagStart)))
        {
            return false;
        }

        int detailsTagEnd = line.IndexOf('>', detailsTagStart);
        return detailsTagEnd >= 0 && string.IsNullOrWhiteSpace(line[(detailsTagEnd + 1)..]);
    }

    private static bool IsOnlyClosingHtmlTags(ReadOnlySpan<char> prefix)
    {
        int index = 0;
        while (index < prefix.Length)
        {
            while (index < prefix.Length && char.IsWhiteSpace(prefix[index]))
                index++;

            if (index == prefix.Length)
                return true;
            if (prefix[index] != '<' || index + 2 >= prefix.Length || prefix[index + 1] != '/')
                return false;

            index += 2;
            if (!char.IsAsciiLetter(prefix[index]))
                return false;
            index++;
            while (index < prefix.Length && IsHtmlTagNameCharacter(prefix[index]))
                index++;
            while (index < prefix.Length && char.IsWhiteSpace(prefix[index]))
                index++;
            if (index >= prefix.Length || prefix[index] != '>')
                return false;
            index++;
        }

        return true;
    }

    private static bool IsHtmlTagNameCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '-' or ':' or '_';

    private static bool StartsWithTag(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Length == prefix.Length ||
            value[prefix.Length] is '>' or ' ' or '\t' or '\r' or '\n';
    }

    private static BlockState ContinuePreservingIndent(BlockProcessor processor)
    {
        // TryContinue runs before child containers. Markdig has already parsed
        // this line's indentation; leaving Column at the end of that scan makes
        // RestartIndent treat it as a new baseline and hides list indentation
        // from descendants. Rewind only to this parser's owned prefix so nested
        // lists, block quotes, and code still see the remaining source.
        processor.GoToColumn(processor.ColumnBeforeIndent);
        return BlockState.Continue;
    }
}
