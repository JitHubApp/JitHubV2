using MarkdownRenderer.CodeBlocks;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class OwnedCodeHighlighterAssignmentTests
{
    [Fact]
    public void SameServiceWithDistinctOwnerIsRejectedWithoutDisposingEitherOwner()
    {
        var service = new TestHighlighter();
        var currentOwner = new TrackingDisposable();
        var nextOwner = new TrackingDisposable();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            OwnedCodeHighlighterAssignment.ValidateTransfer(
                service,
                currentOwner,
                service,
                nextOwner));

        Assert.Contains("different owner", exception.Message, StringComparison.Ordinal);
        Assert.False(currentOwner.IsDisposed);
        Assert.False(nextOwner.IsDisposed);
    }

    [Fact]
    public void IdempotentAssignmentAndNewServiceTransferRemainValid()
    {
        var currentService = new TestHighlighter();
        var replacementService = new TestHighlighter();
        var currentOwner = new TrackingDisposable();
        var replacementOwner = new TrackingDisposable();

        OwnedCodeHighlighterAssignment.ValidateTransfer(
            currentService,
            currentOwner,
            currentService,
            currentOwner);
        OwnedCodeHighlighterAssignment.ValidateTransfer(
            currentService,
            currentOwner,
            replacementService,
            replacementOwner);

        Assert.False(currentOwner.IsDisposed);
        Assert.False(replacementOwner.IsDisposed);
    }

#pragma warning disable CS0618 // Pure ownership tests use the UI-independent compatibility contract.
    private sealed class TestHighlighter : ICodeBlockSyntaxHighlighter
#pragma warning restore CS0618
    {
        public ValueTask<CodeBlockHighlightResult?> HighlightAsync(CodeBlockHighlightRequest request)
            => ValueTask.FromResult<CodeBlockHighlightResult?>(CodeBlockHighlightResult.Empty);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
