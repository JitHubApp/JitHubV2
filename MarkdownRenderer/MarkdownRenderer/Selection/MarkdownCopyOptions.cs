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

/// <summary>
/// Options for copying a markdown selection to the clipboard.
/// </summary>
public sealed class MarkdownCopyOptions
{
    /// <summary>Gets the default copy options used by keyboard and context-menu copy.</summary>
    public static MarkdownCopyOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets the plain-text payload mode. Defaults to exact markdown source.
    /// </summary>
    public MarkdownPlainTextCopyMode PlainTextMode { get; init; } = MarkdownPlainTextCopyMode.SourceMarkdown;

    /// <summary>
    /// Gets or sets whether to also write a CF_HTML payload. Defaults to true.
    /// </summary>
    public bool IncludeHtml { get; init; } = true;
}
