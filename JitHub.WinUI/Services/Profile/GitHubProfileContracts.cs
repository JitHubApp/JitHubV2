using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public interface IGitHubProfileQueryService
{
    Task<DashboardSectionResult<GitHubUser>> GetIdentityAsync(
        string accessToken,
        string userId,
        string? login,
        bool forceAuthenticatedUser,
        CancellationToken cancellationToken = default);

    Task<GitHubUserProfileSnapshot> GetProfileAsync(
        string accessToken,
        string userId,
        string? login,
        bool forceAuthenticatedUser,
        CancellationToken cancellationToken = default);

    Task<GitHubUser> UpdateAuthenticatedProfileAsync(
        string accessToken,
        string userId,
        GitHubUserProfileUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubRepository[]>> GetRepositoriesAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubRepository[]>> GetRepositoriesPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubRepository[]>> GetStarredRepositoriesAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubRepository[]>> GetStarredRepositoriesPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubUser[]>> GetFollowersAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubUser[]>> GetFollowersPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubUser[]>> GetFollowingAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubUser[]>> GetFollowingPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubActivityEvent[]>> GetPublicActivityAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default);

    Task<DashboardSectionResult<GitHubActivityEvent[]>> GetPublicActivityPageAsync(
        string accessToken,
        string userId,
        string login,
        int page,
        CancellationToken cancellationToken = default);

    Task FollowUserAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default);

    Task UnfollowUserAsync(
        string accessToken,
        string userId,
        string login,
        CancellationToken cancellationToken = default);
}

public static class GitHubProfilePageSizes
{
    public const int Repositories = 50;
    public const int Stars = 50;
    public const int People = 50;
    public const int Activity = 50;
    public const int ActivityMaximum = 300;
}

public sealed record GitHubUserProfileSnapshot(
    DashboardSectionResult<GitHubUser> User,
    DashboardSectionResult<GitHubProfileReadme> Readme,
    DashboardSectionResult<GitHubContributionCalendar> Contributions,
    DashboardSectionResult<GitHubPinnedProfileItem[]> PinnedItems,
    DashboardSectionResult<GitHubRepository[]> Repositories,
    DashboardSectionResult<GitHubRepository[]> StarredRepositories,
    DashboardSectionResult<GitHubUser[]> Followers,
    DashboardSectionResult<GitHubUser[]> Following,
    DashboardSectionResult<GitHubActivityEvent[]> PublicActivity,
    DashboardSectionResult<GitHubOrganization[]> Organizations,
    DashboardSectionResult<GitHubProfileViewerState> ViewerState,
    DashboardSectionResult<GitHubProfileHighlight[]> Highlights);

public sealed record GitHubProfileReadme(
    string Markdown,
    string HtmlUrl,
    string RepositoryFullName,
    bool Exists)
{
    public static GitHubProfileReadme Missing(string login) => new(
        string.Empty,
        string.Empty,
        string.IsNullOrWhiteSpace(login) ? string.Empty : $"{login}/{login}",
        false);
}

public sealed record GitHubContributionCalendar(
    int TotalContributions,
    IReadOnlyList<GitHubContributionWeek> Weeks);

public sealed record GitHubContributionWeek(IReadOnlyList<GitHubContributionDay> Days);

public sealed record GitHubContributionDay(
    DateTimeOffset Date,
    int ContributionCount,
    string Color,
    int Weekday);

public sealed record GitHubProfileHighlight(string Id, string Label, string Glyph, string Tone);

public sealed record GitHubPinnedProfileItem(
    string Kind,
    string Name,
    string NameWithOwner,
    string Description,
    string Url,
    string Language,
    string LanguageColor,
    int Stargazers,
    int Forks,
    DateTimeOffset? UpdatedAt,
    bool IsPrivate,
    bool IsFork);

public sealed record GitHubProfileViewerState(
    bool IsViewer,
    bool ViewerCanFollow,
    bool ViewerIsFollowing,
    string StatusMessage,
    string StatusEmoji);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubOrganization
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("login")]
    public string Login { get; init; } = string.Empty;

    [JsonPropertyName("node_id")]
    public string? NodeId { get; init; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }
}

public sealed class GitHubUserProfileUpdateRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("blog")]
    public string? Blog { get; init; }

    [JsonPropertyName("twitter_username")]
    public string? TwitterUsername { get; init; }

    [JsonPropertyName("company")]
    public string? Company { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("hireable")]
    public bool? Hireable { get; init; }

    [JsonPropertyName("bio")]
    public string? Bio { get; init; }
}

