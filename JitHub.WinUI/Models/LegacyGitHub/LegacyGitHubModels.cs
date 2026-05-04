using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace JitHub.Models.LegacyGitHub;

internal static class CompatValueReader
{
    public static string StringAt(object?[] values, int index)
    {
        return index < values.Length
            ? values[index]?.ToString() ?? string.Empty
            : string.Empty;
    }

    public static bool BoolAt(object?[] values, int index)
    {
        if (index >= values.Length || values[index] is null)
        {
            return false;
        }

        return values[index] switch
        {
            bool value => value,
            _ => bool.TryParse(values[index]!.ToString(), out bool parsed) && parsed
        };
    }

    public static int IntAt(object?[] values, int index)
    {
        if (index >= values.Length || values[index] is null)
        {
            return 0;
        }

        return values[index] switch
        {
            int value => value,
            long value => (int)value,
            _ => int.TryParse(values[index]!.ToString(), out int parsed) ? parsed : 0
        };
    }

    public static long LongAt(object?[] values, int index)
    {
        if (index >= values.Length || values[index] is null)
        {
            return 0;
        }

        return values[index] switch
        {
            long value => value,
            int value => value,
            _ => long.TryParse(values[index]!.ToString(), out long parsed) ? parsed : 0
        };
    }

    public static DateTimeOffset DateTimeOffsetAt(object?[] values, int index)
    {
        return NullableDateTimeOffsetAt(values, index) ?? DateTimeOffset.MinValue;
    }

    public static DateTimeOffset? NullableDateTimeOffsetAt(object?[] values, int index)
    {
        if (index >= values.Length || values[index] is null)
        {
            return null;
        }

        return values[index] switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(value),
            _ => DateTimeOffset.TryParse(values[index]!.ToString(), out DateTimeOffset parsed) ? parsed : null
        };
    }

    public static T? ObjectAt<T>(object?[] values, int index)
        where T : class
    {
        return index < values.Length ? values[index] as T : null;
    }

    public static IReadOnlyList<T> ListAt<T>(object?[] values, int index)
    {
        if (index >= values.Length || values[index] is null)
        {
            return Array.Empty<T>();
        }

        if (values[index] is IReadOnlyList<T> typedReadOnlyList)
        {
            return typedReadOnlyList;
        }

        if (values[index] is ICollection<T> typedCollection)
        {
            return typedCollection.ToList();
        }

        if (values[index] is IEnumerable<T> typedEnumerable)
        {
            return typedEnumerable.ToList();
        }

        return Array.Empty<T>();
    }

    public static StringEnum<TEnum> StringEnumAt<TEnum>(object?[] values, int index)
        where TEnum : struct, Enum
    {
        if (index >= values.Length || values[index] is null)
        {
            return new StringEnum<TEnum>();
        }

        return values[index] switch
        {
            StringEnum<TEnum> value => value,
            TEnum value => new StringEnum<TEnum>(value),
            _ => new StringEnum<TEnum>(values[index]!.ToString())
        };
    }

    public static TEnum EnumAt<TEnum>(object?[] values, int index)
        where TEnum : struct, Enum
    {
        return StringEnumAt<TEnum>(values, index).Value;
    }
}

public class ApiException : Exception
{
    public ApiException()
    {
    }

    public ApiException(string? message)
        : base(message)
    {
    }

    public ApiException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public HttpStatusCode StatusCode { get; init; }
}

public class AuthorizationException : ApiException
{
    public AuthorizationException()
    {
    }

    public AuthorizationException(string? message)
        : base(message)
    {
    }

    public AuthorizationException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RateLimitExceededException : AuthorizationException
{
    public RateLimitExceededException()
    {
    }

    public RateLimitExceededException(string? message)
        : base(message)
    {
    }

    public RateLimitExceededException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class NotFoundException : ApiException
{
    public NotFoundException()
    {
    }

    public NotFoundException(string? message)
        : base(message)
    {
    }
}

public sealed class AbuseException : ApiException
{
    public AbuseException()
    {
    }

    public AbuseException(string? message)
        : base(message)
    {
    }
}

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

    public string StringValue { get; set; } = string.Empty;

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
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;
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
    }

    public RenameInfo(string from, string to)
    {
        From = from;
        To = to;
    }

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;
}

public class Account
{
    public Account()
    {
    }
}

public class User : Account
{
    public User()
    : this([])
{
}

