using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface IGitHubRequestQueue
{
    Task<T> EnqueueAsync<T>(
        string dedupeKey,
        GitHubRequestPriority priority,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);

    Task<T> EnqueueForAccountAsync<T>(
        string accountPartition,
        string dedupeKey,
        GitHubRequestPriority priority,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(dedupeKey, priority, work, cancellationToken);
}

public sealed class GitHubRequestQueue : IGitHubRequestQueue
{
    private readonly SemaphoreSlim _foregroundReads;
    private readonly SemaphoreSlim _backgroundReads;
    private readonly SemaphoreSlim _mutationLane;
    private readonly ConcurrentDictionary<string, Lazy<InFlightRequest>> _inFlight = new(StringComparer.Ordinal);
    private readonly IAccountWorkQuiescence? _accountWork;

    public GitHubRequestQueue()
        : this(accountWork: null, foregroundReadConcurrency: 2, backgroundReadConcurrency: 1, mutationConcurrency: 1)
    {
    }

    public GitHubRequestQueue(IAccountWorkQuiescence accountWork)
        : this(accountWork, foregroundReadConcurrency: 2, backgroundReadConcurrency: 1, mutationConcurrency: 1)
    {
    }

    internal GitHubRequestQueue(int foregroundReadConcurrency, int backgroundReadConcurrency, int mutationConcurrency)
        : this(accountWork: null, foregroundReadConcurrency, backgroundReadConcurrency, mutationConcurrency)
    {
    }

    internal GitHubRequestQueue(
        IAccountWorkQuiescence? accountWork,
        int foregroundReadConcurrency,
        int backgroundReadConcurrency,
        int mutationConcurrency)
    {
        _accountWork = accountWork;
        _foregroundReads = new SemaphoreSlim(foregroundReadConcurrency);
        _backgroundReads = new SemaphoreSlim(backgroundReadConcurrency);
        _mutationLane = new SemaphoreSlim(mutationConcurrency);
    }

    internal int InFlightCount => _inFlight.Count;

    public async Task<T> EnqueueAsync<T>(
        string dedupeKey,
        GitHubRequestPriority priority,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
        => await EnqueueCoreAsync(
            accountPartition: null,
            dedupeKey,
            priority,
            work,
            cancellationToken).ConfigureAwait(false);

    public async Task<T> EnqueueForAccountAsync<T>(
        string accountPartition,
        string dedupeKey,
        GitHubRequestPriority priority,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        return await EnqueueCoreAsync(partition, dedupeKey, priority, work, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> EnqueueCoreAsync<T>(
        string? accountPartition,
        string dedupeKey,
        GitHubRequestPriority priority,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        if (priority == GitHubRequestPriority.Mutation)
        {
            object? mutationResult = await RunAsync(
                accountPartition,
                priority,
                work,
                cancellationToken).ConfigureAwait(false);
            return mutationResult is T typedMutationResult
                ? typedMutationResult
                : throw new InvalidOperationException("A GitHub mutation returned an unexpected result type.");
        }

        while (true)
        {
            var candidate = new Lazy<InFlightRequest>(
                () => new InFlightRequest(
                    this,
                    accountPartition,
                    priority,
                    async sharedToken => await work(sharedToken).ConfigureAwait(false)),
                LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<InFlightRequest> shared = _inFlight.GetOrAdd(dedupeKey, candidate);
            InFlightRequest request = shared.Value;
            if (!request.TryAddSubscriber(priority))
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<InFlightRequest>>(dedupeKey, shared));
                continue;
            }

            if (ReferenceEquals(candidate, shared))
            {
                _ = ObserveAndRemoveAsync(dedupeKey, shared, request.Completion);
            }

            int subscriberRemoved = 0;
            void RemoveSubscriberOnce()
            {
                if (Interlocked.Exchange(ref subscriberRemoved, 1) == 0)
                {
                    request.RemoveSubscriber();
                }
            }

            using CancellationTokenRegistration cancellationRegistration =
                cancellationToken.Register(RemoveSubscriberOnce);
            try
            {
                object? result = await request.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                return result is T typedResult
                    ? typedResult
                    : throw new InvalidOperationException("A GitHub request dedupe key was reused with a different result type.");
            }
            finally
            {
                RemoveSubscriberOnce();
            }
        }
    }

    private async Task ObserveAndRemoveAsync(
        string dedupeKey,
        Lazy<InFlightRequest> shared,
        Task<object?> completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // Each awaiting caller receives this failure. Observe abandoned shared
            // work as well so caller cancellation cannot create an unobserved fault.
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<InFlightRequest>>(dedupeKey, shared));
        }
    }

    private async Task<object?> RunAsync<T>(
        string? accountPartition,
        GitHubRequestPriority priority,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim lane = GetLane(priority);
        await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IAccountWorkLease? lease = EnterAccountWork(accountPartition, cancellationToken);
            CancellationToken effectiveToken = lease?.CancellationToken ?? cancellationToken;
            return await work(effectiveToken).ConfigureAwait(false);
        }
        finally
        {
            lane.Release();
        }
    }

    private IAccountWorkLease? EnterAccountWork(
        string? accountPartition,
        CancellationToken cancellationToken) =>
        accountPartition is null || _accountWork is null
            ? null
            : _accountWork.Enter(accountPartition, cancellationToken);

    private SemaphoreSlim GetLane(GitHubRequestPriority priority) =>
        priority switch
        {
            GitHubRequestPriority.BackgroundRefresh or GitHubRequestPriority.Prefetch => _backgroundReads,
            GitHubRequestPriority.Mutation => _mutationLane,
            _ => _foregroundReads
        };

    private static int GetPriorityRank(GitHubRequestPriority priority) =>
        priority switch
        {
            GitHubRequestPriority.UserInitiated => 4,
            GitHubRequestPriority.Visible => 3,
            GitHubRequestPriority.BackgroundRefresh => 2,
            GitHubRequestPriority.Prefetch => 1,
            _ => 0
        };

    private sealed class InFlightRequest
    {
        private readonly GitHubRequestQueue _owner;
        private readonly string? _accountPartition;
        private readonly Func<CancellationToken, Task<object?>> _work;
        private readonly CancellationTokenSource _sharedWorkCancellation = new();
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private GitHubRequestPriority _priority;
        private CancellationTokenSource? _laneWaitCancellation;
        private int _subscriberCount;
        private bool _acceptingSubscribers = true;
        private bool _runStarted;
        private bool _workStarted;

        public InFlightRequest(
            GitHubRequestQueue owner,
            string? accountPartition,
            GitHubRequestPriority priority,
            Func<CancellationToken, Task<object?>> work)
        {
            _owner = owner;
            _accountPartition = accountPartition;
            _priority = priority;
            _work = work;
        }

        public Task<object?> Completion => _completion.Task;

        public bool TryAddSubscriber(GitHubRequestPriority priority)
        {
            CancellationTokenSource? laneWait = null;
            bool start = false;
            lock (_gate)
            {
                if (!_acceptingSubscribers)
                {
                    return false;
                }

                _subscriberCount++;
                if (!_workStarted && GetPriorityRank(priority) > GetPriorityRank(_priority))
                {
                    _priority = priority;
                    laneWait = _laneWaitCancellation;
                }

                if (!_runStarted)
                {
                    _runStarted = true;
                    start = true;
                }
            }

            try
            {
                laneWait?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The lane was acquired between promotion and cancellation. The
                // generation check in RunSharedAsync already retries on the newer lane.
            }

            if (start)
            {
                _ = CompleteSharedAsync();
            }

            return true;
        }

        public void RemoveSubscriber()
        {
            bool cancelSharedWork = false;
            lock (_gate)
            {
                if (_subscriberCount <= 0)
                {
                    return;
                }

                _subscriberCount--;
                if (_subscriberCount == 0)
                {
                    _acceptingSubscribers = false;
                    cancelSharedWork = !_completion.Task.IsCompleted;
                }
            }

            if (cancelSharedWork)
            {
                try
                {
                    _sharedWorkCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private async Task CompleteSharedAsync()
        {
            try
            {
                _completion.TrySetResult(await RunSharedAsync().ConfigureAwait(false));
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        private async Task<object?> RunSharedAsync()
        {
            try
            {
                while (true)
                {
                    GitHubRequestPriority priority;
                    CancellationTokenSource laneWait;
                    lock (_gate)
                    {
                        priority = _priority;
                        laneWait = CancellationTokenSource.CreateLinkedTokenSource(_sharedWorkCancellation.Token);
                        _laneWaitCancellation = laneWait;
                    }

                    SemaphoreSlim lane = _owner.GetLane(priority);
                    try
                    {
                        await lane.WaitAsync(laneWait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_sharedWorkCancellation.IsCancellationRequested)
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_laneWaitCancellation, laneWait))
                            {
                                _laneWaitCancellation = null;
                            }
                        }

                        laneWait.Dispose();
                        continue;
                    }

                    bool useAcquiredLane;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_laneWaitCancellation, laneWait))
                        {
                            _laneWaitCancellation = null;
                        }

                        useAcquiredLane = priority == _priority;
                        if (useAcquiredLane)
                        {
                            _workStarted = true;
                        }
                    }

                    laneWait.Dispose();
                    if (!useAcquiredLane)
                    {
                        lane.Release();
                        continue;
                    }

                    try
                    {
                        using IAccountWorkLease? lease = _owner.EnterAccountWork(
                            _accountPartition,
                            _sharedWorkCancellation.Token);
                        CancellationToken effectiveToken = lease?.CancellationToken ?? _sharedWorkCancellation.Token;
                        return await _work(effectiveToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        lane.Release();
                    }
                }
            }
            finally
            {
                _sharedWorkCancellation.Dispose();
            }
        }
    }
}
