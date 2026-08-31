using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using JitHub.Models.Activities;
using JitHub.Models.LegacyGitHub;
using JitHub.Models.PRConversation;
using JitHub.Services;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels.ConversationViewModels;

public enum PullRequestTimelineInlineKind
{
    Text,
    Strong,
    Action,
    Label
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PullRequestTimelineInlinePartViewModel
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

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PullRequestTimelineItemViewModel
{
    public string AutomationScope { get; set; } = string.Empty;

    public string AvatarAutomationId => $"PullRequestTimelineAvatar_{AutomationScope}";
    public string ActorDisplayName { get; init; } = string.Empty;

    public string? ActorLogin { get; init; }

    public string? ActorAvatarUrl { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string Glyph { get; init; } = "\uE8A7";

    public ActivityCardTone Tone { get; init; }

    public List<PullRequestTimelineInlinePartViewModel> SentenceParts { get; init; } = [];

    public List<ActivityCardDetailViewModel> Details { get; init; } = [];

    public bool HasDetails => Details.Count > 0;
}

public static class PullRequestTimelineItemViewModelFactory
{
    public static PullRequestTimelineItemViewModel Create(ConversationNode node, ICommand? actionCommand)
    {
        PullRequestTimelineItemViewModel viewModel = node switch
        {
            CommitNode commit => BuildCommit(commit, actionCommand),
            EventNode @event => BuildEvent(@event, actionCommand),
            _ => BuildFallback(node)
        };
        viewModel.AutomationScope = CreateAutomationScope(node);
        return viewModel;
    }

    private static string CreateAutomationScope(ConversationNode node) => node switch
    {
        CommitNode commit when !string.IsNullOrWhiteSpace(commit.NodeId) => $"commit:{commit.NodeId}",
        CommitNode commit when !string.IsNullOrWhiteSpace(commit.Sha) => $"commit:{commit.Sha}",
        EventNode @event when !string.IsNullOrWhiteSpace(@event.NodeId) => $"event:{@event.NodeId}",
        EventNode @event when @event.Id > 0 => $"event:{@event.Id}",
        _ => $"{node.GetType().Name}:{node.Repo?.Id}:{node.Number}:{node.CreatedAt:O}"
    };

    private static PullRequestTimelineItemViewModel BuildCommit(CommitNode node, ICommand? command)
    {
        UserIdentityPresentation actor = Actor(node.Author, node.Commit.Author.Name);
        string message = FirstLine(
            node.Commit.Message,
            LocalizedResourceText.GetString(
                "PullRequestTimeline.CommitMessageUnavailable",
                "Commit message unavailable"));

        return new PullRequestTimelineItemViewModel
        {
            ActorDisplayName = actor.DisplayName,
            ActorLogin = actor.AuthenticatedLogin,
            ActorAvatarUrl = node.Author?.AvatarUrl,
            CreatedAt = SafeCreatedAt(node.CreatedAt),
            Glyph = "\uE930",
            Tone = ActivityCardTone.Accent,
            SentenceParts = LocalizedSentence(
                "CommitAdded",
                "{0} added commit {1} {2}",
                Strong(actor.DisplayName),
                CommitLink(node, command),
                Strong(message)),
            Details = DetailList(("\uE8A7", node.Commit.Author.Email))
        };
    }

    private static PullRequestTimelineItemViewModel BuildEvent(EventNode node, ICommand? command)
    {
        UserIdentityPresentation actor = Actor(node.Actor);
        EventInfoState state = node.State;
        string? reviewer = UserName(node.RequestedReviewer);
        string? assignee = UserName(node.Assignee);
        string? assigner = UserName(node.Assigner);
        string? milestone = node.Milestone?.Title;

        List<PullRequestTimelineInlinePartViewModel> sentence = state switch
        {
            EventInfoState.Closed => LocalizedSentence("Closed", "{0} closed this pull request", Strong(actor.DisplayName)),
            EventInfoState.Reopened => LocalizedSentence("Reopened", "{0} reopened this pull request", Strong(actor.DisplayName)),
            EventInfoState.Merged => string.IsNullOrWhiteSpace(node.CommitId)
                ? LocalizedSentence("Merged", "{0} merged this pull request", Strong(actor.DisplayName))
                : LocalizedSentence("MergedAt", "{0} merged this pull request at {1}", Strong(actor.DisplayName), CommitLink(node, command)),
            EventInfoState.HeadRefForcePushed => string.IsNullOrWhiteSpace(node.CommitId)
                ? LocalizedSentence("ForcePushed", "{0} force-pushed this branch", Strong(actor.DisplayName))
                : LocalizedSentence("ForcePushedTo", "{0} force-pushed this branch to {1}", Strong(actor.DisplayName), CommitLink(node, command)),
            EventInfoState.HeadRefDeleted => LocalizedSentence("HeadRefDeleted", "{0} deleted the head branch", Strong(actor.DisplayName)),
            EventInfoState.HeadRefRestored => LocalizedSentence("HeadRefRestored", "{0} restored the head branch", Strong(actor.DisplayName)),
            EventInfoState.ReadyForReview => LocalizedSentence("ReadyForReview", "{0} marked this pull request ready for review", Strong(actor.DisplayName)),
            EventInfoState.ReviewRequested => LocalizedSentence(
                "ReviewRequested",
                "{0} requested review from {1}",
                Strong(actor.DisplayName),
                Strong(reviewer ?? TeamName(node.RequestedTeam) ?? TimelineText("ReviewerFallback", "a reviewer"))),
            EventInfoState.ReviewRequestRemoved => LocalizedSentence(
                "ReviewRequestRemoved",
                "{0} removed review request from {1}",
                Strong(actor.DisplayName),
                Strong(reviewer ?? TeamName(node.RequestedTeam) ?? TimelineText("ReviewerFallback", "a reviewer"))),
            EventInfoState.ReviewDismissed => LocalizedSentence("ReviewDismissed", "{0} dismissed a review", Strong(actor.DisplayName)),
            EventInfoState.Reviewed => LocalizedSentence("Reviewed", "{0} reviewed this pull request", Strong(actor.DisplayName)),
            EventInfoState.Labeled => LocalizedSentence("Labeled", "{0} added label {1}", Strong(actor.DisplayName), Label(node.Label)),
            EventInfoState.Unlabeled => LocalizedSentence("Unlabeled", "{0} removed label {1}", Strong(actor.DisplayName), Label(node.Label)),
            EventInfoState.Assigned => LocalizedSentence(
                "Assigned",
                "{0} assigned {1}",
                Strong(actor.DisplayName),
                Strong(assignee ?? assigner ?? TimelineText("SomeoneFallback", "someone"))),
            EventInfoState.Unassigned => LocalizedSentence(
                "Unassigned",
                "{0} unassigned {1}",
                Strong(actor.DisplayName),
                Strong(assignee ?? TimelineText("SomeoneFallback", "someone"))),
            EventInfoState.Milestoned => LocalizedSentence(
                "Milestoned",
                "{0} added milestone {1}",
                Strong(actor.DisplayName),
                Strong(milestone ?? TimelineText("MilestoneFallback", "a milestone"))),
            EventInfoState.Demilestoned => LocalizedSentence(
                "Demilestoned",
                "{0} removed milestone {1}",
                Strong(actor.DisplayName),
                Strong(milestone ?? TimelineText("MilestoneFallback", "a milestone"))),
            EventInfoState.Renamed => LocalizedSentence(
                "Renamed",
                "{0} renamed this pull request from {1} to {2}",
                Strong(actor.DisplayName),
                Strong(Quote(node.RenameInfo?.From, TimelineText("OldTitleFallback", "old title"))),
                Strong(Quote(node.RenameInfo?.To, TimelineText("NewTitleFallback", "new title")))),
            EventInfoState.Locked => string.IsNullOrWhiteSpace(node.LockReason)
                ? LocalizedSentence("Locked", "{0} locked this conversation", Strong(actor.DisplayName))
                : LocalizedSentence("LockedAs", "{0} locked this conversation as {1}", Strong(actor.DisplayName), Strong(node.LockReason)),
            EventInfoState.Unlocked => LocalizedSentence("Unlocked", "{0} unlocked this conversation", Strong(actor.DisplayName)),
            EventInfoState.CommentDeleted => LocalizedSentence("CommentDeleted", "{0} deleted a comment", Strong(actor.DisplayName)),
            EventInfoState.MarkedAsDuplicate => LocalizedSentence("MarkedAsDuplicate", "{0} marked this as a duplicate", Strong(actor.DisplayName)),
            EventInfoState.UnmarkedAsDuplicate => LocalizedSentence("UnmarkedAsDuplicate", "{0} removed duplicate status", Strong(actor.DisplayName)),
            EventInfoState.BaseRefChanged => LocalizedSentence("BaseRefChanged", "{0} changed the base branch", Strong(actor.DisplayName)),
            EventInfoState.Crossreferenced => LocalizedSentence("Crossreferenced", "{0} cross-referenced this pull request", Strong(actor.DisplayName)),
            EventInfoState.Referenced => string.IsNullOrWhiteSpace(node.CommitId)
                ? LocalizedSentence("Referenced", "{0} referenced this pull request", Strong(actor.DisplayName))
                : LocalizedSentence("ReferencedFrom", "{0} referenced this pull request from {1}", Strong(actor.DisplayName), CommitLink(node, command)),
            EventInfoState.Mentioned => LocalizedSentence("Mentioned", "{0} mentioned this pull request", Strong(actor.DisplayName)),
            EventInfoState.Pinned => LocalizedSentence("Pinned", "{0} pinned this pull request", Strong(actor.DisplayName)),
            EventInfoState.Unpinned => LocalizedSentence("Unpinned", "{0} unpinned this pull request", Strong(actor.DisplayName)),
            EventInfoState.Connected => LocalizedSentence("Connected", "{0} connected this pull request", Strong(actor.DisplayName)),
            EventInfoState.Disconnected => LocalizedSentence("Disconnected", "{0} disconnected this pull request", Strong(actor.DisplayName)),
            EventInfoState.Commented => LocalizedSentence("Commented", "{0} commented", Strong(actor.DisplayName)),
            EventInfoState.CommitCommented => LocalizedSentence("CommitCommented", "{0} commented on commit {1}", Strong(actor.DisplayName), CommitLink(node, command)),
            EventInfoState.LineCommented => LocalizedSentence("LineCommented", "{0} commented on a changed line", Strong(actor.DisplayName)),
            EventInfoState.AddedToProject => LocalizedSentence("AddedToProject", "{0} added this to a project", Strong(actor.DisplayName)),
            EventInfoState.MovedColumnsInProject => LocalizedSentence("MovedColumnsInProject", "{0} moved this in a project", Strong(actor.DisplayName)),
            EventInfoState.RemovedFromProject => LocalizedSentence("RemovedFromProject", "{0} removed this from a project", Strong(actor.DisplayName)),
            EventInfoState.ConvertedNoteToIssue => LocalizedSentence("ConvertedNoteToIssue", "{0} converted a project note to this item", Strong(actor.DisplayName)),
            EventInfoState.Subscribed => LocalizedSentence("Subscribed", "{0} subscribed to updates", Strong(actor.DisplayName)),
            EventInfoState.Unsubscribed => LocalizedSentence("Unsubscribed", "{0} unsubscribed from updates", Strong(actor.DisplayName)),
            EventInfoState.Transferred => LocalizedSentence("Transferred", "{0} transferred this pull request", Strong(actor.DisplayName)),
            _ => LocalizedSentence("Updated", "{0} updated this pull request", Strong(actor.DisplayName))
        };

        return new PullRequestTimelineItemViewModel
        {
            ActorDisplayName = actor.DisplayName,
            ActorLogin = actor.AuthenticatedLogin,
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
            ActorDisplayName = "GitHub",
            ActorLogin = null,
            CreatedAt = SafeCreatedAt(node.CreatedAt),
            Glyph = "\uE946",
            Tone = ActivityCardTone.Neutral,
            SentenceParts = LocalizedSentence(
                "FallbackUpdated",
                "GitHub updated this pull request")
        };
    }

    private static List<ActivityCardDetailViewModel> DetailsFor(EventNode node)
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

    private static UserIdentityPresentation Actor(User? user, string? fallback = null) =>
        UserIdentityNavigationPolicy.CreatePresentation(
            user?.Login,
            user?.Name,
            string.IsNullOrWhiteSpace(fallback) ? TimelineText("SomeoneFallback", "someone") : fallback!);

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
            return Strong(TimelineText("LabelFallback", "label"));
        }

        return new PullRequestTimelineInlinePartViewModel
        {
            Kind = PullRequestTimelineInlineKind.Label,
            Text = label.Name,
            Label = label
        };
    }

