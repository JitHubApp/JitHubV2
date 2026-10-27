using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Bounded source-identity-preserving placement transfer for ordinary
/// StackBox/ListItemBox roots. It captures before private measurement, keeps
/// every published box/run/image identity in place, and owns only detached
/// candidate references and placement fences.
/// </summary>
internal sealed class BlockMeasuredPlacementTransfer
{
    private const int MaximumNodes = PrivateRasterPlacementTree.MaximumNodes;
    private const int MaximumDepth = PrivateRasterPlacementTree.MaximumDepth;

    internal readonly struct ImagePair(
        ImageBox Source,
        ImageBox Prepared,
        bool IsInline,
        float ExpectedSourceWidth,
        Rect SourceBounds)
    {
        public ImageBox Source { get; init; } = Source;
        public ImageBox Prepared { get; init; } = Prepared;
        public bool IsInline { get; init; } = IsInline;
        public float ExpectedSourceWidth { get; init; } = ExpectedSourceWidth;
        public Rect SourceBounds { get; init; } = SourceBounds;
    }

    private readonly BlockBox _source;
    private readonly BlockBox _prepared;
    private readonly float _expectedRootWidth;
    private readonly NodeFence[] _nodes;
    private readonly InlineContainerBox.MeasuredPlacementTransfer[] _inlineTransfers;
    private readonly ImagePair[] _imagePairs;
    private readonly HashSet<double[]> _sourceEdgeOwners;
    private readonly HashSet<double[]> _candidateEdgeOwners;
    private float _expectedPreparedRootX;
    private float _expectedPreparedRootY;
    private bool _hasExpectedPreparedRootOrigin;

    private BlockMeasuredPlacementTransfer(
        BlockBox source,
        BlockBox prepared,
        float expectedRootWidth,
        NodeFence[] nodes,
        InlineContainerBox.MeasuredPlacementTransfer[] inlineTransfers,
        ImagePair[] imagePairs,
        HashSet<double[]> sourceEdgeOwners)
    {
        _source = source;
        _prepared = prepared;
        _expectedRootWidth = expectedRootWidth;
        _nodes = nodes;
        _inlineTransfers = inlineTransfers;
        _imagePairs = imagePairs;
        _sourceEdgeOwners = sourceEdgeOwners;
        // Reused by final validation. Capacity is fixed before the source
        // gate; Clear/Add under that gate never needs to resize or allocate.
        _candidateEdgeOwners = new HashSet<double[]>(MaximumNodes, ReferenceEqualityComparer.Instance);
    }

    internal BlockBox Source => _source;
    internal BlockBox Prepared => _prepared;
    internal ReadOnlySpan<InlineContainerBox.MeasuredPlacementTransfer> InlineTransfers => _inlineTransfers;
    internal ReadOnlySpan<ImagePair> ImagePairs => _imagePairs;

    /// <summary>
    /// Records the exact final origin after the private root has been arranged.
    /// Capture still requires a pristine root-local clone at (0, 0); publication
    /// validates that the detached candidate was arranged at its generation's
    /// absolute origin rather than accepting an arbitrary finite placement.
    /// </summary>
    internal bool TrySetExpectedPreparedRootOrigin(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
            return false;

        Rect bounds = _prepared.Bounds;
        if (!IsFiniteBounds(bounds) || bounds.Width != _expectedRootWidth ||
            bounds.X != x || bounds.Y != y)
        {
            return false;
        }

        _expectedPreparedRootX = x;
        _expectedPreparedRootY = y;
        _hasExpectedPreparedRootOrigin = true;
        return true;
    }

    /// <summary>
    /// Captures an ordinary measured StackBox/ListItemBox and its private
    /// unmeasured clone. The caller must hold the source snapshot gate and
    /// must not measure or arrange <paramref name="prepared"/> until this
    /// succeeds. Nested tables and unsupported/hosted content fail closed.
    /// </summary>
    internal static bool TryCapture(
        BlockBox source,
        BlockBox prepared,
        float expectedRootWidth,
        out BlockMeasuredPlacementTransfer? transfer)
        => TryCaptureCore(source, prepared, expectedRootWidth, out transfer,
            allowMeasuredVectorPlacement: false);

