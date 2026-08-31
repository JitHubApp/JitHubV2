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

public sealed class GitHubDashboardQueryService : IGitHubDashboardQueryService
{
    private const int RecentRepositoryCount = 8;
    private const int ActivityCount = 12;
    private const int NotificationCount = 10;
    private const int RecommendationCount = 5;
    private readonly IGitHubQueryService _queryService;

    public GitHubDashboardQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public async Task<DashboardHomeSnapshot> GetSnapshotAsync(
        string accessToken,
        string userId,
        GitHubUser? currentUser,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return CreatePublicPreviewSnapshot();
        }

        string normalizedUserId = GitHubAccountPartition.Require(userId);
        DashboardSectionResult<GitHubUser> userResult = currentUser is null
            ? await FetchSectionAsync(
                CreateQuery(
                    accessToken,
                    normalizedUserId,
                    "user",
                    GitHubCachePolicy.MutableResource,
                    Phase0GitHubJsonSerializerContext.Default.GitHubUser,
                    (string[])["dashboard-user"],
                    GitHubRequestPriority.Visible),
                new GitHubUser(),
                cancellationToken)
            : new DashboardSectionResult<GitHubUser>(
                currentUser,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.Add(GitHubCachePolicy.TtlForResource(GitHubCachePolicy.MutableResource)));

        GitHubUser? user = userResult.Value.Id > 0 || !string.IsNullOrWhiteSpace(userResult.Value.Login)
            ? userResult.Value
            : currentUser;

        if (user is null || string.IsNullOrWhiteSpace(user.Login))
        {
            return DashboardHomeSnapshot.Empty with
            {
                User = user,
                Metrics = new DashboardSectionResult<DashboardMetricItem[]>(
                    [],
                    CacheState.Error,
                    null,
                    null,
                    ErrorMessage: "GitHub profile details are not available yet.")
            };
        }

        Task<DashboardSectionResult<GitHubRepository[]>> repositoriesTask = GetRecentRepositoriesAsync(
            accessToken,
            normalizedUserId,
            cancellationToken);
        Task<DashboardSectionResult<GitHubActivityEvent[]>> userEventsTask = GetActivityEventsAsync(
            accessToken,
            normalizedUserId,
            user.Login,
            received: false,
            cancellationToken);
        Task<DashboardSectionResult<GitHubActivityEvent[]>> receivedEventsTask = GetActivityEventsAsync(
            accessToken,
            normalizedUserId,
            user.Login,
            received: true,
            cancellationToken);
        Task<DashboardSectionResult<GitHubNotificationThread[]>> notificationsTask = GetNotificationsAsync(
            accessToken,
            normalizedUserId,
            cancellationToken);
        Task<DashboardSectionResult<GitHubRepository[]>> starredTask = GetStarredRepositoriesAsync(
            accessToken,
            normalizedUserId,
            cancellationToken);
        Task<DashboardSectionResult<GitHubSearchCountResponse>> issueCountTask = GetSearchCountAsync(
            accessToken,
            normalizedUserId,
            $"type:issue state:open involves:{user.Login}",
            "dashboard-issue-count",
            cancellationToken);
        Task<DashboardSectionResult<GitHubSearchCountResponse>> pullRequestCountTask = GetSearchCountAsync(
            accessToken,
            normalizedUserId,
            $"type:pr state:open involves:{user.Login}",
            "dashboard-pr-count",
            cancellationToken);

        await Task.WhenAll(
            repositoriesTask,
            userEventsTask,
            receivedEventsTask,
            notificationsTask,
            starredTask,
            issueCountTask,
            pullRequestCountTask);

        DashboardSectionResult<GitHubRepository[]> repositories = await repositoriesTask;
        DashboardSectionResult<GitHubActivityEvent[]> userEvents = await userEventsTask;
        DashboardSectionResult<GitHubActivityEvent[]> receivedEvents = await receivedEventsTask;
        DashboardSectionResult<GitHubNotificationThread[]> notifications = await notificationsTask;
        DashboardSectionResult<GitHubRepository[]> starred = await starredTask;
        DashboardSectionResult<GitHubSearchCountResponse> issueCount = await issueCountTask;
        DashboardSectionResult<GitHubSearchCountResponse> pullRequestCount = await pullRequestCountTask;

