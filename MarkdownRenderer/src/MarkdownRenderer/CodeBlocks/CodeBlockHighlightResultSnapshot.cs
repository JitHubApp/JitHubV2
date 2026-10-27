using System;
using System.Threading;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Detaches provider-owned span collections before renderer publication. This
/// is a stable-result boundary, not an aggregate memory admission policy; the
/// bounded cache continues to decide which complete results it can retain.
/// </summary>
internal static class CodeBlockHighlightResultSnapshot
{
    internal static CodeBlockHighlightResult CopyProviderResult(
        CodeBlockHighlightResult? result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null)
            return CodeBlockHighlightResult.Empty;

        // Count and indexer are provider-controlled interface calls. Consume
        // them here on the worker, before dispatcher publication or any
        // snapshot/layout lease, and never retain the provider's list.
        var providerSpans = result.Spans;
        int count = providerSpans.Count;
        cancellationToken.ThrowIfCancellationRequested();
        if (count < 0)
        {
            throw new InvalidOperationException(
                "The code highlighter returned a negative span count.");
        }

        if (count == 0)
            return CodeBlockHighlightResult.Empty;

        var spans = new CodeBlockHighlightSpan[count];
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            spans[index] = providerSpans[index];
            // The indexer is provider-controlled and may block or run callbacks.
            // Observe cancellation immediately after each call so a canceled
            // operation does not continue invoking that provider on later spans.
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CodeBlockHighlightResult(Array.AsReadOnly(spans));
    }
}
