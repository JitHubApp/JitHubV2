using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Locates top-level blocks for scroll anchoring, block scroll targets,
/// focus-target realization and measured-state queries.
/// </summary>
internal sealed class SnapshotBlockLocator
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly SnapshotRetirement _retirement;

    internal SnapshotBlockLocator(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        SnapshotRetirement retirement)
    {
        _geometry = geometry;
        _recovery = recovery;
        _retirement = retirement;
    }

    internal (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(double verticalOffset)
    {
        if (verticalOffset <= 0)
            return null;

        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return null;

            foreach (var block in _geometry.Blocks)
            {
                LayoutRootPlacement placement = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(_geometry.BlockOrdinals[block]);
                if (placement.Bottom < verticalOffset)
                    continue;

                return (block.BlockIndex, placement.Top - verticalOffset);
            }
        }

        return null;
    }

    /// <summary>
    /// Captures replacement-time anchor state only when the current bounds can
    /// be read immediately. The UI thread must not wait behind lazy measure.
    /// </summary>
    internal bool TryCaptureScrollAnchor(double verticalOffset, out SnapshotScrollAnchor anchor)
    {
        anchor = default;
        if (!Monitor.TryEnter(_geometry.Gate))
            return false;

        try
        {
            if (_recovery.IsRequired)
                return false;

            int? blockIndex = null;
            double offsetFromTop = 0;
            if (verticalOffset > 0)
            {
                LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
                if (placements.IsDocumentOrder &&
                    placements.TryFindFirstBottomAtOrAfter(verticalOffset, out int indexedOrdinal))
                {
                    BlockBox block = _geometry.Blocks[indexedOrdinal];
                    LayoutRootPlacement placement = placements.GetPlacement(indexedOrdinal);
                    blockIndex = block.BlockIndex;
                    offsetFromTop = placement.Top - verticalOffset;
                }
                else if (!placements.IsDocumentOrder)
                {
                    // Defensive custom placements may not be in document order.
                    // Keep the established document-order choice for that rare
                    // case rather than silently changing anchoring semantics.
                    for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
                    {
                        BlockBox block = _geometry.Blocks[ordinal];
                        LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                        if (placement.Bottom < verticalOffset)
                            continue;

                        blockIndex = block.BlockIndex;
                        offsetFromTop = placement.Top - verticalOffset;
                        break;
                    }
                }
            }

            anchor = new SnapshotScrollAnchor(blockIndex, offsetFromTop, _geometry.Size.Height);
            return true;
        }
        finally
        {
            Monitor.Exit(_geometry.Gate);
        }
    }

    /// <summary>
    /// Captures the scalar geometry needed to navigate to a block without
    /// allowing callers to retain mutable box references beyond the snapshot
    /// read lease. A lazy, unmeasured owner reports its estimated outer band;
    /// its descendant position is not authoritative until that owner measures.
    /// </summary>
    internal SnapshotBlockScrollTargetState TryCaptureBlockScrollTarget(
        int blockIndex,
        out SnapshotBlockScrollTarget target)
    {
        target = default;
        if (!Monitor.TryEnter(_geometry.Gate))
            return SnapshotBlockScrollTargetState.Busy;

        try
        {
            if (_recovery.IsRequired)
                return SnapshotBlockScrollTargetState.Unavailable;

            if (_retirement.IsRetirementRequested ||
                _retirement.IsDisposed)
            {
                return SnapshotBlockScrollTargetState.Unavailable;
            }

            if (!_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal) ||
                (uint)ordinal >= (uint)_geometry.Blocks.Count)
            {
                return SnapshotBlockScrollTargetState.NotFound;
            }

            BlockBox owner = _geometry.Blocks[ordinal];
            bool measured = !_geometry.LazyLayoutEnabled ||
                _geometry.MeasuredTopLevelBlocks is null ||
                _geometry.MeasuredTopLevelBlocks[ordinal];
            if (!TryFindBlockBoundsNoLock(owner, blockIndex, out Rect requestedBounds))
                return SnapshotBlockScrollTargetState.NotFound;

            LayoutRootPlacement ownerPlacement = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal);
            Rect ownerBounds = ownerPlacement.LogicalBounds;
            requestedBounds = _geometry.CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, requestedBounds);
            target = new SnapshotBlockScrollTarget(
                blockIndex,
                measured ? requestedBounds.Top : ownerBounds.Top,
                ownerBounds.Top,
                ownerBounds.Bottom,
                ownerBounds.Height,
                measured,
                _geometry.LayoutRevision);
            return measured
                ? SnapshotBlockScrollTargetState.Ready
                : SnapshotBlockScrollTargetState.RequiresRealization;
        }
        finally
        {
            Monitor.Exit(_geometry.Gate);
        }
    }

    private static bool TryFindBlockBoundsNoLock(BlockBox box, int blockIndex, out Rect bounds)
    {
        if (box.BlockIndex == blockIndex)
        {
            bounds = box.Bounds;
            return true;
        }

        switch (box)
        {
            case ListItemBox listItem:
                if (TryFindBlockBoundsNoLock(listItem.Marker, blockIndex, out bounds) ||
                    TryFindBlockBoundsNoLock(listItem.Content, blockIndex, out bounds))
                {
                    return true;
                }
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryFindBlockBoundsNoLock(child, blockIndex, out bounds))
                        return true;
                }
                break;
            case CodeBlockBox codeBlock:
                foreach (InlineContainerBox chunk in codeBlock.Chunks)
                {
                    if (TryFindBlockBoundsNoLock(chunk, blockIndex, out bounds))
                        return true;
                }
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryFindBlockBoundsNoLock(cell, blockIndex, out bounds))
                        return true;
                }
                break;
        }

        bounds = default;
        return false;
    }

    /// <summary>
    /// Returns the top offset only when the captured top-level block identity
    /// still exists, using the same nonblocking lease as paint and hit testing.
    /// </summary>
    internal bool TryCaptureScrollAnchorTarget(
        int blockIndex,
        double offsetFromTop,
        out double targetOffset)
    {
        targetOffset = 0;
        if (!Monitor.TryEnter(_geometry.Gate))
            return false;

        try
        {
            if (_recovery.IsRequired ||
                _retirement.IsRetirementRequested ||
                _retirement.IsDisposed || _geometry.Blocks.Count == 0)
            {
                return false;
            }

            if (_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            {
                targetOffset = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal).Top - offsetFromTop;
                return double.IsFinite(targetOffset);
            }

            // The caller preserves the existing height-ratio fallback when
            // the captured block identity no longer exists in the replacement
            // snapshot. Never anchor to a merely nearby, unrelated block.
            return false;
        }
        finally
        {
            Monitor.Exit(_geometry.Gate);
        }
    }

    internal IReadOnlyList<BlockBox> GetMeasuredTopLevelBlocks()
    {
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return Array.Empty<BlockBox>();

            if (!_geometry.LazyLayoutEnabled || _geometry.MeasuredTopLevelBlocks is null)
                return _geometry.Blocks;

            var measured = new List<BlockBox>(_geometry.MeasuredTopLevelBlockCount);
            for (int n = 0; n < _geometry.Blocks.Count; n++)
            {
                if (_geometry.MeasuredTopLevelBlocks[n])
                    measured.Add(_geometry.Blocks[n]);
            }

            return measured;
        }
    }

    internal bool IsTopLevelBlockMeasured(BlockBox block)
    {
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return false;

            if (!_geometry.LazyLayoutEnabled || _geometry.MeasuredTopLevelBlocks is null)
                return true;

            return !_geometry.BlockOrdinals.TryGetValue(block, out int ordinal) ||
                _geometry.MeasuredTopLevelBlocks[ordinal];
        }
    }

    /// <summary>
    /// Reads the lazy-layout state for a focusable descendant without making
    /// the UI thread wait behind an in-flight background measurement. For an
    /// unrealized descendant, <paramref name="estimatedBand"/> is the current
    /// estimated bounds of its owning top-level block.
    /// </summary>
    internal FocusTargetLayoutState TryGetFocusTargetLayoutState(
        int blockIndex,
        out LazyLayoutBand estimatedBand)
    {
        estimatedBand = default;
        if (!Monitor.TryEnter(_geometry.Gate))
            return FocusTargetLayoutState.Busy;

        try
        {
            if (_recovery.IsRequired ||
                _retirement.IsRetirementRequested ||
                _retirement.IsDisposed)
            {
                return FocusTargetLayoutState.Unavailable;
            }

            if (!_geometry.LazyLayoutEnabled || _geometry.MeasuredTopLevelBlocks is null)
                return FocusTargetLayoutState.Ready;

            if (!_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal) ||
                (uint)ordinal >= (uint)_geometry.Blocks.Count)
            {
                return FocusTargetLayoutState.Unavailable;
            }

            if (_geometry.MeasuredTopLevelBlocks[ordinal])
                return FocusTargetLayoutState.Ready;

            Rect bounds = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal).LogicalBounds;
            if (!double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Bottom))
                return FocusTargetLayoutState.Unavailable;

            double top = Math.Max(0, bounds.Top);
            estimatedBand = new LazyLayoutBand(top, Math.Max(top + 1, bounds.Bottom));
            return FocusTargetLayoutState.RequiresRealization;
        }
        finally
        {
            Monitor.Exit(_geometry.Gate);
        }
    }
}
