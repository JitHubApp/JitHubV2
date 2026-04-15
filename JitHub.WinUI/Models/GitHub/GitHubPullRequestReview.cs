using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubPullRequestReview
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("pull_request_url")]
    public string? PullRequestUrl { get; set; }

    [JsonPropertyName("submitted_at")]
    public DateTimeOffset? SubmittedAt { get; set; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("author_association")]
    public string? AuthorAssociation { get; set; }

    [JsonPropertyName("reactions")]
    public GitHubReactionSummary Reactions { get; set; } = new();
}

public sealed class GitHubPullRequestReviewComment
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("pull_request_url")]
    public string? PullRequestUrl { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("diff_hunk")]
    public string? DiffHunk { get; set; }

    [JsonPropertyName("position")]
    public int? Position { get; set; }

    [JsonPropertyName("original_position")]
    public int? OriginalPosition { get; set; }

    [JsonPropertyName("pull_request_review_id")]
    public long? PullRequestReviewId { get; set; }

    [JsonPropertyName("in_reply_to_id")]
    public long? InReplyToId { get; set; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; set; }

    [JsonPropertyName("original_commit_id")]
    public string? OriginalCommitId { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("reactions")]
    public GitHubReactionSummary Reactions { get; set; } = new();

    [JsonPropertyName("author_association")]
    public string? AuthorAssociation { get; set; }
}
