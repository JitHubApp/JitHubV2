using System.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Turns a worker lease into one whose worker has published the caller's font
/// generation. While the catalog is prepared or scanned, the caller parks on a
/// detached font-wait reservation instead of holding the scheduling slot that
/// geometry-only documents need.
/// </summary>
internal sealed class WorkerTextAdmission(
    WorkerScheduler scheduler,
    WorkerRequestFactory requests,
    ResvgMarkdownSvgRendererOptions options)
{
    private readonly WorkerScheduler _scheduler = scheduler;
    private readonly WorkerRequestFactory _requests = requests;
    private readonly ResvgMarkdownSvgRendererOptions _options = options;

    internal async Task<TextWorkerAdmission> PrepareTextWorkerAdmissionAsync(
        WorkerLease initialLease,
        WindowsWorkerProcess initialWorker,
        WorkerOperation operation,
        MarkdownSvgRenderPriority priority,
        long requestGeneration,
        CancellationToken cancellationToken,
        MarkdownSvgRenderDemand? demand
        , long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels
        )
    {
        WorkerFontCatalogGeneration.ValidateRequest(requestGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        if (requestGeneration != _requests.FontGeneration)
            throw new OperationCanceledException("The SVG font environment changed before font readiness was acquired.");

        if (initialWorker.ReadyFontGeneration == requestGeneration)
            return new TextWorkerAdmission(initialLease, initialWorker);
        if (initialWorker.ReadyFontGeneration > requestGeneration)
            throw new WorkerProtocolException("The SVG worker font generation is newer than the host generation.");

        // Convert the active slot into one bounded logical admission before
        // releasing it. Queue entries remain source-free; this ticket accounts
        // for the caller's still-owned preflight while it waits off-slot.
        var elapsed = Stopwatch.StartNew();
        using var readinessDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _scheduler.DisposeToken);
        readinessDeadline.CancelAfter(WorkerSchedulingPolicy.InitializationDeadline);
        CancellationToken waitToken = readinessDeadline.Token;
        WorkerFontWaitReservation? reservation = _scheduler.ReserveDetachedFontWait(initialLease);
        initialLease.Dispose();
        SemaphoreSlim readinessGate = _scheduler.PrimaryFontCatalogGate;
        bool gateEntered = false;
        WorkerLease? candidateLease = null;
        bool transferred = false;
        long initializationWallStarted = 0;
        long workerCpuAtStart = -1;
        WindowsWorkerProcess? lastProbeWorker = null;
        bool lastProbeWasInitialCatalog = false;
        long fontWaitStartedAt = 0;
        string initializationPhase = "font-catalog-startup";
        try
        {
            fontWaitStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
            await readinessGate.WaitAsync(waitToken).ConfigureAwait(false);
            gateEntered = true;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                waitToken.ThrowIfCancellationRequested();
                if (requestGeneration != _requests.FontGeneration)
                    throw new OperationCanceledException("The SVG font environment changed while waiting for its catalog.");

                // Once the primary's generation is known current, do not hold
                // the refresh gate while this operation waits in the normal
                // queue. That lets concurrently admitted visible text renders
                // build queue pressure and preserves secondary-worker policy.
                if (candidateLease is null && _scheduler.IsPrimaryFontCatalogReady(requestGeneration))
                {
                    readinessGate.Release();
                    gateEntered = false;
                    candidateLease = await _scheduler.AcquireAsync(
                        operation,
                        demand?.Priority ?? priority,
                        waitToken,
                        requiresFontCatalog: true,
                        fontWaitReservation: reservation,
                        demandTicket: demand).ConfigureAwait(false);
                    reservation = null; // Admission atomically consumed the ticket.
                    waitToken.ThrowIfCancellationRequested();

                    WindowsWorkerProcess readyWorker = await _scheduler.EnsureWorkerAsync(
                        candidateLease
                        , diagnosticWorkId,
                        diagnosticRequestId,
                        diagnosticDocumentId,
                        diagnosticSourceBytes,
                        diagnosticTargetPixels
                        ).ConfigureAwait(false);
                    if (requestGeneration != _requests.FontGeneration)
                        throw new OperationCanceledException("The SVG font environment changed before text work was dispatched.");
                    if (readyWorker.ReadyFontGeneration == requestGeneration)
                    {
                        _scheduler.RebindLeaseCancellation(candidateLease, cancellationToken, waitToken);
                        transferred = true;
                        return new TextWorkerAdmission(candidateLease, readyWorker);
                    }
                    if (readyWorker.ReadyFontGeneration > requestGeneration)
                        throw new WorkerProtocolException("The SVG worker font generation is newer than the host generation.");

                    // A restarted or stale secondary can race the readiness
                    // snapshot. Park this caller again, then serialize one
                    // short HELLO probe under the gate before retrying.
                    reservation = _scheduler.ReserveDetachedFontWait(candidateLease);
                    candidateLease.Dispose();
                    candidateLease = null;
                    await readinessGate.WaitAsync(waitToken).ConfigureAwait(false);
                    gateEntered = true;
                    continue;
                }

                candidateLease = await _scheduler.AcquireAsync(
                    operation,
                    demand?.Priority ?? priority,
                    waitToken,
                    requiresFontCatalog: true,
                    fontWaitReservation: reservation,
                    demandTicket: demand).ConfigureAwait(false);
                reservation = null; // Admission atomically consumed the ticket.
                waitToken.ThrowIfCancellationRequested();

                WindowsWorkerProcess worker = await _scheduler.EnsureWorkerAsync(
                    candidateLease
                    , diagnosticWorkId,
                    diagnosticRequestId,
                    diagnosticDocumentId,
                    diagnosticSourceBytes,
                    diagnosticTargetPixels
                    ).ConfigureAwait(false);

                if (requestGeneration != _requests.FontGeneration)
                    throw new OperationCanceledException("The SVG font environment changed before its catalog probe.");

                if (worker.ReadyFontGeneration >= 0)
                {
                    if (worker.ReadyFontGeneration > requestGeneration)
                        throw new WorkerProtocolException("The SVG worker published a font generation newer than the host target.");
                    if (worker.ReadyFontGeneration == requestGeneration)
                    {
                        _scheduler.RebindLeaseCancellation(candidateLease, cancellationToken, waitToken);
                        readinessGate.Release();
                        gateEntered = false;
                        transferred = true;
                        return new TextWorkerAdmission(candidateLease, worker);
                    }
                }

                Task catalogSupply = worker.EnsureFontCatalogSuppliedAsync(
                    _requests.CreateControlRequest(WorkerOperation.FontCatalog, worker, fontGeneration: requestGeneration),
                    waitToken);
                if (!catalogSupply.IsCompleted)
                {
                    // File enumeration and mapping preparation must not retain
                    // the scheduling slot needed by geometry-only documents.
                    reservation = _scheduler.ReserveDetachedFontWait(candidateLease);
                    candidateLease.Dispose();
                    candidateLease = null;
                    initializationPhase = "font-catalog-mappings";
                    await catalogSupply.ConfigureAwait(false);
                    continue;
                }
                await catalogSupply.ConfigureAwait(false);

                TimeSpan remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    if (lastProbeWorker is not null && lastProbeWasInitialCatalog)
                        WorkerFontCatalogDiagnostics.RecordFontCatalogWaitDeadline(
                            lastProbeWorker,
                            workerCpuAtStart,
                            elapsed.Elapsed,
                            initializationPhase);
                    throw new WorkerInitializationDeadlineException(
                        $"The resvg worker did not publish font catalog generation {requestGeneration} before the initialization deadline (phase: {initializationPhase}).",
                        new TimeoutException());
                }

                if (initializationWallStarted == 0)
                {
                    initializationWallStarted = Stopwatch.GetTimestamp();
                    workerCpuAtStart = WorkerTimeoutEvents.Log.IsEnabled()
                        ? worker.GetProcessCpuTicks()
                        : -1;
                }
                lastProbeWorker = worker;
                lastProbeWasInitialCatalog = worker.ReadyFontGeneration < 0;

                WorkerResponse response;
                try
                {
                    response = await worker.ExchangeAsync(
                        _requests.CreateControlRequest(
                            WorkerOperation.Hello,
                            worker,
                            fontGeneration: requestGeneration),
                        remaining < _options.RequestDeadline ? remaining : _options.RequestDeadline,
                        waitToken,
                        timeoutEvidenceScope: new WorkerTimeoutEvidenceScope(
                            WorkerSchedulingPolicy.InitializationDeadline,
                            workerCpuAtStart,
                            initializationWallStarted)
                        , diagnosticWorkId: diagnosticWorkId,
                        diagnosticStageId: (int)SvgWorkerStageId.FontCatalogHelloRoundTrip,
                        diagnosticDocumentId: diagnosticDocumentId,
                        diagnosticSourceBytes: diagnosticSourceBytes,
                        diagnosticTargetPixels: diagnosticTargetPixels
                        ).ConfigureAwait(false);
                }
                catch (Exception exception) when (WorkerResponseValidation.IsWorkerTransportFailure(exception))
                {
                    _scheduler.Invalidate(candidateLease.Slot, worker);
                    bool initialCatalog = worker.ReadyFontGeneration < 0;
                    if (initialCatalog)
                        _scheduler.RecordStartupFailure();
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                    throw new WorkerInitializationDeadlineException(
                        $"The resvg worker could not confirm font catalog generation {requestGeneration} (phase: {initializationPhase}).",
                        exception);
                }

                cancellationToken.ThrowIfCancellationRequested();
                waitToken.ThrowIfCancellationRequested();
                if (requestGeneration != _requests.FontGeneration)
                    throw new OperationCanceledException("The SVG font environment changed while its catalog probe was active.");
                try
                {
                    WorkerResponseValidation.ValidateFontCatalogProbeResponse(requestGeneration, response);
                }
                catch (WorkerProtocolException exception)
                {
                    throw WorkerResponseValidation.InvalidateMalformedFontCatalogProbe(
                        exception,
                        () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            waitToken.ThrowIfCancellationRequested();
                            if (requestGeneration != _requests.FontGeneration)
                            {
                                throw new OperationCanceledException(
                                    "The SVG font environment changed while its catalog probe was being validated.");
                            }

                            if (lastProbeWasInitialCatalog)
                                _scheduler.RecordStartupFailure();
                        },
                        () => _scheduler.Invalidate(candidateLease.Slot, worker));
                }

                if (response.Status == WorkerStatus.FontCatalogFailure)
                {
                    if (requestGeneration != _requests.FontGeneration)
                        throw new OperationCanceledException("The SVG font environment changed while its catalog probe was active.");
                    throw new WorkerInitializationException(
                        "The resvg worker could not refresh its Windows font catalog.",
                        new WorkerProtocolException(response.Detail.Length == 0
                            ? "The font catalog refresh failed."
                            : response.Detail));
                }

                if (response.Status == WorkerStatus.Ok)
                {
                    worker.MarkFontCatalogReady(response.ReadyFontGeneration);
                    WorkerFontCatalogDiagnostics.RecordFontCatalogInitializationPhases(
                        response.Detail,
                        diagnosticWorkId,
                        diagnosticRequestId,
                        diagnosticDocumentId,
                        worker.InstanceId);
                    readinessGate.Release();
                    gateEntered = false;
                    _scheduler.RebindLeaseCancellation(candidateLease, cancellationToken, waitToken);
                    transferred = true;
                    return new TextWorkerAdmission(candidateLease, worker);
                }

                initializationPhase = response.Detail switch
                {
                    "font-catalog-mappings" or
                    "user-local-fonts" or
                    "user-roaming-fonts" or
                    "font-family-defaults" or
                    "text-pipeline-warmup" => response.Detail,
                    _ => "unknown",
                };

                // Keep the caller's logical admission, but relinquish the only
                // worker lease while native scanning continues asynchronously.
                reservation = _scheduler.ReserveDetachedFontWait(candidateLease);
                candidateLease.Dispose();
                candidateLease = null;
                remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    continue;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)),
                    waitToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && readinessDeadline.IsCancellationRequested)
        {
            if (_scheduler.DisposeToken.IsCancellationRequested)
                throw new ObjectDisposedException(nameof(WorkerPool));
            if (lastProbeWorker is not null && lastProbeWasInitialCatalog)
                WorkerFontCatalogDiagnostics.RecordFontCatalogWaitDeadline(
                    lastProbeWorker,
                    workerCpuAtStart,
                    elapsed.Elapsed,
                    initializationPhase);
            throw new WorkerInitializationDeadlineException(
                $"The resvg worker did not publish font catalog generation {requestGeneration} before the initialization deadline (phase: {initializationPhase}).",
                new TimeoutException());
        }
        finally
        {
            if (candidateLease is not null && !transferred)
                candidateLease.Dispose();
            reservation?.Dispose();
            if (gateEntered)
                readinessGate.Release();
            if (fontWaitStartedAt != 0)
            {
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.FontCatalogWait,
                    initialWorker.InstanceId,
                    diagnosticRequestId,
                    diagnosticDocumentId,
                    fontWaitStartedAt,
                    sourceBytes: diagnosticSourceBytes,
                    targetPixels: diagnosticTargetPixels);
            }
        }
    }
}
