using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum IssuePrefetchReason
{
    NavigationHandoff,
    Dwell,
    Hover,
    Neighbor
}

public enum IssuePrefetchResult
{
    Success,
    Cancelled,
    Unavailable,
    Failed
}

public sealed record IssueNavigationSnapshot(
    string Owner,
    string RepositoryName,
    int IssueNumber,
    GitHubIssue Issue,
    GitHubIssueComment[] Comments,
    DateTimeOffset StoredAt,
    string Source);

public interface IIssueNavigationCache
{
    void Store(string accountPartition, IssueNavigationSnapshot snapshot);

    bool TryGet(
        string accountPartition,
        string owner,
        string repositoryName,
        int issueNumber,
        out IssueNavigationSnapshot snapshot);

    Task<IssuePrefetchResult> PrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason,
        CancellationToken cancellationToken = default);

    IDisposable SchedulePrefetch(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason,
        TimeSpan delay,
        Action<IssuePrefetchResult, TimeSpan>? completed = null);

    Task ClearPartitionAsync(
        string accountPartition,
        CancellationToken cancellationToken = default);
}

public sealed class IssueNavigationCache : IIssueNavigationCache
{
    private static readonly TimeSpan SnapshotSoftTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, IssueNavigationSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<IssuePrefetchResult>> _inFlightPrefetches = new(StringComparer.OrdinalIgnoreCase);
    private readonly IGitHubIssueQueryService? _issueQueryService;
    private readonly IGitHubMeQueryService? _legacyMeQueryService;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly IAdaptivePrefetchPolicy _prefetchPolicy;

    public IssueNavigationCache(IGitHubIssueQueryService issueQueryService)
        : this(
            issueQueryService,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    public IssueNavigationCache(
        IGitHubIssueQueryService issueQueryService,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator)
        : this(issueQueryService, accountWork, taskCoordinator, UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    public IssueNavigationCache(
        IGitHubIssueQueryService issueQueryService,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator,
        IAdaptivePrefetchPolicy prefetchPolicy)
    {
        _issueQueryService = issueQueryService ?? throw new ArgumentNullException(nameof(issueQueryService));
        _accountWork = accountWork ?? throw new ArgumentNullException(nameof(accountWork));
        _taskCoordinator = taskCoordinator ?? throw new ArgumentNullException(nameof(taskCoordinator));
        _prefetchPolicy = prefetchPolicy ?? throw new ArgumentNullException(nameof(prefetchPolicy));
    }

    internal IssueNavigationCache(IGitHubMeQueryService meQueryService)
        : this(
            meQueryService,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    internal IssueNavigationCache(
        IGitHubMeQueryService meQueryService,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator)
        : this(meQueryService, accountWork, taskCoordinator, UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    internal IssueNavigationCache(
        IGitHubMeQueryService meQueryService,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator,
        IAdaptivePrefetchPolicy prefetchPolicy)
    {
        _legacyMeQueryService = meQueryService ?? throw new ArgumentNullException(nameof(meQueryService));
        _accountWork = accountWork ?? throw new ArgumentNullException(nameof(accountWork));
        _taskCoordinator = taskCoordinator ?? throw new ArgumentNullException(nameof(taskCoordinator));
        _prefetchPolicy = prefetchPolicy ?? throw new ArgumentNullException(nameof(prefetchPolicy));
    }

    public void Store(string accountPartition, IssueNavigationSnapshot snapshot)
    {
        string? normalizedPartition = NormalizeAccountPartition(accountPartition);
        if (normalizedPartition is null ||
            snapshot.IssueNumber <= 0 ||
            string.IsNullOrWhiteSpace(snapshot.Owner) ||
            string.IsNullOrWhiteSpace(snapshot.RepositoryName))
        {
            return;
        }

        try
        {
            using IAccountWorkLease lease = _accountWork.Enter(normalizedPartition);
            _snapshots[CreateKey(normalizedPartition, snapshot.Owner, snapshot.RepositoryName, snapshot.IssueNumber)] = snapshot;
        }
        catch (OperationCanceledException)
        {
            // Account removal rejects navigation handoffs once quiescence begins.
        }
    }

    public bool TryGet(
        string accountPartition,
        string owner,
        string repositoryName,
        int issueNumber,
        out IssueNavigationSnapshot snapshot)
    {
        snapshot = default!;
        string? normalizedPartition = NormalizeAccountPartition(accountPartition);
        if (normalizedPartition is null ||
            issueNumber <= 0 ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName))
        {
            return false;
        }

        if (!_snapshots.TryGetValue(
                CreateKey(normalizedPartition, owner, repositoryName, issueNumber),
                out IssueNavigationSnapshot? candidate))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - candidate.StoredAt > SnapshotSoftTtl)
        {
            return false;
        }

        snapshot = candidate;
        return true;
    }

    public Task<IssuePrefetchResult> PrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason,
        CancellationToken cancellationToken = default)
    {
        string? normalizedPartition = NormalizeAccountPartition(userId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            normalizedPartition is null ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName) ||
            issueNumber <= 0)
        {
            return Task.FromResult(IssuePrefetchResult.Unavailable);
        }

        string key = CreateKey(normalizedPartition, owner, repositoryName, issueNumber);
        Task<IssuePrefetchResult> prefetchTask = _inFlightPrefetches.GetOrAdd(
            key,
            _ => RunTrackedPrefetchAsync(
                key,
                accessToken,
                normalizedPartition,
                owner,
                repositoryName,
                issueNumber,
                reason,
                cancellationToken));
        return prefetchTask;
    }

