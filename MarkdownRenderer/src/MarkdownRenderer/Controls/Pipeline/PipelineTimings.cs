#if MARKDOWN_LAYOUT_DIAGNOSTICS
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
#endif

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The stage timings of the pipeline whose snapshot is committed. Diagnostic
/// callers run on the UI thread. A canceled or superseded build never
/// replaces these timings, so they always describe the committed snapshot.
/// </summary>
internal sealed class PipelineTimings
{
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private bool _hasInitialDispatcher;
    private MarkdownInitialLayoutDispatcherDiagnosticsSnapshot _initialDispatcher;
    private MarkdownLayoutDiagnostics? _initialDispatcherSource;
#endif

    /// <summary>The timings of the committed snapshot's pipeline.</summary>
    internal MarkdownPipelineTimingSnapshot Last { get; private set; }

    /// <summary>Records the timings of a pipeline that just committed.</summary>
    internal void Publish(MarkdownPipelineTimingSnapshot timing) => Last = timing;

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    /// <summary>The dispatcher diagnostics of the first captured layout build.</summary>
    internal MarkdownInitialLayoutDispatcherDiagnosticsSnapshot InitialDispatcher => _initialDispatcher;

    /// <summary>
    /// Starts the dispatcher probe for a build that captures
    /// <paramref name="diagnostics"/>, until the first such build commits.
    /// </summary>
    internal InitialLayoutDispatcherProbe? StartInitialDispatcherProbe(
        DispatcherQueue? dispatcher,
        MarkdownLayoutDiagnostics? diagnostics) =>
        diagnostics is not null && !_hasInitialDispatcher
            ? new InitialLayoutDispatcherProbe(this, dispatcher, diagnostics)
            : null;

    /// <summary>Keeps the probe's capture once its build commits.</summary>
    internal void CompleteInitialDispatcherProbe(
        InitialLayoutDispatcherProbe? probe,
        MarkdownLayoutDiagnostics? diagnostics)
    {
        if (_hasInitialDispatcher || probe is null)
            return;

        _initialDispatcher = probe.StopAndCapture();
        _initialDispatcherSource = diagnostics;
        _hasInitialDispatcher = true;
    }

    internal void OnInitialDispatcherSentinelDispatched(MarkdownLayoutDiagnostics diagnostics)
    {
        if (!_hasInitialDispatcher || !ReferenceEquals(_initialDispatcherSource, diagnostics))
            return;

        _initialDispatcher = diagnostics.CaptureDispatcher();
        Last = Last.WithInitialLayoutDispatcherDiagnostics(_initialDispatcher);
    }
#endif
}
