using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>
/// Collects, while a semantic document is built, the first node registered
/// for each inline box and each horizontal overflow box.
/// </summary>
internal sealed class MarkdownSemanticNodeIndexRegistry
{
    private readonly Dictionary<InlineContainerBox, MarkdownSemanticNode> _nodeByInlineBox = new();
    private readonly Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode> _nodeByHorizontalOverflow = new(
        ReferenceEqualityComparer.Instance);

    /// <summary>Registers <paramref name="node"/> before any of its semantic children.</summary>
    internal void Register(MarkdownSemanticNode node)
    {
        // Match EnumerateDepthFirst's preorder/first-wins contracts. Register
        // parent wrappers before creating any semantic children.
        if (node.InlineBox is { } inline &&
            node.InlineRun is null &&
            node.Role is (MarkdownSemanticRole.Paragraph or
                MarkdownSemanticRole.Heading or
                MarkdownSemanticRole.CodeBlock))
        {
            _nodeByInlineBox.TryAdd(inline, node);
        }

        if (node.VectorSemanticIndex < 0 && node.Box is IHorizontalOverflowBox overflow)
            _nodeByHorizontalOverflow.TryAdd(overflow, node);
    }

    /// <summary>Freezes the registered nodes into the document's node indexes.</summary>
    internal MarkdownSemanticNodeIndexes ToIndexes()
        => MarkdownSemanticNodeIndexes.FromNodeMaps(_nodeByInlineBox, _nodeByHorizontalOverflow);
}
