using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public interface IGitHubRepositoryQueryService
{
    Task<CachedResult<GitHubRepository>> GetRepositoryAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubRepository>> GetRepositoryAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy,
        GitHubRequestPriority priority,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubRepository>> GetRepositoryAsync(
        string accessToken,
        string userId,
        long repositoryId,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubBranch[]>> GetBranchesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int page,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubResourceState>> GetStarStateAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubRepositorySubscription>> GetWatchStateAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<GitHubRepository?> FindExistingForkAsync(
        string accessToken,
        string userId,
        string sourceOwner,
        string sourceRepositoryName,
        string forkOwner,
        CancellationToken cancellationToken = default);

    Task InvalidateStarStateAsync(
        string userId,
        string owner,
        string repositoryName,
        long repositoryId,
        CancellationToken cancellationToken = default);

    Task InvalidateWatchStateAsync(
        string userId,
        string owner,
        string repositoryName,
        long repositoryId,
        CancellationToken cancellationToken = default);

    Task InvalidateRepositoryAsync(
        string userId,
        string owner,
        string repositoryName,
        long repositoryId,
        CancellationToken cancellationToken = default);
}

public static class RepositoryQueryRefreshPolicy
{
    public static bool ShouldPromote<T>(CachedResult<T> result)
        where T : class =>
        result.IsRefreshInProgress || result.CacheState is CacheState.Stale or CacheState.Refreshing;
}

public sealed class GitHubRepositoryQueryService : IGitHubRepositoryQueryService
{
    public const int BranchPageSize = 100;
    private const int ForkPageSize = 100;
    private const int MaximumForkPages = 10;
    private readonly IGitHubQueryService _queryService;
    private readonly bool _enableAutomationFixtures;

    public GitHubRepositoryQueryService(IGitHubQueryService queryService)
        : this(queryService, enableAutomationFixtures: true)
    {
    }

    internal GitHubRepositoryQueryService(
        IGitHubQueryService queryService,
        bool enableAutomationFixtures)
    {
        _queryService = queryService;
        _enableAutomationFixtures = enableAutomationFixtures;
    }

    public Task<CachedResult<GitHubRepository>> GetRepositoryAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default) =>
        GetRepositoryAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            fetchPolicy,
            GitHubRequestPriority.Visible,
            cancellationToken);

    public Task<CachedResult<GitHubRepository>> GetRepositoryAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy,
        GitHubRequestPriority priority,
        CancellationToken cancellationToken = default)
    {
        if (_enableAutomationFixtures && RepositoryActionAutomationScenario.UsesLocalReadFixtures)
        {
            if (RepositoryActionAutomationScenario.ShouldRateLimitForkReadiness(owner))
            {
                return Task.FromException<CachedResult<GitHubRepository>>(
                    new GitHubRateLimitException(
                        HttpStatusCode.TooManyRequests,
                        "Automation fork readiness rate limit.",
                        TimeSpan.FromSeconds(1)));
            }

            return RepositoryActionAutomationScenario.CreateRepositoryResultAsync(owner, repositoryName);
        }

        string path = $"repos/{Escape(owner)}/{Escape(repositoryName)}";
        return ExecuteAsync(
            CreateQuery(
                accessToken,
                userId,
                path,
                GitHubCachePolicy.RepositoryMetadataResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubRepository,
                (string[])["repository", "repository-metadata", RepositoryTag(owner, repositoryName), RepositoryNameTag(owner, repositoryName)],
                priority),
            fetchPolicy,
            cancellationToken);
    }

    public Task<CachedResult<GitHubRepository>> GetRepositoryAsync(
        string accessToken,
        string userId,
        long repositoryId,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (_enableAutomationFixtures && RepositoryActionAutomationScenario.UsesLocalReadFixtures)
        {
            return Task.FromResult(CreateCached(RepositoryActionAutomationScenario.CreateRepository("JitHubApp", "JitHubV2", repositoryId)));
        }

        string path = $"repositories/{repositoryId}";
        return ExecuteAsync(
            CreateQuery(
                accessToken,
                userId,
                path,
                GitHubCachePolicy.RepositoryMetadataResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubRepository,
                (string[])["repository", "repository-metadata", $"repository-id:{repositoryId}"]),
            fetchPolicy,
            cancellationToken);
    }

    public Task<CachedResult<GitHubBranch[]>> GetBranchesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int page,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        if (_enableAutomationFixtures && RepositoryActionAutomationScenario.UsesLocalReadFixtures)
        {
            return Task.FromResult(CreateCached(
                RepositoryActionAutomationScenario.CreateBranches(owner, repositoryName, Math.Max(1, page))));
        }

        string path =
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/branches?per_page={BranchPageSize}&page={Math.Max(1, page)}";
        GitHubQuery<GitHubBranch[]> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.RepositoryMetadataResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubBranchArray,
            (string[])["repository", "repository-branches", RepositoryTag(owner, repositoryName)],
            priority);
        return ExecuteAsync(query, fetchPolicy, cancellationToken);
    }

    public Task<CachedResult<GitHubResourceState>> GetStarStateAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (_enableAutomationFixtures && RepositoryActionAutomationScenario.UsesLocalReadFixtures)
        {
            if (RepositoryActionAutomationScenario.StatesUnavailable)
            {
                return Task.FromException<CachedResult<GitHubResourceState>>(
                    new GitHubApiException(HttpStatusCode.ServiceUnavailable, "Automation star state unavailable."));
            }

            return Task.FromResult(CreateCached(new GitHubResourceState
            {
                Exists = RepositoryActionAutomationScenario.IsStarred
            }));
        }

        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromException<CachedResult<GitHubResourceState>>(
                new GitHubAuthenticationException("Sign in to load repository star state."));
        }

        string path = $"user/starred/{Escape(owner)}/{Escape(repositoryName)}";
        GitHubQuery<GitHubResourceState> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubResourceState,
            (string[])["repository", "repository-star", RepositoryTag(owner, repositoryName), StarTag(owner, repositoryName)],
            GitHubRequestPriority.Visible,
            acceptNotFound: true,
            emptyResponseFactory: status => new GitHubResourceState
            {
                Exists = status != HttpStatusCode.NotFound
            });
        return ExecuteAsync(query, fetchPolicy, cancellationToken);
    }

    public Task<CachedResult<GitHubRepositorySubscription>> GetWatchStateAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default)
    {
        if (_enableAutomationFixtures && RepositoryActionAutomationScenario.UsesLocalReadFixtures)
        {
            if (RepositoryActionAutomationScenario.StatesUnavailable)
            {
                return Task.FromException<CachedResult<GitHubRepositorySubscription>>(
                    new GitHubApiException(HttpStatusCode.ServiceUnavailable, "Automation watch state unavailable."));
            }

            return Task.FromResult(CreateCached(new GitHubRepositorySubscription
            {
                Subscribed = RepositoryActionAutomationScenario.IsWatching
            }));
        }

        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromException<CachedResult<GitHubRepositorySubscription>>(
                new GitHubAuthenticationException("Sign in to load repository watch state."));
        }

        string path = $"repos/{Escape(owner)}/{Escape(repositoryName)}/subscription";
        GitHubQuery<GitHubRepositorySubscription> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositorySubscription,
            (string[])["repository", "repository-watch", RepositoryTag(owner, repositoryName), WatchTag(owner, repositoryName)],
            GitHubRequestPriority.Visible,
            acceptNotFound: true,
            emptyResponseFactory: _ => new GitHubRepositorySubscription());
        return ExecuteAsync(query, fetchPolicy, cancellationToken);
    }

    public async Task<GitHubRepository?> FindExistingForkAsync(
        string accessToken,
        string userId,
        string sourceOwner,
        string sourceRepositoryName,
        string forkOwner,
        CancellationToken cancellationToken = default)
    {
        if (_enableAutomationFixtures && RepositoryActionAutomationScenario.TryCreateReconciledFork(
            sourceOwner,
            sourceRepositoryName,
            forkOwner,
            out GitHubRepository? automationFork))
        {
            return automationFork;
        }

        for (int page = 1; page <= MaximumForkPages; page++)
        {
            string path =
                $"repos/{Escape(sourceOwner)}/{Escape(sourceRepositoryName)}/forks?sort=newest&per_page={ForkPageSize}&page={page}";
            CachedResult<GitHubRepository[]> result = await ExecuteAsync(
                CreateQuery(
                    accessToken,
                    userId,
                    path,
                    GitHubCachePolicy.MutableResource,
                    Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
                    (string[])["repository", "repository-forks", RepositoryTag(sourceOwner, sourceRepositoryName)],
                    GitHubRequestPriority.BackgroundRefresh),
                QueryFetchPolicy.NetworkOnly,
                cancellationToken).ConfigureAwait(false);
            GitHubRepository[] forks = result.Value ?? [];
            GitHubRepository? existing = forks.FirstOrDefault(repository =>
                string.Equals(repository.Owner.Login, forkOwner, StringComparison.OrdinalIgnoreCase));
            if (existing is not null || forks.Length < ForkPageSize)
            {
                return existing;
            }
        }

        return null;
    }

    public Task InvalidateStarStateAsync(
        string userId,
        string owner,
        string repositoryName,
        long repositoryId,
        CancellationToken cancellationToken = default) =>
        _queryService.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            (string[])[StarTag(owner, repositoryName)],
            cancellationToken);

    public Task InvalidateWatchStateAsync(
        string userId,
        string owner,
        string repositoryName,
        long repositoryId,
        CancellationToken cancellationToken = default) =>
        _queryService.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            (string[])[WatchTag(owner, repositoryName)],
            cancellationToken);

    public Task InvalidateRepositoryAsync(
        string userId,
        string owner,
        string repositoryName,
        long repositoryId,
        CancellationToken cancellationToken = default) =>
        _queryService.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            RepositoryInvalidationTags(owner, repositoryName, repositoryId),
            cancellationToken);

    private Task<CachedResult<T>> ExecuteAsync<T>(
        GitHubQuery<T> query,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken)
        where T : class =>
        fetchPolicy == QueryFetchPolicy.NetworkOnly
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, fetchPolicy, cancellationToken);

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        JsonTypeInfo<T> jsonTypeInfo,
        IReadOnlyList<string> tags,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        bool acceptNotFound = false,
        Func<HttpStatusCode, T>? emptyResponseFactory = null)
        where T : class
    {
        string normalizedUserId = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            normalizedUserId,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(normalizedUserId, HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            priority,
            AcceptNotFound: acceptNotFound,
            EmptyResponseFactory: emptyResponseFactory);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());

    internal static string RepositoryTag(string owner, string repositoryName) =>
        $"repo:{owner.Trim().ToLowerInvariant()}/{repositoryName.Trim().ToLowerInvariant()}";

    internal static string RepositoryNameTag(string owner, string repositoryName) =>
        $"repository-name:{owner.Trim().ToLowerInvariant()}/{repositoryName.Trim().ToLowerInvariant()}";

    internal static string RepositoryIdTag(long repositoryId) =>
        repositoryId > 0 ? $"repository-id:{repositoryId}" : string.Empty;

    internal static string StarTag(string owner, string repositoryName) =>
        $"{RepositoryTag(owner, repositoryName)}:star-state";

    internal static string WatchTag(string owner, string repositoryName) =>
        $"{RepositoryTag(owner, repositoryName)}:watch-state";

    private static IReadOnlyCollection<string> RepositoryInvalidationTags(
        string owner,
        string repositoryName,
        long repositoryId,
        params string[] additionalTags)
    {
        HashSet<string> tags = new(StringComparer.Ordinal)
        {
            RepositoryTag(owner, repositoryName),
            RepositoryNameTag(owner, repositoryName)
        };
        if (repositoryId > 0)
        {
            tags.Add(RepositoryIdTag(repositoryId));
        }

        foreach (string tag in additionalTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    internal static CachedResult<T> CreateCached<T>(T value)
        where T : class
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new CachedResult<T>(value, CacheState.Fresh, now, now.AddMinutes(30));
    }
}

