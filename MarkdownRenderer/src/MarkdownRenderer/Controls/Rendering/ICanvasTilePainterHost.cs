using Microsoft.UI.Dispatching;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Rendering;

/// <summary>What canvas tile painting needs from the control.</summary>
internal interface ICanvasTilePainterHost
{
    DispatcherQueue? DispatcherQueue { get; }

    bool IsLoaded { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void ScheduleVisibleDeferredScenes();
}
