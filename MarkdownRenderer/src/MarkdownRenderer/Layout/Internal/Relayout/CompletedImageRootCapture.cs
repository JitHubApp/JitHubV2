using System;
using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Relayout;

/// <summary>
/// Capture half of a completed-image relayout, run under the source gate:
/// resolves the changed top-level roots, admits them against the batch
/// budget, and creates the private placement clones with their source fences
/// and measured-placement transfers. Every failure here is
/// <see cref="PreparedImageRelayoutResult.Unavailable"/>.
/// </summary>
internal sealed class CompletedImageRootCapture
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly MarkdownSourceMap _sourceMap;

    internal CompletedImageRootCapture(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        MarkdownSourceMap sourceMap)
    {
        _geometry = geometry;
        _recovery = recovery;
        _sourceMap = sourceMap;
    }

    /// <summary>
    /// Maps the changed block identities to distinct top-level roots, checks
    /// each root's committed placement and creates the batch's private context.
    /// </summary>
    internal bool TryResolveRootsNoLock(
        CompletedImageRelayoutBatch batch,
        IReadOnlyCollection<int> changedBlockIndices)
    {
        int count = batch.Count;
        var ordinals = new int[count];
        var changedIdentities = new HashSet<int>(count);
        int next = 0;
        foreach (int blockIndex in changedBlockIndices)
        {
            if (next >= count || !changedIdentities.Add(blockIndex) ||
                !_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
                return false;
            ordinals[next++] = ordinal;
        }
        if (next != count || _recovery.IsRequired || _geometry.PaintOnlyGeometryTainted)
            return false;
        LayoutSnapshotCommittedMetadata committedSource = _geometry.ReadPublishedMetadata();
        batch.SourcePlacements = committedSource.Placements;
        batch.MeasuredCount = committedSource.MeasuredCount;
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
        batch.Count = count;
        batch.SourceRoots = new BlockBox[count];
        batch.Changed = new ChangedBlockMeasure[count];
        batch.InlineTransfers = new InlineMeasuredPlacementTransfer?[count];
        batch.TableTransfers = new TableMeasuredPlacementTransfer?[count];
        batch.ContainerTransfers = new BlockMeasuredPlacementTransfer?[count];
        batch.ContentWidth = _geometry.GetContentWidthNoLock();
        batch.ContentX = (float)_geometry.DocumentPadding.Left;
        return TryResolveRootContextsNoLock(batch, ordinals);
    }

    /// <summary>
    /// Admits every resolved root against one shared budget, rejecting
    /// unsupported or duplicate roots before any lease is acquired.
    /// </summary>
    internal bool TryAdmitRootsNoLock(CompletedImageRelayoutBatch batch, out int imageCount)
    {
        var admission = new CompletedImageRootAdmission(
            batch.SourceContext!, batch.Context!, batch.ContentWidth, batch.Count);
        imageCount = 0;
        for (int index = 0; index < batch.Count; index++)
        {
            batch.CancellationToken.ThrowIfCancellationRequested();
            if (!admission.TryAdmit(batch.SourceRoots[index]))
                return false;
        }
        imageCount = admission.ImageCount;
        return true;
    }

    /// <summary>
    /// Creates each root's private clone and captures the source fences and
    /// image transfers that commit will validate and adopt.
    /// </summary>
    internal bool TryCaptureClonesNoLock(CompletedImageRelayoutBatch batch, int imageCount)
    {
        var capturedSources = new List<RasterPlacementSourceFence>(imageCount);
        var capturedTransfers = new List<ImageMeasuredPlacementTransfer>(imageCount);
        for (int index = 0; index < batch.Count; index++)
        {
            batch.CancellationToken.ThrowIfCancellationRequested();
            BlockBox root = batch.SourceRoots[index];
            bool captured = root is ImageBox image
                ? TryCaptureImageClone(batch, index, image, capturedSources, capturedTransfers)
                : root is InlineContainerBox inline
                    ? TryCaptureInlineClone(batch, index, inline, capturedSources, capturedTransfers)
                    : root is TableBox table
                        ? TryCaptureTableClone(batch, index, table, capturedSources, capturedTransfers)
                        : TryCaptureContainerClone(batch, index, root, capturedSources, capturedTransfers);
            if (!captured)
                return false;
        }
        if (capturedSources.Count != imageCount || capturedTransfers.Count != imageCount)
            return false;
        batch.Sources = capturedSources.ToArray();
        batch.Transfers = capturedTransfers.ToArray();
        return true;
    }

    private bool TryResolveRootContextsNoLock(CompletedImageRelayoutBatch batch, int[] ordinals)
    {
        float contentX = batch.ContentX;
        float contentWidth = batch.ContentWidth;
        MarkdownLayoutContext? sourceContext = null;
        // Reject unsupported/duplicate roots before acquiring any lease.
        for (int index = 0; index < batch.Count; index++)
        {
            int ordinal = ordinals[index];
            if (ordinal < 0 || ordinal >= _geometry.Blocks.Count || !_geometry.IsTopLevelMeasuredNoLock(ordinal))
                return false;
            BlockBox root = _geometry.Blocks[ordinal];
            MarkdownLayoutContext? rootContext = GetPreparedImageRootContext(root);
            if (root.IsDirty || rootContext is null ||
                rootContext.PerformanceSession is not { IsDisposed: false } ||
                rootContext.ImagePreparationOwner is null ||
                !ReferenceEquals(_sourceMap, rootContext.SourceMap) ||
                (sourceContext is not null && !ReferenceEquals(sourceContext, rootContext)) ||
                !_geometry.HasCoherentCommittedPlacementNoLock(ordinal, root.Bounds, contentX, contentWidth))
                return false;
            sourceContext ??= rootContext;
            batch.SourceRoots[index] = root;
            batch.Changed[index] = new ChangedBlockMeasure(ordinal, true, root.Bounds, root.Margin,
                ordinal > 0 && StackBox.SharesSafeHtmlInlineFlow(_geometry.Blocks[ordinal - 1], root),
                ordinal < _geometry.Blocks.Count - 1 && StackBox.SharesSafeHtmlInlineFlow(root, _geometry.Blocks[ordinal + 1]));
        }
        batch.Revision = _geometry.LayoutRevision;
        batch.SourceContext = sourceContext;
        batch.Context = sourceContext!.CreateRasterPlacementContext(
            sourceContext.ImageCancellationToken, sourceContext.ImagePreparationOwner!,
            retirementSink: null, imageUnavailable: null);
        return true;
    }

    private static bool TryCaptureImageClone(
        CompletedImageRelayoutBatch batch,
        int index,
        ImageBox image,
        List<RasterPlacementSourceFence> capturedSources,
        List<ImageMeasuredPlacementTransfer> capturedTransfers)
    {
        if (!image.TryCreateCompletedRasterPlacementClone(batch.Context!, out ImageBox? clone,
                out SharedCanvasBitmapCache.Lease? lease, out var svgFence) ||
            clone is null || lease is null)
            return false;
        batch.Prepared[index] = clone;
        capturedSources.Add(new(image, lease, svgFence));
        capturedTransfers.Add(new(image, clone, lease, svgFence));
        return true;
    }

    private static bool TryCaptureInlineClone(
        CompletedImageRelayoutBatch batch,
        int index,
        InlineContainerBox inline,
        List<RasterPlacementSourceFence> capturedSources,
        List<ImageMeasuredPlacementTransfer> capturedTransfers)
    {
        float contentWidth = batch.ContentWidth;
        if (!inline.TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(batch.Context!, contentWidth,
                out InlineContainerBox? clone, out RasterPlacementSourceFence[]? fences) ||
            clone is null)
            return false;
        batch.Prepared[index] = clone;
        if (fences is null || fences.Length != clone.InlineImageRuns.Count ||
            !inline.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(clone, contentWidth,
                out batch.InlineTransfers[index]))
            return false;
        batch.AllInlineTransfers.Add(batch.InlineTransfers[index]!);
        for (int imageIndex = 0; imageIndex < fences.Length; imageIndex++)
        {
            RasterPlacementSourceFence fence = fences[imageIndex];
            if (fence.Lease is null || fence.IsIdleDescriptor ||
                !ReferenceEquals(fence.Image, inline.InlineImageRuns[imageIndex].Run.Image) ||
                !fence.Image.TryGetCompletedInlinePlacementWidth(out float inlineWidth))
                return false;
            capturedSources.Add(fence);
            capturedTransfers.Add(new(fence.Image, clone.InlineImageRuns[imageIndex].Run.Image,
                fence.Lease, fence.SvgMetadata, ExpectedInlineLayout: true,
                ExpectedAvailableWidth: inlineWidth));
        }
        return true;
    }

    private static bool TryCaptureTableClone(
        CompletedImageRelayoutBatch batch,
        int index,
        TableBox table,
        List<RasterPlacementSourceFence> capturedSources,
        List<ImageMeasuredPlacementTransfer> capturedTransfers)
    {
        float contentWidth = batch.ContentWidth;
        bool created = PrivateRasterPlacementTree.TryCreateCloneWithMeasuredVectorPlacement(table, batch.SourceContext!, batch.Context!,
            contentWidth, out BlockBox? clone, out RasterPlacementSourceFence[]? fences);
        batch.Prepared[index] = clone;
        if (!created ||
            clone is not TableBox tableClone)
            return false;
        if (fences is null ||
            !table.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(tableClone, contentWidth,
                out batch.TableTransfers[index]) || batch.TableTransfers[index] is not { } tableTransfer)
            return false;
        int imageIndex = 0;
        foreach (InlineMeasuredPlacementTransfer cellTransfer in tableTransfer.CellTransfers)
        {
            batch.AllInlineTransfers.Add(cellTransfer);
            IReadOnlyList<(InlineImageRun Run, int Offset)> sourceImages = cellTransfer.Source.InlineImageRuns;
            IReadOnlyList<(InlineImageRun Run, int Offset)> cloneImages = cellTransfer.Prepared.InlineImageRuns;
            if (sourceImages.Count != cloneImages.Count)
                return false;
            for (int cellImageIndex = 0; cellImageIndex < sourceImages.Count; cellImageIndex++)
            {
                if (imageIndex >= fences.Length)
                    return false;
                RasterPlacementSourceFence fence = fences[imageIndex++];
                if (fence.Lease is null || fence.IsIdleDescriptor ||
                    !ReferenceEquals(fence.Image, sourceImages[cellImageIndex].Run.Image) ||
                    !fence.Image.TryGetCompletedInlinePlacementWidth(out float inlineWidth))
                    return false;
                capturedSources.Add(fence);
                capturedTransfers.Add(new(fence.Image, cloneImages[cellImageIndex].Run.Image,
                    fence.Lease, fence.SvgMetadata, ExpectedInlineLayout: true,
                    ExpectedAvailableWidth: inlineWidth));
            }
        }
        return imageIndex == fences.Length;
    }

    private static bool TryCaptureContainerClone(
        CompletedImageRelayoutBatch batch,
        int index,
        BlockBox root,
        List<RasterPlacementSourceFence> capturedSources,
        List<ImageMeasuredPlacementTransfer> capturedTransfers)
    {
        float contentWidth = batch.ContentWidth;
        bool created = PrivateRasterPlacementTree.TryCreateCloneWithMeasuredVectorPlacement(root, batch.SourceContext!, batch.Context!,
            contentWidth, out BlockBox? clone, out RasterPlacementSourceFence[]? fences);
        batch.Prepared[index] = clone;
        if (!created || clone is null || fences is null ||
            !BlockMeasuredPlacementTransfer.TryCaptureWithMeasuredVectorPlacement(root, clone, contentWidth,
                out batch.ContainerTransfers[index]) || batch.ContainerTransfers[index] is not { } containerTransfer)
            return false;
        foreach (InlineMeasuredPlacementTransfer leafTransfer in containerTransfer.InlineTransfers)
            batch.AllInlineTransfers.Add(leafTransfer);
        if (containerTransfer.ImagePairs.Length != fences.Length)
            return false;
        for (int imageIndex = 0; imageIndex < fences.Length; imageIndex++)
        {
            BlockMeasuredPlacementTransfer.ImagePair pair = containerTransfer.ImagePairs[imageIndex];
            RasterPlacementSourceFence fence = fences[imageIndex];
            if (fence.Lease is null || fence.IsIdleDescriptor ||
                !ReferenceEquals(fence.Image, pair.Source) ||
                !float.IsFinite(pair.ExpectedSourceWidth) || pair.ExpectedSourceWidth <= 0)
                return false;
            capturedSources.Add(fence);
            capturedTransfers.Add(new(pair.Source, pair.Prepared, fence.Lease, fence.SvgMetadata,
                ExpectedInlineLayout: pair.IsInline, ExpectedAvailableWidth: pair.ExpectedSourceWidth));
        }
        return true;
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
}
