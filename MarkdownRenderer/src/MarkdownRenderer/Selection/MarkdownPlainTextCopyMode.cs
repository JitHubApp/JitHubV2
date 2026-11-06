namespace MarkdownRenderer.Selection;

/// <summary>
/// Selects the plain-text payload written during copy operations.
/// </summary>
public enum MarkdownPlainTextCopyMode
{
    /// <summary>Write the exact markdown source slice as the plain-text clipboard payload.</summary>
    SourceMarkdown,

    /// <summary>Write rendered semantic text as the plain-text clipboard payload.</summary>
    RenderedText,
}
