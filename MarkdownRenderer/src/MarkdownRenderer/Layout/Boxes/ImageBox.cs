using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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
internal sealed partial class ImageBox : BlockBox, ISelectionBackgroundGeometrySource, IImageBitmapOutputHost, ISvgTileGapFill, IImageMeasureHost, ISvgHostDeferralHost,
    IImageSourceLoadHost
{
    // Selected artwork keeps the same translucent tint as Math and
    // Mermaid scenes instead of an opaque block-wide band.
    private const float SelectionArtworkBackgroundOpacity = 0.25f;

    private const long MaxSvgOutputRasterBytes = 64L * 1024 * 1024;
    // Direct2D bitmap limits vary by feature level and a recovering/WARP
    // device can transiently report a larger capability than it can allocate.
    // Keep the untiled path within the Windows-wide guaranteed ceiling so the
    // decision is deterministic across hardware, WARP, and CI machines.
    private const int MaxUntiledSvgDimensionPixels = 16_384;
    private const int MaxPooledSvgUploadBytes = 1024 * 1024;
    internal const int SvgTileSizePixels = 1024;

    internal readonly MarkdownLayoutContext Context;
    // Caches, budgets and gates shared with the scope's boxes; clones keep their source's.
    private readonly ImageResourceScope _resources;

    // The bitmap-output owners this box composes, bound to its scope.
    internal ImageBitmapOutputDeferral OutputDeferral { get; }
    internal ImageBitmapOutputAdmission OutputAdmission { get; }
    private readonly SvgHostLoadDeferral _svgHostDeferral;
    internal SvgHostLoadDeferral SvgHostDeferral => _svgHostDeferral;
    // The load pipeline's owner, bound to this box and its scope.
    private readonly ImageSourceLoader _sourceLoader;
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
    private MarkdownSvgRenderDemand? _svgLoadDemandTicket;
    private int _svgLoadDemandGeneration;
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
    private volatile bool _loadStarted;
    private bool _loadFailed;
    private int _unavailableReported;
    private volatile bool _disposed;
    // Measured geometry and the placeholder/caption layouts, under their own lock.
    private readonly ImageMeasureCustody _measure;
    // Accessibility metadata extracted from the SVG root, set after a successful
    // load. Null when the SVG is missing <title>/<desc>, the asset is a bitmap,
    // or extraction failed.
    private string? _svgTitle;
    private string? _svgDesc;

    /// <summary>Raised when the asset finishes loading and a repaint is required.
    /// The event arg's <see cref="LoadCompletedEventArgs.LayoutInvalidated"/>
    /// indicates whether the host must re-run layout (intrinsic size may have
    /// changed) or merely repaint. Always raised on the UI thread.</summary>
    public event EventHandler<LoadCompletedEventArgs>? LoadCompleted;

    private void RaiseLoadCompleted(bool layoutInvalidated)
    {
        ReportUnreportedFailure();
        LoadCompleted?.Invoke(this, LoadCompletedEventArgs.For(layoutInvalidated));
    }

    private void ReportUnreportedFailure()
    {
        // Every published error placeholder must reach the host, including a
        // decoder failure after successful resolution and a cached failure.
        // Keep an earlier policy/SVG-specific reason; do not report it twice.
        if (_loadFailed && !_disposed && !_resourcesSuspended &&
            Interlocked.CompareExchange(ref _unavailableReported, 1, 0) == 0)
        {
            Context.ImageUnavailable?.Invoke(Url, MarkdownImageUnavailableReason.Unavailable, _svgFailureReason);
        }
    }

    public CanvasHorizontalAlignment ContentAlignment { get; set; } = CanvasHorizontalAlignment.Left;

    // A promoted Markdown image still participates in its paragraph's text
    // line box. Kept zero for standalone/HTML inline images, whose owner lays
    // out the line itself.
    internal float MinimumParagraphLineHeight { get; set; }

    public ImageBox(
        MarkdownLayoutContext context, ImageResourceScope resources,
        string url,
        string alt,
        SafeHtmlLength? requestedWidth = null,
        SafeHtmlLength? requestedHeight = null)
        : this(context, resources, url, alt, requestedWidth, requestedHeight, NativeResourceRetirement.Shared, null, null)
    {
    }

    internal ImageBox(
        MarkdownLayoutContext context, ImageResourceScope resources,
        string url,
        string alt,
        NativeResourceRetirement nativeRetirement,
        Action<IDisposable>? nativeDisposer = null,
        Action<CanvasTextLayout>? nativeLayoutOptionsSetter = null)
        : this(context, resources, url, alt, null, null, nativeRetirement, nativeDisposer, nativeLayoutOptionsSetter)
    {
    }

    internal ImageBox(
        MarkdownLayoutContext context, ImageResourceScope resources,
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
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _measure = new ImageMeasureCustody(
            this, context, nativeRetirement, nativeDisposer, nativeLayoutOptionsSetter);
        _svgHostDeferral = new SvgHostLoadDeferral(this, _resources);
        Url = url ?? string.Empty;
        Alt = alt ?? string.Empty;
        _requestedWidth = requestedWidth;
        _requestedHeight = requestedHeight;
        _imageLifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            Context.ImageCancellationToken);
        (OutputDeferral, OutputAdmission) = _resources.CreateBitmapOutput(this, Context, _imageLifetimeCancellation);
        _sourceLoader = CreateSourceLoader();
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

        if (_preResolutionCacheKey is { } cacheKey && _resources.Failures.HasFailedUrl(cacheKey))
        {
            // Preserve transport/bitmap failure state across rebuilds. SVG
            // provider failures use a separate renderer-and-generation-scoped
            // cache after the bytes have been securely preflighted.
            _loadFailed = true;
            _failureCarryable = true;
            _loadStarted = true;
            return;
        }

        // A rebuild of the same document keeps this image's completed
        // result, so it neither reports Loading nor reserves a placeholder size
        // while its pixels are reacquired. Bitmaps are never carried.
        if (Context.CompletedImageCarry is { } completedImages &&
            completedImages.TryGetValue(Url, out CompletedImageCarry? carried))
        {
            if (carried.Failed)
            {
                // Show the same permanent failure as the earlier
                // layout instead of reporting Loading until it fails again.
                _isSvg = carried.IsSvg;
                if (carried.IsSvg)
                    _svgIntrinsicSize = carried.IntrinsicSize;
                else
                    _rasterIntrinsicSize = carried.IntrinsicSize;
                _svgFailureReason = carried.SvgFailureReason;
                _svgFailureDescription = carried.SvgFailureDescription;
                _loadFailed = true;
                _loadStarted = true;
                _failureCarryable = true;
                return;
            }

            if (carried.HasUsableIntrinsicSize)
                _carriedCompletion = carried;
        }

        if (_preResolutionCacheKey is { } bitmapCacheKey && !_isSvg &&
            OutputAdmission.TryAcquireSourceSizedRasterBitmap(bitmapCacheKey, out var cachedLease) &&
            cachedLease is not null)
        {
            ReplaceBitmap(cachedLease.Bitmap, cachedLease);
            _loadStarted = true;
            return;
        }

        if (_preResolutionCacheKey is { } svgCacheKey && _resources.SvgSources.TryGetValue(svgCacheKey, out _))
            _isSvg = true;

        if (!_isSvg)
            return;

        // SVG cache hit path. If the cached rasterized bitmap was produced
        // with the current theme color + device pixel scale, materialize it
        // synchronously so the very first paint shows the image — no async
        // pass through ProcessCachedSvgAsync, no placeholder flash.
        if (_preResolutionCacheKey is { } cachedSvgKey && _resources.SvgSources.TryGetValue(cachedSvgKey, out var entry))
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
                _resources.BitmapCache.TryAcquire(
                    Context.ResourceCreator.Device,
                    previousIdentity,
                    out SharedCanvasBitmapCache.Lease? previousLease) &&
                previousLease is not null)
            {
                if (OutputAdmission.TryAdmitCachedBitmapOutput(previousLease))
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

    /// <summary>SVG &lt;title&gt; element value, or null. Used by the automation
    /// peer as the accessible name when richer than alt.</summary>
    public string? SvgTitle => _svgTitle ?? _carriedCompletion?.SvgTitle;

    /// <summary>SVG &lt;desc&gt; element value, or null. Used by the automation
    /// peer as <c>HelpText</c> for screen-reader description.</summary>
    public string? SvgDesc => _bitmap is null
        ? _svgFailureDescription ?? _svgDesc ?? _carriedCompletion?.SvgDescription
        : _svgDesc;

    /// <summary>Typed provider failure for diagnostics and focused tests.</summary>
    internal MarkdownSvgFailureReason? SvgFailureReason => _svgFailureReason;

    /// <summary>Physical SVG raster dimensions currently presented by this box.</summary>
    internal (int Width, int Height) SvgRasterPixelSize =>
        (_svgBitmapWidthPixels, _svgBitmapHeightPixels);

    /// <summary>True when this box is using bounded visible SVG tiles.</summary>
    internal bool UsesSvgTilesForAutomation => _svgUsesTiles;

    /// <summary>One read of the admitted image work, under the image-work lock.</summary>
    internal ImageWorkState GetWorkState()
    {
        lock (_svgWorkSync)
            return new ImageWorkState(_svgWorkCount, _svgCancellationDisposed);
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
            !OutputDeferral.GetState().IsPristine)
        {
            return false;
        }

        ImageBox? idlePlacement = null;
        try
        {
            lock (_svgWorkSync)
            {
                lock (SvgHostDeferral.Sync)
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
                !OutputDeferral.GetState().IsPristine ||
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
            !OutputDeferral.GetState().IsPristine)
        {
            return false;
        }

        lock (_svgWorkSync)
        {
            lock (SvgHostDeferral.Sync)
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
            !OutputDeferral.GetState().IsPristine)
        {
            return false;
        }

        lock (_svgWorkSync)
        {
            lock (SvgHostDeferral.Sync)
            {
                return IsUnstartedImagePlacementEligibleNoLock(expectedLoadDemandGeneration);
            }
        }
    }

    private bool IsUnstartedImagePlacementEligibleNoLock(int expectedGeneration)
    {
        // Caller holds _svgWorkSync then the SVG host deferral lock, the same order
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

    /// <inheritdoc cref="ImageBitmapOutputDeferral.NeedsBitmapOutputRecovery"/>
    internal bool NeedsBitmapOutputRecovery => OutputDeferral.NeedsBitmapOutputRecovery;

    /// <inheritdoc cref="ImageBitmapOutputDeferral.ObserveDeferredBitmapOutput"/>
    internal bool ObserveBitmapOutputRecovery(bool isVisible) => OutputDeferral.ObserveDeferredBitmapOutput(isVisible);

    MarkdownSvgRenderDemand? IImageBitmapOutputHost.LoadDemand => Volatile.Read(ref _svgLoadDemandTicket);
    int IImageBitmapOutputHost.LoadDemandGeneration => Volatile.Read(ref _svgLoadDemandGeneration);
    int IImageBitmapOutputHost.SvgRenderGeneration => Volatile.Read(ref _svgRenderGeneration);
    bool IImageBitmapOutputHost.IsViewportVisible => Volatile.Read(ref _svgCurrentViewportVisible) == 1;
    bool IImageBitmapOutputHost.IsRetired => _disposed || _resourcesSuspended;

    bool IImageBitmapOutputHost.IsImageWorkDrained
    {
        get { lock (_svgWorkSync) return _svgWorkCount == 0 && !_disposed && !_resourcesSuspended; }
    }

    bool IImageBitmapOutputHost.HasNoImageWork
    {
        get { lock (_svgWorkSync) return _svgWorkCount == 0; }
    }

    bool IImageBitmapOutputHost.HasRetainedBitmapOutput(bool existingTileOutput)
    {
        if (Volatile.Read(ref _bitmap) is not null ||
            Volatile.Read(ref _bitmapLease) is not null)
        {
            return true;
        }

        lock (_svgTileLock)
            return (_svgFallbackTiles?.Count ?? 0) > 0 ||
                (!existingTileOutput && _svgTiles.Count > 0);
    }

    void IImageBitmapOutputHost.RecordImageRetirementFailure(Exception exception) =>
        RecordImageRetirementFailure(exception);
    void IImageBitmapOutputHost.TryImageCleanup(Action cleanup) => TryImageCleanup(cleanup);
    void IImageBitmapOutputHost.RaiseLoadCompleted(bool layoutInvalidated) => RaiseLoadCompleted(layoutInvalidated);

    void IImageBitmapOutputHost.RaiseBitmapOutputRecoverySignal() =>
        LoadCompleted?.Invoke(this, LoadCompletedEventArgs.ForBitmapOutputRecoverySignal());

    void IImageBitmapOutputHost.StartLoad(
        bool isVisible, MarkdownSvgRenderDemand? demandOverride, int loadDemandGeneration) =>
        StartLoad(isVisible, demandOverride, loadDemandGeneration);

    bool IImageBitmapOutputHost.TryResetLoad(MarkdownSvgRenderDemand demand, int generation, int svgGeneration)
    {
        lock (SvgHostDeferral.Sync)
        {
            if (_disposed || _resourcesSuspended ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand) ||
                generation != Volatile.Read(ref _svgLoadDemandGeneration) ||
                svgGeneration != Volatile.Read(ref _svgRenderGeneration))
            {
                return false;
            }

            _loadStarted = false;
            return true;
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
           !OutputDeferral.IsBitmapOutputDeferred &&
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

        if (!Monitor.TryEnter(_measure.Sync))
            return false;

        try
        {
            if (_disposed || _resourcesSuspended || _loadFailed || !_loadStarted ||
                _imageLifetimeCancellation.IsCancellationRequested || _measure.CheckpointActive ||
                _isSvg || _bitmap is null)
            {
                return false;
            }

            if (_requestedWidth is not { IsPercent: false } width ||
                !float.IsFinite(width.Value) || width.Value <= 0 ||
                _requestedHeight is not { IsPercent: false } height ||
                !float.IsFinite(height.Value) || height.Value <= 0 ||
                !float.IsFinite(_measure.AvailableWidth) || _measure.AvailableWidth <= 0)
            {
                return false;
            }

            float available = Math.Max(
                1f,
                _measure.IsInlineLayout
                    ? _measure.AvailableWidth
                    : _measure.AvailableWidth - (float)(Margin.Left + Margin.Right));
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
                   _measure.ImageWidth == expectedWidth && _measure.ImageHeight == expectedHeight &&
                   (!_measure.IsInlineLayout ||
                       (Bounds.Width == expectedWidth && Bounds.Height == expectedHeight));
        }
        finally
        {
            Monitor.Exit(_measure.Sync);
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
        _resources = source._resources;
        _measure = source._measure.CreateForClone(this, context);
        _svgHostDeferral = new SvgHostLoadDeferral(this, _resources);
        Url = source.Url;
        Alt = source.Alt;
        _requestedWidth = source._requestedWidth;
        _requestedHeight = source._requestedHeight;
        _imageLifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.ImageCancellationToken);
        (OutputDeferral, OutputAdmission) = _resources.CreateBitmapOutput(this, Context, _imageLifetimeCancellation);
        _sourceLoader = CreateSourceLoader();
        try
        {
            // These are immutable request descriptors, not resource state.
            // Data-URI payloads are rejected by the caller so this does not
            // retain or duplicate an embedded source buffer.
            _builtInSource = source._builtInSource;
            _preResolutionCacheKey = source._preResolutionCacheKey;
            _activeCacheKey = source._preResolutionCacheKey;
            _isSvg = source._isSvg; // a URI hint only; accepted SVG state is not copied
            _carriedCompletion = source._carriedCompletion; // immutable scalars
            _measure.IsInlineLayout = source._measure.IsInlineLayout;
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
        _resources = source._resources;
        _measure = source._measure.CreateForClone(this, context);
        _svgHostDeferral = new SvgHostLoadDeferral(this, _resources);
        Url = source.Url;
        Alt = source.Alt;
        _requestedWidth = source._requestedWidth;
        _requestedHeight = source._requestedHeight;
        _imageLifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.ImageCancellationToken);
        (OutputDeferral, OutputAdmission) = _resources.CreateBitmapOutput(this, Context, _imageLifetimeCancellation);
        _sourceLoader = CreateSourceLoader();
        try
        {
            _builtInSource = source._builtInSource;
            _preResolutionCacheKey = svgMetadataFence is { } svgFenceForKey
                ? svgFenceForKey.PreResolutionCacheKey
                : source._preResolutionCacheKey;
            _activeCacheKey = svgMetadataFence is { } svgFence
                ? svgFence.ActiveCacheKey
                : source._activeCacheKey;
            _measure.IsInlineLayout = source._measure.IsInlineLayout;
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
            _carriedCompletion = source._carriedCompletion;
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
        : _hasPresentedImage || _bitmap is not null || _svgHasRenderedTile || _carriedCompletion is not null
            ? MarkdownImageAccessibilityState.Loaded
            : MarkdownImageAccessibilityState.Loading;

    /// <summary>
    /// True while a visible image cannot show its pixels yet: it is still
    /// loading, or it carries an earlier layout's completed result
    /// and has not presented in this layout. Readiness checks of visible
    /// content use this, never the carried Loaded state alone.
    /// </summary>
    internal bool IsAwaitingVisiblePixelsForAutomation =>
        AccessibilityState == MarkdownImageAccessibilityState.Loading ||
        (_carriedCompletion is not null && !_loadFailed && !_hasPresentedImage &&
         _bitmap is null && !_svgHasRenderedTile);

    /// <summary>
    /// Captures this image's completed result for the next rebuild of the same
    /// document. An image that has not presented in this layout
    /// forwards the result it carried in, so consecutive rebuilds keep it.
    /// Call on the UI thread, which owns image publication.
    /// </summary>
    internal bool TryCaptureCompletedCarry(out CompletedImageCarry? carry)
    {
        carry = null;
        if (_disposed || string.IsNullOrEmpty(Url))
            return false;
        if (_loadFailed)
        {
            if (!_failureCarryable)
                return false;
            carry = CompletedImageCarry.ForFailure(
                Url,
                _isSvg,
                _isSvg ? _svgIntrinsicSize : _rasterIntrinsicSize,
                _svgFailureReason,
                _svgFailureDescription);
            return true;
        }

        bool presented = _hasPresentedImage || _bitmap is not null || _svgHasRenderedTile;
        Size intrinsic = default;
        if (presented)
        {
            if (_isSvg)
            {
                intrinsic = _svgIntrinsicSize;
            }
            else if (_rasterIntrinsicSize.Width > 0 && _rasterIntrinsicSize.Height > 0)
            {
                intrinsic = _rasterIntrinsicSize;
            }
            else if (_bitmap is { } bitmap)
            {
                try
                {
                    intrinsic = bitmap.Size;
                }
                catch (ObjectDisposedException)
                {
                    intrinsic = default;
                }
            }
        }

        var candidate = new CompletedImageCarry(
            Url,
            _isSvg,
            intrinsic,
            _svgTitle ?? _carriedCompletion?.SvgTitle,
            _svgDesc ?? _carriedCompletion?.SvgDescription);
        if (presented && candidate.HasUsableIntrinsicSize)
        {
            carry = candidate;
            return true;
        }

        carry = _carriedCompletion;
        return carry is not null;
    }

    /// <summary>
    /// The intrinsic size this box measures with while it reacquires a
    /// carried completed image, so the rebuilt layout matches the
    /// layout the image already had. Its own known size always wins.
    /// </summary>
    private bool TryGetCarriedMeasurementIntrinsicSize(out Size size)
    {
        size = default;
        if (_carriedCompletion is not { } carried || _bitmap is not null || _loadFailed)
            return false;
        if (_isSvg && _svgIntrinsicSize.Width > 0 && _svgIntrinsicSize.Height > 0)
            return false;
        if (!_isSvg && _rasterIntrinsicSize.Width > 0 && _rasterIntrinsicSize.Height > 0)
            return false;
        size = carried.IntrinsicSize;
        return carried.HasUsableIntrinsicSize;
    }

    internal Rect AutomationRenderDestination => _measure.IsInlineLayout
        ? Bounds
        : new Rect(GetContentX(), GetContentY(), _measure.ImageWidth, _measure.ImageHeight);

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
    public float MeasuredImageHeight => _measure.ImageHeight;

    /// <summary>Test-only: width of the image content area at last measure (excludes margins).</summary>
    public float MeasuredImageWidth => _measure.ImageWidth;

    /// <summary>Test-only: height of the caption area at last measure (excludes margins).</summary>
    public float MeasuredCaptionHeight => _measure.CaptionHeight;

    /// <summary>The measured geometry and native text-layout custody.</summary>
    internal ImageMeasureCustody MeasureCustody => _measure;

    internal void SetCompletedBitmapForTests(SharedCanvasBitmapCache.Lease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _loadStarted = true;
        _loadFailed = false;
        ReplaceBitmap(lease.Bitmap, lease);
    }

    internal InlineMeasureCheckpoint BeginInlineCheckpoint()
    {
        lock (_measure.Sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            return new InlineMeasureCheckpoint(this, _measure.OpenCheckpointNoLock());
        }
    }

    bool IImageMeasureHost.IsDisposed => _disposed;

    void IImageMeasureHost.RestoreBounds(Rect bounds) => Bounds = bounds;

    internal void FinishInlineCheckpoint(
        InlineMeasureCheckpoint checkpoint,
        bool commit)
    {
        lock (_measure.Sync)
        {
            if (!_measure.CheckpointActive ||
                !checkpoint.BelongsTo(this, _measure.CheckpointGeneration))
            {
                return;
            }

            _measure.FinishCheckpointNoLock(checkpoint, commit);
        }
    }

    /// <summary>
    /// Measures the image as an inline atomic cell. Inline images share the
    /// same loading/cache pipeline as block images, but they do not render
    /// captions or margins and reserve a compact placeholder before loading.
    /// </summary>
    internal Size MeasureInline(float availableWidth, float lineHeight)
    {
        lock (_measure.Sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            if (_measure.CheckpointActive)
                throw new InvalidOperationException("An inline image measure requires its active checkpoint.");
            return MeasureInlineCore(availableWidth, lineHeight);
        }
    }

    internal Size MeasureInline(
        float availableWidth,
        float lineHeight,
        InlineMeasureCheckpoint checkpoint)
    {
        lock (_measure.Sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ImageBox));
            if (!_measure.CheckpointActive ||
                !checkpoint.BelongsTo(this, _measure.CheckpointGeneration))
            {
                throw new InvalidOperationException("The inline image measure checkpoint is no longer active.");
            }
            return MeasureInlineCore(availableWidth, lineHeight);
        }
    }

    private Size MeasureInlineCore(float availableWidth, float lineHeight)
    {
        _measure.IsInlineLayout = true;
        _measure.AvailableWidth = availableWidth;
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
        else if (_bitmap is { } bmp)
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
                bw = (float)bmp.Size.Width;
                bh = (float)bmp.Size.Height;
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
        else if (TryGetCarriedMeasurementIntrinsicSize(out Size carriedSize))
        {
            float scale = Math.Min(1f, maxW / (float)carriedSize.Width);
            w = Math.Max(1f, (float)carriedSize.Width * scale);
            h = Math.Max(1f, (float)carriedSize.Height * scale);
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
        _measure.ImageWidth = w;
        _measure.ImageHeight = h;
        if (_bitmap is null)
        {
            UpdatePlaceholder(w, h);
        }
        _measure.CaptionHeight = 0f;
        _measure.ReplaceCaption(null, null, 0f);
        Bounds = new Rect(0, 0, w, h);
        return new Size(w, h);
    }

    /// <summary>Sets the document-coordinate rectangle used by an inline image.</summary>
    internal void SetInlineBounds(Rect rect)
    {
        Bounds = rect;
        if (ShouldCollapseFailedDecorativeImage)
        {
            _measure.ImageWidth = 0;
            _measure.ImageHeight = 0;
            return;
        }

        _measure.ImageWidth = (float)Math.Max(1, rect.Width);
        _measure.ImageHeight = (float)Math.Max(1, rect.Height);
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

        if (ShouldCollapseFailedDecorativeImage)
        {
            // Keep the image's single source/selection character, but a failed
            // dimensionless decorative image has no visual cell to outline.
            EnsureLoading();
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
        if (_loadFailed)
        {
            ReportUnreportedFailure();
            return;
        }

        // A pressure-parked tiled image retains its good pixels. Only the
        // geometry-aware paint path can decide that a visible tile is missing
        // (or that the target changed) and re-enter authorized loading.
        if (Volatile.Read(ref _svgPressureParked))
            return;

        if (OutputDeferral.ObserveDeferredBitmapOutput(isVisible))
            return;

        if (_loadStarted)
        {
            MarkdownSvgRenderDemand? currentDemand = Volatile.Read(ref _svgLoadDemandTicket);
            if (isVisible)
                currentDemand?.PromoteVisible();
            SvgHostDeferral.TryResumeDeferredSvgLoadForObservedDemandOrCapacity(currentDemand);
            return;
        }

        if (SvgHostDeferral.IsDeferred &&
            Volatile.Read(ref _svgLoadDemandTicket) is { } deferredDemand)
        {
            if (isVisible)
                deferredDemand.PromoteVisible();
            SvgHostDeferral.TryResumeDeferredSvgLoadForObservedDemandOrCapacity(deferredDemand);
            return;
        }

        StartLoad(isVisible: isVisible);
    }

    MarkdownSvgRenderDemand? ISvgHostDeferralHost.LoadDemand => Volatile.Read(ref _svgLoadDemandTicket);
    int ISvgHostDeferralHost.LoadDemandGeneration => Volatile.Read(ref _svgLoadDemandGeneration);
    long ISvgHostDeferralHost.SvgHostVisualDemandEpoch => Volatile.Read(ref _svgHostVisualDemandEpoch);
    void ISvgHostDeferralHost.MarkLoadNotStarted() => _loadStarted = false;

    void ISvgHostDeferralHost.StartLoad(
        bool isVisible, MarkdownSvgRenderDemand? demandOverride, int loadDemandGeneration) =>
        StartLoad(isVisible, demandOverride, loadDemandGeneration);

    bool ISvgHostDeferralHost.TryCaptureSvgHostDeferralCancellation(
        SvgHostCapacityDeferral deferral, out CancellationToken cancellationToken)
    {
        lock (_svgWorkSync)
        {
            if (_disposed || _resourcesSuspended ||
                !ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), deferral.Demand) ||
                deferral.LoadGeneration != Volatile.Read(ref _svgLoadDemandGeneration))
            {
                cancellationToken = default;
                return false;
            }

            cancellationToken = (_imageResidencyCancellation ?? _imageLifetimeCancellation).Token;
            return true;
        }
    }

    public override float Measure(float availableWidth)
    {
        _measure.AvailableWidth = availableWidth;
        if (ShouldCollapseFailedDecorativeImage)
        {
            CollapseFailedDecorativeImage(availableWidth);
            return 0;
        }

        float maxW = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
        Size rasterIntrinsicSize = GetRasterMeasurementIntrinsicSize();
        float w, h;

        if (_bitmap is { } bmp)
        {
            // Intrinsic-first sizing: render at the bitmap's natural size and
            // only downscale (preserving aspect) when the intrinsic width
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
                bw = (float)bmp.Size.Width;
                bh = (float)bmp.Size.Height;
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
        else if (TryGetCarriedMeasurementIntrinsicSize(out Size carriedSize))
        {
            float scale = Math.Min(1f, maxW / (float)carriedSize.Width);
            w = (float)carriedSize.Width * scale;
            h = (float)carriedSize.Height * scale;
        }
        else if (_isSvg && (_svgIntrinsicSize.Width > 0 || _loadStarted))
        {
            // SVG load is in flight. Reserve space using the intrinsic size
            // we recovered from the cache; fall back to a 16:9-ish band
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
        _measure.ImageWidth = w;
        _measure.ImageHeight = h;
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
            replacementCaption = _measure.CreateOwnedTextLayout(
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

        _measure.ReplaceCaption(replacementCaption, replacementCaptionRecord, replacementCaptionHeight);

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
            _measure.CheckpointActive || !float.IsFinite(_measure.AvailableWidth) ||
            _measure.AvailableWidth <= 0 || !double.IsFinite(intrinsicSize.Width) ||
            !double.IsFinite(intrinsicSize.Height) || intrinsicSize.Width <= 0 ||
            intrinsicSize.Height <= 0 || intrinsicSize.Width > int.MaxValue ||
            intrinsicSize.Height > int.MaxValue)
        {
            return false;
        }

        float maxWidth = Math.Max(1f, _measure.IsInlineLayout ? _measure.AvailableWidth :
            _measure.AvailableWidth - (float)(Margin.Left + Margin.Right));
        float scale = Math.Min(1f, maxWidth / (float)intrinsicSize.Width);
        float width = (float)intrinsicSize.Width * scale;
        float height = (float)intrinsicSize.Height * scale;
        if (_measure.IsInlineLayout)
        {
            width = Math.Max(1f, width);
            height = Math.Max(1f, height);
        }
        ApplyRequestedSize(ref width, ref height, maxWidth);
        if (_measure.ImageWidth != width || _measure.ImageHeight != height)
            return false;

        // Prove actual committed geometry, not just matching advisory source
        // dimensions. Inline checkpoint rollback and clipped inline placement
        // can restore a different rectangle before publication.
        return _measure.IsInlineLayout
            ? Bounds.Width == width && Bounds.Height == height && _measure.CaptionHeight == 0
            : Bounds.Width == _measure.AvailableWidth && Bounds.Height ==
                Math.Max(height, GetParagraphLineHeight()) + _measure.CaptionHeight +
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
        var imageRect = new Rect(x, y, _measure.ImageWidth, _measure.ImageHeight);
        PaintImageContent(ds, imageRect, viewport);

        if (_measure.Caption is not null)
        {
            var cs = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            float cy = (float)(Bounds.Y + Margin.Top) +
                Math.Max(_measure.ImageHeight, GetParagraphLineHeight()) + (float)cs.Margin.Top;
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(ds, _measure.Caption, x, cy, cs.Foreground);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    internal override bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        if (!Bounds.Contains(point))
        {
            position = DocumentPosition.Zero;
            return false;
        }

        // A promoted Markdown image occupies one atomic source slot. While an
        // existing selection is being extended, points on its visible artwork
        // use the artwork's halves for the before/after boundary. Bounds also
        // includes the wrapped alt caption and outer margins; using its center
        // can put the after boundary below the entire painted image. Preserve
        // the full-block fallback for caption and margin points, and keep
        // ordinary HitTest start-only so touching an image does not change tap
        // behavior.
        Rect artwork = AutomationRenderDestination;
        double midpointTop = artwork.Contains(point) ? artwork.Top : Bounds.Top;
        double midpointHeight = artwork.Contains(point) ? artwork.Height : Bounds.Height;
        bool afterMidpoint = point.Y >= midpointTop + midpointHeight / 2.0;
        position = new DocumentPosition(BlockIndex, 0, afterMidpoint ? 1 : 0);
        return true;
    }

    public override System.Collections.Generic.IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        if (SelectionIntersectsAtomicSlot(range))
            yield return Bounds;
    }

    public override void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Color color, Rect viewport)
    {
        if (!SelectionIntersectsAtomicSlot(range))
            return;

        if (Bounds.Right < viewport.Left || Bounds.Left > viewport.Right ||
            Bounds.Bottom < viewport.Top || Bounds.Top > viewport.Bottom)
        {
            return;
        }

        float x = GetContentX();
        float y = GetContentY();
        var imageRect = new Rect(x, y, _measure.ImageWidth, _measure.ImageHeight);

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

        if (_measure.Caption is not null)
        {
            var cs = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            float cy = (float)(Bounds.Y + Margin.Top) +
                Math.Max(_measure.ImageHeight, GetParagraphLineHeight()) + (float)cs.Margin.Top;
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(ds, _measure.Caption, x, cy, color);
        }
    }

    /// <summary>
    /// Returns paint-only selection geometry: a translucent tint over the
    /// painted artwork and an opaque band behind the caption text, which the
    /// selection foreground repaints in the selection text color. The full
    /// block bounds remain authoritative for endpoints and hit tests.
    /// </summary>
    public System.Collections.Generic.IEnumerable<SelectionBackgroundGeometry> GetSelectionBackgroundGeometry(
        DocumentRange range)
    {
        if (!SelectionIntersectsAtomicSlot(range))
            yield break;

        // An inline image's arranged bounds are its painted content.
        if (_measure.IsInlineLayout)
        {
            yield return new SelectionBackgroundGeometry(Bounds, 1f);
            yield break;
        }

        float x = GetContentX();
        float y = GetContentY();
        if (_measure.ImageWidth > 0 && _measure.ImageHeight > 0)
        {
            yield return new SelectionBackgroundGeometry(
                new Rect(x, y, _measure.ImageWidth, _measure.ImageHeight),
                SelectionArtworkBackgroundOpacity);
        }

        if (_measure.Caption is { } caption)
        {
            Rect layout = caption.LayoutBounds;
            if (layout.Width > 0 && layout.Height > 0)
            {
                var cs = Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
                float cy = (float)(Bounds.Y + Margin.Top) +
                    Math.Max(_measure.ImageHeight, GetParagraphLineHeight()) + (float)cs.Margin.Top;
                yield return new SelectionBackgroundGeometry(
                    new Rect(x + layout.X, cy + layout.Y, layout.Width, layout.Height),
                    1f);
            }
        }
    }

    private bool SelectionIntersectsAtomicSlot(DocumentRange range)
    {
        DocumentRange normalized = range.Normalized();
        if (normalized.IsEmpty ||
            normalized.End.BlockIndex < BlockIndex ||
            normalized.Start.BlockIndex > BlockIndex)
        {
            return false;
        }

        var before = new DocumentPosition(BlockIndex, 0, 0);
        var after = new DocumentPosition(BlockIndex, 0, 1);
        return normalized.Start < after && normalized.End > before;
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

        SvgHostDeferral.ClearSvgHostDeferral();
        OutputDeferral.ClearBitmapOutputDeferral();
        _measure.InvalidateInlineCheckpointForDisposal();

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

    internal void CompleteImageWork()
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
            OutputDeferral.ArmDeferredBitmapOutputRetryAfterWorkDrained();
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

    internal void CompleteSvgDocumentUse(SvgDocumentUseState state, bool candidate)
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
            OutputDeferral.ArmDeferredBitmapOutputRetryAfterWorkDrained();
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
            SvgHostOwnerSlot? terminalOwnerSlot;
            lock (_svgWorkSync)
            {
                terminalDocument = _svgDocument;
                _svgDocument = null;
                terminalOwnerSlot = Volatile.Read(ref _svgTileRenderPlan)?.OwnerSlot;
                Volatile.Write(ref _svgTileRenderPlan, null);
            }
            SvgHostDeferral.ClearSvgHostDeferral();
            OutputDeferral.ClearBitmapOutputDeferral();
            terminalOwnerSlot?.Dispose();
            if (terminalDocument is not null)
            {
                try { terminalDocument.Dispose(); }
                catch (Exception exception) { AddFailure(exception); }
            }
            try { ReleaseSvgTiles(); } catch (Exception exception) { AddFailure(exception); }
            try { ReleaseBitmap(); } catch (Exception exception) { AddFailure(exception); }
            try { _measure.RetireNativeLayoutsForDisposal(AddFailure); }
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

    private void StartLoad(
        bool isVisible = true,
        MarkdownSvgRenderDemand? demandOverride = null,
        int loadDemandGeneration = 0)
    {
        if (!TryBeginImageWork(out ImageWorkLease? setupWork) || setupWork is null)
            return;

        using ImageWorkLease loadSetup = setupWork;
        OutputDeferral.ClearBitmapOutputDeferral(preserveRetryOffer: demandOverride is not null);
        IDisposable? previousDeferredWaiter = null;
        MarkdownSvgRenderDemand demand;
        lock (SvgHostDeferral.Sync)
        {
            if (demandOverride is null)
            {
                previousDeferredWaiter = SvgHostDeferral.ClearSvgHostDeferralNoLock();
                demand = new MarkdownSvgRenderDemand(isVisible
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
            Volatile.Write(ref _unavailableReported, 0);
        }
        previousDeferredWaiter?.Dispose();
        if (string.IsNullOrEmpty(Url)) { _loadFailed = true; return; }

        // A source-cache hit can often also hit the exact device bitmap cache.
        // This path is synchronous and never wakes the worker.
        if (_isSvg)
        {
            if (TryPublishCachedSvgBitmap(layoutInvalidated: false,
                    expectedWork: OutputAdmission.CaptureBitmapOutputWorkContext(loadSetup.Token, expectedDemand: demand)))
                return;
        }

        // SVG data URIs (data:image/svg+xml,...) may contain raw < > characters
        // that are illegal in RFC 3986, which causes Uri.TryCreate to return
        // false. Parse them directly from the raw URL string.
        if (_isSvg && Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            _ = _sourceLoader.LoadSvgDataUriAsync(Url, demand, loadDemandGeneration);
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

                bool scheduled;
                try
                {
                    scheduled = performanceSession.TryScheduleImageActivation(
                        demand,
                        () =>
                        {
                            Task resolution = _sourceLoader.ResolveAndLoadAsync(resolver, demand, loadDemandGeneration);
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
            else
            {
                _ = _sourceLoader.ResolveAndLoadAsync(resolver, demand, loadDemandGeneration);
            }
            return;
        }

        if (_builtInSource is not { } source)
        {
            PublishFailure(cacheKey: string.Empty);
            return;
        }

        _sourceLoader.LoadFromDefaultSource(source, demand, loadDemandGeneration);
    }

    private void ResetLoadStartedAfterUnstartedActivation(
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        CancellationToken activationCancellation)
    {
        // The volatile lifecycle flags are checked alongside the exact load
        // identity under StartLoad's gate. A replacement must take this same
        // gate before it can set _loadStarted again, so a late callback cannot
        // clear a newer activation even if a resumed box reuses its ticket;
        // the old residency token is canceled before that callback can reset.
        lock (SvgHostDeferral.Sync)
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

    private ImageSourceLoader CreateSourceLoader() =>
        new(this, Context, _resources, OutputAdmission, OutputDeferral, _svgHostDeferral, Url,
            _requestedWidth, _requestedHeight);

    bool IImageSourceLoadHost.IsSvg
    {
        get => _isSvg;
        set => _isSvg = value;
    }

    string? IImageSourceLoadHost.ActiveCacheKey
    {
        get => _activeCacheKey;
        set => _activeCacheKey = value;
    }

    MarkdownBuiltInImageSource? IImageSourceLoadHost.BuiltInSource => _builtInSource;

    void IImageSourceLoadHost.TrackDetachedImageWork(Task detachedOperation, bool failRetirementOnFault) =>
        TrackDetachedImageWork(detachedOperation, failRetirementOnFault);

    void IImageSourceLoadHost.PresentCachedRasterBitmap(SharedCanvasBitmapCache.Lease cachedLease)
    {
        if (_disposed)
        {
            TryImageCleanup(cachedLease.Dispose);
            return;
        }

        ReplaceBitmap(cachedLease.Bitmap, cachedLease);
        RaiseLoadCompleted(layoutInvalidated: true);
    }

    bool IImageSourceLoadHost.TryPublishCachedSvgSource(
        SvgCacheEntry entry, string resolvedCacheKey, BitmapOutputWorkContext? expectedWork) =>
        IsSvgCacheEntryCurrent(entry) && TryPublishCachedSvgBitmap(
            layoutInvalidated: true,
            sourceEntry: entry,
            resolvedCacheKey: resolvedCacheKey,
            expectedWork: expectedWork);

    void IImageSourceLoadHost.PresentSvgDataUriFailure()
    {
        if (_disposed) return;
        _loadFailed = true;
        _failureCarryable = true;
        string cacheKey = _activeCacheKey ?? Url;
        _resources.Failures.RememberFailedUrl(cacheKey);
        RaiseLoadCompleted(layoutInvalidated: true);
    }

    private float GetContentX()
    {
        float left = (float)(Bounds.X + Margin.Left);
        float available = Math.Max(0, (float)Bounds.Width - (float)(Margin.Left + Margin.Right));
        return ContentAlignment switch
        {
            CanvasHorizontalAlignment.Center => left + Math.Max(0, (available - _measure.ImageWidth) / 2f),
            CanvasHorizontalAlignment.Right => left + Math.Max(0, available - _measure.ImageWidth),
            _ => left,
        };
    }

    private float GetContentY()
        => (float)(Bounds.Y + Margin.Top) +
           Math.Max(0f, (GetParagraphLineHeight() - _measure.ImageHeight) / 2f);

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
        {
            Volatile.Write(ref _unavailableReported, 1);
            Context.ImageUnavailable?.Invoke(Url, reason, svgFailureReason);
        }
    }

    bool IImageLoadHost.IsDisposed => _disposed;

    bool IImageLoadHost.TryBeginImageWork(out ImageWorkLease? work) => TryBeginImageWork(out work);

    void IImageLoadHost.TryImageCleanup(Action cleanup) => TryImageCleanup(cleanup);

    void IImageLoadHost.RecordImageRetirementFailure(Exception exception) =>
        RecordImageRetirementFailure(exception);

    void IImageLoadHost.PublishOnUiThread(Action publish, Action? onDropped) =>
        PublishOnUiThread(publish, onDropped);

    void IImageLoadHost.ReportUnavailable(
        MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgFailureReason) =>
        ReportUnavailable(reason, svgFailureReason);

    void IImageLoadHost.PublishFailure(string cacheKey, bool cachePermanently) =>
        PublishFailure(cacheKey, cachePermanently);

    void IImageLoadHost.ResetLoadAfterCancellation()
    {
        if (!_disposed)
            _loadStarted = false;
    }

    double IImageRasterLoadHost.RasterSizingAvailableWidth =>
        _measure.AvailableWidth - Margin.Left - Margin.Right;

    void IImageRasterLoadHost.PresentRasterPreview(SharedCanvasBitmapCache.Lease cachedPreview)
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
    }

    void IImageRasterLoadHost.PresentRasterLoad(in RasterLoadCompletion completion)
    {
        if (_disposed)
        {
            if (completion.Lease is { } retiredLease)
                TryImageCleanup(retiredLease.Dispose);
            if (completion.OwnedBitmap is { } retiredBitmap)
                TryImageCleanup(retiredBitmap.Dispose);
            return;
        }

        bool exactReservedGeometry = completion.TransformedDecode && !completion.Failed && !completion.DeviceLost &&
            completion.Lease is not null &&
            Context.PerformanceSession is { IsDisposed: false } session &&
            session.Options.PrefetchDocumentImages &&
            session.Options.UseDisplaySizedRasterDecode &&
            HasExactReservedRasterGeometry(completion.IntrinsicSize);
        bool layoutChanged = (_bitmap is null && !exactReservedGeometry) ||
            (completion.TransformedDecode &&
             !exactReservedGeometry &&
             (_rasterIntrinsicSize.Width != completion.IntrinsicSize.Width ||
              _rasterIntrinsicSize.Height != completion.IntrinsicSize.Height));
        if (completion.Failed && _bitmap is null)
        {
            _loadFailed = true;
            _failureCarryable = completion.CacheRasterDecodeFailure;
            if (completion.CacheRasterDecodeFailure)
                _resources.Failures.RememberFailedRasterDecode(completion.CacheKey, completion.FailureFormatKey, completion.RasterDecodeFailureIdentity);
        }
        else if (completion.DeviceLost)
        {
            _loadStarted = false;
        }
        else if (completion.Lease is not null)
        {
            if (completion.TransformedDecode)
                _rasterIntrinsicSize = completion.IntrinsicSize;
            ReplaceBitmap(completion.Lease.Bitmap, completion.Lease);
        }

        RaiseLoadCompleted(layoutInvalidated: layoutChanged);
    }

    long ISvgLoadHost.SvgHostVisualDemandEpoch => Volatile.Read(ref _svgHostVisualDemandEpoch);

    int ISvgLoadHost.AdvanceSvgRenderGeneration() => AdvanceSvgRenderGeneration();

    string ISvgLoadHost.GetSvgSourceSecurityPartitionKey(string resolvedCacheKey) =>
        GetSvgSourceSecurityPartitionKey(resolvedCacheKey);

    bool ISvgLoadHost.IsKnownSvgSource(byte[] rawBytes, string securityPartitionHash) =>
        IsKnownSvgSource(rawBytes, securityPartitionHash);

    bool ISvgLoadHost.TryGetKnownSvgPreflight(
        byte[] rawBytes, string securityPartitionHash, Size intrinsicHint, out SvgPreflight preflight)
    {
        if (IsKnownSvgSource(rawBytes, securityPartitionHash) &&
            _svgContentHash is { } cachedContentHash &&
            _svgSecurityPartitionHash is { } cachedPartitionHash)
        {
            preflight = (
                intrinsicHint,
                _svgTitle,
                _svgDesc,
                cachedContentHash,
                cachedPartitionHash);
            return true;
        }

        preflight = default;
        return false;
    }

    void ISvgLoadHost.PublishSvgFailure(
        MarkdownSvgException exception, string failureCacheKey, bool cachePermanently, int renderGeneration) =>
        PublishSvgFailure(exception, failureCacheKey, cachePermanently, renderGeneration);

    void ISvgLoadHost.PublishSvgFailure(
        MarkdownSvgFailureReason reason, string description, string failureCacheKey,
        bool cachePermanently, int renderGeneration) =>
        PublishSvgFailure(reason, description, failureCacheKey, cachePermanently, renderGeneration);

    MarkdownSvgColor ISvgLoadHost.GetSemanticSvgColor() => GetSemanticSvgColor();

    MarkdownSvgColorScheme ISvgLoadHost.GetSvgColorScheme() => GetSvgColorScheme();

    bool ISvgLoadHost.TryPublishContentCachedSvg(
        string contentCacheKey, string resolvedCacheKey, string? builtInSourceAliasKey,
        bool layoutInvalidated, int renderGeneration, BitmapOutputWorkContext bitmapOutputWork) =>
        TryPublishContentCachedSvg(
            contentCacheKey, resolvedCacheKey, builtInSourceAliasKey,
            layoutInvalidated, renderGeneration, bitmapOutputWork);

    SvgDocumentUseLease? ISvgLoadHost.TryAcquireCandidateSvgDocumentUse(
        IMarkdownSvgDocument document, out bool ownershipConflict) =>
        TryAcquireCandidateSvgDocumentUse(document, out ownershipConflict);

    bool ISvgLoadHost.IsSvgProviderCurrent(
        IMarkdownSvgRenderer renderer, long providerCacheGeneration, bool hasText) =>
        IsSvgProviderCurrent(renderer, providerCacheGeneration, hasText);

    void ISvgLoadHost.PublishSvgGenerationInvalidated(int renderGeneration) =>
        PublishSvgGenerationInvalidated(renderGeneration);

    SvgRasterPlan ISvgLoadHost.PickRasterDimensions(Size intrinsic) => PickRasterDimensions(intrinsic);

    string ISvgLoadHost.BuildSvgBitmapIdentity(
        string contentHash, string securityPartitionHash, MarkdownSvgDocumentInfo info, int width, int height,
        MarkdownSvgColorScheme colorScheme, MarkdownSvgColor semanticColor, long rendererIdentity,
        long providerCacheGeneration, MarkdownSvgPhysicalViewport physicalViewport) =>
        BuildSvgBitmapIdentity(
            contentHash, securityPartitionHash, info, width, height, colorScheme, semanticColor,
            rendererIdentity, providerCacheGeneration, physicalViewport);

    void ISvgLoadHost.HandleSvgWorkFailure(
        Exception exception, CancellationToken cancellationToken, CancellationToken sessionLifetimeToken,
        string resolvedCacheKey, string svgFailureCacheKey, int renderGeneration) =>
        HandleSvgWorkFailure(
            exception, cancellationToken, sessionLifetimeToken, resolvedCacheKey, svgFailureCacheKey, renderGeneration);

    void ISvgLoadHost.RetireUncommittedSvgDocument(IMarkdownSvgDocument document) =>
        RetireUncommittedSvgDocument(document);

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

    (Action Publish, Action? OnDropped) ISvgLoadHost.CreateTiledSvgPublication(
        SvgPublicationMetadata metadata, IMarkdownSvgDocument openedDocument,
        SvgDocumentUseLease candidateUse, long publishRequestedAt) =>
        CreateTiledSvgPublication(metadata, openedDocument, candidateUse, publishRequestedAt);

    (Action Publish, Action? OnDropped) ISvgLoadHost.CreateBitmapSvgPublication(
        SvgPublicationMetadata metadata, SharedCanvasBitmapCache.Lease publishedLease, long publishRequestedAt) =>
        CreateBitmapSvgPublication(metadata, publishedLease, publishRequestedAt);

    Task<MarkdownSvgRaster> ISvgLoadHost.RenderSvgDocumentAsync(
        IMarkdownSvgDocument document, MarkdownSvgRenderRequest request, CancellationToken cancellationToken,
        SvgDocumentUseLease parentUse, MarkdownSvgRenderDemand? demand) =>
        RenderSvgDocumentAsync(document, request, cancellationToken, parentUse, demand);

    void ISvgLoadHost.ValidateSvgRaster(MarkdownSvgRaster raster, int expectedWidth, int expectedHeight) =>
        ValidateSvgRaster(raster, expectedWidth, expectedHeight);

    CanvasBitmap ISvgLoadHost.CreateBitmapFromSvgRaster(
        MarkdownSvgRaster raster, long diagnosticWorkId, long diagnosticSourceBytes,
        long diagnosticTargetPixels, BitmapOutputAdmission.Reservation? bitmapReservation) =>
        CreateBitmapFromSvgRaster(
            raster, diagnosticWorkId, diagnosticSourceBytes, diagnosticTargetPixels, bitmapReservation);

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
        if (ShouldCollapseFailedDecorativeImage)
            return;

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
            SvgTilePainter.PaintLayer(
                ds,
                _svgTiles,
                destination,
                viewport,
                _svgTileCacheWidthPixels,
                _svgTileCacheHeightPixels,
                _svgTileCachePhysicalViewport,
                this,
                useFallbackTiles: true);
        }

        QueueVisibleSvgTiles(destination, viewport);
    }

    // The caller holds _svgTileLock. A hole in the new tile grid shows the
    // fallback grid, then the previous whole bitmap, then the placeholder.
    void ISvgTileGapFill.FillSvgTileGap(CanvasDrawingSession ds, Rect destination, Rect gap, bool useFallbackTiles)
    {
        if (useFallbackTiles && _svgFallbackTiles is { Count: > 0 } fallback &&
            _svgFallbackWidthPixels > 0 && _svgFallbackHeightPixels > 0)
        {
            SvgTilePainter.PaintLayer(ds, fallback, destination, gap,
                _svgFallbackWidthPixels, _svgFallbackHeightPixels,
                _svgFallbackPhysicalViewport, this, useFallbackTiles: false);
        }
        else if (_bitmap is { } previousBitmap)
        {
            PaintWholeBitmap(ds, previousBitmap, destination);
        }
        else
        {
            PaintPlaceholder(ds, destination);
        }
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

        Rect visible = SvgTilePainter.IntersectRects(destination, viewport);
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
                        if (SvgFallbackTileRetention.ShouldRetain(
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
                var tileDemand = new MarkdownSvgRenderDemand(priority);
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
        using var svgWorkScope = MarkdownSvgStageTrace.BeginWorkScope();
        long svgWorkId = svgWorkScope.WorkId;
        long svgSourceBytes = _svgSourceByteLength;
        long targetPixels = (long)key.Width * key.Height;
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
                plan.Document is IMarkdownSvgPrioritizedDocument;
            if (!providerOwnsAdmission)
            {
                await _resources.SvgProviderAdmission.WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
                providerAdmissionHeld = true;
            }
            CancellationToken cancellationToken = admissionCancellation.Token;
            if (!IsCurrentSvgTilePlan(plan) || !IsCurrentSvgRendererState())
                return;
            BitmapOutputWorkContext bitmapOutputWork = OutputAdmission.CaptureBitmapOutputWorkContext(
                cancellationToken, isExistingSvgTileOutput: true, expectedDemand: plan.Demand,
                expectedSvgGeneration: plan.RenderGeneration);
            CanvasDevice device = Context.ResourceCreator.Device;
            if (!OutputAdmission.TryAcquireAdmittedSvgBitmap(device, identity, out bitmapLease, isExistingSvgTileOutput: true,
                    expectedWork: bitmapOutputWork))
            {
                if (OutputDeferral.IsBitmapOutputDeferred)
                    return;
                renderGate = _resources.BitmapCache.GetDecodeGate(device, identity);
                await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                renderGateHeld = true;

                _ = OutputAdmission.TryAcquireAdmittedSvgBitmap(device, identity, out bitmapLease, isExistingSvgTileOutput: true,
                    expectedWork: bitmapOutputWork);
                if (bitmapLease is null)
                {
                    if (OutputDeferral.IsBitmapOutputDeferred ||
                        !OutputAdmission.TryReserveBitmapOutput(checked((long)key.Width * key.Height * 4), out bitmapReservation,
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
                        }
                        try
                        {
                            using var stageTrace = MarkdownSvgStageTrace.Measure(
                                svgWorkId,
                                MarkdownSvgWorkStage.BitmapCacheStore,
                                svgSourceBytes,
                                targetPixels);
                            bitmapLease = _resources.BitmapCache.StoreSvgAndAcquire(
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
                        }
                        catch
                        {
                            OutputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
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
            OutputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
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
                    TryImageCleanup(() => _resources.SvgProviderAdmission.Release());
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
                            _failureCarryable = false; // a tile render failure is not proven permanent
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
                        _measure.AvailableWidth - (float)(Margin.Left + Margin.Right));
                    UpdatePlaceholder(maxWidth, _measure.ImageHeight);
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
                return SvgTileCacheTransition.ForPublication(
                    token,
                    Volatile.Read(ref _svgTileRenderPlan),
                    plan,
                    _svgTiles,
                    _svgFallbackTiles,
                    _svgTileCacheIdentityPrefix,
                    _svgTileCacheWidthPixels,
                    _svgTileCacheHeightPixels,
                    _svgTileCachePhysicalViewport,
                    _svgTilesInFlight.Values);
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
                return SvgTileCacheTransition.ForFailure(
                    token,
                    plan,
                    _svgTiles,
                    _svgFallbackTiles,
                    _svgTileCacheIdentityPrefix,
                    _svgTileCacheWidthPixels,
                    _svgTileCacheHeightPixels,
                    _svgTileCachePhysicalViewport,
                    _svgFallbackIdentityPrefix,
                    _svgFallbackWidthPixels,
                    _svgFallbackHeightPixels,
                    _svgFallbackPhysicalViewport,
                    _svgTilesInFlight.Values);
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
            long rendererIdentity = _resources.ProviderIdentities.GetSvgRendererIdentity(renderer);
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
            if (!OutputAdmission.TryAcquireAdmittedSvgBitmap(
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
            SvgPublicationMetadata metadata = new(
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
        if (!_resources.SvgSources.TryGetValue(contentCacheKey, out SvgCacheEntry? entry) ||
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
            _resources.SvgSources.Set(builtInSourceAliasKey, entry);
        return true;
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
            bool transferred = false;
            try
            {
                bool layoutInvalidated = ShouldInvalidateSvgGeometry(
                    metadata.MetadataWasKnown, _svgIntrinsicSize, metadata.Intrinsic);
                if (!TryCommitSvgPublication(metadata, plan: null))
                    return;

                if (!string.IsNullOrEmpty(metadata.ResolvedCacheKey))
                {
                    _resources.Failures.ForgetSvgFailure(metadata.SvgFailureCacheKey);
                    _resources.Failures.ForgetFailedUrl(metadata.ResolvedCacheKey);
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
        SvgHostOwnerSlot? abandonedOwnerSlot = null;
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
        MarkdownSvgRenderDemand? demand = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SvgDocumentUseLease nativeUse = TryAcquireNestedSvgDocumentUse(parentUse) ??
            throw new OperationCanceledException(cancellationToken);
        ValueTask<MarkdownSvgRaster> operation;
        try
        {
            operation = demand is not null && document is IMarkdownSvgPrioritizedDocument hostDocument
                ? hostDocument.RenderAsync(request, demand, cancellationToken)
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
            _failureCarryable = _loadFailed && cachePermanently;
            if (_loadFailed && cachePermanently &&
                failureCacheKey.Length is > 0 and <= ImageFailureCache.MaxKeyLength)
            {
                _resources.Failures.RememberSvgFailure(
                    failureCacheKey, reason, SvgSourcePreflight.TrimMetadata(description));
            }

            if (_loadFailed)
            {
                ReportUnavailable(MarkdownImageUnavailableReason.Unavailable, reason);
                float maxWidth = Math.Max(
                    1f,
                    _measure.AvailableWidth - (float)(Margin.Left + Margin.Right));
                UpdatePlaceholder(maxWidth, _measure.ImageHeight);
            }

            RaiseLoadCompleted(layoutInvalidated: _loadFailed);
        });
    }

    private SvgRasterPlan PickRasterDimensions(Size intrinsic)
    {
        float naturalWidth = (float)Math.Max(1d, intrinsic.Width);
        float naturalHeight = (float)Math.Max(1d, intrinsic.Height);
        float maxWidth = _measure.IsInlineLayout
            ? Math.Max(1f, _measure.AvailableWidth)
            : Math.Max(1f, _measure.AvailableWidth - (float)(Margin.Left + Margin.Right));
        if (!float.IsFinite(maxWidth) || _measure.AvailableWidth <= 0)
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
                Math.Min(MaxSvgOutputRasterBytes, _resources.BitmapOutput.MaximumSingleOutputBytes)) ||
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

    private bool IsSvgCacheEntryCurrent(SvgCacheEntry entry)
    {
        try
        {
            return Context.SvgRenderer is { } renderer &&
                   entry.RendererIdentity == _resources.ProviderIdentities.GetSvgRendererIdentity(renderer) &&
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
                   _resources.ProviderIdentities.GetSvgRendererIdentity(renderer) == _svgRendererIdentity &&
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
        , long diagnosticWorkId,
        long diagnosticSourceBytes = 0,
        long diagnosticTargetPixels = 0
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
            using var bufferCopyTrace = MarkdownSvgStageTrace.Measure(
                diagnosticWorkId,
                MarkdownSvgWorkStage.SvgBitmapBufferCopy,
                diagnosticSourceBytes,
                diagnosticTargetPixels);
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

            using var createBitmapTrace = MarkdownSvgStageTrace.Measure(
                diagnosticWorkId,
                MarkdownSvgWorkStage.SvgCanvasBitmapCreateFromBytes,
                diagnosticSourceBytes,
                diagnosticTargetPixels);
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

    private void PublishFailure(
        string cacheKey,
        bool cachePermanently = true)
    {
        PublishOnUiThread(() =>
        {
            if (_disposed) return;
            _loadFailed = true;
            _failureCarryable = cachePermanently;
            if (cachePermanently && !string.IsNullOrEmpty(cacheKey))
            {
                _resources.Failures.RememberFailedUrl(cacheKey);
            }

            // Block images and explicitly-sized inline images preserve their geometry. A
            // compact inline image without requested dimensions expands once to show its alt
            // text; the host relayouts the committed snapshot and does not recreate or retry it.
            float maxWidth = Math.Max(
                1f,
                _measure.AvailableWidth - (float)(Margin.Left + Margin.Right));
            UpdatePlaceholder(maxWidth, _measure.ImageHeight);
            RaiseLoadCompleted(
                layoutInvalidated: ShouldExpandInlineFailure ||
                    ShouldCollapseFailedDecorativeImage);
        });
    }

    private void CollapseFailedDecorativeImage(float availableWidth)
    {
        _measure.ReplacePlaceholder(null, null);
        _measure.ReplaceCaption(null, null, 0f);
        _measure.ImageWidth = 0;
        _measure.ImageHeight = 0;
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

        bool compactInlineFailure = _measure.IsInlineLayout && _loadFailed;
        if (_measure.Placeholder is null ||
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
                _measure.Placeholder,
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
        bool compactInlineFailure = _measure.IsInlineLayout && _loadFailed;
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
        CanvasTextLayout replacement = _measure.CreateOwnedTextLayout(
            text,
            formatLease.Format,
            layoutWidth,
            layoutHeight,
            out NativeResourceRetirementRecord replacementRecord);
        if (compactInlineFailure)
        {
            // The one-line pill is clamped to the available width. Trim the
            // label with an ellipsis instead of letting the centered text
            // spill past both edges into the pill's clip. The accessible
            // name keeps the full text.
            replacement.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
            replacement.TrimmingSign = CanvasTrimmingSign.Ellipsis;
        }
        _measure.ReplacePlaceholder(replacement, replacementRecord);
    }

    private bool ShouldExpandInlineFailure =>
        _measure.IsInlineLayout &&
        _loadFailed &&
        _requestedWidth is null &&
        _requestedHeight is null;

    private bool ShouldCollapseFailedDecorativeImage =>
        _loadFailed &&
        string.IsNullOrWhiteSpace(Alt) &&
        _requestedWidth is null &&
        _requestedHeight is null;

    internal bool IsFailedDecorativeImageCollapsed => ShouldCollapseFailedDecorativeImage;

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
        NativeResourceRetirementRecord retirementRecord = _measure.ReserveNativeLayoutRetirementRecord();
        CanvasTextLayout? layout = null;
        float measuredWidth;
        try
        {
            layout = CanvasTextLayoutFactory.Create(
                Context.ResourceCreator,
                GetInlineFailureText(),
                formatLease.Format,
                Math.Max(maxWidth, 4096f),
                height,
                Context.LayoutDiagnostics);
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

}
