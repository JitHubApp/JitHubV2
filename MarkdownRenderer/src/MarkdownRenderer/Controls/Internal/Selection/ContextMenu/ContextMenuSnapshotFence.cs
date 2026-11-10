using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Selection.ContextMenu;

/// <summary>
/// The snapshot, generation and layout revision a context menu was asked
/// for. The menu opens, and keeps its target, only while that exact tuple is
/// still published and the view is loaded.
/// </summary>
internal readonly record struct ContextMenuSnapshotFence(
    LayoutSnapshot? Snapshot,
    long Generation,
    long LayoutRevision)
{
    /// <summary>
    /// Pointer gestures pass the exact geometry generation they used while
    /// interpreting the gesture. Keyboard invocation passes no snapshot and
    /// captures the published tuple at entry, so a later relayout cannot
    /// silently retarget the menu.
    /// </summary>
    internal static ContextMenuSnapshotFence For(
        PublishedLayout published,
        LayoutSnapshot? requiredSnapshot,
        long requiredSnapshotGeneration,
        long requiredLayoutRevision)
    {
        LayoutSnapshot? snapshot = requiredSnapshot ?? published.Snapshot;
        return new(
            snapshot,
            requiredSnapshot is null ? published.Generation : requiredSnapshotGeneration,
            requiredSnapshot is null ? snapshot?.LayoutRevision ?? 0 : requiredLayoutRevision);
    }

    /// <summary>True while the view is loaded and this tuple is still the published layout.</summary>
    internal bool IsCurrent(MarkdownViewLifetime lifetime, PublishedLayout published) =>
        !lifetime.IsDisposed && !lifetime.IsUnloaded &&
        ReferenceEquals(Snapshot, published.Snapshot) &&
        Generation == published.Generation &&
        (Snapshot is null || Snapshot.LayoutRevision == LayoutRevision);
}
