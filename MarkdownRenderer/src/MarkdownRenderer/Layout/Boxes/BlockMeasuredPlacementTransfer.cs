using System;
using System.Collections.Generic;
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
    internal const int MaximumNodes = PrivateRasterPlacementTree.MaximumNodes;
    internal const int MaximumDepth = PrivateRasterPlacementTree.MaximumDepth;

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
    private readonly BlockMeasuredPlacementNodeFence[] _nodes;
    private readonly InlineMeasuredPlacementTransfer[] _inlineTransfers;
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
        BlockMeasuredPlacementNodeFence[] nodes,
        InlineMeasuredPlacementTransfer[] inlineTransfers,
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
    internal ReadOnlySpan<InlineMeasuredPlacementTransfer> InlineTransfers => _inlineTransfers;
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

        BlockMeasuredPlacementCaptureBuilder capture = new BlockMeasuredPlacementCaptureBuilder(allowMeasuredVectorPlacement);
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
            BlockMeasuredPlacementNodeFence node = _nodes[index];
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
            BlockMeasuredPlacementNodeFence node = _nodes[index];
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

    internal static bool IsFiniteBounds(Rect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && bounds.Width > 0 &&
        double.IsFinite(bounds.Height) && bounds.Height >= 0;

    internal static bool IsFiniteNonnegativeThickness(Thickness value) =>
        double.IsFinite(value.Left) && value.Left >= 0 &&
        double.IsFinite(value.Top) && value.Top >= 0 &&
        double.IsFinite(value.Right) && value.Right >= 0 &&
        double.IsFinite(value.Bottom) && value.Bottom >= 0;

    internal static bool IsFiniteThickness(Thickness value) =>
        double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
        double.IsFinite(value.Right) && double.IsFinite(value.Bottom);

    internal static bool IsSupportedContainerInline(string elementKey) =>
        elementKey is MarkdownElementKeys.Body or
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or
            MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or
            MarkdownElementKeys.ListMarker;

    internal static bool SameRect(Rect first, Rect second) =>
        first.X == second.X && first.Y == second.Y &&
        first.Width == second.Width && first.Height == second.Height;

    internal static bool IsPristineSourceMetadata(BlockBox source, BlockMeasuredPlacementNodeFence node) =>
        source.BlockIndex == node.BlockIndex &&
        source.SafeHtmlInlineFlowScopeId is null &&
        source.RawPlacementMargin.Equals(node.RawMargin) &&
        source.Margin.Equals(node.EffectiveMargin) &&
        source.CollapsedTopMargin == node.CollapsedTopMargin &&
        source.SuppressTopMargin == node.SuppressTopMargin &&
        source.SuppressBottomMargin == node.SuppressBottomMargin &&
        SameRect(source.Bounds, node.SourceBounds) &&
        !source.IsDirty;

    internal static bool SameStackSettings(StackBox stack, BlockMeasuredPlacementStackSettings settings) => settings.Matches(stack);
}
