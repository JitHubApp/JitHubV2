using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

internal sealed class ResvgMarkdownSvgDocument : IMarkdownSvgDocument, IMarkdownSvgPrioritizedDocument
{
    private readonly ResvgMarkdownSvgRenderer _renderer;
    private readonly WorkerPool _workers;
    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly SvgDocumentSourceSnapshot _sourceSnapshot;
    private readonly string _identity;
    private readonly WorkerDocumentHandle _workerDocument;
    private readonly SvgDocumentLifetime _lifetime;

    public ResvgMarkdownSvgDocument(
        ResvgMarkdownSvgRenderer renderer,
        WorkerPool workers,
        ResvgMarkdownSvgRendererOptions options,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        string identity,
        MarkdownSvgDocumentInfo info,
        WorkerDocumentHandle workerDocument)
    {
        _renderer = renderer;
        _workers = workers;
        _options = options;
        // Retain only our immutable preflight snapshot. The caller may supply
        // a small slice of a much larger pooled buffer and reuse it after Open.
        // The snapshot is explicitly cleared after Dispose has canceled and
        // drained every render admitted through this document.
        _sourceSnapshot = new SvgDocumentSourceSnapshot(preflight, openRequest);
        _identity = identity;
        _workerDocument = new WorkerDocumentHandle(
            workerDocument.DocumentId,
            workerDocument.PreferredWorkerInstanceId,
            renderer.Quarantine.CreateContentAdmissionGuard(identity),
            renderer.Quarantine.CreateContentFailureCallback(identity));
        WorkerPool workerPool = workers;
        WorkerDocumentHandle documentHandle = _workerDocument;
        SvgDocumentSourceSnapshot sourceSnapshot = _sourceSnapshot;
        _lifetime = new SvgDocumentLifetime(() =>
        {
            try
            {
                workerPool.ForgetDocument(documentHandle);
            }
            finally
            {
                try
                {
                    // The guard owns only the renderer and immutable content
                    // identity. Clear it after all admitted renders have drained.
                    documentHandle.ReleaseContentAdmissionGuard();
                }
                finally
                {
                    sourceSnapshot.Release();
                }
            }
        });
        Info = info;
        if (!renderer.TryRegisterDocument(_lifetime))
        {
            // Open can finish worker attachment concurrently with renderer
            // shutdown. A losing handle must never escape with private bytes
            // or a live worker token; no render has been admitted yet.
            _lifetime.Dispose();
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.Canceled,
                "The SVG renderer is shutting down.");
        }
    }

    public MarkdownSvgDocumentInfo Info { get; }

    public ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default) =>
        RenderCoreAsync(request, demand: null, cancellationToken);

    ValueTask<MarkdownSvgRaster> IMarkdownSvgPrioritizedDocument.RenderAsync(
        MarkdownSvgRenderRequest request,
        MarkdownSvgRenderDemand demand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(demand);
        return RenderCoreAsync(
            request with { Priority = demand.Priority },
            demand,
            cancellationToken);
    }

    private async ValueTask<MarkdownSvgRaster> RenderCoreAsync(
        MarkdownSvgRenderRequest request,
        MarkdownSvgRenderDemand? demand,
        CancellationToken cancellationToken)
    {
        SvgDocumentLifetime.RenderLease lifetime = EnterRender();
        SvgDocumentRenderSnapshot source = default;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            (int width, int height) = ValidateRenderRequest(request);
            int outputLength = GetOutputLength(width, height);
            ThrowIfRenderCanceled(cancellationToken, lifetime.Token);

            // This is deliberately before source snapshot access and worker
            // dispatch. Repeated renders from any retained handle observe the
            // same hash quarantine without reattaching the source.
            _renderer.Quarantine.ThrowIfQuarantined(_identity);
            source = _sourceSnapshot.GetForRender();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            WorkerRenderResult workerResult;
            try
            {
                workerResult = await _workers.RenderDocumentAsync(
                    _workerDocument,
                    source.Preflight,
                    source.OpenRequest,
                    request,
                    outputLength,
                    linked.Token,
                    demand).ConfigureAwait(false);
            }
            catch (Exception exception) when (TryClassifyWorkerRenderFailure(
                exception,
                out MarkdownSvgFailureReason reason,
                out bool quarantineContent))
            {
                if (quarantineContent)
                    _renderer.Quarantine.QuarantineWorkerFailure(_identity);
                throw new MarkdownSvgException(reason, innerException: exception);
            }

            return CreateRaster(workerResult, request, width, height, outputLength);
        }
        finally
        {
            // Completed async state machines may remain reachable through a
            // retained render task. Clear the source aliases before releasing
            // the lifetime lease so the lease remains the sole owner until all
            // admitted worker work has crossed its completion barrier.
            source = default;
            lifetime.Dispose();
        }
    }

    public void Dispose() => _lifetime.Dispose();

    private int GetOutputLength(int width, int height)
    {
        int outputLength;
        try
        {
            outputLength = checked(checked(width * height) * 4);
        }
        catch (OverflowException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, "The requested SVG raster dimensions overflow.", exception);
        }
        if (outputLength > _options.MaxOutputRasterBytes)
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, "The requested SVG raster exceeds the output ceiling; request bounded tiles.");
        return outputLength;
    }

    private static void ThrowIfRenderCanceled(CancellationToken cancellationToken, CancellationToken lifetimeToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lifetimeToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
    }

    /// <summary>
    /// Maps a worker render failure onto its host-visible reason, testing the
    /// failure types in the order the render catches them. A
    /// <see cref="MarkdownSvgException"/> and any unlisted failure are not
    /// mapped and propagate unchanged.
    /// </summary>
    /// <param name="exception">The failure from the worker render.</param>
    /// <param name="reason">The host-visible failure reason.</param>
    /// <param name="quarantineContent">Whether the failure is evidence against the SVG's content hash.</param>
    /// <returns>Whether the failure maps to a new <see cref="MarkdownSvgException"/>.</returns>
    private static bool TryClassifyWorkerRenderFailure(
        Exception exception,
        out MarkdownSvgFailureReason reason,
        out bool quarantineContent)
    {
        quarantineContent = false;
        switch (exception)
        {
            case OperationCanceledException:
            case ObjectDisposedException:
                reason = MarkdownSvgFailureReason.Canceled;
                return true;
            case WorkerInitializationDeadlineException:
                // A secondary worker can be created lazily for this render.
                // Its cold-start state says nothing about the SVG content.
                reason = MarkdownSvgFailureReason.Timeout;
                return true;
            case WorkerInitializationException:
                reason = MarkdownSvgFailureReason.WorkerFailure;
                return true;
            case WorkerDeadlineException:
                reason = MarkdownSvgFailureReason.Timeout;
                quarantineContent = true;
                return true;
            case MarkdownSvgException:
                reason = default;
                return false;
            case WorkerProtocolException or IOException or InvalidOperationException:
                reason = MarkdownSvgFailureReason.WorkerFailure;
                quarantineContent = true;
                return true;
            default:
                reason = default;
                return false;
        }
    }

    /// <summary>
    /// Validates the worker's raster metadata against the request and
    /// transfers the output mapping into the returned raster. The worker
    /// result is disposed either way.
    /// </summary>
    /// <param name="workerResult">The worker's render result.</param>
    /// <param name="request">The render request.</param>
    /// <param name="width">The validated raster width.</param>
    /// <param name="height">The validated raster height.</param>
    /// <param name="outputLength">The expected output length.</param>
    /// <returns>The raster that owns the output mapping.</returns>
    private MarkdownSvgRaster CreateRaster(
        WorkerRenderResult workerResult,
        MarkdownSvgRenderRequest request,
        int width,
        int height,
        int outputLength)
    {
        using (workerResult)
        {
            WorkerResponse response = workerResult.Response;
            _renderer.Quarantine.ThrowForRejectedResponse(response, _identity);
            if (response.OutputLength != outputLength || response.Width != width || response.Height != height ||
                response.Stride != checked(width * 4) || response.PixelFormat != request.PixelFormat)
            {
                _renderer.Quarantine.QuarantineWorkerFailure(_identity, response.WorkerInstanceId);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "The SVG worker returned malformed raster metadata.");
            }

            SharedMemoryLease memory = workerResult.TransferOutputMemory();
            try
            {
                return new MarkdownSvgRaster(memory, outputLength, width, height, response.Stride, response.PixelFormat);
            }
            catch
            {
                ((IDisposable)memory).Dispose();
                throw;
            }
        }
    }

    private SvgDocumentLifetime.RenderLease EnterRender()
    {
        if (_renderer.IsDisposed || _lifetime.TryEnterRender() is not { } lifetime)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.Canceled,
                "The SVG document is no longer accepting render requests.");
        }

        return lifetime;
    }

    internal static (int Width, int Height) ValidateRenderRequest(MarkdownSvgRenderRequest request)
    {
        if (request.TargetWidthPixels <= 0 || request.TargetHeightPixels <= 0)
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, "SVG output dimensions must be positive.");
        if (!PhysicalViewportValidation.IsValid(
                request.PhysicalViewport,
                request.TargetWidthPixels,
                request.TargetHeightPixels))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The fractional SVG physical viewport must be finite, positive, and round up to the requested output dimensions.");
        }
        if (!Enum.IsDefined(request.PixelFormat))
            throw new ArgumentOutOfRangeException(nameof(request));
        if (!Enum.IsDefined(request.Priority))
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.TileRegion is not { } tile)
            return (request.TargetWidthPixels, request.TargetHeightPixels);
        long right = (long)tile.X + tile.Width;
        long bottom = (long)tile.Y + tile.Height;
        if (tile.X < 0 || tile.Y < 0 || tile.Width <= 0 || tile.Height <= 0 ||
            right > request.TargetWidthPixels || bottom > request.TargetHeightPixels)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, "The SVG tile lies outside the complete output.");
        }
        return (tile.Width, tile.Height);
    }
}
