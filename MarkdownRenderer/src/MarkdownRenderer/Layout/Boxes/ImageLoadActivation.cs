using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Starts an image box's loads: the synchronous cache probe at construction,
/// viewport-driven activation, each load's demand ticket and generation,
/// and the dispatch to the data-URI, resolver or built-in source path. Also
/// the box's side of its bitmap-output and SVG host-capacity deferrals,
/// which restart or reset a load.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because activation, its resets and the
/// box's side of both deferrals all keep the one demand-ticket rule below;
/// splitting them would spread that lock rule across types. It splits once
/// the deferral callbacks own their tickets.
///
/// A load's demand ticket and generation change only under the SVG host
/// deferral lock, after the load's setup work unit is admitted, so a late
/// reset can never clear a newer activation even when a resumed box reuses
/// its ticket. Lock order: image-work lock, then the deferral lock.
/// </remarks>
internal sealed class ImageLoadActivation : IImageBitmapOutputHost, ISvgHostDeferralHost
{
    private readonly ImageBox _box;

    internal ImageLoadActivation(ImageBox box) => _box = box;

    private ImageWorkLifetime Lifetime => _box.Lifetime;

    private ImageLoadState Load => _box.LoadState;

    private SvgHostLoadDeferral HostDeferral => _box.SvgHostDeferral;

    /// <summary>
    /// The cache identity usable before host resolution, or null when only
    /// the resolver may name it. A host resolver may apply authentication and
    /// account partitioning, so its source URL is not a safe process-wide
    /// cache identity.
    /// </summary>
    internal static string? GetPreResolutionCacheKey(
        MarkdownLayoutContext context,
        string url,
        MarkdownBuiltInImageSource? builtInSource)
    {
        bool canUseSourceBeforeResolution =
            MarkdownImageCacheIdentityPolicy.CanUseSourceBeforeResolution(
                context.ImageResolver is not null,
                url);
        if (canUseSourceBeforeResolution &&
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return builtInSource?.CacheKey ?? url;
        }

        return canUseSourceBeforeResolution && builtInSource is { CanLoadWithoutResolver: true } source
            ? source.CacheKey
            : null;
    }

    /// <summary>
    /// Adopts what the caches already know at construction: a remembered
    /// failure, a completed result carried from the previous layout, or a
    /// device bitmap of the exact source, so the first paint needs no load.
    /// </summary>
    internal void AdoptCachedResult(string? preResolutionCacheKey)
    {
        ImageResourceScope resources = _box.Resources;
        if (preResolutionCacheKey is { } cacheKey && resources.Failures.HasFailedUrl(cacheKey))
        {
            // Preserve transport/bitmap failure state across rebuilds. SVG
            // provider failures use a separate renderer-and-generation-scoped
            // cache after the bytes have been securely preflighted.
            Load.IsFailed = true;
            Load.IsFailureCarryable = true;
            Load.IsStarted = true;
            return;
        }

        if (TryAdoptCarriedCompletion())
            return;

        if (preResolutionCacheKey is { } bitmapCacheKey && !Load.IsSvg &&
            _box.OutputAdmission.TryAcquireSourceSizedRasterBitmap(bitmapCacheKey, out var cachedLease) &&
            cachedLease is not null)
        {
            _box.Presentation.Replace(cachedLease.Bitmap, cachedLease);
            Load.IsStarted = true;
            return;
        }

        if (preResolutionCacheKey is { } svgCacheKey && resources.SvgSources.TryGetValue(svgCacheKey, out _))
            Load.IsSvg = true;

        if (Load.IsSvg && preResolutionCacheKey is { } cachedSvgKey &&
            resources.SvgSources.TryGetValue(cachedSvgKey, out var entry))
        {
            AdoptCachedSvg(entry);
        }
    }

    // A rebuild of the same document keeps this image's completed result, so
    // it neither reports Loading nor reserves a placeholder size while its
    // pixels are reacquired. Bitmaps are never carried. Returns true when a
    // carried permanent failure ended construction.
    private bool TryAdoptCarriedCompletion()
    {
        if (_box.Context.CompletedImageCarry is not { } completedImages ||
            !completedImages.TryGetValue(_box.Url, out CompletedImageCarry? carried))
        {
            return false;
        }

        if (carried.Failed)
        {
            // Show the same permanent failure as the earlier
            // layout instead of reporting Loading until it fails again.
            Load.IsSvg = carried.IsSvg;
            if (carried.IsSvg)
                _box.Svg.AdoptCarriedFailure(carried.IntrinsicSize, carried.SvgFailureReason, carried.SvgFailureDescription);
            else
            {
                _box.Presentation.RasterIntrinsicSize = carried.IntrinsicSize;
                _box.Svg.AdoptCarriedFailure(null, carried.SvgFailureReason, carried.SvgFailureDescription);
            }
            Load.IsFailed = true;
            Load.IsStarted = true;
            Load.IsFailureCarryable = true;
            return true;
        }

        if (carried.HasUsableIntrinsicSize)
            Load.CarriedCompletion = carried;
        return false;
    }

