using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum CommitPrefetchReason
{
    NavigationHandoff,
    Dwell,
    Hover,
    Neighbor
}

public enum CommitPrefetchOutcome
{
    Success,
    Failure,
    Canceled,
    Suppressed
}

public sealed record CommitNavigationSnapshot(
    string Owner,
    string RepositoryName,
    string Sha,
    GitHubCommit Commit,
    GitHubCommitComment[] Comments,
    GitHubCombinedStatus? CombinedStatus,
    GitHubCheckRun[] CheckRuns,
    GitHubPullRequest[] AssociatedPullRequests,
    DateTimeOffset StoredAt,
    string Source);

public interface ICommitNavigationCache
{
    void Store(string accountPartition, CommitNavigationSnapshot snapshot);

    bool TryGet(
        string accountPartition,
        string owner,
        string repositoryName,
        string sha,
        out CommitNavigationSnapshot snapshot);

    Task ClearPartitionAsync(
        string accountPartition,
        CancellationToken cancellationToken = default);

    Task PrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken = default);

    Task<CommitPrefetchOutcome> PrefetchWithResultAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken = default);

    IDisposable SchedulePrefetch(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        TimeSpan delay);
}

public sealed class CommitNavigationCache : ICommitNavigationCache
{
    private static readonly TimeSpan SnapshotSoftTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, CommitNavigationSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<CommitPrefetchOutcome>> _inFlightPrefetches = new(StringComparer.OrdinalIgnoreCase);
    private readonly IGitHubCommitQueryService _commitQueryService;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly IAdaptivePrefetchPolicy _prefetchPolicy;

    public CommitNavigationCache(IGitHubCommitQueryService commitQueryService)
        : this(
            commitQueryService,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    public CommitNavigationCache(
        IGitHubCommitQueryService commitQueryService,
        IAccountWorkQuiescence accountWork)
        : this(
            commitQueryService,
            accountWork,
            new ApplicationTaskCoordinator(),
            UnrestrictedAdaptivePrefetchPolicy.Instance)
    {
    }

    public CommitNavigationCache(
        IGitHubCommitQueryService commitQueryService,
        IAccountWorkQuiescence accountWork,
        IAdaptivePrefetchPolicy prefetchPolicy)
        : this(commitQueryService, accountWork, new ApplicationTaskCoordinator(), prefetchPolicy)
    {
    }

    public CommitNavigationCache(
        IGitHubCommitQueryService commitQueryService,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator,
        IAdaptivePrefetchPolicy prefetchPolicy)
    {
        _commitQueryService = commitQueryService ?? throw new ArgumentNullException(nameof(commitQueryService));
        _accountWork = accountWork ?? throw new ArgumentNullException(nameof(accountWork));
        _taskCoordinator = taskCoordinator ?? throw new ArgumentNullException(nameof(taskCoordinator));
        _prefetchPolicy = prefetchPolicy ?? throw new ArgumentNullException(nameof(prefetchPolicy));
    }

    public void Store(string accountPartition, CommitNavigationSnapshot snapshot)
    {
        string? normalizedPartition = NormalizeAccountPartition(accountPartition);
        if (normalizedPartition is null ||
            string.IsNullOrWhiteSpace(snapshot.Owner) ||
            string.IsNullOrWhiteSpace(snapshot.RepositoryName) ||
            string.IsNullOrWhiteSpace(snapshot.Sha))
        {
            return;
        }

        try
        {
            using IAccountWorkLease lease = _accountWork.Enter(normalizedPartition);
            _snapshots[CreateKey(normalizedPartition, snapshot.Owner, snapshot.RepositoryName, snapshot.Sha)] = snapshot;
        }
        catch (OperationCanceledException)
        {
            // Account removal rejects late navigation handoffs.
        }
    }

    public bool TryGet(
        string accountPartition,
        string owner,
        string repositoryName,
        string sha,
        out CommitNavigationSnapshot snapshot)
    {
        snapshot = default!;
        string? normalizedPartition = NormalizeAccountPartition(accountPartition);
        if (normalizedPartition is null ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName) ||
            string.IsNullOrWhiteSpace(sha))
        {
            return false;
        }

        if (!_snapshots.TryGetValue(
                CreateKey(normalizedPartition, owner, repositoryName, sha),
                out CommitNavigationSnapshot? candidate))
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

    public Task ClearPartitionAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        cancellationToken.ThrowIfCancellationRequested();
        string prefix = partition.ToLowerInvariant() + ":";
        foreach (string key in _snapshots.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _snapshots.TryRemove(key, out _);
            }
        }

