using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers;

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
            RecordQueueWait(preflight, document, requestId, targetPixels, diagnosticWorkId, queueStartedAt);
            cancellationToken.ThrowIfCancellationRequested();
            WindowsWorkerProcess worker = await _scheduler.EnsureWorkerAsync(
                lease, diagnosticWorkId, requestId, document.DocumentId, preflight.Source.Length, targetPixels)
                .ConfigureAwait(false);
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
                    demand,
                    diagnosticWorkId,
                    requestId,
                    document.DocumentId,
                    preflight.Source.Length,
                    targetPixels).ConfigureAwait(false);
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

    private static void RecordQueueWait(
        SvgPreflightResult preflight,
        WorkerDocumentHandle document,
        ulong requestId,
        long targetPixels,
        long diagnosticWorkId,
        long queueStartedAt) =>
        SvgWorkerStageTrace.Record(
            diagnosticWorkId,
            SvgWorkerStageId.PoolSchedulingQueueWait,
            0,
            requestId,
            document.DocumentId,
            queueStartedAt,
            sourceBytes: preflight.Source.Length,
            targetPixels: targetPixels);

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
        CancellationToken cancellationToken,
        long diagnosticWorkId,
        long targetPixels)
    {
        var render = new WorkerRenderInputs(
            lease,
            worker,
            document,
            preflight,
            openRequest,
            renderRequest,
            outputMemory,
            requestGeneration,
            diagnosticWorkId,
            targetPixels,
            cancellationToken);
        try
        {
            using WorkerDocumentStateGate.Lease documentStateLease =
                await worker.DocumentStateGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateContentAdmission();

            if (preflight.Info.HasText && requestGeneration != _requests.FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before rendering.");
            WorkerRequest request = CreateRenderRequest(in render, requestId);
            try
            {
                // A document can be opened on one worker and rendered on the other.
                // The missing-document probe, the authoritative attach/parse, and
                // the raster operation are separate bounded worker transactions.
                // Charging all three to one shared stopwatch makes an otherwise
                // valid SVG fail nondeterministically during visible image bursts.
                WorkerResponse response = await ExchangeRender(in render, request).ConfigureAwait(false);
                ValidateRenderResponse(in render, response, documentMissingAllowed: true, operation: "render");
                WorkerResponse? attachment = await WorkerSharedMemoryMapping.AttachDocumentIfMissingAsync(
                    response,
                    CreateDocumentReattachment(render)).ConfigureAwait(false);
                if (attachment is null)
                    return WorkerResponseValidation.Stamp(response, worker);
                if (!TryAcceptReattachment(in render, attachment.Value))
                    return attachment.Value;

                request = request with { RequestId = _requests.NextRequestId() };
                response = await ExchangeRender(in render, request).ConfigureAwait(false);
                ValidateRenderResponse(
                    in render,
                    response,
                    documentMissingAllowed: false,
                    operation: "render after reattachment");
                return WorkerResponseValidation.Stamp(response, worker);
            }
            catch (Exception exception) when (WorkerResponseValidation.IsWorkerTransportFailure(exception))
            {
                ThrowIfTransportFailureIsCancellation(in render);
                throw;
            }
        }
        finally
        {
            // The worker transaction and any missing-document reattach have
            // drained before these borrowed source inputs are cleared.
            render = default;
            preflight = null!;
            openRequest = null!;
            renderRequest = null!;
            outputMemory = null!;
        }
    }

    /// <summary>
    /// Checks a missing-document reattachment before the render is retried:
    /// its font generation for text, its status, then the caller's
    /// cancellation and content admission. Prefers the worker that now holds
    /// the document.
    /// </summary>
    /// <param name="render">The admitted render's inputs.</param>
    /// <param name="attachment">The reattachment response.</param>
    /// <returns><see langword="false"/> when the reattachment failed and its response is the result.</returns>
    private bool TryAcceptReattachment(in WorkerRenderInputs render, WorkerResponse attachment)
    {
        if (render.Preflight.Info.HasText)
            WorkerResponseValidation.ValidateTextResponse(attachment, render.RequestGeneration, _requests.FontGeneration, "document reattachment");
        if (attachment.Status != WorkerStatus.Ok)
            return false;
        render.Document.PreferWorker(attachment.WorkerInstanceId);
        render.CancellationToken.ThrowIfCancellationRequested();
        render.Document.ValidateContentAdmission();
        return true;
    }

    /// <summary>Builds the render transaction for the leased worker.</summary>
    private WorkerRequest CreateRenderRequest(in WorkerRenderInputs render, ulong requestId) =>
        new(
            WorkerOperation.Render,
            requestId,
            render.Worker.NonceLow,
            render.Worker.NonceHigh,
            0,
            render.Preflight.Hash,
            0,
            render.OutputMemory.Memory.Length,
            render.RenderRequest.TargetWidthPixels,
            render.RenderRequest.TargetHeightPixels,
            render.RenderRequest.TileRegion,
            WorkerRequestFactory.NormalizeLocale(render.OpenRequest.Locale),
            render.OpenRequest.ColorScheme,
            render.OpenRequest.SemanticColor,
            render.RenderRequest.PixelFormat,
            render.RequestGeneration,
            _requests.PerWorkerParsedResourceCacheBytes,
            render.Document.DocumentId,
            _options,
            render.RenderRequest.PhysicalViewport);

    /// <summary>
    /// Sends one render transaction under its own request deadline. Caller
    /// cancellation is observed between transactions, never inside one.
    /// </summary>
    private ValueTask<WorkerResponse> ExchangeRender(in WorkerRenderInputs render, WorkerRequest request) =>
        render.Worker.ExchangeAsync(
            request,
            _options.RequestDeadline,
            CancellationToken.None,
            render.OutputMemory
            ,
            diagnosticWorkId: render.DiagnosticWorkId,
            diagnosticStageId: (int)SvgWorkerStageId.RenderTransaction,
            diagnosticSourceBytes: render.Preflight.Source.Length,
            diagnosticTargetPixels: render.TargetPixels
            );

    /// <summary>
    /// Validates a render response in order: raster shape or content
    /// quarantine, then status, then caller cancellation, then font generation.
    /// </summary>
    private void ValidateRenderResponse(
        in WorkerRenderInputs render,
        WorkerResponse response,
        bool documentMissingAllowed,
        string operation)
    {
        ValidateRenderResponseBeforeCancellation(
            response,
            render.Worker,
            render.Lease,
            render.Document,
            render.Preflight,
            render.RenderRequest,
            render.RequestGeneration,
            render.OutputMemory,
            render.CancellationToken);
        WorkerResponseValidation.ValidateRenderStatus(
            response,
            requiresFontCatalog: render.Preflight.Info.HasText,
            requestGeneration: render.RequestGeneration,
            documentMissingAllowed: documentMissingAllowed,
            operation: operation);
        render.CancellationToken.ThrowIfCancellationRequested();
        if (render.Preflight.Info.HasText)
            WorkerResponseValidation.ValidateTextResponse(response, render.RequestGeneration, _requests.FontGeneration, operation);
    }

    /// <summary>
    /// Creates the reattachment that maps the preflight source again and
    /// attaches it to the worker when the render reports the document missing.
    /// The returned callback holds its own copy of the inputs.
    /// </summary>
    private Func<Task<WorkerResponse>> CreateDocumentReattachment(WorkerRenderInputs render) =>
        () =>
        {
            render.Document.ValidateContentAdmission();
            return WorkerSharedMemoryMapping.WithSourceMappingAsync(
                _sourceMemoryFactory,
                WorkerSharedMemoryMapping.NewSourceMappingName(),
                render.Preflight.Source,
                sourceMemory => _bindings.AttachDocumentAsync(
                    render.Lease,
                    render.Worker,
                    render.Document.DocumentId,
                    render.Preflight,
                    render.OpenRequest,
                    sourceMemory,
                    _options.RequestDeadline,
                    render.CancellationToken,
                    _requests.NextRequestId(),
                    render.RequestGeneration,
                    null,
                    render.Document
                    ,
                    render.DiagnosticWorkId,
                    (int)SvgWorkerStageId.DocumentMissingAttach
                    ).AsTask(),
                render.CancellationToken,
                render.Document.ValidateContentAdmission,
                _sourceMemoryFactory is null ? render.Worker.ObjectSecurity : null);
        };

    /// <summary>
    /// Quarantines content and releases the worker after a render transport
    /// failure, then turns caller cancellation or an intentional font rotation
    /// into cancellation. Otherwise returns so the caller rethrows the failure.
    /// </summary>
    private void ThrowIfTransportFailureIsCancellation(in WorkerRenderInputs render)
    {
        WorkerLease lease = render.Lease;
        WindowsWorkerProcess worker = render.Worker;
        bool callerCanceled = render.CancellationToken.IsCancellationRequested;
        long currentGeneration = _requests.FontGeneration;
        bool generationCurrent = !render.Preflight.Info.HasText || render.RequestGeneration == currentGeneration;
        WorkerResponseValidation.QuarantineContentBeforeWorkerRelease(
            render.Document,
            contentFailureCallback: null,
            callerCanceled: callerCanceled,
            requiresFontCatalog: render.Preflight.Info.HasText,
            requestGeneration: render.RequestGeneration,
            currentGeneration: currentGeneration,
            invalidateWorker: () => _scheduler.Invalidate(lease.Slot, worker));
        if (callerCanceled)
            throw new OperationCanceledException(render.CancellationToken);
        if (render.Preflight.Info.HasText && !generationCurrent)
            throw new OperationCanceledException("The SVG worker was intentionally rotated after a font change.");
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
