using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.Activities;
using JitHub.Models.GitHub;

namespace JitHub.WinUI.ViewModels.Activities;

public static class ActivityMockData
{
    private static readonly ICommand NoOpCommand = new RelayCommand<ActivityNavigationTarget>(_ => { });

    public static List<ActivityCardViewModel> CreateCards(ICommand? command = null)
    {
        command ??= NoOpCommand;
        List<GitHubActivityEvent> events =
        [
            Event("PushEvent", Serialize(new PushEventPayload
            {
                Ref = "refs/heads/main",
                Head = "6f3c99a4ce8ad11a2ef473f9feef7faec605c50d",
                Commits =
                [
                    new GitHubActivityPushCommit { Sha = "6f3c99a4ce8ad11a2ef473f9feef7faec605c50d", Message = "Polish activity cards and chip navigation" },
                    new GitHubActivityPushCommit { Sha = "1bc0fef88d3e8b71f32ef7a7b826ded91ec9e002", Message = "Add typed GitHub event payloads" }
                ]
            }, GitHubJsonSerializerContext.Default.PushEventPayload), 0),
            Event("PushEvent", Serialize(new PushEventPayload
            {
                Ref = "refs/heads/empty-payload-guard",
                Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Commits = []
            }, GitHubJsonSerializerContext.Default.PushEventPayload), 18),
            Event("CommitCommentEvent", Serialize(new CommitCommentEventPayload
            {
                Action = "created",
                Comment = new GitHubCommitComment
                {
                    CommitId = "1bc0fef88d3e8b71f32ef7a7b826ded91ec9e002",
                    Path = "src/JitHub/App.xaml.cs",
                    Line = 42,
                    Body = "This initialization path is much safer now."
                }
            }, GitHubJsonSerializerContext.Default.CommitCommentEventPayload), 1),
            Event("CreateEvent", Serialize(new CreateEventPayload
            {
                Ref = "feature/activity-overhaul",
                RefType = "branch",
                FullRef = "refs/heads/feature/activity-overhaul",
                MasterBranch = "main",
                Description = "Native GitHub client for Windows."
            }, GitHubJsonSerializerContext.Default.CreateEventPayload), 2),
            Event("DeleteEvent", Serialize(new DeleteEventPayload
            {
                Ref = "old-dashboard-feed",
                RefType = "branch",
                FullRef = "refs/heads/old-dashboard-feed"
            }, GitHubJsonSerializerContext.Default.DeleteEventPayload), 3),
            Event("DiscussionEvent", Serialize(new DiscussionEventPayload
            {
                Action = "created",
                Discussion = new GitHubActivityDiscussion { Number = 8, Title = "Should activity cards support project events?" }
            }, GitHubJsonSerializerContext.Default.DiscussionEventPayload), 4),
            Event("ForkEvent", Serialize(new ForkEventPayload
            {
                Action = "forked",
                Forkee = Repository("jithub-labs", "JitHubV2")
            }, GitHubJsonSerializerContext.Default.ForkEventPayload), 5),
            Event("GollumEvent", Serialize(new GollumEventPayload
            {
                Pages =
                [
                    new GitHubActivityWikiPage { Title = "Activity design notes", PageName = "Activity-design-notes", Action = "edited" },
                    new GitHubActivityWikiPage { Title = "Release checklist", PageName = "Release-checklist", Action = "created" }
                ]
            }, GitHubJsonSerializerContext.Default.GollumEventPayload), 6),
            Event("IssueCommentEvent", Serialize(new IssueCommentEventPayload
            {
                Action = "created",
                Issue = Issue(128, "Home feed should show meaningful GitHub activity"),
                Comment = new GitHubIssueComment { Body = "The new card actions should stay inside JitHub." }
            }, GitHubJsonSerializerContext.Default.IssueCommentEventPayload), 7),
            Event("IssuesEvent", Serialize(new IssuesEventPayload
            {
                Action = "opened",
                Issue = Issue(133, "Activity chips need keyboard focus states"),
                Label = new GitHubLabel { Name = "accessibility", Color = "7BB99C" }
            }, GitHubJsonSerializerContext.Default.IssuesEventPayload), 8),
            Event("MemberEvent", Serialize(new MemberEventPayload
            {
                Action = "added",
                Member = new GitHubActor { Login = "xueyang-song", AvatarUrl = "https://avatars.githubusercontent.com/u/1?v=4" }
            }, GitHubJsonSerializerContext.Default.MemberEventPayload), 9),
            Event("PublicEvent", Serialize(new PublicEventPayload(), GitHubJsonSerializerContext.Default.PublicEventPayload), 10),
            Event("PullRequestEvent", Serialize(new PullRequestEventPayload
            {
                Action = "merged",
                Number = 72,
                PullRequest = PullRequest(72, "Replace browser-launch activity cards with native actions", merged: true)
            }, GitHubJsonSerializerContext.Default.PullRequestEventPayload), 11),
            Event("PullRequestReviewEvent", Serialize(new PullRequestReviewEventPayload
            {
                Action = "created",
                PullRequest = PullRequest(74, "Add mock fixtures for every GitHub activity event"),
                Review = new GitHubPullRequestReview { State = "approved", CommitId = "6f3c99a4ce8ad11a2ef473f9feef7faec605c50d" }
            }, GitHubJsonSerializerContext.Default.PullRequestReviewEventPayload), 12),
            Event("PullRequestReviewCommentEvent", Serialize(new PullRequestReviewCommentEventPayload
            {
                Action = "created",
                PullRequest = PullRequest(75, "Tighten activity card spacing"),
                Comment = new GitHubPullRequestReviewComment
                {
                    Body = "This should use the smaller app radius token.",
                    Path = "JitHub.WinUI/Views/Controls/App/ActivityCard.xaml",
                    Position = 18,
                    CommitId = "6f3c99a4ce8ad11a2ef473f9feef7faec605c50d"
                }
            }, GitHubJsonSerializerContext.Default.PullRequestReviewCommentEventPayload), 13),
            Event("ReleaseEvent", Serialize(new ReleaseEventPayload
            {
                Action = "published",
                Release = new GitHubActivityRelease { Name = "JitHub Preview 2026.5", TagName = "v2026.5-preview" }
            }, GitHubJsonSerializerContext.Default.ReleaseEventPayload), 14),
            Event("WatchEvent", Serialize(new WatchEventPayload { Action = "started" }, GitHubJsonSerializerContext.Default.WatchEventPayload), 15),
            Event("FutureEvent", JsonDocument.Parse("{\"note\":\"GitHub added a new event type\"}").RootElement.Clone(), 16),
            Event("IssuesEvent", JsonDocument.Parse("{\"action\":\"opened\"}").RootElement.Clone(), 17)
        ];

        return ActivityCardViewModelFactory.CreateMany(events, command);
    }

