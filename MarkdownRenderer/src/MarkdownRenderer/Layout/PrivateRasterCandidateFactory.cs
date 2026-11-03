using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds a private, bounded copy-on-write placement tree of a snapshot for
/// completed raster and SVG images, so the replacement snapshot can be measured
/// without holding the source layout gate across DirectWrite work.
/// </summary>
internal sealed class PrivateRasterCandidateFactory
{
    private readonly LayoutSnapshot _source;
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly SnapshotRetirement _retirement;
    private readonly SnapshotBandRealization _bands;
    private readonly SnapshotHorizontalOverflowIndex _horizontalOverflow;
    private readonly IReadOnlyDictionary<int, int> _footnoteDefBlocks;
    private readonly IReadOnlyDictionary<int, int> _footnoteRefBlocks;
    private readonly IReadOnlyDictionary<string, int> _fragmentTargetBlocks;
    private readonly int _generatedBlockIndexUpperBound;

    internal PrivateRasterCandidateFactory(
        LayoutSnapshot source,
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        SnapshotRetirement retirement,
        SnapshotBandRealization bands,
        SnapshotHorizontalOverflowIndex horizontalOverflow,
        IReadOnlyDictionary<int, int> footnoteDefBlocks,
        IReadOnlyDictionary<int, int> footnoteRefBlocks,
        IReadOnlyDictionary<string, int> fragmentTargetBlocks,
        int generatedBlockIndexUpperBound)
    {
        _source = source;
        _geometry = geometry;
        _recovery = recovery;
        _retirement = retirement;
        _bands = bands;
        _horizontalOverflow = horizontalOverflow;
        _footnoteDefBlocks = footnoteDefBlocks;
        _footnoteRefBlocks = footnoteRefBlocks;
        _fragmentTargetBlocks = fragmentTargetBlocks;
        _generatedBlockIndexUpperBound = generatedBlockIndexUpperBound;
    }

    /// <summary>Generated block-index bound captured from the construction context.</summary>
    internal int GeneratedBlockIndexUpperBound => _generatedBlockIndexUpperBound;

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
            lock (_geometry.Gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_retirement.IsRetiringOrDisposed || _recovery.IsRequired ||
                    _bands.VectorSceneResidencyPolicy is not null ||
                    _geometry.Blocks.Count is 0 or > LayoutSnapshot.MaximumPrivateRasterPlacementRoots ||
                    !PrivateRasterPlacementTree.TryGetFirstInlineContext(
                        _geometry.Blocks, out MarkdownLayoutContext? sourceContext) || sourceContext is null ||
                    !ReferenceEquals(_source.SourceMap, sourceContext.SourceMap) ||
                    _source.SemanticDocument.Text.Length > LayoutSnapshot.MaximumPrivateRasterPlacementSemanticTextLength ||
                    changedBlockIndices.Count is 0 or > LayoutSnapshot.MaximumPrivateRasterPlacementRoots ||
                    !float.IsFinite(width) || width != _geometry.AvailableWidth ||
                    _geometry.LayoutRevision == long.MaxValue)
                    return null;

                var changedOrdinals = new HashSet<int>();
                foreach (int blockIndex in changedBlockIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
                        return null;
                    changedOrdinals.Add(ordinal);
                }

                lazyLayoutEnabled = _geometry.LazyLayoutEnabled;
                if (lazyLayoutEnabled)
                {
                    if (_geometry.MeasuredTopLevelBlocks is not { } sourceMeasuredMask ||
                        sourceMeasuredMask.Length != _geometry.Blocks.Count)
                        return null;

                    measuredRootMask = (bool[])sourceMeasuredMask.Clone();
                    for (int ordinal = 0; ordinal < measuredRootMask.Length; ordinal++)
                    {
                        if (measuredRootMask[ordinal])
                            capturedMeasuredRootCount++;
                    }
                    if (capturedMeasuredRootCount != _geometry.MeasuredTopLevelBlockCount)
                        return null;
                }
                else
                {
                    if (_geometry.MeasuredTopLevelBlocks is not null || _geometry.MeasuredTopLevelBlockCount != 0)
                        return null;
                    capturedMeasuredRootCount = _geometry.Blocks.Count;
                }

