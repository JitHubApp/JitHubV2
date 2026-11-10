using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Shows an abbreviation's expansion in a tooltip above the hovered run.
/// </summary>
/// <remarks>
/// The tooltip opens after a short hover delay and closes after a short
/// delay once the pointer leaves, so moving across a run's edge does not
/// flicker it. The tooltip is attached to the Win2D canvas, which is
/// recreated on every load, and only exists while it is open.
/// </remarks>
internal sealed class AbbreviationTooltipPresenter
{
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(200);

    private readonly DependencyObject _dispatcherOwner;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PointerHoverState _hover;

    private ToolTip? _toolTip;
    private DispatcherQueueTimer? _showTimer;
    private DispatcherQueueTimer? _hideTimer;
    private AbbreviationRun? _shown;
    private AbbreviationRun? _pending;
    private Rect _pendingPlacementRect;

    /// <param name="dispatcherOwner">
    /// The renderer, whose dispatcher queue runs the show and hide timers.
    /// </param>
    /// <param name="surface">The visual tree whose canvas anchors the tooltip.</param>
    /// <param name="lifetime">Suppresses showing once the renderer unloads.</param>
    /// <param name="hover">
    /// The pointer's hover state. A delayed show proceeds only while its
    /// abbreviation is still hovered.
    /// </param>
    public AbbreviationTooltipPresenter(
        DependencyObject dispatcherOwner,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PointerHoverState hover)
    {
        _dispatcherOwner = dispatcherOwner;
        _surface = surface;
        _lifetime = lifetime;
        _hover = hover;
    }

    /// <summary>
    /// Moves the tooltip to follow the run under the pointer: schedules it
    /// for a newly hovered abbreviation, keeps it for the same one, and
    /// schedules a hide otherwise.
    /// </summary>
    public void Update(InlineRun? hoveredRun, InlineContainerBox? hoveredBox, Point pointerPoint)
    {
        var abbreviation = hoveredRun as AbbreviationRun;
        if (abbreviation is null ||
            string.IsNullOrWhiteSpace(abbreviation.Expansion) ||
            _surface.Canvas is null ||
            hoveredBox is null ||
            !hoveredBox.TryGetRunBounds(abbreviation, pointerPoint, out var placementRect))
        {
            ScheduleHide();
            return;
        }

        CancelHide();
        if (ReferenceEquals(abbreviation, _shown))
        {
            CancelShow();
            return;
        }

        if (ReferenceEquals(abbreviation, _pending))
        {
            _pendingPlacementRect = placementRect;
            return;
        }

        if (_shown is not null)
            Hide();

        ScheduleShow(abbreviation, placementRect);
    }

    /// <summary>Closes the tooltip after the hide delay.</summary>
    public void ScheduleHide()
    {
        CancelShow();
        _pending = null;

        if (_toolTip is null)
        {
            _shown = null;
            return;
        }

        var timer = EnsureHideTimer();
        if (timer is null)
        {
            Hide();
            return;
        }

        timer.Stop();
        timer.Interval = HideDelay;
        timer.Start();
    }

    /// <summary>Closes the tooltip now and cancels any pending show.</summary>
    public void Hide()
    {
        CancelShow();
        CancelHide();
        _pending = null;
        Close();
        _shown = null;
    }

    /// <summary>
    /// Forgets which abbreviation is shown or pending without closing the
    /// tooltip. Used when the layout that held those runs is replaced.
    /// </summary>
    public void ForgetTarget()
    {
        _shown = null;
        _pending = null;
    }

    /// <summary>Stops and drops both timers. They are created again on next use.</summary>
    public void ReleaseTimers()
    {
        _showTimer?.Stop();
        _hideTimer?.Stop();
        _showTimer = null;
        _hideTimer = null;
    }

    private void ScheduleShow(AbbreviationRun abbreviation, Rect placementRect)
    {
        CancelHide();
        _pending = abbreviation;
        _pendingPlacementRect = placementRect;

        var timer = EnsureShowTimer();
        if (timer is null)
        {
            Show(abbreviation, placementRect);
            return;
        }

        timer.Stop();
        timer.Interval = ShowDelay;
        timer.Start();
    }

    private void ShowPending()
    {
        _showTimer?.Stop();
        var abbreviation = _pending;
        var placementRect = _pendingPlacementRect;
        _pending = null;

        if (_lifetime.IsUnloaded ||
            abbreviation is null ||
            !ReferenceEquals(abbreviation, _hover.Run as AbbreviationRun))
        {
            return;
        }

        Show(abbreviation, placementRect);
    }

    private void Show(AbbreviationRun abbreviation, Rect placementRect)
    {
        if (_lifetime.IsUnloaded || _surface.Canvas is null || string.IsNullOrWhiteSpace(abbreviation.Expansion))
            return;

        Close();
        _shown = abbreviation;
        _toolTip = new ToolTip
        {
            Content = abbreviation.Expansion,
            IsHitTestVisible = false,
            Placement = PlacementMode.Top,
            PlacementTarget = _surface.Canvas,
            PlacementRect = placementRect,
            VerticalOffset = -4,
        };
        ToolTipService.SetToolTip(_surface.Canvas, _toolTip);
        _toolTip.IsOpen = true;
    }

    private void Close()
    {
        if (_toolTip is not null)
        {
            try { _toolTip.IsOpen = false; } catch { }
            if (_surface.Canvas is not null)
                ToolTipService.SetToolTip(_surface.Canvas, null);
            _toolTip = null;
        }
    }

    private void CancelShow() => _showTimer?.Stop();

    private void CancelHide() => _hideTimer?.Stop();

    private DispatcherQueueTimer? EnsureShowTimer()
    {
        if (_showTimer is not null)
            return _showTimer;

        var dispatcher = GetDispatcherQueue();
        if (dispatcher is null)
            return null;

        _showTimer = dispatcher.CreateTimer();
        _showTimer.Tick += (_, _) => ShowPending();
        return _showTimer;
    }

    private DispatcherQueueTimer? EnsureHideTimer()
    {
        if (_hideTimer is not null)
            return _hideTimer;

        var dispatcher = GetDispatcherQueue();
        if (dispatcher is null)
            return null;

        _hideTimer = dispatcher.CreateTimer();
        _hideTimer.Tick += (_, _) => Hide();
        return _hideTimer;
    }

    private DispatcherQueue? GetDispatcherQueue()
        => _dispatcherOwner.DispatcherQueue ?? _surface.Canvas?.DispatcherQueue;
}
