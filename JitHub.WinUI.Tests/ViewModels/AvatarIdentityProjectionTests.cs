using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class AvatarIdentityProjectionTests
{
    [Fact]
    public void MissingCommentAuthorUsesDisplayFallbackWithoutCreatingRoute()
    {
        var item = new MeIssueCommentViewItem(new GitHubIssueComment
        {
            User = new GitHubActor()
        });

        Assert.Equal("unknown", item.AuthorDisplayName);
        Assert.Null(item.AuthenticatedLogin);
    }

    [Fact]
    public void RealCommentAuthorKeepsAuthenticatedRoute()
    {
        var item = new MeIssueCommentViewItem(new GitHubIssueComment
        {
            User = new GitHubActor { Login = "octocat" }
        });

        Assert.Equal("octocat", item.AuthorDisplayName);
        Assert.Equal("octocat", item.AuthenticatedLogin);
    }

    [Fact]
    public void TimelineDisplayOnlyActorIsNotRoutable()
    {
        UserIdentityPresentation item = UserIdentityNavigationPolicy.CreatePresentation(
            login: null,
            displayName: "Display Name",
            fallbackDisplayName: "someone");

        Assert.Equal("Display Name", item.DisplayName);
        Assert.Null(item.AuthenticatedLogin);
    }

    [Fact]
    public void MissingAndBotTimelineActorsAreNotRoutable()
    {
        UserIdentityPresentation missing = UserIdentityNavigationPolicy.CreatePresentation(null, null, "someone");
        UserIdentityPresentation bot = UserIdentityNavigationPolicy.CreatePresentation(
            "dependabot[bot]",
            null,
            "someone");

        Assert.Equal("someone", missing.DisplayName);
        Assert.Null(missing.AuthenticatedLogin);
        Assert.Equal("dependabot[bot]", bot.DisplayName);
        Assert.Null(bot.AuthenticatedLogin);
    }

    [Fact]
    public void RealTimelineActorKeepsAuthenticatedRoute()
    {
        UserIdentityPresentation item = UserIdentityNavigationPolicy.CreatePresentation(
            "octocat",
            "The Octocat",
            "someone");

        Assert.Equal("octocat", item.DisplayName);
        Assert.Equal("octocat", item.AuthenticatedLogin);
    }

    [Fact]
    public void MissingAndFallbackPullRequestAuthorsAreNotRoutable()
    {
        Assert.Null(UserIdentityNavigationPolicy.GetRoutableLogin(null));
        Assert.Null(UserIdentityNavigationPolicy.GetRoutableLogin(string.Empty));
        Assert.Null(UserIdentityNavigationPolicy.GetRoutableLogin("unknown"));
        Assert.Null(UserIdentityNavigationPolicy.GetRoutableLogin("dependabot[bot]"));
    }

    [Fact]
    public void RealPullRequestAuthorKeepsAuthenticatedRoute()
    {
        Assert.Equal("octocat", UserIdentityNavigationPolicy.GetRoutableLogin("octocat"));
    }

    [Fact]
    public void NullAndBotPullRequestCommentsRemainPassive()
    {
        GitHubIssueComment missing = new() { Id = 10, User = null! };
        GitHubIssueComment bot = new()
        {
            Id = 11,
            User = new GitHubActor { Login = "dependabot[bot]", AvatarUrl = "bot-avatar" }
        };

        Assert.Equal("unknown", missing.AuthorDisplayName);
        Assert.Null(missing.AuthorProfileLogin);
        Assert.Equal("IssueComment_10", missing.AvatarAutomationId);
        Assert.Equal("dependabot[bot]", bot.AuthorDisplayName);
        Assert.Null(bot.AuthorProfileLogin);
        Assert.Equal("bot-avatar", bot.AuthorAvatarUrl);
    }

    [Fact]
    public void RepeatedIssueAndCommitCommentIdsRemainStableAcrossRefresh()
    {
        GitHubIssueComment issueBefore = new() { Id = 42 };
        GitHubIssueComment issueAfter = new() { Id = 42, Body = "updated" };
        GitHubIssueComment issueNeighbor = new() { Id = 43 };
        GitHubCommitComment commitBefore = new() { Id = 17 };
        GitHubCommitComment commitAfter = new() { Id = 17, Body = "updated" };

        Assert.Equal(issueBefore.AvatarAutomationId, issueAfter.AvatarAutomationId);
        Assert.NotEqual(issueBefore.AvatarAutomationId, issueNeighbor.AvatarAutomationId);
        Assert.Equal(commitBefore.AvatarAutomationId, commitAfter.AvatarAutomationId);
    }
}