    // SVG cache hit path. If the cached rasterized bitmap was produced with
    // the current theme color + device pixel scale, materialize it
    // synchronously so the very first paint shows the image: no async pass
    // through ProcessCachedSvgAsync, no placeholder flash.
    private void AdoptCachedSvg(SvgCacheEntry entry)
    {
        bool compatible = _box.SvgCachedPublisher.IsCacheEntryCurrent(entry);
        if (compatible)
            _box.Svg.AdoptCacheEntry(entry);

        // Retain the prior good device bitmap while an exact DPI, theme,
        // or size replacement is prepared. Paint scales this immutable
        // fallback and swaps it only after the new lease is ready.
        if (compatible &&
            entry.LastBitmapIdentity is { } previousIdentity &&
            _box.Resources.BitmapCache.TryAcquire(
                _box.Context.ResourceCreator.Device,
                previousIdentity,
                out SharedCanvasBitmapCache.Lease? previousLease) &&
            previousLease is not null)
        {
            if (_box.OutputAdmission.TryAdmitCachedBitmapOutput(previousLease))
            {
                _box.Presentation.Replace(previousLease.Bitmap, previousLease, entry.LastPhysicalViewport);
                _box.Svg.AdoptCachedBitmapGeometry(
                    entry.LastBitmapWidthPixels, entry.LastBitmapHeightPixels, entry.LastPhysicalViewport);
            }
            else
                Lifetime.TryCleanup(previousLease.Dispose);
        }
    }

    /// <summary>
    /// Starts the load if it has not started, or promotes and resumes the
    /// current or deferred one. A failed box only reports its failure; a
    /// pressure-parked tiled image keeps its good pixels until the
    /// geometry-aware paint path finds a missing visible tile.
    /// </summary>
    internal void EnsureLoading(bool isVisible)
    {
        if (Load.IsFailed)
        {
            _box.FailurePublisher.ReportUnreportedFailure();
            return;
        }

        if (_box.Svg.IsPressureParked)
            return;

        if (_box.OutputDeferral.ObserveDeferredBitmapOutput(isVisible))
            return;

        if (Load.IsStarted)
        {
            MarkdownSvgRenderDemand? currentDemand = Load.Demand;
            if (isVisible)
                currentDemand?.PromoteVisible();
            HostDeferral.TryResumeDeferredSvgLoadForObservedDemandOrCapacity(currentDemand);
            return;
        }

        if (HostDeferral.IsDeferred &&
            Load.Demand is { } deferredDemand)
        {
            if (isVisible)
                deferredDemand.PromoteVisible();
            HostDeferral.TryResumeDeferredSvgLoadForObservedDemandOrCapacity(deferredDemand);
            return;
        }

        StartLoad(isVisible: isVisible);
    }

    /// <summary>
    /// Reopens a pressure-parked SVG that a visible paint needs: the deferred
    /// load is resumed, or a not-started load restarts under its ticket.
    /// </summary>
    internal void ReopenPressureParkedSvgForVisibleDemand(long visualDemandEpoch)
    {
        MarkdownSvgRenderDemand? demand = Load.Demand;
        if (demand is null)
            return;
        demand.PromoteVisible();

        if (HostDeferral.IsDeferred)
        {
            HostDeferral.TryResumeDeferredSvgLoadForObservedDemandOrCapacity(demand);
            HostDeferral.TryQueueDeferredSvgHostRetry(
                demand,
                Load.DemandGeneration,
                observedDemandVersion: 0,
                observedCapacityVersion: 0,
                observedOwnerVersion: 0,
                observedVisualDemandEpoch: visualDemandEpoch);
            return;
        }

        if (!Load.IsStarted)
        {
            StartLoad(
                isVisible: true,
                demandOverride: demand,
                loadDemandGeneration: Load.DemandGeneration);
        }
    }

