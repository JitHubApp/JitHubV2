using System;
using MarkdownRenderer.Layout;
using Microsoft.UI.Input;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Decides when a recognized gesture has the measured geometry it needs.
/// A tap, hold or right-tap over lazily measured layout waits until its
/// hit-test band is measured and published, then is reprojected from
/// viewport coordinates onto the current canvas before it runs.
/// </summary>
internal sealed class DeferredGestureAdmission
{
    private readonly IDeferredGestureLayoutHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly DocumentPointerInteraction _document;

    internal DeferredGestureAdmission(
        IDeferredGestureLayoutHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        TouchTapSuppressionPolicy touchTapSuppression,
        DocumentPointerInteraction document)
    {
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _touchTapSuppression = touchTapSuppression;
        _document = document;
    }

    /// <summary>
    /// True when <paramref name="input"/> hit-tests layout-owned text and so
    /// must wait for measured geometry instead of an estimated root.
    /// </summary>
    internal bool IsMeasuredHitTestGesture(PointerInput input)
    {
        bool candidate = input.Kind switch
        {
            PointerInputKind.Tap => input.GestureDevice == PointerDeviceType.Touch,
            PointerInputKind.DoubleTap => input.GestureDevice == PointerDeviceType.Touch,
            PointerInputKind.Hold => input.GestureDevice is PointerDeviceType.Touch or PointerDeviceType.Pen,
            // Right-click menus also target layout-owned text and must not be
            // placed against an estimated/unpublished root. Contact geometry
            // is consulted only for touch/pen by IncludesContactProbes.
            PointerInputKind.RightTap => true,
            _ => false,
        };
        if (!candidate)
            return false;
        if (input.Kind == PointerInputKind.Tap &&
            _touchTapSuppression.ShouldSuppressTap(input.TouchTapGestureEpoch, Environment.TickCount64, input.GesturePosition))
            return false;
        return !_host.IsPointOverEmbed(input.GesturePosition) &&
            !_host.IsPointOverCodeBlockAction(input.GesturePosition);
    }

    /// <summary>
    /// Finds the band <paramref name="input"/> hit-tests. Returns false when
    /// the gesture needs no measured band; otherwise <paramref name="ready"/>
    /// says whether that band is measured and published for
    /// <paramref name="snapshot"/>.
    /// </summary>
    internal bool TryGetAdmissionBand(
        ref PointerInput input,
        LayoutSnapshot snapshot,
        out LazyLayoutBand band,
        out bool ready)
    {
        ready = true;
        band = default;
        input.WaitForLayoutPublication = false;
        if (input.WaitForMeasuredHitTest && _host.IsLayoutPublicationPending(snapshot))
        {
            // A worker's measured mask is not enough: scroll anchoring and
            // realization-plan publication still need to commit on the UI
            // thread before a queued canvas-local hit can be replayed.
            input.WaitForLayoutPublication = true;
            ready = false;
            return true;
        }

        if (!TryGetHitTestBand(ref input, out LazyLayoutBand gestureBand))
            return false;

        band = gestureBand;
        if (!snapshot.IsInputHitTestBandMeasuredUnderInteraction(gestureBand))
        {
            ready = false;
            return true;
        }

        // The production hit-test is ordered: a selectable direct gesture hit
        // returns before the wider touch-contact fallback probes. Do not delay
        // that common case on neighboring, unmeasured contact roots.
        if (!GestureHitTestBands.IncludesContactProbes(input) ||
            _document.IsSelectableTouchPoint(input.GesturePosition, snapshot, out _))
        {
            return true;
        }

        if (!GestureHitTestBands.TryCreateGestureHitTestBand(
                input.GesturePosition,
                _host.RasterizationScale,
                includeContact: true,
                capturedContact: input.GestureContact,
                band: out LazyLayoutBand contactBand))
        {
            return true;
        }

        band = contactBand;
        ready = snapshot.IsInputHitTestBandMeasuredUnderInteraction(contactBand);
        return true;
    }

    /// <summary>
    /// True when <paramref name="snapshot"/> is the published layout of a
    /// live renderer and its lazy layout has been applied on the UI thread.
    /// </summary>
    internal bool IsLayoutPublished(LayoutSnapshot snapshot) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(snapshot, _published.Snapshot) &&
        !_host.IsLayoutPublicationPending(snapshot);

    /// <summary>
    /// Maps a deferred gesture's viewport position and contact back onto the
    /// current canvas. Returns false, and marks the projection invalid, when
    /// the viewport can no longer map it.
    /// </summary>
    internal bool TryReproject(ref PointerInput input)
    {
        if (!input.HasViewportGesturePosition ||
            !_viewport.TryMapViewportToCanvas(input.ViewportGesturePosition, out var currentPosition))
        {
            input.InvalidDeferredProjection = true;
            return false;
        }
        input.GesturePosition = currentPosition;
        if (input.HasViewportGestureContact &&
            _viewport.TryMapViewportContactToCanvas(input.ViewportGestureContact, out TouchContactGeometry contact))
        {
            input.GestureContact = contact;
            input.HasGestureContact = true;
        }
        else
        {
            input.GestureContact = default;
            input.HasGestureContact = false;
        }
        return true;
    }

    /// <summary>
    /// Makes sure lazy layout will measure <paramref name="hitBand"/>, widened
    /// to the visible viewport. Returns false when no request covers the band,
    /// so the waiting gesture can never become ready.
    /// </summary>
    internal bool TryRequestBandRealization(LayoutSnapshot snapshot, LazyLayoutBand hitBand)
    {
        if (_host.IsLazyLayoutBandCovered(snapshot, hitBand))
            return true;

        LazyLayoutBand viewportBand = hitBand;
        if (_viewport.TryGet(out double viewportTop, out double viewportHeight, out _) &&
            double.IsFinite(viewportTop) && double.IsFinite(viewportHeight) &&
            viewportHeight > 0 && viewportHeight <= 6000)
        {
            viewportBand = LazyLayoutBand.FromVisibleViewport(viewportTop, viewportHeight);
        }
        var requestedBand = new LazyLayoutBand(
            Math.Min(hitBand.Top, viewportBand.Top),
            Math.Max(hitBand.Bottom, viewportBand.Bottom));
        _host.QueueLazyLayoutForBand(
            snapshot,
            requestedBand,
            viewportBand,
            preserveScrollAnchor: _surface.Scroll is not null);

        // QueueLazyLayoutForBand may decline the request when its recovery
        // clock/dispatcher handoff is unavailable.
        return _host.IsLazyLayoutBandCovered(snapshot, hitBand);
    }

    private bool TryGetHitTestBand(ref PointerInput input, out LazyLayoutBand band)
    {
        band = default;
        if (input.IsDeferredGeometryWait && !TryReproject(ref input))
            return false;
        if (!input.WaitForMeasuredHitTest)
            return false;

        if (!IsMeasuredHitTestGesture(input))
        {
            input.WaitForMeasuredHitTest = false;
            return false;
        }

        return GestureHitTestBands.TryCreateGestureHitTestBand(
            input.GesturePosition,
            _host.RasterizationScale,
            includeContact: false,
            capturedContact: input.GestureContact,
            band: out band);
    }
}
