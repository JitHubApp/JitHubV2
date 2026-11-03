using System.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Warms the optional secondary worker: process startup while it owns its
/// slot, then font-catalog readiness through short probes that leave the slot
/// free for geometry work. Any failure only disables this optimization.
/// </summary>
internal sealed class SecondaryWorkerWarmup(
    ISecondaryWorkerWarmupHost host,
    WorkerRequestFactory requests,
    ResvgMarkdownSvgRendererOptions options)
{
    private readonly ISecondaryWorkerWarmupHost _host = host;
    private readonly WorkerRequestFactory _requests = requests;
    private readonly ResvgMarkdownSvgRendererOptions _options = options;

    internal async Task WarmSecondaryAsync(
        WorkerSlot slot,
        WorkerLease lease)
    {
        WindowsWorkerProcess? worker = slot.Worker;
        bool warmupSucceeded = false;
        try
        {
            await slot.Readiness!.WarmAsync(
                async _ =>
                {
                    worker = await _host.EnsureWorkerAsync(
                        lease
                        , 0, 0, 0, 0, 0
                        ).ConfigureAwait(false);
                    // Process startup is the only long portion that owns the
                    // slot continuously. A ready worker can serve geometry
                    // while the font catalog coordinator scans in background.
                    slot.Readiness!.MarkWorkerReady();
                    lease.Dispose();
                },
                () => _host.ShouldWarmSecondaryFontCatalog(slot, worker),
                async cancellationToken =>
                {
                    if (worker is null)
                        throw new InvalidOperationException("The secondary SVG worker was not started before font warmup.");

                    return await EnsureSecondaryFontCatalogReadyAsync(
                        slot,
                        worker,
                        cancellationToken).ConfigureAwait(false);
                },
                CancellationToken.None).ConfigureAwait(false);
            warmupSucceeded = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // This worker is an optional capacity optimization. A cold-start
            // failure must not fail or replay content; the primary keeps serving
            // the queue under its existing deadlines and quarantine policy.
            slot.Readiness!.FailWarmup();
        }
        finally
        {
            _host.CompleteSecondaryWarmup(slot, warmupSucceeded);
            lease.Dispose();
        }
    }

    private async Task<long> EnsureSecondaryFontCatalogReadyAsync(
        WorkerSlot slot,
        WindowsWorkerProcess initialWorker,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        WindowsWorkerProcess worker = initialWorker;
        string initializationPhase = "font-catalog-refresh";
        while (_host.CanContinueSecondaryWarmup(slot, worker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long targetGeneration = _requests.FontGeneration;
            WorkerFontCatalogGeneration.ValidateRequest(targetGeneration);
            if (worker.ReadyFontGeneration == targetGeneration)
                return targetGeneration;

            TimeSpan remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                return worker.ReadyFontGeneration;
            }

            await worker.EnsureFontCatalogSuppliedAsync(
                _requests.CreateControlRequest(WorkerOperation.FontCatalog, worker, fontGeneration: targetGeneration),
                cancellationToken).ConfigureAwait(false);
            remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase("font-catalog-mappings");
                return worker.ReadyFontGeneration;
            }

            WorkerLease? lease = await AcquireSecondaryFontProbeAsync(
                slot,
                worker,
                remaining,
                cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                if (elapsed.Elapsed >= WorkerSchedulingPolicy.InitializationDeadline)
                {
                    slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                    WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                }
                return worker.ReadyFontGeneration;
            }

            WorkerResponse response;
            try
            {
                worker = await _host.EnsureWorkerAsync(
                    lease
                    , 0, 0, 0, 0, 0
                    ).ConfigureAwait(false);
                if (!_host.CanContinueSecondaryWarmup(slot, worker))
                    return worker.ReadyFontGeneration;

                targetGeneration = _requests.FontGeneration;
                remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                    WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                    return worker.ReadyFontGeneration;
                }

                response = await worker.ExchangeAsync(
                    _requests.CreateControlRequest(
                        WorkerOperation.Hello,
                        worker,
                        fontGeneration: targetGeneration),
                    remaining < _options.RequestDeadline ? remaining : _options.RequestDeadline,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lease.Dispose();
            }

            if (response.OutputLength != 0)
                throw new WorkerProtocolException("The resvg worker returned output for secondary font-catalog initialization.");
            if (targetGeneration != _requests.FontGeneration)
                continue;
            WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(targetGeneration, response);
            if (response.Status == WorkerStatus.FontCatalogFailure)
            {
                // Font enumeration is environmental. Keep the healthy optional
                // process for geometry and retry only after a newer notification.
                slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                return worker.ReadyFontGeneration;
            }
            if (response.Status == WorkerStatus.Ok)
            {
                if (response.ReadyFontGeneration != targetGeneration)
                    throw new WorkerProtocolException("The secondary worker published a font generation different from the host target.");
                worker.MarkFontCatalogReady(response.ReadyFontGeneration);
                slot.Readiness!.MarkFontCatalogReady(response.ReadyFontGeneration);
                WorkerFontCatalogDiagnostics.RecordFontCatalogInitializationPhases(response.Detail, 0, 0, 0, worker.InstanceId);
                return response.ReadyFontGeneration;
            }
            if (response.Status != WorkerStatus.FontCatalogPending)
                throw new WorkerProtocolException("The secondary worker returned an invalid font-catalog probe status.");

            initializationPhase = response.Detail switch
            {
                "font-catalog-mappings" or
                "user-local-fonts" or
                "user-roaming-fonts" or
                "font-family-defaults" or
                "text-pipeline-warmup" => response.Detail,
                _ => "unknown",
            };
            remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)),
                    cancellationToken).ConfigureAwait(false);
        }

        return worker.ReadyFontGeneration;
    }

    private async Task<WorkerLease?> AcquireSecondaryFontProbeAsync(
        WorkerSlot slot,
        WindowsWorkerProcess worker,
        TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(remaining);
        Task<WorkerLease> acquire = _host.AcquireSecondaryFontProbeAsync(
            worker.InstanceId,
            probeCancellation.Token);
        bool transferred = false;
        try
        {
            while (!acquire.IsCompleted)
            {
                await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken))
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (probeCancellation.IsCancellationRequested || !_host.CanContinueSecondaryWarmup(slot, worker))
                    return null;
            }

            WorkerLease lease = await acquire.ConfigureAwait(false);
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
            {
                // Cancellation can race with assignment. Drain and dispose a
                // lease that was granted just before the token was observed.
                await WorkerAdmissionTasks.CancelAndDrainAcquireAsync(acquire, probeCancellation.Cancel).ConfigureAwait(false);
            }
        }
    }
}
