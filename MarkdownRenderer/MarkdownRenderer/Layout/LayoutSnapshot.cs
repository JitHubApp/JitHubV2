using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Images;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

// Short-lived, value-only handoff for immutable text projections. This must
// never grow to include a LayoutSnapshot, semantic node/span, box, run, or
// control reference: candidate construction occurs after the source read gate.
internal readonly struct MarkdownSemanticTextProjectionReuse
{
    internal readonly MarkdownSourceMap SourceMap;
    internal readonly MarkdownAutomationTextFingerprint SourceFingerprint;
    internal readonly string Text;
    internal readonly TextElementBoundaryIndex TextElementBoundaries;
    internal readonly MarkdownAutomationTextFingerprint TextProjectionFingerprint;

    private MarkdownSemanticTextProjectionReuse(
        MarkdownSourceMap sourceMap,
        MarkdownAutomationTextFingerprint sourceFingerprint,
        string text,
        TextElementBoundaryIndex textElementBoundaries,
        MarkdownAutomationTextFingerprint textProjectionFingerprint)
    {
        SourceMap = sourceMap;
        SourceFingerprint = sourceFingerprint;
        Text = text;
        TextElementBoundaries = textElementBoundaries;
        TextProjectionFingerprint = textProjectionFingerprint;
    }

    internal static MarkdownSemanticTextProjectionReuse Capture(LayoutSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MarkdownSemanticDocument semantic = source.SemanticDocument;
        return new MarkdownSemanticTextProjectionReuse(
            source.SourceMap,
            source.SourceFingerprint,
            semantic.Text,
            semantic.TextElementBoundaries,
            semantic.TextProjectionFingerprint);
    }
}

/// <summary>
/// Holds the land-out block tree for a single markdown source. Computed off the
/// UI thread; published atomically to <see cref="MarkdownRenderer.Controls.MarkdownRendererControl"/>.
/// </summary>
internal sealed partial class LayoutSnapshot : System.IDisposable
{
    private readonly object _layoutLock = new();
    private readonly IReadOnlyDictionary<int, int> _footnoteDefBlocks;
    private readonly IReadOnlyDictionary<int, int> _footnoteRefBlocks;
    private readonly IReadOnlyDictionary<string, int> _fragmentTargetBlocks;
    private readonly Dictionary<BlockBox, int> _blockOrdinals;
    private readonly IReadOnlyDictionary<int, int> _topLevelOrdinalByBlockIndex;
    private readonly Dictionary<ImageBox, int> _imageTopLevelOrdinalByIdentity;
    private readonly HashSet<IHorizontalOverflowBox> _horizontalOverflowBoxes;
    private readonly Dictionary<int, IHorizontalOverflowBox> _horizontalOverflowByBlockIndex;
    private readonly FocusablePlanEntry[] _focusablePlan;
    private readonly LayoutPlanCandidate[] _layoutPlanCandidates;
    private readonly FocusableItem[]? _fixedFocusableItems;
    // Only measured roots that contain at least one realization candidate are
    // retained here. This keeps lazy plan publication proportional to the
    // measured candidate subset instead of rescanning every root or candidate.
    // The list is nullable so eager snapshots pay no additional allocation.
    private List<int>? _measuredLayoutPlanRootOrdinals;
    private bool _measuredLayoutPlanRootOrdinalsAreSorted = true;
    // Mutable block trees are refreshed under _layoutLock, but interactive
    // readers consume only the immutable placement/index pair owned by the
    // currently published metadata object.
    private LayoutPlacementGeneration? _pendingPlacementGeneration;
    private readonly List<int> _sceneBandOrdinalScratch = [];
    private readonly List<BlockBox> _sceneProbeBlockScratch = [];
    private float[]? _relayoutHeightScratch;
    private Rect[]? _relayoutBoundsScratch;
    private readonly SharedLayoutMetrics? _sharedLayoutMetrics;
    private readonly Thickness _documentPadding;
    private readonly float _blockSpacing;
    private readonly int _generatedBlockIndexUpperBound;
    private IMarkdownVectorSceneResidencyPolicy? _vectorSceneResidencyPolicy;
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
    // A failed live layout can leave nested mutable/native geometry that cannot
    // be certified by restoring only top-level bounds. Unlike the transient
    // paint-only taint, this lock-free latch makes the published snapshot
    // permanently unreadable and routes its owner to replacement.
    private int _geometryRecoveryRequired;
    private int _geometryRecoveryRequestClaimed;
    private int _geometryRecoveryIsDeviceLost;
    private int _geometryRecoveryFailureHResult;
    private int _measuredTopLevelBlockCount;
    private long _layoutRevision;
    private Size _size;
    private CommittedMetadata _committedMetadata = null!;
    private TaskCompletionSource? _retirementCompletion;
    private int _retirementRequested;
    private int _disposed;
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

    private LayoutSnapshot(
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
        _size = new Size(width, height);
        _footnoteDefBlocks = footnoteDefBlocks ?? new Dictionary<int, int>();
        _footnoteRefBlocks = footnoteRefBlocks ?? new Dictionary<int, int>();
        _fragmentTargetBlocks = fragmentTargetBlocks ?? new Dictionary<string, int>(StringComparer.Ordinal);
        _sharedLayoutMetrics = sharedLayoutMetrics;
        _documentPadding = documentPadding;
        _vectorSceneResidencyPolicy = vectorSceneResidencyPolicy;
        _generatedBlockIndexUpperBound = generatedBlockIndexUpperBound;
        _blockSpacing = (float)(double.IsFinite(blockSpacing) ? Math.Max(0, blockSpacing) : 0);
        _availableWidth = Math.Max(1f, width);
        _blockOrdinals = new Dictionary<BlockBox, int>(Blocks.Count, ReferenceEqualityComparer.Instance);
        var topLevelOrdinalByBlockIndex = new BlockOrdinalIndex(generatedBlockIndexUpperBound);
        _topLevelOrdinalByBlockIndex = topLevelOrdinalByBlockIndex;
        _imageTopLevelOrdinalByIdentity = new Dictionary<ImageBox, int>(ReferenceEqualityComparer.Instance);
        _horizontalOverflowBoxes = new HashSet<IHorizontalOverflowBox>(ReferenceEqualityComparer.Instance);
        _horizontalOverflowByBlockIndex = new Dictionary<int, IHorizontalOverflowBox>();
        var indexBuilder = new LayoutSnapshotIndexBuilder(
            topLevelOrdinalByBlockIndex,
            _imageTopLevelOrdinalByIdentity,
            _horizontalOverflowBoxes,
            _horizontalOverflowByBlockIndex);
        for (int i = 0; i < Blocks.Count; i++)
            _blockOrdinals[Blocks[i]] = i;

        // Semantic construction already visits every reachable block, table
        // cell and inline run. Collect snapshot-only layout indexes in that
        // traversal rather than rescanning the same tree first.
        SemanticDocument = MarkdownSemanticDocument.Build(this, matchingTextProjection, indexBuilder);
        _focusablePlan = indexBuilder.CompleteFocusablePlan();
        _layoutPlanCandidates = indexBuilder.CompleteLayoutPlanCandidates();
        topLevelOrdinalByBlockIndex.Freeze();
        bool hasDynamicFocusable = false;
        for (int index = 0; index < _focusablePlan.Length; index++)
        {
            if (_focusablePlan[index].HorizontalOverflow is not null)
            {
                hasDynamicFocusable = true;
                break;
            }
        }
        if (hasDynamicFocusable)
        {
            // Dynamic overflow entries are filtered against the committed
            // viewport at query time, so a fixed copy would be immediately
            // discarded. Avoid that full-plan allocation for mixed documents.
            _fixedFocusableItems = null;
        }
        else
        {
            var fixedFocusableItems = new FocusableItem[_focusablePlan.Length];
            for (int index = 0; index < _focusablePlan.Length; index++)
                fixedFocusableItems[index] = _focusablePlan[index].Item;
            _fixedFocusableItems = fixedFocusableItems;
        }
        RefreshViewportIndexNoLock();
        _committedMetadata = new CommittedMetadata(
            _size,
            0,
            blocks.Count,
            _pendingPlacementGeneration ?? throw new InvalidOperationException("Initial placement generation was not prepared."));
        _committedMetadata.MarkPublished();
        _pendingPlacementGeneration = null;
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
    public Size Size => Volatile.Read(ref _committedMetadata).Size;

    /// <summary>
    /// The immutable placement/index generation currently published with the
    /// snapshot metadata. Readers may retain it without holding the layout
    /// gate; mutable block geometry still requires a snapshot read lease.
    /// </summary>
    internal LayoutPlacementGeneration PublishedPlacementGeneration
        => Volatile.Read(ref _committedMetadata).Placements;

    /// <summary>
    /// Monotonically increasing revision for measured bounds and document size.
    /// A background realization may finish after a newer request supersedes it;
    /// callers use this revision to apply the newest cumulative layout state.
    /// </summary>
    internal long LayoutRevision => Volatile.Read(ref _committedMetadata).Revision;

    internal bool IsRetiringOrDisposed => Volatile.Read(ref _retirementRequested) != 0 ||
        Volatile.Read(ref _disposed) != 0;

    // Read by UI scheduling paths without taking _layoutLock. The control uses
    // the cause scalars to preserve device-loss backoff without retaining an
    // exception graph or touching native state on the recovery path.
    internal bool GeometryRecoveryRequired => Volatile.Read(ref _geometryRecoveryRequired) != 0;
    internal bool GeometryRecoveryIsDeviceLost => Volatile.Read(ref _geometryRecoveryIsDeviceLost) != 0;
    internal int GeometryRecoveryFailureHResult => Volatile.Read(ref _geometryRecoveryFailureHResult);

    internal bool TryClaimGeometryRecoveryRequest()
    {
        if (!GeometryRecoveryRequired ||
            Interlocked.CompareExchange(ref _geometryRecoveryRequestClaimed, 1, 0) != 0)
        {
            return false;
        }

        // Recheck after the CAS to keep the request claim coupled to the latch.
        if (GeometryRecoveryRequired)
            return true;

        Interlocked.CompareExchange(ref _geometryRecoveryRequestClaimed, 0, 1);
        return false;
    }

    private void MarkGeometryRecoveryRequiredNoLock(Exception failure, Exception? secondaryFailure = null)
    {
        if (_geometryRecoveryRequired != 0)
        {
            if (secondaryFailure is not null && GraphicsDeviceErrors.IsDeviceLost(secondaryFailure))
                Volatile.Write(ref _geometryRecoveryIsDeviceLost, 1);
            return;
        }

        Volatile.Write(ref _geometryRecoveryFailureHResult, failure.HResult);
        bool deviceLost = GraphicsDeviceErrors.IsDeviceLost(failure) ||
            GraphicsDeviceErrors.IsDeviceLost(secondaryFailure);
        Volatile.Write(ref _geometryRecoveryIsDeviceLost, deviceLost ? 1 : 0);
        Volatile.Write(ref _geometryRecoveryRequired, 1);
    }

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
            CommittedMetadata committed = _committedMetadata;
            if (GeometryRecoveryRequired || _paintOnlyGeometryTainted ||
                committed.Revision != _layoutRevision ||
                committed.Size.Width != _size.Width || committed.Size.Height != _size.Height ||
                committed.MeasuredCount != (_lazyLayoutEnabled ? _measuredTopLevelBlockCount : Blocks.Count) ||
                !float.IsFinite(_availableWidth) || availableWidth != _availableWidth ||
                !_imageTopLevelOrdinalByIdentity.TryGetValue(image, out int ordinal) ||
                ordinal < 0 || !IsTopLevelMeasuredNoLock(ordinal) || Blocks[ordinal].IsDirty)
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
    public bool IsLazyLayoutEnabled => _lazyLayoutEnabled;

    /// <summary>Number of top-level blocks that have real measured bounds.</summary>
    public int MeasuredTopLevelBlockCount => Volatile.Read(ref _committedMetadata).MeasuredCount;

    /// <summary>Number of top-level blocks in the snapshot.</summary>
    public int TopLevelBlockCount => Blocks.Count;

    /// <summary>
    /// Bounded COW tree: eager and lazy paragraphs/headings, ordinary
    /// list/quote stacks, tables and standalone completed image roots containing
    /// text, links and completed rasters/full SVGs. Every mutable wrapper, cell and root is cloned, including
    /// unchanged siblings. Shape preflight, metadata copying and bitmap lease
    /// cloning hold the source lock; aggregate layout text is capped at 64 Ki
    /// and semantic text (including image alt text) at 128 Ki UTF-16 units.
    /// Lazy candidates preserve the captured measured-root mask and estimated
    /// heights: native measure/arrangement runs only for roots already measured
    /// by the source. Indexing and semantic construction use the private tree.
    /// Unsupported shapes leave the existing snapshot completely untouched.
    /// </summary>
    internal SingleRasterLayoutCandidate? CreateSingleRasterCandidate(
        IReadOnlyCollection<int> changedBlockIndices,
        float width,
        CancellationToken cancellationToken,
        CancellationToken imageCancellationToken,
        object imagePreparationOwner,
        Action<Task>? retirementSink,
        Action<string, MarkdownImageUnavailableReason, MarkdownSvgFailureReason?>? imageUnavailable,
        out MarkdownLayoutContext? constructionContext)
    {
        constructionContext = null;
        MarkdownLayoutContext? context = null;
        List<BlockBox>? roots = null;
        List<RasterPlacementSourceFence>? sources = null;
        LayoutSnapshot? snapshot = null;
        MarkdownSemanticTextProjectionReuse? textProjectionReuse = null;
        HorizontalPlacementSourceFence[]? horizontalSources = null;
        List<CodeBlockPlacementSourceFence>? codeSources = null;
        Exception? constructionFailure = null;
        bool lazyLayoutEnabled = false;
        bool[]? measuredRootMask = null;
        float[]? capturedRootHeights = null;
        int capturedMeasuredRootCount = 0;
        SharedLayoutMetrics? capturedSharedLayoutMetrics = null;
        long revision;
        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        try
        {
            lock (_layoutLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRetiringOrDisposed || GeometryRecoveryRequired ||
                    _vectorSceneResidencyPolicy is not null ||
                    Blocks.Count is 0 or > MaximumPrivateRasterPlacementRoots ||
                    !PrivateRasterPlacementTree.TryGetFirstInlineContext(
                        Blocks, out MarkdownLayoutContext? sourceContext) || sourceContext is null ||
                    !ReferenceEquals(SourceMap, sourceContext.SourceMap) ||
                    SemanticDocument.Text.Length > MaximumPrivateRasterPlacementSemanticTextLength ||
                    changedBlockIndices.Count is 0 or > MaximumPrivateRasterPlacementRoots ||
                    !float.IsFinite(width) || width != _availableWidth ||
                    _layoutRevision == long.MaxValue)
                    return null;

                var changedOrdinals = new HashSet<int>();
                foreach (int blockIndex in changedBlockIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
                        return null;
                    changedOrdinals.Add(ordinal);
                }

                lazyLayoutEnabled = _lazyLayoutEnabled;
                if (lazyLayoutEnabled)
                {
                    if (_measuredTopLevelBlocks is not { } sourceMeasuredMask ||
                        sourceMeasuredMask.Length != Blocks.Count)
                        return null;

                    measuredRootMask = (bool[])sourceMeasuredMask.Clone();
                    for (int ordinal = 0; ordinal < measuredRootMask.Length; ordinal++)
                    {
                        if (measuredRootMask[ordinal])
                            capturedMeasuredRootCount++;
                    }
                    if (capturedMeasuredRootCount != _measuredTopLevelBlockCount)
                        return null;
                }
                else
                {
                    if (_measuredTopLevelBlocks is not null || _measuredTopLevelBlockCount != 0)
                        return null;
                    capturedMeasuredRootCount = Blocks.Count;
                }

                // A lazy root's current extent is already the authoritative
                // estimate used by its viewport index. Copy only scalar values
                // before releasing the source gate; never estimate or measure a
                // source root in the private build phase.
                if (lazyLayoutEnabled)
                {
                    capturedRootHeights = new float[Blocks.Count];
                    for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double sourceHeight = Blocks[ordinal].Bounds.Height;
                        if (!double.IsFinite(sourceHeight) || sourceHeight < 0 || sourceHeight > float.MaxValue)
                            return null;
                        float capturedHeight = (float)sourceHeight;
                        if (!float.IsFinite(capturedHeight))
                            return null;
                        capturedRootHeights[ordinal] = capturedHeight;
                    }
                    capturedSharedLayoutMetrics = _sharedLayoutMetrics;
                }

                revision = _layoutRevision;
                context = sourceContext.CreateRasterPlacementContext(
                    imageCancellationToken, imagePreparationOwner,
                    retirementSink, imageUnavailable);
                constructionContext = context;

                // Preflight the complete bounded shape before acquiring the
                // first clone lease. Never share an unchanged mutable sibling
                // under independently owned snapshot locks/retirement lifetimes.
                float capturedContentWidth = GetContentWidthNoLock();
                var capture = new PrivateRasterPlacementTree.CaptureState(
                    allowUnmeasuredTextOnly: lazyLayoutEnabled, allowIdleImagePlacements: true,
                    allowCodePlacements: true);
                bool changedRaster = false;
                for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!capture.TryAddRoot(Blocks[ordinal], sourceContext, context,
                            capturedContentWidth, out PrivateRasterPlacementTree.RootCounts counts))
                        return null;
                    // Only never-started, source-free descriptors may cross
                    // from estimated roots. Completed or active image state in
                    // an unmeasured root still uses the ordinary relayout path.
                    if (lazyLayoutEnabled && !measuredRootMask![ordinal] &&
                        counts.ImageCount != counts.IdleImageCount)
                        return null;
                    changedRaster |= changedOrdinals.Contains(ordinal) && counts.ImageCount > 0;
                }
                int imageCount = capture.ImageCount;
                if (!changedRaster || imageCount == 0)
                    return null;

