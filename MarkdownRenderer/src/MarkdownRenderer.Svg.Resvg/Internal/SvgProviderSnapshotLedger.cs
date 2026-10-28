#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Diagnostic-only accounting for the actual source-byte backing capacity of
/// provider-owned SVG snapshots whose ownership tickets are still live. This is telemetry, not an
/// admission limit, memory measurement, or guarantee that arbitrary copies made
/// elsewhere are accounted for. It does not include image/HTTP caches, worker
/// parsed-resource caches, or other allocations outside these snapshot tickets.
/// </summary>
internal sealed class SvgProviderSnapshotLedger
{
    private static long s_nextScopeId;

    private readonly object _gate = new();
    private readonly SvgProviderSnapshotLedger? _aggregate;
    private Counters _counters;

    static SvgProviderSnapshotLedger()
    {
        Process = new SvgProviderSnapshotLedger(scopeId: 0, aggregate: null);
    }

    /// <summary>The process-wide aggregate used by renderer ledgers that opt in.</summary>
    internal static SvgProviderSnapshotLedger Process { get; }

    /// <summary>Creates an independent numeric scope, optionally contributing to an aggregate.</summary>
    internal SvgProviderSnapshotLedger(SvgProviderSnapshotLedger? aggregateLedger = null)
        : this(Interlocked.Increment(ref s_nextScopeId), aggregateLedger)
    {
    }

    private SvgProviderSnapshotLedger(long scopeId, SvgProviderSnapshotLedger? aggregate)
    {
        if (aggregate?._aggregate is not null)
        {
            throw new ArgumentException(
                "An aggregate ledger cannot itself contribute to another aggregate.",
                nameof(aggregate));
        }

        ScopeId = scopeId;
        _aggregate = aggregate;
    }

    /// <summary>A content-free numeric identity for diagnostic correlation.</summary>
    internal long ScopeId { get; }

    /// <summary>
    /// Adds one owned source snapshot to this ledger. The caller remains
    /// responsible for admitting memory before allocating or copying the source.
    /// </summary>
    internal Lease Track(int byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);

        var lease = new Lease(this, byteLength);
        Change change;
        if (_aggregate is null)
        {
            lock (_gate)
            {
                Counters next = _counters.AfterTrack(byteLength);
                _counters = next;
                change = new Change(next.ToSnapshot(), default, HasAggregate: false);
            }
        }
        else
        {
            lock (_aggregate._gate)
            {
                lock (_gate)
                {
                    Counters localNext = _counters.AfterTrack(byteLength);
                    Counters aggregateNext = _aggregate._counters.AfterTrack(byteLength);
                    _counters = localNext;
                    _aggregate._counters = aggregateNext;
                    change = new Change(localNext.ToSnapshot(), aggregateNext.ToSnapshot(), HasAggregate: true);
                }
            }
        }

