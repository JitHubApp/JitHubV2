using System.Globalization;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CommitSectionProjectionPolicyTests
{
    [Fact]
    public void PartialCommitSections_UpdatePrefixAndPreservePublishedTails()
    {
        GitHubCommitComment refreshedComment = new() { Id = 1, Body = "refreshed" };
        GitHubCommitComment cachedCommentTail = new() { Id = 2, Body = "cached tail" };
        GitHubCheckRun refreshedCheck = new() { Id = 10, Name = "refreshed check" };
        GitHubCheckRun cachedCheckTail = new() { Id = 11, Name = "cached check tail" };
        GitHubPullRequest refreshedPullRequest = new() { Number = 20, Title = "refreshed PR" };
        GitHubPullRequest cachedPullRequestTail = new() { Number = 21, Title = "cached PR tail" };
        CommitSectionState partial = new(
            CacheState.Fresh,
            Completeness: PagedDataCompleteness.Partial,
            LoadedItemCount: 1,
            LoadedPageCount: 1);

        GitHubCommitComment[] comments = CommitSectionProjectionPolicy.Project(
            [refreshedComment],
            [new GitHubCommitComment { Id = 1, Body = "old" }, cachedCommentTail],
            partial,
            static item => item.Id.ToString(CultureInfo.InvariantCulture));
        GitHubCheckRun[] checks = CommitSectionProjectionPolicy.Project(
            [refreshedCheck],
            [new GitHubCheckRun { Id = 10, Name = "old" }, cachedCheckTail],
            partial,
            static item => item.Id.ToString(CultureInfo.InvariantCulture));
        GitHubPullRequest[] pullRequests = CommitSectionProjectionPolicy.Project(
            [refreshedPullRequest],
            [new GitHubPullRequest { Number = 20, Title = "old" }, cachedPullRequestTail],
            partial,
            static item => item.Number.ToString(CultureInfo.InvariantCulture));

        Assert.Equal([1, 2], comments.Select(static item => item.Id));
        Assert.Same(refreshedComment, comments[0]);
        Assert.Same(cachedCommentTail, comments[1]);
        Assert.Equal([10, 11], checks.Select(static item => item.Id));
        Assert.Same(refreshedCheck, checks[0]);
        Assert.Same(cachedCheckTail, checks[1]);
        Assert.Equal([20, 21], pullRequests.Select(static item => item.Number));
        Assert.Same(refreshedPullRequest, pullRequests[0]);
        Assert.Same(cachedPullRequestTail, pullRequests[1]);
    }

    [Fact]
    public void ErrorStateIsNeverAuthoritativeEvenWhenCompletenessWasReportedComplete()
    {
        GitHubCommitComment cached = new() { Id = 7, Body = "cached" };
        CommitSectionState failed = new(
            CacheState.Error,
            ErrorMessage: "refresh failed",
            Completeness: PagedDataCompleteness.Complete);

        GitHubCommitComment[] projection = CommitSectionProjectionPolicy.Project(
            [],
            [cached],
            failed,
            static item => item.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Same(cached, Assert.Single(projection));
    }

    [Fact]
    public void CompleteCommitSectionRemovesRowsMissingFromAuthoritativeResult()
    {
        GitHubCommitComment refreshed = new() { Id = 1, Body = "refreshed" };

        GitHubCommitComment[] projection = CommitSectionProjectionPolicy.Project(
            [refreshed],
            [new GitHubCommitComment { Id = 1 }, new GitHubCommitComment { Id = 2 }],
            new CommitSectionState(CacheState.Fresh),
            static item => item.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Same(refreshed, Assert.Single(projection));
    }
}
