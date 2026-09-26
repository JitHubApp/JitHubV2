using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Html.Internal;
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
    private const int MaxNestedMarkdownDepth = 4;
    private const string GitHubMarkdownContainersFeature =
        "MarkdownRenderer.GitHub.MarkdownInHtmlContainers";
    [ThreadStatic]
    private static int s_nestedMarkdownDepth;
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

        if (element.Name is "form" or "button")
            return null;

        if (!IsSupportedElement(element.Name))
        {
            if (_options.UnknownElementBehavior == SafeHtmlUnknownElementBehavior.RenderLiteral)
            {
                var unknown = CreateInlineBox(context, MarkdownElementKeys.Body, inheritedAlignment);
                AddLiteralElement(unknown, element, sourceOffset, HtmlInlineContext.Empty);
                return unknown.Runs.Count == 0 ? null : unknown;
            }

            var inline = CreateInlineBox(context, MarkdownElementKeys.Body, inheritedAlignment);
            PopulateInline(inline, element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            return inline.Runs.Count == 0 ? null : inline;
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

        if (element.Name is "video" or "audio")
        {
            InlineContainerBox media = CreateInlineBox(
                context,
                MarkdownElementKeys.Body,
                alignment);
            AddMediaRun(media, element, context, sourceOffset, HtmlInlineContext.Empty);
            return media.Runs.Count == 0 ? null : media;
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
        BlockBox? markdownContainer = TryBuildMarkdownContainer(
            element,
            context,
            sourceOffset,
            alignment);
        if (markdownContainer is not null)
        {
            return markdownContainer;
        }

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

    private static BlockBox? TryBuildMarkdownContainer(
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        if (!context.Registry.HasPresentationFeature(GitHubMarkdownContainersFeature) ||
            element.Name is not ("article" or "aside" or "center" or "div" or "main" or "section") ||
            element.Children.Count != 1 ||
            element.Children[0] is not SafeHtmlText text ||
            string.IsNullOrWhiteSpace(text.RawText) ||
            s_nestedMarkdownDepth >= MaxNestedMarkdownDepth)
        {
            return null;
        }

        // CommonMark deliberately treats the contents of a type-6 HTML block as
        // raw text. GitHub README rendering permits Markdown in these inert
        // presentation containers, which is heavily used for centered badge and
        // sponsor groups. Reparse only the text child, under the same immutable
        // pipeline and a strict recursion ceiling; active HTML remains governed
        // by the safe-HTML policy on every nested pass.
        s_nestedMarkdownDepth++;
        try
        {
            MarkdownDocument fragment = Markdown.Parse(
                text.RawText,
                context.Registry.BuildPipeline());
            OffsetMarkdownSpans(fragment, sourceOffset + text.SourceStart);
            var stack = CreateStack(context);
            GfmChildBuilder.PopulateChildren(stack, fragment, context);
            ApplyTextAlignment(stack, ToCanvasAlignment(alignment));
            return stack.Children.Count == 0 ? null : stack;
        }
        finally
        {
            s_nestedMarkdownDepth--;
        }
    }

    private static void OffsetMarkdownSpans(ContainerBlock container, int offset)
    {
        OffsetMarkdownSpan(container, offset);
        foreach (Block block in container)
        {
            if (block is ContainerBlock childContainer)
            {
                OffsetMarkdownSpans(childContainer, offset);
            }
            else
            {
                OffsetMarkdownSpan(block, offset);
                if (block is LeafBlock { Inline: not null } leaf)
                {
                    OffsetInlineSpans(leaf.Inline, offset);
                }
            }
        }
    }

    private static void OffsetInlineSpans(ContainerInline container, int offset)
    {
        OffsetMarkdownSpan(container, offset);
        foreach (Inline inline in container)
        {
            if (inline is ContainerInline child)
            {
                OffsetInlineSpans(child, offset);
            }
            else
            {
                OffsetMarkdownSpan(inline, offset);
            }
        }
    }

    private static void ApplyTextAlignment(BlockBox block, CanvasHorizontalAlignment alignment)
    {
        if (block is InlineContainerBox inline)
        {
            inline.TextAlignment = alignment;
            return;
        }

        if (block is not StackBox stack)
        {
            return;
        }

        foreach (BlockBox child in stack.Children)
        {
            ApplyTextAlignment(child, alignment);
        }
    }

    private static void OffsetMarkdownSpan(MarkdownObject node, int offset)
    {
        if (node.Span.Start < 0)
        {
            return;
        }

        node.Span = new Markdig.Syntax.SourceSpan(
            checked(node.Span.Start + offset),
            checked(node.Span.End + offset));
    }

    private BlockBox? BuildDetails(
        SafeHtmlElement details,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        var stack = CreateStack(context);
        Thickness bodyMargin = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).Margin;
        stack.Margin = new Thickness(0, 0, 0, Math.Max(0, bodyMargin.Bottom * 2));
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
            : SafeHtmlParser.CollapseWhitespace(string.Concat(
                DescendantSummaryLabelText(summary).Select(node => node.DecodedText))).Trim();
        if (summaryText.Length == 0)
        {
            summaryText = defaultSummary;
        }

        string? summaryHeadingKey = summary is null
            ? null
            : FindDescendantHeadingKey(summary);
        string summaryStyleKey = summaryHeadingKey ?? MarkdownElementKeys.Strong;
        InlineContainerBox summaryBox = CreateInlineBox(context, summaryStyleKey, alignment);
        summaryBox.Add(new LinkRun(
            $"{(expanded ? "\u25BE" : "\u25B8")} {summaryText}",
            $"markdown-disclosure:{disclosureId}")
        {
            AccessibilityName = summaryText,
            DisclosureId = disclosureId,
            ElementKey = summaryStyleKey,
            IsExpanded = expanded,
            SourceSpan = new SourceSpan(
                sourceOffset + (summary?.SourceStart ?? details.SourceStart),
                summary?.SourceLength ?? details.SourceLength),
        });
        stack.Add(summaryBox);

        if (summary is not null)
        {
            // GitHub permits fenced Markdown inside <summary> and emits it as a
            // nested <pre>. A closed disclosure still displays the complete
            // summary, so flattening that code into the disclosure label both
            // loses its layout and removes its code-block accessibility
            // semantics. Keep the compact textual label as the toggle and
            // publish each visible preformatted descendant exactly once.
            foreach (SafeHtmlElement preformatted in DescendantPreformatted(summary))
            {
                BlockBox? codeBlock = BuildPreformatted(
                    preformatted,
                    context,
                    sourceOffset,
                    alignment);
                if (codeBlock is not null)
                    stack.Add(codeBlock);
            }
        }

        if (summary is not null && _options.EnableImages)
        {
            // A disclosure summary may contain diagrams or badges. They remain
            // visible when <details> is closed, just like multiline Markdown
            // content in the summary. Build the inline content once to retain
            // picture selection and link metadata, then publish only its atomic
            // media runs because textual descendants are already represented by
            // the disclosure control above.
            InlineContainerBox summaryContent = CreateInlineBox(
                context,
                MarkdownElementKeys.Body,
                alignment);
            PopulateInline(
                summaryContent,
                summary.Children,
                context,
                sourceOffset,
                HtmlInlineContext.Empty);
            InlineImageRun[] summaryImages = summaryContent.Runs
                .OfType<InlineImageRun>()
                .ToArray();
            if (summaryImages.Length > 0)
            {
                InlineContainerBox summaryMedia = CreateInlineBox(
                    context,
                    MarkdownElementKeys.Body,
                    alignment);
                foreach (InlineImageRun image in summaryImages)
                    summaryMedia.Add(image);
                stack.Add(summaryMedia);
            }
        }

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

        text = CodeBlockMetadata.NormalizeCodeLineEndings(text);
        var sourceSpan = new SourceSpan(sourceOffset + pre.SourceStart, pre.SourceLength);
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            sourceSpan,
            text,
            language: null,
            new Dictionary<string, string>());
        var codeBlock = new CodeBlockBox(
            context,
            metadata,
            text,
            context.IsCodeBlockCopyEnabled,
            showLineNumbers: false)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
        {
            BlockIndex = context.NextBlockIndex(),
            TextAlignment = ToCanvasAlignment(alignment),
            CodeBlockTextOffset = 0,
            CodeBlockTextLength = text.Length,
        };
        chunk.Add(new TextRun(text) { SourceSpan = sourceSpan });
        codeBlock.AddChunk(chunk);
        return codeBlock;
    }

    private BlockBox? BuildHtmlList(
        SafeHtmlElement list,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment,
        bool ordered)
    {
        using var listScope = context.PushListDepth();
        var stack = CreateStack(context);
        int index = 1;
        if (ordered &&
            list.TryGetAttribute("start", out string rawStart) &&
            int.TryParse(rawStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStart))
        {
            index = parsedStart;
        }

        ElementStyle listStyle = context.ThemeSnapshot.GetStyle(
            MarkdownElementKeys.ListMarker,
            context.CreateStyleContextSnapshot(),
            context.CreateStyleAliasSnapshot());
        float markerWidth = Math.Max(
            1f,
            listStyle.ListIndent +
            Math.Max(0, context.ListDepth - 1) * listStyle.NestedListIndent);
        foreach (SafeHtmlElement item in list.Children.OfType<SafeHtmlElement>().Where(element => element.Name == "li"))
        {
            InlineContainerBox marker = CreateInlineBox(
                context,
                MarkdownElementKeys.ListMarker,
                alignment);
            marker.Add(new TextRun(ordered ? $"{index}." : "\u2022")
            {
                ElementKey = MarkdownElementKeys.ListMarker,
                SourceSpan = new SourceSpan(sourceOffset + item.SourceStart, 0),
            });

            StackBox content = CreateStack(context);
            AppendBlocks(content, item.Children, context, sourceOffset, alignment);
            if (content.Children.Count > 0)
            {
                stack.Add(new ListItemBox(marker, content, markerWidth)
                {
                    BlockIndex = context.NextBlockIndex(),
                    FlowDirection = context.FlowDirection,
                });
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
                SafeHtmlElement? preformatted = FindDescendant(cell.Element, "pre");
                bool hasMixedContent = preformatted is not null &&
                    HasRenderableContentOutside(cell.Element, preformatted);
                InlineContainerBox box = preformatted is null || hasMixedContent
                    ? CreateInlineBox(context, key, SafeHtmlParser.GetAlignment(cell.Element))
                    : BuildPreformattedTableCell(
                        preformatted,
                        context,
                        sourceOffset,
                        SafeHtmlParser.GetAlignment(cell.Element));
                if (preformatted is null || hasMixedContent)
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

    private static InlineContainerBox BuildPreformattedTableCell(
        SafeHtmlElement pre,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        string text = CodeBlockMetadata.NormalizeCodeLineEndings(
            string.Concat(DescendantText(pre).Select(static node => node.DecodedText)));
        var sourceSpan = new SourceSpan(sourceOffset + pre.SourceStart, pre.SourceLength);
        var box = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
        {
            BlockIndex = context.NextBlockIndex(),
            TextAlignment = ToCanvasAlignment(alignment),
            CodeBlockTextOffset = 0,
            CodeBlockTextLength = text.Length,
        };
        if (text.Length > 0)
        {
            box.Add(new TextRun(text)
            {
                ElementKey = MarkdownElementKeys.CodeBlock,
                SourceSpan = sourceSpan,
            });
        }

        return box;
    }

    private static bool HasRenderableContentOutside(
        SafeHtmlElement root,
        SafeHtmlElement excludedPreformatted)
    {
        foreach (SafeHtmlNode child in root.Children)
        {
            if (ReferenceEquals(child, excludedPreformatted))
                continue;

            if (child is SafeHtmlText text)
            {
                if (!string.IsNullOrWhiteSpace(text.DecodedText))
                    return true;
                continue;
            }

            if (child is not SafeHtmlElement element ||
                SafeHtmlParser.IsSuppressedElement(element.Name))
            {
                continue;
            }

            if (element.Name is "img" or "picture" or "video" or "audio" or "svg" or "input" or "hr" ||
                HasRenderableContentOutside(element, excludedPreformatted))
            {
                return true;
            }
        }

        return false;
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

        // Indentation around an image inside an anchor is layout whitespace,
        // not a second hyperlink. Keeping it as a LinkRun creates a blank UIA
        // link beside the linked-image peer and inflates link navigation.
        if (context.LinkUrl is { Length: > 0 } && string.IsNullOrWhiteSpace(value))
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
        run.SemanticHeadingKey = context.HeadingKey ?? string.Empty;
        if (run is LinkRun && !string.IsNullOrEmpty(context.StyleKey))
            run.StyleModifierKeys = new[] { context.StyleKey };
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
        if (SafeHtmlParser.IsSuppressedElement(element.Name) || element.Name is "form" or "button")
        {
            return;
        }

        if (element.Name == "input")
        {
            AddInputRun(box, element, context, sourceOffset);
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

        if (element.Name == "pre")
        {
            AddPreformattedInline(box, element, sourceOffset, inlineContext);
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

        if (element.Name is "video" or "audio")
        {
            AddMediaRun(box, element, context, sourceOffset, inlineContext);
            return;
        }

        if (element.Name == "svg")
        {
            // GitHub appends a decorative permalink Octicon to every rendered
            // heading. It is explicitly excluded from the accessibility tree
            // and is browser chrome rather than README content; materializing
            // it as an atomic linked image adds a visible replacement glyph
            // and a duplicate hyperlink to the native document.
            bool isAriaHidden = element.TryGetAttribute("aria-hidden", out string ariaHidden) &&
                ariaHidden.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (_options.EnableImages && !isAriaHidden)
                AddInlineSvgRun(box, element, context, sourceOffset, inlineContext);
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

        string? headingKey = HeadingKey(element.Name);
        if (!string.IsNullOrEmpty(headingKey))
            childContext = childContext with { HeadingKey = headingKey };

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

    private static void AddPreformattedInline(
        InlineContainerBox box,
        SafeHtmlElement pre,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        var span = new SourceSpan(sourceOffset + pre.SourceStart, pre.SourceLength);
        if (box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });

        string text = CodeBlockMetadata.NormalizeCodeLineEndings(
            string.Concat(DescendantText(pre).Select(static node => node.DecodedText)));
        if (text.Length > 0)
        {
            var run = new TextRun(text)
            {
                ElementKey = MarkdownElementKeys.CodeBlock,
                SourceSpan = span,
                SemanticHeadingKey = inlineContext.HeadingKey ?? string.Empty,
            };
            run.SetStyleAliases(inlineContext.StyleAliases);
            box.Add(run);
        }

        if (box.Runs.Count > 0 && box.Runs[^1] is not LineBreakRun)
            box.Add(new LineBreakRun(isHard: true) { SourceSpan = span });
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

        bool hasExplicitAlt = image.TryGetAttribute("alt", out string alt);
        image.TryGetAttribute("title", out string title);
        SafeHtmlLength? width = GetImageLength(selectedSource, image, "width");
        SafeHtmlLength? height = GetImageLength(selectedSource, image, "height");
        var run = new InlineImageRun(
            context,
            !hasExplicitAlt
                ? SafeHtmlParser.ResolveMissingImageAlternative(
                    source,
                    context.ResolveString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName))
                : alt,
            source,
            string.IsNullOrWhiteSpace(title) ? null : title,
            inlineContext.LinkUrl,
            inlineContext.LinkTitle,
            width,
            height)
        {
            SourceSpan = new SourceSpan(sourceOffset + image.SourceStart, image.SourceLength),
            SemanticHeadingKey = inlineContext.HeadingKey ?? string.Empty,
        };
        run.SetStyleAliases(inlineContext.StyleAliases);
        box.Add(run);
    }

    private static void AddInputRun(
        InlineContainerBox box,
        SafeHtmlElement input,
        MarkdownLayoutContext context,
        int sourceOffset)
    {
        if (!input.TryGetAttribute("type", out string type) ||
            !type.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool isChecked = input.TryGetAttribute("checked", out _);
        var sourceRange = new SourceSpan(sourceOffset + input.SourceStart, input.SourceLength);
        box.Add(TaskMarkerControlFactory.CreateReadOnlyRun(
            context,
            isChecked,
            sourceRange));
    }

    private static void AddMediaRun(
        InlineContainerBox box,
        SafeHtmlElement media,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        SafeHtmlElement sourceElement = media;
        if (!SafeHtmlParser.TryGetSafeImageSource(sourceElement, out string mediaSource))
        {
            sourceElement = FindDescendant(media, "source") ?? media;
            if (!SafeHtmlParser.TryGetSafeImageSource(sourceElement, out mediaSource))
                mediaSource = string.Empty;
        }

        string? accessibilityName = media.TryGetAttribute("aria-label", out string ariaLabel) &&
            !string.IsNullOrWhiteSpace(ariaLabel)
                ? ariaLabel.Trim()
                : media.TryGetAttribute("title", out string title) && !string.IsNullOrWhiteSpace(title)
                    ? title.Trim()
                    : null;
        bool isVideo = media.Name == "video";
        SafeHtmlLength? width = SafeHtmlParser.TryGetLength(media, "width", out SafeHtmlLength parsedWidth)
            ? parsedWidth
            : null;
        SafeHtmlLength? height = SafeHtmlParser.TryGetLength(media, "height", out SafeHtmlLength parsedHeight)
            ? parsedHeight
            : null;
        string? posterSource = media.TryGetAttribute("poster", out string poster) &&
            SafeHtmlParser.TryNormalizeImageSource(poster, out string safePoster)
                ? safePoster
                : null;
        InlineImageRun run = SafeHtmlMediaRunFactory.Create(
            context,
            isVideo,
            accessibilityName,
            mediaSource,
            posterSource,
            width,
            height,
            new SourceSpan(sourceOffset + media.SourceStart, media.SourceLength),
            inlineContext.LinkUrl,
            inlineContext.LinkTitle);
        run.SetStyleAliases(inlineContext.StyleAliases);
        box.Add(run);
    }

    private static void AddInlineSvgRun(
        InlineContainerBox box,
        SafeHtmlElement svg,
        MarkdownLayoutContext context,
        int sourceOffset,
        HtmlInlineContext inlineContext)
    {
        // Inline SVG is inert image content, not HTML UI. Preserve the exact
        // authored bytes and route them through the same bounded SVG preflight
        // and isolated renderer as ordinary Markdown images. This keeps script,
        // foreignObject, nested network/file references, and resource bombs
        // subject to one security policy instead of duplicating an SVG parser
        // in the safe-HTML layer.
        if (svg.RawOpeningTag.Length == 0 || svg.RawClosingTag.Length == 0)
            return;

        var markup = new StringBuilder(Math.Max(64, svg.SourceLength));
        AppendRawMarkup(markup, svg);
        if (markup.Length == 0)
            return;

        string dataUri = "data:image/svg+xml;base64," +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(markup.ToString()));
        string accessibilityName = GetSvgAccessibilityName(svg);
        SafeHtmlLength? width = SafeHtmlParser.TryGetLength(svg, "width", out SafeHtmlLength parsedWidth)
            ? parsedWidth
            : null;
        SafeHtmlLength? height = SafeHtmlParser.TryGetLength(svg, "height", out SafeHtmlLength parsedHeight)
            ? parsedHeight
            : null;
        var run = new InlineImageRun(
            context,
            accessibilityName,
            dataUri,
            title: null,
            inlineContext.LinkUrl,
            inlineContext.LinkTitle,
            width,
            height)
        {
            SourceSpan = new SourceSpan(sourceOffset + svg.SourceStart, svg.SourceLength),
        };
        run.SetStyleAliases(inlineContext.StyleAliases);
        box.Add(run);
    }

    private static void AppendRawMarkup(StringBuilder destination, SafeHtmlElement element)
    {
        destination.Append(element.RawOpeningTag);
        if (element.RawTrailingMarkup.Length > 0)
        {
            destination.Append(element.RawTrailingMarkup);
            return;
        }

        foreach (SafeHtmlNode child in element.Children)
        {
            if (child is SafeHtmlText text)
                destination.Append(text.RawText);
            else if (child is SafeHtmlElement nested)
                AppendRawMarkup(destination, nested);
        }
        destination.Append(element.RawClosingTag);
    }

    private static string GetSvgAccessibilityName(SafeHtmlElement svg)
    {
        if (svg.TryGetAttribute("aria-label", out string ariaLabel) &&
            !string.IsNullOrWhiteSpace(ariaLabel))
        {
            return ariaLabel.Trim();
        }

        SafeHtmlElement? title = FindDescendant(svg, "title");
        return title is null
            ? string.Empty
            : string.Concat(DescendantText(title).Select(static text => text.DecodedText)).Trim();
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

    private static IEnumerable<SafeHtmlText> DescendantSummaryLabelText(SafeHtmlElement parent)
    {
        foreach (SafeHtmlNode child in parent.Children)
        {
            if (child is SafeHtmlText text)
            {
                yield return text;
                continue;
            }

            if (child is not SafeHtmlElement element ||
                SafeHtmlParser.IsSuppressedElement(element.Name) ||
                element.Name == "pre")
            {
                continue;
            }

            foreach (SafeHtmlText nested in DescendantSummaryLabelText(element))
                yield return nested;
        }
    }

    private static IEnumerable<SafeHtmlElement> DescendantPreformatted(SafeHtmlElement parent)
    {
        foreach (SafeHtmlElement child in parent.Children.OfType<SafeHtmlElement>())
        {
            if (SafeHtmlParser.IsSuppressedElement(child.Name))
                continue;

            if (child.Name == "pre")
            {
                yield return child;
                continue;
            }

            foreach (SafeHtmlElement nested in DescendantPreformatted(child))
                yield return nested;
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
        "summary" or "table" or "ul" or "video" or "audio";

    private static bool IsSupportedElement(string name) => name is
        "a" or "address" or "article" or "aside" or "b" or "blockquote" or "br" or
        "caption" or "center" or "cite" or "code" or "col" or "colgroup" or "del" or
        "details" or "div" or "em" or "figcaption" or "figure" or "footer" or "h1" or
        "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "hr" or "i" or "img" or
        "ins" or "kbd" or "li" or "main" or "mark" or "nav" or "ol" or "p" or
        "picture" or "pre" or "s" or "samp" or "section" or "small" or "source" or
        "span" or "strike" or "strong" or "sub" or "summary" or "sup" or "svg" or "table" or
        "tbody" or "td" or "tfoot" or "th" or "thead" or "tr" or "u" or "ul" or "var" or
        "video" or "audio" or "input";

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
        string? HeadingKey,
        IReadOnlyList<string> StyleAliases)
    {
        public static HtmlInlineContext Empty => new(null, null, null, null, Array.Empty<string>());
    }

    private static string? FindDescendantHeadingKey(SafeHtmlElement parent)
    {
        foreach (SafeHtmlElement child in parent.Children.OfType<SafeHtmlElement>())
        {
            string? key = HeadingKey(child.Name);
            if (key is not null)
                return key;

            key = FindDescendantHeadingKey(child);
            if (key is not null)
                return key;
        }

        return null;
    }

    private sealed record HtmlTableCell(SafeHtmlElement Element, int ColumnSpan);

    private sealed record HtmlTableRow(IReadOnlyList<HtmlTableCell> Cells, bool IsHeader);
}
