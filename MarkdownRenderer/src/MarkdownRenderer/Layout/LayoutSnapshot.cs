using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Holds the laid-out block tree for a single markdown source. Computed off the
/// UI thread; published atomically to <see cref="MarkdownRenderer.Controls.MarkdownRendererControl"/>.
/// </summary>
internal sealed class LayoutSnapshot : System.IDisposable, ILayoutSnapshotRetirementHost
{
    private readonly object _layoutLock = new();
    private readonly IReadOnlyDictionary<int, int> _footnoteDefBlocks;
    private readonly IReadOnlyDictionary<int, int> _footnoteRefBlocks;
    private readonly IReadOnlyDictionary<string, int> _fragmentTargetBlocks;
    private readonly Dictionary<ImageBox, int> _imageTopLevelOrdinalByIdentity;
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery = new();
    private readonly SnapshotRetirement _retirement;
    private readonly SnapshotHorizontalOverflowIndex _horizontalOverflow;
    private readonly SnapshotFocusablePlan _focusables;
    private readonly LayoutPlanCandidateIndex _planCandidates;
    private readonly SnapshotBandRealization _bands;
    private readonly SnapshotBlockLocator _locator;
    private readonly ChangedBlockRelayout _relayout;
    private readonly CompletedImageRootRelayout _imageRoots;
    private readonly PrivateRasterCandidateFactory _rasterCandidates;
    private readonly SnapshotPainter _painter;
    private readonly SnapshotHitTester _hitTester;
    private readonly SnapshotPlacementQueries _placements;
    private long _automationDocumentGeneration;

    public LayoutSnapshot(
        IReadOnlyList<BlockBox> blocks,
        MarkdownSourceMap sourceMap,
        float width,
        float height,
        IReadOnlyDictionary<int, int>? footnoteDefBlocks = null,
        IReadOnlyDictionary<int, int>? footnoteRefBlocks = null,
        IReadOnlyDictionary<string, int>? fragmentTargetBlocks = null,
        SharedLayoutMetrics? sharedLayoutMetrics = null,
        Thickness documentPadding = default,
        double blockSpacing = 0,
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy = null)
        : this(
            blocks,
            sourceMap,
            width,
            height,
            footnoteDefBlocks,
            footnoteRefBlocks,
            fragmentTargetBlocks,
            sharedLayoutMetrics,
            documentPadding,
            blockSpacing,
            vectorSceneResidencyPolicy,
            textProjectionReuse: null,
            generatedBlockIndexUpperBound: 0)
    {
    }

    // Keep the original constructor/reflection shape intact. Only builders
    // with a captured generated-ID domain opt into the compact index.
    internal LayoutSnapshot(
        IReadOnlyList<BlockBox> blocks,
        MarkdownSourceMap sourceMap,
        float width,
        float height,
        int generatedBlockIndexUpperBound,
        IReadOnlyDictionary<int, int>? footnoteDefBlocks = null,
        IReadOnlyDictionary<int, int>? footnoteRefBlocks = null,
        IReadOnlyDictionary<string, int>? fragmentTargetBlocks = null,
        SharedLayoutMetrics? sharedLayoutMetrics = null,
        Thickness documentPadding = default,
        double blockSpacing = 0,
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy = null)
        : this(
            blocks,
            sourceMap,
            width,
            height,
            footnoteDefBlocks,
            footnoteRefBlocks,
            fragmentTargetBlocks,
            sharedLayoutMetrics,
            documentPadding,
            blockSpacing,
            vectorSceneResidencyPolicy,
            textProjectionReuse: null,
            generatedBlockIndexUpperBound: generatedBlockIndexUpperBound)
    {
    }

