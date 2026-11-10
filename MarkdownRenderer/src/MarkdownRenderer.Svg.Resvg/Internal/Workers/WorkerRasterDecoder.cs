using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers;

/// <summary>
/// Decodes one untrusted raster image in the isolated worker into a mapping
/// that carries both the encoded input and the decoded output.
/// </summary>
internal sealed class WorkerRasterDecoder(
    WorkerScheduler scheduler,
    WorkerRequestFactory requests,
    ResvgMarkdownSvgRendererOptions options)
{
    private readonly WorkerScheduler _scheduler = scheduler;
    private readonly WorkerRequestFactory _requests = requests;
    private readonly ResvgMarkdownSvgRendererOptions _options = options;

    internal async Task<WorkerRenderResult> DecodeRasterAsync(
        MarkdownRasterDecodeRequest request,
        RasterDecodeGeometry geometry,
        byte[] hash,
        Action contentAdmissionGuard,
        Action contentFailureCallback,
        CancellationToken cancellationToken)
    {
        WorkerLease? lease = null;
        SharedMemoryLease? memory = null;
        try
        {
            MarkdownSvgRenderPriority priority = request.Priority == MarkdownRasterDecodePriority.Visible
                ? MarkdownSvgRenderPriority.Visible : MarkdownSvgRenderPriority.Overscan;
            // No source copy or output section exists while this request is queued.
            lease = await _scheduler.AcquireAsync(WorkerOperation.RasterDecode, priority, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard();
            WindowsWorkerProcess worker = await _scheduler.EnsureWorkerAsync(lease
                , 0, 0, 0, 0, 0
                ).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard();
            memory = new SharedMemoryLease(WorkerSharedMemoryMapping.NewSourceMappingName(), request.EncodedBytes,
                geometry.OutputLength, worker.ObjectSecurity);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard();

            WorkerRequest wireRequest = new(WorkerOperation.RasterDecode, _requests.NextRequestId(),
                worker.NonceLow, worker.NonceHigh, 0, hash, request.EncodedBytes.Length,
                geometry.OutputLength, request.TargetWidthPixels, request.TargetHeightPixels,
                null, string.Empty, MarkdownSvgColorScheme.Light, null,
                RasterDecodePolicy.ToWorkerFormat(request.PixelFormat), 0, 0, 0, _options,
                RasterWorkingBytes: geometry.WorkingBytes, RasterSourceWidth: geometry.SourceWidth,
                RasterSourceHeight: geometry.SourceHeight, RasterOrientation: geometry.Orientation);
            WorkerResponse response;
            try
            {
                // The pool owns cancellation/preemption once work is active. The transaction must
                // drain or the worker must terminate before its borrowed mapping is released.
                response = await worker.ExchangeAsync(wireRequest, _options.RequestDeadline,
                    CancellationToken.None, memory).ConfigureAwait(false);
                RasterDecodePolicy.ValidateResponse(response, request, geometry);
                if (response.Status == WorkerStatus.WorkerFailure)
                    throw new WorkerProtocolException("The isolated raster decoder failed.");
            }
            catch (Exception exception) when (WorkerResponseValidation.IsWorkerTransportFailure(exception))
            {
                WorkerResponseValidation.QuarantineContentBeforeWorkerRelease(null, contentFailureCallback,
                    cancellationToken.IsCancellationRequested, false, 0, 0,
                    () => _scheduler.Invalidate(lease.Slot, worker));
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new WorkerRenderResult(WorkerResponseValidation.Stamp(response, worker), memory);
            memory = null;
            return result;
        }
        finally
        {
            try
            {
                ((IDisposable?)memory)?.Dispose();
            }
            finally
            {
                lease?.Dispose();
                memory = null;
                lease = null;
                request = null!;
                hash = null!;
                contentAdmissionGuard = null!;
                contentFailureCallback = null!;
            }
        }
    }
}
