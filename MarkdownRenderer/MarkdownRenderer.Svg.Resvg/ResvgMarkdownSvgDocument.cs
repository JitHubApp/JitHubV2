using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

internal sealed class ResvgMarkdownSvgDocument : IMarkdownSvgDocument
{
    private readonly ResvgMarkdownSvgRenderer _renderer;
    private readonly WorkerPool _workers;
    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly SvgPreflightResult _preflight;
    private readonly MarkdownSvgOpenRequest _openRequest;
    private readonly string _identity;
    private readonly WorkerDocumentHandle _workerDocument;
    private readonly object _lifetimeLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private SharedMemoryLease? _sourceMemory;
    private int _activeRenders;
    private int _disposed;

    public ResvgMarkdownSvgDocument(
        ResvgMarkdownSvgRenderer renderer,
        WorkerPool workers,
        ResvgMarkdownSvgRendererOptions options,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        string identity,
        MarkdownSvgDocumentInfo info,
        WorkerDocumentHandle workerDocument,
        SharedMemoryLease sourceMemory)
    {
        _renderer = renderer;
        _workers = workers;
        _options = options;
        _preflight = preflight;
        _openRequest = openRequest;
        _identity = identity;
        _workerDocument = workerDocument;
        _sourceMemory = sourceMemory;
        Info = info;
    }

    public MarkdownSvgDocumentInfo Info { get; }

    public async ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        RenderLifetimeLease lifetime = EnterRender();
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

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var memory = new SharedMemoryLease(
                $"Local\\MarkdownRenderer.Resvg.{Environment.ProcessId}.{Guid.NewGuid():N}",
                [],
                outputLength);
            bool transferred = false;
            try
            {
                WorkerResponse response;
                try
                {
                    response = await _workers.RenderDocumentAsync(
                        _workerDocument,
                        _preflight,
                        _openRequest,
                        request,
                        lifetime.SourceMemory,
                        memory,
                        linked.Token).ConfigureAwait(false);
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

                _renderer.ThrowForRejectedResponse(response, _identity);
                if (response.OutputLength != outputLength || response.Width != width || response.Height != height ||
                    response.Stride != checked(width * 4) || response.PixelFormat != request.PixelFormat)
                {
                    _renderer.QuarantineWorkerFailure(_identity, response.WorkerInstanceId);
                    throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "The SVG worker returned malformed raster metadata.");
                }
                var raster = new MarkdownSvgRaster(memory, outputLength, width, height, response.Stride, response.PixelFormat);
                transferred = true;
                return raster;
            }
            finally
            {
                if (!transferred)
                    ((IDisposable)memory).Dispose();
            }
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    public void Dispose()
    {
        SharedMemoryLease? source = null;
        bool release = false;
        lock (_lifetimeLock)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            if (_activeRenders == 0)
            {
                source = _sourceMemory;
                _sourceMemory = null;
                release = true;
            }
        }
        _lifetime.Cancel();
        if (release)
            ReleaseResources(source);
    }

    private RenderLifetimeLease EnterRender()
    {
        lock (_lifetimeLock)
        {
            if (_disposed != 0 || _renderer.IsDisposed)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.Canceled,
                    "The SVG document is no longer accepting render requests.");
            }
            SharedMemoryLease source = _sourceMemory ??
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.Canceled,
                    "The SVG document is no longer accepting render requests.");
            _activeRenders++;
            return new RenderLifetimeLease(this, source, _lifetime.Token);
        }
    }

    private void ExitRender()
    {
        SharedMemoryLease? source = null;
        bool release = false;
        lock (_lifetimeLock)
        {
            _activeRenders--;
            if (_disposed != 0 && _activeRenders == 0)
            {
                source = _sourceMemory;
                _sourceMemory = null;
                release = true;
            }
        }
        if (release)
            ReleaseResources(source);
    }

    private void ReleaseResources(SharedMemoryLease? source)
    {
        _workers.ForgetDocument(_workerDocument);
        if (source is not null)
            ((IDisposable)source).Dispose();
        _lifetime.Dispose();
    }

    private static (int Width, int Height) ValidateRenderRequest(MarkdownSvgRenderRequest request)
    {
        if (request.TargetWidthPixels <= 0 || request.TargetHeightPixels <= 0)
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, "SVG output dimensions must be positive.");
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

    private sealed class RenderLifetimeLease(
        ResvgMarkdownSvgDocument owner,
        SharedMemoryLease sourceMemory,
        CancellationToken token) : IDisposable
    {
        private int _disposed;

        public SharedMemoryLease SourceMemory { get; } = sourceMemory;
        public CancellationToken Token { get; } = token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ExitRender();
        }
    }
}
