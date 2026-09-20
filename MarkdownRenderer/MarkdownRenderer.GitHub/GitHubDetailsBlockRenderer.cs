using System.Globalization;
using System.Linq;
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
        };
        var summary = new InlineContainerBox(context, MarkdownElementKeys.Strong)
        {
            BlockIndex = context.NextBlockIndex(),
        };
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
        }

        return stack;
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

        SafeHtmlDocument document = SafeHtmlParser.Parse(
            markup,
            SafeHtmlParseLimits.Default,
            context.CancellationToken);
        string text = SafeHtmlParser.CollapseWhitespace(string.Concat(
            document.Root.Children.SelectMany(DescendantText))).Trim();
        return text.Length == 0 ? fallback : text;
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
