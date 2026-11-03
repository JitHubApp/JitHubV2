using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Walks a source StackBox/ListItemBox tree and its private clone for one
/// <see cref="BlockMeasuredPlacementTransfer"/> capture, collecting node
/// fences, inline transfers and image pairs within the shared node, run,
/// text and style-key budgets.
/// </summary>
internal sealed class BlockMeasuredPlacementCaptureBuilder
{
    private readonly bool _allowMeasuredVectorPlacement;
    private readonly HashSet<BlockBox> _sources = new(BlockMeasuredPlacementTransfer.MaximumNodes, ReferenceEqualityComparer.Instance);
    private readonly HashSet<BlockBox> _prepared = new(BlockMeasuredPlacementTransfer.MaximumNodes, ReferenceEqualityComparer.Instance);
    private readonly HashSet<ImageBox> _images = new(ImageBox.MaximumMeasuredPlacementTransferBatchCount,
        ReferenceEqualityComparer.Instance);
    private readonly HashSet<ImageBox> _preparedImages = new(ImageBox.MaximumMeasuredPlacementTransferBatchCount,
        ReferenceEqualityComparer.Instance);
    private readonly HashSet<double[]> _sourceEdgeOwners = new(BlockMeasuredPlacementTransfer.MaximumNodes, ReferenceEqualityComparer.Instance);
    private int _nodeCount;
    private int _runCount;
    private int _textLength;
    private long _styleKeyCount;

    internal readonly List<BlockMeasuredPlacementNodeFence> Nodes = new(BlockMeasuredPlacementTransfer.MaximumNodes);
    internal readonly List<InlineMeasuredPlacementTransfer> InlineTransfers = new(BlockMeasuredPlacementTransfer.MaximumNodes);
    internal readonly List<BlockMeasuredPlacementTransfer.ImagePair> ImagePairs = new(ImageBox.MaximumMeasuredPlacementTransferBatchCount);
    internal HashSet<double[]> SourceEdgeOwners => _sourceEdgeOwners;
    internal MarkdownLayoutContext? SourceContext;
    internal MarkdownLayoutContext? PreparedContext;

    internal BlockMeasuredPlacementCaptureBuilder(bool allowMeasuredVectorPlacement)
    {
        _allowMeasuredVectorPlacement = allowMeasuredVectorPlacement;
    }

    internal bool Visit(BlockBox source, BlockBox prepared, int depth, bool isRoot)
    {
        if (depth > BlockMeasuredPlacementTransfer.MaximumDepth || ++_nodeCount > BlockMeasuredPlacementTransfer.MaximumNodes ||
            source is null || prepared is null || ReferenceEquals(source, prepared) ||
            !_sources.Add(source) || !_prepared.Add(prepared) ||
            source.IsDirty || source.SafeHtmlInlineFlowScopeId is not null ||
            source.BlockIndex < 0 || prepared.BlockIndex != source.BlockIndex ||
            prepared.SafeHtmlInlineFlowScopeId is not null ||
            !BlockMeasuredPlacementTransfer.IsFiniteBounds(source.Bounds) ||
            !BlockMeasuredPlacementTransfer.IsFiniteThickness(source.RawPlacementMargin) ||
            !BlockMeasuredPlacementTransfer.IsFiniteThickness(source.Margin) ||
            !double.IsFinite(source.CollapsedTopMargin) ||
            !prepared.RawPlacementMargin.Equals(source.RawPlacementMargin) ||
            !prepared.Margin.Equals(source.Margin) ||
            prepared.CollapsedTopMargin != source.CollapsedTopMargin ||
            prepared.SuppressTopMargin != source.SuppressTopMargin ||
            prepared.SuppressBottomMargin != source.SuppressBottomMargin)
        {
            return false;
        }

        return source switch
        {
            StackBox stack when stack.GetType() == typeof(StackBox) && prepared is StackBox preparedStack =>
                VisitStack(stack, preparedStack, depth, isRoot),
            ListItemBox listItem when listItem.GetType() == typeof(ListItemBox) &&
                                      prepared is ListItemBox preparedListItem =>
                VisitListItem(listItem, preparedListItem, depth, isRoot),
            InlineContainerBox inline when inline.GetType() == typeof(InlineContainerBox) &&
                                           prepared is InlineContainerBox preparedInline =>
                VisitInline(inline, preparedInline),
            ImageBox image when image.GetType() == typeof(ImageBox) && prepared is ImageBox preparedImage =>
                VisitImage(image, preparedImage),
            _ => false,
        };
    }

