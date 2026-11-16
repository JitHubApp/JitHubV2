using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

/// <summary>
/// Immutable nested formatting for a flattened link label. Keeping these
/// ranges on one LinkRun preserves the authored link's single semantic node,
/// keyboard stop, accessible name, and source span.
/// </summary>
internal sealed class LinkInlineStyleMap
{
    private readonly LinkInlineStyleSpan[] _spans;

    internal LinkInlineStyleMap(
        List<LinkInlineStyleSpan> spans,
        long styleKeyEntryCount)
    {
        ArgumentNullException.ThrowIfNull(spans);
        // The builder's mutable list must not escape into a published run.
        _spans = spans.ToArray();
        StyleKeyEntryCount = styleKeyEntryCount;
    }

    internal int Count => _spans.Length;
    internal long StyleKeyEntryCount { get; }
    internal LinkInlineStyleSpan GetSpan(int index) => _spans[index];

    internal bool IsValidForTextLength(int textLength)
    {
        if (textLength < 0 || StyleKeyEntryCount < _spans.Length)
            return false;

        int previousEnd = 0;
        for (int index = 0; index < _spans.Length; index++)
        {
            LinkInlineStyleSpan span = _spans[index];
            if (span.Start < previousEnd || span.Length <= 0 ||
                span.Start > textLength - span.Length || !IsValidPath(span.Path))
            {
                return false;
            }

            previousEnd = span.Start + span.Length;
        }

        return true;
    }

    private static bool IsValidPath(LinkInlineStylePath? path)
    {
        if (path is null || path.Depth <= 0)
            return false;

        int expectedDepth = path.Depth;
        for (LinkInlineStylePath? current = path; current is not null; current = current.Parent)
        {
            if (current.Depth != expectedDepth || string.IsNullOrWhiteSpace(current.StyleKey))
                return false;
            for (int index = 0; index < current.StyleAliases.Count; index++)
                if (string.IsNullOrWhiteSpace(current.StyleAliases[index]))
                    return false;
            expectedDepth--;
        }

        return expectedDepth == 0;
    }
}
