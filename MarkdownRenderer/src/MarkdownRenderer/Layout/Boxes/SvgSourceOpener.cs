using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.MarkdownSvgPhysicalViewport PhysicalViewport);
using SvgFailure = (MarkdownRenderer.Images.MarkdownSvgFailureReason Reason, string Description);
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);

namespace MarkdownRenderer.Layout.Boxes;

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

            Task<SvgOpenStageResult> core = _context.Dispatcher?.HasThreadAccess == true
                ? Task.Run(() => RunCoreAsync(rawBytes), CancellationToken.None)
                : RunCoreAsync(rawBytes);
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
        CancellationToken lifetimeToken = renderWork.Token;
        int renderGeneration = _host.AdvanceSvgRenderGeneration();
        string resolvedCacheKey = securityPartitionKey ?? _url;
        string effectiveSecurityPartitionKey = _host.GetSvgSourceSecurityPartitionKey(resolvedCacheKey);
        IMarkdownSvgRenderer? renderer = _context.SvgRenderer;
        IMarkdownSvgAdmission? hostAdmission = renderer as IMarkdownSvgAdmission;
        long svgWorkId = MarkdownSvgStageTrace.CurrentWorkId;
        long svgSourceBytes = rawBytes.LongLength;
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
            return default;
        }

        IMarkdownPerformanceSessionInternal? preparationSession =
            _context.PerformanceSession as IMarkdownPerformanceSessionInternal;
        CancellationToken sessionLifetimeToken = preparationSession?.LifetimeToken ?? CancellationToken.None;
        // Session retirement cancels the complete preparation/open/render
        // operation, not only the CPU-admitted synchronous region. Keep this
        // token alive through actual work drain so retirement is never mistaken
        // for a retryable provider generation transition.
        CancellationTokenSource? preparationCancellation = preparationSession is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken, sessionLifetimeToken);
        CancellationToken cancellationToken = preparationCancellation?.Token ?? lifetimeToken;
        BitmapOutputWorkContext bitmapOutputWork = _outputAdmission.CaptureBitmapOutputWorkContext(
            cancellationToken, expectedDemand: demand, expectedSvgGeneration: renderGeneration);
        IMarkdownSvgDocument? openedDocument = null;
        SvgDocumentUseLease? openedDocumentUse = null;
        SvgHostOwnerSlot? ownerSlot = null;
        SvgHostCapacityDeferral? hostCapacityDeferral = null;
        IMarkdownSvgPreparationLease? hostPreparationLease = null;
        long hostPreparationScratchBytes = 0;
        long hostPreparedSourceBytes = 0;
        MarkdownSvgSourcePreparation? preparation = null;
        MarkdownSvgOpenRequest? openRequest = null;
        SvgCacheEntry? sourceEntry = null;
        long sourceReservationBytes = 0;
        object? admissionDomain = null;
        BoundedAsyncKeyedGate.Entry? openAdmissionEntry = null;
        bool providerAdmissionHeld = false;
        bool renderOwnershipTransferred = false;
        string currentSecurityPartitionHash = SvgSourcePreflight.HashSecurityPartition(effectiveSecurityPartitionKey);
        bool metadataWasKnown = _host.IsKnownSvgSource(rawBytes, currentSecurityPartitionHash);
        long providerCacheGeneration = 0;
        long rendererIdentity = _resources.ProviderIdentities.GetSvgRendererIdentity(renderer);
        string svgFailureCacheKey = string.Empty;

        try
        {
            // The optional provider prepares a bounded inert static image on
            // this background path. The host inspects its metadata and the
            // provider repeats authoritative checks before worker admission.
            SvgPreflight preflight;
            // Sanitization, hashing and metadata inspection are preparation
            // work too. Borrow the optional session slot and process-wide
            // preparation gate only for that synchronous region, never across
            // a keyed/provider/worker wait. The source-scaled weight schedules
            // transient work; it does not reserve retained source bytes.
            {
                CancellationToken preparationToken = cancellationToken;
                try
                {
                    preparationToken.ThrowIfCancellationRequested();
                    using IDisposable? preparationSlot = preparationSession is null
                        ? null
                        : await preparationSession.EnterCpuPreparationAsync(
                                _context.EffectiveImagePreparationOwner ?? _context,
                                exclusive: false,
                                preparationToken)
                            .ConfigureAwait(false);
                    using IDisposable processPreparationSlot =
                        await _resources.Preparation.EnterAsync(
                                _context.EffectiveImagePreparationOwner ?? _context,
                                rawBytes.LongLength * 3,
                                exclusive: false,
                                preparationToken)
                            .ConfigureAwait(false);
                    // CPU admission is complete before taking byte ownership.
                    // Only the opt-in session path and a participating provider
                    // use this reservation; legacy/custom providers are unchanged.
                    if (preparationSession is not null &&
                        renderer is IMarkdownSvgPreparationAdmission scratchAdmission)
                    {
                        long demandVersionAtPreparation = demand.Version;
                        bool admitted = scratchAdmission.TryReservePreparationScratch(
                            demand,
                            out hostPreparationLease,
                            out long capacityVersionAtPreparation,
                            out hostPreparationScratchBytes);
                        if (hostPreparationScratchBytes <= 0 ||
                            capacityVersionAtPreparation < 0 ||
                            admitted != (hostPreparationLease is not null))
                        {
                            throw new MarkdownSvgException(
                                MarkdownSvgFailureReason.WorkerFailure,
                                "The SVG provider returned invalid preparation admission.");
                        }
                        if (!admitted)
                        {
                            hostCapacityDeferral = new SvgHostCapacityDeferral(
                                scratchAdmission,
                                demand,
                                loadDemandGeneration,
                                capacityVersionAtPreparation,
                                hostPreparationScratchBytes,
                                RequiresOwnerSlot: false,
                                OwnerVersionAtDenial: 0,
                                DemandVersionAtDenial: demandVersionAtPreparation,
                                VisualDemandEpochAtDenial: _host.SvgHostVisualDemandEpoch);
                            // Do not call PrepareSource or park while owning bytes.
                            // The caller drains the original resolver lease before
                            // registering the scalar-only capacity waiter.
                            rawBytes = Array.Empty<byte>();
                            return new SvgOpenStageResult(null, hostCapacityDeferral);
                        }
                    }
                    {
                        using var stageTrace = MarkdownSvgStageTrace.Measure(
                            svgWorkId,
                            MarkdownSvgWorkStage.SourcePreparation,
                            svgSourceBytes);
                        preparationToken.ThrowIfCancellationRequested();
                        preparation = renderer.PrepareSource(rawBytes, preparationToken);
                        preparationToken.ThrowIfCancellationRequested();
                    }
                    if (preparation is null)
                        throw new MarkdownSvgException(
                            MarkdownSvgFailureReason.WorkerFailure,
                            "The SVG provider returned no source preparation result.");
                    if (preparation.SanitizedBytes is { Length: 0 } or { Length: > MaxSvgBytes })
                        preparation = MarkdownSvgSourcePreparation.Reject(
                            MarkdownSvgFailureReason.ResourceLimitExceeded,
                            "The SVG provider returned an empty or over-budget source.");
                    if (preparation.SanitizedBytes is null && preparation.FailureReason is null)
                        preparation = MarkdownSvgSourcePreparation.Reject(
                            MarkdownSvgFailureReason.UnsupportedContent,
                            "The SVG provider did not admit this source.");
                    if (hostPreparationLease is not null)
                    {
                        if (preparation.SanitizedBytes is { } preparedSource &&
                            !ReferenceEquals(preparedSource, rawBytes))
                        {
                            hostPreparedSourceBytes = preparedSource.LongLength;
                            hostPreparationLease.SetLength(hostPreparedSourceBytes);
                        }
                        else
                        {
                            // Rejection/identity preparation owns no distinct
                            // host output. The resolver still owns the input.
                            hostPreparationLease.Dispose();
                            hostPreparationLease = null;
                        }
                    }
                    rawBytes = preparation.SanitizedBytes ?? rawBytes;
                    providerCacheGeneration = renderer.CacheGeneration;
                    {
                        using var stageTrace = MarkdownSvgStageTrace.Measure(
                            svgWorkId,
                            MarkdownSvgWorkStage.HostPreflight,
                            svgSourceBytes);
                        if (preparation.SanitizedBytes is not null &&
                            _host.TryGetKnownSvgPreflight(
                                rawBytes, currentSecurityPartitionHash, intrinsicHint, out SvgPreflight knownPreflight))
                        {
                            preflight = knownPreflight;
                        }
                        else
                        {
                            preflight = SvgSourcePreflight.Preflight(
                                rawBytes,
                                preparation,
                                intrinsicHint,
                                effectiveSecurityPartitionKey,
                                preparationToken);
                        }
                    }
                    preparationToken.ThrowIfCancellationRequested();
                }
                catch (ObjectDisposedException) when (
                    preparationToken.IsCancellationRequested || sessionLifetimeToken.IsCancellationRequested)
                {
                    // A provider/preparation disposal race coinciding with
                    // linked session/image cancellation is not invalid SVG
                    // content and must not publish an unavailable image.
                    throw new OperationCanceledException(preparationToken);
                }
            }

            svgFailureCacheKey = SvgCacheKeys.Failure(
                preflight.ContentHash,
                preflight.SecurityPartitionHash,
                rendererIdentity,
                providerCacheGeneration);
            if (_resources.Failures.TryGetSvgFailure(
                    svgFailureCacheKey,
                    out SvgFailure cachedFailure))
            {
                _host.PublishSvgFailure(
                    cachedFailure.Reason,
                    cachedFailure.Description,
                    svgFailureCacheKey,
                    cachePermanently: true,
                    renderGeneration);
                return default;
            }

            if (preparation.FailureReason is { } preflightFailureReason)
            {
                _host.PublishSvgFailure(
                    preflightFailureReason,
                    preparation.FailureDescription ??
                        "The SVG was rejected by host preflight (invalid-content).",
                    svgFailureCacheKey,
                    cachePermanently: true,
                    renderGeneration);
                return default;
            }

            cancellationToken.ThrowIfCancellationRequested();
            MarkdownSvgColor semanticColor = _host.GetSemanticSvgColor();
            MarkdownSvgColorScheme colorScheme = _host.GetSvgColorScheme();
            string contentCacheKey = SvgCacheKeys.Content(
                preflight.ContentHash,
                preflight.SecurityPartitionHash,
                rendererIdentity,
                providerCacheGeneration,
                _context.Language,
                colorScheme,
                semanticColor);

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
            if (_host.TryPublishContentCachedSvg(
                    contentCacheKey,
                    resolvedCacheKey,
                    builtInSourceAliasKey,
                    layoutInvalidated: !metadataWasKnown,
                    renderGeneration,
                    bitmapOutputWork))
            {
                return default;
            }
            if (_outputDeferral.IsBitmapOutputDeferred)
                return default;

            if (hostAdmission is null)
            {
                using var stageTrace = MarkdownSvgStageTrace.Measure(
                    svgWorkId,
                    MarkdownSvgWorkStage.HostAdmissionWait,
                    svgSourceBytes);
                openAdmissionEntry = await _resources.SvgOpenAdmissions
                    .AcquireAsync(contentCacheKey, cancellationToken)
                    .ConfigureAwait(false);

                if (_host.TryPublishContentCachedSvg(
                        contentCacheKey,
                        resolvedCacheKey,
                        builtInSourceAliasKey,
                        layoutInvalidated: !metadataWasKnown,
                        renderGeneration,
                        bitmapOutputWork))
                {
                    return default;
                }
                if (_outputDeferral.IsBitmapOutputDeferred)
                    return default;

                await _resources.SvgProviderAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
                providerAdmissionHeld = true;
            }

            // The provider owns the hard deadline for each active worker
            // transaction. Do not start a second wall-clock deadline around
            // provider queueing plus Open + Render: a visible badge burst can
            // legitimately wait behind earlier visible work even though every
            // individual worker request stays inside its immutable ceiling.

            openRequest = new MarkdownSvgOpenRequest(
                rawBytes,
                _context.Language,
                colorScheme,
                semanticColor,
                demand.Priority);

            {
                using var stageTrace = MarkdownSvgStageTrace.Measure(
                    svgWorkId,
                    MarkdownSvgWorkStage.ProviderOpen,
                    svgSourceBytes);
                // Open may still read the caller's bytes until it really
                // completes. Keep the source reservation and image-work lease
                // across that completion, even if the provider ignores cancel.
                // The provider owns its bounded transaction deadline; do not
                // release caller memory on cancellation of just our await.
                if (hostAdmission is not null)
                {
                    long demandVersionAtOpen = demand.Version;
                    MarkdownSvgAdmissionResult hostResult = await hostAdmission.TryOpenAsync(
                            openRequest,
                            demand,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (hostResult.Status == MarkdownSvgAdmissionStatus.CapacityUnavailable)
                    {
                        if (hostResult.Document is not null ||
                            hostResult.SourceReservationBytes != 0 ||
                            hostResult.AdmissionDomain is not null ||
                            hostResult.RequiredSourceReservationBytes <= 0)
                        {
                            openedDocument = hostResult.Document;
                            throw new MarkdownSvgException(
                                MarkdownSvgFailureReason.WorkerFailure,
                                "The SVG provider returned an invalid capacity denial.");
                        }

                        hostCapacityDeferral = new SvgHostCapacityDeferral(
                            hostAdmission,
                            demand,
                            loadDemandGeneration,
                            hostResult.CapacityVersion,
                            Math.Max(hostPreparationScratchBytes,
                                checked(hostPreparedSourceBytes + hostResult.RequiredSourceReservationBytes)),
                            RequiresOwnerSlot: false,
                            OwnerVersionAtDenial: 0,
                            DemandVersionAtDenial: demandVersionAtOpen,
                            VisualDemandEpochAtDenial: _host.SvgHostVisualDemandEpoch);
                        openRequest = null;
                        preparation = null!;
                        rawBytes = Array.Empty<byte>();
                        return new SvgOpenStageResult(null, hostCapacityDeferral);
                    }

                    // Keep a returned candidate in the outer cleanup owner
                    // before validating its admission metadata. Malformed or
                    // foreign-domain handles are still caller-owned and must
                    // be retired in finally.
                    openedDocument = hostResult.Document;
                    if (hostResult.Status != MarkdownSvgAdmissionStatus.Opened ||
                        openedDocument is null)
                    {
                        throw new MarkdownSvgException(
                            MarkdownSvgFailureReason.WorkerFailure,
                            "The SVG provider returned no opened document.");
                    }
                    admissionDomain = hostResult.AdmissionDomain;
                    sourceReservationBytes = hostResult.SourceReservationBytes;
                    if (admissionDomain is null || sourceReservationBytes <= 0 ||
                        !ReferenceEquals(admissionDomain, hostAdmission.AdmissionDomain))
                    {
                        throw new MarkdownSvgException(
                            MarkdownSvgFailureReason.WorkerFailure,
                            "The SVG provider returned inconsistent source-admission ownership.");
                    }
                    openRequest = null;
                }
                else
                {
                    openedDocument = await renderer.OpenAsync(openRequest, cancellationToken)
                        .ConfigureAwait(false);
                    openRequest = null;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (hostAdmission is not null && _host.TryPublishContentCachedSvg(
                    contentCacheKey,
                    resolvedCacheKey,
                    builtInSourceAliasKey,
                    layoutInvalidated: !metadataWasKnown,
                    renderGeneration,
                    bitmapOutputWork))
            {
                return default;
            }
            if (_outputDeferral.IsBitmapOutputDeferred)
                return default;

            // Open is a candidate, not a replacement of the accepted tile
            // document. Hold a use until raster completion or UI publication.
            openedDocumentUse = _host.TryAcquireCandidateSvgDocumentUse(openedDocument, out bool ownershipConflict);
            if (ownershipConflict)
            {
                // A broken provider returned a handle already reserved by
                // another Open. That owner (or the accepted image) must remain
                // responsible for it; this request must not dispose its handle.
                openedDocument = null;
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "The SVG renderer returned an already-owned document handle.");
            }
            if (openedDocumentUse is null)
                throw new OperationCanceledException(cancellationToken);

            MarkdownSvgDocumentInfo info = openedDocument.Info ??
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "The SVG renderer returned no document metadata.");
            if (!_host.IsSvgProviderCurrent(renderer, providerCacheGeneration, info.HasText))
            {
                _host.PublishSvgGenerationInvalidated(renderGeneration);
                return default;
            }
            Size intrinsic = ResolveIntrinsicSize(info, preflight.Intrinsic);
            string? description = string.IsNullOrWhiteSpace(info.Description)
                ? preflight.Desc
                : SvgSourcePreflight.TrimMetadata(info.Description);

            sourceEntry = new SvgCacheEntry(
                rawBytes,
                intrinsic,
                preflight.Title,
                description,
                preflight.ContentHash,
                preflight.SecurityPartitionHash,
                info,
                rendererIdentity,
                providerCacheGeneration);
            // Resolver CacheKey is a security/content identity, never a
            // built-in URI alias. Only authorized built-in reads may publish
            // entries consumed by pre-resolution and NotHandled fallback.
            if (!string.IsNullOrEmpty(builtInSourceAliasKey))
                _resources.SvgSources.Set(builtInSourceAliasKey, sourceEntry);
            _resources.SvgSources.Set(contentCacheKey, sourceEntry);

            SvgRasterPlan rasterPlan = _host.PickRasterDimensions(intrinsic);
            int targetWidth = rasterPlan.Width;
            int targetHeight = rasterPlan.Height;
            long targetPixels = (long)targetWidth * targetHeight;
            string bitmapIdentity = _host.BuildSvgBitmapIdentity(
                preflight.ContentHash,
                preflight.SecurityPartitionHash,
                info,
                targetWidth,
                targetHeight,
                colorScheme,
                semanticColor,
                rendererIdentity,
                providerCacheGeneration,
                rasterPlan.PhysicalViewport);

            // From here only the bounded source cache owns the prepared array.
            // Render waits and completed async tasks must not pin an evicted
            // entry beyond that policy. UI publication uses weak source identity;
            // later bitmap memoization rechecks the exact bounded cache key.
            var publicationSourceIdentity = new WeakReference<byte[]>(rawBytes);
            long publicationSourceBytes = rawBytes.LongLength;
            sourceEntry = null;
            preparation = null;
            openRequest = null;
            rawBytes = Array.Empty<byte>();
            hostPreparedSourceBytes = 0;
            hostPreparationLease?.Dispose();
            hostPreparationLease = null;

            if (rasterPlan.UsesTiles)
            {
                if (hostAdmission is not null)
                {
                    ownerSlot = _resources.SvgHostAdmission.TryReserveOwnerSlot(
                        admissionDomain!,
                        demand,
                        out long ownerVersionAtDenial);
                    if (ownerSlot is null)
                    {
                        hostCapacityDeferral = new SvgHostCapacityDeferral(
                            hostAdmission,
                            demand,
                            loadDemandGeneration,
                            hostAdmission.CapacityVersion,
                            sourceReservationBytes,
                            RequiresOwnerSlot: true,
                            OwnerVersionAtDenial: ownerVersionAtDenial,
                            DemandVersionAtDenial: demand.Version,
                            VisualDemandEpochAtDenial: _host.SvgHostVisualDemandEpoch);
                        rawBytes = Array.Empty<byte>();
                        preparation = null!;
                        return new SvgOpenStageResult(null, hostCapacityDeferral);
                    }
                }
            }

            SvgPublicationMetadata metadata = new(
                publicationSourceIdentity,
                publicationSourceBytes,
                renderer,
                rendererIdentity,
                providerCacheGeneration,
                renderGeneration,
                metadataWasKnown,
                resolvedCacheKey,
                svgFailureCacheKey,
                intrinsic,
                preflight.Title,
                description,
                preflight.ContentHash,
                preflight.SecurityPartitionHash,
                info,
                targetWidth,
                targetHeight,
                bitmapIdentity,
                svgWorkId,
                PublishRequestedAt: 0,
                targetPixels,
                demand,
                hostAdmission,
                sourceReservationBytes,
                ownerSlot,
                rasterPlan.PhysicalViewport);
            // Allocate the entire handoff before relinquishing any owner. It
            // contains no caller bytes/preparation/cache-entry aliases. Pending
            // disposal and consumed render completion are mutually exclusive.
            var work = new SvgOpenedRenderWork(
                _host, _resources, metadata, contentCacheKey, builtInSourceAliasKey, rasterPlan.UsesTiles,
                openedDocument, openedDocumentUse, ownerSlot, renderWork,
                preparationCancellation, cancellationToken, sessionLifetimeToken,
                openAdmissionEntry, providerAdmissionHeld);
            renderOwnershipTransferred = true;
            openedDocument = null;
            openedDocumentUse = null;
            ownerSlot = null;
            preparationCancellation = null;
            openAdmissionEntry = null;
            providerAdmissionHeld = false;
            return new SvgOpenStageResult(work, null);
        }
        catch (Exception exception)
        {
            _host.HandleSvgWorkFailure(exception, cancellationToken, sessionLifetimeToken,
                resolvedCacheKey, svgFailureCacheKey, renderGeneration);
        }
        finally
        {
            // Clear every source-bearing state-machine alias before returning
            // to the caller, which can then retire its original source lease.
            sourceEntry = null;
            openRequest = null;
            preparation = null;
            rawBytes = Array.Empty<byte>();
            if (hostPreparationLease is not null)
                _host.TryImageCleanup(hostPreparationLease.Dispose);
            try
            {
                _resources.SvgOpenAdmissions.Release(ref openAdmissionEntry);
            }
            catch (Exception exception)
            {
                _host.RecordImageRetirementFailure(exception);
            }
            if (providerAdmissionHeld)
                _host.TryImageCleanup(() => _resources.SvgProviderAdmission.Release());
            if (openedDocument is not null)
                _host.RetireUncommittedSvgDocument(openedDocument);
            if (openedDocumentUse is not null)
                _host.TryImageCleanup(openedDocumentUse.Dispose);
            if (ownerSlot is not null)
                _host.TryImageCleanup(ownerSlot.Dispose);
            if (preparationCancellation is not null)
                _host.TryImageCleanup(preparationCancellation.Dispose);
            if (!renderOwnershipTransferred)
                renderWork.Dispose();
        }

        return new SvgOpenStageResult(null, hostCapacityDeferral);
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
