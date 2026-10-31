using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

// Borrowed only during descriptor capture. The candidate converts these to weak
// identities before it can be retained for UI publication.
internal readonly struct RasterPlacementSourceFence(
    ImageBox Image, SharedCanvasBitmapCache.Lease? Lease,
    ImageBox.CompletedSvgPlacementMetadataFence? SvgMetadata = null,
    bool IsIdleDescriptor = false,
    int IdleLoadDemandGeneration = 0,
    bool WasSubscribed = true)
{
    public ImageBox Image { get; init; } = Image;
    public SharedCanvasBitmapCache.Lease? Lease { get; init; } = Lease;
    public ImageBox.CompletedSvgPlacementMetadataFence? SvgMetadata { get; init; } = SvgMetadata;
    public bool IsIdleDescriptor { get; init; } = IsIdleDescriptor;
    public int IdleLoadDemandGeneration { get; init; } = IdleLoadDemandGeneration;
    public bool WasSubscribed { get; init; } = WasSubscribed;
}

internal readonly struct HorizontalPlacementSourceFence(IHorizontalOverflowBox Box, double Offset)
{
    public IHorizontalOverflowBox Box { get; init; } = Box;
    public double Offset { get; init; } = Offset;
}

// These strong identities exist only during bounded source capture. The owner
// stores the old code box weakly and never owns its retirement lifetime.
internal readonly struct CodeBlockPlacementSourceFence(
    CodeBlockBox Source, CodeBlockBox Placement, long Revision, bool WasMeasured = true)
{
    public CodeBlockBox Source { get; init; } = Source;
    public CodeBlockBox Placement { get; init; } = Placement;
    public long Revision { get; init; } = Revision;
    public bool WasMeasured { get; init; } = WasMeasured;
}

internal readonly struct CodeBlockPlacementIdentity(
    WeakReference<CodeBlockBox> Source, CodeBlockBox Placement, long Revision, bool WasMeasured)
{
    public WeakReference<CodeBlockBox> Source { get; init; } = Source;
    public CodeBlockBox Placement { get; init; } = Placement;
    public long Revision { get; init; } = Revision;
    public bool WasMeasured { get; init; } = WasMeasured;
}

/// <summary>
/// Strong, one-shot ownership of one unpublished private placement tree.
/// The historical name does not restrict the root or completed raster count.
/// Only explicit immutable
/// bitmap leases are shared. Source identities are weak and never own retirement.
/// The worker drains construction work before offering this owner to the UI.
/// </summary>
internal sealed class SingleRasterLayoutCandidate : IDisposable
{
    private LayoutSnapshot? _snapshot;
    private MarkdownLayoutContext? _context;
    private readonly SourceIdentity[] _sources;
    private readonly HorizontalIdentity[] _horizontalSources;
    private readonly int _sourceSubscribedImageCount;
    private CodeBlockPlacementIdentity[] _codePlacements;

    private readonly struct SourceIdentity(
        WeakReference<ImageBox> Image,
        WeakReference<SharedCanvasBitmapCache.Lease>? Lease,
        ImageBox.CompletedSvgPlacementMetadataFence? SvgMetadata,
        bool IsIdleDescriptor,
        int IdleLoadDemandGeneration,
        bool WasSubscribed)
    {
        public WeakReference<ImageBox> Image { get; init; } = Image;
        public WeakReference<SharedCanvasBitmapCache.Lease>? Lease { get; init; } = Lease;
        public ImageBox.CompletedSvgPlacementMetadataFence? SvgMetadata { get; init; } = SvgMetadata;
        public bool IsIdleDescriptor { get; init; } = IsIdleDescriptor;
        public int IdleLoadDemandGeneration { get; init; } = IdleLoadDemandGeneration;
        public bool WasSubscribed { get; init; } = WasSubscribed;
    }

    private readonly struct HorizontalIdentity(WeakReference<IHorizontalOverflowBox> Box, double Offset)
    {
        public WeakReference<IHorizontalOverflowBox> Box { get; init; } = Box;
        public double Offset { get; init; } = Offset;
    }

