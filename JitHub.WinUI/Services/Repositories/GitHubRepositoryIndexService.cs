using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GitHubRepositoryIndexService : IGitHubRepositoryIndexService
{
    internal const int PageSize = 100;
    internal const int MaximumPages = 100;
    internal static readonly TimeSpan SynchronizationReuseWindow = TimeSpan.FromMinutes(1);

    private readonly IGitHubQueryService _queryService;
    private readonly IGitHubCacheStore _cacheStore;
    private readonly ITelemetryService _telemetry;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly ConcurrentDictionary<string, RepositoryIndexState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _syncGates = new(StringComparer.Ordinal);

    public GitHubRepositoryIndexService(
        IGitHubQueryService queryService,
        IGitHubCacheStore cacheStore,
        ITelemetryService telemetry)
        : this(queryService, cacheStore, telemetry, new AccountWorkQuiescence())
    {
    }

    public GitHubRepositoryIndexService(
        IGitHubQueryService queryService,
        IGitHubCacheStore cacheStore,
        ITelemetryService telemetry,
        IAccountWorkQuiescence accountWork)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _telemetry = SafeTelemetryService.Wrap(telemetry);
        _accountWork = accountWork ?? throw new ArgumentNullException(nameof(accountWork));
    }

    public event EventHandler<AccountRepositoryIndexChangedEventArgs>? Changed;

    public AccountRepositoryIndexSnapshot GetSnapshot(string userId)
    {
        string partition = GitHubAccountPartition.Require(userId, nameof(userId));
        return _states.TryGetValue(partition, out RepositoryIndexState? state)
            ? state.CreateSnapshot()
            : AccountRepositoryIndexSnapshot.Empty(partition);
    }

    public async Task<AccountRepositoryIndexSnapshot> InitializeAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        using IAccountWorkLease lease = _accountWork.Enter(partition, cancellationToken);
        cancellationToken = lease.CancellationToken;
        RepositoryIndexState state = _states.GetOrAdd(partition, static id => new RepositoryIndexState(id));
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            state.ApplyPage(CreatePreviewRepositories(now), 1, isComplete: true, CacheState.Fresh, now);
            state.MarkCacheLoaded();
            RaiseChanged(state);
            return state.CreateSnapshot();
        }

        if (state.HasLoadedCache)
        {
            return state.CreateSnapshot();
        }

        for (int page = 1; page <= MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitHubQuery<GitHubRepository[]> query = CreateQuery(
                accessToken,
                partition,
                page,
                GitHubRequestPriority.BackgroundRefresh);
            CachedResult<GitHubRepository[]>? cached = await _cacheStore.TryGetAsync(query, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (cached?.Value is not { } repositories)
            {
                break;
            }

            bool isComplete = repositories.Length < PageSize;
            state.ApplyPage(repositories, page, isComplete, cached.CacheState, cached.FetchedAt);
            RaiseChanged(state);
            if (isComplete)
            {
                break;
            }
        }

        state.MarkCacheLoaded();
        RaiseChanged(state);
        return state.CreateSnapshot();
    }

    public async Task<AccountRepositoryIndexSnapshot> SynchronizeAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        using IAccountWorkLease lease = _accountWork.Enter(partition, cancellationToken);
        cancellationToken = lease.CancellationToken;
        RepositoryIndexState state = _states.GetOrAdd(partition, static id => new RepositoryIndexState(id));
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            Stopwatch previewStopwatch = Stopwatch.StartNew();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubRepository[] repositories = CreatePreviewRepositories(now);
            state.BeginSynchronization();
            RaiseChanged(state);
            state.CompleteSynchronization(repositories, 1, CacheState.Fresh, now);
            RaiseChanged(state);
            TrackSynchronization("success", state.CreateSnapshot(), previewStopwatch.Elapsed);
            return state.CreateSnapshot();
        }

        if (!forceRefresh && state.CanReuseSynchronization(SynchronizationReuseWindow))
        {
            return state.CreateSnapshot();
        }

        SemaphoreSlim gate = _syncGates.GetOrAdd(partition, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            if (!forceRefresh && state.CanReuseSynchronization(SynchronizationReuseWindow))
            {
                return state.CreateSnapshot();
            }

            state.BeginSynchronization();
            RaiseChanged(state);

            List<GitHubRepository> remoteRepositories = [];
            CacheState aggregateCacheState = CacheState.Miss;
            DateTimeOffset? newestFetch = null;
            for (int page = 1; page <= MaximumPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GitHubQuery<GitHubRepository[]> query = CreateQuery(
                    accessToken,
                    partition,
                    page,
                    page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);
                CachedResult<GitHubRepository[]> result = await _queryService.RefreshAsync(query, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                GitHubRepository[] repositories = result.Value ?? [];
                remoteRepositories.AddRange(repositories);
                aggregateCacheState = result.CacheState;
                newestFetch = result.FetchedAt ?? newestFetch;

                bool isComplete = repositories.Length < PageSize;
                state.ApplyNetworkProgress(remoteRepositories, page, isComplete, aggregateCacheState, newestFetch);
                RaiseChanged(state);
                if (isComplete)
                {
                    state.CompleteSynchronization(remoteRepositories, page, aggregateCacheState, newestFetch);
                    RaiseChanged(state);
                    TrackSynchronization("success", state.CreateSnapshot(), stopwatch.Elapsed);
                    return state.CreateSnapshot();
                }
            }

            state.CompleteSynchronization(remoteRepositories, MaximumPages, aggregateCacheState, newestFetch);
            RaiseChanged(state);
            TrackSynchronization("partial", state.CreateSnapshot(), stopwatch.Elapsed);
            return state.CreateSnapshot();
        }
        catch (OperationCanceledException)
        {
            state.CancelSynchronization();
            RaiseChanged(state);
            throw;
        }
        catch (Exception ex)
        {
            state.FailSynchronization(JitHub.WinUI.Helpers.UserFacingError.For(
                ex,
                JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                "repository-index"));
            RaiseChanged(state);
            TrackSynchronization("error", state.CreateSnapshot(), stopwatch.Elapsed);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveRepositoriesAsync(
        string userId,
        IReadOnlyCollection<long> repositoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);
        string partition = GitHubAccountPartition.Require(userId, nameof(userId));
        using IAccountWorkLease lease = _accountWork.Enter(partition, cancellationToken);
        cancellationToken = lease.CancellationToken;
        if (repositoryIds.Count == 0 || !_states.TryGetValue(partition, out RepositoryIndexState? state))
        {
            return;
        }

        state.Remove(repositoryIds);
        RaiseChanged(state);
        await _queryService.InvalidateTagsAsync(
            partition,
            (string[])[AccountTag(partition)],
            cancellationToken).ConfigureAwait(false);
    }

    public Task ClearPartitionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(userId, nameof(userId));
        cancellationToken.ThrowIfCancellationRequested();
        _states.TryRemove(partition, out _);
        if (_syncGates.TryRemove(partition, out SemaphoreSlim? gate))
        {
            gate.Dispose();
        }

        Changed?.Invoke(
            this,
            new AccountRepositoryIndexChangedEventArgs(AccountRepositoryIndexSnapshot.Empty(partition)));
        return Task.CompletedTask;
    }

    private static GitHubQuery<GitHubRepository[]> CreateQuery(
        string accessToken,
        string partition,
        int page,
        GitHubRequestPriority priority)
    {
        int normalizedPage = Math.Clamp(page, 1, MaximumPages);
        string relativePath = GitHubAuthenticationConstants.IsPublicAccessToken(accessToken)
            ? $"users/JitHubApp/repos?sort=updated&direction=desc&per_page={PageSize}&page={normalizedPage}"
            : $"user/repos?visibility=all&affiliation=owner,collaborator,organization_member&sort=updated&direction=desc&per_page={PageSize}&page={normalizedPage}";
        return new GitHubQuery<GitHubRepository[]>(
            accessToken,
            partition,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(partition, HttpMethod.Get, relativePath),
            GitHubCachePolicy.RepositoryResource,
            GitHubCachePolicy.TtlForResource(GitHubCachePolicy.RepositoryResource),
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            (string[])["account-repositories", AccountTag(partition), "user-repos", "repo"],
            priority);
    }

    private void RaiseChanged(RepositoryIndexState state) =>
        Changed?.Invoke(this, new AccountRepositoryIndexChangedEventArgs(state.CreateSnapshot()));

    private static string AccountTag(string partition) => $"account-repositories:{partition}";

    private void TrackSynchronization(string result, AccountRepositoryIndexSnapshot snapshot, TimeSpan elapsed)
    {
        _telemetry.TrackEvent("repositories.sync.completed", new Dictionary<string, string?>
        {
            ["result"] = result,
            ["cache_state"] = snapshot.CacheState.ToString(),
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(elapsed),
            ["count_bucket"] = CountBucket(snapshot.IndexedCount)
        });
    }

    private static string CountBucket(int count) => count switch
    {
        <= 0 => "0",
        <= 10 => "1_10",
        <= 50 => "11_50",
        <= 100 => "51_100",
        <= 500 => "101_500",
        _ => "501_plus"
    };

    internal static GitHubRepository[] CreatePreviewRepositories(DateTimeOffset now) =>
        ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.CreateRepositories(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.RepositoryCount))
            :
            [
                CreatePreviewRepository(9001, "JitHubApp/JitHubV2", "Native Windows GitHub workflows built with WinUI.", "C#", 420, now.AddHours(-1), ["winui", "github-client"]),
                CreatePreviewRepository(9002, "JitHubApp/open-ui", "Compact native controls for high-density developer tools.", "XAML", 184, now.AddHours(-4), ["fluent", "controls"]),
                CreatePreviewRepository(9003, "JitHubApp/automation-lab", "Reliable UI automation fixtures and accessibility probes.", "C#", 96, now.AddDays(-1), ["testing", "accessibility"]),
                CreatePreviewRepository(9004, "JitHubApp/markdown-renderer", "Selectable native Markdown rendering for WinUI applications.", "C++", 76, now.AddDays(-2), ["markdown", "renderer"]),
                CreatePreviewRepository(9005, "JitHubApp/design-notes", "Archived interface explorations retained for reference.", "Markdown", 18, now.AddDays(-12), ["design"], archived: true),
                CreatePreviewRepository(9006, "JitHubApp/windows-community-toolkit", "A fork used to validate repository filters and navigation.", "C#", 12, now.AddDays(-5), ["windows"], fork: true)
            ];

    private static GitHubRepository CreatePreviewRepository(
        long id,
        string fullName,
        string description,
        string language,
        int stars,
        DateTimeOffset updatedAt,
        string[] topics,
        bool archived = false,
        bool fork = false)
    {
        string[] identity = fullName.Split('/', 2);
        return new GitHubRepository
        {
            Id = id,
            Name = identity[1],
            FullName = fullName,
            Description = description,
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{fullName}",
            Archived = archived,
            Fork = fork,
            Language = language,
            StargazersCount = stars,
            UpdatedAt = updatedAt,
            PushedAt = updatedAt,
            Visibility = "public",
            Topics = topics,
            Owner = new GitHubRepositoryOwner
            {
                Login = identity[0],
                AvatarUrl = "ms-appx:///Assets/Octocat.png",
                HtmlUrl = $"https://github.com/{identity[0]}"
            }
        };
    }

    private sealed class RepositoryIndexState
    {
        private readonly object _gate = new();
        private readonly string _userId;
        private List<GitHubRepository> _repositories = [];
        private bool _isComplete;
        private bool _isSynchronizing;
        private bool _hasLoadedCache;
        private int _pagesLoaded;
        private CacheState _cacheState = CacheState.Miss;
        private DateTimeOffset? _updatedAt;
        private DateTimeOffset? _lastSuccessfulSynchronizationAt;
        private string? _errorMessage;

        public RepositoryIndexState(string userId)
        {
            _userId = userId;
        }

        public bool HasLoadedCache
        {
            get { lock (_gate) { return _hasLoadedCache; } }
        }

        public bool CanReuseSynchronization(TimeSpan reuseWindow)
        {
            lock (_gate)
            {
                return _lastSuccessfulSynchronizationAt is { } completedAt &&
                        DateTimeOffset.UtcNow - completedAt <= reuseWindow &&
                        string.IsNullOrWhiteSpace(_errorMessage);
            }
        }

        public void MarkCacheLoaded()
        {
            lock (_gate)
            {
                _hasLoadedCache = true;
            }
        }

        public void BeginSynchronization()
        {
            lock (_gate)
            {
                _isSynchronizing = true;
                _errorMessage = null;
            }
        }

        public void ApplyPage(
            IReadOnlyList<GitHubRepository> page,
            int pageNumber,
            bool isComplete,
            CacheState cacheState,
            DateTimeOffset? fetchedAt)
        {
            lock (_gate)
            {
                List<GitHubRepository> prefix = pageNumber == 1
                    ? []
                    : _repositories.Take((pageNumber - 1) * PageSize).ToList();
                ApplyStableMerge(prefix.Concat(page), preserveExistingTail: !isComplete);
                _pagesLoaded = Math.Max(_pagesLoaded, pageNumber);
                _isComplete = isComplete;
                _cacheState = cacheState;
                _updatedAt = fetchedAt ?? _updatedAt;
            }
        }

        public void ApplyNetworkProgress(
            IReadOnlyList<GitHubRepository> remote,
            int pageNumber,
            bool isComplete,
            CacheState cacheState,
            DateTimeOffset? fetchedAt)
        {
            lock (_gate)
            {
                ApplyStableMerge(remote, preserveExistingTail: !isComplete);
                _pagesLoaded = pageNumber;
                _isComplete = isComplete;
                _cacheState = cacheState;
                _updatedAt = fetchedAt ?? _updatedAt;
            }
        }

        public void CompleteSynchronization(
            IReadOnlyList<GitHubRepository> remote,
            int pagesLoaded,
            CacheState cacheState,
            DateTimeOffset? fetchedAt)
        {
            lock (_gate)
            {
                ApplyStableMerge(remote, preserveExistingTail: false);
                _pagesLoaded = pagesLoaded;
                _isComplete = pagesLoaded < MaximumPages || remote.Count % PageSize != 0;
                _isSynchronizing = false;
                _cacheState = cacheState;
                _updatedAt = fetchedAt ?? DateTimeOffset.UtcNow;
                _lastSuccessfulSynchronizationAt = DateTimeOffset.UtcNow;
                _errorMessage = null;
                _hasLoadedCache = true;
            }
        }

        public void CancelSynchronization()
        {
            lock (_gate)
            {
                _isSynchronizing = false;
            }
        }

        public void FailSynchronization(string errorMessage)
        {
            lock (_gate)
            {
                _isSynchronizing = false;
                _errorMessage = errorMessage;
                _cacheState = _repositories.Count > 0 ? CacheState.Stale : CacheState.Error;
                _hasLoadedCache = true;
            }
        }

        public void Remove(IReadOnlyCollection<long> repositoryIds)
        {
            HashSet<long> ids = repositoryIds.ToHashSet();
            lock (_gate)
            {
                _repositories.RemoveAll(repository => ids.Contains(repository.Id));
            }
        }

        public AccountRepositoryIndexSnapshot CreateSnapshot()
        {
            lock (_gate)
            {
                return new AccountRepositoryIndexSnapshot(
                    _userId,
                    _repositories.ToArray(),
                    _isComplete,
                    _isSynchronizing,
                    _pagesLoaded,
                    _cacheState,
                    _updatedAt,
                    _errorMessage);
            }
        }

        private void ApplyStableMerge(IEnumerable<GitHubRepository> incoming, bool preserveExistingTail)
        {
            Dictionary<long, GitHubRepository> existing = _repositories
                .Where(static repository => repository.Id > 0)
                .GroupBy(static repository => repository.Id)
                .ToDictionary(static group => group.Key, static group => group.First());
            List<GitHubRepository> merged = [];
            HashSet<long> seen = [];
            foreach (GitHubRepository repository in incoming)
            {
                if (repository.Id <= 0 || seen.Add(repository.Id))
                {
                    merged.Add(repository);
                }
            }

            if (preserveExistingTail)
            {
                foreach (GitHubRepository repository in _repositories)
                {
                    if (repository.Id <= 0 || seen.Add(repository.Id))
                    {
                        merged.Add(repository);
                    }
                }
            }

            _repositories = merged;
        }
    }
}