    public IDisposable SchedulePrefetch(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason,
        TimeSpan delay,
        Action<IssuePrefetchResult, TimeSpan>? completed = null)
    {
        ScheduledPrefetchCompletion<IssuePrefetchResult> completion = new(completed);
        string? normalizedPartition = NormalizeAccountPartition(userId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            normalizedPartition is null ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName) ||
            issueNumber <= 0)
        {
            completion.Complete(IssuePrefetchResult.Unavailable);
            return DisposableAction.Empty;
        }

        if (!_prefetchPolicy.Evaluate(
                normalizedPartition,
                AdaptivePrefetchFeature.Issues,
                AdaptivePrefetchStage.Schedule).IsAllowed)
        {
            completion.Complete(IssuePrefetchResult.Unavailable);
            return DisposableAction.Empty;
        }

        CancellationTokenSource cancellation = new();
        Task scheduledTask = _taskCoordinator.RunAsync(
            token => RunScheduledPrefetchAsync(
                accessToken,
                normalizedPartition,
                owner,
                repositoryName,
                issueNumber,
                reason,
                delay,
                token,
                completion),
            new ApplicationTaskOptions("issues.prefetch.scheduled", normalizedPartition),
            cancellation.Token);
        _ = scheduledTask.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((ScheduledPrefetchCompletion<IssuePrefetchResult>)state!).Complete(IssuePrefetchResult.Cancelled);
            },
            completion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return new DisposableAction(cancellation, scheduledTask);
    }

    private async Task RunScheduledPrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason,
        TimeSpan delay,
        CancellationToken cancellationToken,
        ScheduledPrefetchCompletion<IssuePrefetchResult> completion)
    {
        IssuePrefetchResult result = IssuePrefetchResult.Unavailable;
        try
        {
            await Task.Delay(delay, cancellationToken);
            result = await PrefetchAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                issueNumber,
                reason,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = IssuePrefetchResult.Cancelled;
        }
        catch
        {
            result = IssuePrefetchResult.Failed;
        }
        finally
        {
            completion.Complete(result);
        }
    }

    public Task ClearPartitionAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        cancellationToken.ThrowIfCancellationRequested();
        string prefix = partition.Trim().ToLowerInvariant() + ":";
        foreach (string key in _snapshots.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _snapshots.TryRemove(key, out _);
        }

        foreach (string key in _inFlightPrefetches.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _inFlightPrefetches.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    private async Task<IssuePrefetchResult> RunTrackedPrefetchAsync(
        string key,
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        IssuePrefetchResult result = IssuePrefetchResult.Unavailable;
        try
        {
            await _taskCoordinator.RunAsync(
                async token =>
                {
                    try
                    {
                        using IAccountWorkLease lease = _accountWork.Enter(userId, token);
                        if (!_prefetchPolicy.Evaluate(
                                userId,
                                AdaptivePrefetchFeature.Issues,
                                AdaptivePrefetchStage.Execute).IsAllowed)
                        {
                            return;
                        }

                        await PrefetchCoreAsync(
                            accessToken,
                            userId,
                            owner,
                            repositoryName,
                            issueNumber,
                            reason,
                            lease.CancellationToken).ConfigureAwait(false);
                        result = TryGet(userId, owner, repositoryName, issueNumber, out _)
                            ? IssuePrefetchResult.Success
                            : IssuePrefetchResult.Unavailable;
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                },
                new ApplicationTaskOptions("issues.prefetch", userId),
                cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return result;
        }
        finally
        {
            _inFlightPrefetches.TryRemove(key, out _);
        }
    }

    private async Task PrefetchCoreAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            if (TryGet(userId, owner, repositoryName, issueNumber, out IssueNavigationSnapshot cached) &&
                cached.Comments.Length > 0 &&
                DateTimeOffset.UtcNow - cached.StoredAt < TimeSpan.FromMinutes(2))
            {
                return;
            }

            GitHubIssue? issue;
            GitHubIssueComment[] comments;
            if (_issueQueryService is not null)
            {
                IssuePrefetchAggregate aggregate = await _issueQueryService.GetIssuePrefetchAsync(
                    accessToken, userId, owner, repositoryName, issueNumber, cancellationToken);
                issue = aggregate.Issue;
                comments = aggregate.Comments;
            }
            else
            {
                IssuePrefetchAggregate aggregate = await _legacyMeQueryService!.GetIssuePrefetchAsync(
                    accessToken, userId, owner, repositoryName, issueNumber, cancellationToken);
                issue = aggregate.Issue;
                comments = aggregate.Comments;
            }

            if (issue is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Store(userId, new IssueNavigationSnapshot(
                    owner,
                    repositoryName,
                    issueNumber,
                    issue,
                    comments,
                    DateTimeOffset.UtcNow,
                    reason.ToString()));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw;
        }
    }

    private static string CreateKey(string accountPartition, string owner, string repositoryName, int issueNumber) =>
        $"{accountPartition.Trim().ToLowerInvariant()}:{owner.Trim().ToLowerInvariant()}/{repositoryName.Trim().ToLowerInvariant()}#{issueNumber.ToString(CultureInfo.InvariantCulture)}";

    private static string? NormalizeAccountPartition(string accountPartition)
    {
        string normalized = accountPartition?.Trim() ?? string.Empty;
        return normalized.Length == 0 ||
               normalized.Equals("current", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private sealed class DisposableAction : IDisposable
    {
        public static readonly IDisposable Empty = new DisposableAction(null, null);

        private CancellationTokenSource? _cancellation;
        private Task? _scheduledTask;

        public DisposableAction(CancellationTokenSource? cancellation, Task? scheduledTask)
        {
            _cancellation = cancellation;
            _scheduledTask = scheduledTask;
        }

        public void Dispose()
        {
            CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
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
                // A scheduled prefetch may complete and dispose its token before
                // the view model cancels the stale handle on the next selection.
            }

            finally
            {
                cancellation.Dispose();
                GC.KeepAlive(Interlocked.Exchange(ref _scheduledTask, null));
            }
        }
    }
}