    internal LayoutSnapshot(
        IReadOnlyList<BlockBox> blocks,
        MarkdownSourceMap sourceMap,
        float width,
        float height,
        IReadOnlyDictionary<int, int>? footnoteDefBlocks,
        IReadOnlyDictionary<int, int>? footnoteRefBlocks,
        IReadOnlyDictionary<string, int>? fragmentTargetBlocks,
        SharedLayoutMetrics? sharedLayoutMetrics,
        Thickness documentPadding,
        double blockSpacing,
        IMarkdownVectorSceneResidencyPolicy? vectorSceneResidencyPolicy,
        MarkdownSemanticTextProjectionReuse? textProjectionReuse,
        int generatedBlockIndexUpperBound)
    {
        Blocks = blocks;
        SourceMap = sourceMap;
        MarkdownSemanticTextProjectionReuse? matchingTextProjection =
            textProjectionReuse is { } candidateProjection &&
            ReferenceEquals(sourceMap, candidateProjection.SourceMap)
                ? candidateProjection
                : null;
        SourceFingerprint = matchingTextProjection is { } exactProjection
            ? exactProjection.SourceFingerprint
            : MarkdownAutomationTextFingerprint.Compute(sourceMap.SourceText);
        _footnoteDefBlocks = footnoteDefBlocks ?? new Dictionary<int, int>();
        _footnoteRefBlocks = footnoteRefBlocks ?? new Dictionary<int, int>();
        _fragmentTargetBlocks = fragmentTargetBlocks ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var blockOrdinals = new Dictionary<BlockBox, int>(Blocks.Count, ReferenceEqualityComparer.Instance);
        var topLevelOrdinalByBlockIndex = new BlockOrdinalIndex(generatedBlockIndexUpperBound);
        _geometry = new LayoutSnapshotGeometry(
            _layoutLock,
            blocks,
            blockOrdinals,
            topLevelOrdinalByBlockIndex,
            new Size(width, height),
            Math.Max(1f, width),
            documentPadding,
            (float)(double.IsFinite(blockSpacing) ? Math.Max(0, blockSpacing) : 0),
            sharedLayoutMetrics);
        _retirement = new SnapshotRetirement(_layoutLock, this);
        _imageTopLevelOrdinalByIdentity = new Dictionary<ImageBox, int>(ReferenceEqualityComparer.Instance);
        _horizontalOverflow = new SnapshotHorizontalOverflowIndex(_geometry, _recovery, _retirement);
        var indexBuilder = new LayoutSnapshotIndexBuilder(
            topLevelOrdinalByBlockIndex,
            _imageTopLevelOrdinalByIdentity,
            _horizontalOverflow.Boxes,
            _horizontalOverflow.ByBlockIndex);
        for (int i = 0; i < Blocks.Count; i++)
            blockOrdinals[Blocks[i]] = i;

        // Semantic construction already visits every reachable block, table
        // cell and inline run. Collect snapshot-only layout indexes in that
        // traversal rather than rescanning the same tree first.
        SemanticDocument = MarkdownSemanticDocument.Build(this, matchingTextProjection, indexBuilder);
        _focusables = new SnapshotFocusablePlan(
            _geometry, _recovery, _retirement, indexBuilder.CompleteFocusablePlan());
        _planCandidates = new LayoutPlanCandidateIndex(
            _geometry, _recovery, indexBuilder.CompleteLayoutPlanCandidates());
        topLevelOrdinalByBlockIndex.Freeze();
        _bands = new SnapshotBandRealization(
            _geometry, _recovery, _retirement, _planCandidates, vectorSceneResidencyPolicy);
        _locator = new SnapshotBlockLocator(_geometry, _recovery, _retirement);
        _relayout = new ChangedBlockRelayout(_geometry, _recovery);
        _imageRoots = new CompletedImageRootRelayout(
            _geometry, _recovery, _retirement, _relayout,
            new CompletedImageRootCapture(_geometry, _recovery, sourceMap));
        _rasterCandidates = new PrivateRasterCandidateFactory(
            this, _geometry, _recovery, _retirement, _bands, _horizontalOverflow,
            _footnoteDefBlocks, _footnoteRefBlocks, _fragmentTargetBlocks, generatedBlockIndexUpperBound);
        _painter = new SnapshotPainter(_geometry, _recovery);
        _hitTester = new SnapshotHitTester(_geometry, _recovery);
        _placements = new SnapshotPlacementQueries(_geometry, _recovery);
        _geometry.RefreshViewportIndexNoLock();
        _geometry.PublishInitialMetadataNoLock(new LayoutSnapshotCommittedMetadata(
            _geometry.Size,
            0,
            blocks.Count,
            _geometry.PendingPlacementGeneration ?? throw new InvalidOperationException("Initial placement generation was not prepared.")));
    }

