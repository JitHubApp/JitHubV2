using MarkdownRenderer.Hosting;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The renderer services a <see cref="SelectionContextMenuPresenter"/> calls
/// while it builds the context menu and runs its commands.
/// </summary>
internal interface ISelectionContextMenuHost
{
    /// <summary>True when the reader may select text, which enables Select All.</summary>
    bool IsSelectionEnabled { get; }

    /// <summary>The host's command provider, which can override or disable menu commands.</summary>
    IMarkdownCommandProvider? CommandProvider { get; }

    /// <summary>Resolves a localized string, falling back to <paramref name="fallback"/>.</summary>
    string ResolveLocalizedString(string key, string fallback);

    /// <summary>Raises the renderer's CopyCompleted event.</summary>
    void RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded);

    /// <summary>
    /// The canvas rectangle of the keyboard-focused link or control, or null
    /// when nothing is focused or its geometry is unavailable.
    /// </summary>
    Rect? GetFocusedItemRect();
}