    internal SingleRasterLayoutCandidate(LayoutSnapshot snapshot, MarkdownLayoutContext context,
        RasterPlacementSourceFence[] sources, long sourceRevision, float width,
        HorizontalPlacementSourceFence[]? horizontalSources = null,
        CodeBlockPlacementSourceFence[]? codeSources = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length == 0)
            throw new ArgumentException("A raster placement candidate needs source identities.", nameof(sources));
        _sources = new SourceIdentity[sources.Length];
        for (int index = 0; index < sources.Length; index++)
        {
            RasterPlacementSourceFence source = sources[index];
            ArgumentNullException.ThrowIfNull(source.Image);
            if (source.IsIdleDescriptor
                    ? source.Lease is not null || source.SvgMetadata is not null || source.IdleLoadDemandGeneration != 0
                    : source.Lease is null || source.IdleLoadDemandGeneration != 0)
                throw new ArgumentException("The image placement source fence is inconsistent.", nameof(sources));
            _sources[index] = new(new(source.Image), source.Lease is { } lease ? new(lease) : null,
                source.SvgMetadata, source.IsIdleDescriptor, source.IdleLoadDemandGeneration, source.WasSubscribed);
            if (source.WasSubscribed)
                _sourceSubscribedImageCount++;
        }
        _horizontalSources = new HorizontalIdentity[horizontalSources?.Length ?? 0];
        for (int index = 0; index < _horizontalSources.Length; index++)
        {
            HorizontalPlacementSourceFence source = horizontalSources![index];
            ArgumentNullException.ThrowIfNull(source.Box);
            _horizontalSources[index] = new(new(source.Box), source.Offset);
        }
        _codePlacements = new CodeBlockPlacementIdentity[codeSources?.Length ?? 0];
        for (int index = 0; index < _codePlacements.Length; index++)
        {
            CodeBlockPlacementSourceFence source = codeSources![index];
            ArgumentNullException.ThrowIfNull(source.Source);
            ArgumentNullException.ThrowIfNull(source.Placement);
            if (ReferenceEquals(source.Source, source.Placement) ||
                source.Source.BlockIndex != source.Placement.BlockIndex ||
                !ReferenceEquals(source.Source.Metadata, source.Placement.Metadata) ||
                !ReferenceEquals(source.Source.CodeText, source.Placement.CodeText))
                throw new ArgumentException("The code placement identity is inconsistent.", nameof(codeSources));
            _codePlacements[index] = new(new(source.Source), source.Placement, source.Revision, source.WasMeasured);
        }
        _snapshot = snapshot;
        _context = context;
        SourceRevision = sourceRevision;
        Width = width;
    }

    internal long SourceRevision { get; }
    internal float Width { get; }
    internal int SourceImageCount => _sources.Length;
    internal int SourceSubscribedImageCount => _sourceSubscribedImageCount;
    internal IReadOnlyList<CodeBlockPlacementIdentity> CodePlacements => Volatile.Read(ref _codePlacements);
    internal LayoutSnapshot Snapshot => _snapshot ?? throw new ObjectDisposedException(nameof(SingleRasterLayoutCandidate));
    internal MarkdownLayoutContext Context => _context ?? throw new ObjectDisposedException(nameof(SingleRasterLayoutCandidate));

    internal Task DrainConstructionAsync() => _context?.DrainOwnedRetirementsAsync() ?? Task.CompletedTask;

    internal bool HasCurrentSourceRaster()
    {
        if (Volatile.Read(ref _snapshot) is null)
            return false;
        foreach (SourceIdentity source in _sources)
            if (!IsCurrentSource(source, source.SvgMetadata?.ProviderCacheGeneration ?? 0))
                return false;
        // Horizontal scrolling does not advance LayoutRevision. A user may
        // scroll a source table while the private tree is being measured;
        // never publish a candidate that restores an obsolete scroll offset.
        foreach (HorizontalIdentity source in _horizontalSources)
            if (!source.Box.TryGetTarget(out IHorizontalOverflowBox? box) ||
                box.HorizontalOffset != source.Offset)
                return false;
        foreach (CodeBlockPlacementIdentity source in Volatile.Read(ref _codePlacements))
            if (!source.Source.TryGetTarget(out CodeBlockBox? box) ||
                !box.IsCurrentPrivatePlacement(source.Revision))
                return false;
        return true;
    }

    // The external provider getter must run before the control's local commit
    // fence, never under a snapshot/image lock. The public provider contract
    // requires an inexpensive, thread-safe generation snapshot. Callers still
    // recheck all local identities after any possible provider reentrancy.
    internal bool HasCurrentSourceProviders()
    {
        MarkdownLayoutContext? context = Volatile.Read(ref _context);
        if (context is null || Volatile.Read(ref _snapshot) is null)
            return false;
        bool hasSvg = false;
        bool hasText = false;
        foreach (SourceIdentity source in _sources)
        {
            hasSvg |= source.SvgMetadata is not null;
            hasText |= source.SvgMetadata?.Info.HasText == true;
        }
        if (!TryCaptureProviderGeneration(context, hasSvg, hasText, out long generation) ||
            !ReferenceEquals(context, Volatile.Read(ref _context)) || Volatile.Read(ref _snapshot) is null)
            return false;
        foreach (SourceIdentity source in _sources)
            if (!IsCurrentSource(source, generation))
                return false;
        return true;
    }

    // Construction-time provider/local validation after descriptor capture
    // releases the source gate, and before private native Measure/Arrange.
    internal static bool AreSourceProvidersCurrent(
        IReadOnlyList<RasterPlacementSourceFence> sources, MarkdownLayoutContext context)
        => AreSourceProvidersCurrent(sources, context, out _);

    internal static bool AreSourceProvidersCurrent(
        IReadOnlyList<RasterPlacementSourceFence> sources, MarkdownLayoutContext context,
        out long providerGeneration)
    {
        providerGeneration = 0;
        bool hasSvg = false;
        bool hasText = false;
        foreach (RasterPlacementSourceFence source in sources)
        {
            hasSvg |= source.SvgMetadata is not null;
            hasText |= source.SvgMetadata?.Info.HasText == true;
        }
        if (!TryCaptureProviderGeneration(context, hasSvg, hasText, out long generation))
            return false;
        providerGeneration = generation;
        foreach (RasterPlacementSourceFence source in sources)
            if (source.IsIdleDescriptor
                    ? !source.Image.IsCurrentIdleImagePlacement(source.IdleLoadDemandGeneration)
                    : source.Lease is null || !source.Image.IsCurrentCompletedRasterPlacement(source.Lease, source.SvgMetadata, generation))
                return false;
        return true;
    }

    private static bool IsCurrentSource(SourceIdentity source, long providerGeneration)
    {
        if (!source.Image.TryGetTarget(out ImageBox? image))
            return false;
        if (source.IsIdleDescriptor)
            return image.IsCurrentIdleImagePlacement(source.IdleLoadDemandGeneration);
        return source.Lease is { } leaseIdentity &&
               leaseIdentity.TryGetTarget(out SharedCanvasBitmapCache.Lease? lease) &&
               image.IsCurrentCompletedRasterPlacement(lease, source.SvgMetadata, providerGeneration);
    }

    private static bool TryCaptureProviderGeneration(
        MarkdownLayoutContext context, bool hasSvg, bool hasText, out long generation)
    {
        generation = 0;
        if (!hasSvg)
            return true;
        try
        {
            if (context.SvgRenderer is not { } renderer)
                return false;
            if (hasText)
                generation = renderer.CacheGeneration;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Matching counts alone is insufficient: replacement or reordered plans
    // must never adopt candidates captured from a different set of placements.
    // This bounded, allocation-free check runs only on the publishing UI thread.
    internal bool MatchesSourceImages(IReadOnlyList<ImageBox> images, IReadOnlySet<ImageBox> subscriptions)
    {
        if (Volatile.Read(ref _snapshot) is null || images.Count != _sourceSubscribedImageCount ||
            subscriptions.Count != _sourceSubscribedImageCount)
            return false;
        int ordinal = 0;
        foreach (SourceIdentity source in _sources)
        {
            if (!source.WasSubscribed)
                continue;
            if (!source.Image.TryGetTarget(out ImageBox? image) ||
                !ReferenceEquals(images[ordinal++], image) || !subscriptions.Contains(image))
                return false;
        }
        return true;
    }

    // The UI calls this only after all fences and before callback-capable work.
    internal LayoutSnapshot TransferToControl()
    {
        LayoutSnapshot snapshot = Interlocked.Exchange(ref _snapshot, null) ??
            throw new ObjectDisposedException(nameof(SingleRasterLayoutCandidate));
        Interlocked.Exchange(ref _context, null);
        Interlocked.Exchange(ref _codePlacements, Array.Empty<CodeBlockPlacementIdentity>());
        return snapshot;
    }

    public void Dispose()
    {
        LayoutSnapshot? snapshot = Interlocked.Exchange(ref _snapshot, null);
        MarkdownLayoutContext? context = Interlocked.Exchange(ref _context, null);
        Interlocked.Exchange(ref _codePlacements, Array.Empty<CodeBlockPlacementIdentity>());
        if (snapshot is not null)
            context?.RegisterRetirement(snapshot.Retire());
    }
}
