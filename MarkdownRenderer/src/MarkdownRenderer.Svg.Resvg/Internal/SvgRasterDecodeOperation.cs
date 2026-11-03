using System.Security.Cryptography;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Decodes one explicitly registered static raster for
/// <see cref="ResvgMarkdownSvgRenderer"/> in the same isolated worker pool,
/// under the renderer's open-operation borrow and content quarantine.
/// </summary>
internal sealed class SvgRasterDecodeOperation(
    ResvgMarkdownSvgRenderer owner,
    ResvgMarkdownSvgRendererOptions options,
    WorkerPool workers,
    SvgContentQuarantine quarantine,
    SvgDocumentLifetime openOperations)
{
    private readonly ResvgMarkdownSvgRenderer _owner = owner;
    private readonly ResvgMarkdownSvgRendererOptions _options = options;
    private readonly WorkerPool _workers = workers;
    private readonly SvgContentQuarantine _quarantine = quarantine;
    private readonly SvgDocumentLifetime _openOperations = openOperations;

    /// <summary>Inspects, admits and decodes one raster in a worker.</summary>
    /// <param name="request">The decode request.</param>
    /// <param name="cancellationToken">Cancels the decode.</param>
    /// <returns>The decoded raster, which owns its output mapping.</returns>
    internal async Task<MarkdownRaster> DecodeAsync(MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken)
    {
        SvgDocumentLifetime.RenderLease? operation = null;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            if (_owner.IsDisposed)
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
                if (_quarantine.Contains(identity))
                    throw new MarkdownRasterException(MarkdownRasterFailureReason.WorkerFailure,
                        "This raster image is quarantined after an isolated decoder failure.");
            }
            CheckQuarantine();
            using WorkerRenderResult decoded = await _workers.DecodeRasterAsync(request, geometry, hash,
                CheckQuarantine, () => _quarantine.Add(identity), cancellationToken).ConfigureAwait(false);
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
        catch (ObjectDisposedException exception) when (_owner.IsDisposed || cancellationToken.IsCancellationRequested)
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
