namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Controls whether native code blocks use the renderer's enhanced header chrome
/// or plain Markdown presentation.
/// </summary>
public enum CodeBlockChromeMode
{
    /// <summary>Show the native language header and its optional copy action.</summary>
    Enhanced,

    /// <summary>Omit the native header and header action while keeping code styling and line-number policy.</summary>
    PlainMarkdown,
}
