using MarkdownRenderer.CodeBlocks;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class CodeBlockHighlightPublicationFenceTests
{
    [Fact]
    public void OnlyCurrentProviderRevisionAndGenerationCanPublish()
    {
        var original = new TestHighlighter { Revision = 7 };
        var replacement = new TestHighlighter { Revision = 7 };

        Assert.True(CodeBlockHighlightPublicationFence.IsCurrent(original, original, 7, 3, 3));
        Assert.False(CodeBlockHighlightPublicationFence.IsCurrent(replacement, original, 7, 3, 3));
        Assert.False(CodeBlockHighlightPublicationFence.IsCurrent(original, original, 7, 4, 3));

        original.Revision = 8;
        Assert.False(CodeBlockHighlightPublicationFence.IsCurrent(original, original, 7, 3, 3));
    }

    [Fact]
    public void DisposedProviderRevisionCannotEscapePublicationFence()
    {
        var provider = new TestHighlighter { Revision = 1 };
        provider.Dispose();

        Assert.False(CodeBlockHighlightPublicationFence.IsCurrent(provider, provider, 1, 1, 1));
    }

    [Fact]
    public void RelayoutCompletionRetriesOnlyWhenTheOriginalWorkIsStillCurrent()
    {
        Assert.Equal(
            CodeBlockHighlightCompletionAction.Publish,
            CodeBlockHighlightPublicationFence.DecideCompletion(
                snapshotChanged: false, isUnloaded: false, workCanceled: false,
                sceneSessionDisposed: false, providerCurrent: true));
        Assert.Equal(
            CodeBlockHighlightCompletionAction.RetryCurrentSnapshot,
            CodeBlockHighlightPublicationFence.DecideCompletion(
                snapshotChanged: true, isUnloaded: false, workCanceled: false,
                sceneSessionDisposed: false, providerCurrent: true));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void RetiredCompletionNeverRetriesOrPublishes(
        bool isUnloaded,
        bool workCanceled,
        bool sceneSessionDisposed,
        bool providerCurrent)
    {
        Assert.Equal(
            CodeBlockHighlightCompletionAction.Drop,
            CodeBlockHighlightPublicationFence.DecideCompletion(
                snapshotChanged: true, isUnloaded, workCanceled,
                sceneSessionDisposed, providerCurrent));
    }

#pragma warning disable CS0618 // Pure-logic tests intentionally avoid the WinUI host-services source file.
    private sealed class TestHighlighter : ICodeBlockSyntaxHighlighter, IDisposable
#pragma warning restore CS0618
    {
        private bool _disposed;
        private int _revision;

        public int Revision
        {
            get => _disposed
                ? throw new ObjectDisposedException(nameof(TestHighlighter))
                : _revision;
            set => _revision = value;
        }

        public ValueTask<CodeBlockHighlightResult?> HighlightAsync(CodeBlockHighlightRequest request) =>
            ValueTask.FromResult<CodeBlockHighlightResult?>(CodeBlockHighlightResult.Empty);

        public void Dispose() => _disposed = true;
    }
}