    public readonly IReadOnlyList<BlockBox> Blocks;
    public readonly MarkdownSourceMap SourceMap;
    internal readonly MarkdownAutomationTextFingerprint SourceFingerprint;
    internal readonly MarkdownSemanticDocument SemanticDocument;
    internal const int MaximumPrivateRasterPlacementRoots = 64;
    internal const int MaximumPrivateRasterPlacementTextLength =
        InlineContainerBox.MaximumPrivateRasterPlacementTextLength;
    internal const int MaximumPrivateRasterPlacementSemanticTextLength = 128 * 1024;

    /// <summary>
    /// Logical UIA text-document generation assigned exactly once by the
    /// control before this snapshot is published. Unlike layout revisions,
    /// this value is stable across reflow and restyle snapshots.
    /// </summary>
    internal long AutomationDocumentGeneration => Volatile.Read(ref _automationDocumentGeneration);

    internal void AssignAutomationDocumentGeneration(long generation)
    {
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        if (Interlocked.CompareExchange(ref _automationDocumentGeneration, generation, 0) != 0)
            throw new InvalidOperationException("The automation document generation is already assigned.");
    }
    // UI scheduling checks these before attempting a paint/read lease. They
    // must never wait behind background native layout or observe its candidate
    // geometry. Publish the small immutable tuple only on successful commits.
    public Size Size => _geometry.ReadPublishedMetadata().Size;

    /// <summary>
    /// The immutable placement/index generation currently published with the
    /// snapshot metadata. Readers may retain it without holding the layout
    /// gate; mutable block geometry still requires a snapshot read lease.
    /// </summary>
    internal LayoutPlacementGeneration PublishedPlacementGeneration
        => _geometry.ReadPublishedMetadata().Placements;

    /// <summary>
    /// Monotonically increasing revision for measured bounds and document size.
    /// A background realization may finish after a newer request supersedes it;
    /// callers use this revision to apply the newest cumulative layout state.
    /// </summary>
    internal long LayoutRevision => _geometry.ReadPublishedMetadata().Revision;

    internal bool IsRetiringOrDisposed => _retirement.IsRetiringOrDisposed;

    // Read by UI scheduling paths without taking _layoutLock. The control uses
    // the cause scalars to preserve device-loss backoff without retaining an
    // exception graph or touching native state on the recovery path.
    internal bool GeometryRecoveryRequired => _recovery.IsRequired;
    internal bool GeometryRecoveryIsDeviceLost => _recovery.IsDeviceLost;
    internal int GeometryRecoveryFailureHResult => _recovery.FailureHResult;

    internal bool TryClaimGeometryRecoveryRequest() =>
        _recovery.TryClaimGeometryRecoveryRequest();

    /// <summary>
    /// Nonblocking paint-only proof for a completed fixed-size raster. This is
    /// intentionally stricter than comparing restored bounds: nested native
    /// measurements may have changed before a failed outer layout pass rolled
    /// its visible placements back.
    /// </summary>
    internal bool TryCanUsePaintOnlyImageCompletion(ImageBox image, float availableWidth)
    {
        if (image is null || !float.IsFinite(availableWidth) || !TryBeginInteraction())
            return false;

        try
        {
            LayoutSnapshotCommittedMetadata committed = _geometry.CommittedMetadata;
            if (GeometryRecoveryRequired || _geometry.PaintOnlyGeometryTainted ||
                committed.Revision != _geometry.LayoutRevision ||
                committed.Size.Width != _geometry.Size.Width || committed.Size.Height != _geometry.Size.Height ||
                committed.MeasuredCount != (_geometry.LazyLayoutEnabled ? _geometry.MeasuredTopLevelBlockCount : Blocks.Count) ||
                !float.IsFinite(_geometry.AvailableWidth) || availableWidth != _geometry.AvailableWidth ||
                !_imageTopLevelOrdinalByIdentity.TryGetValue(image, out int ordinal) ||
                ordinal < 0 || !_geometry.IsTopLevelMeasuredNoLock(ordinal) || Blocks[ordinal].IsDirty)
            {
                return false;
            }

            return image.TryCanUsePaintOnlyCompletion();
        }
        finally
        {
            EndInteraction();
        }
    }

    /// <summary>True when this snapshot measures top-level blocks on demand.</summary>
    public bool IsLazyLayoutEnabled => _geometry.LazyLayoutEnabled;

    /// <summary>Number of top-level blocks that have real measured bounds.</summary>
    public int MeasuredTopLevelBlockCount => _geometry.ReadPublishedMetadata().MeasuredCount;

