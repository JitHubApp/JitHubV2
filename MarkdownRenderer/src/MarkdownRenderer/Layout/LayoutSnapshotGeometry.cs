using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Owns a snapshot's mutable committed geometry: top-level arrangement, the
/// lazy measurement mask, document size and revision, the pending and
/// published placement generations and the reflow continuation cache.
/// Members suffixed NoLock, and every mutable property, require the caller to
/// hold <see cref="Gate"/>. The snapshot and its collaborators share that one
/// gate, so commit order is unchanged.
/// </summary>
internal sealed class LayoutSnapshotGeometry
{
    private readonly object _gate;
    private readonly IReadOnlyList<BlockBox> _blocks;
    private readonly Dictionary<BlockBox, int> _blockOrdinals;
    private readonly IReadOnlyDictionary<int, int> _topLevelOrdinalByBlockIndex;
    // Mutable block trees are refreshed under the layout gate, but interactive
    // readers consume only the immutable placement/index pair owned by the
    // currently published metadata object.
    private LayoutPlacementGeneration? _pendingPlacementGeneration;
    private float[]? _relayoutHeightScratch;
    private Rect[]? _relayoutBoundsScratch;
    private readonly SharedLayoutMetrics? _sharedLayoutMetrics;
    private readonly Thickness _documentPadding;
    private readonly float _blockSpacing;
    private volatile bool _lazyLayoutEnabled;
    private bool[]? _measuredTopLevelBlocks;
    // Exact float continuation after AdvanceBlock, not a double Bounds.Bottom
    // reconstruction. Only lazy snapshots pay four bytes per root. Revision
    // fencing makes unrelated placement commits invalidate this acceleration;
    // every box still owns its authoritative, physically arranged Bounds.
    private float[]? _lazyReflowContinuations;
    private long? _lazyReflowContinuationRevision;
    private float _lazyReflowContinuationWidth;
    private float _availableWidth;
    // Live Measure/Arrange is not copy-on-write. A failed pass can leave nested
    // child state changed even after outer bounds are restored, so paint-only
    // image publication stays disabled until a complete remeasure repairs it.
    private bool _paintOnlyGeometryTainted;
    private int _measuredTopLevelBlockCount;
    private long _layoutRevision;
    private Size _size;
    private LayoutSnapshotCommittedMetadata _committedMetadata = null!;

    internal LayoutSnapshotGeometry(
        object gate,
        IReadOnlyList<BlockBox> blocks,
        Dictionary<BlockBox, int> blockOrdinals,
        IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex,
        Size size,
        float availableWidth,
        Thickness documentPadding,
        float blockSpacing,
        SharedLayoutMetrics? sharedLayoutMetrics,
        LayoutPassCancellation passCancellation,
        LayoutPlacementTransformContext placementTransform)
    {
        ArgumentNullException.ThrowIfNull(passCancellation);
        ArgumentNullException.ThrowIfNull(placementTransform);
        PassCancellation = passCancellation;
        PlacementTransform = placementTransform;
        _gate = gate;
        _blocks = blocks;
        _blockOrdinals = blockOrdinals;
        _topLevelOrdinalByBlockIndex = topLevelOrdinalByBlockIndex;
        _size = size;
        _availableWidth = availableWidth;
        _documentPadding = documentPadding;
        _blockSpacing = blockSpacing;
        _sharedLayoutMetrics = sharedLayoutMetrics;
    }

    /// <summary>The snapshot layout gate that guards every mutable member.</summary>
    internal object Gate => _gate;

    /// <summary>
    /// The pass token the snapshot's boxes observe. A pass pushes its token
    /// here only while it holds <see cref="Gate"/>.
    /// </summary>
    internal LayoutPassCancellation PassCancellation { get; }

    /// <summary>
    /// The root-placement translation the snapshot's boxes read. Paint, hit
    /// testing and geometry reads enter a root's translation here only while
    /// they hold <see cref="Gate"/>.
    /// </summary>
    internal LayoutPlacementTransformContext PlacementTransform { get; }

    internal IReadOnlyList<BlockBox> Blocks => _blocks;

    internal Dictionary<BlockBox, int> BlockOrdinals => _blockOrdinals;