    private bool VisitStack(StackBox source, StackBox prepared, int depth, bool isRoot)
    {
        if (!source.IsPrivateRasterPlacementCloneEligible ||
            !prepared.IsPrivateRasterPlacementCloneEligible ||
            source.HasResolvedSafeHtmlAlignment || prepared.HasResolvedSafeHtmlAlignment ||
            !IsValidStackConfiguration(source) ||
            !SameStackConfiguration(source, prepared) ||
            !IsPristineShellClone(prepared) ||
            source.Children.Count != prepared.Children.Count ||
            source.Children.Count > BlockMeasuredPlacementTransfer.MaximumNodes - _nodeCount ||
            (isRoot && source.Bounds.Width <= 0))
        {
            return false;
        }

        BlockBox[] sourceChildren = CopyChildren(source.Children);
        BlockBox[] preparedChildren = CopyChildren(prepared.Children);
        if (sourceChildren.Length != preparedChildren.Length)
            return false;

        double[] sourceEdges = source.PrivateRasterPlacementChildBottomEdges;
        if (sourceEdges.Length != sourceChildren.Length ||
            (sourceEdges.Length > 0 && !_sourceEdgeOwners.Add(sourceEdges)))
        {
            return false;
        }

        var edgeValues = new double[sourceEdges.Length];
        for (int index = 0; index < sourceEdges.Length; index++)
        {
            double edge = sourceEdges[index];
            if (!double.IsFinite(edge) || edge != sourceChildren[index].Bounds.Bottom)
                return false;
            edgeValues[index] = edge;
        }

        Nodes.Add(BlockMeasuredPlacementNodeFence.ForStack(source, prepared, sourceChildren, preparedChildren,
            sourceEdges, edgeValues, BlockMeasuredPlacementStackSettings.Capture(source), isRoot));

        for (int index = 0; index < sourceChildren.Length; index++)
            if (!Visit(sourceChildren[index], preparedChildren[index], depth + 1, isRoot: false))
                return false;
        return true;
    }

    private bool VisitListItem(ListItemBox source, ListItemBox prepared, int depth, bool isRoot)
    {
        if (!IsFiniteNonnegative(source.MarkerWidth) || source.MarkerWidth != prepared.MarkerWidth ||
            source.FlowDirection != prepared.FlowDirection ||
            !IsValidFlowDirection(source.FlowDirection) ||
            !IsPristineShellClone(prepared) ||
            source.Marker is null || source.Content is null ||
            prepared.Marker is null || prepared.Content is null ||
            2 > BlockMeasuredPlacementTransfer.MaximumNodes - _nodeCount ||
            (isRoot && source.Bounds.Width <= 0))
        {
            return false;
        }

        Nodes.Add(BlockMeasuredPlacementNodeFence.ForListItem(source, prepared,
            source.Marker, prepared.Marker, source.Content, prepared.Content,
            source.MarkerWidth, source.FlowDirection, isRoot));

        return Visit(source.Marker, prepared.Marker, depth + 1, isRoot: false) &&
               Visit(source.Content, prepared.Content, depth + 1, isRoot: false);
    }

    private bool VisitInline(InlineContainerBox source, InlineContainerBox prepared)
    {
        float width = (float)source.Bounds.Width;
        if (!float.IsFinite(width) || width <= 0 || !BlockMeasuredPlacementTransfer.IsSupportedContainerInline(source.ElementKey))
            return false;

        int runCount;
        int imageCount;
        long styleKeys;
        bool countsValid = _allowMeasuredVectorPlacement
            ? source.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                prepared.Context, width, out runCount, out imageCount, out styleKeys)
            : source.TryGetPrivateRasterPlacementCounts(
                prepared.Context, width, out runCount, out imageCount, out styleKeys,
                allowUnmeasuredTextOnly: false,
                allowIdleImagePlacements: false,
                allowCodeForegroundSpans: false);
        if (!countsValid ||
            runCount < 0 || imageCount < 0 ||
            runCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - _runCount ||
            source.PrivateRasterPlacementTextLength >
                InlineContainerBox.MaximumPrivateRasterPlacementTextLength - _textLength ||
            !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(ref _styleKeyCount, styleKeys))
        {
            return false;
        }

