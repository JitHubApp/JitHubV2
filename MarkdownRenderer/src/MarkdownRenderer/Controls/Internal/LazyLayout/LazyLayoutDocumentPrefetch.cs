using System;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.LazyLayout;

/// <summary>
/// Measures the rest of the document while the viewport is settled, one
/// band at a time nearest the viewport and below it first, so a later
/// scroll or jump finds its layout already measured. It runs only when the
/// performance session opts in and no other measurement is pending; a
/// viewport request makes it yield at the next root boundary.
/// </summary>
internal sealed class LazyLayoutDocumentPrefetch
{
    /// <summary>Viewports of layout one prefetch band measures at most.</summary>
    private const double BandViewports = 4;

    /// <summary>
    /// The least height, in DIPs, one prefetch band measures, so a small
    /// viewport does not turn the document into many tiny commits.
    /// </summary>
    private const double MinimumBandHeight = 4000;

    private readonly LazyLayoutRealization _realization;
    private readonly ILazyLayoutHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;

    internal LazyLayoutDocumentPrefetch(
        LazyLayoutRealization realization,
        ILazyLayoutHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewSurface surface,
        MarkdownViewport viewport)
    {
        _realization = realization;
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _surface = surface;
        _viewport = viewport;
    }

    private bool IsEnabled => _host.PerformanceSession is { Options.PrefetchDocumentLayout: true };

    /// <summary>
    /// Queues measurement of the unmeasured layout nearest the viewport when
    /// no other request is pending or running.
    /// </summary>
    internal void Queue()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !IsEnabled || _realization.HasPendingWork ||
            _published.Snapshot is not { IsLazyLayoutEnabled: true } snapshot ||
            !_viewport.TryGet(out double viewportTop, out double viewportHeight, out _))
        {
            return;
        }

        LazyLayoutBand visible = LazyLayoutBand.FromVisibleViewport(viewportTop, viewportHeight);
        double height = Math.Max(MinimumBandHeight, viewportHeight * BandViewports);
        if (!snapshot.TryFindUnmeasuredBand(visible.Top, visible.Bottom, height, out LazyLayoutBand band))
            return;

        _realization.QueueForBand(snapshot, band, visible, preserveScrollAnchor: _surface.Scroll is not null);
    }

    /// <summary>
    /// Continues prefetch once the current completion's callbacks finish and
    /// input and rendering had their turn.
    /// </summary>
    internal void Dispatch()
    {
        if (!IsEnabled || _host.UiScheduler is not { } dispatcher)
            return;

        var receiver = new WeakReference<LazyLayoutDocumentPrefetch>(this);
        try
        {
            _ = dispatcher.TryEnqueue(
                MarkdownUiPriority.Low,
                () =>
                {
                    if (receiver.TryGetTarget(out var prefetch))
                        prefetch.Queue();
                });
        }
        catch (Exception exception)
        {
            // Prefetch is an optimization; the next viewport change retries it.
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] document layout prefetch dispatch failed: {exception.GetType().Name} " +
                $"(0x{exception.HResult:X8}).");
        }
    }
}
