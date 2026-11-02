using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Rendering;

/// <summary>What canvas tile painting needs from the control.</summary>
internal interface ICanvasTilePainterHost
{
    DispatcherQueue? DispatcherQueue { get; }

    bool IsLoaded { get; }

    /// <summary>True when a deferred scene status refresh waits for a successful paint.</summary>
    bool IsDeferredSceneStatusRefreshPending { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RefreshDeferredSceneStatusAfterSuccessfulPaintIfNeeded();

    void ScheduleVisibleDeferredScenes();
}
