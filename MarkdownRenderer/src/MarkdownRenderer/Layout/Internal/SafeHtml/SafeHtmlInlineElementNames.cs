namespace MarkdownRenderer.Layout.Internal.SafeHtml;

/// <summary>
/// Classifies safe-HTML element names for inline layout: which elements the
/// inline renderer supports, the Markdown style key an element contributes,
/// and which style keys are script (subscript/superscript) roles.
/// </summary>
internal static class SafeHtmlInlineElementNames
{
    internal static bool IsScriptStyleKey(string? styleKey)
        => styleKey is Theming.MarkdownElementKeys.Subscript or
            Theming.MarkdownElementKeys.Superscript;

    internal static bool IsSupportedElement(string name) => name is
        "a" or "address" or "article" or "aside" or "b" or "blockquote" or "br" or
        "caption" or "center" or "cite" or "code" or "col" or "colgroup" or "del" or
        "details" or "div" or "em" or "figcaption" or "figure" or "footer" or "h1" or
        "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "hr" or "i" or "img" or
        "ins" or "kbd" or "li" or "main" or "mark" or "nav" or "ol" or "p" or
        "audio" or "input" or "picture" or "pre" or "s" or "samp" or "section" or "small" or "source" or
        "span" or "strike" or "strong" or "sub" or "summary" or "sup" or "table" or
        "tbody" or "td" or "tfoot" or "th" or "thead" or "tr" or "u" or "ul" or "var" or "video";

    internal static string? GetStyleKey(string name) => name switch
    {
        "b" or "strong" => Theming.MarkdownElementKeys.Strong,
        "i" or "em" or "cite" or "var" => Theming.MarkdownElementKeys.Emphasis,
        "s" or "strike" or "del" => Theming.MarkdownElementKeys.Strikethrough,
        "sub" => Theming.MarkdownElementKeys.Subscript,
        "sup" => Theming.MarkdownElementKeys.Superscript,
        "ins" or "u" => Theming.MarkdownElementKeys.Inserted,
        "mark" => Theming.MarkdownElementKeys.Marked,
        "code" or "kbd" or "samp" => Theming.MarkdownElementKeys.CodeInline,
        _ => null,
    };
}
