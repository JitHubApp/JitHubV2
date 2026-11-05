using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.MarkdownSvgPhysicalViewport PhysicalViewport);
using SvgTileKey = (int X, int Y, int Width, int Height);

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// An image box's side of its SVG tile cache and tile renderer: the accepted
/// tile plan and parking state they read, the work, document uses and
/// uploads they need, and the publication of a rendered tile or a terminal
/// tile failure.
/// </summary>
/// <remarks>
/// A terminal tile failure detaches its exact accepted plan under the
/// publication lock, so an old callback can never restore or clear tile
/// state after a replacement commits. The accepted plan remains usable
/// while a newer replacement is pending, and the failure never cancels the
/// publication epoch of that replacement.
/// </remarks>
internal sealed class SvgTileHost : ISvgTileRendererHost
{
    private readonly ImageBox _box;

    internal SvgTileHost(ImageBox box) => _box = box;

    private ImageWorkLifetime Lifetime => _box.Lifetime;

    private SvgImageState Svg => _box.Svg;

    private SvgTileCache Cache => _box.SvgTileCache;

    SvgTileRenderPlan? ISvgTileCacheHost.AcceptedSvgTilePlan => Svg.AcceptedPlan;
    SvgTileRenderPlan? ISvgTileCacheHost.SvgParkingPlan => Svg.ParkingPlan;
    bool ISvgTileCacheHost.IsSvgPressureParked => Svg.IsPressureParked;
    long ISvgTileCacheHost.SvgHostVisualDemandEpoch => Svg.VisualDemandEpoch;
    long ISvgTileCacheHost.AdvanceSvgHostVisualDemandEpoch() => Svg.AdvanceVisualDemandEpoch();
    bool ISvgTileCacheHost.IsAcceptedSvgTilePlan(SvgTileRenderPlan plan) => Svg.IsAcceptedTilePlan(plan);
    void ISvgTileCacheHost.PublishOnUiThread(Action publish, Action? onDropped) => _box.PublishOnUiThread(publish, onDropped);
    void ISvgTileCacheHost.TryImageCleanup(Action cleanup) => Lifetime.TryCleanup(cleanup);
    void ISvgTileCacheHost.RecordImageRetirementFailure(Exception exception) => Lifetime.RecordFailure(exception);

    // A hole in the tile grid with no fallback tile shows the previous whole
    // bitmap, then the placeholder. The tile cache holds its lock throughout.
    void ISvgTileCacheHost.FillSvgTileGapWithoutTiles(CanvasDrawingSession ds, Rect destination)
    {
        if (_box.Presentation.Bitmap is { } previousBitmap)
        {
            _box.Painter.PaintWholeBitmap(ds, previousBitmap, destination);
        }
        else
        {
            _box.Placeholder.PaintPlaceholder(ds, destination);
        }
    }

