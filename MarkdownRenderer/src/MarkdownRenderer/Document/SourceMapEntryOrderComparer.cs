using System.Collections.Generic;

namespace MarkdownRenderer.Document;

/// <summary>
/// Orders source-map entries by block, inline, then insertion order.
/// </summary>
internal sealed class SourceMapEntryOrderComparer : IComparer<MarkdownSourceMap.Entry>
{
    internal static SourceMapEntryOrderComparer Instance { get; } = new();

    public int Compare(MarkdownSourceMap.Entry x, MarkdownSourceMap.Entry y) => CompareDocumentOrder(x, y);

    internal static int CompareDocumentOrder(MarkdownSourceMap.Entry left, MarkdownSourceMap.Entry right)
    {
        int comparison = left.BlockIndex.CompareTo(right.BlockIndex);
        if (comparison != 0)
            return comparison;

        comparison = left.InlineIndex.CompareTo(right.InlineIndex);
        return comparison != 0 ? comparison : left.InsertionOrder.CompareTo(right.InsertionOrder);
    }
}
