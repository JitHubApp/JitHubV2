using System;

namespace MarkdownRenderer.Controls;

/// <summary>Event data for safe HTML disclosure activation.</summary>
public sealed class MarkdownDisclosureToggledEventArgs : EventArgs
{
    /// <summary>Initializes disclosure event data.</summary>
    public MarkdownDisclosureToggledEventArgs(bool isExpanded) => IsExpanded = isExpanded;

    /// <summary>Gets whether the disclosure is expanded after activation.</summary>
    public bool IsExpanded { get; }
}
