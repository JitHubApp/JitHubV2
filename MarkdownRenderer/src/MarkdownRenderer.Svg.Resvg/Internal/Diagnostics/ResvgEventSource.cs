using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;

/// <summary>
/// Allocation-free-when-disabled ETW instrumentation for the resvg worker pool.
/// The provider is internal: release tooling consumes its versioned event
/// contract by provider and event name. Payloads are numeric only and never
/// carry source text, hashes, paths, URLs or authored metadata.
/// </summary>
[EventSource(Name = ProviderName)]
internal sealed class ResvgEventSource : EventSource
{
    internal const string ProviderName = "MarkdownRenderer-Svg-Resvg";

    internal static ResvgEventSource Log { get; } = new();

    // Work ids correlate stage events from every pool that reports through
    // this provider, so the provider instance owns the counter.
    private long _nextWorkId;

    // Worker instance ids name a worker in this provider's events and in its
    // pool, so they must stay distinct across every pool that reports here.
    private long _nextWorkerInstanceId;

    private ResvgEventSource()
    {
    }

    [NonEvent]
    internal bool IsWorkerStageMeasurementEnabled() =>
        IsEnabled(EventLevel.Informational, EventKeywords.None);

    [NonEvent]
    internal bool IsWorkerPolicyMeasurementEnabled() =>
        IsEnabled(EventLevel.Informational, EventKeywords.None);

    /// <summary>
    /// Starts a process-local work id for one worker-pool operation. Zero means
    /// no observer is active, so call sites skip their timers entirely.
    /// </summary>
    [NonEvent]
    internal long BeginWork() => IsWorkerStageMeasurementEnabled()
        ? Interlocked.Increment(ref _nextWorkId)
        : 0;

    /// <summary>
    /// Allocates the positive instance id of one adopted worker process. Ids
    /// are never reused while this provider lives.
    /// </summary>
    [NonEvent]
    internal long AllocateWorkerInstanceId() => Interlocked.Increment(ref _nextWorkerInstanceId);

    /// <summary>
    /// Correlated worker substage evidence. Payload fields are numeric only:
    /// work/stage/worker/request/document IDs, elapsed stopwatch ticks, worker
    /// CPU ticks (-1 when unavailable), source bytes, and output pixels.
    /// </summary>
    [Event(1, Level = EventLevel.Informational)]
    internal void WorkerStage(
        long workId,
        int stageId,
        long workerInstanceId,
        long requestId,
        long documentId,
        long elapsedStopwatchTicks,
        long workerCpuTicks,
        long sourceBytes,
        long targetPixels)
    {
        if (IsEnabled())
        {
            WriteEvent(1, [workId, stageId, workerInstanceId, requestId,
                documentId, elapsedStopwatchTicks, workerCpuTicks, sourceBytes,
                targetPixels]);
        }
    }

    /// <summary>
    /// Emits a bounded, numeric-only policy snapshot. Phase values follow
    /// <see cref="SvgWorkerPolicyPhase"/>. Architecture and worker-state fields
    /// are numeric enums. No content identity or user-authored data is
    /// accepted by this event.
    /// </summary>
    [NonEvent]
    internal void RecordWorkerPolicy(SvgWorkerPolicySnapshot snapshot)
    {
        WorkerPolicy(
            snapshot.EventSequence,
            snapshot.DecisionSequence,
            snapshot.Phase,
            snapshot.ProcessId,
            snapshot.StopwatchTicks,
            snapshot.StopwatchFrequency,
            snapshot.ProcessArchitecture,
            snapshot.LogicalProcessorCount,
            snapshot.EnergySaverOn,
            snapshot.EnergySaverQueryFailed,
            snapshot.CanUseSecondary,
            snapshot.PrimaryBusy,
            snapshot.PrimaryWorkerState,
            snapshot.PrimaryWorkerInstanceId,
            snapshot.PrimaryFontCatalogReady,
            snapshot.VisibleOpenCount,
            snapshot.VisibleRenderCount,
            snapshot.SecondaryReadinessState,
            snapshot.SecondaryBusy,
            snapshot.SecondaryWorkerState,
            snapshot.SecondaryWorkerInstanceId,
            snapshot.SecondaryFontCatalogReady,
            snapshot.ShouldStartSecondary);
    }

    [Event(2, Level = EventLevel.Informational)]
    internal void WorkerPolicy(
        long eventSequence,
        long decisionSequence,
        int phase,
        int processId,
        long stopwatchTicks,
        long stopwatchFrequency,
        int processArchitecture,
        int logicalProcessorCount,
        int energySaverOn,
        int energySaverQueryFailed,
        int canUseSecondary,
        int primaryBusy,
        int primaryWorkerState,
        long primaryWorkerInstanceId,
        int primaryFontCatalogReady,
        int visibleOpenCount,
        int visibleRenderCount,
        int secondaryReadinessState,
        int secondaryBusy,
        int secondaryWorkerState,
        long secondaryWorkerInstanceId,
        int secondaryFontCatalogReady,
        int shouldStartSecondary)
    {
        if (IsEnabled())
        {
            WriteEvent(2,
            [
                eventSequence,
                decisionSequence,
                phase,
                processId,
                stopwatchTicks,
                stopwatchFrequency,
                processArchitecture,
                logicalProcessorCount,
                energySaverOn,
                energySaverQueryFailed,
                canUseSecondary,
                primaryBusy,
                primaryWorkerState,
                primaryWorkerInstanceId,
                primaryFontCatalogReady,
                visibleOpenCount,
                visibleRenderCount,
                secondaryReadinessState,
                secondaryBusy,
                secondaryWorkerState,
                secondaryWorkerInstanceId,
                secondaryFontCatalogReady,
                shouldStartSecondary,
            ]);
        }
    }
}
