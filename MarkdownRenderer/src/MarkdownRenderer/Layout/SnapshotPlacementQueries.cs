using System;
using System.Threading;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Maps between physical box coordinates and the logical coordinates of the
/// committed placement generation, and resolves viewport slots to measured
/// roots. Members suffixed UnderPaint require the caller to hold a snapshot
/// read lease.
/// </summary>
internal sealed class SnapshotPlacementQueries
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;

    internal SnapshotPlacementQueries(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery)
    {
        _geometry = geometry;
        _recovery = recovery;
    }

    /// <summary>
    /// Gets the committed viewport-index range for an admitted interactive
    /// reader. Callers must hold a successful TryBeginPaint/TryBeginInteraction
    /// lease for the entire use of the returned slots.
    /// </summary>
    internal ViewportRange GetMeasuredViewportRangeUnderPaint(Rect viewport)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        if (_recovery.IsRequired ||
            !double.IsFinite(viewport.Top) ||
            !double.IsFinite(viewport.Bottom) ||
            viewport.Bottom < viewport.Top)
        {
            return new ViewportRange(0, 0);
        }

        return _geometry.FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
    }

    /// <summary>
    /// Resolves one viewport-index slot to a measured root intersecting the
    /// requested vertical band. The caller must keep its snapshot read lease.
    /// </summary>
    internal bool TryGetMeasuredViewportBlockAtSlotUnderPaint(
        int slot,
        Rect viewport,
        out BlockBox block)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        block = null!;
        LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
        if (_recovery.IsRequired ||
            (uint)slot >= (uint)placements.Count)
        {
            return false;
        }

        int ordinal = placements.GetBlockOrdinal(slot);
        if ((uint)ordinal >= (uint)_geometry.Blocks.Count)
            return false;

        BlockBox candidate = _geometry.Blocks[ordinal];
        LayoutRootPlacement placement = placements.GetPlacement(ordinal);
        if (placement.Bottom < viewport.Top ||
            placement.Top > viewport.Bottom ||
            !placement.Measured)
        {
            return false;
        }

        block = candidate;
        return true;
    }

    internal int GetBlockOrdinalAtViewportSlotUnderPaint(int slot)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
        if ((uint)slot >= (uint)placements.Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return placements.GetBlockOrdinal(slot);
    }

    internal Rect ToLogicalRectForBlockIndexUnderPaint(int blockIndex, Rect physicalRect)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
            ? _geometry.CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, physicalRect)
            : physicalRect;
    }

    internal LayoutPlacementTransformContext.Scope EnterPlacementTransformScopeForBlockIndexUnderPaint(
        int blockIndex)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        if (!_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            return default;

        LayoutRootPlacement placement = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal);
        return LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
    }

    internal LayoutPlacementTransformContext.Scope EnterPlacementTransformScopeForTopLevelBlockUnderPaint(
        BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return EnterPlacementTransformScopeForBlockIndexUnderPaint(block.BlockIndex);
    }

    internal Rect ToLogicalRectForBlockIndex(int blockIndex, Rect physicalRect)
    {
        lock (_geometry.Gate)
        {
            return _geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
                ? _geometry.CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, physicalRect)
                : physicalRect;
        }
    }

    internal Func<int, Rect, Rect> CaptureLogicalRectMapper()
    {
        lock (_geometry.Gate)
        {
            LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
            return (blockIndex, physicalRect) =>
                _geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
                    ? placements.ToLogicalRect(ordinal, physicalRect)
                    : physicalRect;
        }
    }

    internal Point ToLogicalPointForBlockIndexUnderPaint(int blockIndex, Point physicalPoint)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        if (!_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            return physicalPoint;
        return _geometry.CurrentPlacementGenerationNoLock().ToLogicalPoint(ordinal, physicalPoint);
    }

    internal Point ToPhysicalPointForBlockIndexUnderPaint(int blockIndex, Point logicalPoint)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
            ? _geometry.CurrentPlacementGenerationNoLock().ToPhysicalPoint(ordinal, logicalPoint)
            : logicalPoint;
    }

    internal Rect ToPhysicalViewportForTopLevelBlockUnderPaint(BlockBox block, Rect logicalViewport)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _geometry.BlockOrdinals.TryGetValue(block, out int ordinal)
            ? _geometry.CurrentPlacementGenerationNoLock().ToPhysicalViewport(ordinal, logicalViewport)
            : logicalViewport;
    }

    internal Point ToPhysicalPointForTopLevelBlockUnderPaint(BlockBox block, Point logicalPoint)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _geometry.BlockOrdinals.TryGetValue(block, out int ordinal)
            ? _geometry.CurrentPlacementGenerationNoLock().ToPhysicalPoint(ordinal, logicalPoint)
            : logicalPoint;
    }

    internal bool TryGetLogicalTopForBlockIndex(int blockIndex, out double top)
    {
        lock (_geometry.Gate)
        {
            if (_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            {
                top = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal).Top;
                return double.IsFinite(top);
            }

            top = 0;
            return false;
        }
    }

    internal Rect ToLogicalRectForTopLevelBlockUnderPaint(BlockBox block, Rect physicalRect)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _geometry.BlockOrdinals.TryGetValue(block, out int ordinal)
            ? _geometry.CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, physicalRect)
            : physicalRect;
    }

    internal bool TryGetMeasuredBlockByOrdinalUnderPaint(int ordinal, out BlockBox block)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        block = null!;
        if (_recovery.IsRequired || (uint)ordinal >= (uint)_geometry.Blocks.Count ||
            !_geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal).Measured)
        {
            return false;
        }

        block = _geometry.Blocks[ordinal];
        return true;
    }

    internal bool TryGetMeasuredTopLevelBlockForBlockIndexUnderPaint(
        int blockIndex,
        out BlockBox block)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        block = null!;
        return _geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal) &&
            TryGetMeasuredBlockByOrdinalUnderPaint(ordinal, out block);
    }
}
