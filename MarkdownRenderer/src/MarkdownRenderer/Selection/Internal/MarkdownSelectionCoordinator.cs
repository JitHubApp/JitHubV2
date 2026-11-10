using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Selection.Internal;

/// <summary>
/// Keeps at most one rendered-text selection among the peers that share one
/// XamlRoot: when a peer selects, the others clear theirs. The root's
/// <see cref="Controls.MarkdownXamlRootServices"/> owns one coordinator, so
/// selections in different windows or islands never clear each other.
/// </summary>
/// <remarks>
/// Peers are held weakly, so a renderer that is collected without unloading
/// drops out on the next registration or clear.
/// </remarks>
internal sealed class MarkdownSelectionCoordinator
{
    private readonly object _gate = new();
    private readonly List<WeakReference<ISelectionPeer>> _peers = new();

    public void Register(ISelectionPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        lock (_gate)
        {
            PruneLocked();
            foreach (var weak in _peers)
            {
                if (weak.TryGetTarget(out var existing) && ReferenceEquals(existing, peer))
                    return;
            }

            _peers.Add(new WeakReference<ISelectionPeer>(peer));
        }
    }

    public void Unregister(ISelectionPeer peer)
    {
        lock (_gate)
        {
            _peers.RemoveAll(weak =>
                !weak.TryGetTarget(out var existing) ||
                ReferenceEquals(existing, peer));
        }
    }

    public void ClearSelectionsExcept(ISelectionPeer? owner)
    {
        ISelectionPeer[] snapshot;
        lock (_gate)
        {
            PruneLocked();
            var live = new List<ISelectionPeer>(_peers.Count);
            foreach (var weak in _peers)
            {
                if (weak.TryGetTarget(out var peer) &&
                    !ReferenceEquals(peer, owner))
                {
                    live.Add(peer);
                }
            }

            snapshot = live.ToArray();
        }

        foreach (var peer in snapshot)
            peer.ClearSelectionFromCoordinator();
    }

    private void PruneLocked()
    {
        _peers.RemoveAll(static weak => !weak.TryGetTarget(out _));
    }
}
