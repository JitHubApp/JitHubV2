using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class MyWorkItemsDataIntegrityContractTests
{
    [Theory]
    [InlineData("MyIssuesPage.xaml", "IssueListItemTemplate")]
    [InlineData("MyIssuesPage.xaml", "CommentTemplate")]
    [InlineData("MyPullRequestsPage.xaml", "PullRequestListItemTemplate")]
    [InlineData("MyPullRequestsPage.xaml", "CommentTemplate")]
    public void KeyedTemplates_UseLiveOneWayBindings(string fileName, string templateKey)
    {
        XDocument document = XDocument.Load(GetPagePath(fileName));
        XElement template = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataTemplate" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" &&
                string.Equals(attribute.Value, templateKey, StringComparison.Ordinal)));
        XAttribute[] bindings = template
            .DescendantsAndSelf()
            .Attributes()
            .Where(attribute => attribute.Value.Contains("{x:Bind", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding => Assert.Contains("Mode=OneWay", binding.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void MyPullRequests_RestoresKeyedViewportAcrossRefresh()
    {
        string source = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml.cs"));

        Assert.Contains("ViewModel.ListSnapshotApplying += OnListSnapshotApplying", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ListSnapshotApplied += OnListSnapshotApplied", source, StringComparison.Ordinal);
        Assert.Contains("ListViewScrollAnchor.Capture(PullRequestsList, GetPullRequestItemKey)", source, StringComparison.Ordinal);
        Assert.Contains("anchor?.RestoreAfterCollectionChange(DispatcherQueue)", source, StringComparison.Ordinal);
        Assert.Contains("pullRequest.StableKey", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MyPullRequests_UsesInternalNavigationCopyAndGlyph()
    {
        string source = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml"));

        Assert.Contains("Open pull request", source, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE72A;\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Open in repo", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MyPullRequests_UsesNativeLazySectionWorkspace()
    {
        string xaml = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml"));
        string viewModel = File.ReadAllText(GetViewModelPath("MePageModels.cs"));

        Assert.Contains("<SelectorBar", xaml, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsSection_Conversation", xaml, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsSection_Commits", xaml, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsSection_Reviews", xaml, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsSection_Timeline", xaml, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestCommentsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestCommitsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestReviewsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestReviewCommentsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestTimelineEventsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("KeyedCollectionDiffOptions.PreserveMissing", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountWorkspaces_AutomaticallyPageListsAndDetailCollections()
    {
        string viewModel = File.ReadAllText(GetViewModelPath("MePageModels.cs"));
        string contracts = File.ReadAllText(GetServicePath("Me", "GitHubMeContracts.cs"));
        string queryService = File.ReadAllText(GetServicePath("Me", "GitHubMeQueryService.cs"));

        Assert.Contains("private const int PageSize = 100", viewModel, StringComparison.Ordinal);
        Assert.Contains("private const int CommentPageSize = 100", viewModel, StringComparison.Ordinal);
        Assert.Contains("GitHubPagedReconciler.LoadAsync<GitHubSearchIssuesResponse, GitHubIssue>", viewModel, StringComparison.Ordinal);
        Assert.Contains("GitHubPagedReconciler.LoadAsync<GitHubIssueComment[], GitHubIssueComment>", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestCommentsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestCommitsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestReviewsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestReviewCommentsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetAllPullRequestTimelineEventsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GetIssuesPageAsync", contracts, StringComparison.Ordinal);
        Assert.Contains("GetPullRequestsPageAsync", contracts, StringComparison.Ordinal);
        Assert.Contains("&page={ClampPage(page)}", queryService, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize: 30", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void MyPullRequests_ReusesCanonicalSnapshotSectionsAndInternalRoute()
    {
        string viewModel = File.ReadAllText(GetViewModelPath("MePageModels.cs"));
        string xaml = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml"));

        Assert.Contains("_pullRequestNavigationCache.TryGet", viewModel, StringComparison.Ordinal);
        Assert.Contains("PullRequestNavigationSnapshot", viewModel, StringComparison.Ordinal);
        Assert.Contains("PullRequestWorkspaceSection", viewModel, StringComparison.Ordinal);
        Assert.Contains("_shell.OpenRepositoryTarget", viewModel, StringComparison.Ordinal);
        Assert.Contains("PullRequestNavigationStoreMode.PreservePopulatedSections", viewModel, StringComparison.Ordinal);
        Assert.Contains("x:Uid=\"MyPullRequestsOpenRepositoryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MyPullRequestsOpenInRepositoryButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher.LaunchUriAsync", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void MyPullRequests_DrawersExposeAccessibleInPanelCloseActions()
    {
        string xaml = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml"));
        string codeBehind = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml.cs"));

        Assert.Contains("MyPullRequestsCloseListPaneButton", xaml, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsCloseInspectorPaneButton", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Close pull request list\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Close inspector\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VisibleDrawer == AdaptiveWorkspaceDrawer.Leading", codeBehind, StringComparison.Ordinal);
        Assert.Contains("VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PullRequestsWorkspace.CloseDrawer()", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void MyWorkItemInspectorCollections_UseKeyedReconciliationAndLiveBindings()
    {
        string viewModel = File.ReadAllText(GetViewModelPath("MePageModels.cs"));
        string issueXaml = File.ReadAllText(GetPagePath("MyIssuesPage.xaml"));
        string pullRequestXaml = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml"));

        Assert.DoesNotContain("SelectedLabels.Clear()", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedAssignees.Clear()", viewModel, StringComparison.Ordinal);
        Assert.Contains("SelectedLabels.ApplySnapshot", viewModel, StringComparison.Ordinal);
        Assert.Contains("SelectedAssignees.ApplySnapshot", viewModel, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"viewmodels:MeLabelViewItem\"", issueXaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"viewmodels:MeActorViewItem\"", issueXaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"viewmodels:MeLabelViewItem\"", pullRequestXaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"viewmodels:MeActorViewItem\"", pullRequestXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollAnchor_CancelsDelayedRestoreAfterDirectUserInput()
    {
        string source = File.ReadAllText(GetControlPath("ListViewScrollAnchor.cs"));

        Assert.Contains("PointerPressedEvent", source, StringComparison.Ordinal);
        Assert.Contains("PointerWheelChangedEvent", source, StringComparison.Ordinal);
        Assert.Contains("KeyDownEvent", source, StringComparison.Ordinal);
        Assert.Contains("ShouldRestore(_userInteracted)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MyIssuesPage.xaml", "MyIssuesCommentsList")]
    [InlineData("MyPullRequestsPage.xaml", "MyPullRequestsCommentsList")]
    public void Conversations_UseOneVirtualizedSelectableReadingSurface(string fileName, string automationId)
    {
        XDocument document = XDocument.Load(GetPagePath(fileName));
        XElement commentsList = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "ListView" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.AutomationId" && attribute.Value == automationId));

        Assert.NotNull(commentsList.Elements().SingleOrDefault(element => element.Name.LocalName == "ListView.Header"));
        Assert.Equal("{StaticResource CommentTemplate}", commentsList.Attribute("ItemTemplate")?.Value);
        Assert.Equal("None", commentsList.Attribute("SelectionMode")?.Value);
        Assert.Contains(commentsList.Descendants(), element => element.Name.LocalName == "MarkdownViewer");
        Assert.DoesNotContain(commentsList.Descendants(), element => element.Name.LocalName == "ItemsControl");
    }

    [Theory]
    [InlineData("MyIssuesPage.xaml")]
    [InlineData("MyPullRequestsPage.xaml")]
    public void MyWorkItemPages_HaveUniqueStaticAutomationIdsAndStableRowIdentity(string fileName)
    {
        XDocument document = XDocument.Load(GetPagePath(fileName));
        string[] staticIds = document.Descendants()
            .Attributes()
            .Where(attribute => attribute.Name.LocalName == "AutomationProperties.AutomationId" &&
                !attribute.Value.Contains("{x:Bind", StringComparison.Ordinal))
            .Select(static attribute => attribute.Value)
            .ToArray();

        Assert.Equal(staticIds.Length, staticIds.Distinct(StringComparer.Ordinal).Count());
        string source = document.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("{x:Bind AutomationId, Mode=OneWay}", source, StringComparison.Ordinal);
        Assert.Contains("{x:Bind AutomationName, Mode=OneWay}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MyPullRequests_LocalizesSectionsAndSupportsLongLabelFilterFallback()
    {
        string xaml = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml"));
        string codeBehind = File.ReadAllText(GetPagePath("MyPullRequestsPage.xaml.cs"));

        Assert.Contains("ViewModel.ConversationSectionLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("PullRequestOpenStateLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsStateCompactPicker", xaml, StringComparison.Ordinal);
        Assert.Contains("MyIssuesFilterLayoutPolicy.ShouldUseCompact", codeBehind, StringComparison.Ordinal);
        Assert.Contains("my-pull-requests-pseudo-long-labels", codeBehind, StringComparison.Ordinal);
    }

    private static string GetPagePath(string fileName) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "JitHub.WinUI", "Views", "Pages", fileName));

    private static string GetViewModelPath(string fileName) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "JitHub.WinUI", "ViewModels", "Pages", fileName));

    private static string GetControlPath(string fileName) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "JitHub.WinUI", "Views", "Controls", "Common", fileName));

    private static string GetServicePath(params string[] segments) => Path.GetFullPath(Path.Combine(
        [
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "JitHub.WinUI", "Services",
            .. segments
        ]));
}
