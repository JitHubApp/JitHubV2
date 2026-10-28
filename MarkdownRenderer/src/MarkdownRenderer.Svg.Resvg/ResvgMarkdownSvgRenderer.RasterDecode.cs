using System.Security.Cryptography;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

public sealed partial class ResvgMarkdownSvgRenderer
{
    /// <inheritdoc />
    public bool SupportsFormat(MarkdownRasterFormat format) => format == MarkdownRasterFormat.WebP;

    /// <inheritdoc />
    public async ValueTask<MarkdownRaster> DecodeAsync(MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            // Includes hashing, metadata inspection, worker startup and mapping initialization.
            // None of those synchronous phases may run on a calling UI thread.
            return await Task.Run(() => DecodeRasterCoreAsync(request, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            request = null!;
        }
    }

    private async Task<MarkdownRaster> DecodeRasterCoreAsync(MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken)
    {
        SvgDocumentLifetime.RenderLease? operation = null;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            if (IsDisposed)
                throw new MarkdownRasterException(MarkdownRasterFailureReason.Canceled);
            operation = _openOperations.TryEnterRender()
                ?? throw new MarkdownRasterException(MarkdownRasterFailureReason.Canceled);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Token);
            cancellationToken = linkedCancellation.Token;
            RasterDecodeGeometry geometry = RasterDecodePolicy.Inspect(request, cancellationToken);
            if (geometry.OutputLength > _options.MaxOutputRasterBytes)
                throw new MarkdownRasterException(MarkdownRasterFailureReason.ResourceLimitExceeded);
            byte[] hash = SHA256.HashData(request.EncodedBytes.Span);
            string identity = "raster:" + Convert.ToHexString(hash);
            void CheckQuarantine()
            {
                if (_quarantine.ContainsKey(identity))
                    throw new MarkdownRasterException(MarkdownRasterFailureReason.WorkerFailure,
                        "This raster image is quarantined after an isolated decoder failure.");
            }
            CheckQuarantine();
            using WorkerRenderResult decoded = await _workers.DecodeRasterAsync(request, geometry, hash,
                CheckQuarantine, () => _quarantine.TryAdd(identity, 0), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (decoded.Response.Status != WorkerStatus.Ok)
                throw new MarkdownRasterException(decoded.Response.Status switch
                {
                    WorkerStatus.Unsupported => MarkdownRasterFailureReason.UnsupportedContent,
                    WorkerStatus.ResourceLimit => MarkdownRasterFailureReason.ResourceLimitExceeded,
                    _ => MarkdownRasterFailureReason.WorkerFailure,
                });
            SharedMemoryLease? memory = decoded.TransferOutputMemory();
            try
            {
                var raster = new MarkdownRaster(memory, request.TargetWidthPixels, request.TargetHeightPixels,
                    checked(request.TargetWidthPixels * 4), request.PixelFormat);
                memory = null;
                return raster;
            }
            finally
            {
                ((IDisposable?)memory)?.Dispose();
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownRasterException(MarkdownRasterFailureReason.Canceled, innerException: exception);
        }
        catch (ObjectDisposedException exception) when (IsDisposed || cancellationToken.IsCancellationRequested)
        {
            throw new MarkdownRasterException(MarkdownRasterFailureReason.Canceled, innerException: exception);
        }
        catch (WorkerDeadlineException exception)
        {
            throw new MarkdownRasterException(MarkdownRasterFailureReason.Timeout, innerException: exception);
        }
        catch (MarkdownSvgException exception)
        {
            throw new MarkdownRasterException(exception.Reason switch
            {
                MarkdownSvgFailureReason.ArchitectureMismatch => MarkdownRasterFailureReason.ArchitectureMismatch,
                MarkdownSvgFailureReason.Canceled => MarkdownRasterFailureReason.Canceled,
                MarkdownSvgFailureReason.Timeout => MarkdownRasterFailureReason.Timeout,
                MarkdownSvgFailureReason.ResourceLimitExceeded => MarkdownRasterFailureReason.ResourceLimitExceeded,
                _ => MarkdownRasterFailureReason.WorkerFailure,
            }, innerException: exception);
        }
        catch (Exception exception) when (exception is WorkerProtocolException or WorkerInitializationException or IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            throw new MarkdownRasterException(MarkdownRasterFailureReason.WorkerFailure, innerException: exception);
        }
        finally
        {
            linkedCancellation?.Dispose();
            operation?.Dispose();
            request = null!;
        }
    }
}
