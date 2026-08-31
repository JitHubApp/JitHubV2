using Xunit;
using System.Text.RegularExpressions;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceProbeContractTests
{
    [Fact]
    public void RouteProbe_ArchivesBoundedDiagnosticsForEveryIteration()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.PerformanceGate",
            "ProductPerformanceRouteProbe.cs"));

        Assert.Contains("ArchiveDiagnostics(runCase, partitionRoot)", source, StringComparison.Ordinal);
        Assert.Contains("runCase.Fixture.ToString().ToLowerInvariant()", source, StringComparison.Ordinal);
        Assert.Contains("runCase.Route.Id", source, StringComparison.Ordinal);
        Assert.Contains("iteration-{runCase.Iteration + 1:D2}", source, StringComparison.Ordinal);
        Assert.Contains("diagnostics.ndjson", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TraversalTrace_PublishesUiAutomationOnceAfterTheMeasuredCommit()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        int started = source.IndexOf(
            "private void ProductPerformanceReadiness_TraversalStarted",
            StringComparison.Ordinal);
        int stage = source.IndexOf(
            "private void ProductPerformanceReadiness_TraversalStageRecorded",
            started,
            StringComparison.Ordinal);
        int runOnUiThread = source.IndexOf(
            "private void RunOnUiThread",
            stage,
            StringComparison.Ordinal);

        Assert.True(started >= 0 && stage > started && runOnUiThread > stage);
        Assert.Contains(
            "_productPerformanceTraversalTraceValue = string.Empty",
            source[started..stage],
            StringComparison.Ordinal);
        Assert.Contains(
            "lock (_productPerformanceTraversalTraceGate)",
            source[started..stage],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AutomationProperties.SetItemStatus",
            source[started..runOnUiThread],
            StringComparison.Ordinal);
        Assert.Contains(
            "_productPerformanceTraversalTraceValue = string.IsNullOrEmpty",
            source[stage..runOnUiThread],
            StringComparison.Ordinal);
        Assert.Contains(
            "stage.StartedTimestamp != _productPerformanceTraversalTraceStartedTimestamp",
            source[stage..runOnUiThread],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RunOnUiThread",
            source[stage..runOnUiThread],
            StringComparison.Ordinal);

        int committed = source.IndexOf(
            "private void ProductPerformanceReadiness_TraversalCommitted",
            StringComparison.Ordinal);
        int routeCommitted = source.IndexOf(
            "private void ProductPerformanceReadiness_RouteCommitted",
            committed,
            StringComparison.Ordinal);
        string commitHandler = source[committed..routeCommitted];
        Assert.True(
            commitHandler.IndexOf(
                "lock (_productPerformanceTraversalTraceGate)",
                StringComparison.Ordinal) <
            commitHandler.IndexOf(
                "RunOnUiThread",
                StringComparison.Ordinal));
        Assert.Contains(
            "if (publishTrace)\n            {\n                AutomationProperties.SetItemStatus(\n                    ProductPerformanceTraversalTrace,\n                    traceValue)",
            commitHandler.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.True(
            commitHandler.IndexOf(
                "AutomationProperties.SetItemStatus(",
                StringComparison.Ordinal) <
            commitHandler.IndexOf(
                "SchedulePerformanceMarkerSettlement(marker, commit)",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalListRoutes_PublishAppSideScrollFrameTimestamps()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> expectedProbeHosts = new(StringComparer.Ordinal)
        {
            ["DashboardPage.xaml.cs"] = "ProductPerformanceScrollProbe.TryStart(\n            DashboardMainRailScrollViewer,\n            DashboardMainRailScrollViewer)",
            ["NotificationsPage.xaml.cs"] = "ProductPerformanceScrollProbe.TryStart(NotificationsList, scrollViewer)",
            ["RepoManagePage.xaml.cs"] = "ProductPerformanceScrollProbe.TryStart(RepositoriesList, scrollViewer)",
            ["RepoSearchResultPage.xaml.cs"] = "ProductPerformanceScrollProbe.TryStart(ResultsList, scrollViewer)",
            ["StarsPage.xaml.cs"] = "ProductPerformanceScrollProbe.TryStart(RepositoriesList, scrollViewer)"
        };

        foreach ((string fileName, string expectedProbe) in expectedProbeHosts)
        {
            string source = File.ReadAllText(Path.Combine(
                root,
                "JitHub.WinUI",
                "Views",
                "Pages",
                fileName));
            Assert.Contains(expectedProbe, source, StringComparison.Ordinal);
            Assert.Contains("_performanceScrollProbe?.Dispose()", source, StringComparison.Ordinal);
        }
    }

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
            "if (resolved.ShouldPromote && !ProductPerformanceReadiness.IsEnabled)",
            source,
            StringComparison.Ordinal);
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
    public void RepositoryPullRequestRoute_NavigatesChildSurfaceOnlyOnce()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "RepositoryViewModels",
            "RepoDetailViewModel.cs"));
        int handlerStart = source.IndexOf("private async Task HandleNavigatedTo(", StringComparison.Ordinal);
        int handlerEnd = source.IndexOf("private long BeginRepositoryTransition(", handlerStart, StringComparison.Ordinal);

        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = source[handlerStart..handlerEnd];
        int pullRequestNavigations = handler.Split(
            "GoToPullRequestPage(",
            StringSplitOptions.None).Length - 1;

        Assert.Equal(1, pullRequestNavigations);
        Assert.DoesNotContain("EnsurePreviewPullRequestPageAsync", source, StringComparison.Ordinal);
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

        string myIssuesCode = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "MyIssuesPage.xaml.cs"));
        Assert.Contains(
            "ProductPerformanceScrollProbe.TryStart(IssuesList, scrollViewer)",
            myIssuesCode,
            StringComparison.Ordinal);

        string repoCodePage = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCodePage.xaml.cs"));
        Assert.Contains(
            "FileTree.StartPerformanceScrollProbe(FileTreeHost)",
            repoCodePage,
            StringComparison.Ordinal);
        string performanceRepoFileTree = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));
        Assert.Contains(
            "FindDescendant<ScrollViewer>(FileTreeView)",
            performanceRepoFileTree,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProductPerformanceScrollProbe.TryStart(statusHost, scrollViewer)",
            performanceRepoFileTree,
            StringComparison.Ordinal);

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
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.Commits, Mode=OneWay}\"", commitsPage, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedCommit, Mode=OneWay}\"", commitsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding", commitsPage, StringComparison.Ordinal);
        Assert.True(
            commitsPageCode.IndexOf(
                "ProductPerformanceReadiness.CommitTraversal(\"repo_commits\", commit.AutomationId)",
                StringComparison.Ordinal) >
            commitsPageCode.IndexOf("ViewModel.SelectedCommit = commit", StringComparison.Ordinal));
        Assert.Contains(
            "ProductPerformanceRenderCommitter.ScheduleAfterNextFrame",
            commitsPageCode,
            StringComparison.Ordinal);
        Assert.Contains("CommitDetailTitle.Text", commitsPageCode, StringComparison.Ordinal);
        Assert.True(
            commitsPageCode.IndexOf("ProductPerformanceReadiness.BeginTraversal(", StringComparison.Ordinal) <
            commitsPageCode.IndexOf("CommitSelectionAfterRenderedFrame(commit", StringComparison.Ordinal));
        Assert.True(
            commitsPageCode.IndexOf("CommitSelectionAfterRenderedFrame(commit", StringComparison.Ordinal) <
            commitsPageCode.IndexOf("ViewModel.SelectedCommit = commit", StringComparison.Ordinal));

        string pullRequestsPageCode = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml.cs"));
        Assert.Contains("SchedulePullRequestTraversalCommit", pullRequestsPageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForRenderedFrameAsync", pullRequestsPageCode, StringComparison.Ordinal);
        Assert.Contains(
            "ProductPerformanceRenderCommitter.ScheduleAfterNextFrame",
            pullRequestsPageCode,
            StringComparison.Ordinal);
        Assert.Contains("PullRequestDetailTitle.Text", pullRequestsPageCode, StringComparison.Ordinal);
        int pullRequestSelectionStart = pullRequestsPageCode.IndexOf(
            "private void PullRequestsList_SelectionChanged",
            StringComparison.Ordinal);
        int pullRequestCommitStart = pullRequestsPageCode.IndexOf(
            "private void SchedulePullRequestTraversalCommit",
            pullRequestSelectionStart,
            StringComparison.Ordinal);
        string pullRequestSelection = pullRequestsPageCode[pullRequestSelectionStart..pullRequestCommitStart];
        int pullRequestBegin = pullRequestSelection.IndexOf(
            "int generation = BeginPullRequestTraversal(pullRequest)",
            StringComparison.Ordinal);
        Assert.True(
            pullRequestBegin >= 0 &&
            pullRequestSelection.IndexOf(
                "SchedulePullRequestSelection(pullRequest, generation)",
                pullRequestBegin,
                StringComparison.Ordinal) > pullRequestBegin);
        int pullRequestPrime = pullRequestSelection.IndexOf(
            "PrimePullRequestSelection(pullRequest)",
            pullRequestBegin,
            StringComparison.Ordinal);
        int pullRequestHoverCancellation = pullRequestSelection.IndexOf(
            "ViewModel.CancelHoverPrefetch()",
            pullRequestBegin,
            StringComparison.Ordinal);
        int pullRequestScheduleCommit = pullRequestSelection.IndexOf(
            "SchedulePullRequestTraversalCommit(pullRequest, generation)",
            pullRequestPrime,
            StringComparison.Ordinal);
        int pullRequestScheduleHydration = pullRequestSelection.IndexOf(
            "SchedulePullRequestSelection(pullRequest, generation)",
            pullRequestScheduleCommit,
            StringComparison.Ordinal);
        Assert.True(
            pullRequestHoverCancellation > pullRequestBegin &&
            pullRequestHoverCancellation < pullRequestPrime &&
            pullRequestPrime > pullRequestBegin &&
            pullRequestScheduleCommit > pullRequestPrime &&
            pullRequestScheduleHydration > pullRequestScheduleCommit);
        Assert.DoesNotContain(
            "ViewModel.SelectedPullRequest = pullRequest;",
            pullRequestSelection,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ViewModel.IsPullRequestSelectionCoherent(pullRequest)",
            pullRequestsPageCode,
            StringComparison.Ordinal);
        int pullRequestPointerStart = pullRequestsPageCode.IndexOf(
            "private void PullRequestListItem_PointerPressed",
            StringComparison.Ordinal);
        int pullRequestPrimeInPointer = pullRequestsPageCode.IndexOf(
            "PrimePullRequestSelection(pullRequest)",
            pullRequestPointerStart,
            StringComparison.Ordinal);
        int pullRequestHoverCancellationInPointer = pullRequestsPageCode.IndexOf(
            "ViewModel.CancelHoverPrefetch()",
            pullRequestPointerStart,
            StringComparison.Ordinal);
        int pullRequestCommitInPointer = pullRequestsPageCode.IndexOf(
            "SchedulePullRequestTraversalCommit(pullRequest, generation)",
            pullRequestPrimeInPointer,
            StringComparison.Ordinal);
        int pullRequestNativeSelection = pullRequestsPageCode.IndexOf(
            "PullRequestsList.SelectedItem = pullRequest",
            pullRequestCommitInPointer,
            StringComparison.Ordinal);
        int pullRequestHydrationInPointer = pullRequestsPageCode.IndexOf(
            "SchedulePullRequestSelection(pullRequest, generation, focusSelection: true)",
            pullRequestNativeSelection,
            StringComparison.Ordinal);
        Assert.True(
            pullRequestPointerStart >= 0 &&
            pullRequestHoverCancellationInPointer > pullRequestPointerStart &&
            pullRequestHoverCancellationInPointer < pullRequestPrimeInPointer &&
            pullRequestPrimeInPointer > pullRequestPointerStart &&
            pullRequestCommitInPointer > pullRequestPrimeInPointer &&
            pullRequestNativeSelection > pullRequestCommitInPointer &&
            pullRequestHydrationInPointer > pullRequestNativeSelection);
        Assert.Contains("repo_pull_requests.render.scheduled", pullRequestsPageCode, StringComparison.Ordinal);
        Assert.Contains("repo_pull_requests.render.committed", pullRequestsPageCode, StringComparison.Ordinal);

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
            int selectedItemAssignment = pageCode.IndexOf(
                "ViewModel.SelectedItem = item",
                StringComparison.Ordinal);
            int traversalCommit = pageCode.IndexOf(
                "ProductPerformanceReadiness.CommitTraversal(",
                StringComparison.Ordinal);
            Assert.True(selectedItemAssignment >= 0 && traversalCommit >= 0);
            Assert.Contains($"\"{route}\"", pageCode[traversalCommit..], StringComparison.Ordinal);
            Assert.Contains("DetailTitleText.Text", pageCode, StringComparison.Ordinal);
            if (pageName == "MyIssuesPage")
            {
                Assert.True(traversalCommit > selectedItemAssignment);
                Assert.Contains("ViewModel.IsSelectedHeaderCoherent(item)", pageCode, StringComparison.Ordinal);
                Assert.Contains("CommitSelectedIssueAfterRender", pageCode, StringComparison.Ordinal);
                Assert.Contains(
                    "item && IssuesWorkspace.IsLeadingDrawerOpen",
                    pageCode,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "ProductPerformanceRenderCommitter.ScheduleAfterNextFrame",
                    pageCode,
                    StringComparison.Ordinal);
            }
            else
            {
                Assert.True(traversalCommit < selectedItemAssignment);
                Assert.Contains("ScheduleSelectedPullRequestCommit", pageCode, StringComparison.Ordinal);
                Assert.Contains("PullRequestListItem_PointerPressed", pageCode, StringComparison.Ordinal);
                Assert.Contains(
                    "PointerPressed=\"PullRequestListItem_PointerPressed\"",
                    pageXaml,
                    StringComparison.Ordinal);
                Assert.Contains("my_pull_requests.pointer.selected", pageCode, StringComparison.Ordinal);
                Assert.Contains("my_pull_requests.selection.primed", pageCode, StringComparison.Ordinal);
                Assert.Contains("my_pull_requests.list.selected", pageCode, StringComparison.Ordinal);
                Assert.Contains("my_pull_requests.hydration.scheduled", pageCode, StringComparison.Ordinal);
                Assert.Contains("DeferredFrameAction.Schedule(", pageCode, StringComparison.Ordinal);
                Assert.Contains(
                    "item && PullRequestsWorkspace.IsLeadingDrawerOpen",
                    pageCode,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "ProductPerformanceRenderCommitter.ScheduleAfterNextFrame",
                    pageCode,
                    StringComparison.Ordinal);
                int pointerStart = pageCode.IndexOf(
                    "private void PullRequestListItem_PointerPressed",
                    StringComparison.Ordinal);
                int pointerPrime = pageCode.IndexOf("PrimePullRequestSelection(item);", pointerStart, StringComparison.Ordinal);
                int pointerCommit = pageCode.IndexOf("ScheduleSelectedPullRequestCommit(item, generation);", pointerPrime, StringComparison.Ordinal);
                int nativeSelection = pageCode.IndexOf("PullRequestsList.SelectedItem = item;", pointerCommit, StringComparison.Ordinal);
                int deferredHydration = pageCode.IndexOf("SchedulePullRequestSelection(item, generation", nativeSelection, StringComparison.Ordinal);
                Assert.True(
                    pointerStart >= 0 &&
                    pointerPrime > pointerStart &&
                    pointerCommit > pointerPrime &&
                    nativeSelection > pointerCommit &&
                    deferredHydration > nativeSelection);
            }

            Assert.True(
                pageCode.IndexOf("ProductPerformanceReadiness.BeginTraversal(", StringComparison.Ordinal) <
                pageCode.IndexOf("ViewModel.SelectedItem = item", StringComparison.Ordinal));
        }

        string selectionModels = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "MePageModels.cs"));
        int coherenceStart = selectionModels.IndexOf(
            "public bool IsSelectedHeaderCoherent",
            StringComparison.Ordinal);
        int coherenceEnd = selectionModels.IndexOf(
            "public async Task InitializeAsync",
            coherenceStart,
            StringComparison.Ordinal);
        Assert.True(coherenceStart >= 0 && coherenceEnd > coherenceStart);
        string coherenceContract = selectionModels[coherenceStart..coherenceEnd];
        Assert.Contains("SelectedIssue?.Number == item.Issue.Number", coherenceContract, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Issue.Title", coherenceContract, StringComparison.Ordinal);

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
        int headerNotification = selectionHandler.IndexOf(
            "NotifySelectedCommitHeaderPropertiesChanged()",
            StringComparison.Ordinal);
        int deferredLoad = selectionHandler.IndexOf(
            "BackgroundTaskObserver.Run(",
            StringComparison.Ordinal);
        Assert.True(headerNotification >= 0 && deferredLoad > headerNotification);
        Assert.DoesNotContain("PopulateCommit(value", selectionHandler, StringComparison.Ordinal);
        int deferredDetailStart = commitsViewModel.IndexOf(
            "private async Task ShowCommitAfterInputCommitAsync",
            StringComparison.Ordinal);
        int inputDelay = commitsViewModel.IndexOf(
            "await Task.Delay(InputCriticalDetailDeferral)",
            deferredDetailStart,
            StringComparison.Ordinal);
        int deferredTransition = commitsViewModel.IndexOf(
            "BeginCommitDetailTransition(commit)",
            inputDelay,
            StringComparison.Ordinal);
        Assert.True(deferredDetailStart >= 0 && inputDelay > deferredDetailStart && deferredTransition > inputDelay);
        Assert.Contains("DiffRowProjection = CommitDiffRowProjection.Empty;", commitsViewModel, StringComparison.Ordinal);

        string mePageModels = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "MePageModels.cs"));
        Assert.Contains("LoadSelectedItemAfterInputAsync(value)", mePageModels, StringComparison.Ordinal);
        int meSelectionLoadStart = mePageModels.IndexOf(
            "private async Task LoadSelectedIssueAsync",
            StringComparison.Ordinal);
        int meHeaderAssignment = mePageModels.IndexOf(
            "SelectedIssue = item.Issue;",
            meSelectionLoadStart,
            StringComparison.Ordinal);
        int meDetailDelay = mePageModels.IndexOf(
            "await Task.Delay(InputCriticalDetailDeferral, cancellationToken)",
            meSelectionLoadStart,
            StringComparison.Ordinal);
        int meBodyProjection = mePageModels.IndexOf(
            "ApplySelectedIssue(item.Issue);",
            meHeaderAssignment,
            StringComparison.Ordinal);
        Assert.True(
            meSelectionLoadStart >= 0 &&
            meHeaderAssignment > meSelectionLoadStart &&
            meDetailDelay > meHeaderAssignment &&
            meBodyProjection > meDetailDelay);

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
            "await OnSelectNode(node, ct).ConfigureAwait(false)",
            selectNodeStart,
            StringComparison.Ordinal);
        Assert.True(selectNodeStart >= 0 && selectNodeCallback > selectNodeStart);
        Assert.DoesNotContain("await Task.Yield();", repoFileTreeViewModel[selectNodeStart..selectNodeCallback], StringComparison.Ordinal);

        string repoFileTree = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));
        int repoCodeLoad = repoFileTree.IndexOf("ViewModel?.SelectNodeCommand.Execute(nodeVm)", StringComparison.Ordinal);
        int repoCodeSelectionMethod = repoFileTree.IndexOf(
            "private void SelectFileNode(",
            StringComparison.Ordinal);
        int repoCodeSelectionEnd = repoFileTree.IndexOf(
            "private bool RaiseFileInvoked(",
            repoCodeSelectionMethod,
            StringComparison.Ordinal);
        string repoCodeSelection = repoFileTree[repoCodeSelectionMethod..repoCodeSelectionEnd];
        Assert.True(repoCodeLoad >= 0);
        Assert.DoesNotContain("ProductPerformanceReadiness.CommitTraversal", repoFileTree, StringComparison.Ordinal);
        Assert.Contains("RaiseFileInvoked(nodeVm)", repoFileTree, StringComparison.Ordinal);
        Assert.Contains("ProductPerformanceReadiness.BeginTraversal(", repoFileTree, StringComparison.Ordinal);
        Assert.Contains("_pendingSelectionPath", repoFileTree, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", repoFileTree, StringComparison.Ordinal);
        int repoCodePointerStart = repoFileTree.IndexOf(
            "private void OnTreeItemPointerPressed",
            StringComparison.Ordinal);
        int repoCodeNativePointerSelection = repoFileTree.IndexOf(
            "FileTreeView.SelectedNode = nativeNode;",
            repoCodePointerStart,
            StringComparison.Ordinal);
        Assert.True(
            repoCodePointerStart >= 0 &&
            repoCodeNativePointerSelection > repoCodePointerStart);
        Assert.DoesNotContain(
            "DeferredFrameAction.Schedule(",
            repoFileTree[repoCodePointerStart..repoCodeSelectionMethod],
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectFileNode(nodeVm, visualSelectionContainer: container);",
            repoFileTree,
            StringComparison.Ordinal);
        Assert.Contains("repo_code.visual.selected", repoFileTree, StringComparison.Ordinal);
        Assert.True(
            repoCodeSelection.IndexOf("bool handled = RaiseFileInvoked(nodeVm);", StringComparison.Ordinal) <
            repoCodeSelection.IndexOf("if (!handled)", StringComparison.Ordinal));

        string repoCodePageSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCodePage.xaml.cs"));
        int repoCodeTraversalStart = repoCodePageSource.IndexOf(
            "ScheduleFileTraversalCommit(_pendingFileTraversal)",
            StringComparison.Ordinal);
        int repoCodeVisibleIdentity = repoCodePageSource.IndexOf(
            "ViewModel.IsFileSelectionPresented(pending.Path)",
            repoCodeTraversalStart,
            StringComparison.Ordinal);
        int repoCodeVisualSelection = repoCodePageSource.IndexOf(
            "FileTree.IsFileSelectionVisuallyPresented(pending.Path)",
            repoCodeVisibleIdentity,
            StringComparison.Ordinal);
        int repoCodeTraversal = repoCodePageSource.IndexOf(
            "ProductPerformanceReadiness.CommitTraversal(",
            repoCodeTraversalStart,
            StringComparison.Ordinal);
        Assert.True(
            repoCodeTraversalStart >= 0 &&
            repoCodeVisibleIdentity > repoCodeTraversalStart &&
            repoCodeVisualSelection > repoCodeVisibleIdentity &&
            repoCodeTraversal > repoCodeVisualSelection);
        Assert.Contains(
            "ProductPerformanceRenderCommitter.ScheduleAfterNextFrame",
            repoCodePageSource,
            StringComparison.Ordinal);

        string repoCodeSelectionViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs"));
        int primeSelectionStart = repoCodeSelectionViewModelSource.IndexOf(
            "internal bool PrimeTreeNodeSelection(",
            StringComparison.Ordinal);
        int hydrateSelectionStart = repoCodeSelectionViewModelSource.IndexOf(
            "internal async Task HydratePrimedTreeNodeSelectionAsync(",
            primeSelectionStart,
            StringComparison.Ordinal);
        string primeSelection = repoCodeSelectionViewModelSource[primeSelectionStart..hydrateSelectionStart];
        Assert.Contains("Tree.SelectedNode = current;", primeSelection, StringComparison.Ordinal);
        Assert.Contains("Breadcrumb.PrimePath", repoCodeSelectionViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PrimeFileSelection(", primeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("Breadcrumb.", primeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("Preview.", primeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("Preview.BeginSelection", primeSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySelectFileFromMemoryCache", primeSelection, StringComparison.Ordinal);
        Assert.Contains(
            "Breadcrumb.IsPathTransitioning ||",
            repoCodeSelectionViewModelSource,
            StringComparison.Ordinal);

        string breadcrumbSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoCodeBreadcrumb.xaml"));
        Assert.Contains("x:Name=\"TransitionPathText\"", breadcrumbSource, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind ViewModel.CurrentPath, Mode=OneWay}\"", breadcrumbSource, StringComparison.Ordinal);

        string issueListSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Issue",
            "RepoIssueListPane.xaml.cs"));
        Assert.True(
            issueListSource.IndexOf("list.SelectedItem = issue;", StringComparison.Ordinal) >
            issueListSource.IndexOf("DeferredFrameAction.Schedule(", StringComparison.Ordinal));
        Assert.DoesNotContain("ListViewScrollAnchor.Capture(list)", issueListSource, StringComparison.Ordinal);

        foreach (string postRenderPage in new[]
        {
            "RepoIssuePage.xaml.cs",
            "GistsPage.xaml.cs"
        })
        {
            string source = File.ReadAllText(Path.Combine(
                root,
                "JitHub.WinUI",
                "Views",
                "Pages",
                postRenderPage));
            Assert.Contains(
                "ProductPerformanceRenderCommitter.ScheduleAfterNextFrame",
                source,
                StringComparison.Ordinal);
        }

        string renderCommitter = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Performance",
            "ProductPerformanceRenderCommitter.cs"));
        Assert.Contains("CompositionTarget.Rendered += rendering", renderCommitter, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering += rendering;", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering += renderingWakeUp", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering -= renderingWakeUp", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("render.frame_started", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("Func<bool> isCurrent", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("Func<bool> isReady", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("ReadyTimeout = TimeSpan.FromSeconds(2)", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("if (!isReady()) return;", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("bool scheduleWhenDisabled = false", renderCommitter, StringComparison.Ordinal);
        Assert.Contains("scheduleWhenDisabled: true", repoCodePageSource, StringComparison.Ordinal);

        string visualProbe = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Performance",
            "ProductPerformanceVisualProbe.cs"));
        int dispatcherTickStart = visualProbe.IndexOf(
            "private void DispatcherTimer_Tick",
            StringComparison.Ordinal);
        int traversalArmStart = visualProbe.IndexOf(
            "private void TraversalMeasurement_Armed",
            dispatcherTickStart,
            StringComparison.Ordinal);
        Assert.True(dispatcherTickStart >= 0 && traversalArmStart > dispatcherTickStart);
        string dispatcherTick = visualProbe[dispatcherTickStart..traversalArmStart];
        Assert.Contains("if (_disposed || IsHeartbeatSuppressed())", dispatcherTick, StringComparison.Ordinal);
        Assert.Contains("_dispatcher++;", dispatcherTick, StringComparison.Ordinal);
        Assert.Contains("Publish();", dispatcherTick, StringComparison.Ordinal);
        Assert.DoesNotContain("TryEnqueue", dispatcherTick, StringComparison.Ordinal);
        Assert.Contains("TraversalMeasurementArmed +=", visualProbe, StringComparison.Ordinal);
        Assert.Contains("TraversalMeasurementCompleted +=", visualProbe, StringComparison.Ordinal);
        Assert.Contains("IsHeartbeatSuppressed()", visualProbe, StringComparison.Ordinal);
        Assert.Contains("TraversalSuppressionTimeoutTicks", visualProbe, StringComparison.Ordinal);
        Assert.Contains("RunOnDispatcher(StopRenderingObservation)", visualProbe, StringComparison.Ordinal);
        Assert.Contains("RunOnDispatcher(StartRenderingObservation)", visualProbe, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering -= CompositionTarget_Rendering", visualProbe, StringComparison.Ordinal);

        string previewHostSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "FilePreviewHost.xaml.cs"));
        Assert.Contains("EnsureRenderer(vm);", previewHostSource, StringComparison.Ordinal);
        Assert.Contains("PreviewApplied?.Invoke(", previewHostSource, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.HasThreadAccess", previewHostSource, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", previewHostSource, StringComparison.Ordinal);
        Assert.Contains("GetOrCreateCodeRenderer", previewHostSource, StringComparison.Ordinal);
        Assert.Contains("DataContext = null", previewHostSource, StringComparison.Ordinal);
        Assert.Contains("CachedCodeRendererHost.Visibility = Visibility.Visible", previewHostSource, StringComparison.Ordinal);
        int previewChangeHandlerStart = previewHostSource.IndexOf(
            "private void OnViewModelChanged",
            StringComparison.Ordinal);
        int previewUpdateStart = previewHostSource.IndexOf(
            "private void UpdateState",
            previewChangeHandlerStart,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "nameof(RepoFilePreviewViewModel.Kind)",
            previewHostSource[previewChangeHandlerStart..previewUpdateStart],
            StringComparison.Ordinal);

        string repoCodeViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs"));
        int applyPreviewStart = repoCodeViewModelSource.IndexOf(
            "private void ApplyPreparedFilePreview",
            StringComparison.Ordinal);
        int previewTextAssignment = repoCodeViewModelSource.IndexOf(
            "Preview.Text =",
            applyPreviewStart,
            StringComparison.Ordinal);
        int previewCurrentFileAssignment = repoCodeViewModelSource.IndexOf(
            "Preview.CurrentFile = node;",
            previewTextAssignment,
            StringComparison.Ordinal);
        Assert.True(
            applyPreviewStart >= 0 &&
            previewTextAssignment > applyPreviewStart &&
            previewCurrentFileAssignment > previewTextAssignment);

        string codePreviewSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "Renderers",
            "CodePreview.xaml.cs"));
        Assert.Contains("QueuePreviewBindingFallback", codePreviewSource, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", codePreviewSource, StringComparison.Ordinal);
        Assert.Contains("generation == Volatile.Read(ref _bindingUpdateGeneration)", codePreviewSource, StringComparison.Ordinal);
        Assert.Contains("e.PropertyName == nameof(RepoFilePreviewViewModel.Text)", codePreviewSource, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _bindingUpdateGeneration);\n            ApplyPreviewBindings();", codePreviewSource, StringComparison.Ordinal);

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
        Assert.Contains("TimeSpan elapsed = Stopwatch.GetElapsedTime(", source);
        Assert.DoesNotContain("status.SettledTimestamp is not null", source, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(50);", source);
        Assert.Contains("Thread.Sleep(8);", source);
        Assert.Contains("candidate.Properties.ProcessId.ValueOrDefault == application.ProcessId", source);
        Assert.Contains("catch (COMException exception) when (IsTransientUiAutomationFailure(exception))", source);
        Assert.Contains("catch (Win32Exception exception) when (IsTransientUiAutomationTimeout(exception))", source);
        Assert.Contains("Func<long> activateTraversalTarget = PrepareTraversalActivation(", source);
        Assert.Contains("new IntPtr(appWindow.Properties.NativeWindowHandle.ValueOrDefault)", source);
        Assert.Contains("long traversalActivationStartedTimestamp = activateTraversalTarget();", source);
        Assert.Contains("element.ControlType == ControlType.TreeItem", source);
        Assert.Contains("descendant.ControlType == ControlType.Text", source);
        Assert.Contains("bounds.Left + bounds.Width / 2", source);
        Assert.Contains("Point currentTarget = ResolveTraversalClickPoint(", source);
        Assert.Contains("FindTraversalElementByIdentity(", source);
        Assert.Contains("string expectedIdentity", source);
        Assert.Contains("SendNativePointerMove(currentTarget);", source);
        Assert.Contains("Thread.Sleep(75);", source);
        Assert.Contains("SendNativeClick();", source);
        int activationStart = source.IndexOf(
            "private static Func<long> PrepareTraversalActivation(",
            StringComparison.Ordinal);
        int activationEnd = source.IndexOf(
            "private static Point ResolveTraversalClickPoint(",
            activationStart,
            StringComparison.Ordinal);
        Assert.True(activationStart >= 0 && activationEnd > activationStart);
        Assert.DoesNotContain(
            "SelectionItem.Pattern.Select()",
            source[activationStart..activationEnd],
            StringComparison.Ordinal);
        Assert.Contains("element.ControlType is ControlType.ListItem or ControlType.DataItem", source);
        Assert.Contains("bounds.Left + horizontalInset", source);
        Assert.Contains("Rectangle.Intersect(elementBounds, hostBounds)", source);
        Assert.Contains("Rectangle.Intersect(\n            Rectangle.Intersect(elementBounds, hostBounds),\n            appWindowBounds)", source.ReplaceLineEndings("\n"));
        Assert.Contains("GetWindowRect(windowHandle, out NativeRect bounds)", source);
        Assert.Contains("outside the JitHub selection viewport", source);
        Assert.Contains("bounds.Top + bounds.Height / 2", source);
        Assert.Contains("ActivateWindowForPointerInput(appWindowHandle)", source);
        Assert.Contains("AttachThreadInput(currentThread, targetThread, attach: true)", source);
        Assert.Contains("GetAncestor(GetForegroundWindow(), 2) == GetAncestor(windowHandle, 2)", source);
        Assert.DoesNotContain("Keyboard.Press(VirtualKeyShort.ENTER)", source);
        Assert.Contains("GetExactTraversalIdentity", source);
        Assert.DoesNotContain("IsMeaningfulContentElement", source);
        Assert.DoesNotContain("Take(24)", source);
        Assert.Contains("VerticalScrollPercent", source);
        Assert.Contains("ReadHeartbeat", source);
        Assert.Contains("FindRepoCodeSourceTraversalTarget", source);
        Assert.Contains(
            "route.Id is \"gists\" or \"repo_pull_requests\" or \"repo_commits\"",
            source);
        Assert.Contains("ExpandRepoCodeTreeItem(\n            selectionHost", source.ReplaceLineEndings("\n"));
        Assert.Contains("PrepareScrollableSurface(runCase.Route, scrollTarget)", source);
        Assert.Contains("scrollElement = ResolveVerticalScrollElement(appRoot, runCase.Route);", source);
        Assert.Contains("Leave one observer-free render window", source);
        Assert.Contains("ScrollVertically(appRoot, runCase.Route, ref scrollElement, amount);", source);
        Assert.DoesNotContain(
            "if (currentHeartbeat.Frame <= initialScrollHeartbeat.Frame)\n                    {\n                        Thread.Sleep(1);\n                        continue;",
            source.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "if (currentHeartbeat.Frame > initialScrollHeartbeat.Frame)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (attemptTimeout.Elapsed >= TimeSpan.FromMilliseconds(250))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SendNativeWheelOverElement(\n                                scrollElement,",
            source.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("SendNativeWheel(scrollUp)", source, StringComparison.Ordinal);
        Assert.Contains("MouseData = unchecked((uint)(scrollUp ? 120 : -120))", source, StringComparison.Ordinal);
        Assert.Contains("Flags = 0x0800", source, StringComparison.Ordinal);
        Assert.Contains("probeSequence=", source, StringComparison.Ordinal);
        Assert.Contains("IsVisible(candidate)", source, StringComparison.Ordinal);
        Assert.Contains("DescribeScrollElement(scrollElement)", source, StringComparison.Ordinal);
        Assert.Contains("scroll.VerticalViewSize.ValueOrDefault", source, StringComparison.Ordinal);
        Assert.Contains("ProductPerformanceInputCommitTimeout = TimeSpan.FromSeconds(5)", source);
        Assert.Contains("CommitTextValueOnce(root, automationId, resetValue, description)", source);
        Assert.Contains("Guid.NewGuid():N", source);
        Assert.Contains("FindVisible(root, automationId)", source);
        Assert.Contains("element.Properties.ItemStatus.ValueOrDefault", source);
        Assert.Contains("did not receive a WinUI acknowledgement", source);
        Assert.Contains("long traversalArmRequestedTimestamp = Stopwatch.GetTimestamp();", source);
        Assert.Contains(
            "SelectOrInvoke(WaitForElement(appRoot, \"ProductPerformanceArmTraversalButton\"));\n        // Arming mutates a hidden UIA marker. Let that observer-only work drain\n        // before timing native input so it cannot steal the user's first frame.\n        Thread.Sleep(50);\n        long traversalActivationStartedTimestamp = activateTraversalTarget();",
            source.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("long traversalActivationStartedTimestamp = activateTraversalTarget();", source);
        Assert.Contains("long activationStartedTimestamp = Stopwatch.GetTimestamp();\n            SendNativeClick();", source.ReplaceLineEndings("\n"));
        Assert.Contains("appStartedTimestamp >= minimumStartedTimestamp", source);
        Assert.Contains("appStartedTimestamp >= interactionStartedTimestamp", source);
        Assert.Contains("TimeSpan elapsed = Stopwatch.GetElapsedTime(", source);
        Assert.DoesNotContain("firstRenderedMatch", source);
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

        string scrollProbeSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Performance",
            "ProductPerformanceScrollProbe.cs"));
        Assert.Contains("_renderPending = true;\n        CompositionTarget.Rendering += CompositionTarget_Rendering", scrollProbeSource.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("CompositionTarget.Rendered", scrollProbeSource);
        Assert.Contains("DispatcherQueuePriority.Low", scrollProbeSource);
        Assert.Contains("long renderedTimestamp = Stopwatch.GetTimestamp();", scrollProbeSource);
        Assert.True(
            scrollProbeSource.IndexOf("long renderedTimestamp = Stopwatch.GetTimestamp();", StringComparison.Ordinal) <
            scrollProbeSource.IndexOf("DispatcherQueuePriority.Low", StringComparison.Ordinal));
        Assert.Contains(
            "long sequence = ++_sequence;\n        _renderPending = false;",
            scrollProbeSource.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "new ProductPerformanceScrollStatus(\n                        sequence,\n                        startedTimestamp,\n                        renderedTimestamp)",
            scrollProbeSource.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.True(
            scrollProbeSource.IndexOf("_renderPending = false;", StringComparison.Ordinal) <
            scrollProbeSource.IndexOf("DispatcherQueuePriority.Low", StringComparison.Ordinal));

        string shellSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        Assert.Contains("pending.ExpectedDestinationRoute", shellSource);
        Assert.Contains("string.Equals(commit.Route", shellSource);
        Assert.Contains("ProductPerformanceRouteInput_TextChanged", shellSource);
        Assert.Contains("ProductPerformanceTraversalInput_TextChanged", shellSource);
        Assert.Contains("_productPerformanceRouteInputValue.Trim()", shellSource);
        Assert.Contains("_productPerformanceTraversalInputValue.Trim()", shellSource);
        Assert.Contains("AutomationProperties.SetItemStatus", shellSource);
        Assert.Contains("ProductPerformanceReadiness.ArmTraversalMeasurement()", shellSource);
        Assert.DoesNotContain("ProductPerformanceReadiness.BeginTraversal(route, expectedIdentity, route)", shellSource);
        Assert.Contains("string.Equals(commit.Identity, pending.Identity, StringComparison.Ordinal)", shellSource);

        string shellXaml = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml"));
        Assert.Contains(
            "TextChanged=\"ProductPerformanceRouteInput_TextChanged\"",
            shellXaml);
        Assert.Contains(
            "TextChanged=\"ProductPerformanceTraversalInput_TextChanged\"",
            shellXaml);

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
        int repoCodeSelectionDispatch = repoCodeTreeSource.IndexOf(
            "ViewModel?.SelectNodeCommand.Execute(nodeVm);",
            StringComparison.Ordinal);
        Assert.True(repoCodeSelectionDispatch >= 0);
        Assert.DoesNotContain("ProductPerformanceReadiness.CommitTraversal", repoCodeTreeSource);
        Assert.Contains("RepoFileInvokedEventArgs", repoCodeTreeSource);
        Assert.Contains("DispatcherQueuePriority.Low", repoCodeTreeSource);
        Assert.Contains("_pendingSelectionPath", repoCodeTreeSource);
        Assert.Contains("SelectionChanged=\"OnSelectionChanged\"", repoCodeTreeXaml);
        Assert.Contains("PointerPressed=\"OnTreeItemPointerPressed\"", repoCodeTreeXaml);
        Assert.Contains("sender.SelectedNode?.Content", repoCodeTreeSource);
        Assert.Contains("_nativeSelectedContainer is { IsLoaded: true, IsSelected: true }", repoCodeTreeSource);
        Assert.Contains("GetNodeForContainer(container)", repoCodeTreeSource);
        int repoCodePageSelection = repoCodeTreeSource.IndexOf(
            "bool handled = RaiseFileInvoked(nodeVm);",
            StringComparison.Ordinal);
        int repoCodeVisualSelection = repoCodeTreeSource.IndexOf(
            "IsFileSelectionVisuallyPresented(nodeVm.Path)",
            repoCodePageSelection,
            StringComparison.Ordinal);
        int repoCodePageInvocation = repoCodeTreeSource.IndexOf(
            "ProductPerformanceReadiness.RecordTraversalStage(\"repo_code.page.invoked\")",
            StringComparison.Ordinal);
        Assert.True(
            repoCodePageSelection >= 0 &&
            repoCodeVisualSelection > repoCodePageSelection &&
            repoCodePageInvocation > repoCodeVisualSelection);
        Assert.DoesNotContain(
            "visualSelectionContainer.IsSelected = true;",
            repoCodeTreeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "container.IsSelected = true;",
            repoCodeTreeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FileTreeView.UpdateLayout()",
            repoCodeTreeSource,
            StringComparison.Ordinal);
        Assert.Contains("PointerUpdateKind.LeftButtonPressed", repoCodeTreeSource);
        Assert.Contains("DispatcherQueuePriority.Low", shellSource);
        Assert.Contains("commit.StartedTimestamp ?? commit.CommittedTimestamp", shellSource);
        Assert.DoesNotContain("_productPerformanceRenderingHandlers", shellSource);
        Assert.DoesNotContain("ProductPerformanceCompositionTarget_Rendering", shellSource);
    }

    [Fact]
    public void PerformanceGate_PersistsPerTraversalTimingAndAppStageTrace()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI.PerformanceGate",
            "ProductPerformanceRouteProbe.cs"));

        Assert.Contains("traversal-observations.ndjson", source, StringComparison.Ordinal);
        Assert.Contains("IsTransientUiAutomationTimeout", source, StringComparison.Ordinal);
        Assert.Contains("exception.NativeErrorCode == 1460", source, StringComparison.Ordinal);
        Assert.Contains("catch (Win32Exception exception)", source, StringComparison.Ordinal);
        Assert.Contains("inputMilliseconds = timing.Input.TotalMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("renderMilliseconds = timing.Render.TotalMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("trace = timing.Trace", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("stream.Flush(flushToDisk: true)", source, StringComparison.Ordinal);
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
    public void RepoCodeSelection_PresentsPrimedSelectionBeforeHydratingFileBody()
    {
        string root = FindRepositoryRoot();
        string page = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCodePage.xaml.cs"));
        string tree = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));

        int prime = page.IndexOf("ViewModel.PrimeTreeNodeSelection(e.Node, initialization.Token, out long selectionGeneration)", StringComparison.Ordinal);
        int frame = page.IndexOf("await WaitForRenderedFrameAsync(initialization.Token)", StringComparison.Ordinal);
        int hydrate = page.IndexOf("ViewModel.HydratePrimedTreeNodeSelectionAsync(e.Node, selectionGeneration)", StringComparison.Ordinal);

        Assert.True(prime >= 0 && frame > prime && hydrate > frame);
        Assert.Contains("e.Handled = true", page);
        Assert.Contains("if (!handled)", tree);

        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "CodeViewer",
            "RepoCodePageViewModel.cs"));
        int primeMethod = viewModel.IndexOf("internal bool PrimeTreeNodeSelection(", StringComparison.Ordinal);
        int hydrateMethod = viewModel.IndexOf("internal async Task HydratePrimedTreeNodeSelectionAsync(", StringComparison.Ordinal);
        Assert.True(primeMethod >= 0 && hydrateMethod > primeMethod);
        string primeBody = viewModel[primeMethod..hydrateMethod];
        Assert.Contains("Tree.SelectedNode = current;", primeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PrimeFileSelection(", primeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Breadcrumb.", primeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Preview.", primeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoPullRequestSelection_PresentsFromPointerPressAndKeepsKeyboardSelection()
    {
        string root = FindRepositoryRoot();
        string page = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml.cs"));
        Assert.Contains("PointerUpdateKind.LeftButtonPressed", page);
        Assert.Contains("container.AddHandler(", page);
        Assert.Contains("handledEventsToo: true", page);
        Assert.Contains("ListViewItem { Content: GitHubPullRequest item }", page);
        Assert.Contains("int generation = BeginPullRequestTraversal(pullRequest);", page);
        Assert.Contains("PullRequestsList.SelectedItem = pullRequest;", page);
        Assert.Contains("if (_pointerSelectionInProgress)", page);
        Assert.Contains("PullRequestsList_SelectionChanged", page);
        Assert.Contains("PrimePullRequestSelection(pullRequest);", page);
        Assert.Contains("PullRequestDetailTitle.Text = pullRequest.Title;", page);
        Assert.Contains("repo_pull_requests.pointer.selected", page);
        Assert.Contains("DeferredFrameAction.Schedule(", page);
        Assert.Contains("SchedulePullRequestSelection(pullRequest, generation);", page);
        Assert.DoesNotContain("ViewModel.IsPullRequestSelectionCoherent(pullRequest)", page);
        Assert.Contains("PullRequestDetailTitle.Text", page);
        Assert.Contains("_pendingPointerHydrationNumber = null", page);
        Assert.Contains("PullRequestsWorkspace.IsLeadingDrawerOpen", page);
        Assert.Contains("container.Focus(FocusState.Pointer);", page);
    }

    [Fact]
    public void RepoIssueSelection_PresentsTitleBeforeDeferredHydration()
    {
        string root = FindRepositoryRoot();
        string listPane = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueListPane.xaml.cs"));
        string detailPane = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueDetailPane.xaml.cs"));
        string page = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Pages", "RepoIssuePage.xaml.cs"));

        Assert.Contains("IssueSelected?.Invoke(this, new RepoIssueSelectedEventArgs(issue));", listPane);
        Assert.Contains("DeferredFrameAction.Schedule(", listPane);
        Assert.Contains("IssueListItemContainer_PointerPressed", listPane);
        Assert.Contains("handledEventsToo: true", listPane);
        Assert.Contains("repo_issues.pointer.selected", listPane);
        Assert.Contains("repo_issues.selection.primed", listPane);
        Assert.Contains("repo_issues.list.selected", listPane);
        Assert.Contains("repo_issues.commit.scheduled", listPane);
        Assert.Contains("PrimeIssueSelection", detailPane);
        Assert.Contains("_issueDetailPane?.PrimeIssueSelection(args.Issue);", page);
        Assert.Contains("IsIssueSelectionPrimed(issue)", page);
        Assert.Contains("pane.CaptureScrollAnchor()", page);
        Assert.Contains("container.Focus(FocusState.Pointer);", listPane);
        Assert.True(
            listPane.IndexOf("list.SelectedItem = issue;", StringComparison.Ordinal) >
            listPane.IndexOf("DeferredFrameAction.Schedule(", StringComparison.Ordinal));
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
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
