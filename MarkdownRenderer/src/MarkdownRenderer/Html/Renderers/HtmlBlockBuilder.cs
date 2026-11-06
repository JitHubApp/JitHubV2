using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Builds the block boxes of safe-HTML content: containers, headings,
/// quotes, lists, rules and media, delegating tables, disclosures and
/// nested Markdown to their owners.
/// </summary>
internal sealed class HtmlBlockBuilder : IHtmlBlockBuilderHost
{
    private readonly SafeHtmlOptions _options;
    private readonly HtmlInlineBuilder _inline;
    private readonly HtmlNestedMarkdown _nestedMarkdown;
    private readonly HtmlTableBuilder _tables;
    private readonly HtmlDetailsBuilder _details;

    internal HtmlBlockBuilder(
        SafeHtmlOptions options,
        HtmlInlineBuilder inline,
        HtmlNestedMarkdown nestedMarkdown)
    {
        _options = options;
        _inline = inline;
        _nestedMarkdown = nestedMarkdown;
        _tables = new HtmlTableBuilder(this, inline);
        _details = new HtmlDetailsBuilder(this, inline, options);
    }

    BlockBox? IHtmlBlockBuilderHost.BuildElementBlock(
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment) =>
        BuildElementBlock(element, context, sourceOffset, inheritedAlignment);

    void IHtmlBlockBuilderHost.AppendBlocks(
        StackBox destination,
        IReadOnlyList<SafeHtmlNode> nodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment) =>
        AppendBlocks(destination, nodes, context, sourceOffset, inheritedAlignment);

    internal void AppendBlocks(
        StackBox destination,
        IReadOnlyList<SafeHtmlNode> nodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment,
        HtmlInlineFragmentMarginPolicy inlineFragmentMarginPolicy = HtmlInlineFragmentMarginPolicy.PreserveExisting)
    {
        List<SafeHtmlNode> inlineNodes = [];
        foreach (SafeHtmlNode node in nodes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (node is SafeHtmlElement element && HtmlElementNames.IsBlockElement(element.Name))
            {
                FlushInlineNodes(
                    destination,
                    inlineNodes,
                    context,
                    sourceOffset,
                    inheritedAlignment,
                    inlineFragmentMarginPolicy,
                    followedByBlock: true);
                BlockBox? block = BuildElementBlock(element, context, sourceOffset, inheritedAlignment);
                if (block is not null)
                {
                    HtmlBoxes.AddChild(destination, block, context);
                }
            }
            else
            {
                inlineNodes.Add(node);
            }
        }

        FlushInlineNodes(
            destination,
            inlineNodes,
            context,
            sourceOffset,
            inheritedAlignment,
            inlineFragmentMarginPolicy,
            followedByBlock: false);
    }

