using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubIssue
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

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("closed_at")]
    public DateTimeOffset? ClosedAt { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("assignees")]
    public GitHubActor[] Assignees { get; set; } = [];

    [JsonPropertyName("labels")]
    public GitHubLabel[] Labels { get; set; } = [];

    [JsonPropertyName("milestone")]
    public GitHubMilestone? Milestone { get; set; }

    [JsonPropertyName("reactions")]
    public GitHubReactionSummary Reactions { get; set; } = new();

    [JsonPropertyName("pull_request")]
    public GitHubIssuePullRequestMarker? PullRequest { get; set; }

    public bool IsPullRequest => PullRequest is not null;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubIssuePullRequestMarker
{
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubLabel
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubMilestone
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("due_on")]
    public DateTimeOffset? DueOn { get; set; }
}
