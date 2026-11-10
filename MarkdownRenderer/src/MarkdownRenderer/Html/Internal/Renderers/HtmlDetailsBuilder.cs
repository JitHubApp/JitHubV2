using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Builds a safe-HTML details disclosure: its summary toggle, the visible
/// summary code and media, and the body when expanded.
/// </summary>
internal sealed class HtmlDetailsBuilder
{
    private readonly IHtmlBlockBuilderHost _host;
    private readonly HtmlInlineBuilder _inline;
    private readonly SafeHtmlOptions _options;

    internal HtmlDetailsBuilder(
        IHtmlBlockBuilderHost host,
        HtmlInlineBuilder inline,
        SafeHtmlOptions options)
    {
        _host = host;
        _inline = inline;
        _options = options;
    }

    internal BlockBox? BuildDetails(
        SafeHtmlElement details,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        var stack = HtmlBoxes.CreateStack(context);
        Thickness bodyMargin = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).Margin;
        stack.Margin = new Thickness(0, 0, 0, Math.Max(0, bodyMargin.Bottom * 2));
        SafeHtmlElement? summary = details.Children
            .OfType<SafeHtmlElement>()
            .FirstOrDefault(element => element.Name == "summary");
        int absoluteSourceStart = sourceOffset + details.SourceStart;
        string disclosureId = context.MapSafeHtmlSourceRange(absoluteSourceStart, 0).Start
            .ToString(CultureInfo.InvariantCulture);
        bool defaultExpanded = details.TryGetAttribute("open", out _);
        bool expanded = context.IsDisclosureExpanded(disclosureId, defaultExpanded);
        AppendSummaryToggle(stack, details, summary, disclosureId, expanded, context, sourceOffset, alignment);

        if (summary is not null)
            AppendSummaryCode(stack, summary, context, sourceOffset, alignment);

        if (summary is not null && _options.EnableImages)
            AppendSummaryMedia(stack, summary, context, sourceOffset, alignment);

        if (!expanded)
        {
            return stack;
        }

        AppendExpandedBody(stack, details, summary, context, sourceOffset, alignment);
        return HtmlBoxes.ReturnIfNonEmpty(stack, context);
    }

    private static void AppendSummaryToggle(
        StackBox stack,
        SafeHtmlElement details,
        SafeHtmlElement? summary,
        string disclosureId,
        bool expanded,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        string defaultSummary = context.ResolveString(
            MarkdownStringKeys.HtmlDetails,
            MarkdownLocalizedStrings.HtmlDetails);
        string summaryText = summary is null
            ? defaultSummary
            : SafeHtmlParser.CollapseDecodedWhitespace(string.Concat(
                HtmlElementQueries.DescendantSummaryLabelText(summary).Select(node => node.DecodedText))).Trim();
        if (summaryText.Length == 0)
        {
            summaryText = defaultSummary;
        }

        string? summaryHeadingKey = summary is null
            ? null
            : HtmlElementQueries.FindDescendantHeadingKey(summary);
        string summaryStyleKey = summaryHeadingKey ?? MarkdownElementKeys.Strong;
        InlineContainerBox summaryBox = HtmlBoxes.CreateInlineBox(context, summaryStyleKey, alignment);
        summaryBox.Add(new LinkRun(
            $"{(expanded ? "▾" : "▸")} {summaryText}",
            $"markdown-disclosure:{disclosureId}")
        {
            AccessibilityName = summaryText,
            DisclosureId = disclosureId,
            ElementKey = summaryStyleKey,
            IsExpanded = expanded,
            SourceSpan = context.MapSafeHtmlSourceRange(
                sourceOffset + (summary?.SourceStart ?? details.SourceStart),
                summary?.SourceLength ?? details.SourceLength),
        });
        HtmlBoxes.AddChild(stack, summaryBox, context);
    }

    private static void AppendSummaryCode(
        StackBox stack,
        SafeHtmlElement summary,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        // GitHub permits fenced Markdown inside <summary> and emits it as a
        // nested <pre>. A closed disclosure still displays the complete
        // summary, so flattening that code into the disclosure label both
        // loses its layout and removes its code-block accessibility
        // semantics. Keep the compact textual label as the toggle and
        // publish each visible preformatted descendant exactly once.
        foreach (SafeHtmlElement preformatted in HtmlElementQueries.DescendantPreformatted(summary))
        {
            BlockBox? codeBlock = HtmlPreformattedBlock.BuildPreformatted(
                preformatted,
                context,
                sourceOffset,
                alignment);
            if (codeBlock is not null)
                HtmlBoxes.AddChild(stack, codeBlock, context);
        }
    }

    private void AppendSummaryMedia(
        StackBox stack,
        SafeHtmlElement summary,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        // A disclosure summary may contain diagrams or badges. They remain
        // visible when <details> is closed, just like multiline Markdown
        // content in the summary. Build the inline content once to retain
        // picture selection and link metadata, then publish only its atomic
        // media runs because textual descendants are already represented by
        // the disclosure control above.
        InlineContainerBox summaryContent = HtmlBoxes.CreateInlineBox(
            context,
            MarkdownElementKeys.Body,
            alignment);
        _inline.PopulateInline(
            summaryContent,
            summary.Children,
            context,
            sourceOffset,
            HtmlInlineContext.Empty);
        InlineRun[] summaryVisualRuns = summaryContent.DetachInlineImagesAndSpacersForTransfer();
        foreach (InlineRun visualRun in summaryVisualRuns)
        {
            if (visualRun is InlineImageRun image)
                context.RegisterConstructionBlock(image.Image);
        }
        if (summaryVisualRuns.Length > 0)
        {
            InlineContainerBox summaryMedia = HtmlBoxes.CreateInlineBox(
                context,
                MarkdownElementKeys.Body,
                alignment);
            foreach (InlineRun visualRun in summaryVisualRuns)
                HtmlBoxes.AddRun(summaryMedia, visualRun);
            HtmlBoxes.AddChild(stack, summaryMedia, context);
        }

        // The summary box was only a staging owner for atomic visual runs.
        // Detached image resources now belong to the summary media box;
        // dispose remaining temporary text/vector runs.
        context.DiscardConstructionBlock(summaryContent);
    }

    private void AppendExpandedBody(
        StackBox stack,
        SafeHtmlElement details,
        SafeHtmlElement? summary,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        IReadOnlyList<SafeHtmlNode> body = details.Children.Where(node => !ReferenceEquals(node, summary)).ToArray();
        var bodyStack = HtmlBoxes.CreateStack(context);
        float indent = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).ListIndent;
        bodyStack.ContentPadding = context.FlowDirection == FlowDirection.RightToLeft
            ? new Thickness(0, 0, indent, 0)
            : new Thickness(indent, 0, 0, 0);
        _host.AppendBlocks(bodyStack, body, context, sourceOffset, alignment);
        if (bodyStack.Children.Count > 0)
        {
            HtmlBoxes.AddChild(stack, bodyStack, context);
        }
        else
        {
            context.DiscardConstructionBlock(bodyStack);
        }
    }
}