    private void FlushInlineNodes(
        StackBox destination,
        List<SafeHtmlNode> inlineNodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment,
        HtmlInlineFragmentMarginPolicy inlineFragmentMarginPolicy,
        bool followedByBlock)
    {
        if (inlineNodes.Count == 0)
        {
            return;
        }

        InlineContainerBox box = HtmlBoxes.CreateInlineBox(context, MarkdownElementKeys.Body, alignment);
        _inline.PopulateInline(box, inlineNodes, context, sourceOffset, HtmlInlineContext.Empty);
        if (box.Runs.Count > 0)
        {
            ConfigureBreakLayout(
                box,
                preserveParagraphMargin: inlineFragmentMarginPolicy == HtmlInlineFragmentMarginPolicy.Paragraph,
                followedByBlock: followedByBlock);
            if (inlineFragmentMarginPolicy == HtmlInlineFragmentMarginPolicy.AnonymousContainerFlow)
            {
                box.UseContainerMargin = false;
                box.Margin = default;
            }
            HtmlBoxes.AddChild(destination, box, context);
        }
        else
        {
            context.DiscardConstructionBlock(box);
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

        if (!HtmlElementNames.IsSupportedElement(element.Name))
        {
            if (_options.UnknownElementBehavior == SafeHtmlUnknownElementBehavior.RenderLiteral)
            {
                var unknown = HtmlBoxes.CreateInlineBox(context, MarkdownElementKeys.Body, inheritedAlignment);
                HtmlTextRuns.AddLiteralElement(
                    unknown,
                    element,
                    sourceOffset,
                    HtmlInlineContext.Empty with { SourceProjection = context.SourceProjections.SafeHtml });
                return HtmlBoxes.ReturnIfNonEmpty(unknown, context);
            }

            var inline = HtmlBoxes.CreateInlineBox(context, MarkdownElementKeys.Body, inheritedAlignment);
            _inline.PopulateInline(inline, element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            return HtmlBoxes.ReturnIfNonEmpty(inline, context);
        }

        using var classScope = context.Styles.PushAliases(_inline.GetAllowedClassAliases(element));

        SafeHtmlAlignment alignment = HtmlAlignments.EffectiveAlignment(element, inheritedAlignment);
        string? headingKey = HtmlElementNames.HeadingKey(element.Name);
        if (headingKey is not null)
        {
            InlineContainerBox heading = HtmlBoxes.CreateInlineBox(context, headingKey, alignment);
            _inline.PopulateInline(heading, element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            return HtmlBoxes.ReturnIfNonEmpty(heading, context);
        }

        if (element.Name is "video" or "audio")
        {
            InlineContainerBox media = HtmlBoxes.CreateInlineBox(
                context,
                MarkdownElementKeys.Body,
                alignment);
            HtmlMediaRuns.AddMediaRun(media, element, context, sourceOffset, HtmlInlineContext.Empty);
            return HtmlBoxes.ReturnIfNonEmpty(media, context);
        }

        return element.Name switch
        {
            "hr" => HtmlBoxes.TrackBlock(context, new ThematicBreakBox(context) { BlockIndex = context.NextBlockIndex() }),
            "table" => _tables.BuildTable(element, context, sourceOffset, alignment),
            "blockquote" => BuildQuote(element, context, sourceOffset, alignment),
            "pre" => HtmlPreformattedBlock.BuildPreformatted(element, context, sourceOffset, alignment),
            "details" => _details.BuildDetails(element, context, sourceOffset, alignment),
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
        BlockBox? markdownContainer = _nestedMarkdown.TryBuildMarkdownContainer(
            element,
            context,
            sourceOffset,
            alignment);
        if (markdownContainer is not null)
        {
            return markdownContainer;
        }

        if (!element.Children.Any(node => node is SafeHtmlElement child && HtmlElementNames.IsBlockElement(child.Name)))
        {
            InlineContainerBox inline = HtmlBoxes.CreateInlineBox(context, MarkdownElementKeys.Body, alignment);
            _inline.PopulateInline(inline, element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            ConfigureBreakLayout(
                inline,
                preserveParagraphMargin: element.Name == "p");
            if (element.Name != "p")
            {
                // Body styling supplies Markdown paragraph spacing. A plain
                // safe-HTML container is a block boundary, but its anonymous
                // inline contents are not a paragraph of their own.
                inline.UseContainerMargin = false;
                inline.Margin = default;
            }
            return HtmlBoxes.ReturnIfNonEmpty(inline, context);
        }

        var stack = HtmlBoxes.CreateStack(context, collapsePositiveAdjacentMargins: true);
        AppendBlocks(
            stack,
            element.Children,
            context,
            sourceOffset,
            alignment,
            element.Name == "p"
                ? HtmlInlineFragmentMarginPolicy.Paragraph
                : HtmlInlineFragmentMarginPolicy.AnonymousContainerFlow);
        return HtmlBoxes.ReturnIfNonEmpty(stack, context);
    }

    private static void ConfigureBreakLayout(
        InlineContainerBox box,
        bool preserveParagraphMargin,
        bool followedByBlock = false)
    {
        if (box.Runs.Count == 1 && box.Runs[0] is LineBreakRun standaloneBreak)
        {
            // A standalone <br> in an HTML block establishes one empty line box;
            // its enclosing HTML <p>, when present, still owns the paragraph margin.
            // Keep semantic text/source mapping intact while laying out a same-length
            // blank rather than letting DirectWrite append another empty terminal line.
            standaloneBreak.UseSingleLineLayout = true;
            if (!preserveParagraphMargin)
            {
                box.UseContainerMargin = false;
                box.Margin = default;
            }
            return;
        }

        if (followedByBlock &&
            box.Runs.Count > 0 &&
            box.Runs[^1] is LineBreakRun terminalBreak &&
            terminalBreak.Text == "\n")
        {
            // A block starts at the line created by the final hard break. If the
            // inline layout also materializes that terminal line as a full empty
            // row, the block is pushed down one extra line. Use the break's
            // same-length layout blank only at this flush boundary; earlier breaks
            // remain hard breaks and all semantic/source ranges remain unchanged.
            terminalBreak.UseSingleLineLayout = true;
        }
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
        var stack = HtmlBoxes.CreateStack(context);
        stack.ContentPadding = style.Padding;
        stack.AccentBar = style.AccentBar;
        stack.Background = style.Background;
        stack.BorderBrush = style.BorderBrush;
        stack.BorderThickness = style.BorderThickness;
        stack.CornerRadius = style.CornerRadius;
        stack.Margin = style.Margin;
        stack.FlowDirection = context.FlowDirection;
        using var scope = context.Styles.PushContext(MarkdownElementKeys.Quote);
        AppendBlocks(stack, quote.Children, context, sourceOffset, alignment);
        return HtmlBoxes.ReturnIfNonEmpty(stack, context);
    }

    private BlockBox? BuildHtmlList(
        SafeHtmlElement list,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment,
        bool ordered)
    {
        using var listScope = context.Styles.PushListDepth();
        var stack = HtmlBoxes.CreateStack(context);
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
            Math.Max(0, context.Styles.ListDepth - 1) * listStyle.NestedListIndent);
        foreach (SafeHtmlElement item in list.Children.OfType<SafeHtmlElement>().Where(element => element.Name == "li"))
        {
            InlineContainerBox marker = HtmlBoxes.CreateInlineBox(
                context,
                MarkdownElementKeys.ListMarker,
                alignment);
            marker.Add(new TextRun(ordered ? $"{index}." : "\u2022")
            {
                ElementKey = MarkdownElementKeys.ListMarker,
                SourceSpan = context.MapSafeHtmlSourceRange(sourceOffset + item.SourceStart, 0),
            });

            StackBox content = HtmlBoxes.CreateStack(context);
            AppendBlocks(content, item.Children, context, sourceOffset, alignment);
            if (content.Children.Count > 0)
            {
                var listItem = new ListItemBox(marker, content, markerWidth)
                {
                    BlockIndex = context.NextBlockIndex(),
                    FlowDirection = context.FlowDirection,
                };
                context.RegisterConstructionBlock(listItem);
                context.AdoptConstructionBlock(marker);
                context.AdoptConstructionBlock(content);
                HtmlBoxes.AddChild(stack, listItem, context);
            }
            else
            {
                context.DiscardConstructionBlock(content);
                context.DiscardConstructionBlock(marker);
            }

            index++;
        }

        return HtmlBoxes.ReturnIfNonEmpty(stack, context);
    }
}
