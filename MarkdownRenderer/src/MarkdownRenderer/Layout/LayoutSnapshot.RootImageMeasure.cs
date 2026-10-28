using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

internal enum PreparedImageRelayoutResult { Unavailable, Committed, Retry }

internal sealed partial class LayoutSnapshot
{
    // Bounds private native preparation independently of total document size.
    // This does not raise the separate whole-tree COW envelope.
    internal const int MaximumPreparedImageRoots = ImageBox.MaximumMeasuredPlacementTransferBatchCount;
    // Captured on the UI dispatcher before work is queued. A contact that starts
    // during private measurement must finish against its original geometry;
    // rejecting its old epoch is a retry, never permission for live Measure.
    private long _pointerInputEpoch;
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
        lock (_layoutLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRetiringOrDisposed || GeometryRecoveryRequired ||
                PointerInputEpoch != expectedPointerInputEpoch)
                return false;
            RelayoutChangedBlocks(changedBlockIndices, availableWidth, cancellationToken);
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
        Action? beforeMeasureOutsideGateForTests = null,
        long? expectedPointerInputEpoch = null,
        Action? beforeCommitOutsideGateForTests = null)
    {
        ArgumentNullException.ThrowIfNull(changedBlockIndices);
        constructionContext = null;
        if (changedBlockIndices.Count != 1)
            return PreparedImageRelayoutResult.Unavailable;
        return TryRelayoutCompletedImageRoots(changedBlockIndices, availableWidth,
            cancellationToken, out constructionContext, beforeMeasureOutsideGateForTests,
            expectedPointerInputEpoch, beforeCommitOutsideGateForTests);
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
        Action? beforeMeasureOutsideGateForTests = null,
        long? expectedPointerInputEpoch = null,
        Action? beforeCommitOutsideGateForTests = null)
    {
        ArgumentNullException.ThrowIfNull(changedBlockIndices);
        constructionContext = null;
        int count = changedBlockIndices.Count;
        if (count <= 0 || count > MaximumPreparedImageRoots || !float.IsFinite(availableWidth))
            return PreparedImageRelayoutResult.Unavailable;

        var prepared = new BlockBox?[count];
        MarkdownLayoutContext? context = null;
        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        try
        {
            BlockBox[] sourceRoots;
            RasterPlacementSourceFence[] sources;
            ChangedBlockMeasure[] changed;
            ImageBox.MeasuredPlacementTransfer[] transfers;
            InlineContainerBox.MeasuredPlacementTransfer?[] inlineTransfers;
            TableBox.MeasuredPlacementTransfer?[] tableTransfers;
            BlockMeasuredPlacementTransfer?[] containerTransfers;
            var allInlineTransfers = new List<InlineContainerBox.MeasuredPlacementTransfer>();
            float contentWidth;
            float contentX;
            long revision;
            long pointerEpoch;
            int measuredCount;
            LayoutPlacementGeneration sourcePlacements;
            lock (_layoutLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pointerEpoch = expectedPointerInputEpoch ?? PointerInputEpoch;
                if (IsRetiringOrDisposed || GeometryRecoveryRequired ||
                    PointerInputEpoch != pointerEpoch ||
                    availableWidth != _availableWidth || _layoutRevision == long.MaxValue)
                    return PreparedImageRelayoutResult.Retry;
                var ordinals = new int[count];
                var changedIdentities = new HashSet<int>(count);
                int next = 0;
                foreach (int blockIndex in changedBlockIndices)
                {
                    if (next >= count || !changedIdentities.Add(blockIndex) ||
                        !_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
                        return PreparedImageRelayoutResult.Unavailable;
                    ordinals[next++] = ordinal;
                }
                if (next != count || GeometryRecoveryRequired || _paintOnlyGeometryTainted)
                    return PreparedImageRelayoutResult.Unavailable;
                CommittedMetadata committedSource = Volatile.Read(ref _committedMetadata);
                sourcePlacements = committedSource.Placements;
                measuredCount = committedSource.MeasuredCount;
                Array.Sort(ordinals);
                // Image completion reports the owning inline/cell identity,
                // not necessarily the top-level root. Several completed table
                // cells (or root plus child IDs) must prepare that table once,
                // just as the established changed-block reflow does. Keep the
                // input dispatch ceiling and compact only this bounded array.
                int uniqueRootCount = 0;
                for (int index = 0; index < count; index++)
                    if (index == 0 || ordinals[index] != ordinals[index - 1])
                        ordinals[uniqueRootCount++] = ordinals[index];
                count = uniqueRootCount;
                sourceRoots = new BlockBox[count];
                changed = new ChangedBlockMeasure[count];
                inlineTransfers = new InlineContainerBox.MeasuredPlacementTransfer?[count];
                tableTransfers = new TableBox.MeasuredPlacementTransfer?[count];
                containerTransfers = new BlockMeasuredPlacementTransfer?[count];
                contentWidth = GetContentWidthNoLock();
                contentX = (float)_documentPadding.Left;
                // Both descriptor capture and native preparation stay bounded
                // by changed content, not the complete document/sibling tree.
                int textLength = 0;
                int runCount = 0;
                int imageCount = 0;
                int nodeCount = count;
                long styleKeyCopyEntries = 0;
                var imageIdentities = new HashSet<ImageBox>(ReferenceEqualityComparer.Instance);
                var inlineIdentities = new HashSet<InlineContainerBox>(ReferenceEqualityComparer.Instance);
                // These clones can only transfer measured placement back to
                // retained source owners; they are never publishable snapshots.
                // General whole-tree COW capture remains vector-ineligible.
                var tableCapture = new PrivateRasterPlacementTree.CaptureState(
                    allowMeasuredVectorPlacement: true);
                MarkdownLayoutContext? sourceContext = null;
                // Reject unsupported/duplicate roots before acquiring any lease.
                for (int index = 0; index < count; index++)
                {
                    int ordinal = ordinals[index];
                    if (ordinal < 0 || ordinal >= Blocks.Count || !IsTopLevelMeasuredNoLock(ordinal))
                        return PreparedImageRelayoutResult.Unavailable;
                    BlockBox root = Blocks[ordinal];
                    MarkdownLayoutContext? rootContext = GetPreparedImageRootContext(root);
                    if (root.IsDirty || rootContext is null ||
                        rootContext.PerformanceSession is not { IsDisposed: false } ||
                        rootContext.ImagePreparationOwner is null ||
                        !ReferenceEquals(SourceMap, rootContext.SourceMap) ||
                        (sourceContext is not null && !ReferenceEquals(sourceContext, rootContext)) ||
                        !HasCoherentCommittedPlacementNoLock(ordinal, root.Bounds, contentX, contentWidth))
                        return PreparedImageRelayoutResult.Unavailable;
                    sourceContext ??= rootContext;
                    sourceRoots[index] = root;
                    changed[index] = new ChangedBlockMeasure(ordinal, true, root.Bounds, root.Margin,
                        ordinal > 0 && StackBox.SharesSafeHtmlInlineFlow(Blocks[ordinal - 1], root),
                        ordinal < Blocks.Count - 1 && StackBox.SharesSafeHtmlInlineFlow(root, Blocks[ordinal + 1]));
                }
                revision = _layoutRevision;
                context = sourceContext!.CreateRasterPlacementContext(
                    sourceContext.ImageCancellationToken, sourceContext.ImagePreparationOwner!,
                    retirementSink: null, imageUnavailable: null);
                for (int index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (sourceRoots[index])
                    {
                        case ImageBox image:
                            if (image.Alt.Length > MaximumPrivateRasterPlacementTextLength - textLength ||
                                ++imageCount > ImageBox.MaximumMeasuredPlacementTransferBatchCount ||
                                !imageIdentities.Add(image) ||
                                !image.TryClassifyPrivateImagePlacement(context, false, out _, out _))
                                return PreparedImageRelayoutResult.Unavailable;
                            textLength += image.Alt.Length;
                            break;
                        case InlineContainerBox inline:
                            if (!inlineIdentities.Add(inline) ||
                                !inline.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(context, contentWidth,
                                    out int rootRuns, out int rootImages, out long rootStyleKeys) ||
                                rootImages <= 0 ||
                                rootRuns > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount ||
                                rootImages > ImageBox.MaximumMeasuredPlacementTransferBatchCount - imageCount ||
                                inline.PrivateRasterPlacementTextLength >
                                    MaximumPrivateRasterPlacementTextLength - textLength ||
                                !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                                    ref styleKeyCopyEntries, rootStyleKeys))
                                return PreparedImageRelayoutResult.Unavailable;
                            runCount += rootRuns;
                            imageCount += rootImages;
                            textLength += inline.PrivateRasterPlacementTextLength;
                            foreach (var entry in inline.InlineImageRuns)
                                if (!imageIdentities.Add(entry.Run.Image))
                                    return PreparedImageRelayoutResult.Unavailable;
                            break;
                        case TableBox table:
                            // Empty/sparse malicious row arrays must not turn a
                            // bounded cell budget into an unbounded preflight walk.
                            if (table.RowCount is <= 0 or > PrivateRasterPlacementTree.MaximumNodes ||
                                table.ColumnCount is <= 0 or > PrivateRasterPlacementTree.MaximumNodes ||
                                !tableCapture.TryAddRoot(table, sourceContext!, context, contentWidth,
                                    out PrivateRasterPlacementTree.RootCounts tableCounts) ||
                                tableCounts.ImageCount <= 0 ||
                                tableCounts.NodeCount - 1 > PrivateRasterPlacementTree.MaximumNodes - nodeCount ||
                                tableCounts.RunCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount ||
                                tableCounts.ImageCount > ImageBox.MaximumMeasuredPlacementTransferBatchCount - imageCount ||
                                tableCounts.TextLength > MaximumPrivateRasterPlacementTextLength - textLength ||
                                !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                                    ref styleKeyCopyEntries, tableCounts.StyleKeyCopyEntries))
                                return PreparedImageRelayoutResult.Unavailable;
                            nodeCount += tableCounts.NodeCount - 1;
                            runCount += tableCounts.RunCount;
                            imageCount += tableCounts.ImageCount;
                            textLength += tableCounts.TextLength;
                            foreach (InlineContainerBox cell in table.GetCellBoxes())
                            {
                                if (!inlineIdentities.Add(cell))
                                    return PreparedImageRelayoutResult.Unavailable;
                                foreach (var entry in cell.InlineImageRuns)
                                    if (!imageIdentities.Add(entry.Run.Image))
                                        return PreparedImageRelayoutResult.Unavailable;
                            }
                            break;
                        case StackBox or ListItemBox:
                            if (!TryCaptureContainerImageIdentities(sourceRoots[index], inlineIdentities, imageIdentities) ||
                                !tableCapture.TryAddRoot(sourceRoots[index], sourceContext!, context, contentWidth,
                                    out PrivateRasterPlacementTree.RootCounts containerCounts) ||
                                containerCounts.ImageCount <= 0 ||
                                containerCounts.NodeCount - 1 > PrivateRasterPlacementTree.MaximumNodes - nodeCount ||
                                containerCounts.RunCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - runCount ||
                                containerCounts.ImageCount > ImageBox.MaximumMeasuredPlacementTransferBatchCount - imageCount ||
                                containerCounts.TextLength > MaximumPrivateRasterPlacementTextLength - textLength ||
                                !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                                    ref styleKeyCopyEntries, containerCounts.StyleKeyCopyEntries))
                                return PreparedImageRelayoutResult.Unavailable;
                            nodeCount += containerCounts.NodeCount - 1;
                            runCount += containerCounts.RunCount;
                            imageCount += containerCounts.ImageCount;
                            textLength += containerCounts.TextLength;
                            break;
                        default:
                            return PreparedImageRelayoutResult.Unavailable;
                    }
                }
                constructionContext = context;
                var capturedSources = new List<RasterPlacementSourceFence>(imageCount);
                var capturedTransfers = new List<ImageBox.MeasuredPlacementTransfer>(imageCount);
                for (int index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (sourceRoots[index] is ImageBox image)
                    {
                        if (!image.TryCreateCompletedRasterPlacementClone(context, out ImageBox? clone,
                                out SharedCanvasBitmapCache.Lease? lease, out var svgFence) ||
                            clone is null || lease is null)
                            return PreparedImageRelayoutResult.Unavailable;
                        prepared[index] = clone;
                        capturedSources.Add(new(image, lease, svgFence));
                        capturedTransfers.Add(new(image, clone, lease, svgFence));
                    }
                    else if (sourceRoots[index] is InlineContainerBox inline)
                    {
                        if (!inline.TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(context, contentWidth,
                                out InlineContainerBox? clone, out RasterPlacementSourceFence[]? fences) ||
                            clone is null)
                            return PreparedImageRelayoutResult.Unavailable;
                        prepared[index] = clone;
                        if (fences is null || fences.Length != clone.InlineImageRuns.Count ||
                            !inline.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(clone, contentWidth,
                                out inlineTransfers[index]))
                            return PreparedImageRelayoutResult.Unavailable;
                        allInlineTransfers.Add(inlineTransfers[index]!);
                        for (int imageIndex = 0; imageIndex < fences.Length; imageIndex++)
                        {
                            RasterPlacementSourceFence fence = fences[imageIndex];
                            if (fence.Lease is null || fence.IsIdleDescriptor ||
                                !ReferenceEquals(fence.Image, inline.InlineImageRuns[imageIndex].Run.Image) ||
                                !fence.Image.TryGetCompletedInlinePlacementWidth(out float inlineWidth))
                                return PreparedImageRelayoutResult.Unavailable;
                            capturedSources.Add(fence);
                            capturedTransfers.Add(new(fence.Image, clone.InlineImageRuns[imageIndex].Run.Image,
                                fence.Lease, fence.SvgMetadata, ExpectedInlineLayout: true,
                                ExpectedAvailableWidth: inlineWidth));
                        }
                    }
                    else if (sourceRoots[index] is TableBox table)
                    {
                        bool created = PrivateRasterPlacementTree.TryCreateCloneWithMeasuredVectorPlacement(table, sourceContext!, context,
                            contentWidth, out BlockBox? clone, out RasterPlacementSourceFence[]? fences);
                        prepared[index] = clone;
                        if (!created ||
                            clone is not TableBox tableClone)
                            return PreparedImageRelayoutResult.Unavailable;
                        if (fences is null ||
                            !table.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(tableClone, contentWidth,
                                out tableTransfers[index]) || tableTransfers[index] is not { } tableTransfer)
                            return PreparedImageRelayoutResult.Unavailable;
                        int imageIndex = 0;
                        foreach (InlineContainerBox.MeasuredPlacementTransfer cellTransfer in tableTransfer.CellTransfers)
                        {
                            allInlineTransfers.Add(cellTransfer);
                            IReadOnlyList<(InlineImageRun Run, int Offset)> sourceImages = cellTransfer.Source.InlineImageRuns;
                            IReadOnlyList<(InlineImageRun Run, int Offset)> cloneImages = cellTransfer.Prepared.InlineImageRuns;
                            if (sourceImages.Count != cloneImages.Count)
                                return PreparedImageRelayoutResult.Unavailable;
                            for (int cellImageIndex = 0; cellImageIndex < sourceImages.Count; cellImageIndex++)
                            {
                                if (imageIndex >= fences.Length)
                                    return PreparedImageRelayoutResult.Unavailable;
                                RasterPlacementSourceFence fence = fences[imageIndex++];
                                if (fence.Lease is null || fence.IsIdleDescriptor ||
                                    !ReferenceEquals(fence.Image, sourceImages[cellImageIndex].Run.Image) ||
                                    !fence.Image.TryGetCompletedInlinePlacementWidth(out float inlineWidth))
                                    return PreparedImageRelayoutResult.Unavailable;
                                capturedSources.Add(fence);
                                capturedTransfers.Add(new(fence.Image, cloneImages[cellImageIndex].Run.Image,
                                    fence.Lease, fence.SvgMetadata, ExpectedInlineLayout: true,
                                    ExpectedAvailableWidth: inlineWidth));
                            }
                        }
                        if (imageIndex != fences.Length)
                            return PreparedImageRelayoutResult.Unavailable;
                    }
                    else
                    {
                        bool created = PrivateRasterPlacementTree.TryCreateCloneWithMeasuredVectorPlacement(sourceRoots[index], sourceContext!, context,
                            contentWidth, out BlockBox? clone, out RasterPlacementSourceFence[]? fences);
                        prepared[index] = clone;
                        if (!created || clone is null || fences is null ||
                            !BlockMeasuredPlacementTransfer.TryCaptureWithMeasuredVectorPlacement(sourceRoots[index], clone, contentWidth,
                                out containerTransfers[index]) || containerTransfers[index] is not { } containerTransfer)
                            return PreparedImageRelayoutResult.Unavailable;
                        foreach (InlineContainerBox.MeasuredPlacementTransfer leafTransfer in containerTransfer.InlineTransfers)
                            allInlineTransfers.Add(leafTransfer);
                        if (containerTransfer.ImagePairs.Length != fences.Length)
                            return PreparedImageRelayoutResult.Unavailable;
                        for (int imageIndex = 0; imageIndex < fences.Length; imageIndex++)
                        {
                            BlockMeasuredPlacementTransfer.ImagePair pair = containerTransfer.ImagePairs[imageIndex];
                            RasterPlacementSourceFence fence = fences[imageIndex];
                            if (fence.Lease is null || fence.IsIdleDescriptor ||
                                !ReferenceEquals(fence.Image, pair.Source) ||
                                !float.IsFinite(pair.ExpectedSourceWidth) || pair.ExpectedSourceWidth <= 0)
                                return PreparedImageRelayoutResult.Unavailable;
                            capturedSources.Add(fence);
                            capturedTransfers.Add(new(pair.Source, pair.Prepared, fence.Lease, fence.SvgMetadata,
                                ExpectedInlineLayout: pair.IsInline, ExpectedAvailableWidth: pair.ExpectedSourceWidth));
                        }
                    }
                }
                if (capturedSources.Count != imageCount || capturedTransfers.Count != imageCount)
                    return PreparedImageRelayoutResult.Unavailable;
                sources = capturedSources.ToArray();
                transfers = capturedTransfers.ToArray();
            }
            // Provider getters, native caption layout, and candidate cleanup
            // must never run under the source's read/paint/interaction gate.
            if (!SingleRasterLayoutCandidate.AreSourceProvidersCurrent(sources, context))
                return PreparedImageRelayoutResult.Retry;
            beforeMeasureOutsideGateForTests?.Invoke();
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
            long nextRevision = revision + 1;
            var placementInputs = new LayoutPlacementInput[Blocks.Count];
            int changedInputIndex = 0;
            float flowY = (float)_documentPadding.Top;
            for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LayoutRootPlacement current = sourcePlacements.GetPlacement(ordinal);
                bool isChanged = changedInputIndex < count && changed[changedInputIndex].Ordinal == ordinal;
                bool sharesFlowWithNext = sourcePlacements.SharesFlowWithNext(ordinal);
                float height;
                if (isChanged)
                {
                    float measuredHeight;
                    using (LayoutPlacementTransformContext.Enter(contentX, flowY))
                    {
                        measuredHeight = prepared[changedInputIndex]!.Measure(contentWidth);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!float.IsFinite(measuredHeight) || measuredHeight < 0)
                        return PreparedImageRelayoutResult.Retry;

                    // Arrange at the final origin after the measurement scope
                    // has ended. The candidate will become the source root at
                    // this exact base origin, so applying the scope again here
                    // would double-place descendants and DirectWrite layouts.
                    prepared[changedInputIndex]!.Arrange(contentX, flowY, contentWidth);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (containerTransfers[changedInputIndex] is { } containerTransfer &&
                        !containerTransfer.TrySetExpectedPreparedRootOrigin(contentX, flowY))
                    {
                        return PreparedImageRelayoutResult.Retry;
                    }
                    // ReflowNoLock advances measured roots from their arranged
                    // Bounds.Height, which can differ by one ULP from Measure's
                    // desired-height return. Use that same committed extent.
                    height = (float)prepared[changedInputIndex]!.Bounds.Height;
                    if (!float.IsFinite(height) || height < 0)
                        return PreparedImageRelayoutResult.Retry;
                    changed[changedInputIndex].Height = height;
                    placementInputs[ordinal] = new LayoutPlacementInput(
                        contentX,
                        flowY,
                        contentX,
                        contentWidth,
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
                if (ordinal < Blocks.Count - 1 && !sharesFlowWithNext)
                    flowY += _blockSpacing;
                if (!float.IsFinite(flowY))
                    return PreparedImageRelayoutResult.Retry;
            }
            if (changedInputIndex != count)
                return PreparedImageRelayoutResult.Retry;

            // Prepared roots are already arranged at their final logical origin.
            // Unchanged roots retain their existing immutable base origins and
            // are repositioned by the successor generation without suffix Arrange.
            LayoutPlacementGeneration candidatePlacements = LayoutPlacementGeneration.Create(
                placementInputs,
                (float)_documentPadding.Top,
                _blockSpacing,
                (float)_documentPadding.Bottom,
                availableWidth,
                nextRevision);
            float[]? candidateContinuations = _lazyLayoutEnabled
                ? candidatePlacements.CopyContinuations()
                : null;
            CommittedMetadata candidateMetadata = new(
                candidatePlacements.Size,
                nextRevision,
                measuredCount,
                candidatePlacements);
            // Table column resolution may change every cell's measurement width.
            // Fence the old source width and new private width independently;
            // adopting a placement must never assume the source was remeasured.
            for (int index = 0; index < transfers.Length; index++)
            {
                ImageBox.MeasuredPlacementTransfer transfer = transfers[index];
                if (!transfer.Prepared.TryCaptureNativeLayoutPlacementFence(out var preparedNativeFence))
                    return PreparedImageRelayoutResult.Retry;
                transfer = transfer with { ExpectedPreparedNativeLayouts = preparedNativeFence };
                if (transfer.ExpectedInlineLayout)
                {
                    if (!transfer.Prepared.TryGetCompletedInlinePlacementWidth(out float preparedWidth))
                        return PreparedImageRelayoutResult.Retry;
                    transfer = transfer with { ExpectedPreparedAvailableWidth = preparedWidth };
                }
                transfers[index] = transfer;
            }
            // Vector regions refer to the non-publishable metric clones.
            // Rebind them to the retained semantic runs before taking the
            // final gate; publication performs only validated scalar swaps.
            foreach (InlineContainerBox.MeasuredPlacementTransfer inlineTransfer in allInlineTransfers)
                if (!inlineTransfer.TryPrepareMeasuredVectorGeometry())
                    return PreparedImageRelayoutResult.Retry;
            cancellationToken.ThrowIfCancellationRequested();
            if (!SingleRasterLayoutCandidate.AreSourceProvidersCurrent(sources, context,
                    out long currentProviderGeneration))
                return PreparedImageRelayoutResult.Retry;

            // Preallocate outside the read/paint/input gate. Final validation
            // adds at most four owners per inline or image pair, without a
            // resize, replacing the quadratic all-cells alias comparison.
            var nativeLayoutOwners = new HashSet<CanvasTextLayout>(
                allInlineTransfers.Count * 4 + transfers.Length * 4,
                ReferenceEqualityComparer.Instance);
            var nativeRetirementOwners = new HashSet<NativeResourceRetirementRecord>(
                allInlineTransfers.Count * 4 + transfers.Length * 4,
                ReferenceEqualityComparer.Instance);

            // Exercise the real last cancellable boundary after all native
            // preparation and the immutable successor generation are ready,
            // but before any retained owner or published metadata is changed.
            // The following token check is also the first operation under the
            // publication lock, so cancellation cannot expose a partial commit.
            beforeCommitOutsideGateForTests?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            lock (_layoutLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRetiringOrDisposed || GeometryRecoveryRequired ||
                    PointerInputEpoch != pointerEpoch ||
                    _layoutRevision != revision || _paintOnlyGeometryTainted ||
                    _availableWidth != availableWidth)
                    return PreparedImageRelayoutResult.Retry;

                for (int index = 0; index < count; index++)
                {
                    ChangedBlockMeasure state = changed[index];
                    BlockBox source = sourceRoots[index];
                    if (!ReferenceEquals(Blocks[state.Ordinal], source) || source.IsDirty ||
                        !IsTopLevelMeasuredNoLock(state.Ordinal) ||
                        !source.Bounds.Equals(state.PreviousBounds) ||
                        !source.Margin.Equals(state.PreviousMargin) ||
                        !HasCoherentCommittedPlacementNoLock(state.Ordinal, source.Bounds, contentX, contentWidth))
                        return PreparedImageRelayoutResult.Retry;
                }
                // Validate every native/run owner before mutating any image.
                // The following swaps never install clone semantic identities.
                foreach (TableBox.MeasuredPlacementTransfer? tableTransfer in tableTransfers)
                    if (tableTransfer is not null && !tableTransfer.IsCurrentAndPrepared())
                        return PreparedImageRelayoutResult.Retry;
                foreach (BlockMeasuredPlacementTransfer? containerTransfer in containerTransfers)
                    if (containerTransfer is not null && !containerTransfer.IsCurrentAndPrepared())
                        return PreparedImageRelayoutResult.Retry;
                foreach (InlineContainerBox.MeasuredPlacementTransfer? inlineTransfer in inlineTransfers)
                    if (inlineTransfer is not null && !inlineTransfer.IsCurrentAndPrepared())
                        return PreparedImageRelayoutResult.Retry;
                foreach (InlineContainerBox.MeasuredPlacementTransfer inlineTransfer in allInlineTransfers)
                    if (!inlineTransfer.TryAddNativeLayoutOwners(nativeLayoutOwners, nativeRetirementOwners))
                        return PreparedImageRelayoutResult.Retry;
                foreach (ImageBox.MeasuredPlacementTransfer imageTransfer in transfers)
                    if (!imageTransfer.Source.TryAddNativePlacementOwners(nativeLayoutOwners, nativeRetirementOwners) ||
                        !imageTransfer.Prepared.TryAddNativePlacementOwners(nativeLayoutOwners, nativeRetirementOwners))
                        return PreparedImageRelayoutResult.Retry;
                cancellationToken.ThrowIfCancellationRequested();

                // Publication is one bounded scalar/owner-swap transaction.
                // Cancellation cannot interrupt the adopted placement and
                // metadata pair; all tree measurement and generation/index
                // construction has already completed off-lock.
                bool placementsAdopted = false;
                try
                {
                    using var commitScope = LayoutPassCancellation.Push(CancellationToken.None);
                    if (!ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                            transfers, currentProviderGeneration, contentWidth))
                        return PreparedImageRelayoutResult.Retry;
                    placementsAdopted = true;
                    foreach (InlineContainerBox.MeasuredPlacementTransfer? inlineTransfer in inlineTransfers)
                        inlineTransfer?.SwapPlacementNoThrow();
                    foreach (TableBox.MeasuredPlacementTransfer? tableTransfer in tableTransfers)
                        tableTransfer?.SwapPlacementNoThrow();
                    foreach (BlockMeasuredPlacementTransfer? containerTransfer in containerTransfers)
                        containerTransfer?.SwapPlacementNoThrow();
                    _paintOnlyGeometryTainted = true;
                    for (int index = 0; index < count; index++)
                    {
                        ChangedBlockMeasure state = changed[index];
                        sourceRoots[index].AdoptMeasuredOuterBounds(
                            contentX,
                            (float)candidatePlacements.GetPlacement(state.Ordinal).Top,
                            contentWidth,
                            state.Height);
                    }
                    foreach (ChangedBlockMeasure state in changed)
                        _sharedLayoutMetrics?.RecordHeight(state.Ordinal, state.Height);
                    _size = candidatePlacements.Size;
                    if (candidateContinuations is not null)
                    {
                        _lazyReflowContinuations = candidateContinuations;
                        _lazyReflowContinuationWidth = _availableWidth;
                        _lazyReflowContinuationRevision = nextRevision;
                    }
                    _layoutRevision = nextRevision;
                    PublishCommittedMetadataNoLock(candidateMetadata);
                    _paintOnlyGeometryTainted = false;
                    return PreparedImageRelayoutResult.Committed;
                }
                catch (Exception failure)
                {
                    if (placementsAdopted)
                        MarkGeometryRecoveryRequiredNoLock(failure);
                    throw;
                }
            }
        }
        finally
        {
            // On adoption the clone owns the superseded source caption and
            // placeholder, not the new source layouts. Join this fresh context's
            // retirement ledger after scheduled native work actually returns.
            foreach (BlockBox? clone in prepared)
                if (clone is not null)
                    context!.DiscardConstructionBlock(clone);
        }
    }

    private static MarkdownLayoutContext? GetPreparedImageRootContext(BlockBox root) => root switch
    {
        ImageBox image => image.Context,
        TableBox table when table.SafeHtmlInlineFlowScopeId is null => table.Context,
        InlineContainerBox inline when inline.SafeHtmlInlineFlowScopeId is null &&
            inline.ElementKey is MarkdownElementKeys.Body or MarkdownElementKeys.Heading1 or
                MarkdownElementKeys.Heading2 or MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
                MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 => inline.Context,
        StackBox or ListItemBox when PrivateRasterPlacementTree.TryGetFirstInlineContext(root, out var context) => context,
        _ => null,
    };

    // Reject unsupported nested shapes and cross-root identities before the
    // shared descriptor walk (a sparse nested table is outside this slice) and
    // acquiring any bitmap lease. Table roots have their own variable-width
    // publisher; ordinary container trees deliberately retain fixed leaf widths.
    private static bool TryCaptureContainerImageIdentities(
        BlockBox root, HashSet<InlineContainerBox> inlineIdentities, HashSet<ImageBox> imageIdentities)
    {
        int visited = 0;
        return Visit(root, 0);

        bool Visit(BlockBox box, int depth)
        {
            if (depth > PrivateRasterPlacementTree.MaximumDepth ||
                ++visited > PrivateRasterPlacementTree.MaximumNodes)
                return false;
            switch (box)
            {
                case ImageBox image:
                    return imageIdentities.Add(image);
                case InlineContainerBox inline:
                    if (inline.ElementKey is not (MarkdownElementKeys.Body or MarkdownElementKeys.Heading1 or
                            MarkdownElementKeys.Heading2 or MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
                            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or MarkdownElementKeys.ListMarker) ||
                        inline.Runs.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns ||
                        inline.InlineImageRuns.Count > ImageBox.MaximumMeasuredPlacementTransferBatchCount ||
                        !inlineIdentities.Add(inline))
                        return false;
                    foreach (var entry in inline.InlineImageRuns)
                        if (!imageIdentities.Add(entry.Run.Image))
                            return false;
                    return true;
                case StackBox stack when stack.GetType() == typeof(StackBox) && stack.IsPrivateRasterPlacementCloneEligible:
                    if (stack.Children.Count > PrivateRasterPlacementTree.MaximumNodes - visited)
                        return false;
                    foreach (BlockBox child in stack.Children)
                        if (!Visit(child, depth + 1))
                            return false;
                    return true;
                case ListItemBox list when list.SafeHtmlInlineFlowScopeId is null:
                    return Visit(list.Marker, depth + 1) && Visit(list.Content, depth + 1);
                default:
                    return false;
            }
        }
    }
}