    long ISvgTileRendererHost.SvgSourceByteLength => Svg.SourceByteLength;
    CanvasDevice ISvgTileRendererHost.SvgTileDevice => _box.Context.ResourceCreator.Device;
    bool ISvgTileRendererHost.IsCurrentSvgRendererState() => _box.SvgCachedPublisher.IsCurrentRendererState();
    SvgRasterPlan ISvgTileRendererHost.PickCurrentSvgRasterDimensions() =>
        _box.SvgRasterPlanner.PickRasterDimensions(Svg.IntrinsicSize);
    void ISvgTileRendererHost.ReopenPressureParkedSvgForVisibleDemand(long visualDemandEpoch) =>
        _box.Activation.ReopenPressureParkedSvgForVisibleDemand(visualDemandEpoch);
    bool ISvgTileRendererHost.TryBeginImageWork(out ImageWorkLease? work) => Lifetime.TryBeginWork(out work);
    SvgDocumentUseLease? ISvgTileRendererHost.TryAcquireSvgDocumentUse(IMarkdownSvgDocument document) =>
        _box.SvgDocuments.TryAcquireUse(document);
    Task<MarkdownSvgRaster> ISvgTileRendererHost.RenderSvgDocumentAsync(
        IMarkdownSvgDocument document,
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken,
        SvgDocumentUseLease parentUse,
        MarkdownSvgRenderDemand? demand) =>
        _box.SvgDocuments.RenderAsync(document, request, cancellationToken, parentUse, demand);
    CanvasBitmap ISvgTileRendererHost.CreateBitmapFromSvgRaster(
        MarkdownSvgRaster raster,
        long diagnosticWorkId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels,
        BitmapOutputAdmission.Reservation? bitmapReservation) =>
        SvgRasterUpload.CreateBitmap(
            _box.Context.ResourceCreator, raster, diagnosticWorkId, diagnosticSourceBytes, diagnosticTargetPixels,
            bitmapReservation: bitmapReservation);
    void ISvgTileRendererHost.TrackDetachedImageWork(Task detachedOperation, bool failRetirementOnFault) =>
        Lifetime.TrackDetachedWork(detachedOperation, failRetirementOnFault);

    void ISvgTileRendererHost.OnSvgTilePublished()
    {
        _box.Presentation.HasPresentedImage = true;
        Svg.ClearFailure();
        _box.LoadState.IsFailed = false;
        _box.RaiseLoadCompleted(layoutInvalidated: false);
    }

    void ISvgTileRendererHost.PublishSvgTileFailure(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        MarkdownSvgException exception) =>
        PublishSvgTileFailure(plan, key, workOwner, exception);

    /// <summary>
    /// Publishes a terminal failure of one tile of <paramref name="plan"/>:
    /// the plan and its document are detached, and the box fails unless a
    /// previous whole bitmap or fallback tile remains.
    /// </summary>
    internal void PublishSvgTileFailure(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        MarkdownSvgException exception)
    {
        if (!Cache.TryClaimFailedSvgTile(plan, key, workOwner))
            return;

        MarkdownDiagnostics.WriteLine(
            $"[ImageBox] SVG tile failure {exception.Reason}: {exception.Message}");
        _box.PublishOnUiThread(() => ApplyTileFailure(plan, key, workOwner, exception));
    }