        Publish(change);
        return lease;
    }

    /// <summary>Returns one coherent numeric snapshot under this ledger's state lock.</summary>
    internal SvgProviderSnapshotLedgerSnapshot GetSnapshot()
    {
        lock (_gate)
            return _counters.ToSnapshot();
    }

    private Change Resize(int oldLength, int newLength)
    {
        if (_aggregate is null)
        {
            lock (_gate)
            {
                Counters next = _counters.AfterResize(oldLength, newLength);
                _counters = next;
                return new Change(next.ToSnapshot(), default, HasAggregate: false);
            }
        }

        lock (_aggregate._gate)
        {
            lock (_gate)
            {
                Counters localNext = _counters.AfterResize(oldLength, newLength);
                Counters aggregateNext = _aggregate._counters.AfterResize(oldLength, newLength);
                _counters = localNext;
                _aggregate._counters = aggregateNext;
                return new Change(localNext.ToSnapshot(), aggregateNext.ToSnapshot(), HasAggregate: true);
            }
        }
    }

    private Change Release(int byteLength)
    {
        if (_aggregate is null)
        {
            lock (_gate)
            {
                Counters next = _counters.AfterRelease(byteLength);
                _counters = next;
                return new Change(next.ToSnapshot(), default, HasAggregate: false);
            }
        }

        lock (_aggregate._gate)
        {
            lock (_gate)
            {
                Counters localNext = _counters.AfterRelease(byteLength);
                Counters aggregateNext = _aggregate._counters.AfterRelease(byteLength);
                _counters = localNext;
                _aggregate._counters = aggregateNext;
                return new Change(localNext.ToSnapshot(), aggregateNext.ToSnapshot(), HasAggregate: true);
            }
        }
    }

    private void Publish(Change change)
    {
        try
        {
            SvgProviderSnapshotLedgerEvents events = SvgProviderSnapshotLedgerEvents.Log;
            events.TryPublish(ScopeId, change.Local);
            if (change.HasAggregate)
                events.TryPublish(_aggregate!.ScopeId, change.Aggregate);
        }
        catch (Exception)
        {
            // EventSource initialization/publication is best-effort too: a
            // diagnostic failure cannot strand a counted ownership ticket.
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly object _gate = new();
        private SvgProviderSnapshotLedger? _owner;
        private int _length;

        internal Lease(SvgProviderSnapshotLedger owner, int length)
        {
            _owner = owner;
            _length = length;
        }

        /// <summary>Updates the accounted backing capacity; this does not resize or own a source buffer.</summary>
        internal void SetLength(int byteLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(byteLength);

            SvgProviderSnapshotLedger owner;
            Change change;
            lock (_gate)
            {
                owner = _owner ?? throw new ObjectDisposedException(nameof(Lease));
                if (_length == byteLength)
                    return;

                change = owner.Resize(_length, byteLength);
                _length = byteLength;
            }

            // EventSource callbacks are intentionally outside both ledger and
            // lease state locks; diagnostics cannot participate in ownership.
            owner.Publish(change);
        }

        /// <summary>Releases this accounting ticket once; repeated calls are harmless.</summary>
        public void Dispose()
        {
            SvgProviderSnapshotLedger? owner;
            Change change;
            lock (_gate)
            {
                owner = _owner;
                if (owner is null)
                    return;

                change = owner.Release(_length);
                _owner = null;
                _length = 0;
            }

            owner.Publish(change);
        }
    }

    private readonly record struct Change(
        SvgProviderSnapshotLedgerSnapshot Local,
        SvgProviderSnapshotLedgerSnapshot Aggregate,
        bool HasAggregate);

    private readonly record struct Counters(
        long LiveBytes,
        long PeakBytes,
        long LiveSnapshots,
        long PeakSnapshots,
        long Sequence)
    {
        public Counters AfterTrack(int byteLength)
        {
            long liveBytes = checked(LiveBytes + byteLength);
            long liveSnapshots = checked(LiveSnapshots + 1);
            return new Counters(
                liveBytes,
                Math.Max(PeakBytes, liveBytes),
                liveSnapshots,
                Math.Max(PeakSnapshots, liveSnapshots),
                NextSequence(Sequence));
        }

        public Counters AfterResize(int oldLength, int newLength)
        {
            long liveBytes = newLength >= oldLength
                ? checked(LiveBytes + ((long)newLength - oldLength))
                : checked(LiveBytes - ((long)oldLength - newLength));
            if (liveBytes < 0)
                throw new InvalidOperationException("The SVG source snapshot ledger underflowed.");

            return new Counters(
                liveBytes,
                Math.Max(PeakBytes, liveBytes),
                LiveSnapshots,
                PeakSnapshots,
                NextSequence(Sequence));
        }

        public Counters AfterRelease(int byteLength)
        {
            if (LiveSnapshots <= 0 || LiveBytes < byteLength)
                throw new InvalidOperationException("The SVG source snapshot ledger underflowed.");

            return new Counters(
                LiveBytes - byteLength,
                PeakBytes,
                LiveSnapshots - 1,
                PeakSnapshots,
                NextSequence(Sequence));
        }

        public SvgProviderSnapshotLedgerSnapshot ToSnapshot() => new(
            LiveBytes,
            PeakBytes,
            LiveSnapshots,
            PeakSnapshots,
            Sequence);

        private static long NextSequence(long sequence) =>
            sequence == long.MaxValue ? sequence : sequence + 1;
    }
}

/// <summary>A coherent, content-free view of one provider snapshot ledger.</summary>
internal readonly record struct SvgProviderSnapshotLedgerSnapshot(
    long LiveBytes,
    long PeakBytes,
    long LiveSnapshots,
    long PeakSnapshots,
    long Sequence);

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
#endif
