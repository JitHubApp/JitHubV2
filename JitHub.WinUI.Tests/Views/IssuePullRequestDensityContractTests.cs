using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JitHub.WinUI.Views.Controls.Common;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class IssuePullRequestDensityContractTests
{
    [Fact]
    public void RepositoryIssueAndPullRequestWorkspacesSharePaneSizing()
    {
        XElement issues = FindByAutomationId(
            LoadXaml("Views/Pages/RepoIssuePage.xaml"),
            "RepoIssuesAdaptiveWorkspace");
        XElement pullRequests = FindByAutomationId(
            LoadXaml("Views/Pages/RepoPullRequestPage.xaml"),
            "RepoPullRequestsAdaptiveWorkspace");

        foreach (string attributeName in new[]
        {
            "LeadingPaneWidth",
            "TrailingPaneWidth",
            "NarrowBreakpoint",
            "MediumBreakpoint",
            "WideBreakpoint"
        })
        {
            Assert.Equal(
                (string?)issues.Attribute(attributeName),
                (string?)pullRequests.Attribute(attributeName));
        }
    }

    [Fact]
    public void RepositoryIssueAndPullRequestListCommandsShareDensity()
    {
        XDocument issues = LoadXaml("Views/Controls/Issue/RepoIssueListPane.xaml");
        XDocument pullRequests = LoadXaml("Views/Pages/RepoPullRequestPage.xaml");
        XElement issueNewButton = FindByAutomationId(issues, "RepoIssuesNewIssueButton");
        XElement pullRequestNewButton = FindByAutomationId(pullRequests, "RepoPullRequestsNewButton");

        Assert.Equal(
            (string?)issueNewButton.Attribute("MinHeight"),
            (string?)pullRequestNewButton.Attribute("MinHeight"));
        Assert.Equal(
            (string?)issueNewButton.Attribute("MinWidth"),
            (string?)pullRequestNewButton.Attribute("MinWidth"));
        Assert.Equal(
            (string?)issueNewButton.Attribute("Padding"),
            (string?)pullRequestNewButton.Attribute("Padding"));

        XElement issueTabs = FindByAutomationId(issues, "RepoIssuesStateSegmented");
        XElement pullRequestTabs = FindByAutomationId(pullRequests, "RepoPullRequestsStateSegmented");
        Assert.Equal(
            issueTabs.Elements().Select(static item => item.Attribute("Padding")?.Value).ToArray(),
            pullRequestTabs.Elements().Select(static item => item.Attribute("Padding")?.Value).ToArray());
    }

    [Fact]
    public void PullRequestDetailTabsAreCompactAndContentSized()
    {
        XDocument pullRequests = LoadXaml("Views/Pages/RepoPullRequestPage.xaml");
        XElement tabs = FindByAutomationId(pullRequests, "RepoPullRequestsSectionSegmented");

        Assert.Equal("{StaticResource AppContentSizedSegmentedStyle}", (string?)tabs.Attribute("Style"));
        Assert.Equal("{ThemeResource AppFontSize12}", (string?)tabs.Attribute("FontSize"));
        Assert.All(tabs.Elements(), item =>
        {
            Assert.Equal("{StaticResource AppContentSizedSegmentedItemStyle}", (string?)item.Attribute("Style"));
            Assert.Null(item.Attribute("Width"));
            Assert.Null(item.Attribute("Height"));
            Assert.Null(item.Attribute("Padding"));
        });
    }

    [Fact]
    public void PullRequestListLoadingIndicatorDoesNotReserveIdleCommandSpace()
    {
        XDocument pullRequests = LoadXaml("Views/Pages/RepoPullRequestPage.xaml");
        XElement loadingRing = Assert.Single(pullRequests.Descendants(), element =>
            element.Name.LocalName == "ProgressRing" &&
            (string?)element.Attribute("Grid.Column") == "2");

        Assert.Equal("{Binding IsPullRequestListLoading}", (string?)loadingRing.Attribute("IsActive"));
        Assert.Equal(
            "{Binding IsPullRequestListLoading, Converter={StaticResource BoolToVisibilityConverter}}",
            (string?)loadingRing.Attribute("Visibility"));

        string viewModel = File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoPullRequestPageViewModel.cs"));
        Assert.Contains("public partial bool IsPullRequestListLoading", viewModel, StringComparison.Ordinal);
        Assert.Contains("requestId == _listRequestId", viewModel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Views/Controls/Issue/RepoIssueListPane.xaml", "RepoIssuesFilterButton", "RepoIssuesScopeComboBox", "RepoIssuesSortComboBox", "RepoIssuesDirectionComboBox")]
    [InlineData("Views/Pages/RepoPullRequestPage.xaml", "RepoPullRequestsFilterButton", "RepoPullRequestsSortComboBox", "RepoPullRequestsDirectionComboBox")]
    public void SecondaryListFiltersLiveInOnDemandFlyouts(
        string relativePath,
        string filterButtonId,
        params string[] filterControlIds)
    {
        XDocument xaml = LoadXaml(relativePath);
        XElement filterButton = FindByAutomationId(xaml, filterButtonId);

        Assert.Equal("Button", filterButton.Name.LocalName);
        Assert.Equal("{ThemeResource AppDimension36}", (string?)filterButton.Attribute("Width"));
        Assert.Equal("{ThemeResource AppDimension36}", (string?)filterButton.Attribute("Height"));

        foreach (string id in filterControlIds)
        {
            XElement filterControl = FindByAutomationId(xaml, id);
            Assert.Contains(filterControl.Ancestors(), ancestor => ancestor.Name.LocalName == "Flyout");
            Assert.Null(filterControl.Attribute("Header"));
            Assert.Equal("0", (string?)filterControl.Attribute("SelectedIndex"));
            Assert.Contains(filterControl.Elements(), element => element.Name.LocalName == "ComboBoxItem");
        }
    }

    [Theory]
    [InlineData("Views/Controls/Issue/RepoIssueListPane.xaml", "RepoIssuesStateSegmented", "RepoIssuesCloseListPaneButton")]
    [InlineData("Views/Pages/RepoPullRequestPage.xaml", "RepoPullRequestsStateSegmented", "RepoPullRequestsCloseListPaneButton")]
    public void StateTabsDoNotKeepSpacingForCollapsedDrawerButtons(
        string relativePath,
        string segmentedId,
        string closeButtonId)
    {
        XDocument xaml = LoadXaml(relativePath);
        XElement segmented = FindByAutomationId(xaml, segmentedId);
        XElement header = Assert.IsType<XElement>(segmented.Parent);
        Assert.Equal("Grid", header.Name.LocalName);
        XElement closeButton = FindByAutomationId(xaml, closeButtonId);

        Assert.Equal("{ThemeResource AppGap0}", (string?)header.Attribute("ColumnSpacing"));
        Assert.Equal("{ThemeResource AppMargin0_0_8_0}", (string?)closeButton.Attribute("Margin"));
    }

    [Theory]
    [InlineData("Views/Controls/Issue/RepoIssueListPane.xaml")]
    [InlineData("Views/Pages/RepoPullRequestPage.xaml")]
    public void ListIdentityChipsUseRoundedRectangleContainers(string relativePath)
    {
        XDocument xaml = LoadXaml(relativePath);

        Assert.Contains(xaml.Descendants(), element =>
            element.Name.LocalName == "Avatar" &&
            (string?)element.Attribute("ShowLogin") == "True" &&
            (string?)element.Attribute("ContainerCornerRadius") == "{ThemeResource AppRadiusSmall}");
    }

    [Theory]
    [InlineData("Views/Controls/Issue/RepoIssueDetailPane.xaml", "RepoIssuesOpenCommentButton")]
    [InlineData("Views/Pages/RepoPullRequestPage.xaml", "RepoPullRequestsOpenCompactCommentButton")]
    public void OnDemandCommentComposersStayInsideTheirFlyoutAndKeepAccentContrast(
        string relativePath,
        string commentButtonId)
    {
        XDocument xaml = LoadXaml(relativePath);
        XElement commentButton = FindByAutomationId(xaml, commentButtonId);
        XElement flyout = Assert.Single(commentButton.Descendants(), element => element.Name.LocalName == "Flyout");

        Assert.Contains(flyout.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "MaxWidth" &&
            (string?)element.Attribute("Value") == "{ThemeResource AppDimension460}");
        Assert.Contains(flyout.Descendants(), element =>
            element.Name.LocalName == "Border" &&
            (string?)element.Attribute("Width") == "{ThemeResource AppDimension440}");
        Assert.All(
            commentButton.Descendants().Where(element => element.Name.LocalName is "SymbolIcon" or "TextBlock"),
            element => Assert.Equal(
                "{ThemeResource AppAccentForegroundBrush}",
                (string?)element.Attribute("Foreground")));
    }

    [Fact]
    public void CommentAndConversationMarkdownMatchTheirInsetParentSurface()
    {
        Assert.Equal(
            MarkdownHostContract.GetSurfaceColorToken(MarkdownHostContract.Conversation),
            MarkdownHostContract.GetSurfaceColorToken(MarkdownHostContract.Comment));
        Assert.Equal(
            MarkdownHostContract.GetSurfaceFallback(MarkdownHostContract.Conversation, dark: false),
            MarkdownHostContract.GetSurfaceFallback(MarkdownHostContract.Comment, dark: false));
        Assert.Equal(
            MarkdownHostContract.GetSurfaceFallback(MarkdownHostContract.Conversation, dark: true),
            MarkdownHostContract.GetSurfaceFallback(MarkdownHostContract.Comment, dark: true));
    }

    [Fact]
    public void PullRequestReviewThreadMarkdownUsesItsParentSurfaceToken()
    {
        XDocument xaml = LoadXaml("Views/Pages/RepoPullRequestPage.xaml");
        XElement[] nestedThreadViewers = xaml.Descendants()
            .Where(element =>
                element.Name.LocalName == "MarkdownViewer" &&
                element.Ancestors().Any(ancestor =>
                    ancestor.Name.LocalName == "Border" &&
                    (string?)ancestor.Attribute("Background") == "{ThemeResource AppSurfaceSubtleBrush}"))
            .ToArray();

        Assert.Equal(2, nestedThreadViewers.Length);
        Assert.All(
            nestedThreadViewers,
            viewer => Assert.Equal("AppSurfaceSubtle", (string?)viewer.Attribute("SurfaceColorToken")));

        string viewerSource = File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        Assert.Contains("SurfaceColorTokenProperty", viewerSource, StringComparison.Ordinal);
        Assert.Contains("SurfaceColorToken.Trim()", viewerSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Views/Controls/Issue/RepoIssueDetailPane.xaml", "RepoIssuesBody")]
    [InlineData("Views/Pages/RepoPullRequestPage.xaml", "RepoPullRequestsBody")]
    public void ConversationBodiesSizeToRenderedContent(string relativePath, string automationInstanceId)
    {
        XDocument xaml = LoadXaml(relativePath);
        XElement body = Assert.Single(xaml.Descendants(), element =>
            element.Name.LocalName == "MarkdownViewer" &&
            (string?)element.Attribute("AutomationInstanceId") == automationInstanceId);

        Assert.Null(body.Attribute("Height"));
        Assert.Null(body.Attribute("MinHeight"));
    }

    private static XElement FindByAutomationId(XDocument xaml, string automationId) =>
        Assert.Single(xaml.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == automationId);

    private static XDocument LoadXaml(string relativePath) =>
        XDocument.Load(SourcePath(["JitHub.WinUI", .. relativePath.Split('/')]));

    private static string SourcePath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}
