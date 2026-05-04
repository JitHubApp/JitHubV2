using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

internal sealed class GitHubIssueCreateRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }
}

internal sealed class GitHubRepositoryCreateRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("homepage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Homepage { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("visibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Visibility { get; set; }

    [JsonPropertyName("auto_init")]
    public bool AutoInit { get; set; }

    [JsonPropertyName("license_template")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LicenseTemplate { get; set; }

    [JsonPropertyName("gitignore_template")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitignoreTemplate { get; set; }

    [JsonPropertyName("has_issues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasIssues { get; set; }

    [JsonPropertyName("has_projects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasProjects { get; set; }

    [JsonPropertyName("has_wiki")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasWiki { get; set; }
}

internal sealed class GitHubIssueUpdateRequest
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }
}

internal sealed class GitHubIssueMetadataUpdateRequest
{
    [JsonPropertyName("assignees")]
    public IReadOnlyList<string> Assignees { get; set; } = Array.Empty<string>();

    [JsonPropertyName("labels")]
    public IReadOnlyList<string> Labels { get; set; } = Array.Empty<string>();

    [JsonPropertyName("milestone")]
    public int? Milestone { get; set; }
}

internal sealed class GitHubIssueCommentCreateRequest
{
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

internal sealed class GitHubPullRequestCreateRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("head")]
    public string Head { get; set; } = string.Empty;

    [JsonPropertyName("base")]
    public string Base { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }
}

internal sealed class GitHubPullRequestUpdateRequest
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }
}

internal sealed class GitHubPullRequestMergeRequest
{
    [JsonPropertyName("commit_title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitTitle { get; set; }

    [JsonPropertyName("commit_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitMessage { get; set; }

    [JsonPropertyName("merge_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MergeMethod { get; set; }
}

internal sealed class GitHubPullRequestReviewersUpdateRequest
{
    [JsonPropertyName("reviewers")]
    public IReadOnlyList<string> Reviewers { get; set; } = Array.Empty<string>();
}

internal sealed class GitHubReactionCreateRequest
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

internal sealed class GitHubRepositorySubscriptionRequest
{
    [JsonPropertyName("subscribed")]
    public bool Subscribed { get; set; }

    [JsonPropertyName("ignored")]
    public bool Ignored { get; set; }
}
