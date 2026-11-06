using System;
using System.Threading;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Full live relayout of a snapshot's measured roots after a width change or
/// an unknown changed owner. Measurement stays cancellable; arrangement and
/// publication then finish as one non-cancelable commit.
/// </summary>
internal sealed class MeasuredBlocksRelayout
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;

    internal MeasuredBlocksRelayout(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery)
    {
        _geometry = geometry;
        _recovery = recovery;
    }

    /// <summary>
    /// Remeasures the committed block tree after an asynchronous image changes its
    /// intrinsic size. Reusing the current boxes preserves resolver-partitioned image
    /// state; rebuilding would create a fresh unresolved box and restart the load.
    /// </summary>
    internal void RelayoutMeasuredBlocks(float availableWidth, CancellationToken cancellationToken)
    {
        lock (_geometry.Gate)
        {
            using var cancellationScope = _geometry.PassCancellation.Push(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_recovery.IsRequired)
                return;

            float normalizedWidth = Math.Max(1f, availableWidth);
            float contentWidth = _geometry.GetContentWidth(normalizedWidth);
            float contentX = (float)_geometry.DocumentPadding.Left;
            _geometry.GetRelayoutScratchNoLock(out float[] heights, out Rect[] previousBounds);
            bool priorPaintOnlyGeometryTainted = _geometry.PaintOnlyGeometryTainted;
            _geometry.PaintOnlyGeometryTainted = true;
            int lastMeasureAttempted = MeasureRootsNoLock(heights, previousBounds, contentWidth, cancellationToken);

            Size committedSize;
            long nextRevision;
            LayoutSnapshotCommittedMetadata committedMetadata;
            try
            {
                float documentHeight = _geometry.ComputeDocumentHeightNoLock(heights, cancellationToken);
                committedSize = new Size(normalizedWidth, documentHeight);
                nextRevision = unchecked(_geometry.LayoutRevision + 1);
                committedMetadata = _geometry.CreateCommittedMetadataNoLock(committedSize, nextRevision);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (lastMeasureAttempted >= 0)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                _geometry.RestoreMeasuredPlacementsNoLock(lastMeasureAttempted, previousBounds);
                throw;
            }

            // Measurement remains cancellable. Once placement starts, every
            // top-level and nested arrange plus its index/metadata publication
            // must finish as one snapshot commit, even if a rebuild arrives.
            bool arrangeAttempted = false;
            try
            {
                using var commitScope = _geometry.PassCancellation.Push(CancellationToken.None);
                float[]? continuationUpdate = ArrangeRootsNoLock(heights, contentX, contentWidth, ref arrangeAttempted);
                _geometry.AvailableWidth = normalizedWidth;
                _geometry.Size = committedSize;
                _geometry.RefreshViewportIndexNoLock();
                _geometry.LayoutRevision = nextRevision;
                _geometry.CompleteLazyReflowContinuationUpdateNoLock(continuationUpdate, nextRevision);
                _geometry.PublishCommittedMetadataNoLock(committedMetadata);
                // A complete eager relayout repairs every box. For a lazy snapshot,
                // only clear a prior taint after every top-level root was actually
                // measured again; estimated roots may hide failed nested mutation.
                _geometry.PaintOnlyGeometryTainted = priorPaintOnlyGeometryTainted &&
                    _geometry.LazyLayoutEnabled && _geometry.MeasuredTopLevelBlockCount != _geometry.Blocks.Count;
            }
            catch (Exception failure)
            {
                if (lastMeasureAttempted >= 0 || arrangeAttempted)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                throw;
            }
        }
    }

    // Measures every measured root and estimates the rest. Returns the last
    // ordinal whose Measure was attempted, or -1; a failure restores the
    // attempted placements before rethrowing.
    private int MeasureRootsNoLock(
        float[] heights,
        Rect[] previousBounds,
        float contentWidth,
        CancellationToken cancellationToken)
    {
        int lastMeasureAttempted = -1;
        try
        {
            for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockBox block = _geometry.Blocks[ordinal];
                if (_geometry.IsTopLevelMeasuredNoLock(ordinal))
                {
                    block.ThrowIfCancellationRequested();
                    previousBounds[ordinal] = block.Bounds;
                    lastMeasureAttempted = ordinal;
                    heights[ordinal] = block.Measure(contentWidth);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else
                {
                    heights[ordinal] = _geometry.EstimateHeight(block, ordinal);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            if (lastMeasureAttempted >= 0)
                _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
            _geometry.RestoreMeasuredPlacementsNoLock(lastMeasureAttempted, previousBounds);
            throw;
        }
        return lastMeasureAttempted;
    }

    private float[]? ArrangeRootsNoLock(
        float[] heights,
        float contentX,
        float contentWidth,
        ref bool arrangeAttempted)
    {
        float y = (float)_geometry.DocumentPadding.Top;
        float[]? continuationUpdate = _geometry.BeginLazyReflowContinuationUpdateNoLock(0, y);
        for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
        {
            BlockBox block = _geometry.Blocks[ordinal];
            float height = heights[ordinal];
            if (_geometry.IsTopLevelMeasuredNoLock(ordinal))
            {
                arrangeAttempted = true;
                block.Arrange(contentX, y, contentWidth);
                _geometry.SharedLayoutMetrics?.RecordHeight(ordinal, height);
            }
            else
            {
                arrangeAttempted = true;
                block.ArrangeEstimated(contentX, y, contentWidth, height);
            }

            _geometry.AdvanceBlock(ref y, height, ordinal);
            if (continuationUpdate is not null)
                continuationUpdate[ordinal] = y;
        }
        return continuationUpdate;
    }
}
