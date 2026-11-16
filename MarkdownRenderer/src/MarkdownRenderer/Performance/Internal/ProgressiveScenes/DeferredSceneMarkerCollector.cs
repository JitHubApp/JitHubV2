using System;
using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Performance.Internal.Residency;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>
/// Walks measured layout boxes that intersect a viewport band and collects
/// the private deferred-scene markers of their visible code blocks and
/// inline scenes, without rescanning the syntax tree.
/// </summary>
internal static class DeferredSceneMarkerCollector
{
    internal static bool TryCollect(
        IReadOnlyList<BlockBox> measuredTopLevelBlocks,
        IReadOnlyList<int> topLevelOrdinals,
        LayoutPlacementGeneration placements,
        LayoutPlacementTransformContext placementTransform,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy,
        out IReadOnlyList<string> markerKeys,
        out bool visibleSceneResourcesReady)
    {
        ArgumentNullException.ThrowIfNull(measuredTopLevelBlocks);
        ArgumentNullException.ThrowIfNull(topLevelOrdinals);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(placementTransform);
        ArgumentNullException.ThrowIfNull(topLevelOrdinalByBlockIndex);
        List<string> markers = residencyPolicy?.VisibleDeferredSceneMarkerScratch ?? [];
        markers.Clear();
        markerKeys = markers;
        visibleSceneResourcesReady = true;
        if (!double.IsFinite(viewportTop) || !double.IsFinite(viewportBottom) || viewportBottom < viewportTop ||
            viewport is { } bounds && !IsFiniteNonNegativeRect(bounds))
        {
            return false;
        }

        if (topLevelOrdinals.Count != measuredTopLevelBlocks.Count ||
            placements.Count == 0 && measuredTopLevelBlocks.Count != 0)
        {
            return false;
        }

        var logicalBand = new LazyLayoutBand(viewportTop, viewportBottom);
        for (int index = 0; index < measuredTopLevelBlocks.Count; index++)
        {
            BlockBox block = measuredTopLevelBlocks[index];
            int ordinal = topLevelOrdinals[index];
            if ((uint)ordinal >= (uint)placements.Count ||
                !topLevelOrdinalByBlockIndex.TryGetValue(block.BlockIndex, out int resolvedOrdinal) ||
                resolvedOrdinal != ordinal)
            {
                return false;
            }

            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            using LayoutPlacementTransformContext.Scope placementScope =
                placementTransform.Enter(placement.TranslationX, placement.TranslationY);
            LazyLayoutBand rootLocalBand = placements.ToPhysicalBand(ordinal, logicalBand);
            Rect? rootLocalViewport = viewport is { } logicalViewport
                ? placements.ToPhysicalViewport(ordinal, logicalViewport)
                : null;
            if (!double.IsFinite(rootLocalBand.Top) || !double.IsFinite(rootLocalBand.Bottom) ||
                rootLocalBand.Bottom < rootLocalBand.Top ||
                rootLocalViewport is { } localBounds && !IsFiniteNonNegativeRect(localBounds))
            {
                return false;
            }

            if (!TryCollect(
                    block,
                    rootLocalBand.Top,
                    rootLocalBand.Bottom,
                    rootLocalViewport,
                    residencyPolicy,
                    markers,
                    ref visibleSceneResourcesReady))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCollect(
        BlockBox block,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady)
    {
        Rect bounds = block.Bounds;
        if (!IsFiniteNonNegativeRect(bounds))
            return false;
        if (bounds.Width == 0 || bounds.Height == 0 ||
            bounds.Bottom < viewportTop || bounds.Top > viewportBottom ||
            viewport is { } viewportBounds && !Intersects(bounds, viewportBounds))
        {
            return true;
        }

        switch (block)
        {
            case CodeBlockBox codeBlock:
                markerKeys.Add(codeBlock.StableKey);
                return true;
            case ListItemBox listItem:
                return TryCollect(
                    listItem.Content,
                    viewportTop,
                    viewportBottom,
                    viewport,
                    residencyPolicy,
                    markerKeys,
                    ref visibleSceneResourcesReady);
            case VectorSceneBox vectorScene:
                visibleSceneResourcesReady &= vectorScene.IsNativeResourcesPrepared;
                return true;
            case InlineContainerBox inlineContainer:
                if (residencyPolicy is null)
                {
                    // Eager/non-progressive sessions never publish deferred
                    // placeholders. Their inline native resources are created
                    // synchronously during layout measurement.
                    return !inlineContainer.HasDeferredSceneMarker;
                }
                return residencyPolicy.TryAppendVisibleInlineSceneMarkers(
                    inlineContainer,
                    viewportTop,
                    viewportBottom,
                    viewport,
                    markerKeys,
                    ref visibleSceneResourcesReady);
            case TableBox table:
                return TryCollectTableCells(
                    table,
                    viewportTop,
                    viewportBottom,
                    viewport,
                    residencyPolicy,
                    markerKeys,
                    ref visibleSceneResourcesReady);
            case StackBox stack:
                return TryCollectStackChildren(
                    stack,
                    viewportTop,
                    viewportBottom,
                    viewport,
                    residencyPolicy,
                    markerKeys,
                    ref visibleSceneResourcesReady);
            default:
                return true;
        }
    }

    private static bool TryCollectTableCells(
        TableBox table,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady)
    {
        foreach (InlineContainerBox cell in table.GetCellBoxesInVerticalBand(viewportTop, viewportBottom))
        {
            if (!TryCollect(
                    cell,
                    viewportTop,
                    viewportBottom,
                    viewport,
                    residencyPolicy,
                    markerKeys,
                    ref visibleSceneResourcesReady))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryCollectStackChildren(
        StackBox stack,
        double viewportTop,
        double viewportBottom,
        Rect? viewport,
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy,
        List<string> markerKeys,
        ref bool visibleSceneResourcesReady)
    {
        IReadOnlyList<BlockBox> children = stack.Children;
        int first = FindFirstStackChildIntersecting(children, viewportTop);
        for (int index = first; index < children.Count; index++)
        {
            BlockBox child = children[index];
            if (child.Bounds.Top > viewportBottom)
                break;
            if (!TryCollect(
                    child,
                    viewportTop,
                    viewportBottom,
                    viewport,
                    residencyPolicy,
                    markerKeys,
                    ref visibleSceneResourcesReady))
            {
                return false;
            }
        }
        return true;
    }

    private static int FindFirstStackChildIntersecting(
        IReadOnlyList<BlockBox> children,
        double viewportTop)
    {
        int low = 0;
        int high = children.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (children[middle].Bounds.Bottom < viewportTop)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static bool IsFiniteNonNegativeRect(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom) &&
        rect.Width >= 0 && rect.Height >= 0;

    internal static bool IsFiniteNonEmptyRect(Rect rect) =>
        IsFiniteNonNegativeRect(rect) && rect.Width > 0 && rect.Height > 0;

    private static bool Intersects(Rect left, Rect right) =>
        left.Right > right.Left && left.Left < right.Right &&
        left.Bottom > right.Top && left.Top < right.Bottom;
}
