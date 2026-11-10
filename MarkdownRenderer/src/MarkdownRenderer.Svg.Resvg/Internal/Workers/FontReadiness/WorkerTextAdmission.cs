using System.Diagnostics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Fonts;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;

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
        var wait = new WorkerFontReadinessWait(
            operation,
            priority,
            requestGeneration,
            demand,
            cancellationToken,
            waitToken,
            _scheduler.PrimaryFontCatalogGate,
            elapsed,
            diagnosticWorkId,
            diagnosticRequestId,
            diagnosticDocumentId,
            diagnosticSourceBytes,
            diagnosticTargetPixels)
        {
            Reservation = reservation,
        };
        try
        {
            wait.FontWaitStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
            await wait.ReadinessGate.WaitAsync(waitToken).ConfigureAwait(false);
            wait.GateEntered = true;
            return await WaitForReadyWorkerAsync(wait).ConfigureAwait(false);
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
            throw CreateReadinessDeadlineException(wait);
        }
        finally
        {
            EndReadinessWait(wait, initialWorker);
        }
    }

    /// <summary>
    /// Releases whatever the wait still owns, in the order lease, reservation,
    /// gate, and records the font-wait stage.
    /// </summary>
    private static void EndReadinessWait(WorkerFontReadinessWait wait, WindowsWorkerProcess initialWorker)
    {
        if (wait.CandidateLease is not null && !wait.Transferred)
            wait.CandidateLease.Dispose();
        wait.Reservation?.Dispose();
        if (wait.GateEntered)
            wait.ReadinessGate.Release();
        if (wait.FontWaitStartedAt != 0)
        {
            SvgWorkerStageTrace.Record(
                wait.DiagnosticWorkId,
                SvgWorkerStageId.FontCatalogWait,
                initialWorker.InstanceId,
                wait.DiagnosticRequestId,
                wait.DiagnosticDocumentId,
                wait.FontWaitStartedAt,
                sourceBytes: wait.DiagnosticSourceBytes,
                targetPixels: wait.DiagnosticTargetPixels);
        }
    }

    /// <summary>
    /// Repeats lease, catalog supply and HELLO probe until a worker publishes the
    /// caller's generation. The caller entered <see cref="WorkerFontReadinessWait.ReadinessGate"/>
    /// before the first pass; every exit leaves cleanup to the caller's finally.
    /// </summary>
    private async Task<TextWorkerAdmission> WaitForReadyWorkerAsync(WorkerFontReadinessWait wait)
    {
        while (true)
        {
            ThrowIfAbandoned(wait, "The SVG font environment changed while waiting for its catalog.");

            // Once the primary's generation is known current, do not hold
            // the refresh gate while this operation waits in the normal
            // queue. That lets concurrently admitted visible text renders
            // build queue pressure and preserves secondary-worker policy.
            if (wait.CandidateLease is null && _scheduler.IsPrimaryFontCatalogReady(wait.RequestGeneration))
            {
                TextWorkerAdmission? primaryAdmission = await TryAdmitAfterPrimaryReadyAsync(wait).ConfigureAwait(false);
                if (primaryAdmission is not null)
                    return primaryAdmission;
                continue;
            }

            WindowsWorkerProcess worker = await AcquireCandidateWorkerAsync(wait).ConfigureAwait(false);
            if (wait.RequestGeneration != _requests.FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before its catalog probe.");

            if (TryAdmitCurrentWorker(wait, worker) is { } currentAdmission)
                return currentAdmission;

            if (!await SupplyFontCatalogAsync(wait, worker).ConfigureAwait(false))
                continue;

            TimeSpan remaining = WorkerSchedulingPolicy.InitializationDeadline - wait.Elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw CreateReadinessDeadlineException(wait);

            WorkerResponse response = await ProbeFontCatalogAsync(wait, worker, remaining).ConfigureAwait(false);
            if (TryAdmitProbedWorker(wait, worker, response) is { } probedAdmission)
                return probedAdmission;

            await ParkUntilNextProbeAsync(wait, response).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the readiness gate and queues normally because the primary is
    /// already current. Returns <see langword="null"/> after re-parking the
    /// caller under the gate when the granted worker raced the readiness snapshot.
    /// </summary>
    private async Task<TextWorkerAdmission?> TryAdmitAfterPrimaryReadyAsync(WorkerFontReadinessWait wait)
    {
        wait.ReadinessGate.Release();
        wait.GateEntered = false;
        WindowsWorkerProcess readyWorker = await AcquireCandidateWorkerAsync(wait).ConfigureAwait(false);
        if (wait.RequestGeneration != _requests.FontGeneration)
            throw new OperationCanceledException("The SVG font environment changed before text work was dispatched.");
        if (readyWorker.ReadyFontGeneration == wait.RequestGeneration)
        {
            _scheduler.RebindLeaseCancellation(wait.CandidateLease!, wait.CancellationToken, wait.WaitToken);
            wait.Transferred = true;
            return new TextWorkerAdmission(wait.CandidateLease!, readyWorker);
        }
        if (readyWorker.ReadyFontGeneration > wait.RequestGeneration)
            throw new WorkerProtocolException("The SVG worker font generation is newer than the host generation.");

        // A restarted or stale secondary can race the readiness
        // snapshot. Park this caller again, then serialize one
        // short HELLO probe under the gate before retrying.
        ParkCandidate(wait);
        await wait.ReadinessGate.WaitAsync(wait.WaitToken).ConfigureAwait(false);
        wait.GateEntered = true;
        return null;
    }

    /// <summary>
    /// Acquires a font-catalog lease with the caller's parked ticket and starts
    /// its worker. The lease is recorded in <paramref name="wait"/> before any
    /// later check can throw, so the caller's finally releases it.
    /// </summary>
    private async Task<WindowsWorkerProcess> AcquireCandidateWorkerAsync(WorkerFontReadinessWait wait)
    {
        wait.CandidateLease = await _scheduler.AcquireAsync(
            wait.Operation,
            wait.Demand?.Priority ?? wait.Priority,
            wait.WaitToken,
            requiresFontCatalog: true,
            fontWaitReservation: wait.Reservation,
            demandTicket: wait.Demand).ConfigureAwait(false);
        wait.Reservation = null; // Admission atomically consumed the ticket.
        wait.WaitToken.ThrowIfCancellationRequested();

        return await _scheduler.EnsureWorkerAsync(
            wait.CandidateLease
            , wait.DiagnosticWorkId,
            wait.DiagnosticRequestId,
            wait.DiagnosticDocumentId,
            wait.DiagnosticSourceBytes,
            wait.DiagnosticTargetPixels
            ).ConfigureAwait(false);
    }

    /// <summary>Admits the caller when the leased worker already publishes its generation.</summary>
    private TextWorkerAdmission? TryAdmitCurrentWorker(WorkerFontReadinessWait wait, WindowsWorkerProcess worker)
    {
        if (worker.ReadyFontGeneration < 0)
            return null;
        if (worker.ReadyFontGeneration > wait.RequestGeneration)
            throw new WorkerProtocolException("The SVG worker published a font generation newer than the host target.");
        if (worker.ReadyFontGeneration != wait.RequestGeneration)
            return null;

        _scheduler.RebindLeaseCancellation(wait.CandidateLease!, wait.CancellationToken, wait.WaitToken);
        wait.ReadinessGate.Release();
        wait.GateEntered = false;
        wait.Transferred = true;
        return new TextWorkerAdmission(wait.CandidateLease!, worker);
    }

    /// <summary>
    /// Ensures the worker has the catalog mapping for the caller's generation.
    /// Returns <see langword="false"/> after parking the caller when supply had
    /// to wait, so the loop re-acquires a lease afterwards.
    /// </summary>
    private async Task<bool> SupplyFontCatalogAsync(WorkerFontReadinessWait wait, WindowsWorkerProcess worker)
    {
        Task catalogSupply = worker.EnsureFontCatalogSuppliedAsync(
            _requests.CreateControlRequest(WorkerOperation.FontCatalog, worker, fontGeneration: wait.RequestGeneration),
            wait.WaitToken);
        if (!catalogSupply.IsCompleted)
        {
            // File enumeration and mapping preparation must not retain
            // the scheduling slot needed by geometry-only documents.
            ParkCandidate(wait);
            wait.InitializationPhase = "font-catalog-mappings";
            await catalogSupply.ConfigureAwait(false);
            return false;
        }
        await catalogSupply.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sends one HELLO probe for the caller's generation, bounded by the
    /// remaining readiness deadline. A transport failure invalidates the worker.
    /// </summary>
    private async Task<WorkerResponse> ProbeFontCatalogAsync(
        WorkerFontReadinessWait wait,
        WindowsWorkerProcess worker,
        TimeSpan remaining)
    {
        if (wait.InitializationWallStarted == 0)
        {
            wait.InitializationWallStarted = Stopwatch.GetTimestamp();
            wait.WorkerCpuAtStart = WorkerTimeoutEvents.Log.IsEnabled()
                ? worker.GetProcessCpuTicks()
                : -1;
        }
        wait.LastProbeWorker = worker;
        wait.LastProbeWasInitialCatalog = worker.ReadyFontGeneration < 0;

        try
        {
            return await worker.ExchangeAsync(
                _requests.CreateControlRequest(
                    WorkerOperation.Hello,
                    worker,
                    fontGeneration: wait.RequestGeneration),
                remaining < _options.RequestDeadline ? remaining : _options.RequestDeadline,
                wait.WaitToken,
                timeoutEvidenceScope: new WorkerTimeoutEvidenceScope(
                    WorkerSchedulingPolicy.InitializationDeadline,
                    wait.WorkerCpuAtStart,
                    wait.InitializationWallStarted)
                , diagnosticWorkId: wait.DiagnosticWorkId,
                diagnosticStageId: (int)SvgWorkerStageId.FontCatalogHelloRoundTrip,
                diagnosticDocumentId: wait.DiagnosticDocumentId,
                diagnosticSourceBytes: wait.DiagnosticSourceBytes,
                diagnosticTargetPixels: wait.DiagnosticTargetPixels
                ).ConfigureAwait(false);
        }
        catch (Exception exception) when (WorkerResponseValidation.IsWorkerTransportFailure(exception))
        {
            _scheduler.Invalidate(wait.CandidateLease!.Slot, worker);
            bool initialCatalog = worker.ReadyFontGeneration < 0;
            if (initialCatalog)
                _scheduler.RecordStartupFailure();
            if (wait.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(wait.CancellationToken);
            WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(wait.InitializationPhase);
            throw new WorkerInitializationDeadlineException(
                $"The resvg worker could not confirm font catalog generation {wait.RequestGeneration} (phase: {wait.InitializationPhase}).",
                exception);
        }
    }

    /// <summary>
    /// Validates a HELLO response and admits the caller when the worker
    /// reports its catalog ready. Returns <see langword="null"/> while the
    /// worker is still scanning.
    /// </summary>
    private TextWorkerAdmission? TryAdmitProbedWorker(
        WorkerFontReadinessWait wait,
        WindowsWorkerProcess worker,
        WorkerResponse response)
    {
        ThrowIfAbandoned(wait, "The SVG font environment changed while its catalog probe was active.");
        try
        {
            WorkerResponseValidation.ValidateFontCatalogProbeResponse(wait.RequestGeneration, response);
        }
        catch (WorkerProtocolException exception)
        {
            throw InvalidateMalformedProbe(wait, worker, exception);
        }

        if (response.Status == WorkerStatus.FontCatalogFailure)
        {
            if (wait.RequestGeneration != _requests.FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed while its catalog probe was active.");
            throw new WorkerInitializationException(
                "The resvg worker could not refresh its Windows font catalog.",
                new WorkerProtocolException(response.Detail.Length == 0
                    ? "The font catalog refresh failed."
                    : response.Detail));
        }

        if (response.Status != WorkerStatus.Ok)
            return null;

        worker.MarkFontCatalogReady(response.ReadyFontGeneration);
        WorkerFontCatalogDiagnostics.RecordFontCatalogInitializationPhases(
            response.Detail,
            wait.DiagnosticWorkId,
            wait.DiagnosticRequestId,
            wait.DiagnosticDocumentId,
            worker.InstanceId);
        wait.ReadinessGate.Release();
        wait.GateEntered = false;
        _scheduler.RebindLeaseCancellation(wait.CandidateLease!, wait.CancellationToken, wait.WaitToken);
        wait.Transferred = true;
        return new TextWorkerAdmission(wait.CandidateLease!, worker);
    }

    /// <summary>
    /// Builds the exception for a malformed HELLO response. It lives apart from
    /// the probe so its callbacks allocate only when a response is malformed.
    /// </summary>
    private WorkerInitializationException InvalidateMalformedProbe(
        WorkerFontReadinessWait wait,
        WindowsWorkerProcess worker,
        WorkerProtocolException exception) =>
        WorkerResponseValidation.InvalidateMalformedFontCatalogProbe(
            exception,
            () =>
            {
                wait.CancellationToken.ThrowIfCancellationRequested();
                wait.WaitToken.ThrowIfCancellationRequested();
                if (wait.RequestGeneration != _requests.FontGeneration)
                {
                    throw new OperationCanceledException(
                        "The SVG font environment changed while its catalog probe was being validated.");
                }

                if (wait.LastProbeWasInitialCatalog)
                    _scheduler.RecordStartupFailure();
            },
            () => _scheduler.Invalidate(wait.CandidateLease!.Slot, worker));

    /// <summary>
    /// Records the reported scanning phase, relinquishes the worker lease while
    /// native scanning continues, and waits briefly before the next probe.
    /// </summary>
    private async Task ParkUntilNextProbeAsync(WorkerFontReadinessWait wait, WorkerResponse response)
    {
        wait.InitializationPhase = response.Detail switch
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
        ParkCandidate(wait);
        TimeSpan remaining = WorkerSchedulingPolicy.InitializationDeadline - wait.Elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return;
        await Task.Delay(
            TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)),
            wait.WaitToken).ConfigureAwait(false);
    }

    /// <summary>Trades the held lease for a detached font-wait reservation.</summary>
    private void ParkCandidate(WorkerFontReadinessWait wait)
    {
        wait.Reservation = _scheduler.ReserveDetachedFontWait(wait.CandidateLease!);
        wait.CandidateLease!.Dispose();
        wait.CandidateLease = null;
    }

    /// <summary>Throws when the caller canceled, the wait expired, or the font generation rotated.</summary>
    private void ThrowIfAbandoned(WorkerFontReadinessWait wait, string generationChangedMessage)
    {
        wait.CancellationToken.ThrowIfCancellationRequested();
        wait.WaitToken.ThrowIfCancellationRequested();
        if (wait.RequestGeneration != _requests.FontGeneration)
            throw new OperationCanceledException(generationChangedMessage);
    }

    /// <summary>
    /// Records deadline evidence for an initial-catalog probe and builds the
    /// readiness-deadline exception.
    /// </summary>
    private static WorkerInitializationDeadlineException CreateReadinessDeadlineException(WorkerFontReadinessWait wait)
    {
        if (wait.LastProbeWorker is not null && wait.LastProbeWasInitialCatalog)
            WorkerFontCatalogDiagnostics.RecordFontCatalogWaitDeadline(
                wait.LastProbeWorker,
                wait.WorkerCpuAtStart,
                wait.Elapsed.Elapsed,
                wait.InitializationPhase);
        return new WorkerInitializationDeadlineException(
            $"The resvg worker did not publish font catalog generation {wait.RequestGeneration} before the initialization deadline (phase: {wait.InitializationPhase}).",
            new TimeoutException());
    }
}
