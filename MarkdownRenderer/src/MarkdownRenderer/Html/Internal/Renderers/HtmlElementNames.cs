using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Classifies safe-HTML element names for the block renderer.
/// </summary>
internal static class HtmlElementNames
{
    internal static string? HeadingKey(string name) => name switch
    {
        "h1" => MarkdownElementKeys.Heading1,
        "h2" => MarkdownElementKeys.Heading2,
        "h3" => MarkdownElementKeys.Heading3,
        "h4" => MarkdownElementKeys.Heading4,
        "h5" => MarkdownElementKeys.Heading5,
        "h6" => MarkdownElementKeys.Heading6,
        _ => null,
    };

    internal static bool IsBlockElement(string name) => name is
        "address" or "article" or "aside" or "blockquote" or "center" or "details" or "div" or
        "figcaption" or "figure" or "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or
        "h6" or "header" or "hr" or "main" or "nav" or "ol" or "p" or "pre" or "section" or
        "summary" or "table" or "ul" or "video" or "audio";

    internal static bool IsSupportedElement(string name) => name is
        "a" or "address" or "article" or "aside" or "b" or "blockquote" or "br" or
        "caption" or "center" or "cite" or "code" or "col" or "colgroup" or "del" or
        "details" or "div" or "em" or "figcaption" or "figure" or "footer" or "h1" or
        "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "hr" or "i" or "img" or
        "ins" or "kbd" or "li" or "main" or "mark" or "nav" or "ol" or "p" or
        "picture" or "pre" or "s" or "samp" or "section" or "small" or "source" or
        "span" or "strike" or "strong" or "sub" or "summary" or "sup" or "svg" or "table" or
        "tbody" or "td" or "tfoot" or "th" or "thead" or "tr" or "u" or "ul" or "var" or
        "video" or "audio" or "input";
}
