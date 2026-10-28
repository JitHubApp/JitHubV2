using System;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.CodeBlocks;

internal enum CodeBlockHighlightCompletionAction
{
    Drop,
    Publish,
    RetryCurrentSnapshot,
}

/// <summary>Rejects results produced by a replaced or revised highlighter.</summary>
internal static class CodeBlockHighlightPublicationFence
{
    internal static CodeBlockHighlightCompletionAction DecideCompletion(
        bool snapshotChanged,
        bool isUnloaded,
        bool workCanceled,
        bool sceneSessionDisposed,
        bool providerCurrent)
    {
        if (isUnloaded || workCanceled || sceneSessionDisposed || !providerCurrent)
            return CodeBlockHighlightCompletionAction.Drop;

        return snapshotChanged
            ? CodeBlockHighlightCompletionAction.RetryCurrentSnapshot
            : CodeBlockHighlightCompletionAction.Publish;
    }

    internal static bool IsCurrent(
        ICodeHighlighter? currentProvider,
        ICodeHighlighter requestProvider,
        int requestProviderRevision,
        int currentGeneration,
        int requestGeneration)
    {
        if (currentGeneration != requestGeneration ||
            !ReferenceEquals(currentProvider, requestProvider))
        {
            return false;
        }

        try
        {
            return currentProvider.Revision == requestProviderRevision;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
