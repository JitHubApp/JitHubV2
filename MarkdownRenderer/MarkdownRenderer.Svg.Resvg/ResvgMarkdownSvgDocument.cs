using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

internal sealed class ResvgMarkdownSvgDocument : IMarkdownSvgDocument, IMarkdownSvgHostDocument
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
            renderer.CreateContentAdmissionGuard(identity),
            renderer.CreateContentFailureCallback(identity));
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

    ValueTask<MarkdownSvgRaster> IMarkdownSvgHostDocument.RenderForHostAsync(
        MarkdownSvgRenderRequest request,
        MarkdownSvgDemandTicket demand,
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
        MarkdownSvgDemandTicket? demand,
        CancellationToken cancellationToken)
    {
        SvgDocumentLifetime.RenderLease lifetime = EnterRender();
        SvgDocumentRenderSnapshot source = default;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            (int width, int height) = ValidateRenderRequest(request);
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

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lifetime.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException exception)
            {
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
            }

            // This is deliberately before source snapshot access and worker
            // dispatch. Repeated renders from any retained handle observe the
            // same hash quarantine without reattaching the source.
            _renderer.ThrowIfQuarantined(_identity);
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
            catch (OperationCanceledException exception)
            {
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
            }
            catch (ObjectDisposedException exception)
            {
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
            }
            catch (WorkerInitializationDeadlineException exception)
            {
                // A secondary worker can be created lazily for this render.
                // Its cold-start state says nothing about the SVG content.
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
            }
            catch (WorkerInitializationException exception)
            {
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
            }
            catch (WorkerDeadlineException exception)
            {
                _renderer.QuarantineWorkerFailure(_identity);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
            }
            catch (MarkdownSvgException)
            {
                throw;
            }
            catch (Exception exception) when (exception is WorkerProtocolException or IOException or InvalidOperationException)
            {
                _renderer.QuarantineWorkerFailure(_identity);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
            }

            using (workerResult)
            {
                WorkerResponse response = workerResult.Response;
                _renderer.ThrowForRejectedResponse(response, _identity);
                if (response.OutputLength != outputLength || response.Width != width || response.Height != height ||
                    response.Stride != checked(width * 4) || response.PixelFormat != request.PixelFormat)
                {
                    _renderer.QuarantineWorkerFailure(_identity, response.WorkerInstanceId);
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

/// <summary>
/// Owns the sanitized source snapshot needed for worker attachment and render
/// fallback. Its owner releases it only after all admitted renders have drained.
/// </summary>
internal sealed class SvgDocumentSourceSnapshot
{
    private SourceState? _state;

    public SvgDocumentSourceSnapshot(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(openRequest);

        // Preserve the authoritative preflight snapshot, not the caller's
        // possibly sliced/reusable input memory. The request copy carries only
        // immutable rendering metadata; source bytes live solely in preflight
        // and are supplied to worker mappings from that owner.
        _state = new SourceState(
            preflight,
            openRequest with { SanitizedSvgBytes = ReadOnlyMemory<byte>.Empty });
    }

    public SvgDocumentRenderSnapshot GetForRender()
    {
        SourceState? state = Volatile.Read(ref _state);
        if (state is null)
            throw new ObjectDisposedException(nameof(SvgDocumentSourceSnapshot));

        return state.GetForRender();
    }

    public void Release()
    {
        SourceState? state = Interlocked.Exchange(ref _state, null);
        state?.Release();
    }

    private sealed class SourceState(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest)
    {
        private SvgPreflightResult? _preflight = preflight;
        private MarkdownSvgOpenRequest? _openRequest = openRequest;

        public SvgDocumentRenderSnapshot GetForRender()
        {
            SvgPreflightResult? currentPreflight = Volatile.Read(ref _preflight);
            MarkdownSvgOpenRequest? currentOpenRequest = Volatile.Read(ref _openRequest);
            if (currentPreflight is null || currentOpenRequest is null)
                throw new ObjectDisposedException(nameof(SvgDocumentSourceSnapshot));

            return new SvgDocumentRenderSnapshot(currentPreflight, currentOpenRequest);
        }

        public void Release()
        {
            // The request holds rendering metadata only. Clear it and release
            // the sole source owner, including any retained task that still
            // holds the preflight result object itself.
            Interlocked.Exchange(ref _openRequest, null);
            Interlocked.Exchange(ref _preflight, null)?.ReleaseSource();
        }
    }
}

internal readonly record struct SvgDocumentRenderSnapshot(
    SvgPreflightResult Preflight,
    MarkdownSvgOpenRequest OpenRequest);