                horizontalSources = new HorizontalPlacementSourceFence[_horizontalOverflowBoxes.Count];
                int horizontalIndex = 0;
                foreach (IHorizontalOverflowBox overflow in _horizontalOverflowBoxes)
                    horizontalSources[horizontalIndex++] = new(overflow, overflow.HorizontalOffset);
                textProjectionReuse = MarkdownSemanticTextProjectionReuse.Capture(this);
                roots = new List<BlockBox>(Blocks.Count);
                sources = new List<RasterPlacementSourceFence>(imageCount);
                codeSources = new List<CodeBlockPlacementSourceFence>();
                bool changedCompletedImage = false;
                for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
                {
                    BlockBox block = Blocks[ordinal];
                    int codeStart = codeSources.Count;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!PrivateRasterPlacementTree.TryCreateClone(
                            block, sourceContext, context, capturedContentWidth, out BlockBox? root,
                            out RasterPlacementSourceFence[]? rootSources,
                            allowUnmeasuredTextOnly: lazyLayoutEnabled,
                            allowIdleImagePlacements: true, codeSources: codeSources) || root is null)
                        return null;
                    // The preallocated root list becomes the sole cleanup
                    // owner before any later per-root fence processing.
                    roots.Add(root);
                    if (rootSources is null)
                        throw new InvalidOperationException("A private root omitted its raster source fences.");
                    bool wasSubscribed = !lazyLayoutEnabled || measuredRootMask![ordinal];
                    for (int codeIndex = codeStart; codeIndex < codeSources.Count; codeIndex++)
                        codeSources[codeIndex] = codeSources[codeIndex] with { WasMeasured = wasSubscribed };
                    foreach (RasterPlacementSourceFence source in rootSources)
                    {
                        changedCompletedImage |= changedOrdinals.Contains(ordinal) && !source.IsIdleDescriptor;
                        sources.Add(source with { WasSubscribed = wasSubscribed });
                    }
                }
                if (sources.Count != imageCount)
                    throw new InvalidOperationException("The private document omitted a raster source fence.");
                if (!changedCompletedImage)
                    return null;
            }

            // No source lock spans DirectWrite or semantic/index construction.
            cancellationToken.ThrowIfCancellationRequested();
            if (!SingleRasterLayoutCandidate.AreSourceProvidersCurrent(sources!, context!))
                return null;
            foreach (CodeBlockPlacementSourceFence code in codeSources!)
                if (!code.Source.IsCurrentPrivatePlacement(code.Revision))
                    return null;
            float contentWidth = (float)Math.Max(1, width - _documentPadding.Left - _documentPadding.Right);
            float height = (float)_documentPadding.Top;
            float[]? candidateContinuations = lazyLayoutEnabled ? new float[roots!.Count] : null;
            for (int ordinal = 0; ordinal < roots!.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockBox root = roots[ordinal];
                float rootHeight;
                if (!lazyLayoutEnabled || measuredRootMask![ordinal])
                {
                    rootHeight = root.Measure(contentWidth);
                    root.Arrange((float)_documentPadding.Left, height, contentWidth);
                }
                else
                {
                    rootHeight = capturedRootHeights![ordinal];
                    root.ArrangeEstimated(
                        (float)_documentPadding.Left,
                        height,
                        contentWidth,
                        rootHeight);
                }
                height += rootHeight;
                if (ordinal < roots.Count - 1 &&
                    !StackBox.SharesSafeHtmlInlineFlow(root, roots[ordinal + 1]))
                    height += _blockSpacing;
                if (candidateContinuations is not null)
                    candidateContinuations[ordinal] = height;
            }
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = new LayoutSnapshot(roots.ToArray(), SourceMap, width,
                height + (float)_documentPadding.Bottom,
                _footnoteDefBlocks, _footnoteRefBlocks, _fragmentTargetBlocks,
                documentPadding: _documentPadding, blockSpacing: _blockSpacing,
                sharedLayoutMetrics: capturedSharedLayoutMetrics,
                vectorSceneResidencyPolicy: null,
                textProjectionReuse: textProjectionReuse,
                generatedBlockIndexUpperBound: _generatedBlockIndexUpperBound);
            if (lazyLayoutEnabled)
            {
                snapshot._lazyLayoutEnabled = true;
                snapshot._measuredTopLevelBlocks = measuredRootMask;
                snapshot._measuredTopLevelBlockCount = capturedMeasuredRootCount;
                snapshot.RebuildMeasuredLayoutPlanRootOrdinalsNoLock();
                snapshot.RefreshViewportIndexNoLock();
            }
            snapshot._layoutRevision = revision + 1;
            snapshot._lazyReflowContinuations = candidateContinuations;
            snapshot._lazyReflowContinuationWidth = width;
            snapshot._lazyReflowContinuationRevision = lazyLayoutEnabled ? revision + 1 : null;
            LayoutPlacementGeneration candidatePlacements = snapshot.CurrentPlacementGenerationNoLock();
            snapshot._committedMetadata = new CommittedMetadata(
                snapshot._size,
                revision + 1,
                lazyLayoutEnabled ? capturedMeasuredRootCount : roots.Count,
                candidatePlacements.WithRevision(revision + 1));
            snapshot._committedMetadata.MarkPublished();
            snapshot._pendingPlacementGeneration = null;
            if (AutomationDocumentGeneration > 0)
                snapshot.AssignAutomationDocumentGeneration(AutomationDocumentGeneration);
            if (!snapshot.SourceFingerprint.Equals(SourceFingerprint) ||
                !snapshot.SemanticDocument.TextProjectionFingerprint.Equals(SemanticDocument.TextProjectionFingerprint) ||
                !snapshot.SemanticDocument.SemanticRangeFingerprint.Equals(SemanticDocument.SemanticRangeFingerprint))
                throw new InvalidOperationException("A raster placement candidate changed logical document semantics.");
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new SingleRasterLayoutCandidate(snapshot, context!, sources!.ToArray(),
                revision, width, horizontalSources, codeSources!.ToArray());
            snapshot = null;
            roots = null;
            return candidate;
        }
        catch (Exception exception)
        {
            constructionFailure = exception;
            throw;
        }
        finally
        {
            // The caller joins this context's ledger after the scheduled native
            // operation exits, including failures before snapshot construction.
            if (snapshot is not null)
                context!.RegisterRetirement(snapshot.Retire());
            else if (roots is not null)
            {
                Exception? cleanupFailure = null;
                foreach (BlockBox root in roots)
                {
                    try { context!.DiscardConstructionBlock(root); }
                    catch (Exception exception) { cleanupFailure ??= exception; }
                }
                if (cleanupFailure is not null)
                {
                    if (constructionFailure is null)
                        throw cleanupFailure;
                    try { constructionFailure.Data["MarkdownRenderer.PrivateDocumentCleanupFailure"] = cleanupFailure; }
                    catch { /* Cleanup diagnostics must not replace the construction failure. */ }
                }
            }
        }
    }

    /// <summary>
    /// Approximate weight of the block-scene device resources pinned for the
    /// current lazy-layout band. This is telemetry and relative cost, not an
    /// exact native-memory measurement or a hard budget.
    /// </summary>
    internal long ActivePreparedVectorSceneEstimatedBytes
    {
        get { lock (_layoutLock) return _vectorSceneResidencyPolicy?.ActiveEstimatedBytes ?? 0; }
    }

    internal int ActivePreparedVectorSceneCount
    {
        get { lock (_layoutLock) return _vectorSceneResidencyPolicy?.ActiveSceneCount ?? 0; }
    }

    /// <summary>
    /// Scalar wake state only. These volatile policy properties are safe for
    /// UI/ledger coordination without inspecting mutable scene or box state.
    /// </summary>
    internal bool HasCapacityDeferredScenes =>
        !GeometryRecoveryRequired &&
        Volatile.Read(ref _vectorSceneResidencyPolicy)?.HasCapacityDeferredScenes == true;

    internal long ActiveCapacityGeneration =>
        Volatile.Read(ref _vectorSceneResidencyPolicy)?.ActiveCapacityGeneration ?? 0;

    // Callers must hold a paint/read lease before using this borrowed policy.
    // Snapshot retirement clears and disposes it under the same layout lock.
    internal IMarkdownVectorSceneResidencyPolicy? VectorSceneResidencyPolicy
        => _vectorSceneResidencyPolicy;

    internal int InlineSceneGeometryIndexBuildCount
    {
        get { lock (_layoutLock) return _vectorSceneResidencyPolicy?.InlineGeometryIndexBuildCount ?? 0; }
    }

    internal int SceneCommandGraphEstimateCount
    {
        get { lock (_layoutLock) return _vectorSceneResidencyPolicy?.CommandGraphEstimateCount ?? 0; }
    }

    internal int ActiveInlineSceneGeometryIndexCount
    {
        get { lock (_layoutLock) return _vectorSceneResidencyPolicy?.ActiveInlineSceneGeometryIndexCount ?? 0; }
    }

    /// <summary>Returns the block index of the footnote definition for the gnven order, or null.</summary>
    public int? FootnoteDefBlock(int order)
        => _footnoteDefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns the block index of the inline citation paragraph for the gnven order, or null.</summary>
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

    public void Dispose()
    {
        Interlocked.Exchange(ref _retirementRequested, 1);
        TaskCompletionSource completion = GetRetirementCompletion();
        bool ownsDisposal = false;
        Exception? failure = null;
        Task[] pendingRetirementTasks = Array.Empty<Task>();
        try
        {
            lock (_layoutLock)
            {
                DisposeCoreNoLock(ref ownsDisposal, out pendingRetirementTasks);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (ownsDisposal)
            {
                CompleteRetirementAfterDrain(
                    pendingRetirementTasks,
                    failure,
                    completion);
            }
        }
    }

    /// <summary>
    /// Retires a snapshot without waiting for an in-flight background measure
    /// to release the layout lock. Snapshot replacement and control teardown
    /// call this from the UI thread; final native-resource disposal always runs
    /// on the pool and waits for any active measure there.
    /// </summary>
    internal Task Retire()
    {
        TaskCompletionSource completion = GetRetirementCompletion();
        if (Interlocked.Exchange(ref _retirementRequested, 1) != 0)
        {
            return completion.Task;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static snapshot => snapshot.DisposeRetiredSnapshot(),
            this,
            preferLocal: false);
        return completion.Task;
    }

    private void DisposeRetiredSnapshot()
    {
        TaskCompletionSource completion = GetRetirementCompletion();
        bool ownsDisposal = false;
        Exception? failure = null;
        Task[] pendingRetirementTasks = Array.Empty<Task>();
        try
        {
            lock (_layoutLock)
            {
                DisposeCoreNoLock(ref ownsDisposal, out pendingRetirementTasks);
            }
        }
        catch (Exception exception)
        {
            // Retirement runs on the pool, where rethrowing would terminate the
            // process. Preserve the failure on the completion task so internal
            // drainers can surface it deterministically.
            failure = exception;
        }
        finally
        {
            if (ownsDisposal)
            {
                CompleteRetirementAfterDrain(
                    pendingRetirementTasks,
                    failure,
                    completion);
            }
        }
    }

    private TaskCompletionSource GetRetirementCompletion()
    {
        TaskCompletionSource? completion = Volatile.Read(ref _retirementCompletion);
        if (completion is not null)
            return completion;

        var candidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Interlocked.CompareExchange(ref _retirementCompletion, candidate, null) ?? candidate;
    }

    private static void CompleteRetirementAfterDrain(
        Task[] pendingRetirementTasks,
        Exception? failure,
        TaskCompletionSource completion)
    {
        if (pendingRetirementTasks.Length == 0)
        {
            CompleteRetirement(completion, failure);
            return;
        }

        _ = CompleteRetirementAfterDrainAsync(
            pendingRetirementTasks,
            failure,
            completion);
    }

    private static async Task CompleteRetirementAfterDrainAsync(
        Task[] pendingRetirementTasks,
        Exception? failure,
        TaskCompletionSource completion)
    {
        try
        {
            await Task.WhenAll(pendingRetirementTasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(
                    "Snapshot disposal and native-work retirement both failed.",
                    failure,
                    exception);
        }

        CompleteRetirement(completion, failure);
    }

    private static void CompleteRetirement(
        TaskCompletionSource completion,
        Exception? failure)
    {
        if (failure is null)
            completion.TrySetResult();
        else
            completion.TrySetException(failure);
    }

    private void DisposeCoreNoLock(
        ref bool ownsDisposal,
        out Task[] pendingRetirementTasks)
    {
        pendingRetirementTasks = Array.Empty<Task>();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ownsDisposal = true;
        BlockBoxCleanupScope cleanup = default;
        IMarkdownVectorSceneResidencyPolicy? residencyPolicy = _vectorSceneResidencyPolicy;
        _vectorSceneResidencyPolicy = null;
        _sceneProbeBlockScratch.Clear();
        _sceneBandOrdinalScratch.Clear();
        _lazyReflowContinuations = null;
        _lazyReflowContinuationRevision = null;
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

    /// <summary>
    /// Enables viewport-relative top-level measurement and realizes the first
    /// viewport band. The block tree and source map already exist, but expensive
    /// text/native layout objects are created only as bands are measured.
    /// </summary>
    internal void EnableLazyLayout(
        float availableWidth,
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken,
        LazyLayoutBand? initialVisibleVectorBand = null)
    {
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return;

            if (_lazyLayoutEnabled)
                return;

            _lazyLayoutEnabled = true;
            _availableWidth = Math.Max(1f, availableWidth);
            _measuredTopLevelBlocks = new bool[Blocks.Count];
            _measuredLayoutPlanRootOrdinals = null;
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
            _measuredTopLevelBlockCount = 0;
            ReflowNoLock();
        }

        LazyLayoutBand realizationBand = LazyLayoutBand.FromViewport(
            viewportTop, viewportHeight, overscan);
        LazyLayoutBand visibleVectorBand = initialVisibleVectorBand ?? realizationBand;
        // Rect projection can extend a fractional visible edge even when the
        // caller requests zero overscan. Every admitted scene still requires
        // measured geometry, including that projected edge.
        realizationBand = new LazyLayoutBand(
            Math.Min(realizationBand.Top, visibleVectorBand.Top),
            Math.Max(realizationBand.Bottom, visibleVectorBand.Bottom));
        EnsureMeasuredBand(realizationBand, visibleVectorBand, cancellationToken);
    }

    internal LazyLayoutCommit EnsureMeasuredViewport(
        double viewportTop,
        double viewportHeight,
        double overscan,
        CancellationToken cancellationToken)
        => EnsureMeasuredBand(LazyLayoutBand.FromViewport(viewportTop, viewportHeight, overscan), cancellationToken);

    /// <summary>
    /// Returns true when every block intersecting the requested estimated or
    /// measured band already owns its native layout objects. The UI-thread
    /// viewport path uses this before dispatching a background realization so
    /// a genuinely warm scroll performs no task/CTS/delegate allocation.
    /// </summary>
    internal bool IsBandMeasured(LazyLayoutBand band)
        => IsBandMeasured(band, acceptTerminalFailure: false);

    // Scheduling completion is distinct from successful rendering. A typed
    // terminal fallback needs no more producer work for this committed band,
    // but must never make the rendering/readiness gate pass.
    internal bool IsBandWorkSettled(LazyLayoutBand band)
        => IsBandMeasured(band, acceptTerminalFailure: true);

    /// <summary>
    /// Checks realization coverage for the broader layout band while evaluating
    /// vector-scene readiness only for the actual visible band. Look-ahead
    /// geometry can therefore be measured without admitting its native scenes.
   /// </summary>
    internal bool IsBandWorkSettled(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand)
        => IsBandMeasured(realizationBand, visibleVectorBand, acceptTerminalFailure: true);

    /// <summary>
    /// Returns whether geometry for a realization band is already measured,
    /// without requiring its vector scenes to own native resources. This lets
    /// the control suppress repeated capacity retries while still scheduling
    /// genuinely missing layout work.
    /// </summary>
    internal bool IsRealizationBandMeasured(LazyLayoutBand realizationBand)
    {
        if (!Monitor.TryEnter(_layoutLock))
            return false;

        try
        {
            return !GeometryRecoveryRequired && IsRealizationBandMeasuredNoLock(realizationBand);
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    /// <summary>
    /// Checks whether every root a pointer hit-test could inspect in this band
    /// already has authoritative measured geometry. The caller must hold the
    /// snapshot interaction/read lease; this method performs no layout work.
    /// </summary>
    internal bool IsInputHitTestBandMeasuredUnderInteraction(LazyLayoutBand band)
    {
        if (!Monitor.IsEntered(_layoutLock) ||
            IsRetiringOrDisposed ||
            !double.IsFinite(band.Top) ||
            !double.IsFinite(band.Bottom) ||
            band.Top < 0 ||
            band.Bottom < band.Top ||
            GeometryRecoveryRequired)
        {
            return false;
        }

        if (band.Top == 0 && band.Bottom == 0)
            return true;

        return IsRealizationBandMeasuredNoLock(band);
    }

    private bool IsBandMeasured(LazyLayoutBand band, bool acceptTerminalFailure)
        => IsBandMeasured(band, band, acceptTerminalFailure);

    private bool IsBandMeasured(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand,
        bool acceptTerminalFailure)
    {
        if (!Monitor.TryEnter(_layoutLock))
            return false;

        try
        {
            if (GeometryRecoveryRequired)
                return false;

            if (!IsRealizationBandMeasuredNoLock(realizationBand))
                return false;

            _sceneBandOrdinalScratch.Clear();
            try
            {
                LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
                var range = FindViewportRangeNoLock(visibleVectorBand.Top, visibleVectorBand.Bottom);
                for (int i = range.Start; i < range.End; i++)
                {
                    int ordinal = placements.GetBlockOrdinal(i);
                    LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                    if (!visibleVectorBand.Intersects(placement.Top, placement.Bottom))
                        continue;
                    if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null &&
                        !_measuredTopLevelBlocks[ordinal])
                        return false;
                    _sceneBandOrdinalScratch.Add(ordinal);
                }

                return _vectorSceneResidencyPolicy is not { } policy || (acceptTerminalFailure
                    ? policy.IsBandWorkSettled(
                        _sceneBandOrdinalScratch,
                        visibleVectorBand,
                        placements,
                        _topLevelOrdinalByBlockIndex)
                    : policy.AreNativeResourcesPrepared(
                        _sceneBandOrdinalScratch,
                        visibleVectorBand,
                        placements,
                        _topLevelOrdinalByBlockIndex));
            }
            finally
            {
                _sceneBandOrdinalScratch.Clear();
            }
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    private bool IsRealizationBandMeasuredNoLock(LazyLayoutBand band)
    {
        if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
            return true;

        var range = FindViewportRangeNoLock(band.Top, band.Bottom);
        for (int i = range.Start; i < range.End; i++)
        {
            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            int ordinal = placements.GetBlockOrdinal(i);
            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            if (band.Intersects(placement.Top, placement.Bottom) &&
                !_measuredTopLevelBlocks[ordinal])
            {
                return false;
            }
        }

        return true;
    }

    internal LazyLayoutCommit EnsureMeasuredBand(LazyLayoutBand band, CancellationToken cancellationToken)
        => EnsureMeasuredBand(band, band, cancellationToken);

    /// <summary>
    /// Measures/reflows only the realization band, then refreshes vector
    /// residency against the visible band. Eager snapshots still refresh
    /// residency but never repeat their full document measure pass.
    /// </summary>
    internal LazyLayoutCommit EnsureMeasuredBand(
        LazyLayoutBand realizationBand,
        LazyLayoutBand visibleVectorBand,
        CancellationToken cancellationToken)
    {
        CancellationToken effectiveCancellationToken = cancellationToken.CanBeCanceled
            ? cancellationToken
            : LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
        using var cancellationScope = LayoutPassCancellation.Push(effectiveCancellationToken);
        lock (_layoutLock)
        {
            effectiveCancellationToken.ThrowIfCancellationRequested();
            // A realization can already be queued when its snapshot is
            // replaced. Retirement and measurement share this lock, so check
            // the terminal state only after acquiring it: if retirement won
            // the race, no disposed native layout may be touched.
            if (Volatile.Read(ref _retirementRequested) != 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                return LazyLayoutCommit.Unchanged(_size.Height, _layoutRevision);
            }

            if (GeometryRecoveryRequired)
                return LazyLayoutCommit.Unchanged(_size.Height, _layoutRevision);

            if ((!_lazyLayoutEnabled || _measuredTopLevelBlocks is null) &&
                _vectorSceneResidencyPolicy is null)
                return LazyLayoutCommit.Unchanged(_size.Height, _layoutRevision);

            double oldHeight = _size.Height;
            long oldRevision = _layoutRevision;
            int oldMeasuredTopLevelBlockCount = _measuredTopLevelBlockCount;
            bool oldMeasuredLayoutPlanRootOrdinalsAreSorted =
                _measuredLayoutPlanRootOrdinalsAreSorted;
            int firstMeasureAttempted = Blocks.Count;
            List<int>? measuredOrdinals = null;
            List<int>? requestedSceneBandOrdinals = null;
            bool nativeSceneResourcesChanged = false;
            bool priorPaintOnlyGeometryTainted = _paintOnlyGeometryTainted;

            try
            {
                if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null)
                {
                    float contentWidth = GetContentWidthNoLock();
                    while (true)
                    {
                        effectiveCancellationToken.ThrowIfCancellationRequested();
                        var range = FindViewportRangeNoLock(realizationBand.Top, realizationBand.Bottom);
                        List<int>? newlyMeasured = null;
                        int firstAffectedOrdinal = Blocks.Count;
                bool geometryUnchanged = CurrentPlacementGenerationNoLock().IsDocumentOrder &&
                            !priorPaintOnlyGeometryTainted;
                        for (int i = range.Start; i < range.End; i++)
                        {
                            effectiveCancellationToken.ThrowIfCancellationRequested();
                            int ordinal = CurrentPlacementGenerationNoLock().GetBlockOrdinal(i);
                            BlockBox block = Blocks[ordinal];
                            LayoutRootPlacement committedPlacement = CurrentPlacementGenerationNoLock().GetPlacement(ordinal);
                            if (_measuredTopLevelBlocks[ordinal] ||
                                !realizationBand.Intersects(committedPlacement.Top, committedPlacement.Bottom))
                            {
                                continue;
                            }

                            // Measure resets most block bounds to local (0,0).
                            // Preserve the candidate's old top until reflow assigns
                            // its authoritative position after new heights settle.
                            Rect priorBounds = block.Bounds;
                            Thickness priorMargin = block.Margin;
                            bool priorFlowWithPrevious = ordinal > 0 &&
                                StackBox.SharesSafeHtmlInlineFlow(Blocks[ordinal - 1], block);
                            bool priorFlowWithNext = ordinal < Blocks.Count - 1 &&
                                StackBox.SharesSafeHtmlInlineFlow(block, Blocks[ordinal + 1]);
                            // A factory/Arrange can mutate then throw before this
                            // batch gets copied into measuredOrdinals. Rollback must
                            // include that attempted root, not just completed ones.
                            firstMeasureAttempted = Math.Min(firstMeasureAttempted, ordinal);
                            _paintOnlyGeometryTainted = true;
                            block.Measure(contentWidth);
                            block.Arrange(
                                (float)_documentPadding.Left,
                                (float)priorBounds.Top,
                                contentWidth);
                            bool flowWithPrevious = ordinal > 0 &&
                                StackBox.SharesSafeHtmlInlineFlow(Blocks[ordinal - 1], block);
                            bool flowWithNext = ordinal < Blocks.Count - 1 &&
                                StackBox.SharesSafeHtmlInlineFlow(block, Blocks[ordinal + 1]);
                            geometryUnchanged &= IsFiniteBounds(priorBounds) &&
                                double.IsFinite(priorBounds.Right) && double.IsFinite(priorBounds.Bottom) &&
                                priorBounds.Width >= 0 && priorBounds.Height >= 0 &&
                                block.Bounds.Equals(priorBounds) &&
                                block.Margin.Equals(priorMargin) &&
                                flowWithPrevious == priorFlowWithPrevious &&
                                flowWithNext == priorFlowWithNext;
                            firstAffectedOrdinal = Math.Min(firstAffectedOrdinal,
                                flowWithPrevious != priorFlowWithPrevious ? Math.Max(0, ordinal - 1) : ordinal);
                            (newlyMeasured ??= []).Add(ordinal);
                        }

                        if (newlyMeasured is null)
                            break;

                        foreach (int ordinal in newlyMeasured)
                        {
                            // Keep rollback ownership before publishing the bit;
                            // growing this list can fail without changing the mask.
                            (measuredOrdinals ??= []).Add(ordinal);
                            _measuredTopLevelBlocks[ordinal] = true;
                            _measuredTopLevelBlockCount++;
                            AddMeasuredLayoutPlanRootOrdinalNoLock(ordinal);
                        }

                        // Recompute all candidate ordinals after changed heights
                        // shift downstream blocks. The next pass measures every
                        // previously unmeasured block newly entering the band.
                        if (!geometryUnchanged)
                            ReflowNoLock(incrementRevision: false, firstOrdinal: firstAffectedOrdinal);
                    }
                }

                if (_vectorSceneResidencyPolicy is { } residencyPolicy)
                {
                    LayoutPlacementGeneration scenePlacements = CurrentPlacementGenerationNoLock();
                    var range = FindViewportRangeNoLock(visibleVectorBand.Top, visibleVectorBand.Bottom);
                    requestedSceneBandOrdinals = new List<int>(range.End - range.Start);
                    for (int i = range.Start; i < range.End; i++)
                    {
                        int ordinal = scenePlacements.GetBlockOrdinal(i);
                        LayoutRootPlacement placement = scenePlacements.GetPlacement(ordinal);
                        if (!visibleVectorBand.Intersects(placement.Top, placement.Bottom))
                            continue;
                        if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null &&
                            !_measuredTopLevelBlocks[ordinal])
                        {
                            // The realization band must include the actual
                            // viewport; never attach a native scene to estimated
                            // geometry and pretend that scene is visible-ready.
                            throw new InvalidOperationException(
                                "The requested visible vector band contains an unrealized top-level block.");
                        }
                        requestedSceneBandOrdinals.Add(ordinal);
                    }

                    nativeSceneResourcesChanged = residencyPolicy.ReplaceActiveBand(
                        requestedSceneBandOrdinals,
                        visibleVectorBand,
                        scenePlacements,
                        _topLevelOrdinalByBlockIndex,
                        effectiveCancellationToken);
                }

                // The measured-root index is consumed by the UI-thread
                // realization-plan publisher. Finalize any out-of-order band
                // arrivals while this mutation still owns the layout gate,
                // before the new geometry/metadata can be observed.
                SortMeasuredLayoutPlanRootOrdinalsNoLock();

                // ReflowNoLock already published the candidate geometry and
                // viewport index under this lock. Commit shared estimates and
                // advance the revision only after all visible native resources
                // have been prepared successfully.
                if (measuredOrdinals is { Count: > 0 })
                {
                    foreach (int ordinal in measuredOrdinals)
                    {
                        _sharedLayoutMetrics?.RecordHeight(
                            ordinal,
                            (float)Blocks[ordinal].Bounds.Height);
                    }
                    _layoutRevision = oldRevision + 1;
                    PreserveLazyReflowContinuationRevisionNoLock(oldRevision, _layoutRevision);
                }
            }
            catch (Exception realizationFailure)
            {
                // Even when outer bounds and measured bits are rolled back,
                // Measure/Arrange may have changed nested native state. A
                // canceled pass after its first attempted root is not a
                // certifiable snapshot repair; only replacement can recover it.
                if (firstMeasureAttempted < Blocks.Count)
                    MarkGeometryRecoveryRequiredNoLock(realizationFailure);

                // A failed preparation may have created a prefix of the new
                // band before cancellation/device loss. Release those unpinned
                // resources while candidate geometry is still available; the
                // policy's own transactional catch is defensive duplication.
                if (requestedSceneBandOrdinals is not null &&
                    _vectorSceneResidencyPolicy is { } failedBandPolicy)
                {
                    try
                    {
                        failedBandPolicy.ReleaseUnpinnedResources(
                            requestedSceneBandOrdinals,
                            visibleVectorBand,
                            CurrentPlacementGenerationNoLock(),
                            _topLevelOrdinalByBlockIndex);
                    }
                    catch
                    {
                        // Keep the original measurement/preparation failure.
                    }
                }

                // Measure can mutate a box before observing cancellation or a
                // device-loss exception. Restore the last logically committed
                // measured/estimated arrangement before another thread can
                // inspect this snapshot.
                if (measuredOrdinals is { Count: > 0 })
                {
                    foreach (int ordinal in measuredOrdinals)
                        _measuredTopLevelBlocks![ordinal] = false;
                    _measuredTopLevelBlockCount = oldMeasuredTopLevelBlockCount;
                    RemoveUnmeasuredLayoutPlanRootOrdinalsNoLock();
                    _measuredLayoutPlanRootOrdinalsAreSorted =
                        oldMeasuredLayoutPlanRootOrdinalsAreSorted;
                }
                _layoutRevision = oldRevision;
                // Include the predecessor: an unsuccessful Measure may also
                // have changed the attempted root's safe-HTML flow identity.
                try
                {
                    using var rollbackScope = LayoutPassCancellation.Push(CancellationToken.None);
                    if (firstMeasureAttempted < Blocks.Count)
                    {
                        // Capacity-only residency refreshes do not mutate
                        // arrangement. In particular, eager snapshots must
                        // not trigger even a suffix Reflow when native
                        // admission is canceled or fails.
                        ReflowNoLock(incrementRevision: false,
                            firstOrdinal: Math.Max(0, firstMeasureAttempted - 1));
                    }
                }
                catch (Exception rollbackFailure)
                {
                    if (firstMeasureAttempted < Blocks.Count)
                        MarkGeometryRecoveryRequiredNoLock(realizationFailure, rollbackFailure);

                    // A custom/native Arrange can fail again during recovery.
                    // Do not expose partially restored bounds under the previous
                    // revision. Retirement rejects new read/paint leases and
                    // drains resources asynchronously after this gate releases.
                    _lazyReflowContinuationRevision = null;
                    _ = Retire();
                    throw new AggregateException(
                        "Lazy realization and placement recovery both failed.",
                        realizationFailure, rollbackFailure);
                }
                throw;
            }

            try
            {
                PublishCommittedMetadataNoLock();
                _paintOnlyGeometryTainted = priorPaintOnlyGeometryTainted;
                LazyLayoutCommit commit = measuredOrdinals is { Count: > 0 }
                    ? new LazyLayoutCommit(
                        true,
                        measuredOrdinals.Count,
                        oldHeight,
                        _size.Height,
                        _layoutRevision)
                    : LazyLayoutCommit.Unchanged(_size.Height, _layoutRevision);
                return commit with
                {
                    NativeSceneResourcesChanged = nativeSceneResourcesChanged,
                    SceneGraphRebindRequired = _vectorSceneResidencyPolicy?.HasPendingGraphRebinds == true,
                };
            }
            catch (Exception failure)
            {
                if (firstMeasureAttempted < Blocks.Count)
                    MarkGeometryRecoveryRequiredNoLock(failure);
                throw;
            }
        }
    }

    internal (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(double verticalOffset)
    {
        if (verticalOffset <= 0)
            return null;

        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return null;

            foreach (var block in Blocks)
            {
                LayoutRootPlacement placement = CurrentPlacementGenerationNoLock().GetPlacement(_blockOrdinals[block]);
                if (placement.Bottom < verticalOffset)
                    continue;

                return (block.BlockIndex, placement.Top - verticalOffset);
            }
        }

        return null;
    }

    /// <summary>
    /// Captures replacement-time anchor state only when the current bounds can
    /// be read immediately. The UI thread must not wait behind lazy measure.
    /// </summary>
    internal bool TryCaptureScrollAnchor(double verticalOffset, out SnapshotScrollAnchor anchor)
    {
        anchor = default;
        if (!Monitor.TryEnter(_layoutLock))
            return false;

        try
        {
            if (GeometryRecoveryRequired)
                return false;

            int? blockIndex = null;
            double offsetFromTop = 0;
            if (verticalOffset > 0)
            {
                LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
                if (placements.IsDocumentOrder &&
                    placements.TryFindFirstBottomAtOrAfter(verticalOffset, out int indexedOrdinal))
                {
                    BlockBox block = Blocks[indexedOrdinal];
                    LayoutRootPlacement placement = placements.GetPlacement(indexedOrdinal);
                    blockIndex = block.BlockIndex;
                    offsetFromTop = placement.Top - verticalOffset;
                }
                else if (!placements.IsDocumentOrder)
                {
                    // Defensive custom placements may not be in document order.
                    // Keep the established document-order choice for that rare
                    // case rather than silently changing anchoring semantics.
                    for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
                    {
                        BlockBox block = Blocks[ordinal];
                        LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                        if (placement.Bottom < verticalOffset)
                            continue;

                        blockIndex = block.BlockIndex;
                        offsetFromTop = placement.Top - verticalOffset;
                        break;
                    }
                }
            }

            anchor = new SnapshotScrollAnchor(blockIndex, offsetFromTop, _size.Height);
            return true;
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    /// <summary>
    /// Captures the scalar geometry needed to navigate to a block without
    /// allowing callers to retain mutable box references beyond the snapshot
    /// read lease. A lazy, unmeasured owner reports its estimated outer band;
    /// its descendant position is not authoritative until that owner measures.
    /// </summary>
    internal SnapshotBlockScrollTargetState TryCaptureBlockScrollTarget(
        int blockIndex,
        out SnapshotBlockScrollTarget target)
    {
        target = default;
        if (!Monitor.TryEnter(_layoutLock))
            return SnapshotBlockScrollTargetState.Busy;

        try
        {
            if (GeometryRecoveryRequired)
                return SnapshotBlockScrollTargetState.Unavailable;

            if (Volatile.Read(ref _retirementRequested) != 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                return SnapshotBlockScrollTargetState.Unavailable;
            }

            if (!_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal) ||
                (uint)ordinal >= (uint)Blocks.Count)
            {
                return SnapshotBlockScrollTargetState.NotFound;
            }

            BlockBox owner = Blocks[ordinal];
            bool measured = !_lazyLayoutEnabled ||
                _measuredTopLevelBlocks is null ||
                _measuredTopLevelBlocks[ordinal];
            if (!TryFindBlockBoundsNoLock(owner, blockIndex, out Rect requestedBounds))
                return SnapshotBlockScrollTargetState.NotFound;

            LayoutRootPlacement ownerPlacement = CurrentPlacementGenerationNoLock().GetPlacement(ordinal);
            Rect ownerBounds = ownerPlacement.LogicalBounds;
            requestedBounds = CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, requestedBounds);
            target = new SnapshotBlockScrollTarget(
                blockIndex,
                measured ? requestedBounds.Top : ownerBounds.Top,
                ownerBounds.Top,
                ownerBounds.Bottom,
                ownerBounds.Height,
                measured,
                _layoutRevision);
            return measured
                ? SnapshotBlockScrollTargetState.Ready
                : SnapshotBlockScrollTargetState.RequiresRealization;
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    private static bool TryFindBlockBoundsNoLock(BlockBox box, int blockIndex, out Rect bounds)
    {
        if (box.BlockIndex == blockIndex)
        {
            bounds = box.Bounds;
            return true;
        }

        switch (box)
        {
            case ListItemBox listItem:
                if (TryFindBlockBoundsNoLock(listItem.Marker, blockIndex, out bounds) ||
                    TryFindBlockBoundsNoLock(listItem.Content, blockIndex, out bounds))
                {
                    return true;
                }
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryFindBlockBoundsNoLock(child, blockIndex, out bounds))
                        return true;
                }
                break;
            case CodeBlockBox codeBlock:
                foreach (InlineContainerBox chunk in codeBlock.Chunks)
                {
                    if (TryFindBlockBoundsNoLock(chunk, blockIndex, out bounds))
                        return true;
                }
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryFindBlockBoundsNoLock(cell, blockIndex, out bounds))
                        return true;
                }
                break;
        }

        bounds = default;
        return false;
    }

    /// <summary>
    /// Returns the top offset only when the captured top-level block identity
    /// still exists, using the same nonblocking lease as paint and hit testing.
    /// </summary>
    internal bool TryCaptureScrollAnchorTarget(
        int blockIndex,
        double offsetFromTop,
        out double targetOffset)
    {
        targetOffset = 0;
        if (!Monitor.TryEnter(_layoutLock))
            return false;

        try
        {
            if (GeometryRecoveryRequired ||
                Volatile.Read(ref _retirementRequested) != 0 ||
                Volatile.Read(ref _disposed) != 0 || Blocks.Count == 0)
            {
                return false;
            }

            if (_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            {
                targetOffset = CurrentPlacementGenerationNoLock().GetPlacement(ordinal).Top - offsetFromTop;
                return double.IsFinite(targetOffset);
            }

            // The caller preserves the existing height-ratio fallback when
            // the captured block identity no longer exists in the replacement
            // snapshot. Never anchor to a merely nearby, unrelated block.
            return false;
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    internal IReadOnlyList<BlockBox> GetMeasuredTopLevelBlocks()
    {
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return Array.Empty<BlockBox>();

            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return Blocks;

            var measured = new List<BlockBox>(_measuredTopLevelBlockCount);
            for (int n = 0; n < Blocks.Count; n++)
            {
                if (_measuredTopLevelBlocks[n])
                    measured.Add(Blocks[n]);
            }

            return measured;
        }
    }

    /// <summary>
    /// Remeasures the committed block tree after an asynchronous image changes its
    /// intrinsic size. Reusing the current boxes preserves resolver-partitioned image
    /// state; rebuilding would create a fresh unresolved box and restart the load.
    /// </summary>
    internal void RelayoutMeasuredBlocks(float availableWidth, CancellationToken cancellationToken)
    {
        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        lock (_layoutLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GeometryRecoveryRequired)
                return;

            float normalizedWidth = Math.Max(1f, availableWidth);
            float contentWidth = GetContentWidth(normalizedWidth);
            float contentX = (float)_documentPadding.Left;
            GetRelayoutScratchNoLock(out float[] heights, out Rect[] previousBounds);
            int lastMeasureAttempted = -1;
            bool priorPaintOnlyGeometryTainted = _paintOnlyGeometryTainted;
            _paintOnlyGeometryTainted = true;
            try
            {
                for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BlockBox block = Blocks[ordinal];
                    if (IsTopLevelMeasuredNoLock(ordinal))
                    {
                        block.ThrowIfCancellationRequested();
                        previousBounds[ordinal] = block.Bounds;
                        lastMeasureAttempted = ordinal;
                        heights[ordinal] = block.Measure(contentWidth);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        heights[ordinal] = EstimateHeight(block, ordinal);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (lastMeasureAttempted >= 0)
                    MarkGeometryRecoveryRequiredNoLock(failure);
                RestoreMeasuredPlacementsNoLock(lastMeasureAttempted, previousBounds);
                throw;
            }

            Size committedSize;
            long nextRevision;
            CommittedMetadata committedMetadata;
            try
            {
                float documentHeight = ComputeDocumentHeightNoLock(heights, cancellationToken);
                committedSize = new Size(normalizedWidth, documentHeight);
                nextRevision = unchecked(_layoutRevision + 1);
                committedMetadata = CreateCommittedMetadataNoLock(committedSize, nextRevision);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (lastMeasureAttempted >= 0)
                    MarkGeometryRecoveryRequiredNoLock(failure);
                RestoreMeasuredPlacementsNoLock(lastMeasureAttempted, previousBounds);
                throw;
            }

            // Measurement remains cancellable. Once placement starts, every
            // top-level and nested arrange plus its index/metadata publication
            // must finish as one snapshot commit, even if a rebuild arrives.
            bool arrangeAttempted = false;
            try
            {
                using var commitScope = LayoutPassCancellation.Push(CancellationToken.None);
                float y = (float)_documentPadding.Top;
                float[]? continuationUpdate = BeginLazyReflowContinuationUpdateNoLock(0, y);
                for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
                {
                    BlockBox block = Blocks[ordinal];
                    float height = heights[ordinal];
                    if (IsTopLevelMeasuredNoLock(ordinal))
                    {
                        arrangeAttempted = true;
                        block.Arrange(contentX, y, contentWidth);
                        _sharedLayoutMetrics?.RecordHeight(ordinal, height);
                    }
                    else
                    {
                        arrangeAttempted = true;
                        block.ArrangeEstimated(contentX, y, contentWidth, height);
                    }

                    AdvanceBlock(ref y, height, ordinal);
                    if (continuationUpdate is not null)
                        continuationUpdate[ordinal] = y;
                }

                _availableWidth = normalizedWidth;
                _size = committedSize;
                RefreshViewportIndexNoLock();
                _layoutRevision = nextRevision;
                CompleteLazyReflowContinuationUpdateNoLock(continuationUpdate, nextRevision);
                PublishCommittedMetadataNoLock(committedMetadata);
                // A complete eager relayout repairs every box. For a lazy snapshot,
                // only clear a prior taint after every top-level root was actually
                // measured again; estimated roots may hide failed nested mutation.
                _paintOnlyGeometryTainted = priorPaintOnlyGeometryTainted &&
                    _lazyLayoutEnabled && _measuredTopLevelBlockCount != Blocks.Count;
            }
            catch (Exception failure)
            {
                if (lastMeasureAttempted >= 0 || arrangeAttempted)
                    MarkGeometryRecoveryRequiredNoLock(failure);
                throw;
            }
        }
    }

    /// <summary>
    /// Allocation-stable counterpart for scene readiness/scheduling probes.
    /// The caller must already hold TryBeginPaint/TryBeginInteraction so this
    /// snapshot-owned scratch list cannot be overwritten before consumption.
    /// </summary>
    internal bool TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
        double top,
        double bottom,
        out IReadOnlyList<BlockBox> blocks)
        => TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            top,
            bottom,
            out blocks,
            out _,
            out _,
            out _);

    internal bool TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
        double top,
        double bottom,
        out IReadOnlyList<BlockBox> blocks,
        out IReadOnlyList<int> topLevelOrdinals,
        out LayoutPlacementGeneration placements,
        out IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex)
    {
        blocks = Array.Empty<BlockBox>();
        topLevelOrdinals = Array.Empty<int>();
        placements = null!;
        topLevelOrdinalByBlockIndex = null!;
        if (!Monitor.IsEntered(_layoutLock) || GeometryRecoveryRequired || !double.IsFinite(top) ||
            !double.IsFinite(bottom) || bottom < top)
        {
            return false;
        }

        _sceneProbeBlockScratch.Clear();
        _sceneBandOrdinalScratch.Clear();
        var band = new LazyLayoutBand(top, bottom);
        placements = CurrentPlacementGenerationNoLock();
        ViewportRange range = placements.Find(top, bottom);
        for (int index = range.Start; index < range.End; index++)
        {
            int ordinal = placements.GetBlockOrdinal(index);
            BlockBox block = Blocks[ordinal];
            LayoutRootPlacement placement = placements.GetPlacement(ordinal);
            if (placement.Bottom < top || placement.Top > bottom)
                continue;
            if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null &&
                !_measuredTopLevelBlocks[ordinal])
            {
                _sceneProbeBlockScratch.Clear();
                _sceneBandOrdinalScratch.Clear();
                placements = null!;
                return false;
            }

            _sceneProbeBlockScratch.Add(block);
            _sceneBandOrdinalScratch.Add(ordinal);
        }

        if (_vectorSceneResidencyPolicy?.AreNativeResourcesPrepared(
                _sceneBandOrdinalScratch,
                band,
                placements,
                _topLevelOrdinalByBlockIndex) == false)
        {
            _sceneProbeBlockScratch.Clear();
            _sceneBandOrdinalScratch.Clear();
            placements = null!;
            return false;
        }

        blocks = _sceneProbeBlockScratch;
        topLevelOrdinals = _sceneBandOrdinalScratch;
        topLevelOrdinalByBlockIndex = _topLevelOrdinalByBlockIndex;
        return true;
    }

    /// <summary>
    /// Checks terminal vector failures for the current logical viewport while
    /// holding the snapshot read lease. Scene owners remain physically arranged
    /// relative to their roots, so the residency policy maps each failed owner
    /// through this published placement generation before testing visibility.
    /// </summary>
    internal bool HasFailedScenesInBandUnderPaint(LazyLayoutBand band)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        if (GeometryRecoveryRequired || !double.IsFinite(band.Top) ||
            !double.IsFinite(band.Bottom) || band.Bottom < band.Top ||
            _vectorSceneResidencyPolicy is not { } policy)
        {
            return false;
        }

        return policy.HasFailedScenes(
            band,
            CurrentPlacementGenerationNoLock(),
            _topLevelOrdinalByBlockIndex);
    }

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

        using var cancellationScope = LayoutPassCancellation.Push(cancellationToken);
        lock (_layoutLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GeometryRecoveryRequired)
                return;

            float normalizedWidth = Math.Max(1f, availableWidth);
            if (Math.Abs(normalizedWidth - _availableWidth) > 0.5f)
            {
                // Width changes affect line wrapping in every measured block
                // and therefore still require the complete relayout contract.
                RelayoutMeasuredBlocks(normalizedWidth, cancellationToken);
                return;
            }

            var changedOrdinals = new HashSet<int>();
            int firstChangedOrdinal = Blocks.Count;
            bool hasUnknownOwner = false;
            foreach (int blockIndex in changedBlockIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
                {
                    hasUnknownOwner = true;
                    continue;
                }

                changedOrdinals.Add(ordinal);
                firstChangedOrdinal = Math.Min(firstChangedOrdinal, ordinal);
            }

            if (hasUnknownOwner)
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

            float contentWidth = GetContentWidthNoLock();
            float contentX = (float)_documentPadding.Left;
            int[] orderedChangedOrdinals = new int[changedOrdinals.Count];
            int nextChangedIndex = 0;
            foreach (int ordinal in changedOrdinals)
                orderedChangedOrdinals[nextChangedIndex++] = ordinal;
            Array.Sort(orderedChangedOrdinals);

            var changedMeasures = new ChangedBlockMeasure[orderedChangedOrdinals.Length];
            for (int index = 0; index < orderedChangedOrdinals.Length; index++)
            {
                int ordinal = orderedChangedOrdinals[index];
                BlockBox block = Blocks[ordinal];
                changedMeasures[index] = new ChangedBlockMeasure(
                    ordinal,
                    IsTopLevelMeasuredNoLock(ordinal),
                    block.Bounds,
                    block.Margin,
                    ordinal > 0 && StackBox.SharesSafeHtmlInlineFlow(
                        Blocks[ordinal - 1],
                        block),
                    ordinal < Blocks.Count - 1 && StackBox.SharesSafeHtmlInlineFlow(
                        block,
                        Blocks[ordinal + 1]));
            }

            bool priorPaintOnlyGeometryTainted = _paintOnlyGeometryTainted;
            _paintOnlyGeometryTainted = true;
            bool anyMeasureAttempted = false;
            try
            {
                for (int index = 0; index < changedMeasures.Length; index++)
                {
                    ChangedBlockMeasure state = changedMeasures[index];
                    if (!state.IsMeasured)
                        continue;

                    cancellationToken.ThrowIfCancellationRequested();
                    BlockBox block = Blocks[state.Ordinal];
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
                    MarkGeometryRecoveryRequiredNoLock(failure);
                RestoreChangedBlockPlacementsNoLock(changedMeasures);
                throw;
            }

            // When a changed root joins or leaves a safe-HTML inline flow, the
            // preceding root's continuation also changes. Reflow must begin at
            // that predecessor; otherwise the old shared-flow boundary is
            // retained and both the changed root and its suffix shift incorrectly.
            foreach (ChangedBlockMeasure state in changedMeasures)
            {
                if (state.Ordinal > 0 &&
                    state.SharedFlowWithPrevious != StackBox.SharesSafeHtmlInlineFlow(
                        Blocks[state.Ordinal - 1],
                        Blocks[state.Ordinal]))
                {
                    firstChangedOrdinal = Math.Min(firstChangedOrdinal, state.Ordinal - 1);
                }
            }

            // A visual-order snapshot has no trustworthy document prefix from
            // which a suffix transaction can continue. Reflow from the document
            // origin and rebuild the index from the resulting actual bounds.
            if (!CurrentPlacementGenerationNoLock().IsDocumentOrder)
                firstChangedOrdinal = 0;

            bool unchangedGeometry = false;
            long unchangedRevision = 0;
            CommittedMetadata? unchangedMetadata = null;
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
                    unchangedRevision = unchecked(_layoutRevision + 1);
                    unchangedMetadata = CreateCommittedMetadataNoLock(_size, unchangedRevision);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (anyMeasureAttempted)
                    MarkGeometryRecoveryRequiredNoLock(failure);
                RestoreChangedBlockPlacementsNoLock(changedMeasures);
                throw;
            }

            if (unchangedGeometry)
            {
                ChangedBlockMeasure state = changedMeasures[0];
                BlockBox block = Blocks[state.Ordinal];
                // No token checks may interrupt the arrange/publication pair.
                using var commitScope = LayoutPassCancellation.Push(CancellationToken.None);
                try
                {
                    block.Arrange(
                        (float)state.PreviousBounds.X,
                        (float)state.PreviousBounds.Y,
                        (float)state.PreviousBounds.Width);
                    _sharedLayoutMetrics?.RecordHeight(state.Ordinal, state.Height);
                    PreserveLazyReflowContinuationRevisionNoLock(_layoutRevision, unchangedRevision);
                    _layoutRevision = unchangedRevision;
                    PublishCommittedMetadataNoLock(unchangedMetadata!);
                    _paintOnlyGeometryTainted = priorPaintOnlyGeometryTainted;
                    return;
                }
                catch (Exception failure)
                {
                    MarkGeometryRecoveryRequiredNoLock(failure);
                    throw;
                }
            }

            long committedRevision;
            CommittedMetadata committed;
            try
            {
                committedRevision = unchecked(_layoutRevision + 1);
                // Reserve before the first live placement. The mandatory
                // sequential Arrange pass below computes final height too;
                // a second suffix walk adds gate work without changing it.
                committed = CreateCommittedMetadataNoLock(_size, committedRevision);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                if (anyMeasureAttempted)
                    MarkGeometryRecoveryRequiredNoLock(failure);
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
                using (LayoutPassCancellation.Push(CancellationToken.None))
                {
                    float y = GetChangedRelayoutStartNoLock(firstChangedOrdinal);
                    float[]? continuationUpdate = BeginLazyReflowContinuationUpdateNoLock(firstChangedOrdinal, y);
                    int changedIndex = 0;
                    for (int ordinal = firstChangedOrdinal; ordinal < Blocks.Count; ordinal++)
                    {
                        while (changedIndex < changedMeasures.Length &&
                            changedMeasures[changedIndex].Ordinal < ordinal)
                        {
                            changedIndex++;
                        }

                        bool isChanged = changedIndex < changedMeasures.Length &&
                            changedMeasures[changedIndex].Ordinal == ordinal;
                        BlockBox block = Blocks[ordinal];
                        float height;
                        if (IsTopLevelMeasuredNoLock(ordinal))
                        {
                            height = isChanged
                                ? changedMeasures[changedIndex].Height
                                : (float)block.Bounds.Height;
                            arrangeAttempted = true;
                            block.Arrange(contentX, y, contentWidth);
                            if (isChanged)
                                _sharedLayoutMetrics?.RecordHeight(ordinal, height);
                        }
                        else
                        {
                            height = EstimateHeight(block, ordinal);
                            arrangeAttempted = true;
                            block.ArrangeEstimated(contentX, y, contentWidth, height);
                        }

                        AdvanceBlock(ref y, height, ordinal);
                        if (continuationUpdate is not null)
                            continuationUpdate[ordinal] = y;
                    }

                    y += (float)_documentPadding.Bottom;
                    _size = new Size(_availableWidth, y);
                    CompleteCommittedMetadataSizeNoLock(committed, _size);
                    RefreshViewportIndexNoLock(firstChangedOrdinal);
                    _layoutRevision = committedRevision;
                    CompleteLazyReflowContinuationUpdateNoLock(continuationUpdate, committedRevision);
                    PublishCommittedMetadataNoLock(committed);
                }
            }
            catch (Exception failure)
            {
                if (arrangeAttempted || anyMeasureAttempted)
                    MarkGeometryRecoveryRequiredNoLock(failure);
                throw;
            }
            _paintOnlyGeometryTainted = priorPaintOnlyGeometryTainted;
        }
    }

    private bool CanUseUnchangedGeometryCommitNoLock(
        ChangedBlockMeasure state,
        float contentX,
        float contentWidth)
    {
        BlockBox changedBlock = Blocks[state.Ordinal];
        return state.Height == state.PreviousBounds.Height &&
            changedBlock.Bounds.Height == state.PreviousBounds.Height &&
            changedBlock.Bounds.Width == state.PreviousBounds.Width &&
            changedBlock.Margin.Equals(state.PreviousMargin) &&
            state.SharedFlowWithPrevious == (state.Ordinal > 0 &&
                StackBox.SharesSafeHtmlInlineFlow(
                    Blocks[state.Ordinal - 1],
                    changedBlock)) &&
            state.SharedFlowWithNext == (state.Ordinal < Blocks.Count - 1 &&
                StackBox.SharesSafeHtmlInlineFlow(
                    changedBlock,
                    Blocks[state.Ordinal + 1])) &&
            HasCoherentCommittedPlacementNoLock(
                state.Ordinal,
                state.PreviousBounds,
                contentX,
                contentWidth);
    }

    private float GetChangedRelayoutStartNoLock(int firstChangedOrdinal)
    {
        LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
        return firstChangedOrdinal == 0 || !placements.IsDocumentOrder
            ? (float)_documentPadding.Top
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
            Blocks[state.Ordinal].ArrangeEstimated(
                (float)bounds.X,
                (float)bounds.Y,
                (float)bounds.Width,
                (float)bounds.Height);
        }
    }

    private bool HasCoherentCommittedPlacementNoLock(
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

    private static bool IsFiniteBounds(Rect bounds) =>
        double.IsFinite(bounds.X) &&
        double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height);

    internal bool IsTopLevelBlockMeasured(BlockBox block)
    {
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return false;

            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return true;

            return !_blockOrdinals.TryGetValue(block, out int ordinal) ||
                _measuredTopLevelBlocks[ordinal];
        }
    }

    private void ReflowNoLock(bool incrementRevision = true, int firstOrdinal = 0)
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
    private float[]? BeginLazyReflowContinuationUpdateNoLock(int firstOrdinal, float start)
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

    private void CompleteLazyReflowContinuationUpdateNoLock(float[]? update, long revision)
    {
        if (update is null)
            return;
        _lazyReflowContinuationWidth = _availableWidth;
        _lazyReflowContinuationRevision = revision;
    }

    private void PreserveLazyReflowContinuationRevisionNoLock(long previousRevision, long nextRevision)
    {
        if (_lazyReflowContinuationRevision == previousRevision)
            _lazyReflowContinuationRevision = nextRevision;
    }

    private void PublishCommittedMetadataNoLock()
    {
        int measuredCount = _lazyLayoutEnabled ? _measuredTopLevelBlockCount : Blocks.Count;
        CommittedMetadata previous = _committedMetadata;
        if (previous.Revision == _layoutRevision && previous.MeasuredCount == measuredCount &&
            previous.Size.Width == _size.Width && previous.Size.Height == _size.Height &&
            _pendingPlacementGeneration is null)
            return;

        LayoutPlacementGeneration placements = _pendingPlacementGeneration ??
            (measuredCount != previous.MeasuredCount && _measuredTopLevelBlocks is not null
                ? previous.Placements.WithMeasuredRoots(_measuredTopLevelBlocks, _layoutRevision)
                : previous.Placements);
        PublishCommittedMetadataNoLock(new CommittedMetadata(_size, _layoutRevision, measuredCount, placements));
    }

    private CommittedMetadata CreateCommittedMetadataNoLock(
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
    private void CompleteCommittedMetadataSizeNoLock(CommittedMetadata metadata, Size size)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.CompleteSizeBeforePublish(size);
    }

    private void PublishCommittedMetadataNoLock(CommittedMetadata metadata)
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

    private sealed class CommittedMetadata(Size size, long revision, int measuredCount)
    {
        private Size _size = size;
        private LayoutPlacementGeneration? _placements;
        private bool _published;
        private bool _sizeCompleted;
        private bool _placementsCompleted;

        internal Size Size => _size;
        internal readonly long Revision = revision;
        internal readonly int MeasuredCount = measuredCount;
        internal LayoutPlacementGeneration Placements => _placements ??
            throw new InvalidOperationException("Layout metadata has no placement generation.");
        internal bool HasPlacements => _placements is not null;

        internal CommittedMetadata(
            Size size,
            long revision,
            int measuredCount,
            LayoutPlacementGeneration? placements)
            : this(size, revision, measuredCount)
        {
            _placements = placements;
            _placementsCompleted = placements is not null;
        }

        internal void CompleteSizeBeforePublish(Size size)
        {
            if (_published || _sizeCompleted)
                throw new InvalidOperationException("Layout metadata size may be completed once before publication.");
            _size = size;
            _sizeCompleted = true;
        }

        internal void CompletePlacementsBeforePublish(LayoutPlacementGeneration placements)
        {
            ArgumentNullException.ThrowIfNull(placements);
            if (_published || _placementsCompleted)
                throw new InvalidOperationException("Layout metadata placements may be completed once before publication.");
            _placements = placements;
            _placementsCompleted = true;
        }

        internal void CompletePlacementRevisionBeforePublish(long revision)
        {
            if (_published || !_placementsCompleted)
                throw new InvalidOperationException("Layout metadata placement revision may be completed only before publication.");
            LayoutPlacementGeneration current = Placements;
            _placements = current.WithRevision(revision);
        }

        internal void MarkPublished()
        {
            if (_published)
                throw new InvalidOperationException("Layout metadata may be published only once.");
            _published = true;
        }
    }

    private struct ChangedBlockMeasure
    {
        internal ChangedBlockMeasure(
            int ordinal,
            bool isMeasured,
            Rect previousBounds,
            Thickness previousMargin,
            bool sharedFlowWithPrevious,
            bool sharedFlowWithNext)
        {
            Ordinal = ordinal;
            IsMeasured = isMeasured;
            PreviousBounds = previousBounds;
            PreviousMargin = previousMargin;
            SharedFlowWithPrevious = sharedFlowWithPrevious;
            SharedFlowWithNext = sharedFlowWithNext;
            MeasureAttempted = false;
            Height = 0;
        }

        internal readonly int Ordinal;
        internal readonly bool IsMeasured;
        internal readonly Rect PreviousBounds;
        internal readonly Thickness PreviousMargin;
        internal readonly bool SharedFlowWithPrevious;
        internal readonly bool SharedFlowWithNext;
        internal bool MeasureAttempted;
        internal float Height;
    }

    /// <summary>
    /// Reads the lazy-layout state for a focusable descendant without making
    /// the UI thread wait behind an in-flight background measurement. For an
    /// unrealized descendant, <paramref name="estimatedBand"/> is the current
    /// estimated bounds of its owning top-level block.
    /// </summary>
    internal FocusTargetLayoutState TryGetFocusTargetLayoutState(
        int blockIndex,
        out LazyLayoutBand estimatedBand)
    {
        estimatedBand = default;
        if (!Monitor.TryEnter(_layoutLock))
            return FocusTargetLayoutState.Busy;

        try
        {
            if (GeometryRecoveryRequired ||
                Volatile.Read(ref _retirementRequested) != 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                return FocusTargetLayoutState.Unavailable;
            }

            if (!_lazyLayoutEnabled || _measuredTopLevelBlocks is null)
                return FocusTargetLayoutState.Ready;

            if (!_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal) ||
                (uint)ordinal >= (uint)Blocks.Count)
            {
                return FocusTargetLayoutState.Unavailable;
            }

            if (_measuredTopLevelBlocks[ordinal])
                return FocusTargetLayoutState.Ready;

            Rect bounds = CurrentPlacementGenerationNoLock().GetPlacement(ordinal).LogicalBounds;
            if (!double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Bottom))
                return FocusTargetLayoutState.Unavailable;

            double top = Math.Max(0, bounds.Top);
            estimatedBand = new LazyLayoutBand(top, Math.Max(top + 1, bounds.Bottom));
            return FocusTargetLayoutState.RequiresRealization;
        }
        finally
        {
            Monitor.Exit(_layoutLock);
        }
    }

    private float GetContentWidthNoLock()
        => (float)Math.Max(
            1,
            _availableWidth - _documentPadding.Left - _documentPadding.Right);

    private float GetContentWidth(float availableWidth)
        => (float)Math.Max(
            1,
            availableWidth - _documentPadding.Left - _documentPadding.Right);

    private bool IsTopLevelMeasuredNoLock(int ordinal)
        => !_lazyLayoutEnabled ||
            (_measuredTopLevelBlocks is not null && _measuredTopLevelBlocks[ordinal]);

    private void GetRelayoutScratchNoLock(out float[] heights, out Rect[] previousBounds)
    {
        int count = Blocks.Count;
        if (_relayoutHeightScratch is null || _relayoutHeightScratch.Length != count)
            _relayoutHeightScratch = new float[count];
        if (_relayoutBoundsScratch is null || _relayoutBoundsScratch.Length != count)
            _relayoutBoundsScratch = new Rect[count];

        heights = _relayoutHeightScratch;
        previousBounds = _relayoutBoundsScratch;
    }

    private float ComputeDocumentHeightNoLock(float[] heights, CancellationToken cancellationToken)
    {
        float y = (float)_documentPadding.Top;
        for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdvanceBlock(ref y, heights[ordinal], ordinal);
        }

        return y + (float)_documentPadding.Bottom;
    }

    private void RestoreMeasuredPlacementsNoLock(int lastMeasureAttempted, Rect[] previousBounds)
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

    private void AdvanceBlock(ref float y, float height, int blockOrdinal)
    {
        y += height;
        if (blockOrdinal < Blocks.Count - 1 &&
            !StackBox.SharesSafeHtmlInlineFlow(Blocks[blockOrdinal], Blocks[blockOrdinal + 1]))
            y += _blockSpacing;
    }

    private float EstimateHeight(BlockBox block, int ordinal)
    {
        if (_sharedLayoutMetrics?.TryGetHeight(ordinal, out float sharedHeight) == true)
            return sharedHeight;

        float margin = (float)(block.Margin.Top + block.Margin.Bottom);
        return block switch
        {
            ImageBox => Math.Max(160f, margin + 120f),
            EmbedBox => Math.Max(64f, margin + 48f),
            CodeBlockBox => Math.Max(72f, margin + 64f),
            TableBox table => Math.Clamp(36f + table.RowCount * 34f + margin, 72f, 360f),
            ListItemBox => Math.Max(36f, margin + 32f),
            StackBox stack => Math.Clamp(32f + stack.Children.Count * 28f + margin, 48f, 420f),
            ThematicBreakBox => Math.Max(16f, margin + 1f),
            InlineContainerBox => Math.Max(28f, margin + 24f),
            _ => Math.Max(32f, margin + 28f),
        };
    }

    public void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long inlineBytes = 0;
        long tableBytes = 0;
        long codeBytes = 0;
        long otherBytes = 0;
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return;

            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            var range = FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
            for (int i = range.Start; i < range.End; i++)
            {
                int n = placements.GetBlockOrdinal(i);
                var b = Blocks[n];
                LayoutRootPlacement placement = placements.GetPlacement(n);
                if (placement.Bottom < viewport.Top || placement.Top > viewport.Bottom) continue;
                if (!placement.Measured) continue;
                long before = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                Matrix3x2 originalTransform = ds.Transform;
                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(
                        placement.TranslationX, placement.TranslationY, originalTransform);
                try
                {
                    ds.Transform = Matrix3x2.CreateTranslation(
                        placement.TranslationX,
                        placement.TranslationY) * originalTransform;
                    b.Paint(ds, placements.ToPhysicalViewport(n, viewport));
                }
                finally
                {
                    ds.Transform = originalTransform;
                }
                if (measure)
                {
                    long allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
                    switch (b)
                    {
                        case InlineContainerBox:
                            inlineBytes += allocated;
                            break;
                        case TableBox:
                            tableBytes += allocated;
                            break;
                        case CodeBlockBox:
                            codeBytes += allocated;
                            break;
                        default:
                            otherBytes += allocated;
                            break;
                    }
                }
            }
        }
        if (measure)
        {
            performance.SnapshotPaintAllocationBreakdown(
                MarkdownPerformanceEventSource.GetLogicalFrameId(),
                inlineBytes,
                tableBytes,
                codeBytes,
                otherBytes);
        }
    }

    public void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return;

            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            var visible = FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
            for (int i = visible.Start; i < visible.End; i++)
            {
                int n = placements.GetBlockOrdinal(i);
                var b = Blocks[n];
                LayoutRootPlacement placement = placements.GetPlacement(n);
                if (placement.Bottom < viewport.Top || placement.Top > viewport.Bottom) continue;
                if (!placement.Measured) continue;
                Matrix3x2 originalTransform = ds.Transform;
                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(
                        placement.TranslationX, placement.TranslationY, originalTransform);
                try
                {
                    ds.Transform = Matrix3x2.CreateTranslation(
                        placement.TranslationX,
                        placement.TranslationY) * originalTransform;
                    PaintSelectionForeground(
                        b, ds, range, color, placements.ToPhysicalViewport(n, viewport));
                }
                finally
                {
                    ds.Transform = originalTransform;
                }
            }
        }
    }

    private static void PaintSelectionForeground(BlockBox box, CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        box.PaintSelectionForeground(ds, range, color, viewport);
    }

    public bool HitTest(Point point, out DocumentPosition position)
    {
        position = DocumentPosition.Zero;
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return false;

            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            var candidates = FindViewportRangeNoLock(point.Y - 4, point.Y + 4);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int n = placements.GetBlockOrdinal(i);
                var b = Blocks[n];
                LayoutRootPlacement placement = placements.GetPlacement(n);
                if (!placement.Measured) continue;
                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (b.HitTest(placements.ToPhysicalPoint(n, point), out position)) return true;
            }
        }
        position = DocumentPosition.Zero;
        return false;
    }

    /// <summary>
    /// Resolves a drag-selection endpoint. Unlike ordinary hit testing, this
    /// permits atomic visual content to expose before/after boundaries across
    /// its full arranged bounds without making that surface initiate selection.
    /// </summary>
    internal bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        position = DocumentPosition.Zero;
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return false;

            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            var candidates = FindViewportRangeNoLock(point.Y - 4, point.Y + 4);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = placements.GetBlockOrdinal(i);
                BlockBox block = Blocks[ordinal];
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                if (!placement.Measured)
                {
                    continue;
                }

                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (block.HitTestSelectionEndpoint(
                        placements.ToPhysicalPoint(ordinal, point), out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    /// <summary>
    /// Resolves named vector semantics independently from application
    /// selection. TextPattern clients must be able to place a range on a
    /// linked or informational diagram item even when it is not Selectable.
    /// </summary>
    internal bool TryHitTestVectorText(Point point, out DocumentPosition position)
    {
        position = DocumentPosition.Zero;
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return false;

            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            var candidates = FindViewportRangeNoLock(point.Y - 1, point.Y + 1);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = placements.GetBlockOrdinal(i);
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                if (!placement.Measured)
                {
                    continue;
                }

                using LayoutPlacementTransformContext.Scope placementScope =
                    LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
                if (TryHitTestVectorText(
                        Blocks[ordinal], placements.ToPhysicalPoint(ordinal, point), out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    internal bool TryGetHorizontalOverflow(
        Point point,
        out IHorizontalOverflowBox? overflow)
    {
        overflow = null;
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return false;

            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            var candidates = FindViewportRangeNoLock(point.Y - 1, point.Y + 1);
            for (int i = candidates.Start; i < candidates.End; i++)
            {
                int ordinal = placements.GetBlockOrdinal(i);
                if (!placements.GetPlacement(ordinal).Measured)
                {
                    continue;
                }

                if (FindHorizontalOverflow(
                        Blocks[ordinal], placements.ToPhysicalPoint(ordinal, point)) is { } found)
                {
                    overflow = found;
                    return true;
                }
            }
        }

        overflow = null;
        return false;
    }

    internal bool TrySetHorizontalOffset(
        IHorizontalOverflowBox overflow,
        double offset)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired || Volatile.Read(ref _disposed) != 0 ||
                !_horizontalOverflowBoxes.Contains(overflow))
                return false;

            return overflow.SetHorizontalOffset(offset);
        }
    }

    internal bool TryGetHorizontalOverflowForBlockIndex(
        int blockIndex,
        out IHorizontalOverflowBox? overflow)
    {
        overflow = null;
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return false;

            if (_horizontalOverflowByBlockIndex.TryGetValue(blockIndex, out var found) &&
                found.CanScrollHorizontally)
            {
                overflow = found;
                return true;
            }
        }

        overflow = null;
        return false;
    }

    internal bool TryScrollHorizontal(
        IHorizontalOverflowBox overflow,
        double delta)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired || Volatile.Read(ref _disposed) != 0 ||
                !_horizontalOverflowBoxes.Contains(overflow))
                return false;

            return overflow.ScrollHorizontal(delta);
        }
    }

    private static IHorizontalOverflowBox? FindHorizontalOverflow(BlockBox box, Point point)
    {
        if (!box.Bounds.Contains(point))
            return null;

        if (box is IHorizontalOverflowBox overflow && overflow.CanScrollHorizontally)
            return overflow;

        if (box is ListItemBox listItem)
            return FindHorizontalOverflow(listItem.Marker, point) ??
                   FindHorizontalOverflow(listItem.Content, point);

        if (box is StackBox stack)
        {
            foreach (var child in stack.Children)
            {
                if (FindHorizontalOverflow(child, point) is { } found)
                    return found;
            }
        }

        return null;
    }

    private static bool TryHitTestVectorText(
        BlockBox box,
        Point point,
        out DocumentPosition position)
    {
        if (!box.Bounds.Contains(point))
        {
            position = DocumentPosition.Zero;
            return false;
        }

        if (box is VectorSceneBox vector)
            return vector.TryGetTextPositionAt(point, out position);

        if (box is ListItemBox listItem)
        {
            return TryHitTestVectorText(listItem.Marker, point, out position) ||
                   TryHitTestVectorText(listItem.Content, point, out position);
        }

        if (box is TableBox table)
        {
            foreach (InlineContainerBox cell in table.GetCellBoxes())
            {
                if (TryHitTestVectorText(cell, point, out position))
                    return true;
            }
        }
        else if (box is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                if (TryHitTestVectorText(child, point, out position))
                    return true;
            }
        }

        position = DocumentPosition.Zero;
        return false;
    }

    /// <summary>
    /// Returns the cached document-order focus plan, filtered by current
    /// horizontal-overflow eligibility. Used for Tab/Shift+Tab navigation.
    /// </summary>
    public IReadOnlyList<FocusableItem> CollectFocusableItems()
    {
        if (GeometryRecoveryRequired)
            return Array.Empty<FocusableItem>();

        if (_fixedFocusableItems is not null)
            return _fixedFocusableItems;

        var list = new List<FocusableItem>(_focusablePlan.Length);
        lock (_layoutLock)
        {
            if (GeometryRecoveryRequired)
                return Array.Empty<FocusableItem>();
            CollectFocusableItemsNoLock(list);
        }
        return list;
    }

    /// <summary>
    /// Collects the current focus plan only while the caller owns a snapshot
    /// read lease. The returned list contains value-only block/run identities;
    /// no box or snapshot reference escapes this method.
    /// </summary>
    internal bool TryCollectFocusableItemsUnderReadLease(
        out IReadOnlyList<FocusableItem> items)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        items = Array.Empty<FocusableItem>();
        if (GeometryRecoveryRequired || IsRetiringOrDisposed)
            return false;

        if (_fixedFocusableItems is { } fixedItems)
        {
            items = fixedItems;
            return true;
        }

        var list = new List<FocusableItem>(_focusablePlan.Length);
        CollectFocusableItemsNoLock(list);
        items = list;
        return true;
    }

    private void CollectFocusableItemsNoLock(List<FocusableItem> list)
    {
        foreach (FocusablePlanEntry entry in _focusablePlan)
        {
            if (entry.HorizontalOverflow is not { } overflow)
            {
                list.Add(entry.Item);
                continue;
            }

            if (_lazyLayoutEnabled &&
                _measuredTopLevelBlocks is not null &&
                _topLevelOrdinalByBlockIndex.TryGetValue(entry.Item.BlockIndex, out int ordinal) &&
                !_measuredTopLevelBlocks[ordinal])
            {
                continue;
            }

            if (overflow.CanScrollHorizontally)
                list.Add(entry.Item);
        }
    }

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
        if (Monitor.IsEntered(_layoutLock) && _paintOnlyGeometryTainted)
            return false;

        if (!Monitor.TryEnter(_layoutLock))
            return false;
        if (!GeometryRecoveryRequired &&
            Volatile.Read(ref _retirementRequested) == 0 &&
            Volatile.Read(ref _disposed) == 0)
        {
            return true;
        }

        Monitor.Exit(_layoutLock);
        return false;
    }

    internal void EndInteraction() => Monitor.Exit(_layoutLock);

    internal void EndPaint() => Monitor.Exit(_layoutLock);

    private void RefreshViewportIndexNoLock()
        => RefreshViewportIndexNoLock(0);

    /// <summary>
    /// Rebuilds the viewport index from the given document ordinal onward when
    /// the committed index still maps slots directly to block ordinals. Custom
    /// boxes may violate visual/document ordering, in which case the index uses
    /// its full defensive refresh and sort instead.
    /// </summary>
    private void RefreshViewportIndexNoLock(int firstOrdinal)
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

        var inputs = new LayoutPlacementInput[Blocks.Count];
        for (int ordinal = 0; ordinal < Blocks.Count; ordinal++)
        {
            if (preservePrefix && ordinal < start)
            {
                LayoutRootPlacement prior = source!.GetPlacement(ordinal);
                inputs[ordinal] = new LayoutPlacementInput(
                    prior.BaseX,
                    prior.BaseTop,
                    prior.X,
                    prior.Width,
                    prior.Height,
                    prior.Measured,
                    source.SharesFlowWithNext(ordinal));
                continue;
            }

            BlockBox block = Blocks[ordinal];
            Rect bounds = block.Bounds;
            bool measured = _measuredTopLevelBlocks is null || _measuredTopLevelBlocks[ordinal];
            bool sharesFlowWithNext = ordinal < Blocks.Count - 1 &&
                StackBox.SharesSafeHtmlInlineFlow(block, Blocks[ordinal + 1]);
            inputs[ordinal] = new LayoutPlacementInput(
                (float)bounds.X,
                (float)bounds.Y,
                (float)bounds.X,
                (float)bounds.Width,
                measured ? (float)bounds.Height : EstimateHeight(block, ordinal),
                measured,
                sharesFlowWithNext);
        }

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

    private ViewportRange FindViewportRangeNoLock(double top, double bottom)
        => CurrentPlacementGenerationNoLock().Find(top, bottom);

    private LayoutPlacementGeneration CurrentPlacementGenerationNoLock()
    {
        if (_pendingPlacementGeneration is { } pending)
            return pending;
        return _committedMetadata?.Placements ??
            throw new InvalidOperationException("The snapshot has no committed placement generation.");
    }

    /// <summary>
    /// Gets the committed viewport-index range for an admitted interactive
    /// reader. Callers must hold a successful TryBeginPaint/TryBeginInteraction
    /// lease for the entire use of the returned slots.
    /// </summary>
    internal ViewportRange GetMeasuredViewportRangeUnderPaint(Rect viewport)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        if (GeometryRecoveryRequired ||
            !double.IsFinite(viewport.Top) ||
            !double.IsFinite(viewport.Bottom) ||
            viewport.Bottom < viewport.Top)
        {
            return new ViewportRange(0, 0);
        }

        return FindViewportRangeNoLock(viewport.Top, viewport.Bottom);
    }

    /// <summary>
    /// Resolves one viewport-index slot to a measured root intersecting the
    /// requested vertical band. The caller must keep its snapshot read lease.
    /// </summary>
    internal bool TryGetMeasuredViewportBlockAtSlotUnderPaint(
        int slot,
        Rect viewport,
        out BlockBox block)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        block = null!;
        LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
        if (GeometryRecoveryRequired ||
            (uint)slot >= (uint)placements.Count)
        {
            return false;
        }

        int ordinal = placements.GetBlockOrdinal(slot);
        if ((uint)ordinal >= (uint)Blocks.Count)
            return false;

        BlockBox candidate = Blocks[ordinal];
        LayoutRootPlacement placement = placements.GetPlacement(ordinal);
        if (placement.Bottom < viewport.Top ||
            placement.Top > viewport.Bottom ||
            !placement.Measured)
        {
            return false;
        }

        block = candidate;
        return true;
    }

    internal int GetBlockOrdinalAtViewportSlotUnderPaint(int slot)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
        if ((uint)slot >= (uint)placements.Count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return placements.GetBlockOrdinal(slot);
    }

    internal Rect ToLogicalRectForBlockIndexUnderPaint(int blockIndex, Rect physicalRect)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
            ? CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, physicalRect)
            : physicalRect;
    }

    internal LayoutPlacementTransformContext.Scope EnterPlacementTransformScopeForBlockIndexUnderPaint(
        int blockIndex)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        if (!_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            return default;

        LayoutRootPlacement placement = CurrentPlacementGenerationNoLock().GetPlacement(ordinal);
        return LayoutPlacementTransformContext.Enter(placement.TranslationX, placement.TranslationY);
    }

    internal LayoutPlacementTransformContext.Scope EnterPlacementTransformScopeForTopLevelBlockUnderPaint(
        BlockBox block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return EnterPlacementTransformScopeForBlockIndexUnderPaint(block.BlockIndex);
    }

    internal Rect ToLogicalRectForBlockIndex(int blockIndex, Rect physicalRect)
    {
        lock (_layoutLock)
        {
            return _topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
                ? CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, physicalRect)
                : physicalRect;
        }
    }

    internal Func<int, Rect, Rect> CaptureLogicalRectMapper()
    {
        lock (_layoutLock)
        {
            LayoutPlacementGeneration placements = CurrentPlacementGenerationNoLock();
            return (blockIndex, physicalRect) =>
                _topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
                    ? placements.ToLogicalRect(ordinal, physicalRect)
                    : physicalRect;
        }
    }

    internal Point ToLogicalPointForBlockIndexUnderPaint(int blockIndex, Point physicalPoint)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        if (!_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            return physicalPoint;
        return CurrentPlacementGenerationNoLock().ToLogicalPoint(ordinal, physicalPoint);
    }

    internal Point ToPhysicalPointForBlockIndexUnderPaint(int blockIndex, Point logicalPoint)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal)
            ? CurrentPlacementGenerationNoLock().ToPhysicalPoint(ordinal, logicalPoint)
            : logicalPoint;
    }

    internal Rect ToPhysicalViewportForTopLevelBlockUnderPaint(BlockBox block, Rect logicalViewport)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _blockOrdinals.TryGetValue(block, out int ordinal)
            ? CurrentPlacementGenerationNoLock().ToPhysicalViewport(ordinal, logicalViewport)
            : logicalViewport;
    }

    internal Point ToPhysicalPointForTopLevelBlockUnderPaint(BlockBox block, Point logicalPoint)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _blockOrdinals.TryGetValue(block, out int ordinal)
            ? CurrentPlacementGenerationNoLock().ToPhysicalPoint(ordinal, logicalPoint)
            : logicalPoint;
    }

    internal bool TryGetLogicalTopForBlockIndex(int blockIndex, out double top)
    {
        lock (_layoutLock)
        {
            if (_topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
            {
                top = CurrentPlacementGenerationNoLock().GetPlacement(ordinal).Top;
                return double.IsFinite(top);
            }

            top = 0;
            return false;
        }
    }

    internal Rect ToLogicalRectForTopLevelBlockUnderPaint(BlockBox block, Rect physicalRect)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");
        return _blockOrdinals.TryGetValue(block, out int ordinal)
            ? CurrentPlacementGenerationNoLock().ToLogicalRect(ordinal, physicalRect)
            : physicalRect;
    }

    internal bool TryGetMeasuredBlockByOrdinalUnderPaint(int ordinal, out BlockBox block)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        block = null!;
        if (GeometryRecoveryRequired || (uint)ordinal >= (uint)Blocks.Count ||
            !CurrentPlacementGenerationNoLock().GetPlacement(ordinal).Measured)
        {
            return false;
        }

        block = Blocks[ordinal];
        return true;
    }

    internal bool TryGetMeasuredTopLevelBlockForBlockIndexUnderPaint(
        int blockIndex,
        out BlockBox block)
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        block = null!;
        return _topLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal) &&
            TryGetMeasuredBlockByOrdinalUnderPaint(ordinal, out block);
    }

    /// <summary>
    /// Provides the immutable, traversal-ordered subset of boxes that can
    /// contribute realization plans. The caller must hold a snapshot read
    /// lease while using the box references because their native geometry is
    /// mutable across layout commits.
    /// </summary>
    internal ReadOnlySpan<LayoutPlanCandidate> GetLayoutPlanCandidatesUnderPaint()
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        return GeometryRecoveryRequired
            ? ReadOnlySpan<LayoutPlanCandidate>.Empty
            : _layoutPlanCandidates;
    }

    /// <summary>
    /// Enumerates every currently measured realization candidate in document
    /// order. For a lazy document-order placement this visits only candidate
    /// ranges belonging to measured roots; eager and custom-placement layouts
    /// retain the conservative full-candidate traversal. The caller must hold
    /// the snapshot interaction lease for the entire enumeration.
    /// </summary>
    internal LayoutPlanCandidateEnumerator GetMeasuredLayoutPlanCandidatesUnderPaint()
    {
        if (!Monitor.IsEntered(_layoutLock))
            throw new InvalidOperationException("A snapshot read lease is required.");

        if (GeometryRecoveryRequired)
            return new LayoutPlanCandidateEnumerator(ReadOnlySpan<LayoutPlanCandidate>.Empty);

        if (!_lazyLayoutEnabled ||
            _measuredTopLevelBlocks is null ||
            !CurrentPlacementGenerationNoLock().IsDocumentOrder)
        {
            return new LayoutPlanCandidateEnumerator(_layoutPlanCandidates);
        }

        List<int>? measuredRoots = _measuredLayoutPlanRootOrdinals;
        if (measuredRoots is null || measuredRoots.Count == 0)
            return new LayoutPlanCandidateEnumerator(
                _layoutPlanCandidates,
                ReadOnlySpan<int>.Empty);

        if (!_measuredLayoutPlanRootOrdinalsAreSorted)
            throw new InvalidOperationException(
                "Measured layout-plan roots must be ordered before they are published.");

        return new LayoutPlanCandidateEnumerator(
            _layoutPlanCandidates,
            CollectionsMarshal.AsSpan(measuredRoots));
    }

    private void AddMeasuredLayoutPlanRootOrdinalNoLock(int ordinal)
    {
        if (!TryFindLayoutPlanCandidateRange(_layoutPlanCandidates, ordinal, out _, out _))
            return;

        List<int>? measuredRoots = _measuredLayoutPlanRootOrdinals;
        if (measuredRoots is null)
        {
            measuredRoots = new List<int>();
            _measuredLayoutPlanRootOrdinals = measuredRoots;
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
        }

        // A root transitions from unmeasured to measured only once. The mask
        // check in EnsureMeasuredBand is the duplicate fence, so do not pay a
        // linear list membership scan while admitting a large measured band.
        if (measuredRoots.Count > 0 && ordinal < measuredRoots[^1])
            _measuredLayoutPlanRootOrdinalsAreSorted = false;
        measuredRoots.Add(ordinal);
    }

    private void RebuildMeasuredLayoutPlanRootOrdinalsNoLock()
    {
        bool[]? measuredMask = _measuredTopLevelBlocks;
        if (measuredMask is null || _layoutPlanCandidates.Length == 0)
        {
            _measuredLayoutPlanRootOrdinals = null;
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
            return;
        }

        List<int>? measuredRoots = null;
        int previousOrdinal = -1;
        foreach (LayoutPlanCandidate candidate in _layoutPlanCandidates)
        {
            int ordinal = candidate.TopLevelOrdinal;
            if (ordinal == previousOrdinal)
                continue;
            previousOrdinal = ordinal;
            if (ordinal >= 0 && ordinal < measuredMask.Length && measuredMask[ordinal])
                (measuredRoots ??= new List<int>()).Add(ordinal);
        }

        _measuredLayoutPlanRootOrdinals = measuredRoots;
        _measuredLayoutPlanRootOrdinalsAreSorted = true;
    }

    private void SortMeasuredLayoutPlanRootOrdinalsNoLock()
    {
        if (_measuredLayoutPlanRootOrdinals is not { Count: > 1 } measuredRoots ||
            _measuredLayoutPlanRootOrdinalsAreSorted)
        {
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
            return;
        }

        measuredRoots.Sort();
        _measuredLayoutPlanRootOrdinalsAreSorted = true;
    }

    private void RemoveUnmeasuredLayoutPlanRootOrdinalsNoLock()
    {
        if (_measuredLayoutPlanRootOrdinals is not { } measuredRoots ||
            _measuredTopLevelBlocks is not { } measuredMask)
        {
            return;
        }

        // Rollback is exceptional and may follow allocation/native failure.
        // Compact once after restoring the authoritative mask instead of
        // repeatedly removing individual roots from the middle of the list.
        int destination = 0;
        for (int source = 0; source < measuredRoots.Count; source++)
        {
            int ordinal = measuredRoots[source];
            if (ordinal >= 0 && ordinal < measuredMask.Length && measuredMask[ordinal])
                measuredRoots[destination++] = ordinal;
        }
        if (destination < measuredRoots.Count)
            measuredRoots.RemoveRange(destination, measuredRoots.Count - destination);
        if (measuredRoots.Count == 0)
        {
            _measuredLayoutPlanRootOrdinals = null;
            _measuredLayoutPlanRootOrdinalsAreSorted = true;
        }

        // Removing entries preserves relative order whether or not the list
        // had been sorted before the failed transaction.
    }

    private static bool TryFindLayoutPlanCandidateRange(
        ReadOnlySpan<LayoutPlanCandidate> candidates,
        int ordinal,
        out int start,
        out int end)
    {
        int low = 0;
        int high = candidates.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (candidates[middle].TopLevelOrdinal < ordinal)
                low = middle + 1;
            else
                high = middle;
        }

        start = low;
        high = candidates.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (candidates[middle].TopLevelOrdinal <= ordinal)
                low = middle + 1;
            else
                high = middle;
        }

        end = low;
        return start < end;
    }

    internal ref struct LayoutPlanCandidateEnumerator
    {
        private readonly ReadOnlySpan<LayoutPlanCandidate> _candidates;
        private readonly ReadOnlySpan<int> _measuredRootOrdinals;
        private readonly bool _filterByMeasuredRoot;
        private int _nextCandidateIndex;
        private int _candidateRangeEnd;
        private int _nextMeasuredRootIndex;

        internal LayoutPlanCandidateEnumerator(ReadOnlySpan<LayoutPlanCandidate> candidates)
        {
            _candidates = candidates;
            _measuredRootOrdinals = default;
            _filterByMeasuredRoot = false;
            _nextCandidateIndex = 0;
            _candidateRangeEnd = 0;
            _nextMeasuredRootIndex = 0;
            Current = default;
        }

        internal LayoutPlanCandidateEnumerator(
            ReadOnlySpan<LayoutPlanCandidate> candidates,
            ReadOnlySpan<int> measuredRootOrdinals)
        {
            _candidates = candidates;
            _measuredRootOrdinals = measuredRootOrdinals;
            _filterByMeasuredRoot = true;
            _nextCandidateIndex = 0;
            _candidateRangeEnd = 0;
            _nextMeasuredRootIndex = 0;
            Current = default;
        }

        internal LayoutPlanCandidate Current { get; private set; }

        internal bool MoveNext()
        {
            if (!_filterByMeasuredRoot)
            {
                if (_nextCandidateIndex >= _candidates.Length)
                    return false;

                Current = _candidates[_nextCandidateIndex++];
                return true;
            }

            while (true)
            {
                if (_nextCandidateIndex < _candidateRangeEnd)
                {
                    Current = _candidates[_nextCandidateIndex++];
                    return true;
                }

                if (_nextMeasuredRootIndex >= _measuredRootOrdinals.Length)
                    return false;

                LayoutSnapshot.TryFindLayoutPlanCandidateRange(
                    _candidates,
                    _measuredRootOrdinals[_nextMeasuredRootIndex++],
                    out _nextCandidateIndex,
                    out _candidateRangeEnd);
            }
        }
    }

    internal static bool IsInlineEmbedKeyboardFocusable(InlineEmbedRun embed)
    {
        ArgumentNullException.ThrowIfNull(embed);
        // Task markers are represented by inline embeds even when their host
        // deliberately exposes read-only document semantics. Do not add those
        // inert objects to Tab order; custom embeds without task metadata keep
        // the legacy assumption that their realized element is interactive.
        return embed.AutomationMetadata?.CanToggle != false;
    }

    internal readonly struct FocusablePlanEntry(
        FocusableItem Item,
        IHorizontalOverflowBox? HorizontalOverflow)
    {
        public readonly FocusableItem Item = Item;
        public readonly IHorizontalOverflowBox? HorizontalOverflow = HorizontalOverflow;
    }

}

