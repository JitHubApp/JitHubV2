using System;
using System.Diagnostics;
using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Selection.Adorner;
using MarkdownRenderer.Diagnostics.Internal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Controls.Internal.View;

/// <summary>
/// Follows the viewport: the owned scroll viewer's view changes, or the
/// ancestor viewport's effective-viewport changes. Each change queues lazy
/// layout, refreshes the text adorner when it has state that can move,
/// realises visible embeds and schedules visible work.
/// </summary>
internal sealed class ScrollViewTracking
{
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewport _viewport;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly InteractiveTextAdorner _textAdorner;
    private readonly EmbedRealization _embeds;
    private readonly VisibleWorkScheduler _visibleWork;
    private bool _isIntermediate;

    internal ScrollViewTracking(
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        MarkdownViewport viewport,
        LazyLayoutRealization lazyLayout,
        SelectionAdornerPresenter adorner,
        InteractiveTextAdorner textAdorner,
        EmbedRealization embeds,
        VisibleWorkScheduler visibleWork)
    {
        _surface = surface;
        _lifetime = lifetime;
        _viewport = viewport;
        _lazyLayout = lazyLayout;
        _adorner = adorner;
        _textAdorner = textAdorner;
        _embeds = embeds;
        _visibleWork = visibleWork;
    }

    /// <summary>True while the owned scroll viewer reports an intermediate (gesture or inertia) view.</summary>
    internal bool IsIntermediate => _isIntermediate;

    internal void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (_surface.OwnsScrollViewport || _lifetime.IsDisposed)
            return;

        if (!_viewport.TryAcceptExternal(args.EffectiveViewport, out bool materiallyChanged) ||
            !materiallyChanged || _lifetime.IsUnloaded)
            return;

        _lazyLayout.QueueForViewport();
        _adorner.InvalidateGeometry();
        _adorner.Invalidate();
        _embeds.RealizeVisible();
        _visibleWork.ScheduleCodeBlockHighlighting();
    }

    /// <summary>
    /// Subscribes to the owned scroll viewer's view changes, replacing an
    /// earlier subscription, after a replacement is realised.
    /// </summary>
    internal void Attach()
    {
        if (_surface.Scroll is not null)
        {
            _surface.Scroll.ViewChanged -= OnViewChanged;
            _surface.Scroll.ViewChanged += OnViewChanged;
        }
    }

    /// <summary>
    /// Unsubscribes so scroll-inertia events after visual-tree removal don't
    /// reach a partially-torn-down control, and forgets the intermediate state.
    /// </summary>
    internal void Detach()
    {
        if (_surface.Scroll is not null) _surface.Scroll.ViewChanged -= OnViewChanged;
        _isIntermediate = false;
    }

    /// <summary>A replacement for another document starts outside any scroll gesture.</summary>
    internal void ResetIntermediate() => _isIntermediate = false;

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _isIntermediate = e.IsIntermediate;
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long afterLazyLayout = allocatedBefore;
        long afterAdorner = allocatedBefore;
        long afterRealization = allocatedBefore;
        try
        {
            _lazyLayout.QueueForViewport();
            if (measure)
                afterLazyLayout = GC.GetAllocatedBytesForCurrentThread();
            // CanvasVirtualControl already requests newly exposed document
            // tiles. Only viewport-refresh the separate text adorner when it
            // has visible, pending, or interactive state that can change.
            // UpdateSelectionAdornerViewport includes the invalidation, so do
            // not issue a second request for the same scroll callback.
            if (_textAdorner.ShouldRefreshForScroll())
                _adorner.InvalidateGeometry();
            if (measure)
                afterAdorner = GC.GetAllocatedBytesForCurrentThread();
            // Run a final realisation pass after intermediate-view bursts settle
            // so we don't thrash during fling/inertia. ViewChanged with
            // IsIntermediate=false fires at the end of inertia; we also realise on
            // intermediate ticks to keep the visual current.
            _embeds.RealizeVisible();
            if (measure)
                afterRealization = GC.GetAllocatedBytesForCurrentThread();
            _visibleWork.ScheduleCodeBlockHighlighting();
        }
        finally
        {
            if (measure)
            {
                long allocated = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                performance.ScrollWork(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Stopwatch.GetTimestamp() - started,
                    allocated);
                performance.ScrollAllocationBreakdown(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Math.Max(0, afterLazyLayout - allocatedBefore),
                    Math.Max(0, afterAdorner - afterLazyLayout),
                    Math.Max(0, afterRealization - afterAdorner),
                    Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - afterRealization));
                _embeds.GetCodeBlockActionRealizationCounts(out int realizedActions, out int offscreenActions);
                performance.CodeActionRealization(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    realizedActions,
                    offscreenActions);
            }
        }
    }
}
