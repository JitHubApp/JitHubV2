using System;
using MarkdownRenderer.Controls.Focus;
using Microsoft.UI.Xaml.Automation;
using Windows.Foundation;

namespace MarkdownRenderer.Controls;

/// <summary>
/// UI Automation's scroll pattern and ScrollIntoView requests. Every request
/// is immediate: an animated change can be superseded by lazy-layout
/// realization before its first frame.
/// </summary>
internal sealed class AutomationScrolling
{
    private const double SmallStep = 48;
    private const double LargeStepViewportRatio = 0.8;
    private const double IntoViewMargin = 24;

    private readonly MarkdownViewSurface _surface;
    private readonly FocusedItemReveal _reveal;

    internal AutomationScrolling(MarkdownViewSurface surface, FocusedItemReveal reveal)
    {
        _surface = surface;
        _reveal = reveal;
    }

    /// <summary>
    /// Reads the owned scroll viewer's metrics. Returns false, with zeros,
    /// when the renderer follows an ancestor viewport.
    /// </summary>
    internal bool TryGetMetrics(
        out double verticalOffset,
        out double viewportHeight,
        out double extentHeight)
    {
        if (_surface.Scroll is null)
        {
            verticalOffset = 0;
            viewportHeight = 0;
            extentHeight = 0;
            return false;
        }

        verticalOffset = _surface.Scroll.VerticalOffset;
        viewportHeight = _surface.Scroll.ViewportHeight;
        extentHeight = _surface.Scroll.ExtentHeight;
        return true;
    }

    internal void Scroll(ScrollAmount amount)
    {
        if (_surface.Scroll is null || amount == ScrollAmount.NoAmount)
            return;
        _reveal.NoteViewportRequest();

        double largeStep = Math.Max(SmallStep, _surface.Scroll.ViewportHeight * LargeStepViewportRatio);
        double delta = amount switch
        {
            ScrollAmount.LargeDecrement => -largeStep,
            ScrollAmount.SmallDecrement => -SmallStep,
            ScrollAmount.SmallIncrement => SmallStep,
            ScrollAmount.LargeIncrement => largeStep,
            _ => 0,
        };
        double maximum = Math.Max(0, _surface.Scroll.ExtentHeight - _surface.Scroll.ViewportHeight);
        double target = Math.Clamp(_surface.Scroll.VerticalOffset + delta, 0, maximum);
        _surface.Scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    internal void SetVerticalPercent(double percent)
    {
        if (_surface.Scroll is null)
            return;
        _reveal.NoteViewportRequest();

        double maximum = Math.Max(0, _surface.Scroll.ExtentHeight - _surface.Scroll.ViewportHeight);
        double target = maximum * (Math.Clamp(percent, 0, 100) / 100);
        _surface.Scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    /// <summary>
    /// Brings a document rect into view, aligned to the top or scrolled by
    /// the least distance that shows it with a margin.
    /// </summary>
    internal void ScrollIntoView(Rect rect, bool alignToTop)
    {
        _reveal.NoteViewportRequest();
        if (_surface.Scroll is null)
        {
            _surface.BringIntoExternalViewport(rect, animate: false, alignToTop ? 0 : 0.5);
            return;
        }
        double target = alignToTop
            ? rect.Top
            : rect.Top < _surface.Scroll.VerticalOffset + IntoViewMargin
                ? rect.Top - IntoViewMargin
                : rect.Bottom > _surface.Scroll.VerticalOffset + _surface.Scroll.ViewportHeight - IntoViewMargin
                    ? rect.Bottom - _surface.Scroll.ViewportHeight + IntoViewMargin
                    : _surface.Scroll.VerticalOffset;
        _surface.Scroll.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }
}
