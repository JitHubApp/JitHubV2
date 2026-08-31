using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record RepositoryForkOperationResult<TRepository>(
    TRepository Repository,
    bool IsReady,
    bool WasCreated,
    long Generation,
    DateTimeOffset? RetryAvailableAt = null,
    RepositoryForkReadinessFailure ReadinessFailure = RepositoryForkReadinessFailure.None)
    where TRepository : class;

public sealed class RepositoryForkReconciliationPendingException : Exception
{
    public RepositoryForkReconciliationPendingException(DateTimeOffset retryAvailableAt)
        : base("GitHub may already have accepted the fork. JitHub will reconcile it before sending another create request.")
    {
        RetryAvailableAt = retryAvailableAt;
    }

    public DateTimeOffset RetryAvailableAt { get; }
}

public sealed class RepositoryForkOperation<TRepository>
    where TRepository : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private PendingFork? _pendingFork;
    private readonly Dictionary<string, UncertainFork> _uncertainSources = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _uncertainRepostDelay;
    private readonly int _minimumReconciliationAttempts;
    private long _generation;

    public RepositoryForkOperation(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? uncertainRepostDelay = null,
        int minimumReconciliationAttempts = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumReconciliationAttempts);
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _uncertainRepostDelay = uncertainRepostDelay ?? TimeSpan.FromSeconds(30);
        if (_uncertainRepostDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(uncertainRepostDelay));
        }

        _minimumReconciliationAttempts = minimumReconciliationAttempts;
    }

    public bool HasPendingFork
    {
        get
        {
            lock (_stateGate)
            {
                return _pendingFork is not null;
            }
        }
    }

    public void AdoptAcceptedFork(string sourceKey, TRepository repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(repository);
        lock (_stateGate)
        {
            _pendingFork = new PendingFork(sourceKey, repository, _generation);
            _uncertainSources.Remove(sourceKey);
        }
    }

    public async Task<RepositoryForkOperationResult<TRepository>> ResumeAsync(
        string sourceKey,
        Func<CancellationToken, Task<TRepository?>> createForkAsync,
        Func<TRepository, int, CancellationToken, Task<bool>> readinessProbeAsync,
        CancellationToken cancellationToken,
        int maxAttempts = RepositoryForkReadinessPolicy.DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<CancellationToken, Task<TRepository?>>? reconcileForkAsync = null,
        TimeSpan? maxElapsed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(createForkAsync);
        ArgumentNullException.ThrowIfNull(readinessProbeAsync);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool wasCreated = false;
            long generation;
            PendingFork? pending;
            lock (_stateGate)
            {
                if (_pendingFork is not null &&
                    !string.Equals(_pendingFork.SourceKey, sourceKey, StringComparison.Ordinal))
                {
                    _generation++;
                    _pendingFork = null;
                }

                generation = _generation;
                pending = _pendingFork;
            }

            if (pending is null)
            {
                UncertainFork? uncertain;
                lock (_stateGate)
                {
                    _uncertainSources.TryGetValue(sourceKey, out uncertain);
                }

                if (uncertain is not null)
                {
                    if (reconcileForkAsync is null)
                    {
                        throw new InvalidOperationException(
                            "A previous fork request has an uncertain outcome and requires reconciliation before retrying.");
                    }

                    TRepository? reconciled = await reconcileForkAsync(cancellationToken).ConfigureAwait(false);
                    if (reconciled is not null)
                    {
                        lock (_stateGate)
                        {
                            ThrowIfStaleLocked(generation, cancellationToken);
                            pending = new PendingFork(sourceKey, reconciled, generation);
                            _pendingFork = pending;
                            _uncertainSources.Remove(sourceKey);
                        }
                    }
                    else
                    {
                        DateTimeOffset now = _utcNow();
                        UncertainFork checkedState = uncertain with
                        {
                            ReconciliationAttempts = uncertain.ReconciliationAttempts + 1,
                            LastCheckedAt = now
                        };
                        bool mayRepost = checkedState.ReconciliationAttempts >= _minimumReconciliationAttempts &&
                            now - checkedState.CreatedAt >= _uncertainRepostDelay;
                        lock (_stateGate)
                        {
                            ThrowIfStaleLocked(generation, cancellationToken);
                            if (mayRepost)
                            {
                                _uncertainSources.Remove(sourceKey);
                            }
                            else
                            {
                                _uncertainSources[sourceKey] = checkedState;
                            }
                        }

                        if (!mayRepost)
                        {
                            TimeSpan remaining = _uncertainRepostDelay - (now - checkedState.CreatedAt);
                            TimeSpan retryDelay = remaining > TimeSpan.FromSeconds(2)
                                ? TimeSpan.FromSeconds(2)
                                : remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(250);
                            throw new RepositoryForkReconciliationPendingException(now.Add(retryDelay));
                        }
                    }
                }
            }

            if (pending is null)
            {
                TRepository? created;
                try
                {
                    created = await createForkAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (IsTransportOutcomeUncertain(ex))
                    {
                        lock (_stateGate)
                        {
                            MarkUncertainLocked(sourceKey);
                        }
                    }

                    throw;
                }

                if (created is null)
                {
                    lock (_stateGate)
                    {
                        MarkUncertainLocked(sourceKey);
                    }

                    throw new InvalidOperationException("GitHub accepted the fork request but returned no fork repository.");
                }

                lock (_stateGate)
                {
                    if (generation != _generation)
                    {
                        MarkUncertainLocked(sourceKey);
                        throw new OperationCanceledException("A newer repository navigation superseded this fork operation.");
                    }

                    pending = new PendingFork(sourceKey, created, generation);
                    _pendingFork = pending;
                    _uncertainSources.Remove(sourceKey);
                }

                // GitHub may have accepted the POST immediately before cancellation. Persist the
                // returned fork first so a retry resumes probing instead of creating a duplicate.
                wasCreated = true;
                cancellationToken.ThrowIfCancellationRequested();
            }

            RepositoryForkReadinessResult readiness = await RepositoryForkReadinessPolicy.WaitForReadyResultAsync(
                (attempt, token) => readinessProbeAsync(pending.Repository, attempt, token),
                cancellationToken,
                maxAttempts,
                delay,
                maxElapsed).ConfigureAwait(false);

            lock (_stateGate)
            {
                ThrowIfStaleLocked(generation, cancellationToken);
                if (!ReferenceEquals(_pendingFork, pending))
                {
                    throw new OperationCanceledException("A newer fork operation superseded this result.");
                }
            }

            return new RepositoryForkOperationResult<TRepository>(
                pending.Repository,
                readiness.IsReady,
                wasCreated,
                generation,
                readiness.RetryAvailableAt,
                readiness.Failure);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset()
    {
        lock (_stateGate)
        {
            _generation++;
            _pendingFork = null;
        }
    }

    public bool Complete(RepositoryForkOperationResult<TRepository> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_stateGate)
        {
            if (_generation != result.Generation ||
                _pendingFork is not { } pending ||
                pending.Generation != result.Generation ||
                !ReferenceEquals(pending.Repository, result.Repository))
            {
                return false;
            }

            _pendingFork = null;
            return true;
        }
    }

    private void ThrowIfStaleLocked(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _generation)
        {
            throw new OperationCanceledException("A newer repository navigation superseded this fork operation.");
        }
    }

    private void MarkUncertainLocked(string sourceKey)
    {
        if (!_uncertainSources.ContainsKey(sourceKey))
        {
            DateTimeOffset now = _utcNow();
            _uncertainSources[sourceKey] = new UncertainFork(sourceKey, now, now, 0);
        }
    }

    internal static bool IsTransportOutcomeUncertain(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        HttpRequestException => true,
        IOException => true,
        TimeoutException => true,
        GitHubApiException => false,
        _ => false
    };

    private sealed record PendingFork(string SourceKey, TRepository Repository, long Generation);

    private sealed record UncertainFork(
        string SourceKey,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastCheckedAt,
        int ReconciliationAttempts);
}
