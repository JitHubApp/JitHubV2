using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Html;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Renders the safe HTML subset GitHub READMEs commonly use for presentation.
/// Active content and unsafe URL schemes are never materialized.
/// </summary>
internal sealed class HtmlBlockRenderer : MarkdownNodeRenderer<HtmlBlock>
{
    private const int MaxTableColumns = 64;
    private readonly SafeHtmlOptions _options;

    internal HtmlBlockRenderer(SafeHtmlOptions? options = null)
    {
        _options = options ?? SafeHtmlOptions.Default;
    }

    /// <inheritdoc />
    public override BlockBox? BuildBlock(HtmlBlock htmlBlock, MarkdownLayoutContext context)
    {
        SafeHtmlBudgets budgets = _options.Budgets;
        SafeHtmlDocument document = SafeHtmlParser.Parse(
            htmlBlock.Lines.ToString(),
            new SafeHtmlParseLimits(
                budgets.MaxInputLength,
                budgets.MaxNodeCount,
                 budgets.MaxNestingDepth,
                 budgets.MaxAttributeCount,
                 budgets.MaxAttributeValueLength,
                 budgets.MaxTagLength),
            context.CancellationToken);
        var root = CreateStack(context);
        AppendBlocks(root, document.Root.Children, context, htmlBlock.Span.Start, SafeHtmlAlignment.Inherit);

        if (document.IsTruncated)
        {
            var notice = CreateInlineBox(context, MarkdownElementKeys.Body, SafeHtmlAlignment.Inherit);
            notice.Add(new TextRun(context.ResolveString(
                MarkdownStringKeys.HtmlBudgetExceeded,
                MarkdownLocalizedStrings.HtmlBudgetExceeded))
            {
                SourceSpan = SourceSpan.Empty,
            });
            root.Add(notice);
        }

        // A configured safe-HTML renderer owns every HtmlBlock, including blocks
        // that intentionally produce no visual content (comments, declarations,
        // and suppressed active elements). Returning null would invoke the core
        // literal fallback and leak that hidden markup into the document.
        return root;
    }

    private static StackBox CreateStack(MarkdownLayoutContext context) => new()
    {
        BlockIndex = context.NextBlockIndex(),
        FlowDirection = context.FlowDirection,
    };

