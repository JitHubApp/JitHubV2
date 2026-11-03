using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Resolves pointer and selection-endpoint hit tests against the committed
/// placement generation, including vector-scene text targets.
/// </summary>
internal sealed class SnapshotHitTester
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;

    internal SnapshotHitTester(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery)
    {
        _geometry = geometry;
        _recovery = recovery;
    }

    internal bool HitTest(Point point, out DocumentPosition position)
    {
        position = DocumentPosition.Zero;
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return false;

            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            var candidates = _geometry.FindViewportRangeNoLock(point.Y - 4, point.Y + 4);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int n = placements.GetBlockOrdinal(i);
                var b = _geometry.Blocks[n];
                LayoutRootPlacement placement = placements.GetPlacement(n);
                if (!placement.Measured) continue;
                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (b.HitTest(placements.ToPhysicalPoint(n, point), out position)) return true;
            }
        }
        position = DocumentPosition.Zero;
        return false;
    }

    /// <summary>
    /// Resolves a drag-selection endpoint. Unlike ordinary hit testing, this
    /// permits atomic visual content to expose before/after boundaries across
    /// its full arranged bounds without making that surface initiate selection.
    /// </summary>
    internal bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        position = DocumentPosition.Zero;
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return false;

            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            var candidates = _geometry.FindViewportRangeNoLock(point.Y - 4, point.Y + 4);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = placements.GetBlockOrdinal(i);
                BlockBox block = _geometry.Blocks[ordinal];
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                if (!placement.Measured)
                {
                    continue;
                }

                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (block.HitTestSelectionEndpoint(
                        placements.ToPhysicalPoint(ordinal, point), out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    /// <summary>
    /// Resolves named vector semantics independently from application
    /// selection. TextPattern clients must be able to place a range on a
    /// linked or informational diagram item even when it is not Selectable.
    /// </summary>
    internal bool TryHitTestVectorText(Point point, out DocumentPosition position)
    {
        position = DocumentPosition.Zero;
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return false;

            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            var candidates = _geometry.FindViewportRangeNoLock(point.Y - 1, point.Y + 1);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = placements.GetBlockOrdinal(i);
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                if (!placement.Measured)
                {
                    continue;
                }

                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (TryHitTestVectorText(
                        _geometry.Blocks[ordinal], placements.ToPhysicalPoint(ordinal, point), out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    private static bool TryHitTestVectorText(
        BlockBox box,
        Point point,
        out DocumentPosition position)
    {
        if (!box.Bounds.Contains(point))
        {
            position = DocumentPosition.Zero;
            return false;
        }

        if (box is VectorSceneBox vector)
            return vector.TryGetTextPositionAt(point, out position);

        if (box is ListItemBox listItem)
        {
            return TryHitTestVectorText(listItem.Marker, point, out position) ||
                   TryHitTestVectorText(listItem.Content, point, out position);
        }

        if (box is TableBox table)
        {
            foreach (InlineContainerBox cell in table.GetCellBoxes())
            {
                if (TryHitTestVectorText(cell, point, out position))
                    return true;
            }
        }
        else if (box is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                if (TryHitTestVectorText(child, point, out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }
}
