using System.Threading;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Readiness;

/// <summary>
/// Answers UI Automation's deferred scene (Mermaid, Math, SVG) readiness for
/// the visible band and forwards scene status changes to the automation peer.
/// Owns the request to repeat that notification after the next successful
/// paint when the snapshot was busy.
/// </summary>
internal sealed class DeferredSceneReadiness
{
    private readonly IDeferredSceneReadinessHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly RebuildScheduler _rebuild;
    private int _refreshAfterPaint;

    internal DeferredSceneReadiness(
        IDeferredSceneReadinessHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        RebuildScheduler rebuild)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _rebuild = rebuild;
    }

    /// <summary>True while a status notification waits for the next successful paint.</summary>
    internal bool IsRefreshAfterPaintPending => Volatile.Read(ref _refreshAfterPaint) != 0;

    internal void CancelRefreshAfterPaint() => Interlocked.Exchange(ref _refreshAfterPaint, 0);

    /// <summary>Readiness of the deferred scenes in the current viewport.</summary>
    internal MarkdownVisibleSceneReadiness GetVisibleStatus()
    {
        IMarkdownPerformanceSessionInternal? session = _host.PerformanceSession;
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return session?.HasRegisteredDeferredSceneWork(_host.DocumentOwner) == true
                ? MarkdownVisibleSceneReadiness.NotReady : MarkdownVisibleSceneReadiness.Ready;

        long generation = _rebuild.Generation;
        long revision = snapshot.LayoutRevision;
        bool hasViewport = _viewport.TryGet(out double top, out double height, out double width);
        Rect viewport = new(0, top, width, height);
        if (!hasViewport || !IsFiniteNonEmpty(viewport))
        {
            return _published.Generation != generation ||
                session?.HasRegisteredDeferredSceneWork(_host.DocumentOwner) == true
                ? MarkdownVisibleSceneReadiness.NotReady : MarkdownVisibleSceneReadiness.Ready;
        }

        return GetVisibleReadiness(snapshot, generation, revision, viewport);
    }

    /// <summary>
    /// Tells the automation peer that loading status may have changed and
    /// drains the snapshot's graph status changes into it. A busy snapshot
    /// repeats the notification after the next successful paint.
    /// </summary>
    internal void NotifyItemStatusMayHaveChanged()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        MarkdownAutomationPeer? peer = _host.FindAutomationPeer();
        peer?.NotifyLoadingStatusMayHaveChanged();
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null || !snapshot.TryBeginInteraction())
        {
            if (snapshot is not null)
                Interlocked.Exchange(ref _refreshAfterPaint, 1);
            return;
        }
        try
        {
            if (snapshot.VectorSceneResidencyPolicy is { } policy)
            {
                try
                {
                    foreach (VectorSceneBox box in policy.GraphStatusChanges)
                        peer?.NotifyVectorSceneStatusChanged(box);
                }
                finally
                {
                    // Drain even without a UIA listener: completed changes must
                    // not become a second document-sized owner list.
                    policy.ClearGraphStatusChanges();
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    /// <summary>Repeats a status notification that a busy snapshot postponed.</summary>
    internal void RefreshAfterSuccessfulPaintIfNeeded()
    {
        if (Interlocked.Exchange(ref _refreshAfterPaint, 0) == 0 ||
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded)
        {
            return;
        }

        NotifyItemStatusMayHaveChanged();
    }

    /// <summary>
    /// True while <paramref name="snapshot"/> at <paramref name="layoutRevision"/>
    /// is still the published snapshot of the active generation.
    /// </summary>
    internal bool IsSnapshotCurrent(
        LayoutSnapshot snapshot,
        long generation,
        long layoutRevision) =>
        ReferenceEquals(_published.Snapshot, snapshot) &&
        _rebuild.Generation == generation &&
        _published.Generation == generation &&
        snapshot.LayoutRevision == layoutRevision;

    private MarkdownVisibleSceneReadiness GetVisibleReadiness(
        LayoutSnapshot expectedSnapshot,
        long expectedGeneration,
        long expectedLayoutRevision,
        Rect viewport)
    {
        if (expectedSnapshot is null ||
            expectedGeneration <= 0 ||
            !IsFiniteNonEmpty(viewport) ||
            !IsSnapshotCurrent(
                expectedSnapshot,
                expectedGeneration,
                expectedLayoutRevision))
        {
            return MarkdownVisibleSceneReadiness.NotReady;
        }

        IMarkdownPerformanceSessionInternal? session = _host.PerformanceSession;
        if (session is not null)
        {
            MarkdownVisibleSceneReadiness readiness = session.GetVisibleDeferredSceneReadiness(
                _host.DocumentOwner,
                expectedSnapshot,
                expectedGeneration,
                expectedLayoutRevision,
                viewport);
            if (readiness == MarkdownVisibleSceneReadiness.Busy)
                Interlocked.Exchange(ref _refreshAfterPaint, 1);
            return readiness;
        }

        // Without the optional session there are no deferred markers. Keep the
        // native-resource measured-band gate in the base renderer so eager
        // vector scenes remain covered for every consumer.
        if (!expectedSnapshot.TryBeginPaint())
        {
            Interlocked.Exchange(ref _refreshAfterPaint, 1);
            return MarkdownVisibleSceneReadiness.Busy;
        }

        try
        {
            if (!IsSnapshotCurrent(
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision) ||
                !ReferenceEquals(_host.PerformanceSession, session))
            {
                return MarkdownVisibleSceneReadiness.NotReady;
            }

            LazyLayoutBand visibleBand = LazyLayoutBand.FromViewport(
                viewport.Top,
                viewport.Height,
                overscan: 0);
            return expectedSnapshot.IsBandMeasured(visibleBand) &&
                IsSnapshotCurrent(
                    expectedSnapshot,
                    expectedGeneration,
                    expectedLayoutRevision) &&
                ReferenceEquals(_host.PerformanceSession, session)
                ? MarkdownVisibleSceneReadiness.Ready : MarkdownVisibleSceneReadiness.NotReady;
        }
        finally
        {
            expectedSnapshot.EndPaint();
        }
    }

    private static bool IsFiniteNonEmpty(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom) &&
        rect.Width > 0 && rect.Height > 0;
}
