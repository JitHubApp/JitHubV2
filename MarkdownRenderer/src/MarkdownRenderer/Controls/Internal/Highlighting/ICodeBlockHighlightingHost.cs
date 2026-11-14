using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;

namespace MarkdownRenderer.Controls.Internal.Highlighting;

/// <summary>
/// What code block highlighting needs from the control.
/// </summary>
internal interface ICodeBlockHighlightingHost
{
    /// <summary>The document owner the performance session admits scene work for: the control.</summary>
    object DocumentOwner { get; }

    /// <summary>The control's UI thread; none when the control has no dispatcher. Read on the UI thread only.</summary>
    IMarkdownUiScheduler? UiScheduler { get; }

    /// <summary>True when plan collection measured at least one code block of the published snapshot.</summary>
    bool HasMeasuredCodeBlocks { get; }

    /// <summary>
    /// The visible document band, in DIPs. Returns false, with an estimate
    /// based on the renderer's size, when no viewport is known yet.
    /// </summary>
    bool TryGetViewport(out double top, out double height);

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
    Task RetireCancellationTokenSource(RetirableCancellationTokenSource? cts, Task? lifetime);
}