    private void AppendBlocks(
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

    private void FlushInlineNodes(
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

    private BlockBox? BuildElementBlock(
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment)
    {
        if (SafeHtmlParser.IsSuppressedElement(element.Name))
        {
            return null;
        }

        if (element.Name is "form" or "input" or "button")
            return null;

        if (!IsSupportedElement(element.Name))
        {
            var unknown = CreateInlineBox(context, MarkdownElementKeys.Body, inheritedAlignment);
            if (_options.UnknownElementBehavior == SafeHtmlUnknownElementBehavior.RenderLiteral)
                AddLiteralElement(unknown, element, sourceOffset, HtmlInlineContext.Empty);
            else
                PopulateInline(unknown, element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            return unknown.Runs.Count == 0 ? null : unknown;
        }

        using var classScope = context.PushStyleAliases(GetAllowedClassAliases(element));

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

    private BlockBox? BuildContainer(
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

    private BlockBox? BuildDetails(
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
        string defaultSummary = context.ResolveString(
            MarkdownStringKeys.HtmlDetails,
            MarkdownLocalizedStrings.HtmlDetails);
        string summaryText = summary is null
            ? defaultSummary
            : SafeHtmlParser.CollapseWhitespace(string.Concat(DescendantText(summary).Select(node => node.DecodedText))).Trim();
        if (summaryText.Length == 0)
        {
            summaryText = defaultSummary;
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

    private BlockBox? BuildQuote(
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

    private BlockBox? BuildHtmlList(
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

    private BlockBox? BuildTable(
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

    private void PopulateInline(
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
        string decoded = text.DecodedText;
        bool leadingWhitespace = decoded.Length > 0 && IsCollapsibleWhitespace(decoded[0]);
        bool trailingWhitespace = decoded.Length > 0 && IsCollapsibleWhitespace(decoded[^1]);
        bool atLineStart = box.Runs.Count == 0 || box.Runs[^1] is LineBreakRun;
        bool previousEndsWithWhitespace = !atLineStart &&
            box.Runs[^1].Text.Length > 0 &&
            char.IsWhiteSpace(box.Runs[^1].Text[^1]);

        // HTML collapses whitespace across element boundaries, not separately
        // inside each text node. Retain one boundary space around styled/link
        // elements while still discarding indentation at the start of a block.
        if (leadingWhitespace && !atLineStart && !previousEndsWithWhitespace)
            value = " " + value;
        if (trailingWhitespace && value.Length > 0 && !char.IsWhiteSpace(value[^1]))
            value += " ";

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
        run.SetStyleAliases(context.StyleAliases);
        box.Add(run);
    }

    private static bool IsCollapsibleWhitespace(char value) =>
        value != '\u00A0' && char.IsWhiteSpace(value);

    private void AddInlineElement(
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

        if (!IsSupportedElement(element.Name))
        {
            if (_options.UnknownElementBehavior == SafeHtmlUnknownElementBehavior.RenderLiteral)
                AddLiteralElement(box, element, sourceOffset, inlineContext);
            else
                PopulateInline(box, element.Children, context, sourceOffset, inlineContext);
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
            if (!_options.EnableImages)
            {
                SafeHtmlElement? image = element.Name == "img" ? element : FindDescendant(element, "img");
                if (image is not null &&
                    image.TryGetAttribute("alt", out string disabledAlt) &&
                    !string.IsNullOrWhiteSpace(disabledAlt))
                {
                    AddTextRun(
                        box,
                        new SafeHtmlText(disabledAlt, image.SourceStart, image.SourceLength),
                        sourceOffset,
                        inlineContext);
                }

                return;
            }

            AddImageRun(box, element, context, sourceOffset, inlineContext);
            return;
        }

        if (element.Name == "source")
        {
            return;
        }

        HtmlInlineContext childContext = inlineContext;
        IReadOnlyList<string> classAliases = GetAllowedClassAliases(element);
        if (classAliases.Count > 0)
        {
            childContext = childContext with
            {
                StyleAliases = CombineAliases(childContext.StyleAliases, classAliases),
            };
        }

        if (_options.EnableLinks &&
            element.Name == "a" &&
            SafeHtmlParser.TryGetSafeLink(element, out string href))
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

    private void AddImageRun(
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
            string.IsNullOrWhiteSpace(alt)
                ? context.ResolveString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName)
                : alt,
            source,
            string.IsNullOrWhiteSpace(title) ? null : title,
            inlineContext.LinkUrl,
            inlineContext.LinkTitle,
            width,
            height)
        {
            SourceSpan = new SourceSpan(sourceOffset + image.SourceStart, image.SourceLength),
        };
        run.SetStyleAliases(inlineContext.StyleAliases);
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

    private static bool IsSupportedElement(string name) => name is
        "a" or "address" or "article" or "aside" or "b" or "blockquote" or "br" or
        "caption" or "center" or "cite" or "code" or "col" or "colgroup" or "del" or
        "details" or "div" or "em" or "figcaption" or "figure" or "footer" or "h1" or
        "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "hr" or "i" or "img" or
        "ins" or "kbd" or "li" or "main" or "mark" or "nav" or "ol" or "p" or
        "picture" or "pre" or "s" or "samp" or "section" or "small" or "source" or
        "span" or "strike" or "strong" or "sub" or "summary" or "sup" or "table" or
        "tbody" or "td" or "tfoot" or "th" or "thead" or "tr" or "u" or "ul" or "var";

    private static void AddLiteralElement(
        InlineContainerBox box,
        SafeHtmlElement element,
        int sourceOffset,
        HtmlInlineContext context)
    {
        AddLiteralText(
            box,
            element.RawOpeningTag,
            sourceOffset + element.SourceStart,
            context);

        if (element.RawTrailingMarkup.Length > 0)
        {
            AddLiteralText(
                box,
                element.RawTrailingMarkup,
                sourceOffset + element.RawTrailingStart,
                context);
        }
        else
        {
            foreach (SafeHtmlNode child in element.Children)
            {
                if (child is SafeHtmlText text)
                {
                    AddLiteralText(box, text.RawText, sourceOffset + text.SourceStart, context);
                }
                else if (child is SafeHtmlElement childElement)
                {
                    AddLiteralElement(box, childElement, sourceOffset, context);
                }
            }

            AddLiteralText(
                box,
                element.RawClosingTag,
                sourceOffset + element.RawClosingStart,
                context);
        }
    }

    private static void AddLiteralText(
        InlineContainerBox box,
        string text,
        int sourceStart,
        HtmlInlineContext context)
    {
        if (text.Length == 0)
            return;

        InlineRun run = context.LinkUrl is { Length: > 0 } href
            ? new LinkRun(text, href, context.LinkTitle)
            : new TextRun(text);
        run.SourceSpan = new SourceSpan(sourceStart, text.Length);
        run.SetStyleAliases(context.StyleAliases);
        box.Add(run);
    }

    private IReadOnlyList<string> GetAllowedClassAliases(SafeHtmlElement element)
    {
        if (_options.AllowedStyleClasses.Count == 0 ||
            !element.TryGetAttribute("class", out string value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var aliases = new List<string>();
        foreach (string token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_options.AllowedStyleClasses.Contains(token))
                aliases.Add(MarkdownElementKeys.Class(token));
        }

        return aliases.Count == 0 ? Array.Empty<string>() : aliases.ToArray();
    }

    private static IReadOnlyList<string> CombineAliases(
        IReadOnlyList<string> inherited,
        IReadOnlyList<string> local)
    {
        if (inherited.Count == 0)
            return local;
        if (local.Count == 0)
            return inherited;

        var combined = new string[inherited.Count + local.Count];
        for (int index = 0; index < inherited.Count; index++)
            combined[index] = inherited[index];
        for (int index = 0; index < local.Count; index++)
            combined[inherited.Count + index] = local[index];
        return combined;
    }

    private readonly record struct HtmlInlineContext(
        string? LinkUrl,
        string? LinkTitle,
        string? StyleKey,
        IReadOnlyList<string> StyleAliases)
    {
        public static HtmlInlineContext Empty => new(null, null, null, Array.Empty<string>());
    }

    private sealed record HtmlTableCell(SafeHtmlElement Element, int ColumnSpan);

    private sealed record HtmlTableRow(IReadOnlyList<HtmlTableCell> Cells, bool IsHeader);
}
