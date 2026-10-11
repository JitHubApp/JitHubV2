#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System;
using System.Diagnostics;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Audit-only counters for the initial layout build. Capture freezes the
/// accumulator after the worker completes so later relayouts cannot contaminate it.
/// </summary>
internal sealed class MarkdownLayoutDiagnostics
{
    private int _topLevelBlockCount;
    private int _canvasTextLayoutConstructorCalls;
    private long _canvasTextLayoutConstructorTicks;
    private long _blockConstructionTicks;
    private long _measureArrangeTicks;
    private long _snapshotConstructionTicks;
    private int _captured;
    private long _dispatcherHeartbeatLastTimestamp;
    private long _dispatcherHeartbeatMaxGapTicks;
    private int _dispatcherHeartbeatSampleCount;
    private int _dispatcherHeartbeatCaptured;
    private long _dispatcherSentinelWorkerCompletionTimestamp;
    private long _dispatcherSentinelDispatchedTimestamp;
    private int _dispatcherSentinelEnqueued;

    internal bool IsCaptured => Volatile.Read(ref _captured) != 0;

    internal void SetTopLevelBlockCount(int count)
    {
        if (Volatile.Read(ref _captured) == 0)
            Volatile.Write(ref _topLevelBlockCount, count);
    }

    internal void RecordCanvasTextLayoutConstructor(long elapsedTicks)
    {
        if (Volatile.Read(ref _captured) != 0)
            return;

        Interlocked.Increment(ref _canvasTextLayoutConstructorCalls);
        Interlocked.Add(ref _canvasTextLayoutConstructorTicks, Math.Max(0, elapsedTicks));
    }

    internal void RecordBlockConstruction(long elapsedTicks) =>
        AddTicks(ref _blockConstructionTicks, elapsedTicks);

    internal void RecordMeasureArrange(long elapsedTicks) =>
        AddTicks(ref _measureArrangeTicks, elapsedTicks);

    internal void RecordSnapshotConstruction(long elapsedTicks) =>
        AddTicks(ref _snapshotConstructionTicks, elapsedTicks);

    internal void StartDispatcherHeartbeat(long timestamp) =>
        Volatile.Write(ref _dispatcherHeartbeatLastTimestamp, Math.Max(0, timestamp));

    internal void RecordDispatcherHeartbeat(long timestamp)
    {
        if (Volatile.Read(ref _dispatcherHeartbeatCaptured) != 0 || timestamp <= 0)
            return;

        long previous = Interlocked.Exchange(ref _dispatcherHeartbeatLastTimestamp, timestamp);
        if (previous <= 0 || timestamp <= previous)
            return;

        UpdateMaximum(ref _dispatcherHeartbeatMaxGapTicks, timestamp - previous);
        Interlocked.Increment(ref _dispatcherHeartbeatSampleCount);
    }

    internal void RecordDispatcherSentinelEnqueue(long workerCompletionTimestamp, bool enqueued)
    {
        Volatile.Write(
            ref _dispatcherSentinelWorkerCompletionTimestamp,
            Math.Max(0, workerCompletionTimestamp));
        Volatile.Write(ref _dispatcherSentinelEnqueued, enqueued ? 1 : 0);
    }

    internal void RecordDispatcherSentinelDispatched(long timestamp)
    {
        if (timestamp > 0)
            Interlocked.CompareExchange(ref _dispatcherSentinelDispatchedTimestamp, timestamp, 0);
    }

    internal MarkdownInitialLayoutDispatcherDiagnosticsSnapshot CaptureDispatcher(bool freezeHeartbeat = false)
    {
        if (freezeHeartbeat)
            Interlocked.Exchange(ref _dispatcherHeartbeatCaptured, 1);

        long workerCompleted = Interlocked.Read(ref _dispatcherSentinelWorkerCompletionTimestamp);
        long sentinelDispatched = Interlocked.Read(ref _dispatcherSentinelDispatchedTimestamp);
        long sentinelDelayTicks = workerCompleted > 0 && sentinelDispatched >= workerCompleted
            ? sentinelDispatched - workerCompleted
            : 0;

        return new MarkdownInitialLayoutDispatcherDiagnosticsSnapshot(
            Volatile.Read(ref _dispatcherSentinelEnqueued) != 0,
            sentinelDispatched > 0,
            ToMilliseconds(sentinelDelayTicks),
            Volatile.Read(ref _dispatcherHeartbeatSampleCount),
            ToMilliseconds(Interlocked.Read(ref _dispatcherHeartbeatMaxGapTicks)));
    }

    internal MarkdownLayoutDiagnosticsSnapshot Capture()
    {
        Interlocked.Exchange(ref _captured, 1);
        return new MarkdownLayoutDiagnosticsSnapshot(
            Volatile.Read(ref _topLevelBlockCount),
            Volatile.Read(ref _canvasTextLayoutConstructorCalls),
            ToMilliseconds(Interlocked.Read(ref _canvasTextLayoutConstructorTicks)),
            ToMilliseconds(Interlocked.Read(ref _blockConstructionTicks)),
            ToMilliseconds(Interlocked.Read(ref _measureArrangeTicks)),
            ToMilliseconds(Interlocked.Read(ref _snapshotConstructionTicks)));
    }

    private void AddTicks(ref long field, long elapsedTicks)
    {
        if (Volatile.Read(ref _captured) == 0)
            Interlocked.Add(ref field, Math.Max(0, elapsedTicks));
    }

    private static void UpdateMaximum(ref long field, long value)
    {
        long observed = Volatile.Read(ref field);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(ref field, value, observed);
            if (previous == observed)
                return;

            observed = previous;
        }
    }

    private static double ToMilliseconds(long elapsedTicks) =>
        elapsedTicks > 0 ? Stopwatch.GetElapsedTime(0, elapsedTicks).TotalMilliseconds : 0;
}

internal readonly record struct MarkdownLayoutDiagnosticsSnapshot(
    int TopLevelBlockCount,
    int CanvasTextLayoutConstructorCalls,
    double CanvasTextLayoutConstructorMilliseconds,
    double BlockConstructionMilliseconds,
    double MeasureArrangeMilliseconds,
    double SnapshotConstructionMilliseconds);

internal readonly record struct MarkdownInitialLayoutDispatcherDiagnosticsSnapshot(
    bool SentinelEnqueued,
    bool SentinelDispatched,
    double SentinelDelayMilliseconds,
    int HeartbeatSampleCount,
    double HeartbeatMaxGapMilliseconds);
#endif
