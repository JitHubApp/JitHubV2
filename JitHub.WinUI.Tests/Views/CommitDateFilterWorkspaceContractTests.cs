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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
