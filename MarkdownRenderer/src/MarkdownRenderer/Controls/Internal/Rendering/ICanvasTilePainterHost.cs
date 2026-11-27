using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Rendering;

/// <summary>What canvas tile painting needs from the control.</summary>
internal interface ICanvasTilePainterHost
{
    DispatcherQueue? DispatcherQueue { get; }

    bool IsLoaded { get; }

    /// <summary>The window root the renderer is shown in, if any.</summary>
    XamlRoot? XamlRoot { get; }

    /// <summary>Device pixels per DIP of the window the renderer is shown in.</summary>
    double RasterizationScale { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void ScheduleVisibleDeferredScenes();
}
