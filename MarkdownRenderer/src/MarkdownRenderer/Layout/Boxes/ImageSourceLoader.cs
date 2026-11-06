using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Fetches an image box's source and routes it into the load pipeline: the
/// host resolver (with its deadline and optional source lease), the built-in
/// data-URI, local and packaged sources, and the process-wide failure, raster
/// and SVG source caches. Resolved bytes go to the box's
/// <see cref="ImageRasterLoader"/> or, for SVG, through its
/// <see cref="SvgSourceOpener"/> and <see cref="SvgOpenedRenderer"/>. Source
/// ownership is kept through preparation and actual Open, then released before
/// source-free Render.
/// </summary>
internal sealed class ImageSourceLoader
{
    internal const int MaxRemoteImageBytes = RasterImageResourceBudget.MaxInputBytes;
    // Resolver implementations can have their own bounded network attempt and
    // then switch to a safe alternate representation (for example, from
    // GitHub Camo to the canonical HTTPS origin).  Keep this document-level
    // guard outside two 20-second transport attempts so it remains the final
    // safety net instead of racing the resolver's recovery path.
    internal static readonly TimeSpan ResolverTimeout = TimeSpan.FromSeconds(45);

    private readonly IImageSourceLoadHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputAdmission _outputAdmission;
    private readonly SvgHostLoadDeferral _svgHostDeferral;
    private readonly string _url;
    private readonly BuiltInImageSourceReader _reader;
    private readonly ImageRasterLoader _rasterLoader;
    private readonly SvgSourceOpener _svgOpener;
    private readonly SvgOpenedRenderer _svgRenderer;

    internal ImageSourceLoader(
        IImageSourceLoadHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        ImageBitmapOutputAdmission outputAdmission,
        ImageBitmapOutputDeferral outputDeferral,
        SvgHostLoadDeferral svgHostDeferral,
        string url,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight)
    {
        _host = host;
        _context = context;
        _resources = resources;
        _outputAdmission = outputAdmission;
        _svgHostDeferral = svgHostDeferral;
        _url = url;
        _reader = new BuiltInImageSourceReader(context, host);
        _rasterLoader = new ImageRasterLoader(
            host, context, resources, outputAdmission, outputDeferral, requestedWidth, requestedHeight);
        _svgOpener = new SvgSourceOpener(host, context, resources, outputAdmission, outputDeferral, url);
        _svgRenderer = new SvgOpenedRenderer(host, context, resources, outputAdmission, outputDeferral);
    }

    internal void LoadFromDefaultSource(
        MarkdownBuiltInImageSource source,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        switch (source.Kind)
        {
            case MarkdownBuiltInImageSourceKind.InsecureHttp:
                _host.ReportUnavailable(MarkdownImageUnavailableReason.InsecureRemoteContent);
                _host.PublishFailure(cacheKey: string.Empty);
                return;

            case MarkdownBuiltInImageSourceKind.RemoteHttps:
                // Network loading is resolver-owned so the host can apply consent,
                // authentication, account partitioning, and its own fetch cache.
                _host.ReportUnavailable(MarkdownImageUnavailableReason.RemoteContentBlocked);
                _host.PublishFailure(cacheKey: string.Empty);
                return;

            case MarkdownBuiltInImageSourceKind.Data:
            case MarkdownBuiltInImageSourceKind.Local:
                _ = LoadBuiltInImageAsync(source, _host.IsSvg, demand, loadDemandGeneration);
                return;

            default:
                _host.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
                _host.PublishFailure(cacheKey: string.Empty);
                return;
        }
    }

