using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Pairs a weak one-shot snapshot publication with the generation's actual
/// worker-drained signal. Dispatch callbacks may claim the publication only
/// after the worker Task has completed and released its strong graph borrow.
/// </summary>
internal sealed class WeakSnapshotPublicationDrainFence<TSnapshot> where TSnapshot : class
{
    private readonly object _gate = new();
    private WeakSnapshotPublication<TSnapshot>? _publication;
    private long _generation;
    private long _workDrainedGeneration;

    internal bool HasPendingPublication
    {
        get
        {
            lock (_gate)
                return _publication is not null;
        }
    }

    internal void Reset(long generation)
    {
        WeakSnapshotPublication<TSnapshot>? previous;
        lock (_gate)
        {
            _generation = generation;
            _workDrainedGeneration = 0;
            previous = _publication;
            _publication = null;
        }

        previous?.Discard();
    }

    internal bool TryStage(long generation, WeakSnapshotPublication<TSnapshot> publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        WeakSnapshotPublication<TSnapshot>? previous = null;
        bool accepted;
        lock (_gate)
        {
            accepted = generation == _generation;
            if (accepted)
            {
                previous = _publication;
                _publication = publication;
            }
        }

        previous?.Discard();
        if (!accepted)
            publication.Discard();
        return accepted;
    }

    internal void MarkWorkDrained(long generation)
    {
        lock (_gate)
        {
            if (generation == _generation)
                _workDrainedGeneration = generation;
        }
    }

    internal bool TryPublishAfterWorkDrains(long generation)
    {
        WeakSnapshotPublication<TSnapshot>? publication = null;
        lock (_gate)
        {
            if (generation == _generation &&
                _workDrainedGeneration == generation &&
                _publication is not null)
            {
                publication = _publication;
                _publication = null;
                _workDrainedGeneration = 0;
            }
        }

        if (publication is null)
            return false;

        publication.Publish();
        return true;
    }
}
