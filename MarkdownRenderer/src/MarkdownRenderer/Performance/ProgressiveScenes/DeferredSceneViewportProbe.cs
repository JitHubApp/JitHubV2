using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// Probes the owning control's measured viewport for visible deferred-scene
/// markers and schedules their producers. Owns the dispatcher callbacks bound
/// to that control and the last placeholder status it reported, which it reads
/// and writes under the plan gate it shares with the plan's visible set.
/// </summary>
internal sealed class DeferredSceneViewportProbe
{
    private readonly object _gate;
    private readonly IDeferredSceneViewportHost _host;
    private readonly IMarkdownPerformanceSessionInternal _session;
    private MarkdownRendererControl? _callbackOwner;
    private Func<Action, bool>? _enqueuePublication;
    private Action? _onPublished;
    private Action? _onRetryVisibleBand;
    private bool _hasObservedVisiblePlaceholderStatus;
    private bool _visiblePlaceholderStatusPending;

    /// <param name="gate">
    /// The owning plan's monitor. Scheduling the visible set and reporting
    /// placeholder status are checked against the plan's retirement under it.
    /// </param>
    /// <param name="host">The owning plan's visible set and scheduling entry point.</param>
    /// <param name="session">Session whose disposal stops probing.</param>
    internal DeferredSceneViewportProbe(
        object gate,
        IDeferredSceneViewportHost host,
        IMarkdownPerformanceSessionInternal session)
    {
        _gate = gate;
        _host = host;
        _session = session;
    }

    internal bool ScheduleVisible(object documentOwner)
    {
        if (_session.IsDisposed || documentOwner is not MarkdownRendererControl control)
            return false;

        EnsureControlCallbacks(control);

        LayoutSnapshot? snapshot = control.CurrentSnapshot;
        if (snapshot is null || !control.TryGetViewport(out double top, out double height, out _) ||
            !double.IsFinite(top) || !double.IsFinite(height) || height <= 0)
        {
            RequestProbeAfterPaint(control);
            return true;
        }
        double bottom = top + height;
        int visibleMarkerCount;
        if (!snapshot.TryBeginPaint())
        {
            RequestProbeAfterPaint(control);
            return true;
        }
        try
        {
            visibleMarkerCount = UpdateVisibleMarkersUnderPaintLease(snapshot, top, bottom);
        }
        finally
        {
            snapshot.EndPaint();
        }

        if (visibleMarkerCount < 0)
        {
            // The snapshot is being measured or has a stale inline geometry
            // index. A lazy band can finish measuring, or its scenes finish
            // preparing, without another layout commit, so ask the control to
            // re-probe after the next successful paint.
            RequestProbeAfterPaint(control);
            return true;
        }

        NotifyVisiblePlaceholderStatusIfChanged(control, pending: visibleMarkerCount != 0);

        // The visible set is plan-owned and updated in place. Scheduling under
        // the same reentrant monitor avoids a per-scroll copy; TrySchedule only
        // queues background work and takes no external/UI callback inline.
        lock (_gate)
        {
            _host.ScheduleVisibleMarkersNoLock(
                _enqueuePublication!,
                _onPublished!,
                _onRetryVisibleBand);
        }

        return true;
    }

    /// <summary>
    /// Collects the measured band's visible markers into the plan's visible
    /// set while the caller holds the snapshot paint lease. Returns -1 when the
    /// band is not measured or its geometry index is stale.
    /// </summary>
    private int UpdateVisibleMarkersUnderPaintLease(LayoutSnapshot snapshot, double top, double bottom)
    {
        bool bandMeasured = snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                top,
                bottom,
                out IReadOnlyList<BlockBox> visibleBlocks,
                out IReadOnlyList<int> visibleOrdinals,
                out LayoutPlacementGeneration placements,
                out IReadOnlyDictionary<int, int> topLevelOrdinalByBlockIndex);
        if (bandMeasured &&
            DeferredSceneMarkerCollector.TryCollect(
                visibleBlocks,
                visibleOrdinals,
                placements,
                snapshot.PlacementTransform,
                topLevelOrdinalByBlockIndex,
                top,
                bottom,
                viewport: null,
                residencyPolicy: snapshot.VectorSceneResidencyPolicy,
                out IReadOnlyList<string> measuredVisibleMarkers,
                out _))
        {
            // Consume the policy-owned marker scratch before releasing the
            // snapshot lease, which serializes subsequent viewport probes.
            return _host.UpdateVisibleMarkers(measuredVisibleMarkers);
        }

        return -1;
    }

    private void RequestProbeAfterPaint(MarkdownRendererControl control)
    {
        control.RequestDeferredSceneProbeAfterPaint();
        NotifyVisiblePlaceholderStatusIfChanged(control, pending: true);
    }

    private void NotifyVisiblePlaceholderStatusIfChanged(
        MarkdownRendererControl control,
        bool pending)
    {
        bool changed;
        lock (_gate)
        {
            if (_host.IsDisposedNoLock)
                return;

            changed = !_hasObservedVisiblePlaceholderStatus ||
                _visiblePlaceholderStatusPending != pending;
            _hasObservedVisiblePlaceholderStatus = true;
            _visiblePlaceholderStatusPending = pending;
        }

        if (changed)
            control.DeferredSceneReadiness.NotifyItemStatusMayHaveChanged();
    }

    private void EnsureControlCallbacks(MarkdownRendererControl control)
    {
        if (ReferenceEquals(_callbackOwner, control))
            return;

        _callbackOwner = control;
        _enqueuePublication = action => control.DispatcherQueue?.TryEnqueue(() => action()) == true;
        _onPublished = control.RequestRebuildAfterDeferredScene;
        _onRetryVisibleBand = () =>
        {
            control.DispatcherQueue?.TryEnqueue(control.ScheduleVisibleDeferredScenes);
        };
    }

    /// <summary>Drops the control's callbacks at plan retirement. Call under the plan gate.</summary>
    internal void ReleaseControlNoLock()
    {
        _callbackOwner = null;
        _enqueuePublication = null;
        _onPublished = null;
        _onRetryVisibleBand = null;
    }
}
