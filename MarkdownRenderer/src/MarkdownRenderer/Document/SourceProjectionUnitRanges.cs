using System;
using System.Net;

namespace MarkdownRenderer.Document;

/// <summary>
/// Builds the legacy per-code-unit source ranges for a transformed inline
/// run, aligning rendered text with escapes, entities, emoji shortcodes and
/// source-only delimiters. Each range packs two span-relative offsets in one
/// 32-bit word.
/// </summary>
internal static class SourceProjectionUnitRanges
{
    private const int MaxRenderedLength = 16 * 1024;
    private const int MaxSourceLength = 32 * 1024;
    private const int MaxEntityLength = 64;
    private const int MaxEmojiShortcodeLength = 64;

    /// <summary>
    /// Returns the packed range for each rendered code unit, or null when the
    /// run is identical to its source, over the projection limits, or cannot be
    /// aligned exactly.
    /// </summary>
    internal static uint[]? TryBuild(string sourceText, SourceSpan span, string renderedText)
    {
        if (string.IsNullOrEmpty(renderedText) ||
            span.Start < 0 ||
            span.Length <= 0 ||
            span.End > sourceText.Length ||
            renderedText.Length > MaxRenderedLength ||
            span.Length > MaxSourceLength ||
            span.Length > ushort.MaxValue)
        {
            return null;
        }

        ReadOnlySpan<char> source = sourceText.AsSpan(span.Start, span.Length);
        if (source.SequenceEqual(renderedText.AsSpan()))
            return null;

        var ranges = new uint[renderedText.Length];
        int sourceCursor = 0;
        int renderedCursor = 0;

        while (renderedCursor < renderedText.Length)
        {
            if (!TryFindNextEmission(source, sourceCursor, renderedText, renderedCursor, out Emission emission))
            {
                // A single visual scalar is intrinsically atomic even when its
                // producer is host-defined (for example a custom emoji map).
                if (renderedCursor == 0 && GetScalarLength(renderedText, 0) == renderedText.Length)
                {
                    Array.Fill(ranges, PackRange(0, source.Length));
                    return ranges;
                }

                return null;
            }

            // The span limit above makes these casts exact. Keep this local
            // guard too, so any future change to emission/bounds fails closed.
            if ((uint)emission.SourceStart > ushort.MaxValue ||
                (uint)emission.SourceEnd > ushort.MaxValue)
            {
                return null;
            }

            uint packedRange = PackRange(emission.SourceStart, emission.SourceEnd);
            for (int index = 0; index < emission.RenderedLength; index++)
            {
                ranges[renderedCursor + index] = packedRange;
            }

            sourceCursor = emission.SourceEnd;
            renderedCursor += emission.RenderedLength;
        }

        // Source-only opening and closing syntax belongs to the corresponding
        // edge selection, while interior selections remain delimiter-free.
        ranges[0] = PackRange(0, GetRangeEnd(ranges[0]));
        ranges[^1] = PackRange(GetRangeStart(ranges[^1]), source.Length);

        if (!IsValidProjection(ranges, source.Length))
            return null;

        return ranges;
    }

    private static uint PackRange(int sourceStart, int sourceEnd) =>
        (uint)sourceStart | ((uint)sourceEnd << 16);

    internal static int GetRangeStart(uint packedRange) =>
        (int)(packedRange & ushort.MaxValue);

    internal static int GetRangeEnd(uint packedRange) =>
        (int)(packedRange >> 16);

    private static bool TryFindNextEmission(
        ReadOnlySpan<char> source,
        int sourceCursor,
        string rendered,
        int renderedCursor,
        out Emission emission)
    {
        for (int sourceIndex = sourceCursor; sourceIndex < source.Length; sourceIndex++)
        {
            if (TryMatchEntity(source, sourceIndex, rendered, renderedCursor, out emission) ||
                TryMatchEscape(source, sourceIndex, rendered, renderedCursor, out emission) ||
                TryMatchEmojiShortcode(source, sourceIndex, rendered, renderedCursor, out emission))
            {
                return true;
            }

            if (source[sourceIndex] == rendered[renderedCursor])
            {
                emission = new Emission(sourceIndex, sourceIndex + 1, 1);
                return true;
            }
        }

        emission = default;
        return false;
    }

