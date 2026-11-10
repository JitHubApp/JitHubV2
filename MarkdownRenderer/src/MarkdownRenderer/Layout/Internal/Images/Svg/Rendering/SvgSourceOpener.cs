using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Performance;
using SvgFailure = (MarkdownRenderer.Images.MarkdownSvgFailureReason Reason, string Description);
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// Prepares, preflights and opens an image box's admitted SVG source off the
/// UI thread: provider source preparation and scratch admission, the host
/// preflight and failure cache, content-cache followers, the keyed open and
/// provider admissions, the provider or host-admission Open, and the source
/// cache entry. A successful open hands a source-free
/// <see cref="SvgOpenedRenderWork"/> to rendering; a capacity denial returns a
/// scalar-only <see cref="SvgHostCapacityDeferral"/>.
/// </summary>
internal sealed class SvgSourceOpener
{
    internal const int MaxSvgBytes = 8 * 1024 * 1024;

    private readonly ISvgLoadHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputAdmission _outputAdmission;
    private readonly ImageBitmapOutputDeferral _outputDeferral;
    private readonly string _url;
    private readonly SvgSourcePreparer _preparer;
    private readonly SvgProviderOpener _providerOpener;
    private readonly SvgRenderHandoff _handoff;

    internal SvgSourceOpener(
        ISvgLoadHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        ImageBitmapOutputAdmission outputAdmission,
        ImageBitmapOutputDeferral outputDeferral,
        string url)
    {
        _host = host;
        _context = context;
        _resources = resources;
        _outputAdmission = outputAdmission;
        _outputDeferral = outputDeferral;
        _url = url;
        _preparer = new SvgSourcePreparer(host, context, resources);
        _providerOpener = new SvgProviderOpener(host, context);
        _handoff = new SvgRenderHandoff(host, resources);
    }

    /// <summary>
    /// Prepares and opens an admitted SVG off the UI thread. A successful result
    /// owns a source-free render handoff; callers release original source
    /// reservations before consuming it.
    /// </summary>
    internal async Task<SvgOpenStageResult> OpenAsync(
        byte[] rawBytes,
        Size intrinsicHint,
        string? securityPartitionKey,
        string? builtInSourceAliasKey,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        if (!_host.TryBeginImageWork(out ImageWorkLease? renderWork) || renderWork is null)
            return default;

        try
        {
            long svgWorkId = MarkdownSvgStageTrace.BeginWork();
            long dispatchStartedAt = svgWorkId != 0 &&
                MarkdownPerformanceEventSource.Log.IsSvgWorkMeasurementEnabled()
                    ? Stopwatch.GetTimestamp()
                    : 0;
            async Task<SvgOpenStageResult> RunCoreAsync(byte[] bytes)
            {
                using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope(svgWorkId);
                using (MarkdownSvgStageTrace.MeasureFrom(
                    svgWorkId,
                    MarkdownSvgWorkStage.OuterDispatchQueueWait,
                    dispatchStartedAt,
                    bytes.LongLength))
                {
                }
                try
                {
                    return await OpenCoreAsync(
                            bytes, intrinsicHint, renderWork, securityPartitionKey,
                            builtInSourceAliasKey, demand, loadDemandGeneration)
                        .ConfigureAwait(false);
                }
                finally
                {
                    bytes = Array.Empty<byte>();
                }
            }

            Task<SvgOpenStageResult> core = SvgWorkDispatch.RunOffDispatcher(
                _context.Dispatcher?.HasThreadAccess == true, () => RunCoreAsync(rawBytes));
            return await core.ConfigureAwait(false);
        }
        catch
        {
            renderWork.Dispose();
            throw;
        }
        finally
        {
            rawBytes = Array.Empty<byte>();
        }
    }

