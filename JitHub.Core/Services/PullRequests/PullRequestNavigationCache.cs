using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum PullRequestPrefetchReason
{
    NavigationHandoff,
    Dwell,
    Hover,
    Neighbor
}

public enum PullRequestPrefetchResult
{
    Success,
    Cancelled,
    Unavailable,
    Failed
}

public enum PullRequestNavigationStoreMode
{
    Replace,
    PreservePopulatedSections
}

public sealed record PullRequestNavigationSnapshot(
    string Owner,
    string RepositoryName,
    int PullRequestNumber,
    GitHubPullRequest PullRequest,
    GitHubIssue? Issue,
    GitHubIssueComment[] Comments,
    GitHubCommit[] Commits,
    GitHubPullRequestReview[] Reviews,
    GitHubPullRequestReviewComment[] ReviewComments,
    GitHubIssueEvent[] TimelineEvents,
    DateTimeOffset StoredAt,
    string Source);

public interface IPullRequestNavigationCache
{
    void Store(
        string accountPartition,
        PullRequestNavigationSnapshot snapshot,
        PullRequestNavigationStoreMode mode = PullRequestNavigationStoreMode.Replace);

    bool TryGet(
        string accountPartition,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        out PullRequestNavigationSnapshot snapshot);

    Task<PullRequestPrefetchResult> PrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        PullRequestPrefetchReason reason,
        CancellationToken cancellationToken = default);

    IDisposable SchedulePrefetch(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        PullRequestPrefetchReason reason,
        TimeSpan delay,
        Action<PullRequestPrefetchResult, TimeSpan>? completed = null);

    Task ClearPartitionAsync(
        string accountPartition,
        CancellationToken cancellationToken = default);
}

public sealed partial class PullRequestNavigationCache : IPullRequestNavigationCache
{
    private static readonly TimeSpan SnapshotSoftTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PullRequestNavigationSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<PullRequestPrefetchResult>> _inFlightPrefetches = new(StringComparer.OrdinalIgnoreCase);
    private readonly IGitHubPullRequestQueryService _pullRequestQueryService;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly IAdaptivePrefetchPolicy _prefetchPolicy;

