using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Performance;

/// <summary>
/// The control, snapshot, generation and layout revision a readiness probe
/// answers for. The answer counts only while all of them are still current.
/// </summary>
internal readonly record struct DeferredSceneReadinessExpectation(
    MarkdownRendererControl Owner,
    LayoutSnapshot Snapshot,
    long Generation,
    long LayoutRevision)
{
    internal bool IsCurrent(IMarkdownPerformanceSessionInternal session) =>
        Owner.DeferredSceneReadiness.IsSnapshotCurrent(
            Snapshot,
            Generation,
            LayoutRevision) &&
        ReferenceEquals(Owner.PerformanceSession, session);
}
