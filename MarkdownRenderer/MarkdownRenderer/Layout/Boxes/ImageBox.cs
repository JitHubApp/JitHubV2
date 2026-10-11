using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Utilities;
using SvgTileKey = (int X, int Y, int Width, int Height);
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.MarkdownSvgPhysicalViewport PhysicalViewport);
using SvgFailure = (MarkdownRenderer.Images.MarkdownSvgFailureReason Reason, string Description);
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Renders a markdown image. The bitmap is loaded asynchronously and the box
/// self-invalidates the canvas once decoding completes. Until then, the alt
/// text is shown as a placeholder. When alt text is non-empty it is also
/// rendered as a caption below the image.
///
/// SVG content is admitted by the host resource preflight and then opened and
/// rasterized by the optional engine-neutral <see cref="IMarkdownSvgRenderer"/>.
/// The premultiplied raster is uploaded off the UI thread and shared through
/// device-scoped bitmap leases. SVG text remains inside the provider render so
/// transforms, clipping, bidi, and text paths are preserved.
/// </summary>
internal sealed partial class ImageBox : BlockBox
{
    // Renderer-owned resource caches are bounded by retained bytes rather than
    // entry count so a handful of very large images cannot bypass the budget.
    private static readonly long MaxSvgCacheBytes = Environment.Is64BitProcess
        ? 64L * 1024 * 1024
        : 32L * 1024 * 1024;
    private const int MaxFailedUrlEntrnes = 512;
    // Negative caching is optional memoization, not an image admission policy.
    // Count bounds alone must not retain hundreds of multi-megabyte data URIs
    // or host-supplied keys after the owning document has retired.
    private const int MaxFailedCacheKeyLength = 4096;

    private static void RememberFailedUrl(string cacheKey)
    {
        if (cacheKey.Length is 0 or > MaxFailedCacheKeyLength)
            return;
        _failedUrls.TryAdd(cacheKey, 0);
        TrimCache(_failedUrls, MaxFailedUrlEntrnes);
    }

    private static void TrimCache<TValue>(ConcurrentDictionary<string, TValue> cache, int maxEntrnes)
    {
        // NOTE: We intentnonally do NOT dispose evncted values here, even when
        // TValue is IDisposable. CanvasBitmap entries in _bitmapCache are
        // shared by reference with live ImageBox instances (cache-hit boxes
        // alias the cached handle into _bitmap). Disposing under eviction
        // would yank the GPU resource out from under any box still paintnng
        // that URL. Releasing the dictnonary slot is enough — once no live
        // ImageBox holds the reference, the GC + fnnalnzer reclanm the
        // underlying handle. SVG entries are records with no native
        // resources so this is a non-nssue for _svgCache enther way.
        while (cache.Count > maxEntrnes)
        {
            var vnctnm = cache.Keys.FirstOrDefault();
            if (vnctnm is null) break;
            cache.TryRemove(vnctnm, out _);
        }
    }

    /// <summary>Device-independent SVG source state retained across relayout.</summary>
    /// <remarks>
    /// CPU rasters are never retained here. The provider owns parsed trees and
    /// resources; <see cref="SharedCanvasBitmapCache"/> owns uploaded,
    /// device-scoped bitmaps.
    /// </remarks>
    private sealed class SvgCacheEntry(
        byte[] RawBytes,
        Size Intrinsic,
        string? Title,
        string? Desc,
        string ContentHash,
        string SecurityPartitionHash,
        MarkdownSvgDocumentInfo Info,
        long RendererIdentity,
        long ProviderCacheGeneration,
        string? LastBitmapIdentity = null,
        int LastBitmapWidthPixels = 0,
        int LastBitmapHeightPixels = 0,
        MarkdownSvgPhysicalViewport? LastPhysicalViewport = null)
    {
        internal readonly byte[] RawBytes = RawBytes;
        internal readonly Size Intrinsic = Intrinsic;
        internal readonly string? Title = Title;
        internal readonly string? Desc = Desc;
        internal readonly string ContentHash = ContentHash;
        internal readonly string SecurityPartitionHash = SecurityPartitionHash;
        internal readonly MarkdownSvgDocumentInfo Info = Info;
        internal readonly long RendererIdentity = RendererIdentity;
        internal readonly long ProviderCacheGeneration = ProviderCacheGeneration;
        internal readonly string? LastBitmapIdentity = LastBitmapIdentity;
        internal readonly int LastBitmapWidthPixels = LastBitmapWidthPixels;
        internal readonly int LastBitmapHeightPixels = LastBitmapHeightPixels;
        internal readonly MarkdownSvgPhysicalViewport? LastPhysicalViewport = LastPhysicalViewport;

        internal SvgCacheEntry WithLastBitmap(
            string bitmapIdentity,
            int bitmapWidthPixels,
            int bitmapHeightPixels,
            MarkdownSvgPhysicalViewport? physicalViewport) =>
            new(
                RawBytes,
                Intrinsic,
                Title,
                Desc,
                ContentHash,
                SecurityPartitionHash,
                Info,
                RendererIdentity,
                ProviderCacheGeneration,
                bitmapIdentity,
                bitmapWidthPixels,
                bitmapHeightPixels,
                physicalViewport);
    }

    // An accepted UI callback may remain queued after the source-read lease has
    // drained. Carry only weak source identity and scalar/display metadata into
    // that callback; never capture the resolver/provider byte array or a cache
    // entry that owns it.
    private sealed class SvgPublicationMetadata(
        WeakReference<byte[]> SourceIdentity,
        long SourceByteLength,
        IMarkdownSvgRenderer Renderer,
        long RendererIdentity,
        long ProviderCacheGeneration,
        int RenderGeneration,
        bool MetadataWasKnown,
        string ResolvedCacheKey,
        string SvgFailureCacheKey,
        Size Intrinsic,
        string? Title,
        string? Description,
        string ContentHash,
        string SecurityPartitionHash,
        MarkdownSvgDocumentInfo Info,
        int TargetWidthPixels,
        int TargetHeightPixels,
        string BitmapIdentity,
        long WorkId,
        long PublishRequestedAt,
        long TargetPixels,
        MarkdownSvgDemandTicket? Demand = null,
        IMarkdownSvgHostAdmission? HostAdmission = null,
        long SourceReservationBytes = 0,
        SvgHostAdmissionCoordinator.OwnerSlot? OwnerSlot = null,
        MarkdownSvgPhysicalViewport? PhysicalViewport = null)
    {
        internal readonly WeakReference<byte[]> SourceIdentity = SourceIdentity;
        internal readonly long SourceByteLength = SourceByteLength;
        internal readonly IMarkdownSvgRenderer Renderer = Renderer;
        internal readonly long RendererIdentity = RendererIdentity;
        internal readonly long ProviderCacheGeneration = ProviderCacheGeneration;
        internal readonly int RenderGeneration = RenderGeneration;
        internal readonly bool MetadataWasKnown = MetadataWasKnown;
        internal readonly string ResolvedCacheKey = ResolvedCacheKey;
        internal readonly string SvgFailureCacheKey = SvgFailureCacheKey;
        internal readonly Size Intrinsic = Intrinsic;
        internal readonly string? Title = Title;
        internal readonly string? Description = Description;
        internal readonly string ContentHash = ContentHash;
        internal readonly string SecurityPartitionHash = SecurityPartitionHash;
        internal readonly MarkdownSvgDocumentInfo Info = Info;
        internal readonly int TargetWidthPixels = TargetWidthPixels;
        internal readonly int TargetHeightPixels = TargetHeightPixels;
        internal readonly string BitmapIdentity = BitmapIdentity;
        internal readonly long WorkId = WorkId;
        internal readonly long PublishRequestedAt = PublishRequestedAt;
        internal readonly long TargetPixels = TargetPixels;
        internal readonly MarkdownSvgDemandTicket? Demand = Demand;
        internal readonly IMarkdownSvgHostAdmission? HostAdmission = HostAdmission;
        internal readonly long SourceReservationBytes = SourceReservationBytes;
        internal readonly SvgHostAdmissionCoordinator.OwnerSlot? OwnerSlot = OwnerSlot;
        internal readonly MarkdownSvgPhysicalViewport? PhysicalViewport = PhysicalViewport;
    }

    private readonly struct SvgOpenStageResult(
        SvgOpenedRenderWork? Work, SvgHostCapacityDeferral? Deferral)
    {
        public SvgOpenedRenderWork? Work { get; init; } = Work;
        public SvgHostCapacityDeferral? Deferral { get; init; } = Deferral;
    }

    internal struct SvgOpenedRenderOwnership
    {
        private int _state; // 0 pending, 1 consumed, 2 retired

        internal bool TryConsume() =>
            Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        internal bool TryDisposePending() =>
            Interlocked.CompareExchange(ref _state, 2, 0) == 0;

        internal bool TryCompleteConsumed() =>
            Interlocked.CompareExchange(ref _state, 2, 1) == 1;
    }

    /// <summary>
    /// One-shot source-free ownership between actual Open completion and Render.
    /// Pending disposal retires an unconsumed candidate; after consumption only
    /// actual render completion may release documents, cancellation and gates.
    /// </summary>
    private sealed class SvgOpenedRenderWork(
        ImageBox owner, SvgPublicationMetadata metadata, string contentCacheKey,
        string? builtInSourceAliasKey, bool usesTiles, IMarkdownSvgDocument document, SvgDocumentUseLease documentUse,
        SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot, ImageWorkLease imageWork,
        CancellationTokenSource? preparationCancellation, CancellationToken cancellationToken,
        CancellationToken sessionLifetimeToken, BoundedAsyncKeyedGate.Entry? openAdmissionEntry,
        bool providerAdmissionHeld) : IDisposable
    {
        private ImageBox? _owner = owner;
        private SvgOpenedRenderOwnership _ownership;
        private CancellationTokenSource? _preparationCancellation = preparationCancellation;
        private readonly ImageWorkLease _imageWork = imageWork;
        private readonly bool _providerAdmissionHeld = providerAdmissionHeld;
        internal readonly SvgPublicationMetadata Metadata = metadata;
        internal readonly string ContentCacheKey = contentCacheKey;
        internal readonly string? BuiltInSourceAliasKey = builtInSourceAliasKey;
        internal readonly bool UsesTiles = usesTiles;
        internal readonly CancellationToken CancellationToken = cancellationToken;
        internal readonly CancellationToken SessionLifetimeToken = sessionLifetimeToken;
        internal IMarkdownSvgDocument? Document = document;
        internal SvgDocumentUseLease? DocumentUse = documentUse;
        internal SvgHostAdmissionCoordinator.OwnerSlot? OwnerSlot = ownerSlot;
        private BoundedAsyncKeyedGate.Entry? _openAdmissionEntry = openAdmissionEntry;

        internal bool TryConsume() => _ownership.TryConsume();

        internal void TransferTiledOwnership()
        {
            Document = null;
            DocumentUse = null;
            OwnerSlot = null;
        }

        public void Dispose()
        {
            if (_ownership.TryDisposePending())
                Cleanup();
        }

        internal void Complete()
        {
            if (_ownership.TryCompleteConsumed())
                Cleanup();
        }

        private void Cleanup()
        {
            ImageBox? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
                return;
            IMarkdownSvgDocument? document = Document;
            SvgDocumentUseLease? documentUse = DocumentUse;
            SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot = OwnerSlot;
            CancellationTokenSource? cancellation = _preparationCancellation;
            Document = null;
            DocumentUse = null;
            OwnerSlot = null;
            _preparationCancellation = null;
            try
            {
                try
                {
                    _svgOpenAdmissions.Release(ref _openAdmissionEntry);
                }
                catch (Exception exception)
                {
                    owner.RecordImageRetirementFailure(exception);
                }
                if (_providerAdmissionHeld)
                    owner.TryImageCleanup(() => _svgProviderAdmissionGate.Release());
                if (document is not null)
                    owner.RetireUncommittedSvgDocument(document);
                if (documentUse is not null)
                    owner.TryImageCleanup(documentUse.Dispose);
                if (ownerSlot is not null)
                    owner.TryImageCleanup(ownerSlot.Dispose);
            }
            finally
            {
                if (cancellation is not null)
                    owner.TryImageCleanup(cancellation.Dispose);
                _imageWork.Dispose();
            }
        }
    }

    private readonly struct SvgHostCapacityDeferral(
        IMarkdownSvgHostAdmission Admission,
        MarkdownSvgDemandTicket Demand,
        int LoadGeneration,
        long CapacityVersion,
        long RequiredSourceReservationBytes,
        bool RequiresOwnerSlot,
        long OwnerVersionAtDenial,
        long DemandVersionAtDenial,
        long VisualDemandEpochAtDenial)
    {
        internal IMarkdownSvgHostAdmission Admission { get; } = Admission;
        internal MarkdownSvgDemandTicket Demand { get; } = Demand;
        internal int LoadGeneration { get; } = LoadGeneration;
        internal long CapacityVersion { get; } = CapacityVersion;
        internal long RequiredSourceReservationBytes { get; } = RequiredSourceReservationBytes;
        internal bool RequiresOwnerSlot { get; } = RequiresOwnerSlot;
        internal long OwnerVersionAtDenial { get; } = OwnerVersionAtDenial;
        internal long DemandVersionAtDenial { get; } = DemandVersionAtDenial;
        internal long VisualDemandEpochAtDenial { get; } = VisualDemandEpochAtDenial;
    }

    /// <summary>
    /// Coordinates ownership between accepted publication and its rejection or
    /// exception cleanup callback. Once transferred, only the image or tile
    /// dictionary owner may release the lease.
    /// </summary>
    internal struct SvgTilePublicationOwnership
    {
        private int _state;

        internal bool TryTransfer() =>
            Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        internal bool TryReleaseUntransferred() =>
            Interlocked.CompareExchange(ref _state, 2, 0) == 0;
    }

    private sealed class SvgRendererCacheIdentity(long value)
    {
        internal readonly long Value = value;
    }

    private sealed class ImageWorkLease : IDisposable
    {
        private ImageBox? _owner;

        internal ImageWorkLease(ImageBox owner, CancellationToken token)
        {
            _owner = owner;
            Token = token;
        }

        internal readonly CancellationToken Token;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.CompleteImageWork();
    }

    private sealed class SvgDocumentUseState(IMarkdownSvgDocument document)
    {
        internal readonly IMarkdownSvgDocument Document = document;
        internal int ActiveUses;
        internal bool CandidateReserved;
        internal bool Retired;
        internal bool DisposalScheduled;
        internal ImageWorkLease? ReservedDisposalWork;
    }

    private sealed class SvgDocumentUseLease : IDisposable
    {
        private ImageBox? _owner;

        internal SvgDocumentUseLease(ImageBox owner, SvgDocumentUseState state, bool candidate = false)
        {
            _owner = owner;
            State = state;
            IsCandidate = candidate;
        }

        internal readonly SvgDocumentUseState State;
        internal readonly bool IsCandidate;
        internal bool IsOwnedBy(ImageBox owner) => ReferenceEquals(Volatile.Read(ref _owner), owner);

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.CompleteSvgDocumentUse(State, IsCandidate);
    }

    private static readonly WeightedLruCache<string, SvgCacheEntry> _svgCache = new(
        MaxSvgCacheBytes,
        GetSvgCacheEntryWeight,
        comparer: StringComparer.Ordinal,
        keyWeightSelector: static key => 48L + (long)key.Length * sizeof(char));
    private static readonly ConditionalWeakTable<IMarkdownSvgRenderer, SvgRendererCacheIdentity>
        _svgRendererIdentities = new();
    private static readonly BoundedAsyncKeyedGate _svgOpenAdmissions = new(
        maximumTrackedKeys: 256,
        overflowStripeCount: 128);
    // Keep aggregate host admission below the optional provider's default
    // bounded queue. Queue wait is not rendering work and must never consume a
    // document's hard worker deadline or turn a large badge wall into hundreds
    // of false resource-limit failures.
    private static readonly SemaphoreSlim _svgProviderAdmissionGate = new(16, 16);
    private static long _nextSvgRendererIdentity;

    // URLs that have permanently failed to load/uarse. New ImageBox instances
    // for the same URL start in _loadFailed=true so the fatal state survnves
    // the layout rebuild trnggered by the original fanlpre.
    private static readonly ConcurrentDictionary<string, byte> _failedUrls = new();
    private static readonly ConcurrentDictionary<string, SvgFailure> _svgFailures = new();

    private const int MaxSvgBytes = 8 * 1024 * 1024;
    private const long MaxSvgOutputRasterBytes = 64L * 1024 * 1024;
    // Direct2D bitmap limits vary by feature level and a recovering/WARP
    // device can transiently report a larger capability than it can allocate.
    // Keep the untiled path within the Windows-wide guaranteed ceiling so the
    // decision is deterministic across hardware, WARP, and CI machines.
    private const int MaxUntiledSvgDimensionPixels = 16_384;
    private const int MaxPooledSvgUploadBytes = 1024 * 1024;
    internal const int SvgTileSizePixels = 1024;
    private const int MaxRemoteImageBytes = RasterImageResourceBudget.MaxInputBytes;
    // Resolver implementations can have their own bounded network attempt and
    // then switch to a safe alternate representation (for example, from
    // GitHub Camo to the canonical HTTPS origin).  Keep this document-level
    // guard outside two 20-second transport attempts so it remains the final
    // safety net instead of racing the resolver's recovery path.
    private static readonly TimeSpan ImageResolverTimeout = TimeSpan.FromSeconds(45);

    internal readonly MarkdownLayoutContext Context;
    internal readonly string Url;
    public readonly string Alt;
    private readonly SafeHtmlLength? _requestedWidth;
    private readonly SafeHtmlLength? _requestedHeight;
    private readonly MarkdownBuiltInImageSource? _builtInSource;
    private readonly string? _preResolutionCacheKey;
    private string? _activeCacheKey;
    private volatile bool _isSvg;
    private CanvasBitmap? _bitmap;
    private SharedCanvasBitmapCache.Lease? _bitmapLease;
    // Geometry belongs to the lease, not the latest accepted tile plan: the
    // previous whole bitmap remains visible while a replacement is rendering.
    private MarkdownSvgPhysicalViewport? _bitmapPhysicalViewport;
    // Weak identity only: the weighted SVG source cache is the sole long-lived
    // host owner of raw bytes. Reopening after a raster variant miss goes back
    // through the authorized resolver or admitted built-in source path.
    private WeakReference<byte[]>? _svgSourceIdentity;
    private string? _svgAnonymousPartitionKey;
    private long _svgSourceByteLength;
    private Size _svgIntrinsicSize;
    private string? _svgContentHash;
    private string? _svgSecurityPartitionHash;
    private MarkdownSvgDocumentInfo? _svgInfo;
    private IMarkdownSvgDocument? _svgDocument;
    private readonly CancellationTokenSource _imageLifetimeCancellation;
    private readonly object _svgWorkSync = new();
    // Serializes the two-phase local-plan/tile-cache publication without
    // holding the lifecycle lock while coordinator or tile-cache state changes.
    private readonly object _svgPublicationSync = new();
    private ConditionalWeakTable<IMarkdownSvgDocument, SvgDocumentUseState>? _svgDocumentUses;
    private int _svgWorkCount;
    private bool _svgCancellationDisposed;
    private bool _imageRetirementFinalizing;
    private bool _imageRetirementCompleted;
    private const int MaximumRetirementFailureDetails = 8;
    private List<Exception>? _imageRetirementFailures;
    private int _omittedRetirementFailureCount;
    private List<Task>? _imageRetirementLateWork;
    private TaskCompletionSource? _imageWorkDrained;
    private TaskCompletionSource? _imageRetirementCompletion;
    private int _svgBitmapWidthPixels;
    private int _svgBitmapHeightPixels;
    private MarkdownSvgPhysicalViewport? _svgPhysicalViewport;
    private Size _rasterIntrinsicSize;
    private MarkdownSvgFailureReason? _svgFailureReason;
    private string? _svgFailureDescription;
    private Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> _svgTiles = new();
    // Keep the original CTS map stable for lifecycle cleanup. The parallel
    // work-owner map carries only immutable plan/ticket identity for parking
    // and exact stale-completion rejection.
    private readonly Dictionary<SvgTileKey, CancellationTokenSource> _svgTilesInFlight = new();
    private readonly Dictionary<SvgTileKey, SvgTileWorkOwner> _svgTileWorkOwners = new();
    private readonly object _svgTileLock = new();
    private object? _svgTileCacheTransitionToken;
    private bool _svgUsesTiles;
    private bool _svgHasRenderedTile;
    private int _svgRenderGeneration;
    private long _svgRendererIdentity;
    private long _svgProviderCacheGeneration;
    // Installed only with accepted UI metadata. A replacement Open can adopt
    // its document before that publication; never combine it with the prior
    // image's pixel dimensions, generation, or GPU-cache identity.
    private SvgTileRenderPlan? _svgTileRenderPlan;
    // A pressure-park transition temporarily closes tile admission while it
    // detaches the accepted document. It is never a source/document owner.
    private SvgTileRenderPlan? _svgParkingPlan;
    private MarkdownSvgDemandTicket? _svgLoadDemandTicket;
    private int _svgLoadDemandGeneration;
    private readonly object _svgHostDeferredSync = new();
    private SvgHostAdmissionCoordinator.PendingWaiter? _svgHostDeferredWaiter;
    private object? _svgHostDeferredWaiterIdentity;
    private object? _svgHostDeferredRegistrationAttempt;
    private object? _svgHostDeferredDenialIdentity;
    private WeakReference<IMarkdownSvgHostAdmission>? _svgHostDeferredAdmission;
    private int _svgHostDeferred;
    private long _svgHostDeferredDemandVersion;
    private long _svgHostDeferredCapacityVersion;
    private long _svgHostDeferredOwnerVersion;
    private long _svgHostDeferredVisualDemandEpoch;
    private long _svgHostDeferredRequiredSourceBytes;
    private bool _svgHostDeferredRequiresOwnerSlot;
    private bool _svgHostDeferredCapacityReady;
    // Instance-scoped managed test seam; normal retries use the two existing
    // non-inline queues without allocating a test callback.
    private Func<Action, bool>? _svgDeferredRetryEnqueueOverride = null;
    private bool _svgPressureParked;
    private long _svgPressureParkPublicationEpoch;
    private string? _svgTileCacheIdentityPrefix;
    private int _svgTileCacheWidthPixels;
    private int _svgTileCacheHeightPixels;
    private MarkdownSvgPhysicalViewport? _svgTileCachePhysicalViewport;
    private Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? _svgFallbackTiles;
    private string? _svgFallbackIdentityPrefix;
    private int _svgFallbackWidthPixels;
    private int _svgFallbackHeightPixels;
    private MarkdownSvgPhysicalViewport? _svgFallbackPhysicalViewport;
    private int _svgVisibleFirstTileX;
    private int _svgVisibleFirstTileY;
    private int _svgVisibleLastTileX;
    private int _svgVisibleLastTileY;
    private bool _svgVisibleTileRangeKnown;
    // -1 means the accepted plan has not received a viewport pass yet, 0 is
    // known offscreen, and 1 is in the current viewport.
    private int _svgCurrentViewportVisible = -1;
    private long _svgViewportObservationVersion;
    private long _svgHostVisualDemandEpoch;
    private bool _svgPressureDemandSignatureKnown;
    private int _svgPressureDemandFirstX;
    private int _svgPressureDemandFirstY;
    private int _svgPressureDemandLastX;
    private int _svgPressureDemandLastY;
    private int _svgPressureDemandWidth;
    private int _svgPressureDemandHeight;
    private MarkdownSvgPhysicalViewport? _svgPressureDemandPhysicalViewport;
    private const int SvgHostDeferredNone = 0;
    private const int SvgHostDeferredWaiting = 1;
    private const int SvgHostDeferredRetryQueued = 2;

    private sealed class SvgTileWorkOwner(
        SvgTileRenderPlan plan,
        CancellationTokenSource cancellation,
        MarkdownSvgDemandTicket demand)
    {
        internal readonly SvgTileRenderPlan Plan = plan;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly MarkdownSvgDemandTicket Demand = demand;
        // 0=active, 1=terminal failure claimed, 2=retired/reassigned,
        // 3=terminal failure map invalidation in progress.
        // The transition is coordinated while holding _svgTileLock.
        internal int State;
    }

    private readonly struct SvgDeferredLoadWork(
        ImageBox owner,
        MarkdownSvgDemandTicket demand,
        int loadGeneration,
        object? denialIdentity)
    {
        internal readonly ImageBox Owner = owner;
        internal readonly MarkdownSvgDemandTicket Demand = demand;
        internal readonly int LoadGeneration = loadGeneration;
        internal readonly object? DenialIdentity = denialIdentity;
    }

    internal sealed class SvgTileRenderPlan(
        IMarkdownSvgDocument document,
        string identityPrefix,
        int fullWidthPixels,
        int fullHeightPixels,
        int renderGeneration,
        MarkdownSvgDemandTicket? demand = null,
        IMarkdownSvgHostAdmission? hostAdmission = null,
        long sourceReservationBytes = 0,
        SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot = null,
        MarkdownSvgPhysicalViewport? physicalViewport = null)
    {
        // Serializes the short viewport-state mutation with the final owner
        // detach in pressure parking. It is per-plan so a late release from an
        // obsolete operation cannot reopen a replacement plan's gate.
        private int _viewportMutationState;

        internal readonly IMarkdownSvgDocument Document = document;
        internal readonly string IdentityPrefix = identityPrefix;
        internal readonly int FullWidthPixels = fullWidthPixels;
        internal readonly int FullHeightPixels = fullHeightPixels;
        internal readonly MarkdownSvgPhysicalViewport PhysicalViewport = physicalViewport ??
            new MarkdownSvgPhysicalViewport(fullWidthPixels, fullHeightPixels);
        internal readonly int RenderGeneration = renderGeneration;
        internal readonly MarkdownSvgDemandTicket Demand = demand ??
            new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Visible);
        internal readonly IMarkdownSvgHostAdmission? HostAdmission = hostAdmission;
        internal readonly object? AdmissionDomain = ownerSlot?.AdmissionDomain;
        internal readonly long SourceReservationBytes = sourceReservationBytes;
        internal readonly SvgHostAdmissionCoordinator.OwnerSlot? OwnerSlot = ownerSlot;

        // 0=open, 1=viewport mutation in progress, 2=parking commit claimed.
        internal bool TryBeginViewportMutation() =>
            Interlocked.CompareExchange(ref _viewportMutationState, 1, 0) == 0;

        internal void EndViewportMutation() =>
            Interlocked.CompareExchange(ref _viewportMutationState, 0, 1);

        internal bool TryClaimViewportParking() =>
            Interlocked.CompareExchange(ref _viewportMutationState, 2, 0) == 0;

        internal void EndViewportParking() =>
            Interlocked.CompareExchange(ref _viewportMutationState, 0, 2);

