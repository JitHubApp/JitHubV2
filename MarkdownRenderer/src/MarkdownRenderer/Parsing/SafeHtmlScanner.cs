using System;
using System.Threading;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Bounded character-scanning primitives shared by the safe HTML tag reader
/// and tree builder. Every loop polls cancellation at a fixed interval.
/// </summary>
internal static class SafeHtmlScanner
{
    internal static void SkipWhitespace(
        string source,
        ref int cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        while (cursor < limit && char.IsWhiteSpace(source[cursor]))
        {
            CheckCancellation(cancellationToken, cursor);
            cursor++;
        }
    }

    internal static bool IsNameCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or ':';

    internal static bool IsAttributeNameCharacter(char character) =>
        IsNameCharacter(character) || character is '_' or '.';

    internal static bool StartsWith(string source, int start, int limit, string value) =>
        start + value.Length <= limit &&
        source.AsSpan(start, value.Length).Equals(value.AsSpan(), StringComparison.Ordinal);

    internal static int FindCharacter(
        string source,
        char value,
        int start,
        int limit,
        CancellationToken cancellationToken)
    {
        for (int index = start; index < limit; index++)
        {
            CheckCancellation(cancellationToken, index);
            if (source[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    internal static int FindSequence(
        string source,
        string value,
        int start,
        int limit,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        int lastStart = limit - value.Length;
        for (int index = start; index <= lastStart; index++)
        {
            CheckCancellation(cancellationToken, index);
            if (source.AsSpan(index, value.Length).Equals(value.AsSpan(), comparison))
            {
                return index;
            }
        }

        return -1;
    }

    internal static void CheckCancellation(CancellationToken cancellationToken, int index)
    {
        const int CheckInterval = 256;
        if ((index & (CheckInterval - 1)) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
