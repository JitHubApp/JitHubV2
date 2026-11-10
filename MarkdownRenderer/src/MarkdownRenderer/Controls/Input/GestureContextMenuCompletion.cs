using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Opens the context menu a recognized gesture asked for, after the
/// snapshot lease has ended, only while the snapshot, generation and layout
/// revision the gesture hit-tested are still published.
/// </summary>
internal sealed class GestureContextMenuCompletion
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly IContextMenuOpener _contextMenu;

    internal GestureContextMenuCompletion(
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        IContextMenuOpener contextMenu)
    {
        _lifetime = lifetime;
        _published = published;
        _contextMenu = contextMenu;
    }

    /// <summary>
    /// Returns whether the gesture is handled: its own result when it asked
    /// for no menu, otherwise whether the menu opened.
    /// </summary>
    internal bool Complete(
        PointerInput input,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        if (!input.ShowContextMenuAfterInteraction)
            return input.Handled;

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !ReferenceEquals(_published.Snapshot, snapshot) ||
            snapshotGeneration != _published.Generation ||
            input.GestureLayoutRevision != layoutRevision ||
            snapshot.LayoutRevision != layoutRevision)
        {
            return false;
        }

        return _contextMenu.Show(
            input.GesturePosition,
            touchSelection: input.TouchSelectionContextMenu,
            requiredSnapshot: snapshot,
            requiredSnapshotGeneration: snapshotGeneration,
            requiredLayoutRevision: layoutRevision);
    }
}