internal static class RepositoryActionAutomationScenario
{
    private const string Prefix = "repository-actions-";
    private static readonly object Gate = new();
    private static bool _isStarred;
    private static bool _isWatching;
    private static int _forkPostCount;
    private static int _forkReadinessRateLimitCount;
    private static int _repositoryRouteReadCount;
    private static bool _websiteShowcase;

    internal static string Name =>
        Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO")?.Trim().ToLowerInvariant() ?? string.Empty;

    internal static bool IsEnabled =>
        Name.StartsWith(Prefix, StringComparison.Ordinal) &&
        AppDataPathPolicy.TryGetAutomationRoots(out _, out _);

    internal static bool UsesLocalReadFixtures =>
        (IsEnabled || Volatile.Read(ref _websiteShowcase)) &&
        AppDataPathPolicy.TryGetAutomationRoots(out _, out _);

    internal static void ConfigureWebsiteShowcase(bool enabled) =>
        Volatile.Write(ref _websiteShowcase, enabled);

    internal static bool StatesUnavailable => Name.EndsWith("disabled", StringComparison.Ordinal);

    internal static int ForkPostCount
    {
        get { lock (Gate) { return _forkPostCount; } }
    }

    internal static void RecordForkPost()
    {
        lock (Gate)
        {
            _forkPostCount++;
        }
    }

