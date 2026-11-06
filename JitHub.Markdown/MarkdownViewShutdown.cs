using System;
using System.Threading.Tasks;

namespace JitHub.Markdown;

/// <summary>
/// The window close path's Markdown teardown. Pages own the Markdown views and
/// the views borrow the process-wide Markdown providers, so the views retire
/// first: this removes the page content (content nested in the page, such as
/// the shell's frame, and then the page itself), drains the renderer ledger
/// after each removal, and shuts the providers down only when no renderer is
/// still alive. A renderer that outlived the drain may still call into the
/// providers, so they are then left for process teardown to reclaim.
/// </summary>
public sealed class MarkdownViewShutdown
{
    private readonly MarkdownRendererLifetimeRegistry _renderers;
    private readonly TimeSpan _rendererDisposalTimeout;
    private readonly Action<Exception, string> _reportHandledFailure;

    /// <summary>Creates the teardown for one window close.</summary>
    /// <param name="renderers">The app's renderer ledger.</param>
    /// <param name="rendererDisposalTimeout">How long each drain waits for renderers to retire.</param>
    /// <param name="reportHandledFailure">Reports a failed step, with its category, without stopping the close.</param>
    public MarkdownViewShutdown(
        MarkdownRendererLifetimeRegistry renderers,
        TimeSpan rendererDisposalTimeout,
        Action<Exception, string> reportHandledFailure)
    {
        _renderers = renderers ?? throw new ArgumentNullException(nameof(renderers));
        _rendererDisposalTimeout = rendererDisposalTimeout;
        _reportHandledFailure = reportHandledFailure ?? throw new ArgumentNullException(nameof(reportHandledFailure));
    }

    /// <summary>
    /// Removes the page content, drains its renderers and then, when every
    /// renderer retired, shuts down the shared providers. Failed steps are
    /// reported and the close goes on.
    /// </summary>
    /// <param name="detachNestedContentAsync">
    /// Removes content nested in the page, or <see langword="null"/> when the page has none.
    /// </param>
    /// <param name="detachContentAsync">Removes the page and waits, bounded, for it to unload.</param>
    /// <param name="shutdownProvidersAsync">Retires the shared Markdown providers.</param>
    public async Task RunAsync(
        Func<Task>? detachNestedContentAsync,
        Func<Task> detachContentAsync,
        Func<Task> shutdownProvidersAsync)
    {
        ArgumentNullException.ThrowIfNull(detachContentAsync);
        ArgumentNullException.ThrowIfNull(shutdownProvidersAsync);

        bool renderersRetired = false;
        try
        {
            if (detachNestedContentAsync is not null)
            {
                try
                {
                    await detachNestedContentAsync();
                }
                catch (Exception exception)
                {
                    _reportHandledFailure(exception, "markdown-shell-content-shutdown");
                }

                renderersRetired = await DrainRenderersAsync();
            }

            try
            {
                await detachContentAsync();
            }
            finally
            {
                renderersRetired = await DrainRenderersAsync();
            }
        }
        catch (Exception exception)
        {
            _reportHandledFailure(exception, "markdown-view-shutdown");
        }

        if (!renderersRetired)
            return;

        try
        {
            await shutdownProvidersAsync();
        }
        catch (Exception exception)
        {
            _reportHandledFailure(exception, "markdown-runtime-shutdown");
        }
    }

    // Waits for the removed views' renderers, then retires any host that is
    // still registered. Only an empty ledger lets the providers shut down.
    private async Task<bool> DrainRenderersAsync()
    {
        await _renderers.WaitForRendererDisposalAsync(_rendererDisposalTimeout);
        try
        {
            _renderers.PrepareAllForApplicationShutdown();
        }
        catch (Exception exception)
        {
            _reportHandledFailure(exception, "markdown-renderer-shutdown");
        }

        return _renderers.ActiveRendererCount == 0;
    }
}
