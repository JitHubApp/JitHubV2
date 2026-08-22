using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JitHub.WinUI.Views.Controls.Common;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class IssuePullRequestDensityContractTests
{
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
        Assert.Equal("36", (string?)filterButton.Attribute("Width"));
        Assert.Equal("36", (string?)filterButton.Attribute("Height"));

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

        Assert.Equal("0", (string?)header.Attribute("ColumnSpacing"));
        Assert.Equal("0,0,8,0", (string?)closeButton.Attribute("Margin"));
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
            (string?)element.Attribute("Value") == "460");
        Assert.Contains(flyout.Descendants(), element =>
            element.Name.LocalName == "Border" &&
            (string?)element.Attribute("Width") == "440");
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

    private static XElement FindByAutomationId(XDocument xaml, string automationId) =>
        Assert.Single(xaml.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == automationId);

    private static XDocument LoadXaml(string relativePath) =>
        XDocument.Load(SourcePath(["JitHub.WinUI", .. relativePath.Split('/')]));

    private static string SourcePath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}
