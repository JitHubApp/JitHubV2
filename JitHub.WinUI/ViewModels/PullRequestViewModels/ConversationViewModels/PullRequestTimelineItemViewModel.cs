using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using JitHub.Models.Activities;
using JitHub.Models.LegacyGitHub;
using JitHub.Models.PRConversation;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels.ConversationViewModels;

public enum PullRequestTimelineInlineKind
{
    Text,
    Strong,
    Action,
    Label
}

public sealed class PullRequestTimelineInlinePartViewModel
{
    public PullRequestTimelineInlineKind Kind { get; init; }

    public string Text { get; init; } = string.Empty;

    public string Glyph { get; init; } = string.Empty;

    public ActivityNavigationTarget? Target { get; init; }

    public ICommand? Command { get; init; }

    public Label? Label { get; init; }

    public bool IsAction => Kind == PullRequestTimelineInlineKind.Action
        && Target is not null
        && Command?.CanExecute(Target) == true;
}

public sealed class PullRequestTimelineItemViewModel
{
    public string ActorLogin { get; init; } = string.Empty;

    public string? ActorAvatarUrl { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string Glyph { get; init; } = "\uE8A7";

    public ActivityCardTone Tone { get; init; }

    public IReadOnlyList<PullRequestTimelineInlinePartViewModel> SentenceParts { get; init; } = [];

    public IReadOnlyList<ActivityCardDetailViewModel> Details { get; init; } = [];

    public bool HasDetails => Details.Count > 0;
}

public static class PullRequestTimelineItemViewModelFactory
{
    public static PullRequestTimelineItemViewModel Create(ConversationNode node, ICommand? actionCommand)
    {
        return node switch
        {
            CommitNode commit => BuildCommit(commit, actionCommand),
            EventNode @event => BuildEvent(@event, actionCommand),
            _ => BuildFallback(node)
        };
    }

    private static PullRequestTimelineItemViewModel BuildCommit(CommitNode node, ICommand? command)
    {
        string actor = Actor(node.Author, node.Commit.Author.Name);
        string message = FirstLine(node.Commit.Message, "Commit message unavailable");

        return new PullRequestTimelineItemViewModel
        {
            ActorLogin = actor,
            ActorAvatarUrl = node.Author?.AvatarUrl,
            CreatedAt = SafeCreatedAt(node.CreatedAt),
            Glyph = "\uE930",
            Tone = ActivityCardTone.Accent,
            SentenceParts = Sentence(
                Strong(actor),
                Text(" added commit "),
                CommitLink(node, command),
                Text(" "),
                Strong(message)),
            Details = DetailList(("\uE8A7", node.Commit.Author.Email))
        };
    }

