namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Controls when native code blocks show a line-number gutter.
/// </summary>
public enum CodeBlockLineNumberMode
{
    /// <summary>Show line numbers for multiline code blocks.</summary>
    AutoMultiline,

    /// <summary>Always show line numbers unless a block explicitly disables them.</summary>
    Always,

    /// <summary>Never show line numbers unless a block explicitly enables them.</summary>
    Never,
}