    internal async Task ResolveAndLoadAsync(
        IMarkdownImageResolver resolver,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        // Keep the document resolver and any returned-but-not-yet-rasterized
        // source inside this box's retirement barrier. The lifetime token is
        // linked to the host token, so replacement cancels either path.
        if (!_host.TryBeginImageWork(out ImageWorkLease? admittedWork) || admittedWork is null)
            return;

        using ImageWorkLease imageWork = admittedWork;
        CancellationToken lifetimeToken = imageWork.Token;
        BitmapOutputWorkContext bitmapOutputWork = _outputAdmission.CaptureBitmapOutputWorkContext(
            lifetimeToken, expectedDemand: demand);
        var resolveContext = new MarkdownImageResolveContext(
            _context.ImageBaseUri,
            _context.ImageDocumentPath,
            _context.AllowThirdPartyRemoteImages,
            _context.ImageDocumentSource);
        var state = new ImageResolveState(
            resolver, resolveContext, demand, loadDemandGeneration, lifetimeToken, bitmapOutputWork);
        if (!await TryResolveAsync(state).ConfigureAwait(false))
            return;

        // Keep original source ownership through preparation and actual Open,
        // then release it before source-free Render. Fetch slots are not bytes.
        state.ConsumerSourceLease = state.SourceLease;
        try
        {
            if (_host.IsDisposed)
                return;

            if (MarkdownImageCacheIdentityPolicy.CanUseSourceAfterResolution(state.Resolution))
            {
                LoadBuiltInFallback(state);
                return;
            }

            await LoadResolvedAssetAsync(state).ConfigureAwait(false);
        }
        finally
        {
            ReleaseResolvedSource(state);
        }
    }

    /// <returns><see langword="false"/> when the resolver was canceled, timed out or failed.</returns>
    private async Task<bool> TryResolveAsync(ImageResolveState state)
    {
        try
        {
            if (state.Resolver is IMarkdownPerformanceDocumentScope)
            {
                state.SourceLease = await ImageResolverDeadline.RunAsync(
                        token => StartLeasedResolver(state, token),
                        ResolverTimeout,
                        state.LifetimeToken,
                        abandonedResultDisposer: static lease => lease.Dispose(),
                        abandonedCompletionObserver: cleanup =>
                            _host.TrackDetachedImageWork(cleanup, failRetirementOnFault: true))
                    .ConfigureAwait(false);
                state.Resolution = state.SourceLease.Resolution;
            }
            else
            {
                state.Resolution = await ImageResolverDeadline.RunAsync(
                    token => StartResolver(state, token),
                    ResolverTimeout,
                    state.LifetimeToken)
                .ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            _host.ResetLoadAfterCancellation();
            return false;
        }
        catch (TimeoutException)
        {
            MarkdownDiagnostics.WriteLine("[ImageBox] image resolver timed out.");
            _host.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
            _host.PublishFailure(cacheKey: string.Empty);
            return false;
        }
        catch (Exception ex)
        {
            if (state.SourceLease is not null)
                _host.TryImageCleanup(state.SourceLease.Dispose);
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] image resolver failed: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            _host.PublishFailure(cacheKey: string.Empty);
            return false;
        }
        finally
        {
            // Register even if completion raced the resolver deadline: the
            // helper observes a completed fault and otherwise adds a lease for
            // an operation that is still running. The current resolver lease
            // remains active until after this finally block.
            if (state.ResolverTask is not null)
                _host.TrackDetachedImageWork(state.ResolverTask);
            // A completed resolver task stores its source-bearing result.
            // Detached tracking owns only work still draining; this caller no
            // longer needs the task alias once resolution has returned.
            state.ResolverTask = null;
        }
    }

    private ValueTask<MarkdownImageResolution> StartResolver(ImageResolveState state, CancellationToken token)
    {
        ValueTask<MarkdownImageResolution> operation =
            state.Resolver.ResolveAsync(_url, state.ResolveContext, token);
        Task<MarkdownImageResolution> task = operation.AsTask();
        state.ResolverTask = task;
        return new ValueTask<MarkdownImageResolution>(task);
    }

    private ValueTask<IMarkdownImageResolutionLease> StartLeasedResolver(
        ImageResolveState state, CancellationToken token)
    {
        var scope = (IMarkdownPerformanceDocumentScope)state.Resolver;
        ValueTask<IMarkdownImageResolutionLease> operation =
            scope.ResolveWithSourceLeaseAsync(_url, state.ResolveContext, token);
        Task<IMarkdownImageResolutionLease> task = operation.AsTask();
        state.ResolverTask = task;
        return new ValueTask<IMarkdownImageResolutionLease>(task);
    }

    // The resolver left this source to the built-in loader.
    private void LoadBuiltInFallback(ImageResolveState state)
    {
        if (_host.BuiltInSource is not { } fallbackSource)
        {
            _host.PublishFailure(cacheKey: string.Empty);
            return;
        }

        _host.ActiveCacheKey = fallbackSource.CacheKey;

        // Only sources the built-in loader is allowed to expose can consult
        // its process-wide caches. This prevents a NotHandled HTTPS result
        // from observing bytes cached by an earlier resolver-owned request.
        if (fallbackSource.CanLoadWithoutResolver &&
            TryLoadFallbackFromProcessCache(fallbackSource.CacheKey, state.BitmapOutputWork))
        {
            return;
        }

        LoadFromDefaultSource(fallbackSource, state.Demand, state.LoadDemandGeneration);
    }

