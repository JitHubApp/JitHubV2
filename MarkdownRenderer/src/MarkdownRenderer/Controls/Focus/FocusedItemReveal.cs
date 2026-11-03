using System;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Scrolls the focused item into view after keyboard focus moves. A reveal
/// remembers the viewport it started from; a newer scroll request or a
/// viewport the reveal did not set ends it, so geometry retries never
/// scroll back over a scroll the user or automation asked for.
/// </summary>
internal sealed class FocusedItemReveal
{
    private readonly IFocusedItemRevealHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly KeyboardFocusModel _focus;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly DispatcherQueue? _dispatcher;

    // Counts explicit scroll requests so a delayed focus reveal
    // never overrides a newer one, even before its view change applies.
    private long _viewportRequestSerial;
    private FocusRevealOrigin? _focusReveal;

    internal FocusedItemReveal(
        IFocusedItemRevealHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        KeyboardFocusModel focus,
        HorizontalOverflowScrollVisuals overflowScroll,
        DispatcherQueue? dispatcher)
    {
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _focus = focus;
        _overflowScroll = overflowScroll;
        _dispatcher = dispatcher;
    }

    /// <summary>Records an explicit scroll request; pending focus reveals yield to it.</summary>
    internal void NoteViewportRequest() => _viewportRequestSerial++;

    /// <summary>
    /// Starts revealing the focused item from the current viewport. Geometry
    /// retries continue it only while <see cref="IsCurrent"/> holds.
    /// </summary>
    internal bool Begin()
    {
        _viewport.TryGet(out double top, out _, out _);
        _focusReveal = new FocusRevealOrigin(_viewportRequestSerial, top, null);
        return ScrollIntoView();
    }

    /// <summary>True while the reveal still owns the viewport; forgets it otherwise.</summary>
    internal bool IsCurrent()
    {
        _viewport.TryGet(out double top, out _, out _);
        if (FocusRevealPolicy.IsCurrent(_focusReveal, _viewportRequestSerial, top))
            return true;
        _focusReveal = null;
        return false;
    }