    /// <summary>Number of top-level blocks in the snapshot.</summary>
    public int TopLevelBlockCount => Blocks.Count;

    // Bounds private native preparation independently of total document size.
    // This does not raise the separate whole-tree COW envelope.
    internal const int MaximumPreparedImageRoots = ImageMeasuredPlacementTransfer.MaximumBatchCount;

    /// <summary>Mutable committed geometry, guarded by the snapshot layout gate.</summary>
    internal LayoutSnapshotGeometry Geometry => _geometry;

    /// <summary>Generated block-index bound this snapshot (and its private clones) were built with.</summary>
    internal int GeneratedBlockIndexUpperBound => _rasterCandidates.GeneratedBlockIndexUpperBound;

    /// <summary>Realization-plan candidates and their measured-root subset.</summary>
    internal LayoutPlanCandidateIndex PlanCandidates => _planCandidates;

    /// <summary>Keyboard focus plan collected during construction.</summary>
    internal SnapshotFocusablePlan Focusables => _focusables;

    /// <summary>Horizontally scrollable boxes indexed during construction.</summary>
    internal SnapshotHorizontalOverflowIndex HorizontalOverflow => _horizontalOverflow;

    /// <summary>Top-level ordinal of every image box indexed during construction.</summary>
    internal IReadOnlyDictionary<ImageBox, int> ImageTopLevelOrdinals => _imageTopLevelOrdinalByIdentity;

    internal long PointerInputEpoch => _imageRoots.PointerInputEpoch;

    internal void NotifyPointerInput() => _imageRoots.NotifyPointerInput();

    internal bool TryRelayoutChangedBlocks(
        IReadOnlyCollection<int> changedBlockIndices, float availableWidth,
        CancellationToken cancellationToken, long expectedPointerInputEpoch)
        => _imageRoots.TryRelayoutChangedBlocks(
            changedBlockIndices, availableWidth, cancellationToken, expectedPointerInputEpoch);

    internal PreparedImageRelayoutResult TryRelayoutCompletedImageRoot(
        IReadOnlyCollection<int> changedBlockIndices,
        float availableWidth,
        CancellationToken cancellationToken,
        out MarkdownLayoutContext? constructionContext,
        long? expectedPointerInputEpoch = null,
        CompletedImageRelayoutPhaseHooks? phaseHooks = null)
        => _imageRoots.TryRelayoutCompletedImageRoot(
            changedBlockIndices, availableWidth, cancellationToken, out constructionContext,
            expectedPointerInputEpoch, phaseHooks);

    internal PreparedImageRelayoutResult TryRelayoutCompletedImageRoots(
        IReadOnlyCollection<int> changedBlockIndices,
        float availableWidth,
        CancellationToken cancellationToken,
        out MarkdownLayoutContext? constructionContext,
        long? expectedPointerInputEpoch = null,
        CompletedImageRelayoutPhaseHooks? phaseHooks = null)
        => _imageRoots.TryRelayoutCompletedImageRoots(
            changedBlockIndices, availableWidth, cancellationToken, out constructionContext,
            expectedPointerInputEpoch, phaseHooks);

    internal SingleRasterLayoutCandidate? CreateSingleRasterCandidate(
        IReadOnlyCollection<int> changedBlockIndices,
        float width,
        CancellationToken cancellationToken,
        CancellationToken imageCancellationToken,
        object imagePreparationOwner,
        Action<Task>? retirementSink,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable,
        out MarkdownLayoutContext? constructionContext)
        => _rasterCandidates.CreateSingleRasterCandidate(changedBlockIndices, width, cancellationToken, imageCancellationToken, imagePreparationOwner, retirementSink, imageUnavailable, out constructionContext);

    internal long ActivePreparedVectorSceneEstimatedBytes => _bands.ActivePreparedVectorSceneEstimatedBytes;

    internal int ActivePreparedVectorSceneCount => _bands.ActivePreparedVectorSceneCount;

    internal bool HasCapacityDeferredScenes => _bands.HasCapacityDeferredScenes;

    internal long ActiveCapacityGeneration => _bands.ActiveCapacityGeneration;

    internal IMarkdownVectorSceneResidencyPolicy? VectorSceneResidencyPolicy => _bands.VectorSceneResidencyPolicy;

    internal int InlineSceneGeometryIndexBuildCount => _bands.InlineSceneGeometryIndexBuildCount;

