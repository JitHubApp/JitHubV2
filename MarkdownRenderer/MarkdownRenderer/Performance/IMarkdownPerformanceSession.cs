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
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Windows.Foundation;

namespace MarkdownRenderer.Performance;

/// <summary>Aggregate, privacy-safe resource-preparation counters.</summary>
public sealed class MarkdownPerformanceSnapshot
{
    internal MarkdownPerformanceSnapshot(
        long sourceCacheBytes,
        long sourceCacheHits,
        long imageFetches,
        long imageFetchMilliseconds,
        long imageFetchFailures,
        long imageFetchCancellations,
        long sourceCacheEvictions,
        int pendingImageFetches,
        int activeImageFetches,
        long cpuPreparations,
        long cpuPreparationMilliseconds,
        long scenePreparations,
        long scenePreparationMilliseconds,
        long inFlightSourceBytes,
        long peakInFlightSourceBytes,
        int pendingSourceByteRequests,
        long sceneCacheEstimatedBytes,
        long sceneCacheHits,
        long sceneCacheEvictions)
    {
        SourceCacheBytes = sourceCacheBytes;
        SourceCacheHits = sourceCacheHits;
        ImageFetches = imageFetches;
        ImageFetchMilliseconds = imageFetchMilliseconds;
        ImageFetchFailures = imageFetchFailures;
        ImageFetchCancellations = imageFetchCancellations;
        SourceCacheEvictions = sourceCacheEvictions;
        PendingImageFetches = pendingImageFetches;
        ActiveImageFetches = activeImageFetches;
        CpuPreparations = cpuPreparations;
        CpuPreparationMilliseconds = cpuPreparationMilliseconds;
        ScenePreparations = scenePreparations;
        ScenePreparationMilliseconds = scenePreparationMilliseconds;
        InFlightSourceBytes = inFlightSourceBytes;
        PeakInFlightSourceBytes = peakInFlightSourceBytes;
        PendingSourceByteRequests = pendingSourceByteRequests;
        SceneCacheEstimatedBytes = sceneCacheEstimatedBytes;
        SceneCacheHits = sceneCacheHits;
        SceneCacheEvictions = sceneCacheEvictions;
    }

    /// <summary>Retained source bytes.</summary>
    public long SourceCacheBytes { get; }
    /// <summary>Successful reads from the source cache.</summary>
    public long SourceCacheHits { get; }
    /// <summary>Source resolutions begun by the session.</summary>
    public long ImageFetches { get; }
    /// <summary>Total elapsed source-resolution time, rounded to milliseconds.</summary>
    public long ImageFetchMilliseconds { get; }
    /// <summary>Source resolutions that faulted for a reason other than cancellation.</summary>
    public long ImageFetchFailures { get; }
    /// <summary>Queued or active resolutions canceled before completion.</summary>
    public long ImageFetchCancellations { get; }
    /// <summary>Source cache entries evicted to respect the byte budget.</summary>
    public long SourceCacheEvictions { get; }
    /// <summary>Resolutions waiting for admission or a speculative-work policy.</summary>
    public int PendingImageFetches { get; }
    /// <summary>Resolutions currently inside a host resolver.</summary>
    public int ActiveImageFetches { get; }
    /// <summary>Raster and SVG host-source preparation slots admitted; provider/worker waits are excluded.</summary>
    public long CpuPreparations { get; }
    /// <summary>Total elapsed raster and SVG host-source preparation-slot time, rounded to milliseconds; provider/worker waits are excluded.</summary>
    public long CpuPreparationMilliseconds { get; }
    /// <summary>Scene preparation slots admitted.</summary>
    public long ScenePreparations { get; }
    /// <summary>Conservative weight held only by the completed-scene cache, excluding active layouts and pending handoffs.</summary>
    public long SceneCacheEstimatedBytes { get; }
    /// <summary>Successful completed-scene cache reads; weak reuse from live layouts is not a cache hit.</summary>
    public long SceneCacheHits { get; }
    /// <summary>Completed-scene cache entries removed to respect its weighted budget.</summary>
    public long SceneCacheEvictions { get; }
    /// <summary>Total elapsed scene preparation-slot time, rounded to milliseconds.</summary>
    public long ScenePreparationMilliseconds { get; }
    /// <summary>Bytes admitted for source reads and participating consumer lifetimes; independently bounded caches and legacy resolver allocations are not included.</summary>
    public long InFlightSourceBytes { get; }
    /// <summary>Peak admitted source-buffer bytes since this session was created.</summary>
    public long PeakInFlightSourceBytes { get; }
    /// <summary>Source-buffer reservations waiting for weighted admission.</summary>
    public int PendingSourceByteRequests { get; }
}

