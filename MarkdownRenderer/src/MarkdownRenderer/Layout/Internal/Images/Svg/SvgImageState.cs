using System;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);

namespace MarkdownRenderer.Layout.Internal.Images.Svg;

/// <summary>
/// The SVG an image box has accepted: the weak source identity and its
/// length, intrinsic size, accessibility title and description, content and
/// security-partition hashes, document info, provider identities, raster
/// size and physical viewport, the last SVG failure, and the render state
/// around it: the render generation, the accepted tile plan, the
/// pressure-park marker and parked state, and the visual-demand epoch.
/// </summary>
/// <remarks>
/// The weighted SVG source cache is the sole long-lived host owner of raw
/// bytes; this state keeps a weak identity only, so reopening after a raster
/// variant miss goes back through the authorized resolver or admitted
/// built-in source path. The accepted tile plan is installed only with
/// accepted UI metadata: a replacement open can adopt its document before
/// that publication, and must never be combined with the prior image's
/// pixel dimensions, generation, or GPU-cache identity. A pressure-park
/// transition temporarily closes tile admission while it detaches the
/// accepted document; the parking plan is never a source or document owner.
/// Members ending in NoLock run under the image-work lock.
/// </remarks>
internal sealed class SvgImageState
{
    private readonly ImageWorkLifetime _lifetime;
    private readonly SvgDocumentCustody _documents;
    private string? _anonymousPartitionKey;
    private int _renderGeneration;
    private SvgTileRenderPlan? _acceptedPlan;
    private SvgTileRenderPlan? _parkingPlan;
    private bool _pressureParked;
    private long _pressureParkPublicationEpoch;
    private long _visualDemandEpoch;

    internal SvgImageState(ImageWorkLifetime lifetime, SvgDocumentCustody documents)
    {
        _lifetime = lifetime;
        _documents = documents;
    }

    /// <summary>The accepted source bytes, held weakly.</summary>
    internal WeakReference<byte[]>? SourceIdentity { get; private set; }

    /// <summary>The accepted source length in bytes.</summary>
    internal long SourceByteLength { get; private set; }

    /// <summary>This box's local security partition for an unkeyed source, once created.</summary>
    internal string? AnonymousPartitionKey => Volatile.Read(ref _anonymousPartitionKey);

    /// <summary>The accepted intrinsic size in DIPs.</summary>
    internal Size IntrinsicSize { get; private set; }

    /// <summary>The SVG &lt;title&gt; value, or null.</summary>
    internal string? Title { get; private set; }

    /// <summary>The SVG &lt;desc&gt; value, or null.</summary>
    internal string? Description { get; private set; }

    /// <summary>The accepted content hash.</summary>
    internal string? ContentHash { get; private set; }

    /// <summary>The accepted security-partition hash.</summary>
    internal string? SecurityPartitionHash { get; private set; }

    /// <summary>The accepted document info.</summary>
    internal MarkdownSvgDocumentInfo? Info { get; private set; }

    /// <summary>The renderer's cache identity at acceptance.</summary>
    internal long RendererIdentity { get; private set; }

    /// <summary>The renderer's cache generation at acceptance.</summary>
    internal long ProviderCacheGeneration { get; private set; }

    /// <summary>The accepted raster width in physical pixels.</summary>
    internal int BitmapWidthPixels { get; private set; }

    /// <summary>The accepted raster height in physical pixels.</summary>
    internal int BitmapHeightPixels { get; private set; }

    /// <summary>The accepted physical viewport.</summary>
    internal MarkdownSvgPhysicalViewport? PhysicalViewport { get; private set; }

    /// <summary>The last SVG failure, if any.</summary>
    internal MarkdownSvgFailureReason? FailureReason { get; private set; }

    /// <summary>The last SVG failure's description, kept only without a good image.</summary>
    internal string? FailureDescription { get; private set; }

