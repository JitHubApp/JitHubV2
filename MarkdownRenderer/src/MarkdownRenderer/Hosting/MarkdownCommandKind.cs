namespace MarkdownRenderer.Hosting;

/// <summary>Stable identifiers for target-aware markdown commands.</summary>
public enum MarkdownCommandKind
{
    /// <summary>Copy rendered plain text and rich HTML.</summary>
    CopyRendered,
    /// <summary>Copy exact markdown source.</summary>
    CopyMarkdown,
    /// <summary>Copy a link target.</summary>
    CopyLink,
    /// <summary>Copy an image.</summary>
    CopyImage,
    /// <summary>Copy code without its fence.</summary>
    CopyCode,
    /// <summary>Copy a table in a host-selected format.</summary>
    CopyTable,
    /// <summary>Activate a declarative action.</summary>
    Activate,
    /// <summary>Toggle editable task-list state.</summary>
    ToggleTask,
}