    public PullRequestNavigationCache(IGitHubPullRequestQueryService pullRequestQueryService)
        : this(
            pullRequestQueryService,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    public PullRequestNavigationCache(
        IGitHubPullRequestQueryService pullRequestQueryService,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator)
        : this(pullRequestQueryService, accountWork, taskCoordinator, UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    public PullRequestNavigationCache(
        IGitHubPullRequestQueryService pullRequestQueryService,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator,
        IAdaptivePrefetchPolicy prefetchPolicy)
    {
        _pullRequestQueryService = pullRequestQueryService ?? throw new ArgumentNullException(nameof(pullRequestQueryService));
        _accountWork = accountWork ?? throw new ArgumentNullException(nameof(accountWork));
        _taskCoordinator = taskCoordinator ?? throw new ArgumentNullException(nameof(taskCoordinator));
        _prefetchPolicy = prefetchPolicy ?? throw new ArgumentNullException(nameof(prefetchPolicy));
    }

    public void Store(
        string accountPartition,
        PullRequestNavigationSnapshot snapshot,
        PullRequestNavigationStoreMode mode = PullRequestNavigationStoreMode.Replace)
    {
        string? normalizedPartition = NormalizeAccountPartition(accountPartition);
        if (normalizedPartition is null ||
            snapshot.PullRequestNumber <= 0 ||
            string.IsNullOrWhiteSpace(snapshot.Owner) ||
            string.IsNullOrWhiteSpace(snapshot.RepositoryName))
        {
            return;
        }

        try
        {
            using IAccountWorkLease lease = _accountWork.Enter(normalizedPartition);
            string key = CreateKey(normalizedPartition, snapshot.Owner, snapshot.RepositoryName, snapshot.PullRequestNumber);
            if (mode == PullRequestNavigationStoreMode.Replace)
            {
                _snapshots[key] = snapshot;
                return;
            }

            _snapshots.AddOrUpdate(
                key,
                snapshot,
                (_, existing) => MergeNavigationHandoff(existing, snapshot));
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
        int pullRequestNumber,
        out PullRequestNavigationSnapshot snapshot)
    {
        snapshot = default!;
        string? normalizedPartition = NormalizeAccountPartition(accountPartition);
        if (normalizedPartition is null ||
            pullRequestNumber <= 0 ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName))
        {
            return false;
        }

        if (!_snapshots.TryGetValue(
                CreateKey(normalizedPartition, owner, repositoryName, pullRequestNumber),
                out PullRequestNavigationSnapshot? candidate))
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

    public Task<PullRequestPrefetchResult> PrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        PullRequestPrefetchReason reason,
        CancellationToken cancellationToken = default)
    {
        string? normalizedPartition = NormalizeAccountPartition(userId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            normalizedPartition is null ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName) ||
            pullRequestNumber <= 0)
        {
            return Task.FromResult(PullRequestPrefetchResult.Unavailable);
        }

        string key = CreateKey(normalizedPartition, owner, repositoryName, pullRequestNumber);
        return _inFlightPrefetches.GetOrAdd(
            key,
            _ => RunTrackedPrefetchAsync(
                key,
                accessToken,
                normalizedPartition,
                owner,
                repositoryName,
                pullRequestNumber,
                reason,
                cancellationToken));
    }

    public IDisposable SchedulePrefetch(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        PullRequestPrefetchReason reason,
        TimeSpan delay,
        Action<PullRequestPrefetchResult, TimeSpan>? completed = null)
    {
        ScheduledPrefetchCompletion<PullRequestPrefetchResult> completion = new(completed);
        string? normalizedPartition = NormalizeAccountPartition(userId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            normalizedPartition is null ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName) ||
            pullRequestNumber <= 0)
        {
            completion.Complete(PullRequestPrefetchResult.Unavailable);
            return DisposableAction.Empty;
        }

        if (!_prefetchPolicy.Evaluate(
                normalizedPartition,
                AdaptivePrefetchFeature.PullRequests,
                AdaptivePrefetchStage.Schedule).IsAllowed)
        {
            completion.Complete(PullRequestPrefetchResult.Unavailable);
            return DisposableAction.Empty;
        }

        CancellationTokenSource cancellation = new();
        Task scheduledTask = _taskCoordinator.RunAsync(
            token => RunScheduledPrefetchAsync(
                accessToken,
                normalizedPartition,
                owner,
                repositoryName,
                pullRequestNumber,
                reason,
                delay,
                token,
                completion),
            new ApplicationTaskOptions("pull_requests.prefetch.scheduled", normalizedPartition),
            cancellation.Token);
        completion.Observe(scheduledTask, PullRequestPrefetchResult.Cancelled);
        return new DisposableAction(cancellation, scheduledTask);
    }

