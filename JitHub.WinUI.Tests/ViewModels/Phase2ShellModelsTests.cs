using System;
using System.Windows.Input;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class Phase2ShellModelsTests
{
    [Fact]
    public void ShellWorkspaceTabIdentity_NormalizesSingletonSearchAndRepositoryKeys()
    {
        Assert.Equal("home", ShellWorkspaceTabIdentity.Home().Key);
        Assert.Equal("settings", ShellWorkspaceTabIdentity.Settings().Key);
        Assert.Equal("profile", ShellWorkspaceTabIdentity.Profile().Key);

        ShellWorkspaceTabIdentity search = ShellWorkspaceTabIdentity.Search("  Flutter UI  ");
        Assert.Equal("search:flutter ui", search.Key);
        Assert.Equal("search", search.Page);

        GitHubRepository repository = CreateRepository(fullName: "Flutter/Flutter", branch: "Main");
        ShellWorkspaceTabIdentity repoTab = ShellWorkspaceTabIdentity.Repository(repository, RepoPageType.CodePage, " Main ");

        Assert.Equal("repo:flutter/flutter:CodePage:main", repoTab.Key);
        Assert.Equal("code", repoTab.Page);
    }

    [Theory]
    [InlineData(RepoPageType.CodePage, "code")]
    [InlineData(RepoPageType.IssuePage, "issues")]
    [InlineData(RepoPageType.PullRequestPage, "pull-requests")]
    [InlineData(RepoPageType.CommitPage, "commits")]
    public void ShellWorkspaceTabIdentity_MapsRepositoryPages(RepoPageType page, string expected)
    {
        Assert.Equal(expected, ShellWorkspaceTabIdentity.PageName(page));
        Assert.Equal(expected, ShellWorkspaceTabIdentity.Repository("Owner/Repo", page).Page);
    }

    [Theory]
    [InlineData("home", "home")]
    [InlineData("issues", "issues")]
    [InlineData("pull-requests", "pull-requests")]
    [InlineData("notifications", "notifications")]
    [InlineData("stars", "stars")]
    [InlineData("gists", "gists")]
    [InlineData("settings", "settings")]
    [InlineData("search", "")]
    [InlineData("profile", "")]
    [InlineData("code", "")]
    [InlineData("commits", "")]
    public void ShellWorkspaceTabIdentity_MapsVisibleRailDestinations(string page, string expected)
    {
        Assert.Equal(expected, ShellWorkspaceTabIdentity.NavigationItemId(page));
    }

    [Fact]
    public void ShellRepositoryItem_ProjectsRepositoryMetadataAndSelection()
    {
        CountingCommand command = new();
        GitHubRepository repository = CreateRepository(privateRepo: true, fork: true, archived: true);
        ShellRepositoryItem item = new(repository, command);

        Assert.Equal("octo/example", item.FullName);
        Assert.Equal("example", item.Name);
        Assert.Equal("octo", item.Owner);
        Assert.Equal("Phase 2 shell repo", item.Description);
        Assert.True(item.IsPrivate);
        Assert.True(item.IsFork);
        Assert.True(item.IsArchived);
        Assert.Equal("Private", item.VisibilityLabel);
        Assert.Equal("Forked", item.RepositoryKindLabel);

        item.IsSelected = true;
        item.Command.Execute(null);

        Assert.True(item.IsSelected);
        Assert.Equal(1, command.ExecuteCount);
    }

    [Fact]
    public void ShellRepositoryItem_KeyedUpdatePreservesRowAndCommandUsesNewestRepository()
    {
        GitHubRepository? opened = null;
        GitHubRepository initial = CreateRepository(fullName: "octo/example");
        initial.Id = 42;
        ShellRepositoryItem item = new(initial, repository => opened = repository);
        GitHubRepository refreshed = CreateRepository(fullName: "octo/example", archived: true);
        refreshed.Id = 42;
        refreshed.Description = "Refreshed metadata";

        bool changed = item.Update(refreshed);
        item.Command.Execute(null);

        Assert.True(changed);
        Assert.Equal("42", item.Key);
        Assert.Equal("Refreshed metadata", item.Description);
        Assert.True(item.IsArchived);
        Assert.Same(refreshed, opened);
    }

    [Fact]
    public void ShellNavigationItem_ExposesSelectionForAccessibility()
    {
        ShellNavigationItem item = new("home", "Home", "\uE80F", new CountingCommand());
        Assert.Equal("Not selected", item.SelectionStatus);

        item.IsSelected = true;

        Assert.Equal("Selected", item.SelectionStatus);
    }

    [Fact]
    public void StarsNavigationItemDoesNotPresentLibrarySizeAsANotification()
    {
        ShellNavigationItem item = new("stars", "Stars", "\uE734", new CountingCommand());

        Assert.False(item.HasBadge);
        Assert.Equal(0, item.BadgeValue);
        Assert.Equal(string.Empty, item.BadgeText);
    }

    [Theory]
    [InlineData(ShellCommandSearchResultKind.Command, "Command")]
    [InlineData(ShellCommandSearchResultKind.Repository, "Repository")]
    [InlineData(ShellCommandSearchResultKind.SearchQuery, "Search")]
    public void ShellCommandSearchResult_ProjectsLabelsAndRunsCommand(ShellCommandSearchResultKind kind, string expectedLabel)
    {
        CountingCommand command = new();
        ShellCommandSearchResult result = new(
            kind,
            "Open Settings",
            "Command subtitle",
            "\uE713",
            100,
            command,
            payload: "payload");

        Assert.Equal(expectedLabel, result.KindLabel);
        Assert.Equal("Open Settings, Command subtitle", result.AutomationName);
        Assert.Equal("payload", result.Payload);

        result.Command.Execute(null);

        Assert.Equal(1, command.ExecuteCount);
    }

    [Fact]
    public void ShellCommandSearchResult_AutomationNameOmitsEmptySubtitle()
    {
        ShellCommandSearchResult result = new(
            ShellCommandSearchResultKind.Command,
            "Go Home",
            string.Empty,
            "\uE80F",
            100,
            new CountingCommand());

        Assert.Equal("Go Home", result.AutomationName);
    }

    private static GitHubRepository CreateRepository(
        string fullName = "octo/example",
        string branch = "main",
        bool privateRepo = false,
        bool fork = false,
        bool archived = false)
    {
        string[] parts = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string owner = parts.Length > 0 ? parts[0] : "octo";
        string name = parts.Length > 1 ? parts[1] : "example";

        return new GitHubRepository
        {
            Name = name,
            FullName = fullName,
            Description = "Phase 2 shell repo",
            DefaultBranch = branch,
            Private = privateRepo,
            Fork = fork,
            Archived = archived,
            Owner = new GitHubRepositoryOwner
            {
                Login = owner
            }
        };
    }

    private sealed class CountingCommand : ICommand
    {
        public int ExecuteCount { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            ExecuteCount++;
        }
    }
}
