using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal.Sources;

[EventSource(Name = "MarkdownRenderer.Svg.Resvg.SourceSnapshots")]
internal sealed class SvgProviderSnapshotLedgerEvents : EventSource
{
    internal static readonly SvgProviderSnapshotLedgerEvents Log = new();

    // Scope ids correlate SnapshotChanged events from every ledger that
    // publishes through this provider, so the provider instance owns them.
    private long _nextScopeId;

    private SvgProviderSnapshotLedgerEvents()
    {
    }

    /// <summary>Allocates a positive scope id that no other ledger on this provider uses.</summary>
    [NonEvent]
    internal long AllocateScopeId() => Interlocked.Increment(ref _nextScopeId);

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
