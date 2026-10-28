using System;
using System.Text;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SourceSliceSemanticTextProjectionTests
{
    [Fact]
    public void SourceSliceAndSpanOperationsMatchIndependentStringBuilderAtEveryUtf16Offset()
    {
        var actual = new SemanticTextProjectionAccumulator();
        var expected = new StringBuilder();

        void Check()
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int index = 0; index < expected.Length; index++)
                Assert.Equal(expected[index], actual[index]);

            string expectedText = expected.ToString();
            Assert.Equal(expectedText, actual.GetText());
            Assert.Equal(expectedText, actual.GetText());
        }

        string source = "left:A\uD83D\uDE03\u05D0e\u0301:right";
        int start = source.IndexOf("A", StringComparison.Ordinal);
        int count = source.IndexOf(":right", StringComparison.Ordinal) - start;
        actual.AppendSlice(source, start, count);
        expected.Append(source, start, count);
        Check();

        actual.Append(" \u0627\u0644\u0639\u0631\u0628\u064A\u0629");
        expected.Append(" \u0627\u0644\u0639\u0631\u0628\u064A\u0629");
        Check();

        char[] mutableSpanSource = "!span \uD83D\uDE42!".ToCharArray();
        ReadOnlySpan<char> snapshotSpan = mutableSpanSource.AsSpan(1, mutableSpanSource.Length - 2);
        actual.Append(snapshotSpan);
        expected.Append(snapshotSpan);
        Array.Fill(mutableSpanSource, '?');
        Check();

        actual.Append(null);
        expected.Append((string?)null);
        actual.Append('X');
        expected.Append('X');
        actual.Append('\n');
        expected.Append('\n');
        actual.Append('\n');
        expected.Append('\n');
        Check();

        actual.Length -= 3;
        expected.Length -= 3;
        Check();

        // Truncate through source-slice output, then append a fresh
        // slice and a surrogate pair as separate UTF-16 code units.
        string tailSource = "pA\uD83D\uDE42BCsuffix";
        actual.AppendSlice(tailSource, 1, 5);
        expected.Append(tailSource, 1, 5);
        Check();
        actual.Length -= 2;
        expected.Length -= 2;
        Check();
        actual.AppendSlice("!\uD800\uDC00", 1, 2);
        expected.Append("!\uD800\uDC00", 1, 2);
        actual.Append('\u05D0');
        expected.Append('\u05D0');
        Check();
    }

    [Fact]
    public void ExpectedProjectionReusesExactIdentityAndMaterializesOnlyOnMismatch()
    {
        string expectedText = "first\n\nA\uD83D\uDE03\u05D0e\u0301";
        var exact = new SemanticTextProjectionAccumulator(expectedText);
        var exactOracle = new StringBuilder();

        exact.AppendSlice(expectedText, 0, 5);
        exactOracle.Append(expectedText, 0, 5);
        AssertLengthAndCharactersMatch(exact, exactOracle);
        exact.Append('\n');
        exactOracle.Append('\n');
        exact.Append("\n");
        exactOracle.Append('\n');
        exact.AppendSlice(expectedText, 7, expectedText.Length - 7);
        exactOracle.Append(expectedText, 7, expectedText.Length - 7);
        AssertMatches(exact, exactOracle);
        Assert.Same(expectedText, exact.GetText());

        string source = "first\n\nA\uD83D\uDE03\u05D0eX";
        var mismatch = new SemanticTextProjectionAccumulator(expectedText);
        var mismatchOracle = new StringBuilder();
        mismatch.AppendSlice(source, 0, 5);
        mismatchOracle.Append(source, 0, 5);
        mismatch.Append('\n');
        mismatchOracle.Append('\n');
        mismatch.Append('\n');
        mismatchOracle.Append('\n');
        mismatch.AppendSlice(source, 7, source.Length - 7);
        mismatchOracle.Append(source, 7, source.Length - 7);
        AssertMatches(mismatch, mismatchOracle);
        Assert.NotSame(expectedText, mismatch.GetText());
    }

    [Fact]
    public void InvalidSlicesAndNullSourcesDoNotChangePreviouslyAppendedContent()
    {
        var actual = new SemanticTextProjectionAccumulator();
        var expected = new StringBuilder();
        actual.AppendSlice("stable", 1, 4);
        expected.Append("stable", 1, 4);
        AssertMatches(actual, expected);

        Assert.Throws<ArgumentNullException>(() => actual.AppendSlice(null!, 0, 1));
        AssertMatches(actual, expected);
        Assert.Throws<ArgumentOutOfRangeException>(() => actual.AppendSlice("abc", -1, 1));
        AssertMatches(actual, expected);
        Assert.Throws<ArgumentOutOfRangeException>(() => actual.AppendSlice("abc", 0, 4));
        AssertMatches(actual, expected);
        Assert.Throws<ArgumentOutOfRangeException>(() => actual.AppendSlice("abc", int.MaxValue, 1));
        AssertMatches(actual, expected);
        Assert.Throws<ArgumentOutOfRangeException>(() => actual.AppendSlice("abc", 1, int.MaxValue));
        AssertMatches(actual, expected);
    }

    [Fact]
    public void SmallSpanIsSnapshottedBeforeItsCallerMutatesTheSource()
    {
        var actual = new SemanticTextProjectionAccumulator();
        var expected = new StringBuilder();
        char[] source = "?abc\uD83D\uDE03?".ToCharArray();
        ReadOnlySpan<char> span = source.AsSpan(1, source.Length - 2);
        actual.Append(span);
        expected.Append(span);

        Array.Fill(source, '!');

        AssertMatches(actual, expected);
        Assert.Equal("abc\uD83D\uDE03", actual.GetText());
    }

    [Fact]
    public void PendingNewlinesCanBeTrimmedAndReappendedExactly()
    {
        string expectedText = "prefix\n\nnext";
        var actual = new SemanticTextProjectionAccumulator(expectedText);
        var expected = new StringBuilder();
        actual.Append("prefix");
        expected.Append("prefix");
        actual.Append('\n');
        expected.Append('\n');
        actual.Append("\n");
        expected.Append('\n');
        AssertLengthAndCharactersMatch(actual, expected);

        actual.Length = expected.Length - 1;
        expected.Length--;
        AssertLengthAndCharactersMatch(actual, expected);

        actual.Append('\n');
        expected.Append('\n');
        actual.AppendSlice(expectedText, 8, expectedText.Length - 8);
        expected.Append(expectedText, 8, expectedText.Length - 8);
        AssertMatches(actual, expected);
        Assert.Same(expectedText, actual.GetText());

        actual.Length = 7;
        expected.Length = 7;
        actual.Append("X");
        expected.Append("X");
        AssertMatches(actual, expected);
    }

    [Fact]
    public void CancellationBeforeFirstSourceSliceLeavesEmptyAccumulatorUntouched()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var actual = new SemanticTextProjectionAccumulator();
            Assert.Throws<OperationCanceledException>(() => actual.AppendSlice("large", 0, 5));
            Assert.Equal(0, actual.Length);
            Assert.Throws<IndexOutOfRangeException>(() => _ = actual[0]);
        }
    }

    [Fact]
    public void CancellationAfterSuccessfulSourceSlicesPreservesText()
    {
        using var cancellation = new CancellationTokenSource();
        using (LayoutPassCancellation.Push(cancellation.Token))
        {
            var actual = new SemanticTextProjectionAccumulator();
            var expected = new StringBuilder();
            actual.AppendSlice("retained", 0, 8);
            expected.Append("retained");
            actual.Append('\n');
            expected.Append('\n');

            int oldLength = actual.Length;
            char[] oldContent = expected.ToString().ToCharArray();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => actual.Append("new fragment"));
            Assert.Throws<OperationCanceledException>(() => actual.GetText());
            Assert.Equal(oldLength, actual.Length);
            for (int index = 0; index < oldContent.Length; index++)
                Assert.Equal(oldContent[index], actual[index]);
        }
    }

    [Fact]
    public void LongNoncontiguousSlicesMatchIndependentStringBuilder()
    {
        const int sliceLength = 32_769;
        string payload = new string('x', sliceLength) + "!" + new string('y', sliceLength);
        var accumulator = new SemanticTextProjectionAccumulator();
        var builder = new StringBuilder();
        accumulator.AppendSlice(payload, 0, sliceLength);
        builder.Append(payload, 0, sliceLength);
        accumulator.Append('|');
        builder.Append('|');
        accumulator.AppendSlice(payload, sliceLength + 1, sliceLength);
        builder.Append(payload, sliceLength + 1, sliceLength);

        Assert.Equal(builder.Length, accumulator.Length);
        Assert.Equal(builder.ToString(), accumulator.GetText());
        Assert.Equal('x', accumulator[sliceLength - 1]);
        Assert.Equal('|', accumulator[sliceLength]);
        Assert.Equal('y', accumulator[accumulator.Length - 1]);
    }

    private static void AssertMatches(
        SemanticTextProjectionAccumulator actual,
        StringBuilder expected)
    {
        AssertLengthAndCharactersMatch(actual, expected);

        string expectedText = expected.ToString();
        Assert.Equal(expectedText, actual.GetText());
        Assert.Equal(expectedText, actual.GetText());
    }

    private static void AssertLengthAndCharactersMatch(
        SemanticTextProjectionAccumulator actual,
        StringBuilder expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], actual[index]);
    }
}
