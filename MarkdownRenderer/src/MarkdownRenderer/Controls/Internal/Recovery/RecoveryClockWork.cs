using System;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Commit;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Recovery;

/// <summary>
/// The work the <see cref="RecoveryClock"/> retries: deferred UI handoffs
/// that a dispatcher may have rejected or withheld (bitmap output recovery,
/// image relayout, layout publication, lazy layout, overflow-scroll and focus
/// geometry retries, focus plan publication), and the rule for stopping the
/// clock once none is pending.
/// </summary>
internal sealed class RecoveryClockWork
{
    private readonly IRecoveryClockWorkHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly RecoveryClock _recoveryClock;
    private readonly LayoutCommitState _layoutCommit;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImageRelayout _imageRelayout;
    private readonly FocusableItemsRefreshRequest _focusRefresh;

    internal RecoveryClockWork(
        IRecoveryClockWorkHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        RecoveryClock recoveryClock,
        LayoutCommitState layoutCommit,
        ImageSubscriptions imageSubscriptions,
        ImageRelayout imageRelayout,
        FocusableItemsRefreshRequest focusRefresh)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _recoveryClock = recoveryClock;
        _layoutCommit = layoutCommit;
        _imageSubscriptions = imageSubscriptions;
        _imageRelayout = imageRelayout;
        _focusRefresh = focusRefresh;
    }

    /// <summary>True while lazy band realization or its layout publication is outstanding.</summary>
    internal bool HasPendingLazyLayoutWork =>
        _host.HasPendingLazyLayout || _layoutCommit.Pending is not null;

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
                if (_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
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
                _host.PublishPendingLayout();
                _host.ProcessQueuedLazyLayout();
                _host.TryPublishLazyLayoutCompletion();
                _host.ProcessOverflowScrollRetry();
                _host.ProcessFocusGeometryWork();
                _host.TryPublishPendingFocusableItems();
                _imageRelayout.RecoverFaultedHandoff();
            }
        }
        catch (Exception exception)
        {
            // Reentrant newer publications retain their own recovery ticket.
            if (layoutTicket == _layoutCommit.Ticket)
                _layoutCommit.ClearPending();
            RelayoutDiagnostics.ReportFailure("layout publication", exception);
            if (!_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
                _host.RequestFullRebuild();
            else
                _focusRefresh.ClearForCurrentSnapshot();
            if (_published.Snapshot is { } failedLayoutSnapshot)
                _host.NotifyLazyLayoutFailed(failedLayoutSnapshot);
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
            _host.HasPendingOverflowScrollRetry ||
            _focusRefresh.Pending.IsPending ||
            _host.HasPendingFocusGeometryWork))
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
            _host.HasPendingOverflowScrollRetry ||
            _focusRefresh.Pending.IsPending ||
            _host.HasPendingFocusGeometryWork ||
            HasPendingLazyLayoutWork)
        {
            return;
        }

        Stop();
    }
}
