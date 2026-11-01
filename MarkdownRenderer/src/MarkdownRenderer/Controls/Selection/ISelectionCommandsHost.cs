using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The renderer services <see cref="SelectionCommands"/> calls.
/// </summary>
internal interface ISelectionCommandsHost
{
    /// <summary>The host's command provider, which may replace the built-in copy commands.</summary>
    IMarkdownCommandProvider? CommandProvider { get; }

    /// <summary>Raises the renderer's copy-completed event.</summary>
    void RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded);
}
