using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubCheckRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("details_url")]
    public string? DetailsUrl { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("app")]
    public GitHubCheckRunApp? App { get; set; }
}

public sealed class GitHubCheckRunApp
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }
}

internal sealed class GitHubCheckRunResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("check_runs")]
    public GitHubCheckRun[] CheckRuns { get; set; } = [];
}
