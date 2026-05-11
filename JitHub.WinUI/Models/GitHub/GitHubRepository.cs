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

    [JsonPropertyName("stargazers_count")]
    public int StargazersCount { get; set; }

    [JsonPropertyName("watchers_count")]
    public int WatchersCount { get; set; }

    [JsonPropertyName("forks_count")]
    public int ForksCount { get; set; }

    [JsonPropertyName("open_issues_count")]
    public int OpenIssuesCount { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("owner")]
    public GitHubRepositoryOwner Owner { get; set; } = new();
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