    private async Task LoadResolvedAssetAsync(ImageResolveState state)
    {
        state.Asset = state.Resolution.Asset;
        if (state.Asset is null || state.Asset.Bytes.Length == 0)
        {
            _host.ReportUnavailable(state.Resolution.UnavailableReason);
            _host.PublishFailure(cacheKey: string.Empty);
            return;
        }

        bool isSvg = ResolvedImageFormat.LooksLikeSvg(state.Asset);
        // Replace the constructor's source-URI hint as soon as the resolver has
        // supplied the actual representation. This also covers warm raster-cache
        // hits, which return before the decode path below.
        _host.IsSvg = isSvg;
        // Both SVG and raster cache reuse require the resolver's explicit
        // security-partitioned identity. ResolvedUri is metadata, not an
        // account boundary; an unkeyed asset remains local to this box.
        string cacheKey = MarkdownImageCacheIdentityPolicy.GetResolvedAssetKey(state.Asset);
        _host.ActiveCacheKey = cacheKey;
        if (cacheKey.Length > 0 && _resources.Failures.HasFailedUrl(cacheKey))
        {
            _host.PublishFailure(cacheKey: string.Empty);
            return;
        }

        if (!isSvg && TryPublishCachedRasterBitmap(cacheKey, state.BitmapOutputWork))
        {
            return;
        }

        if (isSvg)
        {
            await OpenAndRenderResolvedSvgAsync(state, cacheKey).ConfigureAwait(false);
            return;
        }

        // A resolver can follow redirects to a representation whose format differs
        // from the source URI suffix (for example, an *.svg URL that resolves to a
        // transparent PNG). Once bytes have been resolved, their representation is
        // authoritative and the pre-resolution URL hint must not leak into layout,
        // accessibility, or subsequent cache decisions.
        await _rasterLoader.LoadAsync(state.Asset.Bytes, cacheKey, state.BitmapOutputWork).ConfigureAwait(false);
    }

    private async Task OpenAndRenderResolvedSvgAsync(ImageResolveState state, string cacheKey)
    {
        SvgOpenStageResult result = await _svgOpener.OpenAsync(
                state.Asset!.Bytes, intrinsicHint: default, securityPartitionKey: cacheKey,
                builtInSourceAliasKey: null, state.Demand, state.LoadDemandGeneration)
            .ConfigureAwait(false);
        using SvgOpenedRenderWork? openedWork = result.Work;
        // Clear source aliases before invoking user cleanup. A synchronous
        // helper detaches and disposes the lease without leaving a disposed
        // lease (whose Resolution may still own bytes) in this async frame.
        ReleaseResolvedSource(state);
        if (result.Deferral is { } hostDeferral)
            _svgHostDeferral.DeferSvgHostLoad(hostDeferral);
        else if (openedWork is not null)
            await _svgRenderer.RenderAsync(openedWork).ConfigureAwait(false);
    }

    private static void ReleaseResolvedSource(ImageResolveState state)
    {
        state.Asset = null;
        state.Resolution = default;
        state.SourceLease = null;
        DisposeCallerSource(ref state.ConsumerSourceLease);
    }

    private bool TryPublishCachedRasterBitmap(
        string cacheKey, BitmapOutputWorkContext? expectedWork = null)
    {
        if (!_outputAdmission.TryAcquireSourceSizedRasterBitmap(cacheKey, out SharedCanvasBitmapCache.Lease? cachedLease, expectedWork) ||
            cachedLease is null)
        {
            return false;
        }

        _host.PublishOnUiThread(() => _host.PresentCachedRasterBitmap(cachedLease), cachedLease.Dispose);
        return true;
    }

