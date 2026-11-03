using System;
using System.Collections.Generic;
using Markdig.Helpers;
using Markdig.Syntax.Inlines;

namespace MarkdownRenderer;

/// <summary>
/// 0.29-gfm extended-autolink repairs: restores URL suffixes after a closing
/// parenthesis and links bare email addresses inside literal text.
/// </summary>
internal static class Gfm029AutolinkRepair
{
    internal static void RepairUrlSuffixes(ContainerInline container, string source)
    {
        for (Inline? child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is not LinkInline { IsAutoLink: true, IsImage: false } link ||
                link.Url is null ||
                link.FirstChild is not LiteralInline label ||
                link.NextSibling is not LiteralInline suffixLiteral ||
                suffixLiteral.Span.Start != link.Span.End + 1)
            {
                continue;
            }

            int suffixStart = suffixLiteral.Span.Start;
            int suffixEnd = suffixLiteral.Span.End + 1;
            if (suffixStart < 0 || suffixEnd > source.Length || suffixStart >= suffixEnd)
                continue;

            string literalText = suffixLiteral.Content.ToString();
            if (literalText.Length != suffixEnd - suffixStart ||
                !source.AsSpan(suffixStart, literalText.Length).SequenceEqual(literalText.AsSpan()) ||
                source[suffixStart] != ')')
            {
                continue;
            }

            int tokenEnd = suffixStart;
            while (tokenEnd < suffixEnd &&
                   !char.IsWhiteSpace(source[tokenEnd]) &&
                   source[tokenEnd] is not '<' and not '>')
            {
                tokenEnd++;
            }
            while (tokenEnd > suffixStart && source[tokenEnd - 1] is '.' or ',' or ';' or ':' or '!' or '?')
                tokenEnd--;

            bool hasNonPunctuationTail = false;
            for (int index = suffixStart + 1; index < tokenEnd; index++)
            {
                if (char.IsLetterOrDigit(source[index]))
                {
                    hasNonPunctuationTail = true;
                    break;
                }
            }
            if (!hasNonPunctuationTail)
                continue;

            string suffix = source.Substring(suffixStart, tokenEnd - suffixStart);
            link.Url += suffix;
            link.Span = new Markdig.Syntax.SourceSpan(link.Span.Start, tokenEnd - 1);
            link.UrlSpan = link.Span;
            label.Content = new StringSlice(source, label.Span.Start, tokenEnd - 1);
            label.Span = new Markdig.Syntax.SourceSpan(label.Span.Start, tokenEnd - 1);

            if (tokenEnd > suffixLiteral.Span.End)
            {
                suffixLiteral.Remove();
            }
            else
            {
                int consumed = tokenEnd - suffixStart;
                suffixLiteral.Content = new StringSlice(source, tokenEnd, suffixLiteral.Span.End);
                suffixLiteral.Span = new Markdig.Syntax.SourceSpan(tokenEnd, suffixLiteral.Span.End);
                suffixLiteral.Column += consumed;
            }
        }
    }

    internal static void LinkEmailLiterals(ContainerInline container, string source)
    {
        Inline? child = container.FirstChild;
        while (child is not null)
        {
            Inline? next = child.NextSibling;
            if (child is LiteralInline literal)
                ReplaceEmailLiteral(literal, source);
            child = next;
        }
    }

    private static void ReplaceEmailLiteral(LiteralInline literal, string source)
    {
        ReadOnlySpan<char> contentSpan = literal.Content.AsSpan();
        int sourceStart = literal.Span.Start;
        if (sourceStart < 0 ||
            literal.Span.End < sourceStart ||
            literal.Span.End >= source.Length ||
            contentSpan.Length != literal.Span.End - sourceStart + 1 ||
            !source.AsSpan(sourceStart, contentSpan.Length).SequenceEqual(contentSpan) ||
            contentSpan.IndexOf('@') < 0)
        {
            return;
        }

        string content = contentSpan.ToString();
        List<(int Start, int End)> ranges = FindEmailRanges(content);
        ranges.RemoveAll(range =>
        {
            int following = sourceStart + range.End;
            return following < source.Length && source[following] is '-' or '_';
        });
        if (ranges.Count == 0)
            return;

        var replacements = new List<Inline>(ranges.Count * 2 + 1);
        int copiedThrough = 0;
        foreach ((int start, int end) in ranges)
        {
            if (start > copiedThrough)
            {
                replacements.Add(CreateLiteral(
                    source,
                    sourceStart + copiedThrough,
                    sourceStart + start - 1,
                    literal.Line,
                    literal.Column + copiedThrough));
            }

            int absoluteStart = sourceStart + start;
            int absoluteEnd = sourceStart + end - 1;
            string email = content.Substring(start, end - start);
            var span = new Markdig.Syntax.SourceSpan(absoluteStart, absoluteEnd);
            var link = new LinkInline
            {
                Url = "mailto:" + email,
                IsAutoLink = true,
                IsClosed = true,
                Span = span,
                UrlSpan = span,
                Line = literal.Line,
                Column = literal.Column + start,
            };
            link.AppendChild(CreateLiteral(
                source,
                absoluteStart,
                absoluteEnd,
                literal.Line,
                literal.Column + start));
            replacements.Add(link);
            copiedThrough = end;
        }

        if (copiedThrough < content.Length)
        {
            replacements.Add(CreateLiteral(
                source,
                sourceStart + copiedThrough,
                literal.Span.End,
                literal.Line,
                literal.Column + copiedThrough));
        }

        Inline first = replacements[0];
        literal.ReplaceBy(first);
        Inline previous = first;
        for (int index = 1; index < replacements.Count; index++)
        {
            previous.InsertAfter(replacements[index]);
            previous = replacements[index];
        }
    }

    private static List<(int Start, int End)> FindEmailRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int at = text.IndexOf('@', searchFrom);
            if (at < 0)
                break;

            int start = at;
            while (start > 0 && IsEmailLocalCharacter(text[start - 1]))
                start--;
            int end = at + 1;
            while (end < text.Length && IsEmailDomainCharacter(text[end]))
                end++;
            while (end > at + 1 && text[end - 1] == '.')
                end--;

            ReadOnlySpan<char> local = text.AsSpan(start, at - start);
            ReadOnlySpan<char> domain = text.AsSpan(at + 1, end - at - 1);
            if (!local.IsEmpty && IsValidEmailDomain(domain))
            {
                ranges.Add((start, end));
                searchFrom = end;
            }
            else
            {
                searchFrom = at + 1;
            }
        }

        return ranges;
    }

    private static bool IsEmailLocalCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) ||
           ".!#$%&'*+/=?^_`{|}~-".Contains(value, StringComparison.Ordinal);

    private static bool IsEmailDomainCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_';

    private static bool IsValidEmailDomain(ReadOnlySpan<char> domain)
    {
        int labelStart = 0;
        int labelCount = 0;
        for (int index = 0; index <= domain.Length; index++)
        {
            if (index < domain.Length && domain[index] != '.')
                continue;

            ReadOnlySpan<char> label = domain[labelStart..index];
            if (label.IsEmpty ||
                !char.IsAsciiLetterOrDigit(label[0]) ||
                !char.IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }
            foreach (char value in label)
            {
                if (!char.IsAsciiLetterOrDigit(value) && value != '-')
                    return false;
            }

            labelCount++;
            labelStart = index + 1;
        }

        return labelCount >= 2;
    }

    private static LiteralInline CreateLiteral(
        string source,
        int start,
        int end,
        int line,
        int column)
        => new(new StringSlice(source, start, end))
        {
            Span = new Markdig.Syntax.SourceSpan(start, end),
            Line = line,
            Column = column,
            IsClosed = true,
        };
}
