using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PullRequestIdentityProjectionTests
{
    [Fact]
    public void NullReviewAuthor_MapsToPassiveStableIdentity()
    {
        PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
            actor: null,
            fallbackDisplayName: "unknown",
            stableScope: "PullRequestReview_42");

        Assert.Equal("unknown", identity.DisplayName);
        Assert.Null(identity.ProfileLogin);
        Assert.False(identity.IsProfileAvailable);
        Assert.Equal(string.Empty, identity.AvatarUrl);
        Assert.Equal("PullRequestReview_42", identity.AutomationInstanceId);
    }

    [Theory]
    [InlineData("ghost")]
    [InlineData("[deleted]")]
    [InlineData("dependabot[bot]")]
    [InlineData("unknown")]
    public void DeletedBotAndUnknownReviewAuthors_RemainVisibleButPassive(string login)
    {
        GitHubActor actor = new() { Login = login, AvatarUrl = "https://avatars.example/1" };

        PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
            actor,
            fallbackDisplayName: "unknown",
            stableScope: "review-comment:7");

        Assert.Equal(login, identity.DisplayName);
        Assert.Null(identity.ProfileLogin);
        Assert.False(identity.IsProfileAvailable);
        Assert.Equal("https://avatars.example/1", identity.AvatarUrl);
        Assert.Equal("review-comment_7", identity.AutomationInstanceId);
    }

    [Fact]
    public void RealReviewAuthor_IsRoutableAndUsesStableRowScope()
    {
        GitHubActor actor = new() { Id = 99, Login = "octocat", AvatarUrl = "avatar" };

        PullRequestIdentityPresentation first = PullRequestIdentityProjection.Create(actor, "unknown", "review:12");
        PullRequestIdentityPresentation refreshed = PullRequestIdentityProjection.Create(
            new GitHubActor { Id = 99, Login = "octocat", AvatarUrl = "new-avatar" },
            "unknown",
            "review:12");
        PullRequestIdentityPresentation anotherRow = PullRequestIdentityProjection.Create(actor, "unknown", "review:13");

        Assert.Equal("octocat", first.ProfileLogin);
        Assert.Equal(first.AutomationInstanceId, refreshed.AutomationInstanceId);
        Assert.NotEqual(first.AutomationInstanceId, anotherRow.AutomationInstanceId);
    }

    [Fact]
    public void ReviewModelMapping_MapsNullUserPassivelyWithoutDereference()
    {
        GitHubPullRequestReview review = new()
        {
            Id = 42,
            User = null!
        };
        PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
            review,
            fallbackDisplayName: "unknown",
            automationPrefix: "PullRequestReview");

        Assert.Equal("unknown", identity.DisplayName);
        Assert.Null(identity.ProfileLogin);
        Assert.False(identity.IsProfileAvailable);
        Assert.Equal(string.Empty, identity.AvatarUrl);
        Assert.Equal("PullRequestReview_42", identity.AutomationInstanceId);
    }

    [Theory]
    [InlineData("ghost")]
    [InlineData("[deleted]")]
    [InlineData("dependabot[bot]")]
    [InlineData("unknown")]
    public void ReviewCommentModelMapping_KeepsUnavailableUsersPassive(string login)
    {
        GitHubActor actor = new() { Login = login };
        GitHubPullRequestReviewComment comment = new()
        {
            Id = 13,
            PullRequestReviewId = 12,
            User = actor
        };
        PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
            comment,
            fallbackDisplayName: "unknown",
            automationPrefix: "PullRequestReviewComment");

        Assert.Equal(login, identity.DisplayName);
        Assert.Null(identity.ProfileLogin);
        Assert.False(identity.IsProfileAvailable);
        Assert.Equal("PullRequestReviewComment_13", identity.AutomationInstanceId);
    }

    [Fact]
    public void IdlessReviewRepliesRemainStableAcrossRefreshAndUniqueBetweenRows()
    {
        DateTimeOffset created = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        GitHubPullRequestReviewComment first = new()
        {
            NodeId = "PRRC_first",
            PullRequestReviewId = 5,
            CreatedAt = created
        };
        GitHubPullRequestReviewComment refreshed = new()
        {
            NodeId = "PRRC_first",
            PullRequestReviewId = 5,
            CreatedAt = created,
            Body = "updated"
        };
        GitHubPullRequestReviewComment neighbor = new()
        {
            NodeId = "PRRC_second",
            PullRequestReviewId = 5,
            CreatedAt = created
        };

        string firstId = CreateReplyIdentity(first).AutomationInstanceId;
        string refreshedId = CreateReplyIdentity(refreshed).AutomationInstanceId;
        string neighborId = CreateReplyIdentity(neighbor).AutomationInstanceId;

        Assert.Equal(firstId, refreshedId);
        Assert.NotEqual(firstId, neighborId);

        static PullRequestIdentityPresentation CreateReplyIdentity(
            GitHubPullRequestReviewComment value) => PullRequestIdentityProjection.Create(
                value,
                fallbackDisplayName: "unknown",
                automationPrefix: "PullRequestReviewReply");
    }
}
