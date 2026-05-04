using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace JitHub.Models.GitHub;

public interface IGitHubActivityPayload
{
}

public sealed class UnknownActivityPayload : IGitHubActivityPayload
{
    public string EventType { get; set; } = string.Empty;

    public JsonElement RawPayload { get; set; }
}

public sealed class CommitCommentEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("comment")]
    public GitHubCommitComment? Comment { get; set; }
}

public sealed class CreateEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("ref_type")]
    public string? RefType { get; set; }

    [JsonPropertyName("full_ref")]
    public string? FullRef { get; set; }

    [JsonPropertyName("master_branch")]
    public string? MasterBranch { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("pusher_type")]
    public string? PusherType { get; set; }
}

public sealed class DeleteEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("ref_type")]
    public string? RefType { get; set; }

    [JsonPropertyName("full_ref")]
    public string? FullRef { get; set; }

    [JsonPropertyName("pusher_type")]
    public string? PusherType { get; set; }
}

public sealed class DiscussionEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("discussion")]
    public GitHubActivityDiscussion? Discussion { get; set; }
}

public sealed class ForkEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("forkee")]
    public GitHubRepository? Forkee { get; set; }
}

public sealed class GollumEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("pages")]
    public GitHubActivityWikiPage[] Pages { get; set; } = [];
}

public sealed class IssueCommentEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("issue")]
    public GitHubIssue? Issue { get; set; }

    [JsonPropertyName("comment")]
    public GitHubIssueComment? Comment { get; set; }
}

public sealed class IssuesEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("issue")]
    public GitHubIssue? Issue { get; set; }

    [JsonPropertyName("assignee")]
    public GitHubActor? Assignee { get; set; }

    [JsonPropertyName("assignees")]
    public GitHubActor[] Assignees { get; set; } = [];

    [JsonPropertyName("label")]
    public GitHubLabel? Label { get; set; }

    [JsonPropertyName("labels")]
    public GitHubLabel[] Labels { get; set; } = [];
}

public sealed class MemberEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("member")]
    public GitHubActor? Member { get; set; }
}

public sealed class PublicEventPayload : IGitHubActivityPayload
{
}

public sealed class PullRequestEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("pull_request")]
    public GitHubPullRequest? PullRequest { get; set; }

    [JsonPropertyName("assignee")]
    public GitHubActor? Assignee { get; set; }

    [JsonPropertyName("assignees")]
    public GitHubActor[] Assignees { get; set; } = [];

    [JsonPropertyName("label")]
    public GitHubLabel? Label { get; set; }

    [JsonPropertyName("labels")]
    public GitHubLabel[] Labels { get; set; } = [];
}

public sealed class PullRequestReviewEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("pull_request")]
    public GitHubPullRequest? PullRequest { get; set; }

    [JsonPropertyName("review")]
    public GitHubPullRequestReview? Review { get; set; }
}

public sealed class PullRequestReviewCommentEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("pull_request")]
    public GitHubPullRequest? PullRequest { get; set; }

    [JsonPropertyName("comment")]
    public GitHubPullRequestReviewComment? Comment { get; set; }
}

public sealed class PushEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("repository_id")]
    public long RepositoryId { get; set; }

    [JsonPropertyName("push_id")]
    public long PushId { get; set; }

    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("head")]
    public string? Head { get; set; }

    [JsonPropertyName("before")]
    public string? Before { get; set; }

    [JsonPropertyName("size")]
    public int? Size { get; set; }

    [JsonPropertyName("distinct_size")]
    public int? DistinctSize { get; set; }

    [JsonIgnore]
    public int? EnrichedCommitCount { get; set; }

    [JsonIgnore]
    public int CommitCount => EnrichedCommitCount
        ?? PositiveOrNull(Size)
        ?? PositiveOrNull(DistinctSize)
        ?? Commits.Length;

    [JsonPropertyName("commits")]
    public GitHubActivityPushCommit[] Commits { get; set; } = [];

    private static int? PositiveOrNull(int? value) => value.GetValueOrDefault() > 0 ? value : null;
}

public sealed class ReleaseEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("release")]
    public GitHubActivityRelease? Release { get; set; }
}

public sealed class WatchEventPayload : IGitHubActivityPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }
}

public sealed class GitHubActivityDiscussion
{
    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

public sealed class GitHubActivityWikiPage
{
    [JsonPropertyName("page_name")]
    public string? PageName { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

public sealed class GitHubActivityPushCommit
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("distinct")]
    public bool Distinct { get; set; }

    [JsonPropertyName("author")]
    public GitHubActivityCommitAuthor? Author { get; set; }
}

public sealed class GitHubActivityCommitAuthor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

public sealed class GitHubActivityRelease
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }
}

public static class GitHubActivityPayloadFactory
{
    public static IGitHubActivityPayload Create(string? eventType, JsonElement payload)
    {
        return eventType switch
        {
            "CommitCommentEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.CommitCommentEventPayload) ?? new CommitCommentEventPayload(),
            "CreateEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.CreateEventPayload) ?? new CreateEventPayload(),
            "DeleteEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.DeleteEventPayload) ?? new DeleteEventPayload(),
            "DiscussionEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.DiscussionEventPayload) ?? new DiscussionEventPayload(),
            "ForkEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.ForkEventPayload) ?? new ForkEventPayload(),
            "GollumEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.GollumEventPayload) ?? new GollumEventPayload(),
            "IssueCommentEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.IssueCommentEventPayload) ?? new IssueCommentEventPayload(),
            "IssuesEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.IssuesEventPayload) ?? new IssuesEventPayload(),
            "MemberEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.MemberEventPayload) ?? new MemberEventPayload(),
            "PublicEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.PublicEventPayload) ?? new PublicEventPayload(),
            "PullRequestEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.PullRequestEventPayload) ?? new PullRequestEventPayload(),
            "PullRequestReviewEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.PullRequestReviewEventPayload) ?? new PullRequestReviewEventPayload(),
            "PullRequestReviewCommentEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.PullRequestReviewCommentEventPayload) ?? new PullRequestReviewCommentEventPayload(),
            "PushEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.PushEventPayload) ?? new PushEventPayload(),
            "ReleaseEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.ReleaseEventPayload) ?? new ReleaseEventPayload(),
            "WatchEvent" => Deserialize(payload, GitHubJsonSerializerContext.Default.WatchEventPayload) ?? new WatchEventPayload(),
            _ => new UnknownActivityPayload { EventType = eventType ?? string.Empty, RawPayload = payload }
        };
    }

    private static T? Deserialize<T>(JsonElement payload, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        try
        {
            return payload.Deserialize(jsonTypeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