    private void ApplyTileFailure(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        MarkdownSvgException exception)
    {
        ImageWorkLease? preparedDocumentCleanup = null;
        try
        {
            if (!Cache.IsCurrentSvgTilePlan(plan) || !_box.SvgCachedPublisher.IsCurrentRendererState())
                return;

            SvgTileCacheTransition? tileTransition;
            ImageWorkLease? documentCleanup;
            bool loadFailed;
            lock (_box.SvgPublication.Sync)
            {
                if (!TryApplyFailureTransition(
                        plan, key, workOwner, exception, ref preparedDocumentCleanup,
                        out tileTransition, out documentCleanup, out loadFailed))
                {
                    return;
                }
            }

            if (documentCleanup is not null)
                _box.SvgDocuments.ScheduleDisposal(plan.Document, documentCleanup);

            Cache.RetireSvgTileCacheTransition(tileTransition!);
            try { plan.OwnerSlot?.Dispose(); }
            catch (Exception retirementFailure) { Lifetime.RecordFailure(retirementFailure); }

            if (loadFailed)
            {
                _box.FailurePublisher.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable, exception.Reason);
                _box.Placeholder.UpdateForFailure();
            }

            _box.RaiseLoadCompleted(layoutInvalidated: loadFailed);
        }
        finally
        {
            // A rejected/stale failure publication still releases the
            // reservation, but only after all publication locks have exited.
            preparedDocumentCleanup?.Dispose();
        }
    }

    // Caller holds the publication lock. Serializes failure invalidation and
    // its tile-map transition against publication. The cleanup lease is
    // reserved and the already-owned document state captured before the
    // exact tile owner is invalidated; if that allocation fails, the accepted
    // plan and document are untouched and the staged transition is aborted.
    private bool TryApplyFailureTransition(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        MarkdownSvgException exception,
        ref ImageWorkLease? preparedDocumentCleanup,
        out SvgTileCacheTransition? tileTransition,
        out ImageWorkLease? documentCleanup,
        out bool loadFailed)
    {
        documentCleanup = null;
        loadFailed = false;
        tileTransition = null;
        if (!Cache.IsCurrentSvgTilePlan(plan))
            return false;

        tileTransition = Cache.PrepareSvgTileFailureTransition(plan);
        if (tileTransition is null)
            return false;

        bool tileTransitionApplied = false;
        try
        {
            SvgDocumentUseState preparedDocumentState;
            lock (Lifetime.Sync)
            {
                if (!ReferenceEquals(_box.SvgDocuments.Current, plan.Document))
                    return false;
                preparedDocumentState = _box.SvgDocuments.GetUseStateNoLock(plan.Document);
                preparedDocumentCleanup = Lifetime.BeginWorkNoLock(CancellationToken.None);
            }

            if (!TryInvalidateFailedSvgTile(plan, key, workOwner, tileTransition.Token) ||
                !Cache.ApplySvgTileCacheTransition(tileTransition))
            {
                return false;
            }
            tileTransitionApplied = true;
            bool hasTileFallback = tileTransition.RestoreFallback;

            lock (Lifetime.Sync)
            {
                if (ReferenceEquals(_box.SvgDocuments.Current, plan.Document))
                {
                    documentCleanup = _box.SvgDocuments.RetireNoLock(
                        plan.Document,
                        preparedDocumentState,
                        ref preparedDocumentCleanup);
                    _box.SvgDocuments.SetCurrentNoLock(null);
                }

                bool hasPreviousGoodBitmap = _box.Presentation.Bitmap is not null || hasTileFallback;
                Svg.SetFailure(exception.Reason, hasPreviousGoodBitmap ? null : exception.Message);
                _box.LoadState.IsFailed = !hasPreviousGoodBitmap;
                _box.LoadState.IsFailureCarryable = false; // a tile render failure is not proven permanent
                loadFailed = _box.LoadState.IsFailed;
            }
            return true;
        }
        finally
        {
            if (!tileTransitionApplied)
                Cache.AbortSvgTileCacheTransition(tileTransition);
        }
    }

    /// <summary>
    /// Detaches <paramref name="plan"/> after its tile failed terminally,
    /// while the failed work still owns the tile. Claims terminal ownership
    /// under the tile lock, then invalidates under the work lock; state 3
    /// prevents the completion callback from removing or reassigning the map
    /// entry between those two locks.
    /// </summary>
    internal bool TryInvalidateFailedSvgTile(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        object? expectedTransitionToken)
    {
        if (!Cache.TryBeginFailedTileInvalidation(plan, key, workOwner))
            return false;

        lock (Lifetime.Sync)
        {
            if (Volatile.Read(ref workOwner.State) != 3 ||
                !ReferenceEquals(plan, workOwner.Plan) ||
                !Svg.IsAcceptedTilePlan(plan) ||
                !Cache.IsCurrentTransition(expectedTransitionToken))
            {
                return false;
            }

            if (expectedTransitionToken is null &&
                Cache.HasPendingTransition)
            {
                return false;
            }

            // The accepted plan remains usable while a newer replacement is
            // pending. A terminal failure detaches this exact plan, but must
            // not cancel the publication epoch of that replacement.
            Svg.AdvanceRenderGenerationIfCurrentNoLock(workOwner.Plan.RenderGeneration);
            Svg.SetUsesTilesNoLock(false);
            Svg.SetAcceptedPlanNoLock(null);
            Svg.SetParkingPlanNoLock(null);
            Svg.SetPressureParkedNoLock(false);
            return true;
        }
    }
}