    /// <summary>
    /// Starts a load under a new demand ticket, or under
    /// <paramref name="demandOverride"/> while it and
    /// <paramref name="loadDemandGeneration"/> are still current.
    /// </summary>
    internal void StartLoad(
        bool isVisible = true,
        MarkdownSvgRenderDemand? demandOverride = null,
        int loadDemandGeneration = 0)
    {
        if (!Lifetime.TryBeginWork(out ImageWorkLease? setupWork) || setupWork is null)
            return;

        using ImageWorkLease loadSetup = setupWork;
        _box.OutputDeferral.ClearBitmapOutputDeferral(preserveRetryOffer: demandOverride is not null);
        if (!TryBeginDemand(isVisible, demandOverride, ref loadDemandGeneration, out MarkdownSvgRenderDemand? demand) ||
            demand is null)
        {
            return;
        }

        string url = _box.Url;
        if (string.IsNullOrEmpty(url)) { Load.IsFailed = true; return; }

        // A source-cache hit can often also hit the exact device bitmap cache.
        // This path is synchronous and never wakes the worker.
        if (Load.IsSvg)
        {
            if (_box.SvgCachedPublisher.TryPublish(layoutInvalidated: false,
                    expectedWork: _box.OutputAdmission.CaptureBitmapOutputWorkContext(loadSetup.Token, expectedDemand: demand)))
                return;
        }

        // SVG data URIs (data:image/svg+xml,...) may contain raw < > characters
        // that are illegal in RFC 3986, which causes Uri.TryCreate to return
        // false. Parse them directly from the raw URL string.
        if (Load.IsSvg && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            _ = _box.SourceLoader.LoadSvgDataUriAsync(url, demand, loadDemandGeneration);
            return;
        }

        if (_box.Context.ImageResolver is { } resolver)
        {
            StartResolvedLoad(resolver, demand, loadDemandGeneration);
            return;
        }

        if (_box.BuiltInSource is not { } source)
        {
            _box.FailurePublisher.PublishFailure(cacheKey: string.Empty);
            return;
        }

        _box.SourceLoader.LoadFromDefaultSource(source, demand, loadDemandGeneration);
    }

    // Under the deferral lock: a new load takes a fresh ticket and clears a
    // deferred waiter; a restart keeps its ticket only while it is current.
    private bool TryBeginDemand(
        bool isVisible,
        MarkdownSvgRenderDemand? demandOverride,
        ref int loadDemandGeneration,
        out MarkdownSvgRenderDemand? demand)
    {
        IDisposable? previousDeferredWaiter = null;
        lock (HostDeferral.Sync)
        {
            if (demandOverride is null)
            {
                previousDeferredWaiter = HostDeferral.ClearSvgHostDeferralNoLock();
                demand = new MarkdownSvgRenderDemand(isVisible
                    ? MarkdownSvgRenderPriority.Visible
                    : MarkdownSvgRenderPriority.Overscan);
                loadDemandGeneration = Load.BeginDemand(demand);
            }
            else
            {
                if (!ReferenceEquals(Load.Demand, demandOverride) ||
                    loadDemandGeneration != Load.DemandGeneration)
                {
                    demand = null;
                    return false;
                }
                demand = demandOverride;
            }
            Load.IsStarted = true;
            Load.ResetUnavailableReport();
        }
        previousDeferredWaiter?.Dispose();
        return true;
    }

    // Opt-in prefetch already calls this resolver off the UI thread. A
    // visible source-cache hit can otherwise run its synchronous resolver
    // prefix inside snapshot commit. Start it on the session's bounded
    // activation lane rather than waiting behind ThreadPool work.
    private void StartResolvedLoad(
        IMarkdownImageResolver resolver,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        if (_box.Context.PerformanceSession is not { } performanceSession)
        {
            _ = _box.SourceLoader.ResolveAndLoadAsync(resolver, demand, loadDemandGeneration);
            return;
        }

        if (!Lifetime.TryBeginWork(out ImageWorkLease? queuedWork) || queuedWork is null)
            return;

        bool scheduled;
        try
        {
            scheduled = performanceSession.TryScheduleImageActivation(
                demand,
                () =>
                {
                    Task resolution = _box.SourceLoader.ResolveAndLoadAsync(resolver, demand, loadDemandGeneration);
                    queuedWork.Dispose();
                    ObserveImageActivationTask(resolution);
                },
                () =>
                {
                    ResetLoadStartedAfterUnstartedActivation(
                        demand,
                        loadDemandGeneration,
                        queuedWork.Token);
                    queuedWork.Dispose();
                },
                queuedWork.Token);
        }
        catch
        {
            ResetLoadStartedAfterUnstartedActivation(
                demand,
                loadDemandGeneration,
                queuedWork.Token);
            queuedWork.Dispose();
            throw;
        }
        if (!scheduled)
        {
            ResetLoadStartedAfterUnstartedActivation(
                demand,
                loadDemandGeneration,
                queuedWork.Token);
            queuedWork.Dispose();
        }
    }

