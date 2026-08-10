using System;
using System.Threading;

namespace JitHub.WinUI.ViewModels.Common;

public enum RepositoryDataAvailability
{
    Loading,
    Available,
    Unavailable
}

public sealed class RepositoryLoadCoordinator
{
    private readonly object _gate = new();
    private long _generation;

    public bool IsLoading { get; private set; }

    public bool HasRepository { get; private set; }

    public RepositoryDataAvailability BranchState { get; private set; } = RepositoryDataAvailability.Loading;

    public RepositoryDataAvailability StarState { get; private set; } = RepositoryDataAvailability.Loading;

    public RepositoryDataAvailability WatchState { get; private set; } = RepositoryDataAvailability.Loading;

    public bool IsStarStateKnown => StarState == RepositoryDataAvailability.Available;

    public bool IsWatchStateKnown => WatchState == RepositoryDataAvailability.Available;

    public bool CanFork => !IsLoading && HasRepository;

    public bool CanToggleStar => CanFork && IsStarStateKnown;

    public bool CanToggleWatch => CanFork && IsWatchStateKnown;

    public long CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public long Begin(bool preserveAvailableState = false)
    {
        lock (_gate)
        {
            long generation = ++_generation;
            IsLoading = true;
            if (!preserveAvailableState)
            {
                HasRepository = false;
                BranchState = RepositoryDataAvailability.Loading;
                StarState = RepositoryDataAvailability.Loading;
                WatchState = RepositoryDataAvailability.Loading;
            }
            return generation;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return generation == _generation;
        }
    }

    public void ThrowIfStale(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation)
            {
                throw new OperationCanceledException("A newer repository navigation superseded this load.", cancellationToken);
            }
        }
    }

    public void PublishIfCurrent<T>(
        long generation,
        T value,
        Action<T> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation)
            {
                throw new OperationCanceledException("A newer repository navigation superseded this load.", cancellationToken);
            }

            publish(value);
        }
    }

    public bool MarkRepositoryAvailable(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            HasRepository = true;
            return true;
        }
    }

    public bool MarkStarStateKnown(long generation)
    {
        return SetState(generation, RepositoryStateKind.Star, RepositoryDataAvailability.Available);
    }

    public bool MarkWatchStateKnown(long generation)
    {
        return SetState(generation, RepositoryStateKind.Watch, RepositoryDataAvailability.Available);
    }

    public bool MarkBranchStateKnown(long generation) =>
        SetState(generation, RepositoryStateKind.Branch, RepositoryDataAvailability.Available);

    public bool MarkStarStateUnavailable(long generation) =>
        SetState(generation, RepositoryStateKind.Star, RepositoryDataAvailability.Unavailable);

    public bool MarkWatchStateUnavailable(long generation) =>
        SetState(generation, RepositoryStateKind.Watch, RepositoryDataAvailability.Unavailable);

    public bool MarkBranchStateUnavailable(long generation) =>
        SetState(generation, RepositoryStateKind.Branch, RepositoryDataAvailability.Unavailable);

    public bool Complete(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            IsLoading = false;
            return true;
        }
    }

    private bool SetState(
        long generation,
        RepositoryStateKind kind,
        RepositoryDataAvailability availability)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            switch (kind)
            {
                case RepositoryStateKind.Branch:
                    BranchState = availability;
                    break;
                case RepositoryStateKind.Star:
                    StarState = availability;
                    break;
                case RepositoryStateKind.Watch:
                    WatchState = availability;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            return true;
        }
    }

    private enum RepositoryStateKind
    {
        Branch,
        Star,
        Watch
    }
}
