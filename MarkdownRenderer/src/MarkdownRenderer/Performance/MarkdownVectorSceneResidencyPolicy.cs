using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Owns residency pins for native vector resources in the realized layout band.
/// Eligible completed block graphs can be detached and refilled through their
/// plan. Participating drawings share weighted native admission; denied scenes
/// keep readable fallback while awaiting a capacity-generation wake. Neither
/// the graph estimate nor that complexity weight is an RSS guarantee.
/// </summary>
internal sealed class MarkdownVectorSceneResidencyPolicy : IMarkdownVectorSceneResidencyPolicy
{
    // Command graphs are immutable and may be visited by more than one layout
    // snapshot during resize/rebuild. Keep only the scalar estimate here: the
    // weak key must never become another owner of a scene graph.
    private static readonly ConditionalWeakTable<MarkdownVectorScene, SceneResourceEstimate> s_resourceEstimates = new();

    private IReadOnlyList<BlockBox> _blocks;
    private MarkdownProgressiveScenePlan? _graphPlan;
    private readonly List<VectorSceneBox> _pendingGraphRebinds = [];
    private readonly List<VectorSceneBox> _graphStatusChanges = [];
    private readonly VectorNativeResourceAdmission? _nativeAdmission;
    private LazyLayoutBand? _pendingGraphBand;
    private Dictionary<object, SceneEntry> _activeScenes =
        new(ReferenceEqualityComparer.Instance);
    // Pending fallback/rebound native resources are owned separately from the
    // last committed band. Superseding a pending band retires these pins without
    // touching the still-good committed band.
    private readonly Dictionary<object, SceneEntry> _transientScenes =
        new(ReferenceEqualityComparer.Instance);
    private Dictionary<InlineContainerBox, InlineSceneGeometryIndex> _activeInlineSceneIndexes =
        new(ReferenceEqualityComparer.Instance);
    // LayoutSnapshot serializes policy calls with its paint/read lock. Reuse
    // this buffer for readiness probes so they do not retain a second document
    // graph or allocate a fresh per-scene array on every viewport check.
    private readonly List<SceneEntry> _readinessScratch = [];
    private readonly HashSet<object> _readinessSeen = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _visibleDeferredSceneMarkerScratch = [];
    private LazyLayoutBand? _activeBand;
    private long _activeEstimatedBytes;
    private int _inlineGeometryIndexBuildCount;
    private int _commandGraphEstimateCount;
    private bool _disposed;
    private bool _hasCapacityDeferredScenes;
    private long _activeCapacityGeneration;

    internal MarkdownVectorSceneResidencyPolicy(
        IReadOnlyList<BlockBox> blocks,
        MarkdownProgressiveScenePlan? graphPlan = null,
        VectorNativeResourceAdmission? nativeAdmission = null)
    {
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _graphPlan = graphPlan;
        _nativeAdmission = nativeAdmission;
    }

    public bool HasPendingGraphRebinds => _pendingGraphRebinds.Count != 0;
    public bool HasCapacityDeferredScenes => Volatile.Read(ref _hasCapacityDeferredScenes);
    public long ActiveCapacityGeneration => Volatile.Read(ref _activeCapacityGeneration);
    public IReadOnlyList<VectorSceneBox> GraphStatusChanges => _graphStatusChanges;
    public void ClearGraphStatusChanges() => _graphStatusChanges.Clear();
    internal void RecordGraphStatusChange(VectorSceneBox box)
    {
        if (!_graphStatusChanges.Contains(box))
            _graphStatusChanges.Add(box);
    }

    internal IReadOnlyList<VectorSceneBox> PendingGraphRebinds => _pendingGraphRebinds;

    internal bool CanCommitGraphRebind(VectorSceneBox box, LazyLayoutBand band) =>
        !_disposed && _pendingGraphBand is { } pending &&
        pending.Top == band.Top && pending.Bottom == band.Bottom && _pendingGraphRebinds.Contains(box);

    internal bool AdoptGraphRebind(
        VectorSceneBox box, LazyLayoutBand band, VectorSceneDrawing replacement, long estimate)
    {
        if (!CanCommitGraphRebind(box, band) || !box.IsGraphDetached ||
            (_nativeAdmission is null && _activeScenes.ContainsKey(box)))
            return false;
        // Reserve the pin before adoption: even an allocation failure cannot
        // leave a prepared replacement outside policy retirement ownership.
        bool wasTracked = _transientScenes.TryGetValue(box, out SceneEntry previous);
        _transientScenes[box] = new SceneEntry(box, estimate);
        try
        {
            box.AdoptPreparedReplacement(replacement);
        }
        catch
        {
            if (wasTracked)
                _transientScenes[box] = previous;
            else
                _transientScenes.Remove(box);
            throw;
        }
        if (_activeScenes.TryGetValue(box, out SceneEntry priorActive))
        {
            _activeScenes[box] = new SceneEntry(box, estimate);
            _transientScenes.Remove(box);
            _activeEstimatedBytes = SaturatingAdd(
                Math.Max(0, _activeEstimatedBytes - priorActive.EstimatedBytes), estimate);
        }
        if (box.NativePreparationStatus == VectorNativePreparationStatus.DeferredCapacity)
        {
            Volatile.Write(ref _hasCapacityDeferredScenes, true);
            // The failed attempt's generation must survive later refunds. The
            // control registers then rechecks, closing a stationary missed wake.
            Volatile.Write(ref _activeCapacityGeneration,
                Math.Min(ActiveCapacityGeneration, replacement.PreparationOutcome.CapacityGeneration));
        }
        return true;
    }

    public int ActiveSceneCount => _activeScenes.Count + _transientScenes.Count;

    /// <summary>Weighted diagnostic estimate, not an allocation-size guarantee.</summary>
    public long ActiveEstimatedBytes
    {
        get
        {
            long estimate = _activeEstimatedBytes;
            foreach (SceneEntry entry in _transientScenes.Values)
                estimate = SaturatingAdd(estimate, entry.EstimatedBytes);
            return estimate;
        }
    }

