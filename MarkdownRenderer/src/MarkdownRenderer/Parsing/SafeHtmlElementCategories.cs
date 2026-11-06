using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Element-name sets the safe HTML tag reader and tree builder share: void
/// elements never open a scope, and raw-content elements have their content
/// skipped and suppressed.
/// </summary>
internal static class SafeHtmlElementCategories
{
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
}
