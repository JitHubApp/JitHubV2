namespace MarkdownRenderer.Controls;

/// <summary>Navigation disposition requested by a link gesture.</summary>
public enum MarkdownLinkDisposition
{
    /// <summary>Navigate in the host's current view.</summary>
    CurrentView,
    /// <summary>Open a new foreground view or window.</summary>
    NewView,
    /// <summary>Open a new background view when the host supports it.</summary>
    BackgroundView,
    /// <summary>Delegate navigation to the system or external application.</summary>
    External,
}
