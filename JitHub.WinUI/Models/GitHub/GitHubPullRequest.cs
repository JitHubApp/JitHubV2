using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubPullRequest
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("comments")]
    public int Comments { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("mergeable")]
    public bool? Mergeable { get; set; }

    [JsonPropertyName("mergeable_state")]
    public string? MergeableState { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("head")]
    public GitHubPullRequestBranch Head { get; set; } = new();

    [JsonPropertyName("base")]
    public GitHubPullRequestBranch Base { get; set; } = new();

    [JsonPropertyName("requested_reviewers")]
    public GitHubActor[] RequestedReviewers { get; set; } = [];
}

public sealed class GitHubPullRequestBranch
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("ref")]
    public string GitRef { get; set; } = string.Empty;

    [JsonIgnore]
    public string Ref => GitRef;
}
