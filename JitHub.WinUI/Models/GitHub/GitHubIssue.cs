using System;
using System.Globalization;
using System.Text.Json.Serialization;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;

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

    [JsonPropertyName("repository_url")]
    public string? RepositoryUrl { get; set; }

    [JsonPropertyName("comments")]
    public int Comments { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("closed_at")]
    public DateTimeOffset? ClosedAt { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("active_lock_reason")]
    public string? ActiveLockReason { get; set; }

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

    [JsonIgnore]
    public string AutomationId => $"RepoIssueRow_{(Id > 0 ? Id : Number).ToString(CultureInfo.InvariantCulture)}";

    [JsonIgnore]
    public string AutomationName => $"Issue #{Number.ToString(CultureInfo.CurrentCulture)}: " +
        (string.IsNullOrWhiteSpace(Title) ? "Untitled issue" : Title.Trim());

    [JsonIgnore]
    public string AuthorDisplayName => UserIdentityNavigationPolicy.CreatePresentation(
        User?.Login,
        displayName: null,
        LocalizedResourceText.GetString("Common.UnknownUser", "unknown")).DisplayName;

    [JsonIgnore]
    public string? AuthorProfileLogin => UserIdentityNavigationPolicy.GetRoutableLogin(User?.Login);

    [JsonIgnore]
    public string AuthorAvatarUrl => User?.AvatarUrl ?? string.Empty;

    [JsonIgnore]
    public MarkdownDocumentSource? MarkdownSource =>
        MarkdownDocumentSourceFactory.TryCreateFromGitHubUrl(
            IsPullRequest ? "pull-request-body" : "issue-body",
            Id > 0 ? Id.ToString() : Number.ToString(),
            HtmlUrl);
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
