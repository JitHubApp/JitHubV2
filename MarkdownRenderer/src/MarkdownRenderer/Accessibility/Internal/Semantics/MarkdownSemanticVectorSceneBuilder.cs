using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Document.Internal;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using static MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownSemanticNodeFactory;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>
/// Builds the semantic nodes, text and spans of a vector scene for
/// <see cref="MarkdownSemanticDocumentBuilder"/>, appending to the
/// document's shared text projection and span buffer.
/// </summary>
internal sealed class MarkdownSemanticVectorSceneBuilder
{
    private readonly SemanticTextProjectionAccumulator _text;
    private readonly PagedEntryBuffer<MarkdownTextSpan> _spans;
    private readonly LayoutSnapshotIndexBuilder? _layoutIndexes;
    private readonly MarkdownSemanticNodeIndexRegistry _nodeIndexes;

    internal MarkdownSemanticVectorSceneBuilder(
        SemanticTextProjectionAccumulator text,
        PagedEntryBuffer<MarkdownTextSpan> spans,
        LayoutSnapshotIndexBuilder? layoutIndexes,
        MarkdownSemanticNodeIndexRegistry nodeIndexes)
    {
        _text = text;
        _spans = spans;
        _layoutIndexes = layoutIndexes;
        _nodeIndexes = nodeIndexes;
    }

    /// <summary>
    /// Appends the scene's text and spans and builds its node with a child
    /// node for each exposed semantic item. The caller appends the block
    /// separator.
    /// </summary>
    internal MarkdownSemanticNode Build(VectorSceneBox vector)
    {
        IReadOnlyList<MarkdownVectorSemanticItem> semantics = vector.SemanticItems;
        int start = _text.Length;
        (int Start, int End)?[] semanticRanges = AppendVectorSceneText(vector, semantics, start);
        int end = _text.Length;

        string? accessibilityName = ResolveVectorSceneAccessibilityName(vector, semantics);
        MarkdownSemanticNode node = CreateVectorSceneNode(vector, start, end, accessibilityName);
        _nodeIndexes.Register(node);

        MarkdownSemanticNode?[] semanticNodes = BuildVectorSemanticNodes(vector, semantics, semanticRanges, start);
        MarkdownVectorSemanticTree.AttachVectorSemanticNodes(
            semantics,
            semanticNodes,
            node,
            vector.CancellationToken);

        return node;
    }

    /// <summary>
    /// Appends the scene's exposed text and returns the text range of each
    /// named semantic item (null for items that contribute no text).
    /// </summary>
    private (int Start, int End)?[] AppendVectorSceneText(
        VectorSceneBox vector,
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        int start)
    {
        var semanticRanges = new (int Start, int End)?[semantics.Count];
        for (int i = 0; i < semantics.Count; i++)
        {
            vector.ThrowIfCancellationRequested();
            MarkdownVectorSemanticItem item = semantics[i];
            _layoutIndexes?.ObserveVectorSemantic(vector, i, item.Flags);
            if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram) ||
                string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            semanticRanges[i] = AppendVectorText(vector, i, item.Name!, start);
        }

        if (semantics.Count == 0 &&
            !string.IsNullOrWhiteSpace(vector.SemanticText))
        {
            // Math and other unstructured vector scenes deliberately expose
            // their semantic source as one exact range. Structured scenes expose
            // each named, non-decorative child as an exact range above; Selectable
            // independently controls application selection and pointer hit testing.
            AppendVectorText(vector, -1, vector.SemanticText, start);
        }

        if (semantics.Count == 0 &&
            _text.Length == start &&
            !string.IsNullOrWhiteSpace(vector.AccessibilityName))
        {
            AppendVectorText(vector, -1, vector.AccessibilityName!, start);
        }

        return semanticRanges;
    }

    /// <summary>
    /// Creates and registers a node for each exposed, non-root semantic item,
    /// indexed by semantic item (null for items that are not exposed).
    /// </summary>
    private MarkdownSemanticNode?[] BuildVectorSemanticNodes(
        VectorSceneBox vector,
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        (int Start, int End)?[] semanticRanges,
        int start)
    {
        var semanticNodes = new MarkdownSemanticNode?[semantics.Count];
        for (int i = 0; i < semanticNodes.Length; i++)
        {
            vector.ThrowIfCancellationRequested();
            MarkdownVectorSemanticItem item = semantics[i];
            if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram))
            {
                continue;
            }
            MarkdownSemanticNode child = CreateVectorSemanticNode(vector, i, item, semanticRanges[i], start);
            _nodeIndexes.Register(child);
            semanticNodes[i] = child;
        }

        return semanticNodes;
    }

    private (int Start, int End) AppendVectorText(
        VectorSceneBox vector,
        int semanticIndex,
        string value,
        int blockStart)
    {
        if (_text.Length > blockStart)
            _text.Append('\n');
        int start = _text.Length;
        _text.Append(value);
        int end = _text.Length;
        _spans.Add(new MarkdownTextSpan(
            start,
            end,
            null,
            null,
            null,
            null,
            null,
            vector,
            semanticIndex,
            vector.ResolveSemanticTextStyle(semanticIndex)));
        return (start, end);
    }
}
