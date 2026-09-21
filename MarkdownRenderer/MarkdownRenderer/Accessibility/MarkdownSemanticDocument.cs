using System;
using System.Collections.Generic;
using System.Text;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

internal enum MarkdownSemanticRole
{
    Document,
    Group,
    Paragraph,
    Heading,
    CodeBlock,
    Link,
    List,
    ListItem,
    Table,
    TableCell,
    Image,
    Math,
    Diagram,
    Embed,
    Abbreviation,
}

internal sealed class MarkdownSemanticNode
{
    private readonly List<MarkdownSemanticNode> _children = new();

    public MarkdownSemanticNode(MarkdownSemanticRole role, BlockBox? box = null)
    {
        Role = role;
        Box = box;
    }

    public MarkdownSemanticRole Role { get; }
    public BlockBox? Box { get; }
    public InlineContainerBox? InlineBox { get; init; }
    public ImageBox? ImageBox { get; init; }
    public EmbedBox? EmbedBox { get; init; }
    public DeclarativeHostedElementBox? HostedElementBox { get; init; }
    public VectorSceneBox? VectorSceneBox { get; init; }
    public int VectorSemanticIndex { get; init; } = -1;
    public MarkdownVectorSemanticRole? VectorSemanticRole { get; init; }
    public MarkdownVectorSemanticFlags VectorSemanticFlags { get; init; }
    public TableBox? TableBox { get; init; }
    public InlineRun? InlineRun { get; init; }
    public MarkdownSemanticNode? Parent { get; private set; }
    public IReadOnlyList<MarkdownSemanticNode> Children => _children;
    public int TextStart { get; set; }
    public int TextEnd { get; set; }
    public int HeadingLevel { get; init; }
    public string? CodeLanguage { get; init; }
    public string? HelpText { get; init; }
    public int Row { get; init; } = -1;
    public int Column { get; init; } = -1;
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public int Level { get; set; }
    public int PositionInSet { get; set; }
    public int SizeOfSet { get; set; }
    public bool IsHeader { get; init; }
    public MarkdownAccessibilityRole AccessibilityRole { get; init; }
    public string? AccessibilityName { get; init; }
    public string? AccessibilityDescription { get; init; }
    public string? AutomationId { get; init; }

    public Rect Bounds
    {
        get
        {
            if (InlineBox is not null && InlineRun is not null)
            {
                var runRect = InlineBox.GetRunRect(InlineRun.InlineIndex);
                if (runRect.Width > 0 && runRect.Height > 0)
                    return runRect;
            }

            if (VectorSceneBox is { } vector)
                return VectorSemanticIndex >= 0 ? vector.GetSemanticBounds(VectorSemanticIndex) : vector.Bounds;

            return Box?.Bounds ?? InlineBox?.Bounds ?? ImageBox?.Bounds ?? EmbedBox?.Bounds ?? HostedElementBox?.Bounds ?? default;
        }
    }

    public void Add(MarkdownSemanticNode child)
    {
        child.Parent = this;
        _children.Add(child);
    }
}

internal sealed record MarkdownTextSpan(
    int TextStart,
    int TextEnd,
    InlineContainerBox? InlineBox,
    InlineRun? InlineRun,
    ImageBox? ImageBox,
    EmbedBox? EmbedBox,
    DeclarativeHostedElementBox? HostedElementBox,
    VectorSceneBox? VectorSceneBox = null,
    int VectorSemanticIndex = -1);

