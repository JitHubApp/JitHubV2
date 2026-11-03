using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal;

[EventSource(Name = "MarkdownRenderer.Svg.Resvg.SourceSnapshots")]
internal sealed class SvgProviderSnapshotLedgerEvents : EventSource
{
    internal static readonly SvgProviderSnapshotLedgerEvents Log = new();

    private SvgProviderSnapshotLedgerEvents()
    {
    }

    [Event(1, Level = EventLevel.Verbose)]
    public void SnapshotChanged(
        long scopeId,
        long liveBytes,
        long peakBytes,
        long liveSnapshots,
        long peakSnapshots,
        long sequence)
    {
        if (IsEnabled())
        {
            WriteEvent(1, [scopeId, liveBytes, peakBytes, liveSnapshots, peakSnapshots, sequence]);
        }
    }

    [NonEvent]
    internal void TryPublish(long scopeId, SvgProviderSnapshotLedgerSnapshot snapshot)
    {
        try
        {
            SnapshotChanged(
                scopeId,
                snapshot.LiveBytes,
                snapshot.PeakBytes,
                snapshot.LiveSnapshots,
                snapshot.PeakSnapshots,
                snapshot.Sequence);
        }
        catch (Exception)
        {
            // Telemetry is best effort and must never fail an image operation.
        }
    }
}
