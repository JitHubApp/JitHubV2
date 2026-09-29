namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Controls whether fenced and indented code blocks preserve long lines or
/// wrap them to the local block viewport.
/// </summary>
public enum CodeBlockWrappingMode
{
    /// <summary>
    /// Preserve every logical line and expose block-local horizontal
    /// scrolling when content is wider than the viewport. This is the default.
    /// </summary>
    NoWrap,

    /// <summary>Wrap long lines to the available code viewport width.</summary>
    Wrap,
}
