using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepository
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("fork")]
    public bool Fork { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("has_issues")]
    public bool? HasIssues { get; set; }

    [JsonPropertyName("allow_merge_commit")]
    public bool? AllowMergeCommit { get; set; }

    [JsonPropertyName("allow_squash_merge")]
    public bool? AllowSquashMerge { get; set; }

    [JsonPropertyName("allow_rebase_merge")]
    public bool? AllowRebaseMerge { get; set; }

    [JsonPropertyName("allow_auto_merge")]
    public bool? AllowAutoMerge { get; set; }

    [JsonPropertyName("stargazers_count")]
    public int StargazersCount { get; set; }

    [JsonPropertyName("watchers_count")]
    public int WatchersCount { get; set; }

    [JsonPropertyName("subscribers_count")]
    public int SubscribersCount { get; set; }

    [JsonPropertyName("forks_count")]
    public int ForksCount { get; set; }

    [JsonPropertyName("open_issues_count")]
    public int OpenIssuesCount { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("pushed_at")]
    public DateTimeOffset? PushedAt { get; set; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = string.Empty;

    [JsonPropertyName("topics")]
    public string[] Topics { get; set; } = [];

    [JsonPropertyName("permissions")]
    public GitHubRepositoryPermissions? Permissions { get; set; }

    [JsonPropertyName("owner")]
    public GitHubRepositoryOwner Owner { get; set; } = new();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepositoryPermissions
{
    [JsonPropertyName("admin")]
    public bool Admin { get; set; }

    [JsonPropertyName("maintain")]
    public bool Maintain { get; set; }

    [JsonPropertyName("push")]
    public bool Push { get; set; }

    [JsonPropertyName("triage")]
    public bool Triage { get; set; }

    [JsonPropertyName("pull")]
    public bool Pull { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubStarredRepository
{
    [JsonPropertyName("starred_at")]
    public DateTimeOffset StarredAt { get; set; }

    [JsonPropertyName("repo")]
    public GitHubRepository Repository { get; set; } = new();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepositoryOwner
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}