        if (_snapshots.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            _inFlightPrefetches.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromException(new InvalidOperationException(
                "The commit navigation cache still contains work for the removed account."));
        }

        return Task.CompletedTask;
    }

    public async Task PrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken = default)
    {
        _ = await PrefetchWithResultAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            sha,
            reason,
            cancellationToken);
    }

    public Task<CommitPrefetchOutcome> PrefetchWithResultAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken = default)
    {
        string? normalizedPartition = NormalizeAccountPartition(userId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            normalizedPartition is null ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName) ||
            string.IsNullOrWhiteSpace(sha))
        {
            return Task.FromResult(CommitPrefetchOutcome.Failure);
        }

        string key = CreateKey(normalizedPartition, owner, repositoryName, sha);
        return _inFlightPrefetches.GetOrAdd(
            key,
            _ => PrefetchCoreAsync(accessToken, normalizedPartition, owner, repositoryName, sha, reason, cancellationToken));
    }

    public IDisposable SchedulePrefetch(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        TimeSpan delay)
    {
        string? normalizedPartition = NormalizeAccountPartition(userId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            normalizedPartition is null ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName) ||
            string.IsNullOrWhiteSpace(sha))
        {
            return DisposableAction.Empty;
        }

        if (!_prefetchPolicy.Evaluate(
                normalizedPartition,
                AdaptivePrefetchFeature.Commits,
                AdaptivePrefetchStage.Schedule).IsAllowed)
        {
            return DisposableAction.Empty;
        }

        CancellationTokenSource cancellation = new();
        Task scheduledTask = _taskCoordinator.RunAsync(
            token => RunScheduledPrefetchAsync(
                accessToken,
                normalizedPartition,
                owner,
                repositoryName,
                sha,
                reason,
                delay,
                token),
            new ApplicationTaskOptions("commits.prefetch.scheduled", normalizedPartition),
            cancellation.Token);
        return new DisposableAction(cancellation, scheduledTask);
    }

    private async Task RunScheduledPrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            _ = await PrefetchWithResultAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                sha,
                reason,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<CommitPrefetchOutcome> PrefetchCoreAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken)
    {
        string key = CreateKey(userId, owner, repositoryName, sha);
        IAccountWorkLease? lease = null;
        try
        {
            lease = _accountWork.Enter(userId, cancellationToken);
            CancellationToken effectiveToken = lease.CancellationToken;
            if (!_prefetchPolicy.Evaluate(
                    userId,
                    AdaptivePrefetchFeature.Commits,
                    AdaptivePrefetchStage.Execute).IsAllowed)
            {
                return CommitPrefetchOutcome.Suppressed;
            }

            if (TryGet(userId, owner, repositoryName, sha, out CommitNavigationSnapshot cached) &&
                cached.Commit.Files.Length > 0 &&
                DateTimeOffset.UtcNow - cached.StoredAt < TimeSpan.FromMinutes(2))
            {
                return CommitPrefetchOutcome.Success;
            }

            CommitDetailAggregate? aggregate = await _commitQueryService.GetCommitPrefetchAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                sha,
                effectiveToken);
            effectiveToken.ThrowIfCancellationRequested();
            if (aggregate is not null)
            {
                Store(userId, new CommitNavigationSnapshot(
                    owner,
                    repositoryName,
                    aggregate.Commit.Sha,
                    aggregate.Commit,
                    aggregate.Comments,
                    aggregate.CombinedStatus,
                    aggregate.CheckRuns,
                    aggregate.AssociatedPullRequests,
                    DateTimeOffset.UtcNow,
                    reason.ToString()));
                return CommitPrefetchOutcome.Success;
            }

            return CommitPrefetchOutcome.Failure;
        }
        catch (OperationCanceledException)
        {
            return CommitPrefetchOutcome.Canceled;
        }
        catch
        {
            // Predictive fetch should never become a visible navigation failure.
            return CommitPrefetchOutcome.Failure;
        }
        finally
        {
            _inFlightPrefetches.TryRemove(key, out _);
            lease?.Dispose();
        }
    }

    private static string CreateKey(
        string accountPartition,
        string owner,
        string repositoryName,
        string sha) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{accountPartition.Trim().ToLowerInvariant()}:{owner.Trim().ToLowerInvariant()}/{repositoryName.Trim().ToLowerInvariant()}@{sha.Trim().ToLowerInvariant()}");

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
            }
            finally
            {
                cancellation.Dispose();
                GC.KeepAlive(Interlocked.Exchange(ref _scheduledTask, null));
            }
        }
    }
}
