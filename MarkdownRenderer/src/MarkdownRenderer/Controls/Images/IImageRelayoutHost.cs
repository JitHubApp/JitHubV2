using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.Images;

/// <summary>What image relayout needs from the control.</summary>
internal interface IImageRelayoutHost
{
    /// <summary>The control's dispatcher; read on the UI thread only.</summary>
    Microsoft.UI.Dispatching.DispatcherQueue? DispatcherQueue { get; }

    IMarkdownPerformanceSession? PerformanceSession { get; }

    /// <summary>The width layout runs at, in DIPs.</summary>
    float LayoutWidth { get; }

    /// <summary>
    /// Settles a finished rebuild pipeline whose UI completion was withheld,
    /// then returns true while a rebuild is pending or building.
    /// </summary>
    bool DefersToRebuild();

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RequestFullRebuild();

    /// <summary>
    /// Stops the recovery clock. Unless <paramref name="force"/> is set, it
    /// keeps running while other deferred work is pending.
    /// </summary>
    void StopRecoveryClock(bool force);

    Task RetireCancellationTokenSource(CancellationTokenSource? cts, Task? lifetime);
}
