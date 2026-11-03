using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Live relayout transactions over a snapshot's own boxes: full remeasure of
/// measured roots after a width change, and changed-root relayout with
/// suffix-only reflow. Both restore committed placements and latch geometry
/// recovery when a failed pass cannot be certified.
/// </summary>
internal sealed class ChangedBlockRelayout
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;

    internal ChangedBlockRelayout(
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
        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        lock (_geometry.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_recovery.IsRequired)
                return;

            float normalizedWidth = Math.Max(1f, availableWidth);
            float contentWidth = _geometry.GetContentWidth(normalizedWidth);
            float contentX = (float)_geometry.DocumentPadding.Left;
            _geometry.GetRelayoutScratchNoLock(out float[] heights, out Rect[] previousBounds);
            int lastMeasureAttempted = -1;
            bool priorPaintOnlyGeometryTainted = _geometry.PaintOnlyGeometryTainted;
            _geometry.PaintOnlyGeometryTainted = true;
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
                using var commitScope = LayoutPassCancellation.Push(CancellationToken.None);
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

    /// <summary>
    /// Remeasures only the top-level owners of asynchronously changed content,
    /// then repositions their trailing siblings using already committed heights.
    /// This keeps a late image from rebuilding every DirectWrite text layout in
    /// the document while preserving exact downstream geometry. Cancellation is
    /// honored through measurement and becomes non-cancelable once placement
    /// starts so the snapshot cannot publish a canceled partial suffix.
    /// </summary>
    internal void RelayoutChangedBlocks(
        IReadOnlyCollection<int> changedBlockIndices,
        float availableWidth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changedBlockIndices);
        if (changedBlockIndices.Count == 0)
            return;

        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        lock (_geometry.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_recovery.IsRequired)
                return;

            float normalizedWidth = Math.Max(1f, availableWidth);
            if (Math.Abs(normalizedWidth - _geometry.AvailableWidth) > 0.5f)
            {
                // Width changes affect line wrapping in every measured block
                // and therefore still require the complete relayout contract.
                RelayoutMeasuredBlocks(normalizedWidth, cancellationToken);
                return;
            }

            var changedOrdinals = new HashSet<int>();
            int firstChangedOrdinal = _geometry.Blocks.Count;
            bool hasUnknownOwner = false;
            foreach (int blockIndex in changedBlockIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
                {
                    hasUnknownOwner = true;
                    continue;
                }

                changedOrdinals.Add(ordinal);
                firstChangedOrdinal = Math.Min(firstChangedOrdinal, ordinal);
            }

            if (hasUnknownOwner)
            {
                // A custom box can raise completion without participating in
                // the normal block-index contract. Preserve its loaded state,
                // but fall back to the established whole-tree relayout so its
                // new intrinsic size cannot leave committed geometry stale.
                RelayoutMeasuredBlocks(normalizedWidth, cancellationToken);
                return;
            }

            if (changedOrdinals.Count == 0)
                return;

            float contentWidth = _geometry.GetContentWidthNoLock();
            float contentX = (float)_geometry.DocumentPadding.Left;
            int[] orderedChangedOrdinals = new int[changedOrdinals.Count];
            int nextChangedIndex = 0;
            foreach (int ordinal in changedOrdinals)
                orderedChangedOrdinals[nextChangedIndex++] = ordinal;
            Array.Sort(orderedChangedOrdinals);

            var changedMeasures = new ChangedBlockMeasure[orderedChangedOrdinals.Length];
            for (int index = 0; index < orderedChangedOrdinals.Length; index++)
            {
                int ordinal = orderedChangedOrdinals[index];
                BlockBox block = _geometry.Blocks[ordinal];
                changedMeasures[index] = new ChangedBlockMeasure(
                    ordinal,
                    _geometry.IsTopLevelMeasuredNoLock(ordinal),
                    block.Bounds,
                    block.Margin,
                    ordinal > 0 && StackBox.SharesSafeHtmlInlineFlow(
                        _geometry.Blocks[ordinal - 1],
                        block),
                    ordinal < _geometry.Blocks.Count - 1 && StackBox.SharesSafeHtmlInlineFlow(
                        block,
                        _geometry.Blocks[ordinal + 1]));
            }

            bool priorPaintOnlyGeometryTainted = _geometry.PaintOnlyGeometryTainted;
            _geometry.PaintOnlyGeometryTainted = true;
            bool anyMeasureAttempted = false;
            try
            {
                for (int index = 0; index < changedMeasures.Length; index++)
                {
                    ChangedBlockMeasure state = changedMeasures[index];
                    if (!state.IsMeasured)
                        continue;

                    cancellationToken.ThrowIfCancellationRequested();
                    BlockBox block = _geometry.Blocks[state.Ordinal];
                    block.ThrowIfCancellationRequested();
                    state.MeasureAttempted = true;
                    changedMeasures[index] = state;
                    anyMeasureAttempted = true;
                    state.Height = block.Measure(contentWidth);
                    changedMeasures[index] = state;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (anyMeasureAttempted)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                RestoreChangedBlockPlacementsNoLock(changedMeasures);
                throw;
            }

            // When a changed root joins or leaves a safe-HTML inline flow, the
            // preceding root's continuation also changes. Reflow must begin at
            // that predecessor; otherwise the old shared-flow boundary is
            // retained and both the changed root and its suffix shift incorrectly.
            foreach (ChangedBlockMeasure state in changedMeasures)
            {
                if (state.Ordinal > 0 &&
                    state.SharedFlowWithPrevious != StackBox.SharesSafeHtmlInlineFlow(
                        _geometry.Blocks[state.Ordinal - 1],
                        _geometry.Blocks[state.Ordinal]))
                {
                    firstChangedOrdinal = Math.Min(firstChangedOrdinal, state.Ordinal - 1);
                }
            }

            // A visual-order snapshot has no trustworthy document prefix from
            // which a suffix transaction can continue. Reflow from the document
            // origin and rebuild the index from the resulting actual bounds.
            if (!_geometry.CurrentPlacementGenerationNoLock().IsDocumentOrder)
                firstChangedOrdinal = 0;

            bool unchangedGeometry = false;
            long unchangedRevision = 0;
            LayoutSnapshotCommittedMetadata? unchangedMetadata = null;
            try
            {
                unchangedGeometry = changedMeasures.Length == 1 &&
                    changedMeasures[0].IsMeasured &&
                    CanUseUnchangedGeometryCommitNoLock(
                        changedMeasures[0],
                        contentX,
                        contentWidth);
                if (unchangedGeometry)
                {
                    unchangedRevision = unchecked(_geometry.LayoutRevision + 1);
                    unchangedMetadata = _geometry.CreateCommittedMetadataNoLock(_geometry.Size, unchangedRevision);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (anyMeasureAttempted)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                RestoreChangedBlockPlacementsNoLock(changedMeasures);
                throw;
            }

            if (unchangedGeometry)
            {
                ChangedBlockMeasure state = changedMeasures[0];
                BlockBox block = _geometry.Blocks[state.Ordinal];
                // No token checks may interrupt the arrange/publication pair.
                using var commitScope = LayoutPassCancellation.Push(CancellationToken.None);
                try
                {
                    block.Arrange(
                        (float)state.PreviousBounds.X,
                        (float)state.PreviousBounds.Y,
                        (float)state.PreviousBounds.Width);
                    _geometry.SharedLayoutMetrics?.RecordHeight(state.Ordinal, state.Height);
                    _geometry.PreserveLazyReflowContinuationRevisionNoLock(_geometry.LayoutRevision, unchangedRevision);
                    _geometry.LayoutRevision = unchangedRevision;
                    _geometry.PublishCommittedMetadataNoLock(unchangedMetadata!);
                    _geometry.PaintOnlyGeometryTainted = priorPaintOnlyGeometryTainted;
                    return;
                }
                catch (Exception failure)
                {
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                    throw;
                }
            }

            long committedRevision;
            LayoutSnapshotCommittedMetadata committed;
            try
            {
                committedRevision = unchecked(_geometry.LayoutRevision + 1);
                // Reserve before the first live placement. The mandatory
                // sequential Arrange pass below computes final height too;
                // a second suffix walk adds gate work without changing it.
                committed = _geometry.CreateCommittedMetadataNoLock(_geometry.Size, committedRevision);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (anyMeasureAttempted)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                RestoreChangedBlockPlacementsNoLock(changedMeasures);
                throw;
            }

            // Measurement remains cancellable. Once any outer placement starts,
            // nested StackBox/TableBox arrangement and metadata/index publication
            // finish under one non-cancelable commit scope. This is a cancellation
            // boundary, not copy-on-write: an arbitrary Measure/Arrange exception
            // can still leave a mutated box tree and needs a separate transactional
            // box/snapshot design.
            bool arrangeAttempted = false;
            try
            {
                using (LayoutPassCancellation.Push(CancellationToken.None))
                {
                    float y = GetChangedRelayoutStartNoLock(firstChangedOrdinal);
                    float[]? continuationUpdate = _geometry.BeginLazyReflowContinuationUpdateNoLock(firstChangedOrdinal, y);
                    int changedIndex = 0;
                    for (int ordinal = firstChangedOrdinal; ordinal < _geometry.Blocks.Count; ordinal++)
                    {
                        while (changedIndex < changedMeasures.Length &&
                            changedMeasures[changedIndex].Ordinal < ordinal)
                        {
                            changedIndex++;
                        }

                        bool isChanged = changedIndex < changedMeasures.Length &&
                            changedMeasures[changedIndex].Ordinal == ordinal;
                        BlockBox block = _geometry.Blocks[ordinal];
                        float height;
                        if (_geometry.IsTopLevelMeasuredNoLock(ordinal))
                        {
                            height = isChanged
                                ? changedMeasures[changedIndex].Height
                                : (float)block.Bounds.Height;
                            arrangeAttempted = true;
                            block.Arrange(contentX, y, contentWidth);
                            if (isChanged)
                                _geometry.SharedLayoutMetrics?.RecordHeight(ordinal, height);
                        }
                        else
                        {
                            height = _geometry.EstimateHeight(block, ordinal);
                            arrangeAttempted = true;
                            block.ArrangeEstimated(contentX, y, contentWidth, height);
                        }

                        _geometry.AdvanceBlock(ref y, height, ordinal);
                        if (continuationUpdate is not null)
                            continuationUpdate[ordinal] = y;
                    }

                    y += (float)_geometry.DocumentPadding.Bottom;
                    _geometry.Size = new Size(_geometry.AvailableWidth, y);
                    _geometry.CompleteCommittedMetadataSizeNoLock(committed, _geometry.Size);
                    _geometry.RefreshViewportIndexNoLock(firstChangedOrdinal);
                    _geometry.LayoutRevision = committedRevision;
                    _geometry.CompleteLazyReflowContinuationUpdateNoLock(continuationUpdate, committedRevision);
                    _geometry.PublishCommittedMetadataNoLock(committed);
                }
            }
            catch (Exception failure)
            {
                if (arrangeAttempted || anyMeasureAttempted)
                    _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
                throw;
            }
            _geometry.PaintOnlyGeometryTainted = priorPaintOnlyGeometryTainted;
        }
    }

    private bool CanUseUnchangedGeometryCommitNoLock(
        ChangedBlockMeasure state,
        float contentX,
        float contentWidth)
    {
        BlockBox changedBlock = _geometry.Blocks[state.Ordinal];
        return state.Height == state.PreviousBounds.Height &&
            changedBlock.Bounds.Height == state.PreviousBounds.Height &&
            changedBlock.Bounds.Width == state.PreviousBounds.Width &&
            changedBlock.Margin.Equals(state.PreviousMargin) &&
            state.SharedFlowWithPrevious == (state.Ordinal > 0 &&
                StackBox.SharesSafeHtmlInlineFlow(
                    _geometry.Blocks[state.Ordinal - 1],
                    changedBlock)) &&
            state.SharedFlowWithNext == (state.Ordinal < _geometry.Blocks.Count - 1 &&
                StackBox.SharesSafeHtmlInlineFlow(
                    changedBlock,
                    _geometry.Blocks[state.Ordinal + 1])) &&
            _geometry.HasCoherentCommittedPlacementNoLock(
                state.Ordinal,
                state.PreviousBounds,
                contentX,
                contentWidth);
    }

    private float GetChangedRelayoutStartNoLock(int firstChangedOrdinal)
    {
        LayoutPlacementGeneration placements = _geometry.CurrentPlacementGenerationNoLock();
        return firstChangedOrdinal == 0 || !placements.IsDocumentOrder
            ? (float)_geometry.DocumentPadding.Top
            : placements.GetContinuation(firstChangedOrdinal - 1);
    }

    private void RestoreChangedBlockPlacementsNoLock(ChangedBlockMeasure[] changedMeasures)
    {
        // Restore only committed outer placement and mark attempted owners dirty.
        // A canceled nested Measure may already have changed child/native state;
        // this helper deliberately does not claim to roll those mutations back.
        foreach (ChangedBlockMeasure state in changedMeasures)
        {
            if (!state.MeasureAttempted)
                continue;

            Rect bounds = state.PreviousBounds;
            _geometry.Blocks[state.Ordinal].ArrangeEstimated(
                (float)bounds.X,
                (float)bounds.Y,
                (float)bounds.Width,
                (float)bounds.Height);
        }
    }
}
