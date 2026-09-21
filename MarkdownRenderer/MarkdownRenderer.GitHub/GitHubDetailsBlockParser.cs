using System;
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
        if (processor.IsCodeIndent ||
            !TryReadOpening(processor.Line.ToString(), out bool isOpen, out string remainder))
        {
            return BlockState.None;
        }

        var block = new GitHubDetailsBlock(this)
        {
            Column = processor.Column,
            Span = new Markdig.Syntax.SourceSpan(processor.Start, processor.Line.End),
            IsOpenByDefault = isOpen,
        };
        if (TryReadSummaryStart(remainder, out string summary, out bool summaryClosed))
        {
            block.HasSummary = true;
            block.IsReadingSummary = !summaryClosed;
            block.SummarySourceStart = processor.Start;
            block.AppendSummaryLine(summary);
        }
        processor.NewBlocks.Push(block);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var details = (GitHubDetailsBlock)block;
        string line = processor.Line.ToString().Trim();
        if (details.IsReadingSummary)
        {
            int closingStart = line.IndexOf("</summary", StringComparison.OrdinalIgnoreCase);
            if (closingStart >= 0)
            {
                details.AppendSummaryLine(line[..closingStart]);
                details.IsReadingSummary = false;
            }
            else
            {
                details.AppendSummaryLine(line);
            }

            details.UpdateSpanEnd(processor.Line.End);
            return BlockState.ContinueDiscard;
        }

        if (StartsWithTag(line, "</details"))
        {
            details.UpdateSpanEnd(processor.Line.End);
            return BlockState.BreakDiscard;
        }

        if (!details.HasSummary &&
            TryReadSummaryStart(line, out string summary, out bool summaryClosed))
        {
            details.HasSummary = true;
            details.IsReadingSummary = !summaryClosed;
            details.SummarySourceStart = processor.Start;
            details.AppendSummaryLine(summary);
            details.UpdateSpanEnd(processor.Line.End);
            return BlockState.ContinueDiscard;
        }

        details.UpdateSpanEnd(processor.Line.End);
        return BlockState.Continue;
    }

    private static bool TryReadOpening(string line, out bool isOpen, out string remainder)
    {
        isOpen = false;
        remainder = string.Empty;
        string trimmed = line.Trim();
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
        remainder = trimmed[(tagEnd + 1)..].Trim();
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
        out bool isClosed)
    {
        summary = string.Empty;
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
        summary = isClosed
            ? line[(openingEnd + 1)..closingStart]
            : line[(openingEnd + 1)..];
        return true;
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

    private static bool StartsWithTag(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Length == prefix.Length ||
            value[prefix.Length] is '>' or ' ' or '\t' or '\r' or '\n';
    }
}