    private async Task<SvgOpenStageResult> OpenCoreAsync(
        byte[] rawBytes,
        Size intrinsicHint,
        ImageWorkLease renderWork,
        string? securityPartitionKey,
        string? builtInSourceAliasKey,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        SvgOpenState? state = BeginOpen(
            rawBytes, intrinsicHint, renderWork, securityPartitionKey,
            builtInSourceAliasKey, demand, loadDemandGeneration);
        if (state is null)
            return default;
        // The open state now owns the only reference to the source array.
        rawBytes = Array.Empty<byte>();

        try
        {
            if (!await _preparer.TryPrepareAsync(state).ConfigureAwait(false))
                return new SvgOpenStageResult(null, state.HostCapacityDeferral);
            if (TryPublishKnownFailure(state))
                return default;

            state.CancellationToken.ThrowIfCancellationRequested();
            ResolveContentIdentity(state);
            if (!await TryEnterOpenAdmissionAsync(state).ConfigureAwait(false))
                return default;
            if (!await _providerOpener.TryOpenAsync(state).ConfigureAwait(false))
                return new SvgOpenStageResult(null, state.HostCapacityDeferral);
            state.CancellationToken.ThrowIfCancellationRequested();

            if (state.HostAdmission is not null && TryPublishContentFollower(state))
                return default;
            if (_outputDeferral.IsBitmapOutputDeferred)
                return default;
            if (!TryAcceptOpenedDocument(state))
                return default;

            SvgOpenedRenderWork? work = _handoff.TryHandOff(state);
            return work is null
                ? new SvgOpenStageResult(null, state.HostCapacityDeferral)
                : new SvgOpenStageResult(work, null);
        }
        catch (Exception exception)
        {
            _host.HandleSvgWorkFailure(exception, state.CancellationToken, state.SessionLifetimeToken,
                state.ResolvedCacheKey, state.SvgFailureCacheKey, state.RenderGeneration);
        }
        finally
        {
            ReleaseOpenState(state);
        }

        return new SvgOpenStageResult(null, state.HostCapacityDeferral);
    }

