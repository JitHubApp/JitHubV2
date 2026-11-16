using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Focus.Hosted;

internal readonly struct HostedFocusTuple(
    LayoutSnapshot Snapshot,
    long SnapshotGeneration,
    long LayoutRevision,
    long PlanEpoch,
    System.Collections.Generic.IReadOnlyList<FocusableItem> Items)
{
    public readonly LayoutSnapshot Snapshot = Snapshot;
    public readonly long SnapshotGeneration = SnapshotGeneration;
    public readonly long LayoutRevision = LayoutRevision;
    public readonly long PlanEpoch = PlanEpoch;
    public readonly System.Collections.Generic.IReadOnlyList<FocusableItem> Items = Items;
}