        DashboardSectionResult<GitHubActivityEvent[]> activity = new(
            DashboardActivityMerger.Merge(userEvents.Value, receivedEvents.Value, ActivityCount),
            CombineCacheState(userEvents.CacheState, receivedEvents.CacheState),
            Max(userEvents.FetchedAt, receivedEvents.FetchedAt),
            Min(userEvents.StaleAfter, receivedEvents.StaleAfter),
            userEvents.IsRefreshInProgress || receivedEvents.IsRefreshInProgress,
            FirstError(userEvents, receivedEvents));

        DashboardSectionResult<GitHubRepository[]> recommendations = await GetRecommendationsAsync(
            accessToken,
            normalizedUserId,
            repositories,
            starred,
            cancellationToken);

        DashboardSectionResult<DashboardMetricItem[]> metrics = BuildMetrics(
            user,
            repositories,
            issueCount,
            pullRequestCount);

        return new DashboardHomeSnapshot(
            user,
            metrics,
            repositories,
            activity,
            notifications,
            recommendations);
    }

    private async Task<DashboardSectionResult<GitHubRepository[]>> GetRecentRepositoriesAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken)
    {
        GitHubQuery<GitHubRepository[]> query = CreateQuery(
            accessToken,
            userId,
            $"user/repos?sort=updated&direction=desc&per_page={RecentRepositoryCount}&page=1",
            GitHubCachePolicy.RepositoryResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            (string[])["dashboard-recent-repos", "user-repos", "repo"],
            GitHubRequestPriority.Visible);
        return await FetchSectionAsync(query, Array.Empty<GitHubRepository>(), cancellationToken);
    }

    private async Task<DashboardSectionResult<GitHubActivityEvent[]>> GetActivityEventsAsync(
        string accessToken,
        string userId,
        string login,
        bool received,
        CancellationToken cancellationToken)
    {
        string escapedLogin = Uri.EscapeDataString(login);
        string path = received
            ? $"users/{escapedLogin}/received_events?per_page={ActivityCount}&page=1"
            : $"users/{escapedLogin}/events?per_page={ActivityCount}&page=1";
        GitHubQuery<GitHubActivityEvent[]> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubActivityEventArray,
            received
                ? (string[])["dashboard-received-events"]
                : (string[])["dashboard-user-events"],
            GitHubRequestPriority.Visible);
        return await FetchSectionAsync(query, Array.Empty<GitHubActivityEvent>(), cancellationToken);
    }

    private async Task<DashboardSectionResult<GitHubNotificationThread[]>> GetNotificationsAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken)
    {
        GitHubQuery<GitHubNotificationThread[]> query = CreateQuery(
            accessToken,
            userId,
            $"notifications?all=false&participating=false&per_page={NotificationCount}",
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubNotificationThreadArray,
            (string[])["dashboard-notifications", "notifications"],
            GitHubRequestPriority.Visible);
        return await FetchSectionAsync(
            query,
            Array.Empty<GitHubNotificationThread>(),
            cancellationToken,
            reconnectOnUnauthorized: true);
    }

    private async Task<DashboardSectionResult<GitHubRepository[]>> GetStarredRepositoriesAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken)
    {
        GitHubQuery<GitHubRepository[]> query = CreateQuery(
            accessToken,
            userId,
            "user/starred?sort=updated&direction=desc&per_page=12&page=1",
            GitHubCachePolicy.RepositoryResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            (string[])["dashboard-starred-repos", "repo"],
            GitHubRequestPriority.Visible);
        return await FetchSectionAsync(query, Array.Empty<GitHubRepository>(), cancellationToken);
    }

    private async Task<DashboardSectionResult<GitHubSearchCountResponse>> GetSearchCountAsync(
        string accessToken,
        string userId,
        string queryText,
        string tag,
        CancellationToken cancellationToken)
    {
        string path = $"search/issues?q={Uri.EscapeDataString(queryText)}&per_page=1&page=1";
        GitHubQuery<GitHubSearchCountResponse> query = CreateQuery(
            accessToken,
            userId,
            path,
            GitHubCachePolicy.SearchResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubSearchCountResponse,
            (string[])[tag],
            GitHubRequestPriority.Visible);
        return await FetchSectionAsync(query, new GitHubSearchCountResponse(), cancellationToken);
    }

    private async Task<DashboardSectionResult<GitHubRepository[]>> GetRecommendationsAsync(
        string accessToken,
        string userId,
        DashboardSectionResult<GitHubRepository[]> recentRepositories,
        DashboardSectionResult<GitHubRepository[]> starredRepositories,
        CancellationToken cancellationToken)
    {
        DashboardSectionResult<GitHubRepository[]> languageSearch = DashboardSectionResult<GitHubRepository[]>.Empty([]);
        string? primaryLanguage = DashboardRecommendationBuilder.SelectPrimaryLanguage(recentRepositories.Value);
        if (!string.IsNullOrWhiteSpace(primaryLanguage))
        {
            string queryText = $"language:{primaryLanguage} stars:>100 sort:stars";
            GitHubQuery<GitHubRepositorySearchResponse> query = CreateQuery(
                accessToken,
                userId,
                $"search/repositories?q={Uri.EscapeDataString(queryText)}&sort=stars&order=desc&per_page=8&page=1",
                GitHubCachePolicy.SearchResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubRepositorySearchResponse,
                (string[])["dashboard-recommendation-search", "repo-search"],
                GitHubRequestPriority.Visible);
            DashboardSectionResult<GitHubRepositorySearchResponse> search = await FetchSectionAsync(
                query,
                new GitHubRepositorySearchResponse(),
                cancellationToken);
            languageSearch = new DashboardSectionResult<GitHubRepository[]>(
                search.Value.Items ?? [],
                search.CacheState,
                search.FetchedAt,
                search.StaleAfter,
                search.IsRefreshInProgress,
                search.ErrorMessage,
                search.RequiresReconnect);
        }

        GitHubRepository[] recommendations = DashboardRecommendationBuilder.Build(
            recentRepositories.Value,
            starredRepositories.Value,
            languageSearch.Value,
            RecommendationCount);

        return new DashboardSectionResult<GitHubRepository[]>(
            recommendations,
            CombineCacheState(recentRepositories.CacheState, starredRepositories.CacheState, languageSearch.CacheState),
            Max(recentRepositories.FetchedAt, starredRepositories.FetchedAt, languageSearch.FetchedAt),
            Min(recentRepositories.StaleAfter, starredRepositories.StaleAfter, languageSearch.StaleAfter),
            recentRepositories.IsRefreshInProgress || starredRepositories.IsRefreshInProgress || languageSearch.IsRefreshInProgress,
            FirstError(recentRepositories, starredRepositories, languageSearch));
    }

    private static DashboardSectionResult<DashboardMetricItem[]> BuildMetrics(
        GitHubUser user,
        DashboardSectionResult<GitHubRepository[]> repositories,
        DashboardSectionResult<GitHubSearchCountResponse> issueCount,
        DashboardSectionResult<GitHubSearchCountResponse> pullRequestCount)
    {
        CacheState cacheState = CombineCacheState(repositories.CacheState, issueCount.CacheState, pullRequestCount.CacheState);
        DashboardMetricItem[] items =
        [
            new("Repositories", FormatCount(user.PublicRepos), "public repos", "\uE8B7", repositories.CacheState, DashboardMetricIds.Repositories),
            new("Open issues", FormatCount(issueCount.Value.TotalCount), "involving you", "\uE8A5", issueCount.CacheState, DashboardMetricIds.Issues),
            new("Open PRs", FormatCount(pullRequestCount.Value.TotalCount), "involving you", "\uE8EE", pullRequestCount.CacheState, DashboardMetricIds.PullRequests),
            new("Followers", FormatCount(user.Followers), "profile followers", "\uE716", CacheState.Fresh, DashboardMetricIds.Followers)
        ];

        return new DashboardSectionResult<DashboardMetricItem[]>(
            items,
            cacheState,
            Max(repositories.FetchedAt, issueCount.FetchedAt, pullRequestCount.FetchedAt),
            Min(repositories.StaleAfter, issueCount.StaleAfter, pullRequestCount.StaleAfter),
            repositories.IsRefreshInProgress || issueCount.IsRefreshInProgress || pullRequestCount.IsRefreshInProgress,
            FirstError(repositories, issueCount, pullRequestCount));
    }

    private async Task<DashboardSectionResult<T>> FetchSectionAsync<T>(
        GitHubQuery<T> query,
        T fallbackValue,
        CancellationToken cancellationToken,
        bool reconnectOnUnauthorized = false)
        where T : class
    {
        try
        {
            CachedResult<T> result = await _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
            return new DashboardSectionResult<T>(
                result.Value ?? fallbackValue,
                result.CacheState,
                result.FetchedAt,
                result.StaleAfter,
                result.IsRefreshInProgress,
                result.RefreshError is null
                    ? null
                    : JitHub.WinUI.Helpers.UserFacingError.For(
                        result.RefreshError,
                        JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                        "dashboard-section"));
        }
        catch (GitHubAuthenticationException ex) when (reconnectOnUnauthorized)
        {
            return new DashboardSectionResult<T>(
                fallbackValue,
                CacheState.Error,
                null,
                null,
                ErrorMessage: JitHub.WinUI.Helpers.UserFacingError.For(ex, JitHub.WinUI.Helpers.UserFacingErrorKind.Loading, "dashboard-notifications"),
                RequiresReconnect: true);
        }
        catch (GitHubApiException ex) when (reconnectOnUnauthorized && ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new DashboardSectionResult<T>(
                fallbackValue,
                CacheState.Error,
                null,
                null,
                ErrorMessage: JitHub.WinUI.Helpers.UserFacingError.For(ex, JitHub.WinUI.Helpers.UserFacingErrorKind.Loading, "dashboard-notifications"),
                RequiresReconnect: true);
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            return new DashboardSectionResult<T>(
                fallbackValue,
                CacheState.Error,
                null,
                null,
                ErrorMessage: JitHub.WinUI.Helpers.UserFacingError.For(ex, JitHub.WinUI.Helpers.UserFacingErrorKind.Loading, "dashboard-notifications"));
        }
    }

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        JsonTypeInfo<T> jsonTypeInfo,
        IReadOnlyList<string> tags,
        GitHubRequestPriority priority)
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
            priority);
    }

    private static DashboardHomeSnapshot CreatePublicPreviewSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled)
        {
            GitHubRepository[] largeRepositories = ProductPerformanceLargeAccountFixture.CreateRepositories(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.RepositoryCount));
            GitHubActivityEvent[] largeActivity = ProductPerformanceLargeAccountFixture.CreateActivity(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.ActivityCount));
            GitHubNotificationThread[] largeNotifications = ProductPerformanceLargeAccountFixture.CreateNotifications(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.NotificationCount));
            GitHubUser largeUser = new()
            {
                Id = 4_042_024,
                Login = "performance-owner",
                Name = "Performance Owner",
                AvatarUrl = "ms-appx:///Assets/Octocat.png",
                HtmlUrl = "https://github.com/performance-owner",
                PublicRepos = largeRepositories.Count(static repository => !repository.Private),
                Followers = ProductPerformanceLargeAccountFixture.PeopleCount,
                Following = 128
            };
            DashboardMetricItem[] largeMetrics =
            [
                new("Repositories", largeRepositories.Length.ToString(), "public repos", "\uE8B7", CacheState.Fresh, DashboardMetricIds.Repositories),
                new("Open issues", ProductPerformanceLargeAccountFixture.WorkItemCount.ToString(), "involving you", "\uE8A5", CacheState.Fresh, DashboardMetricIds.Issues),
                new("Open PRs", ProductPerformanceLargeAccountFixture.WorkItemCount.ToString(), "involving you", "\uE8EE", CacheState.Fresh, DashboardMetricIds.PullRequests),
                new("Followers", ProductPerformanceLargeAccountFixture.PeopleCount.ToString(), "profile followers", "\uE716", CacheState.Fresh, DashboardMetricIds.Followers)
            ];
            return new DashboardHomeSnapshot(
                largeUser,
                new DashboardSectionResult<DashboardMetricItem[]>(largeMetrics, CacheState.Fresh, now, now.AddMinutes(5)),
                new DashboardSectionResult<GitHubRepository[]>(largeRepositories, CacheState.Fresh, now, now.AddMinutes(30)),
                new DashboardSectionResult<GitHubActivityEvent[]>(largeActivity, CacheState.Fresh, now, now.AddMinutes(5)),
                new DashboardSectionResult<GitHubNotificationThread[]>(largeNotifications, CacheState.Fresh, now, now.AddMinutes(5)),
                new DashboardSectionResult<GitHubRepository[]>(largeRepositories.Take(100).ToArray(), CacheState.Fresh, now, now.AddMinutes(15)));
        }

        GitHubUser user = new()
        {
            Id = 4_042_024,
            Login = "JitHubApp",
            Name = "JitHub",
            AvatarUrl = "https://avatars.githubusercontent.com/u/170190931",
            HtmlUrl = "https://github.com/JitHubApp",
            PublicRepos = 4,
            Followers = 128,
            Following = 8
        };
        GitHubRepository[] repositories =
        [
            CreatePreviewRepository(1, "JitHubApp", "JitHubV2", "Native Windows GitHub client built with WinUI.", "C#", 420, 32),
            CreatePreviewRepository(2, "JitHubApp", "open-ui", "High-density UI primitives for desktop tools.", "C#", 184, 21),
            CreatePreviewRepository(3, "JitHubApp", "agentic-company", "Workflow experiments for developer tooling.", "TypeScript", 96, 9)
        ];
        GitHubActivityEvent[] activity =
        [
            CreatePreviewActivity("preview-1", "PushEvent", repositories[0], now.AddMinutes(-8)),
            CreatePreviewActivity("preview-2", "IssuesEvent", repositories[1], now.AddHours(-1)),
            CreatePreviewActivity("preview-3", "WatchEvent", repositories[2], now.AddHours(-3))
        ];
        DashboardMetricItem[] metrics =
        [
            new("Repositories", "4", "public repos", "\uE8B7", CacheState.Fresh, DashboardMetricIds.Repositories),
            new("Open issues", "12", "involving you", "\uE8A5", CacheState.Fresh, DashboardMetricIds.Issues),
            new("Open PRs", "5", "involving you", "\uE8EE", CacheState.Fresh, DashboardMetricIds.PullRequests),
            new("Followers", "128", "profile followers", "\uE716", CacheState.Fresh, DashboardMetricIds.Followers)
        ];

        return new DashboardHomeSnapshot(
            user,
            new DashboardSectionResult<DashboardMetricItem[]>(metrics, CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubRepository[]>(repositories, CacheState.Fresh, now, now.AddMinutes(30)),
            new DashboardSectionResult<GitHubActivityEvent[]>(activity, CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubNotificationThread[]>([], CacheState.Fresh, now, now.AddMinutes(5)),
            new DashboardSectionResult<GitHubRepository[]>(repositories.Take(5).ToArray(), CacheState.Fresh, now, now.AddMinutes(15)));
    }

    private static GitHubRepository CreatePreviewRepository(
        long id,
        string owner,
        string name,
        string description,
        string language,
        int stars,
        int forks) =>
        new()
        {
            Id = id,
            Name = name,
            FullName = $"{owner}/{name}",
            Description = description,
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Language = language,
            StargazersCount = stars,
            ForksCount = forks,
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = $"https://github.com/{owner}"
            },
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-id)
        };

    private static GitHubActivityEvent CreatePreviewActivity(
        string id,
        string type,
        GitHubRepository repository,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            Type = type,
            CreatedAt = createdAt,
            Actor = new GitHubActor
            {
                Id = 4_042_024,
                Login = "JitHubApp",
                AvatarUrl = "https://avatars.githubusercontent.com/u/170190931"
            },
            Repo = new GitHubActivityRepository
            {
                Id = repository.Id,
                Name = repository.FullName,
                Url = repository.HtmlUrl
            }
        };

    private static string FormatCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}m";
        }

        return value >= 1_000 ? $"{value / 1_000d:0.#}k" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CacheState CombineCacheState(params CacheState[] states)
    {
        if (states.Any(static state => state == CacheState.Error))
        {
            return CacheState.Error;
        }

        if (states.Any(static state => state == CacheState.Stale || state == CacheState.Refreshing))
        {
            return CacheState.Stale;
        }

        if (states.Any(static state => state == CacheState.Miss))
        {
            return CacheState.Miss;
        }

        return CacheState.Fresh;
    }

    private static DateTimeOffset? Max(params DateTimeOffset?[] values) =>
        values.Where(static value => value.HasValue).DefaultIfEmpty().Max();

    private static DateTimeOffset? Min(params DateTimeOffset?[] values) =>
        values.Where(static value => value.HasValue).DefaultIfEmpty().Min();

    private static string? FirstError(params object[] sections)
    {
        foreach (object section in sections)
        {
            string? error = section switch
            {
                DashboardSectionResult<GitHubRepository[]> repositories => repositories.ErrorMessage,
                DashboardSectionResult<GitHubActivityEvent[]> activity => activity.ErrorMessage,
                DashboardSectionResult<GitHubNotificationThread[]> notifications => notifications.ErrorMessage,
                DashboardSectionResult<GitHubSearchCountResponse> count => count.ErrorMessage,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(error))
            {
                return error;
            }
        }

        return null;
    }
}
