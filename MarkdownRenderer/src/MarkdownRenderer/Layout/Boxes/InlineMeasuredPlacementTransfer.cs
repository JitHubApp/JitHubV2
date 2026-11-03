using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Captures a source inline box and its private measured clone so placement
/// can be moved without replacing the source box, runs, source-map entries,
/// or image event subscribers. The clone owns the retired native layouts
/// after the no-throw swap and is retired only after the snapshot gate is
/// released.
/// </summary>
internal sealed class InlineMeasuredPlacementTransfer
{
    private readonly struct PreparedVectorPlacement
    {
        internal PreparedVectorPlacement(
            InlineVectorSceneRun source,
            InlineVectorSceneRun prepared)
        {
            Source = source;
            Prepared = prepared;
            Width = prepared.DesiredWidth;
            Height = prepared.DesiredHeight;
            Baseline = prepared.DesiredBaseline;
            LocalBounds = prepared.LocalBounds;
        }

        internal readonly InlineVectorSceneRun Source;
        internal readonly InlineVectorSceneRun Prepared;
        internal readonly float Width;
        internal readonly float Height;
        internal readonly float Baseline;
        internal readonly Rect LocalBounds;
    }

    private readonly InlineContainerBox _source;
    private readonly InlineContainerBox _prepared;
    private readonly float _expectedAvailableWidth;
    private readonly bool _isTableCellTransfer;
    private readonly bool _isContainerLeafTransfer;
    private readonly bool _allowMeasuredVectorPlacement;
    private readonly int _vectorRunCount;
    private readonly Rect _sourceBounds;
    private readonly Thickness _sourceRawMargin;
    private readonly Thickness _preparedRawMargin;
    private readonly CanvasTextLayout _sourceLayout;
    private readonly NativeResourceRetirementRecord _sourceLayoutRetirementRecord;
    private readonly ScriptBaselineGeometry _sourceScriptBaselineGeometry;
    private readonly CanvasTextLayout? _sourceSelectionLayout;
    private readonly NativeResourceRetirementRecord? _sourceSelectionLayoutRetirementRecord;
    private readonly CanvasLineMetrics[]? _sourceLineMetrics;
    private readonly bool _sourceLineMetricsRead;
    private readonly Color _sourceSelectionColor;
    private readonly float _sourceSelectionWidth;
    private readonly float _sourceLastWidth;
    private readonly bool _sourceBufferDirty;
    private readonly string _sourceBuffer;
    private readonly int _sourceRunTextLength;
    private readonly long _sourceSceneGeneration;
    private readonly long _sourcePendingSceneGeneration;
    private readonly InlineSceneGeometry[] _sourcePendingSceneGeometry;
    private readonly object? _sourceVectorSceneState;
    private readonly object? _preparedVectorSceneState;
    private readonly bool _sourceProgressiveSceneResidency;
    private InlineSceneGeometry[]? _preparedVectorGeometrySnapshot;
    private InlineSceneGeometry[]? _preparedVectorGeometryForSource;
    private readonly PreparedVectorPlacement[] _preparedVectorPlacements;
    private long _preparedSceneGeometryGeneration;
    private long _preparedPendingSceneGeometryGeneration;
    private bool _preparedVectorGeometryReady;
    private readonly InlineMeasuredRunState[] _runs;
    private readonly IReadOnlyList<string> _containerContextKeys;
    private readonly IReadOnlyList<string> _containerAliasKeys;
    private readonly IReadOnlyList<string>[] _effectiveRunAliases;
    private readonly ElementStyle?[] _sourceResolvedRunStyles;
    private readonly ElementStyle _resolvedContainerStyle;

