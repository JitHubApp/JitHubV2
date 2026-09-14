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
}

/// <summary>
/// Typed, allocation-free observer used by the in-process pinned-machine
/// harness. External profilers continue to consume the EventSource contract;
/// this sink avoids letting EventListener payload allocation perturb a 4 KiB
/// per-frame gate.
/// </summary>
internal interface IMarkdownPerformanceSink
{
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
