using System;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>Validates transfers of a control-owned code-highlighter lease.</summary>
internal static class OwnedCodeHighlighterAssignment
{
    /// <summary>
    /// Rejects replacing an owner while retaining the same service instance.
    /// The renderer cannot know whether disposing the former owner also
    /// invalidates that service, so accepting this transition could dispose a
    /// provider that remains installed and is used by a later generation.
    /// </summary>
    internal static void ValidateTransfer(
#pragma warning disable CS0618 // Pure ownership validation supports both highlighter contracts.
        ICodeBlockSyntaxHighlighter? currentService,
#pragma warning restore CS0618
        IDisposable? currentOwner,
#pragma warning disable CS0618 // Pure ownership validation supports both highlighter contracts.
        ICodeBlockSyntaxHighlighter nextService,
#pragma warning restore CS0618
        IDisposable nextOwner)
    {
        ArgumentNullException.ThrowIfNull(nextService);
        ArgumentNullException.ThrowIfNull(nextOwner);

        if (currentOwner is not null &&
            ReferenceEquals(currentService, nextService) &&
            !ReferenceEquals(currentOwner, nextOwner))
        {
            throw new InvalidOperationException(
                "The active code-highlighter service cannot be reassigned with a different owner. " +
                "Replace the service instance or retain its existing owner.");
        }
    }
}