    private static bool TryMatchEscape(
        ReadOnlySpan<char> source,
        int sourceIndex,
        string rendered,
        int renderedCursor,
        out Emission emission)
    {
        if (source[sourceIndex] == '\\' &&
            sourceIndex + 1 < source.Length &&
            source[sourceIndex + 1] == rendered[renderedCursor])
        {
            emission = new Emission(sourceIndex, sourceIndex + 2, 1);
            return true;
        }

        emission = default;
        return false;
    }

    private static bool TryMatchEntity(
        ReadOnlySpan<char> source,
        int sourceIndex,
        string rendered,
        int renderedCursor,
        out Emission emission)
    {
        if (source[sourceIndex] != '&')
        {
            emission = default;
            return false;
        }

        int searchLength = Math.Min(MaxEntityLength, source.Length - sourceIndex);
        int relativeEnd = source.Slice(sourceIndex, searchLength).IndexOf(';');
        if (relativeEnd <= 1)
        {
            emission = default;
            return false;
        }

        int sourceLength = relativeEnd + 1;
        string encoded = source.Slice(sourceIndex, sourceLength).ToString();
        string decoded = WebUtility.HtmlDecode(encoded);
        if (decoded.Length == 0 ||
            string.Equals(decoded, encoded, StringComparison.Ordinal) ||
            renderedCursor + decoded.Length > rendered.Length ||
            !rendered.AsSpan(renderedCursor, decoded.Length).SequenceEqual(decoded.AsSpan()))
        {
            emission = default;
            return false;
        }

        emission = new Emission(sourceIndex, sourceIndex + sourceLength, decoded.Length);
        return true;
    }

    private static bool TryMatchEmojiShortcode(
        ReadOnlySpan<char> source,
        int sourceIndex,
        string rendered,
        int renderedCursor,
        out Emission emission)
    {
        if (source[sourceIndex] != ':')
        {
            emission = default;
            return false;
        }

        int scalarLength = GetScalarLength(rendered, renderedCursor);
        int scalarValue = scalarLength == 2
            ? char.ConvertToUtf32(rendered[renderedCursor], rendered[renderedCursor + 1])
            : rendered[renderedCursor];
        if (scalarValue <= 0x7F)
        {
            emission = default;
            return false;
        }

        int maximumEnd = Math.Min(source.Length, sourceIndex + MaxEmojiShortcodeLength);
        int end = sourceIndex + 1;
        while (end < maximumEnd && source[end] != ':')
        {
            char value = source[end];
            if (!(char.IsAsciiLetterOrDigit(value) || value is '_' or '+' or '-'))
            {
                emission = default;
                return false;
            }

            end++;
        }

        if (end <= sourceIndex + 1 || end >= maximumEnd || source[end] != ':')
        {
            emission = default;
            return false;
        }

        emission = new Emission(sourceIndex, end + 1, scalarLength);
        return true;
    }

    private static int GetScalarLength(string value, int index)
        => index + 1 < value.Length &&
           char.IsHighSurrogate(value[index]) &&
           char.IsLowSurrogate(value[index + 1])
            ? 2
            : 1;

    private static bool IsValidProjection(uint[] ranges, int sourceLength)
    {
        int previousStart = -1;
        int previousEnd = -1;
        for (int index = 0; index < ranges.Length; index++)
        {
            uint packedRange = ranges[index];
            int start = GetRangeStart(packedRange);
            int end = GetRangeEnd(packedRange);
            if (end < start ||
                end > sourceLength ||
                start < previousStart ||
                end < previousEnd)
            {
                return false;
            }

            previousStart = start;
            previousEnd = end;
        }

        return true;
    }

    private readonly struct Emission(int sourceStart, int sourceEnd, int renderedLength)
    {
        internal readonly int SourceStart = sourceStart;

        internal readonly int SourceEnd = sourceEnd;

        internal readonly int RenderedLength = renderedLength;
    }
}
