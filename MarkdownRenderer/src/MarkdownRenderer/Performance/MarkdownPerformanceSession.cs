using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Images;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using Windows.Foundation;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Shares bounded image preparation between document views in one security partition.
/// The host owns this service; controls borrow it and own their document scopes.
/// </summary>
public sealed class MarkdownPerformanceSession :
    IMarkdownPerformanceSessionInternal,
    IMarkdownDocumentScopeOpener,
    IDisposable,
    IAsyncDisposable
{
    private readonly MarkdownPerformanceSessionLifetime _lifetime = new();
    private readonly MarkdownSessionWorkDispatch _workDispatch;
    private readonly MarkdownSessionAdmissions _admissions;
    private readonly MarkdownSharedImageFetcher _fetcher;
    private readonly MarkdownSessionImageServices _imageServices;
    private readonly MarkdownDocumentScopeRegistry _documentScopes;
    private readonly bool _memoryPressureSubscribed;
    private readonly MarkdownDeferredScenePlanRegistry _scenePlans;
    private readonly MarkdownVisibleSceneReadinessProbe _readinessProbe;
    private readonly MarkdownStyleRoleDemandCache _styleRoleDemand;
    private readonly MarkdownSessionParsePipeline _parsePipeline;
    private readonly MarkdownVectorSceneEstimateCache _vectorSceneEstimates = new();
    private readonly VectorNativeResourceAdmission _vectorNativeAdmission =
        VectorNativeResourceAdmission.CreateDefault();

    /// <summary>Creates an opt-in session. Use a distinct session for each account/security partition.</summary>
    public MarkdownPerformanceSession(MarkdownPerformanceOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        MarkdownPerformanceOptionsValidation.Validate(options);
        CompletedSceneCache = new MarkdownCompletedSceneCache(options.SceneCacheBudgetBytes);
        _workDispatch = new MarkdownSessionWorkDispatch(_lifetime);
        _admissions = new MarkdownSessionAdmissions(_lifetime, options);
        SourceCache = new MarkdownImageSourceCache(_lifetime, options.SourceCacheBudgetBytes);
        _fetcher = new MarkdownSharedImageFetcher(_lifetime, _admissions, SourceCache, options);
        _imageServices = new MarkdownSessionImageServices(
            options, _lifetime, _admissions, SourceCache, _fetcher);
        _documentScopes = new MarkdownDocumentScopeRegistry(
            this, this, _lifetime, options.PrefetchDocumentImages);
        _scenePlans = new MarkdownDeferredScenePlanRegistry(this, _lifetime);
        _readinessProbe = new MarkdownVisibleSceneReadinessProbe(this);
        _styleRoleDemand = new MarkdownStyleRoleDemandCache(_lifetime);
        _parsePipeline = new MarkdownSessionParsePipeline(
            _lifetime, _scenePlans, _styleRoleDemand, options.DeferOffscreenScenes);
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            _memoryPressureSubscribed = true;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException)
        {
            // The byte-budgeted LRU still applies in headless and unpackaged hosts.
        }
    }

    // Hosts set this only while profiling. Payloads contain only an
    // in-process owner identity, fixed stage ID, and stopwatch ticks—never source
    // content or resource identifiers. A typed callback avoids runtime event
    // discovery and remains compatible with NativeAOT trimming.
    internal Action<int, int, long>? ParseStageDiagnosticRecorder
    {
        get => _parsePipeline.StageRecorder;
        set => _parsePipeline.StageRecorder = value;
    }

    internal MarkdownCompletedSceneCache CompletedSceneCache { get; }

    // Admission queue depth is sampled only by internal diagnostics/tests. It
    // adds no work to the CPU-preparation hot path.
    internal int PendingCpuPreparations => _admissions.PendingCpuPreparations;

    // Admitted work that shutdown still waits for. Sampled only by internal
    // diagnostics/tests.
    internal int ActiveWork => _lifetime.ActiveWork;

    // The session-wide image source cache. Internal diagnostics/tests sample
    // its retained-entry and capacity counters.
    internal MarkdownImageSourceCache SourceCache { get; }

    // Each control's progressive scene plan. Internal diagnostics/tests look
    // up the plan registered for an owner.
    internal MarkdownDeferredScenePlanRegistry ScenePlans => _scenePlans;

    // The renderer checks this once per layout request so stopwatch reads and
    // native-layout counting are absent unless a host explicitly enables layout diagnostics.
    internal bool IsLayoutDiagnosticCaptureEnabled { get; set; }

    /// <summary>Gets the immutable settings captured at construction.</summary>
    public MarkdownPerformanceOptions Options { get; }

    internal bool IsDisposed => _lifetime.IsDisposed;

    event EventHandler? IMarkdownPerformanceSessionInternal.Disposed
    {
        add
        {
            bool invokeImmediately = !_lifetime.TryAddDisposedHandler(value);
            if (invokeImmediately && value is not null)
                NotifyDisposedHandlers(value);
        }
        remove => _lifetime.RemoveDisposedHandler(value);
    }

    internal long ActiveSourceBytes => _admissions.SourceBytes.ActiveBytes;

    // Internal evidence for cancellation callback failures; see
    // MarkdownPerformanceSessionLifetime.CancellationRetirementFailures.
    internal long CancellationRetirementFailures => _lifetime.CancellationRetirementFailures;

    bool IMarkdownPerformanceSessionInternal.IsDisposed => IsDisposed;
    CancellationToken IMarkdownPerformanceSessionInternal.LifetimeToken => _lifetime.Token;
    IDisposable? IMarkdownPerformanceSessionInternal.TryBeginRasterWork()
    {
        return _lifetime.TryBeginWorkLease();
    }
    bool IMarkdownPerformanceSessionInternal.IsLayoutDiagnosticCaptureEnabled =>
        IsLayoutDiagnosticCaptureEnabled;

    VectorNativeResourceAdmission IMarkdownPerformanceSessionInternal.VectorNativeAdmission =>
        _vectorNativeAdmission;

    bool IMarkdownPerformanceSessionInternal.TryGetStyleRoleDemandMask(
        MarkdownDocument document,
        CancellationToken cancellationToken,
        out ulong roleDemandMask) =>
        _styleRoleDemand.TryGetMask(document, cancellationToken, out roleDemandMask);

    ValueTask<MarkdownRenderer.Document.MarkdownDocument?>
        IMarkdownPerformanceSessionInternal.ParseAndPrepareDocumentAsync(
        MarkdownEngine? engine,
        MarkdownRenderer.Document.MarkdownDocument? document,
        string? source,
        MarkdownRenderer.Parsing.MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken) =>
        _parsePipeline.ParseAndPrepareDocumentAsync(
            engine,
            document,
            source,
            legacyRegistry,
            documentOwner,
            cancellationToken);

    Task<T> IMarkdownPerformanceSessionInternal.ScheduleLayoutWorkAsync<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken) =>
        _workDispatch.ScheduleLayoutWorkAsync(priority, work, cancellationToken);

    bool IMarkdownPerformanceSessionInternal.TryScheduleImageActivation(
        IMarkdownImageActivationDemand demand,
        Action startAsyncWork,
        Action onNotStarted,
        CancellationToken cancellationToken) =>
        _workDispatch.TryScheduleImageActivation(demand, startAsyncWork, onNotStarted, cancellationToken);

    bool IMarkdownPerformanceSessionInternal.TryGetDeferredSceneResult(
        object documentOwner,
        string marker,
        out MarkdownContentFragment? result) =>
        _scenePlans.TryGetResult(documentOwner, marker, out result);

    bool IMarkdownPerformanceSessionInternal.TryGetDeferredSceneResult(
        object documentOwner, string marker,
        out MarkdownContentFragment? result, out object? projectionIdentity) =>
        _scenePlans.TryGetResult(documentOwner, marker, out result, out projectionIdentity);

    bool IMarkdownPerformanceSessionInternal.HasRegisteredDeferredSceneWork(object documentOwner) =>
        _scenePlans.HasRegisteredWork(documentOwner);

    bool IMarkdownPerformanceSessionInternal.IsRegisteredDeferredSceneMarker(
        object documentOwner,
        string marker) =>
        _scenePlans.IsRegisteredMarker(documentOwner, marker);

    MarkdownVisibleSceneReadiness IMarkdownPerformanceSessionInternal.GetVisibleDeferredSceneReadiness(
        MarkdownRendererControl owner,
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision,
        Rect viewport) =>
        _readinessProbe.GetReadiness(
            owner,
            expectedSnapshot,
            expectedGeneration,
            expectedLayoutRevision,
            viewport);

    IMarkdownVectorSceneResidencyPolicy? IMarkdownPerformanceSessionInternal.CreateVectorSceneResidencyPolicy(
        IReadOnlyList<BlockBox> blocks,
        object? documentOwner,
        LayoutPlacementTransformContext? placementTransform)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        MarkdownProgressiveScenePlan? plan = null;
        if (documentOwner is not null)
            _scenePlans.TryGetPlan(documentOwner, out plan);
        if (!Options.DeferOffscreenScenes || IsDisposed)
            return null;

        // Plain, bounded inline trees have no vector resources for a policy to
        // own. Avoid creating an otherwise empty optional policy so Core can
        // build a private lazy-placement candidate without referencing this
        // optional assembly. Any registered scene plan or unknown tree keeps
        // the ordinary policy and remains ineligible for that fast path.
        if (plan is null && MarkdownVectorSceneResidencyPolicy.CanOmitForPrivatePlacement(blocks))
            return null;

        return new MarkdownVectorSceneResidencyPolicy(
            blocks, plan, _vectorNativeAdmission, _vectorSceneEstimates, placementTransform);
    }

    Task<bool> IMarkdownPerformanceSessionInternal.PrepareDeferredSceneBandAsync(
        object documentOwner,
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        CancellationToken cancellationToken) =>
        _scenePlans.PrepareBandAsync(documentOwner, snapshot, band, cancellationToken);

    IMarkdownImageResidencyPolicy? IMarkdownPerformanceSessionInternal.CreateImageResidencyPolicy(
        Action requestReconcile)
    {
        ArgumentNullException.ThrowIfNull(requestReconcile);
        return Options.ReleaseOffscreenImageResources && !IsDisposed
            ? new MarkdownImageResidencyPolicy(requestReconcile)
            : null;
    }

    /// <summary>
    /// Returns diagnostics reported by deferred scenes in the supplied
    /// immutable document. They become available after visible scene work is
    /// published and remain separate from the document's eager diagnostics.
    /// </summary>
    public IReadOnlyList<MarkdownDiagnostic> GetDeferredSceneDiagnostics(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _scenePlans.GetDiagnostics(document);
    }

    Task IMarkdownPerformanceSessionInternal.AdvanceDeferredSceneGeneration(object documentOwner) =>
        _scenePlans.AdvanceGeneration(documentOwner);

    Task IMarkdownPerformanceSessionInternal.ReleaseDeferredSceneDocument(object documentOwner) =>
        _scenePlans.Release(documentOwner);

    bool IMarkdownPerformanceSessionInternal.ScheduleDeferredScenes(object documentOwner) =>
        _scenePlans.Schedule(documentOwner);

    long IMarkdownPerformanceSessionInternal.BeginRasterPreparation(
        int sourceBytes, int sourceWidth, int sourceHeight) =>
        MarkdownRasterPreparationEventSource.Log.Begin(sourceBytes, sourceWidth, sourceHeight);

    void IMarkdownPerformanceSessionInternal.RecordRasterPreparationStage(
        long preparationId,
        MarkdownRasterPreparationStage stage,
        long elapsedStopwatchTicks) =>
        MarkdownRasterPreparationEventSource.Log.RecordStage(
            preparationId, stage, elapsedStopwatchTicks);

    IMarkdownPerformanceDocumentScope IMarkdownPerformanceSessionInternal.OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) => OpenDocument(resolver, context);

    IMarkdownPerformanceDocumentScope?
        IMarkdownPerformanceSessionInternal.PrepareDocumentScope(
        object documentOwner,
        string source,
        int registryRevision,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) =>
        _documentScopes.Prepare(
            documentOwner,
            source,
            registryRevision,
            document,
            safeHtmlPolicy,
            resolver,
            context);

    void IMarkdownPerformanceSessionInternal.ReleaseDocumentScope(object documentOwner)
        => _documentScopes.Release(documentOwner);

    ValueTask<IDisposable> IMarkdownPerformanceSessionInternal.EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken) =>
        EnterCpuPreparationAsync(documentOwner, exclusive, cancellationToken);

    ValueTask<IMarkdownScenePreparationLease> IMarkdownPerformanceSessionInternal.EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        EnterScenePreparationAsync(documentOwner, cancellationToken);

    /// <summary>Returns aggregate counts without source URLs or content.</summary>
    public MarkdownPerformanceSnapshot GetSnapshot()
    {
        long retainedBytes = SourceCache.RetainedBytes;
        return new MarkdownPerformanceSnapshot(
            retainedBytes,
            SourceCache.Hits,
            _fetcher.Fetches,
            _fetcher.FetchMilliseconds,
            _fetcher.FetchFailures,
            _fetcher.FetchCancellations,
            SourceCache.Evictions,
            _fetcher.PendingFetches,
            _fetcher.ActiveFetches,
            _admissions.CpuPreparations,
            _admissions.CpuPreparationMilliseconds,
            _admissions.ScenePreparations,
            _admissions.ScenePreparationMilliseconds,
            _admissions.SourceBytes.ActiveBytes,
            _admissions.SourceBytes.PeakActiveBytes,
            _admissions.SourceBytes.PendingRequests,
            CompletedSceneCache.RetainedWeightBytes,
            CompletedSceneCache.Hits,
            CompletedSceneCache.Evictions);
    }

    /// <summary>Releases source and completed-scene cache retention; active layouts and visible handoffs are unaffected.</summary>
    public void Trim()
    {
        SourceCache.Trim();
        CompletedSceneCache.Trim();
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args) => Trim();

    internal MarkdownPerformanceDocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context)
    {
        return _lifetime.CreateWhileActive(
            this,
            (Session: this, Services: _imageServices, Resolver: resolver, Context: context),
            static state => new MarkdownPerformanceDocumentScope(
                state.Session, state.Services, state.Resolver, state.Context));
    }

    MarkdownPerformanceDocumentScope IMarkdownDocumentScopeOpener.OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context) => OpenDocument(resolver, context);

    internal ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        EnterCpuPreparationAsync(documentOwner, exclusive: false, cancellationToken);

    internal ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken) =>
        _admissions.EnterCpuPreparationAsync(documentOwner, exclusive, cancellationToken);

    async ValueTask<IDisposable> IMarkdownPerformanceSessionInternal.ReserveImageSourceBytesAsync(
        object documentOwner,
        long bytes,
        CancellationToken cancellationToken) =>
        await ReserveSourceByteLeaseAsync(
                documentOwner,
                bytes,
                background: false,
                cancellationToken: cancellationToken,
                requestCancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

    private ValueTask<IDisposable> ReserveSourceByteLeaseAsync(
        object documentOwner,
        long bytes,
        bool background,
        CancellationToken cancellationToken,
        CancellationToken requestCancellationToken) =>
        _admissions.ReserveSourceBytesAsync(
            documentOwner, bytes, background, cancellationToken, requestCancellationToken);

    internal ValueTask<IMarkdownScenePreparationLease> EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken) =>
        _admissions.EnterScenePreparationAsync(documentOwner, cancellationToken);

    // The progressive scene coordinator holds the admission lease until its
    // dispatcher publication is acknowledged. Keep that queue latency out of
    // the scene-preparation metric by recording the completed preparation
    // interval before enqueueing publication. Other callers retain the
    // historical dispose-time measurement.
    internal static void CompleteScenePreparationTiming(IMarkdownScenePreparationLease? lease)
    {
        if (lease is ScenePreparationLease sceneLease)
            sceneLease.CompletePreparationTiming();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task drain;
        TaskCompletionSource<bool> completed;
        EventHandler? disposedHandlers;
        if (!_lifetime.TryBeginShutdown(out drain, out completed, out disposedHandlers))
            return;

        _workDispatch.Dispose();

        ParseStageDiagnosticRecorder = null;
        IsLayoutDiagnosticCaptureEnabled = false;
        _scenePlans.ReleaseAll();
        CompletedSceneCache.Dispose();
        _documentScopes.ReleaseAll();
        NotifyDisposedHandlers(disposedHandlers);

        Task cancellation = _lifetime.CancelAsync();
        if (_memoryPressureSubscribed)
        {
            try { Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased; }
            catch (Exception exception) when (
                exception is System.Runtime.InteropServices.COMException or
                PlatformNotSupportedException or TypeInitializationException)
            {
            }
        }
        Trim();
        SourceCache.ClearResolverIdentities();
        _ = MarkdownSessionShutdown.FinishAsync(
            cancellation, drain, completed, _scenePlans, _workDispatch, _fetcher, _lifetime);
    }

    private void NotifyDisposedHandlers(EventHandler? handlers)
    {
        if (handlers is null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] Disposal observer failed: {exception.Message}");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? completion = _lifetime.ShutdownCompletion;
        if (completion is not null)
            await completion.ConfigureAwait(false);
    }
}