    private async Task RunScheduledPrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        PullRequestPrefetchReason reason,
        TimeSpan delay,
        CancellationToken cancellationToken,
        ScheduledPrefetchCompletion<PullRequestPrefetchResult> completion)
    {
        PullRequestPrefetchResult result = PullRequestPrefetchResult.Unavailable;
        try
        {
            await Task.Delay(delay, cancellationToken);
            result = await PrefetchAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                reason,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = PullRequestPrefetchResult.Cancelled;
        }
        catch
        {
            result = PullRequestPrefetchResult.Failed;
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

    private async Task<PullRequestPrefetchResult> RunTrackedPrefetchAsync(
        string key,
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        PullRequestPrefetchReason reason,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        PullRequestPrefetchResult result = PullRequestPrefetchResult.Unavailable;
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
                                AdaptivePrefetchFeature.PullRequests,
                                AdaptivePrefetchStage.Execute).IsAllowed)
                        {
                            return;
                        }

                        await PrefetchCoreAsync(
                            accessToken,
                            userId,
                            owner,
                            repositoryName,
                            pullRequestNumber,
                            reason,
                            lease.CancellationToken).ConfigureAwait(false);
                        result = TryGet(userId, owner, repositoryName, pullRequestNumber, out _)
                            ? PullRequestPrefetchResult.Success
                            : PullRequestPrefetchResult.Unavailable;
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                },
                new ApplicationTaskOptions("pull_requests.prefetch", userId),
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
        int pullRequestNumber,
        PullRequestPrefetchReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            if (TryGet(userId, owner, repositoryName, pullRequestNumber, out PullRequestNavigationSnapshot cached) &&
                cached.Comments.Length > 0 &&
                cached.Commits.Length > 0 &&
                DateTimeOffset.UtcNow - cached.StoredAt < TimeSpan.FromMinutes(2))
            {
                return;
            }

            PullRequestConversationAggregate? aggregate = await _pullRequestQueryService.GetPullRequestPrefetchAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                cancellationToken);
            if (aggregate is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PullRequestNavigationSnapshot snapshot = new(
                    owner,
                    repositoryName,
                    pullRequestNumber,
                    aggregate.PullRequest,
                    aggregate.Issue,
                    aggregate.Comments,
                    [],
                    [],
                    [],
                    [],
                    DateTimeOffset.UtcNow,
                    reason.ToString());
                Store(userId, PreserveFailedSections(cached, snapshot, aggregate));
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

    private static PullRequestNavigationSnapshot PreserveFailedSections(
        PullRequestNavigationSnapshot? existing,
        PullRequestNavigationSnapshot incoming,
        PullRequestConversationAggregate aggregate)
    {
        if (existing is null)
        {
            return incoming;
        }

        return incoming with
        {
            Issue = aggregate.IssueState.ErrorMessage is null ? incoming.Issue : incoming.Issue ?? existing.Issue,
            Comments = PullRequestSectionProjectionPolicy.ProjectSection(
                incoming.Comments,
                existing.Comments,
                aggregate.CommentsState,
                static comment => comment.Id.ToString(CultureInfo.InvariantCulture)),
            Commits = existing.Commits,
            Reviews = existing.Reviews,
            ReviewComments = existing.ReviewComments,
            TimelineEvents = existing.TimelineEvents
        };
    }

    private static PullRequestNavigationSnapshot MergeNavigationHandoff(
        PullRequestNavigationSnapshot existing,
        PullRequestNavigationSnapshot incoming)
    {
        if (DateTimeOffset.UtcNow - existing.StoredAt > SnapshotSoftTtl)
        {
            return incoming;
        }

        return incoming with
        {
            PullRequest = existing.PullRequest,
            Issue = existing.Issue ?? incoming.Issue,
            Comments = incoming.Comments.Length == 0 ? existing.Comments : incoming.Comments,
            Commits = incoming.Commits.Length == 0 ? existing.Commits : incoming.Commits,
            Reviews = incoming.Reviews.Length == 0 ? existing.Reviews : incoming.Reviews,
            ReviewComments = incoming.ReviewComments.Length == 0 ? existing.ReviewComments : incoming.ReviewComments,
            TimelineEvents = incoming.TimelineEvents.Length == 0 ? existing.TimelineEvents : incoming.TimelineEvents
        };
    }

    private static string CreateKey(string accountPartition, string owner, string repositoryName, int pullRequestNumber) =>
        $"{accountPartition.Trim().ToLowerInvariant()}:{owner.Trim().ToLowerInvariant()}/{repositoryName.Trim().ToLowerInvariant()}#{pullRequestNumber.ToString(CultureInfo.InvariantCulture)}";

    private static string? NormalizeAccountPartition(string accountPartition)
    {
        string normalized = accountPartition?.Trim() ?? string.Empty;
        return normalized.Length == 0 ||
               normalized.Equals("current", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private sealed partial class DisposableAction : IDisposable
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
            }
            finally
            {
                cancellation.Dispose();
                GC.KeepAlive(Interlocked.Exchange(ref _scheduledTask, null));
            }
        }
    }
}
