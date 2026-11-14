using System.Runtime.CompilerServices;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers;

/// <summary>
/// Opens a preflighted SVG on a worker: admission, text readiness when the
/// document has text, and the source-mapped attachment that returns the
/// document handle.
/// </summary>
internal sealed class WorkerDocumentOpener(
    WorkerScheduler scheduler,
    WorkerTextAdmission textAdmission,
    WorkerDocumentBindings bindings,
    WorkerRequestFactory requests,
    ResvgMarkdownSvgRendererOptions options,
    Func<string, SvgSourceBuffer, SharedMemoryLease>? sourceMemoryFactory)
{
    private readonly WorkerScheduler _scheduler = scheduler;
    private readonly WorkerTextAdmission _textAdmission = textAdmission;
    private readonly WorkerDocumentBindings _bindings = bindings;
    private readonly WorkerRequestFactory _requests = requests;
    private readonly ResvgMarkdownSvgRendererOptions _options = options;
    private readonly Func<string, SvgSourceBuffer, SharedMemoryLease>? _sourceMemoryFactory = sourceMemoryFactory;
    private long _nextDocumentId;

    // This internal operation is consumed once by the renderer. Reuse only
    // the runtime-owned async frame, never the document/source lifetime.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<WorkerOpenResult> OpenDocumentAsync(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        CancellationToken cancellationToken,
        Action? contentAdmissionGuard = null,
        Action? contentFailureCallback = null,
        MarkdownSvgRenderDemand? demand = null)
    {
        WorkerLease? lease = null;
        using var openFailureMarker = new WorkerContentFailureMarker(contentFailureCallback);
        Action? openContentFailureCallback = contentFailureCallback is null
            ? null
            : openFailureMarker.InvokeOnce;
        long requestGeneration = _requests.FontGeneration;
        try
        {
            ulong documentId = NextDocumentId();
            ulong requestId = _requests.NextRequestId();
            long diagnosticWorkId = SvgWorkerStageTrace.BeginWork();
            long queueStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
            (lease, WindowsWorkerProcess worker) = await AdmitOpenAsync(
                preflight,
                openRequest,
                demand,
                contentAdmissionGuard,
                documentId,
                requestId,
                requestGeneration,
                diagnosticWorkId,
                queueStartedAt,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Info.HasText && requestGeneration != _requests.FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before the document was attaching.");
            contentAdmissionGuard?.Invoke();
            WorkerResponse response = await WorkerSharedMemoryMapping.WithSourceMappingAsync(
                _sourceMemoryFactory,
                WorkerSharedMemoryMapping.NewSourceMappingName(),
                preflight.Source,
                CreateOpenAttachment(
                    lease,
                    worker,
                    documentId,
                    preflight,
                    openRequest,
                    requestId,
                    requestGeneration,
                    openContentFailureCallback,
                    diagnosticWorkId,
                    cancellationToken),
                cancellationToken,
                contentAdmissionGuard,
                _sourceMemoryFactory is null ? worker.ObjectSecurity : null).ConfigureAwait(false);
            if (preflight.Info.HasText)
                await ValidateOpenedTextAsync(response, worker, documentId, requestGeneration).ConfigureAwait(false);
            var document = new WorkerDocumentHandle(
                documentId,
                response.Status == WorkerStatus.Ok ? response.WorkerInstanceId : 0);
            return new WorkerOpenResult(
                response,
                document);
        }
        finally
        {
            lease?.Dispose();
            // The OPEN transaction has drained before this borrowed callback
            // is cleared. A renderer-owned wrapper receives its own guard.
            contentAdmissionGuard = null;
            contentFailureCallback = null;
            openContentFailureCallback = null;
            // These values are borrowed from the document/source owner. The
            // transaction above has drained before this point; do not let the
            // completed/faulted open Task keep those source-bearing objects.
            preflight = null!;
            openRequest = null!;
        }
    }

    /// <summary>
    /// Acquires an OPEN slot, starts its worker and, for a document with text,
    /// waits for the worker to publish the caller's font generation. A lease
    /// this step does not hand back is released before it returns or throws.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<(WorkerLease Lease, WindowsWorkerProcess Worker)> AdmitOpenAsync(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        MarkdownSvgRenderDemand? demand,
        Action? contentAdmissionGuard,
        ulong documentId,
        ulong requestId,
        long requestGeneration,
        long diagnosticWorkId,
        long queueStartedAt,
        CancellationToken cancellationToken)
    {
        WorkerLease? lease = null;
        bool transferred = false;
        try
        {
            lease = await _scheduler.AcquireAsync(
                WorkerOperation.Open,
                demand?.Priority ?? openRequest.Priority,
                cancellationToken,
                requiresFontCatalog: preflight.Info.HasText,
                demandTicket: demand).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard?.Invoke();
            SvgWorkerStageTrace.Record(
                diagnosticWorkId,
                SvgWorkerStageId.PoolSchedulingQueueWait,
                0,
                requestId,
                documentId,
                queueStartedAt,
                sourceBytes: preflight.Source.Length);
            WindowsWorkerProcess worker = await _scheduler.EnsureWorkerAsync(
                lease
                ,
                diagnosticWorkId,
                requestId,
                documentId,
                preflight.Source.Length,
                0
                ).ConfigureAwait(false);
            if (preflight.Info.HasText)
            {
                TextWorkerAdmission textAdmission = await _textAdmission.PrepareTextWorkerAdmissionAsync(
                    lease,
                    worker,
                    WorkerOperation.Open,
                    demand?.Priority ?? openRequest.Priority,
                    requestGeneration,
                    cancellationToken,
                    demand
                    ,
                    diagnosticWorkId,
                    requestId,
                    documentId,
                    preflight.Source.Length,
                    0
                    ).ConfigureAwait(false);
                lease = textAdmission.Lease;
                worker = textAdmission.Worker;
            }

            transferred = true;
            return (lease, worker);
        }
        finally
        {
            if (!transferred)
                lease?.Dispose();
            preflight = null!;
            openRequest = null!;
        }
    }

    /// <summary>
    /// Creates the OPEN transaction that runs once the preflight source is
    /// mapped into the admitted worker.
    /// </summary>
    private Func<SharedMemoryLease, Task<WorkerResponse>> CreateOpenAttachment(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        ulong documentId,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        ulong requestId,
        long requestGeneration,
        Action? openContentFailureCallback,
        long diagnosticWorkId,
        CancellationToken cancellationToken) =>
        sourceMemory =>
        {
            return _bindings.AttachDocumentAsync(
                lease,
                worker,
                documentId,
                preflight,
                openRequest,
                sourceMemory,
                _options.RequestDeadline,
                cancellationToken,
                requestId,
                requestGeneration,
                openContentFailureCallback,
                null
                ,
                diagnosticWorkId,
                (int)SvgWorkerStageId.OpenTransaction
                ).AsTask();
        };

    /// <summary>
    /// Rejects a text document opened under a superseded font generation and
    /// forgets a document the worker already holds before rethrowing.
    /// </summary>
    private async ValueTask ValidateOpenedTextAsync(
        WorkerResponse response,
        WindowsWorkerProcess worker,
        ulong documentId,
        long requestGeneration)
    {
        try
        {
            WorkerResponseValidation.ValidateTextResponse(response, requestGeneration, _requests.FontGeneration, "document open");
        }
        catch (OperationCanceledException) when (response.Status == WorkerStatus.Ok)
        {
            await _bindings.ForgetDocumentAsync(worker, documentId).ConfigureAwait(false);
            throw;
        }
    }

    private ulong NextDocumentId() => checked((ulong)Interlocked.Increment(ref _nextDocumentId));
}
