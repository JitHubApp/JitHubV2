using System;
using System.Text.Json.Serialization;
using JitHub.WinUI.Helpers;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubIssueComment
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("reactions")]
    public GitHubReactionSummary Reactions { get; set; } = new();

    [JsonPropertyName("author_association")]
    public string? AuthorAssociation { get; set; }

    [JsonPropertyName("pin")]
    public GitHubIssueCommentPin? Pin { get; set; }

    [JsonPropertyName("minimized")]
    public GitHubIssueCommentMinimization? Minimized { get; set; }

    [JsonIgnore]
    public bool IsPinned => Pin is not null;

    [JsonIgnore]
    public bool IsMinimized => Minimized is not null;

    [JsonIgnore]
    public string ViewerLogin { get; set; } = string.Empty;

    [JsonIgnore]
    public bool CanViewerReact { get; set; }

    [JsonIgnore]
    public bool CanViewerReply { get; set; }

    [JsonIgnore]
    public bool CanViewerModerate { get; set; }

    [JsonIgnore]
    public string ReactionsButtonText => LocalizedResourceText.GetString("Common.ReactionsButton", "Reactions");

    [JsonIgnore]
    public string MarkdownAutomationId => $"IssueComment_{Id}";

    [JsonIgnore]
    public string AvatarAutomationId => Id > 0
        ? $"IssueComment_{Id}"
        : $"IssueComment_{NodeId ?? HtmlUrl}_{CreatedAt:O}";

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
            "issue-comment",
            Id > 0 ? Id.ToString() : NodeId ?? HtmlUrl,
            HtmlUrl);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubIssueCommentPin
{
    [JsonPropertyName("pinned_at")]
    public DateTimeOffset PinnedAt { get; set; }

    [JsonPropertyName("pinned_by")]
    public GitHubActor PinnedBy { get; set; } = new();
}

public sealed class GitHubIssueCommentMinimization
{
}