    public int InlineGeometryIndexBuildCount => _inlineGeometryIndexBuildCount;

    public int CommandGraphEstimateCount => Volatile.Read(ref _commandGraphEstimateCount);

    public int ActiveInlineSceneGeometryIndexCount => _activeInlineSceneIndexes.Count;

    /// <summary>
    /// Proves that a small, known subset of a newly built tree cannot contain
    /// deferred vector-scene residency state. This runs before a snapshot or
    /// its layout gate exists, so it reads only immutable construction shape.
    /// Unknown or over-bound trees retain the normal policy.
    /// </summary>
    internal static bool CanOmitForPrivatePlacement(IReadOnlyList<BlockBox> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count > PrivateRasterPlacementTree.MaximumRoots)
            return false;

        var pending = new Stack<(BlockBox Box, int Depth)>(
            Math.Min(blocks.Count, PrivateRasterPlacementTree.MaximumNodes));
        var visited = new HashSet<BlockBox>(ReferenceEqualityComparer.Instance);
        var selectableBlockIndices = new HashSet<int>();
        var images = new HashSet<ImageBox>(ReferenceEqualityComparer.Instance);
        MarkdownLayoutContext? sourceContext = null;
        int visitedCount = 0;
        int runCount = 0;
        int imageCount = 0;
        long textLength = 0;
        long styleKeyCopyEntries = 0;

        for (int rootIndex = blocks.Count - 1; rootIndex >= 0; rootIndex--)
        {
            BlockBox? root = blocks[rootIndex];
            if (root is null || pending.Count >= PrivateRasterPlacementTree.MaximumNodes)
                return false;
            pending.Push((root, 0));
        }

        while (pending.Count > 0)
        {
            (BlockBox box, int depth) = pending.Pop();
            if (depth > PrivateRasterPlacementTree.MaximumDepth ||
                ++visitedCount > PrivateRasterPlacementTree.MaximumNodes ||
                !visited.Add(box))
            {
                return false;
            }

            switch (box)
            {
                case ImageBox image:
                    if (image.BlockIndex <= 0 ||
                        image.Context is null ||
                        !selectableBlockIndices.Add(image.BlockIndex) ||
                        (sourceContext is not null && !ReferenceEquals(sourceContext, image.Context)) ||
                        imageCount >= InlineContainerBox.MaximumPrivateRasterPlacementImages ||
                        !images.Add(image))
                    {
                        return false;
                    }

                    sourceContext ??= image.Context;
                    imageCount++;
                    break;

                case InlineContainerBox inline:
                {
                    if (inline.GetType() != typeof(InlineContainerBox) ||
                        !PrivateRasterPlacementTree.CaptureState.IsSupportedInlineElement(inline.ElementKey) ||
                        inline.SafeHtmlInlineFlowScopeId is not null ||
                        inline.HasInlineEmbeds || inline.HasDeferredSceneMarker ||
                        inline.VectorSceneRunCount != 0 ||
                        !selectableBlockIndices.Add(inline.BlockIndex) ||
                        (sourceContext is not null && !ReferenceEquals(sourceContext, inline.Context)) ||
                        !inline.TryGetPrivateRasterPlacementStyleKeyCopyEntries(
                            out long inlineStyleKeyCopyEntries) ||
                        !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                            ref styleKeyCopyEntries, inlineStyleKeyCopyEntries))
                    {
                        return false;
                    }

                    sourceContext ??= inline.Context;
                    IReadOnlyList<InlineRun> runs = inline.Runs;
                    if (runs.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount)
                        return false;

                    int inlineImageCount = 0;
                    long inlineTextLength = 0;
                    for (int index = 0; index < runs.Count; index++)
                    {
                        InlineRun run = runs[index];
                        switch (run)
                        {
                            case LinkRun { IsDisclosure: false, IsExpanded: false }:
                                break;
                            case InlineImageRun imageRun:
                                inlineImageCount++;
                                if (imageCount >= InlineContainerBox.MaximumPrivateRasterPlacementImages ||
                                    !images.Add(imageRun.Image))
                                {
                                    return false;
                                }
                                imageCount++;
                                break;
                            default:
                                if (!PrivateTextRunPlacement.IsSupported(run))
                                    return false;
                                break;
                        }

                            inlineTextLength += run.TextLength;
                        if (inlineTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength)
                            return false;
                    }

                    if (inlineImageCount != inline.InlineImageRuns.Count ||
                        inlineTextLength != inline.PrivateRasterPlacementTextLength ||
                        inlineTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength - textLength)
                    {
                        return false;
                    }

                    textLength += inlineTextLength;
                    int foregroundSpanCount = inline.PrivateRasterPlacementForegroundSpanCount;
                    if (foregroundSpanCount < 0 ||
                        foregroundSpanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runs.Count ||
                        runs.Count + foregroundSpanCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount)
                        return false;
                    runCount += runs.Count + foregroundSpanCount;
                    break;
                }

                case CodeBlockBox code when code.SafeHtmlInlineFlowScopeId is null &&
                                           (sourceContext is null || ReferenceEquals(sourceContext, code.Context)):
                {
                    if (code.BlockIndex <= 0 || !selectableBlockIndices.Add(code.BlockIndex) ||
                        !code.TryGetPrivatePlacementCounts(code.Context, allowUnmeasured: true,
                            out int lineCount, out long codeStyleKeys) ||
                        lineCount > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count ||
                        !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                            ref styleKeyCopyEntries, codeStyleKeys))
                        return false;
                    sourceContext ??= code.Context;
                    visitedCount += lineCount;
                    if (code.Chunks.Count > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count)
                        return false;
                    for (int index = code.Chunks.Count - 1; index >= 0; index--)
                        pending.Push((code.Chunks[index], depth + 1));
                    break;
                }

