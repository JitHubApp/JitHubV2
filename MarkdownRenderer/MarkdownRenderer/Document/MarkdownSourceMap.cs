using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Document;

/// <summary>
/// Maps rendered document positions back to exact markdown source spans.
/// </summary>
public sealed class MarkdownSourceMap
{
    private readonly List<Entry> _entries = new();
    private readonly string _sourceText;

    /// <summary>Initializes a source map for the supplied markdown source.</summary>
    public MarkdownSourceMap(string sourceText)
    {
        _sourceText = sourceText ?? string.Empty;
    }

    /// <summary>Gets the markdown source text associated with this map.</summary>
    public string SourceText => _sourceText;

    /// <summary>Adds a mapping from a rendered inline run to a source span.</summary>
    public void Add(int blockIndex, int inlineIndex, int renderedLength, SourceSpan span)
    {
        _entries.Add(new Entry(blockIndex, inlineIndex, renderedLength, span));
    }

    /// <summary>Returns the exact markdown source slice covered by a rendered document range.</summary>
    public string Slice(DocumentRange range)
    {
        range = range.Normalized();
        if (range.IsEmpty) return string.Empty;

        // Find the first and last entries that overlap the selection, then
        // slice the original source verbatim between them.  This preserves
        // delimiters that exist in the source (e.g. `|` between table cells,
        // `\n` between list items, `> ` quote markers) without us having to
        // synthesize them from rendered structure — fixing the bug where
        // selections that crossed a row of table cells produced "\n\n"
        // separators because each cell has its own BlockIndex.
        Entry? firstHit = null;
        Entry? lastHit = null;
        int firstFromOffset = 0;
        int lastToOffset = 0;

        foreach (var e in _entries)
        {
            var startPos = new DocumentPosition(e.BlockIndex, e.InlineIndex, 0);
            var endPos = new DocumentPosition(e.BlockIndex, e.InlineIndex, e.RenderedLength);

            if (endPos <= range.Start) continue;
            if (startPos >= range.End) break;

            int from = 0;
            int to = e.RenderedLength;
            if (range.Start.BlockIndex == e.BlockIndex && range.Start.InlineIndex == e.InlineIndex)
                from = Math.Max(0, range.Start.CharacterOffset);
            if (range.End.BlockIndex == e.BlockIndex && range.End.InlineIndex == e.InlineIndex)
                to = Math.Min(e.RenderedLength, range.End.CharacterOffset);
            if (to <= from) continue;

            if (firstHit is null)
            {
                firstHit = e;
                firstFromOffset = ProjectOffset(e, from);
            }
            lastHit = e;
            lastToOffset = ProjectOffset(e, to);
        }

        if (firstHit is null || lastHit is null) return string.Empty;

        int s = Math.Clamp(firstHit.Value.Span.Start + firstFromOffset, 0, _sourceText.Length);
        int eAbs = Math.Clamp(lastHit.Value.Span.Start + lastToOffset, s, _sourceText.Length);
        return _sourceText.Substring(s, eAbs - s);
    }

    /// <summary>
    /// Maps a rendered-text offset within an entry to a byte offset within the
    /// entry's source span. Exact when render-length matches span-length;
    /// otherwise proportional.
    /// </summary>
    private static int ProjectOffset(Entry e, int renderedOffset)
    {
        if (e.RenderedLength <= 0) return 0;
        if (e.RenderedLength == e.Span.Length) return renderedOffset;
        double scale = (double)e.Span.Length / Math.Max(1, e.RenderedLength);
        return (int)Math.Round(renderedOffset * scale);
    }

    internal IReadOnlyList<Entry> Entries => _entries;

    internal readonly record struct Entry(int BlockIndex, int InlineIndex, int RenderedLength, SourceSpan Span);
}
