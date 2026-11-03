using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal;

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

    private static long _nextWorkId;
    private static Action<long, int, long, long, long, long, long, long, long>? _workerStageRecorder;
    private static Action<SvgWorkerPolicySnapshot>? _workerPolicyRecorder;

    private ResvgEventSource()
    {
    }

    [NonEvent]
    internal static void SetWorkerStageRecorder(
        Action<long, int, long, long, long, long, long, long, long>? recorder) =>
        Volatile.Write(ref _workerStageRecorder, recorder);

    [NonEvent]
    internal bool IsWorkerStageMeasurementEnabled() =>
        Volatile.Read(ref _workerStageRecorder) is not null ||
        IsEnabled(EventLevel.Informational, EventKeywords.None);

    [NonEvent]
    internal static void SetWorkerPolicyRecorder(Action<SvgWorkerPolicySnapshot>? recorder) =>
        Volatile.Write(ref _workerPolicyRecorder, recorder);

    [NonEvent]
    internal bool IsWorkerPolicyMeasurementEnabled() =>
        Volatile.Read(ref _workerPolicyRecorder) is not null ||
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
        try
        {
            Volatile.Read(ref _workerStageRecorder)?.Invoke(
                workId,
                stageId,
                workerInstanceId,
                requestId,
                documentId,
                elapsedStopwatchTicks,
                workerCpuTicks,
                sourceBytes,
                targetPixels);
        }
        catch (Exception)
        {
            // Optional diagnostics must never interrupt SVG rendering.
        }

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
        try
        {
            Volatile.Read(ref _workerPolicyRecorder)?.Invoke(snapshot);
        }
        catch (Exception)
        {
            // Optional scheduling evidence must never affect SVG behavior.
        }

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
