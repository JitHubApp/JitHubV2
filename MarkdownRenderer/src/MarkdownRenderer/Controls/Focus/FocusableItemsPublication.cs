using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Publishes the published snapshot's focusable items to the keyboard focus
/// model when they may have changed, such as after a task list command's
/// availability changes. The pending request lives in
/// <see cref="FocusableItemsRefreshRequest"/>; this type collects and
/// publishes it only while it is still current.
/// </summary>
internal sealed class FocusableItemsPublication
{
    private readonly IFocusableItemsPublicationHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly FocusableItemsRefreshRequest _refresh;
    private readonly KeyboardFocusModel _focus;
    private readonly RecoveryClock _recoveryClock;
    private bool _refreshQueued;

    internal FocusableItemsPublication(
        IFocusableItemsPublicationHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        FocusableItemsRefreshRequest refresh,
        KeyboardFocusModel focus,
        RecoveryClock recoveryClock)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _refresh = refresh;
        _focus = focus;
        _recoveryClock = recoveryClock;
    }

    /// <summary>
    /// Queues one refresh after a task list command reports that its
    /// availability changed. Repeated notifications before the refresh runs
    /// share it.
    /// </summary>
    internal void OnTaskCommandAvailabilityChanged()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _refreshQueued)
            return;

        _refreshQueued = true;
        if (!_host.DispatcherQueue.TryEnqueue(Refresh))
            _refreshQueued = false;
    }

    private void Refresh()
    {
        _refreshQueued = false;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot)
            return;

        _refresh.Mark(snapshot, _published.Generation);
        if (!TryPublishPending())
            _recoveryClock.Start();
    }

    /// <summary>
    /// Collects and publishes the focusable items for the pending request
    /// under a snapshot read lease. Returns true when nothing is pending or
    /// the items were published; false leaves the request for the recovery
    /// clock, a rebuild, or a newer request.
    /// </summary>
    internal bool TryPublishPending()
    {
        var request = _refresh.Pending;
        if (!request.IsPending)
            return true;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return false;

        LayoutSnapshot? snapshot = _published.Snapshot;
        long generation = _published.Generation;
        if (snapshot is null)
        {
            _refresh.Reset();
            return false;
        }

        long automationGeneration = snapshot.AutomationDocumentGeneration;
        if (request.SnapshotGeneration != generation ||
            request.AutomationDocumentGeneration != automationGeneration)
        {
            // A newer snapshot supersedes the value-only request. Never follow
            // an old ticket into its retired graph; the current generation gets
            // a fresh request instead.
            _refresh.Mark(snapshot, generation);
            request = _refresh.Pending;
        }

        if (snapshot.IsRetiringOrDisposed)
        {
            _refresh.Clear(request.RequestVersion);
            return false;
        }
        if (snapshot.GeometryRecoveryRequired)
        {
            _host.RequestGeometryRecoveryIfRequired(snapshot);
            _refresh.Clear(request.RequestVersion);
            return false;
        }

        long revisionBefore = snapshot.LayoutRevision;
        if (request.LayoutRevision != revisionBefore)
        {
            _refresh.Retarget(generation, automationGeneration, revisionBefore, request.RequestVersion);
            request = _refresh.Pending;
        }

        if (!snapshot.TryBeginInteraction())
            return false;

        IReadOnlyList<Layout.FocusableItem> collected = Array.Empty<Layout.FocusableItem>();
        long revisionUnderLease = -1;
        bool collectedSuccessfully = false;
        Exception? captureFailure = null;
        try
        {
            if (snapshot.LayoutRevision == revisionBefore &&
                snapshot.TryCollectFocusableItemsUnderReadLease(out collected))
            {
                revisionUnderLease = snapshot.LayoutRevision;
                collectedSuccessfully = true;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A custom overflow owner can fail while its availability is read.
            // The local partial list is discarded, and the normal replacement
            // route is responsible for recovery. Defer diagnostics until after
            // EndInteraction because log sinks are callback-capable.
            captureFailure = exception;
        }
        finally
        {
            snapshot.EndInteraction();
        }

        if (!_refresh.IsCurrent(
                request,
                snapshot,
                generation,
                automationGeneration))
        {
            return false;
        }

        if (captureFailure is not null)
            RelayoutDiagnostics.ReportFailure("focus plan refresh", captureFailure);

        // Diagnostics can reenter the dispatcher/control. Do not let a failed
        // older attempt clear or rebuild over a newer availability request.
        if (!_refresh.IsCurrent(
                request,
                snapshot,
                generation,
                automationGeneration))
        {
            return false;
        }

        if (!collectedSuccessfully)
        {
            if (snapshot.IsRetiringOrDisposed || snapshot.GeometryRecoveryRequired)
                _refresh.Clear(request.RequestVersion);
            else if (!ReferenceEquals(snapshot, _published.Snapshot) || generation != _published.Generation)
                return false;
            else if (revisionBefore != snapshot.LayoutRevision)
            {
                _refresh.Retarget(
                    generation, automationGeneration, snapshot.LayoutRevision, request.RequestVersion);
            }
            else
            {
                _refresh.Clear(request.RequestVersion);
                _host.RequestFullRebuild();
            }
            return false;
        }

        long revisionAfter = snapshot.LayoutRevision;
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            generation != _published.Generation ||
            automationGeneration != snapshot.AutomationDocumentGeneration ||
            revisionUnderLease != revisionBefore ||
            revisionAfter != revisionBefore ||
            !_refresh.IsCurrent(
                request,
                snapshot,
                generation,
                automationGeneration))
        {
            if (ReferenceEquals(snapshot, _published.Snapshot) &&
                generation == _published.Generation &&
                automationGeneration == snapshot.AutomationDocumentGeneration &&
                _refresh.IsCurrent(
                    request,
                    snapshot,
                    generation,
                    automationGeneration))
            {
                _refresh.Retarget(
                    generation, automationGeneration, revisionAfter, request.RequestVersion);
            }
            return false;
        }

        _focus.ReplaceItems(collected);
        _refresh.Clear(request.RequestVersion);
        _host.InvalidateAutomationLayout();
        return true;
    }
}