internal sealed class MarkdownSemanticDocument
{
    private readonly IReadOnlyDictionary<(int BlockIndex, int InlineIndex), MarkdownTextSpan> _spanByPosition;
    private readonly IReadOnlyDictionary<InlineContainerBox, MarkdownSemanticNode> _nodeByInlineBox;
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
    {
        Root = root;
        Text = text;
        TextElementBoundaries = new TextElementBoundaryIndex(text);
        TextSpans = spans;
        var spanByPosition = new Dictionary<(int BlockIndex, int InlineIndex), MarkdownTextSpan>();
        for (int i = 0; i < spans.Count; i++)
        {
            MarkdownTextSpan span = spans[i];
            if (span.InlineBox is { } inline && span.InlineRun is { } run)
                spanByPosition.TryAdd((inline.BlockIndex, run.InlineIndex), span);
            else if (span.VectorSceneBox is { } vector)
                spanByPosition.TryAdd((vector.BlockIndex, span.VectorSemanticIndex), span);
        }
        _spanByPosition = spanByPosition;

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
        _nodeByHorizontalOverflow = nodeByHorizontalOverflow;
    }

    public MarkdownSemanticNode Root { get; }
    public string Text { get; }
    public TextElementBoundaryIndex TextElementBoundaries { get; }
    public IReadOnlyList<MarkdownTextSpan> TextSpans { get; }

    public static MarkdownSemanticDocument Build(LayoutSnapshot snapshot)
    {
        var builder = new Builder();
        return builder.Build(snapshot);
    }

    public string GetText(MarkdownSemanticNode node)
    {
        int start = Math.Clamp(node.TextStart, 0, Text.Length);
        int end = Math.Clamp(node.TextEnd, start, Text.Length);
        return Text.Substring(start, end - start).Trim();
    }

    public int TextOffsetFromDocumentPosition(DocumentPosition position)
    {
        if (_spanByPosition.TryGetValue((position.BlockIndex, position.InlineIndex), out var indexedSpan))
        {
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
        foreach (var span in TextSpans)
        {
            if (span.InlineBox is { } icb && icb.BlockIndex == position.BlockIndex)
            {
                if (span.InlineRun is { } run)
                {
                    if (position.InlineIndex < run.InlineIndex)
                        return span.TextStart;
                    if (position.InlineIndex > run.InlineIndex)
                        continue;

                    return Math.Clamp(
                        span.TextStart + ProjectRenderedOffsetToText(run, position.CharacterOffset, span.TextEnd - span.TextStart),
                        span.TextStart,
                        span.TextEnd);
                }

                return Math.Clamp(span.TextStart + icb.GetBufferCharOffset(position), span.TextStart, span.TextEnd);
            }
        }

        foreach (var span in TextSpans)
        {
            if (span.InlineBox?.BlockIndex == position.BlockIndex ||
                span.ImageBox?.BlockIndex == position.BlockIndex ||
                span.EmbedBox?.BlockIndex == position.BlockIndex ||
                span.HostedElementBox?.BlockIndex == position.BlockIndex ||
                (span.VectorSceneBox?.BlockIndex == position.BlockIndex &&
                 (span.VectorSemanticIndex == position.InlineIndex || span.VectorSemanticIndex < 0)))
            {
                return span.TextStart;
            }
        }

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
                span.VectorSemanticIndex,
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

    public IEnumerable<Rect> GetDocumentRects(int textStart, int textEnd, bool expandDegenerate = false)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);

        if (expandDegenerate && textStart == textEnd && Text.Length > 0)
        {
            if (textStart < Text.Length) textEnd = textStart + 1;
            else textStart = Math.Max(0, textStart - 1);
        }

        foreach (var span in TextSpans)
        {
            if (span.TextEnd < textStart || span.TextStart > textEnd) continue;

            int start = Math.Max(textStart, span.TextStart);
            int end = Math.Min(textEnd, span.TextEnd);
            if (end < start) continue;

            foreach (var rect in GetDocumentRectsForSpan(span, start, end))
                yield return rect;
        }
    }

