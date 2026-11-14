using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Layout.Internal.PrivatePlacement;

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
    private readonly SnapshotSceneResidency _scenes;
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
        SnapshotSceneResidency scenes,
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
        _scenes = scenes;
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
        var build = new PrivateRasterCandidateBuild(cancellationToken);
        Exception? constructionFailure = null;
        try
        {
            lock (_geometry.Gate)
            {
                using var sourceScope = _geometry.PassCancellation.Push(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryAdmitSourceNoLock(build, changedBlockIndices, width) ||
                    !TryCaptureRootExtentsNoLock(build))
                    return null;

                build.Revision = _geometry.LayoutRevision;
                build.Context = build.SourceContext.CreateRasterPlacementContext(
                    imageCancellationToken, imagePreparationOwner,
                    retirementSink, imageUnavailable);
                constructionContext = build.Context;
                using var captureScope = build.Context.PassCancellation.Push(cancellationToken);

                if (!TryPreflightRootsNoLock(build) || !TryCloneRootsNoLock(build))
                    return null;
            }

            // The private tree lives in the candidate's own context, which no
            // other pass reaches, so its measure and snapshot construction
            // observe the token there.
            using var candidateScope = build.Context!.PassCancellation.Push(cancellationToken);

            // No source lock spans DirectWrite or semantic/index construction.
            cancellationToken.ThrowIfCancellationRequested();
            if (!SingleRasterLayoutCandidate.AreSourceProvidersCurrent(build.Sources!, build.Context!))
                return null;
            foreach (CodeBlockPlacementSourceFence code in build.CodeSources!)
                if (!code.Source.IsCurrentPrivatePlacement(code.Revision))
                    return null;
            float height = ArrangePrivateRoots(build, width, out float[]? candidateContinuations);
            cancellationToken.ThrowIfCancellationRequested();
            LayoutSnapshot snapshot = CreatePrivateSnapshot(build, width, height, candidateContinuations);
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new SingleRasterLayoutCandidate(snapshot, build.Context!, build.Sources!.ToArray(),
                build.Revision, width, build.HorizontalSources, build.CodeSources!.ToArray());
            build.Snapshot = null;
            build.Roots = null;
            return candidate;
        }
        catch (Exception exception)
        {
            constructionFailure = exception;
            throw;
        }
        finally
        {
            ReleaseUnpublished(build, constructionFailure);
        }
    }

    // Rejects sources whose shape, semantics, width or changed identities are
    // outside the bounded private-placement slice.
    private bool TryAdmitSourceNoLock(
        PrivateRasterCandidateBuild build,
        IReadOnlyCollection<int> changedBlockIndices,
        float width)
    {
        if (_retirement.IsRetiringOrDisposed || _recovery.IsRequired ||
            _scenes.VectorSceneResidencyPolicy is not null ||
            _geometry.Blocks.Count is 0 or > LayoutSnapshot.MaximumPrivateRasterPlacementRoots ||
            !PrivateRasterPlacementTree.TryGetFirstInlineContext(
                _geometry.Blocks, out MarkdownLayoutContext? sourceContext) || sourceContext is null ||
            !ReferenceEquals(_source.SourceMap, sourceContext.SourceMap) ||
            _source.SemanticDocument.Text.Length > LayoutSnapshot.MaximumPrivateRasterPlacementSemanticTextLength ||
            changedBlockIndices.Count is 0 or > LayoutSnapshot.MaximumPrivateRasterPlacementRoots ||
            !float.IsFinite(width) || width != _geometry.AvailableWidth ||
            _geometry.LayoutRevision == long.MaxValue)
            return false;
        build.SourceContext = sourceContext;

        var changedOrdinals = new HashSet<int>();
        foreach (int blockIndex in changedBlockIndices)
        {
            build.CancellationToken.ThrowIfCancellationRequested();
            if (!_geometry.TopLevelOrdinalByBlockIndex.TryGetValue(blockIndex, out int ordinal))
                return false;
            changedOrdinals.Add(ordinal);
        }
        build.ChangedOrdinals = changedOrdinals;
        return true;
    }

    // Copies the measured-root mask and, for lazy snapshots, every root's
    // current extent as scalars.
    private bool TryCaptureRootExtentsNoLock(PrivateRasterCandidateBuild build)
    {
        bool lazyLayoutEnabled = _geometry.LazyLayoutEnabled;
        build.LazyLayoutEnabled = lazyLayoutEnabled;
        if (lazyLayoutEnabled)
        {
            if (_geometry.MeasuredTopLevelBlocks is not { } sourceMeasuredMask ||
                sourceMeasuredMask.Length != _geometry.Blocks.Count)
                return false;

            bool[] measuredRootMask = (bool[])sourceMeasuredMask.Clone();
            build.MeasuredRootMask = measuredRootMask;
            for (int ordinal = 0; ordinal < measuredRootMask.Length; ordinal++)
            {
                if (measuredRootMask[ordinal])
                    build.CapturedMeasuredRootCount++;
            }
            if (build.CapturedMeasuredRootCount != _geometry.MeasuredTopLevelBlockCount)
                return false;
        }
        else
        {
            if (_geometry.MeasuredTopLevelBlocks is not null || _geometry.MeasuredTopLevelBlockCount != 0)
                return false;
            build.CapturedMeasuredRootCount = _geometry.Blocks.Count;
        }

        // A lazy root's current extent is already the authoritative
        // estimate used by its viewport index. Copy only scalar values
        // before releasing the source gate; never estimate or measure a
        // source root in the private build phase.
        if (lazyLayoutEnabled)
        {
            var capturedRootHeights = new float[_geometry.Blocks.Count];
            build.CapturedRootHeights = capturedRootHeights;
            for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
            {
                build.CancellationToken.ThrowIfCancellationRequested();
                double sourceHeight = _geometry.Blocks[ordinal].Bounds.Height;
                if (!double.IsFinite(sourceHeight) || sourceHeight < 0 || sourceHeight > float.MaxValue)
                    return false;
                float capturedHeight = (float)sourceHeight;
                if (!float.IsFinite(capturedHeight))
                    return false;
                capturedRootHeights[ordinal] = capturedHeight;
            }
            build.CapturedSharedLayoutMetrics = _geometry.SharedLayoutMetrics;
        }
        return true;
    }

    // Preflight the complete bounded shape before acquiring the
    // first clone lease. Never share an unchanged mutable sibling
    // under independently owned snapshot locks/retirement lifetimes.
    private bool TryPreflightRootsNoLock(PrivateRasterCandidateBuild build)
    {
        bool lazyLayoutEnabled = build.LazyLayoutEnabled;
        float capturedContentWidth = _geometry.GetContentWidthNoLock();
        build.CapturedContentWidth = capturedContentWidth;
        var capture = new PrivateRasterPlacementTree.CaptureState(
            allowUnmeasuredTextOnly: lazyLayoutEnabled, allowIdleImagePlacements: true,
            allowCodePlacements: true);
        bool changedRaster = false;
        for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
        {
            build.CancellationToken.ThrowIfCancellationRequested();
            if (!capture.TryAddRoot(_geometry.Blocks[ordinal], build.SourceContext, build.Context!,
                    capturedContentWidth, out PrivateRasterPlacementTree.RootCounts counts))
                return false;
            // Only never-started, source-free descriptors may cross
            // from estimated roots. Completed or active image state in
            // an unmeasured root still uses the ordinary relayout path.
            if (lazyLayoutEnabled && !build.MeasuredRootMask![ordinal] &&
                counts.ImageCount != counts.IdleImageCount)
                return false;
            changedRaster |= build.ChangedOrdinals.Contains(ordinal) && counts.ImageCount > 0;
        }
        build.ImageCount = capture.ImageCount;
        return changedRaster && build.ImageCount != 0;
    }

    // Clones every root, including unchanged siblings, and captures their
    // raster and code-block source fences.
    private bool TryCloneRootsNoLock(PrivateRasterCandidateBuild build)
    {
        bool lazyLayoutEnabled = build.LazyLayoutEnabled;
        var horizontalSources = new HorizontalPlacementSourceFence[_horizontalOverflow.Boxes.Count];
        build.HorizontalSources = horizontalSources;
        int horizontalIndex = 0;
        foreach (IHorizontalOverflowBox overflow in _horizontalOverflow.Boxes)
            horizontalSources[horizontalIndex++] = new(overflow, overflow.HorizontalOffset);
        build.TextProjectionReuse = MarkdownSemanticTextProjectionReuse.Capture(_source);
        var roots = new List<BlockBox>(_geometry.Blocks.Count);
        build.Roots = roots;
        var sources = new List<RasterPlacementSourceFence>(build.ImageCount);
        build.Sources = sources;
        var codeSources = new List<CodeBlockPlacementSourceFence>();
        build.CodeSources = codeSources;
        bool changedCompletedImage = false;
        for (int ordinal = 0; ordinal < _geometry.Blocks.Count; ordinal++)
        {
            BlockBox block = _geometry.Blocks[ordinal];
            int codeStart = codeSources.Count;
            build.CancellationToken.ThrowIfCancellationRequested();
            if (!PrivateRasterPlacementTree.TryCreateClone(
                    block, build.SourceContext, build.Context!, build.CapturedContentWidth, out BlockBox? root,
                    out RasterPlacementSourceFence[]? rootSources,
                    allowUnmeasuredTextOnly: lazyLayoutEnabled,
                    allowIdleImagePlacements: true, codeSources: codeSources) || root is null)
                return false;
            // The preallocated root list becomes the sole cleanup
            // owner before any later per-root fence processing.
            roots.Add(root);
            if (rootSources is null)
                throw new InvalidOperationException("A private root omitted its raster source fences.");
            bool wasSubscribed = !lazyLayoutEnabled || build.MeasuredRootMask![ordinal];
            for (int codeIndex = codeStart; codeIndex < codeSources.Count; codeIndex++)
                codeSources[codeIndex] = codeSources[codeIndex] with { WasMeasured = wasSubscribed };
            foreach (RasterPlacementSourceFence source in rootSources)
            {
                changedCompletedImage |= build.ChangedOrdinals.Contains(ordinal) && !source.IsIdleDescriptor;
                sources.Add(source with { WasSubscribed = wasSubscribed });
            }
        }
        if (sources.Count != build.ImageCount)
            throw new InvalidOperationException("The private document omitted a raster source fence.");
        return changedCompletedImage;
    }

    // Measures roots the source had measured and places estimated roots at
    // their captured extents. Returns the flow height before bottom padding.
    private float ArrangePrivateRoots(
        PrivateRasterCandidateBuild build,
        float width,
        out float[]? candidateContinuations)
    {
        List<BlockBox> roots = build.Roots!;
        float contentWidth = (float)Math.Max(1, width - _geometry.DocumentPadding.Left - _geometry.DocumentPadding.Right);
        float height = (float)_geometry.DocumentPadding.Top;
        candidateContinuations = build.LazyLayoutEnabled ? new float[roots.Count] : null;
        for (int ordinal = 0; ordinal < roots.Count; ordinal++)
        {
            build.CancellationToken.ThrowIfCancellationRequested();
            BlockBox root = roots[ordinal];
            float rootHeight;
            if (!build.LazyLayoutEnabled || build.MeasuredRootMask![ordinal])
            {
                rootHeight = root.Measure(contentWidth);
                root.Arrange((float)_geometry.DocumentPadding.Left, height, contentWidth);
            }
            else
            {
                rootHeight = build.CapturedRootHeights![ordinal];
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
        return height;
    }

    private LayoutSnapshot CreatePrivateSnapshot(
        PrivateRasterCandidateBuild build,
        float width,
        float height,
        float[]? candidateContinuations)
    {
        List<BlockBox> roots = build.Roots!;
        bool lazyLayoutEnabled = build.LazyLayoutEnabled;
        long revision = build.Revision;
        var snapshot = new LayoutSnapshot(roots.ToArray(), _source.SourceMap, width,
            height + (float)_geometry.DocumentPadding.Bottom,
            _footnoteDefBlocks, _footnoteRefBlocks, _fragmentTargetBlocks,
            documentPadding: _geometry.DocumentPadding, blockSpacing: _geometry.BlockSpacing,
            sharedLayoutMetrics: build.CapturedSharedLayoutMetrics,
            vectorSceneResidencyPolicy: null,
            textProjectionReuse: build.TextProjectionReuse,
            generatedBlockIndexUpperBound: _generatedBlockIndexUpperBound,
            layoutContext: build.Context);
        build.Snapshot = snapshot;
        if (lazyLayoutEnabled)
        {
            snapshot.Geometry.LazyLayoutEnabled = true;
            snapshot.Geometry.MeasuredTopLevelBlocks = build.MeasuredRootMask;
            snapshot.Geometry.MeasuredTopLevelBlockCount = build.CapturedMeasuredRootCount;
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
            lazyLayoutEnabled ? build.CapturedMeasuredRootCount : roots.Count,
            candidatePlacements.WithRevision(revision + 1)));
        if (_source.AutomationDocumentGeneration > 0)
            snapshot.AssignAutomationDocumentGeneration(_source.AutomationDocumentGeneration);
        if (!snapshot.SourceFingerprint.Equals(_source.SourceFingerprint) ||
            !snapshot.SemanticDocument.TextProjectionFingerprint.Equals(_source.SemanticDocument.TextProjectionFingerprint) ||
            !snapshot.SemanticDocument.SemanticRangeFingerprint.Equals(_source.SemanticDocument.SemanticRangeFingerprint))
            throw new InvalidOperationException("A raster placement candidate changed logical document semantics.");
        return snapshot;
    }

    private static void ReleaseUnpublished(PrivateRasterCandidateBuild build, Exception? constructionFailure)
    {
        // The caller joins this context's ledger after the scheduled native
        // operation exits, including failures before snapshot construction.
        if (build.Snapshot is { } snapshot)
            build.Context!.RegisterRetirement(snapshot.Retire());
        else if (build.Roots is { } roots)
        {
            Exception? cleanupFailure = null;
            foreach (BlockBox root in roots)
            {
                try { build.Context!.DiscardConstructionBlock(root); }
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
