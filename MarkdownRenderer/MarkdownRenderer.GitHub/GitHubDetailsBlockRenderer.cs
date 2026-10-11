using System.Globalization;
using System.Linq;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.GitHub;

/// <summary>Renders a native, accessible disclosure for a details block.</summary>
internal sealed class GitHubDetailsBlockRenderer : MarkdownNodeRenderer<GitHubDetailsBlock>
{
    public override BlockBox? BuildBlock(GitHubDetailsBlock details, MarkdownLayoutContext context)
    {
        string disclosureId = details.Span.Start.ToString(CultureInfo.InvariantCulture);
        bool expanded = context.IsDisclosureExpanded(disclosureId, details.IsOpenByDefault);
        string summaryText = ParseSummary(details.SummaryMarkup, context);
        var stack = new StackBox
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = context.FlowDirection,
            Margin = GetDisclosureMargin(context),
        };
        context.RegisterConstructionBlock(stack);
        var summary = new InlineContainerBox(context, MarkdownElementKeys.Strong)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        context.RegisterConstructionBlock(summary);
        summary.Add(new LinkRun(
            $"{(expanded ? "\u25BE" : "\u25B8")} {summaryText}",
            $"markdown-disclosure:{disclosureId}")
        {
            AccessibilityName = summaryText,
            DisclosureId = disclosureId,
            ElementKey = MarkdownElementKeys.Strong,
            IsExpanded = expanded,
            SourceSpan = new SourceSpan(details.Span.Start, details.Span.Length),
        });
        stack.Add(summary);
        context.AdoptConstructionBlock(summary);

        AppendSummarySupplementalContent(stack, details, context);

        if (!expanded)
        {
            return stack;
        }

        var body = new StackBox
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = context.FlowDirection,
        };
        float indent = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).ListIndent;
        body.ContentPadding = context.FlowDirection == FlowDirection.RightToLeft
            ? new Thickness(0, 0, indent, 0)
            : new Thickness(indent, 0, 0, 0);
        GfmChildBuilder.PopulateChildren(body, details, context);
        if (body.Children.Count > 0)
        {
            stack.Add(body);
            context.AdoptConstructionBlock(body);
        }
        else
        {
            context.DiscardConstructionBlock(body);
        }

        return stack;
    }

    private static Thickness GetDisclosureMargin(MarkdownLayoutContext context)
    {
        Thickness bodyMargin = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body).Margin;
        return new Thickness(0, 0, 0, System.Math.Max(0, bodyMargin.Bottom * 2));
    }

    private static string ParseSummary(string markup, MarkdownLayoutContext context)
    {
        string fallback = context.ResolveString(
            MarkdownStringKeys.HtmlDetails,
            MarkdownLocalizedStrings.HtmlDetails);
        if (string.IsNullOrWhiteSpace(markup))
        {
            return fallback;
        }

        int paragraphBreak = markup.IndexOf("\n\n", System.StringComparison.Ordinal);
        string primaryMarkup = paragraphBreak >= 0 ? markup[..paragraphBreak] : markup;
        SafeHtmlDocument document = SafeHtmlParser.Parse(
            primaryMarkup,
            SafeHtmlParseLimits.Default,
            context.CancellationToken);
        string text = SafeHtmlParser.CollapseWhitespace(string.Concat(
            document.Root.Children.SelectMany(DescendantText))).Trim();
        return text.Length == 0 ? fallback : text;
    }

    private static void AppendSummarySupplementalContent(
        StackBox destination,
        GitHubDetailsBlock details,
        MarkdownLayoutContext context)
    {
        if (string.IsNullOrWhiteSpace(details.SummaryMarkup))
            return;

        MarkdownDocument fragment = Markdown.Parse(
            details.SummaryMarkup,
            context.Registry.BuildPipeline());
        int sourceOffset = details.SummarySourceStart >= 0
            ? details.SummarySourceStart
            : details.Span.Start;
        OffsetMarkdownSpans(fragment, sourceOffset);

        var rendered = new StackBox
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = context.FlowDirection,
        };
        context.RegisterConstructionBlock(rendered);
        GfmChildBuilder.PopulateChildren(rendered, fragment, context);

        if (rendered.Children.Count == 0)
        {
            context.DiscardConstructionBlock(rendered);
            return;
        }

        BlockBox[] children = rendered.DetachChildrenForTransfer();
        foreach (BlockBox child in children)
            context.RegisterConstructionBlock(child);

        bool first = true;
        foreach (BlockBox child in children)
        {
            if (first && child is InlineContainerBox leading)
            {
                first = false;
                InlineImageRun[] images = leading.DetachInlineImagesForTransfer();
                foreach (InlineImageRun image in images)
                    context.RegisterConstructionBlock(image.Image);

                if (images.Length > 0)
                {
                    var media = new InlineContainerBox(context, MarkdownElementKeys.Body)
                    {
                        BlockIndex = context.NextBlockIndex(),
                        TextAlignment = leading.TextAlignment,
                    };
                    context.RegisterConstructionBlock(media);
                    foreach (InlineImageRun image in images)
                    {
                        media.Add(image);
                        context.AdoptConstructionBlock(image.Image);
                    }
                    destination.Add(media);
                    context.AdoptConstructionBlock(media);
                }

                // The source container is only a staging owner. Transferred
                // images now belong to `media`; all other inline runs (notably
                // vector scenes) must be retired with the discarded container.
                context.DiscardConstructionBlock(leading);
                continue;
            }

            first = false;
            destination.Add(child);
            context.AdoptConstructionBlock(child);
        }

        // Children have been detached and reparented, leaving the staging
        // stack empty. Retire it rather than forgetting a temporary owner.
        context.DiscardConstructionBlock(rendered);
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
                    OffsetInlineSpans(leaf.Inline, offset);
            }
        }
    }

    private static void OffsetInlineSpans(ContainerInline container, int offset)
    {
        OffsetMarkdownSpan(container, offset);
        foreach (Inline inline in container)
        {
            if (inline is ContainerInline child)
                OffsetInlineSpans(child, offset);
            else
                OffsetMarkdownSpan(inline, offset);
        }
    }

    private static void OffsetMarkdownSpan(MarkdownObject node, int offset)
    {
        if (node.Span.Start < 0)
            return;

        node.Span = new Markdig.Syntax.SourceSpan(
            checked(node.Span.Start + offset),
            checked(node.Span.End + offset));
    }

    private static System.Collections.Generic.IEnumerable<string> DescendantText(SafeHtmlNode node)
    {
        if (node is SafeHtmlText text)
        {
            yield return text.DecodedText;
            yield break;
        }

        if (node is not SafeHtmlElement element || SafeHtmlParser.IsSuppressedElement(element.Name))
        {
            yield break;
        }

        foreach (SafeHtmlNode child in element.Children)
        {
            foreach (string value in DescendantText(child))
            {
                yield return value;
            }
        }
    }
}
