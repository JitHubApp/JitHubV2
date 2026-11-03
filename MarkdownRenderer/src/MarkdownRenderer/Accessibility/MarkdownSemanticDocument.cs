using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

internal sealed class MarkdownSemanticDocument
{
    private const int SemanticRangeFingerprintBatchBytes = 4 * 1024;


    public readonly MarkdownSemanticNode Root;
    public readonly string Text;
    internal readonly MarkdownAutomationTextFingerprint TextProjectionFingerprint;
    internal readonly MarkdownAutomationTextFingerprint SemanticRangeFingerprint;
    public readonly TextElementBoundaryIndex TextElementBoundaries;
    public readonly IReadOnlyList<MarkdownTextSpan> TextSpans;

    private readonly IReadOnlyDictionary<(int BlockIndex, int InlineIndex), int> _spanByPosition;
    private readonly IReadOnlyDictionary<InlineContainerBox, MarkdownSemanticNode> _nodeByInlineBox;
    private readonly IReadOnlyDictionary<int, MarkdownSemanticNode> _nodeByInlineBlockIndex;
    private readonly IReadOnlyDictionary<IHorizontalOverflowBox, MarkdownSemanticNode> _nodeByHorizontalOverflow;

    public static MarkdownSemanticDocument Empty { get; } = new(
        new MarkdownSemanticNode(MarkdownSemanticRole.Document)
        {
            TextStart = 0,
            TextEnd = 0,
        },
        string.Empty,
        Array.Empty<MarkdownTextSpan>());

    private MarkdownSemanticDocument(MarkdownSemanticNode root, string text, IReadOnlyList<MarkdownTextSpan> spans)
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
        int mappableSpanCount = 0;
        bool hasPreviousMappedKey = false;
        (int BlockIndex, int InlineIndex) previousMappedKey = default;
        for (int i = 0; i < spans.Count; i++)
        {
            MarkdownTextSpan span = spans[i];
            (int BlockIndex, int InlineIndex) key;
            if (span.InlineBox is { } inline && span.InlineRun is { } run)
            {
                key = (inline.BlockIndex, run.InlineIndex);
            }
            else if (span.VectorSceneBox is { } vector)
            {
                key = (vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex));
            }
            else
            {
                hasPreviousMappedKey = false;
                continue;
            }

