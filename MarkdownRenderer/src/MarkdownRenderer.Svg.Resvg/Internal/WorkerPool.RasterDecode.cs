using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed partial class WorkerPool
{
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
            lease = await AcquireAsync(WorkerOperation.RasterDecode, priority, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard();
            WindowsWorkerProcess worker = await EnsureWorkerAsync(lease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                , 0, 0, 0, 0, 0
#endif
                ).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard();
            memory = new SharedMemoryLease(NewSourceMappingName(), request.EncodedBytes,
                geometry.OutputLength, worker.ObjectSecurity);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard();

            WorkerRequest wireRequest = new(WorkerOperation.RasterDecode, NextRequestId(),
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
            catch (Exception exception) when (IsWorkerTransportFailure(exception))
            {
                QuarantineContentBeforeWorkerRelease(null, contentFailureCallback,
                    cancellationToken.IsCancellationRequested, false, 0, 0,
                    () => Invalidate(lease.Slot, worker));
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new WorkerRenderResult(Stamp(response, worker), memory);
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
