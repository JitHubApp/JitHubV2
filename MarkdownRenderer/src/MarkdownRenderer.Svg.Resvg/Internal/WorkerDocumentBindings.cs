using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Owns the worker-side document bindings: the authoritative OPEN that
/// attaches a document's source, the best-effort CLOSE that forgets it on every
/// live worker, and the parsed-resource cache trim.
/// </summary>
internal sealed class WorkerDocumentBindings(
    WorkerScheduler scheduler,
    WorkerRequestFactory requests,
    ResvgMarkdownSvgRendererOptions options)
{
    private readonly WorkerScheduler _scheduler = scheduler;
    private readonly WorkerRequestFactory _requests = requests;
    private readonly ResvgMarkdownSvgRendererOptions _options = options;
    private long _sourceAttachmentCount;

    internal long SourceAttachmentCount => Volatile.Read(ref _sourceAttachmentCount);

    internal void ForgetDocument(WorkerDocumentHandle document)
    {
        if (_scheduler.IsDisposed)
            return;
        WindowsWorkerProcess[] workers = _scheduler.GetLiveWorkers();
        foreach (WindowsWorkerProcess worker in workers)
            ObserveDetachedTaskFault(ForgetDocumentAsync(worker, document.DocumentId));
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<WorkerResponse> AttachDocumentAsync(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        ulong documentId,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        SharedMemoryLease sourceMemory,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        ulong requestId,
        long requestGeneration,
        Action? contentFailureCallback,
        WorkerDocumentHandle? contentDocument
        , long diagnosticWorkId,
        int diagnosticStageId
        )
    {
        try
        {
            WorkerFontCatalogGeneration.ValidateRequest(requestGeneration);
            if (preflight.Info.HasText && requestGeneration != _requests.FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before document attachment.");
            WorkerRequest request = new(
                WorkerOperation.Open,
                requestId,
                worker.NonceLow,
                worker.NonceHigh,
                0,
                preflight.Hash,
                preflight.Source.Length,
                0,
                0,
                0,
                null,
                WorkerRequestFactory.NormalizeLocale(openRequest.Locale),
                openRequest.ColorScheme,
                openRequest.SemanticColor,
                MarkdownSvgPixelFormat.Rgba8Premultiplied,
                requestGeneration,
                _requests.PerWorkerParsedResourceCacheBytes,
                documentId,
                _options);
            try
            {
                WorkerResponse response = await worker.ExchangeAsync(
                    request,
                    deadline,
                    CancellationToken.None,
                    sourceMemory
                    ,
                    diagnosticWorkId: diagnosticWorkId,
                    diagnosticStageId: diagnosticStageId,
                    diagnosticSourceBytes: preflight.Source.Length
                    ).ConfigureAwait(false);
                if (response.Status == WorkerStatus.WorkerFailure)
                {
                    WorkerResponseValidation.QuarantineContentBeforeWorkerRelease(
                        contentDocument,
                        contentFailureCallback,
                        callerCanceled: cancellationToken.IsCancellationRequested,
                        requiresFontCatalog: preflight.Info.HasText,
                        requestGeneration: requestGeneration,
                        currentGeneration: _requests.FontGeneration,
                        invalidateWorker: () => _scheduler.Invalidate(lease.Slot, worker));
                }
                if (response.Status == WorkerStatus.Ok)
                    WorkerResponseValidation.ValidateOpenResponse(response);
                try
                {
                    WorkerResponseValidation.ValidateOpenStatus(
                        response,
                        requiresFontCatalog: preflight.Info.HasText,
                        requestGeneration: requestGeneration,
                        operation: "document attachment");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (preflight.Info.HasText)
                    {
                        WorkerResponseValidation.ValidateTextResponse(
                            response,
                            requestGeneration,
                            _requests.FontGeneration,
                            "document attachment");
                    }
                }
                catch (OperationCanceledException) when (response.Status == WorkerStatus.Ok)
                {
                    // The worker committed the OPEN before the caller's cancellation
                    // or font-generation change became observable. Do not strand a
                    // live document binding when OpenDocumentAsync cannot return a
                    // handle that would later send CLOSE.
                    await ForgetDocumentAsync(worker, documentId).ConfigureAwait(false);
                    throw;
                }
                if (response.Status == WorkerStatus.Ok)
                    Interlocked.Increment(ref _sourceAttachmentCount);
                return WorkerResponseValidation.Stamp(response, worker);
            }
            catch (Exception exception) when (WorkerResponseValidation.IsWorkerTransportFailure(exception))
            {
                bool callerCanceled = cancellationToken.IsCancellationRequested;
                long currentGeneration = _requests.FontGeneration;
                bool generationCurrent = !preflight.Info.HasText || requestGeneration == currentGeneration;
                WorkerResponseValidation.QuarantineContentBeforeWorkerRelease(
                    contentDocument,
                    contentFailureCallback,
                    callerCanceled: callerCanceled,
                    requiresFontCatalog: preflight.Info.HasText,
                    requestGeneration: requestGeneration,
                    currentGeneration: currentGeneration,
                    invalidateWorker: () => _scheduler.Invalidate(lease.Slot, worker));
                if (callerCanceled)
                    throw new OperationCanceledException(cancellationToken);
                if (preflight.Info.HasText && !generationCurrent)
                    throw new OperationCanceledException("The SVG worker was intentionally rotated after a font change.");
                throw;
            }
        }
        finally
        {
            // The owning mapping helper disposes sourceMemory after this
            // transaction returns; clearing this borrowed reference neither
            // shortens nor extends that mapping lifetime.
            preflight = null!;
            openRequest = null!;
            sourceMemory = null!;
            contentFailureCallback = null;
            contentDocument = null;
        }
    }

    internal Task ForgetDocumentAsync(WindowsWorkerProcess worker, ulong documentId) =>
        WorkerResponseValidation.CloseDocumentOrInvalidateAsync(
            () => worker.ExchangeAsync(
                _requests.CreateControlRequest(WorkerOperation.CloseDocument, worker, documentId),
                _options.RequestDeadline,
                CancellationToken.None).AsTask(),
            () => _scheduler.Invalidate(worker));

    private static void ObserveDetachedTaskFault(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    internal async Task TrimCacheAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_scheduler.IsDisposed, typeof(WorkerPool));
        cancellationToken.ThrowIfCancellationRequested();
        WindowsWorkerProcess[] workers = _scheduler.GetLiveWorkers();

        foreach (WindowsWorkerProcess worker in workers)
        {
            try
            {
                using WorkerDocumentStateGate.Lease documentStateLease =
                    await worker.DocumentStateGate.EnterAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                WorkerResponse response = await worker.ExchangeAsync(
                    _requests.CreateControlRequest(WorkerOperation.TrimCache, worker),
                    _options.RequestDeadline,
                    CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (response.Status != WorkerStatus.Ok || response.OutputLength != 0)
                    throw new WorkerProtocolException("The resvg worker rejected a cache trim request.");
            }
            catch (Exception exception) when (WorkerResponseValidation.IsWorkerTransportFailure(exception))
            {
                _scheduler.Invalidate(worker);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw;
            }
        }
    }
}