                case StackBox stack when stack.GetType() == typeof(StackBox) &&
                                         stack.IsPrivateRasterPlacementCloneEligible &&
                                         stack.SafeHtmlInlineFlowScopeId is null:
                {
                    IReadOnlyList<BlockBox> children = stack.Children;
                    if (children.Count > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count)
                        return false;
                    for (int index = children.Count - 1; index >= 0; index--)
                        pending.Push((children[index], depth + 1));
                    break;
                }

                case ListItemBox listItem when listItem.GetType() == typeof(ListItemBox) &&
                                               listItem.SafeHtmlInlineFlowScopeId is null:
                    if (2 > PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count)
                        return false;
                    pending.Push((listItem.Content, depth + 1));
                    pending.Push((listItem.Marker, depth + 1));
                    break;

                case TableBox table when table.GetType() == typeof(TableBox) &&
                                         table.SafeHtmlInlineFlowScopeId is null &&
                                         (sourceContext is null || ReferenceEquals(sourceContext, table.Context)):
                {
                    sourceContext ??= table.Context;
                    int remainingNodes = PrivateRasterPlacementTree.MaximumNodes - visitedCount - pending.Count;
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                    {
                        if (remainingNodes-- <= 0)
                            return false;
                        pending.Push((cell, depth + 1));
                    }
                    break;
                }