    /// <summary>True while the accepted SVG is drawn from bounded visible tiles.</summary>
    internal bool UsesTiles { get; private set; }

    /// <summary>The current render generation.</summary>
    internal int RenderGeneration => Volatile.Read(ref _renderGeneration);

    /// <summary>The accepted tile plan, if the SVG is tiled.</summary>
    internal SvgTileRenderPlan? AcceptedPlan => Volatile.Read(ref _acceptedPlan);

    /// <summary>The plan a pressure park is detaching, if one is in progress.</summary>
    internal SvgTileRenderPlan? ParkingPlan => Volatile.Read(ref _parkingPlan);

    /// <summary>True while a tiled SVG is parked under memory pressure.</summary>
    internal bool IsPressureParked => Volatile.Read(ref _pressureParked);

    /// <summary>The epoch of the latest pressure park's visual update.</summary>
    internal long PressureParkPublicationEpoch => Volatile.Read(ref _pressureParkPublicationEpoch);

    /// <summary>The latest visual-demand epoch.</summary>
    internal long VisualDemandEpoch => Volatile.Read(ref _visualDemandEpoch);

    /// <summary>
    /// True while <paramref name="plan"/> is the accepted plan over the
    /// current document of a live, tiled box. Request epochs order
    /// replacements; only accepted plan and document identity control the
    /// previous good image's bounded tile residency.
    /// </summary>
    internal bool IsAcceptedTilePlan(SvgTileRenderPlan plan) =>
        !_lifetime.IsDisposed &&
        !_lifetime.IsSuspended &&
        !_lifetime.IsFinalizing &&
        UsesTiles &&
        ReferenceEquals(plan, Volatile.Read(ref _acceptedPlan)) &&
        ReferenceEquals(plan.Document, _documents.Current);

    /// <summary>Takes the image-work lock: starts and returns a new render generation.</summary>
    internal int AdvanceRenderGeneration()
    {
        lock (_lifetime.Sync)
            return Interlocked.Increment(ref _renderGeneration);
    }

    /// <summary>
    /// Starts a new render generation only while <paramref name="generation"/>
    /// is still current, so a newer pending replacement keeps its epoch.
    /// </summary>
    internal void AdvanceRenderGenerationIfCurrentNoLock(int generation)
    {
        if (generation == Volatile.Read(ref _renderGeneration))
            Interlocked.Increment(ref _renderGeneration);
    }

    /// <summary>Advances the visual-demand epoch unless it is saturated, and returns it.</summary>
    internal long AdvanceVisualDemandEpoch()
    {
        long current = Volatile.Read(ref _visualDemandEpoch);
        return current < long.MaxValue
            ? Interlocked.Increment(ref _visualDemandEpoch)
            : current;
    }

    /// <summary>Installs or clears the accepted tile plan.</summary>
    internal void SetAcceptedPlanNoLock(SvgTileRenderPlan? plan) => Volatile.Write(ref _acceptedPlan, plan);

    /// <summary>Publishes or clears the pressure-park marker.</summary>
    internal void SetParkingPlanNoLock(SvgTileRenderPlan? plan) => Volatile.Write(ref _parkingPlan, plan);

    /// <summary>Records whether the accepted SVG is tiled.</summary>
    internal void SetUsesTilesNoLock(bool usesTiles) => UsesTiles = usesTiles;

    /// <summary>Records whether the tiled SVG is parked under memory pressure.</summary>
    internal void SetPressureParkedNoLock(bool parked) => _pressureParked = parked;

    /// <summary>Starts a new pressure-park publication epoch and returns it.</summary>
    internal long AdvancePressureParkPublicationEpochNoLock() =>
        Interlocked.Increment(ref _pressureParkPublicationEpoch);

    /// <summary>Weakly remembers <paramref name="source"/> as the accepted source.</summary>
    internal void RememberSource(byte[] source)
    {
        SourceIdentity = new WeakReference<byte[]>(source);
        SourceByteLength = source.LongLength;
    }

