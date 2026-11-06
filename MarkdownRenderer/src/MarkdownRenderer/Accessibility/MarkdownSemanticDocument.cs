using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using static MarkdownRenderer.Accessibility.MarkdownTextSpanProjection;

namespace MarkdownRenderer.Accessibility;

internal sealed class MarkdownSemanticDocument
{

    public readonly MarkdownSemanticNode Root;
    public readonly string Text;
    internal readonly MarkdownAutomationTextFingerprint TextProjectionFingerprint;
    internal readonly MarkdownAutomationTextFingerprint SemanticRangeFingerprint;
    public readonly TextElementBoundaryIndex TextElementBoundaries;
    public readonly IReadOnlyList<MarkdownTextSpan> TextSpans;

    /// <summary>The builder's (or a manual root's) node lookups by inline box, block index and overflow box.</summary>
    internal readonly MarkdownSemanticNodeIndexes NodeIndexes;

    /// <summary>Maps between rendered document positions and accessible text offsets.</summary>
    internal readonly MarkdownSemanticPositionMap Positions;

    public static MarkdownSemanticDocument Empty { get; } = new(
        new MarkdownSemanticNode(MarkdownSemanticRole.Document)
        {
            TextStart = 0,
            TextEnd = 0,
        },
        string.Empty,
        Array.Empty<MarkdownTextSpan>());

    internal MarkdownSemanticDocument(MarkdownSemanticNode root, string text, IReadOnlyList<MarkdownTextSpan> spans)
        : this(root, text, spans, textProjectionReuse: null)
    {
    }

    private MarkdownSemanticDocument(
        MarkdownSemanticNode root,
        string text,
        IReadOnlyList<MarkdownTextSpan> spans,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse)
        : this(root, text, spans, textProjectionReuse, nodeIndexes: null)
    {
    }

    internal MarkdownSemanticDocument(
        MarkdownSemanticNode root,
        string text,
        IReadOnlyList<MarkdownTextSpan> spans,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse,
        MarkdownSemanticNodeIndexes? nodeIndexes)
    {
        Root = root;
        Text = text;
        if (textProjectionReuse is { } reuse)
        {
            if (!ReferenceEquals(text, reuse.Text) || reuse.TextElementBoundaries.TextLength != text.Length)
                throw new ArgumentException("A reused text projection must match the semantic text exactly.", nameof(textProjectionReuse));
            TextProjectionFingerprint = reuse.TextProjectionFingerprint;
            TextElementBoundaries = reuse.TextElementBoundaries;
        }
        else
        {
            TextProjectionFingerprint = MarkdownAutomationTextFingerprint.Compute(text);
            TextElementBoundaries = new TextElementBoundaryIndex(text);
        }
        TextSpans = spans;
        Positions = new MarkdownSemanticPositionMap(text.Length, spans);

        // Manually constructed roots carry no builder indexes; walk them once.
        // Production builder snapshots pass indexes populated as nodes are created.
        NodeIndexes = nodeIndexes ?? MarkdownSemanticNodeIndexes.FromTree(root);
        SemanticRangeFingerprint = MarkdownSemanticRangeFingerprint.NormalizeNodeRangesAndComputeSemanticRangeFingerprint(root, spans, text.Length);
    }

    public static MarkdownSemanticDocument Build(LayoutSnapshot snapshot)
        => Build(snapshot, textProjectionReuse: null);

