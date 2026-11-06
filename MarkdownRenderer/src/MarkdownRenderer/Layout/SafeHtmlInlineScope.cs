using System.Collections.Generic;

namespace MarkdownRenderer.Layout;

/// <summary>
/// One open inline safe-HTML element on the <see cref="SafeHtmlInlineState"/>
/// scope stack: its name, the style key and safe link it contributes to
/// descendant runs, whether it suppresses its content, its allowed class
/// aliases, and the script ancestry token it pushed, if any.
/// </summary>
internal readonly struct SafeHtmlInlineScope
{
    internal SafeHtmlInlineScope(
        string name,
        string? styleKey,
        string? linkUrl,
        string? linkTitle,
        bool suppressed,
        IReadOnlyList<string> styleAliases,
        SafeHtmlScriptStyleAncestryToken? scriptToken = null)
    {
        Name = name;
        StyleKey = styleKey;
        LinkUrl = linkUrl;
        LinkTitle = linkTitle;
        Suppressed = suppressed;
        StyleAliases = styleAliases;
        ScriptToken = scriptToken;
    }

    internal readonly string Name;
    internal readonly string? StyleKey;
    internal readonly string? LinkUrl;
    internal readonly string? LinkTitle;
    internal readonly bool Suppressed;
    internal readonly IReadOnlyList<string> StyleAliases;
    internal readonly SafeHtmlScriptStyleAncestryToken? ScriptToken;
}
