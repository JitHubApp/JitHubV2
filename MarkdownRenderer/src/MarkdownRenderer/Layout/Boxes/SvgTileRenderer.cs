using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Renders an image's SVG tiles on demand: from each paint's viewport it
/// computes the visible and overscan tile ranges, records them in the
/// <see cref="SvgTileCache"/>, admits bounded render work for missing tiles,
/// and renders, uploads and publishes each tile. A pressure-parked image's
/// visible demand reopens its document through the host. The accepted plan,
/// document leases, image-work leases and failure publication stay with the
/// image box and are reached through <see cref="ISvgTileRendererHost"/>.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it came out of ImageBox whole,
/// with range planning, admission and tile rendering in one type. Range
/// planning is the next split.
/// </remarks>
internal sealed class SvgTileRenderer
{
    private const int SvgTileSizePixels = SvgTileCache.SvgTileSizePixels;

    private readonly ISvgTileRendererHost _host;
    private readonly SvgTileCache _cache;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputDeferral _outputDeferral;
    private readonly ImageBitmapOutputAdmission _outputAdmission;

    internal SvgTileRenderer(
        ISvgTileRendererHost host,
        SvgTileCache cache,
        ImageResourceScope resources,
        ImageBitmapOutputDeferral outputDeferral,
        ImageBitmapOutputAdmission outputAdmission)
    {
        _host = host;
        _cache = cache;
        _resources = resources;
        _outputDeferral = outputDeferral;
        _outputAdmission = outputAdmission;
    }

    private bool TryObservePressureParkedVisibleGap(
        Rect destination,
        Rect viewport,
        out long visualDemandEpoch)
    {
        visualDemandEpoch = 0;
        if (!_host.IsSvgPressureParked)
        {
            return false;
        }

        if (destination.Width <= 0 || destination.Height <= 0)
        {
            _cache.ClearPressureParkedViewportVisibility();
            return false;
        }

        Rect visible = SvgTilePainter.IntersectRects(destination, viewport);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            _cache.ObserveParkedOffscreen();
            return false;
        }

        var desired = _host.PickCurrentSvgRasterDimensions();
        int width = desired.Width;
        int height = desired.Height;
        if (width <= 0 || height <= 0)
        {
            _cache.ClearPressureParkedViewportVisibility();
            return false;
        }

        int visibleLeft = (int)Math.Clamp(
            Math.Floor((visible.Left - destination.Left) * desired.PhysicalViewport.Width / destination.Width),
            0,
            Math.Max(0, width - 1));
        int visibleTop = (int)Math.Clamp(
            Math.Floor((visible.Top - destination.Top) * desired.PhysicalViewport.Height / destination.Height),
            0,
            Math.Max(0, height - 1));
        int visibleRight = (int)Math.Clamp(
            Math.Ceiling((visible.Right - destination.Left) * desired.PhysicalViewport.Width / destination.Width),
            1,
            width);
        int visibleBottom = (int)Math.Clamp(
            Math.Ceiling((visible.Bottom - destination.Top) * desired.PhysicalViewport.Height / destination.Height),
            1,
            height);
        int firstX = visibleLeft / SvgTileSizePixels;
        int firstY = visibleTop / SvgTileSizePixels;
        int lastX = Math.Max(firstX, (visibleRight - 1) / SvgTileSizePixels);
        int lastY = Math.Max(firstY, (visibleBottom - 1) / SvgTileSizePixels);