    /// <summary>
    /// Scrolls a horizontal overflow region and then the viewport so the
    /// focused item is visible. Returns true when either scrolled.
    /// </summary>
    /// <remarks>
    /// The horizontal scroll and the read of the rectangle it produces share
    /// one snapshot lease, and the vertical scroll must use that rectangle,
    /// so the steps stay in one method.
    /// </remarks>
    internal bool ScrollIntoView()
    {
        var items = _focus.Items;
        int focusedIndex = _focus.FocusedIndex;
        if (items is null || focusedIndex < 0 || focusedIndex >= items.Count) return false;
        var item = items[_focus.FocusedIndex];
        if (_published.Snapshot is not { } snapshot)
            return false;

        long generation = _published.Generation;
        long revision = snapshot.LayoutRevision;
        if (!snapshot.TryBeginInteraction())
        {
            _host.RequestFocusGeometryRetry();
            return false;
        }

        Rect? capturedRect = null;
        HorizontalOverflowVisualState visualState = default;
        bool horizontalScrolled = false;
        double oldHorizontalOffset = 0;
        bool captured = false;
        try
        {
            if (IsCurrentTarget(snapshot, generation, revision) &&
                focusedIndex < items.Count &&
                FocusableItemQueries.SameFocusableIdentity(items[focusedIndex], item))
            {
                capturedRect = FocusableItemQueries.GetLogicalRectUnderPaint(snapshot, item);
                captured = true;
                if (capturedRect is { } rect &&
                    snapshot.TryGetHorizontalOverflowForBlockIndex(item.BlockIndex, out var overflow) &&
                    overflow is not null)
                {
                    Rect localViewport = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                        item.BlockIndex, overflow.HorizontalViewportBounds);
                    const double localMargin = 8.0;
                    double physicalShift = 0;
                    if (rect.Left < localViewport.Left + localMargin)
                        physicalShift = localViewport.Left + localMargin - rect.Left;
                    else if (rect.Right > localViewport.Right - localMargin)
                        physicalShift = localViewport.Right - localMargin - rect.Right;

                    if (Math.Abs(physicalShift) > 0.1)
                    {
                        oldHorizontalOffset = overflow.HorizontalOffset;
                        double logicalShift = overflow.IsRightToLeft ? physicalShift : -physicalShift;
                        if (snapshot.TrySetHorizontalOffset(overflow, oldHorizontalOffset + logicalShift))
                        {
                            horizontalScrolled = true;
                            _ = _overflowScroll.TryCaptureUnderLease(
                                snapshot, generation, revision, overflow, oldHorizontalOffset, out visualState);
                            capturedRect = FocusableItemQueries.GetLogicalRectUnderPaint(snapshot, item) ?? rect;
                        }
                    }
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!captured || !IsCurrentTarget(snapshot, generation, revision))
        {
            _host.RequestFocusGeometryRetry();
            return false;
        }

        if (horizontalScrolled)
        {
            if (visualState.Snapshot is not null)
                _overflowScroll.Publish(visualState);
            else
                _overflowScroll.QueueRetry(snapshot, generation, item.BlockIndex,
                    oldHorizontalOffset);
        }

        if (capturedRect is not { } detachedRect)
            return horizontalScrolled;

        _viewport.TryGet(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        const double margin = 24.0;
        bool verticalNeeded = detachedRect.Top < top + margin || detachedRect.Bottom > bottom - margin;
        if (verticalNeeded && !QueueScroll(snapshot, generation, revision, item, detachedRect))
            _host.RequestFocusGeometryRetry();
        return horizontalScrolled || verticalNeeded;
    }

    private bool QueueScroll(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        Layout.FocusableItem item,
        Rect rect)
    {
        var weakSnapshot = new WeakReference<LayoutSnapshot>(snapshot);
        try
        {
            return _dispatcher is { } dispatcher && dispatcher.TryEnqueue(() =>
                ApplyScroll(weakSnapshot, snapshotGeneration, layoutRevision, item, rect));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ApplyScroll(
        WeakReference<LayoutSnapshot> weakSnapshot,
        long snapshotGeneration,
        long layoutRevision,
        Layout.FocusableItem item,
        Rect rect)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !weakSnapshot.TryGetTarget(out LayoutSnapshot? snapshot) ||
            !ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation)
        {
            return;
        }
        if (snapshot.LayoutRevision != layoutRevision)
        {
            _host.RequestFocusGeometryRetry();
            return;
        }
        if (_focus.Current is not { } current ||
            !FocusableItemQueries.SameFocusableIdentity(current, item))
            return;
        if (!IsCurrent())
            return;

        _viewport.TryGet(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        const double margin = 24.0;
        double? revealTop = null;
        if (rect.Top < top + margin)
        {
            revealTop = _surface.Scroll is not null ? Math.Max(0, rect.Top - margin) : rect.Top;
            _ = _surface.Scroll is not null
                ? _surface.Scroll.ChangeView(null, Math.Max(0, rect.Top - margin), null, disableAnimation: false)
                : _surface.BringIntoExternalViewport(rect, animate: true, alignmentRatio: 0);
        }
        else if (rect.Bottom > bottom - margin)
        {
            revealTop = _surface.Scroll is not null
                ? rect.Bottom - viewportHeight + margin
                : rect.Bottom - viewportHeight;
            _ = _surface.Scroll is not null
                ? _surface.Scroll.ChangeView(null, rect.Bottom - viewportHeight + margin, null, disableAnimation: false)
                : _surface.BringIntoExternalViewport(rect, animate: true, alignmentRatio: 1);
        }
        // The reveal's own animated scroll toward this top keeps it current.
        // Owned: ChangeView's target. Ancestor: StartBringIntoView aligns the
        // rect's top (ratio 0) or bottom (ratio 1) to the viewport edge.
        if (revealTop is { } requested && _focusReveal is { } reveal)
            _focusReveal = reveal with { RevealTop = requested };
    }

    /// <summary>
    /// Scrolls the estimated band of a Tab target that lazy layout has not
    /// measured yet into view.
    /// </summary>
    internal void ScrollLazyTargetIntoView(LazyLayoutBand band, bool reverse)
    {
        _viewport.TryGet(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        const double margin = 24;
        if (band.Top >= viewportTop + margin && band.Bottom <= viewportBottom - margin)
            return;

        if (_surface.Scroll is not null)
        {
            double target = reverse
                ? band.Bottom - viewportHeight + margin
                : band.Top - margin;
            double maximum = Math.Max(0, (_published.Snapshot?.Size.Height ?? band.Bottom) - viewportHeight);
            _surface.Scroll.ChangeView(
                null,
                Math.Clamp(target, 0, maximum),
                null,
                disableAnimation: false);
            return;
        }

        var targetRect = new Rect(
            0,
            band.Top,
            Math.Max(1, viewportWidth),
            Math.Max(1, band.Bottom - band.Top));
        _surface.BringIntoExternalViewport(
            targetRect,
            animate: true,
            alignmentRatio: reverse ? 1 : 0);
    }

    private bool IsCurrentTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        _published.IsCurrent(snapshot, snapshotGeneration, layoutRevision);
}
