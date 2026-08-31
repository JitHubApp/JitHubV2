using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.ViewModels.CodeViewer;

/// <summary>
/// Owns the single predictive fetch associated with the current shell route.
/// A newer route cancels obsolete work, while the application coordinator
/// makes account removal and shutdown wait for the active generation.
/// </summary>
public sealed class RepositoryRoutePrefetchCoordinator
{
    private readonly RepoCodeNavigationPreparationCache _codePreparationCache;
    private readonly IGitHubCommitQueryService _commitQueryService;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly object _gate = new();
    private CancellationTokenSource? _currentCancellation;
    private Task _currentTask = Task.CompletedTask;

    public RepositoryRoutePrefetchCoordinator(
        RepoCodeNavigationPreparationCache codePreparationCache,
        IGitHubCommitQueryService commitQueryService,
        IApplicationTaskCoordinator taskCoordinator)
    {
        _codePreparationCache = codePreparationCache;
        _commitQueryService = commitQueryService;
        _taskCoordinator = taskCoordinator;
    }

    public Task StartCodeAsync(
        string accessToken,
        string accountPartition,
        string owner,
        string repositoryName,
        string gitRef)
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, accountPartition);
        return Start(
            partition,
            "shell.route_prefetch.code",
            token => _codePreparationCache.PrefetchRouteAsync(
                owner,
                repositoryName,
                gitRef,
                token));
    }

    public Task StartCommitsAsync(
        string accessToken,
        string accountPartition,
        string owner,
        string repositoryName,
        string gitRef)
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, accountPartition);
        return Start(
            partition,
            "shell.route_prefetch.commits",
            async token =>
            {
                _ = await _commitQueryService.GetCommitsAsync(
                    accessToken,
                    partition,
                    owner,
                    repositoryName,
                    new CommitListQueryOptions { GitRef = gitRef },
                    100,
                    1,
                    token).ConfigureAwait(false);
            });
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _currentCancellation;
            _currentCancellation = null;
        }

        CancelSafely(cancellation);
    }

    internal Task CurrentTask
    {
        get
        {
            lock (_gate)
            {
                return _currentTask;
            }
        }
    }

    private Task Start(
        string accountPartition,
        string taskName,
        Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _currentCancellation;
            _currentCancellation = cancellation;
        }

        CancelSafely(previous);
        Task trackedTask = RunAndCompleteAsync(
            accountPartition,
            taskName,
            operation,
            cancellation);
        lock (_gate)
        {
            if (ReferenceEquals(_currentCancellation, cancellation))
            {
                _currentTask = trackedTask;
            }
        }

        return trackedTask;
    }

    private async Task RunAndCompleteAsync(
        string accountPartition,
        string taskName,
        Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _taskCoordinator.RunAsync(
                async token =>
                {
                    try
                    {
                        await operation(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        HandledFailureReporter.Report(exception, "repository-route-prefetch");
                    }
                },
                new ApplicationTaskOptions(taskName, accountPartition),
                cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Complete(cancellation);
        }
    }

    private void Complete(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_currentCancellation, cancellation))
            {
                _currentCancellation = null;
                _currentTask = Task.CompletedTask;
            }
        }

        cancellation.Dispose();
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

}
