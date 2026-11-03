using System.Diagnostics;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Opt-in, numeric-only worker timing. A work id starts at each worker-pool
/// operation and is carried as internal host metadata, so correlation does
/// not depend on execution-context flow through the worker-pool pump.
/// </summary>
internal static class SvgWorkerStageTrace
{
    internal static long BeginWork() => ResvgEventSource.Log.BeginWork();

    internal static long Start(long workId) =>
        workId > 0 ? Stopwatch.GetTimestamp() : 0;

    internal static void Record(
        long workId,
        SvgWorkerStageId stage,
        long workerInstanceId,
        ulong requestId,
        ulong documentId,
        long startedAt,
        long workerCpuTicks = -1,
        long sourceBytes = 0,
        long targetPixels = 0)
    {
        if (workId <= 0 || startedAt == 0 ||
            !ResvgEventSource.Log.IsWorkerStageMeasurementEnabled())
        {
            return;
        }

        long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - startedAt);
        ResvgEventSource.Log.WorkerStage(
            workId,
            (int)stage,
            workerInstanceId,
            ToDiagnosticId(requestId),
            ToDiagnosticId(documentId),
            elapsedTicks,
            workerCpuTicks,
            Math.Max(0, sourceBytes),
            Math.Max(0, targetPixels));
    }

    internal static void RecordElapsed(
        long workId,
        SvgWorkerStageId stage,
        long workerInstanceId,
        ulong requestId,
        ulong documentId,
        long elapsedStopwatchTicks,
        long workerCpuTicks)
    {
        if (workId <= 0 || elapsedStopwatchTicks < 0 || workerCpuTicks < -1 ||
            !ResvgEventSource.Log.IsWorkerStageMeasurementEnabled())
        {
            return;
        }

        ResvgEventSource.Log.WorkerStage(
            workId,
            (int)stage,
            workerInstanceId,
            ToDiagnosticId(requestId),
            ToDiagnosticId(documentId),
            elapsedStopwatchTicks,
            workerCpuTicks,
            sourceBytes: 0,
            targetPixels: 0);
    }

    internal static long ToDiagnosticId(ulong value) =>
        value <= long.MaxValue ? (long)value : unchecked((long)value);
}
