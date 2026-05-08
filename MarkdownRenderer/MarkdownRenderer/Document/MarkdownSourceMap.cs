using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Document;

public sealed class MarkdownSourceMap
{
    private readonly List<Entry> _entries = new();
    private readonly string _sourceText;

    public MarkdownSourceMap(string sourceText)
    {
        _sourceText = sourceText ?? string.Empty;
    }

    public string SourceText => _sourceText;

    public void Add(int blockIndex, int inlineIndex, int renderedLength, SourceSpan span)
    {
        _entries.Add(new Entry(blockIndex, inlineIndex, renderedLength, span));
    }

    public string Slice(DocumentRange range)
    {
        range = range.Normalized();
        if (range.IsEmpty) return string.Empty;

        var sb = new System.Text.StringBuilder();
        int? lastBlock = null;

        foreach (var e in _entries)
        {
            var startPos = new DocumentPosition(e.BlockIndex, e.InlineIndex, 0);
            var endPos = new DocumentPosition(e.BlockIndex, e.InlineIndex, e.RenderedLength);

            if (endPos <= range.Start) continue;
            if (startPos >= range.End) break;

            if (lastBlock is int lb && lb != e.BlockIndex)
            {
                sb.Append("\n\n");
            }
            lastBlock = e.BlockIndex;

            int from = 0;
            int to = e.RenderedLength;
            if (range.Start.BlockIndex == e.BlockIndex && range.Start.InlineIndex == e.InlineIndex)
                from = Math.Max(0, range.Start.CharacterOffset);
            if (range.End.BlockIndex == e.BlockIndex && range.End.InlineIndex == e.InlineIndex)
                to = Math.Min(e.RenderedLength, range.End.CharacterOffset);

            if (to <= from) continue;

            if (e.RenderedLength == e.Span.Length)
            {
                sb.Append(_sourceText, e.Span.Start + from, to - from);
            }
            else if (from == 0 && to == e.RenderedLength)
            {
                sb.Append(_sourceText, e.Span.Start, e.Span.Length);
            }
            else
            {
                double scale = (double)e.Span.Length / Math.Max(1, e.RenderedLength);
                int s = e.Span.Start + (int)(from * scale);
                int n = (int)((to - from) * scale);
                s = Math.Clamp(s, 0, _sourceText.Length);
                n = Math.Clamp(n, 0, _sourceText.Length - s);
                sb.Append(_sourceText, s, n);
            }
        }

        return sb.ToString();
    }

    internal IReadOnlyList<Entry> Entries => _entries;

    internal readonly record struct Entry(int BlockIndex, int InlineIndex, int RenderedLength, SourceSpan Span);
}
