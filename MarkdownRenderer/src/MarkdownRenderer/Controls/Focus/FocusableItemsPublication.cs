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
    /// <remarks>
    /// The pre-lease targeting reassigns the request and captures the
    /// generations and revision every later check compares.
    /// </remarks>
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

        if (!IsSnapshotUsable(snapshot, request))
            return false;

        long revisionBefore = snapshot.LayoutRevision;
        if (request.LayoutRevision != revisionBefore)
        {
            _refresh.Retarget(generation, automationGeneration, revisionBefore, request.RequestVersion);
            request = _refresh.Pending;
        }

        if (!TryCollectUnderLease(
                snapshot,
                revisionBefore,
                out bool collectedSuccessfully,
                out IReadOnlyList<Layout.FocusableItem> collected,
                out long revisionUnderLease,
                out Exception? captureFailure))
        {
            return false;
        }

        if (!_refresh.IsCurrent(request, snapshot, generation, automationGeneration))
            return false;

        if (captureFailure is not null)
            RelayoutDiagnostics.ReportFailure("focus plan refresh", captureFailure);

        // Diagnostics can reenter the dispatcher/control. Do not let a failed
        // older attempt clear or rebuild over a newer availability request.
        if (!_refresh.IsCurrent(request, snapshot, generation, automationGeneration))
            return false;

        if (!collectedSuccessfully)
        {
            RecoverFailedCollection(request, snapshot, generation, automationGeneration, revisionBefore);
            return false;
        }

        if (RetargetIfChangedDuringCollection(
                request, snapshot, generation, automationGeneration, revisionBefore, revisionUnderLease))
            return false;

        _focus.ReplaceItems(collected);
        _refresh.Clear(request.RequestVersion);
        _host.InvalidateAutomationLayout();
        return true;
    }

    /// <summary>
    /// False, after clearing <paramref name="request"/>, when the snapshot is
    /// retiring or needs geometry recovery, which is requested first.
    /// </summary>
    private bool IsSnapshotUsable(LayoutSnapshot snapshot, FocusableItemsRefreshTicket request)
    {
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
        return true;
    }

    /// <summary>
    /// Collects the focusable items under the snapshot's read lease when the
    /// layout revision is still <paramref name="revisionBefore"/>. False when
    /// the lease is busy. A failure is returned, not reported, so diagnostics
    /// run only after the lease has ended.
    /// </summary>
    private static bool TryCollectUnderLease(
        LayoutSnapshot snapshot,
        long revisionBefore,
        out bool collectedSuccessfully,
        out IReadOnlyList<Layout.FocusableItem> collected,
        out long revisionUnderLease,
        out Exception? captureFailure)
    {
        collected = Array.Empty<Layout.FocusableItem>();
        revisionUnderLease = -1;
        collectedSuccessfully = false;
        captureFailure = null;
        if (!snapshot.TryBeginInteraction())
            return false;

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
        return true;
    }

    // A failed collection clears a request whose snapshot is retiring, keeps
    // it for a newer layout revision, or clears it and rebuilds.
    private void RecoverFailedCollection(
        FocusableItemsRefreshTicket request,
        LayoutSnapshot snapshot,
        long generation,
        long automationGeneration,
        long revisionBefore)
    {
        if (snapshot.IsRetiringOrDisposed || snapshot.GeometryRecoveryRequired)
            _refresh.Clear(request.RequestVersion);
        else if (!ReferenceEquals(snapshot, _published.Snapshot) || generation != _published.Generation)
            return;
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
    }

    // True when the snapshot, generation or layout revision moved, or a newer
    // request arrived, while the items were collected. A request that is still
    // current is kept for the new layout revision.
    private bool RetargetIfChangedDuringCollection(
        FocusableItemsRefreshTicket request,
        LayoutSnapshot snapshot,
        long generation,
        long automationGeneration,
        long revisionBefore,
        long revisionUnderLease)
    {
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
            return true;
        }

        return false;
    }
}