    internal InlineMeasuredPlacementTransfer(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        InlineMeasuredRunState[] runs,
        IReadOnlyList<string> containerContextKeys,
        IReadOnlyList<string> containerAliasKeys,
        IReadOnlyList<string>[] effectiveRunAliases,
        ElementStyle?[] sourceResolvedRunStyles,
        bool isTableCellTransfer = false,
        bool isContainerLeafTransfer = false,
        bool allowMeasuredVectorPlacement = false)
    {
        _source = source;
        _prepared = prepared;
        _expectedAvailableWidth = expectedAvailableWidth;
        _isTableCellTransfer = isTableCellTransfer;
        _isContainerLeafTransfer = isContainerLeafTransfer;
        _allowMeasuredVectorPlacement = allowMeasuredVectorPlacement;
        int vectorRunCount = 0;
        foreach (InlineMeasuredRunState run in runs)
            if (run.SourceVectorRun is not null)
                vectorRunCount++;
        _vectorRunCount = vectorRunCount;
        _preparedVectorPlacements = new PreparedVectorPlacement[vectorRunCount];
        _sourceBounds = source.Bounds;
        _sourceRawMargin = source.RawPlacementMargin;
        _preparedRawMargin = prepared.RawPlacementMargin;
        _sourceLayout = source.TextLayout!;
        _sourceLayoutRetirementRecord = source.TextLayoutRetirementRecord!;
        _sourceScriptBaselineGeometry = source.ScriptBaselineGeometry;
        _sourceSelectionLayout = source.SelectionLayout;
        _sourceSelectionLayoutRetirementRecord = source.SelectionLayoutRetirementRecord;
        _sourceLineMetrics = source.CachedLineMetrics;
        _sourceLineMetricsRead = source.HasReadLineMetrics;
        _sourceSelectionColor = source.SelectionLayoutColor;
        _sourceSelectionWidth = source.SelectionLayoutWidth;
        _sourceLastWidth = source.LastMeasuredWidth;
        _sourceBufferDirty = source.IsBufferDirty;
        _sourceBuffer = source.LayoutBuffer;
        _sourceRunTextLength = source.RunTextLength;
        _sourceSceneGeneration = source.CurrentSceneGeometryGeneration;
        _sourcePendingSceneGeneration = source.PendingSceneGeometryGeneration;
        _sourcePendingSceneGeometry = source.PendingSceneGeometry;
        _sourceVectorSceneState = source.VectorSceneStateHolder;
        _preparedVectorSceneState = prepared.VectorSceneStateHolder;
        _sourceProgressiveSceneResidency = source.UsesProgressiveNativeSceneResidency;
        _runs = runs;
        // These arrays belong to the still-private candidate. Measurement
        // does not mutate them, so borrowing them avoids a second copy of
        // style-key payload during the final-fence capture.
        _containerContextKeys = containerContextKeys;
        _containerAliasKeys = containerAliasKeys;
        _effectiveRunAliases = effectiveRunAliases;
        _sourceResolvedRunStyles = sourceResolvedRunStyles;
        _resolvedContainerStyle = source.ResolvedContainerStyle!;
    }

    internal InlineContainerBox Source => _source;

    internal InlineContainerBox Prepared => _prepared;

    /// <summary>
    /// Returns the measured candidate cell width after a table candidate
    /// has completed Measure and Arrange. The table publisher first calls
    /// <see cref="IsCurrentAndPrepared"/> to validate this scalar.
    /// </summary>
    internal float PreparedAvailableWidth => _prepared.LastMeasuredWidth;

