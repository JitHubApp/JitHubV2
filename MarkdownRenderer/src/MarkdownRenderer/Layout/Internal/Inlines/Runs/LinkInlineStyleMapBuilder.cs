using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

/// <summary>Temporary parser builder; the resulting map owns immutable arrays.</summary>
internal sealed class LinkInlineStyleMapBuilder
{
    private List<LinkInlineStyleSpan>? _spans;
    private long _styleKeyEntryCount;

    internal LinkInlineStylePath CreatePath(
        string styleKey,
        IReadOnlyList<string> styleAliases,
        LinkInlineStylePath? parent)
    {
        ArgumentNullException.ThrowIfNull(styleAliases);
        _styleKeyEntryCount = SaturatingAdd(_styleKeyEntryCount, 1L + styleAliases.Count);
        return new LinkInlineStylePath(styleKey, styleAliases, parent);
    }

    internal void AddRange(int start, int length, LinkInlineStylePath? path)
    {
        if (length <= 0 || path is null)
            return;

        List<LinkInlineStyleSpan> spans = _spans ??= new List<LinkInlineStyleSpan>();
        if (spans.Count > 0)
        {
            LinkInlineStyleSpan previous = spans[^1];
            if (ReferenceEquals(previous.Path, path) && previous.Start <= int.MaxValue - previous.Length &&
                previous.Start + previous.Length == start && previous.Length <= int.MaxValue - length)
            {
                spans[^1] = new LinkInlineStyleSpan(previous.Start, previous.Length + length, path);
                return;
            }
        }

        spans.Add(new LinkInlineStyleSpan(start, length, path));
    }

    internal LinkInlineStyleMap? Build()
        => _spans is { Count: > 0 }
            ? new LinkInlineStyleMap(_spans, SaturatingAdd(_styleKeyEntryCount, _spans.Count))
            : null;

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
