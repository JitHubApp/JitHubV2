using System;
using System.Collections.Generic;
using System.Text;
using Windows.Foundation;
using MarkdownRenderer.Document;
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
    Embed,
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
    public bool IsHeader { get; init; }

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

            return Box?.Bounds ?? InlineBox?.Bounds ?? ImageBox?.Bounds ?? EmbedBox?.Bounds ?? default;
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
    ImageBox? ImageBox,
    EmbedBox? EmbedBox);

internal sealed class MarkdownSemanticDocument
{
    private MarkdownSemanticDocument(MarkdownSemanticNode root, string text, IReadOnlyList<MarkdownTextSpan> spans)
    {
        Root = root;
        Text = text;
        TextSpans = spans;
    }

    public MarkdownSemanticNode Root { get; }
    public string Text { get; }
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
        foreach (var span in TextSpans)
        {
            if (span.InlineBox is { } icb && icb.BlockIndex == position.BlockIndex)
            {
                return Math.Clamp(span.TextStart + icb.GetBufferCharOffset(position), span.TextStart, span.TextEnd);
            }
        }

        foreach (var span in TextSpans)
        {
            if (span.InlineBox?.BlockIndex == position.BlockIndex ||
                span.ImageBox?.BlockIndex == position.BlockIndex ||
                span.EmbedBox?.BlockIndex == position.BlockIndex)
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
        MarkdownTextSpan? previous = null;
        foreach (var span in TextSpans)
        {
            if (textOffset >= span.TextStart && textOffset <= span.TextEnd)
            {
                if (span.InlineBox is { } icb)
                {
                    int bufferOffset = Math.Clamp(textOffset - span.TextStart, 0, Math.Max(0, span.TextEnd - span.TextStart));
                    return icb.GetPositionFromBufferOffset(bufferOffset);
                }

                if (span.ImageBox is { } image)
                    return new DocumentPosition(image.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);

                if (span.EmbedBox is { } embed)
                    return new DocumentPosition(embed.BlockIndex, 0, textOffset <= span.TextStart ? 0 : 1);
            }

            if (textOffset < span.TextStart && previous is not null)
                return PositionFromSpanEnd(previous);

            previous = span;
        }

        return previous is not null ? PositionFromSpanEnd(previous) : DocumentPosition.Zero;
    }

    public IEnumerable<Rect> GetDocumentRects(int textStart, int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, Text.Length);

        if (textStart == textEnd && Text.Length > 0)
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

            if (span.InlineBox is { } icb)
            {
                var startPos = icb.GetPositionFromBufferOffset(start - span.TextStart);
                var endPos = icb.GetPositionFromBufferOffset(end - span.TextStart);
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
            if (node.Role is MarkdownSemanticRole.Link or MarkdownSemanticRole.Image or MarkdownSemanticRole.Embed or
                MarkdownSemanticRole.Table or MarkdownSemanticRole.TableCell or MarkdownSemanticRole.List or MarkdownSemanticRole.ListItem)
            {
                yield return node;
            }
        }
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
            return icb.GetPositionFromBufferOffset(span.TextEnd - span.TextStart);
        if (span.ImageBox is { } image)
            return new DocumentPosition(image.BlockIndex, 0, 1);
        if (span.EmbedBox is { } embed)
            return new DocumentPosition(embed.BlockIndex, 0, 1);
        return DocumentPosition.Zero;
    }

    private sealed class Builder
    {
        private readonly StringBuilder _text = new();
        private readonly List<MarkdownTextSpan> _spans = new();

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
                ImageBox image => BuildImage(image),
                EmbedBox embed => BuildEmbed(embed),
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
                    ? MarkdownLocalizedStrings.CodeLanguageHelp(inline.CodeLanguage)
                    : null,
            };

            int spanStart = _text.Length;
            foreach (var run in inline.Runs)
                _text.Append(run.Text);
            int spanEnd = _text.Length;
            _spans.Add(new MarkdownTextSpan(spanStart, spanEnd, inline, null, null));
            node.TextEnd = _text.Length;

            foreach (var run in inline.Runs)
            {
                if (run is LinkRun link)
                {
                    node.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Link, inline)
                    {
                        InlineBox = inline,
                        InlineRun = link,
                        TextStart = spanStart + inline.GetBufferCharOffset(new DocumentPosition(inline.BlockIndex, run.InlineIndex, 0)),
                        TextEnd = spanStart + inline.GetBufferCharOffset(new DocumentPosition(inline.BlockIndex, run.InlineIndex, run.Text.Length)),
                        HelpText = link.Url,
                    });
                }
                else if (run is InlineEmbedRun embedRun)
                {
                    node.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Embed, inline)
                    {
                        InlineBox = inline,
                        InlineRun = embedRun,
                        TextStart = spanStart + inline.GetBufferCharOffset(new DocumentPosition(inline.BlockIndex, run.InlineIndex, 0)),
                        TextEnd = spanStart + inline.GetBufferCharOffset(new DocumentPosition(inline.BlockIndex, run.InlineIndex, run.Text.Length)),
                    });
                }
                else if (run is InlineImageRun imageRun)
                {
                    node.Add(new MarkdownSemanticNode(MarkdownSemanticRole.Image, inline)
                    {
                        InlineBox = inline,
                        InlineRun = imageRun,
                        TextStart = spanStart + inline.GetBufferCharOffset(new DocumentPosition(inline.BlockIndex, run.InlineIndex, 0)),
                        TextEnd = spanStart + inline.GetBufferCharOffset(new DocumentPosition(inline.BlockIndex, run.InlineIndex, run.Text.Length)),
                        HelpText = !string.IsNullOrWhiteSpace(imageRun.Title) ? imageRun.Title : imageRun.Url,
                    });
                }
            }

            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildImage(ImageBox image)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.Image, image)
            {
                ImageBox = image,
                TextStart = _text.Length,
                HelpText = image.SvgDesc,
            };

            string name = !string.IsNullOrWhiteSpace(image.Alt)
                ? image.Alt
                : !string.IsNullOrWhiteSpace(image.SvgTitle)
                    ? image.SvgTitle!
                    : MarkdownLocalizedStrings.ImageName;
            int start = _text.Length;
            _text.Append(name);
            int end = _text.Length;
            _spans.Add(new MarkdownTextSpan(start, end, null, image, null));
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
            _spans.Add(new MarkdownTextSpan(start, end, null, null, embed));
            node.TextEnd = _text.Length;
            AppendBlockSeparator();
            return node;
        }

        private MarkdownSemanticNode BuildList(StackBox stack)
        {
            var node = new MarkdownSemanticNode(MarkdownSemanticRole.List, stack)
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