    private static PullRequestTimelineItemViewModel BuildEvent(EventNode node, ICommand? command)
    {
        string actor = Actor(node.Actor);
        EventInfoState state = node.State;
        string? reviewer = UserName(node.RequestedReviewer);
        string? assignee = UserName(node.Assignee);
        string? assigner = UserName(node.Assigner);
        string? milestone = node.Milestone?.Title;

        IReadOnlyList<PullRequestTimelineInlinePartViewModel> sentence = state switch
        {
            EventInfoState.Closed => Sentence(Strong(actor), Text(" closed this pull request")),
            EventInfoState.Reopened => Sentence(Strong(actor), Text(" reopened this pull request")),
            EventInfoState.Merged => Sentence(
                Strong(actor),
                Text(" merged this pull request"),
                string.IsNullOrWhiteSpace(node.CommitId) ? null : Text(" at "),
                CommitLink(node, command)),
            EventInfoState.HeadRefForcePushed => Sentence(
                Strong(actor),
                Text(" force-pushed this branch"),
                string.IsNullOrWhiteSpace(node.CommitId) ? null : Text(" to "),
                CommitLink(node, command)),
            EventInfoState.HeadRefDeleted => Sentence(Strong(actor), Text(" deleted the head branch")),
            EventInfoState.HeadRefRestored => Sentence(Strong(actor), Text(" restored the head branch")),
            EventInfoState.ReadyForReview => Sentence(Strong(actor), Text(" marked this pull request ready for review")),
            EventInfoState.ReviewRequested => Sentence(
                Strong(actor),
                Text(" requested review from "),
                Strong(reviewer ?? TeamName(node.RequestedTeam) ?? "a reviewer")),
            EventInfoState.ReviewRequestRemoved => Sentence(
                Strong(actor),
                Text(" removed review request from "),
                Strong(reviewer ?? TeamName(node.RequestedTeam) ?? "a reviewer")),
            EventInfoState.ReviewDismissed => Sentence(Strong(actor), Text(" dismissed a review")),
            EventInfoState.Reviewed => Sentence(Strong(actor), Text(" reviewed this pull request")),
            EventInfoState.Labeled => Sentence(
                Strong(actor),
                Text(" added label "),
                Label(node.Label)),
            EventInfoState.Unlabeled => Sentence(
                Strong(actor),
                Text(" removed label "),
                Label(node.Label)),
            EventInfoState.Assigned => Sentence(
                Strong(actor),
                Text(" assigned "),
                Strong(assignee ?? assigner ?? "someone")),
            EventInfoState.Unassigned => Sentence(
                Strong(actor),
                Text(" unassigned "),
                Strong(assignee ?? "someone")),
            EventInfoState.Milestoned => Sentence(
                Strong(actor),
                Text(" added milestone "),
                Strong(milestone ?? "a milestone")),
            EventInfoState.Demilestoned => Sentence(
                Strong(actor),
                Text(" removed milestone "),
                Strong(milestone ?? "a milestone")),
            EventInfoState.Renamed => Sentence(
                Strong(actor),
                Text(" renamed this pull request from "),
                Strong(Quote(node.RenameInfo?.From, "old title")),
                Text(" to "),
                Strong(Quote(node.RenameInfo?.To, "new title"))),
            EventInfoState.Locked => Sentence(
                Strong(actor),
                Text(" locked this conversation"),
                string.IsNullOrWhiteSpace(node.LockReason) ? null : Text(" as "),
                string.IsNullOrWhiteSpace(node.LockReason) ? null : Strong(node.LockReason)),
            EventInfoState.Unlocked => Sentence(Strong(actor), Text(" unlocked this conversation")),
            EventInfoState.CommentDeleted => Sentence(Strong(actor), Text(" deleted a comment")),
            EventInfoState.MarkedAsDuplicate => Sentence(Strong(actor), Text(" marked this as a duplicate")),
            EventInfoState.UnmarkedAsDuplicate => Sentence(Strong(actor), Text(" removed duplicate status")),
            EventInfoState.BaseRefChanged => Sentence(Strong(actor), Text(" changed the base branch")),
            EventInfoState.Crossreferenced => Sentence(Strong(actor), Text(" cross-referenced this pull request")),
            EventInfoState.Referenced => Sentence(
                Strong(actor),
                Text(" referenced this pull request"),
                string.IsNullOrWhiteSpace(node.CommitId) ? null : Text(" from "),
                CommitLink(node, command)),
            EventInfoState.Mentioned => Sentence(Strong(actor), Text(" mentioned this pull request")),
            EventInfoState.Pinned => Sentence(Strong(actor), Text(" pinned this pull request")),
            EventInfoState.Unpinned => Sentence(Strong(actor), Text(" unpinned this pull request")),
            EventInfoState.Connected => Sentence(Strong(actor), Text(" connected this pull request")),
            EventInfoState.Disconnected => Sentence(Strong(actor), Text(" disconnected this pull request")),
            EventInfoState.Commented => Sentence(Strong(actor), Text(" commented")),
            EventInfoState.CommitCommented => Sentence(
                Strong(actor),
                Text(" commented on commit "),
                CommitLink(node, command)),
            EventInfoState.LineCommented => Sentence(Strong(actor), Text(" commented on a changed line")),
            EventInfoState.AddedToProject => Sentence(Strong(actor), Text(" added this to a project")),
            EventInfoState.MovedColumnsInProject => Sentence(Strong(actor), Text(" moved this in a project")),
            EventInfoState.RemovedFromProject => Sentence(Strong(actor), Text(" removed this from a project")),
            EventInfoState.ConvertedNoteToIssue => Sentence(Strong(actor), Text(" converted a project note to this item")),
            EventInfoState.Subscribed => Sentence(Strong(actor), Text(" subscribed to updates")),
            EventInfoState.Unsubscribed => Sentence(Strong(actor), Text(" unsubscribed from updates")),
            EventInfoState.Transferred => Sentence(Strong(actor), Text(" transferred this pull request")),
            _ => Sentence(Strong(actor), Text($" {Humanize(state)}"))
        };

        return new PullRequestTimelineItemViewModel
        {
            ActorLogin = actor,
            ActorAvatarUrl = node.Actor?.AvatarUrl,
            CreatedAt = SafeCreatedAt(node.CreatedAt),
            Glyph = GlyphFor(state),
            Tone = ToneFor(state),
            SentenceParts = sentence,
            Details = DetailsFor(node)
        };
    }