    public bool TryCoercePointToNearestTextRect(Point point, out Point coercedPoint)
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
                foreach (var rect in GetDocumentRectsForSpan(span, span.TextStart, span.TextEnd))
                    yield return new AccessibilityRect(rect.X, rect.Y, rect.Width, rect.Height);
            }
        }
    }

    private static IEnumerable<Rect> GetDocumentRectsForSpan(
        MarkdownTextSpan span,
        int start,
        int end)
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

            foreach (var rect in icb.GetRangeRects(new DocumentRange(startPos, endPos)))
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

        foreach (var span in TextSpans)
        {
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
            foreach (MarkdownSemanticNode child in enclosing.Children)
            {
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

        foreach (MarkdownSemanticNode child in enclosing.Children)
        {
            if (child.TextEnd > textStart && child.TextStart < textEnd)
                yield return child;
        }
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
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateDepthFirst(child))
                yield return descendant;
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
            return new DocumentPosition(vector.BlockIndex, span.VectorSemanticIndex, 1);
        return DocumentPosition.Zero;
    }

    private static int ProjectRenderedOffsetToText(InlineRun run, int renderedOffset, int textLength)
    {
        if (textLength <= 0 || run.RenderedLength <= 0)
            return 0;

        renderedOffset = Math.Clamp(renderedOffset, 0, run.RenderedLength);
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun)
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
        if (run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun)
            return textOffset <= 0 ? 0 : run.RenderedLength;

        if (run.RenderedLength == textLength)
            return textOffset;

        if (textLength <= 0)
            return 0;

        return Math.Clamp((int)Math.Round(textOffset * (double)run.RenderedLength / textLength), 0, run.RenderedLength);
    }

    private sealed class Builder
    {
        private readonly StringBuilder _text = new();
        private readonly List<MarkdownTextSpan> _spans = new();
        private int _listDepth;

        public MarkdownSemanticDocument Build(LayoutSnapshot snapshot)
        {
            var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document)
            {
                TextStart = 0
            };

            foreach (var block in snapshot.Blocks)
            {
                var node = BuildBlock(block);
                if (node is not null) root.Add(node);
            }

            TrimTrailingNewline();
            root.TextEnd = _text.Length;
            return new MarkdownSemanticDocument(root, _text.ToString(), _spans);
        }

        private MarkdownSemanticNode? BuildBlock(BlockBox box)
        {
            return box switch
            {
                InlineContainerBox inline => BuildInline(inline),
                CodeBlockBox codeBlock => BuildCodeBlock(codeBlock),
                ImageBox image => BuildImage(image),
                EmbedBox embed => BuildEmbed(embed),
                DeclarativeHostedElementBox hosted => BuildHostedElement(hosted),
                VectorSceneBox vector => BuildVectorScene(vector),
                ListItemBox listItem => BuildListItem(listItem),
                TableBox table => BuildTable(table),
                StackBox stack when IsListStack(stack) => BuildList(stack),
                StackBox stack => BuildGroup(stack),
                _ => null,
            };
        }

        private MarkdownSemanticNode BuildInline(InlineContainerBox inline)
        {
            var role = inline.ElementKey == MarkdownElementKeys.CodeBlock
                ? MarkdownSemanticRole.CodeBlock
                : IsHeading(inline.ElementKey)
                    ? MarkdownSemanticRole.Heading
                    : MarkdownSemanticRole.Paragraph;
            var node = new MarkdownSemanticNode(role, inline)
            {
                InlineBox = inline,
                TextStart = _text.Length,
                HeadingLevel = GetHeadingLevel(inline.ElementKey),
                CodeLanguage = inline.CodeLanguage,
                HelpText = inline.ElementKey == MarkdownElementKeys.CodeBlock && !string.IsNullOrWhiteSpace(inline.CodeLanguage)
                    ? inline.Context.ResolveFormattedString(
                        MarkdownStringKeys.CodeLanguageHelp,
                        MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                        inline.CodeLanguage)
                    : null,
            };

            MarkdownSemanticNode? flattenedHeading = null;

            for (int runIndex = 0; runIndex < inline.Runs.Count; runIndex++)
            {
                InlineRun run = inline.Runs[runIndex];
                string accessibleText = GetInlineAccessibleText(run, inline.Context);

                // An unlinked image with empty alternative text is decorative. It must
                // contribute neither a text span nor an element to the UIA document.
                if (run is InlineImageRun { IsLinked: false } && accessibleText.Length == 0)
                    continue;

                bool atomic = IsAtomicInline(run);
                if (atomic &&
                    accessibleText.Length > 0 &&
                    _text.Length > node.TextStart &&
                    NeedsSemanticBoundary(_text[^1], accessibleText[0]))
                {
                    AppendInlineSemanticBoundary(inline, run);
                }

                int runStart = _text.Length;
                _text.Append(accessibleText);
                int runEnd = _text.Length;
                _spans.Add(new MarkdownTextSpan(runStart, runEnd, inline, run, null, null, null));
                int flattenedHeadingLevel = GetHeadingLevel(run.SemanticHeadingKey);
                if (flattenedHeadingLevel > 0)
                {
                    if (flattenedHeading is null || flattenedHeading.HeadingLevel != flattenedHeadingLevel)
                    {
                        flattenedHeading = new MarkdownSemanticNode(MarkdownSemanticRole.Heading, inline)
                        {
                            InlineBox = inline,
                            InlineRun = run,
                            TextStart = runStart,
                            TextEnd = runEnd,
                            HeadingLevel = flattenedHeadingLevel,
                        };
                        node.Add(flattenedHeading);
                    }
                    else
                    {
                        flattenedHeading.TextEnd = runEnd;
                    }
                }
                else
                {
                    flattenedHeading = null;
                }

                MarkdownSemanticNode semanticParent = flattenedHeading ?? node;
                if (run is LinkRun link)
                {
                    semanticParent.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Link, inline)
                    {
                        InlineBox = inline,
                        InlineRun = link,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        HelpText = link.Url,
                    });
                }
                else if (run is InlineEmbedRun embedRun)
                {
                    semanticParent.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Embed, inline)
                    {
                        InlineBox = inline,
                        InlineRun = embedRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        AccessibilityName = embedRun.AutomationMetadata?.CurrentName,
                        AccessibilityDescription = embedRun.AutomationMetadata?.ReadOnlyHelpText,
                        AutomationId = embedRun.AutomationMetadata?.AutomationId,
                    });
                }
                else if (run is InlineImageRun imageRun)
                {
                    semanticParent.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Image, inline)
                    {
                        InlineBox = inline,
                        ImageBox = imageRun.Image,
                        InlineRun = imageRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        HelpText = imageRun.IsLinked
                            ? !string.IsNullOrWhiteSpace(imageRun.LinkTitle)
                                ? imageRun.LinkTitle
                                : imageRun.LinkUrl
                            : !string.IsNullOrWhiteSpace(imageRun.Title)
                                ? imageRun.Title
                            : imageRun.Url,
                    });
                }
                else if (run is InlineVectorSceneRun vectorRun)
                {
                    semanticParent.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Math, inline)
                    {
                        InlineBox = inline,
                        InlineRun = vectorRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        AccessibilityRole = MarkdownAccessibilityRole.Math,
                        AccessibilityName = vectorRun.AccessibilityName,
                        AccessibilityDescription = vectorRun.AccessibilityDescription,
                        AutomationId = string.Concat(
                            "MarkdownMath_",
                            vectorRun.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "_",
                            vectorRun.SourceSpan.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    });
                }
                else if (run is AbbreviationRun abbreviationRun)
                {
                    semanticParent.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Abbreviation, inline)
                    {
                        InlineBox = inline,
                        InlineRun = abbreviationRun,
                        TextStart = runStart,
                        TextEnd = runEnd,
                        HelpText = abbreviationRun.Expansion,
                    });
                }

                if (atomic &&
                    accessibleText.Length > 0 &&
                    TryGetNextInlineAccessibleText(inline, runIndex + 1, out string nextText) &&
                    NeedsSemanticBoundary(accessibleText[^1], nextText[0]))
                {
                    AppendInlineSemanticBoundary(inline, run);
                }
            }
            node.TextEnd = _text.Length;

            AppendBlockSeparator();
            return node;
        }

        private static string GetInlineAccessibleText(
            InlineRun run,
            MarkdownLayoutContext context) =>
            run is InlineImageRun imageRun
                ? MarkdownImageSemanticPolicy.GetInlineAccessibleName(
                    imageRun.AltText,
                    imageRun.IsLinked,
                    imageRun.LinkTitle,
                    context.ResolveString(
                        MarkdownStringKeys.ImageName,
                        MarkdownLocalizedStrings.ImageName)) ?? string.Empty
                : run.AccessibleText;

        private static bool IsAtomicInline(InlineRun run) =>
            run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun;

        private static bool NeedsSemanticBoundary(char left, char right) =>
            char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);

        private static bool TryGetNextInlineAccessibleText(
            InlineContainerBox inline,
            int startIndex,
            out string text)
        {
            for (int index = startIndex; index < inline.Runs.Count; index++)
            {
                text = GetInlineAccessibleText(inline.Runs[index], inline.Context);
                if (text.Length > 0)
                    return true;
            }

            text = string.Empty;
            return false;
        }

        private void AppendInlineSemanticBoundary(InlineContainerBox inline, InlineRun run)
        {
            int start = _text.Length;
            _text.Append(' ');
            _spans.Add(new MarkdownTextSpan(start, start + 1, inline, run, null, null, null));
        }

        private MarkdownSemanticNode BuildCodeBlock(CodeBlockBox codeBlock)
        {
            var hasLanguage = !string.IsNullOrWhiteSpace(codeBlock.CodeLanguage);
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.CodeBlock, codeBlock)
            {
                TextStart = _text.Length,
                CodeLanguage = hasLanguage ? codeBlock.LanguageDisplay : null,
                HelpText = hasLanguage
                    ? codeBlock.Context.ResolveFormattedString(
                        MarkdownStringKeys.CodeLanguageHelp,
                        MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                        codeBlock.LanguageDisplay)
                    : null,
            };

            foreach (var chunk in codeBlock.Chunks)
            {
                foreach (var run in chunk.Runs)
                {
                    int runStart = _text.Length;
                    _text.Append(run.AccessibleText);
                    int runEnd = _text.Length;
                _spans.Add(new MarkdownTextSpan(runStart, runEnd, chunk, run, null, null, null));
                }
            }

            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode? BuildImage(ImageBox image)
        {
            if (MarkdownImageSemanticPolicy.IsDecorativeBlock(image.Alt))
                return null;

            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Image, image)
            {
                ImageBox = image,
                TextStart = _text.Length,
                HelpText = image.SvgDesc,
            };

            string name = image.Alt;
            int start = _text.Length;
            _text.Append(name);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(start, end, null, null, image, null, null));
            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildEmbed(EmbedBox embed)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Embed, embed)
            {
                EmbedBox = embed,
                TextStart = _text.Length,
            };
            int start = _text.Length;
            _text.Append(InlineEmbedRun.PlaceholderChar);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(start, end, null, null, null, embed, null));
            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildVectorScene(VectorSceneBox vector)
        {
            MarkdownContent content = vector.Content;
            int start = _text.Length;
            var semanticRanges = new (int Start, int End)?[vector.Scene.Semantics.Count];
            for (int i = 0; i < vector.Scene.Semantics.Count; i++)
            {
                MarkdownVectorSemanticItem item = vector.Scene.Semantics[i];
                if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                    (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram) ||
                    string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                semanticRanges[i] = AppendVectorText(vector, i, item.Name!, start);
            }

            if (vector.Scene.Semantics.Count == 0 &&
                !string.IsNullOrWhiteSpace(content.SemanticText))
            {
                // Math and other unstructured vector scenes deliberately expose
                // their semantic source as one exact range. Structured scenes expose
                // each named, non-decorative child as an exact range above; Selectable
                // independently controls application selection and pointer hit testing.
                AppendVectorText(vector, -1, content.SemanticText, start);
            }

            if (vector.Scene.Semantics.Count == 0 &&
                _text.Length == start &&
                !string.IsNullOrWhiteSpace(content.AccessibilityName))
            {
                AppendVectorText(vector, -1, content.AccessibilityName!, start);
            }
            int end = _text.Length;

            string? accessibilityName = content.AccessibilityName;
            if (string.IsNullOrWhiteSpace(accessibilityName))
            {
                for (int i = 0; i < vector.Scene.Semantics.Count; i++)
                {
                    MarkdownVectorSemanticItem candidate = vector.Scene.Semantics[i];
                    if (MarkdownVectorSemanticPolicy.IsExposed(candidate.Flags) &&
                        candidate.ParentIndex < 0 &&
                        candidate.Role == MarkdownVectorSemanticRole.Diagram &&
                        !string.IsNullOrWhiteSpace(candidate.Name))
                    {
                        accessibilityName = candidate.Name;
                        break;
                    }
                }
            }

            var node = new MarkdownSemanticNode(
                content.AccessibilityRole == MarkdownAccessibilityRole.Math
                    ? MarkdownSemanticRole.Math
                    : MarkdownSemanticRole.Diagram,
                vector)
            {
                VectorSceneBox = vector,
                TextStart = start,
                TextEnd = end,
                AccessibilityRole = content.AccessibilityRole,
                AccessibilityName = accessibilityName,
                AccessibilityDescription = content.AccessibilityDescription,
                AutomationId = string.Concat(
                    content.AccessibilityRole == MarkdownAccessibilityRole.Math ? "MarkdownMath_" : "MarkdownDiagram_",
                    content.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "_",
                    content.SourceSpan.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };

            var semanticNodes = new MarkdownSemanticNode?[vector.Scene.Semantics.Count];
            for (int i = 0; i < semanticNodes.Length; i++)
            {
                MarkdownVectorSemanticItem item = vector.Scene.Semantics[i];
                if (!MarkdownVectorSemanticPolicy.IsExposed(item.Flags) ||
                    (item.ParentIndex < 0 && item.Role == MarkdownVectorSemanticRole.Diagram))
                {
                    continue;
                }
                MarkdownVectorLinkAction? link = vector.Scene.GetLinkAction(i);
                bool invokable = MarkdownVectorSemanticPolicy.IsInvokable(item.Flags, link is not null);
                var child = new MarkdownSemanticNode(invokable
                    ? MarkdownSemanticRole.Link
                    : MarkdownSemanticRole.Group, vector)
                {
                    VectorSceneBox = vector,
                    VectorSemanticIndex = i,
                    VectorSemanticRole = item.Role,
                    VectorSemanticFlags = item.Flags,
                    TextStart = semanticRanges[i]?.Start ?? start,
                    TextEnd = semanticRanges[i]?.End ?? start,
                    AccessibilityRole = invokable
                        ? MarkdownAccessibilityRole.Link
                        : MarkdownAccessibilityRole.Group,
                    AccessibilityName = item.Name,
                    AccessibilityDescription = item.Description,
                    HelpText = link?.Target ?? link?.Action,
                    AutomationId = string.Concat(
                        "MarkdownDiagramItem_",
                        content.SourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "_",
                        item.SourceId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
                };
                semanticNodes[i] = child;
            }

            for (int i = 0; i < semanticNodes.Length; i++)
            {
                MarkdownSemanticNode? child = semanticNodes[i];
                if (child is null)
                    continue;
                int parent = vector.Scene.Semantics[i].ParentIndex;
                while (parent >= 0 && parent < semanticNodes.Length && semanticNodes[parent] is null)
                    parent = vector.Scene.Semantics[parent].ParentIndex;
                if (parent >= 0 && parent < semanticNodes.Length && semanticNodes[parent] is { } parentNode)
                    parentNode.Add(child);
                else
                    node.Add(child);
            }

            AppendBlockSeparator();
            return node;
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
                semanticIndex));
            return (start, end);
        }

        private MarkdownSemanticNode BuildHostedElement(DeclarativeHostedElementBox hosted)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Embed, hosted)
            {
                HostedElementBox = hosted,
                TextStart = _text.Length,
                AccessibilityRole = hosted.AccessibilityRole,
                AccessibilityName = hosted.AccessibilityName,
                AccessibilityDescription = hosted.AccessibilityDescription,
                AutomationId = hosted.AutomationId,
            };
            int start = _text.Length;
            if (hosted.SemanticText is { } semanticText)
                _text.Append(semanticText);
            else
                _text.Append(InlineEmbedRun.PlaceholderChar);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(start, end, null, null, null, null, hosted));
            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildList(StackBox stack)
        {
            int level = ++_listDepth;
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.List, stack)
            {
                TextStart = _text.Length,
                Level = level,
            };

            int sizeOfSet = 0;
            foreach (var child in stack.Children)
            {
                if (child is ListItemBox)
                    sizeOfSet++;
            }

            int position = 0;
            try
            {
                foreach (var child in stack.Children)
                {
                    var childNode = BuildBlock(child);
                    if (childNode is null)
                        continue;

                    if (childNode.Role == MarkdownSemanticRole.ListItem)
                    {
                        childNode.Level = level;
                        childNode.PositionInSet = ++position;
                        childNode.SizeOfSet = sizeOfSet;
                    }
                    node.Add(childNode);
                }
            }
            finally
            {
                _listDepth--;
            }

            node.TextEnd = _text.Length;
            return node;
        }

        private MarkdownSemanticNode BuildListItem(ListItemBox listItem)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.ListItem, listItem)
            {
                TextStart = _text.Length,
            };

            if (BuildBlock(listItem.Marker) is { } marker) node.Add(marker);
            if (BuildBlock(listItem.Content) is { } content) node.Add(content);
            node.TextEnd = _text.Length;
            return node;
        }

        private MarkdownSemanticNode BuildTable(TableBox table)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Table, table)
            {
                TableBox = table,
                TextStart = _text.Length,
                RowCount = table.RowCount,
                ColumnCount = table.ColumnCount,
            };

            foreach (var cellInfo in table.GetCellInfos())
            {
                var cellNode = new MarkdownSemanticNode(MarkdownSemanticRole.TableCell, cellInfo.Box)
                {
                    InlineBox = cellInfo.Box,
                    TextStart = _text.Length,
                    Row = cellInfo.Row,
                    Column = cellInfo.Column,
                    IsHeader = cellInfo.IsHeader,
                };

                if (BuildInline(cellInfo.Box) is { } inlineNode)
                    cellNode.Add(inlineNode);
                cellNode.TextEnd = _text.Length;
                node.Add(cellNode);
            }

            node.TextEnd = _text.Length;
            return node;
        }

        private MarkdownSemanticNode BuildGroup(StackBox stack)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Group, stack)
            {
                TextStart = _text.Length,
            };

            foreach (var child in stack.Children)
            {
                var childNode = BuildBlock(child);
                if (childNode is not null) node.Add(childNode);
            }

            node.TextEnd = _text.Length;
            return node;
        }

        private void AppendBlockSeparator()
        {
            if (_text.Length == 0 || _text[^1] != '\n')
                _text.Append('\n');
        }

        private void TrimTrailingNewline()
        {
            while (_text.Length > 0 && _text[^1] == '\n')
                _text.Length--;
        }

        private static bool IsListStack(StackBox stack)
        {
            if (stack.Children.Count == 0) return false;
            foreach (var child in stack.Children)
            {
                if (child is not ListItemBox) return false;
            }
            return true;
        }

        private static bool IsHeading(string elementKey) =>
            GetHeadingLevel(elementKey) > 0;

        private static int GetHeadingLevel(string elementKey) => elementKey switch
        {
            MarkdownElementKeys.Heading1 => 1,
            MarkdownElementKeys.Heading2 => 2,
            MarkdownElementKeys.Heading3 => 3,
            MarkdownElementKeys.Heading4 => 4,
            MarkdownElementKeys.Heading5 => 5,
            MarkdownElementKeys.Heading6 => 6,
            _ => 0,
        };
    }
}
