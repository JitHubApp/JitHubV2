using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Changed-root relayout over a snapshot's own boxes: remeasures only the
/// top-level owners of changed content and reflows their suffix. Width
/// changes and unknown owners fall back to the full measured relayout. A
/// failed pass restores committed placements and latches geometry recovery
/// when it cannot be certified.
/// </summary>
internal sealed class ChangedBlockRelayout
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly MeasuredBlocksRelayout _measuredBlocks;

    internal ChangedBlockRelayout(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery)
    {
        _geometry = geometry;
        _recovery = recovery;
        _measuredBlocks = new MeasuredBlocksRelayout(geometry, recovery);
    }

    /// <inheritdoc cref="MeasuredBlocksRelayout.RelayoutMeasuredBlocks"/>
    internal void RelayoutMeasuredBlocks(float availableWidth, CancellationToken cancellationToken)
        => _measuredBlocks.RelayoutMeasuredBlocks(availableWidth, cancellationToken);

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

        lock (_geometry.Gate)
        {
            using var cancellationScope = _geometry.PassCancellation.Push(cancellationToken);
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

            if (!TryResolveChangedOrdinalsNoLock(changedBlockIndices, cancellationToken,
                    out HashSet<int> changedOrdinals, out int firstChangedOrdinal))
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
            ChangedBlockMeasure[] changedMeasures = CaptureChangedMeasuresNoLock(changedOrdinals);

            bool priorPaintOnlyGeometryTainted = _geometry.PaintOnlyGeometryTainted;
            _geometry.PaintOnlyGeometryTainted = true;
            bool anyMeasureAttempted = MeasureChangedRootsNoLock(changedMeasures, contentWidth, cancellationToken);
            firstChangedOrdinal = GetFirstReflowOrdinalNoLock(changedMeasures, firstChangedOrdinal);

            if (TryCommitUnchangedGeometryNoLock(changedMeasures, contentX, contentWidth,
                    anyMeasureAttempted, priorPaintOnlyGeometryTainted, cancellationToken))
                return;

            CommitSuffixReflowNoLock(changedMeasures, firstChangedOrdinal, contentX, contentWidth,
                anyMeasureAttempted, cancellationToken);
            _geometry.PaintOnlyGeometryTainted = priorPaintOnlyGeometryTainted;
        }
    }

    // Returns false when a changed identity has no known top-level owner.
    private bool TryResolveChangedOrdinalsNoLock(
        IReadOnlyCollection<int> changedBlockIndices,
        CancellationToken cancellationToken,
        out HashSet<int> changedOrdinals,
        out int firstChangedOrdinal)
    {
        changedOrdinals = new HashSet<int>();
        firstChangedOrdinal = _geometry.Blocks.Count;
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
        return !hasUnknownOwner;
    }

    private ChangedBlockMeasure[] CaptureChangedMeasuresNoLock(HashSet<int> changedOrdinals)
    {
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
        return changedMeasures;
    }

    // Returns whether any Measure was attempted; a failure restores the
    // attempted placements before rethrowing.
    private bool MeasureChangedRootsNoLock(
        ChangedBlockMeasure[] changedMeasures,
        float contentWidth,
        CancellationToken cancellationToken)
    {
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
        return anyMeasureAttempted;
    }

    private int GetFirstReflowOrdinalNoLock(ChangedBlockMeasure[] changedMeasures, int firstChangedOrdinal)
    {
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
        return firstChangedOrdinal;
    }

    // Commits a single changed root whose outer geometry is unchanged by
    // arranging it in place. Returns false when the suffix reflow is needed.
    private bool TryCommitUnchangedGeometryNoLock(
        ChangedBlockMeasure[] changedMeasures,
        float contentX,
        float contentWidth,
        bool anyMeasureAttempted,
        bool priorPaintOnlyGeometryTainted,
        CancellationToken cancellationToken)
    {
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

        if (!unchangedGeometry)
            return false;

        ChangedBlockMeasure state = changedMeasures[0];
        BlockBox block = _geometry.Blocks[state.Ordinal];
        // No token checks may interrupt the arrange/publication pair.
        using var commitScope = _geometry.PassCancellation.Push(CancellationToken.None);
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
            return true;
        }
        catch (Exception failure)
        {
            _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
            throw;
        }
    }

    // Places every block from the first changed ordinal to the end: changed
    // and measured blocks at their measured height, unmeasured blocks at their
    // estimate. arrangeAttempted is set before the first placement so a
    // failure marks geometry recovery. Returns the y after the last block.
    private float ArrangeSuffixNoLock(
        ChangedBlockMeasure[] changedMeasures,
        int firstChangedOrdinal,
        float contentX,
        float contentWidth,
        float y,
        float[]? continuationUpdate,
        ref bool arrangeAttempted)
    {
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

        return y;
    }

    private void CommitSuffixReflowNoLock(
        ChangedBlockMeasure[] changedMeasures,
        int firstChangedOrdinal,
        float contentX,
        float contentWidth,
        bool anyMeasureAttempted,
        CancellationToken cancellationToken)
    {
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
            using (_geometry.PassCancellation.Push(CancellationToken.None))
            {
                float y = GetChangedRelayoutStartNoLock(firstChangedOrdinal);
                float[]? continuationUpdate = _geometry.BeginLazyReflowContinuationUpdateNoLock(firstChangedOrdinal, y);
                y = ArrangeSuffixNoLock(
                    changedMeasures,
                    firstChangedOrdinal,
                    contentX,
                    contentWidth,
                    y,
                    continuationUpdate,
                    ref arrangeAttempted);

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
