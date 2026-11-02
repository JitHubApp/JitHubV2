using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Rendering;

/// <summary>What canvas tile painting needs from the control.</summary>
internal interface ICanvasTilePainterHost
{
    DispatcherQueue? DispatcherQueue { get; }

    bool IsLoaded { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void ScheduleVisibleDeferredScenes();
}
