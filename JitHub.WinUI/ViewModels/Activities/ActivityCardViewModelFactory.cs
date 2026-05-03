using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using JitHub.Models.Activities;
using JitHub.Models.GitHub;

namespace JitHub.WinUI.ViewModels.Activities;

public static class ActivityCardViewModelFactory
{
    public static ActivityCardViewModel Create(GitHubActivityEvent activityEvent, ICommand? actionCommand = null)
    {
        IGitHubActivityPayload payload = activityEvent.TypedPayload;
        ActivityDraft draft = payload switch
        {
            PushEventPayload push => BuildPush(activityEvent, push, actionCommand),
            CommitCommentEventPayload commitComment => BuildCommitComment(activityEvent, commitComment, actionCommand),
            CreateEventPayload create => BuildCreate(activityEvent, create, actionCommand),
            DeleteEventPayload delete => BuildDelete(activityEvent, delete, actionCommand),
            DiscussionEventPayload discussion => BuildDiscussion(activityEvent, discussion, actionCommand),
            ForkEventPayload fork => BuildFork(activityEvent, fork, actionCommand),
            GollumEventPayload gollum => BuildGollum(activityEvent, gollum, actionCommand),
            IssueCommentEventPayload issueComment => BuildIssueComment(activityEvent, issueComment, actionCommand),
            IssuesEventPayload issues => BuildIssue(activityEvent, issues, actionCommand),
            MemberEventPayload member => BuildMember(activityEvent, member, actionCommand),
            PublicEventPayload _ => BuildPublic(activityEvent, actionCommand),
            PullRequestEventPayload pullRequest => BuildPullRequest(activityEvent, pullRequest, actionCommand),
            PullRequestReviewEventPayload review => BuildPullRequestReview(activityEvent, review, actionCommand),
            PullRequestReviewCommentEventPayload reviewComment => BuildPullRequestReviewComment(activityEvent, reviewComment, actionCommand),
            ReleaseEventPayload release => BuildRelease(activityEvent, release, actionCommand),
            WatchEventPayload watch => BuildWatch(activityEvent, watch, actionCommand),
            UnknownActivityPayload unknown => BuildUnknown(activityEvent, unknown, actionCommand),
            _ => BuildUnknown(activityEvent, new UnknownActivityPayload { EventType = activityEvent.Type, RawPayload = activityEvent.Payload }, actionCommand)
        };

        return new ActivityCardViewModel
        {
            EventId = activityEvent.Id,
            EventType = activityEvent.Type,
            ActorLogin = string.IsNullOrWhiteSpace(activityEvent.Actor.Login) ? "github" : activityEvent.Actor.Login,
            ActorAvatarUrl = activityEvent.Actor.AvatarUrl,
            RepoDisplayName = string.IsNullOrWhiteSpace(activityEvent.Repo.Name) ? "GitHub" : activityEvent.Repo.Name,
            TimestampText = activityEvent.CreatedAt?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "Unknown time",
            Title = draft.Title,
            Subtitle = draft.Subtitle,
            Glyph = draft.Glyph,
            Tone = draft.Tone,
            SentenceParts = draft.SentenceParts.Count > 0 ? draft.SentenceParts : Sentence(Strong(draft.Title)),
            Details = draft.Details,
            Actions = draft.Actions,
            UnsupportedTodos = draft.UnsupportedTodos
        };
    }

    public static IReadOnlyList<ActivityCardViewModel> CreateMany(
        IEnumerable<GitHubActivityEvent> events,
        ICommand? actionCommand = null)
    {
        return events.Select(activityEvent => Create(activityEvent, actionCommand)).ToList();
    }

