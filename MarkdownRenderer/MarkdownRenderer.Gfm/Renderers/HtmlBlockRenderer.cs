using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders the safe HTML subset GitHub READMEs commonly use for presentation.
/// Active content and unsafe URL schemes are never materialized.
/// </summary>
public sealed class HtmlBlockRenderer : MarkdownNodeRenderer<HtmlBlock>
{
    private const int MaxTableColumns = 64;

    /// <inheritdoc />
    public override BlockBox? BuildBlock(HtmlBlock htmlBlock, MarkdownLayoutContext context)
    {
        SafeHtmlDocument document = SafeHtmlParser.Parse(htmlBlock.Lines.ToString());
        var root = CreateStack(context);
        AppendBlocks(root, document.Root.Children, context, htmlBlock.Span.Start, SafeHtmlAlignment.Inherit);

        if (document.IsTruncated)
        {
            var notice = CreateInlineBox(context, MarkdownElementKeys.Body, SafeHtmlAlignment.Inherit);
            notice.Add(new TextRun("Additional HTML content was omitted because it exceeded the renderer safety limit.")
            {
                SourceSpan = SourceSpan.Empty,
            });
            root.Add(notice);
        }

        return root.Children.Count == 0 ? null : root;
    }

    private static StackBox CreateStack(MarkdownLayoutContext context) => new()
    {
        BlockIndex = context.NextBlockIndex(),
        FlowDirection = context.FlowDirection,
    };

    private static void AppendBlocks(
        StackBox destination,
        IReadOnlyList<SafeHtmlNode> nodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment)
    {
        List<SafeHtmlNode> inlineNodes = [];
        foreach (SafeHtmlNode node in nodes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (node is SafeHtmlElement element && IsBlockElement(element.Name))
            {
                FlushInlineNodes(destination, inlineNodes, context, sourceOffset, inheritedAlignment);
                BlockBox? block = BuildElementBlock(element, context, sourceOffset, inheritedAlignment);
                if (block is not null)
                {
                    destination.Add(block);
                }
            }
            else
            {
                inlineNodes.Add(node);
            }
        }

        FlushInlineNodes(destination, inlineNodes, context, sourceOffset, inheritedAlignment);
    }

    private static void FlushInlineNodes(
        StackBox destination,
        List<SafeHtmlNode> inlineNodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        if (inlineNodes.Count == 0)
        {
            return;
        }

        InlineContainerBox box = CreateInlineBox(context, MarkdownElementKeys.Body, alignment);
        PopulateInline(box, inlineNodes, context, sourceOffset, HtmlInlineContext.Empty);
        if (box.Runs.Count > 0)
        {
            destination.Add(box);
        }

        inlineNodes.Clear();
    }

