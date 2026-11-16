using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Performance.Internal.ProgressiveScenes;

/// <summary>
/// The progressive scene plan's visible set, as seen by its viewport probe.
/// Members ending in NoLock are called under the plan gate that the probe
/// shares; the others take it themselves.
/// </summary>
internal interface IDeferredSceneViewportHost
{
    /// <summary>Whether the plan has retired.</summary>
    bool IsDisposedNoLock { get; }

    /// <summary>Replaces the visible set and returns how many registered markers it holds.</summary>
    int UpdateVisibleMarkers(IEnumerable<string> markers);

    /// <summary>Schedules a producer for every marker in the visible set.</summary>
    void ScheduleVisibleMarkersNoLock(
        Func<Action, bool> enqueuePublication,
        Action onPublished,
        Action? onRetryVisibleBand);
}
