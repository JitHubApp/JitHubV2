using System;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>Rejects results produced by a replaced or revised highlighter.</summary>
internal static class CodeBlockHighlightPublicationFence
{
    internal static bool IsCurrent(
#pragma warning disable CS0618 // The fence is shared by the stable service and compatibility contract.
        ICodeBlockSyntaxHighlighter? currentProvider,
        ICodeBlockSyntaxHighlighter requestProvider,
#pragma warning restore CS0618
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