            if (!hasPreviousMappedKey || key != previousMappedKey)
            {
                mappableSpanCount++;
            }
            previousMappedKey = key;
            hasPreviousMappedKey = true;
        }

        // Standalone images and other atomic-only documents can have many
        // semantic spans but no position-index entries. Size from consecutive
        // distinct mapped keys rather than the full span count; the ordered
        // fill/TryAdd loop below still preserves first-wins behavior for every
        // duplicate, including nonconsecutive duplicates.
        var spanByPosition = new Dictionary<(int BlockIndex, int InlineIndex), int>(mappableSpanCount);
        for (int i = 0; i < spans.Count; i++)
        {
            MarkdownTextSpan span = spans[i];
            if (span.InlineBox is { } inline && span.InlineRun is { } run)
                spanByPosition.TryAdd((inline.BlockIndex, run.InlineIndex), i);
            else if (span.VectorSceneBox is { } vector)
                spanByPosition.TryAdd((vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex)), i);
        }
        _spanByPosition = spanByPosition;

        if (nodeIndexes is { } indexes)
        {
            _nodeByInlineBox = indexes.ByInlineBox;
            _nodeByInlineBlockIndex = indexes.ByInlineBlockIndex;
            _nodeByHorizontalOverflow = indexes.ByHorizontalOverflow;
        }
        else
        {
            // Keep the fallback for manually-constructed semantic roots and
            // private-constructor consumers. Production Builder snapshots pass
            // indexes populated as nodes are created, avoiding this extra walk.
            var nodeByInlineBox = new Dictionary<InlineContainerBox, MarkdownSemanticNode>();
            var nodeByHorizontalOverflow = new Dictionary<IHorizontalOverflowBox, MarkdownSemanticNode>(
                ReferenceEqualityComparer.Instance);
            foreach (MarkdownSemanticNode node in EnumerateDepthFirst(root))
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
            _nodeByInlineBox = nodeByInlineBox;
            var nodeByInlineBlockIndex = new Dictionary<int, MarkdownSemanticNode>(nodeByInlineBox.Count);
            var ambiguousInlineBlockIndices = new HashSet<int>();
            foreach (KeyValuePair<InlineContainerBox, MarkdownSemanticNode> entry in nodeByInlineBox)
            {
                int blockIndex = entry.Key.BlockIndex;
                if (!nodeByInlineBlockIndex.TryAdd(blockIndex, entry.Value))
                    ambiguousInlineBlockIndices.Add(blockIndex);
            }
            foreach (int blockIndex in ambiguousInlineBlockIndices)
                nodeByInlineBlockIndex.Remove(blockIndex);
            _nodeByInlineBlockIndex = nodeByInlineBlockIndex;
            _nodeByHorizontalOverflow = nodeByHorizontalOverflow;
        }
        SemanticRangeFingerprint = NormalizeNodeRangesAndComputeSemanticRangeFingerprint(root, spans, text.Length);
    }

    public static MarkdownSemanticDocument Build(LayoutSnapshot snapshot)
        => Build(snapshot, textProjectionReuse: null);

    internal static MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse)
        => Build(snapshot, textProjectionReuse, indexBuilder: null);

    internal static MarkdownSemanticDocument Build(
        LayoutSnapshot snapshot,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse,
        LayoutSnapshotIndexBuilder? indexBuilder)
    {
        if (textProjectionReuse is { } projection &&
            !ReferenceEquals(snapshot.SourceMap, projection.SourceMap))
            textProjectionReuse = null;

        var builder = new MarkdownSemanticDocumentBuilder(textProjectionReuse?.Text, indexBuilder);
        return builder.Build(snapshot, textProjectionReuse);
    }

    internal static void AttachVectorSemanticNodes(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        IReadOnlyList<MarkdownSemanticNode?> semanticNodes,
        MarkdownSemanticNode sceneNode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int semanticCount = semantics.Count;
        if (semanticCount == 0)
            return;

        // Parent indices may point forward. Cache each item's nearest exposed
        // ancestor (including itself) while resolving parents iteratively, so
        // deep runs of omitted/decorative items are walked only once. The final
        // loop still attaches in semantic-index order, preserving sibling order.
        // MarkdownVectorScene has already validated parent indices and rejected cycles.
        const int UnresolvedAncestor = int.MinValue;
        var nearestExposedAncestors = new int[semanticCount];
        Array.Fill(nearestExposedAncestors, UnresolvedAncestor);
        var path = new int[semanticCount];

        for (int i = 0; i < semanticCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownSemanticNode? child = semanticNodes[i];
            if (child is null)
                continue;

            int current = semantics[i].ParentIndex;
            int pathCount = 0;
            int nearestExposedAncestor = -1;
            while ((uint)current < (uint)semanticCount)
            {
                if ((pathCount & 0x3F) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int cachedAncestor = nearestExposedAncestors[current];
                if (cachedAncestor != UnresolvedAncestor)
                {
                    nearestExposedAncestor = cachedAncestor;
                    break;
                }

                if (semanticNodes[current] is not null)
                {
                    nearestExposedAncestor = current;
                    nearestExposedAncestors[current] = current;
                    break;
                }

                path[pathCount++] = current;
                current = semantics[current].ParentIndex;
            }

            int unwindCount = 0;
            while (pathCount > 0)
            {
                if ((unwindCount++ & 0x3F) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int ancestorIndex = path[--pathCount];
                if (semanticNodes[ancestorIndex] is not null)
                    nearestExposedAncestor = ancestorIndex;
                nearestExposedAncestors[ancestorIndex] = nearestExposedAncestor;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)nearestExposedAncestor < (uint)semanticCount &&
                semanticNodes[nearestExposedAncestor] is { } parentNode)
            {
                parentNode.Add(child);
            }
            else
            {
                sceneNode.Add(child);
            }
        }
    }

    private static MarkdownAutomationTextFingerprint NormalizeNodeRangesAndComputeSemanticRangeFingerprint(
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans,
        int textLength)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[64];
        Span<byte> batch = stackalloc byte[SemanticRangeFingerprintBatchBytes];
        int batchLength = 0;
        foreach (MarkdownSemanticNode node in EnumerateDepthFirst(root))
        {
            // The builder trims synthetic terminal separators after creating semantic
            // nodes. Keep each published range inside the final projection;
            // this is folded into the existing fingerprint traversal so it
            // does not add a second document-tree walk.
            node.TextStart = Math.Clamp(node.TextStart, 0, textLength);
            node.TextEnd = Math.Clamp(node.TextEnd, node.TextStart, textLength);
            AppendNodeRangeProjection(node, record);
            AppendFingerprintRecord(hash, batch, ref batchLength, record);
        }
        for (int index = 0; index < spans.Count; index++)
        {
            MarkdownTextSpan span = spans[index];
            record.Clear();
            record[0] = 0x53;
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], span.TextStart);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], span.TextEnd);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], span.InlineBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], span.InlineRun?.InlineIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], span.ImageBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], span.EmbedBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], span.HostedElementBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], span.VectorSceneBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[36..], span.VectorSemanticIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], span.InlineRun?.SourceSpan.Start ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[44..], span.InlineRun?.SourceSpan.Length ?? -1);
            int kindFlags =
                (span.InlineBox is null ? 0 : 1) |
                (span.InlineRun is null ? 0 : 2) |
                (span.ImageBox is null ? 0 : 4) |
                (span.EmbedBox is null ? 0 : 8) |
                (span.HostedElementBox is null ? 0 : 16) |
                (span.VectorSceneBox is null ? 0 : 32);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], kindFlags);
            AppendFingerprintRecord(hash, batch, ref batchLength, record[..52]);
        }

        if (batchLength > 0)
            hash.AppendData(batch[..batchLength]);

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return MarkdownAutomationTextFingerprint.FromSha256(textLength, digest);
    }

    private static void AppendFingerprintRecord(
        IncrementalHash hash,
        Span<byte> batch,
        ref int batchLength,
        ReadOnlySpan<byte> record)
    {
        if (batch.Length - batchLength < record.Length)
        {
            hash.AppendData(batch[..batchLength]);
            batchLength = 0;
        }

        record.CopyTo(batch[batchLength..]);
        batchLength += record.Length;
    }

    private static void AppendNodeRangeProjection(
        MarkdownSemanticNode node,
        Span<byte> record)
    {
        record.Clear();
        record[0] = 0x4E;
        BinaryPrimitives.WriteInt32LittleEndian(record[4..], (int)node.Role);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], node.TextStart);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], node.TextEnd);
        BinaryPrimitives.WriteInt32LittleEndian(record[16..], node.Box?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], node.InlineBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[24..], node.ImageBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[28..], node.EmbedBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[32..], node.HostedElementBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[36..], node.VectorSceneBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[40..], node.InlineRun?.InlineIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[44..], node.VectorSemanticIndex);
        BinaryPrimitives.WriteInt32LittleEndian(record[48..], node.HeadingLevel);
        BinaryPrimitives.WriteInt32LittleEndian(record[52..], node.Row);
        BinaryPrimitives.WriteInt32LittleEndian(record[56..], node.Column);
        BinaryPrimitives.WriteInt32LittleEndian(record[60..], (int)node.AccessibilityRole);
    }

    public string GetText(MarkdownSemanticNode node)
    {
        int start = Math.Clamp(node.TextStart, 0, Text.Length);
        int end = Math.Clamp(node.TextEnd, start, Text.Length);
        return Text.Substring(start, end - start).Trim();
    }

    public int TextOffsetFromDocumentPosition(DocumentPosition position)
    {
        if (_spanByPosition.TryGetValue((position.BlockIndex, position.InlineIndex), out int indexedSpanIndex))
        {
            MarkdownTextSpan indexedSpan = TextSpans[indexedSpanIndex];
            if (indexedSpan.InlineRun is { } indexedRun)
            {
                return Math.Clamp(
                    indexedSpan.TextStart + ProjectRenderedOffsetToText(
                        indexedRun,
                        position.CharacterOffset,
                        indexedSpan.TextEnd - indexedSpan.TextStart),
                    indexedSpan.TextStart,
                    indexedSpan.TextEnd);
            }

            return position.CharacterOffset <= 0 ? indexedSpan.TextStart : indexedSpan.TextEnd;
        }

        // Non-run positions (images/embeds) and deliberately unbounded
        // selection sentinels are uncommon. Preserve the compatibility
        // fallback without putting normal copy/UIA lookups on a linear path.
        int trailingInlineTextEnd = -1;
        foreach (var span in TextSpans)
        {
            if (span.InlineBox is { } icb && icb.BlockIndex == position.BlockIndex)
            {
                if (span.InlineRun is { } run)
                {
                    if (position.InlineIndex < run.InlineIndex)
                        return span.TextStart;
                    if (position.InlineIndex > run.InlineIndex)
                    {
                        trailingInlineTextEnd = span.TextEnd;
                        continue;
                    }

                    return Math.Clamp(
                        span.TextStart + ProjectRenderedOffsetToText(run, position.CharacterOffset, span.TextEnd - span.TextStart),
                        span.TextStart,
                        span.TextEnd);
                }

                return Math.Clamp(span.TextStart + icb.GetBufferCharOffset(position), span.TextStart, span.TextEnd);
            }
        }

        // A decorative image/spacer has no semantic text span of its own. If
        // it follows the final semantic run in this box, map it to the end of
        // that preceding text rather than falling through to the first span.
        if (trailingInlineTextEnd >= 0)
            return trailingInlineTextEnd;

        foreach (var span in TextSpans)
        {
            bool matchesBlockImage = span.ImageBox?.BlockIndex == position.BlockIndex;
            bool matchesBlockEmbed = span.EmbedBox?.BlockIndex == position.BlockIndex;
            bool matchesHostedElement = span.HostedElementBox?.BlockIndex == position.BlockIndex;
            if (matchesBlockImage || matchesBlockEmbed || matchesHostedElement)
            {
                // These block objects publish explicit before/after endpoints
                // through PositionFromSpanEnd, but do not use the normal
                // position index. Keep the compatibility scan endpoint-aware
                // so a complete atomic selection includes its semantic text.
                return position.CharacterOffset <= 0 ? span.TextStart : span.TextEnd;
            }

            if (span.InlineBox?.BlockIndex == position.BlockIndex ||
                (span.VectorSceneBox?.BlockIndex == position.BlockIndex &&
                 GetVectorDocumentInlineIndex(span.VectorSemanticIndex) == position.InlineIndex))
            {
                return span.TextStart;
            }
        }

        // A box containing only omitted decorative objects still has its
        // zero-length semantic block node. Use that boundary so a spacer-only
        // paragraph between other paragraphs does not map to document end.
        if (_nodeByInlineBlockIndex.TryGetValue(position.BlockIndex, out MarkdownSemanticNode? inlineNode))
            return inlineNode.TextStart;

        return position.BlockIndex <= 0 ? 0 : Text.Length;
    }

    public bool TryGetDocumentRange(int textStart, int textEnd, out DocumentRange range)
    {
        var start = PositionFromTextOffset(textStart);
        var end = PositionFromTextOffset(textEnd);
        if (start is { } s && end is { } e)
        {
            range = new DocumentRange(s, e);
            return true;
        }

        range = DocumentRange.Empty;
        return false;
    }

    public DocumentPosition? PositionFromTextOffset(int textOffset)
    {
        textOffset = Math.Clamp(textOffset, 0, Text.Length);
        if (TextSpans.Count == 0)
            return DocumentPosition.Zero;

        int spanIndex = GetTextSpanStartIndex(textOffset);
        if (spanIndex >= TextSpans.Count)
            return PositionFromSpanEnd(TextSpans[^1]);

        MarkdownTextSpan span = TextSpans[spanIndex];
        if (textOffset < span.TextStart)
        {
            return spanIndex > 0
                ? PositionFromSpanEnd(TextSpans[spanIndex - 1])
                : PositionFromSpanEnd(span) with { CharacterOffset = 0 };
        }

        if (span.InlineBox is { } icb)
        {
            if (span.InlineRun is { } run)
            {
                int accessibleOffset = Math.Clamp(textOffset - span.TextStart, 0, Math.Max(0, span.TextEnd - span.TextStart));
                int renderedOffset = ProjectTextOffsetToRendered(run, accessibleOffset, span.TextEnd - span.TextStart);
                return new DocumentPosition(icb.BlockIndex, run.InlineIndex, renderedOffset);
            }

            int bufferOffset = Math.Clamp(textOffset - span.TextStart, 0, Math.Max(0, span.TextEnd - span.TextStart));
            return icb.GetPositionFromBufferOffset(bufferOffset);
        }

        if (span.ImageBox is { } image)
            return new DocumentPosition(image.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.EmbedBox is { } embed)
            return new DocumentPosition(embed.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.HostedElementBox is { } hosted)
            return new DocumentPosition(hosted.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

        if (span.VectorSceneBox is { } vector)
            return new DocumentPosition(
                vector.BlockIndex,
                GetVectorDocumentInlineIndex(span.VectorSemanticIndex),
                textOffset <= span.TextStart ? 0 : 1);

        return DocumentPosition.Zero;
    }

    /// <summary>Finds the first text span whose end is after the requested offset.</summary>
    internal int GetTextSpanStartIndex(int textOffset)
    {
        int low = 0;
        int high = TextSpans.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (TextSpans[middle].TextEnd <= textOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

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

    private static IEnumerable<Rect> GetDocumentRectsForSpan(
        MarkdownTextSpan span,
        int start,
        int end,
        bool logicalInlineCoordinates = false)
    {
        start = Math.Clamp(start, span.TextStart, span.TextEnd);
        end = Math.Clamp(end, start, span.TextEnd);

        if (span.InlineBox is { } icb)
        {
            DocumentPosition startPos;
            DocumentPosition endPos;
            if (span.InlineRun is { } run)
            {
                int textLength = span.TextEnd - span.TextStart;
                startPos = new DocumentPosition(
                    icb.BlockIndex,
                    run.InlineIndex,
                    ProjectTextOffsetToRendered(run, start - span.TextStart, textLength));
                endPos = new DocumentPosition(
                    icb.BlockIndex,
                    run.InlineIndex,
                    ProjectTextOffsetToRendered(run, end - span.TextStart, textLength));
                if (end > start && endPos.CharacterOffset == startPos.CharacterOffset && run.RenderedLength > 0)
                    endPos = endPos with { CharacterOffset = run.RenderedLength };
            }
            else
            {
                startPos = icb.GetPositionFromBufferOffset(start - span.TextStart);
                endPos = icb.GetPositionFromBufferOffset(end - span.TextStart);
            }

            IEnumerable<Rect> inlineRects = logicalInlineCoordinates
                ? icb.GetLogicalRangeRects(new DocumentRange(startPos, endPos))
                : icb.GetRangeRects(new DocumentRange(startPos, endPos));
            foreach (var rect in inlineRects)
                yield return rect;
        }
        else if (span.ImageBox is { } image)
        {
            yield return image.Bounds;
        }
        else if (span.EmbedBox is { } embed)
        {
            yield return embed.Bounds;
        }
        else if (span.HostedElementBox is { } hosted)
        {
            yield return hosted.Bounds;
        }
        else if (span.VectorSceneBox is { } vector)
        {
            yield return span.VectorSemanticIndex >= 0
                ? vector.GetVisibleSemanticBounds(span.VectorSemanticIndex)
                : vector.VisibleContentBounds;
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
            MarkdownSemanticNode.ChildrenView children = enclosing.Children;
            bool ordered = ReferenceEquals(enclosing, Root);
            int firstChild = ordered ? GetOrderedChildStartIndexCore(children, textStart) : 0;
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
            _nodeByInlineBox.TryGetValue(exactRangeScope, out MarkdownSemanticNode? scopedNode) &&
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
            _nodeByInlineBlockIndex.TryGetValue(blockIndex, out MarkdownSemanticNode? scopedNode) &&
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
        return _nodeByInlineBox.TryGetValue(inlineBox, out node!);
    }

    internal bool TryGetHorizontalOverflowNode(
        IHorizontalOverflowBox overflow,
        out MarkdownSemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        return _nodeByHorizontalOverflow.TryGetValue(overflow, out node!);
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

        MarkdownSemanticNode.ChildrenView children = enclosing.Children;
        bool ordered = ReferenceEquals(enclosing, Root);
        int firstChild = ordered ? GetOrderedChildStartIndexCore(children, textStart) : 0;
        for (int index = firstChild; index < children.Count; index++)
        {
            MarkdownSemanticNode child = children[index];
            if (ordered && child.TextStart >= textEnd)
                break;
            if (child.TextEnd > textStart && child.TextStart < textEnd)
                yield return child;
        }
    }

    // Retain the original read-only-list entry point for the independent
    // logarithmic-read contract. Production passes its concrete value view to
    // the same constrained core, avoiding an interface conversion/box.
    private static int GetOrderedChildStartIndex(IReadOnlyList<MarkdownSemanticNode> children, int textStart)
        => GetOrderedChildStartIndexCore(children, textStart);

    private static int GetOrderedChildStartIndexCore<TChildren>(TChildren children, int textStart)
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
            MarkdownSemanticNode.ChildrenView children = parent.Children;
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

    private static DocumentPosition PositionFromSpanEnd(MarkdownTextSpan span)
    {
        if (span.InlineBox is { } icb)
        {
            if (span.InlineRun is { } run)
                return new DocumentPosition(icb.BlockIndex, run.InlineIndex, run.RenderedLength);
            return icb.GetPositionFromBufferOffset(span.TextEnd - span.TextStart);
        }
        if (span.ImageBox is { } image)
            return new DocumentPosition(image.BlockIndex, 0, 1);
        if (span.EmbedBox is { } embed)
            return new DocumentPosition(embed.BlockIndex, 0, 1);
        if (span.HostedElementBox is { } hosted)
            return new DocumentPosition(hosted.BlockIndex, 0, 1);
        if (span.VectorSceneBox is { } vector)
            return new DocumentPosition(vector.BlockIndex, GetVectorDocumentInlineIndex(span.VectorSemanticIndex), 1);
        return DocumentPosition.Zero;
    }

    // The negative vector-semantic index is a metadata sentinel for a whole,
    // unstructured scene, not a public document coordinate. Atomic source-map
    // and selection positions for that scene use the zero-based document slot.
    private static int GetVectorDocumentInlineIndex(int vectorSemanticIndex) =>
        vectorSemanticIndex < 0 ? 0 : vectorSemanticIndex;

    private static int ProjectRenderedOffsetToText(InlineRun run, int renderedOffset, int textLength)
    {
        if (textLength <= 0 || run.RenderedLength <= 0)
            return 0;

        renderedOffset = Math.Clamp(renderedOffset, 0, run.RenderedLength);
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun)
            return renderedOffset <= 0 ? 0 : textLength;

        if (run.RenderedLength == textLength)
            return renderedOffset;

        return Math.Clamp((int)Math.Round(renderedOffset * (double)textLength / run.RenderedLength), 0, textLength);
    }

    private static int ProjectTextOffsetToRendered(InlineRun run, int textOffset, int textLength)
    {
        if (run.RenderedLength <= 0)
            return 0;

        textOffset = Math.Clamp(textOffset, 0, Math.Max(0, textLength));
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun or InlineSpacerRun)
            return textOffset <= 0 ? 0 : run.RenderedLength;

        if (run.RenderedLength == textLength)
            return textOffset;

        if (textLength <= 0)
            return 0;

        return Math.Clamp((int)Math.Round(textOffset * (double)run.RenderedLength / textLength), 0, run.RenderedLength);
    }
}
