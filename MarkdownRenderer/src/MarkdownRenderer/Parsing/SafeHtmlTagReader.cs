using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using static MarkdownRenderer.Parsing.SafeHtmlScanner;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Reads one bounded tag token (opening, closing, self-closing, comment or
/// declaration) with its decoded, capped attributes.
/// </summary>
internal static class SafeHtmlTagReader
{
    internal static bool TryReadTag(
        string source,
        int start,
        int limit,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken,
        ref SafeHtmlScanBudget scanBudget,
        out SafeHtmlTag tag,
        out int endExclusive)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tag = default;
        endExclusive = start;
        if (start < 0 || start >= limit || source[start] != '<')
        {
            return false;
        }

        if (StartsWith(source, start, limit, "<!--"))
        {
            tag = ReadComment(source, start, limit, cancellationToken, out endExclusive);
            return true;
        }

        int closeIndex = FindTagClose(
            source,
            start + 1,
            limit,
            limits.MaxTagLength,
            cancellationToken,
            ref scanBudget);
        if (closeIndex < 0)
        {
            return false;
        }

        endExclusive = closeIndex + 1;
        int cursor = start + 1;
        SkipWhitespace(source, ref cursor, closeIndex, cancellationToken);
        if (cursor >= closeIndex)
        {
            return false;
        }

        if (source[cursor] is '!' or '?')
        {
            tag = new SafeHtmlTag(
                SafeHtmlTagKind.Declaration,
                string.Empty,
                EmptyTagAttributes,
                start,
                endExclusive - start);
            return true;
        }

        return TryReadNamedTag(
            source,
            start,
            cursor,
            closeIndex,
            endExclusive,
            limits,
            cancellationToken,
            out tag);
    }

    private static SafeHtmlTag ReadComment(
        string source,
        int start,
        int limit,
        CancellationToken cancellationToken,
        out int endExclusive)
    {
        int close = FindSequence(
            source,
            "-->",
            start + 4,
            limit,
            StringComparison.Ordinal,
            cancellationToken);
        endExclusive = close < 0 ? limit : close + 3;
        return new SafeHtmlTag(
            SafeHtmlTagKind.Comment,
            string.Empty,
            EmptyTagAttributes,
            start,
            endExclusive - start);
    }

    /// <summary>
    /// Reads an opening, closing or self-closing tag whose name starts at
    /// <paramref name="cursor"/> and whose <c>&gt;</c> is at <paramref name="closeIndex"/>.
    /// </summary>
    private static bool TryReadNamedTag(
        string source,
        int start,
        int cursor,
        int closeIndex,
        int endExclusive,
        SafeHtmlParseLimits limits,
        CancellationToken cancellationToken,
        out SafeHtmlTag tag)
    {
        bool closing = source[cursor] == '/';
        if (closing)
        {
            cursor++;
            SkipWhitespace(source, ref cursor, closeIndex, cancellationToken);
        }

        int nameStart = cursor;
        while (cursor < closeIndex && IsNameCharacter(source[cursor]))
        {
            CheckCancellation(cancellationToken, cursor);
            cursor++;
        }

        if (cursor == nameStart)
        {
            tag = default;
            return false;
        }

        string name = source.Substring(nameStart, cursor - nameStart).ToLowerInvariant();
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool selfClosing = false;

        if (!closing)
        {
            ParseAttributes(
                source,
                ref cursor,
                closeIndex,
                attributes,
                limits.MaxAttributeCount,
                limits.MaxAttributeValueLength,
                cancellationToken,
                out selfClosing);
        }

        tag = new SafeHtmlTag(
            closing
                ? SafeHtmlTagKind.Closing
                : selfClosing || SafeHtmlElementCategories.IsVoidElement(name)
                    ? SafeHtmlTagKind.SelfClosing
                    : SafeHtmlTagKind.Opening,
            name,
            attributes,
            start,
            endExclusive - start);
        return true;
    }

    private static IReadOnlyDictionary<string, string> EmptyTagAttributes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static int FindTagClose(
        string source,
        int start,
        int limit,
        int maxTagLength,
        CancellationToken cancellationToken,
        ref SafeHtmlScanBudget scanBudget)
    {
        char quote = '\0';
        int max = (int)Math.Min(limit, (long)start + Math.Max(0, maxTagLength));
        for (int index = start; index < max; index++)
        {
            CheckCancellation(cancellationToken, index);
            if (!scanBudget.TryConsume())
            {
                return -1;
            }

            char character = source[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static void ParseAttributes(
        string source,
        ref int cursor,
        int closeIndex,
        Dictionary<string, string> attributes,
        int maxAttributeCount,
        int maxAttributeValueLength,
        CancellationToken cancellationToken,
        out bool selfClosing)
    {
        selfClosing = false;
        while (cursor < closeIndex)
        {
            CheckCancellation(cancellationToken, cursor);
            SkipWhitespace(source, ref cursor, closeIndex, cancellationToken);
            if (cursor >= closeIndex)
            {
                return;
            }

            if (source[cursor] == '/')
            {
                selfClosing = true;
                cursor++;
                continue;
            }

            int nameStart = cursor;
            while (cursor < closeIndex && IsAttributeNameCharacter(source[cursor]))
            {
                CheckCancellation(cancellationToken, cursor);
                cursor++;
            }

            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            string name = source.Substring(nameStart, cursor - nameStart).ToLowerInvariant();
            SkipWhitespace(source, ref cursor, closeIndex, cancellationToken);
            string value = string.Empty;
            if (cursor < closeIndex && source[cursor] == '=')
            {
                cursor++;
                SkipWhitespace(source, ref cursor, closeIndex, cancellationToken);
                value = ReadAttributeValue(
                    source,
                    ref cursor,
                    closeIndex,
                    maxAttributeValueLength,
                    cancellationToken);
            }

            if (attributes.Count < maxAttributeCount && !attributes.ContainsKey(name))
            {
                attributes[name] = WebUtility.HtmlDecode(value) ?? string.Empty;
            }
        }
    }

    private static string ReadAttributeValue(
        string source,
        ref int cursor,
        int closeIndex,
        int maxAttributeValueLength,
        CancellationToken cancellationToken)
    {
        if (cursor >= closeIndex)
        {
            return string.Empty;
        }

        int valueStart;
        int valueLength;
        if (source[cursor] is '\'' or '"')
        {
            char quote = source[cursor++];
            valueStart = cursor;
            while (cursor < closeIndex && source[cursor] != quote)
            {
                CheckCancellation(cancellationToken, cursor);
                cursor++;
            }

            valueLength = cursor - valueStart;
            if (cursor < closeIndex)
            {
                cursor++;
            }
        }
        else
        {
            valueStart = cursor;
            while (cursor < closeIndex &&
                   !char.IsWhiteSpace(source[cursor]) &&
                   source[cursor] != '>' &&
                   !(source[cursor] == '/' && cursor + 1 == closeIndex))
            {
                CheckCancellation(cancellationToken, cursor);
                cursor++;
            }

            valueLength = cursor - valueStart;
        }

        valueLength = Math.Min(valueLength, maxAttributeValueLength);
        return valueLength <= 0 ? string.Empty : source.Substring(valueStart, valueLength);
    }
}
