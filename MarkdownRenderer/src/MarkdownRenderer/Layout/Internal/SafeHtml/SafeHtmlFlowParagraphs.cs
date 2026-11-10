using System.Collections.Generic;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout.Internal.SafeHtml;

/// <summary>
/// Classifies paragraphs that sit inside a safe-HTML inline flow, such as
/// badge-only rows and leading line breaks.
/// </summary>
internal static class SafeHtmlFlowParagraphs
{
    internal static bool IsBadgeOnlyHtmlFlowParagraph(ParagraphBlock paragraph)
    {
        bool containsImage = false;
        return paragraph.Inline is { } inline &&
            ContainsOnlyBadgeHtml(inline, ref containsImage) &&
            containsImage;
    }

    internal static bool ContainsOnlyBadgeHtml(ContainerInline inline, ref bool containsImage)
    {
        foreach (Inline child in inline)
        {
            if (child is HtmlInline html)
            {
                if (!SafeHtmlParser.TryParseTagSequence(
                        html.Tag,
                        SafeHtmlParseLimits.Default,
                        out IReadOnlyList<SafeHtmlTag> tags) ||
                    tags.Count == 0)
                {
                    return false;
                }

                foreach (SafeHtmlTag tag in tags)
                {
                    if (tag.Name is not ("a" or "br" or "img" or "picture" or "source" or "span"))
                        return false;
                    containsImage |= tag.Name == "img";
                }

                continue;
            }

            if (child is LiteralInline literal &&
                string.IsNullOrWhiteSpace(literal.Content.ToString()))
            {
                continue;
            }

            if (child is ContainerInline nested && ContainsOnlyBadgeHtml(nested, ref containsImage))
                continue;

            return false;
        }

        return true;
    }

    internal static Inline? FindLeadingHtmlBreak(ContainerInline? inline)
    {
        if (inline is null)
            return null;

        foreach (Inline child in inline)
        {
            if (child is LiteralInline literal &&
                string.IsNullOrWhiteSpace(literal.Content.ToString()))
            {
                continue;
            }

            if (child is HtmlInline html &&
                SafeHtmlParser.TryParseTagSequence(
                    html.Tag,
                    SafeHtmlParseLimits.Default,
                    out IReadOnlyList<SafeHtmlTag> tags) &&
                tags.Count == 1 &&
                tags[0].Name == "br")
            {
                return child;
            }

            return null;
        }

        return null;
    }
}
