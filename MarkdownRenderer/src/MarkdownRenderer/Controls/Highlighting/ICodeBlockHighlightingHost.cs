using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Highlighting;

/// <summary>
/// What code block highlighting needs from the control. The control is also
/// the document owner the performance session admits scene work for.
/// </summary>
internal interface ICodeBlockHighlightingHost
{
    /// <summary>The control's dispatcher; read on the UI thread only.</summary>
    DispatcherQueue? DispatcherQueue { get; }

    /// <summary>The highlight provider; read on the UI thread only.</summary>
    ICodeHighlighter? CodeHighlighter { get; }

    /// <summary>Whether code blocks are highlighted at all.</summary>
    bool IsCodeBlockSyntaxHighlightingEnabled { get; }

    /// <summary>The performance session the control renders with, if any.</summary>
    IMarkdownPerformanceSession? PerformanceSession { get; }

    /// <summary>The theme of the committed snapshot.</summary>
    ThemeSnapshot? ThemeSnapshot { get; }

    /// <summary>The recent scroll velocity, which skews the look-ahead band.</summary>
    double LazyScrollVelocityPixelsPerSecond { get; }

    /// <summary>
    /// Runs the control's visible highlight pass, which first checks for
    /// geometry recovery and schedules deferred scenes.
    /// </summary>
    void ScheduleVisibleCodeBlockHighlighting();

    /// <summary>Repaints the document canvas.</summary>
    void InvalidateCanvas();

    /// <summary>
    /// Cancels <paramref name="cts"/> and disposes it after
    /// <paramref name="lifetime"/> completes.
    /// </summary>
    Task RetireCancellationTokenSource(CancellationTokenSource? cts, Task? lifetime);
}