    /// <summary>
    /// Builds a bounded source-run remap for candidate vector geometry.
    /// Call after the private candidate has completed Measure/Arrange and
    /// before entering the snapshot's final publication gate. This is the
    /// only allocation in the vector-geometry transfer; final validation
    /// and the swap use the captured arrays and managed references only.
    /// </summary>
    internal bool TryPrepareMeasuredVectorGeometry()
    {
        if (_vectorRunCount == 0)
            return true;
        if (_preparedVectorGeometryReady)
            return MatchesPreparedVectorGeometry();

        int vectorCount = 0;
        foreach (InlineMeasuredRunState run in _runs)
            if (run.SourceVectorRun is not null)
                vectorCount++;

        InlineContainerBox prepared = _prepared;
        if (vectorCount != _source.VectorSceneRunCount ||
            vectorCount != prepared.VectorSceneRunCount ||
            (vectorCount != 0 && !_allowMeasuredVectorPlacement) ||
            prepared.IsDirty || prepared.TextLayout is null || prepared.IsBufferDirty ||
            !InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(prepared))
        {
            return false;
        }

        InlineSceneGeometry[] candidateGeometry = prepared.PendingSceneGeometry;
        InlineSceneGeometry[] remappedGeometry = Array.Empty<InlineSceneGeometry>();
        int placementOrdinal = 0;
        foreach (InlineMeasuredRunState run in _runs)
        {
            if (run.SourceVectorRun is not { } sourceVector ||
                run.PreparedVectorRun is not { } preparedVector)
            {
                continue;
            }
            if (placementOrdinal >= _preparedVectorPlacements.Length ||
                !run.MatchesSourceAndPreparedVectorState(_allowMeasuredVectorPlacement) ||
                !InlineMeasuredPlacementCapture.IsFiniteNonnegative(preparedVector.LocalBounds))
            {
                return false;
            }
            _preparedVectorPlacements[placementOrdinal++] = new PreparedVectorPlacement(
                sourceVector, preparedVector);
        }
        if (placementOrdinal != vectorCount)
            return false;

        if (vectorCount > 0 && prepared.UsesProgressiveNativeSceneResidency)
        {
            if (candidateGeometry.Length != vectorCount ||
                prepared.PendingSceneGeometryGeneration == 0 ||
                prepared.PendingSceneGeometryGeneration != prepared.CurrentSceneGeometryGeneration)
            {
                return false;
            }

            remappedGeometry = new InlineSceneGeometry[vectorCount];
            int geometryOrdinal = 0;
            foreach (InlineMeasuredRunState run in _runs)
            {
                if (run.SourceVectorRun is not { } sourceVector)
                    continue;
                if (run.PreparedVectorRun is not { IsMeasuredPlacementOnly: true } preparedVector ||
                    geometryOrdinal >= candidateGeometry.Length)
                {
                    return false;
                }

                InlineSceneGeometry measuredGeometry = candidateGeometry[geometryOrdinal];
                if (!ReferenceEquals(measuredGeometry.Run, preparedVector) ||
                    !ReferenceEquals(measuredGeometry.Run.Scene, run.VectorScene) ||
                    !InlineMeasuredPlacementCapture.HasUsableMeasuredVectorRegions(measuredGeometry))
                {
                    return false;
                }

                remappedGeometry[geometryOrdinal] = measuredGeometry.WithRun(sourceVector);
                geometryOrdinal++;
            }

            if (geometryOrdinal != vectorCount)
                return false;
        }
        else if (candidateGeometry.Length != 0 || prepared.PendingSceneGeometryGeneration != 0 &&
                 !prepared.UsesProgressiveNativeSceneResidency)
        {
            return false;
        }

        _preparedVectorGeometrySnapshot = candidateGeometry;
        _preparedVectorGeometryForSource = remappedGeometry;
        _preparedSceneGeometryGeneration = prepared.CurrentSceneGeometryGeneration;
        _preparedPendingSceneGeometryGeneration = prepared.PendingSceneGeometryGeneration;
        _preparedVectorGeometryReady = true;
        return MatchesPreparedVectorGeometry();
    }

