using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SemanticTextProjectionAccumulatorTests
{
    [Fact]
    public void ExactMatchReusesExpectedStringAcrossFragmentsAndUtf16Boundaries()
    {
        string expected = "A\uD800\uDC00e\u0301\uDFFF";
        var accumulator = new SemanticTextProjectionAccumulator(expected);

        accumulator.Append("A\uD800");
        accumulator.Append("\uDC00e");
        accumulator.Append('\u0301');
        accumulator.Append("\uDFFF");

        Assert.Same(expected, accumulator.GetText());
    }

    [Fact]
    public void MismatchAtLastCodeUnitMaterializesVerifiedPrefixAndKeepsExactFallback()
    {
        string expected = string.Concat(new string('x', 16_384), "A");
        string actualSuffix = "B";
        var accumulator = new SemanticTextProjectionAccumulator(expected);

        accumulator.Append(expected.AsSpan(0, 16_384).ToString());
        accumulator.Append(actualSuffix);

        string result = accumulator.GetText();
        Assert.NotSame(expected, result);
        Assert.Equal(string.Concat(new string('x', 16_384), actualSuffix), result);
    }

    [Fact]
    public void LongerAndShorterProjectionsUseNormalExactFallback()
    {
        string expected = "same";
        var longer = new SemanticTextProjectionAccumulator(expected);
        longer.Append("same+");
        string longerResult = longer.GetText();
        Assert.NotSame(expected, longerResult);
        Assert.Equal("same+", longerResult);

        var shorter = new SemanticTextProjectionAccumulator(expected);
        shorter.Append("sam");
        string shorterResult = shorter.GetText();
        Assert.NotSame(expected, shorterResult);
        Assert.Equal("sam", shorterResult);
    }

    [Fact]
    public void TerminalNewlinesCanBeTrimmedWithoutMaterializingTheMatchingPrefix()
    {
        string expected = "retained semantic text";
        var accumulator = new SemanticTextProjectionAccumulator(expected);
        accumulator.Append(expected);
        accumulator.Append('\n');
        accumulator.Append("\n\n");

        Assert.Equal(expected.Length + 3, accumulator.Length);
        Assert.Equal('\n', accumulator[^1]);
        accumulator.Length--;
        accumulator.Length--;
        accumulator.Length--;

        Assert.Same(expected, accumulator.GetText());
    }

    [Fact]
    public void DeferredNewlineBecomesComparableWhenLaterContentArrives()
    {
        string expected = "first\nsecond";
        var accumulator = new SemanticTextProjectionAccumulator(expected);
        accumulator.Append("first");
        accumulator.Append('\n');
        Assert.Equal(6, accumulator.Length);
        Assert.Equal('\n', accumulator[^1]);
        accumulator.Append("second");

        Assert.Same(expected, accumulator.GetText());
    }

    [Fact]
    public void DeferredNewlineMismatchFallsBackWithTheWholeActualSequence()
    {
        string expected = "first second";
        var accumulator = new SemanticTextProjectionAccumulator(expected);
        accumulator.Append("first");
        accumulator.Append('\n');
        accumulator.Append("second");

        string result = accumulator.GetText();
        Assert.NotSame(expected, result);
        Assert.Equal("first\nsecond", result);
    }

    [Fact]
    public void ShrinkingMatchedAndMaterializedTextPreservesSubsequentAppendSemantics()
    {
        string expected = "abcdef";
        var matched = new SemanticTextProjectionAccumulator(expected);
        matched.Append("abcdef\n");
        matched.Length--;
        matched.Length = 3;
        matched.Append("def");
        Assert.Same(expected, matched.GetText());

        var materialized = new SemanticTextProjectionAccumulator(expected);
        materialized.Append("abcXdef");
        materialized.Length = 3;
        materialized.Append("def");
        string fallback = materialized.GetText();
        Assert.NotSame(expected, fallback);
        Assert.Equal(expected, fallback);
    }

    [Fact]
    public void LengthRejectsGrowthAndNegativeValues()
    {
        var accumulator = new SemanticTextProjectionAccumulator("abc");
        accumulator.Append("ab");

        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.Length = 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.Length = -1);
        Assert.Equal("ab", accumulator.GetText());
    }

    [Fact]
    public void CanceledNoProjectionAppendIsObservedBeforeTextCopy()
    {
        string text = new string('x', 32_769);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var accumulator = new SemanticTextProjectionAccumulator();
            Assert.Throws<OperationCanceledException>(() => accumulator.Append('x'));
            Assert.Equal(0, accumulator.Length);
            Assert.Throws<OperationCanceledException>(() => accumulator.Append(text));
            Assert.Equal(0, accumulator.Length);
        }
    }

    [Fact]
    public void MaterializedFallbackChunksLongSuffixAndObservesLaterCancellation()
    {
        string suffix = new string('x', 32_769);

        var firstMismatch = new SemanticTextProjectionAccumulator("different");
        firstMismatch.Append(suffix);
        Assert.Equal(suffix, firstMismatch.GetText());

        var alreadyMaterialized = new SemanticTextProjectionAccumulator("expected");
        alreadyMaterialized.Append("X");
        alreadyMaterialized.Append(suffix);
        Assert.Equal("X" + suffix, alreadyMaterialized.GetText());

        using var cancellation = new CancellationTokenSource();
        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var canceledFallback = new SemanticTextProjectionAccumulator("expected");
            canceledFallback.Append("X");
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => canceledFallback.Append(suffix));
            Assert.Equal(1, canceledFallback.Length);
            Assert.Equal('X', canceledFallback[0]);
        }
    }

    [Fact]
    public void EmptyExpectedProjectionSupportsTrimAndRejectsEveryIndexerPosition()
    {
        string empty = string.Empty;
        var untouched = new SemanticTextProjectionAccumulator(empty);
        Assert.Equal(0, untouched.Length);
        Assert.Same(empty, untouched.GetText());
        Assert.Throws<IndexOutOfRangeException>(() => _ = untouched[-1]);
        Assert.Throws<IndexOutOfRangeException>(() => _ = untouched[0]);

        var trimmed = new SemanticTextProjectionAccumulator(empty);
        trimmed.Append('\n');
        Assert.Equal(1, trimmed.Length);
        Assert.Equal('\n', trimmed[0]);
        trimmed.Length--;
        Assert.Equal(0, trimmed.Length);
        Assert.Same(empty, trimmed.GetText());
    }

    [Fact]
    public void WarmedLongExactMatchDoesNotAllocateDuplicateTextStorage()
    {
        string expected = new string('x', 1_048_576) + "\uD800\uDC00";
        string[] fragments = Enumerable.Range(0, 16)
            .Select(index => expected.Substring(index * 65_536, 65_536))
            .Append(expected.Substring(1_048_576))
            .ToArray();

        var warmup = new SemanticTextProjectionAccumulator(expected);
        foreach (string fragment in fragments)
            warmup.Append(fragment);
        Assert.Same(expected, warmup.GetText());

        var measured = new SemanticTextProjectionAccumulator(expected);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string fragment in fragments)
            measured.Append(fragment);
        string result = measured.GetText();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(expected, result);
        Assert.Equal(0, allocated);
    }
}
