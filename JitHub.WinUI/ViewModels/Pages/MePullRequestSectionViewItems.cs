using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MeLabelViewItem : ObservableObject
{
    public MeLabelViewItem(GitHubLabel label) => Label = label;

    public GitHubLabel Label { get; private set; }

    public string StableKey => GetStableKey(Label);

    public string Name => Label.Name;

    public string? Color => Label.Color;

    public bool Apply(GitHubLabel label)
    {
        if (Label.Id == label.Id &&
            string.Equals(Label.Name, label.Name, StringComparison.Ordinal) &&
            string.Equals(Label.Color, label.Color, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Label.Description, label.Description, StringComparison.Ordinal))
        {
            return false;
        }

        Label = label;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Color));
        return true;
    }

    public static string GetStableKey(GitHubLabel label) => label.Id > 0
        ? label.Id.ToString(CultureInfo.InvariantCulture)
        : label.Name.Trim().ToLowerInvariant();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MeActorViewItem : ObservableObject
{
    public MeActorViewItem(GitHubActor actor) => Actor = actor;

    public GitHubActor Actor { get; private set; }

    public string StableKey => GetStableKey(Actor);

    public string ActorDisplayName => UserIdentityNavigationPolicy.CreatePresentation(
        Actor.Login,
        displayName: null,
        LocalizedResourceText.GetString("Common.UnknownUser", "unknown")).DisplayName;

    public string? AuthenticatedLogin =>
        UserIdentityNavigationPolicy.GetRoutableLogin(Actor.Login);

    public string Login => AuthenticatedLogin ?? string.Empty;

    public string AvatarUrl => Actor.AvatarUrl ?? string.Empty;

    public string AutomationId => $"MeActor_{StableKey}";

    public bool Apply(GitHubActor actor)
    {
        if (Actor.Id == actor.Id &&
            string.Equals(Actor.Login, actor.Login, StringComparison.Ordinal) &&
            string.Equals(Actor.AvatarUrl, actor.AvatarUrl, StringComparison.Ordinal) &&
            string.Equals(Actor.HtmlUrl, actor.HtmlUrl, StringComparison.Ordinal))
        {
            return false;
        }

        Actor = actor;
        OnPropertyChanged(nameof(Actor));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(ActorDisplayName));
        OnPropertyChanged(nameof(AuthenticatedLogin));
        OnPropertyChanged(nameof(Login));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(AutomationId));
        return true;
    }

    public static string GetStableKey(GitHubActor actor) => actor.Id > 0
        ? actor.Id.ToString(CultureInfo.InvariantCulture)
        : actor.Login.Trim().ToLowerInvariant();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MePullRequestCommitViewItem : ObservableObject
{
    public MePullRequestCommitViewItem(GitHubCommit commit) => Commit = commit;

    public GitHubCommit Commit { get; private set; }

    public string StableKey => GetStableKey(Commit);

    public string Summary => Commit.SummaryMessage;

    public string Author => Commit.AuthorDisplayName;

    public string ShortSha => Commit.ShortSha;

    public string Timestamp => Commit.Commit.Author.Date is DateTimeOffset date
        ? MeWorkItemViewItem.FormatTimeAgo(date)
        : string.Empty;

    public string AutomationId => $"MyPullRequestsCommit_{StableKey}";

    public string AutomationName => $"Commit {ShortSha}: {Summary}, by {Author}";

    public bool Apply(GitHubCommit commit)
    {
        if (string.Equals(Commit.Sha, commit.Sha, StringComparison.Ordinal) &&
            string.Equals(Commit.Commit.Message, commit.Commit.Message, StringComparison.Ordinal) &&
            string.Equals(Commit.AuthorDisplayName, commit.AuthorDisplayName, StringComparison.Ordinal) &&
            Commit.Commit.Author.Date == commit.Commit.Author.Date)
        {
            return false;
        }

        Commit = commit;
        OnPropertyChanged(nameof(Commit));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(ShortSha));
        OnPropertyChanged(nameof(Timestamp));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        return true;
    }

    public static string GetStableKey(GitHubCommit commit) => !string.IsNullOrWhiteSpace(commit.Sha)
        ? commit.Sha
        : commit.NodeId ?? commit.HtmlUrl;
}

