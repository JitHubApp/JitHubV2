using System;
using System.Collections.Generic;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Placement;

namespace MarkdownRenderer.Layout.Internal.Snapshots;

/// <summary>
/// Index of horizontally scrollable boxes (code blocks, tables) built during
/// snapshot construction, and the hit-test and scroll operations over them.
/// </summary>
internal sealed class SnapshotHorizontalOverflowIndex
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly SnapshotRetirement _retirement;
    private readonly HashSet<IHorizontalOverflowBox> _horizontalOverflowBoxes =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, IHorizontalOverflowBox> _horizontalOverflowByBlockIndex = new();

    internal HashSet<IHorizontalOverflowBox> Boxes => _horizontalOverflowBoxes;

    internal Dictionary<int, IHorizontalOverflowBox> ByBlockIndex => _horizontalOverflowByBlockIndex;

    internal SnapshotHorizontalOverflowIndex(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        SnapshotRetirement retirement)
    {
        _geometry = geometry;
        _recovery = recovery;
        _retirement = retirement;
    }

    internal bool TryGetHorizontalOverflow(
        Point point,
        out IHorizontalOverflowBox? overflow)
    {
        overflow = null;
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return false;

            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            var candidates = _geometry.FindViewportRangeNoLock(point.Y - 1, point.Y + 1);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = placements.GetBlockOrdinal(i);
                if (!placements.GetPlacement(ordinal).Measured)
                {
                    continue;
                }

                if (FindHorizontalOverflow(
                        _geometry.Blocks[ordinal], placements.ToPhysicalPoint(ordinal, point)) is { } found)
                {
                    overflow = found;
                    return true;
                }
            }
        }

        overflow = null;
        return false;
    }

    internal bool TrySetHorizontalOffset(
        IHorizontalOverflowBox overflow,
        double offset)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired || _retirement.IsDisposed ||
                !_horizontalOverflowBoxes.Contains(overflow))
                return false;

            return overflow.SetHorizontalOffset(offset);
        }
    }

    internal bool TryGetHorizontalOverflowForBlockIndex(
        int blockIndex,
        out IHorizontalOverflowBox? overflow)
    {
        overflow = null;
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return false;

            if (_horizontalOverflowByBlockIndex.TryGetValue(blockIndex, out var found) &&
                found.CanScrollHorizontally)
            {
                overflow = found;
                return true;
            }
        }

        overflow = null;
        return false;
    }

    internal bool TryScrollHorizontal(
        IHorizontalOverflowBox overflow,
        double delta)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired || _retirement.IsDisposed ||
                !_horizontalOverflowBoxes.Contains(overflow))
                return false;

            return overflow.ScrollHorizontal(delta);
        }
    }

    private static IHorizontalOverflowBox? FindHorizontalOverflow(BlockBox box, Point point)
    {
        if (!box.Bounds.Contains(point))
            return null;

        if (box is IHorizontalOverflowBox overflow && overflow.CanScrollHorizontally)
            return overflow;

        if (box is ListItemBox listItem)
            return FindHorizontalOverflow(listItem.Marker, point) ??
                   FindHorizontalOverflow(listItem.Content, point);

        if (box is StackBox stack)
        {
            foreach (var child in stack.Children)
            {
                if (FindHorizontalOverflow(child, point) is { } found)
                    return found;
            }
        }

        return null;
    }
}
