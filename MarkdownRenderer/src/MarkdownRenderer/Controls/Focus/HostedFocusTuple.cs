using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Focus;

internal readonly struct HostedFocusTuple(
    LayoutSnapshot Snapshot,
    long SnapshotGeneration,
    long LayoutRevision,
    long PlanEpoch,
    System.Collections.Generic.IReadOnlyList<Layout.FocusableItem> Items)
{
    public readonly LayoutSnapshot Snapshot = Snapshot;
    public readonly long SnapshotGeneration = SnapshotGeneration;
    public readonly long LayoutRevision = LayoutRevision;
    public readonly long PlanEpoch = PlanEpoch;
    public readonly System.Collections.Generic.IReadOnlyList<Layout.FocusableItem> Items = Items;
}
