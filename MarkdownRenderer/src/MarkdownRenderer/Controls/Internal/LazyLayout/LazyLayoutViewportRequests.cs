using System.Diagnostics;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.LazyLayout;

namespace MarkdownRenderer.Controls.Internal.LazyLayout;

/// <summary>
/// Turns viewport and paint-region requests into lazy-layout band requests.
/// It picks the band (widened in the scroll direction when a performance
/// session opted in, which is when it samples scroll velocity), skips bands
/// whose work is already settled, and hands the rest to
/// <see cref="LazyLayoutRealization.QueueForBand"/>.
/// </summary>
internal sealed class LazyLayoutViewportRequests
{
    private readonly LazyLayoutRealization _realization;
    private readonly LazyLayoutDocumentPrefetch _prefetch;
    private readonly ILazyLayoutHost _host;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly VectorCapacityRetry _vectorCapacity;

    private double _lastViewportTop;
    private long _lastViewportTimestamp;
    private double _scrollVelocity;

    internal LazyLayoutViewportRequests(
        LazyLayoutRealization realization,
        LazyLayoutDocumentPrefetch prefetch,
        ILazyLayoutHost host,
        PublishedLayout published,
        MarkdownViewSurface surface,
        MarkdownViewport viewport,
        VectorCapacityRetry vectorCapacity)
    {
        _realization = realization;
        _prefetch = prefetch;
        _host = host;
        _published = published;
        _surface = surface;
        _viewport = viewport;
        _vectorCapacity = vectorCapacity;
    }

    /// <summary>
    /// The latest viewport scroll velocity, in DIPs per second. Measured only
    /// while a performance session is attached.
    /// </summary>
    internal double ScrollVelocity => _scrollVelocity;

    /// <summary>Forgets the scroll velocity, so the next viewport sample starts fresh.</summary>
    internal void ResetScrollVelocity()
    {
        _lastViewportTimestamp = 0;
        _scrollVelocity = 0;
    }

    /// <summary>
    /// Queues measurement of the band around the current viewport, widened
    /// in the scroll direction when a performance session opted in, unless
    /// that band is already settled.
    /// </summary>
    internal void QueueForViewport(bool forceCapacityRetry = false)
    {
        var snapshot = _published.Snapshot;
        if (_host.RequestGeometryRecoveryIfRequired(snapshot) || snapshot is null)
            return;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand visibleVectorBand = LazyLayoutBand.FromVisibleViewport(
            viewportTop, viewportHeight);
        LazyLayoutBand band;
        if (_host.PerformanceSession is { } performanceSession)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastViewportTimestamp > 0)
            {
                double elapsedSeconds = (now - _lastViewportTimestamp) /
                    (double)Stopwatch.Frequency;
                if (elapsedSeconds is > 0.002 and < 1)
                {
                    _scrollVelocity =
                        (viewportTop - _lastViewportTop) / elapsedSeconds;
                }
            }
            _lastViewportTop = viewportTop;
            _lastViewportTimestamp = now;
            band = LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _scrollVelocity);
        }
        else
        {
            band = LazyLayoutBand.FromViewport(
                viewportTop, viewportHeight, MarkdownLazyLayoutPolicy.OverscanPx);
        }

        // Directional look-ahead and image realization can be broader than the
        // viewport. Keep vector admission tied to the actual viewport rather
        // than letting an unchanged prefetch union absorb a changed viewport.
        band = LazyLayoutBand.Union(band, visibleVectorBand);

        bool capacityAdmissionEnabled = _vectorCapacity.IsAdmissionEnabled;
        bool capacityDeferred = capacityAdmissionEnabled && snapshot.HasCapacityDeferredScenes;
        if (!snapshot.IsLazyLayoutEnabled && !capacityDeferred)
            return;

        if (capacityAdmissionEnabled)
        {
            _vectorCapacity.Synchronize(snapshot);
            forceCapacityRetry |= _vectorCapacity.ConsumePendingWake(snapshot);
        }

        bool capacityRetryDue = capacityDeferred && _vectorCapacity.IsRetryDue(
            snapshot, visibleVectorBand, forceCapacityRetry);
        if (capacityDeferred && !capacityRetryDue &&
            snapshot.IsRealizationBandMeasured(band))
        {
            return;
        }

        if (snapshot.IsBandWorkSettled(band, visibleVectorBand) && !capacityRetryDue)
        {
            // The viewport is ready: spend the spare capacity on the rest of
            // the document so a later jump finds its layout measured.
            _prefetch.Queue();
            return;
        }

        _realization.QueueForBand(
            snapshot,
            band,
            visibleVectorBand,
            preserveScrollAnchor: _surface.Scroll is not null);
    }
}
