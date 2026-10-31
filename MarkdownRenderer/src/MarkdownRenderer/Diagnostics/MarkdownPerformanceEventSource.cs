using System;
using System.Diagnostics.Tracing;
using System.Threading;

namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Allocation-free-when-disabled ETW instrumentation for the pinned-machine
/// performance harness. This provider is intentionally internal: applications
/// consume stable renderer behavior, while release tooling consumes its
/// versioned event contract by provider and event name.
/// </summary>
[EventSource(Name = ProviderName)]
internal sealed class MarkdownPerformanceEventSource : EventSource
{
    internal const string ProviderName = "MarkdownRenderer-Performance";

    internal static MarkdownPerformanceEventSource Log { get; } = new();

    [ThreadStatic]
    private static long _logicalFrameId;
    private static IMarkdownPerformanceSink? _sink;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private static long _nextSvgWorkId;
    private static Action<long, long, long>? _svgStageDiagnosticRecorder;
    private static Action<long, int, long, long, long, long, long, long, long>? _svgWorkerStageDiagnosticRecorder;
    private static Action<MarkdownSvgWorkerPolicySnapshot>? _svgWorkerPolicyDiagnosticRecorder;
#endif

    private MarkdownPerformanceEventSource()
    {
    }

    /// <summary>
    /// Correlates scroll and paint work with a synthetic or composition frame
    /// owned by the performance harness. Production callers leave this at zero.
    /// </summary>
    [NonEvent]
    internal static long GetLogicalFrameId() => _logicalFrameId;

    [NonEvent]
    internal static void SetLogicalFrameId(long value) => _logicalFrameId = value;

    [NonEvent]
    internal static void SetInProcessSink(IMarkdownPerformanceSink? sink) =>
        Volatile.Write(ref _sink, sink);

    [NonEvent]
    internal bool IsMeasurementEnabled() =>
        Volatile.Read(ref _sink) is not null ||
        IsEnabled(EventLevel.Informational, EventKeywords.None);

    [Event(1, Level = EventLevel.Informational)]
    internal void PipelineStarted(long generation, long sourceUtf16Bytes)
    {
        Volatile.Read(ref _sink)?.PipelineStarted(generation, sourceUtf16Bytes);
        if (IsEnabled())
            WriteEvent(1, generation, sourceUtf16Bytes);
    }

    [Event(2, Level = EventLevel.Informational)]
    internal void FirstViewportPresented(long generation, long sourceUtf16Bytes, long elapsedStopwatchTicks)
    {
        Volatile.Read(ref _sink)?.FirstViewportPresented(
            generation,
            sourceUtf16Bytes,
            elapsedStopwatchTicks);
        if (IsEnabled())
            WriteEvent(2, generation, sourceUtf16Bytes, elapsedStopwatchTicks);
    }