    /// <summary>Takes a current SVG source-cache entry's accepted metadata.</summary>
    internal void AdoptCacheEntry(SvgCacheEntry entry)
    {
        RememberSource(entry.RawBytes);
        IntrinsicSize = entry.Intrinsic;
        Title = entry.Title;
        Description = entry.Desc;
        ContentHash = entry.ContentHash;
        SecurityPartitionHash = entry.SecurityPartitionHash;
        Info = entry.Info;
        RendererIdentity = entry.RendererIdentity;
        ProviderCacheGeneration = entry.ProviderCacheGeneration;
    }

    /// <summary>Takes the raster geometry of the cached bitmap a box presents at construction.</summary>
    internal void AdoptCachedBitmapGeometry(int widthPixels, int heightPixels, MarkdownSvgPhysicalViewport? physicalViewport)
    {
        BitmapWidthPixels = widthPixels;
        BitmapHeightPixels = heightPixels;
        PhysicalViewport = physicalViewport;
    }

    /// <summary>Takes a carried permanent failure, with its intrinsic size when the image is an SVG.</summary>
    internal void AdoptCarriedFailure(Size? svgIntrinsicSize, MarkdownSvgFailureReason? reason, string? description)
    {
        if (svgIntrinsicSize is { } intrinsic)
            IntrinsicSize = intrinsic;
        FailureReason = reason;
        FailureDescription = description;
    }

    /// <summary>Takes the accepted state a completed SVG placement fence captured.</summary>
    internal void AdoptPlacementFence(in CompletedSvgPlacementMetadataFence fence)
    {
        SourceIdentity = fence.SourceIdentity;
        _anonymousPartitionKey = fence.AnonymousPartitionKey;
        SourceByteLength = fence.SourceByteLength;
        IntrinsicSize = fence.IntrinsicSize;
        Title = fence.Title;
        Description = fence.Description;
        ContentHash = fence.ContentHash;
        SecurityPartitionHash = fence.SecurityPartitionHash;
        Info = fence.Info;
        RendererIdentity = fence.RendererIdentity;
        ProviderCacheGeneration = fence.ProviderCacheGeneration;
        _renderGeneration = fence.RenderGeneration;
        BitmapWidthPixels = fence.BitmapWidthPixels;
        BitmapHeightPixels = fence.BitmapHeightPixels;
        PhysicalViewport = fence.PhysicalViewport;
        FailureReason = null;
        FailureDescription = null;
    }

    /// <summary>Takes a committed publication's metadata and clears the last failure.</summary>
    internal void ApplyPublicationNoLock(SvgPublicationMetadata metadata)
    {
        SourceIdentity = metadata.SourceIdentity;
        SourceByteLength = metadata.SourceByteLength;
        IntrinsicSize = metadata.Intrinsic;
        Title = metadata.Title;
        Description = metadata.Description;
        ContentHash = metadata.ContentHash;
        SecurityPartitionHash = metadata.SecurityPartitionHash;
        Info = metadata.Info;
        RendererIdentity = metadata.RendererIdentity;
        ProviderCacheGeneration = metadata.ProviderCacheGeneration;
        BitmapWidthPixels = metadata.TargetWidthPixels;
        BitmapHeightPixels = metadata.TargetHeightPixels;
        PhysicalViewport = metadata.PhysicalViewport;
        FailureReason = null;
        FailureDescription = null;
    }

    /// <summary>Records an SVG failure; the description is null while a good image remains.</summary>
    internal void SetFailure(MarkdownSvgFailureReason reason, string? description)
    {
        FailureReason = reason;
        FailureDescription = description;
    }

    /// <summary>Clears the last SVG failure.</summary>
    internal void ClearFailure()
    {
        FailureReason = null;
        FailureDescription = null;
    }

