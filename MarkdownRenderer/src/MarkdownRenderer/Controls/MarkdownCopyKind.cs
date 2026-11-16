namespace MarkdownRenderer.Controls;

/// <summary>Identifies the Markdown content copied to the clipboard.</summary>
public enum MarkdownCopyKind
{
    /// <summary>The active text selection.</summary>
    Selection,

    /// <summary>An entire fenced or indented code block.</summary>
    CodeBlock,
}