internal readonly struct LazyLayoutCommit(
    bool Changed,
    int MeasuredBlocks,
    double OldHeight,
    double NewHeight,
    long LayoutRevision,
    bool NativeSceneResourcesChanged = false)
{
    internal readonly bool Changed = Changed;
    internal readonly int MeasuredBlocks = MeasuredBlocks;
    internal readonly double OldHeight = OldHeight;
    internal readonly double NewHeight = NewHeight;
    internal readonly long LayoutRevision = LayoutRevision;
    internal bool NativeSceneResourcesChanged { get; init; } = NativeSceneResourcesChanged;
    internal bool SceneGraphRebindRequired { get; init; }

    public static LazyLayoutCommit Unchanged(double height, long layoutRevision)
        => new(false, 0, height, height, layoutRevision);
}

internal readonly struct SnapshotScrollAnchor(
    int? BlockIndex,
    double OffsetFromTop,
    double Height)
{
    internal readonly int? BlockIndex = BlockIndex;
    internal readonly double OffsetFromTop = OffsetFromTop;
    internal readonly double Height = Height;
}

internal readonly struct SnapshotBlockScrollTarget(
    int BlockIndex,
    double TargetTop,
    double OwnerTop,
    double OwnerBottom,
    double OwnerHeight,
    bool OwnerMeasured,
    long LayoutRevision)
{
    internal readonly int BlockIndex = BlockIndex;
    internal readonly double TargetTop = TargetTop;
    internal readonly double OwnerTop = OwnerTop;
    internal readonly double OwnerBottom = OwnerBottom;
    internal readonly double OwnerHeight = OwnerHeight;
    internal readonly bool OwnerMeasured = OwnerMeasured;
    internal readonly long LayoutRevision = LayoutRevision;
}

internal enum SnapshotBlockScrollTargetState
{
    Ready,
    RequiresRealization,
    Busy,
    NotFound,
    Unavailable,
}

internal enum FocusTargetLayoutState
{
    Ready,
    RequiresRealization,
    Busy,
    Unavailable,
}