    /// <summary>
    /// Captures a private stack/list image-completion candidate whose inline
    /// vector runs are measurement-only borrowers. This transfer changes only
    /// placement on the stable source tree; the candidate is never published.
    /// </summary>
    internal static bool TryCaptureWithMeasuredVectorPlacement(
        BlockBox source,
        BlockBox prepared,
        float expectedRootWidth,
        out BlockMeasuredPlacementTransfer? transfer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepared);
        if (source is not (StackBox or ListItemBox))
        {
            transfer = null;
            return false;
        }

        return TryCaptureCore(source, prepared, expectedRootWidth, out transfer,
            allowMeasuredVectorPlacement: true);
    }

    private static bool TryCaptureCore(
        BlockBox source,
        BlockBox prepared,
        float expectedRootWidth,
        out BlockMeasuredPlacementTransfer? transfer,
        bool allowMeasuredVectorPlacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepared);
        transfer = null;
        if (ReferenceEquals(source, prepared) ||
            !float.IsFinite(expectedRootWidth) || expectedRootWidth <= 0 ||
            source.Bounds.Width != expectedRootWidth ||
            !IsFiniteBounds(source.Bounds) ||
            source.IsDirty ||
            !IsSupportedRoot(source) ||
            !IsPristineRootClone(prepared, expectedRootWidth))
        {
            return false;
        }

        var capture = new CaptureBuilder(allowMeasuredVectorPlacement);
        if (!capture.Visit(source, prepared, depth: 0, isRoot: true) ||
            capture.ImagePairs.Count == 0 ||
            capture.ImagePairs.Count > ImageBox.MaximumMeasuredPlacementTransferBatchCount ||
            capture.SourceContext is null || capture.PreparedContext is null ||
            !ReferenceEquals(capture.SourceContext.SourceMap, capture.PreparedContext.SourceMap))
        {
            return false;
        }

        transfer = new BlockMeasuredPlacementTransfer(
            source,
            prepared,
            expectedRootWidth,
            capture.Nodes.ToArray(),
            capture.InlineTransfers.ToArray(),
            capture.ImagePairs.ToArray(),
            capture.SourceEdgeOwners);
        return true;
    }

    /// <summary>
    /// Revalidates all source shells, child mappings, fixed widths, prepared
    /// geometry, and inline native-placement transfers. Image lease/provider
    /// fences are checked by the caller's ImageBox batch immediately before
    /// commit. This method allocates nothing and calls no native/provider API.
    /// The caller holds the owning snapshot's exclusive layout gate.
    /// </summary>
    internal bool IsCurrentAndPrepared()
    {
        if (!_hasExpectedPreparedRootOrigin ||
            !float.IsFinite(_expectedPreparedRootX) || !float.IsFinite(_expectedPreparedRootY) ||
            _prepared.Bounds.X != _expectedPreparedRootX ||
            _prepared.Bounds.Y != _expectedPreparedRootY ||
            !ReferenceEquals(_source, _nodes[0].Source) ||
            !ReferenceEquals(_prepared, _nodes[0].Prepared) ||
            _source.IsDirty || _source.Bounds.Width != _expectedRootWidth ||
            !IsFiniteBounds(_source.Bounds) ||
            !IsPristineSourceMetadata(_source, _nodes[0]))
        {
            return false;
        }

        _candidateEdgeOwners.Clear();
        for (int index = 0; index < _nodes.Length; index++)
        {
            NodeFence node = _nodes[index];
            if (!node.IsCurrentAndPrepared())
                return false;

            if (node.Source is StackBox sourceStack && node.Prepared is StackBox preparedStack)
            {
                double[] preparedEdges = preparedStack.PrivateRasterPlacementChildBottomEdges;
                if (preparedEdges.Length > 0)
                {
                    if (ReferenceEquals(preparedEdges, sourceStack.PrivateRasterPlacementChildBottomEdges) ||
                        !_candidateEdgeOwners.Add(preparedEdges))
                    {
                        return false;
                    }

                    // Candidate arrays must also remain detached from every
                    // published stack's viewport-search data. The source set
                    // was captured before measurement and is bounded by the
                    // tree node cap, avoiding an O(nodes²) scan in the gate.
                    if (_sourceEdgeOwners.Contains(preparedEdges))
                        return false;
                }
            }
        }

        for (int index = 0; index < _imagePairs.Length; index++)
        {
            ImagePair pair = _imagePairs[index];
            if (!SameRect(pair.Source.Bounds, pair.SourceBounds))
                return false;
        }

        for (int index = 0; index < _inlineTransfers.Length; index++)
            if (!_inlineTransfers[index].IsCurrentAndPrepared())
                return false;

        return true;
    }

    /// <summary>
    /// Commits only after every root/image/native-owner validation has passed
    /// and the ImageBox batch has adopted its candidate placements. Inline
    /// leaves swap first; shell placement swaps bottom-up. All operations are
    /// prevalidated reference/value swaps and allocate/call no native code.
    /// </summary>
    internal void SwapPlacementNoThrow()
    {
        for (int index = 0; index < _inlineTransfers.Length; index++)
            _inlineTransfers[index].SwapPlacementNoThrow();

        for (int index = _nodes.Length - 1; index >= 0; index--)
        {
            NodeFence node = _nodes[index];
            if (node.Source is StackBox sourceStack && node.Prepared is StackBox preparedStack)
                sourceStack.SwapMeasuredPlacementNoThrow(preparedStack);
            else
                ((ListItemBox)node.Source).SwapMeasuredPlacementNoThrow((ListItemBox)node.Prepared);
        }
    }

    private static bool IsSupportedRoot(BlockBox box) =>
        box is StackBox stack && stack.GetType() == typeof(StackBox) ||
        box is ListItemBox listItem && listItem.GetType() == typeof(ListItemBox);

    private static bool IsPristineRootClone(BlockBox box, float width)
    {
        if (!IsSupportedRoot(box) || !box.IsDirty ||
            !SameRect(box.Bounds, default) ||
            !float.IsFinite(width) || width <= 0)
        {
            return false;
        }

        return box switch
        {
            StackBox stack => stack.IsPrivateRasterPlacementCloneEligible &&
                              stack.PrivateRasterPlacementChildBottomEdges.Length == 0,
            ListItemBox => true,
            _ => false,
        };
    }

    private static bool IsFiniteBounds(Rect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && bounds.Width > 0 &&
        double.IsFinite(bounds.Height) && bounds.Height >= 0;

    private static bool IsFiniteNonnegativeThickness(Thickness value) =>
        double.IsFinite(value.Left) && value.Left >= 0 &&
        double.IsFinite(value.Top) && value.Top >= 0 &&
        double.IsFinite(value.Right) && value.Right >= 0 &&
        double.IsFinite(value.Bottom) && value.Bottom >= 0;

    private static bool IsFiniteThickness(Thickness value) =>
        double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
        double.IsFinite(value.Right) && double.IsFinite(value.Bottom);

    private static bool IsSupportedContainerInline(string elementKey) =>
        elementKey is MarkdownElementKeys.Body or
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or
            MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or
            MarkdownElementKeys.ListMarker;

    private static bool SameRect(Rect first, Rect second) =>
        first.X == second.X && first.Y == second.Y &&
        first.Width == second.Width && first.Height == second.Height;

    private static bool IsPristineSourceMetadata(BlockBox source, NodeFence node) =>
        source.BlockIndex == node.BlockIndex &&
        source.SafeHtmlInlineFlowScopeId is null &&
        source.RawPlacementMargin.Equals(node.RawMargin) &&
        source.Margin.Equals(node.EffectiveMargin) &&
        source.CollapsedTopMargin == node.CollapsedTopMargin &&
        source.SuppressTopMargin == node.SuppressTopMargin &&
        source.SuppressBottomMargin == node.SuppressBottomMargin &&
        SameRect(source.Bounds, node.SourceBounds) &&
        !source.IsDirty;

    private sealed class CaptureBuilder
    {
        private readonly bool _allowMeasuredVectorPlacement;
        private readonly HashSet<BlockBox> _sources = new(MaximumNodes, ReferenceEqualityComparer.Instance);
        private readonly HashSet<BlockBox> _prepared = new(MaximumNodes, ReferenceEqualityComparer.Instance);
        private readonly HashSet<ImageBox> _images = new(ImageBox.MaximumMeasuredPlacementTransferBatchCount,
            ReferenceEqualityComparer.Instance);
        private readonly HashSet<ImageBox> _preparedImages = new(ImageBox.MaximumMeasuredPlacementTransferBatchCount,
            ReferenceEqualityComparer.Instance);
        private readonly HashSet<double[]> _sourceEdgeOwners = new(MaximumNodes, ReferenceEqualityComparer.Instance);
        private int _nodeCount;
        private int _runCount;
        private int _textLength;
        private long _styleKeyCount;

        internal readonly List<NodeFence> Nodes = new(MaximumNodes);
        internal readonly List<InlineContainerBox.MeasuredPlacementTransfer> InlineTransfers = new(MaximumNodes);
        internal readonly List<ImagePair> ImagePairs = new(ImageBox.MaximumMeasuredPlacementTransferBatchCount);
        internal HashSet<double[]> SourceEdgeOwners => _sourceEdgeOwners;
        internal MarkdownLayoutContext? SourceContext;
        internal MarkdownLayoutContext? PreparedContext;

        internal CaptureBuilder(bool allowMeasuredVectorPlacement)
        {
            _allowMeasuredVectorPlacement = allowMeasuredVectorPlacement;
        }

        internal bool Visit(BlockBox source, BlockBox prepared, int depth, bool isRoot)
        {
            if (depth > MaximumDepth || ++_nodeCount > MaximumNodes ||
                source is null || prepared is null || ReferenceEquals(source, prepared) ||
                !_sources.Add(source) || !_prepared.Add(prepared) ||
                source.IsDirty || source.SafeHtmlInlineFlowScopeId is not null ||
                source.BlockIndex < 0 || prepared.BlockIndex != source.BlockIndex ||
                prepared.SafeHtmlInlineFlowScopeId is not null ||
                !IsFiniteBounds(source.Bounds) ||
                !IsFiniteThickness(source.RawPlacementMargin) ||
                !IsFiniteThickness(source.Margin) ||
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
                source.Children.Count > MaximumNodes - _nodeCount ||
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

            Nodes.Add(NodeFence.ForStack(source, prepared, sourceChildren, preparedChildren,
                sourceEdges, edgeValues, StackSettings.Capture(source), isRoot));

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
                2 > MaximumNodes - _nodeCount ||
                (isRoot && source.Bounds.Width <= 0))
            {
                return false;
            }

            Nodes.Add(NodeFence.ForListItem(source, prepared,
                source.Marker, prepared.Marker, source.Content, prepared.Content,
                source.MarkerWidth, source.FlowDirection, isRoot));

            return Visit(source.Marker, prepared.Marker, depth + 1, isRoot: false) &&
                   Visit(source.Content, prepared.Content, depth + 1, isRoot: false);
        }

        private bool VisitInline(InlineContainerBox source, InlineContainerBox prepared)
        {
            float width = (float)source.Bounds.Width;
            if (!float.IsFinite(width) || width <= 0 || !IsSupportedContainerInline(source.ElementKey))
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

            InlineContainerBox.MeasuredPlacementTransfer? inlineTransfer;
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
                !IsFiniteBounds(source.Bounds) ||
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
            prepared.IsDirty && SameRect(prepared.Bounds, default);

        private static bool IsValidStackConfiguration(StackBox stack) =>
            IsFiniteNonnegativeThickness(stack.ContentPadding) &&
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

    private sealed class NodeFence
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
        private readonly StackSettings _stackSettings;
        private readonly InlineContainerBox? _sourceMarker;
        private readonly InlineContainerBox? _preparedMarker;
        private readonly StackBox? _sourceContent;
        private readonly StackBox? _preparedContent;
        private readonly float _markerWidth;
        private readonly FlowDirection _flowDirection;

        private NodeFence(
            BlockBox source,
            BlockBox prepared,
            bool isStack,
            bool isRoot,
            BlockBox[]? sourceChildren,
            BlockBox[]? preparedChildren,
            double[]? sourceEdges,
            double[]? sourceEdgeValues,
            StackSettings stackSettings,
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

        internal static NodeFence ForStack(
            StackBox source,
            StackBox prepared,
            BlockBox[] sourceChildren,
            BlockBox[] preparedChildren,
            double[] sourceEdges,
            double[] sourceEdgeValues,
            StackSettings stackSettings,
            bool isRoot) => new(source, prepared, true, isRoot,
                sourceChildren, preparedChildren, sourceEdges, sourceEdgeValues,
                stackSettings, null, null, null, null, 0, default);

        internal static NodeFence ForListItem(
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
            if (!IsPristineSourceMetadata(source, this) ||
                source.IsDirty != _sourceIsDirty || _sourceIsDirty ||
                prepared.IsDirty ||
                prepared.BlockIndex != BlockIndex ||
                prepared.SafeHtmlInlineFlowScopeId is not null ||
                !prepared.RawPlacementMargin.Equals(RawMargin) ||
                !prepared.Margin.Equals(_effectiveMargin) ||
                prepared.CollapsedTopMargin != _collapsedTopMargin ||
                prepared.SuppressTopMargin != _suppressTopMargin ||
                prepared.SuppressBottomMargin != _suppressBottomMargin ||
                !IsFiniteBounds(prepared.Bounds) ||
                prepared.Bounds.Width != _sourceBounds.Width)
            {
                return false;
            }

            if (_isStack)
            {
                if (source is not StackBox sourceStack || prepared is not StackBox preparedStack ||
                    !sourceStack.IsPrivateRasterPlacementCloneEligible ||
                    !preparedStack.IsPrivateRasterPlacementCloneEligible ||
                    !SameStackSettings(sourceStack, _stackSettings) ||
                    !SameStackSettings(preparedStack, _stackSettings) ||
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

    private readonly struct StackSettings(
        Thickness ContentPadding,
        Windows.UI.Color? AccentBar,
        float AccentBarThickness,
        bool AccentBarAffectsContentLayout,
        Windows.UI.Color? Background,
        Windows.UI.Color? BorderBrush,
        float BorderThickness,
        float CornerRadius,
        float InterChildSpacing,
        bool CollapsePositiveAdjacentMargins,
        FlowDirection FlowDirection,
        bool HasResolvedSafeHtmlAlignment)
    {
        public Thickness ContentPadding { get; init; } = ContentPadding;
        public Windows.UI.Color? AccentBar { get; init; } = AccentBar;
        public float AccentBarThickness { get; init; } = AccentBarThickness;
        public bool AccentBarAffectsContentLayout { get; init; } = AccentBarAffectsContentLayout;
        public Windows.UI.Color? Background { get; init; } = Background;
        public Windows.UI.Color? BorderBrush { get; init; } = BorderBrush;
        public float BorderThickness { get; init; } = BorderThickness;
        public float CornerRadius { get; init; } = CornerRadius;
        public float InterChildSpacing { get; init; } = InterChildSpacing;
        public bool CollapsePositiveAdjacentMargins { get; init; } = CollapsePositiveAdjacentMargins;
        public FlowDirection FlowDirection { get; init; } = FlowDirection;
        public bool HasResolvedSafeHtmlAlignment { get; init; } = HasResolvedSafeHtmlAlignment;

        internal static StackSettings Capture(StackBox stack) => new(
            stack.ContentPadding,
            stack.AccentBar,
            stack.AccentBarThickness,
            stack.AccentBarAffectsContentLayout,
            stack.Background,
            stack.BorderBrush,
            stack.BorderThickness,
            stack.CornerRadius,
            stack.InterChildSpacing,
            stack.CollapsePositiveAdjacentMargins,
            stack.FlowDirection,
            stack.HasResolvedSafeHtmlAlignment);

        internal bool Matches(StackBox stack) =>
            stack.ContentPadding.Equals(ContentPadding) &&
            Nullable.Equals(stack.AccentBar, AccentBar) &&
            stack.AccentBarThickness == AccentBarThickness &&
            stack.AccentBarAffectsContentLayout == AccentBarAffectsContentLayout &&
            Nullable.Equals(stack.Background, Background) &&
            Nullable.Equals(stack.BorderBrush, BorderBrush) &&
            stack.BorderThickness == BorderThickness &&
            stack.CornerRadius == CornerRadius &&
            stack.InterChildSpacing == InterChildSpacing &&
            stack.CollapsePositiveAdjacentMargins == CollapsePositiveAdjacentMargins &&
            stack.FlowDirection == FlowDirection &&
            stack.HasResolvedSafeHtmlAlignment == HasResolvedSafeHtmlAlignment;
    }

    private static bool SameStackSettings(StackBox stack, StackSettings settings) => settings.Matches(stack);
}

internal partial class StackBox
{
    internal double[] PrivateRasterPlacementChildBottomEdges => _childBottomEdges;

    /// <summary>
    /// Verifies the exact float placement emitted by Measure followed by the
    /// idempotent Arrange contract. This is intentionally scalar/array-only
    /// and suitable for the final layout gate.
    /// </summary>
    internal bool HasCoherentMeasuredPlacement()
    {
        Rect bounds = Bounds;
        float availableWidth = (float)bounds.Width;
        if (!IsPrivateRasterPlacementCloneEligible ||
            !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width <= 0 ||
            !double.IsFinite(bounds.Height) || bounds.Height < 0 ||
            !float.IsFinite(availableWidth) || availableWidth <= 0 ||
            !IsValidPlacementThickness(ContentPadding) ||
            !IsValidPlacementThickness(Margin) ||
            !float.IsFinite(InterChildSpacing) || InterChildSpacing < 0 ||
            CollapsePositiveAdjacentMargins ||
            FlowDirection is not (FlowDirection.LeftToRight or FlowDirection.RightToLeft))
        {
            return false;
        }

        float accentInset = GetAccentBarContentInset();
        float innerWidth = availableWidth - (float)(ContentPadding.Left + ContentPadding.Right) -
            (float)(Margin.Left + Margin.Right) - accentInset;
        if (!float.IsFinite(accentInset) || accentInset < 0 ||
            !float.IsFinite(innerWidth) || innerWidth <= 0)
        {
            return false;
        }

        float localY = (float)(Margin.Top + ContentPadding.Top);
        float childStartX = FlowDirection == FlowDirection.RightToLeft
            ? (float)(Margin.Left + ContentPadding.Right)
            : (float)(Margin.Left + ContentPadding.Left + accentInset);
        float deltaX = (float)bounds.X;
        float deltaY = (float)bounds.Y;
        if (!float.IsFinite(localY) || !float.IsFinite(childStartX) ||
            _childBottomEdges.Length != _children.Count)
        {
            return false;
        }

        for (int index = 0; index < _children.Count; index++)
        {
            if (index > 0)
                localY += InterChildSpacing;

            BlockBox child = _children[index];
            Rect childBounds = child.Bounds;
            float childHeight = (float)childBounds.Height;
            float expectedX = childStartX + deltaX;
            float expectedY = localY + deltaY;
            if (!float.IsFinite(localY) || !double.IsFinite(childBounds.X) ||
                !double.IsFinite(childBounds.Y) || !double.IsFinite(childBounds.Width) ||
                childBounds.Width != innerWidth || !double.IsFinite(childBounds.Height) ||
                childBounds.Height < 0 || !float.IsFinite(childHeight) ||
                (double)childHeight != childBounds.Height ||
                !float.IsFinite(expectedX) || !float.IsFinite(expectedY) ||
                childBounds.X != expectedX || childBounds.Y != expectedY ||
                !double.IsFinite(childBounds.Bottom) ||
                !double.IsFinite(_childBottomEdges[index]) ||
                _childBottomEdges[index] != childBounds.Bottom)
            {
                return false;
            }

            localY += childHeight;
        }

        localY += (float)(ContentPadding.Bottom + Margin.Bottom);
        return float.IsFinite(localY) && bounds.Height == localY;
    }

    private static bool IsValidPlacementThickness(Thickness value) =>
        double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
        double.IsFinite(value.Right) && double.IsFinite(value.Bottom);

    internal void SwapMeasuredPlacementNoThrow(StackBox prepared)
    {
        (Bounds, prepared.Bounds) = (prepared.Bounds, Bounds);
        (_childBottomEdges, prepared._childBottomEdges) = (prepared._childBottomEdges, _childBottomEdges);
        (IsDirty, prepared.IsDirty) = (prepared.IsDirty, IsDirty);
    }
}

internal sealed partial class ListItemBox
{
    /// <summary>Validates the exact measured list-column geometry without allocation.</summary>
    internal bool HasCoherentMeasuredPlacement()
    {
        Rect bounds = Bounds;
        float width = (float)bounds.Width;
        Thickness margin = Margin;
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width <= 0 ||
            !double.IsFinite(bounds.Height) || bounds.Height < 0 ||
            !float.IsFinite(width) || width <= 0 ||
            !float.IsFinite(_markerWidth) || _markerWidth < 0 ||
            !double.IsFinite(margin.Left) || !double.IsFinite(margin.Top) ||
            !double.IsFinite(margin.Right) || !double.IsFinite(margin.Bottom) ||
            FlowDirection is not (FlowDirection.LeftToRight or FlowDirection.RightToLeft))
        {
            return false;
        }

        float contentWidth = Math.Max(1f, width - _markerWidth - (float)(margin.Left + margin.Right));
        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        float markerX = (float)bounds.X + (rtl ? contentWidth : 0f) + (float)margin.Left;
        float contentX = (float)bounds.X + (rtl ? 0f : _markerWidth) + (float)margin.Left;
        float contentY = (float)bounds.Y + (float)margin.Top;
        Rect markerBounds = Marker.Bounds;
        Rect contentBounds = Content.Bounds;
        float markerHeight = (float)markerBounds.Height;
        float contentHeight = (float)contentBounds.Height;
        if (!float.IsFinite(contentWidth) || contentWidth <= 0 ||
            !float.IsFinite(markerX) || !float.IsFinite(contentX) || !float.IsFinite(contentY) ||
            !double.IsFinite(markerBounds.X) || markerBounds.X != markerX ||
            !double.IsFinite(markerBounds.Y) || markerBounds.Y != contentY ||
            !double.IsFinite(markerBounds.Width) || markerBounds.Width != _markerWidth ||
            !double.IsFinite(markerBounds.Height) || markerBounds.Height < 0 ||
            !float.IsFinite(markerHeight) || (double)markerHeight != markerBounds.Height ||
            !double.IsFinite(contentBounds.X) || contentBounds.X != contentX ||
            !double.IsFinite(contentBounds.Y) || contentBounds.Y != contentY ||
            !double.IsFinite(contentBounds.Width) || contentBounds.Width != contentWidth ||
            !double.IsFinite(contentBounds.Height) || contentBounds.Height < 0 ||
            !float.IsFinite(contentHeight) || (double)contentHeight != contentBounds.Height)
        {
            return false;
        }

        float expectedHeight = Math.Max(markerHeight, contentHeight) +
            (float)(margin.Top + margin.Bottom);
        return float.IsFinite(expectedHeight) && bounds.Height == expectedHeight &&
               Content.HasCoherentMeasuredPlacement();
    }

    internal void SwapMeasuredPlacementNoThrow(ListItemBox prepared)
    {
        (Bounds, prepared.Bounds) = (prepared.Bounds, Bounds);
        (IsDirty, prepared.IsDirty) = (prepared.IsDirty, IsDirty);
    }
}
