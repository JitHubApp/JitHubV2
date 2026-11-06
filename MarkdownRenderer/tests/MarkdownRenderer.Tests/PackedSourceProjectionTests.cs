using System.Net;
using MarkdownRenderer;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PackedSourceProjectionTests
{
    private const int MaximumRenderedLength = 16 * 1024;
    private const int MaximumSourceLength = 32 * 1024;

    public static IEnumerable<object[]> ProjectionCases()
    {
        const string linkSource = "[alpha](https://example.test)";
        yield return [linkSource, new SourceSpan(0, linkSource.Length), "alpha"];

        const string entitySource = "A&amp;B";
        yield return [entitySource, new SourceSpan(0, entitySource.Length), "A&B"];

        const string escapedSource = @"left \* right";
        yield return [escapedSource, new SourceSpan(0, escapedSource.Length), "left * right"];

        const string shortcodeSource = "before :rocket: after";
        yield return [shortcodeSource, new SourceSpan(0, shortcodeSource.Length), "before 🚀 after"];

        const string customScalarSource = "host emoji";
        yield return [customScalarSource, new SourceSpan(0, customScalarSource.Length), "🚀"];

        const string supplementarySource = "prefix :rocket: suffix";
        yield return [supplementarySource, new SourceSpan(7, 8), "🚀"];

        const string alreadyExact = "unchanged";
        yield return [alreadyExact, new SourceSpan(0, alreadyExact.Length), alreadyExact];

        const string noMapping = "source";
        yield return [noMapping, new SourceSpan(0, noMapping.Length), "two scalars"];

        const string sourceWithPrefix = "prefix [alpha](url) suffix";
        int prefixStart = sourceWithPrefix.IndexOf('[', StringComparison.Ordinal);
        int spanLength = "[alpha](url)".Length;
        yield return [sourceWithPrefix, new SourceSpan(prefixStart, spanLength), "alpha"];
    }

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void PackedProjectionMatchesIndependentLegacyMap(
        string source,
        SourceSpan span,
        string rendered)
    {
        AssertMatchesLegacy(source, span, rendered);
    }

    [Fact]
    public void MaximumSpanAndRenderedLengthsPreserveThe32768Endpoint()
    {
        // The projection is span-relative. The final rendered unit maps through
        // source-only syntax to the exact, inclusive 32,768 endpoint, which is
        // the largest value that must fit in one packed 16-bit half.
        string source = "[" + new string('a', MaximumRenderedLength - 1) + "]" +
            new string('z', MaximumSourceLength - MaximumRenderedLength - 1);
        string rendered = new string('a', MaximumRenderedLength - 1) + "z";
        Assert.Equal(MaximumSourceLength, source.Length);
        Assert.Equal(MaximumRenderedLength, rendered.Length);

        MarkdownSourceMap.SourceProjection actual = Assert.IsType<MarkdownSourceMap.SourceProjection>(
            MarkdownSourceMap.SourceProjection.TryCreate(
                source,
                new SourceSpan(0, source.Length),
                rendered));
        LegacyProjection expected = Assert.IsType<LegacyProjection>(
            LegacyProjection.TryCreate(source, new SourceSpan(0, source.Length), rendered));

        Assert.Equal(MaximumSourceLength, actual.ProjectEnd(rendered.Length));
        AssertEquivalent(expected, actual, rendered.Length);
    }

    [Fact]
    public void OversizedAndInvalidRangesFailClosedLikeTheLegacyImplementation()
    {
        string sourceOverLimit = new string('a', MaximumSourceLength + 1);
        AssertMatchesLegacy(
            sourceOverLimit,
            new SourceSpan(0, sourceOverLimit.Length),
            "different");

        string renderedOverLimit = "\\*" + new string('a', MaximumRenderedLength);
        string renderedOverLimitText = "*" + new string('a', MaximumRenderedLength);
        Assert.Equal(MaximumRenderedLength + 1, renderedOverLimitText.Length);
        AssertMatchesLegacy(
            renderedOverLimit,
            new SourceSpan(0, renderedOverLimit.Length),
            renderedOverLimitText);

        const string smallSource = "A&amp;B";
        AssertMatchesLegacy(smallSource, new SourceSpan(-1, 1), "A");
        AssertMatchesLegacy(smallSource, new SourceSpan(0, -1), "A");
        AssertMatchesLegacy(smallSource, new SourceSpan(smallSource.Length, 1), "A");
    }

    [Fact]
    public async Task PackedProjectionSupportsConcurrentImmutableReaders()
    {
        string source = "[" + new string('x', 3_000) + "] &amp; \\* :rocket:";
        string rendered = new string('x', 3_000) + " & * 🚀";
        SourceSpan span = new(0, source.Length);
        MarkdownSourceMap.SourceProjection actual = Assert.IsType<MarkdownSourceMap.SourceProjection>(
            MarkdownSourceMap.SourceProjection.TryCreate(source, span, rendered));
        LegacyProjection expected = Assert.IsType<LegacyProjection>(
            LegacyProjection.TryCreate(source, span, rendered));

        Task[] readers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => AssertEquivalent(expected, actual, rendered.Length)))
            .ToArray();
        await Task.WhenAll(readers);
    }

    private static void AssertMatchesLegacy(string source, SourceSpan span, string rendered)
    {
        LegacyProjection? expected = LegacyProjection.TryCreate(source, span, rendered);
        MarkdownSourceMap.SourceProjection? actual =
            MarkdownSourceMap.SourceProjection.TryCreate(source, span, rendered);

        Assert.Equal(expected is null, actual is null);
        if (expected is null || actual is null)
            return;

        AssertEquivalent(expected, actual, rendered.Length);
    }

    private static void AssertEquivalent(
        LegacyProjection expected,
        MarkdownSourceMap.SourceProjection actual,
        int renderedLength)
    {
        for (int offset = -1; offset <= renderedLength + 1; offset++)
        {
            Assert.Equal(expected.ProjectStart(offset), actual.ProjectStart(offset));
            Assert.Equal(expected.ProjectEnd(offset), actual.ProjectEnd(offset));
        }
    }

    /// <summary>
    /// Independent test oracle copied from the previous two-int-array
    /// representation. It intentionally does not inspect the packed storage.
    /// </summary>
    private sealed class LegacyProjection
    {
        private const int MaxRenderedLength = 16 * 1024;
        private const int MaxSourceLength = 32 * 1024;
        private const int MaxEntityLength = 64;
        private const int MaxEmojiShortcodeLength = 64;

        private readonly int[] _starts;
        private readonly int[] _ends;

        private LegacyProjection(int[] starts, int[] ends)
        {
            _starts = starts;
            _ends = ends;
        }

        internal int ProjectStart(int renderedOffset)
        {
            if (_starts.Length == 0 || renderedOffset <= 0)
                return 0;
            if (renderedOffset >= _starts.Length)
                return _ends[^1];
            return _starts[renderedOffset];
        }

        internal int ProjectEnd(int renderedOffset)
        {
            if (_ends.Length == 0 || renderedOffset <= 0)
                return 0;
            if (renderedOffset >= _ends.Length)
                return _ends[^1];
            return _ends[renderedOffset - 1];
        }

        internal static LegacyProjection? TryCreate(string sourceText, SourceSpan span, string renderedText)
        {
            if (string.IsNullOrEmpty(renderedText) ||
                span.Start < 0 ||
                span.Length <= 0 ||
                span.End > sourceText.Length ||
                renderedText.Length > MaxRenderedLength ||
                span.Length > MaxSourceLength)
            {
                return null;
            }

            ReadOnlySpan<char> source = sourceText.AsSpan(span.Start, span.Length);
            if (source.SequenceEqual(renderedText.AsSpan()))
                return null;

            var starts = new int[renderedText.Length];
            var ends = new int[renderedText.Length];
            int sourceCursor = 0;
            int renderedCursor = 0;

            while (renderedCursor < renderedText.Length)
            {
                if (!TryFindNextEmission(source, sourceCursor, renderedText, renderedCursor, out Emission emission))
                {
                    if (renderedCursor == 0 && GetScalarLength(renderedText, 0) == renderedText.Length)
                    {
                        Array.Fill(starts, 0);
                        Array.Fill(ends, source.Length);
                        return new LegacyProjection(starts, ends);
                    }

                    return null;
                }

                for (int index = 0; index < emission.RenderedLength; index++)
                {
                    starts[renderedCursor + index] = emission.SourceStart;
                    ends[renderedCursor + index] = emission.SourceEnd;
                }

                sourceCursor = emission.SourceEnd;
                renderedCursor += emission.RenderedLength;
            }

            starts[0] = 0;
            ends[^1] = source.Length;

            if (!IsValidProjection(starts, ends, source.Length))
                return null;

            return new LegacyProjection(starts, ends);
        }

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

            int searchLength = System.Math.Min(MaxEntityLength, source.Length - sourceIndex);
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

            int maximumEnd = System.Math.Min(source.Length, sourceIndex + MaxEmojiShortcodeLength);
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

        private static int GetScalarLength(string value, int index) =>
            index + 1 < value.Length &&
            char.IsHighSurrogate(value[index]) &&
            char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;

        private static bool IsValidProjection(int[] starts, int[] ends, int sourceLength)
        {
            int previousStart = -1;
            int previousEnd = -1;
            for (int index = 0; index < starts.Length; index++)
            {
                if (starts[index] < 0 ||
                    ends[index] < starts[index] ||
                    ends[index] > sourceLength ||
                    starts[index] < previousStart ||
                    ends[index] < previousEnd)
                {
                    return false;
                }

                previousStart = starts[index];
                previousEnd = ends[index];
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
}