    private static BlockBox? BuildElementBlock(
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment)
    {
        if (SafeHtmlParser.IsSuppressedElement(element.Name))
        {
            return null;
        }

        SafeHtmlAlignment alignment = EffectiveAlignment(element, inheritedAlignment);
        string? headingKey = HeadingKey(element.Name);
        if (headingKey is not null)
        {
            InlineContainerBox heading = CreateInlineBox(context, headingKey, alignment);
            PopulateInline(heading, element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            return heading.Runs.Count == 0 ? null : heading;
        }

        return element.Name switch
        {
            "hr" => new ThematicBreakBox(context) { BlockIndex = context.NextBlockIndex() },
            "table" => BuildTable(element, context, sourceOffset),
            "blockquote" => BuildQuote(element, context, sourceOffset, alignment),
            "pre" => BuildPreformatted(element, context, sourceOffset, alignment),
            "details" => BuildDetails(element, context, sourceOffset, alignment),
            "ul" => BuildHtmlList(element, context, sourceOffset, alignment, ordered: false),
            "ol" => BuildHtmlList(element, context, sourceOffset, alignment, ordered: true),
            _ => BuildContainer(element, context, sourceOffset, alignment),
        };
    }

    private static BlockBox? BuildContainer(
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        if (!element.Children.Any(node => node is SafeHtmlElement child && IsBlockElement(child.Name)))
        {
            InlineContainerBox inline = CreateInlineBox(context, MarkdownElementKeys.Body, alignment);
            PopulateInline(inline, element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            return inline.Runs.Count == 0 ? null : inline;
        }

        var stack = CreateStack(context);
        AppendBlocks(stack, element.Children, context, sourceOffset, alignment);
        return stack.Children.Count == 0 ? null : stack;
    }

    private static BlockBox? BuildDetails(
        SafeHtmlElement details,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        var stack = CreateStack(context);
        SafeHtmlElement? summary = details.Children
            .OfType<SafeHtmlElement>()
            .FirstOrDefault(element => element.Name == "summary");
        int absoluteSourceStart = sourceOffset + details.SourceStart;
        string disclosureId = absoluteSourceStart.ToString(CultureInfo.InvariantCulture);
        bool defaultExpanded = details.TryGetAttribute("open", out _);
        bool expanded = context.IsDisclosureExpanded(disclosureId, defaultExpanded);
        string summaryText = summary is null
            ? "Details"
            : SafeHtmlParser.CollapseWhitespace(string.Concat(DescendantText(summary).Select(node => node.DecodedText))).Trim();
        if (summaryText.Length == 0)
        {
            summaryText = "Details";
        }

        InlineContainerBox summaryBox = CreateInlineBox(context, MarkdownElementKeys.Strong, alignment);
        summaryBox.Add(new LinkRun(
            $"{(expanded ? "\u25BE" : "\u25B8")} {summaryText}",
            $"markdown-disclosure:{disclosureId}")
        {
            AccessibilityName = summaryText,
            DisclosureId = disclosureId,
            ElementKey = MarkdownElementKeys.Strong,
            IsExpanded = expanded,
            SourceSpan = new SourceSpan(
                sourceOffset + (summary?.SourceStart ?? details.SourceStart),
                summary?.SourceLength ?? details.SourceLength),
        });
        stack.Add(summaryBox);

        if (!expanded)
        {
            return stack;
        }

        IReadOnlyList<SafeHtmlNode> body = details.Children.Where(node => !ReferenceEquals(node, summary)).ToArray();
        var bodyStack = CreateStack(context);
        float indent = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).ListIndent;
        bodyStack.ContentPadding = context.FlowDirection == FlowDirection.RightToLeft
            ? new Thickness(0, 0, indent, 0)
            : new Thickness(indent, 0, 0, 0);
        AppendBlocks(bodyStack, body, context, sourceOffset, alignment);
        if (bodyStack.Children.Count > 0)
        {
            stack.Add(bodyStack);
        }

        return stack.Children.Count == 0 ? null : stack;
    }

    private static BlockBox? BuildQuote(
        SafeHtmlElement quote,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        ElementStyle style = context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.Quote,
            context.CreateStyleContextSnapshot(),
            context.CreateStyleAliasSnapshot());
        var stack = new StackBox
        {
            BlockIndex = context.NextBlockIndex(),
            ContentPadding = style.Padding,
            AccentBar = style.AccentBar,
            Background = style.Background,
            BorderBrush = style.BorderBrush,
            BorderThickness = style.BorderThickness,
            CornerRadius = style.CornerRadius,
            Margin = style.Margin,
            FlowDirection = context.FlowDirection,
        };
        using var scope = context.PushStyleContext(MarkdownElementKeys.Quote);
        AppendBlocks(stack, quote.Children, context, sourceOffset, alignment);
        return stack.Children.Count == 0 ? null : stack;
    }

    private static BlockBox? BuildPreformatted(
        SafeHtmlElement pre,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        string text = string.Concat(DescendantText(pre).Select(node => node.DecodedText));
        if (text.Length == 0)
        {
            return null;
        }

        InlineContainerBox box = CreateInlineBox(context, MarkdownElementKeys.CodeBlock, alignment);
        box.Add(new TextRun(text)
        {
            SourceSpan = new SourceSpan(sourceOffset + pre.SourceStart, pre.SourceLength),
        });
        return box;
    }

    private static BlockBox? BuildHtmlList(
        SafeHtmlElement list,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment,
        bool ordered)
    {
        var stack = CreateStack(context);
        int index = 1;
        foreach (SafeHtmlElement item in list.Children.OfType<SafeHtmlElement>().Where(element => element.Name == "li"))
        {
            InlineContainerBox box = CreateInlineBox(context, MarkdownElementKeys.Body, alignment);
            string marker = ordered ? $"{index}. " : "\u2022 ";
            box.Add(new TextRun(marker)
            {
                ElementKey = MarkdownElementKeys.ListMarker,
                SourceSpan = new SourceSpan(sourceOffset + item.SourceStart, 0),
            });
            PopulateInline(box, item.Children, context, sourceOffset, HtmlInlineContext.Empty);
            if (box.Runs.Count > 1)
            {
                stack.Add(box);
            }

            index++;
        }

        return stack.Children.Count == 0 ? null : stack;
    }

