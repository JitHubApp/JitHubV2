using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Read-only searches over a parsed safe-HTML element tree.
/// </summary>
internal static class HtmlElementQueries
{
    internal static SafeHtmlElement? FindDescendant(SafeHtmlElement parent, string name)
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

    internal static IEnumerable<SafeHtmlText> DescendantText(SafeHtmlElement parent)
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

    internal static IEnumerable<SafeHtmlText> DescendantSummaryLabelText(SafeHtmlElement parent)
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

    internal static IEnumerable<SafeHtmlElement> DescendantPreformatted(SafeHtmlElement parent)
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

    internal static string? FindDescendantHeadingKey(SafeHtmlElement parent)
    {
        foreach (SafeHtmlElement child in parent.Children.OfType<SafeHtmlElement>())
        {
            string? key = HtmlElementNames.HeadingKey(child.Name);
            if (key is not null)
                return key;

            key = FindDescendantHeadingKey(child);
            if (key is not null)
                return key;
        }

        return null;
    }
}
