using System;
using System.Text.Json.Serialization;
using JitHub.Services.Markdown;
using JitHub.Services;
using MarkdownRenderer.Images;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubCommitComment
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("position")]
    public int? Position { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("reactions")]
    public GitHubReactionSummary Reactions { get; set; } = new();

    [JsonIgnore]
    public string MarkdownAutomationId => $"CommitComment_{Id}";

    [JsonIgnore]
    public string AvatarAutomationId => Id > 0
        ? $"CommitComment_{Id}"
        : $"CommitComment_{NodeId ?? HtmlUrl ?? CommitId}_{CreatedAt:O}";

    [JsonIgnore]
    public string AuthorDisplayName => UserIdentityNavigationPolicy.CreatePresentation(
        User?.Login,
        displayName: null,
        "unknown").DisplayName;

    [JsonIgnore]
    public string? AuthorProfileLogin => UserIdentityNavigationPolicy.GetRoutableLogin(User?.Login);

    [JsonIgnore]
    public string AuthorAvatarUrl => User?.AvatarUrl ?? string.Empty;

    [JsonIgnore]
    public MarkdownDocumentSource? MarkdownSource =>
        MarkdownDocumentSourceFactory.TryCreateFromGitHubUrl(
            "commit-comment",
            Id > 0 ? Id.ToString() : NodeId ?? HtmlUrl ?? string.Empty,
            HtmlUrl,
            CommitId);
}