    private static PullRequestTimelineItemViewModel BuildFallback(ConversationNode node)
    {
        return new PullRequestTimelineItemViewModel
        {
            ActorLogin = "github",
            CreatedAt = SafeCreatedAt(node.CreatedAt),
            Glyph = "\uE946",
            Tone = ActivityCardTone.Neutral,
            SentenceParts = Sentence(Text("GitHub updated this pull request"))
        };
    }

    private static IReadOnlyList<ActivityCardDetailViewModel> DetailsFor(EventNode node)
    {
        List<ActivityCardDetailViewModel> details = [];
        if (!string.IsNullOrWhiteSpace(node.CommitId)
            && node.State is not EventInfoState.Merged
            && node.State is not EventInfoState.HeadRefForcePushed
            && node.State is not EventInfoState.Referenced
            && node.State is not EventInfoState.CommitCommented)
        {
            details.Add(new ActivityCardDetailViewModel { Glyph = "\uE930", Text = ShortSha(node.CommitId) });
        }

        if (node.RenameInfo is not null)
        {
            details.Add(new ActivityCardDetailViewModel { Glyph = "\uE8AC", Text = $"{node.RenameInfo.From} -> {node.RenameInfo.To}" });
        }

        if (!string.IsNullOrWhiteSpace(node.LockReason))
        {
            details.Add(new ActivityCardDetailViewModel { Glyph = "\uE72E", Text = node.LockReason });
        }

        return details;
    }

    private static PullRequestTimelineInlinePartViewModel? CommitLink(CommitNode node, ICommand? command)
    {
        return CommitLink(node.Repo, node.Sha, command);
    }

    private static PullRequestTimelineInlinePartViewModel? CommitLink(EventNode node, ICommand? command)
    {
        return CommitLink(node.Repo, node.CommitId, command);
    }

    private static PullRequestTimelineInlinePartViewModel? CommitLink(Repository repo, string? sha, ICommand? command)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return null;
        }

