using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The captured placement state of one source stack or list-item node and
/// its private clone, revalidated before a block measured-placement
/// transfer swaps them.
/// </summary>
internal sealed class BlockMeasuredPlacementNodeFence
{
    private readonly bool _isStack;
    private readonly bool _isRoot;
    private readonly Rect _sourceBounds;
    private readonly Thickness _effectiveMargin;
    private readonly double _collapsedTopMargin;
    private readonly bool _suppressTopMargin;
    private readonly bool _suppressBottomMargin;
    private readonly bool _sourceIsDirty;
    private readonly BlockBox[]? _sourceChildren;
    private readonly BlockBox[]? _preparedChildren;
    private readonly double[]? _sourceEdges;
    private readonly double[]? _sourceEdgeValues;
    private readonly BlockMeasuredPlacementStackSettings _stackSettings;
    private readonly InlineContainerBox? _sourceMarker;
    private readonly InlineContainerBox? _preparedMarker;
    private readonly StackBox? _sourceContent;
    private readonly StackBox? _preparedContent;
    private readonly float _markerWidth;
    private readonly FlowDirection _flowDirection;

    private BlockMeasuredPlacementNodeFence(
        BlockBox source,
        BlockBox prepared,
        bool isStack,
        bool isRoot,
        BlockBox[]? sourceChildren,
        BlockBox[]? preparedChildren,
        double[]? sourceEdges,
        double[]? sourceEdgeValues,
        BlockMeasuredPlacementStackSettings stackSettings,
        InlineContainerBox? sourceMarker,
        InlineContainerBox? preparedMarker,
        StackBox? sourceContent,
        StackBox? preparedContent,
        float markerWidth,
        FlowDirection flowDirection)
    {
        Source = source;
        Prepared = prepared;
        _isStack = isStack;
        _isRoot = isRoot;
        _sourceBounds = source.Bounds;
        RawMargin = source.RawPlacementMargin;
        _effectiveMargin = source.Margin;
        _collapsedTopMargin = source.CollapsedTopMargin;
        _suppressTopMargin = source.SuppressTopMargin;
        _suppressBottomMargin = source.SuppressBottomMargin;
        _sourceIsDirty = source.IsDirty;
        BlockIndex = source.BlockIndex;
        _sourceChildren = sourceChildren;
        _preparedChildren = preparedChildren;
        _sourceEdges = sourceEdges;
        _sourceEdgeValues = sourceEdgeValues;
        _stackSettings = stackSettings;
        _sourceMarker = sourceMarker;
        _preparedMarker = preparedMarker;
        _sourceContent = sourceContent;
        _preparedContent = preparedContent;
        _markerWidth = markerWidth;
        _flowDirection = flowDirection;
    }

    internal BlockBox Source { get; }
    internal BlockBox Prepared { get; }
    internal int BlockIndex { get; }
    internal Thickness RawMargin { get; }
    internal Thickness EffectiveMargin => _effectiveMargin;
    internal double CollapsedTopMargin => _collapsedTopMargin;
    internal bool SuppressTopMargin => _suppressTopMargin;
    internal bool SuppressBottomMargin => _suppressBottomMargin;
    internal Rect SourceBounds => _sourceBounds;

    internal static BlockMeasuredPlacementNodeFence ForStack(
        StackBox source,
        StackBox prepared,
        BlockBox[] sourceChildren,
        BlockBox[] preparedChildren,
        double[] sourceEdges,
        double[] sourceEdgeValues,
        BlockMeasuredPlacementStackSettings stackSettings,
        bool isRoot) => new(source, prepared, true, isRoot,
            sourceChildren, preparedChildren, sourceEdges, sourceEdgeValues,
            stackSettings, null, null, null, null, 0, default);

    internal static BlockMeasuredPlacementNodeFence ForListItem(
        ListItemBox source,
        ListItemBox prepared,
        InlineContainerBox sourceMarker,
        InlineContainerBox preparedMarker,
        StackBox sourceContent,
        StackBox preparedContent,
        float markerWidth,
        FlowDirection flowDirection,
        bool isRoot) => new(source, prepared, false, isRoot,
            null, null, null, null, default,
            sourceMarker, preparedMarker, sourceContent, preparedContent,
            markerWidth, flowDirection);