    private static BlockBox? BuildTable(
        SafeHtmlElement table,
        MarkdownLayoutContext context,
        int sourceOffset)
    {
        List<HtmlTableRow> rows = [];
        CollectRows(table, inHeaderGroup: false, rows);
        int columnCount = Math.Min(
            MaxTableColumns,
            rows.Count == 0 ? 0 : rows.Max(row => row.Cells.Sum(cell => cell.ColumnSpan)));
        if (columnCount == 0)
        {
            return null;
        }

        List<InlineContainerBox[]> headerRows = [];
        List<InlineContainerBox[]> bodyRows = [];
        var alignments = new TableBox.CellAlignment[columnCount];
        foreach (HtmlTableRow row in rows)
        {
            var cells = new InlineContainerBox[columnCount];
            int column = 0;
            foreach (HtmlTableCell cell in row.Cells)
            {
                if (column >= columnCount)
                {
                    break;
                }

                string key = row.IsHeader ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell;
                InlineContainerBox box = CreateInlineBox(context, key, SafeHtmlParser.GetAlignment(cell.Element));
                PopulateInline(box, cell.Element.Children, context, sourceOffset, HtmlInlineContext.Empty);
                cells[column] = box;
                if (alignments[column] == TableBox.CellAlignment.Default)
                {
                    alignments[column] = ToCellAlignment(SafeHtmlParser.GetAlignment(cell.Element));
                }

                column++;
                for (int span = 1; span < cell.ColumnSpan && column < columnCount; span++, column++)
                {
                    cells[column] = CreateInlineBox(context, key, SafeHtmlAlignment.Inherit);
                }
            }

            for (; column < columnCount; column++)
            {
                cells[column] = CreateInlineBox(
                    context,
                    row.IsHeader ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell,
                    SafeHtmlAlignment.Inherit);
            }

            (row.IsHeader ? headerRows : bodyRows).Add(cells);
        }

        return new TableBox(context, headerRows.ToArray(), bodyRows.ToArray(), alignments)
        {
            BlockIndex = context.NextBlockIndex(),
        };
    }

    private static void CollectRows(SafeHtmlElement element, bool inHeaderGroup, List<HtmlTableRow> rows)
    {
        bool headerGroup = inHeaderGroup || element.Name == "thead";
        if (element.Name == "tr")
        {
            List<HtmlTableCell> cells = [];
            bool hasHeaderCell = false;
            foreach (SafeHtmlElement cell in element.Children
                         .OfType<SafeHtmlElement>()
                         .Where(child => child.Name is "td" or "th"))
            {
                hasHeaderCell |= cell.Name == "th";
                int span = 1;
                if (cell.TryGetAttribute("colspan", out string value) && int.TryParse(value, out int parsed))
                {
                    span = Math.Clamp(parsed, 1, MaxTableColumns);
                }

                cells.Add(new HtmlTableCell(cell, span));
            }

            if (cells.Count > 0)
            {
                rows.Add(new HtmlTableRow(cells, headerGroup || hasHeaderCell));
            }

            return;
        }

        foreach (SafeHtmlElement child in element.Children.OfType<SafeHtmlElement>())
        {
            CollectRows(child, headerGroup, rows);
        }
    }

    private static InlineContainerBox CreateInlineBox(
        MarkdownLayoutContext context,
        string elementKey,
        SafeHtmlAlignment alignment)
    {
        var box = new InlineContainerBox(context, elementKey)
        {
            BlockIndex = context.NextBlockIndex(),
            TextAlignment = ToCanvasAlignment(alignment),
        };
        return box;
    }

