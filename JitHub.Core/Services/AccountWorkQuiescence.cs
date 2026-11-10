using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface IAccountWorkLease : IDisposable
{
    CancellationToken CancellationToken { get; }
}

public interface IAccountWorkQuiescence
{
    IAccountWorkLease Enter(string accountPartition, CancellationToken cancellationToken = default);

    Task QuiesceAsync(string accountPartition, CancellationToken cancellationToken = default);

    bool IsQuiesced(string accountPartition);

    void Activate(string accountPartition);
}

public sealed partial class AccountWorkQuiescence : IAccountWorkQuiescence
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PartitionState> _partitions = new(StringComparer.Ordinal);
    private readonly IApplicationTaskCoordinator? _taskCoordinator;

    public AccountWorkQuiescence()
    {
    }

    public AccountWorkQuiescence(IApplicationTaskCoordinator taskCoordinator)
    {
        _taskCoordinator = taskCoordinator ?? throw new ArgumentNullException(nameof(taskCoordinator));
    }

    public IAccountWorkLease Enter(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        cancellationToken.ThrowIfCancellationRequested();

        PartitionState state;
        lock (_gate)
        {
            state = GetOrCreateState(partition);
            if (state.IsQuiesced)
            {
                throw new OperationCanceledException(
                    "The authenticated account is being removed and no longer accepts work.",
                    innerException: null,
                    state.Cancellation.Token);
            }

            state.ActiveCount++;
        }

        CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            state.Cancellation.Token);
        return new AccountWorkLease(this, partition, linkedCancellation);
    }

    public async Task QuiesceAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        CancellationTokenSource? cancellation = null;
        Task drainTask;
        lock (_gate)
        {
            PartitionState state = GetOrCreateState(partition);
            if (!state.IsQuiesced)
            {
                state.IsQuiesced = true;
                cancellation = state.Cancellation;
            }

            if (state.ActiveCount == 0)
            {
                drainTask = Task.CompletedTask;
            }
            else
            {
                state.Drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drainTask = state.Drained.Task;
            }
        }

        cancellation?.Cancel();
        await drainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool IsQuiesced(string accountPartition)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        lock (_gate)
        {
            return _partitions.TryGetValue(partition, out PartitionState? state) && state.IsQuiesced;
        }
    }

    public void Activate(string accountPartition)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        CancellationTokenSource? cancellation = null;
        bool reactivated = false;
        lock (_gate)
        {
            if (!_partitions.TryGetValue(partition, out PartitionState? state) || !state.IsQuiesced)
            {
                return;
            }

            if (state.ActiveCount != 0)
            {
                throw new InvalidOperationException("Account work cannot be reactivated until quiescence has drained.");
            }

            _partitions.Remove(partition);
            cancellation = state.Cancellation;
            reactivated = true;
        }

        cancellation.Dispose();
        if (reactivated)
        {
            _taskCoordinator?.ActivateAccount(partition);
        }
    }

    private PartitionState GetOrCreateState(string partition)
    {
        if (!_partitions.TryGetValue(partition, out PartitionState? state))
        {
            state = new PartitionState();
            _partitions.Add(partition, state);
        }

        return state;
    }

    private void Exit(string partition, CancellationTokenSource linkedCancellation)
    {
        linkedCancellation.Dispose();
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (!_partitions.TryGetValue(partition, out PartitionState? state) || state.ActiveCount <= 0)
            {
                return;
            }

            state.ActiveCount--;
            if (state.ActiveCount == 0)
            {
                drained = state.Drained;
                state.Drained = null;
            }
        }

        drained?.TrySetResult();
    }

    private sealed class PartitionState
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public int ActiveCount { get; set; }

        public bool IsQuiesced { get; set; }

        public TaskCompletionSource? Drained { get; set; }
    }

    private sealed partial class AccountWorkLease : IAccountWorkLease
    {
        private AccountWorkQuiescence? _owner;
        private readonly string _partition;
        private readonly CancellationTokenSource _linkedCancellation;

        public AccountWorkLease(
            AccountWorkQuiescence owner,
            string partition,
            CancellationTokenSource linkedCancellation)
        {
            _owner = owner;
            _partition = partition;
            _linkedCancellation = linkedCancellation;
        }

        public CancellationToken CancellationToken => _linkedCancellation.Token;

        public void Dispose()
        {
            AccountWorkQuiescence? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit(_partition, _linkedCancellation);
        }
    }
}
