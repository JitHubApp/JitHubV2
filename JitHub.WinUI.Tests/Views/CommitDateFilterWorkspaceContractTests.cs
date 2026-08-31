using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class CommitDateFilterWorkspaceContractTests
{
    [Fact]
    public void CommitHistoryUsesNativeDatePickersAndClearableFilterChips()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml"));

        foreach (string id in new[] { "RepoCommitsSinceFilterPicker", "RepoCommitsUntilFilterPicker" })
        {
            XElement picker = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "CalendarDatePicker" &&
                string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"), id, StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace((string?)picker.Attribute("AutomationProperties.Name")));
            Assert.Equal("CommitDateFilter_DateChanged", (string?)picker.Attribute("DateChanged"));
        }

        foreach (string id in new[] { "RepoCommitsSinceFilterChip", "RepoCommitsUntilFilterChip" })
        {
            XElement chip = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"), id, StringComparison.Ordinal));
            Assert.Contains("Has", (string?)chip.Attribute("Visibility"), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace((string?)chip.Attribute("ToolTipService.ToolTip")));
        }

        Assert.DoesNotContain(document.Descendants(), element =>
            string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"), "RepoCommitsSinceFilterBox", StringComparison.Ordinal) ||
            string.Equals((string?)element.Attribute("AutomationProperties.AutomationId"), "RepoCommitsUntilFilterBox", StringComparison.Ordinal));
    }

    [Fact]
    public void CommitFileNavigationDescribesItsCommitScopedDestination()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml"));
        XElement action = Assert.Single(document.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "RepoCommitsOpenCodeButton",
                StringComparison.Ordinal));

        Assert.Equal("Browse files", (string?)action.Attribute("Content"));
        Assert.Contains("commit", (string?)action.Attribute("AutomationProperties.Name"), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace((string?)action.Attribute("ToolTipService.ToolTip")));
    }

    [Fact]
    public void CommitWorkspaceShowsInspectorOnWideDesktopAndProtectsReadingWidth()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml"));
        XElement workspace = Assert.Single(document.Descendants(), element =>
            string.Equals((string?)element.Attribute("AutomationIdPrefix"), "RepoCommits", StringComparison.Ordinal));

        Assert.Equal("1000", (string?)workspace.Attribute("WideBreakpoint"));
        Assert.Equal("880", (string?)workspace.Attribute("MediumBreakpoint"));
        Assert.Equal("320", (string?)workspace.Attribute("LeadingPaneWidth"));
        Assert.Equal("240", (string?)workspace.Attribute("TrailingPaneWidth"));
    }

    [Fact]
    public void CommitDetailUsesCompactDensityAndOnDemandCompareSearch()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml.cs"));

        XElement metadata = Assert.Single(document.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "CommitDetailMetadata"));
        Assert.NotNull(metadata);
        XElement sectionSelector = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RepoCommitsSectionSegmented");
        Assert.Equal(
            "{StaticResource AppContentSizedSegmentedStyle}",
            (string?)sectionSelector.Attribute("Style"));
        Assert.All(
            sectionSelector.Elements().Where(static element => element.Name.LocalName == "AppSegmentedItem"),
            element =>
            {
                Assert.Equal(
                    "{StaticResource AppContentSizedSegmentedItemStyle}",
                    (string?)element.Attribute("Style"));
                Assert.Null(element.Attribute("Width"));
            });
        XElement compareSearchButton = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RepoCommitsCompareSearchButton");
        XElement compareSearchBox = Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RepoCommitsCompareDiffSearchBox");
        Assert.Contains(compareSearchBox.Ancestors(), ancestor => ancestor.Name.LocalName == "Flyout");
        Assert.Contains("IsCompareDiffVisible", compareSearchButton.Attribute("IsEnabled")?.Value, StringComparison.Ordinal);

        Assert.Contains("AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact", source, StringComparison.Ordinal);
        Assert.Contains("CommitDetailMetadata.Visibility = isCompact", source, StringComparison.Ordinal);
        Assert.Contains("CommitCompareSearchFlyout_Opened", source, StringComparison.Ordinal);
        Assert.Contains("CommitCompareSearchFlyout_Closed", source, StringComparison.Ordinal);
        Assert.Contains("CommitActionKind.ShowSearchTools", source, StringComparison.Ordinal);
        Assert.Contains("CommitActionKind.HideSearchTools", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
