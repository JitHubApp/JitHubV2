using System;

namespace MarkdownRenderer.Controls;

/// <summary>Event data for a Markdown clipboard copy attempt.</summary>
public sealed class MarkdownCopyCompletedEventArgs : EventArgs
{
    /// <summary>Initializes clipboard copy event data.</summary>
    public MarkdownCopyCompletedEventArgs(MarkdownCopyKind kind, bool succeeded)
    {
        Kind = kind;
        Succeeded = succeeded;
    }

    /// <summary>Gets the copied content kind.</summary>
    public MarkdownCopyKind Kind { get; }

    /// <summary>Gets whether the clipboard accepted the content.</summary>
    public bool Succeeded { get; }
}