        string label = ShortSha(sha);
        return new PullRequestTimelineInlinePartViewModel
        {
            Kind = PullRequestTimelineInlineKind.Action,
            Text = label,
            Glyph = "\uE930",
            Command = command,
            Target = new ActivityNavigationTarget
            {
                Kind = ActivityNavigationTargetKind.Commit,
                Label = label,
                RepositoryFullName = FullName(repo),
                Sha = sha
            }
        };
    }

    private static string Actor(User? user, string? fallback = null)
    {
        string login = UserName(user);
        if (!string.IsNullOrWhiteSpace(login))
        {
            return login;
        }

        return string.IsNullOrWhiteSpace(fallback) ? "someone" : fallback!;
    }

    private static string UserName(User? user) =>
        string.IsNullOrWhiteSpace(user?.Login)
            ? user?.Name ?? string.Empty
            : user.Login;

    private static string? TeamName(Team? team) =>
        string.IsNullOrWhiteSpace(team?.Name) ? null : team.Name;

    private static string FirstLine(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string[] lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? fallback : lines[0];
    }

    private static string FullName(Repository repo)
    {
        if (!string.IsNullOrWhiteSpace(repo.FullName))
        {
            return repo.FullName;
        }

        return string.IsNullOrWhiteSpace(repo.Owner?.Login) ? repo.Name : $"{repo.Owner.Login}/{repo.Name}";
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? string.Empty : sha[..Math.Min(7, sha.Length)];

    private static DateTimeOffset SafeCreatedAt(DateTimeOffset value) =>
        value == default ? DateTimeOffset.Now : value;

    private static string Quote(string? value, string fallback) =>
        $"\"{(string.IsNullOrWhiteSpace(value) ? fallback : value)}\"";

    private static string Humanize(EventInfoState state)
    {
        string value = state.ToString();
        List<char> chars = [];
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsWhiteSpace(value[i - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(char.ToLowerInvariant(value[i]));
        }

        return new string(chars.ToArray());
    }

    private static string GlyphFor(EventInfoState state) => state switch
    {
        EventInfoState.Merged => "\uE73E",
        EventInfoState.Closed => "\uE711",
        EventInfoState.Reopened or EventInfoState.ReadyForReview => "\uE930",
        EventInfoState.HeadRefForcePushed => "\uE74A",
        EventInfoState.HeadRefDeleted => "\uE74D",
        EventInfoState.HeadRefRestored => "\uE777",
        EventInfoState.Labeled or EventInfoState.Unlabeled => "\uE8EC",
        EventInfoState.Assigned or EventInfoState.Unassigned => "\uE77B",
        EventInfoState.ReviewRequested or EventInfoState.ReviewRequestRemoved or EventInfoState.Reviewed => "\uE8FD",
        EventInfoState.Renamed => "\uE8AC",
        EventInfoState.Locked => "\uE72E",
        EventInfoState.Unlocked => "\uE785",
        EventInfoState.Milestoned or EventInfoState.Demilestoned => "\uE7C1",
        EventInfoState.Commented or EventInfoState.LineCommented or EventInfoState.CommitCommented or EventInfoState.CommentDeleted => "\uE90A",
        EventInfoState.Pinned or EventInfoState.Unpinned => "\uE718",
        _ => "\uE946"
    };

    private static ActivityCardTone ToneFor(EventInfoState state) => state switch
    {
        EventInfoState.Merged => ActivityCardTone.Purple,
        EventInfoState.Closed or EventInfoState.HeadRefDeleted or EventInfoState.Locked or EventInfoState.CommentDeleted => ActivityCardTone.Danger,
        EventInfoState.Reopened or EventInfoState.ReadyForReview or EventInfoState.HeadRefRestored => ActivityCardTone.Success,
        EventInfoState.HeadRefForcePushed or EventInfoState.BaseRefChanged => ActivityCardTone.Warning,
        EventInfoState.Labeled or EventInfoState.Milestoned or EventInfoState.Pinned => ActivityCardTone.Gold,
        EventInfoState.ReviewRequested or EventInfoState.Reviewed or EventInfoState.ReviewDismissed => ActivityCardTone.Accent,
        _ => ActivityCardTone.Neutral
    };

    private static PullRequestTimelineInlinePartViewModel Text(string text) => new()
    {
        Kind = PullRequestTimelineInlineKind.Text,
        Text = text
    };

    private static PullRequestTimelineInlinePartViewModel Strong(string text) => new()
    {
        Kind = PullRequestTimelineInlineKind.Strong,
        Text = text
    };

    private static PullRequestTimelineInlinePartViewModel? Label(Label? label)
    {
        if (label is null || string.IsNullOrWhiteSpace(label.Name))
        {
            return Strong("label");
        }

        return new PullRequestTimelineInlinePartViewModel
        {
            Kind = PullRequestTimelineInlineKind.Label,
            Text = label.Name,
            Label = label
        };
    }

    private static IReadOnlyList<PullRequestTimelineInlinePartViewModel> Sentence(
        params PullRequestTimelineInlinePartViewModel?[] parts)
    {
        return parts
            .Where(part => part is not null && (!string.IsNullOrEmpty(part.Text) || part.Kind == PullRequestTimelineInlineKind.Label))
            .Cast<PullRequestTimelineInlinePartViewModel>()
            .ToList();
    }

    private static IReadOnlyList<ActivityCardDetailViewModel> DetailList(params (string Glyph, string? Text)[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value.Text))
            .Select(value => new ActivityCardDetailViewModel { Glyph = value.Glyph, Text = value.Text! })
            .ToList();
    }
}
