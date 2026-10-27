using System;
using System.Threading;

namespace MarkdownRenderer.Controls;

/// <summary>
/// A one-shot UI handoff for a result whose published snapshot is already owned
/// by the control. A delayed callback must not keep a retired graph alive. The
/// worker must retain its own strong borrow until its actual work has drained.
/// An optional discard action owns an unpublished candidate, not this weak
/// source identity. A publish winner must retire or transfer that candidate in
/// its callback's finally block.
/// </summary>
internal sealed class WeakSnapshotPublication<TSnapshot> where TSnapshot : class
{
    private sealed class Delivery(
        TSnapshot snapshot,
        Action<TSnapshot?> publish,
        Action? discard)
    {
        internal readonly WeakReference<TSnapshot> Snapshot = new(snapshot);
        internal readonly Action<TSnapshot?> Publish = publish;
        internal readonly Action? Discard = discard;
    }

    private Delivery? _delivery;

    internal WeakSnapshotPublication(
        TSnapshot snapshot,
        Action<TSnapshot?> publish,
        Action? discard = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publish);
        _delivery = new Delivery(snapshot, publish, discard);
    }

    internal void Publish()
    {
        // The winner owns delivery. Clear the handoff before invoking completion
        // so retained delegates, reentrancy and failures cannot replay it.
        Delivery? delivery = Interlocked.Exchange(ref _delivery, null);
        if (delivery is null)
            return;

        delivery.Snapshot.TryGetTarget(out TSnapshot? snapshot);
        delivery.Publish(snapshot);
    }

    internal void Discard()
    {
        // Claim the complete envelope atomically. Only a discard winner owns
        // the optional cleanup action; a publish winner transfers candidate
        // cleanup responsibility to its publication callback.
        Delivery? delivery = Interlocked.Exchange(ref _delivery, null);
        delivery?.Discard?.Invoke();
    }
}