    internal int SceneCommandGraphEstimateCount => _bands.SceneCommandGraphEstimateCount;

    internal int ActiveInlineSceneGeometryIndexCount => _bands.ActiveInlineSceneGeometryIndexCount;

    /// <summary>Returns the block index of the footnote definition for the given order, or null.</summary>
    public int? FootnoteDefBlock(int order)
        => _footnoteDefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns the block index of the inline citation paragraph for the given order, or null.</summary>
    public int? FootnoteRefBlock(int order)
        => _footnoteRefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns the block index registered for a genernc markdown nd fragment, or null.</summary>
    public int? FragmentTargetBlock(string nd)
    {
        nd = NormalizeFragmentId(nd);
        return _fragmentTargetBlocks.TryGetValue(nd, out var v) ? v : null;
    }

    private static string NormalizeFragmentId(string nd)
    {
        nd = nd.Trim();
        if (nd.StartsWith("#", StringComparison.Ordinal))
            nd = nd.Substring(1);
        return nd;
    }

    public void Dispose() =>
        _retirement.Dispose();

    internal Task Retire() =>
        _retirement.Retire();

    void ILayoutSnapshotRetirementHost.DisposeResourcesNoLock(out Task[] pendingRetirementTasks)
    {
        pendingRetirementTasks = Array.Empty<Task>();
        BlockBoxCleanupScope cleanup = default;
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy = _bands.DetachResidencyPolicyNoLock();
        _geometry.LazyReflowContinuations = null;
        _geometry.LazyReflowContinuationRevision = null;
        cleanup.TryDispose(residencyPolicy);

        var retirementTasks = new List<Task>();
        foreach (var block in Blocks)
        {
            cleanup.TryCollectRetirementTasks(block, retirementTasks);
            cleanup.TryDispose(block);
        }

        pendingRetirementTasks = retirementTasks.ToArray();
        cleanup.ThrowIfAny("disposing markdown layout snapshot resources");
    }

    internal void EnableLazyLayout(
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken,
        LazyLayoutBand? initialVisibleVectorBand = null)
        => _bands.EnableLazyLayout(availableWidth, viewportTop, viewportHeight, overscan, cancellationToken, initialVisibleVectorBand);

    internal LazyLayoutCommit EnsureMeasuredViewport(
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
        => _bands.EnsureMeasuredViewport(viewportTop, viewportHeight, overscan, cancellationToken);

    internal bool IsBandMeasured(LazyLayoutBand band) =>
        _bands.IsBandMeasured(band);

    internal bool IsBandWorkSettled(LazyLayoutBand band) =>
        _bands.IsBandWorkSettled(band);

    internal bool IsBandWorkSettled(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand)
        => _bands.IsBandWorkSettled(realizationBand, visibleVectorBand);

    internal bool IsRealizationBandMeasured(LazyLayoutBand realizationBand) =>
        _bands.IsRealizationBandMeasured(realizationBand);

    internal bool IsInputHitTestBandMeasuredUnderInteraction(LazyLayoutBand band) =>
        _bands.IsInputHitTestBandMeasuredUnderInteraction(band);

    internal LazyLayoutCommit EnsureMeasuredBand(LazyLayoutBand band, CancellationToken cancellationToken) =>
        _bands.EnsureMeasuredBand(band, cancellationToken);

    internal LazyLayoutCommit EnsureMeasuredBand(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand,
        CancellationToken cancellationToken)
        => _bands.EnsureMeasuredBand(realizationBand, visibleVectorBand, cancellationToken);

    internal (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(double verticalOffset) =>
        _locator.CaptureScrollAnchor(verticalOffset);

    internal bool TryCaptureScrollAnchor(double verticalOffset, out SnapshotScrollAnchor anchor) =>
        _locator.TryCaptureScrollAnchor(verticalOffset, out anchor);

    internal SnapshotBlockScrollTargetState TryCaptureBlockScrollTarget(
        int blockIndex,
        out SnapshotBlockScrollTarget target)
        => _locator.TryCaptureBlockScrollTarget(blockIndex, out target);

    internal bool TryCaptureScrollAnchorTarget(
        int blockIndex,
        double offsetFromTop,
        out double targetOffset)
        => _locator.TryCaptureScrollAnchorTarget(blockIndex, offsetFromTop, out targetOffset);

    internal IReadOnlyList<BlockBox> GetMeasuredTopLevelBlocks() =>
        _locator.GetMeasuredTopLevelBlocks();

    internal void RelayoutMeasuredBlocks(float availableWidth, CancellationToken cancellationToken) =>
        _relayout.RelayoutMeasuredBlocks(availableWidth, cancellationToken);

    internal bool TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
        double top,
        double bottom,
        out IReadOnlyList<BlockBox> blocks)
        => _bands.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(top, bottom, out blocks);

    internal bool TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
        double top,
        double bottom,
        out IReadOnlyList<BlockBox> blocks,
        out IReadOnlyList<int> topLevelOrdinals,
        out LayoutPlacementGeneration placements,
        out IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
        => _bands.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(top, bottom, out blocks, out topLevelOrdinals, out placements, out topLevelOrdinalByBlockIndex);

    internal bool HasFailedScenesInBandUnderPaint(LazyLayoutBand band) =>
        _bands.HasFailedScenesInBandUnderPaint(band);

    internal void RelayoutChangedBlocks(
        IReadOnlyCollection<int> changedBlockIndices,
        float availableWidth,
        CancellationToken cancellationToken)
        => _relayout.RelayoutChangedBlocks(changedBlockIndices, availableWidth, cancellationToken);

    internal bool IsTopLevelBlockMeasured(BlockBox block) =>
        _locator.IsTopLevelBlockMeasured(block);

    internal FocusTargetLayoutState TryGetFocusTargetLayoutState(
        int blockIndex,
        out LazyLayoutBand estimatedBand)
        => _locator.TryGetFocusTargetLayoutState(blockIndex, out estimatedBand);

    public void Paint(CanvasDrawingSession ds, Rect viewport) =>
        _painter.Paint(ds, viewport);

    public void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport) =>
        _painter.PaintSelectionForeground(ds, range, color, viewport);