    private static GitHubActivityEvent Event(string type, JsonElement payload, int offset)
    {
        return new GitHubActivityEvent
        {
            Id = $"mock-{offset:D2}",
            Type = type,
            Public = true,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-offset * 13),
            Actor = new GitHubActor
            {
                Id = 100 + offset,
                Login = offset % 2 == 0 ? "nerocui" : "codex-bot",
                AvatarUrl = "https://avatars.githubusercontent.com/u/583231?v=4"
            },
            Repo = new GitHubActivityRepository
            {
                Id = 42,
                Name = "JitHubApp/JitHubV2",
                Url = "https://api.github.com/repos/JitHubApp/JitHubV2"
            },
            Payload = payload
        };
    }

    private static JsonElement Serialize<T>(T payload, JsonTypeInfo<T> jsonTypeInfo) =>
        JsonSerializer.SerializeToElement(payload, jsonTypeInfo);

    private static GitHubRepository Repository(string owner, string name)
    {
        return new GitHubRepository
        {
            Id = 500,
            Name = name,
            FullName = $"{owner}/{name}",
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = $"https://github.com/{owner}"
            }
        };
    }

    private static GitHubIssue Issue(int number, string title)
    {
        return new GitHubIssue
        {
            Id = 200 + number,
            Number = number,
            Title = title,
            State = "open",
            CreatedAt = DateTimeOffset.Now.AddDays(-2),
            UpdatedAt = DateTimeOffset.Now.AddHours(-3),
            User = new GitHubActor { Login = "nerocui" }
        };
    }

    private static GitHubPullRequest PullRequest(int number, string title, bool merged = false)
    {
        return new GitHubPullRequest
        {
            Id = 300 + number,
            Number = number,
            Title = title,
            State = merged ? "closed" : "open",
            Merged = merged,
            CreatedAt = DateTimeOffset.Now.AddDays(-1),
            UpdatedAt = DateTimeOffset.Now.AddHours(-2),
            User = new GitHubActor { Login = "codex-bot" },
            Head = new GitHubPullRequestBranch { GitRef = "activity-overhaul", Label = "codex-bot:activity-overhaul" },
            Base = new GitHubPullRequestBranch { GitRef = "main", Label = "JitHubApp:main" }
        };
    }
}