    private static List<PullRequestTimelineInlinePartViewModel> Sentence(
        params PullRequestTimelineInlinePartViewModel?[] parts)
    {
        return parts
            .Where(part => part is not null && (!string.IsNullOrEmpty(part.Text) || part.Kind == PullRequestTimelineInlineKind.Label))
            .Cast<PullRequestTimelineInlinePartViewModel>()
            .ToList();
    }

    private static string TimelineText(string key, string fallback) =>
        LocalizedResourceText.GetString($"PullRequestTimeline.{key}", fallback);

    private static List<PullRequestTimelineInlinePartViewModel> LocalizedSentence(
        string key,
        string fallback,
        params PullRequestTimelineInlinePartViewModel?[] arguments)
    {
        string template = TimelineText(key, fallback);
        List<PullRequestTimelineInlinePartViewModel> parts = [];
        int cursor = 0;
        while (cursor < template.Length)
        {
            int open = template.IndexOf('{', cursor);
            if (open < 0)
            {
                parts.Add(Text(template[cursor..]));
                break;
            }

            if (open > cursor)
            {
                parts.Add(Text(template[cursor..open]));
            }

            int close = template.IndexOf('}', open + 1);
            if (close < 0 ||
                !int.TryParse(template[(open + 1)..close], out int argumentIndex) ||
                argumentIndex < 0 ||
                argumentIndex >= arguments.Length)
            {
                parts.Add(Text(template[open..]));
                break;
            }

            if (arguments[argumentIndex] is { } argument)
            {
                parts.Add(argument);
            }
            cursor = close + 1;
        }

        return Sentence([.. parts]);
    }

    private static List<ActivityCardDetailViewModel> DetailList(params (string Glyph, string? Text)[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value.Text))
            .Select(value => new ActivityCardDetailViewModel { Glyph = value.Glyph, Text = value.Text! })
            .ToList();
    }
}
