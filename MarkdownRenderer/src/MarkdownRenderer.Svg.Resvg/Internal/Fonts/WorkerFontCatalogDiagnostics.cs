using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

namespace MarkdownRenderer.Svg.Resvg.Internal.Fonts;

/// <summary>
/// Records opt-in timing evidence for worker font-catalog initialization.
/// </summary>
internal static class WorkerFontCatalogDiagnostics
{
    internal static void RecordFontCatalogWaitDeadline(
        WindowsWorkerProcess worker,
        long workerCpuAtStart,
        TimeSpan elapsed,
        string initializationPhase)
    {
        long workerCpuAtTimeout = workerCpuAtStart >= 0 ? worker.GetProcessCpuTicks() : -1;
        int workerCpuMilliseconds = workerCpuAtStart >= 0 && workerCpuAtTimeout >= workerCpuAtStart
            ? (int)Math.Min((workerCpuAtTimeout - workerCpuAtStart) / 10_000, int.MaxValue)
            : -1;
        WorkerMemorySnapshot memory = WorkerTimeoutEvents.Log.IsEnabled()
            ? worker.GetProcessMemorySnapshot()
            : WorkerMemorySnapshot.Unavailable;
        WorkerTimeoutEvents.Log.Timeout(
            (int)WorkerOperation.Hello,
            (int)WorkerSchedulingPolicy.InitializationDeadline.TotalMilliseconds,
            workerCpuMilliseconds,
            transportPhase: -1,
            requestWriteMilliseconds: -1,
            workerExited: worker.HasExited ? 1 : 0,
            openProgressPhase: -1,
            workerWorkingSetKiB: memory.WorkingSetKiB,
            workerPrivateCommitKiB: memory.PrivateCommitKiB,
            workerPageFaults: memory.PageFaults,
            elapsedWallMilliseconds: (int)Math.Min(elapsed.TotalMilliseconds, int.MaxValue),
            workerInputSha256: string.Empty,
            workerExecutableSha256: worker.WorkerExecutableSha256 ?? string.Empty);
        WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
    }

    internal static void RecordFontCatalogInitializationPhases(
        string detail,
        long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long workerInstanceId)
    {
        if (diagnosticWorkId <= 0 ||
            !FontCatalogTimingPayload.TryParse(detail, out FontCatalogPhaseTiming[] phases))
        {
            return;
        }

        foreach (FontCatalogPhaseTiming phase in phases)
        {
            SvgWorkerStageTrace.RecordElapsed(
                diagnosticWorkId,
                phase.StageId,
                workerInstanceId,
                diagnosticRequestId,
                diagnosticDocumentId,
                phase.WallStopwatchTicks,
                phase.WorkerCpuTicks);
        }
    }
}
