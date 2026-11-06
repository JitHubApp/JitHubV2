using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation.Peers;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Raises the automation scroll-percent change of horizontal overflow
/// regions. Changes are coalesced per region until a low-priority dispatcher
/// callback raises one event per region, from the first old percent to the
/// latest new one. No event is raised for a region of a replaced snapshot.
/// </summary>
internal sealed class HorizontalOverflowScrollAutomationEvents
{
    private readonly IHorizontalOverflowScrollHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly DispatcherQueue? _dispatcher;

    private bool _queued;
    private readonly Dictionary<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>
        _pending = new(ReferenceEqualityComparer.Instance);

    internal HorizontalOverflowScrollAutomationEvents(
        IHorizontalOverflowScrollHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        DispatcherQueue? dispatcher)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _dispatcher = dispatcher;
    }

    /// <summary>Drops queued notifications.</summary>
    internal void Clear()
    {
        _queued = false;
        _pending.Clear();
    }

    /// <summary>
    /// Queues the scroll of <paramref name="target"/> from
    /// <paramref name="oldOffset"/> to <paramref name="newOffset"/> when an
    /// automation client listens.
    /// </summary>
    internal void Queue(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        IHorizontalOverflowBox target,
        double oldOffset,
        double newOffset,
        double extent,
        double viewport,
        bool isRightToLeft)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) ||
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded ||
            !ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation ||
            !snapshot.SemanticDocument.TryGetHorizontalOverflowNode(target, out _))
        {
            return;
        }

        double oldPhysicalPercent = MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
            oldOffset,
            extent,
            viewport,
            isRightToLeft);
        double newPhysicalPercent = MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
            newOffset,
            extent,
            viewport,
            isRightToLeft);
        if (Math.Abs(newPhysicalPercent - oldPhysicalPercent) <= 0.0001)
            return;

        if (_pending.TryGetValue(
                target,
                out PendingHorizontalOverflowAutomationChange pending) &&
            ReferenceEquals(pending.Snapshot, snapshot))
        {
            _pending[target] = new(
                pending.Snapshot,
                pending.OldPhysicalPercent,
                newPhysicalPercent);
        }
        else
        {
            _pending[target] = new(
                snapshot,
                oldPhysicalPercent,
                newPhysicalPercent);
        }

        if (_queued)
            return;

        _queued = true;
        DispatcherQueue? dispatcher = _dispatcher;
        if (dispatcher is null || !dispatcher.TryEnqueue(
                DispatcherQueuePriority.Low,
                ProcessQueued))
        {
            _queued = false;
            _pending.Clear();
        }
    }

    private void ProcessQueued()
    {
        _queued = false;
        var changes = new KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>[
            _pending.Count];
        ((ICollection<KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>>)
            _pending).CopyTo(changes, 0);
        _pending.Clear();

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } currentSnapshot)
            return;

        MarkdownAutomationPeer? peer = _host.FindAutomationPeer();
        if (peer is null)
            return;

        foreach (KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange> entry in changes)
        {
            PendingHorizontalOverflowAutomationChange change = entry.Value;
            if (!ReferenceEquals(change.Snapshot, currentSnapshot) ||
                !currentSnapshot.SemanticDocument.TryGetHorizontalOverflowNode(entry.Key, out _))
            {
                continue;
            }

            peer.NotifyHorizontalOverflowScrolled(
                entry.Key,
                change.OldPhysicalPercent,
                change.NewPhysicalPercent);
        }
    }
}
