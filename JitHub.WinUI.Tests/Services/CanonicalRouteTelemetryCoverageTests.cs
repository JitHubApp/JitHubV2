using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CanonicalRouteTelemetryCoverageTests
{
    // This is a taxonomy inventory supplement. Behavioral outcome/fault tests live
    // with each route's view-model/service tests and are the release confidence gate.
    [Theory]
    [MemberData(nameof(ExistingRouteCoverage))]
    public void ExistingCanonicalRoute_RetainsExpectedTelemetryFamily(
        string relativePath,
        string[] expectedEvents)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath.Split('/')));
        string[] emitted = Regex.Matches(
                source,
                "\"(?<event>[a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\"",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["event"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.All(expectedEvents, expected => Assert.Contains(expected, emitted));
    }

    public static TheoryData<string, string[]> ExistingRouteCoverage => new()
    {
        {
            "JitHub.WinUI/ViewModels/Pages/ShellPageViewModel.cs",
            ["shell.command.opened", "shell.command.executed", "shell.route.opened", "shell.repo.selected"]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/DashboardPageViewModel.cs",
            [
                "dashboard.opened",
                "dashboard.refresh.started",
                "dashboard.refresh.completed",
                "dashboard.section.loaded",
                "dashboard.quick_action.executed",
                "dashboard.reconnect.clicked"
            ]
        },
        {
            "JitHub.WinUI/Services/Issues/IssueTelemetry.cs",
            [
                "issues.opened",
                "issues.list.loaded",
                "issues.selected",
                "issues.prefetch.started",
                "issues.prefetch.completed",
                "issues.action.executed"
            ]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/MePageModels.cs",
            [
                "issues.opened",
                "issues.list.loaded",
                "issues.selected",
                "issues.prefetch.started",
                "issues.prefetch.completed",
                "issues.action.executed",
                "pull_requests.opened",
                "pull_requests.list.loaded",
                "pull_requests.selected",
                "pull_requests.action.executed"
            ]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/NotificationsPageViewModel.cs",
            ["notifications.opened", "notifications.list.loaded", "notifications.filter.changed", "notifications.action.executed"]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/GistsPageViewModel.cs",
            ["gists.opened", "gists.list.loaded", "gists.filter.changed", "gists.action.executed"]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/RepoManagePageViewModel.cs",
            ["repositories.opened", "repositories.action.executed"]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/RepoSearchResultPageViewModel.cs",
            [
                "repository_search.opened",
                "repository_search.loaded",
                "repository_search.action.executed",
                "repository_search.error"
            ]
        },
        {
            "JitHub.WinUI/Services/Repositories/GitHubRepositoryIndexService.cs",
            ["repositories.sync.completed"]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/StarLibraryPageViewModel.cs",
            ["stars.filter.changed", "stars.sort.changed", "stars.action.executed"]
        },
        {
            "JitHub.WinUI/Services/Stars/GitHubStarLibraryService.cs",
            [
                "stars.sync.completed",
                "stars.category.created",
                "stars.category.updated",
                "stars.category.deleted",
                "stars.membership.changed"
            ]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/RepoPullRequestPageViewModel.cs",
            [
                "pull_requests.selected",
                "pull_requests.section.opened"
            ]
        },
        {
            "JitHub.WinUI/Services/PullRequests/PullRequestTelemetry.cs",
            [
                "pull_requests.prefetch.started",
                "pull_requests.prefetch.completed"
            ]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/RepoCommitsPageViewModel.cs",
            [
                "commits.opened",
                "commits.list.loaded",
                "commits.selected",
                "commits.filter.changed",
                "commits.section.opened",
                "commits.compare.opened"
            ]
        },
        {
            "JitHub.WinUI/ViewModels/CodeViewer/RepoCodePageViewModel.cs",
            [
                "repo_code.opened",
                "repo_code.loaded",
                "repo_code.selected",
                "repo_code.action.executed",
                "repo_code.error",
                "repo_code.cache.observed",
                "repo_code.duration.recorded"
            ]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/ProfilePageViewModel.cs",
            ["profile.opened", "profile.loaded", "profile.section.opened", "profile.action.executed", "profile.error"]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/SettingsPageViewModel.cs",
            ["settings.opened", "settings.loaded", "settings.action.executed", "settings.error"]
        },
        {
            "JitHub.WinUI/ViewModels/Pages/LoginPageViewModel.cs",
            ["auth.opened", "auth.action.executed"]
        },
        {
            "JitHub.WinUI/Services/AuthService.cs",
            ["auth.flow.started", "auth.flow.completed", "auth.session.loaded", "auth.action.executed", "auth.error"]
        }
    };

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
