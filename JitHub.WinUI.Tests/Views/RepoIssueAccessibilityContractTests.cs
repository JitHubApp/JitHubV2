using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class RepoIssueAccessibilityContractTests
{
    [Fact]
    public void InteractiveIssueWorkspaceControlsExposeStableNamesAndIds()
    {
        XDocument[] documents = LoadRepoIssueWorkspaceDocuments();
        string[] requiredIds =
        [
            "RepoIssuesStateSegmented",
            "RepoIssuesState_Open",
            "RepoIssuesState_Closed",
            "RepoIssuesState_All",
            "RepoIssuesNewIssueButton",
            "RepoIssuesSearchBox",
            "RepoIssuesScopeComboBox",
            "RepoIssuesSortComboBox",
            "RepoIssuesDirectionComboBox",
            "RepoIssuesList",
            "RepoIssuesOpenListPaneButton",
            "RepoIssuesCloseListPaneButton",
            "RepoIssuesEditButton",
            "RepoIssuesToggleStateButton",
            "RepoIssuesOpenInspectorPaneButton",
            "RepoIssuesCompactActionOverflowButton",
            "RepoIssuesCompactEditAction",
            "RepoIssuesCompactMetadataAction",
            "RepoIssuesCompactToggleStateAction",
            "RepoIssuesInspectorMetadataButton",
            "RepoIssuesBodyInteractionBar",
            "RepoIssuesOpenCommentButton",
            "RepoIssuesCommentBox",
            "RepoIssuesCommentButton",
            "RepoIssuesCloseInspectorPaneButton"
        ];

        foreach (string id in requiredIds)
        {
            XElement[] elements = documents.SelectMany(static document => document.Descendants()).Where(node =>
                string.Equals((string?)node.Attribute("AutomationProperties.AutomationId"), id, StringComparison.Ordinal)).ToArray();
            Assert.True(elements.Length > 0);
            Assert.All(elements, element =>
                Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name"))));
        }
    }

    [Fact]
    public void PermissionSensitiveCommandsBindToExplicitCapabilities()
    {
        string source = string.Join(
            Environment.NewLine,
            LoadRepoIssueWorkspaceDocuments().Select(static document => document.ToString()));

        Assert.Contains("ViewModel.CanCreateIssue", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CanEditIssue", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CanManageIssueMetadata", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CanChangeIssueState", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CanReactToIssue", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsAddCommentEnabled", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RepoIssuesToggleStateButton\"\r\n                                    Click=\"ToggleIssueStateButton_Click\"\r\n                                    IsEnabled=\"{x:Bind ViewModel.AreIssueActionsEnabled",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MyIssuesDrawersExposeAlignedInPanelCloseControls()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "MyIssuesPage.xaml"));

        string[] requiredIds =
        [
            "MyIssuesOpenListPaneButton",
            "MyIssuesCloseListPaneButton",
            "MyIssuesOpenInspectorPaneButton",
            "MyIssuesCloseInspectorPaneButton"
        ];

        foreach (string id in requiredIds)
        {
            XElement element = document.Descendants().Single(node =>
                string.Equals((string?)node.Attribute("AutomationProperties.AutomationId"), id, StringComparison.Ordinal));
            Assert.Equal("36", (string?)element.Attribute("Width"));
            Assert.Equal("36", (string?)element.Attribute("Height"));
            Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name")));
        }

        string automationSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI.Automation",
            "Program.cs"));
        Assert.Contains("\"MyIssuesCloseListPaneButton\"", automationSource, StringComparison.Ordinal);
        Assert.Contains("\"MyIssuesCloseInspectorPaneButton\"", automationSource, StringComparison.Ordinal);
        Assert.Contains("did not expose its in-panel close control", automationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactBreakpointProtectsTheIssueReadingAndCommentWidth()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoIssuePage.xaml"));
        XElement workspace = document.Descendants().Single(element =>
            string.Equals((string?)element.Attribute("AutomationIdPrefix"), "RepoIssues", StringComparison.Ordinal));

        Assert.Equal("760", (string?)workspace.Attribute("MediumBreakpoint"));
        Assert.Equal("336", (string?)workspace.Attribute("LeadingPaneWidth"));
        Assert.Equal("260", (string?)workspace.Attribute("TrailingPaneWidth"));
        Assert.Equal("False", (string?)workspace.Attribute("ShowPaneButtons"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    private static XDocument[] LoadRepoIssueWorkspaceDocuments()
    {
        string root = FindRepositoryRoot();
        return
        [
            XDocument.Load(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoIssuePage.xaml")),
            XDocument.Load(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueListPane.xaml")),
            XDocument.Load(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueDetailPane.xaml")),
            XDocument.Load(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueInspectorPane.xaml"))
        ];
    }
}
