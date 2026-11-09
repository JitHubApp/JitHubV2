using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public static class ProductPerformanceLargeAccountFixture
{
    public const string Scenario = "performance-large-account";
    public const int RepositoryCount = 600;
    public const int WorkItemCount = 300;
    public const int StarCount = 500;
    public const int NotificationCount = 300;
    public const int ActivityCount = 250;
    public const int PeopleCount = 250;
    public const int GistCount = 500;
    public const int CommitCount = 300;
    public const int StandardRouteItemCount = 120;

    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Languages = ["C#", "TypeScript", "C++", "Rust", "Python", "Dart"];

    public static bool IsBenchmarkEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JITHUB_PERFORMANCE_FIXTURE")) &&
        AppDataPathPolicy.TryGetAutomationRoots(out _, out _);

    public static bool IsEnabled =>
        IsBenchmarkEnabled &&
        string.Equals(
            Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO"),
            Scenario,
            StringComparison.OrdinalIgnoreCase);

    public static int BenchmarkItemCount(int largeAccountCount) =>
        IsEnabled ? largeAccountCount : StandardRouteItemCount;

    public static GitHubRepository[] CreateRepositories(int count = RepositoryCount, string owner = "performance-owner") =>
        Enumerable.Range(1, Math.Max(0, count))
            .Select(index => CreateRepository(index, owner))
            .ToArray();

    public static GitHubStarredRepository[] CreateStars(int count = StarCount) =>
        CreateRepositories(count)
            .Select((repository, index) => new GitHubStarredRepository
            {
                Repository = repository,
                StarredAt = Anchor.AddMinutes(-index)
            })
            .ToArray();

    public static GitHubIssue[] CreateIssues(
        string owner,
        string repository,
        bool pullRequests,
        int count = WorkItemCount) =>
        Enumerable.Range(1, Math.Max(0, count))
            .Select(index => new GitHubIssue
            {
                Id = 2_000_000L + index + (pullRequests ? 1_000_000L : 0),
                Number = index,
                Title = $"Performance {(pullRequests ? "pull request" : "issue")} {index:D4}",
                Body = "Deterministic large-account content used by the JitHub performance gate.",
                State = index % 5 == 0 ? "closed" : "open",
                HtmlUrl = $"https://github.com/{owner}/{repository}/{(pullRequests ? "pull" : "issues")}/{index}",
                RepositoryUrl = $"https://api.github.com/repos/{owner}/{repository}",
                Comments = index % 12,
                CreatedAt = Anchor.AddDays(-index),
                UpdatedAt = Anchor.AddMinutes(-index),
                User = CreateActor(index),
                Assignees = [CreateActor(index % 20 + 1)],
                PullRequest = pullRequests
                    ? new GitHubIssuePullRequestMarker
                    {
                        HtmlUrl = $"https://github.com/{owner}/{repository}/pull/{index}"
                    }
                    : null
            })
            .ToArray();

    public static GitHubPullRequest[] CreatePullRequests(
        string owner,
        string repository,
        int count = WorkItemCount) =>
        Enumerable.Range(1, Math.Max(0, count))
            .Select(index => new GitHubPullRequest
            {
                Id = 4_000_000L + index,
                Number = index,
                Title = $"Performance pull request {index:D4}",
                Body = "Deterministic pull request body for responsive workspace benchmarking.",
                State = index % 5 == 0 ? "closed" : "open",
                HtmlUrl = $"https://github.com/{owner}/{repository}/pull/{index}",
                Comments = index % 10,
                Draft = index % 11 == 0,
                CreatedAt = Anchor.AddDays(-index),
                UpdatedAt = Anchor.AddMinutes(-index),
                User = CreateActor(index),
                Head = new GitHubPullRequestBranch { Label = $"{owner}:perf-{index:D4}", GitRef = $"perf-{index:D4}" },
                Base = new GitHubPullRequestBranch { Label = $"{owner}:main", GitRef = "main" }
            })
            .ToArray();

    public static GitHubNotificationThread[] CreateNotifications(int count = NotificationCount)
    {
        GitHubRepository[] repositories = CreateRepositories(Math.Max(1, Math.Min(count, 40)));
        return Enumerable.Range(1, Math.Max(0, count))
            .Select(index =>
            {
                GitHubRepository repository = repositories[(index - 1) % repositories.Length];
                bool pullRequest = index % 3 == 0;
                return new GitHubNotificationThread
                {
                    Id = $"performance-notification-{index:D4}",
                    Url = $"https://api.github.com/notifications/threads/{index}",
                    SubscriptionUrl = $"https://api.github.com/notifications/threads/{index}/subscription",
                    Unread = index % 4 != 0,
                    Reason = pullRequest ? "review_requested" : "mention",
                    UpdatedAt = Anchor.AddMinutes(-index),
                    Repository = repository,
                    Subject = new GitHubNotificationSubject
                    {
                        Title = $"Performance notification {index:D4}",
                        Type = pullRequest ? "PullRequest" : "Issue",
                        Url = $"https://api.github.com/repos/{repository.FullName}/{(pullRequest ? "pulls" : "issues")}/{index}"
                    }
                };
            })
            .ToArray();
    }

    public static GitHubActivityEvent[] CreateActivity(int count = ActivityCount)
    {
        GitHubRepository[] repositories = CreateRepositories(Math.Max(1, Math.Min(count, 40)));
        using JsonDocument payloadDocument = JsonDocument.Parse("{\"ref\":\"refs/heads/main\",\"commits\":[{\"sha\":\"0000001\"}]}");
        JsonElement payload = payloadDocument.RootElement.Clone();
        return Enumerable.Range(1, Math.Max(0, count))
            .Select(index =>
            {
                GitHubRepository repository = repositories[(index - 1) % repositories.Length];
                return new GitHubActivityEvent
                {
                    Id = $"performance-event-{index:D4}",
                    Type = index % 5 == 0 ? "WatchEvent" : "PushEvent",
                    Public = true,
                    CreatedAt = Anchor.AddMinutes(-index),
                    Actor = CreateActor(index),
                    Repo = new GitHubActivityRepository
                    {
                        Id = repository.Id,
                        Name = repository.FullName,
                        Url = $"https://api.github.com/repos/{repository.FullName}"
                    },
                    Payload = payload
                };
            })
            .ToArray();
    }

    public static GitHubUser[] CreatePeople(int count = PeopleCount) =>
        Enumerable.Range(1, Math.Max(0, count))
            .Select(index => new GitHubUser
            {
                Id = 8_000_000L + index,
                Login = $"performance-user-{index:D4}",
                Name = $"Performance User {index:D4}",
                AvatarUrl = "ms-appx:///Assets/Octocat.png",
                HtmlUrl = $"https://github.com/performance-user-{index:D4}",
                Type = "User"
            })
            .ToArray();

    public static GitHubCommit[] CreateCommits(int count = CommitCount) =>
        Enumerable.Range(1, Math.Max(0, count))
            .Select(index =>
            {
                string sha = index.ToString("x40");
                GitHubActor actor = CreateActor(index);
                DateTimeOffset date = Anchor.AddMinutes(-index);
                return new GitHubCommit
                {
                    Sha = sha,
                    HtmlUrl = $"https://github.com/performance-owner/performance-repo/commit/{sha}",
                    Author = actor,
                    Committer = actor,
                    Commit = new GitHubCommitInfo
                    {
                        Message = $"Performance commit {index:D4}",
                        Author = new GitHubCommitSignature { Name = actor.Login, Date = date },
                        Committer = new GitHubCommitSignature { Name = actor.Login, Date = date },
                        Verification = new GitHubCommitVerification { Verified = index % 3 != 0, Reason = "valid" }
                    }
                };
            })
            .ToArray();

    public static GitHubContributionCalendar CreateContributionCalendar()
    {
        GitHubContributionWeek[] weeks = Enumerable.Range(0, 53)
            .Select(week => new GitHubContributionWeek(
                Enumerable.Range(0, 7)
                    .Select(day =>
                    {
                        int count = (week * 7 + day) % 18;
                        return new GitHubContributionDay(
                            Anchor.AddDays((week * 7) + day - 370),
                            count,
                            count switch
                            {
                                0 => "#1f2a22",
                                <= 4 => "#0e4429",
                                <= 9 => "#006d32",
                                <= 14 => "#26a641",
                                _ => "#39d353"
                            },
                            day);
                    })
                    .ToArray()))
            .ToArray();
        return new GitHubContributionCalendar(
            weeks.Sum(static week => week.Days.Sum(static day => day.ContributionCount)),
            weeks);
    }

    private static GitHubRepository CreateRepository(int index, string owner)
    {
        string name = $"performance-repository-{index:D4}";
        DateTimeOffset updated = Anchor.AddMinutes(-index);
        return new GitHubRepository
        {
            Id = 1_000_000L + index,
            Name = name,
            FullName = $"{owner}/{name}",
            Description = $"Deterministic repository {index:D4} for large-account performance coverage.",
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Private = index % 9 == 0,
            Fork = index % 7 == 0,
            Archived = index % 23 == 0,
            StargazersCount = 50_000 - Math.Min(49_000, index * 37),
            ForksCount = index * 3,
            OpenIssuesCount = index % 120,
            Language = Languages[(index - 1) % Languages.Length],
            UpdatedAt = updated,
            PushedAt = updated,
            Visibility = index % 9 == 0 ? "private" : "public",
            Topics = ["performance", "developer-tools", $"fixture-{index % 12:D2}"],
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                AvatarUrl = "ms-appx:///Assets/Octocat.png",
                HtmlUrl = $"https://github.com/{owner}"
            }
        };
    }

    private static GitHubActor CreateActor(int index) => new()
    {
        Id = 9_000_000L + index,
        Login = $"performance-user-{index % PeopleCount + 1:D4}",
        AvatarUrl = "ms-appx:///Assets/Octocat.png",
        HtmlUrl = $"https://github.com/performance-user-{index % PeopleCount + 1:D4}"
    };
}
