using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record ApplicationTaskOptions(
    string Name,
    string? AccountPartition = null);

public sealed record ApplicationTaskFailure(
    string Name,
    string? AccountPartition,
    Exception Exception);

public sealed record ApplicationTaskShutdownResult(
    bool Completed,
    int PendingTaskCount);

public sealed class ApplicationActivationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await operation(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public interface IApplicationTaskCoordinator
{
    event EventHandler<ApplicationTaskFailure>? TaskFailed;

    int ActiveTaskCount { get; }

    Task RunAsync(
        Func<CancellationToken, Task> operation,
        ApplicationTaskOptions options,
        CancellationToken cancellationToken = default);

    Task CancelAccountAsync(string accountPartition, CancellationToken cancellationToken = default);

    void ActivateAccount(string accountPartition);

    Task<ApplicationTaskShutdownResult> ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ApplicationTaskCoordinator : IApplicationTaskCoordinator, IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, TrackedTask> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _accounts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentBag<CancellationTokenSource> _retiredAccounts = [];
    private long _nextId;
    private bool _isShuttingDown;

    public event EventHandler<ApplicationTaskFailure>? TaskFailed;

    public int ActiveTaskCount => _tasks.Count;

    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        ApplicationTaskOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            throw new ArgumentException("A stable background-task name is required.", nameof(options));
        }

        string? accountPartition = string.IsNullOrWhiteSpace(options.AccountPartition)
            ? null
            : GitHubAccountPartition.Require(options.AccountPartition, nameof(options));
        CancellationToken accountToken = accountPartition is null
            ? CancellationToken.None
            : GetAccountCancellation(accountPartition).Token;

        CancellationTokenSource linked;
        long id;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_isShuttingDown)
            {
                return Task.FromCanceled(_shutdown.Token);
            }

            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token,
                accountToken);
            id = Interlocked.Increment(ref _nextId);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Publish the operation before invoking user code. Operations are allowed
            // to do synchronous work before returning their Task, and account removal
            // or app shutdown must be able to observe and wait for that work.
            _tasks[id] = new TrackedTask(accountPartition, completion.Task);
        }

        Task operationTask;
        if (linked.IsCancellationRequested)
        {
            operationTask = Task.FromCanceled(linked.Token);
        }
        else
        {
            try
            {
                operationTask = operation(linked.Token) ?? Task.CompletedTask;
            }
            catch (Exception exception)
            {
                operationTask = Task.FromException(exception);
            }
        }

        _ = ObserveAsync(id, operationTask, options, linked, completion);
        return completion.Task;
    }

    public async Task CancelAccountAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        CancellationTokenSource source = GetAccountCancellation(partition);
        source.Cancel();
        Task[] accountTasks = _tasks.Values
            .Where(task => string.Equals(task.AccountPartition, partition, StringComparison.Ordinal))
            .Select(static task => task.Task)
            .ToArray();
        if (accountTasks.Length > 0)
        {
            await Task.WhenAll(accountTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void ActivateAccount(string accountPartition)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        CancellationTokenSource? source = null;
        lock (_gate)
        {
            if (_isShuttingDown)
            {
                throw new InvalidOperationException("Background work cannot be activated while the app is shutting down.");
            }

            if (_accounts.TryGetValue(partition, out CancellationTokenSource? existing) &&
                existing.IsCancellationRequested &&
                _accounts.TryRemove(partition, out CancellationTokenSource? removed))
            {
                source = removed;
            }
        }

        if (source is not null)
        {
            _retiredAccounts.Add(source);
        }
    }

    public async Task<ApplicationTaskShutdownResult> ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_gate)
        {
            _isShuttingDown = true;
            if (!_shutdown.IsCancellationRequested)
            {
                _shutdown.Cancel();
            }
        }

        Task[] pending = _tasks.Values.Select(static task => task.Task).ToArray();
        if (pending.Length == 0)
        {
            return new ApplicationTaskShutdownResult(true, 0);
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(pending).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ApplicationTaskShutdownResult(true, 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApplicationTaskShutdownResult(false, _tasks.Count);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _isShuttingDown = true;
            if (!_shutdown.IsCancellationRequested)
            {
                _shutdown.Cancel();
            }
        }

        _shutdown.Dispose();
        foreach (CancellationTokenSource source in _accounts.Values)
        {
            source.Dispose();
        }

        while (_retiredAccounts.TryTake(out CancellationTokenSource? source))
        {
            source.Dispose();
        }
    }

    private CancellationTokenSource GetAccountCancellation(string accountPartition) =>
        _accounts.GetOrAdd(accountPartition, static _ => new CancellationTokenSource());

    private async Task ObserveAsync(
        long id,
        Task operationTask,
        ApplicationTaskOptions options,
        CancellationTokenSource linked,
        TaskCompletionSource completion)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TaskFailed?.Invoke(this, new ApplicationTaskFailure(
                options.Name,
                options.AccountPartition,
                exception));
        }
        finally
        {
            _tasks.TryRemove(id, out _);
            linked.Dispose();
            completion.TrySetResult();
        }
    }

    private sealed record TrackedTask(string? AccountPartition, Task Task);
}