        internal int ViewportMutationState => Volatile.Read(ref _viewportMutationState);
    }

    private sealed class SvgTileCacheTransition(
        object token,
        SvgTileRenderPlan? previousPlan,
        SvgTileRenderPlan? nextPlan,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> expectedTiles,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? expectedFallbackTiles,
        string? previousIdentityPrefix,
        int previousWidthPixels,
        int previousHeightPixels,
        bool sameIdentity,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? replacementTiles,
        SharedCanvasBitmapCache.Lease[]? retiredTiles,
        SharedCanvasBitmapCache.Lease[]? retiredFallbackTiles,
        CancellationTokenSource[] work,
        bool restoreFallback = false,
        string? restoredFallbackIdentityPrefix = null,
        int restoredFallbackWidthPixels = 0,
        int restoredFallbackHeightPixels = 0,
        MarkdownSvgPhysicalViewport? previousPhysicalViewport = null,
        MarkdownSvgPhysicalViewport? restoredFallbackPhysicalViewport = null)
    {
        internal readonly object Token = token;
        internal readonly SvgTileRenderPlan? PreviousPlan = previousPlan;
        internal readonly SvgTileRenderPlan? NextPlan = nextPlan;
        internal readonly Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> ExpectedTiles = expectedTiles;
        internal readonly Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? ExpectedFallbackTiles = expectedFallbackTiles;
        internal readonly string? PreviousIdentityPrefix = previousIdentityPrefix;
        internal readonly int PreviousWidthPixels = previousWidthPixels;
        internal readonly int PreviousHeightPixels = previousHeightPixels;
        internal readonly bool SameIdentity = sameIdentity;
        internal readonly Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? ReplacementTiles = replacementTiles;
        internal readonly SharedCanvasBitmapCache.Lease[]? RetiredTiles = retiredTiles;
        internal readonly SharedCanvasBitmapCache.Lease[]? RetiredFallbackTiles = retiredFallbackTiles;
        internal readonly CancellationTokenSource[] Work = work;
        internal readonly bool RestoreFallback = restoreFallback;
        internal readonly string? RestoredFallbackIdentityPrefix = restoredFallbackIdentityPrefix;
        internal readonly int RestoredFallbackWidthPixels = restoredFallbackWidthPixels;
        internal readonly int RestoredFallbackHeightPixels = restoredFallbackHeightPixels;
        internal readonly MarkdownSvgPhysicalViewport? PreviousPhysicalViewport = previousPhysicalViewport;
        internal readonly MarkdownSvgPhysicalViewport? RestoredFallbackPhysicalViewport = restoredFallbackPhysicalViewport;
    }
    private CanvasTextLayout? _placeholder;
    private CanvasTextLayout? _caption;
    private volatile bool _loadStarted;
    private bool _loadFailed;
    private bool _isInlineLayout;
    private volatile bool _disposed;
    private float _availableWidth;
    private float _imageWidth;
    private float _imageHeight;
    private float _captionHeight;
    private readonly object _inlineMeasureSync = new();
    private bool _inlineMeasureCheckpointActive;
    private int _inlineMeasureCheckpointGeneration;
    private CanvasTextLayout? _inlineMeasureOriginalPlaceholder;
    private NativeResourceRetirementRecord? _inlineMeasureOriginalPlaceholderRetirementRecord;
    private CanvasTextLayout? _inlineMeasureOriginalCaption;
    private NativeResourceRetirementRecord? _inlineMeasureOriginalCaptionRetirementRecord;
    private int _nativeLayoutGeneration;
    private NativeResourceRetirementRecord? _placeholderRetirementRecord;
    private NativeResourceRetirementRecord? _captionRetirementRecord;
    private readonly NativeResourceRetirement _nativeRetirement;
    private readonly Action<IDisposable>? _nativeDisposer;
    private readonly Action<CanvasTextLayout>? _nativeLayoutOptionsSetter;
    // Accessnbnlity metadata extracted from the SVG root, set after a successful
    // load. Null when the SVG is mnssnng <title>/<desc>, the asset is a bitmap,
    // or extraction failed.
    private string? _svgTitle;
    private string? _svgDesc;

    /// <summary>Raised when the asset fnnnshes loading and a repaint is requnred.
    /// The event arg's <see cref="LoadCompletedEventArgs.LayoutInvalidated"/>
    /// indicates whether the host must re-run layout (intrinsic size may have
    /// changed) or merely repaint. Always raised on the UI thread.</summary>
    public event EventHandler<LoadCompletedEventArgs>? LoadCompleted;

    private void RaiseLoadCompleted(bool layoutInvalidated) =>
        LoadCompleted?.Invoke(this, LoadCompletedEventArgs.For(layoutInvalidated));

    public CanvasHorizontalAlignment ContentAlignment { get; set; } = CanvasHorizontalAlignment.Left;

    // A promoted Markdown image still participates in its paragraph's text
    // line box. Kept zero for standalone/HTML inline images, whose owner lays
    // out the line itself.
    internal float MinimumParagraphLineHeight { get; set; }

    public ImageBox(
        MarkdownLayoutContext context,
        string url,
        string alt,
        SafeHtmlLength? requestedWidth = null,
        SafeHtmlLength? requestedHeight = null)
        : this(context, url, alt, requestedWidth, requestedHeight, NativeResourceRetirement.Shared, null, null)
    {
    }

    internal ImageBox(
        MarkdownLayoutContext context,
        string url,
        string alt,
        NativeResourceRetirement nativeRetirement,
        Action<IDisposable>? nativeDisposer = null,
        Action<CanvasTextLayout>? nativeLayoutOptionsSetter = null)
        : this(context, url, alt, null, null, nativeRetirement, nativeDisposer, nativeLayoutOptionsSetter)
    {
    }

    internal ImageBox(
        MarkdownLayoutContext context,
        string url,
        string alt,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight,
        NativeResourceRetirement nativeRetirement,
        Action<IDisposable>? nativeDisposer = null,
        Action<CanvasTextLayout>? nativeLayoutOptionsSetter = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nativeRetirement);
        if ((nativeDisposer is not null || nativeLayoutOptionsSetter is not null) &&
            !nativeRetirement.AllowsTestDisposer)
            throw new ArgumentException("Native fault plans require an isolated retirement owner.", nameof(nativeRetirement));
        Context = context;
        _nativeRetirement = nativeRetirement;
        _nativeDisposer = nativeDisposer;
        _nativeLayoutOptionsSetter = nativeLayoutOptionsSetter;
        Url = url ?? string.Empty;
        Alt = alt ?? string.Empty;
        _requestedWidth = requestedWidth;
        _requestedHeight = requestedHeight;
        _imageLifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            Context.ImageCancellationToken);
        _builtInSource = MarkdownBuiltInImageSourcePolicy.TryResolve(
            Url,
            Context.ImageBaseUri,
            out MarkdownBuiltInImageSource builtInSource)
            ? builtInSource
            : null;
        _isSvg = SvgIntrinsics.LooksLikeSvg(Url) ||
                 SvgIntrinsics.LooksLikeSvg(_builtInSource?.Uri.AbsoluteUri);
        Margin = new Thickness(0, 6, 0, 6);
        if (string.IsNullOrEmpty(Url))
        {
            _loadStarted = true;
            _loadFailed = true;
            return;
        }

        // A host resolver may apply authentication and account partitioning. Its source URL is
        // therefore not a safe process-wide cache identity; wait for the resolver-provided key.
        bool canUseSourceBeforeResolution =
            MarkdownImageCacheIdentityPolicy.CanUseSourceBeforeResolution(
                Context.ImageResolver is not null,
                Url);
        if (canUseSourceBeforeResolution &&
            Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            _preResolutionCacheKey = _builtInSource?.CacheKey ?? Url;
        }
        else if (canUseSourceBeforeResolution && _builtInSource is { CanLoadWithoutResolver: true } source)
        {
            _preResolutionCacheKey = source.CacheKey;
        }
        _activeCacheKey = _preResolutionCacheKey;

        if (_preResolutionCacheKey is { } cacheKey && _failedUrls.ContainsKey(cacheKey))
        {
            // Preserve transport/bitmap failure state across rebuilds. SVG
            // provider failures use a separate renderer-and-generation-scoped
            // cache after the bytes have been securely preflighted.
            _loadFailed = true;
            _loadStarted = true;
            return;
        }

        if (_preResolutionCacheKey is { } bitmapCacheKey && !_isSvg &&
            TryAcquireSourceSizedRasterBitmap(bitmapCacheKey, out var cachedLease) &&
            cachedLease is not null)
        {
            ReplaceBitmap(cachedLease.Bitmap, cachedLease);
            _loadStarted = true;
            return;
        }

        if (_preResolutionCacheKey is { } svgCacheKey && _svgCache.TryGetValue(svgCacheKey, out _))
            _isSvg = true;

        if (!_isSvg)
            return;

        // SVG cache hit path. If the cached rasterized bitmap was produced
        // with the current theme color + device pixel scale, materialize it
        // synchronously so the very first paint shows the image — no async
        // uass through ProcessCachedSvgAsync, no placeholder flash.
        if (_preResolutionCacheKey is { } cachedSvgKey && _svgCache.TryGetValue(cachedSvgKey, out var entry))
        {
            bool compatible = IsSvgCacheEntryCurrent(entry);
            if (compatible)
            {
                RememberSvgSource(entry.RawBytes);
                _svgIntrinsicSize = entry.Intrinsic;
                _svgTitle = entry.Title;
                _svgDesc = entry.Desc;
                _svgContentHash = entry.ContentHash;
                _svgSecurityPartitionHash = entry.SecurityPartitionHash;
                _svgInfo = entry.Info;
                _svgRendererIdentity = entry.RendererIdentity;
                _svgProviderCacheGeneration = entry.ProviderCacheGeneration;
            }

            // Retain the prior good device bitmap while an exact DPI, theme,
            // or size replacement is prepared. Paint scales this immutable
            // fallback and swaps it only after the new lease is ready.
            if (compatible &&
                entry.LastBitmapIdentity is { } previousIdentity &&
                SharedCanvasBitmapCache.TryAcquire(
                    Context.ResourceCreator.Device,
                    previousIdentity,
                    out SharedCanvasBitmapCache.Lease? previousLease) &&
                previousLease is not null)
            {
                if (TryAdmitCachedBitmapOutput(previousLease))
                {
                    ReplaceBitmap(previousLease.Bitmap, previousLease, entry.LastPhysicalViewport);
                    _svgBitmapWidthPixels = entry.LastBitmapWidthPixels;
                    _svgBitmapHeightPixels = entry.LastBitmapHeightPixels;
                    _svgPhysicalViewport = entry.LastPhysicalViewport;
                }
                else
                    TryImageCleanup(previousLease.Dispose);
            }
        }
    }

    /// <summary>The alt text supplied for this image (empty if none).</summary>

    /// <summary>The host-facing image source used by target-aware commands.</summary>


    /// <summary>SVG &lt;title&gt; element value, or null. Used by the automatnon
    /// ueer as the accessnble name when rncher than alt.</summary>
    public string? SvgTitle => _svgTitle;

    /// <summary>SVG &lt;desc&gt; element value, or null. Used by the automatnon
    /// ueer as <c>HeluText</c> for screen-reader descrnutnon.</summary>
    public string? SvgDesc => _bitmap is null
        ? _svgFailureDescription ?? _svgDesc
        : _svgDesc;

    /// <summary>Typed provider failure for diagnostics and focused tests.</summary>
    internal MarkdownSvgFailureReason? SvgFailureReason => _svgFailureReason;

    /// <summary>Physical SVG raster dimensions currently presented by this box.</summary>
    internal (int Width, int Height) SvgRasterPixelSize =>
        (_svgBitmapWidthPixels, _svgBitmapHeightPixels);

    /// <summary>True when this box is using bounded visible SVG tiles.</summary>
    internal bool UsesSvgTilesForAutomation => _svgUsesTiles;

    /// <summary>Number of admitted image operations that have not drained.</summary>
    internal int ActiveImageWorkCountForTests
    {
        get
        {
            lock (_svgWorkSync)
                return _svgWorkCount;
        }
    }

    /// <summary>True after disposal and all admitted image operations drained.</summary>
    internal bool ImageLifetimeDisposedForTests
    {
        get
        {
            lock (_svgWorkSync)
                return _svgCancellationDisposed;
        }
    }

    /// <summary>
    /// Completes after box cancellation callbacks, admitted source/provider
    /// work, queued UI publications, and native-resource release have drained.
    /// It is intentionally a task-only lifetime handle; snapshots do not retain
    /// this box graph while awaiting it.
    /// </summary>
    internal Task RetirementTask
    {
        get
        {
            lock (_svgWorkSync)
                return _imageRetirementCompletion?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// True when the active representation is SVG. Before host resolution completes, this may
    /// temporarily reflect a source-URI hint; resolved bytes and MIME metadata supersede it.
    /// </summary>
    public bool IsSvg => _isSvg;

    /// <summary>Test-only: returns the cached bitmap, if any.</summary>
    public CanvasBitmap? Bitmap => _bitmap;

    internal SharedCanvasBitmapCache.Lease? AcquireBitmapLease()
        => Volatile.Read(ref _bitmapLease)?.TryClone();

    // Candidate-only raster clone. No source bytes, asynchronous work, SVG
    // documents/tiles, or old generation cancellation state cross the boundary.
    internal bool TryCreateCompletedRasterPlacementClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        placement = null;
        expectedSourceLease = null;
        if (!IsRasterPlacementContextCompatible(targetContext))
            return false;
        if (targetContext.ImageCancellationToken.IsCancellationRequested ||
            targetContext.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true })
            return false;

        // A candidate gets its own current-generation build context; never read
        // or forward this box's old build token.
        targetContext.CancellationToken.ThrowIfCancellationRequested();

        SharedCanvasBitmapCache.Lease? sourceLease;
        lock (_svgWorkSync)
        {
            if (!IsCompletedRasterPlacementEligibleNoLock())
                return false;
            sourceLease = Volatile.Read(ref _bitmapLease);
            if (sourceLease is null || _bitmap is null)
                return false;
        }

        SharedCanvasBitmapCache.Lease? candidateLease = sourceLease.TryClone();
        if (candidateLease is null)
            return false;

        try
        {
            bool stillCurrent;
            lock (_svgWorkSync)
            {
                stillCurrent = IsCompletedRasterPlacementEligibleNoLock() &&
                    ReferenceEquals(Volatile.Read(ref _bitmapLease), sourceLease) &&
                    _bitmap is not null;
            }
            if (!stillCurrent)
                return false;

            placement = new ImageBox(targetContext, this, candidateLease);
            candidateLease = null;
            expectedSourceLease = sourceLease;
            return true;
        }
        finally
        {
            candidateLease?.Dispose();
        }
    }

    /// <summary>
    /// Creates a private image placement from either a completed immutable
    /// bitmap or a never-started descriptor. The latter is deliberately much
    /// narrower than a pending-load clone: it carries no source task, bytes,
    /// lease, callback, or old cancellation state, and it cannot invoke a
    /// resolver or cache lookup while the source snapshot is being captured.
    /// </summary>
    internal bool TryCreatePrivateImagePlacementClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        placement = null;
        expectedSourceLease = null;
        svgMetadataFence = null;
        isIdleDescriptor = false;
        expectedIdleLoadDemandGeneration = 0;

        if (!IsRasterPlacementContextCompatible(targetContext))
            return false;
        if (targetContext.ImageCancellationToken.IsCancellationRequested ||
            targetContext.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true })
        {
            return false;
        }

        targetContext.CancellationToken.ThrowIfCancellationRequested();

        // Preserve the established completed bitmap/SVG paths and their exact
        // lease/metadata fences. Only if there is no completed representation
        // do we consider a resource-free, never-started descriptor.
        if (TryCreateCompletedRasterPlacementClone(
                targetContext,
                out ImageBox? completedPlacement,
                out SharedCanvasBitmapCache.Lease? completedLease,
                out CompletedSvgPlacementMetadataFence? completedSvgFence))
        {
            if (completedPlacement is null || completedLease is null)
            {
                if (completedPlacement is not null)
                    targetContext.DiscardConstructionBlock(completedPlacement);
                completedPlacement = null;
                completedLease = null;
                return false;
            }

            placement = completedPlacement;
            expectedSourceLease = completedLease;
            svgMetadataFence = completedSvgFence;
            return true;
        }

        // First observe auxiliary ownership outside the lifecycle locks. The
        // generation/work gate below is the authoritative race fence; the
        // second observation prevents a stale waiter/retirement state from
        // being mistaken for a virgin descriptor.
        if (!AreSvgPlacementAuxiliaryStatesClear() ||
            !HasNoBitmapOutputPlacementWaiterState())
        {
            return false;
        }

        ImageBox? idlePlacement = null;
        try
        {
            lock (_svgWorkSync)
            {
                lock (_svgHostDeferredSync)
                {
                    if (!IsUnstartedImagePlacementEligibleNoLock(expectedGeneration: 0))
                        return false;

                    // This private constructor copies only immutable descriptor
                    // values and scalar placement inputs. In particular it never
                    // enters the ordinary constructor's synchronous cache probe.
                    idlePlacement = new ImageBox(targetContext, this);
                }
            }

            // Keep construction ownership until every post-construction fence
            // has passed. Cancellation here must still retire the fresh linked
            // lifetime source rather than strand it in the candidate ledger.
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            if (!AreSvgPlacementAuxiliaryStatesClear() ||
                !HasNoBitmapOutputPlacementWaiterState() ||
                !IsCurrentIdleImagePlacement(expectedLoadDemandGeneration: 0))
            {
                return false;
            }

            placement = idlePlacement;
            idlePlacement = null;
            isIdleDescriptor = true;
            expectedIdleLoadDemandGeneration = 0;
            return true;
        }
        finally
        {
            if (idlePlacement is not null)
                targetContext.DiscardConstructionBlock(idlePlacement);
        }
    }

    /// <summary>
    /// Allocation-free first-pass classification for a private image placement.
    /// It uses the same completed-image and pristine-idle gates as the clone
    /// factory, but never clones a bitmap lease or starts resolver/provider work.
    /// </summary>
    internal bool TryClassifyPrivateImagePlacement(
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        isIdleDescriptor = false;
        expectedIdleLoadDemandGeneration = 0;

        if (!IsRasterPlacementContextCompatible(targetContext) ||
            targetContext.CancellationToken.IsCancellationRequested ||
            targetContext.ImageCancellationToken.IsCancellationRequested ||
            targetContext.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true })
        {
            return false;
        }

        // Do not acquire a lease here. This source snapshot already owns its
        // bitmap reference; the later clone operation repeats these fences and
        // obtains its independent lease only after whole-tree preflight passes.
        lock (_svgWorkSync)
        {
            if (IsCompletedRasterPlacementEligibleNoLock() &&
                Volatile.Read(ref _bitmapLease) is not null &&
                _bitmap is not null)
            {
                return true;
            }
        }

        // The full-SVG classifier performs only immutable metadata/reference
        // checks. It does not query the provider generation or create a lease.
        if (Context.SvgRenderer is not null &&
            TryCaptureCompletedSvgPlacement(
                out SharedCanvasBitmapCache.Lease? completedSvgLease,
                out _) &&
            completedSvgLease is not null)
        {
            return true;
        }

        if (!allowIdleImagePlacements ||
            !AreSvgPlacementAuxiliaryStatesClear() ||
            !HasNoBitmapOutputPlacementWaiterState())
        {
            return false;
        }

        lock (_svgWorkSync)
        {
            lock (_svgHostDeferredSync)
            {
                if (!IsUnstartedImagePlacementEligibleNoLock(expectedGeneration: 0))
                    return false;
            }
        }

        // Cancellation can race the lifecycle-lock inspection. Fail closed so
        // the tree preflight never approves a candidate already being abandoned.
        if (targetContext.CancellationToken.IsCancellationRequested ||
            targetContext.ImageCancellationToken.IsCancellationRequested)
        {
            return false;
        }

        isIdleDescriptor = true;
        expectedIdleLoadDemandGeneration = 0;
        return true;
    }

    /// <summary>
    /// Final UI-commit fence for a descriptor-only clone. A load attempt is
    /// monotonic for this ImageBox even when a queued activation later resets
    /// <c>_loadStarted</c>; the zero demand generation therefore rejects any
    /// source which began resolving while the candidate was being measured.
    /// </summary>
    internal bool IsCurrentIdleImagePlacement(int expectedLoadDemandGeneration)
    {
        if (expectedLoadDemandGeneration != 0 ||
            !AreSvgPlacementAuxiliaryStatesClear() ||
            !HasNoBitmapOutputPlacementWaiterState())
        {
            return false;
        }

        lock (_svgWorkSync)
        {
            lock (_svgHostDeferredSync)
            {
                return IsUnstartedImagePlacementEligibleNoLock(expectedLoadDemandGeneration);
            }
        }
    }

    private bool IsUnstartedImagePlacementEligibleNoLock(int expectedGeneration)
    {
        // Caller holds _svgWorkSync then _svgHostDeferredSync, the same order
        // used by StartLoad after acquiring its work lease.
        return expectedGeneration == 0 &&
            !_disposed && !_resourcesSuspended &&
            !_imageRetirementFinalizing && !_imageRetirementCompleted &&
            _imageRetirementCompletion is null &&
            _svgWorkCount == 0 &&
            !_loadStarted && !_loadFailed &&
            Volatile.Read(ref _svgLoadDemandGeneration) == expectedGeneration &&
            Volatile.Read(ref _svgLoadDemandTicket) is null &&
            !_imageLifetimeCancellation.IsCancellationRequested &&
            _imageResidencyCancellation is null &&
            _bitmap is null && Volatile.Read(ref _bitmapLease) is null &&
            !_hasPresentedImage &&
            _rasterIntrinsicSize.Width == 0 && _rasterIntrinsicSize.Height == 0 &&
            _svgSourceIdentity is null && _svgSourceByteLength == 0 &&
            _svgAnonymousPartitionKey is null &&
            _svgIntrinsicSize.Width == 0 && _svgIntrinsicSize.Height == 0 &&
            _svgTitle is null && _svgDesc is null &&
            _svgContentHash is null && _svgSecurityPartitionHash is null &&
            _svgInfo is null && _svgDocument is null &&
            _svgRendererIdentity == 0 && _svgProviderCacheGeneration == 0 &&
            Volatile.Read(ref _svgRenderGeneration) == 0 &&
            _svgBitmapWidthPixels == 0 && _svgBitmapHeightPixels == 0 &&
            Volatile.Read(ref _svgTileRenderPlan) is null &&
            Volatile.Read(ref _svgParkingPlan) is null &&
            !_svgUsesTiles && !_svgHasRenderedTile && !_svgPressureParked &&
            !_svgPressureDemandSignatureKnown &&
            _svgFailureReason is null && _svgFailureDescription is null &&
            !Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !(_preResolutionCacheKey?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ?? false) &&
            _builtInSource is not { Kind: MarkdownBuiltInImageSourceKind.Data };
    }

    private bool HasNoBitmapOutputPlacementWaiterState()
    {
        lock (_bitmapOutputSync)
        {
            return _bitmapOutputWaiter is null &&
                _bitmapOutputWaiterOwner is null &&
                _bitmapOutputImageCancellation.Equals(default(CancellationTokenRegistration)) &&
                _bitmapOutputSessionCancellation.Equals(default(CancellationTokenRegistration)) &&
                _bitmapOutputDemand is null &&
                _bitmapOutputWaiterEpoch == 0 &&
                _bitmapOutputRecoverySignaledEpoch == 0 &&
                _bitmapOutputRequiredBytes == 0 &&
                _bitmapOutputCapacityVersion == 0 &&
                _bitmapOutputWaiterSlotVersion == 0 &&
                _bitmapOutputDemandGeneration == 0 &&
                _bitmapOutputSvgGeneration == 0 &&
                _bitmapOutputDeferred == 0 &&
                _bitmapOutputVisible == 0 &&
                _bitmapOutputReplacement == 0 &&
                _bitmapOutputExistingTile == 0 &&
                _bitmapOutputNeedsTrim == 0 &&
                _bitmapOutputRegistering == 0 &&
                _bitmapOutputRegistrationNeeded == 0 &&
                _bitmapOutputCapacityReady == 0 &&
                _bitmapOutputRetryQueued == 0 &&
                _bitmapOutputRetryInProgress == 0 &&
                _bitmapOutputRetryOfferPending == 0 &&
                _bitmapOutputRegistrationFailed == 0 &&
                _bitmapOutputCancellationRegistered == 0;
        }
    }

    // Used only at the eventual UI generation commit. The candidate lease
    // keeps the immutable bitmap alive; this fence rejects source replacement,
    // suspension, or disposal since candidate creation.
    internal bool IsCurrentCompletedRasterPlacement(
        SharedCanvasBitmapCache.Lease expectedSourceLease)
    {
        ArgumentNullException.ThrowIfNull(expectedSourceLease);
        lock (_svgWorkSync)
        {
            return IsCompletedRasterPlacementEligibleNoLock() &&
                ReferenceEquals(Volatile.Read(ref _bitmapLease), expectedSourceLease) &&
                _bitmap is not null;
        }
    }

    private bool IsCompletedRasterPlacementEligibleNoLock()
        => !_disposed &&
           !_resourcesSuspended &&
           !_imageRetirementFinalizing &&
           !_imageRetirementCompleted &&
           _svgWorkCount == 0 &&
           !_isSvg &&
           !IsBitmapOutputDeferred &&
           !_loadFailed &&
           _loadStarted &&
           !_imageLifetimeCancellation.IsCancellationRequested &&
           !Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
           !(_preResolutionCacheKey?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ?? false) &&
           _builtInSource is not { Kind: MarkdownBuiltInImageSourceKind.Data };

    /// <summary>
    /// Returns whether publishing the completed raster can affect only paint.
    /// The authored width/height pair makes its measured rectangle independent
    /// of decoded intrinsic dimensions; the snapshot separately proves that
    /// the owning layout still has its last successful placement.
    /// </summary>
    internal bool TryCanUsePaintOnlyCompletion()
    {
        IMarkdownPerformanceSessionInternal? session = Context.PerformanceSession;
        if (session is null || session.IsDisposed || !session.Options.ReleaseOffscreenImageResources)
        {
            return false;
        }

        if (!Monitor.TryEnter(_inlineMeasureSync))
            return false;

        try
        {
            if (_disposed || _resourcesSuspended || _loadFailed || !_loadStarted ||
                _imageLifetimeCancellation.IsCancellationRequested || _inlineMeasureCheckpointActive ||
                _isSvg || _bitmap is null)
            {
                return false;
            }

            if (_requestedWidth is not { IsPercent: false } width ||
                !float.IsFinite(width.Value) || width.Value <= 0 ||
                _requestedHeight is not { IsPercent: false } height ||
                !float.IsFinite(height.Value) || height.Value <= 0 ||
                !float.IsFinite(_availableWidth) || _availableWidth <= 0)
            {
                return false;
            }

            float available = Math.Max(
                1f,
                _isInlineLayout
                    ? _availableWidth
                    : _availableWidth - (float)(Margin.Left + Margin.Right));
            if (!float.IsFinite(available))
                return false;

            float requestedWidth = width.Resolve(available);
            float requestedHeight = height.Resolve(available);
            if (!float.IsFinite(requestedWidth) || requestedWidth <= 0 ||
                !float.IsFinite(requestedHeight) || requestedHeight <= 0)
            {
                return false;
            }

            float scale = Math.Min(1f, available / requestedWidth);
            float expectedWidth = Math.Max(1f, requestedWidth * scale);
            float expectedHeight = Math.Max(1f, requestedHeight * scale);
            return float.IsFinite(expectedWidth) && float.IsFinite(expectedHeight) &&
                   _imageWidth == expectedWidth && _imageHeight == expectedHeight &&
                   (!_isInlineLayout ||
                       (Bounds.Width == expectedWidth && Bounds.Height == expectedHeight));
        }
        finally
        {
            Monitor.Exit(_inlineMeasureSync);
        }
    }

    private bool IsRasterPlacementContextCompatible(MarkdownLayoutContext target)
        // Context identity is required for either completed-image placement.
        // State eligibility is checked separately; no deferred document owner
        // or old-generation work is carried into a private clone.
        => ReferenceEquals(Context.ResourceCreator, target.ResourceCreator) &&
           ReferenceEquals(Context.ThemeSnapshot, target.ThemeSnapshot) &&
           ReferenceEquals(Context.Registry, target.Registry) &&
           ReferenceEquals(Context.Dispatcher, target.Dispatcher) &&
           ReferenceEquals(Context.ImageResolver, target.ImageResolver) &&
           ReferenceEquals(Context.SvgRenderer, target.SvgRenderer) &&
           ReferenceEquals(Context.PerformanceSession, target.PerformanceSession) &&
           ReferenceEquals(Context.ImagePreparationOwner, target.ImagePreparationOwner) &&
           ReferenceEquals(Context.ImageDocumentSource, target.ImageDocumentSource) &&
           ReferenceEquals(Context.SourceMap, target.SourceMap) &&
           Context.FlowDirection == target.FlowDirection &&
           Context.RasterizationScale == target.RasterizationScale &&
           Context.AllowThirdPartyRemoteImages == target.AllowThirdPartyRemoteImages &&
           string.Equals(Context.Language, target.Language, StringComparison.Ordinal) &&
           string.Equals(Context.ImageDocumentPath, target.ImageDocumentPath, StringComparison.Ordinal) &&
           Equals(Context.ImageBaseUri, target.ImageBaseUri);

    private ImageBox(MarkdownLayoutContext context, ImageBox source)
    {
        Context = context;
        _nativeRetirement = source._nativeRetirement;
        _nativeDisposer = source._nativeDisposer;
        _nativeLayoutOptionsSetter = source._nativeLayoutOptionsSetter;
        Url = source.Url;
        Alt = source.Alt;
        _requestedWidth = source._requestedWidth;
        _requestedHeight = source._requestedHeight;
        _imageLifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.ImageCancellationToken);
        try
        {
            // These are immutable request descriptors, not resource state.
            // Data-URI payloads are rejected by the caller so this does not
            // retain or duplicate an embedded source buffer.
            _builtInSource = source._builtInSource;
            _preResolutionCacheKey = source._preResolutionCacheKey;
            _activeCacheKey = source._preResolutionCacheKey;
            _isSvg = source._isSvg; // a URI hint only; accepted SVG state is not copied
            _isInlineLayout = source._isInlineLayout;
            _loadStarted = false;
            _loadFailed = false;
            ContentAlignment = source.ContentAlignment;
            MinimumParagraphLineHeight = source.MinimumParagraphLineHeight;
            BlockIndex = source.BlockIndex;
            SafeHtmlInlineFlowScopeId = source.SafeHtmlInlineFlowScopeId;
            CopyPlacementMarginsFrom(source);
        }
        catch (Exception constructionFailure)
        {
            try
            {
                _imageLifetimeCancellation.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    constructionFailure.Data["MarkdownRenderer.PrivateIdleImageCloneCleanupFailure"] = cleanupFailure;
                }
                catch
                {
                    // Cleanup diagnostics must not mask construction failure.
                }
            }
            throw;
        }
    }

    private ImageBox(
        MarkdownLayoutContext context,
        ImageBox source,
        SharedCanvasBitmapCache.Lease bitmapLease,
        CompletedSvgPlacementMetadataFence? svgMetadataFence = null)
    {
        Context = context;
        _nativeRetirement = source._nativeRetirement;
        _nativeDisposer = source._nativeDisposer;
        _nativeLayoutOptionsSetter = source._nativeLayoutOptionsSetter;
        Url = source.Url;
        Alt = source.Alt;
        _requestedWidth = source._requestedWidth;
        _requestedHeight = source._requestedHeight;
        _imageLifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.ImageCancellationToken);
        try
        {
            _builtInSource = source._builtInSource;
            _preResolutionCacheKey = svgMetadataFence is { } svgFenceForKey
                ? svgFenceForKey.PreResolutionCacheKey
                : source._preResolutionCacheKey;
            _activeCacheKey = svgMetadataFence is { } svgFence
                ? svgFence.ActiveCacheKey
                : source._activeCacheKey;
            _isInlineLayout = source._isInlineLayout;
            _loadStarted = true;
            ContentAlignment = source.ContentAlignment;
            MinimumParagraphLineHeight = source.MinimumParagraphLineHeight;
            BlockIndex = source.BlockIndex;
            SafeHtmlInlineFlowScopeId = source.SafeHtmlInlineFlowScopeId;
            CopyPlacementMarginsFrom(source);
            // Descriptor capture runs under the source snapshot gate. The
            // source already resolved these intrinsic DIPs when its bitmap
            // was published; never query Win2D Size here for an uncached or
            // legacy lease whose optional IntrinsicSize metadata is absent.
            _rasterIntrinsicSize = source._rasterIntrinsicSize;
            _hasPresentedImage = source._hasPresentedImage;
            _bitmap = bitmapLease.Bitmap;
            _bitmapLease = bitmapLease;

            if (svgMetadataFence is { } acceptedSvg)
            {
                _isSvg = true;
                _svgSourceIdentity = acceptedSvg.SourceIdentity;
                _svgAnonymousPartitionKey = acceptedSvg.AnonymousPartitionKey;
                _svgSourceByteLength = acceptedSvg.SourceByteLength;
                _svgIntrinsicSize = acceptedSvg.IntrinsicSize;
                _svgTitle = acceptedSvg.Title;
                _svgDesc = acceptedSvg.Description;
                _svgContentHash = acceptedSvg.ContentHash;
                _svgSecurityPartitionHash = acceptedSvg.SecurityPartitionHash;
                _svgInfo = acceptedSvg.Info;
                _svgRendererIdentity = acceptedSvg.RendererIdentity;
                _svgProviderCacheGeneration = acceptedSvg.ProviderCacheGeneration;
                _svgRenderGeneration = acceptedSvg.RenderGeneration;
                _svgLoadDemandGeneration = acceptedSvg.LoadDemandGeneration;
                _svgBitmapWidthPixels = acceptedSvg.BitmapWidthPixels;
                _svgBitmapHeightPixels = acceptedSvg.BitmapHeightPixels;
                _svgPhysicalViewport = acceptedSvg.PhysicalViewport;
                _bitmapPhysicalViewport = acceptedSvg.BitmapPhysicalViewport;
                _svgFailureReason = null;
                _svgFailureDescription = null;
                _loadFailed = false;
            }
        }
        catch (Exception constructionFailure)
        {
            // The caller keeps the bitmap lease until construction succeeds.
            // Retire only this partially-created object's linked lifetime.
            try
            {
                _imageLifetimeCancellation.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    constructionFailure.Data["MarkdownRenderer.PrivateRasterImageCloneCleanupFailure"] = cleanupFailure;
                }
                catch
                {
                    // Cleanup diagnostics must not mask construction failure.
                }
            }
            throw;
        }
    }

    internal bool HasBitmapForAutomation => _bitmap is not null;

    /// <summary>Current state projected into the image's UIA accessible name.</summary>
    internal MarkdownImageAccessibilityState AccessibilityState => _loadFailed
        ? MarkdownImageAccessibilityState.Error
        : _hasPresentedImage || _bitmap is not null || _svgHasRenderedTile
            ? MarkdownImageAccessibilityState.Loaded
            : MarkdownImageAccessibilityState.Loading;

    internal Rect AutomationRenderDestination => _isInlineLayout
        ? Bounds
        : new Rect(GetContentX(), GetContentY(), _imageWidth, _imageHeight);

    internal bool HasAllSvgTilesForAutomation(
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY)
    {
        if (firstTileX > lastTileX || firstTileY > lastTileY)
            return false;

        // The host computes a finite, viewport-clamped tile range before this
        // atomic check; keep the resource dictionary observation under one lock.
        lock (_svgTileLock)
        {
            for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
            {
                for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
                {
                    int x = tileX * SvgTileSizePixels;
                    int y = tileY * SvgTileSizePixels;
                    SvgTileKey key = (
                        x,
                        y,
                        Math.Min(SvgTileSizePixels, _svgBitmapWidthPixels - x),
                        Math.Min(SvgTileSizePixels, _svgBitmapHeightPixels - y));
                    if (!_svgTiles.ContainsKey(key))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>Test-only: height of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageHeight => _imageHeight;

    /// <summary>Test-only: width of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageWidth => _imageWidth;

    /// <summary>Test-only: height of the caption area at last measure (excludes margins).</summary>
    public float MeasuredCaptionHeight => _captionHeight;

    internal CanvasTextLayout? PlaceholderLayoutForTests => _placeholder;
    internal CanvasTextLayout? CaptionLayoutForTests => _caption;
    internal NativeResourceRetirementRecord? PlaceholderRetirementRecordForTests => _placeholderRetirementRecord;
    internal NativeResourceRetirementRecord? CaptionRetirementRecordForTests => _captionRetirementRecord;
    internal bool HasNativeLayoutCustodyForTests
    {
        get
        {
            lock (_inlineMeasureSync)
                return HasValidNativeLayoutCustodyNoLock();
        }
    }

    internal void SetCaptionRetirementRecordForTests(NativeResourceRetirementRecord? record)
    {
        lock (_inlineMeasureSync)
        {
            _captionRetirementRecord = record;
            AdvanceNativeLayoutGenerationNoLock();
        }
    }

    internal void SetCompletedBitmapForTests(SharedCanvasBitmapCache.Lease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _loadStarted = true;
        _loadFailed = false;
        ReplaceBitmap(lease.Bitmap, lease);
    }

    /// <summary>
    /// Value checkpoint for one speculative inline measurement. The image box
    /// keeps replaced native layouts alive until this checkpoint is committed
    /// or rolled back.
    /// </summary>
    internal readonly struct InlineMeasureCheckpoint
    {
        internal readonly ImageBox? Owner;
        internal readonly int Generation;
        internal readonly bool WasInlineLayout;
        internal readonly float PreviousAvailableWidth;
        internal readonly float PreviousImageWidth;
        internal readonly float PreviousImageHeight;
        internal readonly float PreviousCaptionHeight;
        internal readonly Rect PreviousBounds;

        internal InlineMeasureCheckpoint(ImageBox owner, int generation)
        {
            Owner = owner;
            Generation = generation;
            WasInlineLayout = owner._isInlineLayout;
            PreviousAvailableWidth = owner._availableWidth;
            PreviousImageWidth = owner._imageWidth;
            PreviousImageHeight = owner._imageHeight;
            PreviousCaptionHeight = owner._captionHeight;
            PreviousBounds = owner.Bounds;
        }

        internal bool BelongsTo(ImageBox owner, int generation) =>
            ReferenceEquals(Owner, owner) && Generation == generation;

        internal void Commit() => Owner?.FinishInlineCheckpoint(this, commit: true);

        internal void Rollback() => Owner?.FinishInlineCheckpoint(this, commit: false);
    }

    internal InlineMeasureCheckpoint BeginInlineCheckpoint()
    {
        lock (_inlineMeasureSync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            if (_inlineMeasureCheckpointActive)
                throw new InvalidOperationException("An inline image measure is already active.");

            int generation = unchecked(_inlineMeasureCheckpointGeneration + 1);
            _inlineMeasureCheckpointGeneration = generation == 0 ? 1 : generation;
            _inlineMeasureCheckpointActive = true;
            _inlineMeasureOriginalPlaceholder = _placeholder;
            _inlineMeasureOriginalPlaceholderRetirementRecord = _placeholderRetirementRecord;
            _inlineMeasureOriginalCaption = _caption;
            _inlineMeasureOriginalCaptionRetirementRecord = _captionRetirementRecord;
            AdvanceNativeLayoutGenerationNoLock();
            return new InlineMeasureCheckpoint(this, _inlineMeasureCheckpointGeneration);
        }
    }

    private void FinishInlineCheckpoint(
        InlineMeasureCheckpoint checkpoint,
        bool commit)
    {
        lock (_inlineMeasureSync)
        {
            if (!_inlineMeasureCheckpointActive ||
                !checkpoint.BelongsTo(this, _inlineMeasureCheckpointGeneration))
            {
                return;
            }

            CanvasTextLayout? originalPlaceholder = _inlineMeasureOriginalPlaceholder;
            NativeResourceRetirementRecord? originalPlaceholderRecord = _inlineMeasureOriginalPlaceholderRetirementRecord;
            CanvasTextLayout? originalCaption = _inlineMeasureOriginalCaption;
            NativeResourceRetirementRecord? originalCaptionRecord = _inlineMeasureOriginalCaptionRetirementRecord;
            if (_disposed)
            {
                // Dispose owns both current and checkpoint-original pairs.
                // Revoke this token but leave every layout/record attached
                // until the asynchronous terminal cleanup can attempt them.
                RevokeInlineCheckpointNoLock();
                return;
            }

            if (commit)
            {
                ClearInlineCheckpoint();
                RetireIfDetached(originalPlaceholder, originalPlaceholderRecord,
                    _placeholder, _placeholderRetirementRecord);
                RetireIfDetached(originalCaption, originalCaptionRecord,
                    _caption, _captionRetirementRecord);
                return;
            }

            CanvasTextLayout? candidatePlaceholder = _placeholder;
            NativeResourceRetirementRecord? candidatePlaceholderRecord = _placeholderRetirementRecord;
            CanvasTextLayout? candidateCaption = _caption;
            NativeResourceRetirementRecord? candidateCaptionRecord = _captionRetirementRecord;
            if (!HasValidNativeLayoutCustodyNoLock() ||
                (originalPlaceholder is null) != (originalPlaceholderRecord is null) ||
                (originalCaption is null) != (originalCaptionRecord is null) ||
                (candidatePlaceholder is null) != (candidatePlaceholderRecord is null) ||
                (candidateCaption is null) != (candidateCaptionRecord is null))
            {
                throw new InvalidOperationException("Image inline-checkpoint native-layout custody is inconsistent.");
            }

            _placeholder = originalPlaceholder;
            _placeholderRetirementRecord = originalPlaceholderRecord;
            _caption = originalCaption;
            _captionRetirementRecord = originalCaptionRecord;
            _isInlineLayout = checkpoint.WasInlineLayout;
            _availableWidth = checkpoint.PreviousAvailableWidth;
            _imageWidth = checkpoint.PreviousImageWidth;
            _imageHeight = checkpoint.PreviousImageHeight;
            _captionHeight = checkpoint.PreviousCaptionHeight;
            Bounds = checkpoint.PreviousBounds;
            AdvanceNativeLayoutGenerationNoLock();
            ClearInlineCheckpoint();

            // Restore the exact original owner pair before publishing the
            // detached candidates. The records are pre-reserved and this
            // handoff invokes no native callbacks under the image lock.
            RetireIfDetached(candidatePlaceholder, candidatePlaceholderRecord,
                _placeholder, _placeholderRetirementRecord);
            RetireIfDetached(candidateCaption, candidateCaptionRecord,
                _caption, _captionRetirementRecord);
        }
    }

    private void ClearInlineCheckpoint()
    {
        _inlineMeasureCheckpointActive = false;
        _inlineMeasureOriginalPlaceholder = null;
        _inlineMeasureOriginalPlaceholderRetirementRecord = null;
        _inlineMeasureOriginalCaption = null;
        _inlineMeasureOriginalCaptionRetirementRecord = null;
    }

    private void InvalidateInlineCheckpointForDisposal()
    {
        lock (_inlineMeasureSync)
        {
            if (!_inlineMeasureCheckpointActive)
                return;

            // Do not publish the original owners here: RetirementTask still
            // has to make its synchronous best-effort disposal attempt and
            // surface those failures. Late checkpoint completion is revoked
            // while its exact original pairs remain attached for that drain.
            RevokeInlineCheckpointNoLock();
        }
    }

    private void RevokeInlineCheckpointNoLock()
    {
        _inlineMeasureCheckpointActive = false;
        int generation = unchecked(_inlineMeasureCheckpointGeneration + 1);
        _inlineMeasureCheckpointGeneration = generation == 0 ? 1 : generation;
        AdvanceNativeLayoutGenerationNoLock();
    }

    private static void RetireNativeLayout(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord)
    {
        if (retirementRecord is null)
        {
            if (layout is not null)
                throw new InvalidOperationException("A detached image text layout has no durable retirement owner.");
            return;
        }

        // Every participating layout is paired with a record reserved before
        // construction. Never invoke native disposal on a detached layout or
        // lose it if Win2D disposal fails.
        retirementRecord?.Publish();
    }

    private static void RetireIfDetached(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord,
        CanvasTextLayout? activeLayout,
        NativeResourceRetirementRecord? activeRecord)
    {
        if (layout is null && retirementRecord is null)
            return;
        if (ReferenceEquals(layout, activeLayout) && ReferenceEquals(retirementRecord, activeRecord))
            return;
        RetireNativeLayout(layout, retirementRecord);
    }

    private NativeResourceRetirementRecord ReserveNativeLayoutRetirementRecord() =>
        _nativeRetirement.Reserve(0, 1, _nativeDisposer);

    private CanvasTextLayout CreateOwnedTextLayout(
        string text,
        CanvasTextFormat format,
        float width,
        float height,
        out NativeResourceRetirementRecord retirementRecord)
    {
        // Reserve the source-free owner before Win2D allocates its layout.
        NativeResourceRetirementRecord reserved = ReserveNativeLayoutRetirementRecord();
        CanvasTextLayout? layout = null;
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            layout = CanvasTextLayoutFactory.Create(
                Context.ResourceCreator,
                text,
                format,
                width,
                height,
                Context.LayoutDiagnostics);
#else
            layout = new CanvasTextLayout(Context.ResourceCreator, text, format, width, height);
#endif
            reserved.SetTemporary(0, layout);
            if (_nativeLayoutOptionsSetter is { } testSetter)
                testSetter(layout);
            else
                layout.Options = CanvasDrawTextOptions.EnableColorFont;
            retirementRecord = reserved;
            return layout;
        }
        catch
        {
            if (layout is not null)
                reserved.SetTemporary(0, layout);
            reserved.Publish();
            throw;
        }
    }

    private void AdvanceNativeLayoutGenerationNoLock() =>
        _nativeLayoutGeneration = unchecked(_nativeLayoutGeneration + 1);

    private bool HasValidNativeLayoutCustodyNoLock() =>
        (_placeholder is null) == (_placeholderRetirementRecord is null) &&
        (_caption is null) == (_captionRetirementRecord is null) &&
        (_placeholder is null || _caption is null || !ReferenceEquals(_placeholder, _caption)) &&
        (_placeholderRetirementRecord is null || _captionRetirementRecord is null ||
            !ReferenceEquals(_placeholderRetirementRecord, _captionRetirementRecord));

    private void ReplacePlaceholder(
        CanvasTextLayout? replacement,
        NativeResourceRetirementRecord? replacementRecord)
    {
        CanvasTextLayout? previous;
        NativeResourceRetirementRecord? previousRecord;
        bool checkpointOwnsPrevious;
        try
        {
            lock (_inlineMeasureSync)
            {
                if (_disposed && replacement is not null)
                    throw new ObjectDisposedException(nameof(ImageBox));

                if (!HasValidNativeLayoutCustodyNoLock() ||
                    (replacement is null) != (replacementRecord is null))
                {
                    throw new InvalidOperationException("Image placeholder native-layout custody is inconsistent.");
                }

                previous = _placeholder;
                previousRecord = _placeholderRetirementRecord;
                checkpointOwnsPrevious = _inlineMeasureCheckpointActive &&
                    ReferenceEquals(previous, _inlineMeasureOriginalPlaceholder) &&
                    ReferenceEquals(previousRecord, _inlineMeasureOriginalPlaceholderRetirementRecord);
                _placeholder = replacement;
                _placeholderRetirementRecord = replacementRecord;
                AdvanceNativeLayoutGenerationNoLock();
            }
        }
        catch
        {
            RetireNativeLayout(replacement, replacementRecord);
            throw;
        }

        if (!checkpointOwnsPrevious)
            RetireNativeLayout(previous, previousRecord);
    }

    private void ReplaceCaption(
        CanvasTextLayout? replacement,
        NativeResourceRetirementRecord? replacementRecord,
        float replacementHeight)
    {
        CanvasTextLayout? previous;
        NativeResourceRetirementRecord? previousRecord;
        bool checkpointOwnsPrevious;
        try
        {
            lock (_inlineMeasureSync)
            {
                if (_disposed && replacement is not null)
                    throw new ObjectDisposedException(nameof(ImageBox));

                if (!HasValidNativeLayoutCustodyNoLock() ||
                    (replacement is null) != (replacementRecord is null))
                {
                    throw new InvalidOperationException("Image caption native-layout custody is inconsistent.");
                }

                previous = _caption;
                previousRecord = _captionRetirementRecord;
                checkpointOwnsPrevious = _inlineMeasureCheckpointActive &&
                    ReferenceEquals(previous, _inlineMeasureOriginalCaption) &&
                    ReferenceEquals(previousRecord, _inlineMeasureOriginalCaptionRetirementRecord);
                _caption = replacement;
                _captionRetirementRecord = replacementRecord;
                _captionHeight = replacementHeight;
                AdvanceNativeLayoutGenerationNoLock();
            }
        }
        catch
        {
            RetireNativeLayout(replacement, replacementRecord);
            throw;
        }

        if (!checkpointOwnsPrevious)
            RetireNativeLayout(previous, previousRecord);
    }

    private void RetireNativeLayoutsForDisposal(Action<Exception> addFailure)
    {
        CanvasTextLayout? placeholder;
        CanvasTextLayout? caption;
        CanvasTextLayout? originalPlaceholder;
        CanvasTextLayout? originalCaption;
        NativeResourceRetirementRecord? placeholderRecord;
        NativeResourceRetirementRecord? captionRecord;
        NativeResourceRetirementRecord? originalPlaceholderRecord;
        NativeResourceRetirementRecord? originalCaptionRecord;
        lock (_inlineMeasureSync)
        {
            placeholder = _placeholder;
            caption = _caption;
            originalPlaceholder = _inlineMeasureOriginalPlaceholder;
            originalCaption = _inlineMeasureOriginalCaption;
            placeholderRecord = _placeholderRetirementRecord;
            captionRecord = _captionRetirementRecord;
            originalPlaceholderRecord = _inlineMeasureOriginalPlaceholderRetirementRecord;
            originalCaptionRecord = _inlineMeasureOriginalCaptionRetirementRecord;

            if (!HasValidNativeLayoutCustodyNoLock() ||
                (originalPlaceholder is null) != (originalPlaceholderRecord is null) ||
                (originalCaption is null) != (originalCaptionRecord is null) ||
                !HasCoherentTerminalOwnerPairs(
                    placeholder, placeholderRecord,
                    caption, captionRecord,
                    originalPlaceholder, originalPlaceholderRecord,
                    originalCaption, originalCaptionRecord))
            {
                throw new InvalidOperationException("Image terminal native-layout custody is inconsistent.");
            }

            _placeholder = null;
            _placeholderRetirementRecord = null;
            _caption = null;
            _captionRetirementRecord = null;
            _inlineMeasureCheckpointActive = false;
            _inlineMeasureOriginalPlaceholder = null;
            _inlineMeasureOriginalPlaceholderRetirementRecord = null;
            _inlineMeasureOriginalCaption = null;
            _inlineMeasureOriginalCaptionRetirementRecord = null;
            AdvanceNativeLayoutGenerationNoLock();
        }

        NativeRetirementFailurePair placeholderFailure =
            RetireTerminalNativeLayout(placeholder, placeholderRecord);
        NativeRetirementFailurePair captionFailure = default;
        NativeRetirementFailurePair originalPlaceholderFailure = default;
        NativeRetirementFailurePair originalCaptionFailure = default;
        if (!ReferenceEquals(caption, placeholder) || !ReferenceEquals(captionRecord, placeholderRecord))
            captionFailure = RetireTerminalNativeLayout(caption, captionRecord);
        if (!ReferenceEquals(originalPlaceholder, placeholder) ||
            !ReferenceEquals(originalPlaceholderRecord, placeholderRecord))
            originalPlaceholderFailure = RetireTerminalNativeLayout(
                originalPlaceholder, originalPlaceholderRecord);
        if ((!ReferenceEquals(originalCaption, placeholder) ||
             !ReferenceEquals(originalCaptionRecord, placeholderRecord)) &&
            (!ReferenceEquals(originalCaption, caption) ||
             !ReferenceEquals(originalCaptionRecord, captionRecord)) &&
            (!ReferenceEquals(originalCaption, originalPlaceholder) ||
             !ReferenceEquals(originalCaptionRecord, originalPlaceholderRecord)))
        {
            originalCaptionFailure = RetireTerminalNativeLayout(originalCaption, originalCaptionRecord);
        }

        // Every detached record has either completed or reached the durable
        // retry registry before diagnostics can allocate, aggregate, or fail.
        AddRetirementFailure(addFailure, placeholderFailure);
        AddRetirementFailure(addFailure, captionFailure);
        AddRetirementFailure(addFailure, originalPlaceholderFailure);
        AddRetirementFailure(addFailure, originalCaptionFailure);
    }

    private static bool HasCoherentTerminalOwnerPairs(
        CanvasTextLayout? placeholder,
        NativeResourceRetirementRecord? placeholderRecord,
        CanvasTextLayout? caption,
        NativeResourceRetirementRecord? captionRecord,
        CanvasTextLayout? originalPlaceholder,
        NativeResourceRetirementRecord? originalPlaceholderRecord,
        CanvasTextLayout? originalCaption,
        NativeResourceRetirementRecord? originalCaptionRecord)
    {
        return SameOrSeparateOwner(placeholder, placeholderRecord,
                   originalPlaceholder, originalPlaceholderRecord) &&
               SameOrSeparateOwner(caption, captionRecord,
                   originalCaption, originalCaptionRecord) &&
               AreDisjointOwners(placeholder, placeholderRecord, caption, captionRecord) &&
               AreDisjointOwners(placeholder, placeholderRecord, originalCaption, originalCaptionRecord) &&
               AreDisjointOwners(caption, captionRecord, originalPlaceholder, originalPlaceholderRecord) &&
               AreDisjointOwners(originalPlaceholder, originalPlaceholderRecord,
                   originalCaption, originalCaptionRecord);
    }

    private static bool SameOrSeparateOwner(
        CanvasTextLayout? leftLayout,
        NativeResourceRetirementRecord? leftRecord,
        CanvasTextLayout? rightLayout,
        NativeResourceRetirementRecord? rightRecord) =>
        ReferenceEquals(leftLayout, rightLayout) == ReferenceEquals(leftRecord, rightRecord);

    private static bool AreDisjointOwners(
        CanvasTextLayout? leftLayout,
        NativeResourceRetirementRecord? leftRecord,
        CanvasTextLayout? rightLayout,
        NativeResourceRetirementRecord? rightRecord) =>
        (leftLayout is null || rightLayout is null || !ReferenceEquals(leftLayout, rightLayout)) &&
        (leftRecord is null || rightRecord is null || !ReferenceEquals(leftRecord, rightRecord));

    private readonly struct NativeRetirementFailurePair(
        Exception? DisposalFailure,
        Exception? PublicationFailure)
    {
        public Exception? DisposalFailure { get; init; } = DisposalFailure;
        public Exception? PublicationFailure { get; init; } = PublicationFailure;
    }

    private static NativeRetirementFailurePair RetireTerminalNativeLayout(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? record)
    {
        if (layout is null)
            return default;
        if (record is null)
        {
            return new NativeRetirementFailurePair(
                new InvalidOperationException("A terminal image text layout has no pre-reserved native owner."),
                null);
        }

        try
        {
            // Preserve the existing RetirementTask contract: make one
            // synchronous best-effort close outside the image lock. A failure
            // is published before its bounded diagnostic is recorded.
            record.Disposer(layout);
            record.SetTemporary(0, null);
            record.Publish();
        }
        catch (Exception failure)
        {
            try
            {
                record.Publish();
                return new NativeRetirementFailurePair(failure, null);
            }
            catch (Exception publicationFailure)
            {
                return new NativeRetirementFailurePair(failure, publicationFailure);
            }
        }

        return default;
    }

    private static void AddRetirementFailure(
        Action<Exception> addFailure,
        NativeRetirementFailurePair failures)
    {
        if (failures.DisposalFailure is { } disposalFailure)
            addFailure(disposalFailure);
        if (failures.PublicationFailure is { } publicationFailure)
            addFailure(publicationFailure);
    }

    /// <summary>
    /// Measures the image as an inline atomic cell. Inline images share the
    /// same loading/cache pipeline as block images, but they do not render
    /// captions or margins and reserve a compact placeholder before loading.
    /// </summary>
    internal Size MeasureInline(float availableWidth, float lineHeight)
    {
        lock (_inlineMeasureSync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            if (_inlineMeasureCheckpointActive)
                throw new InvalidOperationException("An inline image measure requires its active checkpoint.");
            return MeasureInlineCore(availableWidth, lineHeight);
        }
    }

    internal Size MeasureInline(
        float availableWidth,
        float lineHeight,
        InlineMeasureCheckpoint checkpoint)
    {
        lock (_inlineMeasureSync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            if (!_inlineMeasureCheckpointActive ||
                !checkpoint.BelongsTo(this, _inlineMeasureCheckpointGeneration))
            {
                throw new InvalidOperationException("The inline image measure checkpoint is no longer active.");
            }
            return MeasureInlineCore(availableWidth, lineHeight);
        }
    }

    private Size MeasureInlineCore(float availableWidth, float lineHeight)
    {
        _isInlineLayout = true;
        _availableWidth = availableWidth;
        if (ShouldCollapseFailedDecorativeImage)
        {
            CollapseFailedDecorativeImage(availableWidth);
            return new Size(0, 0);
        }

        float maxW = Math.Max(1f, availableWidth);
        Size rasterIntrinsicSize = GetRasterMeasurementIntrinsicSize();
        float w;
        float h;

        if (ShouldExpandInlineFailure)
        {
            h = Math.Clamp(lineHeight, 20f, 28f);
            w = MeasureInlineFailureWidth(maxW, h);
        }
        else if (_bitmap is { } bmu)
        {
            float bw;
            float bh;
            if (_isSvg && _svgIntrinsicSize.Width > 0 && _svgIntrinsicSize.Height > 0)
            {
                bw = (float)_svgIntrinsicSize.Width;
                bh = (float)_svgIntrinsicSize.Height;
            }
            else if (!_isSvg && _rasterIntrinsicSize.Width > 0 && _rasterIntrinsicSize.Height > 0)
            {
                bw = (float)_rasterIntrinsicSize.Width;
                bh = (float)_rasterIntrinsicSize.Height;
            }
            else
            {
                bw = (float)bmu.Size.Width;
                bh = (float)bmu.Size.Height;
            }

            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            w = Math.Max(1f, bw * scale);
            h = Math.Max(1f, bh * scale);
        }
        else if (!_isSvg && rasterIntrinsicSize.Width > 0 && rasterIntrinsicSize.Height > 0)
        {
            float scale = Math.Min(1f, maxW / (float)rasterIntrinsicSize.Width);
            w = Math.Max(1f, (float)rasterIntrinsicSize.Width * scale);
            h = Math.Max(1f, (float)rasterIntrinsicSize.Height * scale);
        }
        else if (_isSvg && (_svgIntrinsicSize.Width > 0 || _loadStarted))
        {
            float bw = _svgIntrinsicSize.Width > 0 ? (float)_svgIntrinsicSize.Width : Math.Max(16f, lineHeight);
            float bh = _svgIntrinsicSize.Height > 0 ? (float)_svgIntrinsicSize.Height : Math.Max(16f, lineHeight);
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            w = Math.Max(1f, bw * scale);
            h = Math.Max(1f, bh * scale);
        }
        else
        {
            h = Math.Clamp(lineHeight, 16f, 32f);
            w = h;
        }

        ApplyRequestedSize(ref w, ref h, maxW);
        _imageWidth = w;
        _imageHeight = h;
        if (_bitmap is null)
        {
            UpdatePlaceholder(w, h);
        }
        _captionHeight = 0f;
        ReplaceCaption(null, null, 0f);
        Bounds = new Rect(0, 0, w, h);
        return new Size(w, h);
    }

    /// <summary>Sets the document-coordnnate rectangle used by an inline image.</summary>
    internal void SetInlineBounds(Rect rect)
    {
        Bounds = rect;
        _imageWidth = (float)Math.Max(1, rect.Width);
        _imageHeight = (float)Math.Max(1, rect.Height);
    }

    /// <summary>Paiits this image into an inline cell.</summary>
    internal void PaintInline(CanvasDrawingSession ds, Rect rect, Rect viewport)
    {
        if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
            rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
        {
            return;
        }

        // Inline images can be nested several containers deep in raw HTML. Painting is the
        // final viewport-aware fallback if a host's lazy-image plan did not discover one.
        EnsureLoading();

        PaintImageContent(ds, rect, viewport);
    }

    internal void PaintInlineSelectionForeground(CanvasDrawingSession ds, Rect rect, Rect viewport, Color selectionForeground)
    {
        if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
            rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
        {
            return;
        }

        NativePaintSessionResources.LayerLease selectionLayer =
            NativePaintSessionResources.CreateLayer(ds, 0.82f, rect);
        try
        {
            PaintInline(ds, rect, viewport);
        }
        catch (Exception primary)
        {
            selectionLayer.ClosePreservingPrimary(primary);
            throw;
        }
        selectionLayer.Close();

        ds.DrawRoundedRectangle(rect, 3, 3, selectionForeground, 1.5f);
    }

    /// <summary>
    /// Triggers the network/disk load for this image if it has not already
    /// started. Called by the renderer when the box enters the viewport.
    /// </summary>
    public void EnsureLoading()
    {
        EnsureLoading(isVisible: true);
    }

    internal void EnsureLoading(bool isVisible)
    {
        // A pressure-parked tiled image retains its good pixels. Only the
        // geometry-aware paint path can decide that a visible tile is missing
        // (or that the target changed) and re-enter authorized loading.
        if (Volatile.Read(ref _svgPressureParked))
            return;

        if (ObserveDeferredBitmapOutput(isVisible))
            return;

        if (_loadStarted)
        {
            MarkdownSvgDemandTicket? currentDemand = Volatile.Read(ref _svgLoadDemandTicket);
            if (isVisible)
                currentDemand?.PromoteVisible();
            TryResumeDeferredSvgLoadForObservedDemandOrCapacity(currentDemand);
            return;
        }

        if (Volatile.Read(ref _svgHostDeferred) != 0 &&
            Volatile.Read(ref _svgLoadDemandTicket) is { } deferredDemand)
        {
            if (isVisible)
                deferredDemand.PromoteVisible();
            TryResumeDeferredSvgLoadForObservedDemandOrCapacity(deferredDemand);
            return;
        }

        StartLoad(isVisible: isVisible);
    }

    private void DeferSvgHostLoad(
        SvgHostCapacityDeferral deferral, object? expectedDenialIdentity = null)
    {
        SvgHostAdmissionCoordinator.PendingWaiter? previousWaiter;
        object registrationAttempt = new();
        CancellationToken cancellationToken;
        lock (_svgWorkSync)
        {
            if (_disposed || _resourcesSuspended ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), deferral.Demand) ||
                deferral.LoadGeneration != Volatile.Read(ref _svgLoadDemandGeneration))
            {
                return;
            }

            cancellationToken = (_imageResidencyCancellation ?? _imageLifetimeCancellation).Token;
        }

        lock (_svgHostDeferredSync)
        {
            if (!ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), deferral.Demand) ||
                deferral.LoadGeneration != Volatile.Read(ref _svgLoadDemandGeneration) ||
                (expectedDenialIdentity is not null &&
                    (!ReferenceEquals(_svgHostDeferredDenialIdentity, expectedDenialIdentity) ||
                        _svgHostDeferred != SvgHostDeferredWaiting ||
                        _svgHostDeferredCapacityReady ||
                        _svgHostDeferredWaiter is { IsFinished: false })))
            {
                return;
            }

            // A fresh denial replaces only this box's earlier source-free
            // waiter. The old registration is disposed after the lock.
            previousWaiter = _svgHostDeferredWaiter;
            _svgHostDeferredWaiter = null;
            _svgHostDeferredWaiterIdentity = null;
            _svgHostDeferredRegistrationAttempt = registrationAttempt;
            _svgHostDeferredDenialIdentity = registrationAttempt;
            _svgHostDeferredAdmission = new WeakReference<IMarkdownSvgHostAdmission>(deferral.Admission);
            _svgHostDeferred = SvgHostDeferredWaiting;
            _svgHostDeferredDemandVersion = deferral.DemandVersionAtDenial;
            _svgHostDeferredVisualDemandEpoch = deferral.VisualDemandEpochAtDenial;
            _svgHostDeferredCapacityVersion = deferral.CapacityVersion;
            _svgHostDeferredOwnerVersion = deferral.OwnerVersionAtDenial;
            _svgHostDeferredRequiredSourceBytes = deferral.RequiredSourceReservationBytes;
            _svgHostDeferredRequiresOwnerSlot = deferral.RequiresOwnerSlot;
            _svgHostDeferredCapacityReady = false;
            _loadStarted = false;
        }
        previousWaiter?.Dispose();

        SvgHostAdmissionCoordinator.PendingWaiter? waiter = null;
        try
        {
            waiter = SvgHostAdmissionCoordinator.Defer(
                this,
                deferral.Admission,
                deferral.Demand,
                deferral.LoadGeneration,
                deferral.CapacityVersion,
                deferral.RequiredSourceReservationBytes,
                deferral.RequiresOwnerSlot,
                deferral.OwnerVersionAtDenial,
                deferral.DemandVersionAtDenial,
                deferral.VisualDemandEpochAtDenial,
                registrationAttempt,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // The bounded waiter is an optimization for prompt recovery. A
            // saturated waiter table leaves only weak scalar state on this box;
            // a later demand/capacity observation can still restart it.
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG capacity wait could not be registered: {exception.GetType().Name}");
        }

        bool retained = false;
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred == SvgHostDeferredWaiting &&
                ReferenceEquals(_svgHostDeferredRegistrationAttempt, registrationAttempt) &&
                ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), deferral.Demand) &&
                deferral.LoadGeneration == Volatile.Read(ref _svgLoadDemandGeneration) &&
                waiter is not null &&
                ReferenceEquals(_svgHostDeferredWaiterIdentity, waiter.Identity) &&
                !waiter.IsFinished)
            {
                _svgHostDeferredWaiter = waiter;
                retained = true;
            }
            else if (ReferenceEquals(_svgHostDeferredRegistrationAttempt, registrationAttempt))
            {
                // A failed/finished registration has no live wait handle. Keep
                // the scalar denial state for a future demand observation, but
                // do not leave an identity that a later waiter could confuse.
                _svgHostDeferredRegistrationAttempt = null;
                if (_svgHostDeferred != SvgHostDeferredRetryQueued && !_svgHostDeferredCapacityReady)
                    _svgHostDeferredWaiterIdentity = null;
            }
        }
        if (!retained)
            waiter?.Dispose();

        // Close the denial-to-registration lost-wakeup window. This also
        // handles a waiter that completed synchronously before its handle was
        // stored on the box.
        TryResumeDeferredSvgLoadForObservedDemandOrCapacity(deferral.Demand);
    }

    private void TryResumeDeferredSvgLoadForObservedDemandOrCapacity(
        MarkdownSvgDemandTicket? demand)
    {
        if (demand is null)
            return;

        IMarkdownSvgHostAdmission? admission = null;
        int loadGeneration;
        long demandVersionAtDenial;
        long capacityVersionAtDenial;
        long ownerVersionAtDenial;
        long visualDemandEpochAtDenial;
        long requiredSourceBytes;
        bool requiresOwnerSlot;
        bool capacityReady;
        bool hasLiveWaiter;
        object? denialIdentity;
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred != SvgHostDeferredWaiting ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
                _svgHostDeferredAdmission is null ||
                !_svgHostDeferredAdmission.TryGetTarget(out admission))
            {
                return;
            }

            loadGeneration = Volatile.Read(ref _svgLoadDemandGeneration);
            demandVersionAtDenial = _svgHostDeferredDemandVersion;
            capacityVersionAtDenial = _svgHostDeferredCapacityVersion;
            ownerVersionAtDenial = _svgHostDeferredOwnerVersion;
            visualDemandEpochAtDenial = _svgHostDeferredVisualDemandEpoch;
            requiresOwnerSlot = _svgHostDeferredRequiresOwnerSlot;
            requiredSourceBytes = _svgHostDeferredRequiredSourceBytes;
            capacityReady = _svgHostDeferredCapacityReady;
            hasLiveWaiter = _svgHostDeferredWaiter is { IsFinished: false };
            denialIdentity = _svgHostDeferredDenialIdentity;
        }

        long demandVersion = demand.Version;
        long capacityVersion;
        long ownerVersion;
        try
        {
            capacityVersion = requiresOwnerSlot ? 0 : admission.CapacityVersion;
            ownerVersion = requiresOwnerSlot
                ? SvgHostAdmissionCoordinator.GetOwnerVersion(admission.AdmissionDomain)
                : 0;
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG capacity state could not be observed: {exception.GetType().Name}");
            return;
        }

        if (!requiresOwnerSlot && !capacityReady && !hasLiveWaiter &&
            denialIdentity is not null && capacityVersion > capacityVersionAtDenial)
        {
            // A release/version pulse may be smaller than the required peak,
            // including the failed attempt's own clone release. If a bounded
            // waiter could not be retained, re-enter only scalar registration
            // once for the real observed version, never source loading.
            DeferSvgHostLoad(new SvgHostCapacityDeferral(
                admission, demand, loadGeneration, capacityVersion,
                requiredSourceBytes, RequiresOwnerSlot: false,
                OwnerVersionAtDenial: 0,
                DemandVersionAtDenial: demandVersionAtDenial,
                VisualDemandEpochAtDenial: visualDemandEpochAtDenial), denialIdentity);
            return;
        }

        TryQueueDeferredSvgHostRetry(
            demand,
            loadGeneration,
            demandVersion > demandVersionAtDenial ? demandVersion : 0,
            capacityVersion > capacityVersionAtDenial ? capacityVersion : 0,
            ownerVersion > ownerVersionAtDenial ? ownerVersion : 0,
            Volatile.Read(ref _svgHostVisualDemandEpoch) > visualDemandEpochAtDenial
                ? Volatile.Read(ref _svgHostVisualDemandEpoch) : 0);
    }

    internal void TryQueueDeferredSvgHostRetry(
        MarkdownSvgDemandTicket demand,
        int loadGeneration,
        long observedDemandVersion,
        long observedCapacityVersion,
        long observedOwnerVersion,
        long observedVisualDemandEpoch = 0,
        bool capacityThresholdSatisfied = false,
        object? waiterIdentity = null)
    {
        SvgHostAdmissionCoordinator.PendingWaiter? waiter;
        SvgDeferredLoadWork retry;
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred != SvgHostDeferredWaiting ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
                loadGeneration != Volatile.Read(ref _svgLoadDemandGeneration))
            {
                return;
            }

            if (waiterIdentity is not null &&
                !ReferenceEquals(_svgHostDeferredWaiterIdentity, waiterIdentity))
            {
                return;
            }
            if (capacityThresholdSatisfied)
            {
                // Threshold readiness is independent of a numeric release
                // version. It belongs only to this accepted source waiter.
                if (_svgHostDeferredRequiresOwnerSlot || waiterIdentity is null)
                    return;
                _svgHostDeferredCapacityReady = true;
            }

            bool eligible = observedDemandVersion > _svgHostDeferredDemandVersion ||
                observedVisualDemandEpoch > _svgHostDeferredVisualDemandEpoch ||
                (_svgHostDeferredRequiresOwnerSlot
                    ? observedOwnerVersion > _svgHostDeferredOwnerVersion
                    : _svgHostDeferredCapacityReady);
            if (!eligible)
                return;

            retry = new SvgDeferredLoadWork(this, demand, loadGeneration,
                _svgHostDeferredDenialIdentity);
            _svgHostDeferred = SvgHostDeferredRetryQueued;
            waiter = _svgHostDeferredWaiter;
            _svgHostDeferredWaiter = null;
        }
        waiter?.Dispose();

        try
        {
            if (!QueueDeferredSvgRetry(retry, fallback: false))
                throw new InvalidOperationException("The primary SVG retry queue rejected the work.");
        }
        catch (Exception taskQueueFailure)
        {
            // Task.Run can fail under transient scheduler pressure. Make one
            // second non-inline queue attempt; if both fail, leave the exact
            // denial eligible for the next demand/capacity observation.
            bool queued = false;
            Exception? fallbackQueueFailure = null;
            try
            {
                queued = QueueDeferredSvgRetry(retry, fallback: true);
            }
            catch (Exception exception)
            {
                fallbackQueueFailure = exception;
            }

            if (queued)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[ImageBox] SVG deferred retry used the fallback queue after {taskQueueFailure.GetType().Name}.");
                return;
            }

            lock (_svgHostDeferredSync)
            {
                if (_svgHostDeferred == SvgHostDeferredRetryQueued &&
                    ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) &&
                    loadGeneration == Volatile.Read(ref _svgLoadDemandGeneration) &&
                    ReferenceEquals(_svgHostDeferredDenialIdentity, retry.DenialIdentity))
                {
                    // Retain the real threshold reason and accepted identity
                    // for one retry on a later demand/paint observation. Do
                    // not recurse into an immediately-ready allocation loop.
                    _svgHostDeferred = SvgHostDeferredWaiting;
                }
            }
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG deferred retry could not be queued: {taskQueueFailure.GetType().Name}" +
                (fallbackQueueFailure is null ? string.Empty : $"/{fallbackQueueFailure.GetType().Name}"));
        }
    }

    private bool QueueDeferredSvgRetry(SvgDeferredLoadWork retry, bool fallback)
    {
        if (_svgDeferredRetryEnqueueOverride is { } testQueue)
            return testQueue(() => RunDeferredSvgLoad(retry));
        if (fallback)
        {
            return ThreadPool.UnsafeQueueUserWorkItem(
                static work => RunDeferredSvgLoad(work), retry, preferLocal: false);
        }
        _ = Task.Run(() => RunDeferredSvgLoad(retry));
        return true;
    }

    private static void RunDeferredSvgLoad(SvgDeferredLoadWork work)
    {
        try
        {
            work.Owner.StartDeferredSvgLoad(work.Demand, work.LoadGeneration, work.DenialIdentity);
        }
        catch (Exception exception)
        {
            // StartLoad owns retryable errors; this boundary prevents an
            // unexpected synchronous setup failure from escaping the pool.
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG deferred retry setup failed: {exception.GetType().Name}");
        }
    }

    private void StartDeferredSvgLoad(
        MarkdownSvgDemandTicket demand, int loadGeneration, object? denialIdentity)
    {
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred != SvgHostDeferredRetryQueued ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
                loadGeneration != Volatile.Read(ref _svgLoadDemandGeneration) ||
                !ReferenceEquals(_svgHostDeferredDenialIdentity, denialIdentity))
            {
                return;
            }
            ClearSvgHostDeferralNoLock();
            _loadStarted = false;
        }

        StartLoad(
            isVisible: demand.Priority == MarkdownSvgRenderPriority.Visible,
            demandOverride: demand,
            loadDemandGeneration: loadGeneration);
    }

    internal bool OnSvgHostAdmissionWaiterRegistered(
        MarkdownSvgDemandTicket demand,
        int loadGeneration,
        object registrationAttempt,
        SvgHostAdmissionCoordinator.PendingWaiter waiter)
    {
        lock (_svgHostDeferredSync)
        {
            if (_svgHostDeferred == SvgHostDeferredWaiting &&
                ReferenceEquals(_svgHostDeferredRegistrationAttempt, registrationAttempt) &&
                ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) &&
                loadGeneration == Volatile.Read(ref _svgLoadDemandGeneration) &&
                !waiter.IsFinished)
            {
                _svgHostDeferredWaiterIdentity = waiter.Identity;
                _svgHostDeferredDenialIdentity ??= registrationAttempt;
                return true;
            }
        }
        return false;
    }

    internal void OnSvgHostAdmissionWaiterFinished(
        MarkdownSvgDemandTicket demand,
        int loadGeneration,
        object waiterIdentity)
    {
        SvgHostAdmissionCoordinator.PendingWaiter? waiter = null;
        lock (_svgHostDeferredSync)
        {
            if (ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) &&
                loadGeneration == Volatile.Read(ref _svgLoadDemandGeneration) &&
                ReferenceEquals(_svgHostDeferredWaiterIdentity, waiterIdentity))
            {
                waiter = _svgHostDeferredWaiter;
                _svgHostDeferredWaiter = null;
                if (_svgHostDeferred != SvgHostDeferredRetryQueued && !_svgHostDeferredCapacityReady)
                {
                    _svgHostDeferredWaiterIdentity = null;
                    _svgHostDeferredRegistrationAttempt = null;
                }
            }
        }
        waiter?.Dispose();
    }

    private void ClearSvgHostDeferral()
    {
        IDisposable? waiter;
        lock (_svgHostDeferredSync)
            waiter = ClearSvgHostDeferralNoLock();
        waiter?.Dispose();
    }

    private IDisposable? ClearSvgHostDeferralNoLock()
    {
        IDisposable? waiter = _svgHostDeferredWaiter;
        _svgHostDeferredWaiter = null;
        _svgHostDeferredWaiterIdentity = null;
        _svgHostDeferredRegistrationAttempt = null;
        _svgHostDeferredDenialIdentity = null;
        _svgHostDeferredAdmission = null;
        _svgHostDeferred = SvgHostDeferredNone;
        _svgHostDeferredDemandVersion = 0;
        _svgHostDeferredCapacityVersion = 0;
        _svgHostDeferredOwnerVersion = 0;
        _svgHostDeferredVisualDemandEpoch = 0;
        _svgHostDeferredRequiredSourceBytes = 0;
        _svgHostDeferredRequiresOwnerSlot = false;
        _svgHostDeferredCapacityReady = false;
        return waiter;
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    internal bool EnsureLoadingForImageActivationTrace(
        long generation,
        int imageOrdinal,
        int viewportMeasured,
        int intersectsViewport,
        long viewportWidthSixtyFourthDips,
        long viewportHeightSixtyFourthDips)
    {
        if (_loadStarted)
            return false;

        var trace = new MarkdownImageActivationCorrelation(
            generation,
            imageOrdinal,
            viewportMeasured,
            intersectsViewport,
            viewportWidthSixtyFourthDips,
            viewportHeightSixtyFourthDips);
        RecordImageActivationStage(trace, MarkdownPerformanceEventSource.ImageActivationStageEnsureLoading);
        StartLoad(trace, isVisible: intersectsViewport != 0);
        return true;
    }

    private static void RecordImageActivationStage(
        MarkdownImageActivationCorrelation trace,
        int stageId) => trace.RecordStage(stageId);
#endif

    public override float Measure(float availableWidth)
    {
        _availableWidth = availableWidth;
        if (ShouldCollapseFailedDecorativeImage)
        {
            CollapseFailedDecorativeImage(availableWidth);
            return 0;
        }

        float maxW = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
        Size rasterIntrinsicSize = GetRasterMeasurementIntrinsicSize();
        float w, h;

        if (_bitmap is { } bmu)
        {
            // Intrinsic-first snznng: render at the bitmap's natural size and
            // only downscale (preservnng asuect) when the intrinsic width
            // exceeds the available column. For SVGs that have a known
            // intrinsic size from cache, prefer that over the rasterized
            // pixel dimensions (which include the DPI multiplier).
            float bw, bh;
            if (_isSvg && _svgIntrinsicSize.Width > 0 && _svgIntrinsicSize.Height > 0)
            {
                bw = (float)_svgIntrinsicSize.Width;
                bh = (float)_svgIntrinsicSize.Height;
            }
            else if (!_isSvg && _rasterIntrinsicSize.Width > 0 && _rasterIntrinsicSize.Height > 0)
            {
                bw = (float)_rasterIntrinsicSize.Width;
                bh = (float)_rasterIntrinsicSize.Height;
            }
            else
            {
                bw = (float)bmu.Size.Width;
                bh = (float)bmu.Size.Height;
            }
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            w = bw * scale;
            h = bh * scale;
        }
        else if (!_isSvg && rasterIntrinsicSize.Width > 0 && rasterIntrinsicSize.Height > 0)
        {
            float scale = Math.Min(1f, maxW / (float)rasterIntrinsicSize.Width);
            w = (float)rasterIntrinsicSize.Width * scale;
            h = (float)rasterIntrinsicSize.Height * scale;
        }
        else if (_isSvg && (_svgIntrinsicSize.Width > 0 || _loadStarted))
        {
            // SVG load is in flnght. Reserve space using the intrinsic size
            // we recovered from the cache; fall back to a 16:9-nsh band
            // when we have no intrinsic at all yet.
            float bw = _svgIntrinsicSize.Width > 0 ? (float)_svgIntrinsicSize.Width : maxW;
            float bh = _svgIntrinsicSize.Height > 0 ? (float)_svgIntrinsicSize.Height : 200f;
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            w = bw * scale;
            h = bh * scale;
        }
        else
        {
            // Placeholder height = 32px alt-text band, stretched to column.
            w = maxW;
            h = 32f;
        }

        ApplyRequestedSize(ref w, ref h, maxW);
        _imageWidth = w;
        _imageHeight = h;
        if (_bitmap is null)
        {
            UpdatePlaceholder(w, h);
        }

        // Build a replacement before detaching the current caption. A factory
        // or Options failure leaves the source placement intact, and the
        // candidate's pre-reserved record retains any native layout.
        CanvasTextLayout? replacementCaption = null;
        NativeResourceRetirementRecord? replacementCaptionRecord = null;
        float replacementCaptionHeight = 0f;
        if (!string.IsNullOrEmpty(Alt))
        {
            var cs = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            using SharedCanvasTextFormatCache.Lease cfmtLease = SharedCanvasTextFormatCache.Acquire(
                cs,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Center,
                Context.FlowDirection,
                Context.Language,
                true,
                CanvasVerticalAlignment.Top);
            replacementCaption = CreateOwnedTextLayout(
                Alt,
                cfmtLease.Format,
                maxW,
                float.MaxValue,
                out replacementCaptionRecord);
            try
            {
                replacementCaptionHeight = (float)replacementCaption.LayoutBounds.Height
                                 + (float)(cs.Margin.Top + cs.Margin.Bottom);
            }
            catch
            {
                replacementCaptionRecord.Publish();
                throw;
            }
        }

        ReplaceCaption(replacementCaption, replacementCaptionRecord, replacementCaptionHeight);

        float contentHeight = Math.Max(h, GetParagraphLineHeight());
        float total = contentHeight + replacementCaptionHeight + (float)(Margin.Top + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, total);
        return total;
    }

    private Size GetRasterMeasurementIntrinsicSize()
    {
        if (_isSvg || _bitmap is not null || _loadFailed ||
            _rasterIntrinsicSize.Width > 0 || _rasterIntrinsicSize.Height > 0)
        {
            return _rasterIntrinsicSize;
        }

        if (Context.PerformanceSession is not { IsDisposed: false } session ||
            !session.Options.PrefetchDocumentImages ||
            !session.Options.UseDisplaySizedRasterDecode ||
            Context.ImageResolver is not IMarkdownPerformanceDocumentScope scope ||
            !scope.TryGetPrefetchedRasterIntrinsicMetadata(Url, out RasterIntrinsicMetadata metadata))
        {
            return default;
        }

        // A scalar hint only: do not claim an authoritative source, retain a
        // buffer/lease, start a load or mutate private-placement descriptor
        // fences. Cache misses never parse or wait during measurement.
        return new Size(metadata.Width, metadata.Height);
    }

    internal bool HasExactReservedRasterGeometry(Size intrinsicSize)
    {
        if (_disposed || _resourcesSuspended || _isSvg || _loadFailed ||
            _inlineMeasureCheckpointActive || !float.IsFinite(_availableWidth) ||
            _availableWidth <= 0 || !double.IsFinite(intrinsicSize.Width) ||
            !double.IsFinite(intrinsicSize.Height) || intrinsicSize.Width <= 0 ||
            intrinsicSize.Height <= 0 || intrinsicSize.Width > int.MaxValue ||
            intrinsicSize.Height > int.MaxValue)
        {
            return false;
        }

        float maxWidth = Math.Max(1f, _isInlineLayout ? _availableWidth :
            _availableWidth - (float)(Margin.Left + Margin.Right));
        float scale = Math.Min(1f, maxWidth / (float)intrinsicSize.Width);
        float width = (float)intrinsicSize.Width * scale;
        float height = (float)intrinsicSize.Height * scale;
        if (_isInlineLayout)
        {
            width = Math.Max(1f, width);
            height = Math.Max(1f, height);
        }
        ApplyRequestedSize(ref width, ref height, maxWidth);
        if (_imageWidth != width || _imageHeight != height)
            return false;

        // Prove actual committed geometry, not just matching advisory source
        // dimensions. Inline checkpoint rollback and clipped inline placement
        // can restore a different rectangle before publication.
        return _isInlineLayout
            ? Bounds.Width == width && Bounds.Height == height && _captionHeight == 0
            : Bounds.Width == _availableWidth && Bounds.Height ==
                Math.Max(height, GetParagraphLineHeight()) + _captionHeight +
                (float)(Margin.Top + Margin.Bottom);
    }

    private void ApplyRequestedSize(ref float width, ref float height, float availableWidth)
    {
        float naturalWidth = Math.Max(1f, width);
        float naturalHeight = Math.Max(1f, height);
        float aspect = naturalWidth / naturalHeight;
        float? requestedWidth = _requestedWidth?.Resolve(availableWidth);
        float? requestedHeight = _requestedHeight is { IsPercent: false } heightLength
            ? heightLength.Resolve(availableWidth)
            : null;

        if (requestedWidth is > 0 && requestedHeight is > 0)
        {
            float scale = Math.Min(1f, availableWidth / requestedWidth.Value);
            width = Math.Max(1f, requestedWidth.Value * scale);
            height = Math.Max(1f, requestedHeight.Value * scale);
            return;
        }

        if (requestedWidth is > 0)
        {
            width = Math.Max(1f, Math.Min(availableWidth, requestedWidth.Value));
            height = Math.Max(1f, width / Math.Max(0.001f, aspect));
            return;
        }

        if (requestedHeight is > 0)
        {
            height = Math.Max(1f, requestedHeight.Value);
            width = Math.Max(1f, height * aspect);
            if (width > availableWidth)
            {
                float scale = availableWidth / width;
                width = availableWidth;
                height = Math.Max(1f, height * scale);
            }
        }
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        float x = GetContentX();
        float y = GetContentY();

        // Use cached image dimensions from Measure() so paint matches layout.
        var imageRect = new Rect(x, y, _imageWidth, _imageHeight);
        PaintImageContent(ds, imageRect, viewport);

        if (_caption is not null)
        {
            var cs = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            float cy = (float)(Bounds.Y + Margin.Top) +
                Math.Max(_imageHeight, GetParagraphLineHeight()) + (float)cs.Margin.Top;
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(ds, _caption, x, cy, cs.Foreground);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    public override System.Collections.Generic.IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        var n = range.Normalized();
        if (BlockIndex >= n.Start.BlockIndex && BlockIndex <= n.End.BlockIndex
            && !(n.Start.BlockIndex == n.End.BlockIndex
                 && n.Start.InlineIndex == n.End.InlineIndex
                 && n.Start.CharacterOffset == n.End.CharacterOffset))
        {
            yield return Bounds;
        }
    }

    public override void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Color color, Rect viewport)
    {
        var n = range.Normalized();
        if (BlockIndex < n.Start.BlockIndex || BlockIndex > n.End.BlockIndex ||
            (n.Start.BlockIndex == n.End.BlockIndex
             && n.Start.InlineIndex == n.End.InlineIndex
             && n.Start.CharacterOffset == n.End.CharacterOffset))
        {
            return;
        }

        if (Bounds.Right < viewport.Left || Bounds.Left > viewport.Right ||
            Bounds.Bottom < viewport.Top || Bounds.Top > viewport.Bottom)
        {
            return;
        }

        float x = GetContentX();
        float y = GetContentY();
        var imageRect = new Rect(x, y, _imageWidth, _imageHeight);

        if (imageRect.Width > 0 && imageRect.Height > 0)
        {
            NativePaintSessionResources.LayerLease selectionLayer =
                NativePaintSessionResources.CreateLayer(ds, 0.82f, imageRect);
            try
            {
                PaintImageContent(ds, imageRect, viewport);
            }
            catch (Exception primary)
            {
                selectionLayer.ClosePreservingPrimary(primary);
                throw;
            }
            selectionLayer.Close();

            ds.DrawRoundedRectangle(imageRect, 3, 3, color, 2f);
        }

        if (_caption is not null)
        {
            var cs = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            float cy = (float)(Bounds.Y + Margin.Top) +
                Math.Max(_imageHeight, GetParagraphLineHeight()) + (float)cs.Margin.Top;
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(ds, _caption, x, cy, color);
        }
    }

    public override void Dispose()
    {
        TaskCompletionSource retirementCompletion;
        lock (_svgWorkSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            retirementCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _imageRetirementCompletion = retirementCompletion;
        }

        ClearSvgHostDeferral();
        ClearBitmapOutputDeferral();
        InvalidateInlineCheckpointForDisposal();

        AdvanceSvgRenderGeneration();
        Task cancellation;
        try
        {
            cancellation = _imageLifetimeCancellation.CancelAsync();
        }
        catch (Exception exception)
        {
            cancellation = Task.FromException(exception);
        }

        _ = CompleteImageRetirementAsync(cancellation, retirementCompletion);
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        Dispose();
        Task retirement = RetirementTask;
        if (!retirement.IsCompleted || retirement.IsFaulted || retirement.IsCanceled)
            pendingTasks.Add(retirement);
    }

    private bool TryBeginImageWork(out ImageWorkLease? work)
    {
        lock (_svgWorkSync)
        {
            if (_disposed || _resourcesSuspended)
            {
                work = null;
                return false;
            }

            if (_svgWorkCount == 0)
            {
                _imageWorkDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (_imageResidencyCancellation is null &&
                Context.PerformanceSession?.Options.ReleaseOffscreenImageResources == true)
                _imageResidencyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _imageLifetimeCancellation.Token);
            var createdWork = new ImageWorkLease(this,
                (_imageResidencyCancellation ?? _imageLifetimeCancellation).Token);
            _svgWorkCount++;
            work = createdWork;
            return true;
        }
    }

    private void CompleteImageWork()
    {
        TaskCompletionSource? workDrained = null;
        lock (_svgWorkSync)
        {
            if (_svgWorkCount <= 0)
                return;

            _svgWorkCount--;
            if (_svgWorkCount == 0)
                workDrained = _imageWorkDrained;
        }

        workDrained?.TrySetResult();
        if (workDrained is not null)
            ArmDeferredBitmapOutputRetryAfterWorkDrained();
        if (workDrained is not null && _resourcesSuspended && !_disposed)
        {
            try { ResidencyWorkDrained?.Invoke(this, EventArgs.Empty); }
            catch (Exception exception) { RecordImageRetirementFailure(exception); }
        }
    }

    private ImageWorkLease BeginImageWorkNoLock(CancellationToken token)
    {
        var lease = new ImageWorkLease(this, token);
        AddImageWorkNoLock();
        return lease;
    }

    private void AddImageWorkNoLock()
    {
        if (_svgWorkCount == 0)
        {
            _imageWorkDrained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _svgWorkCount++;
    }

    private void CompleteSvgDocumentUse(SvgDocumentUseState state, bool candidate)
    {
        ImageWorkLease? disposalWork = null;
        TaskCompletionSource? workDrained = null;
        lock (_svgWorkSync)
        {
            if (state.ActiveUses <= 0 || _svgWorkCount <= 0)
                return;

            if (candidate)
                state.CandidateReserved = false;

            if (state.Retired && state.ActiveUses == 1 && !state.DisposalScheduled)
            {
                // Reserve disposal before releasing this use so terminal
                // retirement never observes a false zero-work gap.
                disposalWork = state.ReservedDisposalWork;
                state.ReservedDisposalWork = null;
                disposalWork ??= BeginImageWorkNoLock(CancellationToken.None);
                state.DisposalScheduled = true;
            }

            state.ActiveUses--;
            _svgWorkCount--;
            if (_svgWorkCount == 0)
                workDrained = _imageWorkDrained;
        }

        if (disposalWork is not null)
            ScheduleSvgDocumentDisposal(state.Document, disposalWork);

        workDrained?.TrySetResult();
        if (workDrained is not null)
            ArmDeferredBitmapOutputRetryAfterWorkDrained();
        if (workDrained is not null && _resourcesSuspended && !_disposed)
        {
            try { ResidencyWorkDrained?.Invoke(this, EventArgs.Empty); }
            catch (Exception exception) { RecordImageRetirementFailure(exception); }
        }
    }

    private void TrackDetachedImageWork(
        Task detachedOperation,
        bool failRetirementOnFault = false)
    {
        ArgumentNullException.ThrowIfNull(detachedOperation);
        if (detachedOperation.IsCompleted)
        {
            if (detachedOperation.IsFaulted)
            {
                _ = detachedOperation.Exception;
                if (failRetirementOnFault)
                    RecordImageRetirementFailure(detachedOperation.Exception!.GetBaseException());
            }
            return;
        }

        ImageWorkLease? lease = null;
        TaskCompletionSource? lateWorkCompletion = null;
        bool lateRetirementWork = false;
        bool retirementAlreadyCompleted = false;
        lock (_svgWorkSync)
        {
            if (_imageRetirementCompleted)
            {
                retirementAlreadyCompleted = true;
            }
            else
            {
                // A child operation normally registers while its parent lease
                // is still active. If a future caller violates that ordering,
                // reopen the drain before finalization or add a task-only late
                // barrier while native cleanup is finalizing. Never silently
                // report successful retirement with detached work outstanding.
                if (_disposed && _svgWorkCount == 0)
                {
                    RecordImageRetirementFailureNoLock(new InvalidOperationException(
                        "Detached image work escaped its parent retirement lease."));
                    if (_imageRetirementFinalizing)
                    {
                        lateWorkCompletion = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        (_imageRetirementLateWork ??= []).Add(lateWorkCompletion.Task);
                        lateRetirementWork = true;
                    }
                }

                if (!lateRetirementWork)
                {
                    if (_svgWorkCount == 0)
                    {
                        _imageWorkDrained = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    _svgWorkCount++;
                    lease = new ImageWorkLease(this, CancellationToken.None);
                }
            }
        }

        if (retirementAlreadyCompleted)
        {
            TaskFaultObservation.ObserveFault(detachedOperation);
            var escapedWork = new InvalidOperationException(
                "Detached image work was registered after its retirement barrier completed.");
            RecordImageRetirementFailure(escapedWork);
            throw escapedWork;
        }

        _ = detachedOperation.ContinueWith(
            static (completed, state) =>
            {
                var tracking = ((ImageBox Owner, ImageWorkLease? Lease, bool FailRetirement, TaskCompletionSource? LateWorkCompletion))state!;
                if (completed.IsFaulted)
                {
                    if (tracking.FailRetirement)
                        tracking.Owner.RecordImageRetirementFailure(
                            completed.Exception!.GetBaseException());
                    else
                        _ = completed.Exception;
                }

                tracking.Lease?.Dispose();
                tracking.LateWorkCompletion?.TrySetResult();
            },
            (this, lease, failRetirementOnFault, lateWorkCompletion),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RecordImageRetirementFailure(Exception failure)
    {
        lock (_svgWorkSync)
            RecordImageRetirementFailureNoLock(failure);
    }

    private void RecordImageRetirementFailureNoLock(Exception failure)
    {
        if (_imageRetirementFailures is null ||
            _imageRetirementFailures.Count < MaximumRetirementFailureDetails)
        {
            (_imageRetirementFailures ??= []).Add(failure);
            return;
        }

        _omittedRetirementFailureCount++;
    }

    private void TryImageCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            // Cleanup errors are retained by the task-only retirement barrier.
            // Do not let one failed native release skip the remaining cleanup
            // actions or replace the operation's primary failure.
            RecordImageRetirementFailure(exception);
        }
    }

    private static void ReleasePendingRasterOutputs(
        ImageBox owner,
        ref SharedCanvasBitmapCache.Lease? bitmapLease,
        ref CanvasBitmap? ownedBitmap)
    {
        if (bitmapLease is { } lease)
            owner.TryImageCleanup(lease.Dispose);
        bitmapLease = null;
        if (ownedBitmap is { } bitmap)
            owner.TryImageCleanup(bitmap.Dispose);
        ownedBitmap = null;
    }

    private void ClearRetiredImageSourceStateNoLock()
    {
        // The snapshot owner retires boxes only after its paint readers drain,
        // and every admitted/late image operation has drained before this is
        // called. Clear only box-owned source/cache identity references: never
        // mutate arrays shared with the SVG cache, and retain all UIA-facing
        // and user-facing metadata.
        _svgSourceIdentity = null;
        _svgSourceByteLength = 0;
        _svgContentHash = null;
        _svgSecurityPartitionHash = null;
        _svgInfo = null;
        _svgRendererIdentity = 0;
        _svgProviderCacheGeneration = 0;
        Volatile.Write(ref _svgTileRenderPlan, null);
        _activeCacheKey = null;
        _svgDocumentUses = null;
    }

    private async Task<T> AwaitTrackedSvgResultAsync<T>(
        ValueTask<T> operation,
        CancellationToken cancellationToken,
        SvgDocumentUseLease? documentUse = null)
        where T : IDisposable
    {
        Task<T>? task = null;
        bool useTransferred = false;
        try
        {
            task = operation.AsTask();
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (task is not null)
            {
                // This task includes disposal of a result which races cancellation.
                // Keep the document's native-use lease with that task too: the
                // provider may ignore cancellation and still be reading its tree.
                Task retirement = RetireUnclaimedSvgResult(task, documentUse);
                useTransferred = documentUse is not null;
                TrackDetachedImageWork(retirement, failRetirementOnFault: true);
            }
            throw;
        }
        finally
        {
            if (!useTransferred)
                documentUse?.Dispose();
        }
    }

    private async Task CompleteImageRetirementAsync(
        Task cancellation,
        TaskCompletionSource completion)
    {
        List<Exception>? failures = null;
        int omittedFailures = 0;

        void AddFailure(Exception exception)
        {
            if (failures is null || failures.Count < MaximumRetirementFailureDetails)
                (failures ??= []).Add(exception);
            else
                omittedFailures++;
        }

        try
        {
            // A ready image has no outstanding cancellation/work to await.
            // Still leave the caller (including the UI thread) before native
            // cleanup; omitting ContinueOnCapturedContext keeps that work on
            // the pool even when the cancellation task is already complete.
            await cancellation.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (Exception exception)
        {
            AddFailure(exception);
        }

        while (true)
        {
            Task? activeWork = null;
            lock (_svgWorkSync)
            {
                if (_svgWorkCount == 0)
                {
                    _imageRetirementFinalizing = true;
                }
                else
                {
                    activeWork = _imageWorkDrained?.Task ?? Task.CompletedTask;
                }
            }

            if (activeWork is null)
                break;

            try
            {
                await activeWork.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddFailure(exception);
            }
        }

        try
        {
            IMarkdownSvgDocument? terminalDocument;
            SvgHostAdmissionCoordinator.OwnerSlot? terminalOwnerSlot;
            lock (_svgWorkSync)
            {
                terminalDocument = _svgDocument;
                _svgDocument = null;
                terminalOwnerSlot = Volatile.Read(ref _svgTileRenderPlan)?.OwnerSlot;
                Volatile.Write(ref _svgTileRenderPlan, null);
            }
            ClearSvgHostDeferral();
            ClearBitmapOutputDeferral();
            terminalOwnerSlot?.Dispose();
            if (terminalDocument is not null)
            {
                try { terminalDocument.Dispose(); }
                catch (Exception exception) { AddFailure(exception); }
            }
            try { ReleaseSvgTiles(); } catch (Exception exception) { AddFailure(exception); }
            try { ReleaseBitmap(); } catch (Exception exception) { AddFailure(exception); }
            try { RetireNativeLayoutsForDisposal(AddFailure); }
            catch (Exception exception) { AddFailure(exception); }
            LoadCompleted = null;
        }
        finally
        {
            try { _imageResidencyCancellation?.Dispose(); } catch (Exception exception) { AddFailure(exception); }
            try { _imageLifetimeCancellation.Dispose(); } catch (Exception exception) { AddFailure(exception); }
            lock (_svgWorkSync)
                _svgCancellationDisposed = true;
        }

        while (true)
        {
            Task[] lateWork;
            lock (_svgWorkSync)
            {
                if (_imageRetirementLateWork is null || _imageRetirementLateWork.Count == 0)
                {
                    ClearRetiredImageSourceStateNoLock();
                    ResidencyWorkDrained = null;
                    _imageRetirementCompleted = true;
                    if (_imageRetirementFailures is { } detachedFailures)
                    {
                        foreach (Exception detachedFailure in detachedFailures)
                            AddFailure(detachedFailure);
                    }
                    omittedFailures += _omittedRetirementFailureCount;
                    if (omittedFailures > 0)
                    {
                        bool replacedDetail = failures is { Count: >= MaximumRetirementFailureDetails };
                        if (replacedDetail)
                            omittedFailures++;
                        var summary = new InvalidOperationException(
                            $"{omittedFailures} additional image retirement failures were omitted.");
                        if (replacedDetail)
                            failures![^1] = summary;
                        else
                            (failures ??= []).Add(summary);
                    }

                    if (failures is null)
                    {
                        completion.TrySetResult();
                    }
                    else
                    {
                        completion.TrySetException(failures.Count == 1
                            ? failures[0]
                            : new AggregateException("Image box retirement failed.", failures));
                    }
                    return;
                }

                lateWork = _imageRetirementLateWork.ToArray();
                _imageRetirementLateWork.Clear();
            }

            // These entries are task-only completion signals. Their continuations
            // record cleanup failures before signaling, avoiding an ordering race
            // between observation and retirement completion.
            await Task.WhenAll(lateWork).ConfigureAwait(false);
        }
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private void StartLoad(
        MarkdownImageActivationCorrelation? activationTrace = null,
        bool isVisible = true,
        MarkdownSvgDemandTicket? demandOverride = null,
        int loadDemandGeneration = 0)
#else
    private void StartLoad(
        bool isVisible = true,
        MarkdownSvgDemandTicket? demandOverride = null,
        int loadDemandGeneration = 0)
#endif
    {
        if (!TryBeginImageWork(out ImageWorkLease? setupWork) || setupWork is null)
            return;

        using ImageWorkLease loadSetup = setupWork;
        ClearBitmapOutputDeferral(preserveRetryOffer: demandOverride is not null);
        IDisposable? previousDeferredWaiter = null;
        MarkdownSvgDemandTicket demand;
        lock (_svgHostDeferredSync)
        {
            if (demandOverride is null)
            {
                previousDeferredWaiter = ClearSvgHostDeferralNoLock();
                demand = new MarkdownSvgDemandTicket(isVisible
                    ? MarkdownSvgRenderPriority.Visible
                    : MarkdownSvgRenderPriority.Overscan);
                loadDemandGeneration = Interlocked.Increment(ref _svgLoadDemandGeneration);
                Volatile.Write(ref _svgLoadDemandTicket, demand);
            }
            else
            {
                if (!ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demandOverride) ||
                    loadDemandGeneration != Volatile.Read(ref _svgLoadDemandGeneration))
                {
                    return;
                }
                demand = demandOverride;
            }
            _loadStarted = true;
        }
        previousDeferredWaiter?.Dispose();
        if (string.IsNullOrEmpty(Url)) { _loadFailed = true; return; }

        // A source-cache hit can often also hit the exact device bitmap cache.
        // This path is synchronous and never wakes the worker.
        if (_isSvg)
        {
            if (TryPublishCachedSvgBitmap(layoutInvalidated: false,
                    expectedWork: CaptureBitmapOutputWorkContext(loadSetup.Token, expectedDemand: demand)))
                return;
        }

        // SVG data URIs (data:image/svg+xml,...) may contain raw < > characters
        // that are illegal in RFC 3986, which causes Uri.TryCreate to return
        // false. Parse them directly from the raw URL string.
        if (_isSvg && Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadSvgDataUriAsync(Url, demand, loadDemandGeneration);
            return;
        }

        if (Context.ImageResolver is { } resolver)
        {
            // Opt-in prefetch already calls this resolver off the UI thread. A
            // visible source-cache hit can otherwise run its synchronous
            // resolver prefix inside snapshot commit. Start it on the session's
            // bounded activation lane rather than waiting behind ThreadPool work.
            if (Context.PerformanceSession is { } performanceSession)
            {
                if (!TryBeginImageWork(out ImageWorkLease? queuedWork) || queuedWork is null)
                    return;

#if MARKDOWN_LAYOUT_DIAGNOSTICS
                if (activationTrace is { } queuedTrace)
                {
                    RecordImageActivationStage(
                        queuedTrace,
                        MarkdownPerformanceEventSource.ImageActivationStageSchedulerQueued);
                }
                bool scheduled;
                try
                {
                    scheduled = performanceSession.TryScheduleImageActivation(
                        demand,
                        () =>
                        {
                            if (activationTrace is { } enteredTrace)
                            {
                                RecordImageActivationStage(
                                    enteredTrace,
                                    MarkdownPerformanceEventSource.ImageActivationStageSchedulerEntered);
                            }
                            Task resolution = ResolveAndLoadAsync(resolver, demand, loadDemandGeneration, activationTrace);
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
#else
                bool scheduled;
                try
                {
                    scheduled = performanceSession.TryScheduleImageActivation(
                        demand,
                        () =>
                        {
                            Task resolution = ResolveAndLoadAsync(resolver, demand, loadDemandGeneration);
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
#endif
                if (!scheduled)
                {
                    ResetLoadStartedAfterUnstartedActivation(
                        demand,
                        loadDemandGeneration,
                        queuedWork.Token);
                    queuedWork.Dispose();
                }
            }
            else
            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                _ = ResolveAndLoadAsync(resolver, demand, loadDemandGeneration, activationTrace);
#else
                _ = ResolveAndLoadAsync(resolver, demand, loadDemandGeneration);
#endif
            }
            return;
        }

        if (_builtInSource is not { } source)
        {
            PublishFailure(cacheKey: string.Empty);
            return;
        }

        LoadFromDefaultSource(source, demand, loadDemandGeneration);
    }

    private void ResetLoadStartedAfterUnstartedActivation(
        MarkdownSvgDemandTicket demand,
        int loadGeneration,
        CancellationToken activationCancellation)
    {
        // The volatile lifecycle flags are checked alongside the exact load
        // identity under StartLoad's gate. A replacement must take this same
        // gate before it can set _loadStarted again, so a late callback cannot
        // clear a newer activation even if a resumed box reuses its ticket;
        // the old residency token is canceled before that callback can reset.
        lock (_svgHostDeferredSync)
        {
            if (_disposed || _resourcesSuspended ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
                loadGeneration != Volatile.Read(ref _svgLoadDemandGeneration) ||
                activationCancellation.IsCancellationRequested)
            {
                return;
            }

            _loadStarted = false;
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

    private void LoadFromDefaultSource(
        MarkdownBuiltInImageSource source,
        MarkdownSvgDemandTicket demand,
        int loadDemandGeneration)
    {
        switch (source.Kind)
        {
            case MarkdownBuiltInImageSourceKind.InsecureHttp:
                ReportUnavailable(MarkdownImageUnavailableReason.InsecureRemoteContent);
                PublishFailure(cacheKey: string.Empty);
                return;

            case MarkdownBuiltInImageSourceKind.RemoteHttps:
                // Network loading is resolver-owned so the host can apply consent,
                // authentication, account partitioning, and its own fetch cache.
                ReportUnavailable(MarkdownImageUnavailableReason.RemoteContentBlocked);
                PublishFailure(cacheKey: string.Empty);
                return;

            case MarkdownBuiltInImageSourceKind.Data:
            case MarkdownBuiltInImageSourceKind.Local:
                _ = LoadBuiltInImageAsync(source, _isSvg, demand, loadDemandGeneration);
                return;

            default:
                ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
                PublishFailure(cacheKey: string.Empty);
                return;
        }
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private async Task ResolveAndLoadAsync(
        IMarkdownImageResolver resolver,
        MarkdownSvgDemandTicket demand,
        int loadDemandGeneration,
        MarkdownImageActivationCorrelation? activationTrace = null)
#else
    private async Task ResolveAndLoadAsync(
        IMarkdownImageResolver resolver,
        MarkdownSvgDemandTicket demand,
        int loadDemandGeneration)
#endif
    {
        // Keep the document resolver and any returned-but-not-yet-rasterized
        // source inside this box's retirement barrier. The lifetime token is
        // linked to the host token, so replacement cancels either path.
        if (!TryBeginImageWork(out ImageWorkLease? admittedWork) || admittedWork is null)
            return;

        using ImageWorkLease imageWork = admittedWork;
        CancellationToken lifetimeToken = imageWork.Token;
        BitmapOutputWorkContext bitmapOutputWork = CaptureBitmapOutputWorkContext(
            lifetimeToken, expectedDemand: demand);

        MarkdownImageResolution resolution;
        MarkdownImageAsset? asset = null;
        IMarkdownImageResolutionLease? sourceLease = null;
        Task? resolverTask = null;
        var resolveContext = new MarkdownImageResolveContext(
            Context.ImageBaseUri,
            Context.ImageDocumentPath,
            Context.AllowThirdPartyRemoteImages,
            Context.ImageDocumentSource);

        ValueTask<MarkdownImageResolution> StartResolver(CancellationToken token)
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (activationTrace is { } resolverTrace)
            {
                RecordImageActivationStage(
                    resolverTrace,
                    MarkdownPerformanceEventSource.ImageActivationStageDocumentScopeCall);
            }
#endif
            ValueTask<MarkdownImageResolution> operation =
                resolver.ResolveAsync(Url, resolveContext, token);
            Task<MarkdownImageResolution> task = operation.AsTask();
            resolverTask = task;
            return new ValueTask<MarkdownImageResolution>(task);
        }

        ValueTask<IMarkdownImageResolutionLease> StartLeasedResolver(CancellationToken token)
        {
            var scope = (IMarkdownPerformanceDocumentScope)resolver;
            ValueTask<IMarkdownImageResolutionLease> operation;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (activationTrace is { } resolverTrace)
            {
                RecordImageActivationStage(
                    resolverTrace,
                    MarkdownPerformanceEventSource.ImageActivationStageDocumentScopeCall);
                operation = scope.ResolveWithSourceLeaseAndActivationTraceAsync(
                    Url, resolveContext, resolverTrace, token);
            }
            else
#endif
            {
                operation = scope.ResolveWithSourceLeaseAsync(Url, resolveContext, token);
            }
            Task<IMarkdownImageResolutionLease> task = operation.AsTask();
            resolverTask = task;
            return new ValueTask<IMarkdownImageResolutionLease>(task);
        }

        try
        {
            if (resolver is IMarkdownPerformanceDocumentScope)
            {
                sourceLease = await ImageResolverDeadline.RunAsync(
                        StartLeasedResolver,
                        ImageResolverTimeout,
                        lifetimeToken,
                        abandonedResultDisposer: static lease => lease.Dispose(),
                        abandonedCompletionObserver: cleanup =>
                            TrackDetachedImageWork(cleanup, failRetirementOnFault: true))
                    .ConfigureAwait(false);
                resolution = sourceLease.Resolution;
            }
            else
            {
                resolution = await ImageResolverDeadline.RunAsync(
                    StartResolver,
                    ImageResolverTimeout,
                    lifetimeToken)
                .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
                _loadStarted = false;
            return;
        }
        catch (TimeoutException)
        {
            MarkdownDiagnostics.WriteLine("[ImageBox] image resolver timed out.");
            ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
            PublishFailure(cacheKey: string.Empty);
            return;
        }
        catch (Exception ex)
        {
            if (sourceLease is not null)
                TryImageCleanup(sourceLease.Dispose);
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] image resolver failed: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            PublishFailure(cacheKey: string.Empty);
            return;
        }
        finally
        {
            // Register even if completion raced the resolver deadline: the
            // helper observes a completed fault and otherwise adds a lease for
            // an operation that is still running. The current resolver lease
            // remains active until after this finally block.
            if (resolverTask is not null)
                TrackDetachedImageWork(resolverTask);
            // A completed resolver task stores its source-bearing result.
            // Detached tracking owns only work still draining; this caller no
            // longer needs the task alias once resolution has returned.
            resolverTask = null;
        }

        // Keep original source ownership through preparation and actual Open,
        // then release it before source-free Render. Fetch slots are not bytes.
        IMarkdownImageResolutionLease? consumerSourceLease = sourceLease;
        try
        {
            if (_disposed)
                return;

            if (MarkdownImageCacheIdentityPolicy.CanUseSourceAfterResolution(resolution))
            {
                if (_builtInSource is not { } fallbackSource)
                {
                    PublishFailure(cacheKey: string.Empty);
                    return;
                }

                _activeCacheKey = fallbackSource.CacheKey;

                // Only sources the built-in loader is allowed to expose can consult
                // its process-wide caches. This prevents a NotHandled HTTPS result
                // from observing bytes cached by an earlier resolver-owned request.
                if (fallbackSource.CanLoadWithoutResolver &&
                    TryLoadFallbackFromProcessCache(fallbackSource.CacheKey, bitmapOutputWork))
                {
                    return;
                }

                LoadFromDefaultSource(fallbackSource, demand, loadDemandGeneration);
                return;
            }

            asset = resolution.Asset;
            if (asset is null || asset.Bytes.Length == 0)
            {
                ReportUnavailable(resolution.UnavailableReason);
                PublishFailure(cacheKey: string.Empty);
                return;
            }

            bool isSvg = LooksLikeSvg(asset);
            // Replace the constructor's source-URI hint as soon as the resolver has
            // supplied the actual representation. This also covers warm raster-cache
            // hits, which return before the decode path below.
            _isSvg = isSvg;
            // Both SVG and raster cache reuse require the resolver's explicit
            // security-partitioned identity. ResolvedUri is metadata, not an
            // account boundary; an unkeyed asset remains local to this box.
            string cacheKey = MarkdownImageCacheIdentityPolicy.GetResolvedAssetKey(asset);
            _activeCacheKey = cacheKey;
            if (cacheKey.Length > 0 && _failedUrls.ContainsKey(cacheKey))
            {
                PublishFailure(cacheKey: string.Empty);
                return;
            }

            if (!isSvg && TryPublishCachedRasterBitmap(cacheKey, bitmapOutputWork))
            {
                return;
            }

            if (isSvg)
            {
                SvgOpenStageResult result = await OpenSvgSourceAsync(
                        asset.Bytes, intrinsicHint: default, securityPartitionKey: cacheKey,
                        builtInSourceAliasKey: null, demand, loadDemandGeneration)
                    .ConfigureAwait(false);
                using SvgOpenedRenderWork? openedWork = result.Work;
                // Clear source aliases before invoking user cleanup. A synchronous
                // helper detaches and disposes the lease without leaving a disposed
                // lease (whose Resolution may still own bytes) in this async frame.
                asset = null;
                resolution = default;
                sourceLease = null;
                DisposeCallerSource(ref consumerSourceLease);
                if (result.Deferral is { } hostDeferral)
                    DeferSvgHostLoad(hostDeferral);
                else if (openedWork is not null)
                    await RenderOpenedSvgAsync(openedWork).ConfigureAwait(false);
                return;
            }

            // A resolver can follow redirects to a representation whose format differs
            // from the source URI suffix (for example, an *.svg URL that resolves to a
            // transparent PNG). Once bytes have been resolved, their representation is
            // authoritative and the pre-resolution URL hint must not leak into layout,
            // accessibility, or subsequent cache decisions.
            await LoadBitmapBytesAsync(asset.Bytes, cacheKey, bitmapOutputWork).ConfigureAwait(false);
        }
        finally
        {
            asset = null;
            resolution = default;
            sourceLease = null;
            DisposeCallerSource(ref consumerSourceLease);
        }
    }

    private float GetContentX()
    {
        float left = (float)(Bounds.X + Margin.Left);
        float available = Math.Max(0, (float)Bounds.Width - (float)(Margin.Left + Margin.Right));
        return ContentAlignment switch
        {
            CanvasHorizontalAlignment.Center => left + Math.Max(0, (available - _imageWidth) / 2f),
            CanvasHorizontalAlignment.Right => left + Math.Max(0, available - _imageWidth),
            _ => left,
        };
    }

    private float GetContentY()
        => (float)(Bounds.Y + Margin.Top) +
           Math.Max(0f, (GetParagraphLineHeight() - _imageHeight) / 2f);

    private float GetParagraphLineHeight()
        => float.IsFinite(MinimumParagraphLineHeight) &&
           MinimumParagraphLineHeight is > 0f and <= 1_000_000f
            ? MinimumParagraphLineHeight
            : 0f;

    private void ReportUnavailable(
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason = null)
    {
        if (!_disposed && !_resourcesSuspended && reason != MarkdownImageUnavailableReason.None)
            Context.ImageUnavailable?.Invoke(Url, reason, svgFailureReason);
    }

    private bool TryPublishCachedRasterBitmap(
        string cacheKey, BitmapOutputWorkContext? expectedWork = null)
    {
        if (!TryAcquireSourceSizedRasterBitmap(cacheKey, out SharedCanvasBitmapCache.Lease? cachedLease, expectedWork) ||
            cachedLease is null)
        {
            return false;
        }

        PublishOnUiThread(() =>
        {
            if (_disposed)
            {
                TryImageCleanup(cachedLease.Dispose);
                return;
            }

            ReplaceBitmap(cachedLease.Bitmap, cachedLease);
            RaiseLoadCompleted(layoutInvalidated: true);
        }, cachedLease.Dispose);
        return true;
    }

    private bool TryAcquireSourceSizedRasterBitmap(
        string cacheKey, out SharedCanvasBitmapCache.Lease? lease,
        BitmapOutputWorkContext? expectedWork = null)
    {
        MarkdownPerformanceOptions? options = Context.PerformanceSession?.Options;
        lease = null;
        // A source-sized entry may have been created by an eager consumer.
        // Its cache identity grants reuse of the source, not permission to
        // bypass this consumer's physical-size planning or output ceiling.
        // Exact display/static-preview variants remain reusable in the decode
        // path, without starting a decoder or taking a preparation slot.
        if (options?.UseDisplaySizedRasterDecode == true ||
            !SharedCanvasBitmapCache.TryAcquire(Context.ResourceCreator.Device, cacheKey, out lease))
        {
            return false;
        }

        bool withinPixelBudget = options is null || lease!.IsWithinPixelBudget(
            options.ReleaseOffscreenImageResources
                ? Math.Min(options.MaxRasterOutputPixels, BitmapOutputAdmission.Shared.MaximumSingleOutputBytes / 4)
                : options.MaxRasterOutputPixels);
        if (withinPixelBudget &&
            (options is null || TryAdmitCachedBitmapOutput(lease!, expectedWork: expectedWork)))
            return true;

        if (!withinPixelBudget)
            RequestOversizedCachedBitmapTrimIfNeeded(lease!);

        TryImageCleanup(lease!.Dispose);
        lease = null;
        return false;
    }

    private bool TryLoadFallbackFromProcessCache(
        string cacheKey, BitmapOutputWorkContext? expectedWork = null)
    {
        if (_failedUrls.ContainsKey(cacheKey))
        {
            PublishFailure(cacheKey: string.Empty);
            return true;
        }

        if (!_isSvg && TryPublishCachedRasterBitmap(cacheKey, expectedWork))
        {
            return true;
        }

        if (!_svgCache.TryGetValue(cacheKey, out SvgCacheEntry? entry))
        {
            return false;
        }

        _isSvg = true;
        // Classification selects the authorized built-in loader on a miss;
        // accepted dimensions/title/description remain untouched until an
        // exact cached bitmap publication commits, just like content followers.
        return IsSvgCacheEntryCurrent(entry) && TryPublishCachedSvgBitmap(
            layoutInvalidated: true,
            sourceEntry: entry,
            resolvedCacheKey: cacheKey,
            expectedWork: expectedWork);
    }

    private static bool LooksLikeSvg(MarkdownImageAsset asset)
    {
        // Resolved payload metadata supersedes the spelling of the requested URI.
        // Redirecting image services commonly retain an .svg source URL while
        // returning a raster placeholder. Treating ResolvedUri as a format signal
        // in that case feeds PNG/JPEG bytes to the SVG provider and produces a
        // misleading unavailable state.
        if (LooksLikeSvgBytes(asset.Bytes))
            return true;

        string? contentType = asset.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        ReadOnlySpan<char> mediaType = contentType.AsSpan();
        int parameterSeparator = mediaType.IndexOf(';');
        if (parameterSeparator >= 0)
            mediaType = mediaType[..parameterSeparator];
        mediaType = mediaType.Trim();

        return mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("image/svg", StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadBuiltInImageAsync(
        MarkdownBuiltInImageSource source,
        bool isSvg,
        MarkdownSvgDemandTicket demand,
        int loadDemandGeneration)
    {
        if (!TryBeginImageWork(out ImageWorkLease? admittedWork) || admittedWork is null)
            return;

        using ImageWorkLease imageWork = admittedWork;
        string cacheKey = source.CacheKey;
        BitmapOutputWorkContext bitmapOutputWork = CaptureBitmapOutputWorkContext(
            imageWork.Token, expectedDemand: demand);
        try
        {
            (byte[] bytes, IDisposable? reservation) = await Task.Run(() => ReadBuiltInSourceBytesAsync(
                    source,
                    isSvg ? MaxSvgBytes : MaxRemoteImageBytes,
                    isSvg ? "SVG" : "raster image",
                    imageWork.Token), CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                if (isSvg)
                {
                    SvgOpenStageResult result = await OpenSvgSourceAsync(
                            bytes, intrinsicHint: default, securityPartitionKey: cacheKey,
                            builtInSourceAliasKey: cacheKey, demand, loadDemandGeneration)
                        .ConfigureAwait(false);
                    using SvgOpenedRenderWork? openedWork = result.Work;
                    bytes = Array.Empty<byte>();
                    source = default;
                    DisposeCallerSource(ref reservation);
                    if (result.Deferral is { } hostDeferral)
                        DeferSvgHostLoad(hostDeferral);
                    else if (openedWork is not null)
                        await RenderOpenedSvgAsync(openedWork).ConfigureAwait(false);
                }
                else
                {
                    await LoadBitmapBytesAsync(bytes, source.CacheKey, bitmapOutputWork).ConfigureAwait(false);
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
            if (!_disposed)
                _loadStarted = false;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] built-in source read failed: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
            PublishFailure(cacheKey);
        }
    }

    private Task LoadBitmapBytesAsync(
        byte[] bytes, string cacheKey, BitmapOutputWorkContext bitmapOutputWork)
    {
        // A session source-cache hit can complete synchronously inside the
        // viewport's EnsureLoading call. Keep header validation, preview-cache
        // search, WIC decode, and GPU upload off that UI callback even then.
        return Context.Dispatcher?.HasThreadAccess == true
            ? Task.Run(() => LoadBitmapBytesCoreAsync(bytes, cacheKey, bitmapOutputWork), CancellationToken.None)
            : LoadBitmapBytesCoreAsync(bytes, cacheKey, bitmapOutputWork);
    }

    private async Task LoadBitmapBytesCoreAsync(
        byte[] bytes, string cacheKey, BitmapOutputWorkContext bitmapOutputWork)
    {
        if (!TryBeginImageWork(out ImageWorkLease? admittedWork) || admittedWork is null)
            return;
        using ImageWorkLease imageWork = admittedWork;
        CancellationToken imageCancellationToken = imageWork.Token;
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(bytes);
        if (!budget.Accepted && !budget.CanRenderStaticPreview)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] raster rejected before CanvasBitmap decode for {cacheKey}: {budget.Reason}");
            ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
            PublishFailure(cacheKey);
            return;
        }

        MarkdownPerformanceOptions? rasterOptions = Context.PerformanceSession?.Options;
        bool boundedBitmapOutput = rasterOptions?.ReleaseOffscreenImageResources == true;
        bool displaySized = rasterOptions?.UseDisplaySizedRasterDecode == true;
        bool sessionPixelCapApplies = Context.PerformanceSession is { } performanceSession &&
            (long)budget.Width * budget.Height > performanceSession.Options.MaxRasterOutputPixels;
        bool composeGifCanvas = budget.FirstFrame is { } gifFrame &&
            (gifFrame.Left != 0 || gifFrame.Top != 0 ||
             gifFrame.Width != budget.Width || gifFrame.Height != budget.Height);
        bool sizingPolicyApplies = displaySized || budget.CanRenderStaticPreview || sessionPixelCapApplies;
        // Participating consumers always use WIC's explicit bounded transform.
        // The shared budget reserves both the GIF canvas and its temporary
        // frame; legacy consumers retain their existing eager decode behavior.
        bool transformedDecode = sizingPolicyApplies || composeGifCanvas || boundedBitmapOutput;
        long maximumOutputPixels = Math.Min(RasterImageResourceBudget.MaxPixelsPerFrame,
            rasterOptions?.MaxRasterOutputPixels ?? RasterImageResourceBudget.MaxPixelsPerFrame);
        if (boundedBitmapOutput)
            maximumOutputPixels = Math.Min(maximumOutputPixels,
                BitmapOutputAdmission.Shared.MaximumSingleOutputBytes / (composeGifCanvas ? 8 : 4));
        var sizingRequest = new RasterImageSizingRequest(
            sizingPolicyApplies ? _availableWidth - Margin.Left - Margin.Right : budget.Width,
            sizingPolicyApplies ? Context.RasterizationScale : 1d,
            sizingPolicyApplies ? _requestedWidth : null,
            sizingPolicyApplies ? _requestedHeight : null,
            maximumOutputPixels);
        (int Width, int Height) rasterSize = transformedDecode
            ? sizingRequest.GetSize(budget.Width, budget.Height)
            : default;
        RasterImageDecodePlan nativePlan = default;
        bool nativeDecode = transformedDecode &&
            RasterImageDecodePlan.TryCreate(bytes, budget, sizingRequest, out nativePlan, imageCancellationToken) &&
            RasterNativeWicDecoder.IsQualifiedPlan(nativePlan);
        if (nativeDecode)
            rasterSize = (nativePlan.OutputWidth, nativePlan.OutputHeight);
        RasterGifFirstFrameDecodePlan nativeGifPlan = default;
        bool nativeGifDecode = transformedDecode && budget.Format == "GIF" &&
            RasterGifFirstFrameDecodePlan.TryCreate(bytes, budget, sizingRequest,
                out nativeGifPlan, imageCancellationToken);
        if (nativeGifDecode)
            rasterSize = (nativeGifPlan.OutputWidth, nativeGifPlan.OutputHeight);
        // A partial GIF is the full logical canvas, not the WIC first-frame
        // bitmap. Partition it before the sizing grammar so old raw entries
        // and preview candidates cannot substitute incompatible geometry.
        string rasterSourceIdentity = composeGifCanvas && cacheKey.Length > 0
            ? cacheKey + "\u001Fgif-canvas-v1" : cacheKey;
        // Owned WIC variants use final orientation, unlike older provisional
        // raw-axis variants. Never substitute those cache entries on promotion.
        if (nativeDecode && rasterSourceIdentity.Length > 0)
            rasterSourceIdentity += RasterNativeWicDecoder.CachePartition;
        if (nativeGifDecode && rasterSourceIdentity.Length > 0)
            rasterSourceIdentity += RasterNativeWicDecoder.GifCachePartition;
        string cacheFamily = transformedDecode && cacheKey.Length > 0
            ? sizingRequest.GetCacheFamily(rasterSourceIdentity, displaySized) : cacheKey;
        string bitmapCacheKey = transformedDecode && cacheKey.Length > 0
            ? sizingRequest.GetCacheKey(cacheFamily, rasterSize)
            : cacheKey;

        CanvasBitmap? ownedBitmap = null;
        BitmapOutputAdmission.Reservation? bitmapReservation = null;
        SharedCanvasBitmapCache.Lease? bitmapLease = null;
        IDisposable? processDecodeSlot = null;
        WicDecodeOperationOwner? wicDecodeOwner = null;
        IDisposable? preparationSlot = null;
        IDisposable? rasterWork = null;
        IMarkdownPerformanceSessionInternal? rasterSession = Context.PerformanceSession;
        Size intrinsicSize = new(budget.Width, budget.Height);
        if (nativeDecode)
            intrinsicSize = new Size(nativePlan.IntrinsicWidth, nativePlan.IntrinsicHeight);
        SemaphoreSlim? decodeGate = null;
        bool decodeGateHeld = false;
        bool failed = false;
        bool deviceLost = false;
        try
        {
            imageCancellationToken.ThrowIfCancellationRequested();
            CanvasDevice device = Context.ResourceCreator.Device;
            if (!string.IsNullOrEmpty(bitmapCacheKey) &&
                TryAcquireRasterBitmap(device, bitmapCacheKey, sizingRequest, transformedDecode, out bitmapLease,
                    bitmapOutputWork))
            {
                // Warm cache hit. No stream, decoder, or GPU allocation.
            }
            else
            {
                rasterWork = rasterSession?.TryBeginRasterWork();
                if (rasterSession is not null && rasterWork is null)
                    throw new OperationCanceledException(rasterSession.LifetimeToken);
                using CancellationTokenSource? decodeCancellation = rasterSession is null ? null :
                    CancellationTokenSource.CreateLinkedTokenSource(imageCancellationToken, rasterSession.LifetimeToken);
                CancellationToken decodeToken = decodeCancellation?.Token ?? imageCancellationToken;
                decodeToken.ThrowIfCancellationRequested();
                // A resize or DPI transition may need a larger raster than an
                // image already uploaded for this source. Keep that cached
                // bitmap visible while the exact replacement is decoded. An
                // exact cache hit above never incurs this variant search.
                if (displaySized &&
                    rasterOptions?.UseCachedRasterPreview == true &&
                    cacheKey.Length > 0 &&
                    (long)rasterSize.Width * rasterSize.Height >= 65_536 &&
                    SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                        device,
                        cacheFamily,
                        sizingRequest,
                        out SharedCanvasBitmapCache.Lease? previewLease) &&
                    previewLease is not null)
                {
                    SharedCanvasBitmapCache.Lease cachedPreview = previewLease;
                    if (!TryAdmitCachedBitmapOutput(cachedPreview, expectedWork: bitmapOutputWork))
                    {
                        TryImageCleanup(cachedPreview.Dispose);
                        if (IsBitmapOutputDeferred)
                            return;
                    }
                    else
                    {
                        PublishOnUiThread(() =>
                        {
                            if (_bitmap is not null)
                            {
                                TryImageCleanup(cachedPreview.Dispose);
                                return;
                            }

                            Size previewIntrinsic = cachedPreview.IntrinsicSize!.Value;
                            _rasterIntrinsicSize = previewIntrinsic;
                            ReplaceBitmap(cachedPreview.Bitmap, cachedPreview);
                            RaiseLoadCompleted(layoutInvalidated: true);
                        }, cachedPreview.Dispose);
                    }
                }

                if (!string.IsNullOrEmpty(bitmapCacheKey))
                {
                    decodeGate = SharedCanvasBitmapCache.GetDecodeGate(device, bitmapCacheKey);
                    await decodeGate.WaitAsync(decodeToken).ConfigureAwait(false);
                    decodeGateHeld = true;

                    // Another request may have populated the cache while this
                    // request waited on the fixed-size keyed decode gate.
                    _ = TryAcquireRasterBitmap(device, bitmapCacheKey, sizingRequest, transformedDecode, out bitmapLease,
                        bitmapOutputWork);
                }

                if (bitmapLease is null)
                {
                    if (IsBitmapOutputDeferred)
                        return;
                    long decodeStarted = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
                        ? Stopwatch.GetTimestamp()
                        : 0;
                    long rasterPreparationId = rasterSession is { IsDisposed: false }
                        ? rasterSession.BeginRasterPreparation(bytes.Length, budget.Width, budget.Height)
                        : 0;
                    long admissionStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                    preparationSlot = rasterSession is { IsDisposed: false } &&
                        Context.EffectiveImagePreparationOwner is { } documentOwner
                        ? await rasterSession.EnterCpuPreparationAsync(
                                documentOwner,
                                exclusive: sessionPixelCapApplies || budget.CanRenderStaticPreview,
                                decodeToken)
                            .ConfigureAwait(false)
                        : null;
                    // Keyed deduplication and optional session limits are not
                    // aggregate codec limits. All ordinary-raster cache misses
                    // share this admission, including empty keys and controls
                    // with no performance session. Native decode cannot be
                    // canceled by this token: hold the lease through its await,
                    // bitmap upload, and output cleanup, never in a callback.
                    long sourcePixels = (long)budget.Width * budget.Height;
                    long outputPixels = nativeDecode ? nativePlan.OutputPixels : nativeGifDecode
                        ? nativeGifPlan.CanvasOutputPixels : transformedDecode
                        ? sizingRequest.GetMaximumOrientedOutputPixels(
                            budget.Width, budget.Height, maySwapAxes: budget.FirstFrame is null)
                        : sourcePixels;
                    // Owned-WIC weights include a conservative source-scratch
                    // allowance informed by oversized WebP measurements. This
                    // coordinates concurrent work; it is not a native peak cap.
                    long processAdmissionWeight = nativeDecode
                        ? RasterDecodeAdmissionEstimate.ForOwnedStaticWic(bytes.Length, nativePlan)
                        : nativeGifDecode
                            ? RasterDecodeAdmissionEstimate.ForOwnedGifWic(bytes.Length, nativeGifPlan)
                            : checked(
                                bytes.Length +
                                checked(sourcePixels * 4) +
                                checked(outputPixels * (composeGifCanvas ? 12 : 8)));
                    processDecodeSlot = await MarkdownImagePreparationAdmission.Shared.EnterAsync(
                        Context.EffectiveImagePreparationOwner ?? Context,
                        processAdmissionWeight,
                        exclusive: sessionPixelCapApplies || budget.CanRenderStaticPreview,
                        decodeToken).ConfigureAwait(false);
                    RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                        MarkdownRasterPreparationStage.PreparationAdmission, admissionStarted);
                    long stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                    if (nativeDecode)
                    {
                        // Admit exact GPU output before any codec factory. The
                        // process estimate also covers overlapping raw/oriented
                        // CPU buffers and remains held through final upload.
                        if (!TryReserveBitmapOutput(checked(nativePlan.OutputPixels * 4),
                                out bitmapReservation, expectedWork: bitmapOutputWork))
                            return;
                        IDisposable nativeCredit = processDecodeSlot!;
                        Task<RasterDecodedPixelLease> nativeTask = Task.Run(
                            () => RasterNativeWicDecoder.Decode(bytes, nativePlan, nativeCredit, decodeToken),
                            CancellationToken.None);
                        // Decode consumes credit on every invocation path. If
                        // scheduling itself throws, the caller still owns it.
                        processDecodeSlot = null;
                        using RasterDecodedPixelLease pixels = await nativeTask.ConfigureAwait(false);
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
                        decodeToken.ThrowIfCancellationRequested();
                        stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                        if (bitmapReservation is not null && !bitmapReservation.Commit())
                            throw new InvalidOperationException("The raster output reservation is no longer active.");
                        ownedBitmap = CanvasBitmap.CreateFromBytes(
                            Context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
                        // Pixel lease disposal clears the CPU buffer and refunds
                        // process credit only after upload; no BitmapDecoder RCW
                        // or codec graph remains alive on the success path.
                    }
                    else if (nativeGifDecode)
                    {
                        long frameBytes = checked(nativeGifPlan.FrameOutputPixels * 4);
                        long canvasBytes = checked(nativeGifPlan.CanvasOutputPixels * 4);
                        if (!TryReserveBitmapOutput(composeGifCanvas ? canvasBytes + frameBytes : frameBytes,
                                out bitmapReservation, expectedWork: bitmapOutputWork))
                            return;
                        // Reserve and split before the codec or either GPU
                        // plane exists. A failed frame disposal must retain its
                        // own charge without preventing healthy canvas cleanup.
                        BitmapOutputAdmission.Reservation? frameReservation = composeGifCanvas
                            ? bitmapReservation?.Split(frameBytes) : null;
                        CanvasBitmap? decodedFrame = null;
                        try
                        {
                            IDisposable nativeCredit = processDecodeSlot!;
                            Task<RasterDecodedPixelLease> nativeTask = Task.Run(
                                () => RasterNativeWicDecoder.DecodeGifFirstFrame(
                                    bytes, nativeGifPlan, nativeCredit, decodeToken),
                                CancellationToken.None);
                            processDecodeSlot = null;
                            using RasterDecodedPixelLease pixels = await nativeTask.ConfigureAwait(false);
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
                            decodeToken.ThrowIfCancellationRequested();
                            stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                            if (composeGifCanvas)
                            {
                                if (frameReservation is not null && !frameReservation.Commit())
                                    throw new InvalidOperationException("The GIF frame reservation is no longer active.");
                                decodedFrame = CanvasBitmap.CreateFromBytes(
                                    Context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                                if (bitmapReservation is not null && !bitmapReservation.Commit())
                                    throw new InvalidOperationException("The GIF canvas reservation is no longer active.");
                                ComposeGifCanvasBitmap(decodedFrame, budget.FirstFrame!.Value,
                                    budget.Width, budget.Height, rasterSize, ref ownedBitmap);
                            }
                            else
                            {
                                if (bitmapReservation is not null && !bitmapReservation.Commit())
                                    throw new InvalidOperationException("The GIF output reservation is no longer active.");
                                ownedBitmap = CanvasBitmap.CreateFromBytes(
                                    Context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                            }
                            decodeToken.ThrowIfCancellationRequested();
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
                        }
                        finally
                        {
                            ReleaseAdmittedBitmap(ref decodedFrame, ref frameReservation);
                        }
                    }
                    else
                    {
                        // Borrow the admitted source array rather than duplicating
                        // up to 32 MiB into an InMemoryRandomAccessStream. Keep both
                        // wrappers alive until the native decode/upload completes.
                        if (transformedDecode)
                        {
                            // Reserve durable custody before either WinRT factory.
                            // Awaiting a projected operation does not close it;
                            // failed terminal cleanup must retain its admission.
                            wicDecodeOwner = WicDecodeOperationOwner.Create();
                            wicDecodeOwner.AdoptProcessDecodeSlot(ref processDecodeSlot);
                        }
                        using MemoryStream? sourceStream = transformedDecode ? null : new(bytes, writable: false);
                        using IRandomAccessStream? directStream = sourceStream?.AsRandomAccessStream();
                        IRandomAccessStream stream = wicDecodeOwner?.OpenSourceStream(bytes) ?? directStream!;
                        decodeToken.ThrowIfCancellationRequested();
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.SourceStreamOpen, stageStarted);
                        if (transformedDecode)
                        {
                            stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                            BitmapDecoder decoder = await wicDecodeOwner!.CreateDecoderOperation(stream);
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.DecoderCreation, stageStarted);
                            decodeToken.ThrowIfCancellationRequested();
                            int firstFrameWidth = budget.FirstFrame?.Width ?? budget.Width;
                            int firstFrameHeight = budget.FirstFrame?.Height ?? budget.Height;
                            if (decoder.PixelWidth != firstFrameWidth || decoder.PixelHeight != firstFrameHeight)
                            {
                                throw new InvalidDataException("The decoded raster dimensions do not match its validated header.");
                            }

                            // GIF descriptors are subrectangles. Their frame axes
                            // validate WIC, while the logical screen owns aspect,
                            // offset and transparent padding in the document.
                            intrinsicSize = budget.FirstFrame is not null
                                ? new Size(budget.Width, budget.Height)
                                : new Size(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                            bool swapDecodeAxes = budget.FirstFrame is null &&
                                (decoder.OrientedPixelWidth != decoder.PixelWidth ||
                                 decoder.OrientedPixelHeight != decoder.PixelHeight);
                            if (swapDecodeAxes)
                            {
                                // WIC applies scaling before EXIF rotation. Plan the
                                // displayed dimensions against the oriented aspect,
                                // then swap the transform axes back to source space.
                                var orientedSize = sizingRequest.GetSize(
                                    checked((int)decoder.OrientedPixelWidth),
                                    checked((int)decoder.OrientedPixelHeight));
                                rasterSize = (orientedSize.Height, orientedSize.Width);
                            }
                            var transform = new BitmapTransform
                            {
                                ScaledWidth = checked((uint)(composeGifCanvas
                                    ? Math.Max(1, (int)Math.Ceiling(firstFrameWidth * (double)rasterSize.Width / budget.Width))
                                    : rasterSize.Width)),
                                ScaledHeight = checked((uint)(composeGifCanvas
                                    ? Math.Max(1, (int)Math.Ceiling(firstFrameHeight * (double)rasterSize.Height / budget.Height))
                                    : rasterSize.Height)),
                                InterpolationMode = BitmapInterpolationMode.Fant,
                            };
                            long canvasBytes = checked((long)rasterSize.Width * rasterSize.Height * 4);
                            long frameBytes = checked((long)transform.ScaledWidth * transform.ScaledHeight * 4);
                            if (!TryReserveBitmapOutput(composeGifCanvas ? canvasBytes + frameBytes : frameBytes,
                                    out bitmapReservation, expectedWork: bitmapOutputWork))
                                return;
                            // Split before either output is born. The temporary
                            // frame's charge survives independently if its native
                            // disposal fails; the canvas may still retire normally.
                            BitmapOutputAdmission.Reservation? frameReservation = composeGifCanvas
                                ? bitmapReservation?.Split(frameBytes) : null;
                            CanvasBitmap? decodedFrame = null;
                            try
                            {
                                stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                                SoftwareBitmap firstFrame = await wicDecodeOwner.CreateSoftwareBitmapOperation(
                                    decoder,
                                    BitmapPixelFormat.Bgra8,
                                    BitmapAlphaMode.Premultiplied,
                                    transform,
                                    ExifOrientationMode.RespectExifOrientation,
                                    ColorManagementMode.ColorManageToSRgb);
                                // The operation owner now retains the result until
                                // upload and real terminal cleanup have completed.
                                wicDecodeOwner.AdoptSoftwareBitmap(firstFrame);
                                RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                    MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
                                decodeToken.ThrowIfCancellationRequested();
                                uint expectedPixelWidth = swapDecodeAxes ? transform.ScaledHeight : transform.ScaledWidth;
                                uint expectedPixelHeight = swapDecodeAxes ? transform.ScaledWidth : transform.ScaledHeight;
                                if (firstFrame.PixelWidth != expectedPixelWidth ||
                                    firstFrame.PixelHeight != expectedPixelHeight ||
                                    checked((long)firstFrame.PixelWidth * firstFrame.PixelHeight) > sizingRequest.MaximumPixels)
                                {
                                    throw new InvalidDataException("The decoded frame does not match the bounded raster transform.");
                                }
                                stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                                if (composeGifCanvas)
                                {
                                    frameReservation?.Commit();
                                    decodedFrame = CanvasBitmap.CreateFromSoftwareBitmap(
                                        Context.ResourceCreator, firstFrame);
                                    bitmapReservation?.Commit();
                                    ComposeGifCanvasBitmap(decodedFrame, budget.FirstFrame!.Value,
                                        budget.Width, budget.Height, rasterSize, ref ownedBitmap);
                                    decodeToken.ThrowIfCancellationRequested();
                                }
                                else
                                {
                                    bitmapReservation?.Commit();
                                    ownedBitmap = CanvasBitmap.CreateFromSoftwareBitmap(Context.ResourceCreator, firstFrame);
                                }
                                RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                    MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
                                if (budget.CanRenderStaticPreview)
                                {
                                    MarkdownDiagnostics.WriteLine(
                                        $"[ImageBox] rendered a bounded first-frame preview for {cacheKey}; " +
                                        $"source={budget.Width}x{budget.Height}, " +
                                        $"preview={rasterSize.Width}x{rasterSize.Height}, frames={budget.FrameCount}.");
                                }
                            }
                            finally
                            {
                                ReleaseAdmittedBitmap(ref decodedFrame, ref frameReservation);
                            }
                        }
                        else
                        {
                            stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                            ownedBitmap = await CanvasBitmap.LoadAsync(Context.ResourceCreator, stream);
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.Win2DDirectLoad, stageStarted);
                        }
                    }

                    decodeToken.ThrowIfCancellationRequested();
                    var outputSize = ownedBitmap.SizeInPixels;
                    if (outputSize.Width == 0 || outputSize.Height == 0 ||
                        outputSize.Width > RasterImageResourceBudget.MaxDimension ||
                        outputSize.Height > RasterImageResourceBudget.MaxDimension ||
                        checked((long)outputSize.Width * outputSize.Height) > sizingRequest.MaximumPixels)
                    {
                        throw new InvalidDataException("The decoded raster output exceeds the consumer's pixel budget.");
                    }
                    if (!string.IsNullOrEmpty(bitmapCacheKey))
                    {
                        stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                        bitmapLease = SharedCanvasBitmapCache.StoreAndAcquire(
                            device, bitmapCacheKey, ownedBitmap,
                            transformedDecode ? intrinsicSize : ownedBitmap.Size,
                            bitmapReservation: bitmapReservation);
                        ownedBitmap = null; // lease now owns the decoded handle
                        bitmapReservation = null;
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.CachePublication, stageStarted);
                    }
                    else
                    {
                        // Unkeyed payloads acquire lifetime accounting on the
                        // preparation worker too. Publication only swaps the
                        // completed lease, without querying native dimensions.
                        bitmapLease = SharedCanvasBitmapCache.CreateUncachedLease(
                            ownedBitmap, checked((int)outputSize.Width), checked((int)outputSize.Height),
                            transformedDecode ? intrinsicSize : ownedBitmap.Size,
                            bitmapReservation: bitmapReservation);
                        ownedBitmap = null;
                        bitmapReservation = null;
                    }
                    if (decodeStarted != 0)
                    {
                        MarkdownPerformanceEventSource.Log.ResourceWork(
                            2,
                            Stopwatch.GetTimestamp() - decodeStarted,
                            (long)(transformedDecode ? rasterSize.Width : budget.Width) *
                                (transformedDecode ? rasterSize.Height : budget.Height));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            ReleasePendingRasterOutputs(this, ref bitmapLease, ref ownedBitmap);
            if (!_disposed)
                _loadStarted = false;
            return;
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] resolved bitmap load deferred after graphics device loss for {cacheKey}: {ex.Message}");
            ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            ReleasePendingRasterOutputs(this, ref bitmapLease, ref ownedBitmap);
            deviceLost = true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[ImageBox] resolved bitmap load failed for {cacheKey}: {ex.Message}");
            ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            ReleasePendingRasterOutputs(this, ref bitmapLease, ref ownedBitmap);
            failed = true;
        }
        finally
        {
            bytes = Array.Empty<byte>();
            ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            if (wicDecodeOwner is not null)
                TryImageCleanup(wicDecodeOwner.Retire);
            if (processDecodeSlot is not null)
                TryImageCleanup(processDecodeSlot.Dispose);
            if (decodeGateHeld)
                TryImageCleanup(() => decodeGate!.Release());
            if (preparationSlot is not null)
                TryImageCleanup(preparationSlot.Dispose);
            if (rasterWork is not null)
                TryImageCleanup(rasterWork.Dispose);
        }

        SharedCanvasBitmapCache.Lease? publishedLease = bitmapLease;
        CanvasBitmap? publishedOwnedBitmap = ownedBitmap;
        if (transformedDecode && publishedLease?.IntrinsicSize is { } cachedIntrinsicSize)
            intrinsicSize = cachedIntrinsicSize;

        PublishOnUiThread(() =>
        {
            if (_disposed)
            {
                if (publishedLease is { } retiredLease)
                    TryImageCleanup(retiredLease.Dispose);
                if (publishedOwnedBitmap is { } retiredBitmap)
                    TryImageCleanup(retiredBitmap.Dispose);
                return;
            }

            bool exactReservedGeometry = transformedDecode && !failed && !deviceLost &&
                publishedLease is not null &&
                Context.PerformanceSession is { IsDisposed: false } session &&
                session.Options.PrefetchDocumentImages &&
                session.Options.UseDisplaySizedRasterDecode &&
                HasExactReservedRasterGeometry(intrinsicSize);
            bool layoutChanged = (_bitmap is null && !exactReservedGeometry) ||
                (transformedDecode &&
                 !exactReservedGeometry &&
                 (_rasterIntrinsicSize.Width != intrinsicSize.Width ||
                  _rasterIntrinsicSize.Height != intrinsicSize.Height));
            if (failed && _bitmap is null)
            {
                _loadFailed = true;
                RememberFailedUrl(cacheKey);
            }
            else if (deviceLost)
            {
                _loadStarted = false;
            }
            else if (publishedLease is not null)
            {
                if (transformedDecode)
                    _rasterIntrinsicSize = intrinsicSize;
                ReplaceBitmap(publishedLease.Bitmap, publishedLease);
            }

            RaiseLoadCompleted(layoutInvalidated: layoutChanged);
        },
        onDropped: () =>
        {
            if (publishedLease is { } droppedLease)
                TryImageCleanup(droppedLease.Dispose);
            if (publishedOwnedBitmap is { } droppedBitmap)
                TryImageCleanup(droppedBitmap.Dispose);
        });
    }

    private void ComposeGifCanvasBitmap(
        CanvasBitmap decodedFrame, (int Left, int Top, int Width, int Height) frameRect,
        int canvasWidth, int canvasHeight, (int Width, int Height) rasterSize,
        [System.Diagnostics.CodeAnalysis.NotNull] ref CanvasBitmap? ownedCanvas)
    {
        // Destination edges stay fractional: rounding a small frame up to
        // whole pixels incorrectly fills its padding at low resolutions.
        var destination = new Rect(
            frameRect.Left * (double)rasterSize.Width / canvasWidth,
            frameRect.Top * (double)rasterSize.Height / canvasHeight,
            frameRect.Width * (double)rasterSize.Width / canvasWidth,
            frameRect.Height * (double)rasterSize.Height / canvasHeight);
        // Publish ownership to the caller before drawing, so its established
        // failure/retirement path also owns a canvas whose draw call fails.
        ownedCanvas = new CanvasRenderTarget(Context.ResourceCreator, rasterSize.Width, rasterSize.Height, 96);
        using CanvasDrawingSession drawing = ((CanvasRenderTarget)ownedCanvas).CreateDrawingSession();
        drawing.Clear(Color.FromArgb(0, 0, 0, 0));
        drawing.DrawImage(decodedFrame, destination,
            new Rect(0, 0, decodedFrame.Size.Width, decodedFrame.Size.Height),
            1f, CanvasImageInterpolation.Linear);
    }

    private bool TryAcquireRasterBitmap(
        CanvasDevice device,
        string key,
        RasterImageSizingRequest sizingRequest,
        bool transformed,
        out SharedCanvasBitmapCache.Lease? lease,
        BitmapOutputWorkContext? expectedWork = null)
    {
        if (!SharedCanvasBitmapCache.TryAcquire(device, key, out lease))
            return false;
        bool withinPixelBudget = lease!.IsWithinPixelBudget(sizingRequest.MaximumPixels);
        bool matchesSizingRequest = withinPixelBudget &&
            (!transformed || lease.MatchesSizingRequest(sizingRequest));
        if (withinPixelBudget && matchesSizingRequest &&
            TryAdmitCachedBitmapOutput(lease, expectedWork: expectedWork))
            return true;

        RequestOversizedCachedBitmapTrimIfNeeded(lease);

        TryImageCleanup(lease.Dispose);
        lease = null;
        return false;
    }

    private async Task<(byte[] Bytes, IDisposable? Reservation)> ReadBuiltInSourceBytesAsync(
        MarkdownBuiltInImageSource source,
        int maxBytes,
        string assetKind,
        CancellationToken cancellationToken)
    {
        if (!source.CanLoadWithoutResolver)
            throw new InvalidOperationException("The source requires a host image resolver.");

        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind == MarkdownBuiltInImageSourceKind.Data)
        {
            // Measure without a byte buffer, then admit before decoding. This
            // whole method runs off the UI thread, including local opens.
            return await ReadDataUriSourceBytesAsync(
                source.Uri.OriginalString, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        if (source.Kind == MarkdownBuiltInImageSourceKind.Local && source.Uri.IsFile)
        {
            await using FileStream stream = new(
                source.Uri.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length == 0 || stream.Length > maxBytes)
            {
                throw new InvalidDataException(
                    $"The local {assetKind} exceeds the compressed input budget.");
            }

            int fileLength = checked((int)stream.Length);
            IDisposable? reservation = await ReserveBuiltInSourceBytesAsync(fileLength, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                byte[] bytes = new byte[fileLength];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return (bytes, reservation);
            }
            catch
            {
                reservation?.Dispose();
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using IRandomAccessStreamWithContentType randomAccessStream = await RandomAccessStreamReference
            .CreateFromUri(source.Uri)
            .OpenReadAsync()
            .AsTask()
            .ConfigureAwait(false);
        // Do not cancel the WinRT bridge task and dispose its returned stream
        // while the underlying open is still completing. The enclosing image
        // work lease keeps snapshot retirement pending until this native
        // operation has actually returned.
        cancellationToken.ThrowIfCancellationRequested();
        if (randomAccessStream.Size == 0 || randomAccessStream.Size > (ulong)maxBytes)
        {
            throw new InvalidDataException(
                $"The local {assetKind} exceeds the compressed input budget.");
        }

        int byteLength = checked((int)randomAccessStream.Size);
        // DataReader keeps a native input buffer alongside the managed array.
        // Cover both before either is materialized; do not cancel and dispose
        // the native operation until it has really returned.
        IDisposable? nativeReservation = await ReserveBuiltInSourceBytesAsync(
            checked(byteLength * 2L), cancellationToken).ConfigureAwait(false);
        try
        {
            uint requestedLength = (uint)byteLength;
            byte[] result = new byte[byteLength];
            using DataReader reader = new(randomAccessStream.GetInputStreamAt(0));
            cancellationToken.ThrowIfCancellationRequested();
            uint loaded = await reader.LoadAsync(requestedLength)
                .AsTask()
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded != requestedLength)
                throw new EndOfStreamException($"The local {assetKind} ended before its declared size.");

            reader.ReadBytes(result);
            return (result, nativeReservation);
        }
        catch
        {
            nativeReservation?.Dispose();
            throw;
        }
    }

    private async ValueTask<IDisposable?> ReserveBuiltInSourceBytesAsync(
        long bytes, CancellationToken cancellationToken) =>
        Context.PerformanceSession is { } session
            ? await session.ReserveImageSourceBytesAsync(
                Context.EffectiveImagePreparationOwner ?? this, bytes, cancellationToken).ConfigureAwait(false)
            : null;

    private async Task<(byte[] Bytes, IDisposable? Reservation)> ReadDataUriSourceBytesAsync(
        string source, int maxBytes, CancellationToken cancellationToken)
    {
        int length = MarkdownDataUriDecoder.GetDecodedLength(source, maxBytes, cancellationToken);
        IDisposable? reservation = await ReserveBuiltInSourceBytesAsync(length, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return (MarkdownDataUriDecoder.Decode(source, maxBytes, cancellationToken), reservation);
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }

    private static bool LooksLikeSvgBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return false;

        int index = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            index = 3;

        while (index < bytes.Length && char.IsWhiteSpace((char)bytes[index]))
            index++;

        int length = Math.Min(bytes.Length - index, 512);
        if (length <= 0)
            return false;

        string prefix = System.Text.Encoding.UTF8.GetString(bytes, index, length);
        return prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
               (prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                prefix.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private async Task LoadSvgDataUriAsync(
        string rawDataUri,
        MarkdownSvgDemandTicket demand,
        int loadDemandGeneration)
    {
        if (!TryBeginImageWork(out ImageWorkLease? decodeWork) || decodeWork is null)
            return;

        using ImageWorkLease imageWork = decodeWork;
        byte[] rawBytes;
        IDisposable? reservation = null;
        try
        {
            (rawBytes, reservation) = await Task.Run(
                    () => ReadDataUriSourceBytesAsync(rawDataUri, MaxSvgBytes, imageWork.Token),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
                _loadStarted = false;
            return;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG data URI decode failed: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            PublishOnUiThread(() =>
            {
                if (_disposed) return;
                _loadFailed = true;
                string cacheKey = _activeCacheKey ?? Url;
                RememberFailedUrl(cacheKey);
                RaiseLoadCompleted(layoutInvalidated: true);
            });
            return;
        }

        try
        {
            rawDataUri = string.Empty;
            SvgOpenStageResult result = await OpenSvgSourceAsync(
                    rawBytes, intrinsicHint: default, securityPartitionKey: _activeCacheKey ?? Url,
                    builtInSourceAliasKey: _activeCacheKey ?? Url, demand, loadDemandGeneration)
                .ConfigureAwait(false);
            using SvgOpenedRenderWork? openedWork = result.Work;
            rawBytes = Array.Empty<byte>();
            DisposeCallerSource(ref reservation);
            if (result.Deferral is { } hostDeferral)
                DeferSvgHostLoad(hostDeferral);
            else if (openedWork is not null)
                await RenderOpenedSvgAsync(openedWork).ConfigureAwait(false);
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

    /// <summary>
    /// Prepares and opens an admitted SVG off the UI thread. A successful result
    /// owns a source-free render handoff; callers release original source
    /// reservations before consuming it.
    /// </summary>
    private async Task<SvgOpenStageResult> OpenSvgSourceAsync(
        byte[] rawBytes,
        Size intrinsicHint,
        string? securityPartitionKey,
        string? builtInSourceAliasKey,
        MarkdownSvgDemandTicket demand,
        int loadDemandGeneration)
    {
        if (!TryBeginImageWork(out ImageWorkLease? renderWork) || renderWork is null)
            return default;

        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            long svgWorkId = MarkdownSvgStageTrace.BeginWork();
            long dispatchStartedAt = svgWorkId != 0 &&
                MarkdownPerformanceEventSource.Log.IsSvgWorkMeasurementEnabled()
                    ? Stopwatch.GetTimestamp()
                    : 0;
#endif
            async Task<SvgOpenStageResult> RunCoreAsync(byte[] bytes)
            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope(svgWorkId);
                using (MarkdownSvgStageTrace.MeasureFrom(
                    svgWorkId,
                    MarkdownSvgWorkStage.OuterDispatchQueueWait,
                    dispatchStartedAt,
                    bytes.LongLength))
                {
                }
#endif
                try
                {
                    return await OpenSvgSourceCoreAsync(
                            bytes, intrinsicHint, renderWork, securityPartitionKey,
                            builtInSourceAliasKey, demand, loadDemandGeneration)
                        .ConfigureAwait(false);
                }
                finally
                {
                    bytes = Array.Empty<byte>();
                }
            }

            Task<SvgOpenStageResult> core = Context.Dispatcher?.HasThreadAccess == true
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

    internal static Task RunSvgCoreOnBackgroundIfNeeded(
        bool dispatcherHasThreadAccess,
        Func<Task> runCore) =>
        dispatcherHasThreadAccess ? Task.Run(runCore) : runCore();

    private string GetSvgSourceSecurityPartitionKey(string resolvedCacheKey)
    {
        if (!string.IsNullOrEmpty(resolvedCacheKey))
            return resolvedCacheKey;
        // Only the resolver can authorize process-wide reuse. An unkeyed
        // resolved asset stays local to this ImageBox, including source, bitmap,
        // failure and admission identities. Never infer trust from its URL.
        string? localKey = Volatile.Read(ref _svgAnonymousPartitionKey);
        if (localKey is not null)
            return localKey;
        string created = "svg-local:" + Guid.NewGuid().ToString("N");
        return Interlocked.CompareExchange(ref _svgAnonymousPartitionKey, created, null) ?? created;
    }

    private async Task<SvgOpenStageResult> OpenSvgSourceCoreAsync(
        byte[] rawBytes,
        Size intrinsicHint,
        ImageWorkLease renderWork,
        string? securityPartitionKey,
        string? builtInSourceAliasKey,
        MarkdownSvgDemandTicket demand,
        int loadDemandGeneration)
    {
        CancellationToken lifetimeToken = renderWork.Token;
        int renderGeneration = AdvanceSvgRenderGeneration();
        string resolvedCacheKey = securityPartitionKey ?? Url;
        string effectiveSecurityPartitionKey = GetSvgSourceSecurityPartitionKey(resolvedCacheKey);
        IMarkdownSvgRenderer? renderer = Context.SvgRenderer;
        IMarkdownSvgHostAdmission? hostAdmission = renderer as IMarkdownSvgHostAdmission;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long svgWorkId = MarkdownSvgStageTrace.CurrentWorkId;
        long svgSourceBytes = rawBytes.LongLength;
#else
        long svgWorkId = 0;
#endif
        if (renderer is null)
        {
            PublishSvgFailure(
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
            Context.PerformanceSession as IMarkdownPerformanceSessionInternal;
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
        BitmapOutputWorkContext bitmapOutputWork = CaptureBitmapOutputWorkContext(
            cancellationToken, expectedDemand: demand, expectedSvgGeneration: renderGeneration);
        IMarkdownSvgDocument? openedDocument = null;
        SvgDocumentUseLease? openedDocumentUse = null;
        SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot = null;
        SvgHostCapacityDeferral? hostCapacityDeferral = null;
        IMarkdownSvgHostPreparationLease? hostPreparationLease = null;
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
        string currentSecurityPartitionHash = HashSvgSecurityPartition(effectiveSecurityPartitionKey);
        bool metadataWasKnown = IsKnownSvgSource(rawBytes, currentSecurityPartitionHash);
        long providerCacheGeneration = 0;
        long rendererIdentity = GetSvgRendererIdentity(renderer);
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
                                Context.EffectiveImagePreparationOwner ?? Context,
                                exclusive: false,
                                preparationToken)
                            .ConfigureAwait(false);
                    using IDisposable processPreparationSlot =
                        await MarkdownImagePreparationAdmission.Shared.EnterAsync(
                                Context.EffectiveImagePreparationOwner ?? Context,
                                rawBytes.LongLength * 3,
                                exclusive: false,
                                preparationToken)
                            .ConfigureAwait(false);
                    // CPU admission is complete before taking byte ownership.
                    // Only the opt-in session path and a participating provider
                    // use this reservation; legacy/custom providers are unchanged.
                    if (preparationSession is not null &&
                        renderer is IMarkdownSvgHostPreparationAdmission scratchAdmission)
                    {
                        long demandVersionAtPreparation = demand.Version;
                        bool admitted = scratchAdmission.TryReserveHostPreparationScratch(
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
                                VisualDemandEpochAtDenial: Volatile.Read(ref _svgHostVisualDemandEpoch));
                            // Do not call PrepareSource or park while owning bytes.
                            // The caller drains the original resolver lease before
                            // registering the scalar-only capacity waiter.
                            rawBytes = Array.Empty<byte>();
                            return new SvgOpenStageResult(null, hostCapacityDeferral);
                        }
                    }
                    {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        using var stageTrace = MarkdownSvgStageTrace.Measure(
                            svgWorkId,
                            MarkdownSvgWorkStage.SourcePreparation,
                            svgSourceBytes);
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        using var stageTrace = MarkdownSvgStageTrace.Measure(
                            svgWorkId,
                            MarkdownSvgWorkStage.HostPreflight,
                            svgSourceBytes);
#endif
                        if (preparation.SanitizedBytes is not null &&
                            IsKnownSvgSource(rawBytes, currentSecurityPartitionHash) &&
                            _svgContentHash is { } cachedContentHash &&
                            _svgSecurityPartitionHash is { } cachedPartitionHash)
                        {
                            preflight = (
                                intrinsicHint,
                                _svgTitle,
                                _svgDesc,
                                cachedContentHash,
                                cachedPartitionHash);
                        }
                        else
                        {
                            preflight = PreflightSvg(
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

            svgFailureCacheKey = BuildSvgFailureCacheKey(
                preflight.ContentHash,
                preflight.SecurityPartitionHash,
                rendererIdentity,
                providerCacheGeneration);
            if (_svgFailures.TryGetValue(
                    svgFailureCacheKey,
                    out SvgFailure cachedFailure))
            {
                PublishSvgFailure(
                    cachedFailure.Reason,
                    cachedFailure.Description,
                    svgFailureCacheKey,
                    cachePermanently: true,
                    renderGeneration);
                return default;
            }

            if (preparation.FailureReason is { } preflightFailureReason)
            {
                PublishSvgFailure(
                    preflightFailureReason,
                    preparation.FailureDescription ??
                        "The SVG was rejected by host preflight (invalid-content).",
                    svgFailureCacheKey,
                    cachePermanently: true,
                    renderGeneration);
                return default;
            }

            cancellationToken.ThrowIfCancellationRequested();
            MarkdownSvgColor semanticColor = GetSemanticSvgColor();
            MarkdownSvgColorScheme colorScheme = GetSvgColorScheme();
            string contentCacheKey = BuildSvgContentCacheKey(
                preflight.ContentHash,
                preflight.SecurityPartitionHash,
                rendererIdentity,
                providerCacheGeneration,
                Context.Language,
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
            if (TryPublishContentCachedSvg(
                    contentCacheKey,
                    resolvedCacheKey,
                    builtInSourceAliasKey,
                    layoutInvalidated: !metadataWasKnown,
                    renderGeneration,
                    bitmapOutputWork))
            {
                return default;
            }
            if (IsBitmapOutputDeferred)
                return default;

            if (hostAdmission is null)
            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                using var stageTrace = MarkdownSvgStageTrace.Measure(
                    svgWorkId,
                    MarkdownSvgWorkStage.HostAdmissionWait,
                    svgSourceBytes);
#endif
                openAdmissionEntry = await _svgOpenAdmissions
                    .AcquireAsync(contentCacheKey, cancellationToken)
                    .ConfigureAwait(false);

                if (TryPublishContentCachedSvg(
                        contentCacheKey,
                        resolvedCacheKey,
                        builtInSourceAliasKey,
                        layoutInvalidated: !metadataWasKnown,
                        renderGeneration,
                        bitmapOutputWork))
                {
                    return default;
                }
                if (IsBitmapOutputDeferred)
                    return default;

                await _svgProviderAdmissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                providerAdmissionHeld = true;
            }

            // The provider owns the hard deadline for each active worker
            // transaction. Do not start a second wall-clock deadline around
            // provider queueing plus Open + Render: a visible badge burst can
            // legitimately wait behind earlier visible work even though every
            // individual worker request stays inside its immutable ceiling.

            openRequest = new MarkdownSvgOpenRequest(
                rawBytes,
                Context.Language,
                colorScheme,
                semanticColor,
                demand.Priority);

            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                using var stageTrace = MarkdownSvgStageTrace.Measure(
                    svgWorkId,
                    MarkdownSvgWorkStage.ProviderOpen,
                    svgSourceBytes);
#endif
                // Open may still read the caller's bytes until it really
                // completes. Keep the source reservation and image-work lease
                // across that completion, even if the provider ignores cancel.
                // The provider owns its bounded transaction deadline; do not
                // release caller memory on cancellation of just our await.
                if (hostAdmission is not null)
                {
                    long demandVersionAtOpen = demand.Version;
                    MarkdownSvgHostOpenResult hostResult = await hostAdmission.OpenForHostAsync(
                            openRequest,
                            demand,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (hostResult.Status == MarkdownSvgHostOpenStatus.CapacityUnavailable)
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
                            VisualDemandEpochAtDenial: Volatile.Read(ref _svgHostVisualDemandEpoch));
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
                    if (hostResult.Status != MarkdownSvgHostOpenStatus.Opened ||
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

            if (hostAdmission is not null && TryPublishContentCachedSvg(
                    contentCacheKey,
                    resolvedCacheKey,
                    builtInSourceAliasKey,
                    layoutInvalidated: !metadataWasKnown,
                    renderGeneration,
                    bitmapOutputWork))
            {
                return default;
            }
            if (IsBitmapOutputDeferred)
                return default;

            // Open is a candidate, not a replacement of the accepted tile
            // document. Hold a use until raster completion or UI publication.
            openedDocumentUse = TryAcquireCandidateSvgDocumentUse(openedDocument, out bool ownershipConflict);
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
            if (!IsSvgProviderCurrent(renderer, providerCacheGeneration, info.HasText))
            {
                PublishSvgGenerationInvalidated(renderGeneration);
                return default;
            }
            Size intrinsic = ResolveIntrinsicSize(info, preflight.Intrinsic);
            string? description = string.IsNullOrWhiteSpace(info.Description)
                ? preflight.Desc
                : TrimSvgMetadata(info.Description);

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
                _svgCache.Set(builtInSourceAliasKey, sourceEntry);
            _svgCache.Set(contentCacheKey, sourceEntry);

            SvgRasterPlan rasterPlan = PickRasterDimensions(intrinsic);
            int targetWidth = rasterPlan.Width;
            int targetHeight = rasterPlan.Height;
            long targetPixels = (long)targetWidth * targetHeight;
            string bitmapIdentity = BuildSvgBitmapIdentity(
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
                    ownerSlot = SvgHostAdmissionCoordinator.TryReserveOwnerSlot(
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
                            VisualDemandEpochAtDenial: Volatile.Read(ref _svgHostVisualDemandEpoch));
                        rawBytes = Array.Empty<byte>();
                        preparation = null!;
                        return new SvgOpenStageResult(null, hostCapacityDeferral);
                    }
                }
            }

            SvgPublicationMetadata metadata = CreateSvgPublicationMetadata(
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
                publishRequestedAt: 0,
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
                this, metadata, contentCacheKey, builtInSourceAliasKey, rasterPlan.UsesTiles,
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
            HandleSvgWorkFailure(exception, cancellationToken, sessionLifetimeToken,
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
                TryImageCleanup(hostPreparationLease.Dispose);
            try
            {
                _svgOpenAdmissions.Release(ref openAdmissionEntry);
            }
            catch (Exception exception)
            {
                RecordImageRetirementFailure(exception);
            }
            if (providerAdmissionHeld)
                TryImageCleanup(() => _svgProviderAdmissionGate.Release());
            if (openedDocument is not null)
                RetireUncommittedSvgDocument(openedDocument);
            if (openedDocumentUse is not null)
                TryImageCleanup(openedDocumentUse.Dispose);
            if (ownerSlot is not null)
                TryImageCleanup(ownerSlot.Dispose);
            if (preparationCancellation is not null)
                TryImageCleanup(preparationCancellation.Dispose);
            if (!renderOwnershipTransferred)
                renderWork.Dispose();
        }

        return new SvgOpenStageResult(null, hostCapacityDeferral);
    }


    private Task RenderOpenedSvgAsync(SvgOpenedRenderWork work)
    {
        if (!work.TryConsume())
            throw new InvalidOperationException("The opened SVG work has already been consumed or disposed.");
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long dispatchStartedAt = Context.Dispatcher?.HasThreadAccess == true &&
            work.Metadata.WorkId != 0 && MarkdownPerformanceEventSource.Log.IsSvgWorkMeasurementEnabled()
                ? Stopwatch.GetTimestamp() : 0;
#else
        long dispatchStartedAt = 0;
#endif
        try
        {
            // Even an asynchronously opened source can complete on the UI
            // dispatcher when every admission/cache operation is synchronous.
            return Context.Dispatcher?.HasThreadAccess == true
                ? Task.Run(() => RenderOpenedSvgCoreAsync(work, dispatchStartedAt), CancellationToken.None)
                : RenderOpenedSvgCoreAsync(work, dispatchStartedAt);
        }
        catch
        {
            work.Complete();
            throw;
        }
    }

    private async Task RenderOpenedSvgCoreAsync(SvgOpenedRenderWork work, long dispatchStartedAt)
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
        MarkdownSvgDemandTicket demand = metadata.Demand!;
        BitmapOutputWorkContext bitmapOutputWork = CaptureBitmapOutputWorkContext(
            cancellationToken, expectedDemand: demand, expectedSvgGeneration: renderGeneration);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long svgWorkId = metadata.WorkId;
        long svgSourceBytes = metadata.SourceByteLength;
#endif
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope(metadata.WorkId);
            if (dispatchStartedAt != 0)
            {
                using var dispatchTrace = MarkdownSvgStageTrace.MeasureFrom(
                    svgWorkId, MarkdownSvgWorkStage.OuterDispatchQueueWait,
                    dispatchStartedAt, svgSourceBytes);
            }
#endif
            cancellationToken.ThrowIfCancellationRequested();
            if (work.UsesTiles)
            {
                long tilePublishRequestedAt = 0;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                tilePublishRequestedAt = Stopwatch.GetTimestamp();
#endif
                var tiledPublication = CreateTiledSvgPublication(
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
                    PublishOnUiThread(tiledPublication.Publish, tiledPublication.OnDropped);
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

            CanvasDevice device = Context.ResourceCreator.Device;
            if (!TryAcquireAdmittedSvgBitmap(device, bitmapIdentity, out bitmapLease,
                    expectedWork: bitmapOutputWork))
            {
                if (IsBitmapOutputDeferred)
                    return;
                {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    using var stageTrace = MarkdownSvgStageTrace.Measure(
                        svgWorkId,
                        MarkdownSvgWorkStage.BitmapCacheWait,
                        svgSourceBytes,
                        targetPixels);
#endif
                    renderGate = SharedCanvasBitmapCache.GetDecodeGate(device, bitmapIdentity);
                    await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    renderGateHeld = true;
                }

                _ = TryAcquireAdmittedSvgBitmap(device, bitmapIdentity, out bitmapLease,
                    expectedWork: bitmapOutputWork);
                if (bitmapLease is null)
                {
                    if (IsBitmapOutputDeferred ||
                        !TryReserveBitmapOutput(checked(targetPixels * 4), out bitmapReservation,
                            expectedWork: bitmapOutputWork))
                        return;
                    MarkdownSvgRaster raster;
                    {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        using var stageTrace = MarkdownSvgStageTrace.Measure(
                            svgWorkId,
                            MarkdownSvgWorkStage.ProviderRender,
                            svgSourceBytes,
                            targetPixels);
#endif
                        raster = await RenderSvgDocumentAsync(
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
                        if (!IsSvgProviderCurrent(renderer, providerCacheGeneration, info.HasText))
                        {
                            PublishSvgGenerationInvalidated(renderGeneration);
                            return;
                        }
                        ValidateSvgRaster(raster, targetWidth, targetHeight);
                        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                            using var stageTrace = MarkdownSvgStageTrace.Measure(
                                svgWorkId,
                                MarkdownSvgWorkStage.BitmapUpload,
                                svgSourceBytes,
                                targetPixels);
                            ownedBitmap = CreateBitmapFromSvgRaster(
                                raster,
                                svgWorkId,
                                svgSourceBytes,
                                targetPixels,
                                bitmapReservation: bitmapReservation);
#else
                            ownedBitmap = CreateBitmapFromSvgRaster(raster, bitmapReservation: bitmapReservation);
#endif
                        }
                        {
                            try
                            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                                using var stageTrace = MarkdownSvgStageTrace.Measure(
                                    svgWorkId,
                                    MarkdownSvgWorkStage.BitmapCacheStore,
                                    svgSourceBytes,
                                    targetPixels);
#endif
                                bitmapLease = SharedCanvasBitmapCache.StoreSvgAndAcquire(
                                    device,
                                    bitmapIdentity,
                                    ownedBitmap,
                                    intrinsicSize: null,
                                    pixelWidth: raster.WidthPixels,
                                    pixelHeight: raster.HeightPixels
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                                    , svgWorkId: svgWorkId,
                                    svgSourceBytes: svgSourceBytes,
                                    svgTargetPixels: targetPixels
#endif
                                    , bitmapReservation: bitmapReservation
                                );
                                ownedBitmap = null;
                                bitmapReservation = null;
                            }
                            catch
                            {
                                ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        TryImageCleanup(raster.Dispose);
                    }
                }
            }

            SharedCanvasBitmapCache.Lease publishedLease = bitmapLease ??
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "The SVG bitmap cache did not return a lease.");

            UpdateSvgSourceCacheBitmap(
                work.BuiltInSourceAliasKey,
                contentCacheKey,
                bitmapIdentity,
                targetWidth,
                targetHeight,
                metadata.PhysicalViewport);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
            long bitmapPublishRequestedAt = Stopwatch.GetTimestamp();
#else
            long bitmapPublishRequestedAt = 0;
#endif
            var bitmapPublication = CreateBitmapSvgPublication(
                metadata,
                publishedLease,
                bitmapPublishRequestedAt);
            bitmapLease = null;
            try
            {
                PublishOnUiThread(bitmapPublication.Publish, bitmapPublication.OnDropped);
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
            HandleSvgWorkFailure(exception, cancellationToken, work.SessionLifetimeToken,
                resolvedCacheKey, svgFailureCacheKey, renderGeneration);
        }
        finally
        {
            ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            if (bitmapLease is { } remainingLease)
                TryImageCleanup(remainingLease.Dispose);
            if (renderGateHeld)
                TryImageCleanup(() => renderGate!.Release());
            // Consumed ownership retires only here, after actual Render and
            // upload/raster cleanup. Caller Dispose cannot revoke it early.
            openedDocument = null;
            openedDocumentUse = null;
            work.Complete();
        }
    }


    private void HandleSvgWorkFailure(
        Exception exception, CancellationToken cancellationToken,
        CancellationToken sessionLifetimeToken, string resolvedCacheKey,
        string svgFailureCacheKey, int renderGeneration)
    {
        if (exception is OperationCanceledException ||
            exception is MarkdownSvgException { Reason: MarkdownSvgFailureReason.Canceled })
        {
            // A parent cancellation may precede the linked-token callback.
            // Retirement cannot become a retryable provider-generation change.
            if (cancellationToken.IsCancellationRequested || sessionLifetimeToken.IsCancellationRequested)
            {
                if (!_disposed)
                    _loadStarted = false;
            }
            else
            {
                PublishSvgGenerationInvalidated(renderGeneration);
            }
            return;
        }
        if (exception is MarkdownSvgException svgException)
        {
            // Target/queue-dependent resource limits must not poison content.
            // Deterministic preflight denials are cached in the Open stage.
            bool deterministic = svgException.Reason == MarkdownSvgFailureReason.UnsupportedContent;
            PublishSvgFailure(svgException, svgFailureCacheKey,
                deterministic && !string.IsNullOrEmpty(svgFailureCacheKey), renderGeneration);
            return;
        }
        if (GraphicsDeviceErrors.IsDeviceLost(exception))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG upload deferred after graphics device loss: {exception.Message}");
            PublishOnUiThread(() =>
            {
                if (_disposed || renderGeneration != Volatile.Read(ref _svgRenderGeneration))
                    return;
                _loadStarted = false;
                RaiseLoadCompleted(layoutInvalidated: false);
            });
            return;
        }
        PublishSvgFailure(
            new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer failed safely.", exception),
            resolvedCacheKey, cachePermanently: false, renderGeneration: renderGeneration);
    }

    private void PaintImageContent(CanvasDrawingSession ds, Rect destination, Rect viewport)
    {
        if (!_svgUsesTiles)
        {
            if (_bitmap is { } bitmap)
                PaintWholeBitmap(ds, bitmap, destination);
            else
                PaintPlaceholder(ds, destination);
            return;
        }

        if (_svgBitmapWidthPixels <= 0 ||
            _svgBitmapHeightPixels <= 0 ||
            destination.Width <= 0 ||
            destination.Height <= 0)
        {
            return;
        }

        lock (_svgTileLock)
        {
            PaintSvgTileLayerNoLock(
                ds,
                _svgTiles,
                destination,
                viewport,
                _svgTileCacheWidthPixels,
                _svgTileCacheHeightPixels,
                _svgTileCachePhysicalViewport,
                useFallbackTiles: true);
        }

        QueueVisibleSvgTiles(destination, viewport);
    }

    private void PaintWholeBitmap(CanvasDrawingSession ds, CanvasBitmap bitmap, Rect destination)
    {
        if (_bitmapPhysicalViewport is { } physical)
        {
            // The provider has already rasterized the fractional viewport's
            // edge coverage. A fractional source rectangle would clip that
            // coverage a second time. Keep the complete pixel allocation at
            // its intended scale, including its partially covered last pixel.
            destination.Width *= bitmap.SizeInPixels.Width / physical.Width;
            destination.Height *= bitmap.SizeInPixels.Height / physical.Height;
        }
        ds.DrawImage(bitmap, destination);
    }

    private static void DrawSvgTileSetNoLock(
        CanvasDrawingSession ds,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> tiles,
        Rect destination,
        Rect viewport,
        int widthPixels,
        int heightPixels,
        MarkdownSvgPhysicalViewport? physicalViewport)
    {
        if (widthPixels <= 0 || heightPixels <= 0)
            return;

        double physicalWidth = physicalViewport?.Width ?? widthPixels;
        double physicalHeight = physicalViewport?.Height ?? heightPixels;
        double scaleX = destination.Width / physicalWidth;
        double scaleY = destination.Height / physicalHeight;
        foreach (KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> tile in tiles)
        {
            Rect tileDestination = new(
                destination.X + tile.Key.X * scaleX,
                destination.Y + tile.Key.Y * scaleY,
                tile.Key.Width * scaleX,
                tile.Key.Height * scaleY);
            if (tileDestination.Right < viewport.Left ||
                tileDestination.Left > viewport.Right ||
                tileDestination.Bottom < viewport.Top ||
                tileDestination.Top > viewport.Bottom)
            {
                continue;
            }

            // As with whole rasters, the worker owns antialiasing at the outer
            // fractional viewport. Do not multiply the final tile's coverage.
            ds.DrawImage(tile.Value.Bitmap, tileDestination);
        }
    }

    private void QueueVisibleSvgTiles(Rect destination, Rect viewport)
    {
        SvgTileRenderPlan? plan = Volatile.Read(ref _svgTileRenderPlan);
        if (plan is null)
        {
            if (TryObservePressureParkedVisibleGap(destination, viewport, out long visualDemandEpoch))
                ReopenPressureParkedSvgForVisibleDemand(visualDemandEpoch);
            return;
        }
        if (!IsCurrentSvgTilePlan(plan))
            return;
        if (!IsCurrentSvgRendererState())
        {
            return;
        }

        Rect visible = IntersectRects(destination, viewport);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            if (!plan.TryBeginViewportMutation())
                return;

            try
            {
                lock (_svgTileLock)
                {
                    if (IsCurrentSvgTilePlan(plan))
                    {
                        bool observationChanged =
                            Volatile.Read(ref _svgCurrentViewportVisible) != 0 ||
                            _svgVisibleTileRangeKnown ||
                            _svgVisibleFirstTileX != 0 || _svgVisibleFirstTileY != 0 ||
                            _svgVisibleLastTileX != 0 || _svgVisibleLastTileY != 0;
                        Volatile.Write(ref _svgCurrentViewportVisible, 0);
                        _svgVisibleTileRangeKnown = false;
                        _svgVisibleFirstTileX = 0;
                        _svgVisibleFirstTileY = 0;
                        _svgVisibleLastTileX = 0;
                        _svgVisibleLastTileY = 0;
                        if (observationChanged)
                            Interlocked.Increment(ref _svgViewportObservationVersion);
                    }
                }
            }
            finally
            {
                plan.EndViewportMutation();
            }
            return;
        }

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

        List<SharedCanvasBitmapCache.Lease>? retired = null;
        List<CancellationTokenSource>? staleWork = null;
        int remainingQueueCapacity;
        if (!plan.TryBeginViewportMutation())
            return;

        try
        {
            lock (_svgTileLock)
            {
                if (!IsCurrentSvgTilePlan(plan))
                    return;

                // Dictionary.Remove does not invalidate its enumerator on the
                // supported .NET runtimes. No additions occur in either loop and
                // the lock excludes concurrent writers. Warm paints need no arrays.
                foreach (KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> tile in _svgTiles)
                {
                    SvgTileKey key = tile.Key;
                    int tileX = key.X / SvgTileSizePixels;
                    int tileY = key.Y / SvgTileSizePixels;
                    if (tileX < firstTileX || tileX > lastTileX ||
                        tileY < firstTileY || tileY > lastTileY)
                    {
                        // Reserve all retirement slots before removing a lease:
                        // an allocation failure must leave ownership recoverable.
                        retired ??= new List<SharedCanvasBitmapCache.Lease>(checked(
                            _svgTiles.Count + (_svgFallbackTiles?.Count ?? 0)));
                        if (_svgTiles.Remove(key))
                            retired.Add(tile.Value);
                    }
                }

                if (_svgFallbackTiles is not null)
                {
                    foreach (KeyValuePair<SvgTileKey, SharedCanvasBitmapCache.Lease> tile in _svgFallbackTiles)
                    {
                        if (ShouldRetainSvgFallbackTile(
                            tile.Key,
                            _svgFallbackWidthPixels,
                            _svgFallbackHeightPixels,
                            plan.FullWidthPixels,
                            plan.FullHeightPixels,
                            destination,
                            visible,
                            _svgTiles,
                            _svgFallbackPhysicalViewport,
                            plan.PhysicalViewport))
                        {
                            continue;
                        }

                        retired ??= new List<SharedCanvasBitmapCache.Lease>(checked(
                            _svgTiles.Count + _svgFallbackTiles.Count));
                        if (_svgFallbackTiles.Remove(tile.Key))
                            retired.Add(tile.Value);
                    }
                }

                foreach (KeyValuePair<SvgTileKey, CancellationTokenSource> work in _svgTilesInFlight)
                {
                    int tileX = work.Key.X / SvgTileSizePixels;
                    int tileY = work.Key.Y / SvgTileSizePixels;
                    if (tileX < firstTileX || tileX > lastTileX ||
                        tileY < firstTileY || tileY > lastTileY)
                    {
                        SvgTileWorkOwner? owner = null;
                        int state = 2;
                        if (_svgTileWorkOwners.TryGetValue(work.Key, out owner))
                        {
                            state = Volatile.Read(ref owner.State);
                        }
                        if (state is not (0 or 2))
                            continue;

                        // Do not claim queued work before its cancellation owner
                        // can be retained, and do not allocate for busy work.
                        staleWork ??= new List<CancellationTokenSource>(_svgTilesInFlight.Count);
                        if (owner is not null && state == 0 &&
                            Interlocked.CompareExchange(ref owner.State, 2, 0) != 0)
                        {
                            continue;
                        }
                        if (_svgTilesInFlight.Remove(work.Key))
                        {
                            _svgTileWorkOwners.Remove(work.Key);
                            staleWork.Add(work.Value);
                        }
                    }
                }

                bool observationChanged =
                    Volatile.Read(ref _svgCurrentViewportVisible) != 1 ||
                    !_svgVisibleTileRangeKnown ||
                    _svgVisibleFirstTileX != firstVisibleTileX ||
                    _svgVisibleFirstTileY != firstVisibleTileY ||
                    _svgVisibleLastTileX != lastVisibleTileX ||
                    _svgVisibleLastTileY != lastVisibleTileY;
                _svgVisibleFirstTileX = firstVisibleTileX;
                _svgVisibleFirstTileY = firstVisibleTileY;
                _svgVisibleLastTileX = lastVisibleTileX;
                _svgVisibleLastTileY = lastVisibleTileY;
                _svgVisibleTileRangeKnown = true;
                Volatile.Write(ref _svgCurrentViewportVisible, 1);
                if (observationChanged)
                    Interlocked.Increment(ref _svgViewportObservationVersion);

                remainingQueueCapacity = Math.Max(0, 32 - _svgTilesInFlight.Count);
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
                if (retired is not null)
                {
                    foreach (SharedCanvasBitmapCache.Lease tile in retired)
                        TryImageCleanup(tile.Dispose);
                }
                if (staleWork is not null)
                {
                    foreach (CancellationTokenSource cancellation in staleWork)
                    {
                        try
                        {
                            _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation);
                        }
                        catch (Exception exception)
                        {
                            RecordImageRetirementFailure(exception);
                        }
                    }
                }
            }
        }

        plan.Demand.PromoteVisible();
        SvgHostAdmissionCoordinator.Touch(plan);

        // The isolated provider has its own bounded priority queue. Keep the
        // host-side visible burst bounded as well; each completion invalidates
        // the canvas so remaining visible tiles are discovered next frame.
        int queued = QueueSvgTileRange(
            plan,
            firstVisibleTileX,
            firstVisibleTileY,
            lastVisibleTileX,
            lastVisibleTileY,
            remainingQueueCapacity,
            MarkdownSvgRenderPriority.Visible);
        _ = QueueSvgTileRange(
            plan,
            firstTileX,
            firstTileY,
            lastTileX,
            lastTileY,
            remainingQueueCapacity - queued,
            MarkdownSvgRenderPriority.Overscan);
    }

    internal static bool ShouldRetainSvgFallbackTile(
        SvgTileKey key,
        int fallbackWidthPixels,
        int fallbackHeightPixels,
        int currentWidthPixels,
        int currentHeightPixels,
        Rect destination,
        Rect visible,
        Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> currentTiles,
        MarkdownSvgPhysicalViewport? fallbackViewport = null,
        MarkdownSvgPhysicalViewport? currentViewport = null)
    {
        // Invalid geometry cannot establish that the previous good image is
        // invisible or replaced. Preserve it until a valid observation arrives.
        long keyRight = (long)key.X + key.Width;
        long keyBottom = (long)key.Y + key.Height;
        if (currentTiles is null ||
            fallbackWidthPixels <= 0 || fallbackHeightPixels <= 0 ||
            currentWidthPixels <= 0 || currentHeightPixels <= 0 ||
            key.X < 0 || key.Y < 0 || key.Width <= 0 || key.Height <= 0 ||
            keyRight > fallbackWidthPixels || keyBottom > fallbackHeightPixels ||
            !IsFiniteNonemptyRect(destination) || !IsFiniteNonemptyRect(visible))
        {
            return true;
        }

        double visibleLeft = Math.Max(destination.Left, visible.Left);
        double visibleTop = Math.Max(destination.Top, visible.Top);
        double visibleRight = Math.Min(destination.Right, visible.Right);
        double visibleBottom = Math.Min(destination.Bottom, visible.Bottom);
        if (visibleLeft >= visibleRight || visibleTop >= visibleBottom)
            return true;

        // The previous bitmap has its own physical grid. Divide first to avoid
        // overflow for extreme but finite DIP coordinates; keep boundary-touching
        // tiles within one old-grid tile of the clipped viewport.
        double fallbackWidth = fallbackViewport?.Width ?? fallbackWidthPixels;
        double fallbackHeight = fallbackViewport?.Height ?? fallbackHeightPixels;
        double currentWidth = currentViewport?.Width ?? currentWidthPixels;
        double currentHeight = currentViewport?.Height ?? currentHeightPixels;
        double bandLeft = (visibleLeft - destination.Left) / destination.Width * fallbackWidth - SvgTileSizePixels;
        double bandTop = (visibleTop - destination.Top) / destination.Height * fallbackHeight - SvgTileSizePixels;
        double bandRight = (visibleRight - destination.Left) / destination.Width * fallbackWidth + SvgTileSizePixels;
        double bandBottom = (visibleBottom - destination.Top) / destination.Height * fallbackHeight + SvgTileSizePixels;
        if (keyRight < bandLeft || key.X > bandRight ||
            keyBottom < bandTop || key.Y > bandBottom)
        {
            return false;
        }

        // Cover the entire old tile, not just its visible portion. Exact rational
        // floor/ceiling prevents DPI changes from hiding a one-pixel gap. All
        // products fit in Int64 because the admitted dimensions fit in Int32.
        long left = (long)key.X * currentWidthPixels / fallbackWidthPixels;
        long top = (long)key.Y * currentHeightPixels / fallbackHeightPixels;
        long rightProduct = keyRight * currentWidthPixels;
        long bottomProduct = keyBottom * currentHeightPixels;
        long right = rightProduct / fallbackWidthPixels + (rightProduct % fallbackWidthPixels == 0 ? 0 : 1);
        long bottom = bottomProduct / fallbackHeightPixels + (bottomProduct % fallbackHeightPixels == 0 ? 0 : 1);
        if (fallbackWidth != fallbackWidthPixels || fallbackHeight != fallbackHeightPixels ||
            currentWidth != currentWidthPixels || currentHeight != currentHeightPixels)
        {
            // Fractional viewports are independent of their integral allocation.
            // Bias one ULP outward: retention may be conservative, never hide a
            // still-uncovered edge during a resize or mixed-DPI transition.
            left = (long)Math.Clamp(Math.Floor(Math.BitDecrement(key.X / fallbackWidth * currentWidth)), 0, currentWidthPixels - 1);
            top = (long)Math.Clamp(Math.Floor(Math.BitDecrement(key.Y / fallbackHeight * currentHeight)), 0, currentHeightPixels - 1);
            right = (long)Math.Clamp(Math.Ceiling(Math.BitIncrement(Math.Min(keyRight, fallbackWidth) / fallbackWidth * currentWidth)), 1, currentWidthPixels);
            bottom = (long)Math.Clamp(Math.Ceiling(Math.BitIncrement(Math.Min(keyBottom, fallbackHeight) / fallbackHeight * currentHeight)), 1, currentHeightPixels);
        }
        int firstTileX = (int)(left / SvgTileSizePixels);
        int firstTileY = (int)(top / SvgTileSizePixels);
        int lastTileX = (int)((right - 1) / SvgTileSizePixels);
        int lastTileY = (int)((bottom - 1) / SvgTileSizePixels);
        long requiredTileCount = ((long)lastTileX - firstTileX + 1) * ((long)lastTileY - firstTileY + 1);
        if (requiredTileCount > currentTiles.Count)
            return true;

        // Enumeration work is bounded by resident entries, not the SVG's area.
        // Values remain opaque: dictionary membership is the published lease
        // contract, so this geometry check makes no native calls or allocations.
        for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            int y = tileY * SvgTileSizePixels;
            for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
            {
                int x = tileX * SvgTileSizePixels;
                SvgTileKey currentKey = (
                    x, y,
                    Math.Min(SvgTileSizePixels, currentWidthPixels - x),
                    Math.Min(SvgTileSizePixels, currentHeightPixels - y));
                if (!currentTiles.ContainsKey(currentKey))
                    return true;
            }
        }
        return false;
    }

    private static bool IsFiniteNonemptyRect(Rect rectangle) =>
        double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) &&
        double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height) &&
        rectangle.Width > 0 && rectangle.Height > 0 &&
        double.IsFinite(rectangle.Right) && double.IsFinite(rectangle.Bottom);

    private bool IsCurrentSvgTilePlan(SvgTileRenderPlan plan) =>
        IsAcceptedSvgTilePlan(plan) &&
        Volatile.Read(ref _svgTileCacheTransitionToken) is null &&
        !ReferenceEquals(plan, Volatile.Read(ref _svgParkingPlan));

    private bool IsAcceptedSvgTilePlan(SvgTileRenderPlan plan) =>
        // Request epochs order replacements; only accepted plan/document
        // identity controls the previous good image's bounded tile residency.
        !_disposed &&
        !_resourcesSuspended &&
        !_imageRetirementFinalizing &&
        _svgUsesTiles &&
        ReferenceEquals(plan, Volatile.Read(ref _svgTileRenderPlan)) &&
        ReferenceEquals(plan.Document, Volatile.Read(ref _svgDocument));

    private int QueueSvgTileRange(
        SvgTileRenderPlan plan,
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY,
        int capacity,
        MarkdownSvgRenderPriority priority)
    {
        if (!IsCurrentSvgTilePlan(plan) || capacity <= 0)
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

                SvgTileWorkOwner? existingVisibleWork = null;
                lock (_svgTileLock)
                {
                    if (!IsCurrentSvgTilePlan(plan) || _svgTiles.ContainsKey(key))
                        continue;
                    if (_svgTilesInFlight.ContainsKey(key))
                    {
                        if (priority == MarkdownSvgRenderPriority.Visible &&
                            _svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? existing) &&
                            ReferenceEquals(existing.Plan, plan))
                        {
                            existingVisibleWork = existing;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else if (_svgTilesInFlight.Count >= 32)
                    {
                        continue;
                    }
                }
                if (existingVisibleWork is not null)
                {
                    existingVisibleWork.Demand.PromoteVisible();
                    continue;
                }

                if (!TryBeginImageWork(out ImageWorkLease? tileWork) || tileWork is null)
                    return queued;

                SvgDocumentUseLease? tileDocumentUse = TryAcquireSvgDocumentUse(plan.Document);
                if (tileDocumentUse is null)
                {
                    tileWork.Dispose();
                    return queued;
                }

                CancellationTokenSource workCancellation;
                var tileDemand = new MarkdownSvgDemandTicket(priority);
                try
                {
                    workCancellation = new CancellationTokenSource();
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
                    lock (_svgTileLock)
                    {
                        admitted = IsCurrentSvgTilePlan(plan) &&
                            !_svgTiles.ContainsKey(key) &&
                            !_svgTilesInFlight.ContainsKey(key) &&
                            _svgTilesInFlight.Count < 32;
                        if (admitted)
                        {
                            _svgTilesInFlight.Add(key, workCancellation);
                            _svgTileWorkOwners.Add(key, workOwner);
                        }
                    }
                }
                catch
                {
                    try { TryImageCleanup(workCancellation.Dispose); }
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
                        TryImageCleanup(workCancellation.Dispose);
                    }
                    finally
                    {
                        tileWork.Dispose();
                        tileDocumentUse.Dispose();
                    }
                    continue;
                }
                queued++;
                ScheduleSvgTileWork(plan, key, workOwner, tileWork, tileDocumentUse);
            }
        }
        return queued;
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
            lock (_svgTileLock)
            {
                if (_svgTilesInFlight.TryGetValue(key, out CancellationTokenSource? current) &&
                    ReferenceEquals(current, workOwner.Cancellation))
                {
                    Interlocked.CompareExchange(ref workOwner.State, 2, 0);
                    _svgTilesInFlight.Remove(key);
                    _svgTileWorkOwners.Remove(key);
                }
            }
            try
            {
                try { _ = RetireTileCancellationSource(workOwner.Cancellation); }
                catch (Exception exception) { RecordImageRetirementFailure(exception); }
            }
            finally
            {
                documentUse.Dispose();
                lifetimeWork.Dispose();
            }
            throw;
        }
    }

    private async Task RenderSvgTileAsync(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        ImageWorkLease lifetimeWork,
        SvgDocumentUseLease documentUse)
    {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope();
        long svgWorkId = svgWorkScope.WorkId;
        long svgSourceBytes = _svgSourceByteLength;
        long targetPixels = (long)key.Width * key.Height;
#endif
        string identity = $"{plan.IdentityPrefix}:tile={key.X},{key.Y},{key.Width},{key.Height}";
        CancellationTokenSource workCancellation = workOwner.Cancellation;
        SharedCanvasBitmapCache.Lease? bitmapLease = null;
        BitmapOutputAdmission.Reservation? bitmapReservation = null;
        CanvasBitmap? ownedBitmap = null;
        SemaphoreSlim? renderGate = null;
        bool renderGateHeld = false;
        bool providerAdmissionHeld = false;
        CancellationTokenSource? admissionCancellation = null;

        try
        {
            if (!IsCurrentSvgTilePlan(plan) || !IsCurrentSvgRendererState())
                return;
            admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeWork.Token,
                workCancellation.Token);
            bool providerOwnsAdmission = plan.HostAdmission is not null &&
                plan.Document is IMarkdownSvgHostDocument;
            if (!providerOwnsAdmission)
            {
                await _svgProviderAdmissionGate.WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
                providerAdmissionHeld = true;
            }
            CancellationToken cancellationToken = admissionCancellation.Token;
            if (!IsCurrentSvgTilePlan(plan) || !IsCurrentSvgRendererState())
                return;
            BitmapOutputWorkContext bitmapOutputWork = CaptureBitmapOutputWorkContext(
                cancellationToken, isExistingSvgTileOutput: true, expectedDemand: plan.Demand,
                expectedSvgGeneration: plan.RenderGeneration);
            CanvasDevice device = Context.ResourceCreator.Device;
            if (!TryAcquireAdmittedSvgBitmap(device, identity, out bitmapLease, isExistingSvgTileOutput: true,
                    expectedWork: bitmapOutputWork))
            {
                if (IsBitmapOutputDeferred)
                    return;
                renderGate = SharedCanvasBitmapCache.GetDecodeGate(device, identity);
                await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                renderGateHeld = true;

                _ = TryAcquireAdmittedSvgBitmap(device, identity, out bitmapLease, isExistingSvgTileOutput: true,
                    expectedWork: bitmapOutputWork);
                if (bitmapLease is null)
                {
                    if (IsBitmapOutputDeferred ||
                        !TryReserveBitmapOutput(checked((long)key.Width * key.Height * 4), out bitmapReservation,
                            isExistingSvgTileOutput: true, expectedWork: bitmapOutputWork))
                        return;
                    MarkdownSvgRaster raster = await RenderSvgDocumentAsync(
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
                            workOwner.Demand)
                        .ConfigureAwait(false);
                    try
                    {
                        if (!IsCurrentSvgTilePlan(plan) || !IsCurrentSvgRendererState())
                        {
                            return;
                        }
                        ValidateSvgRaster(raster, key.Width, key.Height);
                        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                            using var stageTrace = MarkdownSvgStageTrace.Measure(
                                svgWorkId,
                                MarkdownSvgWorkStage.BitmapUpload,
                                svgSourceBytes,
                                targetPixels);
                            ownedBitmap = CreateBitmapFromSvgRaster(
                                raster,
                                svgWorkId,
                                svgSourceBytes,
                                targetPixels,
                                bitmapReservation: bitmapReservation);
#else
                            ownedBitmap = CreateBitmapFromSvgRaster(raster, bitmapReservation: bitmapReservation);
#endif
                        }
                        try
                        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                            using var stageTrace = MarkdownSvgStageTrace.Measure(
                                svgWorkId,
                                MarkdownSvgWorkStage.BitmapCacheStore,
                                svgSourceBytes,
                                targetPixels);
#endif
                            bitmapLease = SharedCanvasBitmapCache.StoreSvgAndAcquire(
                                device,
                                identity,
                                ownedBitmap,
                                intrinsicSize: null,
                                pixelWidth: raster.WidthPixels,
                                pixelHeight: raster.HeightPixels
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                                , svgWorkId: svgWorkId,
                                svgSourceBytes: svgSourceBytes,
                                svgTargetPixels: targetPixels
#endif
                                , bitmapReservation: bitmapReservation
                            );
                            ownedBitmap = null;
                            bitmapReservation = null;
                        }
                        catch
                        {
                            ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
                            throw;
                        }
                    }
                    finally
                    {
                        TryImageCleanup(raster.Dispose);
                    }
                }
            }

            SharedCanvasBitmapCache.Lease publishedLease = bitmapLease ??
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.WorkerFailure,
                    "The SVG tile cache did not return a lease.");
            bitmapLease = null;
            SvgTilePublicationOwnership publicationOwnership = default;
            void ReleaseUntransferredLease()
            {
                if (publicationOwnership.TryReleaseUntransferred())
                    TryImageCleanup(publishedLease.Dispose);
            }

            PublishOnUiThread(
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
                        bool rendererCurrent = IsCurrentSvgRendererState();
                        lock (_svgTileLock)
                        {
                            accepted = rendererCurrent &&
                                IsCurrentSvgTileWorkOwnerNoLock(plan, key, workCancellation);
                            if (accepted)
                            {
                                _svgTiles.Remove(key, out previous);
                                _svgTiles.Add(key, publishedLease);
                                transferred = publicationOwnership.TryTransfer();
                                if (!transferred)
                                {
                                    // A rejection cleanup must never race an
                                    // accepted transfer. If it already won,
                                    // remove the just-added mapping while this
                                    // lock still protects the dictionary.
                                    _svgTiles.Remove(key);
                                    accepted = false;
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (previous is { } replacedTile)
                            TryImageCleanup(replacedTile.Dispose);
                        if (!transferred)
                            ReleaseUntransferredLease();
                    }

                    if (!accepted || !transferred)
                        return;

                    _svgHasRenderedTile = true;
                    _hasPresentedImage = true;
                    _svgFailureReason = null;
                    _svgFailureDescription = null;
                    _loadFailed = false;
                    RaiseLoadCompleted(layoutInvalidated: false);
                },
                ReleaseUntransferredLease);
        }
        catch (OperationCanceledException) when (
            lifetimeWork.Token.IsCancellationRequested ||
            workCancellation.IsCancellationRequested)
        {
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
        }
        catch (MarkdownSvgException ex) when (ex.Reason == MarkdownSvgFailureReason.Canceled)
        {
            // A disposed or invalidated document is retryable against the
            // replacement document and is not an unavailable image.
        }
        catch (MarkdownSvgException ex)
        {
            PublishSvgTileFailure(plan, key, workOwner, ex);
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] SVG tile upload deferred after graphics device loss: {ex.Message}");
        }
        catch (Exception ex)
        {
            PublishSvgTileFailure(
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
            ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            Task cancellationCallbacks = Task.CompletedTask;
            try
            {
                cancellationCallbacks = RetireTileCancellationSource(workCancellation);
            }
            catch (Exception exception)
            {
                RecordImageRetirementFailure(exception);
            }

            try
            {
                if (bitmapLease is { } abandonedLease)
                    TryImageCleanup(abandonedLease.Dispose);
                if (renderGateHeld)
                    TryImageCleanup(() => renderGate!.Release());
                if (providerAdmissionHeld)
                    TryImageCleanup(() => _svgProviderAdmissionGate.Release());
                TryImageCleanup(() => CompleteSvgTileWork(key, workCancellation));
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
                        RecordImageRetirementFailure(exception);
                    }
                }
                finally
                {
                    try
                    {
                        if (admissionCancellation is not null)
                            TryImageCleanup(admissionCancellation.Dispose);
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
    }

    private void PublishSvgTileFailure(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        MarkdownSvgException exception)
    {
        if (!TryClaimFailedSvgTile(plan, key, workOwner))
            return;

        MarkdownDiagnostics.WriteLine(
            $"[ImageBox] SVG tile failure {exception.Reason}: {exception.Message}");
        PublishOnUiThread(() =>
        {
            SvgTileCacheTransition? tileTransition = null;
            ImageWorkLease? documentCleanup = null;
            ImageWorkLease? preparedDocumentCleanup = null;
            SvgDocumentUseState? preparedDocumentState = null;
            bool hasTileFallback = false;
            bool tileTransitionApplied = false;
            bool loadFailed = false;
            try
            {
                if (!IsCurrentSvgTilePlan(plan) || !IsCurrentSvgRendererState())
                    return;

                lock (_svgPublicationSync)
                {
                    // Serialize failure invalidation and its tile-map transition
                    // against publication. This keeps an old callback from
                    // restoring/clearing cache state after a replacement commits.
                    if (!IsCurrentSvgTilePlan(plan))
                        return;

                    tileTransition = PrepareSvgTileFailureTransition(plan);
                    if (tileTransition is null)
                        return;

                    try
                    {
                        // Reserve the cleanup lease and capture the already-owned
                        // document state before invalidating the exact tile owner.
                        // If allocation fails here, the accepted plan/document are
                        // untouched and the staged tile transition is aborted.
                        lock (_svgWorkSync)
                        {
                            if (!ReferenceEquals(Volatile.Read(ref _svgDocument), plan.Document))
                                return;
                            preparedDocumentState = GetSvgDocumentUseStateNoLock(plan.Document);
                            preparedDocumentCleanup = BeginImageWorkNoLock(CancellationToken.None);
                        }

                        if (!TryInvalidateFailedSvgTile(
                            plan,
                            key,
                            workOwner,
                            tileTransition.Token))
                        {
                            return;
                        }

                        if (!ApplySvgTileCacheTransition(tileTransition))
                            return;
                        tileTransitionApplied = true;
                        hasTileFallback = tileTransition.RestoreFallback;

                        lock (_svgWorkSync)
                        {
                            if (ReferenceEquals(Volatile.Read(ref _svgDocument), plan.Document))
                            {
                                documentCleanup = RetireSvgDocumentNoLock(
                                    plan.Document,
                                    preparedDocumentState!,
                                    ref preparedDocumentCleanup);
                                Volatile.Write(ref _svgDocument, null);
                            }

                            bool hasPreviousGoodBitmap = _bitmap is not null || hasTileFallback;
                            _svgFailureReason = exception.Reason;
                            _svgFailureDescription = hasPreviousGoodBitmap ? null : exception.Message;
                            _loadFailed = !hasPreviousGoodBitmap;
                            loadFailed = _loadFailed;
                        }
                    }
                    finally
                    {
                        if (!tileTransitionApplied)
                            AbortSvgTileCacheTransition(tileTransition);
                    }
                }

                if (documentCleanup is not null)
                {
                    ImageWorkLease cleanup = documentCleanup;
                    documentCleanup = null;
                    ScheduleSvgDocumentDisposal(plan.Document, cleanup);
                }

                RetireSvgTileCacheTransition(tileTransition!);
                try { plan.OwnerSlot?.Dispose(); }
                catch (Exception retirementFailure) { RecordImageRetirementFailure(retirementFailure); }

                if (loadFailed)
                {
                    ReportUnavailable(MarkdownImageUnavailableReason.Unavailable, exception.Reason);
                    float maxWidth = Math.Max(
                        1f,
                        _availableWidth - (float)(Margin.Left + Margin.Right));
                    UpdatePlaceholder(maxWidth, _imageHeight);
                }

                RaiseLoadCompleted(layoutInvalidated: loadFailed);
            }
            finally
            {
                // A rejected/stale failure publication still releases the
                // reservation, but only after all publication locks have exited.
                preparedDocumentCleanup?.Dispose();
            }
        });
    }

    private bool TryInvalidateFailedSvgTile(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner,
        object? expectedTransitionToken)
    {
        // Claim terminal ownership under the tile lock, then invalidate under
        // the work lock. State 3 prevents the completion callback from
        // removing/reassigning this map entry between those two locks.
        lock (_svgTileLock)
        {
            if (!ReferenceEquals(workOwner.Plan, plan) ||
                !_svgTilesInFlight.TryGetValue(key, out CancellationTokenSource? current) ||
                !ReferenceEquals(current, workOwner.Cancellation) ||
                !_svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? mappedOwner) ||
                !ReferenceEquals(mappedOwner, workOwner) ||
                Interlocked.CompareExchange(ref workOwner.State, 3, 1) != 1)
            {
                return false;
            }
        }

        lock (_svgWorkSync)
        {
            if (Volatile.Read(ref workOwner.State) != 3 ||
                !ReferenceEquals(plan, workOwner.Plan) ||
                !IsAcceptedSvgTilePlan(plan) ||
                !ReferenceEquals(
                    Volatile.Read(ref _svgTileCacheTransitionToken),
                    expectedTransitionToken))
            {
                return false;
            }

            if (expectedTransitionToken is null &&
                Volatile.Read(ref _svgTileCacheTransitionToken) is not null)
            {
                return false;
            }

            // The accepted plan remains usable while a newer replacement is
            // pending. A terminal failure detaches this exact plan, but must
            // not cancel the publication epoch of that replacement.
            if (workOwner.Plan.RenderGeneration == Volatile.Read(ref _svgRenderGeneration))
                Interlocked.Increment(ref _svgRenderGeneration);
            _svgUsesTiles = false;
            Volatile.Write(ref _svgTileRenderPlan, null);
            Volatile.Write(ref _svgParkingPlan, null);
            _svgPressureParked = false;
            return true;
        }
    }

    private bool TryClaimFailedSvgTile(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        SvgTileWorkOwner workOwner)
    {
        lock (_svgTileLock)
        {
            return ReferenceEquals(workOwner.Plan, plan) &&
                IsCurrentSvgTilePlan(plan) &&
                IsCurrentSvgTileWorkOwnerNoLock(plan, key, workOwner.Cancellation) &&
                Interlocked.CompareExchange(ref workOwner.State, 1, 0) == 0;
        }
    }

    // Caller holds _svgTileLock. Both success and failure are fenced by the
    // exact request owner: eviction/reassignment can supersede work without
    // changing the accepted document or plan identity.
    private bool IsCurrentSvgTileWorkOwnerNoLock(
        SvgTileRenderPlan plan,
        SvgTileKey key,
        CancellationTokenSource workCancellation)
    {
        if (!IsCurrentSvgTilePlan(plan) ||
            !_svgTilesInFlight.TryGetValue(key, out CancellationTokenSource? current) ||
            !ReferenceEquals(current, workCancellation) ||
            !_svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? owner))
        {
            return false;
        }

        return ReferenceEquals(owner.Plan, plan) &&
            ReferenceEquals(owner.Cancellation, workCancellation) &&
            Volatile.Read(ref owner.State) == 0;
    }

    private void CompleteSvgTileWork(
        SvgTileKey key,
        CancellationTokenSource workCancellation)
    {
        void Complete()
        {
            lock (_svgTileLock)
            {
                if (_svgTilesInFlight.TryGetValue(key, out CancellationTokenSource? current) &&
                    ReferenceEquals(current, workCancellation))
                {
                    if (_svgTileWorkOwners.TryGetValue(key, out SvgTileWorkOwner? owner) &&
                        ReferenceEquals(owner.Cancellation, workCancellation))
                    {
                        // The terminal failure transition owns this row until
                        // it detaches the complete tile-map generation.
                        if (Volatile.Read(ref owner.State) == 3)
                            return;
                        _svgTileWorkOwners.Remove(key);
                    }
                    _svgTilesInFlight.Remove(key);
                }
            }
        }

        PublishOnUiThread(
            Complete,
            Complete);
    }

    private void ReleaseSvgTiles()
    {
        SharedCanvasBitmapCache.Lease[] tiles;
        CancellationTokenSource[] work;
        lock (_svgPublicationSync)
        {
            lock (_svgTileLock)
            {
                int tileCount = _svgTiles.Count + (_svgFallbackTiles?.Count ?? 0);
                tiles = new SharedCanvasBitmapCache.Lease[tileCount];
                int offset = 0;
                foreach (SharedCanvasBitmapCache.Lease tile in _svgTiles.Values)
                    tiles[offset++] = tile;
                if (_svgFallbackTiles is { } fallback)
                {
                    foreach (SharedCanvasBitmapCache.Lease tile in fallback.Values)
                        tiles[offset++] = tile;
                }
                work = _svgTilesInFlight.Values.ToArray();
                _svgTiles.Clear();
                _svgFallbackTiles?.Clear();
                _svgFallbackTiles = null;
                _svgFallbackIdentityPrefix = null;
                _svgFallbackWidthPixels = 0;
                _svgFallbackHeightPixels = 0;
                _svgFallbackPhysicalViewport = null;
                _svgTilesInFlight.Clear();
                _svgTileWorkOwners.Clear();
                _svgTileCacheIdentityPrefix = null;
                _svgTileCacheWidthPixels = 0;
                _svgTileCacheHeightPixels = 0;
                _svgTileCachePhysicalViewport = null;
                _svgHasRenderedTile = false;
                _svgVisibleTileRangeKnown = false;
                Volatile.Write(ref _svgCurrentViewportVisible, 0);
                _svgPressureDemandSignatureKnown = false;
                _svgTileCacheTransitionToken = null;
            }
        }

        foreach (SharedCanvasBitmapCache.Lease tile in tiles)
            TryImageCleanup(tile.Dispose);
        foreach (CancellationTokenSource cancellation in work)
        {
            try
            {
                // CancelAsync transitions the token immediately but runs
                // provider/linked-source callbacks away from this UI path.
                // CompleteSvgTileWork owns final source retirement and adds
                // both callback completion and source disposal to the image
                // lifetime barrier.
                _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation);
            }
            catch (Exception exception) { RecordImageRetirementFailure(exception); }
        }
    }

    private SvgTileCacheTransition? PrepareSvgTileCacheTransition(SvgTileRenderPlan? plan)
    {
        object token = new();
        lock (_svgTileLock)
        {
            if (_svgTileCacheTransitionToken is not null)
                return null;

            _svgTileCacheTransitionToken = token;
            try
            {
                SvgTileRenderPlan? previousPlan = Volatile.Read(ref _svgTileRenderPlan);
                Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> expectedTiles = _svgTiles;
                Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? expectedFallback = _svgFallbackTiles;
                string? previousIdentityPrefix = _svgTileCacheIdentityPrefix;
                int previousWidth = _svgTileCacheWidthPixels;
                int previousHeight = _svgTileCacheHeightPixels;
                bool sameIdentity = plan is not null &&
                    string.Equals(previousIdentityPrefix, plan.IdentityPrefix, StringComparison.Ordinal) &&
                    previousWidth == plan.FullWidthPixels &&
                    previousHeight == plan.FullHeightPixels &&
                    Nullable.Equals(_svgTileCachePhysicalViewport, plan.PhysicalViewport);

                SharedCanvasBitmapCache.Lease[]? retiredTiles = null;
                SharedCanvasBitmapCache.Lease[]? retiredFallback = null;
                Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? replacementTiles = null;
                if (plan is null)
                {
                    retiredTiles = expectedTiles.Values.ToArray();
                    if (expectedFallback is not null)
                        retiredFallback = expectedFallback.Values.ToArray();
                    replacementTiles = new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
                }
                else if (!sameIdentity)
                {
                    if (expectedTiles.Count > 0)
                    {
                        if (expectedFallback is not null)
                            retiredFallback = expectedFallback.Values.ToArray();
                        replacementTiles = new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
                    }
                    else if (expectedFallback is null)
                    {
                        replacementTiles = new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
                    }
                }

                CancellationTokenSource[] work = _svgTilesInFlight.Count == 0
                    ? Array.Empty<CancellationTokenSource>()
                    : _svgTilesInFlight.Values.ToArray();
                return new SvgTileCacheTransition(
                    token,
                    previousPlan,
                    plan,
                    expectedTiles,
                    expectedFallback,
                    previousIdentityPrefix,
                    previousWidth,
                    previousHeight,
                    sameIdentity,
                    replacementTiles,
                    retiredTiles,
                    retiredFallback,
                    work,
                    previousPhysicalViewport: _svgTileCachePhysicalViewport);
            }
            catch
            {
                if (ReferenceEquals(_svgTileCacheTransitionToken, token))
                    _svgTileCacheTransitionToken = null;
                throw;
            }
        }
    }

    private SvgTileCacheTransition? PrepareSvgTileFailureTransition(SvgTileRenderPlan plan)
    {
        object token = new();
        lock (_svgTileLock)
        {
            if (_svgTileCacheTransitionToken is not null ||
                !ReferenceEquals(Volatile.Read(ref _svgTileRenderPlan), plan))
            {
                return null;
            }

            _svgTileCacheTransitionToken = token;
            try
            {
                Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> expectedTiles = _svgTiles;
                Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>? expectedFallback = _svgFallbackTiles;
                bool restoreFallback = expectedFallback is { Count: > 0 };

                // Build every potentially allocating piece before the work
                // owner is invalidated. The publication token prevents new
                // tile admission while this transition is staged.
                SharedCanvasBitmapCache.Lease[] retiredTiles = expectedTiles.Count == 0
                    ? Array.Empty<SharedCanvasBitmapCache.Lease>()
                    : expectedTiles.Values.ToArray();
                SharedCanvasBitmapCache.Lease[]? retiredFallback = !restoreFallback &&
                    expectedFallback is { Count: > 0 }
                    ? expectedFallback.Values.ToArray()
                    : null;
                Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease> replacementTiles = restoreFallback
                    ? expectedFallback!
                    : new Dictionary<SvgTileKey, SharedCanvasBitmapCache.Lease>();
                CancellationTokenSource[] work = _svgTilesInFlight.Count == 0
                    ? Array.Empty<CancellationTokenSource>()
                    : _svgTilesInFlight.Values.ToArray();

                return new SvgTileCacheTransition(
                    token,
                    plan,
                    nextPlan: null,
                    expectedTiles,
                    expectedFallback,
                    _svgTileCacheIdentityPrefix,
                    _svgTileCacheWidthPixels,
                    _svgTileCacheHeightPixels,
                    sameIdentity: false,
                    replacementTiles,
                    retiredTiles,
                    retiredFallback,
                    work,
                    restoreFallback,
                    restoreFallback ? _svgFallbackIdentityPrefix : null,
                    restoreFallback ? _svgFallbackWidthPixels : 0,
                    restoreFallback ? _svgFallbackHeightPixels : 0,
                    previousPhysicalViewport: _svgTileCachePhysicalViewport,
                    restoredFallbackPhysicalViewport: restoreFallback ? _svgFallbackPhysicalViewport : null);
            }
            catch
            {
                if (ReferenceEquals(_svgTileCacheTransitionToken, token))
                    _svgTileCacheTransitionToken = null;
                throw;
            }
        }
    }

    private bool ApplySvgTileCacheTransition(SvgTileCacheTransition transition)
    {
        lock (_svgTileLock)
        {
            if (!ReferenceEquals(_svgTileCacheTransitionToken, transition.Token) ||
                !ReferenceEquals(_svgTileRenderPlan, transition.NextPlan) ||
                !ReferenceEquals(_svgTiles, transition.ExpectedTiles) ||
                !ReferenceEquals(_svgFallbackTiles, transition.ExpectedFallbackTiles))
            {
                return false;
            }

            SvgTileRenderPlan? plan = transition.NextPlan;
            if (plan is null)
            {
                if (transition.ReplacementTiles is null)
                    return false;

                _svgTiles = transition.ReplacementTiles;
                _svgFallbackTiles = null;
                _svgFallbackIdentityPrefix = null;
                _svgFallbackWidthPixels = 0;
                _svgFallbackHeightPixels = 0;
                _svgFallbackPhysicalViewport = null;
                if (transition.RestoreFallback)
                {
                    _svgTileCacheIdentityPrefix = transition.RestoredFallbackIdentityPrefix;
                    _svgTileCacheWidthPixels = transition.RestoredFallbackWidthPixels;
                    _svgTileCacheHeightPixels = transition.RestoredFallbackHeightPixels;
                    _svgTileCachePhysicalViewport = transition.RestoredFallbackPhysicalViewport;
                    _svgHasRenderedTile = _svgTiles.Count > 0;
                    Volatile.Write(ref _svgCurrentViewportVisible, -1);
                }
                else
                {
                    _svgTileCacheIdentityPrefix = null;
                    _svgTileCacheWidthPixels = 0;
                    _svgTileCacheHeightPixels = 0;
                    _svgTileCachePhysicalViewport = null;
                    _svgHasRenderedTile = false;
                    Volatile.Write(ref _svgCurrentViewportVisible, 0);
                }
            }
            else
            {
                if (!transition.SameIdentity)
                {
                    if (_svgTiles.Count > 0)
                    {
                        if (transition.ReplacementTiles is null)
                            return false;
                        _svgFallbackTiles = _svgTiles;
                        _svgFallbackIdentityPrefix = transition.PreviousIdentityPrefix;
                        _svgFallbackWidthPixels = transition.PreviousWidthPixels;
                        _svgFallbackHeightPixels = transition.PreviousHeightPixels;
                        _svgFallbackPhysicalViewport = transition.PreviousPhysicalViewport;
                        _svgTiles = transition.ReplacementTiles;
                    }
                    else if (_svgFallbackTiles is null)
                    {
                        if (transition.ReplacementTiles is null)
                            return false;
                        _svgTiles = transition.ReplacementTiles;
                    }
                }

                _svgTileCacheIdentityPrefix = plan.IdentityPrefix;
                _svgTileCacheWidthPixels = plan.FullWidthPixels;
                _svgTileCacheHeightPixels = plan.FullHeightPixels;
                _svgTileCachePhysicalViewport = plan.PhysicalViewport;
                _svgHasRenderedTile = _svgTiles.Count > 0;
                Volatile.Write(ref _svgCurrentViewportVisible, -1);
            }

            _svgTilesInFlight.Clear();
            _svgTileWorkOwners.Clear();
            _svgVisibleTileRangeKnown = false;
            _svgPressureDemandSignatureKnown = false;
            _svgTileCacheTransitionToken = null;
            return true;
        }
    }

    private void AbortSvgTileCacheTransition(SvgTileCacheTransition transition)
    {
        lock (_svgTileLock)
        {
            if (ReferenceEquals(_svgTileCacheTransitionToken, transition.Token))
                _svgTileCacheTransitionToken = null;
        }
    }

    private void RetireSvgTileCacheTransition(SvgTileCacheTransition transition)
    {
        if (transition.RetiredTiles is { } tiles)
        {
            foreach (SharedCanvasBitmapCache.Lease tile in tiles)
                TryImageCleanup(tile.Dispose);
        }
        if (transition.RetiredFallbackTiles is { } fallbackTiles)
        {
            foreach (SharedCanvasBitmapCache.Lease tile in fallbackTiles)
                TryImageCleanup(tile.Dispose);
        }
        foreach (CancellationTokenSource cancellation in transition.Work)
        {
            try { _ = CancellationTokenSourceRetirement.RequestCancellation(cancellation); }
            catch (Exception exception) { RecordImageRetirementFailure(exception); }
        }
    }

    private Task RetireTileCancellationSource(CancellationTokenSource source)
    {
        Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(source);

        // Track callback faults and disposal completion independently. The
        // shared helper disposes only after its callback task; tracking both
        // ensures ImageBox retirement cannot succeed until neither can still
        // access the source or invoke a registration.
        TrackDetachedImageWork(callbacks, failRetirementOnFault: true);
        Task sourceRetirement = CancellationTokenSourceRetirement.DisposeAfter(source, callbacks);
        TrackDetachedImageWork(sourceRetirement, failRetirementOnFault: true);
        return callbacks;
    }

    private static Rect IntersectRects(Rect left, Rect right)
    {
        double x = Math.Max(left.Left, right.Left);
        double y = Math.Max(left.Top, right.Top);
        double width = Math.Max(0, Math.Min(left.Right, right.Right) - x);
        double height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y);
        return new Rect(x, y, width, height);
    }

    private bool TryPublishCachedSvgBitmap(
        bool layoutInvalidated,
        SvgCacheEntry? sourceEntry = null,
        int? expectedRenderGeneration = null,
        string? resolvedCacheKey = null,
        BitmapOutputWorkContext? expectedWork = null)
    {
        if ((sourceEntry?.Info ?? _svgInfo) is not { } info ||
            (sourceEntry?.ContentHash ?? _svgContentHash) is not { } contentHash ||
            (sourceEntry?.SecurityPartitionHash ?? _svgSecurityPartitionHash) is not { } partitionHash)
        {
            return false;
        }

        SharedCanvasBitmapCache.Lease? unclaimedLease = null;
        string publicationCacheKey = resolvedCacheKey ?? _activeCacheKey ?? Url;
        try
        {
            MarkdownSvgColor semanticColor = GetSemanticSvgColor();
            MarkdownSvgColorScheme colorScheme = GetSvgColorScheme();
            IMarkdownSvgRenderer? renderer = Context.SvgRenderer;
            if (renderer is null)
                return false;
            long rendererIdentity = GetSvgRendererIdentity(renderer);
            long providerCacheGeneration = renderer.CacheGeneration;
            if ((sourceEntry?.RendererIdentity ?? _svgRendererIdentity) != rendererIdentity ||
                (info.HasText &&
                 (sourceEntry?.ProviderCacheGeneration ?? _svgProviderCacheGeneration) != providerCacheGeneration))
            {
                return false;
            }
            Size intrinsic = sourceEntry?.Intrinsic ?? _svgIntrinsicSize;
            SvgRasterPlan plan = PickRasterDimensions(intrinsic);
            if (plan.UsesTiles)
                return false;

            int width = plan.Width;
            int height = plan.Height;
            string identity = BuildSvgBitmapIdentity(
                contentHash,
                partitionHash,
                info,
                width,
                height,
                colorScheme,
                semanticColor,
                rendererIdentity,
                providerCacheGeneration,
                plan.PhysicalViewport);
            if (!TryAcquireAdmittedSvgBitmap(
                    Context.ResourceCreator.Device,
                    identity,
                    out SharedCanvasBitmapCache.Lease? lease,
                    expectedWork: expectedWork) ||
                lease is null)
            {
                return false;
            }

            unclaimedLease = lease;
            // A whole-image cache hit is a publication too. In particular it
            // must retire a prior tiled document and its plan after a resize,
            // rather than displaying new dimensions over the old tile grid.
            // Content-cache followers belong to an existing asynchronous
            // request. They must not advance its epoch and overtake a newer
            // resize/theme request that was admitted while they awaited work.
            int generation = expectedRenderGeneration ?? AdvanceSvgRenderGeneration();
            var metadata = new SvgPublicationMetadata(
                sourceEntry is null
                    ? _svgSourceIdentity ?? new WeakReference<byte[]>(Array.Empty<byte>())
                    : new WeakReference<byte[]>(sourceEntry.RawBytes),
                sourceEntry?.RawBytes.LongLength ?? _svgSourceByteLength,
                renderer,
                rendererIdentity,
                providerCacheGeneration,
                generation,
                !layoutInvalidated,
                publicationCacheKey,
                publicationCacheKey,
                intrinsic,
                sourceEntry is null ? _svgTitle : sourceEntry.Title,
                sourceEntry is null ? _svgDesc : sourceEntry.Desc,
                contentHash,
                partitionHash,
                info,
                width,
                height,
                identity,
                0,
                0,
                (long)width * height,
                PhysicalViewport: plan.PhysicalViewport);
            var publication = CreateBitmapSvgPublication(metadata, lease);
            unclaimedLease = null;
            try
            {
                PublishOnUiThread(publication.Publish, publication.OnDropped);
            }
            catch
            {
                publication.OnDropped?.Invoke();
                throw;
            }
            return true;
        }
        catch (MarkdownSvgException ex)
        {
            PublishSvgFailure(
                ex,
                publicationCacheKey,
                cachePermanently: false,
                renderGeneration: expectedRenderGeneration ?? Volatile.Read(ref _svgRenderGeneration));
            return true;
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] ignored an SVG cache hit after device loss: {ex.Message}");
            return false;
        }
        finally
        {
            if (unclaimedLease is not null)
                TryImageCleanup(unclaimedLease.Dispose);
        }
    }

    private bool TryPublishContentCachedSvg(
        string contentCacheKey,
        string resolvedCacheKey,
        string? builtInSourceAliasKey,
        bool layoutInvalidated,
        int renderGeneration,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        if (!_svgCache.TryGetValue(contentCacheKey, out SvgCacheEntry? entry) ||
            !IsSvgCacheEntryCurrent(entry))
        {
            return false;
        }

        // Keep admitted metadata staged until the bitmap publication commits.
        // A cache miss or dropped callback must leave the previous image and
        // its intrinsic dimensions/accessibility metadata coherent.
        if (!TryPublishCachedSvgBitmap(layoutInvalidated, entry, renderGeneration, resolvedCacheKey, bitmapOutputWork))
            return false;

        if (!string.IsNullOrEmpty(builtInSourceAliasKey))
            _svgCache.Set(builtInSourceAliasKey, entry);
        return true;
    }

    private static SvgPreflight PreflightSvg(
        byte[] rawBytes,
        MarkdownSvgSourcePreparation preparation,
        Size intrinsicHint,
        string securityPartition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string contentHash = Convert.ToHexString(SHA256.HashData(rawBytes));
        string partitionHash = HashSvgSecurityPartition(securityPartition);

        cancellationToken.ThrowIfCancellationRequested();
        if (preparation.FailureReason is not null)
            return (
                intrinsicHint,
                null,
                null,
                contentHash,
                partitionHash);

        SvgSourceMetadata metadata = preparation.TryGetPreparedMetadata(rawBytes, out SvgSourceMetadata preparedMetadata)
            ? preparedMetadata
            : SvgSourceMetadata.Extract(rawBytes, cancellationToken);
        Size intrinsic = intrinsicHint;
        if (intrinsic.Width <= 0 || intrinsic.Height <= 0)
        {
            double width = metadata.Geometry.IntrinsicWidth;
            double height = metadata.Geometry.IntrinsicHeight;
            if (width > 0 && height > 0)
                intrinsic = new Size(width, height);
        }

        return (
            intrinsic,
            string.IsNullOrWhiteSpace(metadata.Title) ? null : TrimSvgMetadata(metadata.Title),
            string.IsNullOrWhiteSpace(metadata.Desc) ? null : TrimSvgMetadata(metadata.Desc),
            contentHash,
            partitionHash);
    }

    private bool IsKnownSvgSource(byte[] candidate, string currentSecurityPartitionHash)
    {
        WeakReference<byte[]>? identity = _svgSourceIdentity;
        return _svgInfo is not null &&
               identity is not null &&
               identity.TryGetTarget(out byte[]? known) &&
               ReferenceEquals(known, candidate) &&
               string.Equals(
                   _svgSecurityPartitionHash,
                   currentSecurityPartitionHash,
                   StringComparison.Ordinal);
    }

    private static void UpdateSvgSourceCacheBitmap(
        string? builtInSourceAliasKey,
        string contentCacheKey,
        string bitmapIdentity,
        int width,
        int height,
        MarkdownSvgPhysicalViewport? physicalViewport)
    {
        // Look up only the exact preflight/content/theme/security identity.
        // An evicted source is not resurrected through a weak pointer, and no
        // async continuation needs an extra entry wrapper or retained array.
        if (!_svgCache.TryGetValue(contentCacheKey, out SvgCacheEntry? sourceEntry))
            return;
        SvgCacheEntry completedEntry = sourceEntry.WithLastBitmap(bitmapIdentity, width, height, physicalViewport);
        if (string.IsNullOrEmpty(builtInSourceAliasKey))
        {
            _svgCache.TryReplaceIfReferenceMatches(contentCacheKey, sourceEntry, completedEntry);
            return;
        }
        // A newer resolution, theme/locale variant or eviction may have
        // replaced the URL alias while this image rendered. Memoization is
        // optional: update only the same complete entry, atomically, and never
        // restore an evicted alias or overwrite newer content with a late render.
        // The content and matching URL alias share one cache transaction so
        // concurrent completions cannot publish different bitmap metadata.
        _svgCache.TryReplaceWithAliasIfReferenceMatches(
            contentCacheKey,
            builtInSourceAliasKey,
            sourceEntry,
            completedEntry);
    }

    private static SvgPublicationMetadata CreateSvgPublicationMetadata(
        WeakReference<byte[]> sourceIdentity,
        long sourceByteLength,
        IMarkdownSvgRenderer renderer,
        long rendererIdentity,
        long providerCacheGeneration,
        int renderGeneration,
        bool metadataWasKnown,
        string resolvedCacheKey,
        string svgFailureCacheKey,
        Size intrinsic,
        string? title,
        string? description,
        string contentHash,
        string securityPartitionHash,
        MarkdownSvgDocumentInfo info,
        int targetWidthPixels,
        int targetHeightPixels,
        string bitmapIdentity,
        long workId,
        long publishRequestedAt,
        long targetPixels,
        MarkdownSvgDemandTicket? demand = null,
        IMarkdownSvgHostAdmission? hostAdmission = null,
        long sourceReservationBytes = 0,
        SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot = null,
        MarkdownSvgPhysicalViewport? physicalViewport = null) =>
        new(
            sourceIdentity,
            sourceByteLength,
            renderer,
            rendererIdentity,
            providerCacheGeneration,
            renderGeneration,
            metadataWasKnown,
            resolvedCacheKey,
            svgFailureCacheKey,
            intrinsic,
            title,
            description,
            contentHash,
            securityPartitionHash,
            info,
            targetWidthPixels,
            targetHeightPixels,
            bitmapIdentity,
            workId,
            publishRequestedAt,
            targetPixels,
            demand,
            hostAdmission,
            sourceReservationBytes,
            ownerSlot,
            physicalViewport);

    // Transient callback pairs use a built-in value tuple: no separate carrier
    // object is allocated or retained alongside the actual publication owner.
    private (Action Publish, Action? OnDropped) CreateTiledSvgPublication(
        SvgPublicationMetadata metadata,
        IMarkdownSvgDocument openedDocument,
        SvgDocumentUseLease candidateUse,
        long publishRequestedAt = 0)
    {
        var plan = new SvgTileRenderPlan(
            openedDocument,
            metadata.BitmapIdentity,
            metadata.TargetWidthPixels,
            metadata.TargetHeightPixels,
            metadata.RenderGeneration,
            metadata.Demand,
            metadata.HostAdmission,
            metadata.SourceReservationBytes,
            metadata.OwnerSlot,
            metadata.PhysicalViewport);
        int candidateReleased = 0;
        void ReleaseCandidate()
        {
            if (Interlocked.Exchange(ref candidateReleased, 1) != 0)
                return;
            // Registry admission is staged before local publication. If the
            // callback fails or loses its generation fence, this candidate
            // remains unaccepted and owns the slot. Conversely, once the exact
            // plan is visible, later cleanup (including an exception from
            // cache rebinding) must not release the accepted plan's slot.
            bool acceptedPlan = ReferenceEquals(Volatile.Read(ref _svgTileRenderPlan), plan);
            try
            {
                if (!acceptedPlan)
                    RetireUncommittedSvgDocument(openedDocument);
            }
            finally
            {
                try { candidateUse.Dispose(); }
                finally
                {
                    if (!acceptedPlan)
                        metadata.OwnerSlot?.Dispose();
                }
            }
        }

        void Publish()
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            using (MarkdownSvgStageTrace.MeasureFrom(
                metadata.WorkId,
                MarkdownSvgWorkStage.UiDispatchDelay,
                publishRequestedAt != 0 ? publishRequestedAt : metadata.PublishRequestedAt,
                metadata.SourceByteLength,
                metadata.TargetPixels))
            {
            }
            using var publishTrace = MarkdownSvgStageTrace.Measure(
                metadata.WorkId,
                MarkdownSvgWorkStage.UiPublish,
                metadata.SourceByteLength,
                metadata.TargetPixels);
#endif
            try
            {
                bool layoutInvalidated = ShouldInvalidateSvgGeometry(
                    metadata.MetadataWasKnown, _svgIntrinsicSize, metadata.Intrinsic);
                if (!TryCommitSvgPublication(metadata, plan))
                    return;

                RaiseLoadCompleted(layoutInvalidated);
            }
            finally { ReleaseCandidate(); }
        }

        return (Publish, ReleaseCandidate);
    }

    private (Action Publish, Action? OnDropped) CreateBitmapSvgPublication(
        SvgPublicationMetadata metadata,
        SharedCanvasBitmapCache.Lease publishedLease,
        long publishRequestedAt = 0)
    {
        SvgTilePublicationOwnership ownership = default;
        void ReleaseUntransferredLease()
        {
            if (ownership.TryReleaseUntransferred())
                TryImageCleanup(publishedLease.Dispose);
        }

        void Publish()
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            using (MarkdownSvgStageTrace.MeasureFrom(
                metadata.WorkId,
                MarkdownSvgWorkStage.UiDispatchDelay,
                publishRequestedAt != 0 ? publishRequestedAt : metadata.PublishRequestedAt,
                metadata.SourceByteLength,
                metadata.TargetPixels))
            {
            }
            using var publishTrace = MarkdownSvgStageTrace.Measure(
                metadata.WorkId,
                MarkdownSvgWorkStage.UiPublish,
                metadata.SourceByteLength,
                metadata.TargetPixels);
#endif
            bool transferred = false;
            try
            {
                bool layoutInvalidated = ShouldInvalidateSvgGeometry(
                    metadata.MetadataWasKnown, _svgIntrinsicSize, metadata.Intrinsic);
                if (!TryCommitSvgPublication(metadata, plan: null))
                    return;

                if (!string.IsNullOrEmpty(metadata.ResolvedCacheKey))
                {
                    _svgFailures.TryRemove(metadata.SvgFailureCacheKey, out _);
                    _failedUrls.TryRemove(metadata.ResolvedCacheKey, out _);
                }
                transferred = ownership.TryTransfer();
                if (!transferred)
                    return;
                ReplaceBitmap(publishedLease.Bitmap, publishedLease, metadata.PhysicalViewport);
                RaiseLoadCompleted(layoutInvalidated);
            }
            finally
            {
                if (!transferred)
                    ReleaseUntransferredLease();
            }
        }

        return (Publish, ReleaseUntransferredLease);
    }

    internal static bool ShouldInvalidateSvgGeometry(
        bool metadataWasKnown,
        Size previousIntrinsic,
        Size candidateIntrinsic) =>
        // Known bytes are not a promise of unchanged geometry: a supported
        // color-scheme rule or provider environment can alter intrinsic size.
        !metadataWasKnown || !previousIntrinsic.Equals(candidateIntrinsic);

    private int AdvanceSvgRenderGeneration()
    {
        lock (_svgWorkSync)
            return Interlocked.Increment(ref _svgRenderGeneration);
    }

    private bool TryCommitSvgPublication(SvgPublicationMetadata metadata, SvgTileRenderPlan? plan)
    {
        // Provider code and disposal/event callbacks never execute under the
        // work lock. All local generation advances share this commit lock.
        if (!IsSvgProviderCurrent(
                metadata.Renderer,
                metadata.ProviderCacheGeneration,
                metadata.Info.HasText))
            return false;

        object? planAdmissionDomain = null;
        if (plan?.HostAdmission is { } hostAdmission)
        {
            try
            {
                // Admission providers are external code. Read their domain
                // identity before taking the ImageBox work lock.
                planAdmissionDomain = hostAdmission.AdmissionDomain;
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[ImageBox] SVG admission domain could not be observed: {exception.GetType().Name}");
                return false;
            }

            if (plan.AdmissionDomain is null ||
                !ReferenceEquals(plan.AdmissionDomain, planAdmissionDomain))
            {
                return false;
            }
        }

        IMarkdownSvgDocument? previous = null;
        SvgTileRenderPlan? previousPlan = null;
        ImageWorkLease? previousCleanup = null;
        SvgHostAdmissionCoordinator.OwnerSlot? abandonedOwnerSlot = null;
        IMarkdownSvgDocument? abandonedDocument = null;
        ImageWorkLease? abandonedDocumentCleanup = null;
        SvgTileCacheTransition? tileTransition;
        bool localCommitApplied = false;
        bool tileTransitionApplied = false;
        lock (_svgPublicationSync)
        {
            tileTransition = PrepareSvgTileCacheTransition(plan);
            if (tileTransition is null)
                return false;

            try
            {
                // Registry allocation/locking happens outside _svgWorkSync.
                // Until the exact candidate plan is accepted below, its weak
                // registry row cannot be selected for parking because the
                // owner rechecks accepted plan identity.
                if (plan?.OwnerSlot is { } stagedOwnerSlot)
                    stagedOwnerSlot.Commit(this, plan, plan.Demand, plan.SourceReservationBytes);

                lock (_svgWorkSync)
                {
                    if (_disposed || _resourcesSuspended || _imageRetirementFinalizing ||
                        metadata.RenderGeneration != Volatile.Read(ref _svgRenderGeneration) ||
                        !ReferenceEquals(Volatile.Read(ref _svgTileRenderPlan), tileTransition.PreviousPlan) ||
                        Volatile.Read(ref _svgParkingPlan) is not null)
                    {
                        return false;
                    }
                    if (plan is not null)
                    {
                        SvgDocumentUseState candidate = GetSvgDocumentUseStateNoLock(plan.Document);
                        if (candidate.Retired || candidate.DisposalScheduled ||
                            !candidate.CandidateReserved || candidate.ActiveUses <= 0 ||
                            plan.RenderGeneration != metadata.RenderGeneration ||
                            plan.FullWidthPixels != metadata.TargetWidthPixels ||
                            plan.FullHeightPixels != metadata.TargetHeightPixels ||
                            !plan.PhysicalViewport.Equals(metadata.PhysicalViewport ??
                                new MarkdownSvgPhysicalViewport(metadata.TargetWidthPixels, metadata.TargetHeightPixels)) ||
                            !string.Equals(plan.IdentityPrefix, metadata.BitmapIdentity, StringComparison.Ordinal))
                        {
                            return false;
                        }
                        if (plan.HostAdmission is not null &&
                            (plan.OwnerSlot is null || plan.AdmissionDomain is null ||
                             !ReferenceEquals(plan.AdmissionDomain, planAdmissionDomain)))
                        {
                            return false;
                        }
                    }

                    previous = _svgDocument;
                    previousPlan = Volatile.Read(ref _svgTileRenderPlan);
                    IMarkdownSvgDocument? next = plan?.Document;
                    if (previous is not null && !ReferenceEquals(previous, next))
                        previousCleanup = RetireSvgDocumentNoLock(previous);

                    ApplySvgPublicationMetadataNoLock(metadata);
                    Volatile.Write(ref _svgDocument, next);
                    Volatile.Write(ref _svgTileRenderPlan, plan);
                    Volatile.Write(ref _svgParkingPlan, null);
                    _svgUsesTiles = plan is not null;
                    _svgHasRenderedTile = false;
                    _svgPressureParked = false;
                    _svgPressureDemandSignatureKnown = false;
                    localCommitApplied = true;
                }

                tileTransitionApplied = ApplySvgTileCacheTransition(tileTransition);
                if (!tileTransitionApplied && localCommitApplied && plan is not null)
                {
                    lock (_svgWorkSync)
                    {
                        if (ReferenceEquals(Volatile.Read(ref _svgTileRenderPlan), plan))
                        {
                            abandonedOwnerSlot = plan.OwnerSlot;
                            if (ReferenceEquals(Volatile.Read(ref _svgDocument), plan.Document))
                            {
                                abandonedDocument = plan.Document;
                                abandonedDocumentCleanup = RetireSvgDocumentNoLock(plan.Document);
                                Volatile.Write(ref _svgDocument, null);
                            }
                            Volatile.Write(ref _svgTileRenderPlan, null);
                            Volatile.Write(ref _svgParkingPlan, null);
                            _svgUsesTiles = false;
                            _svgHasRenderedTile = false;
                            _svgPressureDemandSignatureKnown = false;
                            _loadStarted = false;
                        }
                    }
                }
            }
            finally
            {
                if (!tileTransitionApplied)
                    AbortSvgTileCacheTransition(tileTransition);
            }
        }

        if (localCommitApplied)
        {
            if (previousPlan is not null && !ReferenceEquals(previousPlan, plan))
                previousPlan.OwnerSlot?.Dispose();

            if (previous is not null && previousCleanup is not null)
                ScheduleSvgDocumentDisposal(previous, previousCleanup);
        }

        abandonedOwnerSlot?.Dispose();
        if (abandonedDocument is not null && abandonedDocumentCleanup is not null)
            ScheduleSvgDocumentDisposal(abandonedDocument, abandonedDocumentCleanup);

        if (!tileTransitionApplied)
        {
            return false;
        }

        RetireSvgTileCacheTransition(tileTransition!);

        return true;
    }

    private void ApplySvgPublicationMetadataNoLock(SvgPublicationMetadata metadata)
    {
        _isSvg = true;
        _svgSourceIdentity = metadata.SourceIdentity;
        _svgSourceByteLength = metadata.SourceByteLength;
        _svgIntrinsicSize = metadata.Intrinsic;
        _svgTitle = metadata.Title;
        _svgDesc = metadata.Description;
        _svgContentHash = metadata.ContentHash;
        _svgSecurityPartitionHash = metadata.SecurityPartitionHash;
        _svgInfo = metadata.Info;
        _svgRendererIdentity = metadata.RendererIdentity;
        _svgProviderCacheGeneration = metadata.ProviderCacheGeneration;
        _svgBitmapWidthPixels = metadata.TargetWidthPixels;
        _svgBitmapHeightPixels = metadata.TargetHeightPixels;
        _svgPhysicalViewport = metadata.PhysicalViewport;
        _svgFailureReason = null;
        _svgFailureDescription = null;
        _loadFailed = false;
    }

    private static string HashSvgSecurityPartition(string? securityPartition) =>
        Utf8Sha256.GetHexString(securityPartition);

    private void RememberSvgSource(byte[] source)
    {
        _svgSourceIdentity = new WeakReference<byte[]>(source);
        _svgSourceByteLength = source.LongLength;
    }

    private bool IsSvgProviderCurrent(
        IMarkdownSvgRenderer renderer,
        long expectedGeneration,
        bool dependsOnFontGeneration)
    {
        try
        {
            return ReferenceEquals(Context.SvgRenderer, renderer) &&
                   (!dependsOnFontGeneration || renderer.CacheGeneration == expectedGeneration);
        }
        catch
        {
            return false;
        }
    }

    private void PublishSvgGenerationInvalidated(int renderGeneration)
    {
        PublishOnUiThread(() =>
        {
            if (_disposed || renderGeneration != Volatile.Read(ref _svgRenderGeneration))
                return;

            _loadStarted = false;
            RaiseLoadCompleted(layoutInvalidated: false);
        });
    }

    internal static async Task<T> AwaitSvgResultAsync<T>(
        ValueTask<T> operation,
        CancellationToken cancellationToken)
        where T : IDisposable
    {
        Task<T> task = operation.AsTask();
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // WaitAsync may report cancellation while the provider task becomes
            // successful immediately afterward. Always attach cleanup, even if
            // the task is already complete; ExecuteSynchronously closes that
            // race and disposes every result that this caller did not receive.
            TaskFaultObservation.ObserveFault(RetireUnclaimedSvgResult(task));
            throw;
        }
    }

    internal static Task RetireUnclaimedSvgResult<T>(Task<T> task)
        where T : IDisposable
        => RetireUnclaimedSvgResult(task, nativeUse: null);

    private static Task RetireUnclaimedSvgResult<T>(Task<T> task, IDisposable? nativeUse)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.ContinueWith(
            static (completed, state) =>
            {
                var lease = (IDisposable?)state;
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        // Keep native cleanup failures on this barrier task so the
                        // owning ImageBox and LayoutSnapshot can fail retirement.
                        completed.Result.Dispose();
                    }
                    else if (completed.IsFaulted)
                    {
                        // Cancellation can win WaitAsync while a provider later
                        // faults. Observe that abandoned failure as well.
                        _ = completed.Exception;
                    }
                }
                finally
                {
                    lease?.Dispose();
                }
            },
            nativeUse,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private SvgDocumentUseState GetSvgDocumentUseStateNoLock(IMarkdownSvgDocument document)
    {
        ConditionalWeakTable<IMarkdownSvgDocument, SvgDocumentUseState> uses =
            _svgDocumentUses ??= new ConditionalWeakTable<IMarkdownSvgDocument, SvgDocumentUseState>();
        if (!uses.TryGetValue(document, out SvgDocumentUseState? state))
        {
            state = new SvgDocumentUseState(document);
            uses.Add(document, state);
        }

        return state;
    }

    private bool TryAdoptSvgDocument(IMarkdownSvgDocument document)
    {
        IMarkdownSvgDocument? previous = null;
        ImageWorkLease? rejectedDocumentCleanup = null;
        ImageWorkLease? previousDocumentCleanup = null;
        bool adopted = false;
        lock (_svgWorkSync)
        {
            if (_disposed || _imageRetirementFinalizing)
            {
                // The terminal drain owns the currently published document.
                // A distinct late-opened handle was never adopted and needs
                // its own asynchronous retirement.
                if (!ReferenceEquals(_svgDocument, document))
                    rejectedDocumentCleanup = RetireSvgDocumentNoLock(document);
            }
            else
            {
                SvgDocumentUseState state = GetSvgDocumentUseStateNoLock(document);
                if (!state.Retired && !state.DisposalScheduled)
                {
                    previous = _svgDocument;
                    if (previous is not null && !ReferenceEquals(previous, document))
                        previousDocumentCleanup = RetireSvgDocumentNoLock(previous);
                    _svgDocument = document;
                    adopted = true;
                }
                else
                {
                    rejectedDocumentCleanup = RetireSvgDocumentNoLock(document);
                }
            }
        }

        if (rejectedDocumentCleanup is not null)
            ScheduleSvgDocumentDisposal(document, rejectedDocumentCleanup);
        if (!adopted)
            return false;

        if (previous is not null && previousDocumentCleanup is not null)
            ScheduleSvgDocumentDisposal(previous, previousDocumentCleanup);
        return true;
    }

    private SvgDocumentUseLease? TryAcquireSvgDocumentUse(IMarkdownSvgDocument document)
    {
        lock (_svgWorkSync)
        {
            if (_disposed || _imageRetirementFinalizing || _resourcesSuspended ||
                !ReferenceEquals(_svgDocument, document))
            {
                return null;
            }

            SvgDocumentUseState state = GetSvgDocumentUseStateNoLock(document);
            if (state.Retired || state.DisposalScheduled)
                return null;

            var lease = new SvgDocumentUseLease(this, state);
            AddImageWorkNoLock();
            state.ActiveUses++;
            return lease;
        }
    }

    private SvgDocumentUseLease? TryAcquireCandidateSvgDocumentUse(
        IMarkdownSvgDocument document,
        out bool ownershipConflict)
    {
        lock (_svgWorkSync)
        {
            SvgDocumentUseState state = GetSvgDocumentUseStateNoLock(document);
            ownershipConflict = state.CandidateReserved || state.Retired || state.DisposalScheduled;
            if (ownershipConflict || _disposed || _resourcesSuspended || _imageRetirementFinalizing)
                return null;

            var lease = new SvgDocumentUseLease(this, state, candidate: true);
            AddImageWorkNoLock();
            state.ActiveUses++;
            state.CandidateReserved = true;
            return lease;
        }
    }

    private void RetireUncommittedSvgDocument(IMarkdownSvgDocument document)
    {
        ImageWorkLease? cleanup = null;
        lock (_svgWorkSync)
        {
            if (!ReferenceEquals(_svgDocument, document))
                cleanup = RetireSvgDocumentNoLock(document);
        }
        if (cleanup is not null)
            ScheduleSvgDocumentDisposal(document, cleanup);
    }

    private SvgDocumentUseLease? TryAcquireNestedSvgDocumentUse(SvgDocumentUseLease parent)
    {
        if (!parent.IsOwnedBy(this))
            return null;

        lock (_svgWorkSync)
        {
            SvgDocumentUseState state = parent.State;
            if (!parent.IsOwnedBy(this) || state.ActiveUses <= 0 || state.DisposalScheduled)
                return null;

            // Parent leases represent already-admitted use, such as an SVG
            // tile queued before provider admission. That in-flight work may
            // continue after the document is retired, but cannot outlive it.
            var lease = new SvgDocumentUseLease(this, state);
            AddImageWorkNoLock();
            state.ActiveUses++;
            return lease;
        }
    }

    private async Task<MarkdownSvgRaster> RenderSvgDocumentAsync(
        IMarkdownSvgDocument document,
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken,
        SvgDocumentUseLease parentUse,
        MarkdownSvgDemandTicket? demand = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SvgDocumentUseLease nativeUse = TryAcquireNestedSvgDocumentUse(parentUse) ??
            throw new OperationCanceledException(cancellationToken);
        ValueTask<MarkdownSvgRaster> operation;
        try
        {
            operation = demand is not null && document is IMarkdownSvgHostDocument hostDocument
                ? hostDocument.RenderForHostAsync(request, demand, cancellationToken)
                : document.RenderAsync(request, cancellationToken);
        }
        catch
        {
            nativeUse.Dispose();
            throw;
        }

        return await AwaitTrackedSvgResultAsync(
                operation,
                cancellationToken,
                nativeUse)
            .ConfigureAwait(false);
    }

    private void ReleaseSvgDocument(IMarkdownSvgDocument document)
    {
        ImageWorkLease? cleanupWork = null;
        lock (_svgWorkSync)
        {
            if (ReferenceEquals(_svgDocument, document))
            {
                cleanupWork = RetireSvgDocumentNoLock(document);
                _svgDocument = null;
            }
        }

        if (cleanupWork is not null)
            ScheduleSvgDocumentDisposal(document, cleanupWork);
    }

    private void DisposeSvgDocument(IMarkdownSvgDocument document)
    {
        try
        {
            document.Dispose();
        }
        catch (Exception exception)
        {
            // Resource release is best-effort for the renderer pipeline, but
            // teardown must not report success when the provider failed to
            // release an adopted native document. The current image-work lease
            // keeps this failure inside the snapshot retirement barrier.
            RecordImageRetirementFailure(exception);
        }
    }

    private void PublishSvgFailure(
        MarkdownSvgException exception,
        string failureCacheKey,
        bool cachePermanently,
        int renderGeneration)
    {
        PublishSvgFailure(
            exception.Reason,
            exception.Message,
            failureCacheKey,
            cachePermanently,
            renderGeneration);
    }

    private void PublishSvgFailure(
        MarkdownSvgFailureReason reason,
        string description,
        string failureCacheKey,
        bool cachePermanently,
        int renderGeneration)
    {
        MarkdownDiagnostics.WriteLine(
            $"[ImageBox] SVG failure {reason}: {description}");
        PublishOnUiThread(() =>
        {
            if (_disposed || renderGeneration != Volatile.Read(ref _svgRenderGeneration))
                return;

            bool hasPreviousGoodBitmap = _bitmap is not null || _svgHasRenderedTile;
            _svgFailureReason = reason;
            _svgFailureDescription = hasPreviousGoodBitmap ? null : description;
            _loadFailed = !hasPreviousGoodBitmap;
            if (_loadFailed && cachePermanently &&
                failureCacheKey.Length is > 0 and <= MaxFailedCacheKeyLength)
            {
                _svgFailures[failureCacheKey] = (reason, TrimSvgMetadata(description));
                TrimCache(_svgFailures, MaxFailedUrlEntrnes);
            }

            if (_loadFailed)
            {
                ReportUnavailable(MarkdownImageUnavailableReason.Unavailable, reason);
                float maxWidth = Math.Max(
                    1f,
                    _availableWidth - (float)(Margin.Left + Margin.Right));
                UpdatePlaceholder(maxWidth, _imageHeight);
            }

            RaiseLoadCompleted(layoutInvalidated: _loadFailed);
        });
    }

    private static string TrimSvgMetadata(string value)
    {
        const int maximumLength = 4096;
        string trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : trimmed[..maximumLength];
    }

    private Size ResolveIntrinsicSize(MarkdownSvgDocumentInfo info, Size fallback)
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

    private SvgRasterPlan PickRasterDimensions(Size intrinsic)
    {
        float naturalWidth = (float)Math.Max(1d, intrinsic.Width);
        float naturalHeight = (float)Math.Max(1d, intrinsic.Height);
        float maxWidth = _isInlineLayout
            ? Math.Max(1f, _availableWidth)
            : Math.Max(1f, _availableWidth - (float)(Margin.Left + Margin.Right));
        if (!float.IsFinite(maxWidth) || _availableWidth <= 0)
            maxWidth = naturalWidth;

        float fit = naturalWidth > maxWidth ? maxWidth / naturalWidth : 1f;
        float displayWidth = Math.Max(1f, naturalWidth * fit);
        float displayHeight = Math.Max(1f, naturalHeight * fit);
        ApplyRequestedSize(ref displayWidth, ref displayHeight, maxWidth);

        double rasterScale = Context.RasterizationScale;
        if (!double.IsFinite(rasterScale) || rasterScale <= 0)
            rasterScale = 1d;

        int width;
        int height;
        try
        {
            width = checked((int)Math.Ceiling(displayWidth * rasterScale));
            height = checked((int)Math.Ceiling(displayHeight * rasterScale));
        }
        catch (OverflowException ex)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The requested SVG raster dimensions exceed the supported range.",
                ex);
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        long outputBytes;
        try
        {
            outputBytes = checked((long)width * height * 4L);
        }
        catch (OverflowException ex)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The requested SVG raster exceeds the output budget.",
                ex);
        }

        return (
            width,
            height,
            outputBytes > (Context.PerformanceSession?.Options.ReleaseOffscreenImageResources != true ? MaxSvgOutputRasterBytes :
                Math.Min(MaxSvgOutputRasterBytes, BitmapOutputAdmission.Shared.MaximumSingleOutputBytes)) ||
                width > MaxUntiledSvgDimensionPixels ||
                height > MaxUntiledSvgDimensionPixels ||
                width > Context.ResourceCreator.Device.MaximumBitmapSizeInPixels ||
                height > Context.ResourceCreator.Device.MaximumBitmapSizeInPixels,
            new MarkdownSvgPhysicalViewport(displayWidth * rasterScale, displayHeight * rasterScale));
    }

    private string BuildSvgBitmapIdentity(
        string contentHash,
        string securityPartitionHash,
        MarkdownSvgDocumentInfo info,
        int width,
        int height,
        MarkdownSvgColorScheme colorScheme,
        MarkdownSvgColor semanticColor,
        long rendererIdentity,
        long providerCacheGeneration,
        MarkdownSvgPhysicalViewport physicalViewport)
    {
        var identity = new StringBuilder(192);
        identity.Append("svg:v3:renderer=")
            .Append(rendererIdentity)
            .Append(':')
            .Append(securityPartitionHash)
            .Append(':')
            .Append(contentHash)
            .Append(':')
            .Append(width)
            .Append('x')
            .Append(height)
            .Append(":viewport=")
            .Append(BitConverter.DoubleToInt64Bits(physicalViewport.Width).ToString("X16", CultureInfo.InvariantCulture))
            .Append('x')
            .Append(BitConverter.DoubleToInt64Bits(physicalViewport.Height).ToString("X16", CultureInfo.InvariantCulture));

        if (info.HasText)
        {
            identity.Append(":generation=")
                .Append(providerCacheGeneration)
                .Append(":locale=")
                .Append(Context.Language);
        }
        if (info.UsesColorScheme)
            identity.Append(":scheme=").Append((int)colorScheme);
        if (info.UsesCurrentColor)
        {
            identity.Append(":color=")
                .Append(semanticColor.Alpha.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))
                .Append(semanticColor.Red.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))
                .Append(semanticColor.Green.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))
                .Append(semanticColor.Blue.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return identity.ToString();
    }

    private static long GetSvgRendererIdentity(IMarkdownSvgRenderer renderer) =>
        _svgRendererIdentities.GetValue(
            renderer,
            static _ => new SvgRendererCacheIdentity(
                Interlocked.Increment(ref _nextSvgRendererIdentity))).Value;

    private static string BuildSvgContentCacheKey(
        string contentHash,
        string securityPartitionHash,
        long rendererIdentity,
        long providerCacheGeneration,
        string language,
        MarkdownSvgColorScheme colorScheme,
        MarkdownSvgColor semanticColor) =>
        $"svg-content:v1:renderer={rendererIdentity}:generation={providerCacheGeneration}:" +
        $"partition={securityPartitionHash}:content={contentHash}:locale={language}:" +
        $"scheme={(int)colorScheme}:color=" +
        semanticColor.Alpha.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) +
        semanticColor.Red.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) +
        semanticColor.Green.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) +
        semanticColor.Blue.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

    private static string BuildSvgFailureCacheKey(
        string contentHash,
        string securityPartitionHash,
        long rendererIdentity,
        long providerCacheGeneration) =>
        $"svg-failure:v1:renderer={rendererIdentity}:generation={providerCacheGeneration}:" +
        $"{securityPartitionHash}:{contentHash}";

    private bool IsSvgCacheEntryCurrent(SvgCacheEntry entry)
    {
        try
        {
            return Context.SvgRenderer is { } renderer &&
                   entry.RendererIdentity == GetSvgRendererIdentity(renderer) &&
                   (!entry.Info.HasText ||
                    entry.ProviderCacheGeneration == renderer.CacheGeneration);
        }
        catch
        {
            return false;
        }
    }

    private bool IsCurrentSvgRendererState()
    {
        try
        {
            return Context.SvgRenderer is { } renderer &&
                   GetSvgRendererIdentity(renderer) == _svgRendererIdentity &&
                   (_svgInfo?.HasText != true ||
                    renderer.CacheGeneration == _svgProviderCacheGeneration);
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateSvgRaster(
        MarkdownSvgRaster raster,
        int expectedWidth,
        int expectedHeight)
    {
        if (raster.WidthPixels != expectedWidth ||
            raster.HeightPixels != expectedHeight)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned unexpected raster dimensions.");
        }

        int minimumStride = checked(expectedWidth * 4);
        int minimumLength = checked(raster.StrideBytes * expectedHeight);
        if (raster.StrideBytes < minimumStride ||
            raster.LengthBytes < minimumLength ||
            raster.Pixels.Length < minimumLength)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned an invalid raster buffer.");
        }
    }

    private CanvasBitmap CreateBitmapFromSvgRaster(
        MarkdownSvgRaster raster
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long diagnosticWorkId,
        long diagnosticSourceBytes = 0,
        long diagnosticTargetPixels = 0
#endif
        , BitmapOutputAdmission.Reservation? bitmapReservation = null
    )
    {
        int tightStride = checked(raster.WidthPixels * 4);
        int tightLength = checked(tightStride * raster.HeightPixels);
        ReadOnlyMemory<byte> pixels = raster.Pixels;
        DirectXPixelFormat format = raster.PixelFormat switch
        {
            MarkdownSvgPixelFormat.Rgba8Premultiplied =>
                DirectXPixelFormat.R8G8B8A8UIntNormalized,
            MarkdownSvgPixelFormat.Bgra8Premultiplied =>
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
            _ => throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned an unknown pixel format."),
        };
        byte[]? rented = null;
        byte[] bytes;
        try
        {
        if (raster.StrideBytes == tightStride &&
            MemoryMarshal.TryGetArray(pixels, out ArraySegment<byte> segment) &&
            segment.Offset == 0 &&
            segment.Array is { } array &&
            segment.Count >= tightLength)
        {
            bytes = array;
        }
        else
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            using var bufferCopyTrace = MarkdownSvgStageTrace.Measure(
                diagnosticWorkId,
                MarkdownSvgWorkStage.SvgBitmapBufferCopy,
                diagnosticSourceBytes,
                diagnosticTargetPixels);
#endif
            if (tightLength <= MaxPooledSvgUploadBytes)
            {
                rented = ArrayPool<byte>.Shared.Rent(tightLength);
                bytes = rented;
            }
            else
            {
                // Avoid allowing the shared array pool to retain a second,
                // unbudgeted large raster after the provider's shared-memory
                // buffer has been uploaded and released.
                bytes = GC.AllocateUninitializedArray<byte>(tightLength);
            }
            ReadOnlySpan<byte> source = pixels.Span;
            for (int row = 0; row < raster.HeightPixels; row++)
            {
                source.Slice(row * raster.StrideBytes, tightStride)
                    .CopyTo(bytes.AsSpan(row * tightStride, tightStride));
            }
        }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
            using var createBitmapTrace = MarkdownSvgStageTrace.Measure(
                diagnosticWorkId,
                MarkdownSvgWorkStage.SvgCanvasBitmapCreateFromBytes,
                diagnosticSourceBytes,
                diagnosticTargetPixels);
#endif
            if (bitmapReservation is not null && !bitmapReservation.Commit())
                throw new InvalidOperationException("The SVG output reservation is no longer active.");
            return CanvasBitmap.CreateFromBytes(
                Context.ResourceCreator,
                bytes,
                raster.WidthPixels,
                raster.HeightPixels,
                format);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private MarkdownSvgColor GetSemanticSvgColor()
    {
        Color foreground = Context.ThemeSnapshot
            .GetStyle(MarkdownElementKeys.Body)
            .Foreground;
        return new MarkdownSvgColor(
            foreground.R,
            foreground.G,
            foreground.B,
            foreground.A);
    }

    private MarkdownSvgColorScheme GetSvgColorScheme() =>
        Context.ThemeSnapshot.IsDark
            ? MarkdownSvgColorScheme.Dark
            : MarkdownSvgColorScheme.Light;

    private void PublishFailure(string cacheKey, bool cachePermanently = true)
    {
        PublishOnUiThread(() =>
        {
            if (_disposed) return;
            _loadFailed = true;
            if (cachePermanently && !string.IsNullOrEmpty(cacheKey))
            {
                RememberFailedUrl(cacheKey);
            }

            // Block images and explicitly-sized inline images preserve their geometry. A
            // compact inline image without requested dimensions expands once to show its alt
            // text; the host relayouts the committed snapshot and does not recreate or retry it.
            float maxWidth = Math.Max(
                1f,
                _availableWidth - (float)(Margin.Left + Margin.Right));
            UpdatePlaceholder(maxWidth, _imageHeight);
            RaiseLoadCompleted(
                layoutInvalidated: ShouldExpandInlineFailure ||
                    ShouldCollapseFailedDecorativeImage);
        });
    }

    private void CollapseFailedDecorativeImage(float availableWidth)
    {
        ReplacePlaceholder(null, null);
        ReplaceCaption(null, null, 0f);
        _imageWidth = 0;
        _imageHeight = 0;
        Bounds = new Rect(0, 0, Math.Max(0, availableWidth), 0);
    }

    private void PaintPlaceholder(CanvasDrawingSession ds, Rect rect)
    {
        var snapshot = Context.ThemeSnapshot;
        var body = snapshot.GetStyle(MarkdownElementKeys.Body);
        var caption = snapshot.GetStyle(MarkdownElementKeys.ImageCaption);
        Color fill = snapshot.IsHighContrast
            ? snapshot.SurfaceColor
            : Color.FromArgb(0x0F, body.Foreground.R, body.Foreground.G, body.Foreground.B);
        Color border = snapshot.IsHighContrast
            ? snapshot.FocusVisualColor
            : Color.FromArgb(0x52, caption.Foreground.R, caption.Foreground.G, caption.Foreground.B);
        const float radius = 4f;
        ds.FillRoundedRectangle(rect, radius, radius, fill);
        ds.DrawRoundedRectangle(rect, radius, radius, border, 1f);

        bool compactInlineFailure = _isInlineLayout && _loadFailed;
        if (_placeholder is null ||
            (!compactInlineFailure && (rect.Width < 96 || rect.Height < 32)))
        {
            return;
        }

        float horizontalPadding = compactInlineFailure ? 8f : 12f;
        float verticalPadding = compactInlineFailure ? 0f : 8f;
        NativePaintSessionResources.LayerLease clip = NativePaintSessionResources.CreateLayer(ds, 1f, rect);
        try
        {
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(
                ds,
                _placeholder,
                (float)rect.X + Math.Min(horizontalPadding, (float)rect.Width / 4f),
                (float)rect.Y + Math.Min(verticalPadding, (float)rect.Height / 4f),
                caption.Foreground);
        }
        catch (Exception primary)
        {
            clip.ClosePreservingPrimary(primary);
            throw;
        }
        clip.Close();
    }

    private void UpdatePlaceholder(float maxWidth, float maxHeight = float.MaxValue)
    {
        var style = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
        bool compactInlineFailure = _isInlineLayout && _loadFailed;
        using SharedCanvasTextFormatCache.Lease formatLease = SharedCanvasTextFormatCache.Acquire(
            style,
            compactInlineFailure ? CanvasWordWrapping.NoWrap : CanvasWordWrapping.Wrap,
            CanvasHorizontalAlignment.Center,
            Context.FlowDirection,
            Context.Language,
            true,
            CanvasVerticalAlignment.Center);
        string description = compactInlineFailure
            ? GetInlineFailureText()
            : string.IsNullOrWhiteSpace(Alt) ? Url : Alt;
        const int maxDescriptionLength = 240;
        if (description.Length > maxDescriptionLength)
        {
            description = description[..maxDescriptionLength] + "...";
        }

        string text = compactInlineFailure
            ? description
            : _loadFailed
                ? ResolveImageStatus(
                    MarkdownStringKeys.ImageError,
                    MarkdownLocalizedStrings.ImageErrorFormat,
                    description)
                : ResolveImageStatus(
                    MarkdownStringKeys.ImageLoading,
                    MarkdownLocalizedStrings.ImageLoadingFormat,
                    description);
        float horizontalPadding = compactInlineFailure ? 16f : 24f;
        float verticalPadding = compactInlineFailure ? 0f : 16f;
        float layoutWidth = Math.Max(1f, maxWidth - Math.Min(horizontalPadding, maxWidth / 2f));
        float layoutHeight = float.IsFinite(maxHeight)
            ? Math.Max(1f, maxHeight - Math.Min(verticalPadding, maxHeight / 2f))
            : float.MaxValue;
        CanvasTextLayout replacement = CreateOwnedTextLayout(
            text,
            formatLease.Format,
            layoutWidth,
            layoutHeight,
            out NativeResourceRetirementRecord replacementRecord);
        ReplacePlaceholder(replacement, replacementRecord);
    }

    private bool ShouldExpandInlineFailure =>
        _isInlineLayout &&
        _loadFailed &&
        _requestedWidth is null &&
        _requestedHeight is null;

    private bool ShouldCollapseFailedDecorativeImage =>
        _loadFailed &&
        string.IsNullOrWhiteSpace(Alt) &&
        _requestedWidth is null &&
        _requestedHeight is null;

    private float MeasureInlineFailureWidth(float maxWidth, float height)
    {
        var style = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
        using SharedCanvasTextFormatCache.Lease formatLease = SharedCanvasTextFormatCache.Acquire(
            style,
            CanvasWordWrapping.NoWrap,
            CanvasHorizontalAlignment.Left,
            Context.FlowDirection,
            Context.Language,
            true,
            CanvasVerticalAlignment.Top);
        NativeResourceRetirementRecord retirementRecord = ReserveNativeLayoutRetirementRecord();
        CanvasTextLayout? layout = null;
        float measuredWidth;
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            layout = CanvasTextLayoutFactory.Create(
                Context.ResourceCreator,
                GetInlineFailureText(),
                formatLease.Format,
                Math.Max(maxWidth, 4096f),
                height,
                Context.LayoutDiagnostics);
#else
            layout = new CanvasTextLayout(
                Context.ResourceCreator,
                GetInlineFailureText(),
                formatLease.Format,
                Math.Max(maxWidth, 4096f),
                height);
#endif
            retirementRecord.SetTemporary(0, layout);
            const float horizontalPadding = 16f;
            float textWidth = (float)Math.Max(layout.LayoutBounds.Width, layout.DrawBounds.Width);
            measuredWidth = Math.Clamp(MathF.Ceiling(textWidth) + horizontalPadding, height, maxWidth);
        }
        catch
        {
            if (layout is not null)
                retirementRecord.SetTemporary(0, layout);
            // Temporary measurement resources are terminal on both success
            // and failure, so a later native Dispose failure is retried by
            // the same source-free custody path as persistent placements.
            retirementRecord.Publish();
            throw;
        }

        try
        {
            retirementRecord.Disposer(layout!);
            retirementRecord.SetTemporary(0, null);
            retirementRecord.Publish();
        }
        catch
        {
            retirementRecord.Publish();
            throw;
        }

        return measuredWidth;
    }

    private string GetInlineFailureText()
    {
        string description = string.IsNullOrWhiteSpace(Alt)
            ? Context.ResolveString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName)
            : Alt.Trim();
        description = ResolveImageStatus(
            MarkdownStringKeys.ImageError,
            MarkdownLocalizedStrings.ImageErrorFormat,
            description);
        const int maxLength = 80;
        return description.Length <= maxLength
            ? description
            : description[..(maxLength - 3)] + "...";
    }

    private string ResolveImageStatus(string key, string fallbackFormat, string description)
        => Context.ResolveFormattedString(key, fallbackFormat, description);

    /// <summary>Runs <paramref name="publish"/> on the UI dispatcher when one is
    /// configured and we are off-thread; otherwise inline. The inline fallback
    /// is used by headless hosts and remains guarded by the resource-specific
    /// publication and disposal synchronization.</summary>
    internal void PublishOnUiThread(
        Action publish,
        Action? onDropped = null,
        Func<Action, bool>? tryEnqueueOverride = null)
    {
        if (!TryBeginImageWork(out ImageWorkLease? publicationWork) || publicationWork is null)
        {
            InvokeDroppedCleanup(onDropped);
            return;
        }

        // 0=pending, 1=callback running, 2=delivered, 3=dropped. The lease is
        // released by exactly one accepted callback or rejection path. If a
        // dispatcher accepts work and silently loses it, the lease deliberately
        // remains outstanding so runtime drain timeout can fail closed.
        int deliveryState = 0;

        void Drop(int expectedState)
        {
            if (Interlocked.CompareExchange(ref deliveryState, 3, expectedState) != expectedState)
                return;

            InvokeDroppedCleanup(onDropped);
        }

        void InvokeDroppedCleanup(Action? cleanup)
        {
            try
            {
                cleanup?.Invoke();
            }
            catch (Exception exception)
            {
                RecordImageRetirementFailure(exception);
                throw;
            }
        }

        void SafePublish()
        {
            if (Interlocked.CompareExchange(ref deliveryState, 1, 0) != 0)
                return;

            try
            {
                if (_disposed || _resourcesSuspended || publicationWork.Token.IsCancellationRequested ||
                    Context.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true })
                {
                    Drop(expectedState: 1);
                    return;
                }

                publish();
                Interlocked.CompareExchange(ref deliveryState, 2, 1);
            }
            catch (Exception ex) when (_disposed || GraphicsDeviceErrors.IsShutdownOrDisposed(ex))
            {
                MarkdownDiagnostics.WriteLine(
                    $"[ImageBox] ignored image publish during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
                Drop(expectedState: 1);
            }
            catch
            {
                Interlocked.CompareExchange(ref deliveryState, 2, 1);
                throw;
            }
            finally
            {
                publicationWork.Dispose();
            }
        }

        var dispatcher = Context.Dispatcher;
        if (tryEnqueueOverride is not null ||
            (dispatcher is not null && !dispatcher.HasThreadAccess))
        {
            bool accepted;
            try
            {
                accepted = tryEnqueueOverride is null
                    ? dispatcher!.TryEnqueue(SafePublish)
                    : tryEnqueueOverride(SafePublish);
            }
            catch
            {
                try { Drop(expectedState: 0); }
                finally { publicationWork.Dispose(); }
                throw;
            }

            if (!accepted)
            {
                try { Drop(expectedState: 0); }
                finally { publicationWork.Dispose(); }
            }
        }
        else
        {
            SafePublish();
        }
    }

    private void ReplaceBitmap(CanvasBitmap bitmap, SharedCanvasBitmapCache.Lease lease,
        MarkdownSvgPhysicalViewport? physicalViewport = null)
    {
        SharedCanvasBitmapCache.Lease? previousLease = _bitmapLease;

        if (!_isSvg)
            _rasterIntrinsicSize = lease.IntrinsicSize ?? bitmap.Size;
        _hasPresentedImage = true;
        _bitmap = bitmap;
        _bitmapLease = lease;
        _bitmapPhysicalViewport = physicalViewport;

        if (previousLease is { } retiredLease)
            TryImageCleanup(retiredLease.Dispose);
    }

    private void ReleaseBitmap()
    {
        SharedCanvasBitmapCache.Lease? lease = _bitmapLease;

        _bitmap = null;
        _bitmapLease = null;
        _bitmapPhysicalViewport = null;

        if (lease is { } retiredLease)
            TryImageCleanup(retiredLease.Dispose);
    }

    private static long GetSvgCacheEntryWeight(SvgCacheEntry entry)
    {
        long weight = 512L + entry.RawBytes.LongLength;
        weight += (entry.Title?.Length ?? 0) * sizeof(char);
        weight += (entry.Desc?.Length ?? 0) * sizeof(char);
        weight += entry.ContentHash.Length * sizeof(char);
        weight += entry.SecurityPartitionHash.Length * sizeof(char);
        weight += (entry.LastBitmapIdentity?.Length ?? 0) * sizeof(char);

        return Math.Max(1L, weight);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordRasterPreparationStage(
        IMarkdownPerformanceSessionInternal? session,
        long preparationId,
        MarkdownRasterPreparationStage stage,
        long startedAt)
    {
        if (preparationId != 0)
            session!.RecordRasterPreparationStage(
                preparationId, stage, Stopwatch.GetTimestamp() - startedAt);
    }

    /// <summary>
    /// Drops cache ownership for every decoded bitmap created on a lost Win2D
    /// device. Live boxes keep their leases only until the recovery rebuild
    /// disposes them, at which point the invalid resources are released.
    /// </summary>
    internal static void ReleaseDeviceResources(CanvasDevice device) =>
        SharedCanvasBitmapCache.ReleaseDevice(device);

    /// <summary>Drops device-independent SVG source metadata under system
    /// memory pressure. Live boxes retain the source required by their active
    /// document handles; future boxes can repopulate this bounded cache.</summary>
    internal static void TrimSharedCachesForMemoryPressure() => _svgCache.Clear();

    /// <summary>Test hook: clears the static failed-URL latch and SVG cache
    /// so tests don't uollute each other.</summary>
    internal static void ResetFanlpreLatchForTests()
    {
        _failedUrls.Clear();
        _svgFailures.Clear();
        _svgCache.Clear();
        SharedCanvasBitmapCache.Clear();
    }
}
