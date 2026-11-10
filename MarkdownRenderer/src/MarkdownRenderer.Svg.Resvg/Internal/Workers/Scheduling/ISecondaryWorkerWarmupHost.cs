using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

/// <summary>The scheduler services that <see cref="SecondaryWorkerWarmup"/> calls back into.</summary>
internal interface ISecondaryWorkerWarmupHost
{
    /// <summary>Starts or reuses the worker for the slot that <paramref name="lease"/> owns.</summary>
    /// <param name="lease">The admitted slot lease.</param>
    /// <param name="diagnosticWorkId">The diagnostic work ID, or 0.</param>
    /// <param name="diagnosticRequestId">The diagnostic request ID, or 0.</param>
    /// <param name="diagnosticDocumentId">The diagnostic document ID, or 0.</param>
    /// <param name="diagnosticSourceBytes">The diagnostic source size, or 0.</param>
    /// <param name="diagnosticTargetPixels">The diagnostic target pixels, or 0.</param>
    /// <returns>The slot's live worker.</returns>
    Task<WindowsWorkerProcess> EnsureWorkerAsync(
        WorkerLease lease,
        long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels);

    /// <summary>Queues a short font probe pinned to the secondary worker.</summary>
    /// <param name="workerInstanceId">The secondary worker instance.</param>
    /// <param name="cancellationToken">Cancels the queued probe.</param>
    /// <returns>The probe's slot lease.</returns>
    Task<WorkerLease> AcquireSecondaryFontProbeAsync(long workerInstanceId, CancellationToken cancellationToken);

    /// <summary>Whether visible text demand justifies warming the secondary font catalog.</summary>
    /// <param name="slot">The secondary slot.</param>
    /// <param name="worker">The secondary slot's worker, if started.</param>
    /// <returns>True when the catalog should be warmed now.</returns>
    bool ShouldWarmSecondaryFontCatalog(WorkerSlot slot, WindowsWorkerProcess? worker);

    /// <summary>Whether the slot is still the pool's secondary and still owns <paramref name="worker"/>.</summary>
    /// <param name="slot">The secondary slot.</param>
    /// <param name="worker">The worker being warmed.</param>
    /// <returns>True while warmup may continue.</returns>
    bool CanContinueSecondaryWarmup(WorkerSlot slot, WindowsWorkerProcess worker);

    /// <summary>Records the warmup outcome and retires a failed secondary worker.</summary>
    /// <param name="slot">The secondary slot.</param>
    /// <param name="warmupSucceeded">Whether warmup succeeded.</param>
    void CompleteSecondaryWarmup(WorkerSlot slot, bool warmupSucceeded);
}
