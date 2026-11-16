using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;

internal readonly struct PendingHorizontalOverflowAutomationChange(
    LayoutSnapshot Snapshot,
    double OldPhysicalPercent,
    double NewPhysicalPercent)
{
    public readonly LayoutSnapshot Snapshot = Snapshot;
    public readonly double OldPhysicalPercent = OldPhysicalPercent;
    public readonly double NewPhysicalPercent = NewPhysicalPercent;
}
