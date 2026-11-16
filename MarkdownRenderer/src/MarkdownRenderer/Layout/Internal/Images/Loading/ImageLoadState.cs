using System.Threading;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Placement;

namespace MarkdownRenderer.Layout.Internal.Images.Loading;

/// <summary>
/// Where an image box's load stands: the representation it expects (SVG or
/// raster), whether a load has started or failed, whether a failure may be
/// carried into a rebuild, the unavailable-report latch, the active cache
/// identity, a completed result carried from an earlier layout, and the
/// current load demand with its generation.
/// </summary>
/// <remarks>
/// The flags keep the box's original memory semantics: the representation
/// and started flags are volatile, the demand and its generation use
/// volatile reads and an interlocked advance, and the rest are plain fields
/// written on the UI thread or under the image-work lock.
/// </remarks>
internal sealed class ImageLoadState
{
    private volatile bool _isSvg;
    private volatile bool _started;
    private int _unavailableReported;
    private string? _activeCacheKey;
    private MarkdownSvgRenderDemand? _demand;
    private int _demandGeneration;

    /// <summary>
    /// True when the active representation is SVG. Before host resolution this
    /// may reflect a source-URI hint; resolved bytes and MIME metadata supersede it.
    /// </summary>
    internal bool IsSvg
    {
        get => _isSvg;
        set => _isSvg = value;
    }

    /// <summary>True once a load attempt started and has not been reset.</summary>
    internal bool IsStarted
    {
        get => _started;
        set => _started = value;
    }

    /// <summary>True while the box shows its failure placeholder.</summary>
    internal bool IsFailed { get; set; }

    /// <summary>
    /// True while the current failure is permanent (remembered or
    /// deterministic), so a scene rebuild may carry it.
    /// </summary>
    internal bool IsFailureCarryable { get; set; }

    /// <summary>The cache identity of the current source, once known.</summary>
    internal string? ActiveCacheKey
    {
        get => Volatile.Read(ref _activeCacheKey);
        set => _activeCacheKey = value;
    }

    /// <summary>
    /// Completed result carried from an earlier layout of the same document.
    /// Immutable; never owns pixels or native resources.
    /// </summary>
    internal CompletedImageCarry? CarriedCompletion { get; set; }

    /// <summary>The demand of the current load, if any.</summary>
    internal MarkdownSvgRenderDemand? Demand => Volatile.Read(ref _demand);

    /// <summary>The generation of the current load demand; zero before any load.</summary>
    internal int DemandGeneration => Volatile.Read(ref _demandGeneration);

    /// <summary>Starts a new load demand and returns its generation.</summary>
    internal int BeginDemand(MarkdownSvgRenderDemand demand)
    {
        int generation = Interlocked.Increment(ref _demandGeneration);
        Volatile.Write(ref _demand, demand);
        return generation;
    }

    /// <summary>Takes the demand generation of the placement a clone copies.</summary>
    internal void AdoptDemandGeneration(int generation) => _demandGeneration = generation;

    /// <summary>Lets the next published failure reach the host again.</summary>
    internal void ResetUnavailableReport() => Volatile.Write(ref _unavailableReported, 0);

    /// <summary>Records that the host already heard about this load's failure.</summary>
    internal void MarkUnavailableReported() => Volatile.Write(ref _unavailableReported, 1);

    /// <summary>Claims the one unavailable report of this load; false when it was made.</summary>
    internal bool TryClaimUnavailableReport() =>
        Interlocked.CompareExchange(ref _unavailableReported, 1, 0) == 0;
}
