using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>The node lookups a semantic builder collects while it creates nodes, handed to the document so it need not walk the tree again.</summary>
internal readonly struct MarkdownSemanticNodeIndexes(
    Dictionary<InlineContainerBox, MarkdownSemanticNode> ByInlineBox,
    Dictionary<int, MarkdownSemanticNode> ByInlineBlockIndex,
    Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> ByHorizontalOverflow)
{
    public Dictionary<InlineContainerBox, MarkdownSemanticNode> ByInlineBox { get; init; } = ByInlineBox;
    public Dictionary<int, MarkdownSemanticNode> ByInlineBlockIndex { get; init; } = ByInlineBlockIndex;
    public Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> ByHorizontalOverflow { get; init; } = ByHorizontalOverflow;
}