    private bool MatchesPreparedVectorGeometry()
    {
        if (!ReferenceEquals(_prepared.VectorSceneStateHolder, _preparedVectorSceneState))
            return false;
        if (_vectorRunCount == 0)
            return true;
        if (!_preparedVectorGeometryReady ||
            !ReferenceEquals(_prepared.PendingSceneGeometry, _preparedVectorGeometrySnapshot) ||
            _prepared.CurrentSceneGeometryGeneration != _preparedSceneGeometryGeneration ||
            _prepared.PendingSceneGeometryGeneration != _preparedPendingSceneGeometryGeneration)
        {
            return false;
        }

        int vectorCount = 0;
        foreach (InlineMeasuredRunState run in _runs)
            if (run.SourceVectorRun is not null)
                vectorCount++;

        if (!_allowMeasuredVectorPlacement && vectorCount != 0)
            return false;
        if (!MatchesPreparedVectorPlacements(vectorCount))
            return false;
        if (_prepared.UsesProgressiveNativeSceneResidency && vectorCount != 0)
        {
            if (_preparedVectorGeometrySnapshot is null ||
                _preparedVectorGeometrySnapshot.Length != vectorCount ||
                _preparedVectorGeometryForSource is null ||
                _preparedVectorGeometryForSource.Length != vectorCount)
            {
                return false;
            }

            int geometryOrdinal = 0;
            foreach (InlineMeasuredRunState run in _runs)
            {
                if (run.SourceVectorRun is not { } sourceVector ||
                    run.PreparedVectorRun is not { } preparedVector)
                {
                    continue;
                }

                InlineSceneGeometry candidateGeometry = _preparedVectorGeometrySnapshot[geometryOrdinal];
                InlineSceneGeometry sourceGeometry = _preparedVectorGeometryForSource[geometryOrdinal];
                if (!ReferenceEquals(candidateGeometry.Run, preparedVector) ||
                    !ReferenceEquals(sourceGeometry.Run, sourceVector) ||
                    !candidateGeometry.HasSameRegions(in sourceGeometry))
                {
                    return false;
                }
                // WithRun value-copies the scalar region fields and shares
                // inaccessible immutable Rect[] storage. This exact
                // identity/value check stays constant-time under the gate.
                geometryOrdinal++;
            }

            return geometryOrdinal == vectorCount;
        }

        return _preparedVectorGeometryForSource is { Length: 0 } &&
            (_preparedVectorGeometrySnapshot?.Length ?? 0) == 0;
    }