                // A lazy root's current extent is already the authoritative
                // estimate used by its viewport index. Copy only scalar values
                // before releasing the source gate; never estimate or measure a
                // source root in the private build phase.
                if (lazyLayoutEnabled)
                {
                    capturedRootHeights = new float[_geometry.Blocks.Count];
                    for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double sourceHeight = _geometry.Blocks[ordinal].Bounds.Height;
                        if (!double.IsFinite(sourceHeight) || sourceHeight < 0 || sourceHeight > float.MaxValue)
                            return null;
                        float capturedHeight = (float)sourceHeight;
                        if (!float.IsFinite(capturedHeight))
                            return null;
                        capturedRootHeights[ordinal] = capturedHeight;
                    }
                    capturedSharedLayoutMetrics = _geometry.SharedLayoutMetrics;
                }

                revision = _geometry.LayoutRevision;
                context = sourceContext.CreateRasterPlacementContext(
                    imageCancellationToken, imagePreparationOwner,
                    retirementSink, imageUnavailable);
                constructionContext = context;

                // Preflight the complete bounded shape before acquiring the
                // first clone lease. Never share an unchanged mutable sibling
                // under independently owned snapshot locks/retirement lifetimes.
                float capturedContentWidth = _geometry.GetContentWidthNoLock();
                var capture = new PrivateRasterPlacementTree.CaptureState(
                    allowUnmeasuredTextOnly: lazyLayoutEnabled, allowIdleImagePlacements: true,
                    allowCodePlacements: true);
                bool changedRaster = false;
                for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!capture.TryAddRoot(_geometry.Blocks[ordinal], sourceContext, context,
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

                horizontalSources = new HorizontalPlacementSourceFence[_horizontalOverflow.Boxes.Count];
                int horizontalIndex = 0;
                foreach (IHorizontalOverflowBox overflow in _horizontalOverflow.Boxes)
                    horizontalSources[horizontalIndex++] = new(overflow, overflow.HorizontalOffset);
                textProjectionReuse = MarkdownSemanticTextProjectionReuse.Capture(_source);
                roots = new List<BlockBox>(_geometry.Blocks.Count);
                sources = new List<RasterPlacementSourceFence>(imageCount);
                codeSources = new List<CodeBlockPlacementSourceFence>();
                bool changedCompletedImage = false;
                for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
                {
                    BlockBox block = _geometry.Blocks[ordinal];
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
            float contentWidth = (float)Math.Max(1, width - _geometry.DocumentPadding.Left - _geometry.DocumentPadding.Right);
            float height = (float)_geometry.DocumentPadding.Top;
            float[]? candidateContinuations = lazyLayoutEnabled ? new float[roots!.Count] : null;
            for (int ordinal = 0; ordinal < roots!.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockBox root = roots[ordinal];
                float rootHeight;
                if (!lazyLayoutEnabled || measuredRootMask![ordinal])
                {
                    rootHeight = root.Measure(contentWidth);
                    root.Arrange((float)_geometry.DocumentPadding.Left, height, contentWidth);
                }
                else
                {
                    rootHeight = capturedRootHeights![ordinal];
                    root.ArrangeEstimated(
                        (float)_geometry.DocumentPadding.Left,
                        height,
                        contentWidth,
                        rootHeight);
                }
                height += rootHeight;
                if (ordinal < roots.Count - 1 &&
                    !StackBox.SharesSafeHtmlInlineFlow(root, roots[ordinal + 1]))
                    height += _geometry.BlockSpacing;
                if (candidateContinuations is not null)
                    candidateContinuations[ordinal] = height;
            }
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = new LayoutSnapshot(roots.ToArray(), _source.SourceMap, width,
                height + (float)_geometry.DocumentPadding.Bottom,
                _footnoteDefBlocks, _footnoteRefBlocks, _fragmentTargetBlocks,
                documentPadding: _geometry.DocumentPadding, blockSpacing: _geometry.BlockSpacing,
                sharedLayoutMetrics: capturedSharedLayoutMetrics,
                vectorSceneResidencyPolicy: null,
                textProjectionReuse: textProjectionReuse,
                generatedBlockIndexUpperBound: _generatedBlockIndexUpperBound);
            if (lazyLayoutEnabled)
            {
                snapshot.Geometry.LazyLayoutEnabled = true;
                snapshot.Geometry.MeasuredTopLevelBlocks = measuredRootMask;
                snapshot.Geometry.MeasuredTopLevelBlockCount = capturedMeasuredRootCount;
                snapshot.PlanCandidates.RebuildMeasuredLayoutPlanRootOrdinalsNoLock();
                snapshot.Geometry.RefreshViewportIndexNoLock();
            }
            snapshot.Geometry.LayoutRevision = revision + 1;
            snapshot.Geometry.LazyReflowContinuations = candidateContinuations;
            snapshot.Geometry.LazyReflowContinuationWidth = width;
            snapshot.Geometry.LazyReflowContinuationRevision = lazyLayoutEnabled ? revision + 1 : null;
            LayoutPlacementGeneration candidatePlacements = snapshot.Geometry.CurrentPlacementGenerationNoLock();
            snapshot.Geometry.PublishInitialMetadataNoLock(new LayoutSnapshotCommittedMetadata(
                snapshot.Geometry.Size,
                revision + 1,
                lazyLayoutEnabled ? capturedMeasuredRootCount : roots.Count,
                candidatePlacements.WithRevision(revision + 1)));
            if (_source.AutomationDocumentGeneration > 0)
                snapshot.AssignAutomationDocumentGeneration(_source.AutomationDocumentGeneration);
            if (!snapshot.SourceFingerprint.Equals(_source.SourceFingerprint) ||
                !snapshot.SemanticDocument.TextProjectionFingerprint.Equals(_source.SemanticDocument.TextProjectionFingerprint) ||
                !snapshot.SemanticDocument.SemanticRangeFingerprint.Equals(_source.SemanticDocument.SemanticRangeFingerprint))
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
}