    /// <summary>
    /// Returns a load whose queued activation never ran to not-started, while
    /// its exact ticket, generation and activation token are current.
    /// </summary>
    /// <remarks>
    /// The volatile lifecycle flags are checked alongside the exact load
    /// identity under the activation gate. A replacement must take the same
    /// gate before it can start again, so a late callback cannot clear a
    /// newer activation even if a resumed box reuses its ticket; the old
    /// residency token is canceled before that callback can reset.
    /// </remarks>
    internal void ResetLoadStartedAfterUnstartedActivation(
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        CancellationToken activationCancellation)
    {
        lock (HostDeferral.Sync)
        {
            if (Lifetime.IsDisposed || Lifetime.IsSuspended ||
                !ReferenceEquals(Load.Demand, demand) ||
                loadGeneration != Load.DemandGeneration ||
                activationCancellation.IsCancellationRequested)
            {
                return;
            }

            Load.IsStarted = false;
        }
    }

    private static void ObserveImageActivationTask(Task operation)
    {
        if (operation.IsCompleted)
        {
            ReportImageActivationFault(operation);
            return;
        }

        _ = operation.ContinueWith(
            ReportImageActivationFault,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ReportImageActivationFault(Task operation)
    {
        if (operation.Exception is { } exception)
        {
            Exception failure = exception.GetBaseException();
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] image activation failed: {failure.GetType().Name} {GraphicsDeviceErrors.FormatHResult(failure.HResult)}");
        }
    }

    MarkdownSvgRenderDemand? IImageBitmapOutputHost.LoadDemand => Load.Demand;
    int IImageBitmapOutputHost.LoadDemandGeneration => Load.DemandGeneration;
    int IImageBitmapOutputHost.SvgRenderGeneration => _box.Svg.RenderGeneration;
    bool IImageBitmapOutputHost.IsViewportVisible => _box.SvgTileCache.IsViewportVisible;
    bool IImageBitmapOutputHost.IsRetired => Lifetime.IsRetired;

    bool IImageBitmapOutputHost.IsImageWorkDrained
    {
        get { lock (Lifetime.Sync) return Lifetime.WorkCountNoLock == 0 && !Lifetime.IsDisposed && !Lifetime.IsSuspended; }
    }

    bool IImageBitmapOutputHost.HasNoImageWork
    {
        get { lock (Lifetime.Sync) return Lifetime.WorkCountNoLock == 0; }
    }

    bool IImageBitmapOutputHost.HasRetainedBitmapOutput(bool existingTileOutput) =>
        _box.Presentation.HasRetainedBitmap ||
        _box.SvgTileCache.HasRetainedTileOutput(existingTileOutput);

    void IImageBitmapOutputHost.RecordImageRetirementFailure(Exception exception) =>
        Lifetime.RecordFailure(exception);
    void IImageBitmapOutputHost.TryImageCleanup(Action cleanup) => Lifetime.TryCleanup(cleanup);
    void IImageBitmapOutputHost.RaiseLoadCompleted(bool layoutInvalidated) => _box.RaiseLoadCompleted(layoutInvalidated);
    void IImageBitmapOutputHost.RaiseBitmapOutputRecoverySignal() => _box.RaiseBitmapOutputRecoverySignal();

    void IImageBitmapOutputHost.StartLoad(
        bool isVisible, MarkdownSvgRenderDemand? demandOverride, int loadDemandGeneration) =>
        StartLoad(isVisible, demandOverride, loadDemandGeneration);

    bool IImageBitmapOutputHost.TryResetLoad(MarkdownSvgRenderDemand demand, int generation, int svgGeneration)
    {
        lock (HostDeferral.Sync)
        {
            if (Lifetime.IsDisposed || Lifetime.IsSuspended ||
                !ReferenceEquals(Load.Demand, demand) ||
                generation != Load.DemandGeneration ||
                svgGeneration != _box.Svg.RenderGeneration)
            {
                return false;
            }

            Load.IsStarted = false;
            return true;
        }
    }

    MarkdownSvgRenderDemand? ISvgHostDeferralHost.LoadDemand => Load.Demand;
    int ISvgHostDeferralHost.LoadDemandGeneration => Load.DemandGeneration;
    long ISvgHostDeferralHost.SvgHostVisualDemandEpoch => _box.Svg.VisualDemandEpoch;
    void ISvgHostDeferralHost.MarkLoadNotStarted() => Load.IsStarted = false;

    void ISvgHostDeferralHost.StartLoad(
        bool isVisible, MarkdownSvgRenderDemand? demandOverride, int loadDemandGeneration) =>
        StartLoad(isVisible, demandOverride, loadDemandGeneration);

    bool ISvgHostDeferralHost.TryCaptureSvgHostDeferralCancellation(
        SvgHostCapacityDeferral deferral, out CancellationToken cancellationToken)
    {
        lock (Lifetime.Sync)
        {
            if (Lifetime.IsDisposed || Lifetime.IsSuspended ||
                !ReferenceEquals(Load.Demand, deferral.Demand) ||
                deferral.LoadGeneration != Load.DemandGeneration)
            {
                cancellationToken = default;
                return false;
            }

            cancellationToken = Lifetime.CurrentWorkTokenNoLock;
            return true;
        }
    }
}
