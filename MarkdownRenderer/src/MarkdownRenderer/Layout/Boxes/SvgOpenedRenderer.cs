using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

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
            return _context.Dispatcher?.HasThreadAccess == true
                ? Task.Run(() => RenderCoreAsync(work, dispatchStartedAt), CancellationToken.None)
                : RenderCoreAsync(work, dispatchStartedAt);
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
        IMarkdownSvgDocument? openedDocument = work.Document;
        SvgDocumentUseLease? openedDocumentUse = work.DocumentUse;
        SharedCanvasBitmapCache.Lease? bitmapLease = null;
        BitmapOutputAdmission.Reservation? bitmapReservation = null;
        CanvasBitmap? ownedBitmap = null;
        SemaphoreSlim? renderGate = null;
        bool renderGateHeld = false;
        IMarkdownSvgRenderer renderer = metadata.Renderer;
        long providerCacheGeneration = metadata.ProviderCacheGeneration;
        MarkdownSvgDocumentInfo info = metadata.Info;
        int renderGeneration = metadata.RenderGeneration;
        string resolvedCacheKey = metadata.ResolvedCacheKey;
        string svgFailureCacheKey = metadata.SvgFailureCacheKey;
        string contentCacheKey = work.ContentCacheKey;
        Size intrinsic = metadata.Intrinsic;
        int targetWidth = metadata.TargetWidthPixels;
        int targetHeight = metadata.TargetHeightPixels;
        long targetPixels = metadata.TargetPixels;
        string bitmapIdentity = metadata.BitmapIdentity;
        MarkdownSvgRenderDemand demand = metadata.Demand!;
        BitmapOutputWorkContext bitmapOutputWork = _outputAdmission.CaptureBitmapOutputWorkContext(
            cancellationToken, expectedDemand: demand, expectedSvgGeneration: renderGeneration);
        long svgWorkId = metadata.WorkId;
        long svgSourceBytes = metadata.SourceByteLength;
        try
        {
            using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope(metadata.WorkId);
            if (dispatchStartedAt != 0)
            {
                using var dispatchTrace = MarkdownSvgStageTrace.MeasureFrom(
                    svgWorkId, MarkdownSvgWorkStage.OuterDispatchQueueWait,
                    dispatchStartedAt, svgSourceBytes);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (work.UsesTiles)
            {
                long tilePublishRequestedAt = svgWorkId != 0 ? Stopwatch.GetTimestamp() : 0;
                var tiledPublication = _host.CreateTiledSvgPublication(
                    metadata,
                    openedDocument!,
                    openedDocumentUse!,
                    tilePublishRequestedAt);
                // PublishOnUiThread consumes the callbacks on delivery or
                // rejection, including synchronous enqueue failure. Transfer
                // before invoking it so the outer finally cannot double-own.
                work.TransferTiledOwnership();
                openedDocument = null;
                openedDocumentUse = null;
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
                return;
            }

            CanvasDevice device = _context.ResourceCreator.Device;
            if (!_outputAdmission.TryAcquireAdmittedSvgBitmap(device, bitmapIdentity, out bitmapLease,
                    expectedWork: bitmapOutputWork))
            {
                if (_outputDeferral.IsBitmapOutputDeferred)
                    return;
                {
                    using var stageTrace = MarkdownSvgStageTrace.Measure(
                        svgWorkId,
                        MarkdownSvgWorkStage.BitmapCacheWait,
                        svgSourceBytes,
                        targetPixels);
                    renderGate = _resources.BitmapCache.GetDecodeGate(device, bitmapIdentity);
                    await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    renderGateHeld = true;
                }

                _ = _outputAdmission.TryAcquireAdmittedSvgBitmap(device, bitmapIdentity, out bitmapLease,
                    expectedWork: bitmapOutputWork);
                if (bitmapLease is null)
                {
                    if (_outputDeferral.IsBitmapOutputDeferred ||
                        !_outputAdmission.TryReserveBitmapOutput(checked(targetPixels * 4), out bitmapReservation,
                            expectedWork: bitmapOutputWork))
                        return;
                    MarkdownSvgRaster raster;
                    {
                        using var stageTrace = MarkdownSvgStageTrace.Measure(
                            svgWorkId,
                            MarkdownSvgWorkStage.ProviderRender,
                            svgSourceBytes,
                            targetPixels);
                        raster = await _host.RenderSvgDocumentAsync(
                                openedDocument!,
                                new MarkdownSvgRenderRequest(
                                    targetWidth,
                                    targetHeight,
                                    TileRegion: null,
                                    PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
                                    Priority: demand.Priority)
                                { PhysicalViewport = metadata.PhysicalViewport },
                                cancellationToken,
                                openedDocumentUse!,
                                demand)
                            .ConfigureAwait(false);
                    }

                    try
                    {
                        if (!_host.IsSvgProviderCurrent(renderer, providerCacheGeneration, info.HasText))
                        {
                            _host.PublishSvgGenerationInvalidated(renderGeneration);
                            return;
                        }
                        _host.ValidateSvgRaster(raster, targetWidth, targetHeight);
                        {
                            using var stageTrace = MarkdownSvgStageTrace.Measure(
                                svgWorkId,
                                MarkdownSvgWorkStage.BitmapUpload,
                                svgSourceBytes,
                                targetPixels);
                            ownedBitmap = _host.CreateBitmapFromSvgRaster(
                                raster,
                                svgWorkId,
                                svgSourceBytes,
                                targetPixels,
                                bitmapReservation: bitmapReservation);
                        }
                        {
                            try
                            {
                                using var stageTrace = MarkdownSvgStageTrace.Measure(
                                    svgWorkId,
                                    MarkdownSvgWorkStage.BitmapCacheStore,
                                    svgSourceBytes,
                                    targetPixels);
                                bitmapLease = _resources.BitmapCache.StoreSvgAndAcquire(
                                    device,
                                    bitmapIdentity,
                                    ownedBitmap,
                                    intrinsicSize: null,
                                    pixelWidth: raster.WidthPixels,
                                    pixelHeight: raster.HeightPixels
                                    , svgWorkId: svgWorkId,
                                    svgSourceBytes: svgSourceBytes,
                                    svgTargetPixels: targetPixels
                                    , bitmapReservation: bitmapReservation
                                );
                                ownedBitmap = null;
                                bitmapReservation = null;
                            }
                            catch
                            {
                                _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        _host.TryImageCleanup(raster.Dispose);
                    }
                }
            }

            SharedCanvasBitmapCache.Lease publishedLease = bitmapLease ??
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "The SVG bitmap cache did not return a lease.");

            _resources.SvgSources.RecordLastBitmap(
                work.BuiltInSourceAliasKey,
                contentCacheKey,
                bitmapIdentity,
                targetWidth,
                targetHeight,
                metadata.PhysicalViewport);

            long bitmapPublishRequestedAt = svgWorkId != 0 ? Stopwatch.GetTimestamp() : 0;
            var bitmapPublication = _host.CreateBitmapSvgPublication(
                metadata,
                publishedLease,
                bitmapPublishRequestedAt);
            bitmapLease = null;
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

        catch (Exception exception)
        {
            _host.HandleSvgWorkFailure(exception, cancellationToken, work.SessionLifetimeToken,
                resolvedCacheKey, svgFailureCacheKey, renderGeneration);
        }
        finally
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            if (bitmapLease is { } remainingLease)
                _host.TryImageCleanup(remainingLease.Dispose);
            if (renderGateHeld)
                _host.TryImageCleanup(() => renderGate!.Release());
            // Consumed ownership retires only here, after actual Render and
            // upload/raster cleanup. Caller Dispose cannot revoke it early.
            openedDocument = null;
            openedDocumentUse = null;
            work.Complete();
        }
    }
}
