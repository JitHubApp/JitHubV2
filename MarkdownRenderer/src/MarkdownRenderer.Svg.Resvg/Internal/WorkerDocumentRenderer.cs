using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Renders an opened document into a per-request output section: admission,
/// text readiness, the raster transaction, and one reattachment when the
/// worker that serves the render no longer holds the document.
/// </summary>
internal sealed class WorkerDocumentRenderer(
    WorkerScheduler scheduler,
    WorkerTextAdmission textAdmission,
    WorkerDocumentBindings bindings,
    WorkerRequestFactory requests,
    ResvgMarkdownSvgRendererOptions options,
    Func<string, SvgSourceBuffer, SharedMemoryLease>? sourceMemoryFactory,
    Func<string, int, SharedMemoryLease>? outputMemoryFactory)
{
    private readonly WorkerScheduler _scheduler = scheduler;
    private readonly WorkerTextAdmission _textAdmission = textAdmission;
    private readonly WorkerDocumentBindings _bindings = bindings;
    private readonly WorkerRequestFactory _requests = requests;
    private readonly ResvgMarkdownSvgRendererOptions _options = options;
    private readonly Func<string, SvgSourceBuffer, SharedMemoryLease>? _sourceMemoryFactory = sourceMemoryFactory;
    private readonly Func<string, int, SharedMemoryLease>? _outputMemoryFactory = outputMemoryFactory;

    internal Task<WorkerRenderResult> RenderDocumentAsync(
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        MarkdownSvgRenderRequest renderRequest,
        int outputLength,
        CancellationToken cancellationToken,
        MarkdownSvgRenderDemand? demand = null)
    {
        ulong requestId = _requests.NextRequestId();
        long requestGeneration = _requests.FontGeneration;
        long targetPixels = (long)renderRequest.TargetWidthPixels * renderRequest.TargetHeightPixels;
        long diagnosticWorkId = SvgWorkerStageTrace.BeginWork();
        long queueStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
        return WorkerSharedMemoryMapping.WithAdmittedOutputMappingAsync(
            token => AdmitRenderAsync(
                renderRequest,
                preflight,
                document,
                requestId,
                requestGeneration,
                demand,
                targetPixels,
                diagnosticWorkId,
                queueStartedAt,
                token),
            (admission, outputMemory, token) => RenderDocumentAdmittedAsync(
                admission.Lease,
                admission.Worker,
                document,
                preflight,
                openRequest,
                renderRequest,
                outputMemory,
                requestId,
                admission.RequestGeneration,
                token
                , diagnosticWorkId, targetPixels
                ).AsTask(),
            _outputMemoryFactory,
            WorkerSharedMemoryMapping.NewOutputMappingName(),
            outputLength,
            cancellationToken,
            _outputMemoryFactory is null
                ? static (RenderAdmission admission) => admission.Worker.ObjectSecurity
                : null);
    }

    private async Task<RenderAdmission> AdmitRenderAsync(
        MarkdownSvgRenderRequest renderRequest,
        SvgPreflightResult preflight,
        WorkerDocumentHandle document,
        ulong requestId,
        long requestGeneration,
        MarkdownSvgRenderDemand? demand,
        long targetPixels,
        long diagnosticWorkId,
        long queueStartedAt,
        CancellationToken cancellationToken)
    {
        WorkerLease lease = null!;
        bool transferred = false;
        try
        {
            // Include queue admission in the cleanup region: cancellation or
            // queue rejection here must release the source-bearing arguments
            // just like failures after a worker lease has been acquired.
            lease = await _scheduler.AcquireAsync(
                WorkerOperation.Render,
                demand?.Priority ?? renderRequest.Priority,
                cancellationToken,
                requiresFontCatalog: preflight.Info.HasText,
                preferredWorkerInstanceId: document.PreferredWorkerInstanceId,
                demandTicket: demand).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateContentAdmission();
            SvgWorkerStageTrace.Record(
                diagnosticWorkId,
                SvgWorkerStageId.PoolSchedulingQueueWait,
                0,
                requestId,
                document.DocumentId,
                queueStartedAt,
                sourceBytes: preflight.Source.Length,
                targetPixels: targetPixels);
            cancellationToken.ThrowIfCancellationRequested();
            WindowsWorkerProcess worker = await _scheduler.EnsureWorkerAsync(
                lease
                ,
                diagnosticWorkId,
                requestId,
                document.DocumentId,
                preflight.Source.Length,
                targetPixels).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Info.HasText)
            {
                TextWorkerAdmission textAdmission = await _textAdmission.PrepareTextWorkerAdmissionAsync(
                    lease,
                    worker,
                    WorkerOperation.Render,
                    demand?.Priority ?? renderRequest.Priority,
                    requestGeneration,
                    cancellationToken,
                    demand
                    ,
                    diagnosticWorkId,
                    requestId,
                    document.DocumentId,
                    preflight.Source.Length,
                    targetPixels
                    ).ConfigureAwait(false);
                lease = textAdmission.Lease;
                worker = textAdmission.Worker;
            }
            else if (requestGeneration != _requests.FontGeneration)
            {
                // Geometry does not depend on the font catalog. It can adopt
                // the latest generation without waiting for any font work.
                requestGeneration = _requests.FontGeneration;
            }
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateContentAdmission();

            var admission = new RenderAdmission(lease, worker, requestGeneration);
            transferred = true;
            return admission;
        }
        finally
        {
            try
            {
                if (!transferred)
                    lease?.Dispose();
            }
            finally
            {
                preflight = null!;
                renderRequest = null!;
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<WorkerResponse> RenderDocumentAdmittedAsync(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        MarkdownSvgRenderRequest renderRequest,
        SharedMemoryLease outputMemory,
        ulong requestId,
        long requestGeneration,
        CancellationToken cancellationToken
        , long diagnosticWorkId,
        long targetPixels
        )
    {
        try
        {
            using WorkerDocumentStateGate.Lease documentStateLease =
                await worker.DocumentStateGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateContentAdmission();

            if (preflight.Info.HasText && requestGeneration != _requests.FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before rendering.");
            WorkerRequest request = new(
                WorkerOperation.Render,
                requestId,
                worker.NonceLow,
                worker.NonceHigh,
                0,
                preflight.Hash,
                0,
                outputMemory.Memory.Length,
                renderRequest.TargetWidthPixels,
                renderRequest.TargetHeightPixels,
                renderRequest.TileRegion,
                WorkerRequestFactory.NormalizeLocale(openRequest.Locale),
                openRequest.ColorScheme,
                openRequest.SemanticColor,
                renderRequest.PixelFormat,
                requestGeneration,
                _requests.PerWorkerParsedResourceCacheBytes,
                document.DocumentId,
                _options,
                renderRequest.PhysicalViewport);
            try
            {
                // A document can be opened on one worker and rendered on the other.
                // The missing-document probe, the authoritative attach/parse, and
                // the raster operation are separate bounded worker transactions.
                // Charging all three to one shared stopwatch makes an otherwise
                // valid SVG fail nondeterministically during visible image bursts.
                WorkerResponse response = await worker.ExchangeAsync(
                    request,
                    _options.RequestDeadline,
                    CancellationToken.None,
                    outputMemory
                    ,
                    diagnosticWorkId: diagnosticWorkId,
                    diagnosticStageId: (int)SvgWorkerStageId.RenderTransaction,
                    diagnosticSourceBytes: preflight.Source.Length,
                    diagnosticTargetPixels: targetPixels
                    ).ConfigureAwait(false);
                ValidateRenderResponseBeforeCancellation(
                    response,
                    worker,
                    lease,
                    document,
                    preflight,
                    renderRequest,
                    requestGeneration,
                    outputMemory,
                    cancellationToken);
                WorkerResponseValidation.ValidateRenderStatus(
                    response,
                    requiresFontCatalog: preflight.Info.HasText,
                    requestGeneration: requestGeneration,
                    documentMissingAllowed: true,
                    operation: "render");
                cancellationToken.ThrowIfCancellationRequested();
                if (preflight.Info.HasText)
                    WorkerResponseValidation.ValidateTextResponse(response, requestGeneration, _requests.FontGeneration, "render");
                WorkerResponse? attachment = await WorkerSharedMemoryMapping.AttachDocumentIfMissingAsync(
                    response,
                    () =>
                    {
                        document.ValidateContentAdmission();
                        return WorkerSharedMemoryMapping.WithSourceMappingAsync(
                            _sourceMemoryFactory,
                            WorkerSharedMemoryMapping.NewSourceMappingName(),
                            preflight.Source,
                            sourceMemory => _bindings.AttachDocumentAsync(
                                lease,
                                worker,
                                document.DocumentId,
                                preflight,
                                openRequest,
                                sourceMemory,
                                _options.RequestDeadline,
                                cancellationToken,
                                _requests.NextRequestId(),
                                requestGeneration,
                                null,
                                document
                                ,
                                diagnosticWorkId,
                                (int)SvgWorkerStageId.DocumentMissingAttach
                                ).AsTask(),
                            cancellationToken,
                            document.ValidateContentAdmission,
                            _sourceMemoryFactory is null ? worker.ObjectSecurity : null);
                    }).ConfigureAwait(false);
                if (attachment is null)
                    return WorkerResponseValidation.Stamp(response, worker);
                if (preflight.Info.HasText)
                    WorkerResponseValidation.ValidateTextResponse(attachment.Value, requestGeneration, _requests.FontGeneration, "document reattachment");
                if (attachment.Value.Status != WorkerStatus.Ok)
                    return attachment.Value;
                document.PreferWorker(attachment.Value.WorkerInstanceId);
                cancellationToken.ThrowIfCancellationRequested();
                document.ValidateContentAdmission();

                request = request with { RequestId = _requests.NextRequestId() };
                response = await worker.ExchangeAsync(
                    request,
                    _options.RequestDeadline,
                    CancellationToken.None,
                    outputMemory
                    ,
                    diagnosticWorkId: diagnosticWorkId,
                    diagnosticStageId: (int)SvgWorkerStageId.RenderTransaction,
                    diagnosticSourceBytes: preflight.Source.Length,
                    diagnosticTargetPixels: targetPixels
                    ).ConfigureAwait(false);
                ValidateRenderResponseBeforeCancellation(
                    response,
                    worker,
                    lease,
                    document,
                    preflight,
                    renderRequest,
                    requestGeneration,
                    outputMemory,
                    cancellationToken);
                WorkerResponseValidation.ValidateRenderStatus(
                    response,
                    requiresFontCatalog: preflight.Info.HasText,
                    requestGeneration: requestGeneration,
                    documentMissingAllowed: false,
                    operation: "render after reattachment");
                cancellationToken.ThrowIfCancellationRequested();
                if (preflight.Info.HasText)
                    WorkerResponseValidation.ValidateTextResponse(response, requestGeneration, _requests.FontGeneration, "render after reattachment");
                return WorkerResponseValidation.Stamp(response, worker);
            }
            catch (Exception exception) when (WorkerResponseValidation.IsWorkerTransportFailure(exception))
            {
                bool callerCanceled = cancellationToken.IsCancellationRequested;
                long currentGeneration = _requests.FontGeneration;
                bool generationCurrent = !preflight.Info.HasText || requestGeneration == currentGeneration;
                WorkerResponseValidation.QuarantineContentBeforeWorkerRelease(
                    document,
                    contentFailureCallback: null,
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
            // The worker transaction and any missing-document reattach have
            // drained before these borrowed source inputs are cleared.
            preflight = null!;
            openRequest = null!;
            renderRequest = null!;
            outputMemory = null!;
        }
    }

    private void ValidateRenderResponseBeforeCancellation(
        WorkerResponse response,
        WindowsWorkerProcess worker,
        WorkerLease lease,
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgRenderRequest renderRequest,
        long requestGeneration,
        SharedMemoryLease outputMemory,
        CancellationToken cancellationToken)
    {
        if (response.Status == WorkerStatus.Ok)
        {
            WorkerResponseValidation.ValidateRasterResponse(
                response,
                renderRequest,
                outputMemory.Memory.Length);
        }
        else if (response.Status == WorkerStatus.WorkerFailure)
        {
            WorkerResponseValidation.QuarantineContentBeforeWorkerRelease(
                document,
                contentFailureCallback: null,
                callerCanceled: cancellationToken.IsCancellationRequested,
                requiresFontCatalog: preflight.Info.HasText,
                requestGeneration: requestGeneration,
                currentGeneration: _requests.FontGeneration,
                invalidateWorker: () => _scheduler.Invalidate(lease.Slot, worker));
        }
    }
}
