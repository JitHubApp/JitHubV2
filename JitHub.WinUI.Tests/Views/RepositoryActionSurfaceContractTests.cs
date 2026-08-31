using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class RepositoryActionSurfaceContractTests
{
    [Fact]
    public void RepositoryActionControlsExposeStableAutomationSurfaces()
    {
        XDocument page = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoDetailPage.xaml"));
        Dictionary<string, XElement> byId = page.Descendants()
            .Where(element => GetAutomationId(element) is not null)
            .ToDictionary(
                element => GetAutomationId(element)!,
                StringComparer.Ordinal);

        foreach (string id in new[]
        {
            "RepoDetailBranchPicker",
            "RepoDetailBranchFlyoutRoot",
            "RepoDetailBranchSearchBox",
            "RepoDetailBranchList",
            "RepoDetailBranchStatus",
            "RepoDetailWatchButton",
            "RepoDetailStarButton",
            "RepoDetailForkButton",
            "RepoDetailCompactCommandsButton",
            "RepoDetailActionsMenuButton",
            "RepoDetailWatchMenuItem",
            "RepoDetailStarMenuItem",
            "RepoDetailForkMenuItem"
        })
        {
            Assert.Contains(id, byId);
        }

        Assert.Contains(
            "BranchStatusText",
            (string?)byId["RepoDetailBranchStatus"].Attribute("Text"),
            StringComparison.Ordinal);
        Assert.Contains(
            "StarActionLabel",
            (string?)byId["RepoDetailStarButton"].Attribute("AutomationProperties.HelpText"),
            StringComparison.Ordinal);
        Assert.Contains(
            "WatchActionLabel",
            (string?)byId["RepoDetailWatchButton"].Attribute("AutomationProperties.HelpText"),
            StringComparison.Ordinal);

    }

    [Fact]
    public void CompactRepositoryChromeUsesOneNamedOverflowForAllCommandGroups()
    {
        string pageCode = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoDetailPage.xaml.cs"));

        Assert.Contains("RepoDetailCompactRepositoryIdentity", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailCompactSectionCode", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailCompactBranchMenu", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailFrame.Content is RepoCodePage", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.Branches.Count > 0", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailCompactWatch", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailCompactStar", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailCompactFork", pageCode, StringComparison.Ordinal);
        Assert.Contains("IRepositoryCompactCommandProvider", pageCode, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutItem identity", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailIdentityStatusBadge.Visibility = compact ? Visibility.Collapsed", pageCode, StringComparison.Ordinal);
        Assert.Contains("RepoDetailIdentityChrome.Visibility = compact || !hideIdentity", pageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (Branch branch in ViewModel.Branches)", pageCode, StringComparison.Ordinal);

        string pageXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoDetailPage.xaml"));
        Assert.Contains("<ContentPresenter Content=\"{x:Bind ViewModel}\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind FilteredBranches", pageXaml, StringComparison.Ordinal);
        Assert.Contains("<ListView", pageXaml, StringComparison.Ordinal);
        Assert.Contains("<muxc:SelectorBar", pageXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<muxc:NavigationView", pageXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryNavigationCancelsBeforeWindowResourcesAreTornDown()
    {
        string root = FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "MainWindow.xaml.cs"));
        string page = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoDetailPage.xaml.cs"));
        string pageXaml = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoDetailPage.xaml"));

        Assert.Contains("public event EventHandler? ClosingRequested", window, StringComparison.Ordinal);
        Assert.Contains("ClosingRequested?.Invoke(this, EventArgs.Empty)", window, StringComparison.Ordinal);
        Assert.Contains("_mainWindow.ClosingRequested += MainWindow_ClosingRequested", page, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CancelPendingOperations()", page, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"Page_Loaded\"", pageXaml, StringComparison.Ordinal);
        Assert.Contains("Unloaded=\"Page_Unloaded\"", pageXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ForkNavigationGatesTitlePublicationOnCurrentRepositoryGeneration()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));
        int generationGate = viewModel.IndexOf(
            "_repositoryLoadCoordinator.IsCurrent(navigationGeneration)",
            StringComparison.Ordinal);
        int titleChange = viewModel.IndexOf(
            "_navigationService.ChangeTabTitle(newRepo.GetRepositoryFullName())",
            StringComparison.Ordinal);

        Assert.True(generationGate >= 0);
        Assert.True(titleChange > generationGate);
    }

    [Fact]
    public void RepositoryReadsPromoteStaleSnapshotsWithoutClearingVisibleState()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));

        Assert.Contains("PromoteRepositoryAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("PromoteAllBranchesAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("StartBranchPageNetworkRefresh", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (RepositoryQueryRefreshPolicy.ShouldPromote(result))", viewModel, StringComparison.Ordinal);
        Assert.Contains("PromoteStarStateAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("PromoteWatchStateAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("HandledFailureReporter.Report(ex, \"repository-branch-refresh\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("HandledFailureReporter.Report(ex, \"repository-star-refresh\")", viewModel, StringComparison.Ordinal);
        Assert.Contains("HandledFailureReporter.Report(ex, \"repository-watch-refresh\")", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Branches = merged", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalActionStateLoadFailuresStayOutOfTheProminentStatusSurface()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));
        Assert.Contains(
            "if (!GitHubAuthenticationConstants.IsPublicAccessToken(queryContext.AccessToken))",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("BackgroundTaskObserver.MarkFaultObserved(branchesTask)", viewModel, StringComparison.Ordinal);
        Assert.Contains("BackgroundTaskObserver.MarkFaultObserved(starredTask)", viewModel, StringComparison.Ordinal);
        Assert.Contains("BackgroundTaskObserver.MarkFaultObserved(watchingTask)", viewModel, StringComparison.Ordinal);
        int starLoad = viewModel.IndexOf(
            "CachedResult<GitHubResourceState> result = await starredTask;",
            StringComparison.Ordinal);
        int watchLoad = viewModel.IndexOf(
            "CachedResult<GitHubRepositorySubscription> result = await watchingTask;",
            starLoad,
            StringComparison.Ordinal);
        int loadBoundaryEnd = viewModel.IndexOf(
            "private long BeginRepositoryTransition(",
            watchLoad,
            StringComparison.Ordinal);

        Assert.True(starLoad >= 0 && watchLoad > starLoad && loadBoundaryEnd > watchLoad);
        string starFailureBoundary = viewModel[starLoad..watchLoad];
        string watchFailureBoundary = viewModel[watchLoad..loadBoundaryEnd];
        Assert.Contains(
            "HandledFailureReporter.Report(ex, \"repository-star-state-load\")",
            starFailureBoundary,
            StringComparison.Ordinal);
        Assert.Contains(
            "HandledFailureReporter.Report(ex, \"repository-watch-state-load\")",
            watchFailureBoundary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ShowActionStatus(", starFailureBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowActionStatus(", watchFailureBoundary, StringComparison.Ordinal);
    }

    [Fact]
    public void StarMutationCapturesPartitionBeforeRequestAndReconcilesCapturedLibrary()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));
        int capture = viewModel.IndexOf("RepositoryQueryContext queryContext = GetRepositoryQueryContext();", StringComparison.Ordinal);
        int remote = viewModel.IndexOf("_gitHubClientService.StarRepositoryAsync(", capture, StringComparison.Ordinal);
        int library = viewModel.IndexOf("_starLibraryService.NotifyRepositoryStarStateChangedAsync(", remote, StringComparison.Ordinal);

        Assert.True(capture >= 0);
        Assert.True(remote > capture);
        Assert.True(library > remote);
        Assert.Contains("queryContext.AccessToken", viewModel[library..], StringComparison.Ordinal);
        Assert.Contains("queryContext.UserId", viewModel[library..], StringComparison.Ordinal);

        int optimisticPublish = viewModel.IndexOf("ApplyStarDisplayState(", capture, StringComparison.Ordinal);
        int libraryProjection = viewModel.IndexOf(
            "GitHubRepository libraryRepository = CreateStarLibraryRepository(repository);",
            optimisticPublish,
            StringComparison.Ordinal);
        Assert.True(optimisticPublish > capture);
        Assert.True(libraryProjection > optimisticPublish);
    }

    [Fact]
    public void StarAndWatchMutationsDoNotEvictWholeRepositoryCaches()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));
        int starStart = viewModel.IndexOf("private async Task<bool> ToggleStar()", StringComparison.Ordinal);
        int watchStart = viewModel.IndexOf("private async Task<bool> ToggleWatch()", starStart, StringComparison.Ordinal);
        int ownershipStart = viewModel.IndexOf("private bool IsMutationUiCurrent(", watchStart, StringComparison.Ordinal);

        Assert.True(starStart >= 0 && watchStart > starStart && ownershipStart > watchStart);
        Assert.DoesNotContain("InvalidateRepositoryAsync", viewModel[starStart..watchStart], StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidateRepositoryAsync", viewModel[watchStart..ownershipStart], StringComparison.Ordinal);
        Assert.Contains("InvalidateStarStateAsync", viewModel[starStart..watchStart], StringComparison.Ordinal);
        Assert.Contains("InvalidateWatchStateAsync", viewModel[watchStart..ownershipStart], StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedForkAutomationStatusPublishesEvenWhenTransportCancels()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));
        int automationFork = viewModel.IndexOf(
            "created = await GitHubService.ForkRepo(",
            StringComparison.Ordinal);
        int finallyBlock = viewModel.IndexOf("finally", automationFork, StringComparison.Ordinal);
        int statusPublish = viewModel.IndexOf(
            "OnPropertyChanged(nameof(RepositoryAutomationStatusText));",
            finallyBlock,
            StringComparison.Ordinal);

        Assert.True(automationFork >= 0);
        Assert.True(finallyBlock > automationFork);
        Assert.True(statusPublish > finallyBlock);
    }

    private static string? GetAutomationId(XElement element) =>
        (string?)element.Attribute("AutomationProperties.AutomationId") ??
        (string?)element.Attribute("AutomationId");

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
