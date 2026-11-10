using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Focus;

internal readonly struct PendingLazyFocus(
    LayoutSnapshot Snapshot,
    long SnapshotGeneration,
    FocusableItem Item,
    bool Reverse)
{
    public readonly LayoutSnapshot Snapshot = Snapshot;
    public readonly long SnapshotGeneration = SnapshotGeneration;
    public readonly FocusableItem Item = Item;
    public readonly bool Reverse = Reverse;
}
