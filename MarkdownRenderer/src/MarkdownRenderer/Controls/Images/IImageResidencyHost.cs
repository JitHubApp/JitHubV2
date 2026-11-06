using MarkdownRenderer.Performance;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Images;

/// <summary>What image residency needs from the control.</summary>
internal interface IImageResidencyHost
{
    /// <summary>The control's dispatcher; read on the UI thread only.</summary>
    DispatcherQueue? DispatcherQueue { get; }

    /// <summary>The performance session the control renders with, if any.</summary>
    IMarkdownPerformanceSession? PerformanceSession { get; }

    /// <summary>The layout revision whose realization plans were last applied.</summary>
    long AppliedLazyLayoutRevision { get; }

    /// <summary>Repaints the document canvas.</summary>
    void InvalidateCanvas();
}