        return _cache.TryObserveParkedVisibleRange(
            firstX, firstY, lastX, lastY, desired, out visualDemandEpoch);
    }

    internal void QueueVisibleSvgTiles(Rect destination, Rect viewport)
    {
        SvgTileRenderPlan? plan = _host.AcceptedSvgTilePlan;
        if (plan is null)
        {
            if (TryObservePressureParkedVisibleGap(destination, viewport, out long visualDemandEpoch))
                _host.ReopenPressureParkedSvgForVisibleDemand(visualDemandEpoch);
            return;
        }
        if (!_cache.IsCurrentSvgTilePlan(plan))
            return;
        if (!_host.IsCurrentSvgRendererState())
        {
            return;
        }

        Rect visible = SvgTilePainter.IntersectRects(destination, viewport);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            ObserveOffscreen(plan);
            return;
        }

        (int firstTileX, int firstTileY, int lastTileX, int lastTileY,
            int firstVisibleTileX, int firstVisibleTileY, int lastVisibleTileX, int lastVisibleTileY) =
            ComputeTileRanges(plan, destination, visible);

        List<SharedCanvasBitmapCache.Lease>? retired = null;
        List<RetirableCancellationTokenSource>? staleWork = null;
        int remainingQueueCapacity;
        if (!plan.TryBeginViewportMutation())
            return;

        try
        {
            if (!_cache.TryObserveVisibleTiles(
                    plan,
                    destination,
                    visible,
                    firstTileX,
                    firstTileY,
                    lastTileX,
                    lastTileY,
                    firstVisibleTileX,
                    firstVisibleTileY,
                    lastVisibleTileX,
                    lastVisibleTileY,
                    ref retired,
                    ref staleWork,
                    out remainingQueueCapacity))
            {
                return;
            }
        }
        finally
        {
            try
            {
                plan.EndViewportMutation();
            }
            finally
            {
                // Retire outside the tile lock even when a later mutation or
                // demand update fails. Failed native disposal remains charged.
                RetirePrunedTileState(retired, staleWork);
            }
        }

        QueueVisibleThenOverscanTiles(
            plan,
            (firstTileX, firstTileY, lastTileX, lastTileY),
            (firstVisibleTileX, firstVisibleTileY, lastVisibleTileX, lastVisibleTileY),
            remainingQueueCapacity);
    }

    // Promotes the plan to visible demand, then queues missing visible tiles
    // before overscan tiles within the remaining in-flight capacity.
    private void QueueVisibleThenOverscanTiles(
        SvgTileRenderPlan plan,
        (int FirstX, int FirstY, int LastX, int LastY) overscan,
        (int FirstX, int FirstY, int LastX, int LastY) visible,
        int remainingQueueCapacity)
    {
        plan.Demand.PromoteVisible();
        SvgHostAdmissionCoordinator.Touch(plan);

        // The isolated provider has its own bounded priority queue. Keep the
        // host-side visible burst bounded as well; each completion invalidates
        // the canvas so remaining visible tiles are discovered next frame.
        int queued = QueueSvgTileRange(
            plan,
            visible.FirstX,
            visible.FirstY,
            visible.LastX,
            visible.LastY,
            remainingQueueCapacity,
            MarkdownSvgRenderPriority.Visible);
        _ = QueueSvgTileRange(
            plan,
            overscan.FirstX,
            overscan.FirstY,
            overscan.LastX,
            overscan.LastY,
            remainingQueueCapacity - queued,
            MarkdownSvgRenderPriority.Overscan);
    }

    // The visible tile range of the plan's physical grid and the overscan range
    // one tile beyond it. Clamps run in double precision so a rounded
    // coordinate at the maximum admitted dimension cannot wrap.
    private static (int FirstTileX, int FirstTileY, int LastTileX, int LastTileY,
        int FirstVisibleTileX, int FirstVisibleTileY, int LastVisibleTileX, int LastVisibleTileY) ComputeTileRanges(
        SvgTileRenderPlan plan,
        Rect destination,
        Rect visible)
    {
        double pixelsPerDipX = plan.PhysicalViewport.Width / destination.Width;
        double pixelsPerDipY = plan.PhysicalViewport.Height / destination.Height;
        // Clamp while still in double precision: a rounded coordinate at the
        // maximum admitted int dimension must not wrap during conversion.
        int visibleLeft = (int)Math.Clamp(
            Math.Floor((visible.Left - destination.Left) * pixelsPerDipX),
            0,
            Math.Max(0, plan.FullWidthPixels - 1));
        int visibleTop = (int)Math.Clamp(
            Math.Floor((visible.Top - destination.Top) * pixelsPerDipY),
            0,
            Math.Max(0, plan.FullHeightPixels - 1));
        int visibleRight = (int)Math.Clamp(
            Math.Ceiling((visible.Right - destination.Left) * pixelsPerDipX),
            1,
            plan.FullWidthPixels);
        int visibleBottom = (int)Math.Clamp(
            Math.Ceiling((visible.Bottom - destination.Top) * pixelsPerDipY),
            1,
            plan.FullHeightPixels);
        int left = Math.Clamp(
            visibleLeft - SvgTileSizePixels,
            0,
            Math.Max(0, plan.FullWidthPixels - 1));
        int top = Math.Clamp(
            visibleTop - SvgTileSizePixels,
            0,
            Math.Max(0, plan.FullHeightPixels - 1));
        int right = (int)Math.Min(
            (long)visibleRight + SvgTileSizePixels,
            plan.FullWidthPixels);
        int bottom = (int)Math.Min(
            (long)visibleBottom + SvgTileSizePixels,
            plan.FullHeightPixels);

        int firstTileX = left / SvgTileSizePixels;
        int firstTileY = top / SvgTileSizePixels;
        int lastTileX = Math.Max(firstTileX, (right - 1) / SvgTileSizePixels);
        int lastTileY = Math.Max(firstTileY, (bottom - 1) / SvgTileSizePixels);
        int firstVisibleTileX = visibleLeft / SvgTileSizePixels;
        int firstVisibleTileY = visibleTop / SvgTileSizePixels;
        int lastVisibleTileX = Math.Max(firstVisibleTileX, (visibleRight - 1) / SvgTileSizePixels);
        int lastVisibleTileY = Math.Max(firstVisibleTileY, (visibleBottom - 1) / SvgTileSizePixels);
        return (firstTileX, firstTileY, lastTileX, lastTileY,
            firstVisibleTileX, firstVisibleTileY, lastVisibleTileX, lastVisibleTileY);
    }

    private void RetirePrunedTileState(
        List<SharedCanvasBitmapCache.Lease>? retired,
        List<RetirableCancellationTokenSource>? staleWork)
    {
        if (retired is not null)
        {
            foreach (SharedCanvasBitmapCache.Lease tile in retired)
                _host.TryImageCleanup(tile.Dispose);
        }
        if (staleWork is not null)
        {
            foreach (RetirableCancellationTokenSource cancellation in staleWork)
            {
                try
                {
                    _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation);
                }
                catch (Exception exception)
                {
                    _host.RecordImageRetirementFailure(exception);
                }
            }
        }
    }

    // Paint found the plan outside the viewport; the viewport gate is held
    // while the cache records it.
    private void ObserveOffscreen(SvgTileRenderPlan plan)
    {
        if (!plan.TryBeginViewportMutation())
            return;

        try
        {
            _cache.ObserveOffscreen(plan);
        }
        finally
        {
            plan.EndViewportMutation();
        }
    }

    internal int QueueSvgTileRange(
        SvgTileRenderPlan plan,
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY,
        int capacity,
        MarkdownSvgRenderPriority priority)
    {
        if (!_cache.IsCurrentSvgTilePlan(plan) || capacity <= 0)
            return 0;

        int queued = 0;
        for (int tileY = firstTileY; tileY <= lastTileY && queued < capacity; tileY++)
        {
            for (int tileX = firstTileX; tileX <= lastTileX && queued < capacity; tileX++)
            {
                int x = checked(tileX * SvgTileSizePixels);
                int y = checked(tileY * SvgTileSizePixels);
                if (x < 0 || y < 0 || x >= plan.FullWidthPixels || y >= plan.FullHeightPixels)
                    continue;
                SvgTileKey key = (
                    x,
                    y,
                    Math.Min(SvgTileSizePixels, plan.FullWidthPixels - x),
                    Math.Min(SvgTileSizePixels, plan.FullHeightPixels - y));

                if (!_cache.TryProbeTileWork(plan, key, priority, out SvgTileWorkOwner? existingVisibleWork))
                    continue;
                if (existingVisibleWork is not null)
                {
                    existingVisibleWork.Demand.PromoteVisible();
                    continue;
                }

                if (!TryQueueTile(plan, key, priority, out bool stop))
                {
                    if (stop)
                        return queued;
                    continue;
                }
                queued++;
            }
        }
        return queued;
    }

    // Admits and schedules a render of one missing tile. Returns false when
    // the tile was not queued; <paramref name="stop"/> is then true when no
    // further tile can be admitted because image work or the document use
    // could not be acquired.
    private bool TryQueueTile(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        MarkdownSvgRenderPriority priority,
        out bool stop)
    {
        stop = true;
        if (!_host.TryBeginImageWork(out ImageWorkLease? tileWork) || tileWork is null)
            return false;

        SvgDocumentUseLease? tileDocumentUse = _host.TryAcquireSvgDocumentUse(plan.Document);
        if (tileDocumentUse is null)
        {
            tileWork.Dispose();
            return false;
        }

        RetirableCancellationTokenSource workCancellation;
        var tileDemand = new MarkdownSvgRenderDemand(priority);
        try
        {
            workCancellation = new RetirableCancellationTokenSource();
        }
        catch
        {
            tileDocumentUse.Dispose();
            tileWork.Dispose();
            throw;
        }
        var workOwner = new SvgTileWorkOwner(plan, workCancellation, tileDemand);
        bool admitted;
        try
        {
            admitted = _cache.TryAdmitTileWork(plan, key, workOwner);
        }
        catch
        {
            try { _host.TryImageCleanup(workCancellation.Dispose); }
            finally
            {
                tileDocumentUse.Dispose();
                tileWork.Dispose();
            }
            throw;
        }
        if (!admitted)
        {
            try
            {
                _host.TryImageCleanup(workCancellation.Dispose);
            }
            finally
            {
                tileWork.Dispose();
                tileDocumentUse.Dispose();
            }
            stop = false;
            return false;
        }
        ScheduleSvgTileWork(plan, key, workOwner, tileWork, tileDocumentUse);
        stop = false;
        return true;
    }

    // Only admitted work allocates the asynchronous delegate. Keeping both the
    // scheduling and cleanup closures out of QueueSvgTileRange also prevents
    // display-class allocation for tiles already present or in flight.
    private void ScheduleSvgTileWork(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        ImageWorkLease lifetimeWork,
        SvgDocumentUseLease documentUse)
    {
        try
        {
            // Do not pass the token to Task.Run: even canceled work must enter
            // RenderSvgTileAsync's finally and drain its document/work leases.
            _ = Task.Run(() => RenderSvgTileAsync(
                plan, key, workOwner, lifetimeWork, documentUse));
        }
        catch
        {
            _cache.AbandonUnscheduledTileWork(key, workOwner);
            try
            {
                try { _ = RetireTileCancellationSource(workOwner.Cancellation); }
                catch (Exception exception) { _host.RecordImageRetirementFailure(exception); }
            }
            finally
            {
                documentUse.Dispose();
                lifetimeWork.Dispose();
            }
            throw;
        }
    }

    // This method stays whole because its provider admission, decode gate,
    // bitmap reservation, owned bitmap and lease are taken in sequence and all
    // released by one finally; splitting it would pass them between methods by
    // ref and scatter that release order.
    private async Task RenderSvgTileAsync(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        ImageWorkLease lifetimeWork,
        SvgDocumentUseLease documentUse)
    {
        using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope();
        long svgWorkId = svgWorkScope.WorkId;
        long svgSourceBytes = _host.SvgSourceByteLength;
        long targetPixels = (long)key.Width * key.Height;
        string identity = $"{plan.IdentityPrefix}:tile={key.X},{key.Y},{key.Width},{key.Height}";
        RetirableCancellationTokenSource workCancellation = workOwner.Cancellation;
        SharedCanvasBitmapCache.Lease? bitmapLease = null;
        BitmapOutputAdmission.Reservation? bitmapReservation = null;
        CanvasBitmap? ownedBitmap = null;
        SemaphoreSlim? renderGate = null;
        bool renderGateHeld = false;
        bool providerAdmissionHeld = false;
        RetirableCancellationTokenSource? admissionCancellation = null;

        try
        {
            if (!_cache.IsCurrentSvgTilePlan(plan) || !_host.IsCurrentSvgRendererState())
                return;
            admissionCancellation = RetirableCancellationTokenSource.CreateLinked(
                lifetimeWork.Token,
                workCancellation.Token);
            bool providerOwnsAdmission = plan.HostAdmission is not null &&
                plan.Document is IMarkdownSvgPrioritizedDocument;
            if (!providerOwnsAdmission)
            {
                await _resources.SvgProviderAdmission.WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
                providerAdmissionHeld = true;
            }
            CancellationToken cancellationToken = admissionCancellation.Token;
            if (!_cache.IsCurrentSvgTilePlan(plan) || !_host.IsCurrentSvgRendererState())
                return;
            BitmapOutputWorkContext bitmapOutputWork = _outputAdmission.CaptureBitmapOutputWorkContext(
                cancellationToken, isExistingSvgTileOutput: true, expectedDemand: plan.Demand,
                expectedSvgGeneration: plan.RenderGeneration);
            CanvasDevice device = _host.SvgTileDevice;
            if (!_outputAdmission.TryAcquireAdmittedSvgBitmap(device, identity, out bitmapLease, isExistingSvgTileOutput: true,
                    expectedWork: bitmapOutputWork))
            {
                if (_outputDeferral.IsBitmapOutputDeferred)
                    return;
                renderGate = _resources.BitmapCache.GetDecodeGate(device, identity);
                await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                renderGateHeld = true;

                _ = _outputAdmission.TryAcquireAdmittedSvgBitmap(device, identity, out bitmapLease, isExistingSvgTileOutput: true,
                    expectedWork: bitmapOutputWork);
                if (bitmapLease is null)
                {
                    if (_outputDeferral.IsBitmapOutputDeferred ||
                        !_outputAdmission.TryReserveBitmapOutput(checked((long)key.Width * key.Height * 4), out bitmapReservation,
                            isExistingSvgTileOutput: true, expectedWork: bitmapOutputWork))
                        return;
                    MarkdownSvgRaster raster = await RenderTileRasterAsync(
                            plan, key, workOwner, documentUse, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        if (!_cache.IsCurrentSvgTilePlan(plan) || !_host.IsCurrentSvgRendererState())
                        {
                            return;
                        }
                        bitmapLease = UploadTile(
                            raster,
                            key,
                            device,
                            identity,
                            svgWorkId,
                            svgSourceBytes,
                            targetPixels,
                            ref ownedBitmap,
                            ref bitmapReservation);
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
                    "The SVG tile cache did not return a lease.");
            bitmapLease = null;
            PublishTileOnUiThread(plan, key, workCancellation, publishedLease);
        }
        catch (OperationCanceledException) when (
            lifetimeWork.Token.IsCancellationRequested ||
            workCancellation.IsCancellationRequested)
        {
            // The box or this tile's work was canceled; the finally retires it.
        }
        catch (OperationCanceledException)
        {
            // A provider-generation transition can cancel work without
            // canceling this box. Leave the tile absent so the next paint can
            // request it against the current generation.
        }
        catch (MarkdownSvgException ex) when (
            ex.Reason == MarkdownSvgFailureReason.Canceled &&
            (lifetimeWork.Token.IsCancellationRequested ||
             workCancellation.IsCancellationRequested))
        {
            // The provider saw the same cancellation; the finally retires it.
        }
        catch (MarkdownSvgException ex) when (ex.Reason == MarkdownSvgFailureReason.Canceled)
        {
            // A disposed or invalidated document is retryable against the
            // replacement document and is not an unavailable image.
        }
        catch (MarkdownSvgException ex)
        {
            _host.PublishSvgTileFailure(plan, key, workOwner, ex);
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG tile upload deferred after graphics device loss: {ex.Message}");
        }
        catch (Exception ex)
        {
            _host.PublishSvgTileFailure(
                plan,
                key,
                workOwner,
                new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "The SVG tile renderer failed safely.",
                    ex));
        }
        finally
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            await RetireTileRenderAsync(
                    key,
                    workCancellation,
                    bitmapLease,
                    renderGateHeld ? renderGate : null,
                    providerAdmissionHeld,
                    admissionCancellation,
                    documentUse,
                    lifetimeWork)
                .ConfigureAwait(false);
        }
    }

    // Renders one tile region of the plan's physical grid at the work's priority.
    private Task<MarkdownSvgRaster> RenderTileRasterAsync(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        SvgDocumentUseLease documentUse,
        CancellationToken cancellationToken) =>
        _host.RenderSvgDocumentAsync(
            plan.Document,
            new MarkdownSvgRenderRequest(
                plan.FullWidthPixels,
                plan.FullHeightPixels,
                new MarkdownSvgTileRegion(
                    key.X,
                    key.Y,
                    key.Width,
                    key.Height),
                MarkdownSvgPixelFormat.Rgba8Premultiplied,
                workOwner.Demand.Priority)
            { PhysicalViewport = plan.PhysicalViewport },
            cancellationToken,
            documentUse,
            workOwner.Demand);

    // Validates the raster, uploads it, and stores the bitmap in the shared
    // cache. On failure the admitted bitmap and its reservation are released.
    private SharedCanvasBitmapCache.Lease? UploadTile(
        MarkdownSvgRaster raster,
        SvgTileKey key,
        CanvasDevice device,
        string identity,
        long svgWorkId,
        long svgSourceBytes,
        long targetPixels,
        ref CanvasBitmap? ownedBitmap,
        ref BitmapOutputAdmission.Reservation? bitmapReservation)
    {
        SvgRasterUpload.Validate(raster, key.Width, key.Height);
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
        try
        {
            using var stageTrace = MarkdownSvgStageTrace.Measure(
                svgWorkId,
                MarkdownSvgWorkStage.BitmapCacheStore,
                svgSourceBytes,
                targetPixels);
            SharedCanvasBitmapCache.Lease? lease = _resources.BitmapCache.StoreSvgAndAcquire(
                device,
                identity,
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
            return lease;
        }
        catch
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            throw;
        }
    }

    // Installs the rendered lease on the UI thread if its work still owns the
    // tile; a rejected or dropped publication releases the untransferred lease.
    private void PublishTileOnUiThread(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        RetirableCancellationTokenSource workCancellation,
        SharedCanvasBitmapCache.Lease publishedLease)
    {
        SvgTilePublicationOwnership publicationOwnership = default;
        void ReleaseUntransferredLease()
        {
            if (publicationOwnership.TryReleaseUntransferred())
                _host.TryImageCleanup(publishedLease.Dispose);
        }

        _host.PublishOnUiThread(
            () =>
            {
                SharedCanvasBitmapCache.Lease? previous = null;
                bool accepted = false;
                bool transferred = false;
                try
                {
                    // CacheGeneration is supplied by an external provider.
                    // Snapshot it outside our lock; recheck only local plan
                    // and request ownership while mutating the tile map.
                    bool rendererCurrent = _host.IsCurrentSvgRendererState();
                    accepted = _cache.TryPublishTile(
                        plan,
                        key,
                        workCancellation,
                        rendererCurrent,
                        publishedLease,
                        ref publicationOwnership,
                        ref previous,
                        ref transferred);
                }
                finally
                {
                    if (previous is { } replacedTile)
                        _host.TryImageCleanup(replacedTile.Dispose);
                    if (!transferred)
                        ReleaseUntransferredLease();
                }

                if (!accepted || !transferred)
                    return;

                _cache.MarkTileRendered();
                _host.OnSvgTilePublished();
            },
            ReleaseUntransferredLease);
    }

    // Ends a tile render: requests cancellation of its work source, releases
    // the decode gate and provider admission it holds, completes its tile
    // work, and finally releases its document use and image-work lease.
    private async Task RetireTileRenderAsync(
        SvgTileKey key,
        RetirableCancellationTokenSource workCancellation,
        SharedCanvasBitmapCache.Lease? bitmapLease,
        SemaphoreSlim? heldRenderGate,
        bool providerAdmissionHeld,
        RetirableCancellationTokenSource? admissionCancellation,
        SvgDocumentUseLease documentUse,
        ImageWorkLease lifetimeWork)
    {
        Task cancellationCallbacks = Task.CompletedTask;
        try
        {
            cancellationCallbacks = RetireTileCancellationSource(workCancellation);
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
        }

        try
        {
            if (bitmapLease is { } abandonedLease)
                _host.TryImageCleanup(abandonedLease.Dispose);
            if (heldRenderGate is not null)
                _host.TryImageCleanup(() => heldRenderGate.Release());
            if (providerAdmissionHeld)
                _host.TryImageCleanup(() => _resources.SvgProviderAdmission.Release());
            _host.TryImageCleanup(() => _cache.CompleteSvgTileWork(key, workCancellation));
        }
        finally
        {
            try
            {
                try
                {
                    await cancellationCallbacks.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _host.RecordImageRetirementFailure(exception);
                }
            }
            finally
            {
                try
                {
                    if (admissionCancellation is not null)
                        _host.TryImageCleanup(admissionCancellation.Dispose);
                }
                finally
                {
                    documentUse.Dispose();
                    // This parent work lease is the snapshot's proof that the tile
                    // operation has finished. Release it even if a cleanup action
                    // fails or an admission gate was already invalidated.
                    lifetimeWork.Dispose();
                }
            }
        }
    }

    internal Task RetireTileCancellationSource(RetirableCancellationTokenSource source)
    {
        Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(source);

        // Track callback faults and disposal completion independently. The
        // shared helper disposes only after its callback task; tracking both
        // ensures ImageBox retirement cannot succeed until neither can still
        // access the source or invoke a registration.
        _host.TrackDetachedImageWork(callbacks, failRetirementOnFault: true);
        Task sourceRetirement = CancellationTokenSourceRetirement.DisposeAfter(source, callbacks);
        _host.TrackDetachedImageWork(sourceRetirement, failRetirementOnFault: true);
        return callbacks;
    }
}
