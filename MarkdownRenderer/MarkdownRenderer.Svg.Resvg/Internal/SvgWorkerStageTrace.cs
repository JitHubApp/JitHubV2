#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout.Boxes;
#endif

namespace MarkdownRenderer.Svg.Resvg.Internal;

#if MARKDOWN_LAYOUT_DIAGNOSTICS
internal enum SvgWorkerStageId
{
    PoolSchedulingQueueWait = 101,
    WorkerStartupAndPipeHandshake = 102,
    FontCatalogWait = 103,
    WorkerTransactionQueueWait = 104,
    DocumentMissingProbe = 105,
    DocumentMissingAttach = 106,
    RenderTransaction = 107,
    OpenTransaction = 108,
    FontCatalogHelloRoundTrip = 109,
    FontCatalogDatabaseCreate = 110,
    FontCatalogMappedFonts = 111,
    FontCatalogUserLocalFonts = 112,
    FontCatalogUserRoamingFonts = 113,
    FontCatalogFamilyDefaults = 114,
    FontCatalogTextPipelineWarmup = 115,
    FontCatalogTotal = 116,
    PipeRequestWrite = 117,
    PipeRequestFlush = 118,
    PipeResponseRead = 119,
    PreflightDispatchQueueWait = 120,
}
#endif

#if MARKDOWN_LAYOUT_DIAGNOSTICS
/// <summary>
/// Opt-in, numeric-only worker timing. Work IDs are copied at the public
/// request boundary and carried as internal host metadata so correlation does
/// not depend on execution-context flow through the worker-pool pump.
/// </summary>
internal static class SvgWorkerStageTrace
{
    internal static long CaptureCurrentWorkId()
    {
        if (!MarkdownPerformanceEventSource.Log.IsSvgWorkerStageMeasurementEnabled())
            return 0;

        return MarkdownSvgStageTrace.CurrentWorkId;
    }

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
            !MarkdownPerformanceEventSource.Log.IsSvgWorkerStageMeasurementEnabled())
        {
            return;
        }

        long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - startedAt);
        MarkdownPerformanceEventSource.Log.SvgWorkerStage(
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
            !MarkdownPerformanceEventSource.Log.IsSvgWorkerStageMeasurementEnabled())
        {
            return;
        }

        MarkdownPerformanceEventSource.Log.SvgWorkerStage(
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
#endif