    [Event(3, Level = EventLevel.Informational)]
    internal void ScrollWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes)
    {
        Volatile.Read(ref _sink)?.ScrollWork(logicalFrameId, elapsedStopwatchTicks, allocatedBytes);
        if (IsEnabled())
            WriteEvent(3, logicalFrameId, elapsedStopwatchTicks, allocatedBytes);
    }

    [Event(4, Level = EventLevel.Informational)]
    internal void PaintWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes)
    {
        Volatile.Read(ref _sink)?.PaintWork(logicalFrameId, elapsedStopwatchTicks, allocatedBytes);
        if (IsEnabled())
            WriteEvent(4, logicalFrameId, elapsedStopwatchTicks, allocatedBytes);
    }

    /// <summary>
    /// Records request-to-exit latency for the superseded consumer/presentation
    /// pipeline. Parser-worker quiescence is intentionally measured separately.
    /// </summary>
    [Event(5, Level = EventLevel.Informational)]
    internal void SupersededPipelineStopped(long generation, long elapsedStopwatchTicks)
    {
        Volatile.Read(ref _sink)?.SupersededPipelineStopped(generation, elapsedStopwatchTicks);
        if (IsEnabled())
            WriteEvent(5, generation, elapsedStopwatchTicks);
    }

    [Event(6, Level = EventLevel.Informational)]
    internal void ScrollAllocationBreakdown(
        long logicalFrameId,
        long lazyLayoutBytes,
        long adornerBytes,
        long realizationBytes,
        long highlightingBytes)
    {
        Volatile.Read(ref _sink)?.ScrollAllocationBreakdown(
            logicalFrameId,
            lazyLayoutBytes,
            adornerBytes,
            realizationBytes,
            highlightingBytes);
        if (IsEnabled())
            WriteEvent(6, logicalFrameId, lazyLayoutBytes, adornerBytes, realizationBytes, highlightingBytes);
    }

    [Event(7, Level = EventLevel.Informational)]
    internal void PaintAllocationBreakdown(
        long logicalFrameId,
        long schedulingBytes,
        long platformSessionBytes,
        long snapshotPaintBytes,
        long interactivePaintBytes)
    {
        Volatile.Read(ref _sink)?.PaintAllocationBreakdown(
            logicalFrameId,
            schedulingBytes,
            platformSessionBytes,
            snapshotPaintBytes,
            interactivePaintBytes);
        if (IsEnabled())
            WriteEvent(7, logicalFrameId, schedulingBytes, platformSessionBytes, snapshotPaintBytes, interactivePaintBytes);
    }

    [Event(8, Level = EventLevel.Informational)]
    internal void SnapshotPaintAllocationBreakdown(
        long logicalFrameId,
        long inlineBytes,
        long tableBytes,
        long codeBytes,
        long otherBytes)
    {
        Volatile.Read(ref _sink)?.SnapshotPaintAllocationBreakdown(
            logicalFrameId,
            inlineBytes,
            tableBytes,
            codeBytes,
            otherBytes);
        if (IsEnabled())
            WriteEvent(8, logicalFrameId, inlineBytes, tableBytes, codeBytes, otherBytes);
    }

    [Event(9, Level = EventLevel.Informational)]
    internal void CodeActionRealization(long logicalFrameId, long realizedCount, long offscreenCount)
    {
        Volatile.Read(ref _sink)?.CodeActionRealization(logicalFrameId, realizedCount, offscreenCount);
        if (IsEnabled())
            WriteEvent(9, logicalFrameId, realizedCount, offscreenCount);
    }

    /// <summary>
    /// Marks the request-time generation fence and cancellation signal for a
    /// still-running presentation pipeline.
    /// </summary>
    [Event(10, Level = EventLevel.Informational)]
    internal void PipelineSuperseded(long generation)
    {
        Volatile.Read(ref _sink)?.PipelineSuperseded(generation);
        if (IsEnabled())
            WriteEvent(10, generation);
    }

    /// <summary>
    /// Stage identifiers: 1 source fetch, 2 raster decode/upload,
    /// 3 background image reflow, 4 UI image-reflow publication.
    /// No source URLs or content are included in this event.
    /// </summary>
    [Event(11, Level = EventLevel.Informational)]
    internal void ResourceWork(int stage, long elapsedStopwatchTicks, long workUnits)
    {
        if (IsEnabled())
            WriteEvent(11, stage, elapsedStopwatchTicks, workUnits);
    }

    /// <summary>Best-effort cache cleanup failed; payload contains no source data.</summary>
    [Event(16, Level = EventLevel.Warning)]
    internal void BitmapCacheCleanupFailed(int hresult)
    {
        Volatile.Read(ref _sink)?.BitmapCacheCleanupFailed(hresult);
        if (IsEnabled())
            WriteEvent(16, hresult);
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    [NonEvent]
    internal static void SetSvgStageDiagnosticRecorder(Action<long, long, long>? recorder) =>
        Volatile.Write(ref _svgStageDiagnosticRecorder, recorder);

    [NonEvent]
    internal bool IsSvgWorkMeasurementEnabled() =>
        Volatile.Read(ref _svgStageDiagnosticRecorder) is not null ||
        IsEnabled(EventLevel.Informational, EventKeywords.None);

    /// <summary>
    /// Starts a process-local SVG correlation id. A zero id means that no
    /// diagnostic observer is active, so call sites can skip timers entirely.
    /// </summary>
    [NonEvent]
    internal long BeginSvgWork() => IsSvgWorkMeasurementEnabled()
        ? Interlocked.Increment(ref _nextSvgWorkId)
        : 0;

    /// <summary>
    /// Numeric-only SVG stage timing. Never pass source text, URLs, titles,
    /// hashes, or security partition identifiers to this event. The packed
    /// metrics use bits 49-53 for the 5-bit stage id, bits 25-48 for source
    /// bytes, and bits 0-24 for target pixels. Current SVG budgets fit these
    /// widths (8 MiB input and 64 MiB RGBA output).
    /// </summary>
    [Event(12, Level = EventLevel.Informational)]
    internal void SvgWorkStage(long workId, long elapsedStopwatchTicks, long packedMetrics)
    {
        try
        {
            Volatile.Read(ref _svgStageDiagnosticRecorder)?.Invoke(
                workId, elapsedStopwatchTicks, packedMetrics);
        }
        catch (Exception)
        {
            // Diagnostic recording must never interrupt image rendering.
        }
        if (IsEnabled())
            WriteEvent(12, workId, elapsedStopwatchTicks, packedMetrics);
    }

    [NonEvent]
    internal static void SetSvgWorkerStageDiagnosticRecorder(
        Action<long, int, long, long, long, long, long, long, long>? recorder) =>
        Volatile.Write(ref _svgWorkerStageDiagnosticRecorder, recorder);

    [NonEvent]
    internal bool IsSvgWorkerStageMeasurementEnabled() =>
        Volatile.Read(ref _svgWorkerStageDiagnosticRecorder) is not null ||
        IsEnabled(EventLevel.Informational, EventKeywords.None);

    [NonEvent]
    internal static void SetSvgWorkerPolicyDiagnosticRecorder(
        Action<MarkdownSvgWorkerPolicySnapshot>? recorder) =>
        Volatile.Write(ref _svgWorkerPolicyDiagnosticRecorder, recorder);

    [NonEvent]
    internal bool IsSvgWorkerPolicyMeasurementEnabled() =>
        Volatile.Read(ref _svgWorkerPolicyDiagnosticRecorder) is not null ||
        IsEnabled(EventLevel.Informational, EventKeywords.None);

    /// <summary>
    /// Correlated worker substage evidence. Payload fields are numeric only:
    /// work/stage/worker/request/document IDs, elapsed stopwatch ticks, worker
    /// CPU ticks (-1 when unavailable), source bytes, and output pixels. It
    /// deliberately excludes source hashes, paths, URLs, and authored text.
    /// </summary>
    [Event(13, Level = EventLevel.Informational)]
    internal void SvgWorkerStage(
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
            Volatile.Read(ref _svgWorkerStageDiagnosticRecorder)?.Invoke(
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
            WriteEvent(13, [workId, stageId, workerInstanceId, requestId,
                documentId, elapsedStopwatchTicks, workerCpuTicks, sourceBytes,
                targetPixels]);
        }
    }

    /// <summary>
    /// Emits a bounded, numeric-only policy snapshot. Phase values are
    /// 1=evaluated, 2=start requested, 3=worker launch started,
    /// 4=worker process started, 5=ready, 6=failed, and 7=trace limit reached.
    /// Architecture and worker-state
    /// fields are numeric enums. No content identity or user-authored data is
    /// accepted by this event.
    /// </summary>
    [NonEvent]
    internal void RecordSvgWorkerPolicy(MarkdownSvgWorkerPolicySnapshot snapshot)
    {
        try
        {
            Volatile.Read(ref _svgWorkerPolicyDiagnosticRecorder)?.Invoke(snapshot);
        }
        catch (Exception)
        {
            // Optional scheduling evidence must never affect SVG behavior.
        }

        SvgWorkerPolicy(
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

    [Event(14, Level = EventLevel.Informational)]
    internal void SvgWorkerPolicy(
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
            WriteEvent(14,
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
#endif
}

/// <summary>
/// Typed, allocation-free observer used by the in-process pinned-machine
/// harness. External profilers continue to consume the EventSource contract;
/// this sink avoids letting EventListener payload allocation perturb a 4 KiB
/// per-frame gate.
/// </summary>
internal interface IMarkdownPerformanceSink
{
    void BitmapCacheCleanupFailed(int hresult);
    void PipelineStarted(long generation, long sourceUtf16Bytes);
    void FirstViewportPresented(long generation, long sourceUtf16Bytes, long elapsedStopwatchTicks);
    void ScrollWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes);
    void PaintWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes);
    void PipelineSuperseded(long generation);
    void SupersededPipelineStopped(long generation, long elapsedStopwatchTicks);
    void ScrollAllocationBreakdown(
        long logicalFrameId,
        long lazyLayoutBytes,
        long adornerBytes,
        long realizationBytes,
        long highlightingBytes);
    void PaintAllocationBreakdown(
        long logicalFrameId,
        long schedulingBytes,
        long platformSessionBytes,
        long snapshotPaintBytes,
        long interactivePaintBytes);
    void SnapshotPaintAllocationBreakdown(
        long logicalFrameId,
        long inlineBytes,
        long tableBytes,
        long codeBytes,
        long otherBytes);
    void CodeActionRealization(long logicalFrameId, long realizedCount, long offscreenCount);
}