    internal IReadOnlyDictionary<int, int> TopLevelOrdinalByBlockIndex => _topLevelOrdinalByBlockIndex;

    internal SharedLayoutMetrics? SharedLayoutMetrics => _sharedLayoutMetrics;

    internal Thickness DocumentPadding => _documentPadding;

    internal float BlockSpacing => _blockSpacing;

    // Layout transactions (band realization, relayout, root-image transfer,
    // private placement adoption) update these under the gate in a fixed order;
    // keep them as plain state so that order stays visible at each call site.
    internal float AvailableWidth { get => _availableWidth; set => _availableWidth = value; }

    internal Size Size { get => _size; set => _size = value; }

    internal long LayoutRevision { get => _layoutRevision; set => _layoutRevision = value; }

    internal bool LazyLayoutEnabled { get => _lazyLayoutEnabled; set => _lazyLayoutEnabled = value; }

    internal bool[]? MeasuredTopLevelBlocks { get => _measuredTopLevelBlocks; set => _measuredTopLevelBlocks = value; }

    internal int MeasuredTopLevelBlockCount
    {
        get => _measuredTopLevelBlockCount;
        set => _measuredTopLevelBlockCount = value;
    }

    internal bool PaintOnlyGeometryTainted
    {
        get => _paintOnlyGeometryTainted;
        set => _paintOnlyGeometryTainted = value;
    }

    internal float[]? LazyReflowContinuations
    {
        get => _lazyReflowContinuations;
        set => _lazyReflowContinuations = value;
    }

    internal long? LazyReflowContinuationRevision
    {
        get => _lazyReflowContinuationRevision;
        set => _lazyReflowContinuationRevision = value;
    }

    internal float LazyReflowContinuationWidth
    {
        get => _lazyReflowContinuationWidth;
        set => _lazyReflowContinuationWidth = value;
    }

    /// <summary>The metadata most recently published under the gate.</summary>
    internal LayoutSnapshotCommittedMetadata CommittedMetadata => _committedMetadata;

    /// <summary>Lock-free read of the published metadata for UI scheduling paths.</summary>
    internal LayoutSnapshotCommittedMetadata ReadPublishedMetadata() => Volatile.Read(ref _committedMetadata);

    /// <summary>
    /// Publishes the first metadata of a freshly constructed (still private)
    /// snapshot and clears the pending generation it was prepared from.
    /// </summary>
    internal void PublishInitialMetadataNoLock(LayoutSnapshotCommittedMetadata metadata)
    {
        _committedMetadata = metadata;
        _committedMetadata.MarkPublished();
        _pendingPlacementGeneration = null;
    }

    /// <summary>The placement generation prepared by the last viewport-index refresh.</summary>
    internal LayoutPlacementGeneration? PendingPlacementGeneration => _pendingPlacementGeneration;

    internal bool HasCoherentCommittedPlacementNoLock(
        int ordinal,
        Rect changedBounds,
        float contentX,
        float contentWidth)
    {
        const double geometryTolerance = 0.01;
        if (!IsFiniteBounds(changedBounds) ||
            !float.IsFinite(contentX) ||
            !float.IsFinite(contentWidth) ||
            !float.IsFinite(_blockSpacing) ||
            !double.IsFinite(_size.Height) ||
            !double.IsFinite(_documentPadding.Bottom) ||
            changedBounds.Width < 0 ||
            changedBounds.Height < 0 ||
            contentWidth < 0)
        {
            return false;
        }

        LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
        if ((uint)ordinal >= (uint)placements.Count)
            return false;
        LayoutRootPlacement changedPlacement = placements.GetPlacement(ordinal);
        Rect logicalChangedBounds = placements.ToLogicalRect(ordinal, changedBounds);
        if (Math.Abs(logicalChangedBounds.X - contentX) > geometryTolerance ||
            Math.Abs(logicalChangedBounds.Width - contentWidth) > geometryTolerance)
            return false;

        float expectedChangedTop = ordinal == 0
            ? (float)_documentPadding.Top
            : placements.GetContinuation(ordinal - 1);

        if (!float.IsFinite(expectedChangedTop) ||
            Math.Abs(logicalChangedBounds.Y - expectedChangedTop) > geometryTolerance)
            return false;

        if (ordinal < Blocks.Count - 1)
        {
            LayoutRootPlacement next = placements.GetPlacement(ordinal + 1);
            if (Math.Abs(next.X - contentX) > geometryTolerance ||
                Math.Abs(next.Width - contentWidth) > geometryTolerance)
                return false;

            double expectedNextTop = logicalChangedBounds.Y + logicalChangedBounds.Height;
            if (!placements.SharesFlowWithNext(ordinal))
                expectedNextTop += _blockSpacing;

            if (Math.Abs(next.Top - expectedNextTop) > geometryTolerance)
                return false;
        }

        float expectedDocumentHeight = (float)placements.Size.Height;
        return float.IsFinite(expectedDocumentHeight) &&
            Math.Abs(_size.Height - expectedDocumentHeight) <= geometryTolerance &&
            Math.Abs(changedPlacement.Height - logicalChangedBounds.Height) <= geometryTolerance;
    }

