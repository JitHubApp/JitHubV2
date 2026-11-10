using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// The published state one visible-realization pass started from. A pass
/// stops as soon as any of it changes, because factories, image loads and
/// native teardown can synchronously reenter the control.
/// </summary>
internal readonly struct VisibleRealizationPass
{
    internal VisibleRealizationPass(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long realizationPassEpoch,
        ViewportBandIndex? imageIndex,
        ViewportBandIndex? embedIndex,
        ViewportBandIndex? actionIndex)
    {
        Snapshot = snapshot;
        SnapshotGeneration = snapshotGeneration;
        RealizationPassEpoch = realizationPassEpoch;
        ImageIndex = imageIndex;
        EmbedIndex = embedIndex;
        ActionIndex = actionIndex;
    }

    internal LayoutSnapshot Snapshot { get; }

    internal long SnapshotGeneration { get; }

    internal long RealizationPassEpoch { get; }

    internal ViewportBandIndex? ImageIndex { get; }

    internal ViewportBandIndex? EmbedIndex { get; }

    internal ViewportBandIndex? ActionIndex { get; }
}