    internal static MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse)
        => Build(snapshot, textProjectionReuse, indexBuilder: null, CancellationToken.None);

    /// <param name="snapshot">The snapshot whose tree is projected.</param>
    /// <param name="textProjectionReuse">An earlier text projection to reuse when it still matches.</param>
    /// <param name="indexBuilder">Collects the snapshot's layout indexes during the same traversal.</param>
    /// <param name="cancellationToken">The token of the layout pass that builds the snapshot.</param>
    internal static MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse,
        LayoutSnapshotIndexBuilder? indexBuilder,
        CancellationToken cancellationToken)
    {
        if (textProjectionReuse is { } projection &&
            !ReferenceEquals(snapshot.SourceMap, projection.SourceMap))
            textProjectionReuse = null;

        var builder = new MarkdownSemanticDocumentBuilder(
            textProjectionReuse?.Text, indexBuilder, cancellationToken);
        return builder.Build(snapshot, textProjectionReuse);
    }

    public string GetText(MarkdownSemanticNode node)
    {
        int start = Math.Clamp(node.TextStart, 0, Text.Length);
        int end = Math.Clamp(node.TextEnd, start, Text.Length);
        return Text.Substring(start, end - start).Trim();
    }

    public int TextOffsetFromDocumentPosition(DocumentPosition position)
        => Positions.TextOffsetFromDocumentPosition(position, NodeIndexes.ByInlineBlockIndex);

    public bool TryGetDocumentRange(int textStart, int textEnd, out DocumentRange range)
        => Positions.TryGetDocumentRange(textStart, textEnd, out range);

    public DocumentPosition? PositionFromTextOffset(int textOffset)
        => Positions.PositionFromTextOffset(textOffset);

    /// <summary>Finds the first text span whose end is after the requested offset.</summary>
    internal int GetTextSpanStartIndex(int textOffset)
        => Positions.GetTextSpanStartIndex(textOffset);

    public IEnumerable<Rect> GetDocumentRects(
        int textStart,
        int textEnd,
        bool expandDegenerate = false)
        => GetDocumentRects(
            textStart,
            textEnd,
            expandDegenerate,
            placementTransform: null,
            placementScope: null);

    internal IEnumerable<Rect> GetDocumentRects(
        int textStart,
        int textEnd,
        bool expandDegenerate,
        Func<int, Rect, Rect>? placementTransform,
        Func<int, LayoutPlacementTransformContext.Scope>? placementScope)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);

        if (expandDegenerate && textStart == textEnd && Text.Length > 0)
        {
            if (textStart < Text.Length) textEnd = textStart + 1;
            else textStart = Math.Max(0, textStart - 1);
        }

        bool isNondegenerateRange = textStart < textEnd;

        // Span ends are monotonic because the semantic builder appends text and
        // each span in the same pass. Start one code unit before the requested
        // offset so a span ending exactly at the inclusive UIA boundary (and
        // zero-length spans at that boundary) remains eligible, matching the
        // historical linear scan below.
        int firstSpan = GetTextSpanStartIndex(textStart - 1);
        for (int index = firstSpan; index < TextSpans.Count; index++)
        {
            MarkdownTextSpan span = TextSpans[index];
            if (span.TextStart > textEnd)
                break;
            if (span.TextEnd < textStart || span.TextStart > textEnd) continue;

            // Inline text preserves its historical endpoint geometry, and a
            // collapsed caret may still use an atomic object's bounds. For a
            // nonempty [start,end) selection, however, a block object whose
            // text starts exactly at the exclusive end is not selected.
            if (isNondegenerateRange && span.TextStart == textEnd && span.InlineBox is null &&
                (span.ImageBox is not null || span.EmbedBox is not null ||
                 span.HostedElementBox is not null))
            {
                continue;
            }

            int start = Math.Max(textStart, span.TextStart);
            int end = Math.Min(textEnd, span.TextEnd);
            if (end < start) continue;

            using LayoutPlacementTransformContext.Scope scope =
                placementScope?.Invoke(span.LayoutBlockIndex) ?? default;
            bool inlineCoordinatesAreLogical =
                span.InlineBox is not null && placementTransform is not null && placementScope is not null;
            foreach (var rect in GetDocumentRectsForSpan(
                         span, start, end, logicalInlineCoordinates: inlineCoordinatesAreLogical))
            {
                yield return placementTransform is null || inlineCoordinatesAreLogical
                    ? rect
                    : placementTransform(span.LayoutBlockIndex, rect);
            }
        }
    }

    public bool TryCoercePointToNearestTextRect(Point point, out Point coercedPoint)
        => TryCoercePointToNearestTextRect(point, placementTransform: null, out coercedPoint);

    internal bool TryCoercePointToNearestTextRect(
        Point point,
        Func<int, Rect, Rect>? placementTransform,
        out Point coercedPoint)
        => TryCoercePointToNearestTextRect(
            point,
            placementTransform,
            placementScope: null,
            out coercedPoint);

    internal bool TryCoercePointToNearestTextRect(
        Point point,
        Func<int, Rect, Rect>? placementTransform,
        Func<int, LayoutPlacementTransformContext.Scope>? placementScope,
        out Point coercedPoint)
    {
        bool found = AccessibilityGeometry.TryCoercePointToNearestRect(
            new AccessibilityPoint(point.X, point.Y),
            EnumerateTextRects(),
            out AccessibilityPoint accessiblePoint);
        coercedPoint = new Point(accessiblePoint.X, accessiblePoint.Y);
        return found;

        IEnumerable<AccessibilityRect> EnumerateTextRects()
        {
            foreach (var span in TextSpans)
            {
                using LayoutPlacementTransformContext.Scope scope =
                    placementScope?.Invoke(span.LayoutBlockIndex) ?? default;
                bool inlineCoordinatesAreLogical =
                    span.InlineBox is not null && placementTransform is not null && placementScope is not null;
                foreach (var rect in GetDocumentRectsForSpan(
                             span, span.TextStart, span.TextEnd,
                             logicalInlineCoordinates: inlineCoordinatesAreLogical))
                {
                    Rect mapped = placementTransform is null || inlineCoordinatesAreLogical
                        ? rect
                        : placementTransform(span.LayoutBlockIndex, rect);
                    yield return new AccessibilityRect(mapped.X, mapped.Y, mapped.Width, mapped.Height);
                }
            }
        }
    }

    public IEnumerable<ImageBox> GetImagesIntersectingTextRange(int textStart, int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);

        for (int index = GetTextSpanStartIndex(textStart - 1); index < TextSpans.Count; index++)
        {
            MarkdownTextSpan span = TextSpans[index];
            if (span.TextStart > textEnd)
                break;
            if (span.TextEnd < textStart || span.TextStart > textEnd)
                continue;

            if (span.ImageBox is { } blockImage)
            {
                yield return blockImage;
            }
            else if (span.InlineRun is InlineImageRun inlineImage)
            {
                yield return inlineImage.Image;
            }
        }
    }

    public IEnumerable<MarkdownSemanticNode> GetNodesIntersectingTextRange(int textStart, int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        foreach (var node in EnumerateDepthFirst(Root))
        {
            if (node == Root) continue;
            if (node.TextEnd < textStart || node.TextStart > textEnd) continue;
            if ((node.VectorSceneBox is not null && node.VectorSemanticIndex >= 0) ||
                node.Role is MarkdownSemanticRole.Link or MarkdownSemanticRole.Image or MarkdownSemanticRole.Embed or MarkdownSemanticRole.Abbreviation or
                MarkdownSemanticRole.Table or MarkdownSemanticRole.TableCell or MarkdownSemanticRole.List or MarkdownSemanticRole.ListItem)
            {
                yield return node;
            }
        }
    }

    internal MarkdownSemanticNode GetInnermostNodeContainingTextRange(
        int textStart,
        int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        MarkdownSemanticNode enclosing = Root;
        while (true)
        {
            MarkdownSemanticNode? next = null;
            MarkdownSemanticChildrenView children = enclosing.Children;
            bool ordered = ReferenceEquals(enclosing, Root);
            int firstChild = ordered ? GetOrderedChildStartIndex(children, textStart) : 0;
            for (int index = firstChild; index < children.Count; index++)
            {
                MarkdownSemanticNode child = children[index];
                if (ordered && child.TextStart > textStart)
                    break;
                if (ContainsTextRange(child, textStart, textEnd, Text.Length))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
                return enclosing;
            enclosing = next;
        }
    }

    internal MarkdownSemanticNode GetEnclosingNodeForTextRange(
        int textStart,
        int textEnd,
        InlineContainerBox? exactRangeScope)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        if (exactRangeScope is not null &&
            NodeIndexes.ByInlineBox.TryGetValue(exactRangeScope, out MarkdownSemanticNode? scopedNode) &&
            textStart == scopedNode.TextStart &&
            textEnd == scopedNode.TextEnd)
        {
            return scopedNode;
        }

        return GetInnermostNodeContainingTextRange(textStart, textEnd);
    }

    internal MarkdownSemanticNode GetEnclosingNodeForTextRangeByBlockIndex(
        int textStart,
        int textEnd,
        int? exactRangeScopeBlockIndex)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        if (exactRangeScopeBlockIndex is int blockIndex &&
            NodeIndexes.ByInlineBlockIndex.TryGetValue(blockIndex, out MarkdownSemanticNode? scopedNode) &&
            textStart == scopedNode.TextStart &&
            textEnd == scopedNode.TextEnd)
        {
            return scopedNode;
        }

        return GetInnermostNodeContainingTextRange(textStart, textEnd);
    }

    internal bool TryGetInlineContainerNode(
        InlineContainerBox inlineBox,
        out MarkdownSemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(inlineBox);
        return NodeIndexes.ByInlineBox.TryGetValue(inlineBox, out node!);
    }

    internal bool TryGetHorizontalOverflowNode(
        IHorizontalOverflowBox overflow,
        out MarkdownSemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        return NodeIndexes.ByHorizontalOverflow.TryGetValue(overflow, out node!);
    }

    internal IEnumerable<MarkdownSemanticNode> GetImmediateChildrenIntersectingTextRange(
        MarkdownSemanticNode enclosing,
        int textStart,
        int textEnd)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);
        if (textStart == textEnd)
            yield break;

        MarkdownSemanticChildrenView children = enclosing.Children;
        bool ordered = ReferenceEquals(enclosing, Root);
        int firstChild = ordered ? GetOrderedChildStartIndex(children, textStart) : 0;
        for (int index = firstChild; index < children.Count; index++)
        {
            MarkdownSemanticNode child = children[index];
            if (ordered && child.TextStart >= textEnd)
                break;
            if (child.TextEnd > textStart && child.TextStart < textEnd)
                yield return child;
        }
    }

    /// <summary>
    /// Binary-searches ordered siblings for the first child ending at or after
    /// <paramref name="textStart"/>. Production passes the concrete children
    /// view, avoiding an interface conversion/box.
    /// </summary>
    internal static int GetOrderedChildStartIndex<TChildren>(TChildren children, int textStart)
        where TChildren : IReadOnlyList<MarkdownSemanticNode>
    {
        // Only the document's top-level siblings are guaranteed to have ordered
        // appended ranges. Vector semantic hierarchies may reference children
        // outside a parent's own label range, so they must keep the full scan.
        int low = 0;
        int high = children.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (children[middle].TextEnd < textStart)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static bool ContainsTextRange(
        MarkdownSemanticNode node,
        int textStart,
        int textEnd,
        int documentLength)
    {
        if (textStart != textEnd)
            return node.TextStart <= textStart && textEnd <= node.TextEnd;

        // Text offsets are half-open. A caret at a shared boundary belongs to
        // the following node; only the final node may contain document end.
        return node.TextStart <= textStart &&
               (textStart < node.TextEnd ||
                (textStart == documentLength && textStart == node.TextEnd));
    }

    public static IEnumerable<MarkdownSemanticNode> EnumerateDepthFirst(MarkdownSemanticNode node)
    {
        // A recursive iterator creates a state machine for every node and can
        // exhaust the call stack on deeply nested (hostile or generated) input.
        // Store one frame per ancestor; a very wide document root then needs
        // constant auxiliary space rather than a stack of all its children.
        var path = new List<(MarkdownSemanticNode Node, int NextChild)>(8)
        {
            (node, 0),
        };
        yield return node;
        while (path.Count != 0)
        {
            int top = path.Count - 1;
            (MarkdownSemanticNode parent, int nextChild) = path[top];
            MarkdownSemanticChildrenView children = parent.Children;
            if (nextChild >= children.Count)
            {
                path.RemoveAt(top);
                continue;
            }

            path[top] = (parent, nextChild + 1);
            MarkdownSemanticNode child = children[nextChild];
            path.Add((child, 0));
            yield return child;
        }
    }
}
