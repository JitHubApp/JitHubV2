using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Focus;

internal readonly struct PendingLazyFocus(
    LayoutSnapshot Snapshot,
    long SnapshotGeneration,
    Layout.FocusableItem Item,
    bool Reverse)
{
    public readonly LayoutSnapshot Snapshot = Snapshot;
    public readonly long SnapshotGeneration = SnapshotGeneration;
    public readonly Layout.FocusableItem Item = Item;
    public readonly bool Reverse = Reverse;
}
