using System.Globalization;
using System.Threading;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockChunkBoundaryTests
{
    [Theory]
    [InlineData("😀", 1)]
    [InlineData("e\u0301", 1)]
    [InlineData("👩\u200d👩\u200d👧\u200d👦", 3)]
    public void CutInsideGraphemeBacksUpToPreviousBoundary(string grapheme, int cutWithinGrapheme)
    {
        string text = "x" + grapheme + "y";
        int preferredLength = 1 + cutWithinGrapheme;

        int length = CodeBlockChunkBoundary.GetSafeLength(
            text,
            offset: 0,
            preferredLength: preferredLength,
            maximumLength: text.Length,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, length);
    }

    [Fact]
    public void FirstGraphemeCrossingPreferredBoundaryIsIncludedWholeToGuaranteeProgress()
    {
        string text = "😀x";

        int length = CodeBlockChunkBoundary.GetSafeLength(
            text,
            offset: 0,
            preferredLength: 1,
            maximumLength: 2,
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, length);
    }

    [Fact]
    public void SingleGraphemeLongerThanChunkCapIsReturnedWholeAsExplicitException()
    {
        const int maximumLength = 32_768;
        string text = "a" + new string('\u0301', maximumLength);
        Assert.Equal(text.Length, StringInfo.GetNextTextElementLength(text.AsSpan()));

        int length = CodeBlockChunkBoundary.GetSafeLength(
            text,
            offset: 0,
            preferredLength: maximumLength,
            maximumLength: maximumLength,
            cancellationToken: CancellationToken.None);

        Assert.Equal(text.Length, length);
        Assert.True(length > maximumLength);
    }

    [Fact]
    public void AlreadySafePreferredBoundaryIsPreserved()
    {
        string text = "first line\nsecond line";
        int preferredLength = "first line\n".Length;

        int length = CodeBlockChunkBoundary.GetSafeLength(
            text,
            offset: 0,
            preferredLength: preferredLength,
            maximumLength: text.Length,
            cancellationToken: CancellationToken.None);

        Assert.Equal(preferredLength, length);
    }

    [Fact]
    public void HardTargetInsideLineExtendsThroughItsAuthoredLineBreak()
    {
        string text = "prefix\n" + new string('x', 40_000) + "\nnext";
        int offset = "prefix\n".Length;
        const int maximumLength = 32_768;

        int length = CodeBlockChunkBoundary.GetLinePreservingLength(
            text,
            offset,
            preferredLength: maximumLength,
            maximumLength: maximumLength,
            cancellationToken: CancellationToken.None);

        Assert.Equal(40_001, length);
        Assert.Equal('\n', text[offset + length - 1]);
        Assert.Equal("next", text[(offset + length)..]);
    }

    [Fact]
    public void UnterminatedFinalLineExtendsOnlyToEndOfText()
    {
        string text = "first\n" + new string('y', 40_000);
        int offset = "first\n".Length;

        int length = CodeBlockChunkBoundary.GetLinePreservingLength(
            text,
            offset,
            preferredLength: 32_768,
            maximumLength: 32_768,
            cancellationToken: CancellationToken.None);

        Assert.Equal(text.Length - offset, length);
    }

    [Fact]
    public void TerminalLineBreakIsIncludedWithoutCreatingAnEmptyFollowupChunk()
    {
        string text = new string('z', 40_000) + "\n";

        int length = CodeBlockChunkBoundary.GetLinePreservingLength(
            text,
            offset: 0,
            preferredLength: 32_768,
            maximumLength: 32_768,
            cancellationToken: CancellationToken.None);

        Assert.Equal(text.Length, length);
        Assert.Equal('\n', text[length - 1]);
    }

    [Fact]
    public void CancellationIsObservedBeforeScanning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            CodeBlockChunkBoundary.GetSafeLength(
                new string('x', 4_096),
                offset: 0,
                preferredLength: 4_096,
                maximumLength: 4_096,
                cancellationToken: cancellation.Token));
    }
}