    internal static bool ShouldRateLimitForkReadiness(string owner)
    {
        if (!IsEnabled ||
            !Name.EndsWith("rate-limit", StringComparison.Ordinal) ||
            !string.Equals(owner, "automation-user", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Gate)
        {
            return _forkReadinessRateLimitCount++ < 2;
        }
    }

    internal static bool IsStarred
    {
        get { lock (Gate) { return _isStarred; } }
        set { lock (Gate) { _isStarred = value; } }
    }

    internal static bool IsWatching
    {
        get { lock (Gate) { return _isWatching; } }
        set { lock (Gate) { _isWatching = value; } }
    }

    internal static GitHubRepository CreateRepository(string owner, string name, long id = 9001) => new()
    {
        Id = id,
        Name = name,
        FullName = $"{owner}/{name}",
        Description = "Repository action automation fixture",
        DefaultBranch = "main",
        HtmlUrl = $"https://github.com/{owner}/{name}",
        StargazersCount = 42,
        SubscribersCount = 7,
        WatchersCount = 7,
        ForksCount = 3,
        OpenIssuesCount = 5,
        Language = "C#",
        UpdatedAt = DateTimeOffset.UtcNow,
        Owner = new GitHubRepositoryOwner { Login = owner }
    };

    internal static async Task<CachedResult<GitHubRepository>> CreateRepositoryResultAsync(string owner, string name)
    {
        if (Name.EndsWith("route-overlap", StringComparison.Ordinal))
        {
            int read;
            lock (Gate)
            {
                read = _repositoryRouteReadCount++;
            }

            await Task.Delay(read == 0 ? TimeSpan.FromMilliseconds(900) : TimeSpan.FromMilliseconds(75));
        }

        return GitHubRepositoryQueryService.CreateCached(CreateRepository(owner, name));
    }

    internal static GitHubBranch[] CreateBranches(string owner, string name, int page)
    {
        bool isAutomationFork = string.Equals(owner, "automation-user", StringComparison.OrdinalIgnoreCase);
        if (isAutomationFork && Name.EndsWith("timeout", StringComparison.Ordinal))
        {
            return [];
        }

        if (!Name.EndsWith("success", StringComparison.Ordinal))
        {
            return page == 1
                ? [new GitHubBranch { Name = "main" }, new GitHubBranch { Name = "dev" }]
                : [];
        }

        if (page == 1)
        {
            return Enumerable.Range(0, GitHubRepositoryQueryService.BranchPageSize)
                .Select(index => new GitHubBranch
                {
                    Name = index switch
                    {
                        0 => "main",
                        1 => "dev",
                        _ => $"fixture/page-one-{index:000}"
                    }
                })
                .ToArray();
        }

        if (page == 2)
        {
            return
            [
                new GitHubBranch { Name = "release-page-2" },
                new GitHubBranch { Name = "hotfix-page-2" }
            ];
        }

        return [];
    }

    internal static bool TryCreateReconciledFork(
        string sourceOwner,
        string sourceName,
        string forkOwner,
        out GitHubRepository? repository)
    {
        if (IsEnabled && Name.EndsWith("reconcile", StringComparison.Ordinal))
        {
            repository = CreateRepository(forkOwner, sourceName, 9100);
            return true;
        }

        repository = null;
        return false;
    }
}
