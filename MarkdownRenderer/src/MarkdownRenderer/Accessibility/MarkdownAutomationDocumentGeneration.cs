using System;
using Microsoft.UI.Xaml.Automation;

namespace MarkdownRenderer.Accessibility;

/// <summary>Assigns stable, monotonic generations to logical automation documents.</summary>
internal sealed class MarkdownAutomationDocumentGeneration
{
    private MarkdownAutomationDocumentIdentity? _identity;
    private long _generation;

    internal long CurrentGeneration => _generation;

    internal long Publish(MarkdownAutomationDocumentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (_identity is not null && _identity.IsEquivalentTo(identity))
            return _generation;

        _identity = identity;
        return ++_generation;
    }

    internal bool IsCurrent(MarkdownAutomationDocumentIdentity identity) =>
        _identity is not null && _identity.IsEquivalentTo(identity);

    /// <summary>
    /// Ends the identity epoch when its snapshot is unpublished. The next
    /// publication receives a new generation even if it has identical inputs.
    /// </summary>
    internal void Invalidate()
    {
        _identity = null;
        _generation++;
    }

    internal static void EnsureCurrent(long expected, long current)
    {
        if (expected != current)
        {
            throw new ElementNotAvailableException(
                "The markdown text range belongs to a document that is no longer available.");
        }
    }
}