    public bool HitTest(Point point, out DocumentPosition position) =>
        _hitTester.HitTest(point, out position);

    internal bool HitTestSelectionEndpoint(Point point, out DocumentPosition position) =>
        _hitTester.HitTestSelectionEndpoint(point, out position);

    internal bool TryHitTestVectorText(Point point, out DocumentPosition position) =>
        _hitTester.TryHitTestVectorText(point, out position);

    internal bool TryGetHorizontalOverflow(
        Point point,
        out IHorizontalOverflowBox? overflow)
        => _horizontalOverflow.TryGetHorizontalOverflow(point, out overflow);

    internal bool TrySetHorizontalOffset(
        IHorizontalOverflowBox overflow,
        double offset)
        => _horizontalOverflow.TrySetHorizontalOffset(overflow, offset);

    internal bool TryGetHorizontalOverflowForBlockIndex(
        int blockIndex,
        out IHorizontalOverflowBox? overflow)
        => _horizontalOverflow.TryGetHorizontalOverflowForBlockIndex(blockIndex, out overflow);

    internal bool TryScrollHorizontal(
        IHorizontalOverflowBox overflow,
        double delta)
        => _horizontalOverflow.TryScrollHorizontal(overflow, delta);

    public IReadOnlyList<FocusableItem> CollectFocusableItems() =>
        _focusables.CollectFocusableItems();

    internal bool TryCollectFocusableItemsUnderReadLease(
        out IReadOnlyList<FocusableItem> items)
        => _focusables.TryCollectFocusableItemsUnderReadLease(out items);

    /// <summary>
    /// Attempts to reserve the committed layout for a UI-thread paint. Lazy
    /// realization can hold the mutation lock while native text layout runs; a
    /// frame should retain its existing tile instead of blocking behind it.
    /// </summary>
    internal bool TryBeginPaint() => TryBeginRead();

    // Pointer handlers use this before touching any mutable block/native layout
    // state. A busy snapshot is a retry, never a text hit-test miss.
    internal bool TryBeginInteraction() => TryBeginRead();

    private bool TryBeginRead()
    {
        // Monitor is reentrant. Arrange callbacks execute while the owning
        // thread holds _layoutLock, so a custom box could otherwise start a
        // paint or interaction read in the middle of a geometry commit. The
        // taint is also retained for healthy, partially realized lazy layouts;
        // reject only same-thread reentry while that state is being mutated.
        if (Monitor.IsEntered(_layoutLock) && _geometry.PaintOnlyGeometryTainted)
            return false;

        if (!Monitor.TryEnter(_layoutLock))
            return false;
        if (!GeometryRecoveryRequired &&
            !_retirement.IsRetirementRequested &&
            !_retirement.IsDisposed)
        {
            return true;
        }

        Monitor.Exit(_layoutLock);
        return false;
    }

