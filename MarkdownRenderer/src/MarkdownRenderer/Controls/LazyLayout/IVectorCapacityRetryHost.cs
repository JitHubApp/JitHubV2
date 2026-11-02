using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.LazyLayout;

/// <summary>What vector capacity retry needs from the control.</summary>
internal interface IVectorCapacityRetryHost
{
    /// <summary>The control's dispatcher; read on the UI thread only.</summary>
    DispatcherQueue? DispatcherQueue { get; }

    IMarkdownPerformanceSession? PerformanceSession { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    /// <summary>Re-reads the current viewport and queues lazy layout for it.</summary>
    void QueueLazyLayoutForViewport(bool forceCapacityRetry);
}