    public User(params object?[] values)
    {
        AvatarUrl = CompatValueReader.StringAt(values, 0);
        Bio = CompatValueReader.StringAt(values, 1);
        Blog = CompatValueReader.StringAt(values, 2);
        Collaborators = CompatValueReader.IntAt(values, 3);
        Company = CompatValueReader.StringAt(values, 4);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 5);
        UpdatedAt = CompatValueReader.DateTimeOffsetAt(values, 6);
        DiskUsage = CompatValueReader.IntAt(values, 7);
        Email = CompatValueReader.StringAt(values, 8);
        Followers = CompatValueReader.IntAt(values, 9);
        Following = CompatValueReader.IntAt(values, 10);
        GravatarId = CompatValueReader.StringAt(values, 11);
        HtmlUrl = CompatValueReader.StringAt(values, 12);
        OwnedPrivateRepos = CompatValueReader.IntAt(values, 13);
        Id = CompatValueReader.LongAt(values, 14);
        Location = CompatValueReader.StringAt(values, 15);
        Login = CompatValueReader.StringAt(values, 16);
        Name = CompatValueReader.StringAt(values, 17);
        NodeId = CompatValueReader.StringAt(values, 18);
        PrivateGists = CompatValueReader.IntAt(values, 19);
        Plan = CompatValueReader.ObjectAt<object>(values, 20);
        PrivateRepos = CompatValueReader.IntAt(values, 21);
        PublicGists = CompatValueReader.IntAt(values, 22);
        PublicRepos = CompatValueReader.IntAt(values, 23);
        ReceivedEventsUrl = CompatValueReader.StringAt(values, 24);
        Permissions = CompatValueReader.ObjectAt<object>(values, 25);
        SiteAdmin = CompatValueReader.BoolAt(values, 26);
        StarredUrl = CompatValueReader.StringAt(values, 27);
        SuspendedAt = CompatValueReader.ObjectAt<object>(values, 28);
    }

    public string AvatarUrl { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string Blog { get; set; } = string.Empty;
    public int Collaborators { get; set; }
    public string Company { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int DiskUsage { get; set; }
    public string Email { get; set; } = string.Empty;
    public int Followers { get; set; }
    public int Following { get; set; }
    public string GravatarId { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public int OwnedPrivateRepos { get; set; }
    public long Id { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public int PrivateGists { get; set; }
    public object? Plan { get; set; }
    public int PrivateRepos { get; set; }
    public int PublicGists { get; set; }
    public int PublicRepos { get; set; }
    public string ReceivedEventsUrl { get; set; } = string.Empty;
    public object? Permissions { get; set; }
    public bool SiteAdmin { get; set; }
    public string StarredUrl { get; set; } = string.Empty;
    public object? SuspendedAt { get; set; }
}

public class Collaborator : User
{
    public Collaborator()
    : this([])
{
}

    public Collaborator(params object?[] values)
        : base(
            CompatValueReader.StringAt(values, 5),
            string.Empty,
            string.Empty,
            0,
            string.Empty,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            0,
            string.Empty,
            0,
            0,
            string.Empty,
            CompatValueReader.StringAt(values, 8),
            0,
            CompatValueReader.LongAt(values, 1),
            string.Empty,
            CompatValueReader.StringAt(values, 0),
            CompatValueReader.StringAt(values, 0),
            string.Empty,
            0,
            null,
            0,
            0,
            0,
            string.Empty,
            null,
            false,
            string.Empty,
            null)
    {
        Permissions = CompatValueReader.ObjectAt<CollaboratorPermissions>(values, 19);
        Type = CompatValueReader.StringAt(values, 20);
    }

    public new CollaboratorPermissions? Permissions { get; set; }
    public string Type { get; set; } = string.Empty;
}

public class RepositoryContributor : User
{
    public RepositoryContributor()
    : this([])
{
}

    public RepositoryContributor(params object?[] values)
        : base(values)
    {
    }
}

public class Reaction
{
    public Reaction()
    : this([])
{
}

    public Reaction(params object?[] values)
    {
        Id = CompatValueReader.LongAt(values, 0);
        Url = CompatValueReader.StringAt(values, 1);
        User = CompatValueReader.ObjectAt<User>(values, 2) ?? new User();
        Content = CompatValueReader.StringEnumAt<ReactionType>(values, 3);
    }

    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public User User { get; set; } = new();
    public StringEnum<ReactionType> Content { get; set; } = new();
}

public class ReactionSummary
{
    public ReactionSummary()
    : this([])
{
}

    public ReactionSummary(params object?[] values)
    {
        TotalCount = CompatValueReader.IntAt(values, 0);
        Plus1 = CompatValueReader.IntAt(values, 1);
        Minus1 = CompatValueReader.IntAt(values, 2);
        Laugh = CompatValueReader.IntAt(values, 3);
        Confused = CompatValueReader.IntAt(values, 4);
        Heart = CompatValueReader.IntAt(values, 5);
        Hooray = CompatValueReader.IntAt(values, 6);
        Eyes = CompatValueReader.IntAt(values, 7);
        Rocket = CompatValueReader.IntAt(values, 8);
        Url = CompatValueReader.StringAt(values, 9);
    }

    public int TotalCount { get; set; }
    public int Plus1 { get; set; }
    public int Minus1 { get; set; }
    public int Laugh { get; set; }
    public int Confused { get; set; }
    public int Heart { get; set; }
    public int Hooray { get; set; }
    public int Eyes { get; set; }
    public int Rocket { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class Label
{
    public Label()
    : this([])
{
}

    public Label(params object?[] values)
    {
        Id = CompatValueReader.LongAt(values, 0);
        Url = CompatValueReader.StringAt(values, 1);
        Name = CompatValueReader.StringAt(values, 2);
        NodeId = CompatValueReader.StringAt(values, 3);
        Color = CompatValueReader.StringAt(values, 4);
        Description = CompatValueReader.StringAt(values, 5);
        Default = CompatValueReader.BoolAt(values, 6);
    }

    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Default { get; set; }
}

public class Milestone
{
    public Milestone()
    : this([])
{
}

    public Milestone(params object?[] values)
    {
        Url = CompatValueReader.StringAt(values, 0);
        HtmlUrl = CompatValueReader.StringAt(values, 1);
        Id = CompatValueReader.LongAt(values, 2);
        Number = CompatValueReader.IntAt(values, 3);
        NodeId = CompatValueReader.StringAt(values, 4);
        State = CompatValueReader.EnumAt<ItemState>(values, 5);
        Title = CompatValueReader.StringAt(values, 6);
        Description = CompatValueReader.StringAt(values, 7);
        Creator = CompatValueReader.ObjectAt<User>(values, 8) ?? new User();
        OpenIssues = CompatValueReader.IntAt(values, 9);
        ClosedIssues = CompatValueReader.IntAt(values, 10);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 11);
        DueOn = CompatValueReader.NullableDateTimeOffsetAt(values, 12);
        ClosedAt = CompatValueReader.NullableDateTimeOffsetAt(values, 13);
        UpdatedAt = CompatValueReader.NullableDateTimeOffsetAt(values, 14);
    }

    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public long Id { get; set; }
    public int Number { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public StringEnum<ItemState> State { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public User Creator { get; set; } = new();
    public int OpenIssues { get; set; }
    public int ClosedIssues { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DueOn { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class GitReference
{
    public GitReference()
    : this([])
{
}

    public GitReference(params object?[] values)
    {
        Url = CompatValueReader.StringAt(values, 0);
        NodeId = CompatValueReader.StringAt(values, 1);
        Label = CompatValueReader.StringAt(values, 2);
        Ref = CompatValueReader.StringAt(values, 3);
        Sha = CompatValueReader.StringAt(values, 4);
        User = CompatValueReader.ObjectAt<User>(values, 5) ?? new User();
        Repository = CompatValueReader.ObjectAt<Repository>(values, 6) ?? new Repository();
    }

    public string Url { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Ref { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public User User { get; set; } = new();
    public Repository Repository { get; set; } = new();
}

public class Branch
{
    public Branch()
    : this([])
{
}

    public Branch(params object?[] values)
    {
        Name = CompatValueReader.StringAt(values, 0);
        Commit = CompatValueReader.ObjectAt<GitReference>(values, 1) ?? new GitReference();
        Protected = CompatValueReader.BoolAt(values, 2);
    }

    public string Name { get; set; } = string.Empty;
    public GitReference Commit { get; set; } = new();
    public bool Protected { get; set; }
}

public class Blob
{
    public Blob()
    : this([])
{
}

    public Blob(params object?[] values)
    {
        NodeId = CompatValueReader.StringAt(values, 0);
        Content = CompatValueReader.StringAt(values, 1);
        Encoding = CompatValueReader.StringEnumAt<EncodingType>(values, 2);
        Sha = CompatValueReader.StringAt(values, 3);
        Size = CompatValueReader.IntAt(values, 4);
    }

    public string NodeId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public StringEnum<EncodingType> Encoding { get; set; } = new();
    public string Sha { get; set; } = string.Empty;
    public int Size { get; set; }
}

public class Repository
{
    public Repository()
    : this([])
{
}

    public Repository(params object?[] values)
    {
        Url = CompatValueReader.StringAt(values, 0);
        HtmlUrl = CompatValueReader.StringAt(values, 1);
        CloneUrl = CompatValueReader.StringAt(values, 2);
        GitUrl = CompatValueReader.StringAt(values, 3);
        SshUrl = CompatValueReader.StringAt(values, 4);
        SvnUrl = CompatValueReader.StringAt(values, 5);
        MirrorUrl = CompatValueReader.StringAt(values, 6);
        Homepage = CompatValueReader.StringAt(values, 7);
        Id = CompatValueReader.LongAt(values, 8);
        NodeId = CompatValueReader.StringAt(values, 9);
        Owner = CompatValueReader.ObjectAt<User>(values, 10) ?? new User();
        Name = CompatValueReader.StringAt(values, 11);
        FullName = CompatValueReader.StringAt(values, 12);
        Archived = CompatValueReader.BoolAt(values, 13);
        Description = CompatValueReader.StringAt(values, 14);
        Language = CompatValueReader.StringAt(values, 16);
        Private = CompatValueReader.BoolAt(values, 17);
        Fork = CompatValueReader.BoolAt(values, 18);
        ForksCount = CompatValueReader.IntAt(values, 19);
        StargazersCount = CompatValueReader.IntAt(values, 20);
        DefaultBranch = CompatValueReader.StringAt(values, 21);
        OpenIssuesCount = CompatValueReader.IntAt(values, 22);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 24);
        UpdatedAt = CompatValueReader.DateTimeOffsetAt(values, 25);
        WatchersCount = CompatValueReader.IntAt(values, 35);
        SubscribersCount = CompatValueReader.IntAt(values, 41);
        Visibility = CompatValueReader.EnumAt<RepositoryVisibility>(values, 43);
        Topics = CompatValueReader.ListAt<string>(values, 44);
    }

    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string CloneUrl { get; set; } = string.Empty;
    public string GitUrl { get; set; } = string.Empty;
    public string SshUrl { get; set; } = string.Empty;
    public string SvnUrl { get; set; } = string.Empty;
    public string MirrorUrl { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public User Owner { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public bool Archived { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public bool Private { get; set; }
    public bool Fork { get; set; }
    public int ForksCount { get; set; }
    public int StargazersCount { get; set; }
    public string DefaultBranch { get; set; } = string.Empty;
    public int OpenIssuesCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int WatchersCount { get; set; }
    public int SubscribersCount { get; set; }
    public RepositoryVisibility Visibility { get; set; }
    public IReadOnlyList<string> Topics { get; set; } = Array.Empty<string>();
}

public class PullRequest
{
    public PullRequest()
    : this([])
{
}

    public PullRequest(params object?[] values)
    {
        if (values.Length == 1 && values[0] is int number)
        {
            Number = number;
            return;
        }

        Id = CompatValueReader.LongAt(values, 0);
        Url = CompatValueReader.StringAt(values, 1);
        HtmlUrl = CompatValueReader.StringAt(values, 2);
        DiffUrl = CompatValueReader.StringAt(values, 3);
        PatchUrl = CompatValueReader.StringAt(values, 4);
        IssueUrl = CompatValueReader.StringAt(values, 5);
        CommitsUrl = CompatValueReader.StringAt(values, 6);
        ReviewCommentsUrl = CompatValueReader.StringAt(values, 7);
        Number = CompatValueReader.IntAt(values, 8);
        State = CompatValueReader.EnumAt<ItemState>(values, 9);
        Title = CompatValueReader.StringAt(values, 10);
        Body = CompatValueReader.StringAt(values, 11);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 12);
        UpdatedAt = CompatValueReader.DateTimeOffsetAt(values, 13);
        ClosedAt = CompatValueReader.NullableDateTimeOffsetAt(values, 14);
        MergedAt = CompatValueReader.NullableDateTimeOffsetAt(values, 15);
        Head = CompatValueReader.ObjectAt<GitReference>(values, 16) ?? new GitReference();
        Base = CompatValueReader.ObjectAt<GitReference>(values, 17) ?? new GitReference();
        User = CompatValueReader.ObjectAt<User>(values, 18) ?? new User();
        Assignee = CompatValueReader.ObjectAt<User>(values, 19);
        Assignees = CompatValueReader.ListAt<User>(values, 20);
        Draft = CompatValueReader.BoolAt(values, 21);
        Mergeable = values.Length > 22 && values[22] is bool mergeable ? mergeable : null;
        MergeableState = CompatValueReader.StringEnumAt<MergeableState>(values, 23);
        MergedBy = CompatValueReader.ObjectAt<User>(values, 24);
        MergeCommitSha = CompatValueReader.StringAt(values, 25);
        Comments = CompatValueReader.IntAt(values, 26);
        Commits = CompatValueReader.IntAt(values, 27);
        Additions = CompatValueReader.IntAt(values, 28);
        Deletions = CompatValueReader.IntAt(values, 29);
        ChangedFiles = CompatValueReader.IntAt(values, 30);
        Milestone = CompatValueReader.ObjectAt<Milestone>(values, 31);
        Locked = CompatValueReader.BoolAt(values, 32);
        ClosedBy = CompatValueReader.ObjectAt<User>(values, 33);
        RequestedReviewers = CompatValueReader.ListAt<User>(values, 34);
        RequestedTeams = CompatValueReader.ListAt<Team>(values, 35);
        Labels = CompatValueReader.ListAt<Label>(values, 36);
        Reactions = CompatValueReader.ObjectAt<ReactionSummary>(values, 37) ?? new ReactionSummary();
    }

    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string DiffUrl { get; set; } = string.Empty;
    public string PatchUrl { get; set; } = string.Empty;
    public string IssueUrl { get; set; } = string.Empty;
    public string CommitsUrl { get; set; } = string.Empty;
    public string ReviewCommentsUrl { get; set; } = string.Empty;
    public int Number { get; set; }
    public StringEnum<ItemState> State { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public GitReference Head { get; set; } = new();
    public GitReference Base { get; set; } = new();
    public User User { get; set; } = new();
    public User? Assignee { get; set; }
    public IReadOnlyList<User> Assignees { get; set; } = Array.Empty<User>();
    public bool Draft { get; set; }
    public bool? Mergeable { get; set; }
    public StringEnum<MergeableState> MergeableState { get; set; } = new();
    public User? MergedBy { get; set; }
    public string MergeCommitSha { get; set; } = string.Empty;
    public int Comments { get; set; }
    public int Commits { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int ChangedFiles { get; set; }
    public Milestone? Milestone { get; set; }
    public bool Locked { get; set; }
    public User? ClosedBy { get; set; }
    public IReadOnlyList<User> RequestedReviewers { get; set; } = Array.Empty<User>();
    public IReadOnlyList<Team> RequestedTeams { get; set; } = Array.Empty<Team>();
    public IReadOnlyList<Label> Labels { get; set; } = Array.Empty<Label>();
    public ReactionSummary Reactions { get; set; } = new();
}

public class PullRequestMerge
{
    public PullRequestMerge()
    : this([])
{
}

    public PullRequestMerge(params object?[] values)
    {
        Sha = CompatValueReader.StringAt(values, 0);
        Merged = CompatValueReader.BoolAt(values, 1);
        Message = CompatValueReader.StringAt(values, 2);
    }

    public string Sha { get; set; } = string.Empty;
    public bool Merged { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class Issue
{
    public Issue()
    : this([])
{
}

    public Issue(params object?[] values)
    {
        Url = CompatValueReader.StringAt(values, 0);
        HtmlUrl = CompatValueReader.StringAt(values, 1);
        CommentsUrl = CompatValueReader.StringAt(values, 2);
        EventsUrl = CompatValueReader.StringAt(values, 3);
        Number = CompatValueReader.IntAt(values, 4);
        State = CompatValueReader.EnumAt<ItemState>(values, 5);
        Title = CompatValueReader.StringAt(values, 6);
        Body = CompatValueReader.StringAt(values, 7);
        ClosedBy = CompatValueReader.ObjectAt<User>(values, 8);
        User = CompatValueReader.ObjectAt<User>(values, 9) ?? new User();
        Labels = CompatValueReader.ListAt<Label>(values, 10);
        Assignee = CompatValueReader.ObjectAt<User>(values, 11);
        Assignees = CompatValueReader.ListAt<User>(values, 12);
        Milestone = CompatValueReader.ObjectAt<Milestone>(values, 13);
        Comments = CompatValueReader.IntAt(values, 14);
        PullRequest = CompatValueReader.ObjectAt<PullRequest>(values, 15);
        ClosedAt = CompatValueReader.NullableDateTimeOffsetAt(values, 16);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 17);
        UpdatedAt = CompatValueReader.DateTimeOffsetAt(values, 18);
        Id = CompatValueReader.LongAt(values, 19);
        NodeId = CompatValueReader.StringAt(values, 20);
        Locked = CompatValueReader.BoolAt(values, 21);
        Repository = CompatValueReader.ObjectAt<Repository>(values, 22);
        Reactions = CompatValueReader.ObjectAt<ReactionSummary>(values, 23) ?? new ReactionSummary();
        ActiveLockReason = CompatValueReader.StringAt(values, 24);
        AuthorAssociation = CompatValueReader.StringEnumAt<AuthorAssociation>(values, 25);
    }

    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string CommentsUrl { get; set; } = string.Empty;
    public string EventsUrl { get; set; } = string.Empty;
    public int Number { get; set; }
    public StringEnum<ItemState> State { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public User? ClosedBy { get; set; }
    public User User { get; set; } = new();
    public IReadOnlyList<Label> Labels { get; set; } = Array.Empty<Label>();
    public User? Assignee { get; set; }
    public IReadOnlyList<User> Assignees { get; set; } = Array.Empty<User>();
    public Milestone? Milestone { get; set; }
    public int Comments { get; set; }
    public PullRequest? PullRequest { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public bool Locked { get; set; }
    public Repository? Repository { get; set; }
    public ReactionSummary Reactions { get; set; } = new();
    public string ActiveLockReason { get; set; } = string.Empty;
    public StringEnum<AuthorAssociation> AuthorAssociation { get; set; } = new();
}

public class IssueComment
{
    public IssueComment()
    : this([])
{
}

    public IssueComment(params object?[] values)
    {
        Id = CompatValueReader.LongAt(values, 0);
        NodeId = CompatValueReader.StringAt(values, 1);
        Url = CompatValueReader.StringAt(values, 2);
        HtmlUrl = CompatValueReader.StringAt(values, 3);
        Body = CompatValueReader.StringAt(values, 4);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 5);
        UpdatedAt = CompatValueReader.DateTimeOffsetAt(values, 6);
        User = CompatValueReader.ObjectAt<User>(values, 7) ?? new User();
        Reactions = CompatValueReader.ObjectAt<ReactionSummary>(values, 8) ?? new ReactionSummary();
        AuthorAssociation = CompatValueReader.StringEnumAt<AuthorAssociation>(values, 9);
    }

    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public User User { get; set; } = new();
    public ReactionSummary Reactions { get; set; } = new();
    public StringEnum<AuthorAssociation> AuthorAssociation { get; set; } = new();
}

public class PullRequestReviewComment
{
    public PullRequestReviewComment()
    : this([])
{
}

    public PullRequestReviewComment(params object?[] values)
    {
        Url = CompatValueReader.StringAt(values, 0);
        Id = CompatValueReader.LongAt(values, 1);
        NodeId = CompatValueReader.StringAt(values, 2);
        DiffHunk = CompatValueReader.StringAt(values, 3);
        Path = CompatValueReader.StringAt(values, 4);
        Position = values.Length > 5 && values[5] is int position ? position : null;
        OriginalPosition = values.Length > 6 && values[6] is int originalPosition ? originalPosition : null;
        CommitId = CompatValueReader.StringAt(values, 7);
        OriginalCommitId = CompatValueReader.StringAt(values, 8);
        User = CompatValueReader.ObjectAt<User>(values, 9) ?? new User();
        Body = CompatValueReader.StringAt(values, 10);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 11);
        UpdatedAt = CompatValueReader.DateTimeOffsetAt(values, 12);
        HtmlUrl = CompatValueReader.StringAt(values, 13);
        PullRequestUrl = CompatValueReader.StringAt(values, 14);
        Reactions = CompatValueReader.ObjectAt<ReactionSummary>(values, 15) ?? new ReactionSummary();
        InReplyToId = values.Length > 16 && values[16] is long inReplyToId ? inReplyToId : null;
        PullRequestReviewId = values.Length > 17 && values[17] is long reviewId ? reviewId : null;
        AuthorAssociation = CompatValueReader.StringEnumAt<AuthorAssociation>(values, 18);
    }

    public string Url { get; set; } = string.Empty;
    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string DiffHunk { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int? Position { get; set; }
    public int? OriginalPosition { get; set; }
    public string CommitId { get; set; } = string.Empty;
    public string OriginalCommitId { get; set; } = string.Empty;
    public User User { get; set; } = new();
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    public string PullRequestUrl { get; set; } = string.Empty;
    public ReactionSummary Reactions { get; set; } = new();
    public long? InReplyToId { get; set; }
    public long? PullRequestReviewId { get; set; }
    public StringEnum<AuthorAssociation> AuthorAssociation { get; set; } = new();
}

public class PullRequestReview
{
    public PullRequestReview()
    : this([])
{
}

    public PullRequestReview(params object?[] values)
    {
        Id = CompatValueReader.LongAt(values, 0);
        NodeId = CompatValueReader.StringAt(values, 1);
        CommitId = CompatValueReader.StringAt(values, 2);
        User = CompatValueReader.ObjectAt<User>(values, 3) ?? new User();
        Body = CompatValueReader.StringAt(values, 4);
        HtmlUrl = CompatValueReader.StringAt(values, 5);
        PullRequestUrl = CompatValueReader.StringAt(values, 6);
        State = CompatValueReader.StringEnumAt<PullRequestReviewState>(values, 7);
        AuthorAssociation = CompatValueReader.StringEnumAt<AuthorAssociation>(values, 8);
        SubmittedAt = CompatValueReader.DateTimeOffsetAt(values, 9);
    }

    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string CommitId { get; set; } = string.Empty;
    public User User { get; set; } = new();
    public string Body { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string PullRequestUrl { get; set; } = string.Empty;
    public StringEnum<PullRequestReviewState> State { get; set; } = new();
    public StringEnum<AuthorAssociation> AuthorAssociation { get; set; } = new();
    public DateTimeOffset SubmittedAt { get; set; }
}

public class Author
{
    public Author()
    : this([])
{
}

    public Author(params object?[] values)
    {
        Login = CompatValueReader.StringAt(values, 0);
        Id = CompatValueReader.LongAt(values, 1);
        NodeId = CompatValueReader.StringAt(values, 2);
        AvatarUrl = CompatValueReader.StringAt(values, 3);
        GravatarId = CompatValueReader.StringAt(values, 4);
        HtmlUrl = CompatValueReader.StringAt(values, 5);
        Url = CompatValueReader.StringAt(values, 6);
        FollowersUrl = CompatValueReader.StringAt(values, 7);
        FollowingUrl = CompatValueReader.StringAt(values, 8);
        GistsUrl = CompatValueReader.StringAt(values, 9);
        StarredUrl = CompatValueReader.StringAt(values, 10);
        SubscriptionsUrl = CompatValueReader.StringAt(values, 11);
        OrganizationsUrl = CompatValueReader.StringAt(values, 12);
        ReposUrl = CompatValueReader.StringAt(values, 13);
        EventsUrl = CompatValueReader.StringAt(values, 14);
        ReceivedEventsUrl = CompatValueReader.StringAt(values, 15);
        SiteAdmin = CompatValueReader.BoolAt(values, 16);
    }

    public string Login { get; set; } = string.Empty;
    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string GravatarId { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string FollowersUrl { get; set; } = string.Empty;
    public string FollowingUrl { get; set; } = string.Empty;
    public string GistsUrl { get; set; } = string.Empty;
    public string StarredUrl { get; set; } = string.Empty;
    public string SubscriptionsUrl { get; set; } = string.Empty;
    public string OrganizationsUrl { get; set; } = string.Empty;
    public string ReposUrl { get; set; } = string.Empty;
    public string EventsUrl { get; set; } = string.Empty;
    public string ReceivedEventsUrl { get; set; } = string.Empty;
    public bool SiteAdmin { get; set; }
}

public class Committer
{
    public Committer()
    : this([])
{
}

    public Committer(params object?[] values)
    {
        Name = CompatValueReader.StringAt(values, 0);
        Email = CompatValueReader.StringAt(values, 1);
        Date = CompatValueReader.DateTimeOffsetAt(values, 2);
    }

    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
}

public class Commit
{
    public Commit()
    : this([])
{
}

    public Commit(params object?[] values)
    {
        NodeId = CompatValueReader.StringAt(values, 0);
        Url = CompatValueReader.StringAt(values, 1);
        HtmlUrl = CompatValueReader.StringAt(values, 2);
        CommentsUrl = CompatValueReader.StringAt(values, 3);
        Sha = CompatValueReader.StringAt(values, 4);
        AuthorUser = CompatValueReader.ObjectAt<User>(values, 5) ?? new User();
        CommitterUser = CompatValueReader.ObjectAt<User>(values, 6);
        Message = CompatValueReader.StringAt(values, 7);
        Author = CompatValueReader.ObjectAt<Committer>(values, 8) ?? new Committer();
        Committer = CompatValueReader.ObjectAt<Committer>(values, 9) ?? new Committer();
        Tree = CompatValueReader.ObjectAt<GitReference>(values, 10) ?? new GitReference();
        Parents = CompatValueReader.ListAt<GitReference>(values, 11);
        CommentCount = CompatValueReader.IntAt(values, 12);
        Verification = CompatValueReader.ObjectAt<object>(values, 13);
    }

    public string NodeId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string CommentsUrl { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public User AuthorUser { get; set; } = new();
    public User? CommitterUser { get; set; }
    public string Message { get; set; } = string.Empty;
    public Committer Author { get; set; } = new();
    public Committer Committer { get; set; } = new();
    public GitReference Tree { get; set; } = new();
    public IReadOnlyList<GitReference> Parents { get; set; } = Array.Empty<GitReference>();
    public int CommentCount { get; set; }
    public object? Verification { get; set; }
}

public class GitHubCommitFile
{
    public GitHubCommitFile()
    : this([])
{
}

    public GitHubCommitFile(params object?[] values)
    {
        Filename = CompatValueReader.StringAt(values, 0);
        Additions = CompatValueReader.IntAt(values, 1);
        Deletions = CompatValueReader.IntAt(values, 2);
        Changes = CompatValueReader.IntAt(values, 3);
        Status = CompatValueReader.StringAt(values, 4);
        BlobUrl = CompatValueReader.StringAt(values, 5);
        ContentsUrl = CompatValueReader.StringAt(values, 6);
        RawUrl = CompatValueReader.StringAt(values, 7);
        Sha = CompatValueReader.StringAt(values, 8);
        Patch = CompatValueReader.StringAt(values, 9);
        PreviousFilename = CompatValueReader.StringAt(values, 10);
    }

    public string Filename { get; set; } = string.Empty;
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int Changes { get; set; }
    public string Status { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;
    public string ContentsUrl { get; set; } = string.Empty;
    public string RawUrl { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public string Patch { get; set; } = string.Empty;
    public string PreviousFilename { get; set; } = string.Empty;
}

public class GitHubCommitStats
{
    public GitHubCommitStats()
    : this([])
{
}

    public GitHubCommitStats(params object?[] values)
    {
        Additions = CompatValueReader.IntAt(values, 0);
        Deletions = CompatValueReader.IntAt(values, 1);
        Total = CompatValueReader.IntAt(values, 2);
    }

    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int Total { get; set; }
}

public class GitHubCommit
{
    public GitHubCommit()
    : this([])
{
}

    public GitHubCommit(params object?[] values)
    {
        NodeId = CompatValueReader.StringAt(values, 0);
        Url = CompatValueReader.StringAt(values, 1);
        HtmlUrl = CompatValueReader.StringAt(values, 2);
        CommentsUrl = CompatValueReader.StringAt(values, 3);
        Sha = CompatValueReader.StringAt(values, 4);
        Author = CompatValueReader.ObjectAt<Author>(values, 5) ?? new Author();
        CommitterUser = CompatValueReader.ObjectAt<User>(values, 6);
        Committer = CompatValueReader.ObjectAt<Author>(values, 7);
        Node = CompatValueReader.StringAt(values, 8);
        Commit = CompatValueReader.ObjectAt<Commit>(values, 9) ?? new Commit();
        CommitAuthor = CompatValueReader.ObjectAt<Author>(values, 10);
        CommitUrl = CompatValueReader.StringAt(values, 11);
        Stats = CompatValueReader.ObjectAt<GitHubCommitStats>(values, 12);
        Parents = CompatValueReader.ListAt<GitReference>(values, 13);
        Files = CompatValueReader.ListAt<GitHubCommitFile>(values, 14);
    }

    public string NodeId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string CommentsUrl { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public Author? Author { get; set; }
    public User? CommitterUser { get; set; }
    public Author? Committer { get; set; }
    public string Node { get; set; } = string.Empty;
    public Commit Commit { get; set; } = new();
    public Author? CommitAuthor { get; set; }
    public string CommitUrl { get; set; } = string.Empty;
    public GitHubCommitStats? Stats { get; set; }
    public IReadOnlyList<GitReference> Parents { get; set; } = Array.Empty<GitReference>();
    public IReadOnlyList<GitHubCommitFile> Files { get; set; } = Array.Empty<GitHubCommitFile>();
}

public class PullRequestCommit
{
    public PullRequestCommit()
    : this([])
{
}

    public PullRequestCommit(params object?[] values)
    {
        NodeId = CompatValueReader.StringAt(values, 0);
        Author = CompatValueReader.ObjectAt<User>(values, 1) ?? new User();
        Url = CompatValueReader.StringAt(values, 2);
        Commit = CompatValueReader.ObjectAt<Commit>(values, 3) ?? new Commit();
        Committer = CompatValueReader.ObjectAt<User>(values, 4) ?? new User();
        HtmlUrl = CompatValueReader.StringAt(values, 5);
        Parents = CompatValueReader.ListAt<GitReference>(values, 6);
        Sha = CompatValueReader.StringAt(values, 7);
        CommitUrl = CompatValueReader.StringAt(values, 8);
    }

    public string NodeId { get; set; } = string.Empty;
    public User Author { get; set; } = new();
    public string Url { get; set; } = string.Empty;
    public Commit Commit { get; set; } = new();
    public User Committer { get; set; } = new();
    public string HtmlUrl { get; set; } = string.Empty;
    public IReadOnlyList<GitReference> Parents { get; set; } = Array.Empty<GitReference>();
    public string Sha { get; set; } = string.Empty;
    public string CommitUrl { get; set; } = string.Empty;
}

public class CommitComment
{
    public CommitComment()
    : this([])
{
}

    public CommitComment(params object?[] values)
    {
        Id = CompatValueReader.LongAt(values, 0);
        NodeId = CompatValueReader.StringAt(values, 1);
        Url = CompatValueReader.StringAt(values, 2);
        HtmlUrl = CompatValueReader.StringAt(values, 3);
        Body = CompatValueReader.StringAt(values, 4);
        Path = CompatValueReader.StringAt(values, 5);
        Position = CompatValueReader.IntAt(values, 6);
        Line = values.Length > 7 && values[7] is int line ? line : null;
        CommitId = CompatValueReader.StringAt(values, 8);
        User = CompatValueReader.ObjectAt<User>(values, 9) ?? new User();
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 10);
        UpdatedAt = CompatValueReader.DateTimeOffsetAt(values, 11);
        Reactions = CompatValueReader.ObjectAt<ReactionSummary>(values, 12) ?? new ReactionSummary();
    }

    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Position { get; set; }
    public int? Line { get; set; }
    public string CommitId { get; set; } = string.Empty;
    public User User { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ReactionSummary Reactions { get; set; } = new();
}

public class CompareResult
{
    public CompareResult()
    : this([])
{
}

    public CompareResult(params object?[] values)
    {
        Url = CompatValueReader.StringAt(values, 0);
        HtmlUrl = CompatValueReader.StringAt(values, 1);
        PermalinkUrl = CompatValueReader.StringAt(values, 2);
        DiffUrl = CompatValueReader.StringAt(values, 3);
        PatchUrl = CompatValueReader.StringAt(values, 4);
        BaseCommit = CompatValueReader.ObjectAt<GitHubCommit>(values, 5);
        MergeBaseCommit = CompatValueReader.ObjectAt<GitHubCommit>(values, 6);
        Status = CompatValueReader.StringAt(values, 7);
        AheadBy = CompatValueReader.IntAt(values, 8);
        BehindBy = CompatValueReader.IntAt(values, 9);
        TotalCommits = CompatValueReader.IntAt(values, 10);
        Commits = CompatValueReader.ListAt<GitHubCommit>(values, 11);
        Files = CompatValueReader.ListAt<GitHubCommitFile>(values, 12);
    }

    public string Url { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public string PermalinkUrl { get; set; } = string.Empty;
    public string DiffUrl { get; set; } = string.Empty;
    public string PatchUrl { get; set; } = string.Empty;
    public GitHubCommit? BaseCommit { get; set; }
    public GitHubCommit? MergeBaseCommit { get; set; }
    public string Status { get; set; } = string.Empty;
    public int AheadBy { get; set; }
    public int BehindBy { get; set; }
    public int TotalCommits { get; set; }
    public IReadOnlyList<GitHubCommit> Commits { get; set; } = Array.Empty<GitHubCommit>();
    public IReadOnlyList<GitHubCommitFile> Files { get; set; } = Array.Empty<GitHubCommitFile>();
}

public class Subscription
{
    public Subscription()
    {
    }

    public Subscription(bool subscribed, bool ignored, string? reason, DateTimeOffset createdAt, string? url, string? repositoryUrl)
    {
        Subscribed = subscribed;
        Ignored = ignored;
        Reason = reason ?? string.Empty;
        CreatedAt = createdAt;
        Url = url ?? string.Empty;
        RepositoryUrl = repositoryUrl ?? string.Empty;
    }

    public bool Subscribed { get; set; }
    public bool Ignored { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Url { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
}

public class IssueEvent
{
    public IssueEvent()
    : this([])
{
}

    public IssueEvent(params object?[] values)
    {
        Id = CompatValueReader.LongAt(values, 0);
        NodeId = CompatValueReader.StringAt(values, 1);
        Url = CompatValueReader.StringAt(values, 2);
        Actor = CompatValueReader.ObjectAt<User>(values, 3) ?? new User();
        Assignee = CompatValueReader.ObjectAt<User>(values, 4);
        Label = CompatValueReader.ObjectAt<Label>(values, 5);
        Event = CompatValueReader.StringEnumAt<EventInfoState>(values, 6);
        CommitId = CompatValueReader.StringAt(values, 7);
        CreatedAt = CompatValueReader.DateTimeOffsetAt(values, 8);
        DismissedReview = CompatValueReader.ObjectAt<object>(values, 9);
        NodeUrl = CompatValueReader.StringAt(values, 10);
        Rename = CompatValueReader.ObjectAt<RenameInfo>(values, 11);
        RequestedTeam = CompatValueReader.ObjectAt<Team>(values, 12);
        ReviewRequester = CompatValueReader.ObjectAt<User>(values, 13);
        RequestedReviewer = CompatValueReader.ObjectAt<User>(values, 14);
        Assigner = CompatValueReader.ObjectAt<User>(values, 15);
        LockReason = CompatValueReader.StringAt(values, 16);
        Milestone = CompatValueReader.ObjectAt<Milestone>(values, 17);
        ProjectCard = CompatValueReader.ObjectAt<object>(values, 18);
    }

    public long Id { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public User Actor { get; set; } = new();
    public User? Assignee { get; set; }
    public Label? Label { get; set; }
    public StringEnum<EventInfoState> Event { get; set; } = new();
    public string CommitId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public object? DismissedReview { get; set; }
    public string NodeUrl { get; set; } = string.Empty;
    public RenameInfo? Rename { get; set; }
    public Team? RequestedTeam { get; set; }
    public User? ReviewRequester { get; set; }
    public User? RequestedReviewer { get; set; }
    public User? Assigner { get; set; }
    public string LockReason { get; set; } = string.Empty;
    public Milestone? Milestone { get; set; }
    public object? ProjectCard { get; set; }
}