    private static void PopulateInline(
        InlineContainerBox box,
        IReadOnlyList<SafeHtmlNode> nodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        foreach (SafeHtmlNode node in nodes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case SafeHtmlText text:
                    AddTextRun(box, text, sourceOffset, inlineContext);
                    break;
                case SafeHtmlElement element:
                    AddInlineElement(box, element, context, sourceOffset, inlineContext);
                    break;
            }
        }
    }

    private static void AddTextRun(
        InlineContainerBox box,
        SafeHtmlText text,
        int sourceOffset,
        HtmlInlineContext context)
    {
        string value = SafeHtmlParser.CollapseWhitespace(text.RawText);
        if (value.Length == 0)
        {
            return;
        }

        var span = new SourceSpan(sourceOffset + text.SourceStart, text.SourceLength);
        InlineRun run = context.LinkUrl is { Length: > 0 } href
            ? new LinkRun(value, href, context.LinkTitle)
            {
                IsSuperscript = context.StyleKey == MarkdownElementKeys.Superscript,
            }
            : CreateStyledTextRun(value, context.StyleKey);
        run.SourceSpan = span;
        box.Add(run);
    }

    private static void AddInlineElement(
        InlineContainerBox box,
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        if (SafeHtmlParser.IsSuppressedElement(element.Name) || element.Name is "form" or "input" or "button")
        {
            return;
        }

        var span = new SourceSpan(sourceOffset + element.SourceStart, element.SourceLength);
        if (element.Name == "br")
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
            return;
        }

        if (element.Name == "hr")
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
            return;
        }

        if (element.Name is "img" or "picture")
        {
            AddImageRun(box, element, context, sourceOffset, inlineContext);
            return;
        }

        if (element.Name == "source")
        {
            return;
        }

        HtmlInlineContext childContext = inlineContext;
        if (element.Name == "a" && SafeHtmlParser.TryGetSafeLink(element, out string href))
        {
            element.TryGetAttribute("title", out string title);
            childContext = childContext with
            {
                LinkUrl = href,
                LinkTitle = string.IsNullOrWhiteSpace(title) ? null : title,
            };
        }

        string? styleKey = SafeHtmlInlineState.GetStyleKey(element.Name);
        if (!string.IsNullOrEmpty(styleKey))
        {
            childContext = childContext with { StyleKey = styleKey };
        }

        bool blockBoundary = IsBlockElement(element.Name);
        if (blockBoundary && box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
        }

        PopulateInline(box, element.Children, context, sourceOffset, childContext);

        if (blockBoundary && box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
        {
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
        }
    }

    private static void AddImageRun(
        InlineContainerBox box,
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        SafeHtmlElement? image = element.Name == "img" ? element : FindDescendant(element, "img");
        if (image is null)
        {
            return;
        }

        SafeHtmlElement? selectedSource = element.Name == "picture"
            ? SelectPictureSource(element, context.ThemeSnapshot.IsDark)
            : null;
        string source = string.Empty;
        if (selectedSource is not null && selectedSource.TryGetAttribute("srcset", out string sourceSet))
        {
            string candidate = sourceSet.Split(',')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            SafeHtmlParser.TryNormalizeImageSource(candidate, out source);
        }

        if (source.Length == 0 && !SafeHtmlParser.TryGetSafeImageSource(image, out source))
        {
            if (image.TryGetAttribute("alt", out string unavailableAlt) && !string.IsNullOrWhiteSpace(unavailableAlt))
            {
                var fallback = new SafeHtmlText(unavailableAlt, image.SourceStart, image.SourceLength);
                AddTextRun(box, fallback, sourceOffset, inlineContext);
            }

            return;
        }

        image.TryGetAttribute("alt", out string alt);
        image.TryGetAttribute("title", out string title);
        SafeHtmlLength? width = GetImageLength(selectedSource, image, "width");
        SafeHtmlLength? height = GetImageLength(selectedSource, image, "height");
        var run = new InlineImageRun(
            context,
            string.IsNullOrWhiteSpace(alt) ? "image" : alt,
            source,
            string.IsNullOrWhiteSpace(title) ? null : title,
            inlineContext.LinkUrl,
            inlineContext.LinkTitle,
            width,
            height)
        {
            SourceSpan = new SourceSpan(sourceOffset + image.SourceStart, image.SourceLength),
        };
        box.Add(run);
    }

    private static SafeHtmlLength? GetImageLength(
        SafeHtmlElement? source,
        SafeHtmlElement image,
        string name)
    {
        if (source is not null && SafeHtmlParser.TryGetLength(source, name, out SafeHtmlLength sourceLength))
        {
            return sourceLength;
        }

        return SafeHtmlParser.TryGetLength(image, name, out SafeHtmlLength imageLength)
            ? imageLength
            : null;
    }

    private static SafeHtmlElement? SelectPictureSource(SafeHtmlElement picture, bool isDark)
    {
        string expected = isDark ? "dark" : "light";
        foreach (SafeHtmlElement source in picture.Children.OfType<SafeHtmlElement>().Where(element => element.Name == "source"))
        {
            if (source.TryGetAttribute("media", out string media) &&
                NormalizeMediaQuery(media).Contains($"prefers-color-scheme:{expected}", StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    private static string NormalizeMediaQuery(string media) =>
        string.Concat(media.Where(character => !char.IsWhiteSpace(character)));

    private static SafeHtmlElement? FindDescendant(SafeHtmlElement parent, string name)
    {
        foreach (SafeHtmlElement child in parent.Children.OfType<SafeHtmlElement>())
        {
            if (child.Name == name)
            {
                return child;
            }

            SafeHtmlElement? nested = FindDescendant(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<SafeHtmlText> DescendantText(SafeHtmlElement parent)
    {
        foreach (SafeHtmlNode child in parent.Children)
        {
            if (child is SafeHtmlText text)
            {
                yield return text;
            }
            else if (child is SafeHtmlElement element && !SafeHtmlParser.IsSuppressedElement(element.Name))
            {
                foreach (SafeHtmlText nested in DescendantText(element))
                {
                    yield return nested;
                }
            }
        }
    }

    private static InlineRun CreateStyledTextRun(string text, string? styleKey) => styleKey switch
    {
        MarkdownElementKeys.CodeInline => new CodeInlineRun(text),
        MarkdownElementKeys.Strong => new StrongRun(text),
        MarkdownElementKeys.Emphasis => new EmphasisRun(text),
        MarkdownElementKeys.Strikethrough => new StrikethroughRun(text),
        MarkdownElementKeys.Subscript => new SubscriptRun(text),
        MarkdownElementKeys.Superscript => new SuperscriptRun(text),
        MarkdownElementKeys.Inserted => new InsertedRun(text),
        MarkdownElementKeys.Marked => new MarkedRun(text),
        _ => new TextRun(text),
    };

    private static SafeHtmlAlignment EffectiveAlignment(
        SafeHtmlElement element,
        SafeHtmlAlignment inherited)
    {
        if (element.Name == "center")
        {
            return SafeHtmlAlignment.Center;
        }

        SafeHtmlAlignment own = SafeHtmlParser.GetAlignment(element);
        return own == SafeHtmlAlignment.Inherit ? inherited : own;
    }

    private static CanvasHorizontalAlignment ToCanvasAlignment(SafeHtmlAlignment alignment) => alignment switch
    {
        SafeHtmlAlignment.Center => CanvasHorizontalAlignment.Center,
        SafeHtmlAlignment.Right => CanvasHorizontalAlignment.Right,
        _ => CanvasHorizontalAlignment.Left,
    };

    private static TableBox.CellAlignment ToCellAlignment(SafeHtmlAlignment alignment) => alignment switch
    {
        SafeHtmlAlignment.Left => TableBox.CellAlignment.Left,
        SafeHtmlAlignment.Center => TableBox.CellAlignment.Center,
        SafeHtmlAlignment.Right => TableBox.CellAlignment.Right,
        _ => TableBox.CellAlignment.Default,
    };

    private static string? HeadingKey(string name) => name switch
    {
        "h1" => MarkdownElementKeys.Heading1,
        "h2" => MarkdownElementKeys.Heading2,
        "h3" => MarkdownElementKeys.Heading3,
        "h4" => MarkdownElementKeys.Heading4,
        "h5" => MarkdownElementKeys.Heading5,
        "h6" => MarkdownElementKeys.Heading6,
        _ => null,
    };

    private static bool IsBlockElement(string name) => name is
        "address" or "article" or "aside" or "blockquote" or "center" or "details" or "div" or
        "figcaption" or "figure" or "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or
        "h6" or "header" or "hr" or "main" or "nav" or "ol" or "p" or "pre" or "section" or
        "summary" or "table" or "ul";

    private readonly record struct HtmlInlineContext(string? LinkUrl, string? LinkTitle, string? StyleKey)
    {
        public static HtmlInlineContext Empty => new(null, null, null);
    }

    private sealed record HtmlTableCell(SafeHtmlElement Element, int ColumnSpan);

    private sealed record HtmlTableRow(IReadOnlyList<HtmlTableCell> Cells, bool IsHeader);
}