                default:
                    return false;
            }
        }

        return true;
    }

    public List<string> VisibleDeferredSceneMarkerScratch => _visibleDeferredSceneMarkerScratch;

    public bool AreNativeResourcesPrepared(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
        => IsBandPrepared(
            topLevelOrdinals,
            band,
            placements,
            topLevelOrdinalByBlockIndex,
            acceptTerminalFailure: false);

    public bool IsBandWorkSettled(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
        => IsBandPrepared(
            topLevelOrdinals,
            band,
            placements,
            topLevelOrdinalByBlockIndex,
            acceptTerminalFailure: true);

    public bool HasFailedScenes(
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
        if (_disposed || placements is null || topLevelOrdinalByBlockIndex is null ||
            placements.Count != _blocks.Count || !IsValidBand(band))
            return false;

        foreach (object owner in _activeScenes.Keys)
        {
            if (owner is VectorSceneBox { GraphRebindFailed: true } box &&
                IsFailedSceneVisible(box, band, placements, topLevelOrdinalByBlockIndex))
                return true;
        }
        foreach (object owner in _transientScenes.Keys)
        {
            if (owner is VectorSceneBox { GraphRebindFailed: true } box &&
                IsFailedSceneVisible(box, band, placements, topLevelOrdinalByBlockIndex))
                return true;
        }
        return false;
    }

    private bool IsBandPrepared(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        bool acceptTerminalFailure)
    {
        if (_disposed || topLevelOrdinals is null || placements is null ||
            topLevelOrdinalByBlockIndex is null || placements.Count != _blocks.Count || !IsValidBand(band))
            return false;

        _readinessScratch.Clear();
        _readinessSeen.Clear();
        _visibleDeferredSceneMarkerScratch.Clear();
        try
        {
            foreach (int ordinal in topLevelOrdinals)
            {
                if (!TryGetRootLocalBand(
                        ordinal,
                        band,
                        placements,
                        topLevelOrdinalByBlockIndex,
                        out LazyLayoutBand rootLocalBand))
                    return false;

                // The prepared inline index is pixel-snapped in its root's
                // published placement. Query with that same placement, or a
                // fractional translation shifts the local band outside the
                // index even when all of its scenes are already prepared.
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (!TryCollectVisibleBlockScenes(
                        _blocks[ordinal],
                        rootLocalBand,
                        _readinessScratch,
                        _readinessSeen,
                        _activeInlineSceneIndexes,
                        allowIndexBuild: false,
                        CancellationToken.None))
                {
                    return false;
                }
            }

            // The realized pin band may intentionally include overscan. A
            // viewport sub-band is ready when covered by that lease, while a
            // scroll beyond its edge (including a text-only band) must request
            // a new exchange so old scenes are retired.
            if (_activeBand is not { } activeBand ||
                band.Top < activeBand.Top || band.Bottom > activeBand.Bottom)
            {
                return false;
            }

            if (_readinessScratch.Count > _activeScenes.Count)
                return false;

            foreach (SceneEntry entry in _readinessScratch)
            {
                if (!_activeScenes.ContainsKey(entry.Owner) ||
                    (!IsPrepared(entry) && !(acceptTerminalFailure &&
                        (IsCapacityFallback(entry) ||
                         entry.Owner is VectorSceneBox { IsGraphDetached: true, GraphRebindFailed: true }))))
                    return false;
            }

            return true;
        }
        finally
        {
            _readinessScratch.Clear();
            _readinessSeen.Clear();
        }
    }

    public bool ReplaceActiveBand(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(topLevelOrdinals);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(topLevelOrdinalByBlockIndex);
        if (placements.Count != _blocks.Count || !IsValidBand(band))
            throw new ArgumentOutOfRangeException(nameof(band));

        _pendingGraphRebinds.Clear();
        _pendingGraphBand = null;

        List<SceneEntry> requested = [];
        var requestedByOwner = new Dictionary<object, SceneEntry>(ReferenceEqualityComparer.Instance);
        var requestedInlineIndexes = new Dictionary<InlineContainerBox, InlineSceneGeometryIndex>(
            ReferenceEqualityComparer.Instance);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (int ordinal in topLevelOrdinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            using LayoutPlacementTransformContext.Scope placementScope =
                LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
            if (!TryGetRootLocalBand(
                    ordinal,
                    band,
                    placements,
                    topLevelOrdinalByBlockIndex,
                    out LazyLayoutBand rootLocalBand))
                throw new InvalidOperationException("The vector-scene residency request contains an invalid top-level ordinal.");

            if (!TryCollectVisibleBlockScenes(
                    _blocks[ordinal],
                    rootLocalBand,
                    requested,
                    seen,
                    requestedInlineIndexes,
                    allowIndexBuild: true,
                    cancellationToken))
                throw new InvalidOperationException(
                    "Visible vector-scene layout geometry is unavailable; native resources were not published.");
        }

        List<SceneEntry>? obsoleteTransient = null;
        foreach ((object owner, SceneEntry entry) in _transientScenes)
        {
            if (!seen.Contains(owner))
                (obsoleteTransient ??= []).Add(entry);
        }
        if (obsoleteTransient is not null)
        {
            foreach (SceneEntry entry in obsoleteTransient)
                _transientScenes.Remove(entry.Owner);
            ReleaseEntriesBestEffort(obsoleteTransient, _activeScenes, detachGraphs: true);
        }

        // A graph miss is not a layout/provider exception. Leave the prior pin
        // band intact, prepare readable bounded fallback off the UI thread,
        // then let the active lazy request await refill outside this lock/queue
        // slot. No detached owner may be dereferenced through Scene below.
        bool fallbackChanged = false;
        foreach (SceneEntry entry in requested)
        {
            if (entry.Owner is VectorSceneBox { IsGraphDetached: true } box)
            {
                if (!_activeScenes.ContainsKey(box))
                    _transientScenes.TryAdd(box, new SceneEntry(box, 8_192));
                fallbackChanged |= box.PrepareDetachedFallback(cancellationToken, MarkdownDetachedSceneFallback.Create);
                if (!box.GraphRebindFailed)
                    _pendingGraphRebinds.Add(box);
            }
        }
        if (_pendingGraphRebinds.Count != 0)
        {
            _pendingGraphBand = band;
            // Legacy policies keep their old pin transaction. Participating
            // policies can publish the complete readable fallback set first,
            // freeing outgoing native credits before asynchronous graph refill.
            if (_nativeAdmission is null)
                return fallbackChanged;
        }

        // Readiness collection records owners only. Walk immutable commands for
        // diagnostic weights here, on the background band preparation path,
        // never in a warm UI probe.
        for (int index = 0; index < requested.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object owner = requested[index].Owner;
            if (_activeScenes.TryGetValue(owner, out SceneEntry retained) ||
                _transientScenes.TryGetValue(owner, out retained))
            {
                // The command graph is immutable. Moving the band boundary
                // does not change the weight of a scene already pinned here;
                // do not walk a large diagram again just to move its viewport.
                requested[index] = retained;
                requestedByOwner.Add(owner, retained);
                continue;
            }
            if (owner is VectorSceneBox { IsGraphDetached: true })
            {
                // A typed failed-refill source fallback remains unready, but
                // must still join the pin band so leaving it retires its small
                // native text layout. This is a diagnostic weight, not bytes.
                var fallbackEntry = new SceneEntry(owner, 8_192);
                requested[index] = fallbackEntry;
                requestedByOwner.Add(owner, fallbackEntry);
                continue;
            }
            MarkdownVectorScene scene = owner switch
            {
                VectorSceneBox box => box.Scene,
                InlineVectorSceneRun run => run.Scene,
                _ => throw new InvalidOperationException("The residency band contains an unsupported scene owner."),
            };
            var entry = new SceneEntry(owner, GetPreparedResourceEstimateForRebind(scene, cancellationToken));
            requested[index] = entry;
            requestedByOwner.Add(owner, entry);
        }

        long attemptedCapacityGeneration = _nativeAdmission?.CapacityGeneration ?? 0;
        bool resourcesChanged = fallbackChanged;
        try
        {
            // Prepare the incoming set first. The old band remains pinned until
            // every visible item in the new band is ready, preventing blank
            // frames at a viewport transition.
            foreach (SceneEntry entry in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Owner is VectorSceneBox { IsGraphDetached: true })
                    continue;
                if (!IsPrepared(entry))
                    resourcesChanged = true;
                Prepare(entry, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            ReleaseEntriesBestEffort(requested, _activeScenes);
            throw;
        }

        bool changed = resourcesChanged || requestedByOwner.Count != _activeScenes.Count;
        List<SceneEntry>? retired = null;
        foreach ((object owner, SceneEntry active) in _activeScenes)
        {
            if (!requestedByOwner.ContainsKey(owner))
            {
                changed = true;
                (retired ??= []).Add(active);
            }
        }
        foreach ((object owner, SceneEntry transient) in _transientScenes)
        {
            if (!requestedByOwner.ContainsKey(owner) && !_activeScenes.ContainsKey(owner))
                (retired ??= []).Add(transient);
        }

        foreach (object owner in requestedByOwner.Keys)
            changed |= !_activeScenes.ContainsKey(owner);

        // Publish the complete incoming pin set before releasing the outgoing
        // set. LayoutSnapshot holds the lease lock for this entire exchange.
        long estimatedBytes = 0;
        foreach (SceneEntry entry in requested)
            estimatedBytes = SaturatingAdd(estimatedBytes, entry.EstimatedBytes);
        _activeScenes = requestedByOwner;
        _transientScenes.Clear();
        _activeInlineSceneIndexes = requestedInlineIndexes;
        _activeEstimatedBytes = estimatedBytes;
        _activeBand = band;

        // Pending rectangles are a short-lived handoff from layout measurement
        // to the active band index. Do not retain a second copy on the boxes
        // after this transaction commits. On cancellation they remain available
        // for a retry, and the old active indexes and pins stay authoritative.
        foreach ((InlineContainerBox inline, InlineSceneGeometryIndex index) in requestedInlineIndexes)
            inline.ReleasePendingSceneGeometry(index.Generation);

        // Native disposal failures must not unwind a completed band exchange:
        // the newly visible set is already prepared and published. Dispose on
        // the next snapshot retirement remains the final cleanup opportunity.
        ReleaseEntriesBestEffort(retired, keep: null, detachGraphs: true);

        // The outgoing set may have owned the only freeable credits. Preserve
        // the attempt's generation so register-then-recheck schedules one fresh
        // stationary retry after this gate is released. Do not construct a
        // second native prefix or introduce a cancellation point after commit.
        // Failed disposal never makes those credits available.
        bool hasDeferred = false;
        foreach (SceneEntry entry in requested)
            hasDeferred |= IsCapacityDeferred(entry);
        Volatile.Write(ref _activeCapacityGeneration, attemptedCapacityGeneration);
        Volatile.Write(ref _hasCapacityDeferredScenes, hasDeferred);
        return changed;
    }

    public void ReleaseUnpinnedResources(
        IReadOnlyList<int> topLevelOrdinals,
        LazyLayoutBand band,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
        ArgumentNullException.ThrowIfNull(topLevelOrdinals);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(topLevelOrdinalByBlockIndex);
        if (placements.Count != _blocks.Count || !IsValidBand(band))
            return;
        List<SceneEntry> candidates = [];
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (int ordinal in topLevelOrdinals)
        {
            if (!TryGetRootLocalBand(
                    ordinal,
                    band,
                    placements,
                    topLevelOrdinalByBlockIndex,
                    out LazyLayoutBand rootLocalBand))
                return;

            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            using LayoutPlacementTransformContext.Scope placementScope =
                LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
            if (!TryCollectVisibleBlockScenes(
                    _blocks[ordinal],
                    rootLocalBand,
                    candidates,
                    seen,
                    _activeInlineSceneIndexes,
                    allowIndexBuild: false,
                    CancellationToken.None))
            {
                // Incomplete/stale geometry cannot authorize disposal.
                return;
            }
        }

        // A same-band cancellation cleanup cannot steal a pending refill pin.
        // New-band pruning above owns retirement of superseded transient pins.
        candidates.RemoveAll(entry => _transientScenes.ContainsKey(entry.Owner));
        ReleaseEntriesBestEffort(candidates, _activeScenes);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<SceneEntry> active = [.. _activeScenes.Values, .. _transientScenes.Values];
        _activeScenes.Clear();
        _transientScenes.Clear();
        _activeInlineSceneIndexes.Clear();
        _activeEstimatedBytes = 0;
        _activeBand = null;
        Volatile.Write(ref _hasCapacityDeferredScenes, false);
        try
        {
            ReleaseScenes(active);
        }
        finally
        {
            // A retained policy must not keep its retired snapshot's boxes (and
            // their immutable command graphs) alive after the snapshot drains.
            _blocks = Array.Empty<BlockBox>();
            _graphPlan = null;
            _pendingGraphRebinds.Clear();
            _graphStatusChanges.Clear();
            _pendingGraphBand = null;
            _readinessScratch.Clear();
            _readinessSeen.Clear();
            _visibleDeferredSceneMarkerScratch.Clear();
        }
    }

    private bool TryCollectVisibleBlockScenes(
        BlockBox box,
        LazyLayoutBand band,
        List<SceneEntry> scenes,
        HashSet<object> seen,
        Dictionary<InlineContainerBox, InlineSceneGeometryIndex> inlineIndexes,
        bool allowIndexBuild,
        CancellationToken cancellationToken)
    {
        Rect bounds = box.Bounds;
        if (!IsFiniteNonNegativeRect(bounds))
            return false;
        if (bounds.Width == 0 || bounds.Height == 0 ||
            !band.Intersects(bounds.Top, bounds.Bottom))
        {
            return true;
        }

        switch (box)
        {
            case VectorSceneBox vector:
                AddScene(vector, scenes, seen);
                return true;
            case InlineContainerBox inline:
                return TryCollectVisibleInlineScenes(
                    inline,
                    band,
                    scenes,
                    seen,
                    inlineIndexes,
                    allowIndexBuild,
                    cancellationToken);
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxesInVerticalBand(band.Top, band.Bottom))
                {
                    if (!TryCollectVisibleBlockScenes(
                            cell, band, scenes, seen, inlineIndexes, allowIndexBuild, cancellationToken))
                        return false;
                }
                return true;
            case CodeBlockBox codeBlock:
            {
                (int first, int endExclusive) = codeBlock.GetVisibleChunkRange(band.Top, band.Bottom);
                for (int index = first; index < endExclusive; index++)
                {
                    if (!TryCollectVisibleBlockScenes(
                            codeBlock.Chunks[index], band, scenes, seen, inlineIndexes, allowIndexBuild, cancellationToken))
                        return false;
                }
                return true;
            }
            case ListItemBox listItem:
                return TryCollectVisibleBlockScenes(
                        listItem.Marker, band, scenes, seen, inlineIndexes, allowIndexBuild, cancellationToken) &&
                    TryCollectVisibleBlockScenes(
                        listItem.Content, band, scenes, seen, inlineIndexes, allowIndexBuild, cancellationToken);
            case StackBox stack:
            {
                IReadOnlyList<BlockBox> children = stack.Children;
                int first = FindFirstStackChildIntersecting(children, band.Top);
                for (int index = first; index < children.Count; index++)
                {
                    BlockBox child = children[index];
                    if (child.Bounds.Top > band.Bottom)
                        break;
                    if (!TryCollectVisibleBlockScenes(
                            child, band, scenes, seen, inlineIndexes, allowIndexBuild, cancellationToken))
                        return false;
                }
                return true;
            }
            default:
                return true;
        }
    }

    private bool TryGetRootLocalBand(
        int ordinal,
        LazyLayoutBand logicalBand,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        out LazyLayoutBand rootLocalBand)
    {
        rootLocalBand = default;
        if ((uint)ordinal >= (uint)_blocks.Count || placements.Count != _blocks.Count ||
            !topLevelOrdinalByBlockIndex.TryGetValue(_blocks[ordinal].BlockIndex, out int resolvedOrdinal) ||
            resolvedOrdinal != ordinal)
        {
            return false;
        }

        rootLocalBand = placements.ToPhysicalBand(ordinal, logicalBand);
        return IsValidBand(rootLocalBand);
    }

    private bool IsFailedSceneVisible(
        VectorSceneBox box,
        LazyLayoutBand logicalBand,
        LayoutPlacementGeneration placements,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
        if (!topLevelOrdinalByBlockIndex.TryGetValue(box.BlockIndex, out int ordinal) ||
            (uint)ordinal >= (uint)_blocks.Count)
        {
            return false;
        }

        LayoutRootPlacement placement = placements.GetPlacement(ordinal);
        if (!logicalBand.Intersects(placement.Top, placement.Bottom) ||
            !TryGetRootLocalBand(
                ordinal,
                logicalBand,
                placements,
                topLevelOrdinalByBlockIndex,
                out LazyLayoutBand rootLocalBand))
        {
            return false;
        }

        return rootLocalBand.Intersects(box.Bounds.Top, box.Bounds.Bottom);
    }

    private bool TryCollectVisibleInlineScenes(
        InlineContainerBox inline,
        LazyLayoutBand band,
        List<SceneEntry> scenes,
        HashSet<object> seen,
        Dictionary<InlineContainerBox, InlineSceneGeometryIndex> inlineIndexes,
        bool allowIndexBuild,
        CancellationToken cancellationToken)
    {
        if (inline.VectorSceneRunCount == 0)
            return true;

        if (!TryGetInlineSceneGeometryIndex(
                inline,
                band,
                inlineIndexes,
                allowIndexBuild,
                cancellationToken,
                out InlineSceneGeometryIndex? index))
            return false;

        InlineSceneGeometryIndex sceneIndex = index!;
        inline.GetSceneGeometryOrigin(out _, out double originY);
        if (!double.IsFinite(originY))
            return false;
        int end = sceneIndex.FindLastStartingAtOrBefore(band.Bottom - originY);
        int first = sceneIndex.FindFirstWithBottomAtOrAfter(band.Top - originY);
        for (int entryIndex = first; entryIndex <= end; entryIndex++)
        {
            InlineSceneIndexEntry entry = sceneIndex.Entries[entryIndex];
            if (!band.Intersects(originY + entry.Top, originY + entry.Bottom))
                continue;
            AddScene(entry.Geometry.Run, scenes, seen);
        }
        return true;
    }

    private bool TryGetInlineSceneGeometryIndex(
        InlineContainerBox inline,
        LazyLayoutBand band,
        Dictionary<InlineContainerBox, InlineSceneGeometryIndex> candidateIndexes,
        bool allowIndexBuild,
        CancellationToken cancellationToken,
        out InlineSceneGeometryIndex? index)
    {
        index = null;
        long generation = inline.SceneGeometryGeneration;
        if (generation == 0)
            return false;

        inline.GetSceneGeometryOrigin(out _, out double originY);
        if (!double.IsFinite(originY))
            return false;
        var localBand = new LazyLayoutBand(band.Top - originY, band.Bottom - originY);
        if (!IsValidBand(localBand))
            return false;

        if (candidateIndexes.TryGetValue(inline, out InlineSceneGeometryIndex? candidate) &&
            candidate.Generation == generation && candidate.Covers(localBand))
        {
            index = candidate;
            return true;
        }

        if (_activeInlineSceneIndexes.TryGetValue(inline, out InlineSceneGeometryIndex? active) &&
            active.Generation == generation && active.Covers(localBand))
        {
            candidateIndexes[inline] = active;
            index = active;
            return true;
        }

        if (!allowIndexBuild)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        if (!inline.TryGetCurrentSceneGeometry(out long capturedGeneration, out InlineSceneGeometry[] geometry))
        {
            if (!inline.TryCaptureCurrentSceneGeometry(
                    cancellationToken,
                    out capturedGeneration,
                    out geometry))
            {
                return false;
            }
        }
        if (capturedGeneration != generation)
            return false;

        if (!InlineSceneGeometryIndex.TryCreate(
                generation,
                geometry,
                localBand,
                cancellationToken,
                out InlineSceneGeometryIndex? created))
        {
            return false;
        }

        candidateIndexes[inline] = created!;
        _inlineGeometryIndexBuildCount++;
        index = created!;
        return true;
    }

    public bool TryAppendVisibleInlineSceneMarkers(
        InlineContainerBox inline,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady)
    {
        ArgumentNullException.ThrowIfNull(inline);
        ArgumentNullException.ThrowIfNull(markerKeys);
        if (_disposed || !double.IsFinite(viewportTop) || !double.IsFinite(viewportBottom) ||
            viewportBottom < viewportTop)
        {
            return false;
        }
        if (inline.VectorSceneRunCount == 0)
            return true;

        long generation = inline.SceneGeometryGeneration;
        if (generation == 0 ||
            !_activeInlineSceneIndexes.TryGetValue(inline, out InlineSceneGeometryIndex? index) ||
            index.Generation != generation)
        {
            return false;
        }
        InlineSceneGeometryIndex sceneIndex = index!;

        inline.GetSceneGeometryOrigin(out double originX, out double originY);
        if (!double.IsFinite(originX) || !double.IsFinite(originY))
            return false;
        if (!sceneIndex.Covers(new LazyLayoutBand(viewportTop - originY, viewportBottom - originY)))
            return false;

        int end = sceneIndex.FindLastStartingAtOrBefore(viewportBottom - originY);
        int first = sceneIndex.FindFirstWithBottomAtOrAfter(viewportTop - originY);
        for (int entryIndex = first; entryIndex <= end; entryIndex++)
        {
            InlineSceneIndexEntry entry = sceneIndex.Entries[entryIndex];
            if (viewport is { } bounds)
            {
                var extent = new Rect(
                    originX + entry.Left,
                    originY + entry.Top,
                    entry.Right - entry.Left,
                    entry.Bottom - entry.Top);
                if (!IsFiniteNonNegativeRect(extent))
                    return false;
                if (!Intersects(extent, bounds))
                    continue;
            }

            bool visible = false;
            for (int regionIndex = 0; regionIndex < entry.Geometry.RegionCount; regionIndex++)
            {
                Rect local = entry.Geometry.GetRegionBounds(regionIndex);
                var rect = new Rect(originX + local.X, originY + local.Y, local.Width, local.Height);
                if (!IsFiniteNonNegativeRect(rect))
                    return false;
                if (rect.Bottom >= viewportTop && rect.Top <= viewportBottom &&
                    (viewport is not { } viewportBounds || Intersects(rect, viewportBounds)))
                {
                    visible = true;
                    break;
                }
            }

            if (!visible)
                continue;
            InlineVectorSceneRun run = entry.Geometry.Run;
            if (run.DeferredSceneMarker is { } marker)
                markerKeys.Add(marker);
            if (!run.IsNativeResourcesPrepared)
                visibleSceneResourcesReady = false;
        }

        return true;
    }

    private static int FindFirstStackChildIntersecting(
        IReadOnlyList<BlockBox> children,
        double bandTop)
    {
        int low = 0;
        int high = children.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (children[middle].Bounds.Bottom < bandTop)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static void AddScene(
        object owner,
        List<SceneEntry> scenes,
        HashSet<object> seen)
    {
        if (seen.Add(owner))
            scenes.Add(new SceneEntry(owner, 0));
    }

    private static bool Intersects(Rect left, Rect right) =>
        left.Right > right.Left && left.Left < right.Right &&
        left.Bottom > right.Top && left.Top < right.Bottom;

    private static bool IsFiniteNonNegativeRect(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom) &&
        rect.Width >= 0 && rect.Height >= 0;

    private static bool IsValidBand(LazyLayoutBand band) =>
        double.IsFinite(band.Top) && double.IsFinite(band.Bottom) && band.Bottom >= band.Top;

    private static bool IsPrepared(SceneEntry entry)
        => entry.Owner switch
        {
            VectorSceneBox box => box.IsNativeResourcesPrepared,
            InlineVectorSceneRun run => run.IsNativeResourcesPrepared,
            _ => false,
        };

    private static bool IsCapacityFallback(SceneEntry entry) => entry.Owner switch
    {
        VectorSceneBox box => box.IsCapacityFallbackPaintable,
        InlineVectorSceneRun run => run.IsCapacityFallbackPaintable,
        _ => false,
    };

    private static bool IsCapacityDeferred(SceneEntry entry) => entry.Owner switch
    {
        VectorSceneBox box => box.NativePreparationStatus == VectorNativePreparationStatus.DeferredCapacity,
        InlineVectorSceneRun run => run.NativePreparationStatus == VectorNativePreparationStatus.DeferredCapacity,
        _ => false,
    };

    private static void Prepare(SceneEntry entry, CancellationToken cancellationToken)
    {
        switch (entry.Owner)
        {
            case VectorSceneBox box:
                box.PrepareNativeResources(cancellationToken);
                break;
            case InlineVectorSceneRun run:
                run.PrepareNativeResources(cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The residency band contains an unsupported scene owner.");
        }
    }

    private long EstimatePreparedResourceBytes(MarkdownVectorScene scene, CancellationToken cancellationToken)
    {
        // Win2D does not expose allocation sizes for these device resources.
        // This model is a relative diagnostic weight only, never a hard budget.
        long estimate = 256;
        foreach (MarkdownVectorCommand command in scene.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            estimate = SaturatingAdd(estimate, 256);
            if (command.Path is { } path)
            {
                estimate = SaturatingAdd(estimate, 2_048);
                estimate = SaturatingAdd(estimate, SaturatingMultiply(path.Count, 96));
            }

            if (command.Kind == MarkdownVectorCommandKind.DrawText && command.Text is { } text)
            {
                estimate = SaturatingAdd(estimate, 4_096);
                estimate = SaturatingAdd(estimate, SaturatingMultiply(text.Length, 64));
            }

            if (command.Style is { } style && VectorSceneDrawing.RequiresStrokeStyle(style))
            {
                estimate = SaturatingAdd(estimate, 1_024);
                estimate = SaturatingAdd(
                    estimate,
                    SaturatingMultiply(style.DashArray.Count, sizeof(float)));
            }
        }

        estimate = SaturatingAdd(estimate, SaturatingMultiply(scene.Semantics.Count, 128));
        estimate = SaturatingAdd(estimate, SaturatingMultiply(scene.Links.Count, 96));
        Interlocked.Increment(ref _commandGraphEstimateCount);
        return Math.Max(estimate, 1);
    }

    internal long GetPreparedResourceEstimate(MarkdownVectorScene scene) =>
        GetPreparedResourceEstimateForRebind(scene, CancellationToken.None);

    internal long GetPreparedResourceEstimateForRebind(MarkdownVectorScene scene, CancellationToken cancellationToken)
    {
        // The warm path does not need a factory or the table's insertion path.
        if (s_resourceEstimates.TryGetValue(scene, out SceneResourceEstimate? retained))
            return retained.GetOrEstimate(scene, this, cancellationToken);
        return s_resourceEstimates.GetValue(
                scene,
                static _ => new SceneResourceEstimate())
            .GetOrEstimate(scene, this, cancellationToken);
    }

    private void ReleaseEntriesBestEffort(
        IEnumerable<SceneEntry>? candidates,
        IReadOnlyDictionary<object, SceneEntry>? keep,
        bool detachGraphs = false)
    {
        if (candidates is null)
            return;
        foreach (SceneEntry entry in candidates)
        {
            if (keep?.ContainsKey(entry.Owner) == true)
                continue;
            try
            {
                if (detachGraphs && entry.Owner is VectorSceneBox box &&
                    _graphPlan?.CanDetachGraph(box) == true)
                    box.ReleaseSceneGraph();
                else
                    Release(entry);
            }
            catch
            {
                // Preserve the already-published active band. Snapshot disposal
                // retries remaining active resources; an off-band disposal error
                // must not roll back visible geometry.
            }
        }
    }

    private static void ReleaseScenes(IEnumerable<SceneEntry> scenes)
    {
        List<Exception>? failures = null;
        foreach (SceneEntry entry in scenes)
        {
            try
            {
                Release(entry);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
            throw failures[0];
        if (failures is { Count: > 1 })
            throw new AggregateException("One or more native scene resources failed to release.", failures);
    }

    private static void Release(SceneEntry entry)
    {
        switch (entry.Owner)
        {
            case VectorSceneBox box:
                box.ReleaseNativeResources();
                break;
            case InlineVectorSceneRun run:
                run.ReleaseNativeResources();
                break;
        }
    }

    private static long SaturatingMultiply(int count, int weight)
        => count > 0 ? Math.Min(long.MaxValue, (long)count * weight) : 0;

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class InlineSceneGeometryIndex
    {
        private readonly double[] _prefixMaximumBottoms;

        private InlineSceneGeometryIndex(
            long generation,
            LazyLayoutBand coverage,
            InlineSceneIndexEntry[] entries)
        {
            Generation = generation;
            Coverage = coverage;
            Entries = entries;
            _prefixMaximumBottoms = new double[entries.Length];
            double maximum = double.NegativeInfinity;
            for (int index = 0; index < entries.Length; index++)
            {
                maximum = Math.Max(maximum, entries[index].Bottom);
                _prefixMaximumBottoms[index] = maximum;
            }
        }

        internal long Generation { get; }
        private LazyLayoutBand Coverage { get; }
        internal InlineSceneIndexEntry[] Entries { get; }

        internal bool Covers(LazyLayoutBand band) =>
            band.Top >= Coverage.Top && band.Bottom <= Coverage.Bottom;

        internal int FindLastStartingAtOrBefore(double bottom)
        {
            int low = 0;
            int high = Entries.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (Entries[middle].Top <= bottom)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low - 1;
        }

        internal int FindFirstWithBottomAtOrAfter(double top)
        {
            int low = 0;
            int high = _prefixMaximumBottoms.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (_prefixMaximumBottoms[middle] < top)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        internal static bool TryCreate(
            long generation,
            InlineSceneGeometry[] geometry,
            LazyLayoutBand localBand,
            CancellationToken cancellationToken,
            out InlineSceneGeometryIndex? index)
        {
            index = null;
            if (generation == 0 || !IsValidBand(localBand))
                return false;

            List<InlineSceneIndexEntry> entries = [];
            foreach (InlineSceneGeometry item in geometry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.RegionCount <= 0)
                    return false;

                double left = double.PositiveInfinity;
                double right = double.NegativeInfinity;
                double top = double.PositiveInfinity;
                double bottom = double.NegativeInfinity;
                List<Rect>? retainedRegions = null;
                for (int regionIndex = 0; regionIndex < item.RegionCount; regionIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Rect region = item.GetRegionBounds(regionIndex);
                    if (!IsFiniteNonNegativeRect(region))
                        return false;

                    top = Math.Min(top, region.Top);
                    bottom = Math.Max(bottom, region.Bottom);
                    if (!localBand.Intersects(region.Top, region.Bottom))
                        continue;

                    left = Math.Min(left, region.Left);
                    right = Math.Max(right, region.Right);
                    (retainedRegions ??= []).Add(region);
                }

                // Only retain scene owners and line fragments intersecting the
                // candidate band. A long paragraph's off-band Math graph stays
                // owned by its snapshot boxes, not duplicated by this index.
                if (retainedRegions is null)
                    continue;

                Rect[] retained = retainedRegions.ToArray();
                var filteredGeometry = new InlineSceneGeometry(item.Run, retained);
                double retainedTop = double.PositiveInfinity;
                double retainedBottom = double.NegativeInfinity;
                foreach (Rect region in retained)
                {
                    retainedTop = Math.Min(retainedTop, region.Top);
                    retainedBottom = Math.Max(retainedBottom, region.Bottom);
                }
                entries.Add(new InlineSceneIndexEntry(
                    filteredGeometry,
                    retainedTop,
                    retainedBottom,
                    left,
                    right));
            }

            entries.Sort(static (left, right) =>
            {
                int comparison = left.Top.CompareTo(right.Top);
                return comparison != 0 ? comparison : left.Bottom.CompareTo(right.Bottom);
            });
            index = new InlineSceneGeometryIndex(generation, localBand, entries.ToArray());
            return true;
        }
    }

    private readonly struct InlineSceneIndexEntry(
        InlineSceneGeometry Geometry,
        double Top,
        double Bottom,
        double Left,
        double Right)
    {
        public InlineSceneGeometry Geometry { get; } = Geometry;
        public double Top { get; } = Top;
        public double Bottom { get; } = Bottom;
        public double Left { get; } = Left;
        public double Right { get; } = Right;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MarkdownVectorSceneResidencyPolicy));
    }

    private sealed class SceneResourceEstimate
    {
        private long _estimatedBytes;
        private int _initialized;

        internal long GetOrEstimate(MarkdownVectorScene scene, MarkdownVectorSceneResidencyPolicy policy,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _initialized) != 0)
                return Volatile.Read(ref _estimatedBytes);

            lock (this)
            {
                if (_initialized == 0)
                {
                    long estimate = policy.EstimatePreparedResourceBytes(scene, cancellationToken);
                    Volatile.Write(ref _estimatedBytes, estimate);
                    Volatile.Write(ref _initialized, 1);
                }

                return Volatile.Read(ref _estimatedBytes);
            }
        }
    }

    private readonly struct SceneEntry(object Owner, long EstimatedBytes)
    {
        public object Owner { get; } = Owner;
        public long EstimatedBytes { get; } = EstimatedBytes;
    }
}