    internal static bool IsFiniteBounds(Rect bounds) =>
        double.IsFinite(bounds.X) &&
        double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height);

    internal void ReflowNoLock(bool incrementRevision = true, int firstOrdinal = 0)
    {
        float contentWidth = GetContentWidthNoLock();
        float contentX = (float)_documentPadding.Left;
        bool canPreservePrefix = firstOrdinal > 0 && firstOrdinal < Blocks.Count &&
            CurrentPlacementGenerationNoLock().IsDocumentOrder &&
            _lazyReflowContinuationRevision == _layoutRevision &&
            _lazyReflowContinuationWidth == _availableWidth &&
            _lazyReflowContinuations is { } continuations && continuations.Length == Blocks.Count &&
            float.IsFinite(continuations[firstOrdinal - 1]);
        if (!canPreservePrefix)
            firstOrdinal = 0;
        if (_lazyLayoutEnabled &&
            (_lazyReflowContinuations is null || _lazyReflowContinuations.Length != Blocks.Count))
            _lazyReflowContinuations = new float[Blocks.Count];
        float y = firstOrdinal == 0
            ? (float)_documentPadding.Top
            : _lazyReflowContinuations![firstOrdinal - 1];
        // A failed Arrange must never leave a reusable continuation prefix for
        // a later transaction. Full/suffix completion re-establishes the fence.
        _lazyReflowContinuationRevision = null;
        for (int n = firstOrdinal; n < Blocks.Count; n++)
        {
            var block = Blocks[n];
            bool measured = _measuredTopLevelBlocks is not null && _measuredTopLevelBlocks[n];
            if (measured)
            {
                block.Arrange(contentX, y, contentWidth);
                AdvanceBlock(ref y, (float)block.Bounds.Height, n);
            }
            else
            {
                float estimate = EstimateHeight(block, n);
                block.ArrangeEstimated(contentX, y, contentWidth, estimate);
                AdvanceBlock(ref y, estimate, n);
            }
            if (_lazyReflowContinuations is { } retainedContinuations)
                retainedContinuations[n] = y;
        }

        y += (float)_documentPadding.Bottom;
        _size = new Size(_availableWidth, y);
        RefreshViewportIndexNoLock(firstOrdinal);
        if (incrementRevision)
        {
            _layoutRevision++;
            PublishCommittedMetadataNoLock();
        }
        _lazyReflowContinuationWidth = _availableWidth;
        _lazyReflowContinuationRevision = _lazyLayoutEnabled ? _layoutRevision : null;
    }

    // Other placement paths keep the same scalar acceleration during their
    // mandatory Arrange loop, without a second scan or allocation at adoption.
    // If their start differs from the exact committed continuation, invalidate
    // rather than inventing a prefix: a later lazy reflow safely reseeds it.
    internal float[]? BeginLazyReflowContinuationUpdateNoLock(int firstOrdinal, float start)
    {
        float[]? retained = _lazyReflowContinuations;
        bool current = _lazyLayoutEnabled && retained is not null &&
            retained.Length == Blocks.Count && float.IsFinite(start) &&
            (firstOrdinal == 0 ||
                (firstOrdinal > 0 && firstOrdinal < Blocks.Count &&
                    CurrentPlacementGenerationNoLock().IsDocumentOrder &&
                    _lazyReflowContinuationRevision == _layoutRevision &&
                    _lazyReflowContinuationWidth == _availableWidth &&
                    retained[firstOrdinal - 1] == start));
        _lazyReflowContinuationRevision = null;
        return current ? retained : null;
    }

    internal void CompleteLazyReflowContinuationUpdateNoLock(float[]? update, long revision)
    {
        if (update is null)
            return;
        _lazyReflowContinuationWidth = _availableWidth;
        _lazyReflowContinuationRevision = revision;
    }

    internal void PreserveLazyReflowContinuationRevisionNoLock(long previousRevision, long nextRevision)
    {
        if (_lazyReflowContinuationRevision == previousRevision)
            _lazyReflowContinuationRevision = nextRevision;
    }

    internal void PublishCommittedMetadataNoLock()
    {
        int measuredCount = _lazyLayoutEnabled ? _measuredTopLevelBlockCount : Blocks.Count;
        LayoutSnapshotCommittedMetadata previous = _committedMetadata;
        if (previous.Revision == _layoutRevision && previous.MeasuredCount == measuredCount &&
            previous.Size.Width == _size.Width && previous.Size.Height == _size.Height &&
            _pendingPlacementGeneration is null)
            return;

        LayoutPlacementGeneration placements = _pendingPlacementGeneration ??
            (measuredCount != previous.MeasuredCount && _measuredTopLevelBlocks is not null
                ? previous.Placements.WithMeasuredRoots(_measuredTopLevelBlocks, _layoutRevision)
                : previous.Placements);
        PublishCommittedMetadataNoLock(new LayoutSnapshotCommittedMetadata(_size, _layoutRevision, measuredCount, placements));
    }

    internal LayoutSnapshotCommittedMetadata CreateCommittedMetadataNoLock(
        Size size,
        long revision,
        LayoutPlacementGeneration? placements = null)
        => new(
            size,
            revision,
            _lazyLayoutEnabled ? _measuredTopLevelBlockCount : Blocks.Count,
            placements);

    /// <summary>
    /// Completes the size on an unpublished metadata candidate. This supports
    /// transactions that must compute their final sequential float geometry in
    /// the same pass that arranges blocks. Published metadata remains immutable.
    /// </summary>
    internal void CompleteCommittedMetadataSizeNoLock(LayoutSnapshotCommittedMetadata metadata, Size size)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.CompleteSizeBeforePublish(size);
    }

    internal void PublishCommittedMetadataNoLock(LayoutSnapshotCommittedMetadata metadata)
    {
        if (!metadata.HasPlacements)
        {
            LayoutPlacementGeneration placements = _pendingPlacementGeneration ??
                (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null
                    ? _committedMetadata.Placements.WithMeasuredRoots(_measuredTopLevelBlocks, metadata.Revision)
                    : CurrentPlacementGenerationNoLock());
            metadata.CompletePlacementsBeforePublish(placements);
        }
        if (metadata.Placements.Revision != metadata.Revision)
            metadata.CompletePlacementRevisionBeforePublish(metadata.Revision);
        metadata.MarkPublished();
        Volatile.Write(ref _committedMetadata, metadata);
        _pendingPlacementGeneration = null;
    }

    internal float GetContentWidthNoLock()
        => (float)Math.Max(
            1,
            _availableWidth - _documentPadding.Left - _documentPadding.Right);

    internal float GetContentWidth(float availableWidth)
        => (float)Math.Max(
            1,
            availableWidth - _documentPadding.Left - _documentPadding.Right);

    internal bool IsTopLevelMeasuredNoLock(int ordinal)
        => !_lazyLayoutEnabled ||
            (_measuredTopLevelBlocks is not null && _measuredTopLevelBlocks[ordinal]);

    internal void GetRelayoutScratchNoLock(out float[] heights, out Rect[] previousBounds)
    {
        int count = Blocks.Count;
        if (_relayoutHeightScratch is null || _relayoutHeightScratch.Length != count)
            _relayoutHeightScratch = new float[count];
        if (_relayoutBoundsScratch is null || _relayoutBoundsScratch.Length != count)
            _relayoutBoundsScratch = new Rect[count];

        heights = _relayoutHeightScratch;
        previousBounds = _relayoutBoundsScratch;
    }

    internal float ComputeDocumentHeightNoLock(float[] heights, CancellationToken cancellationToken)
    {
        float y = (float)_documentPadding.Top;
        for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdvanceBlock(ref y, heights[ordinal], ordinal);
        }

        return y + (float)_documentPadding.Bottom;
    }

    internal void RestoreMeasuredPlacementsNoLock(int lastMeasureAttempted, Rect[] previousBounds)
    {
        for (int ordinal = 0; ordinal <= lastMeasureAttempted; ordinal++)
        {
            if (!IsTopLevelMeasuredNoLock(ordinal))
                continue;

            Rect bounds = previousBounds[ordinal];
            Blocks[ordinal].ArrangeEstimated(
                (float)bounds.X,
                (float)bounds.Y,
                (float)bounds.Width,
                (float)bounds.Height);
        }
    }

    internal void AdvanceBlock(ref float y, float height, int blockOrdinal)
    {
        y += height;
        if (blockOrdinal < Blocks.Count - 1 &&
            !StackBox.SharesSafeHtmlInlineFlow(Blocks[blockOrdinal], Blocks[blockOrdinal + 1]))
            y += _blockSpacing;
    }

    internal float EstimateHeight(BlockBox block, int ordinal)
        => LayoutPlacementInputCapture.EstimateHeight(_sharedLayoutMetrics, block, ordinal);

    internal void RefreshViewportIndexNoLock()
        => RefreshViewportIndexNoLock(0);

    /// <summary>
    /// Rebuilds the viewport index from the given document ordinal onward when
    /// the committed index still maps slots directly to block ordinals. Custom
    /// boxes may violate visual/document ordering, in which case the index uses
    /// its full defensive refresh and sort instead.
    /// </summary>
    internal void RefreshViewportIndexNoLock(int firstOrdinal)
    {
        if ((uint)firstOrdinal > (uint)Blocks.Count)
            firstOrdinal = 0;

        // The constructor's first index has no committed metadata to read yet;
        // later refreshes may reuse the pending or published generation.
        LayoutPlacementGeneration? source = _pendingPlacementGeneration ??
            _committedMetadata?.Placements;
        bool captureExistingPlacements = source is null &&
            _pendingPlacementGeneration is null && _committedMetadata is null;
        bool preserveCustomVisualOrder = source is { IsDocumentOrder: false };
        bool preservePrefix = source is not null && firstOrdinal > 0 &&
            firstOrdinal < Blocks.Count && source.Count == Blocks.Count && source.IsDocumentOrder;
        int start = preservePrefix ? firstOrdinal : 0;
        if (!preservePrefix)
            source = null;

        LayoutPlacementInput[] inputs = LayoutPlacementInputCapture.Capture(
            Blocks, _measuredTopLevelBlocks, _sharedLayoutMetrics, preservePrefix ? source : null, start);

        LayoutPlacementGeneration generation = captureExistingPlacements
            ? LayoutPlacementGeneration.CaptureExisting(inputs, _size, _layoutRevision, _blockSpacing)
            : preserveCustomVisualOrder
                ? LayoutPlacementGeneration.CaptureExisting(inputs, _size, _layoutRevision, _blockSpacing)
            : LayoutPlacementGeneration.Create(
                inputs,
                (float)_documentPadding.Top,
                _blockSpacing,
                (float)_documentPadding.Bottom,
                _availableWidth,
                _layoutRevision,
                source,
                preservePrefix ? start : 0);
        _pendingPlacementGeneration = generation;

    }

    internal ViewportRange FindViewportRangeNoLock(double top, double bottom)
        => CurrentPlacementGenerationNoLock().Find(top, bottom);

    internal LayoutPlacementGeneration CurrentPlacementGenerationNoLock()
    {
        if (_pendingPlacementGeneration is { } pending)
            return pending;
        return _committedMetadata?.Placements ??
            throw new InvalidOperationException("The snapshot has no committed placement generation.");
    }
}