    /// <summary>
    /// Clears the source and cache identity references of a retired box and
    /// its accepted tile plan. The accessibility title and description, the
    /// intrinsic size and the raster geometry stay.
    /// </summary>
    internal void ClearRetiredSourceStateNoLock()
    {
        SourceIdentity = null;
        SourceByteLength = 0;
        ContentHash = null;
        SecurityPartitionHash = null;
        Info = null;
        RendererIdentity = 0;
        ProviderCacheGeneration = 0;
        Volatile.Write(ref _acceptedPlan, null);
    }

    /// <summary>
    /// The resolver's security partition, or this box's anonymous local
    /// partition when the resolved asset has no key. Only the resolver can
    /// authorize process-wide reuse: an unkeyed resolved asset stays local to
    /// this box, including its source, bitmap, failure and admission
    /// identities. Trust is never inferred from its URL.
    /// </summary>
    internal string GetSecurityPartitionKey(string resolvedCacheKey)
    {
        if (!string.IsNullOrEmpty(resolvedCacheKey))
            return resolvedCacheKey;
        string? localKey = Volatile.Read(ref _anonymousPartitionKey);
        if (localKey is not null)
            return localKey;
        string created = "svg-local:" + Guid.NewGuid().ToString("N");
        return Interlocked.CompareExchange(ref _anonymousPartitionKey, created, null) ?? created;
    }

    /// <summary>True when <paramref name="candidate"/> is the accepted source in the same partition.</summary>
    internal bool IsKnownSource(byte[] candidate, string currentSecurityPartitionHash)
    {
        WeakReference<byte[]>? identity = SourceIdentity;
        return Info is not null &&
               identity is not null &&
               identity.TryGetTarget(out byte[]? known) &&
               ReferenceEquals(known, candidate) &&
               string.Equals(
                   SecurityPartitionHash,
                   currentSecurityPartitionHash,
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// When <paramref name="rawBytes"/> is the accepted source, returns its
    /// accepted preflight metadata with <paramref name="intrinsicHint"/>.
    /// </summary>
    internal bool TryGetKnownPreflight(
        byte[] rawBytes, string securityPartitionHash, Size intrinsicHint, out SvgPreflight preflight)
    {
        if (IsKnownSource(rawBytes, securityPartitionHash) &&
            ContentHash is { } cachedContentHash &&
            SecurityPartitionHash is { } cachedPartitionHash)
        {
            preflight = (
                intrinsicHint,
                Title,
                Description,
                cachedContentHash,
                cachedPartitionHash);
            return true;
        }

        preflight = default;
        return false;
    }

    /// <summary>
    /// True while <paramref name="renderer"/> still has the accepted cache
    /// identity and, for text-bearing SVGs, the accepted cache generation.
    /// </summary>
    internal bool IsRendererCurrent(IMarkdownSvgRenderer? renderer, ImageProviderIdentities identities)
    {
        try
        {
            return renderer is not null &&
                   identities.GetSvgRendererIdentity(renderer) == RendererIdentity &&
                   (Info?.HasText != true ||
                    renderer.CacheGeneration == ProviderCacheGeneration);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True while nothing of an SVG was ever accepted, rendered, planned or
    /// failed: the state of a never-started placement descriptor.
    /// </summary>
    internal bool IsPristineNoLock =>
        SourceIdentity is null && SourceByteLength == 0 &&
        _anonymousPartitionKey is null &&
        IntrinsicSize.Width == 0 && IntrinsicSize.Height == 0 &&
        Title is null && Description is null &&
        ContentHash is null && SecurityPartitionHash is null &&
        Info is null &&
        RendererIdentity == 0 && ProviderCacheGeneration == 0 &&
        Volatile.Read(ref _renderGeneration) == 0 &&
        BitmapWidthPixels == 0 && BitmapHeightPixels == 0 &&
        Volatile.Read(ref _acceptedPlan) is null &&
        Volatile.Read(ref _parkingPlan) is null &&
        !UsesTiles && !_pressureParked &&
        FailureReason is null && FailureDescription is null;
}
