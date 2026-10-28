#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics.Tracing;
using System.Reflection;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgProviderSnapshotLedgerTests
{
    [Fact]
    public void ProcessAggregateUsesZeroAndRendererScopesAreNumeric()
    {
        Assert.Equal(0, SvgProviderSnapshotLedger.Process.ScopeId);

        var first = new SvgProviderSnapshotLedger();
        var second = new SvgProviderSnapshotLedger();

        Assert.True(first.ScopeId > 0);
        Assert.True(second.ScopeId > first.ScopeId);
    }

    [Fact]
    public void TrackResizeAndIdempotentDisposeMaintainExactSnapshots()
    {
        var aggregate = new SvgProviderSnapshotLedger();
        var renderer = new SvgProviderSnapshotLedger(aggregate);
        SvgProviderSnapshotLedger.Lease lease = renderer.Track(10);

        AssertSnapshot(renderer, liveBytes: 10, peakBytes: 10, liveSnapshots: 1, peakSnapshots: 1, sequence: 1);
        AssertSnapshot(aggregate, liveBytes: 10, peakBytes: 10, liveSnapshots: 1, peakSnapshots: 1, sequence: 1);

        lease.SetLength(15);
        AssertSnapshot(renderer, liveBytes: 15, peakBytes: 15, liveSnapshots: 1, peakSnapshots: 1, sequence: 2);
        AssertSnapshot(aggregate, liveBytes: 15, peakBytes: 15, liveSnapshots: 1, peakSnapshots: 1, sequence: 2);

        lease.SetLength(15);
        AssertSnapshot(renderer, liveBytes: 15, peakBytes: 15, liveSnapshots: 1, peakSnapshots: 1, sequence: 2);

        lease.SetLength(4);
        lease.Dispose();
        lease.Dispose();

        AssertSnapshot(renderer, liveBytes: 0, peakBytes: 15, liveSnapshots: 0, peakSnapshots: 1, sequence: 4);
        AssertSnapshot(aggregate, liveBytes: 0, peakBytes: 15, liveSnapshots: 0, peakSnapshots: 1, sequence: 4);
        Assert.Throws<ObjectDisposedException>(() => lease.SetLength(1));
    }

    [Fact]
    public void MultipleRendererLedgersContributeToOneIndependentAggregate()
    {
        var aggregate = new SvgProviderSnapshotLedger();
        var firstRenderer = new SvgProviderSnapshotLedger(aggregate);
        var secondRenderer = new SvgProviderSnapshotLedger(aggregate);

        using SvgProviderSnapshotLedger.Lease first = firstRenderer.Track(10);
        SvgProviderSnapshotLedger.Lease second = secondRenderer.Track(20);
        first.SetLength(15);

        AssertSnapshot(aggregate, liveBytes: 35, peakBytes: 35, liveSnapshots: 2, peakSnapshots: 2, sequence: 3);

        second.Dispose();
        AssertSnapshot(aggregate, liveBytes: 15, peakBytes: 35, liveSnapshots: 1, peakSnapshots: 2, sequence: 4);
        second.Dispose();

        AssertSnapshot(secondRenderer, liveBytes: 0, peakBytes: 20, liveSnapshots: 0, peakSnapshots: 1, sequence: 2);
        AssertSnapshot(aggregate, liveBytes: 15, peakBytes: 35, liveSnapshots: 1, peakSnapshots: 2, sequence: 4);
    }

    [Fact]
    public void ConcurrentTrackResizeAndReleaseKeepLocalAndAggregateCountsCoherent()
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 250;
        var aggregate = new SvgProviderSnapshotLedger();
        var renderer = new SvgProviderSnapshotLedger(aggregate);

        Parallel.For(0, workerCount, worker =>
        {
            int initialLength = 64 + worker;
            int grownLength = 128 + worker;
            for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
            {
                using SvgProviderSnapshotLedger.Lease lease = renderer.Track(initialLength);
                lease.SetLength(grownLength);
            }
        });

        long expectedSequence = workerCount * iterationsPerWorker * 3L;
        SvgProviderSnapshotLedgerSnapshot local = renderer.GetSnapshot();
        SvgProviderSnapshotLedgerSnapshot total = aggregate.GetSnapshot();

        Assert.Equal(0, local.LiveBytes);
        Assert.Equal(0, local.LiveSnapshots);
        Assert.Equal(expectedSequence, local.Sequence);
        Assert.Equal(0, total.LiveBytes);
        Assert.Equal(0, total.LiveSnapshots);
        Assert.Equal(expectedSequence, total.Sequence);
        Assert.InRange(local.PeakSnapshots, 1, workerCount);
        Assert.Equal(local.PeakSnapshots, total.PeakSnapshots);
        Assert.Equal(local.PeakBytes, total.PeakBytes);
    }

    [Fact]
    public async Task ConcurrentResizeAndDisposeOfOneLeaseNeverLeaksCounts()
    {
        const int iterations = 64;
        var ledger = new SvgProviderSnapshotLedger();

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            SvgProviderSnapshotLedger.Lease lease = ledger.Track(iteration + 1);
            using var start = new Barrier(participantCount: 2);
            Task resize = Task.Run(() =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(5));
                try
                {
                    lease.SetLength(iteration + 1024);
                }
                catch (ObjectDisposedException)
                {
                    // Dispose linearized first; a zero-owner lease cannot be resurrected.
                }
            });
            Task dispose = Task.Run(() =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(5));
                lease.Dispose();
            });

            await Task.WhenAll(resize, dispose).WaitAsync(TimeSpan.FromSeconds(5));
            lease.Dispose();
            Assert.Equal(0, ledger.GetSnapshot().LiveBytes);
            Assert.Equal(0, ledger.GetSnapshot().LiveSnapshots);
        }

        SvgProviderSnapshotLedgerSnapshot final = ledger.GetSnapshot();
        Assert.Equal(0, final.LiveBytes);
        Assert.Equal(0, final.LiveSnapshots);
        Assert.InRange(final.Sequence, iterations * 2L, iterations * 3L);
    }

    [Fact]
    public void LedgerAndLeaseStoreNoSourceBearingValues()
    {
        AssertNoSourceFieldTypes(typeof(SvgProviderSnapshotLedger));
        AssertNoSourceFieldTypes(typeof(SvgProviderSnapshotLedger.Lease));
        AssertNoSourceFieldTypes(typeof(SvgProviderSnapshotLedgerSnapshot));

        MethodInfo track = typeof(SvgProviderSnapshotLedger).GetMethod(
            "Track",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(int), Assert.Single(track.GetParameters()).ParameterType);
    }

    [Fact]
    public void EventPublishesNumericManifestAndRunsOutsideLedgerLocks()
    {
        var ledger = new SvgProviderSnapshotLedger();
        using SvgProviderSnapshotLedger.Lease lease = ledger.Track(37);
        using var listener = new LedgerEventListener(ledger, lease);
        listener.EnableEvents(SvgProviderSnapshotLedgerEvents.Log, EventLevel.Verbose);

        lease.SetLength(38);
        EventObservation observation = listener.Observation.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        Assert.Equal("MarkdownRenderer.Svg.Resvg.SourceSnapshots", observation.EventSourceName);
        Assert.Equal(1, observation.EventId);
        Assert.Equal("SnapshotChanged", observation.EventName);
        Assert.True(observation.CrossThreadReentryCompleted,
            "Event listeners must be invoked after ledger and lease state locks have been released.");
        Assert.Equal(6, observation.Payload.Length);
        Assert.Equal(ledger.ScopeId, Convert.ToInt64(observation.Payload[0]));
        Assert.Equal(38, Convert.ToInt64(observation.Payload[1]));
        Assert.Equal(38, Convert.ToInt64(observation.Payload[2]));
        Assert.Equal(1, Convert.ToInt64(observation.Payload[3]));
        Assert.Equal(1, Convert.ToInt64(observation.Payload[4]));
        Assert.Equal(2, Convert.ToInt64(observation.Payload[5]));
    }

    private static void AssertSnapshot(
        SvgProviderSnapshotLedger ledger,
        long liveBytes,
        long peakBytes,
        long liveSnapshots,
        long peakSnapshots,
        long sequence)
    {
        SvgProviderSnapshotLedgerSnapshot snapshot = ledger.GetSnapshot();
        Assert.Equal(liveBytes, snapshot.LiveBytes);
        Assert.Equal(peakBytes, snapshot.PeakBytes);
        Assert.Equal(liveSnapshots, snapshot.LiveSnapshots);
        Assert.Equal(peakSnapshots, snapshot.PeakSnapshots);
        Assert.Equal(sequence, snapshot.Sequence);
    }

    private static void AssertNoSourceFieldTypes(Type type)
    {
        Type[] forbiddenTypes =
        [
            typeof(string),
            typeof(byte[]),
            typeof(ReadOnlyMemory<byte>),
            typeof(Memory<byte>),
            typeof(Uri),
        ];

        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, field => forbiddenTypes.Contains(field.FieldType));
    }

    private sealed class LedgerEventListener(
        SvgProviderSnapshotLedger ledger,
        SvgProviderSnapshotLedger.Lease lease) : EventListener
    {
        public TaskCompletionSource<EventObservation> Observation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _started;

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != "MarkdownRenderer.Svg.Resvg.SourceSnapshots" ||
                eventData.EventName != "SnapshotChanged" ||
                eventData.Payload is not { Count: 6 } payload ||
                Convert.ToInt64(payload[0]) != ledger.ScopeId ||
                Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            // Dispose from another thread while the EventSource callback is
            // active. If SetLength still held either lock when publishing, the
            // disposal would block and this bounded join would fail.
            Task disposeOnOtherThread = Task.Run(lease.Dispose);
            bool callbackCanReenter = disposeOnOtherThread.Wait(TimeSpan.FromSeconds(3));
            if (callbackCanReenter)
                disposeOnOtherThread.GetAwaiter().GetResult();

            Observation.TrySetResult(new EventObservation(
                eventData.EventSource.Name,
                eventData.EventId,
                eventData.EventName,
                payload.ToArray(),
                callbackCanReenter));
        }
    }

    private sealed record EventObservation(
        string EventSourceName,
        int EventId,
        string? EventName,
        object?[] Payload,
        bool CrossThreadReentryCompleted);
}
#endif
