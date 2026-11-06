using System;
using System.Threading.Tasks;

namespace JitHub.Markdown;

/// <summary>
/// The main window's Markdown close sequence. Markdown controls borrow the
/// process-wide highlighter, engine, performance session and SVG worker, so
/// the window's page tree goes first: the nested content, then the page with a
/// bounded wait for it to unload, so the Unloaded handlers dispose the controls
/// and detach their scroll callbacks. <see cref="MarkdownViewShutdown"/> then
/// retires the providers only when every renderer is gone. The window closes
/// afterwards whatever happened, so closing stays bounded.
/// </summary>
/// <remarks>
/// This type holds no XAML: the window passes the steps that touch its visual
/// tree as delegates, so the order is tested without a XAML runtime.
/// </remarks>
public sealed class MarkdownWindowCloseCoordinator
{
    /// <summary>
    /// How long the close waits for each removed page to unload and for its
    /// Markdown renderers to retire.
    /// </summary>
    internal static readonly TimeSpan ViewUnloadTimeout = TimeSpan.FromSeconds(2);

    private readonly MarkdownRendererLifetimeRegistry _renderers;
    private readonly Action<Exception, string> _reportHandledFailure;

    /// <summary>Creates the close sequence for one window close.</summary>
    /// <param name="renderers">The app's renderer ledger.</param>
    /// <param name="reportHandledFailure">Reports a failed step, with its category, without stopping the close.</param>
    public MarkdownWindowCloseCoordinator(
        MarkdownRendererLifetimeRegistry renderers,
        Action<Exception, string> reportHandledFailure)
    {
        _renderers = renderers ?? throw new ArgumentNullException(nameof(renderers));
        _reportHandledFailure = reportHandledFailure ?? throw new ArgumentNullException(nameof(reportHandledFailure));
    }

    /// <summary>
    /// Detaches the nested content, detaches the page and waits for it to
    /// unload, shuts down the shared providers when no renderer is alive, and
    /// then always closes the window.
    /// </summary>
    /// <param name="detachNestedContentAsync">
    /// Removes content nested in the page and waits, bounded by the given
    /// timeout, for it to unload; <see langword="null"/> when the page has none.
    /// </param>
    /// <param name="detachPageAsync">
    /// Removes the window's page and waits, bounded by the given timeout, for it to unload.
    /// </param>
    /// <param name="shutdownProvidersAsync">Retires the shared Markdown providers.</param>
    /// <param name="close">Closes the window. It runs last, even when an earlier step failed.</param>
    public async Task RetireViewsThenCloseAsync(
        Func<TimeSpan, Task>? detachNestedContentAsync,
        Func<TimeSpan, Task> detachPageAsync,
        Func<Task> shutdownProvidersAsync,
        Action close)
    {
        ArgumentNullException.ThrowIfNull(detachPageAsync);
        ArgumentNullException.ThrowIfNull(shutdownProvidersAsync);
        ArgumentNullException.ThrowIfNull(close);

        try
        {
            await new MarkdownViewShutdown(_renderers, ViewUnloadTimeout, _reportHandledFailure).RunAsync(
                detachNestedContentAsync is null ? null : () => detachNestedContentAsync(ViewUnloadTimeout),
                () => detachPageAsync(ViewUnloadTimeout),
                shutdownProvidersAsync);
        }
        finally
        {
            close();
        }
    }
}