public sealed record MePullRequestReviewSnapshot(
    string StableKey,
    GitHubPullRequestReview? Review,
    GitHubPullRequestReviewComment[] Comments);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MePullRequestReviewCommentViewItem : ObservableObject
{
    public MePullRequestReviewCommentViewItem(GitHubPullRequestReviewComment comment) => Comment = comment;

    public GitHubPullRequestReviewComment Comment { get; private set; }

    public string StableKey => GetStableKey(Comment);

    public string Author => PullRequestIdentityProjection.Create(
        Comment.User,
        LocalizedResourceText.GetString("Common.UnknownUser", "unknown"),
        AutomationId).DisplayName;

    public string Path => string.IsNullOrWhiteSpace(Comment.Path)
        ? LocalizedResourceText.GetString("MyPullRequests.Review.Comment", "Review comment")
        : Comment.Path!;

    public string Body => Comment.Body;

    public string CreatedText => MeWorkItemViewItem.FormatTimeAgo(Comment.CreatedAt);

    public string AutomationId => $"MyPullRequestsReviewComment_{StableKey}";

    public string AutomationName => $"Review comment by {Author} on {Path}, {CreatedText}";

    public MarkdownDocumentSource? MarkdownSource => Comment.MarkdownSource;

    public bool Apply(GitHubPullRequestReviewComment comment)
    {
        if (Comment.Id == comment.Id &&
            Comment.UpdatedAt == comment.UpdatedAt &&
            string.Equals(Comment.Body, comment.Body, StringComparison.Ordinal) &&
            string.Equals(Comment.Path, comment.Path, StringComparison.Ordinal) &&
            string.Equals(Comment.User?.Login, comment.User?.Login, StringComparison.Ordinal))
        {
            return false;
        }

        Comment = comment;
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(MarkdownSource));
        return true;
    }

    public static string GetStableKey(GitHubPullRequestReviewComment comment) =>
        PullRequestReviewAutomationIdentity.CreateScope(
            "ReviewComment",
            comment.Id,
            comment.NodeId,
            comment.PullRequestReviewId,
            comment.Position,
            comment.OriginalPosition,
            comment.CreatedAt);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MePullRequestReviewViewItem : ObservableObject
{
    public MePullRequestReviewViewItem(MePullRequestReviewSnapshot snapshot)
    {
        Snapshot = snapshot;
        ApplyComments(snapshot.Comments);
    }

    public MePullRequestReviewSnapshot Snapshot { get; private set; }

    public string StableKey => Snapshot.StableKey;

    public string Reviewer
    {
        get
        {
            GitHubActor? actor = Snapshot.Review?.User ?? Snapshot.Comments.FirstOrDefault()?.User;
            return PullRequestIdentityProjection.Create(
                actor,
                LocalizedResourceText.GetString("Common.UnknownUser", "unknown"),
                AutomationId).DisplayName;
        }
    }

    public string State => LocalizeReviewState(Snapshot.Review?.State);

    public string Body => Snapshot.Review?.Body ?? string.Empty;

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    public string SubmittedText
    {
        get
        {
            DateTimeOffset? date = Snapshot.Review?.SubmittedAt ?? Snapshot.Comments.FirstOrDefault()?.CreatedAt;
            return date.HasValue ? MeWorkItemViewItem.FormatTimeAgo(date.Value) : string.Empty;
        }
    }

    public string AutomationId => $"MyPullRequestsReview_{StableKey}";

    public string AutomationName => $"Review by {Reviewer}: {State}, {SubmittedText}";

    public MarkdownDocumentSource? MarkdownSource => Snapshot.Review?.MarkdownSource;

    public KeyedObservableCollection<MePullRequestReviewCommentViewItem, GitHubPullRequestReviewComment> Comments { get; } = [];

    public bool Apply(MePullRequestReviewSnapshot snapshot)
    {
        bool changed = !HasSameProjection(Snapshot, snapshot);
        Snapshot = snapshot;
        ApplyComments(snapshot.Comments);
        if (!changed)
        {
            return false;
        }

        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(Reviewer));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(HasBody));
        OnPropertyChanged(nameof(SubmittedText));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(MarkdownSource));
        return true;
    }

    public static MePullRequestReviewSnapshot[] CreateSnapshots(
        IReadOnlyList<GitHubPullRequestReview> reviews,
        IReadOnlyList<GitHubPullRequestReviewComment> comments)
    {
        Dictionary<long, GitHubPullRequestReviewComment[]> commentsByReview = comments
            .Where(static comment => comment.PullRequestReviewId.HasValue)
            .GroupBy(static comment => comment.PullRequestReviewId!.Value)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(comment => comment.CreatedAt).ToArray());
        List<MePullRequestReviewSnapshot> snapshots = reviews
            .Select(review => new MePullRequestReviewSnapshot(
                GetReviewKey(review),
                review,
                commentsByReview.Remove(review.Id, out GitHubPullRequestReviewComment[]? matched) ? matched : []))
            .ToList();

        foreach (GitHubPullRequestReviewComment comment in comments
                     .Where(comment => !comment.PullRequestReviewId.HasValue || commentsByReview.ContainsKey(comment.PullRequestReviewId.Value))
                     .OrderBy(comment => comment.CreatedAt))
        {
            snapshots.Add(new MePullRequestReviewSnapshot(
                $"comment:{MePullRequestReviewCommentViewItem.GetStableKey(comment)}",
                null,
                [comment]));
        }

        return snapshots
            .OrderBy(snapshot => snapshot.Review?.SubmittedAt ?? snapshot.Comments.FirstOrDefault()?.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private void ApplyComments(IEnumerable<GitHubPullRequestReviewComment> comments) =>
        Comments.ApplySnapshot(
            comments,
            MePullRequestReviewCommentViewItem.GetStableKey,
            static item => item.StableKey,
            static comment => new MePullRequestReviewCommentViewItem(comment),
            static (item, comment) => item.Apply(comment));

    private static string GetReviewKey(GitHubPullRequestReview review) => review.Id > 0
        ? $"review:{review.Id.ToString(CultureInfo.InvariantCulture)}"
        : $"review:{review.NodeId ?? review.HtmlUrl}";

    private static bool HasSameProjection(MePullRequestReviewSnapshot left, MePullRequestReviewSnapshot right) =>
        string.Equals(left.StableKey, right.StableKey, StringComparison.Ordinal) &&
        string.Equals(left.Review?.State, right.Review?.State, StringComparison.Ordinal) &&
        string.Equals(left.Review?.Body, right.Review?.Body, StringComparison.Ordinal) &&
        left.Review?.SubmittedAt == right.Review?.SubmittedAt &&
        string.Equals(left.Review?.User?.Login, right.Review?.User?.Login, StringComparison.Ordinal);

    private static string LocalizeReviewState(string? state) => state?.ToUpperInvariant() switch
    {
        "APPROVED" => LocalizedResourceText.GetString("MyPullRequests.Review.State.Approved", "Approved"),
        "CHANGES_REQUESTED" => LocalizedResourceText.GetString(
            "MyPullRequests.Review.State.ChangesRequested",
            "Changes requested"),
        "DISMISSED" => LocalizedResourceText.GetString("MyPullRequests.Review.State.Dismissed", "Dismissed"),
        "PENDING" => LocalizedResourceText.GetString("MyPullRequests.Review.State.Pending", "Pending"),
        _ => LocalizedResourceText.GetString("MyPullRequests.Review.State.Commented", "Commented")
    };
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MePullRequestTimelineViewItem : ObservableObject
{
    public MePullRequestTimelineViewItem(GitHubIssueEvent timelineEvent) => TimelineEvent = timelineEvent;

    public GitHubIssueEvent TimelineEvent { get; private set; }

    public string StableKey => GetStableKey(TimelineEvent);

    public string Summary => TimelineEvent.Summary;

    public string MetaText => TimelineEvent.MetaText;

    public string AutomationId => $"MyPullRequestsTimeline_{StableKey}";

    public string AutomationName => string.IsNullOrWhiteSpace(MetaText)
        ? Summary
        : $"{Summary}, {MetaText}";

    public bool Apply(GitHubIssueEvent timelineEvent)
    {
        if (TimelineEvent.Id == timelineEvent.Id &&
            TimelineEvent.CreatedAt == timelineEvent.CreatedAt &&
            string.Equals(TimelineEvent.Event, timelineEvent.Event, StringComparison.Ordinal) &&
            string.Equals(TimelineEvent.Summary, timelineEvent.Summary, StringComparison.Ordinal) &&
            string.Equals(TimelineEvent.Actor.Login, timelineEvent.Actor.Login, StringComparison.Ordinal))
        {
            return false;
        }

        TimelineEvent = timelineEvent;
        OnPropertyChanged(nameof(TimelineEvent));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        return true;
    }

    public static string GetStableKey(GitHubIssueEvent timelineEvent) => timelineEvent.Id > 0
        ? timelineEvent.Id.ToString(CultureInfo.InvariantCulture)
        : $"{timelineEvent.Event}|{timelineEvent.CreatedAt:O}|{timelineEvent.Actor.Login}";
}