    internal bool TryLoadFallbackFromProcessCache(
        string cacheKey, BitmapOutputWorkContext? expectedWork = null)
    {
        if (_resources.Failures.HasFailedUrl(cacheKey))
        {
            _host.PublishFailure(cacheKey: string.Empty);
            return true;
        }

        if (!_host.IsSvg && TryPublishCachedRasterBitmap(cacheKey, expectedWork))
        {
            return true;
        }

        if (!_resources.SvgSources.TryGetValue(cacheKey, out SvgCacheEntry? entry))
        {
            return false;
        }

        _host.IsSvg = true;
        // Classification selects the authorized built-in loader on a miss;
        // accepted dimensions/title/description remain untouched until an
        // exact cached bitmap publication commits, just like content followers.
        return _host.TryPublishCachedSvgSource(entry, cacheKey, expectedWork);
    }

    private async Task LoadBuiltInImageAsync(
        MarkdownBuiltInImageSource source,
        bool isSvg,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        if (!_host.TryBeginImageWork(out ImageWorkLease? admittedWork) || admittedWork is null)
            return;

        using ImageWorkLease imageWork = admittedWork;
        string cacheKey = source.CacheKey;
        BitmapOutputWorkContext bitmapOutputWork = _outputAdmission.CaptureBitmapOutputWorkContext(
            imageWork.Token, expectedDemand: demand);
        try
        {
            (byte[] bytes, IDisposable? reservation) = await Task.Run(() => _reader.ReadAsync(
                    source,
                    isSvg ? SvgSourceOpener.MaxSvgBytes : MaxRemoteImageBytes,
                    isSvg ? "SVG" : "raster image",
                    imageWork.Token), CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                if (isSvg)
                {
                    SvgOpenStageResult result = await _svgOpener.OpenAsync(
                            bytes, intrinsicHint: default, securityPartitionKey: cacheKey,
                            builtInSourceAliasKey: cacheKey, demand, loadDemandGeneration)
                        .ConfigureAwait(false);
                    using SvgOpenedRenderWork? openedWork = result.Work;
                    bytes = Array.Empty<byte>();
                    source = default;
                    DisposeCallerSource(ref reservation);
                    if (result.Deferral is { } hostDeferral)
                        _svgHostDeferral.DeferSvgHostLoad(hostDeferral);
                    else if (openedWork is not null)
                        await _svgRenderer.RenderAsync(openedWork).ConfigureAwait(false);
                }
                else
                {
                    await _rasterLoader.LoadAsync(bytes, source.CacheKey, bitmapOutputWork).ConfigureAwait(false);
                }
            }
            finally
            {
                bytes = Array.Empty<byte>();
                source = default;
                DisposeCallerSource(ref reservation);
            }
        }
        catch (OperationCanceledException)
        {
            _host.ResetLoadAfterCancellation();
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] built-in source read failed: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            _host.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
            _host.PublishFailure(cacheKey);
        }
    }

    internal async Task LoadSvgDataUriAsync(
        string rawDataUri,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        if (!_host.TryBeginImageWork(out ImageWorkLease? decodeWork) || decodeWork is null)
            return;

        using ImageWorkLease imageWork = decodeWork;
        byte[] rawBytes;
        IDisposable? reservation = null;
        try
        {
            (rawBytes, reservation) = await Task.Run(
                    () => _reader.ReadDataUriAsync(rawDataUri, SvgSourceOpener.MaxSvgBytes, imageWork.Token),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _host.ResetLoadAfterCancellation();
            return;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG data URI decode failed: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            _host.PublishOnUiThread(_host.PresentSvgDataUriFailure);
            return;
        }

        try
        {
            rawDataUri = string.Empty;
            SvgOpenStageResult result = await _svgOpener.OpenAsync(
                    rawBytes, intrinsicHint: default, securityPartitionKey: _host.ActiveCacheKey ?? _url,
                    builtInSourceAliasKey: _host.ActiveCacheKey ?? _url, demand, loadDemandGeneration)
                .ConfigureAwait(false);
            using SvgOpenedRenderWork? openedWork = result.Work;
            rawBytes = Array.Empty<byte>();
            DisposeCallerSource(ref reservation);
            if (result.Deferral is { } hostDeferral)
                _svgHostDeferral.DeferSvgHostLoad(hostDeferral);
            else if (openedWork is not null)
                await _svgRenderer.RenderAsync(openedWork).ConfigureAwait(false);
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    private static void DisposeCallerSource<T>(ref T? source)
        where T : class, IDisposable
    {
        T? consumed = source;
        source = null;
        consumed?.Dispose();
    }
}
