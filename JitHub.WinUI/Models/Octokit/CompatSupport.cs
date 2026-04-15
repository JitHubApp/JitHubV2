using System;
using System.Linq;

namespace JitHub.Compatibility.Octokit;

public sealed class StringEnum<TEnum> where TEnum : struct, Enum
{
    public StringEnum()
        : this(default(TEnum))
    {
    }

    public StringEnum(TEnum value)
    {
        Value = value;
        StringValue = value.ToString();
    }

    public StringEnum(string? value)
    {
        StringValue = value ?? string.Empty;
        Value = ParseValue(StringValue);
    }

    public string StringValue { get; set; }

    public TEnum Value { get; set; }

    public override string ToString()
    {
        return StringValue;
    }

    public static implicit operator StringEnum<TEnum>(TEnum value)
    {
        return new(value);
    }

    public static implicit operator StringEnum<TEnum>(string value)
    {
        return new(value);
    }

    public static implicit operator TEnum(StringEnum<TEnum> value)
    {
        return value?.Value ?? default;
    }

    private static TEnum ParseValue(string value)
    {
        if (Enum.TryParse(value, true, out TEnum parsed))
        {
            return parsed;
        }

        string normalized = string.Concat(
            (value ?? string.Empty)
                .Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));

        return Enum.TryParse(normalized, true, out parsed)
            ? parsed
            : default;
    }
}

public enum ItemState
{
    Open,
    Closed
}

public enum ItemStateFilter
{
    Open,
    Closed,
    All
}

public enum IssueFilter
{
    Assigned,
    Created,
    Mentioned,
    Subscribed,
    All
}

public enum IssueSort
{
    Created,
    Updated,
    Comments
}

public enum SortDirection
{
    Ascending,
    Descending
}

public enum PullRequestSort
{
    Created,
    Updated,
    Popularity,
    LongRunning
}

public enum PullRequestMergeMethod
{
    Merge,
    Squash,
    Rebase
}

public enum MergeableState
{
    Dirty,
    Unknown,
    Blocked,
    Behind,
    Unstable,
    HasHooks,
    Clean,
    Draft
}

public enum AuthorAssociation
{
    Collaborator,
    Contributor,
    FirstTimer,
    FirstTimeContributor,
    Member,
    Owner,
    None
}

public enum PullRequestReviewState
{
    Approved,
    ChangesRequested,
    Commented,
    Dismissed,
    Pending
}

public enum EventInfoState
{
    AddedToProject,
    Assigned,
    AutomaticBaseChangeFailed,
    AutomaticBaseChangeSucceeded,
    BaseRefChanged,
    Closed,
    Commented,
    Committed,
    Connected,
    ConvertToDraft,
    ConvertedNoteToIssue,
    Crossreferenced,
    Demilestoned,
    Deployed,
    DeploymentEnvironmentChanged,
    Disconnected,
    HeadRefDeleted,
    HeadRefRestored,
    HeadRefForcePushed,
    Labeled,
    Locked,
    Mentioned,
    MarkedAsDuplicate,
    Merged,
    Milestoned,
    MovedColumnsInProject,
    Pinned,
    ReadyForReview,
    Referenced,
    RemovedFromProject,
    Renamed,
    Reopened,
    ReviewDismissed,
    ReviewRequested,
    ReviewRequestRemoved,
    Reviewed,
    Subscribed,
    Transferred,
    Unassigned,
    Unlabeled,
    Unlocked,
    UnmarkedAsDuplicate,
    Unpinned,
    Unsubscribed,
    UserBlocked,
    LineCommented,
    CommitCommented,
    CommentDeleted
}

public enum CheckStatus
{
    Queued,
    InProgress,
    Completed
}

public enum CheckConclusion
{
    Success,
    Failure,
    Neutral,
    Cancelled,
    TimedOut,
    ActionRequired,
    Skipped,
    Stale
}

public enum CheckStatusFilter
{
    Queued,
    InProgress,
    Completed
}

public enum CheckRunCompletedAtFilter
{
    Latest,
    All
}

public enum ReactionType
{
    Plus1,
    Minus1,
    Laugh,
    Confused,
    Heart,
    Hooray,
    Rocket,
    Eyes
}

public enum RepositoryVisibility
{
    Public,
    Private,
    Internal
}

public enum EncodingType
{
    Utf8,
    Base64
}

public enum StarredSort
{
    Created,
    Updated
}

public sealed class Team
{
}

public sealed class CollaboratorPermissions
{
}

public sealed class ActivityPayload
{
}

public sealed class RenameInfo
{
    public RenameInfo()
    {
        From = string.Empty;
        To = string.Empty;
    }

    public RenameInfo(string from, string to)
    {
        From = from;
        To = to;
    }

    public string From { get; set; }

    public string To { get; set; }
}
