using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Root-local relayout for completed images: measures a bounded batch of
/// image-owning roots outside the source gate, then transfers their placements
/// atomically with a prebuilt placement generation. Pointer input arriving
/// after dispatch invalidates the batch through the pointer-input epoch.
/// </summary>
internal sealed class CompletedImageRootRelayout
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly SnapshotRetirement _retirement;
    private readonly ChangedBlockRelayout _relayout;
    private readonly CompletedImageRootCapture _capture;

    // Captured on the UI dispatcher before work is queued. A contact that starts
    // during private measurement must finish against its original geometry;
    // rejecting its old epoch is a retry, never permission for live Measure.
    private long _pointerInputEpoch;

    internal CompletedImageRootRelayout(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        SnapshotRetirement retirement,
        ChangedBlockRelayout relayout,
        CompletedImageRootCapture capture)
    {
        _geometry = geometry;
        _recovery = recovery;
        _retirement = retirement;
        _relayout = relayout;
        _capture = capture;
    }

    internal long PointerInputEpoch => Interlocked.Read(ref _pointerInputEpoch);

    internal void NotifyPointerInput() => Interlocked.Increment(ref _pointerInputEpoch);

    // Sessionless/unsupported roots retain live reflow, but an input delivered
    // since UI dispatch must never enter that fallback with an old gesture.
    // Input arriving after this check cannot use geometry while the gate is
    // held: dispatch defers it and replays it against the completed placement.
    // This preserves targets, not the responsiveness of private preparation.
    internal bool TryRelayoutChangedBlocks(
        IReadOnlyCollection<int> changedBlockIndices, float availableWidth,
        CancellationToken cancellationToken, long expectedPointerInputEpoch)
    {
        lock (_geometry.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_retirement.IsRetiringOrDisposed || _recovery.IsRequired ||
                PointerInputEpoch != expectedPointerInputEpoch)
                return false;
            _relayout.RelayoutChangedBlocks(changedBlockIndices, availableWidth, cancellationToken);
            return true;
        }
    }

    /// <summary>
    /// Prepares one completed image root independently of document
    /// size or sibling state. Unlike whole-tree COW this keeps the same snapshot,
    /// box, semantic/index and subscription identities. Only privately measured
    /// placement resources are transferred; pending sibling work is untouched.
    /// A prebuilt immutable generation supplies new logical positions without
    /// arranging unchanged roots. Generation construction remains O(N).
    /// </summary>
    internal PreparedImageRelayoutResult TryRelayoutCompletedImageRoot(
        IReadOnlyCollection<int> changedBlockIndices,
        float availableWidth,
        CancellationToken cancellationToken,
        out MarkdownLayoutContext? constructionContext,
        long? expectedPointerInputEpoch = null,
        CompletedImageRelayoutPhaseHooks? phaseHooks = null)
    {
        ArgumentNullException.ThrowIfNull(changedBlockIndices);
        constructionContext = null;
        if (changedBlockIndices.Count != 1)
            return PreparedImageRelayoutResult.Unavailable;
        return TryRelayoutCompletedImageRoots(changedBlockIndices, availableWidth,
            cancellationToken, out constructionContext, expectedPointerInputEpoch, phaseHooks);
    }

    /// <summary>
    /// Measures a bounded batch of completed standalone, paragraph/heading or
    /// table/list/quote-owned images outside the source gate, then transfers
    /// every placement atomically with a prebuilt immutable placement generation.
    /// Sibling boxes keep their identities and are not arranged; their logical
    /// positions are resolved through the new generation. Building that
    /// generation remains O(N), while native layout work is bounded to changed roots.
    /// </summary>
    internal PreparedImageRelayoutResult TryRelayoutCompletedImageRoots(
        IReadOnlyCollection<int> changedBlockIndices,
        float availableWidth,
        CancellationToken cancellationToken,
        out MarkdownLayoutContext? constructionContext,
        long? expectedPointerInputEpoch = null,
        CompletedImageRelayoutPhaseHooks? phaseHooks = null)
    {
        ArgumentNullException.ThrowIfNull(changedBlockIndices);
        constructionContext = null;
        int count = changedBlockIndices.Count;
        if (count <= 0 || count > LayoutSnapshot.MaximumPreparedImageRoots || !float.IsFinite(availableWidth))
            return PreparedImageRelayoutResult.Unavailable;

        var batch = new CompletedImageRelayoutBatch(count, availableWidth, cancellationToken);
        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        try
        {
            lock (_geometry.Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.PointerEpoch = expectedPointerInputEpoch ?? PointerInputEpoch;
                if (_retirement.IsRetiringOrDisposed || _recovery.IsRequired ||
                    PointerInputEpoch != batch.PointerEpoch ||
                    availableWidth != _geometry.AvailableWidth || _geometry.LayoutRevision == long.MaxValue)
                    return PreparedImageRelayoutResult.Retry;
                if (!_capture.TryResolveRootsNoLock(batch, changedBlockIndices) ||
                    !_capture.TryAdmitRootsNoLock(batch, out int imageCount))
                    return PreparedImageRelayoutResult.Unavailable;
                constructionContext = batch.Context;
                if (!_capture.TryCaptureClonesNoLock(batch, imageCount))
                    return PreparedImageRelayoutResult.Unavailable;
            }
            // Provider getters, native caption layout, and candidate cleanup
            // must never run under the source's read/paint/interaction gate.
            if (!SingleRasterLayoutCandidate.AreSourceProvidersCurrent(batch.Sources, batch.Context!))
                return PreparedImageRelayoutResult.Retry;
            phaseHooks?.BeforeMeasureOutsideGate?.Invoke();
            if (TryMeasureChangedRoots(batch) is not { } placementInputs ||
                !TryPrepareCandidate(batch, placementInputs))
                return PreparedImageRelayoutResult.Retry;

            // Exercise the real last cancellable boundary after all native
            // preparation and the immutable successor generation are ready,
            // but before any retained owner or published metadata is changed.
            // The following token check is also the first operation under the
            // publication lock, so cancellation cannot expose a partial commit.
            phaseHooks?.BeforeCommitOutsideGate?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            lock (_geometry.Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsBatchCurrentNoLock(batch))
                    return PreparedImageRelayoutResult.Retry;
                cancellationToken.ThrowIfCancellationRequested();
                return PublishNoLock(batch);
            }
        }
        finally
        {
            // On adoption the clone owns the superseded source caption and
            // placeholder, not the new source layouts. Join this fresh context's
            // retirement ledger after scheduled native work actually returns.
            foreach (BlockBox? clone in batch.Prepared)
                if (clone is not null)
                    batch.Context!.DiscardConstructionBlock(clone);
        }
    }

    // Measure changed clones under their exact logical flow origin so
    // native text/vector metrics snap identically to a fresh full
    // layout. Then arrange each detached root at that absolute origin,
    // outside the placement scope. Keeping the candidate's base origin
    // equal to its logical origin preserves the same float recurrence
    // for nested geometry (notably table row/cell placement); translating
    // a local-zero descendant after its internal additions can differ by
    // one ULP from eager Arrange at the final origin.
    //
    // This is also the one ordinal pass that builds candidate inputs.
    // Carry the same single-precision recurrence as AdvanceBlock and
    // LayoutPlacementGeneration.Create; unchanged roots contribute
    // their current (possibly estimated) height without being arranged.
    // Returns null when the batch must be retried.
    private LayoutPlacementInput[]? TryMeasureChangedRoots(CompletedImageRelayoutBatch batch)
    {
        CancellationToken cancellationToken = batch.CancellationToken;
        LayoutPlacementGeneration sourcePlacements = batch.SourcePlacements;
        var placementInputs = new LayoutPlacementInput[_geometry.Blocks.Count];
        int changedInputIndex = 0;
        float flowY = (float)_geometry.DocumentPadding.Top;
        for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LayoutRootPlacement current = sourcePlacements.GetPlacement(ordinal);
            bool isChanged = changedInputIndex < batch.Count && batch.Changed[changedInputIndex].Ordinal == ordinal;
            bool sharesFlowWithNext = sourcePlacements.SharesFlowWithNext(ordinal);
            float height;
            if (isChanged)
            {
                if (!TryMeasureChangedRoot(batch, changedInputIndex, flowY, out height))
                    return null;
                placementInputs[ordinal] = new LayoutPlacementInput(
                    batch.ContentX,
                    flowY,
                    batch.ContentX,
                    batch.ContentWidth,
                    height,
                    measured: true,
                    sharesFlowWithNext);
                changedInputIndex++;
            }
            else
            {
                height = current.Height;
                placementInputs[ordinal] = new LayoutPlacementInput(
                    current.BaseX,
                    current.BaseTop,
                    current.X,
                    current.Width,
                    height,
                    current.Measured,
                    sharesFlowWithNext);
            }

            flowY += height;
            if (ordinal < _geometry.Blocks.Count - 1 && !sharesFlowWithNext)
                flowY += _geometry.BlockSpacing;
            if (!float.IsFinite(flowY))
                return null;
        }
        return changedInputIndex == batch.Count ? placementInputs : null;
    }

    private static bool TryMeasureChangedRoot(
        CompletedImageRelayoutBatch batch,
        int index,
        float flowY,
        out float height)
    {
        height = 0;
        BlockBox prepared = batch.Prepared[index]!;
        float contentX = batch.ContentX;
        float measuredHeight;
        using (LayoutPlacementTransformContext.Enter(contentX, flowY))
        {
            measuredHeight = prepared.Measure(batch.ContentWidth);
        }
        batch.CancellationToken.ThrowIfCancellationRequested();
        if (!float.IsFinite(measuredHeight) || measuredHeight < 0)
            return false;

        // Arrange at the final origin after the measurement scope
        // has ended. The candidate will become the source root at
        // this exact base origin, so applying the scope again here
        // would double-place descendants and DirectWrite layouts.
        prepared.Arrange(contentX, flowY, batch.ContentWidth);
        batch.CancellationToken.ThrowIfCancellationRequested();
        if (batch.ContainerTransfers[index] is { } containerTransfer &&
            !containerTransfer.TrySetExpectedPreparedRootOrigin(contentX, flowY))
        {
            return false;
        }
        // ReflowNoLock advances measured roots from their arranged
        // Bounds.Height, which can differ by one ULP from Measure's
        // desired-height return. Use that same committed extent.
        height = (float)prepared.Bounds.Height;
        if (!float.IsFinite(height) || height < 0)
            return false;
        batch.Changed[index].Height = height;
        return true;
    }

    // Builds the immutable successor generation and fences every native
    // placement off-lock. Returns false when the batch must be retried.
    private bool TryPrepareCandidate(CompletedImageRelayoutBatch batch, LayoutPlacementInput[] placementInputs)
    {
        // Prepared roots are already arranged at their final logical origin.
        // Unchanged roots retain their existing immutable base origins and
        // are repositioned by the successor generation without suffix Arrange.
        long nextRevision = batch.Revision + 1;
        LayoutPlacementGeneration candidatePlacements = LayoutPlacementGeneration.Create(
            placementInputs,
            (float)_geometry.DocumentPadding.Top,
            _geometry.BlockSpacing,
            (float)_geometry.DocumentPadding.Bottom,
            batch.AvailableWidth,
            nextRevision);
        batch.NextRevision = nextRevision;
        batch.CandidatePlacements = candidatePlacements;
        batch.CandidateContinuations = _geometry.LazyLayoutEnabled
            ? candidatePlacements.CopyContinuations()
            : null;
        batch.CandidateMetadata = new(
            candidatePlacements.Size,
            nextRevision,
            batch.MeasuredCount,
            candidatePlacements);
        // Table column resolution may change every cell's measurement width.
        // Fence the old source width and new private width independently;
        // adopting a placement must never assume the source was remeasured.
        ImageBox.MeasuredPlacementTransfer[] transfers = batch.Transfers;
        for (int index = 0; index < transfers.Length; index++)
        {
            ImageBox.MeasuredPlacementTransfer transfer = transfers[index];
            if (!transfer.Prepared.TryCaptureNativeLayoutPlacementFence(out var preparedNativeFence))
                return false;
            transfer = transfer with { ExpectedPreparedNativeLayouts = preparedNativeFence };
            if (transfer.ExpectedInlineLayout)
            {
                if (!transfer.Prepared.TryGetCompletedInlinePlacementWidth(out float preparedWidth))
                    return false;
                transfer = transfer with { ExpectedPreparedAvailableWidth = preparedWidth };
            }
            transfers[index] = transfer;
        }
        // Vector regions refer to the non-publishable metric clones.
        // Rebind them to the retained semantic runs before taking the
        // final gate; publication performs only validated scalar swaps.
        foreach (InlineMeasuredPlacementTransfer inlineTransfer in batch.AllInlineTransfers)
            if (!inlineTransfer.TryPrepareMeasuredVectorGeometry())
                return false;
        batch.CancellationToken.ThrowIfCancellationRequested();
        if (!SingleRasterLayoutCandidate.AreSourceProvidersCurrent(batch.Sources, batch.Context!,
                out long currentProviderGeneration))
            return false;
        batch.CurrentProviderGeneration = currentProviderGeneration;

        // Preallocate outside the read/paint/input gate. Final validation
        // adds at most four owners per inline or image pair, without a
        // resize, replacing the quadratic all-cells alias comparison.
        batch.NativeLayoutOwners = new HashSet<CanvasTextLayout>(
            batch.AllInlineTransfers.Count * 4 + transfers.Length * 4,
            ReferenceEqualityComparer.Instance);
        batch.NativeRetirementOwners = new HashSet<NativeResourceRetirementRecord>(
            batch.AllInlineTransfers.Count * 4 + transfers.Length * 4,
            ReferenceEqualityComparer.Instance);
        return true;
    }

    private bool IsBatchCurrentNoLock(CompletedImageRelayoutBatch batch)
    {
        if (_retirement.IsRetiringOrDisposed || _recovery.IsRequired ||
            PointerInputEpoch != batch.PointerEpoch ||
            _geometry.LayoutRevision != batch.Revision || _geometry.PaintOnlyGeometryTainted ||
            _geometry.AvailableWidth != batch.AvailableWidth)
            return false;

        for (int index = 0; index < batch.Count; index++)
        {
            ChangedBlockMeasure state = batch.Changed[index];
            BlockBox source = batch.SourceRoots[index];
            if (!ReferenceEquals(_geometry.Blocks[state.Ordinal], source) || source.IsDirty ||
                !_geometry.IsTopLevelMeasuredNoLock(state.Ordinal) ||
                !source.Bounds.Equals(state.PreviousBounds) ||
                !source.Margin.Equals(state.PreviousMargin) ||
                !_geometry.HasCoherentCommittedPlacementNoLock(state.Ordinal, source.Bounds, batch.ContentX, batch.ContentWidth))
                return false;
        }
        // Validate every native/run owner before mutating any image.
        // The following swaps never install clone semantic identities.
        foreach (TableMeasuredPlacementTransfer? tableTransfer in batch.TableTransfers)
            if (tableTransfer is not null && !tableTransfer.IsCurrentAndPrepared())
                return false;
        foreach (BlockMeasuredPlacementTransfer? containerTransfer in batch.ContainerTransfers)
            if (containerTransfer is not null && !containerTransfer.IsCurrentAndPrepared())
                return false;
        foreach (InlineMeasuredPlacementTransfer? inlineTransfer in batch.InlineTransfers)
            if (inlineTransfer is not null && !inlineTransfer.IsCurrentAndPrepared())
                return false;
        foreach (InlineMeasuredPlacementTransfer inlineTransfer in batch.AllInlineTransfers)
            if (!inlineTransfer.TryAddNativeLayoutOwners(batch.NativeLayoutOwners, batch.NativeRetirementOwners))
                return false;
        foreach (ImageBox.MeasuredPlacementTransfer imageTransfer in batch.Transfers)
            if (!imageTransfer.Source.TryAddNativePlacementOwners(batch.NativeLayoutOwners, batch.NativeRetirementOwners) ||
                !imageTransfer.Prepared.TryAddNativePlacementOwners(batch.NativeLayoutOwners, batch.NativeRetirementOwners))
                return false;
        return true;
    }

    private PreparedImageRelayoutResult PublishNoLock(CompletedImageRelayoutBatch batch)
    {
        // Publication is one bounded scalar/owner-swap transaction.
        // Cancellation cannot interrupt the adopted placement and
        // metadata pair; all tree measurement and generation/index
        // construction has already completed off-lock.
        bool placementsAdopted = false;
        try
        {
            using var commitScope = LayoutPassCancellation.Push(CancellationToken.None);
            if (!ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    batch.Transfers, batch.CurrentProviderGeneration, batch.ContentWidth))
                return PreparedImageRelayoutResult.Retry;
            placementsAdopted = true;
            foreach (InlineMeasuredPlacementTransfer? inlineTransfer in batch.InlineTransfers)
                inlineTransfer?.SwapPlacementNoThrow();
            foreach (TableMeasuredPlacementTransfer? tableTransfer in batch.TableTransfers)
                tableTransfer?.SwapPlacementNoThrow();
            foreach (BlockMeasuredPlacementTransfer? containerTransfer in batch.ContainerTransfers)
                containerTransfer?.SwapPlacementNoThrow();
            _geometry.PaintOnlyGeometryTainted = true;
            for (int index = 0; index < batch.Count; index++)
            {
                ChangedBlockMeasure state = batch.Changed[index];
                batch.SourceRoots[index].AdoptMeasuredOuterBounds(
                    batch.ContentX,
                    (float)batch.CandidatePlacements.GetPlacement(state.Ordinal).Top,
                    batch.ContentWidth,
                    state.Height);
            }
            foreach (ChangedBlockMeasure state in batch.Changed)
                _geometry.SharedLayoutMetrics?.RecordHeight(state.Ordinal, state.Height);
            _geometry.Size = batch.CandidatePlacements.Size;
            if (batch.CandidateContinuations is not null)
            {
                _geometry.LazyReflowContinuations = batch.CandidateContinuations;
                _geometry.LazyReflowContinuationWidth = _geometry.AvailableWidth;
                _geometry.LazyReflowContinuationRevision = batch.NextRevision;
            }
            _geometry.LayoutRevision = batch.NextRevision;
            _geometry.PublishCommittedMetadataNoLock(batch.CandidateMetadata);
            _geometry.PaintOnlyGeometryTainted = false;
            return PreparedImageRelayoutResult.Committed;
        }
        catch (Exception failure)
        {
            if (placementsAdopted)
                _recovery.MarkGeometryRecoveryRequiredNoLock(failure);
            throw;
        }
    }
}