    private static ActivityDraft BuildPush(GitHubActivityEvent activityEvent, PushEventPayload payload, ICommand? command)
    {
        string branch = BranchName(payload.Ref);
        GitHubActivityPushCommit[] commits = payload.Commits ?? [];
        int commitCount = payload.CommitCount;
        List<ActivityCardDetailViewModel> details = [];
        foreach (GitHubActivityPushCommit commit in commits.Take(3))
        {
            string message = FirstLine(commit.Message, "Commit message unavailable");
            string prefix = ShortSha(commit.Sha);
            details.Add(new ActivityCardDetailViewModel
            {
                Glyph = "\uE930",
                Text = string.IsNullOrWhiteSpace(prefix) ? message : $"{prefix}  {message}"
            });
        }

        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? branchAction = string.IsNullOrWhiteSpace(branch)
            ? null
            : RepoAction(activityEvent, command, branch, branch);
        ActivityCardActionViewModel? commitAction = commitCount > 0
            ? CommitAction(activityEvent, command, payload.Head)
            : null;
        IReadOnlyList<ActivitySentencePartViewModel> sentenceParts = commitCount > 0
            ? Sentence(
                Strong(Actor(activityEvent)),
                Text(" pushed "),
                Link(commitAction, commitCount == 1 ? ShortSha(payload.Head) : $"{commitCount} commits"),
                Text(" to "),
                Link(repoAction),
                string.IsNullOrWhiteSpace(branch) ? null : Text(" on "),
                Link(branchAction))
            : Sentence(
                Strong(Actor(activityEvent)),
                Text(" pushed to "),
                Link(repoAction),
                string.IsNullOrWhiteSpace(branch) ? null : Text(" on "),
                Link(branchAction));

        return new ActivityDraft
        {
            Title = commitCount switch
            {
                0 => $"{Actor(activityEvent)} pushed to {activityEvent.Repo.Name}",
                1 => $"{Actor(activityEvent)} pushed 1 commit",
                _ => $"{Actor(activityEvent)} pushed {commitCount} commits"
            },
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE74A",
            Tone = ActivityCardTone.Accent,
            SentenceParts = sentenceParts,
            Details = details,
            Actions = Combine(repoAction, branchAction, commitAction)
        };
    }

