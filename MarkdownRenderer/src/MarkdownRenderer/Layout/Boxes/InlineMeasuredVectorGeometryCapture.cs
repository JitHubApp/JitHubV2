using System;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The vector-scene half of an <see cref="InlineMeasuredPlacementTransfer"/>:
/// pairs each source vector run with its measured private clone, rebinds the
/// clone's measured scene geometry to the source runs before the publication
/// gate, and revalidates those captured placements and geometry under it.
/// </summary>
internal sealed class InlineMeasuredVectorGeometryCapture
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
    private readonly InlineMeasuredRunState[] _runs;
    private readonly bool _allowMeasuredVectorPlacement;
    private readonly int _vectorRunCount;
    private readonly object? _preparedVectorSceneState;
    private readonly PreparedVectorPlacement[] _placements;
    private InlineSceneGeometry[]? _geometrySnapshot;
    private InlineSceneGeometry[]? _geometryForSource;
    private long _sceneGeometryGeneration;
    private long _pendingSceneGeometryGeneration;
    private bool _ready;

    internal InlineMeasuredVectorGeometryCapture(
        InlineContainerBox source,
        InlineContainerBox prepared,
        InlineMeasuredRunState[] runs,
        bool allowMeasuredVectorPlacement,
        int vectorRunCount,
        object? preparedVectorSceneState)
    {
        _source = source;
        _prepared = prepared;
        _runs = runs;
        _allowMeasuredVectorPlacement = allowMeasuredVectorPlacement;
        _vectorRunCount = vectorRunCount;
        _preparedVectorSceneState = preparedVectorSceneState;
        _placements = new PreparedVectorPlacement[vectorRunCount];
    }

    /// <summary>The measured geometry rebound to the source runs, once prepared.</summary>
    internal InlineSceneGeometry[]? GeometryForSource => _geometryForSource;

    /// <summary>
    /// Builds a bounded source-run remap for candidate vector geometry.
    /// Call after the private candidate has completed Measure/Arrange and
    /// before entering the snapshot's final publication gate. This is the
    /// only allocation in the vector-geometry transfer; final validation
    /// and the swap use the captured arrays and managed references only.
    /// </summary>
    internal bool TryPrepare()
    {
        if (_vectorRunCount == 0)
            return true;
        if (_ready)
            return Matches();

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
        if (!TryCapturePreparedVectorPlacements(vectorCount))
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
            if (!TryRemapMeasuredGeometryToSource(candidateGeometry, remappedGeometry))
                return false;
        }
        else if (candidateGeometry.Length != 0 || prepared.PendingSceneGeometryGeneration != 0 &&
                 !prepared.UsesProgressiveNativeSceneResidency)
        {
            return false;
        }

        _geometrySnapshot = candidateGeometry;
        _geometryForSource = remappedGeometry;
        _sceneGeometryGeneration = prepared.CurrentSceneGeometryGeneration;
        _pendingSceneGeometryGeneration = prepared.PendingSceneGeometryGeneration;
        _ready = true;
        return Matches();
    }

    // Pairs each source vector run with its prepared clone in run order.
    private bool TryCapturePreparedVectorPlacements(int vectorCount)
    {
        int placementOrdinal = 0;
        foreach (InlineMeasuredRunState run in _runs)
        {
            if (run.SourceVectorRun is not { } sourceVector ||
                run.PreparedVectorRun is not { } preparedVector)
            {
                continue;
            }
            if (placementOrdinal >= _placements.Length ||
                !run.MatchesSourceAndPreparedVectorState(_allowMeasuredVectorPlacement) ||
                !InlineMeasuredPlacementCapture.IsFiniteNonnegative(preparedVector.LocalBounds))
            {
                return false;
            }
            _placements[placementOrdinal++] = new PreparedVectorPlacement(
                sourceVector, preparedVector);
        }

        return placementOrdinal == vectorCount;
    }

    // Rebinds each measured candidate geometry entry to its source vector
    // run, filling remappedGeometry, whose length is the vector count.
    private bool TryRemapMeasuredGeometryToSource(
        InlineSceneGeometry[] candidateGeometry,
        InlineSceneGeometry[] remappedGeometry)
    {
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

        return geometryOrdinal == remappedGeometry.Length;
    }

    internal bool Matches()
    {
        if (!ReferenceEquals(_prepared.VectorSceneStateHolder, _preparedVectorSceneState))
            return false;
        if (_vectorRunCount == 0)
            return true;
        if (!_ready ||
            !ReferenceEquals(_prepared.PendingSceneGeometry, _geometrySnapshot) ||
            _prepared.CurrentSceneGeometryGeneration != _sceneGeometryGeneration ||
            _prepared.PendingSceneGeometryGeneration != _pendingSceneGeometryGeneration)
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
            if (_geometrySnapshot is null ||
                _geometrySnapshot.Length != vectorCount ||
                _geometryForSource is null ||
                _geometryForSource.Length != vectorCount)
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

                InlineSceneGeometry candidateGeometry = _geometrySnapshot[geometryOrdinal];
                InlineSceneGeometry sourceGeometry = _geometryForSource[geometryOrdinal];
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

        return _geometryForSource is { Length: 0 } &&
            (_geometrySnapshot?.Length ?? 0) == 0;
    }

    private bool MatchesPreparedVectorPlacements(int vectorCount)
    {
        if (_placements.Length != vectorCount)
            return false;

        for (int index = 0; index < vectorCount; index++)
        {
            PreparedVectorPlacement expected = _placements[index];
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
}
