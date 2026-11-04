using System;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Rendering;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The work the <see cref="RecoveryClock"/> retries: deferred UI handoffs
/// that a dispatcher may have rejected or withheld (bitmap output recovery,
/// image relayout, layout publication, lazy layout, overflow-scroll and focus
/// geometry retries, focus plan publication), and the rule for stopping the
/// clock once none is pending.
/// </summary>
internal sealed class RecoveryClockWork
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly RecoveryClock _recoveryClock;
    private readonly LayoutCommitState _layoutCommit;
    private readonly LayoutCommitPublisher _layoutPublisher;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImageRelayout _imageRelayout;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;
    private readonly PendingFocusGeometryWork _focusGeometryWork;
    private readonly FocusableItemsRefreshRequest _focusRefresh;
    private readonly FocusableItemsPublication _focusableItems;
    private readonly PointerInputRouter _pointerInput;
    private readonly GeometryRecovery _geometryRecovery;
    private readonly RebuildScheduler _rebuild;

    internal RecoveryClockWork(
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        RecoveryClock recoveryClock,
        LayoutCommitState layoutCommit,
        LayoutCommitPublisher layoutPublisher,
        LazyLayoutRealization lazyLayout,
        ImageSubscriptions imageSubscriptions,
        ImageRelayout imageRelayout,
        HorizontalOverflowScrollVisuals overflowScroll,
        PendingFocusGeometryWork focusGeometryWork,
        FocusableItemsRefreshRequest focusRefresh,
        FocusableItemsPublication focusableItems,
        PointerInputRouter pointerInput,
        GeometryRecovery geometryRecovery,
        RebuildScheduler rebuild)
    {
        _lifetime = lifetime;
        _published = published;
        _recoveryClock = recoveryClock;
        _layoutCommit = layoutCommit;
        _layoutPublisher = layoutPublisher;
        _lazyLayout = lazyLayout;
        _imageSubscriptions = imageSubscriptions;
        _imageRelayout = imageRelayout;
        _overflowScroll = overflowScroll;
        _focusGeometryWork = focusGeometryWork;
        _focusRefresh = focusRefresh;
        _focusableItems = focusableItems;
        _pointerInput = pointerInput;
        _geometryRecovery = geometryRecovery;
        _rebuild = rebuild;
    }

    /// <summary>True while lazy band realization or its layout publication is outstanding.</summary>
    internal bool HasPendingLazyLayoutWork =>
        _lazyLayout.HasPendingWork || _layoutCommit.Pending is not null;

    /// <summary>
    /// One recovery clock tick: retries every piece of deferred work whose UI
    /// handoff may have been rejected or withheld, then stops the clock once
    /// nothing is pending.
    /// </summary>
    internal void ProcessTick()
    {
        long layoutTicket = _layoutCommit.Ticket;
        try
        {
            if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded)
            {
                if (_geometryRecovery.RequestIfRequired(_published.Snapshot))
                {
                    // Drain canceled image work so its retirement/active flag
                    // can complete, but do not probe or retry failed geometry.
                    _focusRefresh.ClearForCurrentSnapshot();
                    _imageRelayout.PublishReadyCompletion();
                    return;
                }
                _imageSubscriptions.ProcessBitmapOutputRecovery();
                _imageRelayout.ProcessQueued();
                _imageRelayout.PublishReadyCompletion();
                _layoutPublisher.PublishPending();
                _lazyLayout.ProcessQueued();
                _lazyLayout.TryPublishCompletion();
                _overflowScroll.ProcessPendingRetry();
                _focusGeometryWork.Process();
                _focusableItems.TryPublishPending();
                _imageRelayout.RecoverFaultedHandoff();
            }
        }
        catch (Exception exception)
        {
            // Reentrant newer publications retain their own recovery ticket.
            if (layoutTicket == _layoutCommit.Ticket)
                _layoutCommit.ClearPending();
            RelayoutDiagnostics.ReportFailure("layout publication", exception);
            if (!_geometryRecovery.RequestIfRequired(_published.Snapshot))
                _rebuild.Request(RebuildReason.Full);
            else
                _focusRefresh.ClearForCurrentSnapshot();
            if (_published.Snapshot is { } failedLayoutSnapshot)
                _pointerInput.OnLazyLayoutCompletion(failedLayoutSnapshot, succeeded: false);
        }
        finally
        {
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
                Stop(force: true);
            else
                StopIfIdle();
        }
    }

    internal void Stop(bool force = false)
    {
        if (!force && (_imageSubscriptions.HasPendingBitmapOutputRecovery || HasPendingLazyLayoutWork ||
            _overflowScroll.HasPendingRetry ||
            _focusRefresh.Pending.IsPending ||
            _focusGeometryWork.HasPendingWork))
            return;

        if (force)
        {
            _imageSubscriptions.ResetBitmapOutputRecovery();
            _focusRefresh.Reset();
        }
        _recoveryClock.Stop();
    }

    internal void StopIfIdle()
    {
        if (_imageRelayout.HasPendingWork ||
            _imageSubscriptions.HasPendingBitmapOutputRecovery ||
            _overflowScroll.HasPendingRetry ||
            _focusRefresh.Pending.IsPending ||
            _focusGeometryWork.HasPendingWork ||
            HasPendingLazyLayoutWork)
        {
            return;
        }

        Stop();
    }
}
