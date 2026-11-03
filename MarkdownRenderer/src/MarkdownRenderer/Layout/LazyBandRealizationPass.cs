using System.Collections.Generic;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Rollback and progress state of one lazy band realization. It captures the
/// committed geometry before any root is measured so a failed pass can
/// restore the measured mask, plan ordinals and revision it started from.
/// </summary>
internal sealed class LazyBandRealizationPass
{
    internal LazyBandRealizationPass(
        LayoutSnapshotGeometry geometry,
        LayoutPlanCandidateIndex planCandidates,
        CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        OldHeight = geometry.Size.Height;
        OldRevision = geometry.LayoutRevision;
        OldMeasuredTopLevelBlockCount = geometry.MeasuredTopLevelBlockCount;
        OldMeasuredLayoutPlanRootOrdinalsAreSorted = planCandidates.MeasuredRootOrdinalsAreSorted;
        FirstMeasureAttempted = geometry.Blocks.Count;
        PriorPaintOnlyGeometryTainted = geometry.PaintOnlyGeometryTainted;
    }

    internal CancellationToken CancellationToken { get; }

    internal double OldHeight { get; }

    internal long OldRevision { get; }

    internal int OldMeasuredTopLevelBlockCount { get; }

    internal bool OldMeasuredLayoutPlanRootOrdinalsAreSorted { get; }

    internal bool PriorPaintOnlyGeometryTainted { get; }

    /// <summary>
    /// Lowest ordinal whose Measure was attempted, or the block count when
    /// none was. A factory/Arrange can mutate then throw before its batch is
    /// copied into <see cref="MeasuredOrdinals"/>, so rollback starts here.
    /// </summary>
    internal int FirstMeasureAttempted { get; set; }

    /// <summary>Roots whose measured bit this pass published.</summary>
    internal List<int>? MeasuredOrdinals { get; set; }

    /// <summary>Visible-band roots handed to the residency policy, if any.</summary>
    internal List<int>? RequestedSceneBandOrdinals { get; set; }

    internal bool NativeSceneResourcesChanged { get; set; }
}
