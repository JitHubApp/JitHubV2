using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class PagedScopeNoticeContractTests
{
    [Theory]
    [InlineData("../Controls/Issue/RepoIssueListPane.xaml", "RepoIssuesScopeNotice", "HasIssueListScopeNotice", "IssueListScopeNotice")]
    [InlineData("RepoCommitsPage.xaml", "RepoCommitsScopeNotice", "HasCommitListScopeNotice", "CommitListScopeNotice")]
    public void PartialScopeNotice_IsVisibleAccessibleAndBoundToCompleteness(
        string pageName,
        string automationId,
        string visibilityProperty,
        string messageProperty)
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            pageName));

        XElement notice = Assert.Single(document.Descendants(), element =>
            string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                automationId,
                StringComparison.Ordinal));

        Assert.Equal("InfoBar", notice.Name.LocalName);
        Assert.Equal("False", (string?)notice.Attribute("IsClosable"));
        Assert.Contains(visibilityProperty, (string?)notice.Attribute("IsOpen"), StringComparison.Ordinal);
        Assert.Contains(messageProperty, (string?)notice.Attribute("Message"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace((string?)notice.Attribute("AutomationProperties.Name")));
    }

    [Fact]
    public void ScopeNotices_UsePlainProductLanguageWithoutCacheTerminology()
    {
        XDocument resources = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Strings",
            "en-US",
            "Resources.resw"));
        string[] names =
        [
            "RepoIssue/PartialScopeNotice",
            "RepoIssue/LimitedScopeNotice",
            "RepoCommits/PartialScopeNotice",
            "RepoCommits/LimitedScopeNotice",
            "RepoCommits/BranchesPartialScopeNotice",
            "RepoCommits/BranchesLimitedScopeNotice"
        ];

        foreach (string name in names)
        {
            XElement entry = Assert.Single(resources.Root!.Elements("data"), element =>
                string.Equals((string?)element.Attribute("name"), name, StringComparison.Ordinal));
            string value = entry.Element("value")?.Value ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.DoesNotContain("cache", value, StringComparison.OrdinalIgnoreCase);
        }
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
