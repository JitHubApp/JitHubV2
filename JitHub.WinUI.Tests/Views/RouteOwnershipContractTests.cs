using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class RouteOwnershipContractTests
{
    [Fact]
    public void CanonicalRouteMapNamesEveryProductionWorkspace()
    {
        string root = FindRepositoryRoot();
        string routeMap = File.ReadAllText(Path.Combine(root, "docs", "jithub-vnext-route-ownership.md"));

        foreach (string page in new[]
        {
            "Views.Pages.ShellPage",
            "Views.Pages.LoginPage",
            "DashboardPage",
            "MyIssuesPage",
            "MyPullRequestsPage",
            "StarsPage",
            "GistsPage",
            "NotificationsPage",
            "ProfilePage",
            "RepoManagePage",
            "RepoDetailPage",
            "RepoCodePage",
            "RepoIssuePage",
            "RepoPullRequestPage",
            "RepoCommitsPage",
            "RepoSearchResultPage",
            "SettingsPage"
        })
        {
            Assert.Contains(page, routeMap, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RetiredDuplicateDetailStacksStayDeleted()
    {
        string root = FindRepositoryRoot();
        foreach (string relativePath in new[]
        {
            "JitHub.WinUI/Converters/Issues/IssueModelToIssueDetailViewModelConverter.cs",
            "JitHub.WinUI/Converters/PullRequests/PullRequestModelToPullRequestDetailViewModelConverter.cs",
            "JitHub.WinUI/Views/Pages/IssuePage/IssueDetailPage.xaml",
            "JitHub.WinUI/Views/Pages/PullRequestConversationPage.xaml",
            "JitHub.WinUI/Views/Pages/PullRequestCommitsPage.xaml",
            "JitHub.WinUI/Views/Pages/RepoCommitDetailPage.xaml",
            "JitHub.WinUI/Views/Pages/RepoDetailCodePage.xaml",
            "JitHub.WinUI/ViewModels/ShellViewModel.cs",
            "JitHub.WinUI/ViewModels/RepositoryViewModels/RepoSearchResultViewModel.cs",
            "JitHub.WinUI/ViewModels/IssueViewModels/RepoIssueViewModel.cs",
            "JitHub.WinUI/ViewModels/IssueViewModels/RepoIssueDetailViewModel.cs",
            "JitHub.WinUI/ViewModels/IssueViewModels/RepoIssuePostingViewModel.cs",
            "JitHub.WinUI/ViewModels/PullRequestViewModels/RepoPullRequestViewModel.cs",
            "JitHub.WinUI/ViewModels/PullRequestViewModels/RepoPullRequestDetailViewModel.cs",
            "JitHub.WinUI/ViewModels/PullRequestViewModels/PullRequestConversationViewModel.cs",
            "JitHub.WinUI/ViewModels/PullRequestViewModels/PullRequestCommitsViewModel.cs",
            "JitHub.WinUI/ViewModels/CommitViewModels/RepoCommitsViewModel.cs",
            "JitHub.WinUI/ViewModels/CommitViewModels/CommitDetailViewModel.cs",
            "JitHub.WinUI/Views/Controls/Issue/RepoIssueDetail.xaml",
            "JitHub.WinUI/Views/Controls/PullRequest/RepoPullRequestDetail.xaml",
            "JitHub.WinUI/Views/Controls/Commit/CommitDetail.xaml",
            "JitHub.WinUI/Views/Controls/Commit/CommitListDetailsItemPresenter.xaml",
            "JitHub.WinUI/Views/Controls/Commit/CommitListDetailsDetailPresenter.xaml",
            "JitHub.WinUI/Services/CommandService.cs"
        })
        {
            Assert.False(File.Exists(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))), relativePath);
        }
    }

    [Fact]
    public void RetiredDuplicateDependencyIslandsStayDeletedAndUnregistered()
    {
        string root = FindRepositoryRoot();
        foreach (string relativePath in new[]
        {
            "JitHub.WinUI/Views/LoginPage.xaml",
            "JitHub.WinUI/Views/RepoListPage.xaml",
            "JitHub.WinUI/ViewModels/LoginViewModel.cs",
            "JitHub.WinUI/ViewModels/DashboardViewModel.cs",
            "JitHub.WinUI/ViewModels/SettingsViewModel.cs",
            "JitHub.WinUI/ViewModels/Pages/RepoDetailPageViewModel.cs",
            "JitHub.WinUI/Views/Controls/RepoSideBar.xaml",
            "JitHub.WinUI/ViewModels/RepositoryViewModels/RepoSideBarViewModel.cs",
            "JitHub.WinUI/ViewModels/RepositoryViewModels/RepoManageViewModel.cs",
            "JitHub.WinUI/Views/Controls/Issue/UserIssueList.xaml",
            "JitHub.WinUI/ViewModels/IssueViewModels/UserIssueListViewModel.cs",
            "JitHub.WinUI/Views/Controls/Code/CodeButton.xaml",
            "JitHub.WinUI/Views/Controls/Issue/IssueButton.xaml",
            "JitHub.WinUI/Views/Controls/PullRequest/PullRequestButton.xaml",
            "JitHub.WinUI/Views/Controls/Commit/CommitButton.xaml",
            "JitHub.WinUI/Views/Controls/PullRequest/PullRequestForm.xaml",
            "JitHub.WinUI/ViewModels/PullRequestViewModels/RepoPullRequestPostingViewModel.cs",
            "JitHub.WinUI/Views/Controls/Commit/FileDiff.xaml",
            "JitHub.WinUI/ViewModels/FileDiffViewModel.cs"
        })
        {
            Assert.False(File.Exists(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))), relativePath);
        }

        string registrations = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "App.xaml.cs"));
        Assert.DoesNotContain("AddTransient<RepoDetailPageViewModel>", registrations, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<ICommandService", registrations, StringComparison.Ordinal);
    }

    [Fact]
    public void DeveloperCommandsAreGuardedAtTheirExecutionBoundary()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        int designMethod = source.IndexOf("public void GoToDesignLabPage()", StringComparison.Ordinal);
        int consoleMethod = source.IndexOf("public void OnOpenDevConsole()", StringComparison.Ordinal);
        Assert.True(designMethod >= 0);
        Assert.True(consoleMethod >= 0);
        Assert.Contains("DeveloperRoutePolicy.CanOpenDesignLab", source[designMethod..consoleMethod], StringComparison.Ordinal);
        Assert.Contains("DeveloperRoutePolicy.CanOpenDevConsole", source[consoleMethod..], StringComparison.Ordinal);
        Assert.Contains("if (GlobalViewModel.DevMode)", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