    private static ActivityDraft BuildCommitComment(GitHubActivityEvent activityEvent, CommitCommentEventPayload payload, ICommand? command)
    {
        string sha = payload.Comment?.CommitId ?? string.Empty;
        List<ActivityCardDetailViewModel> details = [];
        if (!string.IsNullOrWhiteSpace(payload.Comment?.Path))
        {
            string lineText = payload.Comment.Line is int line ? $":{line}" : string.Empty;
            details.Add(new ActivityCardDetailViewModel { Glyph = "\uE8A5", Text = $"{payload.Comment.Path}{lineText}" });
        }
        if (!string.IsNullOrWhiteSpace(payload.Comment?.Body))
        {
            details.Add(new ActivityCardDetailViewModel { Glyph = "\uE90A", Text = FirstLine(payload.Comment.Body, "Commit comment") });
        }

        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? commitAction = CommitAction(activityEvent, command, sha);

        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} commented on a commit",
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE90A",
            Tone = ActivityCardTone.Purple,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text(" commented on commit "),
                Link(commitAction, ShortSha(sha)),
                Text(" in "),
                Link(repoAction)),
            Details = details,
            Actions = Combine(repoAction, commitAction)
        };
    }

    private static ActivityDraft BuildCreate(GitHubActivityEvent activityEvent, CreateEventPayload payload, ICommand? command)
    {
        string refType = SafeLower(payload.RefType, "repository");
        string refName = payload.Ref ?? payload.MasterBranch ?? activityEvent.Repo.Name;
        string title = string.Equals(refType, "repository", StringComparison.OrdinalIgnoreCase)
            ? $"{Actor(activityEvent)} created a repository"
            : $"{Actor(activityEvent)} created {refType} {refName}";

        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? branchAction = string.Equals(refType, "branch", StringComparison.OrdinalIgnoreCase)
            ? RepoAction(activityEvent, command, refName, refName)
            : null;

        return new ActivityDraft
        {
            Title = title,
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE710",
            Tone = ActivityCardTone.Success,
            SentenceParts = string.Equals(refType, "repository", StringComparison.OrdinalIgnoreCase)
                ? Sentence(Strong(Actor(activityEvent)), Text(" created repository "), Link(repoAction))
                : Sentence(
                    Strong(Actor(activityEvent)),
                    Text($" created {refType} "),
                    Link(branchAction, refName) ?? Strong(refName),
                    Text(" in "),
                    Link(repoAction)),
            Details = DetailList(("\uE946", payload.Description)),
            Actions = Combine(repoAction, branchAction)
        };
    }

    private static ActivityDraft BuildDelete(GitHubActivityEvent activityEvent, DeleteEventPayload payload, ICommand? command)
    {
        string refType = SafeLower(payload.RefType, "ref");
        string refName = payload.Ref ?? "unknown ref";
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} deleted {refType} {refName}",
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE74D",
            Tone = ActivityCardTone.Warning,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" deleted {refType} "),
                Strong(refName),
                Text(" from "),
                Link(repoAction)),
            Details = [],
            Actions = Combine(repoAction)
        };
    }

    private static ActivityDraft BuildDiscussion(GitHubActivityEvent activityEvent, DiscussionEventPayload payload, ICommand? command)
    {
        string title = payload.Discussion?.Title ?? "a discussion";
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} {PastTense(payload.Action, "created")} {title}",
            Subtitle = "Discussions are tracked as a future in-app surface.",
            Glyph = "\uE8F2",
            Tone = ActivityCardTone.Neutral,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" {PastTense(payload.Action, "created")} discussion "),
                Strong(title),
                Text(" in "),
                Link(repoAction)),
            Details = [],
            Actions = Combine(repoAction),
            UnsupportedTodos = Unsupported(activityEvent, "Discussion detail pages are not implemented yet.")
        };
    }

    private static ActivityDraft BuildFork(GitHubActivityEvent activityEvent, ForkEventPayload payload, ICommand? command)
    {
        string forkName = payload.Forkee?.FullName ?? "a new fork";
        ActivityCardActionViewModel? sourceRepoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? forkRepoAction = RepositoryAction(payload.Forkee, command, forkName);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} forked {activityEvent.Repo.Name}",
            Subtitle = $"Forked to {forkName}",
            Glyph = "\uE8EE",
            Tone = ActivityCardTone.Gold,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text(" forked "),
                Link(sourceRepoAction),
                Text(" to "),
                Link(forkRepoAction)),
            Details = [],
            Actions = Combine(sourceRepoAction, forkRepoAction)
        };
    }

    private static ActivityDraft BuildGollum(GitHubActivityEvent activityEvent, GollumEventPayload payload, ICommand? command)
    {
        List<ActivityCardDetailViewModel> details = payload.Pages.Take(3)
            .Select(page => new ActivityCardDetailViewModel
            {
                Glyph = "\uE82D",
                Text = $"{PastTense(page.Action, "updated")} {page.Title ?? page.PageName ?? "wiki page"}"
            })
            .ToList();

        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} updated the wiki",
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE82D",
            Tone = ActivityCardTone.Neutral,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text(" updated the wiki in "),
                Link(repoAction)),
            Details = details,
            Actions = Combine(repoAction),
            UnsupportedTodos = Unsupported(activityEvent, "Wiki pages are not implemented yet.")
        };
    }

    private static ActivityDraft BuildIssueComment(GitHubActivityEvent activityEvent, IssueCommentEventPayload payload, ICommand? command)
    {
        GitHubIssue? issue = payload.Issue;
        bool isPullRequest = issue?.IsPullRequest == true;
        string issueLabel = isPullRequest ? "pull request" : "issue";
        int number = issue?.Number ?? 0;
        string numberText = number > 0 ? $" #{number}" : string.Empty;
        string title = issue?.Title ?? $"a {issueLabel}";

        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? threadAction = isPullRequest
            ? PullRequestAction(activityEvent, command, number)
            : IssueAction(activityEvent, command, number);

        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} commented on {issueLabel}{numberText}",
            Subtitle = title,
            Glyph = "\uE90A",
            Tone = ActivityCardTone.Purple,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" commented on {issueLabel} "),
                Link(threadAction, number > 0 ? $"#{number}" : issueLabel),
                Text(" in "),
                Link(repoAction)),
            Details = DetailList(("\uE946", title), ("\uE90A", FirstLine(payload.Comment?.Body, "Comment body unavailable"))),
            Actions = Combine(repoAction, threadAction)
        };
    }

    private static ActivityDraft BuildIssue(GitHubActivityEvent activityEvent, IssuesEventPayload payload, ICommand? command)
    {
        int number = payload.Issue?.Number ?? 0;
        string action = PastTense(payload.Action, "updated");
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? issueAction = IssueAction(activityEvent, command, number);

        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} {action} issue{NumberSuffix(number)}",
            Subtitle = payload.Issue?.Title ?? activityEvent.Repo.Name,
            Glyph = IssueGlyph(payload.Action),
            Tone = IssueTone(payload.Action),
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" {action} issue "),
                Link(issueAction, number > 0 ? $"#{number}" : "issue"),
                Text(" in "),
                Link(repoAction)),
            Details = DetailList(("\uE946", payload.Issue?.Title), ("\uE8EC", payload.Label?.Name), ("\uE77B", payload.Assignee?.Login)),
            Actions = Combine(repoAction, issueAction)
        };
    }

    private static ActivityDraft BuildMember(GitHubActivityEvent activityEvent, MemberEventPayload payload, ICommand? command)
    {
        string member = payload.Member?.Login ?? "a collaborator";
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} {PastTense(payload.Action, "added")} {member}",
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE716",
            Tone = ActivityCardTone.Success,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" {PastTense(payload.Action, "added")} "),
                Strong(member),
                Text(" to "),
                Link(repoAction)),
            Details = [],
            Actions = Combine(repoAction)
        };
    }

    private static ActivityDraft BuildPublic(GitHubActivityEvent activityEvent, ICommand? command)
    {
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} made the repository public",
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE774",
            Tone = ActivityCardTone.Success,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text(" made "),
                Link(repoAction),
                Text(" public")),
            Details = [],
            Actions = Combine(repoAction)
        };
    }

    private static ActivityDraft BuildPullRequest(GitHubActivityEvent activityEvent, PullRequestEventPayload payload, ICommand? command)
    {
        int number = payload.PullRequest?.Number ?? payload.Number ?? 0;
        string action = PastTense(payload.Action, "updated");
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? pullRequestAction = PullRequestAction(activityEvent, command, number);

        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} {action} pull request{NumberSuffix(number)}",
            Subtitle = payload.PullRequest?.Title ?? activityEvent.Repo.Name,
            Glyph = PullRequestGlyph(payload.Action),
            Tone = PullRequestTone(payload.Action, payload.PullRequest?.Merged == true),
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" {action} pull request "),
                Link(pullRequestAction, number > 0 ? $"#{number}" : "PR"),
                Text(" in "),
                Link(repoAction)),
            Details = DetailList(("\uE946", payload.PullRequest?.Title), ("\uE8EC", payload.Label?.Name), ("\uE77B", payload.Assignee?.Login)),
            Actions = Combine(repoAction, pullRequestAction)
        };
    }

    private static ActivityDraft BuildPullRequestReview(GitHubActivityEvent activityEvent, PullRequestReviewEventPayload payload, ICommand? command)
    {
        int number = payload.PullRequest?.Number ?? 0;
        string reviewState = payload.Review?.State ?? payload.Action ?? "review";
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? pullRequestAction = PullRequestAction(activityEvent, command, number);
        ActivityCardActionViewModel? commitAction = CommitAction(activityEvent, command, payload.Review?.CommitId);

        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} reviewed pull request{NumberSuffix(number)}",
            Subtitle = payload.PullRequest?.Title ?? activityEvent.Repo.Name,
            Glyph = "\uE8FD",
            Tone = ActivityCardTone.Accent,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" {PastTense(reviewState, "reviewed")} pull request "),
                Link(pullRequestAction, number > 0 ? $"#{number}" : "PR"),
                Text(" in "),
                Link(repoAction)),
            Details = DetailList(("\uE8FD", reviewState), ("\uE946", payload.PullRequest?.Title), ("\uE930", ShortSha(payload.Review?.CommitId))),
            Actions = Combine(repoAction, pullRequestAction, commitAction)
        };
    }

    private static ActivityDraft BuildPullRequestReviewComment(GitHubActivityEvent activityEvent, PullRequestReviewCommentEventPayload payload, ICommand? command)
    {
        int number = payload.PullRequest?.Number ?? 0;
        string? path = payload.Comment?.Path;
        string lineText = payload.Comment?.Position is int position ? $":{position}" : string.Empty;
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        ActivityCardActionViewModel? pullRequestAction = PullRequestAction(activityEvent, command, number);
        ActivityCardActionViewModel? commitAction = CommitAction(activityEvent, command, payload.Comment?.CommitId);

        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} commented on pull request{NumberSuffix(number)}",
            Subtitle = payload.PullRequest?.Title ?? activityEvent.Repo.Name,
            Glyph = "\uE90A",
            Tone = ActivityCardTone.Purple,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text(" commented on pull request "),
                Link(pullRequestAction, number > 0 ? $"#{number}" : "PR"),
                Text(" in "),
                Link(repoAction)),
            Details = DetailList(("\uE8A5", string.IsNullOrWhiteSpace(path) ? null : $"{path}{lineText}"), ("\uE90A", FirstLine(payload.Comment?.Body, "Review comment body unavailable"))),
            Actions = Combine(repoAction, pullRequestAction, commitAction)
        };
    }

    private static ActivityDraft BuildRelease(GitHubActivityEvent activityEvent, ReleaseEventPayload payload, ICommand? command)
    {
        string releaseName = payload.Release?.Name ?? payload.Release?.TagName ?? "a release";
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} {PastTense(payload.Action, "published")} {releaseName}",
            Subtitle = "Release pages are tracked as a future in-app surface.",
            Glyph = "\uE896",
            Tone = ActivityCardTone.Gold,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text($" {PastTense(payload.Action, "published")} release "),
                Strong(releaseName),
                Text(" in "),
                Link(repoAction)),
            Details = DetailList(("\uE896", releaseName), ("\uE8EE", payload.Release?.TagName)),
            Actions = Combine(repoAction),
            UnsupportedTodos = Unsupported(activityEvent, "Release detail pages are not implemented yet.")
        };
    }

    private static ActivityDraft BuildWatch(GitHubActivityEvent activityEvent, WatchEventPayload payload, ICommand? command)
    {
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} starred the repository",
            Subtitle = activityEvent.Repo.Name,
            Glyph = "\uE734",
            Tone = ActivityCardTone.Gold,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text(" starred "),
                Link(repoAction)),
            Details = [],
            Actions = Combine(repoAction)
        };
    }

    private static ActivityDraft BuildUnknown(GitHubActivityEvent activityEvent, UnknownActivityPayload payload, ICommand? command)
    {
        string eventType = string.IsNullOrWhiteSpace(payload.EventType) ? activityEvent.Type : payload.EventType;
        ActivityCardActionViewModel? repoAction = RepoAction(activityEvent, command, activityEvent.Repo.Name);
        return new ActivityDraft
        {
            Title = $"{Actor(activityEvent)} triggered GitHub activity",
            Subtitle = HumanizeType(eventType),
            Glyph = "\uE946",
            Tone = ActivityCardTone.Neutral,
            SentenceParts = Sentence(
                Strong(Actor(activityEvent)),
                Text(" triggered "),
                Strong(HumanizeType(eventType).ToLowerInvariant()),
                string.IsNullOrWhiteSpace(activityEvent.Repo.Name) ? null : Text(" in "),
                Link(repoAction)),
            Details = DetailList(("\uE946", eventType)),
            Actions = Combine(repoAction),
            UnsupportedTodos = Unsupported(activityEvent, $"Unsupported activity event type: {eventType}")
        };
    }

    private static string Actor(GitHubActivityEvent activityEvent) =>
        string.IsNullOrWhiteSpace(activityEvent.Actor.Login) ? "Someone" : activityEvent.Actor.Login;

    private static string BranchOrRepo(string branch) => string.IsNullOrWhiteSpace(branch) ? "the repository" : branch;

    private static string BranchName(string? gitRef)
    {
        if (string.IsNullOrWhiteSpace(gitRef))
        {
            return string.Empty;
        }

        const string headsPrefix = "refs/heads/";
        const string tagsPrefix = "refs/tags/";
        if (gitRef.StartsWith(headsPrefix, StringComparison.Ordinal))
        {
            return gitRef[headsPrefix.Length..];
        }

        if (gitRef.StartsWith(tagsPrefix, StringComparison.Ordinal))
        {
            return gitRef[tagsPrefix.Length..];
        }

        int slashIndex = gitRef.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex < gitRef.Length - 1 ? gitRef[(slashIndex + 1)..] : gitRef;
    }

    private static string FirstLine(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string[] lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? fallback : lines[0];
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? string.Empty : sha[..Math.Min(7, sha.Length)];

    private static string NumberSuffix(int number) => number > 0 ? $" #{number}" : string.Empty;

    private static string SafeLower(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static string PastTense(string? action, string fallback)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return fallback;
        }

        return action.Trim().ToLowerInvariant() switch
        {
            "open" or "opened" => "opened",
            "close" or "closed" => "closed",
            "reopen" or "reopened" => "reopened",
            "merge" or "merged" => "merged",
            "create" or "created" => "created",
            "delete" or "deleted" => "deleted",
            "edit" or "edited" => "edited",
            "update" or "updated" => "updated",
            "dismiss" or "dismissed" => "dismissed",
            "start" or "started" => "starred",
            "fork" or "forked" => "forked",
            "add" or "added" => "added",
            "labeled" => "labeled",
            "unlabeled" => "unlabeled",
            "assigned" => "assigned",
            "unassigned" => "unassigned",
            "published" => "published",
            _ => action.Trim()
        };
    }

    private static string HumanizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Unknown activity";
        }

        string trimmed = type.EndsWith("Event", StringComparison.Ordinal) ? type[..^5] : type;
        List<char> chars = [];
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (i > 0 && char.IsUpper(trimmed[i]) && !char.IsWhiteSpace(trimmed[i - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(trimmed[i]);
        }

        return new string(chars.ToArray());
    }

    private static string IssueGlyph(string? action) => action?.ToLowerInvariant() switch
    {
        "opened" or "reopened" => "\uE930",
        "closed" => "\uE73E",
        _ => "\uE946"
    };

    private static ActivityCardTone IssueTone(string? action) => action?.ToLowerInvariant() switch
    {
        "opened" or "reopened" => ActivityCardTone.Success,
        "closed" => ActivityCardTone.Danger,
        _ => ActivityCardTone.Accent
    };

    private static string PullRequestGlyph(string? action) => action?.ToLowerInvariant() switch
    {
        "opened" or "reopened" => "\uE8EE",
        "merged" => "\uE73E",
        "closed" => "\uE711",
        _ => "\uE8EE"
    };

    private static ActivityCardTone PullRequestTone(string? action, bool merged) =>
        merged || string.Equals(action, "merged", StringComparison.OrdinalIgnoreCase)
            ? ActivityCardTone.Purple
            : string.Equals(action, "closed", StringComparison.OrdinalIgnoreCase)
                ? ActivityCardTone.Danger
                : ActivityCardTone.Accent;

    private static IReadOnlyList<ActivityCardDetailViewModel> DetailList(params (string Glyph, string? Text)[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value.Text))
            .Select(value => new ActivityCardDetailViewModel { Glyph = value.Glyph, Text = value.Text! })
            .ToList();
    }

    private static IReadOnlyList<ActivityCardActionViewModel> Combine(params ActivityCardActionViewModel?[] actions) =>
        actions.Where(action => action is not null).Cast<ActivityCardActionViewModel>().ToList();

    private static IReadOnlyList<ActivitySentencePartViewModel> Sentence(params ActivitySentencePartViewModel?[] parts) =>
        parts.Where(part => part is not null && !string.IsNullOrEmpty(part.Text))
            .Cast<ActivitySentencePartViewModel>()
            .ToList();

    private static ActivitySentencePartViewModel Text(string text) => new()
    {
        Text = text
    };

    private static ActivitySentencePartViewModel Strong(string text) => new()
    {
        Text = text,
        IsEmphasized = true
    };

    private static ActivitySentencePartViewModel? Link(ActivityCardActionViewModel? action, string? labelOverride = null)
    {
        if (action is null)
        {
            return null;
        }

        return new ActivitySentencePartViewModel
        {
            Text = string.IsNullOrWhiteSpace(labelOverride) ? action.Label : labelOverride,
            Glyph = action.Glyph,
            Target = action.Target,
            Command = action.Command,
            IsEmphasized = true
        };
    }

    private static ActivityCardActionViewModel? RepoAction(
        GitHubActivityEvent activityEvent,
        ICommand? command,
        string label = "Repo",
        string? branch = null)
    {
        if (string.IsNullOrWhiteSpace(activityEvent.Repo.Name))
        {
            return null;
        }

        return new ActivityCardActionViewModel
        {
            Label = label,
            Glyph = "\uE8B7",
            Command = command,
            Target = new ActivityNavigationTarget
            {
                Kind = ActivityNavigationTargetKind.Repository,
                Label = label,
                RepositoryFullName = activityEvent.Repo.Name,
                Branch = branch,
                Repository = CreateRepository(activityEvent.Repo.Name, activityEvent.Repo.Id)
            }
        };
    }

    private static ActivityCardActionViewModel? RepositoryAction(GitHubRepository? repository, ICommand? command, string label)
    {
        string fullName = repository?.FullName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        return new ActivityCardActionViewModel
        {
            Label = label,
            Glyph = "\uE8B7",
            Command = command,
            Target = new ActivityNavigationTarget
            {
                Kind = ActivityNavigationTargetKind.Repository,
                Label = label,
                RepositoryFullName = fullName,
                Repository = repository
            }
        };
    }

    private static ActivityCardActionViewModel? IssueAction(GitHubActivityEvent activityEvent, ICommand? command, int number)
    {
        if (number <= 0 || string.IsNullOrWhiteSpace(activityEvent.Repo.Name))
        {
            return null;
        }

        string label = $"Issue #{number}";
        return new ActivityCardActionViewModel
        {
            Label = label,
            Glyph = "\uE946",
            Command = command,
            Target = new ActivityNavigationTarget
            {
                Kind = ActivityNavigationTargetKind.Issue,
                Label = label,
                RepositoryFullName = activityEvent.Repo.Name,
                Number = number,
                Repository = CreateRepository(activityEvent.Repo.Name, activityEvent.Repo.Id)
            }
        };
    }

    private static ActivityCardActionViewModel? PullRequestAction(GitHubActivityEvent activityEvent, ICommand? command, int number)
    {
        if (number <= 0 || string.IsNullOrWhiteSpace(activityEvent.Repo.Name))
        {
            return null;
        }

        string label = $"PR #{number}";
        return new ActivityCardActionViewModel
        {
            Label = label,
            Glyph = "\uE8EE",
            Command = command,
            Target = new ActivityNavigationTarget
            {
                Kind = ActivityNavigationTargetKind.PullRequest,
                Label = label,
                RepositoryFullName = activityEvent.Repo.Name,
                Number = number,
                Repository = CreateRepository(activityEvent.Repo.Name, activityEvent.Repo.Id)
            }
        };
    }

    private static ActivityCardActionViewModel? CommitAction(GitHubActivityEvent activityEvent, ICommand? command, string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(activityEvent.Repo.Name))
        {
            return null;
        }

        string label = $"Commit {ShortSha(sha)}";
        return new ActivityCardActionViewModel
        {
            Label = label,
            Glyph = "\uE930",
            Command = command,
            Target = new ActivityNavigationTarget
            {
                Kind = ActivityNavigationTargetKind.Commit,
                Label = label,
                RepositoryFullName = activityEvent.Repo.Name,
                Sha = sha,
                Repository = CreateRepository(activityEvent.Repo.Name, activityEvent.Repo.Id)
            }
        };
    }

    private static IReadOnlyList<ActivityNavigationTarget> Unsupported(GitHubActivityEvent activityEvent, string reason)
    {
        return
        [
            new ActivityNavigationTarget
            {
                Kind = ActivityNavigationTargetKind.UnsupportedTodo,
                Label = activityEvent.Type,
                RepositoryFullName = activityEvent.Repo.Name,
                UnsupportedReason = reason,
                Repository = CreateRepository(activityEvent.Repo.Name, activityEvent.Repo.Id)
            }
        ];
    }

    private static GitHubRepository CreateRepository(string fullName, long id)
    {
        string owner = string.Empty;
        string name = fullName;
        string[] parts = fullName.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            owner = parts[0];
            name = parts[1];
        }

        return new GitHubRepository
        {
            Id = id,
            Name = name,
            FullName = fullName,
            HtmlUrl = string.IsNullOrWhiteSpace(fullName) ? string.Empty : $"https://github.com/{fullName}",
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = string.IsNullOrWhiteSpace(owner) ? null : $"https://github.com/{owner}"
            }
        };
    }

    private sealed class ActivityDraft
    {
        public string Title { get; init; } = string.Empty;

        public string Subtitle { get; init; } = string.Empty;

        public string Glyph { get; init; } = "\uE8A7";

        public ActivityCardTone Tone { get; init; }

        public IReadOnlyList<ActivitySentencePartViewModel> SentenceParts { get; init; } = [];

        public IReadOnlyList<ActivityCardDetailViewModel> Details { get; init; } = [];

        public IReadOnlyList<ActivityCardActionViewModel> Actions { get; init; } = [];

        public IReadOnlyList<ActivityNavigationTarget> UnsupportedTodos { get; init; } = [];
    }
}
