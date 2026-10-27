namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed partial class WindowsWorkerProcess
{
    private readonly object _fontPreparationGate = new();
    private readonly CancellationTokenSource _fontPreparationShutdown = new();
    private Task? _fontPreparationTask;
    private long _fontPreparationGeneration = -1;

    // The task owns only approved installed-font file mappings. Font parsing
    // stays in the worker. Geometry requests never wait for this preparation.
    internal Task EnsureFontCatalogSuppliedAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        WorkerFontCatalogGeneration.ValidateRequest(request.FontGeneration);
        Task preparation;
        lock (_fontPreparationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (request.FontGeneration < _fontPreparationGeneration)
                throw new OperationCanceledException("The SVG font catalog request has been superseded.");
            if (request.FontGeneration != _fontPreparationGeneration)
            {
                Task? previous = _fontPreparationTask;
                CancellationToken shutdown = _fontPreparationShutdown.Token;
                preparation = Task.Run(() => SupplyFontCatalogAsync(previous, request, shutdown));
                _fontPreparationTask = preparation;
                _fontPreparationGeneration = request.FontGeneration;
                // Eager preparation must not produce an unobserved failure if
                // no text is ever requested. Text callers still await the exact
                // cached task and receive its failure; no automatic replay.
                _ = preparation.ContinueWith(static completed => { _ = completed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            preparation = _fontPreparationTask!;
        }
        return cancellationToken.CanBeCanceled ? preparation.WaitAsync(cancellationToken) : preparation;
    }

    private async Task SupplyFontCatalogAsync(Task? previous, WorkerRequest request, CancellationToken shutdown)
    {
        if (previous is not null)
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { /* A newer font generation may recover an environmental failure. */ }
        }
        shutdown.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(WorkerSchedulingPolicy.InitializationDeadline);
        InstalledFontCatalogTransfer? transfer = null;
        bool exchangeStarted = false;
        bool acknowledged = false;
        try
        {
            transfer = InstalledFontCatalogTransfer.Create(
                _process,
                ObjectSecurity,
                request.FontGeneration,
                deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            request = request with
            {
                Operation = WorkerOperation.FontCatalog,
                SourceLength = transfer.CatalogLength,
            };
            while (true)
            {
                exchangeStarted = true;
                acknowledged = false;
                WorkerResponse response = await ExchangeAsync(request, request.Options.RequestDeadline,
                    deadline.Token, transfer.CatalogMemory).ConfigureAwait(false);
                ValidateFontCatalogMappingAcknowledgement(response);
                if (response.ReadyFontGeneration > request.FontGeneration)
                    throw new WorkerProtocolException("The SVG worker acknowledged a font catalog newer than the host generation.");
                acknowledged = true;
                if (response.Status == WorkerStatus.FontCatalogPending)
                {
                    // Busy means this table has not been mapped or accepted.
                    // Keep the same bounded, read-only section handles until
                    // the previous replacement finishes; do not enumerate or
                    // open the font files again on every readiness poll. The
                    // transport gate is free for unrelated geometry requests.
                    // Hello also publishes/discards a completed predecessor;
                    // merely resending the table would leave its completion
                    // parked forever and this generation permanently busy.
                    acknowledged = false;
                    WorkerResponse probe = await ExchangeAsync(request with
                    {
                        Operation = WorkerOperation.Hello,
                        SourceLength = 0,
                    }, request.Options.RequestDeadline, deadline.Token).ConfigureAwait(false);
                    WorkerPool.ValidateFontCatalogProbeResponse(request.FontGeneration, probe);
                    acknowledged = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (response.Status != WorkerStatus.Ok)
                    throw new WorkerInitializationException("The isolated SVG worker could not accept the installed font catalog.",
                        new WorkerProtocolException(response.Detail));
                break;
            }
        }
        catch (Exception exception)
        {
            // A partial/invalid reply does not prove the worker finished mapping
            // the borrowed handles. Retire it before the host closes them.
            if (exchangeStarted && !acknowledged)
                DisposeForRestart();
            if (shutdown.IsCancellationRequested && exception is OperationCanceledException)
                throw new OperationCanceledException(shutdown);
            throw new WorkerInitializationException("The installed SVG font catalog could not be prepared.", exception);
        }
        finally
        {
            transfer?.Dispose();
            transfer = null;
            previous = null;
        }
    }

    internal static void ValidateFontCatalogMappingAcknowledgement(WorkerResponse response)
    {
        if (response.Status is not (WorkerStatus.Ok or WorkerStatus.FontCatalogFailure or
                WorkerStatus.ResourceLimit or WorkerStatus.FontCatalogPending) ||
            (response.Status == WorkerStatus.FontCatalogPending && response.Detail != "font-catalog-busy") ||
            response.OutputLength != 0 || response.Stride != 0 || response.Width != 0 || response.Height != 0 ||
            response.IntrinsicWidth != 0 || response.IntrinsicHeight != 0 || response.AspectRatio != 0 ||
            response.HasText || response.UsesCurrentColor || response.UsesColorScheme)
            throw new WorkerProtocolException("The SVG worker returned a malformed font-catalog acknowledgement.");
    }

    private void RetireFontCatalogPreparation()
    {
        Task? preparation;
        lock (_fontPreparationGate)
            preparation = _fontPreparationTask;
        // _disposed was set before this method; no later preparation can start.
        _fontPreparationShutdown.Cancel();
        if (preparation is null)
            _fontPreparationShutdown.Dispose();
        else
            _ = preparation.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _fontPreparationShutdown, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async ValueTask DrainFontCatalogPreparationAsync()
    {
        Task? preparation;
        lock (_fontPreparationGate)
            preparation = _fontPreparationTask;
        if (preparation is not null)
        {
            // The preparation task owns borrowed handles until either a valid
            // acknowledgement or process retirement. Shutdown cancels it; wait
            // for its finally block, without replacing a prior render failure.
            try { await preparation.ConfigureAwait(false); }
            catch (Exception) { }
        }
    }
}