        InlineMeasuredPlacementTransfer? inlineTransfer;
        bool captured = _allowMeasuredVectorPlacement
            ? imageCount > 0
                ? source.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
                    prepared, width, out inlineTransfer)
                : source.TryCaptureContainerLeafMeasuredPlacementTransferWithMeasuredVectorPlacement(
                    prepared, width, out inlineTransfer)
            : imageCount > 0
                ? source.TryCaptureMeasuredPlacementTransfer(prepared, width, out inlineTransfer)
                : source.TryCaptureContainerLeafMeasuredPlacementTransfer(prepared, width, out inlineTransfer);
        if (!captured || inlineTransfer is null ||
            !RecordContexts(source.Context, prepared.Context))
        {
            return false;
        }

        IReadOnlyList<(InlineImageRun Run, int Offset)> sourceImages = source.InlineImageRuns;
        IReadOnlyList<(InlineImageRun Run, int Offset)> preparedImages = prepared.InlineImageRuns;
        if (sourceImages.Count != imageCount || preparedImages.Count != imageCount)
            return false;

        for (int index = 0; index < imageCount; index++)
        {
            ImageBox sourceImage = sourceImages[index].Run.Image;
            ImageBox preparedImage = preparedImages[index].Run.Image;
            if (!TryRecordImage(sourceImage, preparedImage, isInline: true))
                return false;
        }

        _runCount += runCount + source.PrivateRasterPlacementForegroundSpanCount;
        if (_runCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns)
            return false;
        _textLength += source.PrivateRasterPlacementTextLength;
        InlineTransfers.Add(inlineTransfer);
        return true;
    }

    private bool VisitImage(ImageBox source, ImageBox prepared)
    {
        if (source.BlockIndex <= 0 || !TryRecordContexts(source.Context, prepared.Context))
            return false;
        float expectedWidth = (float)source.Bounds.Width;
        return float.IsFinite(expectedWidth) && expectedWidth > 0 &&
               TryRecordImage(source, prepared, isInline: false, expectedWidth);
    }

    private bool TryRecordImage(
        ImageBox source,
        ImageBox prepared,
        bool isInline,
        float expectedWidth = 0)
    {
        if (source is null || prepared is null ||
            source.GetType() != typeof(ImageBox) || prepared.GetType() != typeof(ImageBox) ||
            ReferenceEquals(source, prepared) || source.BlockIndex <= 0 ||
            !BlockMeasuredPlacementTransfer.IsFiniteBounds(source.Bounds) ||
            prepared.BlockIndex != source.BlockIndex ||
            !TryRecordContexts(source.Context, prepared.Context) ||
            _images.Contains(prepared) || _preparedImages.Contains(source) ||
            !_images.Add(source) ||
            !_preparedImages.Add(prepared) ||
            ImagePairs.Count >= ImageBox.MaximumMeasuredPlacementTransferBatchCount)
        {
            return false;
        }

        if (isInline)
        {
            if (!source.TryGetCompletedInlinePlacementWidth(out expectedWidth) ||
                !float.IsFinite(expectedWidth) || expectedWidth <= 0)
            {
                return false;
            }
        }

        if (!float.IsFinite(expectedWidth) || expectedWidth <= 0)
            return false;
        ImagePairs.Add(new(source, prepared, isInline, expectedWidth, source.Bounds));
        return true;
    }

    private bool RecordContexts(MarkdownLayoutContext source, MarkdownLayoutContext prepared) =>
        TryRecordContexts(source, prepared);

    private bool TryRecordContexts(MarkdownLayoutContext source, MarkdownLayoutContext prepared)
    {
        if (source is null || prepared is null ||
            !ReferenceEquals(source.SourceMap, prepared.SourceMap))
        {
            return false;
        }

        if (SourceContext is null)
        {
            SourceContext = source;
            PreparedContext = prepared;
            return true;
        }

        return ReferenceEquals(SourceContext, source) && ReferenceEquals(PreparedContext, prepared);
    }

    private static bool IsPristineShellClone(BlockBox prepared) =>
        prepared.IsDirty && BlockMeasuredPlacementTransfer.SameRect(prepared.Bounds, default);

    private static bool IsValidStackConfiguration(StackBox stack) =>
        BlockMeasuredPlacementTransfer.IsFiniteNonnegativeThickness(stack.ContentPadding) &&
        float.IsFinite(stack.AccentBarThickness) && stack.AccentBarThickness >= 0 &&
        float.IsFinite(stack.BorderThickness) && stack.BorderThickness >= 0 &&
        float.IsFinite(stack.CornerRadius) && stack.CornerRadius >= 0 &&
        float.IsFinite(stack.InterChildSpacing) && stack.InterChildSpacing >= 0 &&
        IsValidFlowDirection(stack.FlowDirection);

    private static bool SameStackConfiguration(StackBox source, StackBox prepared) =>
        source.ContentPadding.Equals(prepared.ContentPadding) &&
        Nullable.Equals(source.AccentBar, prepared.AccentBar) &&
        source.AccentBarThickness == prepared.AccentBarThickness &&
        source.AccentBarAffectsContentLayout == prepared.AccentBarAffectsContentLayout &&
        Nullable.Equals(source.Background, prepared.Background) &&
        Nullable.Equals(source.BorderBrush, prepared.BorderBrush) &&
        source.BorderThickness == prepared.BorderThickness &&
        source.CornerRadius == prepared.CornerRadius &&
        source.InterChildSpacing == prepared.InterChildSpacing &&
        source.CollapsePositiveAdjacentMargins == prepared.CollapsePositiveAdjacentMargins &&
        source.FlowDirection == prepared.FlowDirection &&
        source.HasResolvedSafeHtmlAlignment == prepared.HasResolvedSafeHtmlAlignment;

    private static bool IsFiniteNonnegative(float value) => float.IsFinite(value) && value >= 0;

    private static bool IsValidFlowDirection(FlowDirection direction) =>
        direction is FlowDirection.LeftToRight or FlowDirection.RightToLeft;

    private static BlockBox[] CopyChildren(IReadOnlyList<BlockBox> children)
    {
        var copy = new BlockBox[children.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = children[index];
        return copy;
    }
}
