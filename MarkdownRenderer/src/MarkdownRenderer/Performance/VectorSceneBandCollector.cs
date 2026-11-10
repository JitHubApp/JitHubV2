using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Walks a snapshot's top-level boxes for the vector-scene owners visible in a
/// band, and owns the inline scene geometry indexes published with the active
/// pin band. The residency policy serializes every call (LayoutSnapshot holds
/// its paint/read lock), so this type carries no lock of its own.
/// </summary>
internal sealed class VectorSceneBandCollector
{
    private IReadOnlyList<BlockBox> _blocks;
    private Dictionary<InlineContainerBox, InlineSceneGeometryIndex> _activeInlineIndexes =
        new(ReferenceEqualityComparer.Instance);
    private int _inlineGeometryIndexBuildCount;

    internal VectorSceneBandCollector(IReadOnlyList<BlockBox> blocks)
    {
        _blocks = blocks;
    }

    internal int BlockCount => _blocks.Count;

    internal int InlineGeometryIndexBuildCount => _inlineGeometryIndexBuildCount;

    internal int ActiveInlineIndexCount => _activeInlineIndexes.Count;

    /// <summary>The indexes published with the active band; readiness probes reuse them.</summary>
    internal Dictionary<InlineContainerBox, InlineSceneGeometryIndex> ActiveInlineIndexes => _activeInlineIndexes;

    internal BlockBox GetRoot(int ordinal) => _blocks[ordinal];

    internal void PublishActiveInlineIndexes(Dictionary<InlineContainerBox, InlineSceneGeometryIndex> indexes) =>
        _activeInlineIndexes = indexes;

    internal void ClearActiveInlineIndexes() => _activeInlineIndexes.Clear();

    /// <summary>
    /// Drops the retired snapshot's boxes so a retained policy does not keep
    /// them (and their immutable command graphs) alive.
    /// </summary>
    internal void ReleaseBlocks() => _blocks = Array.Empty<BlockBox>();

    internal bool TryCollectVisibleBlockScenes(
        BlockBox box,
        LazyLayoutBand band,
        List<VectorSceneResidencyEntry> scenes,
        HashSet<object> seen,
        Dictionary<InlineContainerBox, InlineSceneGeometryIndex> inlineIndexes,
        bool allowIndexBuild,
        CancellationToken cancellationToken)
    {
        Rect bounds = box.Bounds;
        if (!VectorSceneBandGeometry.IsFiniteNonNegativeRect(bounds))
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

    internal bool TryGetRootLocalBand(
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
        return VectorSceneBandGeometry.IsValidBand(rootLocalBand);
    }

    internal bool IsFailedSceneVisible(
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
        List<VectorSceneResidencyEntry> scenes,
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
        if (!VectorSceneBandGeometry.IsValidBand(localBand))
            return false;

        if (candidateIndexes.TryGetValue(inline, out InlineSceneGeometryIndex? candidate) &&
            candidate.Generation == generation && candidate.Covers(localBand))
        {
            index = candidate;
            return true;
        }

        if (_activeInlineIndexes.TryGetValue(inline, out InlineSceneGeometryIndex? active) &&
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

    internal bool TryAppendVisibleInlineSceneMarkers(
        InlineContainerBox inline,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady)
    {
        ArgumentNullException.ThrowIfNull(inline);
        ArgumentNullException.ThrowIfNull(markerKeys);
        if (!double.IsFinite(viewportTop) || !double.IsFinite(viewportBottom) ||
            viewportBottom < viewportTop)
        {
            return false;
        }
        if (inline.VectorSceneRunCount == 0)
            return true;

        long generation = inline.SceneGeometryGeneration;
        if (generation == 0 ||
            !_activeInlineIndexes.TryGetValue(inline, out InlineSceneGeometryIndex? index) ||
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
                if (!VectorSceneBandGeometry.IsFiniteNonNegativeRect(extent))
                    return false;
                if (!VectorSceneBandGeometry.Intersects(extent, bounds))
                    continue;
            }

            bool visible = false;
            for (int regionIndex = 0; regionIndex < entry.Geometry.RegionCount; regionIndex++)
            {
                Rect local = entry.Geometry.GetRegionBounds(regionIndex);
                var rect = new Rect(originX + local.X, originY + local.Y, local.Width, local.Height);
                if (!VectorSceneBandGeometry.IsFiniteNonNegativeRect(rect))
                    return false;
                if (rect.Bottom >= viewportTop && rect.Top <= viewportBottom &&
                    (viewport is not { } viewportBounds || VectorSceneBandGeometry.Intersects(rect, viewportBounds)))
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
        List<VectorSceneResidencyEntry> scenes,
        HashSet<object> seen)
    {
        if (seen.Add(owner))
            scenes.Add(new VectorSceneResidencyEntry(owner, 0));
    }
}
