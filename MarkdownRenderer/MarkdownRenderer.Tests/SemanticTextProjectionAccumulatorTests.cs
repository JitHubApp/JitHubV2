using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SemanticTextProjectionAccumulatorTests
{
    private readonly ITestOutputHelper _output;

    public SemanticTextProjectionAccumulatorTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void SourceSlicesAndStructuralCharacters_PreserveExactUtf16()
    {
        string prefix = new('P', 8192);
        string source = "A😀e\u0301שלום\n\ud800Z";
        var accumulator = new SemanticTextProjectionAccumulator();
        accumulator.AppendSlice(prefix, 0, prefix.Length);
        accumulator.AppendSlice(source, 0, 3);
        accumulator.Append('\uFFFC');
        accumulator.AppendSlice(source, 3, source.Length - 3);

        string expected = prefix + source[..3] + "\uFFFC" + source[3..];
        string actual = accumulator.GetText();

        Assert.Equal(expected.Length, accumulator.Length);
        Assert.Equal(expected, actual);
        Assert.Equal(HashUtf16(expected), HashUtf16(actual));
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], accumulator[index]);
    }

    [Fact]
    public void ExactExpectedProjection_IsReusedAcrossSlicesAndTrailingNewline()
    {
        string expected = "Heading\nשלום 😀";
        var accumulator = new SemanticTextProjectionAccumulator(expected);

        accumulator.AppendSlice(expected, 0, 3);
        accumulator.AppendSlice(expected, 3, 5);
        accumulator.AppendSlice(expected, 8, expected.Length - 8);

        Assert.Same(expected, accumulator.GetText());
        Assert.Equal(HashUtf16(expected), HashUtf16(accumulator.GetText()));
    }

    [Fact]
    public void ExpectedPrefixMismatch_ProducesNewExactProjection()
    {
        string expected = "shared-prefix-old";
        string current = "shared-prefix-new";
        var accumulator = new SemanticTextProjectionAccumulator(expected);

        accumulator.AppendSlice(current, 0, 14);
        accumulator.AppendSlice(current, 14, current.Length - 14);

        string actual = accumulator.GetText();
        Assert.NotSame(expected, actual);
        Assert.Equal(current, actual);
        Assert.Equal(HashUtf16(current), HashUtf16(actual));
    }

    [Fact]
    public void ShrinkingProjection_TrimsSourceAndRepeatedSegmentsExactly()
    {
        string prefix = new('p', 8192);
        var accumulator = new SemanticTextProjectionAccumulator();
        accumulator.AppendSlice(prefix, 0, prefix.Length);
        accumulator.Append("alpha");
        accumulator.Append(' ');
        accumulator.Append("beta");
        accumulator.Append('\n');
        accumulator.Append('\n');
        accumulator.Length -= 1;
        accumulator.Length = prefix.Length + 8;

        Assert.Equal(prefix + "alpha be", accumulator.GetText());
    }

    [Fact]
    public void PromotedProjection_MixedAppendShrinkAndReappendMatchesStringBuilder()
    {
        string prefix = new('x', 8192);
        const string unicode = "😀e\u0301שלום";
        const string span = "|span\n";
        const string tail = "tail-end";
        var accumulator = new SemanticTextProjectionAccumulator();
        var expected = new StringBuilder();

        accumulator.AppendSlice(prefix, 0, prefix.Length);
        expected.Append(prefix);
        accumulator.AppendSlice(unicode, 0, 1);
        expected.Append(unicode.AsSpan(0, 1));
        accumulator.AppendSlice(unicode, 1, unicode.Length - 1);
        expected.Append(unicode.AsSpan(1));
        accumulator.Append('\uFFFC');
        expected.Append('\uFFFC');
        accumulator.Append(span.AsSpan());
        expected.Append(span.AsSpan());
        accumulator.AppendSlice(tail, 0, tail.Length);
        expected.Append(tail);

        accumulator.Length -= 3;
        expected.Length -= 3;
        accumulator.AppendSlice("re-appended", 0, 11);
        expected.Append("re-appended");

        string actual = accumulator.GetText();
        Assert.Equal(expected.ToString(), actual);
        Assert.Equal(HashUtf16(expected.ToString()), HashUtf16(actual));
    }

    [Fact]
    public void AlternatingTinySlicesAndCharacters_FallsBackWithoutChangingText()
    {
        const int pairs = 16384;
        const string source = "xy";
        var accumulator = new SemanticTextProjectionAccumulator();
        var expected = new StringBuilder(pairs * 2);

        for (int index = 0; index < pairs; index++)
        {
            char sourceCharacter = source[index & 1];
            char structuralCharacter = (index & 1) == 0 ? '|' : ':';
            accumulator.AppendSlice(source, index & 1, 1);
            accumulator.Append(structuralCharacter);
            expected.Append(sourceCharacter);
            expected.Append(structuralCharacter);
        }

        string actual = accumulator.GetText();
        Assert.True(UsedSegmentBuilderFallback(accumulator), "The adversarial input did not exercise segment compaction.");
        Assert.Equal(expected.ToString(), actual);
        Assert.Equal(HashUtf16(expected.ToString()), HashUtf16(actual));
        Assert.Equal(pairs * 2, accumulator.Length);
    }

    [Fact]
    public void CachedTextStillObservesLayoutCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var accumulator = new SemanticTextProjectionAccumulator();
            accumulator.Append("already materialized");
            Assert.Equal("already materialized", accumulator.GetText());

            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => accumulator.GetText());
        }
    }

    [Fact]
    public void LargeSpanMaterializationChecksCancellationBeforeCopying()
    {
        using var cancellation = new CancellationTokenSource();
        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var coldBuilder = new SemanticTextProjectionAccumulator();
            var segmented = new SemanticTextProjectionAccumulator();
            segmented.Append(new string('p', 8192));
            string largeSpan = new('s', 8192);

            cancellation.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => coldBuilder.Append(largeSpan.AsSpan()));
            Assert.ThrowsAny<OperationCanceledException>(() => segmented.Append(largeSpan.AsSpan()));
        }
    }

    [Fact]
    public void AllocationProfile_LongSlicesAndFragmentedTextDoNotRegressBuilderBaseline()
    {
        const int longLength = 1_048_576;
        string oldText = new('a', longLength);
        string currentText = string.Concat(oldText.AsSpan(0, longLength - 1), "b".AsSpan());
        Func<string> longSliceProjection = () =>
        {
            var accumulator = new SemanticTextProjectionAccumulator(oldText);
            accumulator.AppendSlice(currentText, 0, longLength - 1);
            accumulator.AppendSlice(currentText, longLength - 1, 1);
            return accumulator.GetText();
        };
        Func<string> longSliceBuilder = () =>
        {
            var accumulator = new LegacyStringBuilderAccumulator(oldText);
            accumulator.AppendSlice(currentText, 0, longLength - 1);
            accumulator.AppendSlice(currentText, longLength - 1, 1);
            return accumulator.GetText();
        };

        long candidateLongBytes = MeasureAllocations(longSliceProjection, iterations: 3);
        long builderLongBytes = MeasureAllocations(longSliceBuilder, iterations: 3);
        WriteAllocationComparison("long mismatch", candidateLongBytes, builderLongBytes, iterations: 3);
        Assert.True(candidateLongBytes < builderLongBytes,
            $"Long source-slice projection allocated {candidateLongBytes} bytes; builder baseline allocated {builderLongBytes} bytes.");

        const int paragraphs = 128;
        const int paragraphLength = 8192;
        string coldSource = new('a', paragraphs * paragraphLength);
        Func<string> coldProjection = () =>
        {
            var accumulator = new SemanticTextProjectionAccumulator();
            for (int index = 0; index < paragraphs; index++)
            {
                accumulator.AppendSlice(coldSource, index * paragraphLength, paragraphLength);
                if (index + 1 < paragraphs)
                    accumulator.Append('\n');
            }
            return accumulator.GetText();
        };
        Func<string> coldBuilder = () =>
        {
            var accumulator = new LegacyStringBuilderAccumulator();
            for (int index = 0; index < paragraphs; index++)
            {
                accumulator.AppendSlice(coldSource, index * paragraphLength, paragraphLength);
                if (index + 1 < paragraphs)
                    accumulator.Append('\n');
            }
            return accumulator.GetText();
        };

        string coldExpected = coldBuilder();
        Assert.Equal(paragraphs * paragraphLength + paragraphs - 1, coldExpected.Length);
        Assert.Equal(HashUtf16(coldExpected), HashUtf16(coldProjection()));
        long candidateColdBytes = MeasureAllocations(coldProjection, iterations: 3);
        long builderColdBytes = MeasureAllocations(coldBuilder, iterations: 3);
        WriteAllocationComparison("cold 1 MiB", candidateColdBytes, builderColdBytes, iterations: 3);
        Assert.True(candidateColdBytes < builderColdBytes,
            $"Cold 1 MiB source-slice projection allocated {candidateColdBytes} bytes; builder baseline allocated {builderColdBytes} bytes.");

        const int tinyIterations = 512;
        const string tinyLeft = "head";
        const string tinyRight = "line";
        Func<string> tinyProjection = () =>
        {
            var accumulator = new SemanticTextProjectionAccumulator();
            accumulator.AppendSlice(tinyLeft, 0, tinyLeft.Length);
            accumulator.Append('\n');
            accumulator.AppendSlice(tinyRight, 0, tinyRight.Length);
            return accumulator.GetText();
        };
        Func<string> tinyBuilder = () =>
        {
            var accumulator = new LegacyStringBuilderAccumulator();
            accumulator.AppendSlice(tinyLeft, 0, tinyLeft.Length);
            accumulator.Append('\n');
            accumulator.AppendSlice(tinyRight, 0, tinyRight.Length);
            return accumulator.GetText();
        };

        long candidateTinyBytes = MeasureAllocations(tinyProjection, tinyIterations);
        long builderTinyBytes = MeasureAllocations(tinyBuilder, tinyIterations);
        WriteAllocationComparison("tiny projection", candidateTinyBytes, builderTinyBytes, tinyIterations);
        Assert.True(candidateTinyBytes <= builderTinyBytes,
            $"Tiny source-slice projection allocated {candidateTinyBytes} bytes; builder baseline allocated {builderTinyBytes} bytes.");

        const int fragmentedPairs = 16384;
        const string fragmentSource = "xy";
        Func<string> fragmentedProjection = () =>
        {
            var accumulator = new SemanticTextProjectionAccumulator();
            for (int index = 0; index < fragmentedPairs; index++)
            {
                accumulator.AppendSlice(fragmentSource, index & 1, 1);
                accumulator.Append((index & 1) == 0 ? '|' : ':');
            }
            return accumulator.GetText();
        };
        Func<string> fragmentedBuilder = () =>
        {
            var accumulator = new LegacyStringBuilderAccumulator();
            for (int index = 0; index < fragmentedPairs; index++)
            {
                accumulator.AppendSlice(fragmentSource, index & 1, 1);
                accumulator.Append((index & 1) == 0 ? '|' : ':');
            }
            return accumulator.GetText();
        };

        long candidateFragmentedBytes = MeasureAllocations(fragmentedProjection, iterations: 8);
        long builderFragmentedBytes = MeasureAllocations(fragmentedBuilder, iterations: 8);
        WriteAllocationComparison("32 KiB adversarial fragments", candidateFragmentedBytes, builderFragmentedBytes, iterations: 8);
        Assert.True(candidateFragmentedBytes <= builderFragmentedBytes,
            $"Fragmented source-slice projection allocated {candidateFragmentedBytes} bytes; builder baseline allocated {builderFragmentedBytes} bytes.");
    }

    private static bool UsedSegmentBuilderFallback(SemanticTextProjectionAccumulator accumulator)
    {
        FieldInfo stateField = typeof(SemanticTextProjectionAccumulator).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? state = stateField.GetValue(accumulator);
        PropertyInfo fallbackProperty = typeof(SemanticTextSegmentCollection).GetProperty(
            "BuilderFallbackLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return state is SemanticTextSegmentCollection segments && (bool)fallbackProperty.GetValue(segments)!;
    }

    private void WriteAllocationComparison(string scenario, long candidateBytes, long baselineBytes, int iterations)
    {
        _output.WriteLine(
            "{0}: candidate={1} B ({2:F1} B/op), baseline={3} B ({4:F1} B/op), delta={5} B ({6:F1} B/op).",
            scenario,
            candidateBytes,
            (double)candidateBytes / iterations,
            baselineBytes,
            (double)baselineBytes / iterations,
            candidateBytes - baselineBytes,
            (double)(candidateBytes - baselineBytes) / iterations);
    }

    private sealed class LegacyStringBuilderAccumulator
    {
        private const int ComparisonChunkLength = 4096;

        private readonly string? _expected;
        private readonly CancellationToken _cancellationToken;
        private StringBuilder? _materialized;
        private int _matchedLength;
        private int _pendingTrailingNewlines;

        internal LegacyStringBuilderAccumulator(string? expected = null)
        {
            _expected = expected;
            _cancellationToken = CancellationToken.None;
            _pendingTrailingNewlines = 0;
            if (expected is null)
                _materialized = new StringBuilder();
        }

        internal int PendingTrailingNewlines => _pendingTrailingNewlines;

        internal void AppendSlice(string source, int start, int count)
        {
            if (_materialized is not null)
            {
                AppendBuilder(source, start, count);
                return;
            }

            int offset = start;
            int remaining = count;
            while (remaining > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                string expected = _expected!;
                if (_matchedLength == expected.Length)
                {
                    MaterializePrefix();
                    AppendBuilder(source, offset, remaining);
                    return;
                }

                int compareLength = Math.Min(Math.Min(remaining, ComparisonChunkLength), expected.Length - _matchedLength);
                ReadOnlySpan<char> actual = source.AsSpan(offset, compareLength);
                ReadOnlySpan<char> prior = expected.AsSpan(_matchedLength, compareLength);
                if (actual.SequenceEqual(prior))
                {
                    _matchedLength += compareLength;
                    offset += compareLength;
                    remaining -= compareLength;
                    continue;
                }

                int matchingPrefixLength = 0;
                while (matchingPrefixLength < compareLength && actual[matchingPrefixLength] == prior[matchingPrefixLength])
                    matchingPrefixLength++;
                _matchedLength += matchingPrefixLength;
                offset += matchingPrefixLength;
                remaining -= matchingPrefixLength;
                MaterializePrefix();
                AppendBuilder(source, offset, remaining);
                return;
            }
        }

        internal void Append(char value)
        {
            if (_materialized is not null)
            {
                _materialized.Append(value);
                return;
            }

            if (_matchedLength < _expected!.Length && _expected[_matchedLength] == value)
            {
                _matchedLength++;
                return;
            }

            MaterializePrefix();
            _materialized!.Append(value);
        }

        internal string GetText()
        {
            if (_materialized is not null)
                return _materialized.ToString();
            if (_matchedLength == _expected!.Length)
                return _expected;

            MaterializePrefix();
            return _materialized!.ToString();
        }

        private void MaterializePrefix()
        {
            if (_materialized is not null)
                return;
            var builder = new StringBuilder(Math.Min(_matchedLength, ComparisonChunkLength));
            int offset = 0;
            while (offset < _matchedLength)
            {
                int count = Math.Min(ComparisonChunkLength, _matchedLength - offset);
                builder.Append(_expected!, offset, count);
                offset += count;
            }
            _materialized = builder;
        }

        private void AppendBuilder(string source, int start, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int chunkLength = Math.Min(ComparisonChunkLength, count - offset);
                _materialized!.Append(source, start + offset, chunkLength);
                offset += chunkLength;
            }
        }
    }

    private static long MeasureAllocations(Func<string> operation, int iterations)
    {
        _ = operation();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
            GC.KeepAlive(operation());
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static byte[] HashUtf16(string value)
        => SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.AsSpan()));
}
