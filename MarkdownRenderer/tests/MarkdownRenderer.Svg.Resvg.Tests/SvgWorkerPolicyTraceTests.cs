#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Threading;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgWorkerPolicyTraceTests
{
    // Keep test-generated identities outside the small positive range used by
    // live worker pools, while Interlocked guarantees uniqueness across tests.
    private static long _nextTestPrimaryWorkerInstanceId = 8_000_000_000_000_000L;

    [Fact]
    public async Task EmitsNumericEvaluationAndSecondaryLifecycleWithDecisionCorrelation()
    {
        long primaryWorkerInstanceId = NextTestPrimaryWorkerInstanceId();
        using var listener = new WorkerPolicyListener(primaryWorkerInstanceId);
        var trace = new SvgWorkerPolicyTrace();

        long openOnlyDecisionSequence = trace.Record(CreateSnapshot(
            phase: (int)SvgWorkerPolicyPhase.Evaluated,
            primaryWorkerInstanceId: primaryWorkerInstanceId,
            canUseSecondary: 1,
            primaryBusy: 1,
            visibleOpenCount: 2,
            visibleRenderCount: 0,
            shouldStartSecondary: 0));
        Assert.True(openOnlyDecisionSequence > 0);

        long decisionSequence = trace.Record(CreateSnapshot(
            phase: (int)SvgWorkerPolicyPhase.Evaluated,
            primaryWorkerInstanceId: primaryWorkerInstanceId,
            canUseSecondary: 1,
            primaryBusy: 1,
            visibleOpenCount: 1,
            visibleRenderCount: 2,
            shouldStartSecondary: 1));
        trace.Record(CreateSnapshot(
            phase: (int)SvgWorkerPolicyPhase.StartRequested,
            primaryWorkerInstanceId: primaryWorkerInstanceId,
            decisionSequence: decisionSequence,
            canUseSecondary: 1,
            primaryBusy: 1,
            visibleOpenCount: 1,
            visibleRenderCount: 2,
            secondaryReadinessState: 1,
            secondaryBusy: 1,
            shouldStartSecondary: 1));
        trace.Record(CreateSnapshot(
            phase: (int)SvgWorkerPolicyPhase.WorkerLaunchStarted,
            primaryWorkerInstanceId: primaryWorkerInstanceId,
            decisionSequence: decisionSequence,
            canUseSecondary: 1,
            primaryBusy: 1,
            visibleOpenCount: 1,
            visibleRenderCount: 2,
            secondaryReadinessState: 1,
            secondaryBusy: 1,
            shouldStartSecondary: 1));
        trace.Record(CreateSnapshot(
            phase: (int)SvgWorkerPolicyPhase.WorkerProcessStarted,
            primaryWorkerInstanceId: primaryWorkerInstanceId,
            decisionSequence: decisionSequence,
            canUseSecondary: 1,
            primaryBusy: 1,
            visibleOpenCount: 1,
            visibleRenderCount: 2,
            secondaryReadinessState: 1,
            secondaryBusy: 1,
            secondaryWorkerState: 1,
            secondaryWorkerInstanceId: 41,
            shouldStartSecondary: 1));

        WorkerPolicyEvent first = await listener.NextAsync();
        WorkerPolicyEvent second = await listener.NextAsync();
        WorkerPolicyEvent third = await listener.NextAsync();
        WorkerPolicyEvent fourth = await listener.NextAsync();
        WorkerPolicyEvent fifth = await listener.NextAsync();

        WorkerPolicyEvent[] events = [first, second, third, fourth, fifth];
        Assert.All(events, entry =>
        {
            Assert.Equal(14, entry.EventId);
            Assert.Equal(23, entry.Values.Length);
            Assert.Equal(primaryWorkerInstanceId, Assert.IsType<long>(entry.Values[13]));
            Assert.DoesNotContain(entry.Names, static name =>
                name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("source", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("content", StringComparison.OrdinalIgnoreCase));
        });

        Assert.Equal((int)SvgWorkerPolicyPhase.Evaluated, first.Values[2]);
        Assert.Equal(2, first.Values[15]);
        Assert.Equal(0, first.Values[16]);
        Assert.True((long)first.Values[4]! > 0);
        Assert.True((long)first.Values[5]! > 0);

        Assert.Equal((int)SvgWorkerPolicyPhase.Evaluated, second.Values[2]);
        Assert.Equal(decisionSequence, second.Values[1]);
        Assert.Equal(2, second.Values[16]);
        Assert.Equal((int)SvgWorkerPolicyPhase.StartRequested, third.Values[2]);
        Assert.Equal(decisionSequence, third.Values[1]);
        Assert.Equal((int)SvgWorkerPolicyPhase.WorkerLaunchStarted, fourth.Values[2]);
        Assert.Equal(decisionSequence, fourth.Values[1]);
        Assert.Equal((int)SvgWorkerPolicyPhase.WorkerProcessStarted, fifth.Values[2]);
        Assert.Equal(decisionSequence, fifth.Values[1]);
        Assert.Equal(41L, fifth.Values[20]);
        Assert.False(listener.TryRead(out _));
    }

    [Fact]
    public async Task EmitsOnlyOneTruncationMarkerAfterTheConfiguredEventBound()
    {
        long primaryWorkerInstanceId = NextTestPrimaryWorkerInstanceId();
        using var listener = new WorkerPolicyListener(primaryWorkerInstanceId);
        var trace = new SvgWorkerPolicyTrace(maximumEvents: 2);
        MarkdownSvgWorkerPolicySnapshot snapshot = CreateSnapshot(
            phase: (int)SvgWorkerPolicyPhase.Evaluated,
            primaryWorkerInstanceId: primaryWorkerInstanceId,
            canUseSecondary: 0,
            primaryBusy: 1,
            visibleOpenCount: 1,
            visibleRenderCount: 0,
            shouldStartSecondary: 0);

        trace.Record(snapshot);
        trace.Record(snapshot);
        trace.Record(snapshot);
        trace.Record(snapshot);

        WorkerPolicyEvent first = await listener.NextAsync();
        WorkerPolicyEvent second = await listener.NextAsync();
        WorkerPolicyEvent limit = await listener.NextAsync();
        WorkerPolicyEvent[] events = [first, second, limit];
        Assert.All(events, entry =>
        {
            Assert.Equal(14, entry.EventId);
            Assert.Equal(23, entry.Values.Length);
            Assert.Equal(primaryWorkerInstanceId, Assert.IsType<long>(entry.Values[13]));
            Assert.DoesNotContain(entry.Names, static name =>
                name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("source", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("content", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal((int)SvgWorkerPolicyPhase.Evaluated, first.Values[2]);
        Assert.Equal((int)SvgWorkerPolicyPhase.Evaluated, second.Values[2]);
        Assert.Equal((int)SvgWorkerPolicyPhase.TraceLimitReached, limit.Values[2]);
        Assert.False(listener.TryRead(out _));
    }

    private static MarkdownSvgWorkerPolicySnapshot CreateSnapshot(
        int phase,
        long primaryWorkerInstanceId,
        long decisionSequence = 0,
        int canUseSecondary = 0,
        int primaryBusy = 0,
        int visibleOpenCount = 0,
        int visibleRenderCount = 0,
        int secondaryReadinessState = 0,
        int secondaryBusy = 0,
        int secondaryWorkerState = 0,
        long secondaryWorkerInstanceId = 0,
        int shouldStartSecondary = 0) => new(
            Phase: phase,
            DecisionSequence: decisionSequence,
            ProcessArchitecture: 9,
            LogicalProcessorCount: 16,
            EnergySaverOn: 0,
            EnergySaverQueryFailed: 0,
            CanUseSecondary: canUseSecondary,
            PrimaryBusy: primaryBusy,
            PrimaryWorkerState: 1,
            PrimaryWorkerInstanceId: primaryWorkerInstanceId,
            PrimaryFontCatalogReady: 1,
            VisibleOpenCount: visibleOpenCount,
            VisibleRenderCount: visibleRenderCount,
            SecondaryReadinessState: secondaryReadinessState,
            SecondaryBusy: secondaryBusy,
            SecondaryWorkerState: secondaryWorkerState,
            SecondaryWorkerInstanceId: secondaryWorkerInstanceId,
            SecondaryFontCatalogReady: 0,
            ShouldStartSecondary: shouldStartSecondary);

    private static long NextTestPrimaryWorkerInstanceId() =>
        Interlocked.Increment(ref _nextTestPrimaryWorkerInstanceId);

    private sealed class WorkerPolicyListener : EventListener
    {
        private readonly ConcurrentQueue<WorkerPolicyEvent> _events = new();
        private readonly SemaphoreSlim _available = new(0);
        private long _expectedPrimaryWorkerInstanceId;

        internal WorkerPolicyListener(long expectedPrimaryWorkerInstanceId)
        {
            // EventListener's base constructor can invoke virtual callbacks
            // before this derived constructor assigns the filter. A zero
            // value therefore means "not armed" and is checked before any
            // callback touches the instance-initialized queue/semaphore.
            Volatile.Write(ref _expectedPrimaryWorkerInstanceId, expectedPrimaryWorkerInstanceId);
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == MarkdownPerformanceEventSource.ProviderName)
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            long expectedWorkerInstanceId = Volatile.Read(ref _expectedPrimaryWorkerInstanceId);
            if (expectedWorkerInstanceId == 0 ||
                eventData.EventId != 14 ||
                eventData.Payload is not { Count: > 13 } payload ||
                payload[13] is not long primaryWorkerInstanceId ||
                primaryWorkerInstanceId != expectedWorkerInstanceId)
            {
                return;
            }

            _events.Enqueue(new WorkerPolicyEvent(
                eventData.EventId,
                payload.ToArray(),
                eventData.PayloadNames?.ToArray() ?? []));
            _available.Release();
        }

        internal async Task<WorkerPolicyEvent> NextAsync()
        {
            await _available.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(_events.TryDequeue(out WorkerPolicyEvent? payload));
            return payload!;
        }

        internal bool TryRead(out WorkerPolicyEvent? payload) => _events.TryDequeue(out payload);
    }

    private sealed record WorkerPolicyEvent(int EventId, object?[] Values, string[] Names);
}
#endif