    private bool MatchesPreparedVectorPlacements(int vectorCount)
    {
        if (_preparedVectorPlacements.Length != vectorCount)
            return false;

        for (int index = 0; index < vectorCount; index++)
        {
            PreparedVectorPlacement expected = _preparedVectorPlacements[index];
            InlineVectorSceneRun source = expected.Source;
            InlineVectorSceneRun prepared = expected.Prepared;
            if (prepared.IsDisposed || !prepared.IsMeasuredPlacementOnly ||
                source.IsDisposed || source.IsMeasuredPlacementOnly ||
                !ReferenceEquals(prepared.Scene, source.Scene) ||
                prepared.DesiredWidth != expected.Width ||
                prepared.DesiredHeight != expected.Height ||
                prepared.DesiredBaseline != expected.Baseline ||
                !InlineMeasuredPlacementCapture.SameRect(prepared.LocalBounds, expected.LocalBounds) ||
                !InlineMeasuredPlacementCapture.IsFiniteNonnegative(prepared.LocalBounds))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Revalidates immutable run/source identity and the captured source
    /// layout state after private measurement. The caller holds the owning
    /// snapshot's exclusive layout gate. This performs managed identity and
    /// scalar checks only; it makes no native or host calls.
    /// </summary>
    internal bool IsCurrentAndPrepared()
    {
        InlineContainerBox source = _source;
        InlineContainerBox prepared = _prepared;
        bool compatibleRootInputs = _isTableCellTransfer
            ? InlineMeasuredPlacementCapture.AreCompatibleTableCellRootInputs(source, prepared, _allowMeasuredVectorPlacement)
            : _isContainerLeafTransfer
            ? InlineMeasuredPlacementCapture.AreCompatibleContainerLeafRootInputs(source, prepared, _allowMeasuredVectorPlacement)
            : InlineMeasuredPlacementCapture.AreCompatibleMeasuredRootInputs(
                source, prepared, _expectedAvailableWidth, _allowMeasuredVectorPlacement);
        bool preparedWidthMatches = _isTableCellTransfer
            ? float.IsFinite(prepared.LastMeasuredWidth) && prepared.LastMeasuredWidth > 0f &&
                prepared.Bounds.Width == prepared.LastMeasuredWidth
            : prepared.LastMeasuredWidth == _expectedAvailableWidth &&
                prepared.Bounds.Width == _expectedAvailableWidth;
        bool sourceWidthMatches = !_isTableCellTransfer && !_isContainerLeafTransfer ||
            (source.LastMeasuredWidth == _expectedAvailableWidth &&
             source.Bounds.Width == _expectedAvailableWidth);
        if (!compatibleRootInputs || !sourceWidthMatches ||
            !ReferenceEquals(source.TextLayout, _sourceLayout) ||
            !ReferenceEquals(source.TextLayoutRetirementRecord, _sourceLayoutRetirementRecord) ||
            !ReferenceEquals(source.SelectionLayout, _sourceSelectionLayout) ||
            !ReferenceEquals(source.SelectionLayoutRetirementRecord,
                _sourceSelectionLayoutRetirementRecord) ||
            !ReferenceEquals(source.CachedLineMetrics, _sourceLineMetrics) ||
            source.HasReadLineMetrics != _sourceLineMetricsRead ||
            !source.SelectionLayoutColor.Equals(_sourceSelectionColor) ||
            source.SelectionLayoutWidth != _sourceSelectionWidth ||
            source.LastMeasuredWidth != _sourceLastWidth ||
            source.IsBufferDirty != _sourceBufferDirty ||
            !ReferenceEquals(source.LayoutBuffer, _sourceBuffer) ||
            source.RunTextLength != _sourceRunTextLength ||
            !InlineMeasuredPlacementCapture.SameRect(source.Bounds, _sourceBounds) ||
            !source.RawPlacementMargin.Equals(_sourceRawMargin) ||
            !prepared.RawPlacementMargin.Equals(_preparedRawMargin) ||
            source.CurrentSceneGeometryGeneration != _sourceSceneGeneration ||
            source.PendingSceneGeometryGeneration != _sourcePendingSceneGeneration ||
            !ReferenceEquals(source.PendingSceneGeometry, _sourcePendingSceneGeometry) ||
            !ReferenceEquals(source.VectorSceneStateHolder, _sourceVectorSceneState) ||
            !ReferenceEquals(prepared.VectorSceneStateHolder, _preparedVectorSceneState) ||
            source.UsesProgressiveNativeSceneResidency != _sourceProgressiveSceneResidency ||
            !ReferenceEquals(source.ResolvedContainerStyle, _resolvedContainerStyle) ||
            !ReferenceEquals(prepared.ResolvedContainerStyle, _resolvedContainerStyle) ||
            !InlineMeasuredPlacementCapture.MatchesContainerKeys(source, _containerContextKeys, _containerAliasKeys) ||
            !InlineMeasuredPlacementCapture.MatchesContainerKeys(prepared, _containerContextKeys, _containerAliasKeys) ||
            !InlineMeasuredPlacementCapture.MatchesRuns(
                source, prepared, _runs, _effectiveRunAliases, _sourceResolvedRunStyles,
                _allowMeasuredVectorPlacement))
        {
            return false;
        }

        // The clone must have a complete measured native layout at the
        // requested width, or at its rebalanced table-cell width. Its
        // selection layout is intentionally absent:
        // the source selection layout, if any, moves to the retirement
        // owner and the next source selection paint will lazily rebuild it.
        if (prepared.TextLayout is null || prepared.TextLayoutRetirementRecord is null ||
            prepared.SelectionLayout is not null || prepared.SelectionLayoutRetirementRecord is not null ||
            prepared.IsDirty || prepared.IsBufferDirty ||
            !float.IsFinite(prepared.LastMeasuredWidth) ||
            !preparedWidthMatches ||
            !InlineMeasuredPlacementCapture.IsFiniteNonnegative(prepared.Bounds) ||
            prepared.Bounds.Width != prepared.LastMeasuredWidth ||
            source.IsDirty ||
            !ReferenceEquals(source.TextLayout, _sourceLayout) ||
            !source.ScriptBaselineGeometry.HasSameGeometry(_sourceScriptBaselineGeometry) ||
            !prepared.ScriptBaselineGeometry.HasSameGeometry(_sourceScriptBaselineGeometry) ||
            ReferenceEquals(prepared.TextLayout, _sourceLayout) ||
            ReferenceEquals(prepared.TextLayout, _sourceSelectionLayout) ||
            ReferenceEquals(prepared.TextLayout, prepared.SelectionLayout) ||
            (!_allowMeasuredVectorPlacement &&
                (source.VectorSceneRunCount != 0 || prepared.VectorSceneRunCount != 0)) ||
            !InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(source) || !InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(prepared) ||
            (!_allowMeasuredVectorPlacement &&
                (source.PendingSceneGeometry.Length != 0 || prepared.PendingSceneGeometry.Length != 0)) ||
            !MatchesPreparedVectorGeometry() ||
            (_isContainerLeafTransfer &&
                (source.InlineImageEntries is { Count: > 0 } ||
                 prepared.InlineImageEntries is { Count: > 0 } ||
                 source.HasAtomicInlineObject != (_vectorRunCount != 0) ||
                 prepared.HasAtomicInlineObject != (_vectorRunCount != 0))))
        {
            return false;
        }

        // The source image placement must retain its captured dimensions.
        // A table clone may have a different completed width/height after
        // column rebalancing; its ImageBox pair validates that measured
        // target placement separately. This is scalar-only locked state:
        // ownership, provider, and cancellation fences are repeated by the
        // ImageBox batch.
        foreach (InlineMeasuredRunState run in _runs)
        {
            if (!run.MatchesSourceImageState() ||
                !run.MatchesPreparedImageState(_isTableCellTransfer) ||
                !run.MatchesSourceAndPreparedVectorState(_allowMeasuredVectorPlacement))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports any native text-layout identity shared with another
    /// transfer. It intentionally uses managed reference comparisons only.
    /// </summary>
    internal bool HasNativeLayoutAlias(InlineMeasuredPlacementTransfer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return InlineMeasuredPlacementCapture.Aliases(_source.TextLayout, other._source.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.TextLayout, other._source.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayout, other._source.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayout, other._source.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.TextLayout, other._prepared.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.TextLayout, other._prepared.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayout, other._prepared.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayout, other._prepared.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayout, other._source.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayout, other._source.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayout, other._source.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayout, other._source.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayout, other._prepared.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayout, other._prepared.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayout, other._prepared.TextLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayout, other._prepared.SelectionLayout) ||
            InlineMeasuredPlacementCapture.Aliases(_source.TextLayoutRetirementRecord, other._source.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_source.TextLayoutRetirementRecord, other._source.SelectionLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayoutRetirementRecord, other._source.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayoutRetirementRecord, other._source.SelectionLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_source.TextLayoutRetirementRecord, other._prepared.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_source.TextLayoutRetirementRecord, other._prepared.SelectionLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayoutRetirementRecord, other._prepared.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_source.SelectionLayoutRetirementRecord, other._prepared.SelectionLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayoutRetirementRecord, other._source.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayoutRetirementRecord, other._source.SelectionLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayoutRetirementRecord, other._source.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayoutRetirementRecord, other._source.SelectionLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayoutRetirementRecord, other._prepared.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.TextLayoutRetirementRecord, other._prepared.SelectionLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayoutRetirementRecord, other._prepared.TextLayoutRetirementRecord) ||
            InlineMeasuredPlacementCapture.Aliases(_prepared.SelectionLayoutRetirementRecord, other._prepared.SelectionLayoutRetirementRecord);
    }

    /// <summary>
    /// Adds the current source and candidate native layout owners to a
    /// caller-owned reference-identity set. A duplicate means a native
    /// layout would have more than one placement owner in the batch.
    /// </summary>
    internal bool TryAddNativeLayoutOwners(HashSet<CanvasTextLayout> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        return AddOwner(_source.TextLayout, owners) &&
            AddOwner(_source.SelectionLayout, owners) &&
            AddOwner(_prepared.TextLayout, owners) &&
            AddOwner(_prepared.SelectionLayout, owners);
    }

    internal bool TryAddNativeLayoutOwners(
        HashSet<CanvasTextLayout> layouts,
        HashSet<NativeResourceRetirementRecord> retirementRecords)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(retirementRecords);
        return AddOwner(_source.TextLayout, layouts) &&
            AddOwner(_source.SelectionLayout, layouts) &&
            AddOwner(_prepared.TextLayout, layouts) &&
            AddOwner(_prepared.SelectionLayout, layouts) &&
            AddRetirementOwner(_source.TextLayoutRetirementRecord, retirementRecords) &&
            AddRetirementOwner(_source.SelectionLayoutRetirementRecord, retirementRecords) &&
            AddRetirementOwner(_prepared.TextLayoutRetirementRecord, retirementRecords) &&
            AddRetirementOwner(_prepared.SelectionLayoutRetirementRecord, retirementRecords);
    }

    private static bool AddOwner(CanvasTextLayout? layout, HashSet<CanvasTextLayout> owners) =>
        layout is null || owners.Add(layout);

    private static bool AddRetirementOwner(
        NativeResourceRetirementRecord? record,
        HashSet<NativeResourceRetirementRecord> owners) =>
        record is null || owners.Add(record);

    /// <summary>
    /// Reports a native-layout alias against either generation of this
    /// inline container and an image's caption/placeholder layout. The
    /// ImageBox helper takes its short placement lock and performs only
    /// reference comparisons.
    /// </summary>
    internal bool HasNativeLayoutAlias(ImageBox image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.HasNativePlacementLayoutAlias(_source.TextLayout) ||
            image.HasNativePlacementLayoutAlias(_source.SelectionLayout) ||
            image.HasNativePlacementLayoutAlias(_prepared.TextLayout) ||
            image.HasNativePlacementLayoutAlias(_prepared.SelectionLayout);
    }

    /// <summary>
    /// Reports whether this cell's source or candidate layout is shared
    /// with either generation of a separately published image placement.
    /// This is an identity-only managed check; the ImageBox helper takes
    /// its short placement lock and makes no native calls.
    /// </summary>
    internal bool HasNativeLayoutAlias(ImageBox.MeasuredPlacementTransfer imageTransfer)
    {
        ArgumentNullException.ThrowIfNull(imageTransfer.Source);
        ArgumentNullException.ThrowIfNull(imageTransfer.Prepared);
        return HasNativeLayoutAlias(imageTransfer.Source) ||
            HasNativeLayoutAlias(imageTransfer.Prepared);
    }

    /// <summary>
    /// No-throw reference/scalar transfer. All shape, source, cancellation,
    /// geometry, and image lease fences must have succeeded immediately
    /// beforehand while the snapshot gate is held.
    /// </summary>
    internal void SwapPlacementNoThrow()
    {
        _source.SwapMeasuredPlacementStateNoThrow(
            _prepared,
            _sourceSceneGeneration,
            _sourcePendingSceneGeneration,
            _sourcePendingSceneGeometry,
            _allowMeasuredVectorPlacement && _vectorRunCount > 0,
            _preparedVectorGeometryForSource);

        foreach (InlineMeasuredRunState run in _runs)
        {
            if (run.SourceImageRun is not { } sourceImageRun ||
                run.PreparedImageRun is not { } preparedImageRun)
            {
                continue;
            }

            float sourceWidth = sourceImageRun.DesiredWidth;
            float sourceHeight = sourceImageRun.DesiredHeight;
            sourceImageRun.RestoreMeasuredSize(
                preparedImageRun.DesiredWidth,
                preparedImageRun.DesiredHeight);
            preparedImageRun.RestoreMeasuredSize(sourceWidth, sourceHeight);
        }

        foreach (InlineMeasuredRunState run in _runs)
        {
            if (run.SourceVectorRun is not { } sourceVector ||
                run.PreparedVectorRun is not { } preparedVector)
            {
                continue;
            }

            sourceVector.RestoreLayoutState(
                preparedVector.DesiredWidth,
                preparedVector.DesiredHeight,
                preparedVector.DesiredBaseline,
                preparedVector.LocalBounds);
        }
    }
}
