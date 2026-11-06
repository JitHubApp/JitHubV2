namespace MarkdownRenderer.Layout;

/// <summary>
/// Identifies one open subscript or superscript scope in a
/// <see cref="SafeHtmlInlineState"/> script ancestry. Reference identity lets
/// the owner remove exactly the scope that closes, even when sibling scopes
/// share the same style key.
/// </summary>
internal sealed class SafeHtmlScriptStyleAncestryToken(string styleKey)
{
    internal string StyleKey { get; } = styleKey;
}
