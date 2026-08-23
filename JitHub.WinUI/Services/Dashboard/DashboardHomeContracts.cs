using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed record DashboardHomeSnapshot(
    GitHubUser? User,
    DashboardSectionResult<DashboardMetricItem[]> Metrics,
    DashboardSectionResult<GitHubRepository[]> RecentRepositories,
    DashboardSectionResult<GitHubActivityEvent[]> RecentActivity,
    DashboardSectionResult<GitHubNotificationThread[]> Notifications,
    DashboardSectionResult<GitHubRepository[]> RecommendedRepositories)
{
    public static DashboardHomeSnapshot Empty { get; } = new(
        null,
        DashboardSectionResult<DashboardMetricItem[]>.Empty([]),
        DashboardSectionResult<GitHubRepository[]>.Empty([]),
        DashboardSectionResult<GitHubActivityEvent[]>.Empty([]),
        DashboardSectionResult<GitHubNotificationThread[]>.Empty([]),
        DashboardSectionResult<GitHubRepository[]>.Empty([]));
}

public sealed record DashboardSectionResult<T>(
    T Value,
    CacheState CacheState,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? StaleAfter,
    bool IsRefreshInProgress = false,
    string? ErrorMessage = null,
    bool RequiresReconnect = false,
    PagedDataCompleteness Completeness = PagedDataCompleteness.Complete,
    int LoadedItemCount = 0,
    int LoadedPageCount = 0)
    where T : class
{
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public static DashboardSectionResult<T> Empty(T value) =>
        new(value, CacheState.Miss, null, null);
}

public sealed record DashboardMetricItem(
    string Label,
    string Value,
    string Caption,
    string Glyph,
    CacheState CacheState,
    string Id = "");

public static class DashboardMetricIds
{
    public const string Repositories = "repositories";
    public const string Issues = "issues";
    public const string PullRequests = "pull-requests";
    public const string Followers = "followers";
}

public interface IGitHubDashboardQueryService
{
    Task<DashboardHomeSnapshot> GetSnapshotAsync(
        string accessToken,
        string userId,
        GitHubUser? currentUser,
        CancellationToken cancellationToken = default);
}

public static class DashboardActivityMerger
{
    public static GitHubActivityEvent[] Merge(
        IEnumerable<GitHubActivityEvent>? userEvents,
        IEnumerable<GitHubActivityEvent>? receivedEvents,
        int take)
    {
        Dictionary<string, GitHubActivityEvent> unique = new(StringComparer.Ordinal);
        foreach (GitHubActivityEvent activityEvent in (userEvents ?? []).Concat(receivedEvents ?? []))
        {
            string key = CreateStableActivityId(activityEvent);
            if (!unique.TryGetValue(key, out GitHubActivityEvent? existing) ||
                (activityEvent.CreatedAt ?? DateTimeOffset.MinValue) > (existing.CreatedAt ?? DateTimeOffset.MinValue))
            {
                unique[key] = activityEvent;
            }
        }

        return unique.Values
            .OrderByDescending(static item => item.CreatedAt ?? DateTimeOffset.MinValue)
            .Take(take)
            .ToArray();
    }

    public static string CreateStableActivityId(GitHubActivityEvent activityEvent)
    {
        if (!string.IsNullOrWhiteSpace(activityEvent.Id))
        {
            return activityEvent.Id;
        }

        return string.Join(
            ':',
            activityEvent.Type,
            activityEvent.Repo.Name,
            activityEvent.Actor.Id,
            activityEvent.CreatedAt?.ToUnixTimeMilliseconds() ?? 0);
    }
}

public static class DashboardRecommendationBuilder
{
    public static GitHubRepository[] Build(
        IEnumerable<GitHubRepository>? recentRepositories,
        IEnumerable<GitHubRepository>? starredRepositories,
        IEnumerable<GitHubRepository>? languageSearchRepositories,
        int take)
    {
        Dictionary<string, (GitHubRepository Repository, int Score)> scored = new(StringComparer.OrdinalIgnoreCase);
        Add(scored, starredRepositories, 30);
        Add(scored, languageSearchRepositories, 20);
        Add(scored, recentRepositories, 5);

        return scored.Values
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Repository.StargazersCount)
            .ThenBy(static item => item.Repository.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(static item => item.Repository)
            .Where(static repository => !string.IsNullOrWhiteSpace(repository.FullName))
            .Take(take)
            .ToArray();
    }

    public static string? SelectPrimaryLanguage(IEnumerable<GitHubRepository>? recentRepositories)
    {
        return recentRepositories?
            .Where(static repository => !string.IsNullOrWhiteSpace(repository.Language))
            .GroupBy(static repository => repository.Language!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Key)
            .FirstOrDefault();
    }

    private static void Add(
        Dictionary<string, (GitHubRepository Repository, int Score)> scored,
        IEnumerable<GitHubRepository>? repositories,
        int score)
    {
        foreach (GitHubRepository repository in repositories ?? [])
        {
            string key = !string.IsNullOrWhiteSpace(repository.FullName)
                ? repository.FullName
                : repository.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            scored[key] = scored.TryGetValue(key, out (GitHubRepository Repository, int Score) existing)
                ? (existing.Repository, existing.Score + score)
                : (repository, score);
        }
    }
}
