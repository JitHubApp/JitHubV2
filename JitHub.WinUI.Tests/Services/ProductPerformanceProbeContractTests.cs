using Xunit;
using System.Text.RegularExpressions;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceProbeContractTests
{
    [Fact]
    public void RepositoryTraversal_PaintsChildBeforeStartingAncillaryPromotion()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));
        int codeNavigation = source.IndexOf(
            "GoToCodePage(ResolveInitialCodeViewerArg",
            StringComparison.Ordinal);
        int promotion = source.IndexOf(
            "_ = PromoteRepositoryAfterFirstFrameAsync(",
            codeNavigation,
            StringComparison.Ordinal);

        Assert.True(codeNavigation >= 0);
        Assert.True(promotion > codeNavigation);
        Assert.Contains("ancillary to navigation", source, StringComparison.Ordinal);
        Assert.Contains(
            "await Task.Delay(TimeSpan.FromMilliseconds(34), cancellationToken);",
            source,
            StringComparison.Ordinal);

        string starsSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "StarLibraryPageViewModel.cs"));
        Assert.Contains(
            "StarLibraryPage page = await _libraryService.LoadCachedPageAsync(",
            starsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Task.Run(\n                () => _libraryService.LoadCachedPageAsync",
            starsSource.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CachedNavigation_AvoidsUnownedTreeScansAndEagerHeavyControls()
    {
        string root = FindRepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml.cs"));
        int captureStart = shell.IndexOf("private ShellRouteViewState? CaptureCurrentRouteViewState()", StringComparison.Ordinal);
        int captureEnd = shell.IndexOf("private void RestoreRouteViewState", captureStart, StringComparison.Ordinal);
        Assert.True(captureStart >= 0 && captureEnd > captureStart);
        string capture = shell[captureStart..captureEnd];
        Assert.Contains("ViewModel.CurrentRoutePage, \"settings\"", capture, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CurrentRoutePage, \"home\"", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("?? FindDescendantByAutomationId", capture, StringComparison.Ordinal);

        string stars = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "StarsPage.xaml"));
        string starsCode = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "StarsPage.xaml.cs"));
        Assert.Contains("NavigationCacheMode = NavigationCacheMode.Required", starsCode, StringComparison.Ordinal);
        Assert.DoesNotContain("common:CachedImage", stars, StringComparison.Ordinal);

        foreach (string cachedWorkspace in new[]
        {
            "MyIssuesPage.xaml.cs",
            "MyPullRequestsPage.xaml.cs",
            "NotificationsPage.xaml.cs",
            "RepoManagePage.xaml.cs"
        })
        {
            string source = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", cachedWorkspace));
            Assert.Contains("NavigationCacheMode.Required", source, StringComparison.Ordinal);
        }

        string notificationsCode = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "NotificationsPage.xaml.cs"));
        int notificationClickStart = notificationsCode.IndexOf(
            "private void NotificationsList_ItemClick",
            StringComparison.Ordinal);
        int notificationClickEnd = notificationsCode.IndexOf(
            "private void NotificationsList_ContainerContentChanging",
            notificationClickStart,
            StringComparison.Ordinal);
        Assert.True(notificationClickStart >= 0 && notificationClickEnd > notificationClickStart);
        string notificationClick = notificationsCode[notificationClickStart..notificationClickEnd];
        Assert.Contains("OpenNotificationItem(item)", notificationClick, StringComparison.Ordinal);
        Assert.DoesNotContain("PromoteDestinationPrefetchAsync(item)", notificationClick, StringComparison.Ordinal);
        Assert.Contains("new PointerEventHandler(NotificationRow_PointerPressed)", notificationsCode, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", notificationsCode, StringComparison.Ordinal);
        Assert.Contains("PointerUpdateKind.LeftButtonPressed", notificationsCode, StringComparison.Ordinal);
        Assert.Contains("PointerUpdateKind.LeftButtonReleased", notificationsCode, StringComparison.Ordinal);
        Assert.Contains("FindAncestorButton", notificationsCode, StringComparison.Ordinal);

        string notificationsViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "NotificationsPageViewModel.cs"));
        Assert.Contains("TimeSpan.FromMilliseconds(150)", notificationsViewModel, StringComparison.Ordinal);
        Assert.Contains("token => MarkReadAfterOpenAsync(item, token)", notificationsViewModel, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)", notificationsViewModel, StringComparison.Ordinal);

        string issueNavigationArgs = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Models",
            "NavArgs",
            "IssueNavArg.cs"));
        Assert.Contains("GitHubIssue? NavigationPreview", issueNavigationArgs, StringComparison.Ordinal);

        string shellViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));
        Assert.Contains("NavigationPreview = new GitHubIssue", shellViewModel, StringComparison.Ordinal);

        string issueViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoIssuePageViewModel.cs"));
        int previewBranch = issueViewModel.IndexOf(
            "navArg.NavigationPreview is GitHubIssue notificationPreview",
            StringComparison.Ordinal);
        int fullSnapshotLookup = issueViewModel.IndexOf(
            "bool hasNavigationSnapshot = TryApplyNavigationSnapshot",
            StringComparison.Ordinal);
        Assert.True(previewBranch >= 0 && fullSnapshotLookup > previewBranch);

        string commitsCode = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Commit",
            "CommitDiffViewer.xaml.cs"));
        Assert.Contains("ProductPerformanceScrollProbe.TryStart(this, DiffRowsScrollViewer)", commitsCode, StringComparison.Ordinal);

        string commitsPage = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml"));
        string commitsPageCode = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml.cs"));
        Assert.Contains("SelectionChanged=\"CommitsList_SelectionChanged\"", commitsPage, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", commitsPage, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", commitsPageCode, StringComparison.Ordinal);
        Assert.Contains("new PointerEventHandler(CommitListItem_PointerPressed)", commitsPageCode, StringComparison.Ordinal);
        Assert.Contains("CommitsList.SelectedItem = commit", commitsPageCode, StringComparison.Ordinal);
        Assert.Contains("ProductPerformanceReadiness.CommitTraversal(\"repo_commits\", commit.AutomationId)", commitsPageCode, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedCommit, Mode=OneWay}\"", commitsPage, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", commitsPageCode, StringComparison.Ordinal);
        Assert.True(
            commitsPageCode.IndexOf(
                "ProductPerformanceReadiness.CommitTraversal(\"repo_commits\", commit.AutomationId)",
                StringComparison.Ordinal) >
            commitsPageCode.IndexOf("ViewModel.SelectedCommit = commit", StringComparison.Ordinal));
        Assert.Contains("await WaitForRenderedFrameAsync()", commitsPageCode, StringComparison.Ordinal);
        Assert.Contains("CommitDetailTitle.Text", commitsPageCode, StringComparison.Ordinal);

        string pullRequestsPageCode = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml.cs"));
        Assert.True(
            pullRequestsPageCode.IndexOf(
                "ProductPerformanceReadiness.CommitTraversal(\"repo_pull_requests\", pullRequest.AutomationId)",
                StringComparison.Ordinal) >
            pullRequestsPageCode.IndexOf("ViewModel.SelectedPullRequest = pullRequest", StringComparison.Ordinal));
        Assert.Contains("await WaitForRenderedFrameAsync()", pullRequestsPageCode, StringComparison.Ordinal);
        Assert.Contains("PullRequestDetailTitle.Text", pullRequestsPageCode, StringComparison.Ordinal);

        foreach ((string pageName, string route) in new[]
        {
            ("MyIssuesPage", "my_issues"),
            ("MyPullRequestsPage", "my_pull_requests")
        })
        {
            string pageXaml = File.ReadAllText(Path.Combine(
                root,
                "JitHub.WinUI",
                "Views",
                "Pages",
                $"{pageName}.xaml"));
            string pageCode = File.ReadAllText(Path.Combine(
                root,
                "JitHub.WinUI",
                "Views",
                "Pages",
                $"{pageName}.xaml.cs"));
            Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedItem, Mode=OneWay}\"", pageXaml, StringComparison.Ordinal);
            Assert.Contains("DispatcherQueuePriority.Low", pageCode, StringComparison.Ordinal);
            Assert.True(
                pageCode.IndexOf(
                    $"ProductPerformanceReadiness.CommitTraversal(\"{route}\", item.AutomationId)",
                    StringComparison.Ordinal) <
                pageCode.IndexOf("ViewModel.SelectedItem = item", StringComparison.Ordinal));
        }

        string diffViewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Commit",
            "CommitDiffViewer.xaml"));
        Assert.Contains("VerticalCacheLength=\"2\"", diffViewer, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding LineKind", diffViewer, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Text}", diffViewer, StringComparison.Ordinal);

        string commitsViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoCommitsPageViewModel.cs"));
        int selectionHandlerStart = commitsViewModel.IndexOf(
            "partial void OnSelectedCommitChanged",
            StringComparison.Ordinal);
        int selectionHandlerEnd = commitsViewModel.IndexOf(
            "partial void OnSelectedSectionChanged",
            selectionHandlerStart,
            StringComparison.Ordinal);
        string selectionHandler = commitsViewModel[selectionHandlerStart..selectionHandlerEnd];
        Assert.Contains("NotifySelectedCommitHeaderPropertiesChanged()", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("PopulateCommit(value", selectionHandler, StringComparison.Ordinal);
        int inputYield = commitsViewModel.IndexOf("await Task.Yield();", StringComparison.Ordinal);
        int deferredPopulate = commitsViewModel.IndexOf(
            "PopulateCommit(commit, hasAuthoritativeDiff: false)",
            inputYield,
            StringComparison.Ordinal);
        Assert.True(inputYield >= 0 && deferredPopulate > inputYield);

        string mePageModels = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "MePageModels.cs"));
        Assert.Contains("LoadSelectedItemAfterInputAsync(value)", mePageModels, StringComparison.Ordinal);
        int meInputYield = mePageModels.IndexOf(
            "private async Task LoadSelectedItemAfterInputAsync",
            StringComparison.Ordinal);
        int meSelectionLoad = mePageModels.IndexOf(
            "await LoadSelectedIssueAsync(item)",
            meInputYield,
            StringComparison.Ordinal);
        Assert.True(meInputYield >= 0 && meSelectionLoad > meInputYield);
        Assert.Contains("await Task.Yield();", mePageModels[meInputYield..meSelectionLoad], StringComparison.Ordinal);

        string repoFileTreeViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoFileTreeViewModel.cs"));
        int selectNodeStart = repoFileTreeViewModel.IndexOf(
            "private async Task SelectNodeAsync",
            StringComparison.Ordinal);
        int selectNodeCallback = repoFileTreeViewModel.IndexOf(
            "await OnSelectNode(node, ct)",
            selectNodeStart,
            StringComparison.Ordinal);
        Assert.True(selectNodeStart >= 0 && selectNodeCallback > selectNodeStart);
        Assert.Contains("await Task.Yield();", repoFileTreeViewModel[selectNodeStart..selectNodeCallback], StringComparison.Ordinal);

        string repoFileTree = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));
        int repoCodeTraversal = repoFileTree.IndexOf(
            "ProductPerformanceReadiness.CommitTraversal(\"repo_code\", nodeVm.AutomationId)",
            StringComparison.Ordinal);
        int repoCodeLoad = repoFileTree.IndexOf("ViewModel?.SelectNodeCommand.Execute(nodeVm)", StringComparison.Ordinal);
        Assert.True(repoCodeTraversal >= 0 && repoCodeLoad > repoCodeTraversal);
        Assert.Contains("_pendingSelectionPath", repoFileTree, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", repoFileTree, StringComparison.Ordinal);

        string pullRequestQueryService = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "PullRequests",
            "GitHubPullRequestQueryService.cs"));
        Assert.Contains("ProductPerformanceReadiness.IsEnabled ? 8 : 1", pullRequestQueryService, StringComparison.Ordinal);

        string repository = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoDetailPage.xaml"));
        Assert.Contains("x:Load=\"{x:Bind ShouldLoadRepositoryStatButtons, Mode=OneWay}\"", repository, StringComparison.Ordinal);

        string cachedImage = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Common", "CachedImage.xaml.cs"));
        Assert.Contains("CancelIfStillUnloadedAsync()", cachedImage, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(TimeSpan.FromMilliseconds(500))", cachedImage, StringComparison.Ordinal);
        Assert.Contains("if (ImageElement.Source is null)", cachedImage, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteProbe_SeparatesStartupFromInShellDataReadyNavigationAndExactTraversal()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.PerformanceGate",
            "ProductPerformanceRouteProbe.cs"));

        int startupStart = source.IndexOf("long startupStartedTimestamp = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
        int launch = source.IndexOf("Application.Launch(startInfo)", StringComparison.Ordinal);
        int appRootReady = source.IndexOf("WaitForAppRoot(application, automation)", StringComparison.Ordinal);
        int shellReady = source.IndexOf("WaitForElement(appRoot, \"ShellRoot\")", StringComparison.Ordinal);
        int routeStart = source.IndexOf("long routeStartedTimestamp = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
        Assert.True(startupStart >= 0 && startupStart < launch);
        Assert.True(launch >= 0 && launch < appRootReady);
        Assert.True(appRootReady >= 0 && appRootReady < shellReady);
        Assert.True(shellReady >= 0 && shellReady < routeStart);
        Assert.Contains("requiredStableFrames: 3", source);
        Assert.Contains("routeTransition.FirstDataContent", source);
        Assert.Contains("routeTransition.SettledDataContent", source);
        Assert.Contains("CaptureContentObservation", source);
        Assert.Contains("runCase.Route.RootAutomationId", source);
        Assert.Contains("runCase.Route.ReadyAutomationId", source);
        Assert.Contains("ProductPerformanceReadyStatus.TryParse", source);
        Assert.Contains("ProductPerformanceTraversalReady_", source);
        Assert.Contains("string.Equals(status.Identity, expectedIdentity, StringComparison.Ordinal)", source);
        Assert.Contains("Stopwatch.GetElapsedTime(appStartedTimestamp, firstRenderedTimestamp)", source);
        Assert.DoesNotContain("status.SettledTimestamp is not null", source, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(50);", source);
        Assert.Contains("Thread.Sleep(8);", source);
        Assert.Contains("candidate.Properties.ProcessId.ValueOrDefault == application.ProcessId", source);
        Assert.Contains("exception.HResult == unchecked((int)0x80040201)", source);
        Assert.Contains("Action activateTraversalTarget = PrepareTraversalActivation(target)", source);
        Assert.Contains("activateTraversalTarget();", source);
        Assert.Contains("element.ControlType == ControlType.TreeItem", source);
        Assert.Contains("descendant.ControlType == ControlType.Text", source);
        Assert.Contains("labelBounds.Left + labelBounds.Width / 2", source);
        Assert.Contains("SendNativePointerMove(clickPoint);", source);
        Assert.Contains("Thread.Sleep(75);", source);
        Assert.Contains("return SendNativeClick;", source);
        Assert.Contains("element.ControlType is ControlType.ListItem or ControlType.DataItem", source);
        Assert.Contains("bounds.Left + horizontalInset", source);
        Assert.Contains("bounds.Top + verticalInset", source);
        Assert.Contains("Keyboard.Press(VirtualKeyShort.ENTER)", source);
        Assert.Contains("GetExactTraversalIdentity", source);
        Assert.DoesNotContain("IsMeaningfulContentElement", source);
        Assert.DoesNotContain("Take(24)", source);
        Assert.Contains("VerticalScrollPercent", source);
        Assert.Contains("ReadHeartbeat", source);
        Assert.Contains("FindRepoCodeSourceTraversalTarget", source);
        Assert.Contains("ExpandRepoCodeTreeItem(\n            selectionHost", source.ReplaceLineEndings("\n"));
        Assert.Contains("PrepareScrollableSurface(runCase.Route, scrollTarget)", source);
        Assert.Contains("while (timeout.Elapsed < TimeSpan.FromSeconds(2))", source);
        Assert.Contains("FindVisibleForObservation(appRoot, route.ScrollAutomationId!)", source);
        Assert.Contains("bool measureSelectionBeforeScroll", source);
        Assert.Contains("!measureSelectionBeforeScroll", source);
        Assert.Contains("RepoCodeSourceDirectoryStatus", source);
        Assert.Contains("RepoCodeGeneratedDirectoryStatus", source);
        Assert.Contains("Patterns.ExpandCollapse.Pattern.Expand()", source);
        Assert.Contains("path:src/App.cs", source);
        Assert.DoesNotContain("FirstOrDefault(static element => !IsSelected(element))", source);
        Assert.DoesNotContain("routeRoot.BoundingRectangle", source);
        Assert.DoesNotContain("scrollTarget.BoundingRectangle", source);
        Assert.DoesNotContain("VirtualKeyShort.NEXT", source);
        Assert.DoesNotContain("VirtualKeyShort.PRIOR", source);

        string shellSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        Assert.Contains("pending.ExpectedDestinationRoute", shellSource);
        Assert.Contains("string.Equals(commit.Route", shellSource);

        string repoCodeTreeSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));
        string repoCodeTreeXaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml"));
        int repoCodeTraversalCommit = repoCodeTreeSource.IndexOf(
            "ProductPerformanceReadiness.CommitTraversal(\"repo_code\", nodeVm.AutomationId);",
            StringComparison.Ordinal);
        int repoCodeSelectionDispatch = repoCodeTreeSource.IndexOf(
            "ViewModel?.SelectNodeCommand.Execute(nodeVm);",
            StringComparison.Ordinal);
        Assert.True(repoCodeTraversalCommit >= 0 && repoCodeTraversalCommit < repoCodeSelectionDispatch);
        Assert.Contains("DispatcherQueuePriority.Low", repoCodeTreeSource);
        Assert.Contains("_pendingSelectionPath", repoCodeTreeSource);
        Assert.Contains("SelectionChanged=\"OnSelectionChanged\"", repoCodeTreeXaml);
        Assert.Contains("PointerPressed=\"OnTreeItemPointerPressed\"", repoCodeTreeXaml);
        Assert.Contains("sender.SelectedNode?.Content", repoCodeTreeSource);
        Assert.Contains("PointerUpdateKind.LeftButtonPressed", repoCodeTreeSource);
        Assert.Contains("DispatcherQueuePriority.Low", shellSource);
        Assert.Contains("commit.CommittedTimestamp,\n                commit.CommittedTimestamp", shellSource.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("_productPerformanceRenderingHandlers", shellSource);
        Assert.DoesNotContain("ProductPerformanceCompositionTarget_Rendering", shellSource);
    }

    [Fact]
    public void EveryCanonicalRoute_CommitsRouteSpecificProductDataReadiness()
    {
        string root = FindRepositoryRoot();
        string productSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(root, "JitHub.WinUI"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        string budgetSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Performance",
            "ProductPerformanceBudget.cs"));
        MatchCollection routeMatches = Regex.Matches(
            budgetSource,
            "new\\(\\\"(?<route>[a-z_]+)\\\",[^\\r\\n]+ProductPerformanceRouteReady_(?<marker>[a-z_]+)",
            RegexOptions.CultureInvariant);

        Assert.Equal(14, routeMatches.Count);
        foreach (Match match in routeMatches)
        {
            string route = match.Groups["route"].Value;
            Assert.Equal(route, match.Groups["marker"].Value);
            Assert.Matches(
                $"CommitRoute\\s*\\(\\s*\\\"{Regex.Escape(route)}\\\"",
                productSource);
        }
    }

    [Fact]
    public void RepoCodeNavigation_CommitsFirstFrameBeforeAwaitingTreeInitialization()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCodePage.xaml.cs"));
        int firstFrameCommit = source.IndexOf(
            "$\"repo={owner}/{name};state=visible\"",
            StringComparison.Ordinal);
        int initializeAwait = source.IndexOf(
            "await ViewModel.InitializeAsync(owner!, name!, gitRef!, routeToken)",
            StringComparison.Ordinal);
        int renderedFrameAwait = source.IndexOf(
            "await WaitForRenderedFrameAsync(routeToken)",
            StringComparison.Ordinal);

        Assert.True(firstFrameCommit >= 0 && renderedFrameAwait > firstFrameCommit && initializeAwait > renderedFrameAwait);
        string beforeInitialize = source[..initializeAwait];
        Assert.Contains("await WaitForRenderedFrameAsync(routeToken)", beforeInitialize, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.Register", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeAccountScenario_IsConsumedByEveryCanonicalRouteSurface()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> consumers = new(StringComparer.Ordinal)
        {
            ["home"] = "JitHub.WinUI/Services/Dashboard/GitHubDashboardQueryService.cs",
            ["settings"] = "JitHub.WinUI/Performance/ProductPerformanceVisualProbe.cs",
            ["profile"] = "JitHub.WinUI/Services/Profile/GitHubProfileQueryService.cs",
            ["my_issues"] = "JitHub.WinUI/Services/Me/GitHubMeQueryService.cs",
            ["my_pull_requests"] = "JitHub.WinUI/Services/Me/GitHubMeQueryService.cs",
            ["stars"] = "JitHub.WinUI/Services/Stars/GitHubStarQueryService.cs",
            ["gists"] = "JitHub.WinUI/Services/Gists/GitHubGistQueryService.cs",
            ["notifications"] = "JitHub.WinUI/Services/Notifications/GitHubNotificationQueryService.cs",
            ["repo_manage"] = "JitHub.WinUI/Services/Repositories/GitHubRepositoryIndexService.cs",
            ["repo_search"] = "JitHub.WinUI/Services/Phase0/GitHub/GitHubRepositorySearchQueryService.cs",
            ["repo_code"] = "JitHub.WinUI/Services/CodeViewer/GitHubRepoCodeQueryService.cs",
            ["repo_issues"] = "JitHub.WinUI/Services/Issues/GitHubIssueQueryService.cs",
            ["repo_pull_requests"] = "JitHub.WinUI/Services/PullRequests/GitHubPullRequestQueryService.cs",
            ["repo_commits"] = "JitHub.WinUI/Services/Commits/GitHubCommitQueryService.cs"
        };

        Assert.Equal(14, consumers.Count);
        foreach ((string route, string relativePath) in consumers)
        {
            string source = File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(
                source.Contains("ProductPerformanceLargeAccountFixture", StringComparison.Ordinal) ||
                source.Contains("JITHUB_PERFORMANCE_FIXTURE", StringComparison.Ordinal),
                $"Canonical route '{route}' has no product-side large-account/performance fixture consumer.");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "JitHub.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
