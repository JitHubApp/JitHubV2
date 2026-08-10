using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PullRequestPermissionPolicyTests
{
    [Fact]
    public void AuthorWithoutWritePermissionCanEditAndCloseButCannotMergeOrManageMetadata()
    {
        GitHubRepository repository = CreateRepository(pull: true);
        GitHubPullRequest pullRequest = CreatePullRequest("author");

        PullRequestCapabilities result = PullRequestPermissionPolicy.Evaluate(
            repository, pullRequest, new GitHubIssue(), "author", false);

        Assert.True(result.CanEdit);
        Assert.True(result.CanChangeState);
        Assert.True(result.CanComment);
        Assert.True(result.CanSubmitReviewComment);
        Assert.False(result.CanApprove);
        Assert.False(result.CanRequestChanges);
        Assert.False(result.CanManageMetadata);
        Assert.False(result.CanMerge);
    }

    [Fact]
    public void WriterCanMergeOnlyWithRepositoryEnabledMethods()
    {
        GitHubRepository repository = CreateRepository(push: true);
        repository.AllowMergeCommit = false;
        repository.AllowSquashMerge = true;
        repository.AllowRebaseMerge = false;

        PullRequestCapabilities result = PullRequestPermissionPolicy.Evaluate(
            repository, CreatePullRequest("author"), new GitHubIssue(), "writer", false);

        Assert.True(result.CanMerge);
        Assert.False(result.CanMergeCommit);
        Assert.True(result.CanSquashMerge);
        Assert.False(result.CanRebaseMerge);
    }

    [Theory]
    [InlineData("blocked", "Branch protection")]
    [InlineData("dirty", "conflicts")]
    public void MergeabilityStateDisablesMergeWithExplanation(string state, string expectedReason)
    {
        GitHubRepository repository = CreateRepository(push: true);
        GitHubPullRequest pullRequest = CreatePullRequest("author");
        pullRequest.MergeableState = state;
        if (state == "dirty")
        {
            pullRequest.Mergeable = false;
        }

        PullRequestCapabilities result = PullRequestPermissionPolicy.Evaluate(
            repository, pullRequest, new GitHubIssue(), "writer", false);

        Assert.False(result.CanMerge);
        Assert.Contains(expectedReason, result.MergeUnavailableReason);
    }

    [Fact]
    public void LockedConversationDisablesCommentsAndReactions()
    {
        PullRequestCapabilities result = PullRequestPermissionPolicy.Evaluate(
            CreateRepository(pull: true),
            CreatePullRequest("author"),
            new GitHubIssue { Locked = true },
            "reader",
            false);

        Assert.False(result.CanComment);
        Assert.False(result.CanReact);
        Assert.False(result.CanSubmitReviewComment);
        Assert.False(result.CanApprove);
        Assert.False(result.CanRequestChanges);
    }

    [Fact]
    public void AuthenticatedReaderCanSubmitEveryReviewDecisionForSomeoneElsesOpenPullRequest()
    {
        PullRequestCapabilities result = PullRequestPermissionPolicy.Evaluate(
            CreateRepository(pull: true),
            CreatePullRequest("author"),
            new GitHubIssue(),
            "reviewer",
            false);

        Assert.True(result.CanSubmitReviewComment);
        Assert.True(result.CanApprove);
        Assert.True(result.CanRequestChanges);
    }

    [Fact]
    public void PreviewAndArchivedRepositoriesDisableEveryWriteCapability()
    {
        GitHubRepository repository = CreateRepository(push: true);
        repository.Archived = true;
        PullRequestCapabilities archived = PullRequestPermissionPolicy.Evaluate(
            repository, CreatePullRequest("author"), new GitHubIssue(), "author", false);
        PullRequestCapabilities preview = PullRequestPermissionPolicy.Evaluate(
            CreateRepository(push: true), CreatePullRequest("author"), new GitHubIssue(), "author", true);

        Assert.False(archived.CanEdit || archived.CanComment || archived.CanSubmitReviewComment || archived.CanMerge);
        Assert.False(preview.CanEdit || preview.CanComment || preview.CanSubmitReviewComment || preview.CanMerge);
    }

    private static GitHubRepository CreateRepository(bool pull = false, bool push = false) => new()
    {
        AllowMergeCommit = true,
        AllowSquashMerge = true,
        AllowRebaseMerge = true,
        Permissions = new GitHubRepositoryPermissions
        {
            Pull = pull || push,
            Push = push
        }
    };

    private static GitHubPullRequest CreatePullRequest(string author) => new()
    {
        State = "open",
        User = new GitHubActor { Login = author },
        Mergeable = true,
        MergeableState = "clean"
    };
}