/// <summary>
/// Borrowed, opt-in preparation service. This is a package-boundary contract,
/// not a custom-provider extension point: controls accept the implementation
/// from the optional MarkdownRenderer.Performance package and never own it.
/// </summary>
public interface IMarkdownPerformanceSession
{
    /// <summary>Gets the immutable preparation settings.</summary>
    MarkdownPerformanceOptions Options { get; }

    /// <summary>Returns privacy-safe aggregate counters.</summary>
    MarkdownPerformanceSnapshot GetSnapshot();

    /// <summary>Releases retained source and completed-scene cache data without canceling visible work or retiring active layouts.</summary>
    void Trim();
}

internal interface IMarkdownPerformanceSessionInternal : IMarkdownPerformanceSession
{
    CancellationToken LifetimeToken { get; }
    IDisposable? TryBeginRasterWork();
    bool IsDisposed { get; }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    bool IsLayoutDiagnosticCaptureEnabled { get; }
#endif
    event EventHandler? Disposed;
    bool TryGetStyleRoleDemandMask(
        MarkdownDocument document,
        CancellationToken cancellationToken,
        out ulong roleDemandMask);
    ValueTask<MarkdownDocument?> ParseAndPrepareDocumentAsync(
        MarkdownEngine? engine,
        MarkdownDocument? document,
        string? source,
        MarkdownExtensionRegistry legacyRegistry,
        object documentOwner,
        CancellationToken cancellationToken);
    Task<T> ScheduleLayoutWorkAsync<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken);
    bool TryScheduleImageActivation(
        IMarkdownImageActivationDemand demand,
        Action startAsyncWork,
        Action onNotStarted,
        CancellationToken cancellationToken);
    bool TryGetDeferredSceneResult(
        object documentOwner,
        string marker,
        out MarkdownContentFragment? result);
    bool TryGetDeferredSceneResult(
        object documentOwner, string marker,
        out MarkdownContentFragment? result, out object? projectionIdentity);
    bool HasRegisteredDeferredSceneWork(object documentOwner);
    bool IsRegisteredDeferredSceneMarker(object documentOwner, string marker);
#pragma warning disable MR1001 // The optional scene policy integrates with the compatibility control surface.
    MarkdownVisibleSceneReadiness GetVisibleDeferredSceneReadiness(
        MarkdownRendererControl owner,
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision,
        Rect viewport);
#pragma warning restore MR1001
    IMarkdownVectorSceneResidencyPolicy? CreateVectorSceneResidencyPolicy(
        IReadOnlyList<BlockBox> blocks,
        object? documentOwner = null);
    Task<bool> PrepareDeferredSceneBandAsync(
        object documentOwner,
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        CancellationToken cancellationToken);
    IMarkdownImageResidencyPolicy? CreateImageResidencyPolicy(Action requestReconcile);
    Task AdvanceDeferredSceneGeneration(object documentOwner);
    Task ReleaseDeferredSceneDocument(object documentOwner);
    bool ScheduleDeferredScenes(object documentOwner);
    long BeginRasterPreparation(int sourceBytes, int sourceWidth, int sourceHeight);
    void RecordRasterPreparationStage(
        long preparationId,
        MarkdownRasterPreparationStage stage,
        long elapsedStopwatchTicks);
    IMarkdownPerformanceDocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    IMarkdownPerformanceDocumentScope? PrepareDocumentScope(
        object documentOwner,
        string source,
        int registryRevision,
        Markdig.Syntax.MarkdownDocument document,
        MarkdownRenderer.Parsing.SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    void ReleaseDocumentScope(object documentOwner);
    ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken);
    ValueTask<IDisposable> ReserveImageSourceBytesAsync(
        object documentOwner,
        long bytes,
        CancellationToken cancellationToken);
    ValueTask<IMarkdownScenePreparationLease> EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken);
}

internal enum MarkdownVisibleSceneReadiness
{
    NotReady = 0,
    Ready = 1,
    Busy = 2,
    Failed = 3,
}

/// <summary>
/// Optional, session-provided policy for retaining native vector drawing
/// resources in the current lazy-layout band. Boxes keep ownership of their
/// scenes and resources; this policy only coordinates preparation and release.
/// </summary>
internal interface IMarkdownVectorSceneResidencyPolicy : IDisposable
{
    bool HasPendingGraphRebinds => false;