    internal bool IsCurrentAndPrepared()
    {
        BlockBox source = Source;
        BlockBox prepared = Prepared;
        if (!BlockMeasuredPlacementTransfer.IsPristineSourceMetadata(source, this) ||
            source.IsDirty != _sourceIsDirty || _sourceIsDirty ||
            prepared.IsDirty ||
            prepared.BlockIndex != BlockIndex ||
            prepared.SafeHtmlInlineFlowScopeId is not null ||
            !prepared.RawPlacementMargin.Equals(RawMargin) ||
            !prepared.Margin.Equals(_effectiveMargin) ||
            prepared.CollapsedTopMargin != _collapsedTopMargin ||
            prepared.SuppressTopMargin != _suppressTopMargin ||
            prepared.SuppressBottomMargin != _suppressBottomMargin ||
            !BlockMeasuredPlacementTransfer.IsFiniteBounds(prepared.Bounds) ||
            prepared.Bounds.Width != _sourceBounds.Width)
        {
            return false;
        }

        if (_isStack)
        {
            if (source is not StackBox sourceStack || prepared is not StackBox preparedStack ||
                !sourceStack.IsPrivateRasterPlacementCloneEligible ||
                !preparedStack.IsPrivateRasterPlacementCloneEligible ||
                !BlockMeasuredPlacementTransfer.SameStackSettings(sourceStack, _stackSettings) ||
                !BlockMeasuredPlacementTransfer.SameStackSettings(preparedStack, _stackSettings) ||
                !MatchesChildren(sourceStack.Children, _sourceChildren) ||
                !MatchesChildren(preparedStack.Children, _preparedChildren))
            {
                return false;
            }

            double[] currentSourceEdges = sourceStack.PrivateRasterPlacementChildBottomEdges;
            double[] currentPreparedEdges = preparedStack.PrivateRasterPlacementChildBottomEdges;
            if (!ReferenceEquals(currentSourceEdges, _sourceEdges) ||
                currentSourceEdges.Length != _sourceEdgeValues!.Length ||
                currentPreparedEdges.Length != _preparedChildren!.Length ||
                ReferenceEquals(currentPreparedEdges, currentSourceEdges) ||
                !preparedStack.HasCoherentMeasuredPlacement())
            {
                return false;
            }

            for (int index = 0; index < currentSourceEdges.Length; index++)
                if (currentSourceEdges[index] != _sourceEdgeValues[index] ||
                    currentSourceEdges[index] != _sourceChildren![index].Bounds.Bottom)
                    return false;

            for (int index = 0; index < currentPreparedEdges.Length; index++)
            {
                double edge = currentPreparedEdges[index];
                if (!double.IsFinite(edge) || edge != _preparedChildren[index].Bounds.Bottom)
                    return false;
            }
            return true;
        }

        if (source is not ListItemBox sourceListItem || prepared is not ListItemBox preparedListItem ||
            !ReferenceEquals(sourceListItem.Marker, _sourceMarker) ||
            !ReferenceEquals(preparedListItem.Marker, _preparedMarker) ||
            !ReferenceEquals(sourceListItem.Content, _sourceContent) ||
            !ReferenceEquals(preparedListItem.Content, _preparedContent) ||
            sourceListItem.MarkerWidth != _markerWidth ||
            preparedListItem.MarkerWidth != _markerWidth ||
            sourceListItem.FlowDirection != _flowDirection ||
            preparedListItem.FlowDirection != _flowDirection)
        {
            return false;
        }
        return preparedListItem.HasCoherentMeasuredPlacement();
    }

    private static bool MatchesChildren(IReadOnlyList<BlockBox> current, BlockBox[]? expected)
    {
        if (expected is null || current.Count != expected.Length)
            return false;
        for (int index = 0; index < expected.Length; index++)
            if (!ReferenceEquals(current[index], expected[index]))
                return false;
        return true;
    }
}