public sealed class GitHubGraphQlRequest
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    [JsonPropertyName("variables")]
    public Dictionary<string, string?>? Variables { get; init; }
}

public sealed class GitHubGraphQlResponse<T>
    where T : class
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("errors")]
    public GitHubGraphQlError[]? Errors { get; init; }

    [JsonIgnore]
    public int? RateLimitRemaining { get; internal set; }

    [JsonIgnore]
    public DateTimeOffset? RateLimitReset { get; internal set; }

    [JsonIgnore]
    public TimeSpan? RetryAfter { get; internal set; }

    [JsonIgnore]
    public string? RateLimitResource { get; internal set; }
}

public sealed class GitHubGraphQlError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public interface IGitHubGraphQlTransport
{
    Task<GitHubGraphQlResponse<T>> SendAsync<T>(
        string accessToken,
        GitHubGraphQlRequest request,
        CancellationToken cancellationToken = default)
        where T : class;
}

public sealed class GitHubProfileGraphQlData
{
    [JsonPropertyName("viewer")]
    public GitHubProfileGraphQlUser? Viewer { get; init; }

    [JsonPropertyName("user")]
    public GitHubProfileGraphQlUser? User { get; init; }
}

public sealed class GitHubProfileGraphQlUser
{
    [JsonPropertyName("login")]
    public string Login { get; init; } = string.Empty;

    [JsonPropertyName("isViewer")]
    public bool IsViewer { get; init; }

    [JsonPropertyName("viewerCanFollow")]
    public bool ViewerCanFollow { get; init; }

    [JsonPropertyName("viewerIsFollowing")]
    public bool ViewerIsFollowing { get; init; }

    [JsonPropertyName("isDeveloperProgramMember")]
    public bool IsDeveloperProgramMember { get; init; }

    [JsonPropertyName("isEmployee")]
    public bool IsEmployee { get; init; }

    [JsonPropertyName("isGitHubStar")]
    public bool IsGitHubStar { get; init; }

    [JsonPropertyName("isHireable")]
    public bool IsHireable { get; init; }

    [JsonPropertyName("isBountyHunter")]
    public bool IsBountyHunter { get; init; }

    [JsonPropertyName("isCampusExpert")]
    public bool IsCampusExpert { get; init; }

    [JsonPropertyName("isSiteAdmin")]
    public bool IsSiteAdmin { get; init; }

    [JsonPropertyName("status")]
    public GitHubProfileStatus? Status { get; init; }

    [JsonPropertyName("contributionsCollection")]
    public GitHubProfileContributionsCollection? ContributionsCollection { get; init; }

    [JsonPropertyName("pinnedItems")]
    public GitHubProfilePinnedItemsConnection? PinnedItems { get; init; }
}

public sealed class GitHubProfileStatus
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("emoji")]
    public string? Emoji { get; init; }
}

public sealed class GitHubProfileContributionsCollection
{
    [JsonPropertyName("contributionCalendar")]
    public GitHubProfileContributionCalendarPayload? ContributionCalendar { get; init; }
}

public sealed class GitHubProfileContributionCalendarPayload
{
    [JsonPropertyName("totalContributions")]
    public int TotalContributions { get; init; }

    [JsonPropertyName("weeks")]
    public GitHubProfileContributionWeekPayload[] Weeks { get; init; } = [];
}

public sealed class GitHubProfileContributionWeekPayload
{
    [JsonPropertyName("contributionDays")]
    public GitHubProfileContributionDayPayload[] ContributionDays { get; init; } = [];
}

public sealed class GitHubProfileContributionDayPayload
{
    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; init; }

    [JsonPropertyName("contributionCount")]
    public int ContributionCount { get; init; }

    [JsonPropertyName("color")]
    public string Color { get; init; } = "#1f2a22";

    [JsonPropertyName("weekday")]
    public int Weekday { get; init; }
}

public sealed class GitHubProfilePinnedItemsConnection
{
    [JsonPropertyName("nodes")]
    public GitHubProfilePinnedItemNode[]? Nodes { get; init; }
}

public sealed class GitHubProfilePinnedItemNode
{
    [JsonPropertyName("__typename")]
    public string TypeName { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("nameWithOwner")]
    public string? NameWithOwner { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("stargazerCount")]
    public int StargazerCount { get; init; }

    [JsonPropertyName("forkCount")]
    public int ForkCount { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; init; }

    [JsonPropertyName("isFork")]
    public bool IsFork { get; init; }

    [JsonPropertyName("primaryLanguage")]
    public GitHubProfileLanguage? PrimaryLanguage { get; init; }
}

public sealed class GitHubProfileLanguage
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }
}
