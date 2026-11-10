using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>The node lookups a semantic builder collects while it creates nodes, handed to the document so it need not walk the tree again.</summary>
internal readonly struct MarkdownSemanticNodeIndexes(
    Dictionary<InlineContainerBox, MarkdownSemanticNode> ByInlineBox,
    Dictionary<int, MarkdownSemanticNode> ByInlineBlockIndex,
    Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> ByHorizontalOverflow)
{
    public Dictionary<InlineContainerBox, MarkdownSemanticNode> ByInlineBox { get; init; } = ByInlineBox;
    public Dictionary<int, MarkdownSemanticNode> ByInlineBlockIndex { get; init; } = ByInlineBlockIndex;
    public Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> ByHorizontalOverflow { get; init; } = ByHorizontalOverflow;

    /// <summary>
    /// Completes the indexes from the two maps collected in preorder. The block
    /// index lookup is built once at its exact final capacity; growing it
    /// alongside the graph adds rehashes and oversized arrays. This loop visits
    /// only unique indexed inline boxes, not the tree. A block index shared by
    /// two inline boxes is ambiguous and is left out.
    /// </summary>
    internal static MarkdownSemanticNodeIndexes FromNodeMaps(
        Dictionary<InlineContainerBox, MarkdownSemanticNode> byInlineBox,
        Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> byHorizontalOverflow)
    {
        var nodeByInlineBlockIndex = new Dictionary<int, MarkdownSemanticNode>(byInlineBox.Count);
        var ambiguousInlineBlockIndices = new HashSet<int>();
        foreach (KeyValuePair<InlineContainerBox, MarkdownSemanticNode> entry in byInlineBox)
        {
            int blockIndex = entry.Key.BlockIndex;
            if (!nodeByInlineBlockIndex.TryAdd(blockIndex, entry.Value))
                ambiguousInlineBlockIndices.Add(blockIndex);
        }
        foreach (int blockIndex in ambiguousInlineBlockIndices)
            nodeByInlineBlockIndex.Remove(blockIndex);
        return new MarkdownSemanticNodeIndexes(byInlineBox, nodeByInlineBlockIndex, byHorizontalOverflow);
    }

    /// <summary>
    /// Indexes a manually constructed semantic tree with one preorder walk,
    /// using the same first-wins rules the builder applies as it creates nodes.
    /// </summary>
    internal static MarkdownSemanticNodeIndexes FromTree(MarkdownSemanticNode root)
    {
        var nodeByInlineBox = new Dictionary<InlineContainerBox, MarkdownSemanticNode>();
        var nodeByHorizontalOverflow = new Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode>(
            ReferenceEqualityComparer.Instance);
        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(root))
        {
            // Inline semantic descendants share their parent's layout box. Index
            // only the block node so its TextProvider owns the full paragraph or
            // heading range instead of whichever inline span happened to be first.
            if (node.InlineBox is { } inline &&
                node.InlineRun is null &&
                node.Role is MarkdownSemanticRole.Paragraph or
                    MarkdownSemanticRole.Heading or
                    MarkdownSemanticRole.CodeBlock)
                nodeByInlineBox.TryAdd(inline, node);
            if (node.VectorSemanticIndex < 0 && node.Box is IHorizontalOverflowBox overflow)
                nodeByHorizontalOverflow.TryAdd(overflow, node);
        }

        return FromNodeMaps(nodeByInlineBox, nodeByHorizontalOverflow);
    }
}
