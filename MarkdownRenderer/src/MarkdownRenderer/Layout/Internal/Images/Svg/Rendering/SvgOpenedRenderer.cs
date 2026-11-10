using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// Renders an opened SVG handed over by <see cref="SvgSourceOpener"/>: a tiled
/// image publishes its document to the box's tile cache, and a whole image is
/// rendered once (behind the keyed device-bitmap gate), uploaded, cached and
/// published. Consumed render work retires only after the actual render and
/// upload have finished.
/// </summary>
internal sealed class SvgOpenedRenderer
{
    private readonly ISvgLoadHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputAdmission _outputAdmission;
    private readonly ImageBitmapOutputDeferral _outputDeferral;

    internal SvgOpenedRenderer(
        ISvgLoadHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        ImageBitmapOutputAdmission outputAdmission,
        ImageBitmapOutputDeferral outputDeferral)
    {
        _host = host;
        _context = context;
        _resources = resources;
        _outputAdmission = outputAdmission;
        _outputDeferral = outputDeferral;
    }

    internal Task RenderAsync(SvgOpenedRenderWork work)
    {
        if (!work.TryConsume())
            throw new InvalidOperationException("The opened SVG work has already been consumed or disposed.");
        long dispatchStartedAt = _context.Dispatcher?.HasThreadAccess == true &&
            work.Metadata.WorkId != 0 && MarkdownPerformanceEventSource.Log.IsSvgWorkMeasurementEnabled()
                ? Stopwatch.GetTimestamp() : 0;
        try
        {
            // Even an asynchronously opened source can complete on the UI
            // dispatcher when every admission/cache operation is synchronous.
            return SvgWorkDispatch.RunOffDispatcher(
                _context.Dispatcher?.HasThreadAccess == true, () => RenderCoreAsync(work, dispatchStartedAt));
        }
        catch
        {
            work.Complete();
            throw;
        }
    }

