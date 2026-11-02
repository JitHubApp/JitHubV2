using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.LazyLayout;

/// <summary>What lazy layout realization needs from the control.</summary>
internal interface ILazyLayoutHost
{
    /// <summary>The control's dispatcher; read on the UI thread only.</summary>
    Microsoft.UI.Dispatching.DispatcherQueue? DispatcherQueue { get; }

    IMarkdownPerformanceSession? PerformanceSession { get; }

    /// <summary>The document owner the performance session prepares deferred scenes for.</summary>
    object DeferredSceneOwner { get; }

    /// <summary>The generation of the current rebuild pipeline.</summary>
    long PipelineGeneration { get; }

    /// <summary>Cancels with the current rebuild pipeline; none when no pipeline runs.</summary>
    CancellationToken PipelineToken { get; }

    /// <summary>True while the scroll viewer is in an intermediate (inertial or animated) view.</summary>
    bool IsScrollViewIntermediate { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RequestFullRebuild();

    void HandleDeviceLost(Exception failure);

    void TryResumePendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration);

    void NotifyVisibleDeferredSceneItemStatusMayHaveChanged();

    void ScheduleVisibleDeferredScenes();

    void InvalidateAutomationLayout();

    void InvalidateCanvas();

    /// <summary>Stops the recovery clock unless other deferred work is pending.</summary>
    void StopRecoveryClockIfIdle();

    Task RetireCancellationTokenSource(CancellationTokenSource? cts, Task? lifetime);
}
