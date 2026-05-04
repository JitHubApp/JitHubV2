using System;
using System.Text.Json.Serialization;
using JitHub.WinUI.Helpers;

namespace JitHub.Models.GitHub;

public sealed class GitHubIssueEvent
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("actor")]
    public GitHubActor Actor { get; set; } = new();

    [JsonPropertyName("assignee")]
    public GitHubActor? Assignee { get; set; }

    [JsonPropertyName("assigner")]
    public GitHubActor? Assigner { get; set; }

    [JsonPropertyName("requested_reviewer")]
    public GitHubActor? RequestedReviewer { get; set; }

    [JsonPropertyName("review_requester")]
    public GitHubActor? ReviewRequester { get; set; }

    [JsonPropertyName("requested_team")]
    public GitHubIssueEventRequestedTeam? RequestedTeam { get; set; }

    [JsonPropertyName("label")]
    public GitHubLabel? Label { get; set; }

    [JsonPropertyName("milestone")]
    public GitHubMilestone? Milestone { get; set; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; set; }

    [JsonPropertyName("rename")]
    public GitHubIssueEventRename? Rename { get; set; }

    [JsonPropertyName("dismissed_review")]
    public GitHubIssueEventDismissedReview? DismissedReview { get; set; }

    [JsonPropertyName("lock_reason")]
    public string? LockReason { get; set; }

    [JsonIgnore]
    public string Summary => BuildSummary();

    [JsonIgnore]
    public string MetaText => LocalizedResourceText.Format(
        "GitHubIssueEvent.MetaText",
        "@{0}  •  {1:g}",
        string.IsNullOrWhiteSpace(Actor.Login)
            ? LocalizedResourceText.GetString("Common.UnknownUser", "unknown")
            : Actor.Login,
        CreatedAt.LocalDateTime);

    private string BuildSummary()
    {
        return Event switch
        {
            "assigned" => Assignee is null
                ? LocalizedResourceText.GetString("GitHubIssueEvent.AssignedSelf", "Assigned the item")
                : LocalizedResourceText.Format("GitHubIssueEvent.AssignedUser", "Assigned @{0}", Assignee.Login),
            "unassigned" => Assignee is null
                ? LocalizedResourceText.GetString("GitHubIssueEvent.UnassignedSelf", "Removed an assignee")
                : LocalizedResourceText.Format("GitHubIssueEvent.UnassignedUser", "Removed @{0}", Assignee.Login),
            "labeled" => Label is null
                ? LocalizedResourceText.GetString("GitHubIssueEvent.LabeledSelf", "Added a label")
                : LocalizedResourceText.Format("GitHubIssueEvent.LabeledName", "Added label {0}", Label.Name),
            "unlabeled" => Label is null
                ? LocalizedResourceText.GetString("GitHubIssueEvent.UnlabeledSelf", "Removed a label")
                : LocalizedResourceText.Format("GitHubIssueEvent.UnlabeledName", "Removed label {0}", Label.Name),
            "closed" => LocalizedResourceText.GetString("GitHubIssueEvent.ClosedPullRequest", "Closed the pull request"),
            "reopened" => LocalizedResourceText.GetString("GitHubIssueEvent.ReopenedPullRequest", "Reopened the pull request"),
            "merged" => string.IsNullOrWhiteSpace(CommitId)
                ? LocalizedResourceText.GetString("GitHubIssueEvent.MergedPullRequest", "Merged the pull request")
                : LocalizedResourceText.Format(
                    "GitHubIssueEvent.MergedPullRequestCommit",
                    "Merged the pull request ({0})",
                    CommitId[..Math.Min(7, CommitId.Length)]),
            "review_requested" => RequestedReviewer is not null
                ? LocalizedResourceText.Format("GitHubIssueEvent.RequestedReviewUser", "Requested review from @{0}", RequestedReviewer.Login)
                : RequestedTeam is not null
                    ? LocalizedResourceText.Format("GitHubIssueEvent.RequestedReviewTeam", "Requested review from team {0}", RequestedTeam.Name)
                    : LocalizedResourceText.GetString("GitHubIssueEvent.RequestedReview", "Requested a review"),
            "review_request_removed" => RequestedReviewer is not null
                ? LocalizedResourceText.Format("GitHubIssueEvent.RemovedRequestedReviewer", "Removed requested reviewer @{0}", RequestedReviewer.Login)
                : RequestedTeam is not null
                    ? LocalizedResourceText.Format("GitHubIssueEvent.RemovedRequestedTeam", "Removed requested team {0}", RequestedTeam.Name)
                    : LocalizedResourceText.GetString("GitHubIssueEvent.RemovedReviewRequest", "Removed a review request"),
            "ready_for_review" => LocalizedResourceText.GetString("GitHubIssueEvent.ReadyForReview", "Marked the pull request ready for review"),
            "converted_to_draft" => LocalizedResourceText.GetString("GitHubIssueEvent.ConvertedToDraft", "Converted the pull request to draft"),
            "renamed" => Rename is null
                ? LocalizedResourceText.GetString("GitHubIssueEvent.RenamedPullRequest", "Renamed the pull request")
                : LocalizedResourceText.Format(
                    "GitHubIssueEvent.RenamedPullRequestFormat",
                    "Renamed the pull request from '{0}' to '{1}'",
                    Rename.From,
                    Rename.To),
            "review_dismissed" => DismissedReview is null
                ? LocalizedResourceText.GetString("GitHubIssueEvent.DismissedReview", "Dismissed a review")
                : string.IsNullOrWhiteSpace(DismissedReview.DismissalMessage)
                    ? LocalizedResourceText.GetString("GitHubIssueEvent.DismissedReview", "Dismissed a review")
                    : LocalizedResourceText.Format(
                        "GitHubIssueEvent.DismissedReviewWithMessage",
                        "Dismissed a review: {0}",
                        DismissedReview.DismissalMessage),
            "head_ref_deleted" => LocalizedResourceText.GetString("GitHubIssueEvent.HeadRefDeleted", "Deleted the head branch"),
            "head_ref_restored" => LocalizedResourceText.GetString("GitHubIssueEvent.HeadRefRestored", "Restored the head branch"),
            _ => HumanizeEvent(Event)
        };
    }

    private static string HumanizeEvent(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? LocalizedResourceText.GetString("GitHubIssueEvent.UpdatedPullRequest", "Updated the pull request")
            : value.Replace('_', ' ');
    }
}

public sealed class GitHubIssueEventRequestedTeam
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;
}

public sealed class GitHubIssueEventRename
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

public sealed class GitHubIssueEventDismissedReview
{
    [JsonPropertyName("dismissal_message")]
    public string? DismissalMessage { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }
}