    private async Task RenderCoreAsync(SvgOpenedRenderWork work, long dispatchStartedAt)
    {
        SvgPublicationMetadata metadata = work.Metadata;
        CancellationToken cancellationToken = work.CancellationToken;
        var state = new SvgRenderState(
            work,
            _outputAdmission.CaptureBitmapOutputWorkContext(
                cancellationToken, expectedDemand: metadata.Demand!, expectedSvgGeneration: metadata.RenderGeneration));
        try
        {
            using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope(metadata.WorkId);
            if (dispatchStartedAt != 0)
            {
                using var dispatchTrace = MarkdownSvgStageTrace.MeasureFrom(
                    metadata.WorkId, MarkdownSvgWorkStage.OuterDispatchQueueWait,
                    dispatchStartedAt, metadata.SourceByteLength);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (work.UsesTiles)
            {
                PublishTiled(work);
                return;
            }

            if (await TryAcquireBitmapAsync(state).ConfigureAwait(false))
                PublishBitmap(state);
        }

        catch (Exception exception)
        {
            _host.HandleSvgWorkFailure(exception, cancellationToken, work.SessionLifetimeToken,
                metadata.ResolvedCacheKey, metadata.SvgFailureCacheKey, metadata.RenderGeneration);
        }
        finally
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref state.OwnedBitmap, ref state.BitmapReservation);
            if (state.BitmapLease is { } remainingLease)
                _host.TryImageCleanup(remainingLease.Dispose);
            if (state.RenderGateHeld)
                _host.TryImageCleanup(() => state.RenderGate!.Release());
            // Consumed ownership retires only here, after actual Render and
            // upload/raster cleanup. Caller Dispose cannot revoke it early.
            work.Complete();
        }
    }

    private void PublishTiled(SvgOpenedRenderWork work)
    {
        long tilePublishRequestedAt = work.Metadata.WorkId != 0 ? Stopwatch.GetTimestamp() : 0;
        var tiledPublication = _host.CreateTiledSvgPublication(
            work.Metadata,
            work.Document!,
            work.DocumentUse!,
            tilePublishRequestedAt);
        // PublishOnUiThread consumes the callbacks on delivery or
        // rejection, including synchronous enqueue failure. Transfer
        // before invoking it so the outer finally cannot double-own.
        work.TransferTiledOwnership();
        try
        {
            _host.PublishOnUiThread(tiledPublication.Publish, tiledPublication.OnDropped);
        }
        catch
        {
            // Admission allocation or an unexpected dispatcher failure
            // may throw before it consumes either callback. The owner
            // is idempotent even if delivery/drop already ran.
            tiledPublication.OnDropped?.Invoke();
            throw;
        }
    }

    /// <summary>
    /// Acquires the admitted shared bitmap for this render's identity or, behind
    /// the keyed device-bitmap gate, renders and stores it.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when bitmap output is deferred, its reservation
    /// was denied, or the provider generation changed during the render.
    /// </returns>
    private async Task<bool> TryAcquireBitmapAsync(SvgRenderState state)
    {
        SvgPublicationMetadata metadata = state.Work.Metadata;
        string bitmapIdentity = metadata.BitmapIdentity;
        CanvasDevice device = _context.ResourceCreator.Device;
        if (_outputAdmission.TryAcquireAdmittedSvgBitmap(device, bitmapIdentity, out state.BitmapLease,
                expectedWork: state.BitmapOutputWork))
        {
            return true;
        }
        if (_outputDeferral.IsBitmapOutputDeferred)
            return false;
        {
            using var stageTrace = MarkdownSvgStageTrace.Measure(
                metadata.WorkId,
                MarkdownSvgWorkStage.BitmapCacheWait,
                metadata.SourceByteLength,
                metadata.TargetPixels);
            state.RenderGate = _resources.BitmapCache.GetDecodeGate(device, bitmapIdentity);
            await state.RenderGate.WaitAsync(state.Work.CancellationToken).ConfigureAwait(false);
            state.RenderGateHeld = true;
        }

        _ = _outputAdmission.TryAcquireAdmittedSvgBitmap(device, bitmapIdentity, out state.BitmapLease,
            expectedWork: state.BitmapOutputWork);
        if (state.BitmapLease is not null)
            return true;
        if (_outputDeferral.IsBitmapOutputDeferred ||
            !_outputAdmission.TryReserveBitmapOutput(checked(metadata.TargetPixels * 4), out state.BitmapReservation,
                expectedWork: state.BitmapOutputWork))
        {
            return false;
        }
        return await TryRenderAndStoreAsync(state, device).ConfigureAwait(false);
    }

    /// <returns><see langword="false"/> when the provider generation changed during the render.</returns>
    private async Task<bool> TryRenderAndStoreAsync(SvgRenderState state, CanvasDevice device)
    {
        SvgOpenedRenderWork work = state.Work;
        SvgPublicationMetadata metadata = work.Metadata;
        MarkdownSvgRenderDemand demand = metadata.Demand!;
        long svgWorkId = metadata.WorkId;
        long svgSourceBytes = metadata.SourceByteLength;
        long targetPixels = metadata.TargetPixels;
        MarkdownSvgRaster raster;
        {
            using var stageTrace = MarkdownSvgStageTrace.Measure(
                svgWorkId,
                MarkdownSvgWorkStage.ProviderRender,
                svgSourceBytes,
                targetPixels);
            raster = await _host.RenderSvgDocumentAsync(
                    work.Document!,
                    new MarkdownSvgRenderRequest(
                        metadata.TargetWidthPixels,
                        metadata.TargetHeightPixels,
                        TileRegion: null,
                        PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
                        Priority: demand.Priority)
                    { PhysicalViewport = metadata.PhysicalViewport },
                    work.CancellationToken,
                    work.DocumentUse!,
                    demand)
                .ConfigureAwait(false);
        }

        try
        {
            if (!_host.IsSvgProviderCurrent(metadata.Renderer, metadata.ProviderCacheGeneration, metadata.Info.HasText))
            {
                _host.PublishSvgGenerationInvalidated(metadata.RenderGeneration);
                return false;
            }
            _host.ValidateSvgRaster(raster, metadata.TargetWidthPixels, metadata.TargetHeightPixels);
            UploadAndStore(state, device, raster);
            return true;
        }
        finally
        {
            _host.TryImageCleanup(raster.Dispose);
        }
    }

    // Uploads the validated raster under the output reservation, then lets
    // the shared bitmap cache adopt the bitmap and the reservation together.
    private void UploadAndStore(SvgRenderState state, CanvasDevice device, MarkdownSvgRaster raster)
    {
        SvgPublicationMetadata metadata = state.Work.Metadata;
        long svgWorkId = metadata.WorkId;
        long svgSourceBytes = metadata.SourceByteLength;
        long targetPixels = metadata.TargetPixels;
        {
            using var stageTrace = MarkdownSvgStageTrace.Measure(
                svgWorkId,
                MarkdownSvgWorkStage.BitmapUpload,
                svgSourceBytes,
                targetPixels);
            state.OwnedBitmap = _host.CreateBitmapFromSvgRaster(
                raster,
                svgWorkId,
                svgSourceBytes,
                targetPixels,
                bitmapReservation: state.BitmapReservation);
        }
        try
        {
            using var stageTrace = MarkdownSvgStageTrace.Measure(
                svgWorkId,
                MarkdownSvgWorkStage.BitmapCacheStore,
                svgSourceBytes,
                targetPixels);
            state.BitmapLease = _resources.BitmapCache.StoreSvgAndAcquire(
                device,
                metadata.BitmapIdentity,
                state.OwnedBitmap,
                intrinsicSize: null,
                pixelWidth: raster.WidthPixels,
                pixelHeight: raster.HeightPixels,
                svgWorkId: svgWorkId,
                svgSourceBytes: svgSourceBytes,
                svgTargetPixels: targetPixels,
                bitmapReservation: state.BitmapReservation);
            state.OwnedBitmap = null;
            state.BitmapReservation = null;
        }
        catch
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref state.OwnedBitmap, ref state.BitmapReservation);
            throw;
        }
    }

    private void PublishBitmap(SvgRenderState state)
    {
        SvgOpenedRenderWork work = state.Work;
        SvgPublicationMetadata metadata = work.Metadata;
        SharedCanvasBitmapCache.Lease publishedLease = state.BitmapLease ??
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG bitmap cache did not return a lease.");

        _resources.SvgSources.RecordLastBitmap(
            work.BuiltInSourceAliasKey,
            work.ContentCacheKey,
            metadata.BitmapIdentity,
            metadata.TargetWidthPixels,
            metadata.TargetHeightPixels,
            metadata.PhysicalViewport);

        long bitmapPublishRequestedAt = metadata.WorkId != 0 ? Stopwatch.GetTimestamp() : 0;
        var bitmapPublication = _host.CreateBitmapSvgPublication(
            metadata,
            publishedLease,
            bitmapPublishRequestedAt);
        state.BitmapLease = null;
        try
        {
            _host.PublishOnUiThread(bitmapPublication.Publish, bitmapPublication.OnDropped);
        }
        catch
        {
            bitmapPublication.OnDropped?.Invoke();
            throw;
        }
        // A complete bitmap has no further use for the provider document.
        // Retire it in finally after admitted render uses drain, regardless
        // of whether the UI publication runs, is stale, or is dropped.
        // Resize/theme/device replacements reopen through the source cache
        // or the same authorized resolver; only tiled images retain trees.
    }
}
