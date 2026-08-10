using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using JitHub.Models.CodeViewer;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class GeneratedCollectionAccessibilityContractTests
{
    [Fact]
    public void EveryReachableListViewRecyclingHandlerRestoresContainerSemantics()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");
        List<string> failures = [];
        HashSet<string> actualHooks = new(StringComparer.Ordinal);
        int discoveredLists = 0;

        foreach (string xamlPath in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            string relativePath = Path.GetRelativePath(viewsRoot, xamlPath).Replace(Path.DirectorySeparatorChar, '/');
            XDocument document = XDocument.Load(xamlPath);
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            string codeBehindPath = xamlPath + ".cs";
            string codeBehind = File.Exists(codeBehindPath) ? File.ReadAllText(codeBehindPath) : string.Empty;

            foreach (XElement listView in document.Descendants().Where(element => element.Name.LocalName == "ListView"))
            {
                discoveredLists++;
                string listName = listView.Attribute(xaml + "Name")?.Value
                    ?? listView.Attribute("AutomationProperties.AutomationId")?.Value
                    ?? "<unnamed ListView>";
                string? handler = listView.Attribute("ContainerContentChanging")?.Value;
                if (string.IsNullOrWhiteSpace(handler))
                {
                    failures.Add($"{relativePath}: {listName} has no ContainerContentChanging semantics hook.");
                    continue;
                }

                actualHooks.Add($"{relativePath}|{listName}|{handler}");
                string? body = ExtractMethodBody(codeBehind, handler);
                if (body is null)
                {
                    failures.Add($"{relativePath}: {listName} is wired to missing handler {handler}.");
                    continue;
                }

                if (!body.Contains("AutomationProperties.SetAutomationId", StringComparison.Ordinal) ||
                    !body.Contains("AutomationProperties.SetName", StringComparison.Ordinal))
                {
                    failures.Add($"{relativePath}: {listName}/{handler} does not restore both ID and name on recycled containers.");
                }
            }
        }

        Assert.True(discoveredLists > 0, "No product ListView controls were discovered.");
        Assert.Equal(discoveredLists, actualHooks.Count);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void CriticalDynamicAndNestedCollectionsCannotFallOutOfDiscovery()
    {
        HashSet<string> hooks = DiscoverListHooks();
        string[] required =
        [
            "Controls/CodeViewer/Renderers/CodePreview.xaml|SymbolsList|SymbolsList_ContainerContentChanging",
            "Pages/MyIssuesPage.xaml|IssuesList|IssuesList_ContainerContentChanging",
            "Pages/MyIssuesPage.xaml|MyIssuesCommentsList|CommentsList_ContainerContentChanging",
            "Pages/MyPullRequestsPage.xaml|PullRequestsList|PullRequestsList_ContainerContentChanging",
            "Pages/MyPullRequestsPage.xaml|MyPullRequestsCommentsList|PullRequestDetailList_ContainerContentChanging",
            "Pages/MyPullRequestsPage.xaml|MyPullRequestsCommitsList|PullRequestDetailList_ContainerContentChanging",
            "Pages/MyPullRequestsPage.xaml|MyPullRequestsReviewsList|PullRequestDetailList_ContainerContentChanging",
            "Pages/MyPullRequestsPage.xaml|MyPullRequestsTimelineList|PullRequestDetailList_ContainerContentChanging",
            "Pages/RepoDetailPage.xaml|RepoDetailBranchList|RepoDetailBranchList_ContainerContentChanging",
            "Pages/ShellPage.xaml|ShellRepositoryList|ShellRepositoryList_ContainerContentChanging"
        ];

        foreach (string hook in required)
        {
            Assert.Contains(hook, hooks);
        }
    }

    [Fact]
    public void MissingRecyclingHookIsReportedInsteadOfSkipped()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <ListView x:Name="DynamicRows" ItemsSource="{x:Bind Rows}" />
            </Page>
            """;

        XDocument document = XDocument.Parse(xaml);
        XElement list = Assert.Single(document.Descendants(), element => element.Name.LocalName == "ListView");
        Assert.Null(list.Attribute("ContainerContentChanging"));
    }

    [Fact]
    public void CriticalDynamicCollectionsUseItemKeysInsteadOfContainerIndexes()
    {
        string meItems = ReadProductSource("ViewModels/Pages/MePageModels.cs");
        string issueComments = ReadProductSource("ViewModels/Pages/MeIssueCommentViewItem.cs");
        string pullRequestSections = ReadProductSource("ViewModels/Pages/MePullRequestSectionViewItems.cs");
        string myIssues = ReadProductSource("Views/Pages/MyIssuesPage.xaml.cs");
        string myPullRequests = ReadProductSource("Views/Pages/MyPullRequestsPage.xaml.cs");
        string repoDetail = ReadProductSource("Views/Pages/RepoDetailPage.xaml.cs");
        string codeOutline = ReadProductSource("Views/Controls/CodeViewer/Renderers/CodePreview.xaml.cs");
        string shell = ReadProductSource("Views/Pages/ShellPage.xaml.cs");

        Assert.Contains("$\"MyWorkItem_{Issue.Id.ToString", meItems, StringComparison.Ordinal);
        Assert.Contains("$\"MyWorkItemComment_{StableKey}\"", issueComments, StringComparison.Ordinal);
        Assert.Contains("$\"MyPullRequestsCommit_{StableKey}\"", pullRequestSections, StringComparison.Ordinal);
        Assert.Contains("$\"MyPullRequestsReview_{StableKey}\"", pullRequestSections, StringComparison.Ordinal);
        Assert.Contains("$\"MyPullRequestsTimeline_{StableKey}\"", pullRequestSections, StringComparison.Ordinal);
        Assert.Contains("item.AutomationId", myIssues, StringComparison.Ordinal);
        Assert.Contains("timeline.AutomationId", myPullRequests, StringComparison.Ordinal);
        Assert.Contains("SanitizeAutomationId(branch.Name)", repoDetail, StringComparison.Ordinal);
        Assert.Contains("symbol.AutomationId", codeOutline, StringComparison.Ordinal);
        Assert.Contains("repository.AutomationId", shell, StringComparison.Ordinal);

        string combinedHandlers = myIssues + myPullRequests + repoDetail + codeOutline + shell;
        Assert.DoesNotContain("args.ItemIndex", combinedHandlers, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCreatedGistEditorListRestoresContainerSemantics()
    {
        string source = ReadProductSource("Views/Pages/GistsPage.xaml.cs");
        Assert.Contains("files.ContainerContentChanging +=", source, StringComparison.Ordinal);
        Assert.Contains("UpdateEditorFileContainerAutomation(container, draft", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAutomationId(container,", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(container, draft.AutomationName);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryIssueRowsExposeStableIdentityAndIssueSummary()
    {
        GitHubIssue row = new() { Id = 4201, Number = 42, Title = "Restore keyboard focus" };
        GitHubIssue equivalent = new() { Id = 4201, Number = 42, Title = "Restore keyboard focus" };
        GitHubIssue different = new() { Id = 4301, Number = 43, Title = "Improve contrast" };

        Assert.Equal("RepoIssueRow_4201", row.AutomationId);
        Assert.Equal(row.AutomationId, equivalent.AutomationId);
        Assert.NotEqual(row.AutomationId, different.AutomationId);
        Assert.Equal("Issue #42: Restore keyboard focus", row.AutomationName);
        AssertHumanReadable(row.AutomationId, row.AutomationName, nameof(GitHubIssue));
        AssertTemplateSemantics("Views/Controls/Issue/RepoIssueListPane.xaml");
        AssertContainerSemantics(
            "Views/Controls/Issue/RepoIssueListPane.xaml",
            "IssuesList",
            "IssuesList_ContainerContentChanging",
            "Views/Controls/Issue/RepoIssueListPane.xaml.cs",
            "AutomationProperties.SetAutomationId(container, issue.AutomationId);",
            "AutomationProperties.SetName(container, issue.AutomationName);");
    }

    [Fact]
    public void RepositoryPullRequestRowsExposeStableIdentityAndPullRequestSummary()
    {
        GitHubPullRequest row = new() { Id = 1701, Number = 17, Title = "Ship native review flow" };
        GitHubPullRequest equivalent = new() { Id = 1701, Number = 17, Title = "Ship native review flow" };
        GitHubPullRequest different = new() { Id = 1801, Number = 18, Title = "Fix merge state" };

        Assert.Equal("RepoPullRequestRow_1701", row.AutomationId);
        Assert.Equal(row.AutomationId, equivalent.AutomationId);
        Assert.NotEqual(row.AutomationId, different.AutomationId);
        Assert.Equal("Pull request #17: Ship native review flow", row.AutomationName);
        AssertHumanReadable(row.AutomationId, row.AutomationName, nameof(GitHubPullRequest));
        AssertTemplateSemantics("Views/Pages/RepoPullRequestPage.xaml");
        AssertContainerSemantics(
            "Views/Pages/RepoPullRequestPage.xaml",
            "PullRequestsList",
            "PullRequestsList_ContainerContentChanging",
            "Views/Pages/RepoPullRequestPage.xaml.cs",
            "AutomationProperties.SetAutomationId(container, pullRequest.AutomationId);",
            "AutomationProperties.SetName(container, pullRequest.AutomationName);");
    }

    [Fact]
    public void RepositoryCommitRowsExposeStableIdentityAndCommitSummary()
    {
        GitHubCommit row = CreateCommit("3f9a1c2", "Release transient textures");
        GitHubCommit equivalent = CreateCommit("3f9a1c2", "Release transient textures");
        GitHubCommit different = CreateCommit("8a7b6c1", "Update deployment target");

        Assert.Equal("RepoCommitRow_3f9a1c2", row.AutomationId);
        Assert.Equal(row.AutomationId, equivalent.AutomationId);
        Assert.NotEqual(row.AutomationId, different.AutomationId);
        Assert.Equal("Commit 3f9a1c2: Release transient textures, by Alex", row.AutomationName);
        AssertHumanReadable(row.AutomationId, row.AutomationName, nameof(GitHubCommit));
        AssertTemplateSemantics("Views/Pages/RepoCommitsPage.xaml");
        AssertContainerSemantics(
            "Views/Pages/RepoCommitsPage.xaml",
            "CommitsList",
            "CommitsList_ContainerContentChanging",
            "Views/Pages/RepoCommitsPage.xaml.cs",
            "AutomationProperties.SetAutomationId(container, commit.AutomationId);",
            "AutomationProperties.SetName(container, commit.AutomationName);");
    }

    [Fact]
    public void ShellSearchResultRowsExposeStableIdentityAndMeaningfulSummary()
    {
        StubCommand command = new();
        ShellCommandSearchResult row = new(
            ShellCommandSearchResultKind.Command,
            "Open Settings",
            "Customize JitHub",
            "glyph",
            100,
            command);
        ShellCommandSearchResult equivalent = new(
            ShellCommandSearchResultKind.Command,
            "Open Settings",
            "Customize JitHub",
            "glyph",
            100,
            command);
        ShellCommandSearchResult different = new(
            ShellCommandSearchResultKind.Command,
            "Go Home",
            "Open the dashboard",
            "glyph",
            90,
            command);

        Assert.StartsWith("ShellSearchResult_Command_Open_Settings_", row.AutomationId, StringComparison.Ordinal);
        Assert.Equal(row.AutomationId, equivalent.AutomationId);
        Assert.NotEqual(row.AutomationId, different.AutomationId);
        Assert.Equal("Open Settings, Customize JitHub", row.AutomationName);
        AssertHumanReadable(row.AutomationId, row.AutomationName, nameof(ShellCommandSearchResult));
        AssertTemplateSemantics("Views/Pages/ShellPage.xaml");
        AssertContainerSemantics(
            "Views/Pages/ShellPage.xaml",
            "SearchSuggestionsList",
            "SearchSuggestionsList_ContainerContentChanging",
            "Views/Pages/ShellPage.xaml.cs",
            "AutomationProperties.SetAutomationId(args.ItemContainer, result.AutomationId);",
            "AutomationProperties.SetName(args.ItemContainer, result.AutomationName);");
    }

    [Fact]
    public void RepositoryNavigationItemsExposeStableIdsAndExplicitNames()
    {
        XDocument page = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoDetailPage.xaml"));
        XElement[] items = page.Descendants()
            .Where(element => element.Name.LocalName == "SelectorBarItem")
            .ToArray();

        AssertNavigationItem(items, "RepoNavigation_Code", "Repository code", "Code");
        AssertNavigationItem(items, "RepoNavigation_Issues", "Repository issues", "Issues");
        AssertNavigationItem(items, "RepoNavigation_PullRequests", "Repository pull requests", "Pull Requests");
        AssertNavigationItem(items, "RepoNavigation_Commits", "Repository commits", "Commits");
    }

    private static void AssertNavigationItem(
        IEnumerable<XElement> items,
        string automationId,
        string automationName,
        string text)
    {
        XElement item = Assert.Single(items, element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName.EndsWith(".AutomationId", StringComparison.Ordinal) &&
                attribute.Value == automationId));
        Assert.Equal(
            automationName,
            item.Attributes().Single(attribute =>
                attribute.Name.LocalName.EndsWith(".Name", StringComparison.Ordinal)).Value);
        Assert.Equal(text, item.Attribute("Text")?.Value);
    }

    [Fact]
    public void RepositoryTreeRowsExposeVisibleFilenameInsteadOfModelType()
    {
        RepoTreeNodeViewModel row = new(
            new RepoTreeNode
            {
                Name = "Program.cs",
                Path = "src/Program.cs",
                Sha = "abc",
                IsDirectory = false
            },
            new StubLanguageResolver());

        Assert.Equal("Program.cs, file", row.AutomationName);
        Assert.DoesNotContain(nameof(RepoTreeNodeViewModel), row.AutomationName, StringComparison.Ordinal);
        AssertContainerNameAssignment(
            "Views/Controls/CodeViewer/RepoFileTreeView.xaml.cs",
            "AutomationProperties.SetName(container, node.AutomationName);");
    }

    [Fact]
    public void StarsRowsExposeRepositorySummaryInsteadOfModelType()
    {
        string viewModelSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "StarRepositoryViewItem.cs"));

        Assert.Contains("public string AutomationName", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Repository.FullName", viewModelSource, StringComparison.Ordinal);
        AssertContainerNameAssignment(
            "Views/Pages/StarsPage.xaml.cs",
            "AutomationProperties.SetName(container, item.AutomationName);");
    }

    [Fact]
    public void GistRowsExposeTitleAndVisibilityInsteadOfModelType()
    {
        GistViewItem row = GistViewItem.Create(new GitHubGist
        {
            Id = "gist-1",
            Description = "Build notes",
            Public = true,
            UpdatedAt = DateTimeOffset.Now,
            Files = new Dictionary<string, GitHubGistFile>(StringComparer.OrdinalIgnoreCase)
            {
                ["notes.md"] = new() { Filename = "notes.md" }
            }
        });

        Assert.Contains("Build notes", row.AutomationName, StringComparison.Ordinal);
        Assert.Contains("Public", row.AutomationName, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(GistViewItem), row.AutomationName, StringComparison.Ordinal);
        AssertContainerNameAssignment(
            "Views/Pages/GistsPage.xaml.cs",
            "AutomationProperties.SetName(container, item.AutomationName);");
    }

    private static void AssertContainerNameAssignment(string relativePath, string expectedSource)
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains(expectedSource, source, StringComparison.Ordinal);
    }

    private static void AssertTemplateSemantics(string relativePath)
    {
        string source = ReadProductSource(relativePath);
        Assert.Contains("AutomationProperties.AutomationId=\"{x:Bind AutomationId", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{x:Bind AutomationName", source, StringComparison.Ordinal);
    }

    private static void AssertContainerSemantics(
        string xamlRelativePath,
        string listViewName,
        string expectedHandler,
        string codeBehindRelativePath,
        string expectedIdAssignment,
        string expectedNameAssignment)
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            xamlRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement listView = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "ListView" &&
                string.Equals(element.Attribute(xaml + "Name")?.Value, listViewName, StringComparison.Ordinal));

        Assert.Equal(expectedHandler, listView.Attribute("ContainerContentChanging")?.Value);

        string source = ReadProductSource(codeBehindRelativePath);
        Assert.Contains(expectedIdAssignment, source, StringComparison.Ordinal);
        Assert.Contains(expectedNameAssignment, source, StringComparison.Ordinal);
    }

    private static void AssertHumanReadable(string automationId, string automationName, string clrTypeName)
    {
        Assert.False(string.IsNullOrWhiteSpace(automationId));
        Assert.False(string.IsNullOrWhiteSpace(automationName));
        Assert.DoesNotContain(clrTypeName, automationName, StringComparison.Ordinal);
    }

    private static HashSet<string> DiscoverListHooks()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> hooks = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(viewsRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            XDocument document = XDocument.Load(path);
            foreach (XElement list in document.Descendants().Where(element => element.Name.LocalName == "ListView"))
            {
                string? handler = list.Attribute("ContainerContentChanging")?.Value;
                if (string.IsNullOrWhiteSpace(handler))
                {
                    continue;
                }

                string name = list.Attribute(xaml + "Name")?.Value
                    ?? list.Attribute("AutomationProperties.AutomationId")?.Value
                    ?? "<unnamed ListView>";
                hooks.Add($"{relativePath}|{name}|{handler}");
            }
        }

        return hooks;
    }

    private static string? ExtractMethodBody(string source, string methodName)
    {
        Match method = Regex.Match(
            source,
            $@"\b{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (!method.Success)
        {
            return null;
        }

        int openingBrace = source.IndexOf('{', method.Index + method.Length - 1);
        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
            if (depth == 0)
            {
                return source[openingBrace..(index + 1)];
            }
        }

        return null;
    }

    private static GitHubCommit CreateCommit(string sha, string summary) => new()
    {
        Sha = sha,
        Commit = new GitHubCommitInfo
        {
            Message = summary,
            Author = new GitHubCommitSignature { Name = "Alex" }
        }
    };

    private static string ReadProductSource(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        relativePath.Replace('/', Path.DirectorySeparatorChar)));

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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class StubLanguageResolver : ILanguageIdResolver
    {
        public string Resolve(string fileName, ReadOnlySpan<byte> contentSniff = default) => "plaintext";

        public bool IsKnown(string fileName) => false;
    }

    private sealed class StubCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
