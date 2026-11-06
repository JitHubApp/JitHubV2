using System;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Owns one Markdown subscript or superscript entry pushed onto a
/// <see cref="SafeHtmlInlineState"/> script ancestry; disposing it removes that
/// entry. The default value owns nothing and disposes as a no-op.
/// </summary>
internal readonly struct SafeHtmlScriptStyleScope : IDisposable
{
    private readonly SafeHtmlInlineState? _owner;
    private readonly SafeHtmlScriptStyleAncestryToken? _token;

    internal SafeHtmlScriptStyleScope(SafeHtmlInlineState owner, SafeHtmlScriptStyleAncestryToken token)
    {
        _owner = owner;
        _token = token;
    }

    public void Dispose()
        => _owner?.RemoveScriptToken(_token);
}
