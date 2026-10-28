using System;
using System.Threading;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The strong worker borrow for one coalesced image relayout. Async state-machine
/// and scheduler copies may retain this owner, but it releases its graph and
/// dirty-index aliases only after the scheduled operation has actually exited.
/// </summary>
internal sealed class ImageRelayoutWorkPayload<TSnapshot> where TSnapshot : class
{
    private TSnapshot? _snapshot;
    private int[]? _changed;
    private int _workDrained;
    private static readonly object ClosedPublication = new();
    private object? _publication;

    internal ImageRelayoutWorkPayload(TSnapshot snapshot, int[] changed, float width)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changed);
        _snapshot = snapshot;
        _changed = changed;
        Width = width;
    }

    internal TSnapshot Snapshot => Volatile.Read(ref _snapshot) ??
        throw new InvalidOperationException("The image relayout work has already drained.");

    internal int[] Changed => Volatile.Read(ref _changed) ??
        throw new InvalidOperationException("The image relayout work has already drained.");

    internal float Width { get; }

    internal bool TryOwnPublication(WeakSnapshotPublication<TSnapshot> publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        object? previous =
            Interlocked.CompareExchange(ref _publication, publication, null);
        if (previous is not null)
        {
            bool samePublication = ReferenceEquals(previous, publication);
            if (!samePublication)
                publication.Discard();
            return samePublication;
        }

        if (!ReferenceEquals(Volatile.Read(ref _publication), ClosedPublication))
            return true;

        // The terminal marker prevents late registration atomically. A close
        // after registration may instead enqueue an already-empty no-op.
        publication.Discard();
        return false;
    }

    internal void DiscardPublication()
    {
        // This is a terminal publication fence, not a worker cancellation or
        // resource release. Strong work borrows must survive until actual drain.
        if (Interlocked.Exchange(ref _publication, ClosedPublication) is
            WeakSnapshotPublication<TSnapshot> publication)
            publication.Discard();
    }

    internal void ReleaseAfterWorkDrains()
    {
        Interlocked.Exchange(ref _changed, null);
        Interlocked.Exchange(ref _snapshot, null);
        Volatile.Write(ref _workDrained, 1);
    }

    /// <summary>
    /// Delivers the one-shot publication only after the worker has relinquished
    /// its strong graph and dirty-index borrows. This can be used by the UI
    /// handoff recovery path when a queued callback was rejected or withheld.
    /// </summary>
    internal void PublishAfterWorkDrains()
    {
        if (Volatile.Read(ref _workDrained) == 0)
            return;

        if (Volatile.Read(ref _publication) is WeakSnapshotPublication<TSnapshot> publication)
            publication.Publish();
    }
}
