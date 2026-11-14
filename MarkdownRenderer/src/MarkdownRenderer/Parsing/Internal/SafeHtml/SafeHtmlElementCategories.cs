using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

/// <summary>
/// Element-name sets the safe HTML tag reader, tree builder and cross-block
/// scope tracker share: void elements never open a scope, raw-content elements
/// have their content skipped and suppressed, and paragraph-closing block
/// starts end an open paragraph.
/// </summary>
internal static class SafeHtmlElementCategories
{
    // These start tags close an open paragraph in HTML's "in body" insertion
    // mode. Keep this compatibility behavior deliberately bounded: this is not
    // a general-purpose HTML5 tree builder, and it does not synthesize nodes.
    private static readonly HashSet<string> ParagraphClosingBlockStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hgroup", "hr", "listing", "main", "menu", "nav", "ol", "p", "pre", "search",
        "section", "summary", "table", "ul",
    };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawContentElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "noscript", "template",
    };

    internal static bool IsVoidElement(string name) => VoidElements.Contains(name);

    internal static bool IsRawContentElement(string name) => RawContentElements.Contains(name);

    internal static bool ClosesOpenParagraph(string startTagName) => ParagraphClosingBlockStarts.Contains(startTagName);
}