    // Scalar wake state only. A deferred capacity fallback is settled for
    // ordinary layout work, but is not a native-ready fidelity result.
    bool HasCapacityDeferredScenes => false;
    long ActiveCapacityGeneration => 0;

    int ActiveSceneCount { get; }

    long ActiveEstimatedBytes { get; }

    int InlineGeometryIndexBuildCount { get; }

    int CommandGraphEstimateCount { get; }

    int ActiveInlineSceneGeometryIndexCount { get; }

    List<string> VisibleDeferredSceneMarkerScratch { get; }

    bool AreNativeResourcesPrepared(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex);
    bool IsBandWorkSettled(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex) =>
        AreNativeResourcesPrepared(topLevelOrdinals, band, placements, topLevelOrdinalByBlockIndex);
    bool HasFailedScenes(
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex) => false;
    IReadOnlyList<VectorSceneBox> GraphStatusChanges => Array.Empty<VectorSceneBox>();
    void ClearGraphStatusChanges() { }

    /// <summary>Atomically prepares the requested band before releasing its predecessor.</summary>
    bool ReplaceActiveBand(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends deferred markers for visible inline scenes using the active
    /// scalar geometry index. Returns false when that index is missing or stale;
    /// UI readiness callers must fail closed and request background realization.
    /// </summary>
    bool TryAppendVisibleInlineSceneMarkers(
        InlineContainerBox inline,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady);

    /// <summary>Releases partial scene work for a failed band, excluding current pins.</summary>
    void ReleaseUnpinnedResources(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex);
}

/// <summary>
/// An ImageBox-facing lifecycle seam implemented by the base renderer. Residency
/// policy callbacks run only while the owning snapshot's read lease is held;
/// the drain event is a wake-up signal and must not mutate the target itself.
/// </summary>
internal interface IMarkdownImageResidencyTarget
{
    event EventHandler? ResidencyWorkDrained;

    void SuspendResources();

    bool TryReleaseSuspendedResources();

    bool TryResumeResources();
}

/// <summary>
/// Optional session policy that tracks only active and draining ImageBox
/// references. It never owns or terminally disposes boxes.
/// </summary>
internal interface IMarkdownImageResidencyPolicy : IDisposable
{
    /// <summary>
    /// Reconciles the new active viewport images. The optional published list is
    /// supplied only during a plan rebuild, allowing never-visible boxes to be
    /// suspended in that already-linear pass. Scroll reconciliation omits it.
    /// </summary>
    void Reconcile(
        IReadOnlyList<IMarkdownImageResidencyTarget> activeImages,
        IReadOnlyList<IMarkdownImageResidencyTarget>? publishedImages = null);

    /// <summary>Drops borrowed references/subscriptions when the snapshot is retired or replaced.</summary>
    void Reset();
}

internal interface IMarkdownPerformanceLegacyParser
{
    Task<ParsedMarkdown?> ParseLegacyMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken);
}

internal enum MarkdownRasterPreparationStage
{
    SourceStreamOpen = 1,
    DecoderCreation = 2,
    WicPixelDecode = 3,
    Win2DBitmapUpload = 4,
    Win2DDirectLoad = 5,
    CachePublication = 6,
    PreparationAdmission = 7,
}

internal interface IMarkdownScenePreparationLease : IDisposable
{
    CancellationToken CancellationToken { get; }
}

internal interface IMarkdownPerformanceDocumentScope : IMarkdownImageResolver, IDisposable
{
    ValueTask<IMarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken);
    /// <summary>
    /// Returns only scalar dimensions already derived from a prefetched,
    /// source-cache-resident raster. Implementations must not resolve sources,
    /// expose bytes, promote cache recency, or acquire source admission here.
    /// </summary>
    bool TryGetPrefetchedRasterIntrinsicMetadata(
        string source,
        out RasterIntrinsicMetadata metadata)
    {
        metadata = default;
        return false;
    }
    bool IsDisposed { get; }
    CancellationToken CancellationToken { get; }
    bool Matches(
        IMarkdownPerformanceSessionInternal session,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    Task PrefetchAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    ValueTask<IMarkdownImageResolutionLease> ResolveWithSourceLeaseAndActivationTraceAsync(
        string source,
        MarkdownImageResolveContext context,
        MarkdownImageActivationCorrelation correlation,
        CancellationToken cancellationToken);
    ValueTask<MarkdownImageResolution> ResolveWithActivationTraceAsync(
        string source,
        MarkdownImageResolveContext context,
        MarkdownImageActivationCorrelation correlation,
        CancellationToken cancellationToken);
#endif
}