    internal void EndInteraction() => Monitor.Exit(_layoutLock);

    internal void EndPaint() => Monitor.Exit(_layoutLock);

    internal ViewportRange GetMeasuredViewportRangeUnderPaint(Rect viewport) =>
        _placements.GetMeasuredViewportRangeUnderPaint(viewport);

    internal bool TryGetMeasuredViewportBlockAtSlotUnderPaint(
        int slot,
        Rect viewport,
        out BlockBox block)
        => _placements.TryGetMeasuredViewportBlockAtSlotUnderPaint(slot, viewport, out block);

    internal int GetBlockOrdinalAtViewportSlotUnderPaint(int slot) =>
        _placements.GetBlockOrdinalAtViewportSlotUnderPaint(slot);

    internal Rect ToLogicalRectForBlockIndexUnderPaint(int blockIndex, Rect physicalRect) =>
        _placements.ToLogicalRectForBlockIndexUnderPaint(blockIndex, physicalRect);

    internal LayoutPlacementTransformContext.Scope EnterPlacementTransformScopeForBlockIndexUnderPaint(
        int blockIndex)
        => _placements.EnterPlacementTransformScopeForBlockIndexUnderPaint(blockIndex);

    internal LayoutPlacementTransformContext.Scope EnterPlacementTransformScopeForTopLevelBlockUnderPaint(
        BlockBox block)
        => _placements.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(block);

    internal Rect ToLogicalRectForBlockIndex(int blockIndex, Rect physicalRect) =>
        _placements.ToLogicalRectForBlockIndex(blockIndex, physicalRect);

    internal Func<int, Rect, Rect> CaptureLogicalRectMapper() =>
        _placements.CaptureLogicalRectMapper();

    internal Point ToLogicalPointForBlockIndexUnderPaint(int blockIndex, Point physicalPoint) =>
        _placements.ToLogicalPointForBlockIndexUnderPaint(blockIndex, physicalPoint);

    internal Point ToPhysicalPointForBlockIndexUnderPaint(int blockIndex, Point logicalPoint) =>
        _placements.ToPhysicalPointForBlockIndexUnderPaint(blockIndex, logicalPoint);

    internal Rect ToPhysicalViewportForTopLevelBlockUnderPaint(BlockBox block, Rect logicalViewport) =>
        _placements.ToPhysicalViewportForTopLevelBlockUnderPaint(block, logicalViewport);

    internal Point ToPhysicalPointForTopLevelBlockUnderPaint(BlockBox block, Point logicalPoint) =>
        _placements.ToPhysicalPointForTopLevelBlockUnderPaint(block, logicalPoint);

    internal bool TryGetLogicalTopForBlockIndex(int blockIndex, out double top) =>
        _placements.TryGetLogicalTopForBlockIndex(blockIndex, out top);

    internal Rect ToLogicalRectForTopLevelBlockUnderPaint(BlockBox block, Rect physicalRect) =>
        _placements.ToLogicalRectForTopLevelBlockUnderPaint(block, physicalRect);

    internal bool TryGetMeasuredBlockByOrdinalUnderPaint(int ordinal, out BlockBox block) =>
        _placements.TryGetMeasuredBlockByOrdinalUnderPaint(ordinal, out block);

    internal bool TryGetMeasuredTopLevelBlockForBlockIndexUnderPaint(
        int blockIndex,
        out BlockBox block)
        => _placements.TryGetMeasuredTopLevelBlockForBlockIndexUnderPaint(blockIndex, out block);

    internal ReadOnlySpan<LayoutPlanCandidate> GetLayoutPlanCandidatesUnderPaint() =>
        _planCandidates.GetLayoutPlanCandidatesUnderPaint();

    internal LayoutPlanCandidateEnumerator GetMeasuredLayoutPlanCandidatesUnderPaint() =>
        _planCandidates.GetMeasuredLayoutPlanCandidatesUnderPaint();

    internal static bool IsInlineEmbedKeyboardFocusable(InlineEmbedRun embed) =>
        SnapshotFocusablePlan.IsInlineEmbedKeyboardFocusable(embed);
}