    /// <summary>
    /// Starts one open: advances the render generation, resolves the security
    /// partition, and links the session lifetime. Without a renderer it
    /// publishes the failure, retires <paramref name="renderWork"/> and
    /// returns <see langword="null"/>.
    /// </summary>
    private SvgOpenState? BeginOpen(
        byte[] rawBytes,
        Size intrinsicHint,
        ImageWorkLease renderWork,
        string? securityPartitionKey,
        string? builtInSourceAliasKey,
        MarkdownSvgRenderDemand demand,
        int loadDemandGeneration)
    {
        CancellationToken lifetimeToken = renderWork.Token;
        int renderGeneration = _host.AdvanceSvgRenderGeneration();
        string resolvedCacheKey = securityPartitionKey ?? _url;
        string effectiveSecurityPartitionKey = _host.GetSvgSourceSecurityPartitionKey(resolvedCacheKey);
        IMarkdownSvgRenderer? renderer = _context.SvgRenderer;
        long svgWorkId = MarkdownSvgStageTrace.CurrentWorkId;
        if (renderer is null)
        {
            _host.PublishSvgFailure(
                new MarkdownSvgException(
                    MarkdownSvgFailureReason.ArchitectureMismatch,
                    "No static SVG renderer is configured."),
                resolvedCacheKey,
                cachePermanently: false,
                renderGeneration: renderGeneration);
            renderWork.Dispose();
            return null;
        }

        var state = new SvgOpenState(
            renderWork, rawBytes, intrinsicHint, builtInSourceAliasKey, demand, loadDemandGeneration,
            renderGeneration, resolvedCacheKey, effectiveSecurityPartitionKey, renderer, svgWorkId);
        IMarkdownPerformanceSessionInternal? preparationSession =
            _context.PerformanceSession as IMarkdownPerformanceSessionInternal;
        state.PreparationSession = preparationSession;
        state.SessionLifetimeToken = preparationSession?.LifetimeToken ?? CancellationToken.None;
        // Session retirement cancels the complete preparation/open/render
        // operation, not only the CPU-admitted synchronous region. Keep this
        // token alive through actual work drain so retirement is never mistaken
        // for a retryable provider generation transition.
        state.PreparationCancellation = preparationSession is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken, state.SessionLifetimeToken);
        state.CancellationToken = state.PreparationCancellation?.Token ?? lifetimeToken;
        state.BitmapOutputWork = _outputAdmission.CaptureBitmapOutputWorkContext(
            state.CancellationToken, expectedDemand: demand, expectedSvgGeneration: renderGeneration);
        state.CurrentSecurityPartitionHash = SvgSourcePreflight.HashSecurityPartition(effectiveSecurityPartitionKey);
        state.MetadataWasKnown = _host.IsKnownSvgSource(rawBytes, state.CurrentSecurityPartitionHash);
        state.RendererIdentity = _resources.ProviderIdentities.GetSvgRendererIdentity(renderer);
        return state;
    }

    /// <returns><see langword="true"/> when a cached or preflight failure was published.</returns>
    private bool TryPublishKnownFailure(SvgOpenState state)
    {
        state.SvgFailureCacheKey = SvgCacheKeys.Failure(
            state.Preflight.ContentHash,
            state.Preflight.SecurityPartitionHash,
            state.RendererIdentity,
            state.ProviderCacheGeneration);
        if (_resources.Failures.TryGetSvgFailure(
                state.SvgFailureCacheKey,
                out SvgFailure cachedFailure))
        {
            _host.PublishSvgFailure(
                cachedFailure.Reason,
                cachedFailure.Description,
                state.SvgFailureCacheKey,
                cachePermanently: true,
                state.RenderGeneration);
            return true;
        }

        if (state.Preparation!.FailureReason is { } preflightFailureReason)
        {
            _host.PublishSvgFailure(
                preflightFailureReason,
                state.Preparation.FailureDescription ??
                    "The SVG was rejected by host preflight (invalid-content).",
                state.SvgFailureCacheKey,
                cachePermanently: true,
                state.RenderGeneration);
            return true;
        }

        return false;
    }

    private void ResolveContentIdentity(SvgOpenState state)
    {
        state.SemanticColor = _host.GetSemanticSvgColor();
        state.ColorScheme = _host.GetSvgColorScheme();
        state.ContentCacheKey = SvgCacheKeys.Content(
            state.Preflight.ContentHash,
            state.Preflight.SecurityPartitionHash,
            state.RendererIdentity,
            state.ProviderCacheGeneration,
            _context.Language,
            state.ColorScheme,
            state.SemanticColor);
    }

    private bool TryPublishContentFollower(SvgOpenState state) =>
        _host.TryPublishContentCachedSvg(
            state.ContentCacheKey,
            state.ResolvedCacheKey,
            state.BuiltInSourceAliasKey,
            layoutInvalidated: !state.MetadataWasKnown,
            state.RenderGeneration,
            state.BitmapOutputWork);

    // The source cache cannot prevent concurrent boxes for the same
    // remote SVG from all entering the provider: host resolution and
    // preflight happen independently, and the old bitmap gate was
    // reached only after OpenAsync. Serialize the first provider open
    // by admitted content identity, then let followers consume the
    // completed source + device bitmap cache without waking the
    // worker. Exact-key admission avoids serializing unrelated content;
    // the bounded registry falls back to fixed overflow stripes only
    // during unusually large bursts. Content identity includes the
    // security partition and conservative rendering inputs, so
    // unrelated trust domains never share admitted state.
    /// <returns><see langword="false"/> when a content follower published or output is deferred.</returns>
    private async Task<bool> TryEnterOpenAdmissionAsync(SvgOpenState state)
    {
        if (TryPublishContentFollower(state))
            return false;
        if (_outputDeferral.IsBitmapOutputDeferred)
            return false;
        if (state.HostAdmission is not null)
            return true;

        using var stageTrace = MarkdownSvgStageTrace.Measure(
            state.SvgWorkId,
            MarkdownSvgWorkStage.HostAdmissionWait,
            state.SvgSourceBytes);
        state.OpenAdmissionEntry = await _resources.SvgOpenAdmissions
            .AcquireAsync(state.ContentCacheKey, state.CancellationToken)
            .ConfigureAwait(false);

        if (TryPublishContentFollower(state))
            return false;
        if (_outputDeferral.IsBitmapOutputDeferred)
            return false;

        await _resources.SvgProviderAdmission.WaitAsync(state.CancellationToken).ConfigureAwait(false);
        state.ProviderAdmissionHeld = true;
        return true;
    }

    /// <summary>
    /// Reserves the opened candidate's document use, validates its metadata
    /// and provider generation, and records the source cache entry.
    /// </summary>
    /// <returns><see langword="false"/> when the provider generation changed.</returns>
    private bool TryAcceptOpenedDocument(SvgOpenState state)
    {
        IMarkdownSvgDocument openedDocument = state.OpenedDocument!;
        // Open is a candidate, not a replacement of the accepted tile
        // document. Hold a use until raster completion or UI publication.
        state.OpenedDocumentUse = _host.TryAcquireCandidateSvgDocumentUse(openedDocument, out bool ownershipConflict);
        if (ownershipConflict)
        {
            // A broken provider returned a handle already reserved by
            // another Open. That owner (or the accepted image) must remain
            // responsible for it; this request must not dispose its handle.
            state.OpenedDocument = null;
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned an already-owned document handle.");
        }
        if (state.OpenedDocumentUse is null)
            throw new OperationCanceledException(state.CancellationToken);

        MarkdownSvgDocumentInfo info = openedDocument.Info ??
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned no document metadata.");
        if (!_host.IsSvgProviderCurrent(state.Renderer, state.ProviderCacheGeneration, info.HasText))
        {
            _host.PublishSvgGenerationInvalidated(state.RenderGeneration);
            return false;
        }
        SvgPreflight preflight = state.Preflight;
        state.Info = info;
        state.Intrinsic = ResolveIntrinsicSize(info, preflight.Intrinsic);
        state.Description = string.IsNullOrWhiteSpace(info.Description)
            ? preflight.Desc
            : SvgSourcePreflight.TrimMetadata(info.Description);

        state.SourceEntry = new SvgCacheEntry(
            state.RawBytes,
            state.Intrinsic,
            preflight.Title,
            state.Description,
            preflight.ContentHash,
            preflight.SecurityPartitionHash,
            info,
            state.RendererIdentity,
            state.ProviderCacheGeneration);
        // Resolver CacheKey is a security/content identity, never a
        // built-in URI alias. Only authorized built-in reads may publish
        // entries consumed by pre-resolution and NotHandled fallback.
        if (!string.IsNullOrEmpty(state.BuiltInSourceAliasKey))
            _resources.SvgSources.Set(state.BuiltInSourceAliasKey, state.SourceEntry);
        _resources.SvgSources.Set(state.ContentCacheKey, state.SourceEntry);
        return true;
    }

    private void ReleaseOpenState(SvgOpenState state)
    {
        // Clear every source-bearing state-machine alias before returning
        // to the caller, which can then retire its original source lease.
        state.SourceEntry = null;
        state.OpenRequest = null;
        state.Preparation = null;
        state.RawBytes = Array.Empty<byte>();
        if (state.HostPreparationLease is not null)
            _host.TryImageCleanup(state.HostPreparationLease.Dispose);
        try
        {
            _resources.SvgOpenAdmissions.Release(ref state.OpenAdmissionEntry);
        }
        catch (Exception exception)
        {
            _host.RecordImageRetirementFailure(exception);
        }
        if (state.ProviderAdmissionHeld)
            _host.TryImageCleanup(() => _resources.SvgProviderAdmission.Release());
        if (state.OpenedDocument is not null)
            _host.RetireUncommittedSvgDocument(state.OpenedDocument);
        if (state.OpenedDocumentUse is not null)
            _host.TryImageCleanup(state.OpenedDocumentUse.Dispose);
        if (state.OwnerSlot is not null)
            _host.TryImageCleanup(state.OwnerSlot.Dispose);
        if (state.PreparationCancellation is not null)
            _host.TryImageCleanup(state.PreparationCancellation.Dispose);
        if (!state.RenderOwnershipTransferred)
            state.RenderWork.Dispose();
    }

    private static Size ResolveIntrinsicSize(MarkdownSvgDocumentInfo info, Size fallback)
    {
        double? width = SanitizeSvgDimension(info.IntrinsicWidthDips);
        double? height = SanitizeSvgDimension(info.IntrinsicHeightDips);
        double? ratio = SanitizeSvgRatio(info.IntrinsicAspectRatio);

        if (width is > 0 && height is null && ratio is > 0)
            height = width.Value / ratio.Value;
        else if (height is > 0 && width is null && ratio is > 0)
            width = height.Value * ratio.Value;

        if (width is null && fallback.Width > 0 && double.IsFinite(fallback.Width))
            width = fallback.Width;
        if (height is null && fallback.Height > 0 && double.IsFinite(fallback.Height))
            height = fallback.Height;

        if (width is > 0 && height is null)
            height = ratio is > 0 ? width.Value / ratio.Value : width.Value / 2d;
        if (height is > 0 && width is null)
            width = ratio is > 0 ? height.Value * ratio.Value : height.Value * 2d;

        return new Size(width ?? 300d, height ?? 150d);
    }

    private static double? SanitizeSvgDimension(double? value) =>
        value is > 0 && double.IsFinite(value.Value) ? value : null;

    private static double? SanitizeSvgRatio(double? value) =>
        value is > 0 && double.IsFinite(value.Value) ? value : null;
}
