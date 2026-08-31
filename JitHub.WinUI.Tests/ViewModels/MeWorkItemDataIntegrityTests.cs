using System.Globalization;
using System.Linq;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class MeWorkItemDataIntegrityTests
{
    [Fact]
    public void FailedPullRequestSection_PreservesExistingRows()
    {
        GitHubIssueComment existing = new() { Id = 1, Body = "Cached comment" };

        GitHubIssueComment[] projected = PullRequestSectionProjectionPolicy.ProjectSection(
            [],
            [existing],
            new PullRequestSectionState(CacheState.Miss, ErrorMessage: "Refresh failed."),
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Same(existing, Assert.Single(projected));
    }

    [Fact]
    public void SuccessfulEmptyPullRequestSection_RemovesMissingRows()
    {
        GitHubIssueComment existing = new() { Id = 1, Body = "No longer present" };

        GitHubIssueComment[] projected = PullRequestSectionProjectionPolicy.ProjectSection(
            [],
            [existing],
            new PullRequestSectionState(CacheState.Fresh),
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Empty(projected);
    }

    [Fact]
    public void FailedPullRequestSection_UpdatesPrefixWithoutDroppingCachedRows()
    {
        GitHubIssueComment cached = new() { Id = 1, Body = "Cached comment" };
        GitHubIssueComment partial = new() { Id = 2, Body = "Partial refresh" };

        GitHubIssueComment[] projected = PullRequestSectionProjectionPolicy.ProjectSection(
            [partial],
            [cached],
            new PullRequestSectionState(CacheState.Error, ErrorMessage: "page 2 failed"),
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Equal([2, 1], projected.Select(static comment => comment.Id));
        Assert.Same(partial, projected[0]);
        Assert.Same(cached, projected[1]);
    }

    [Theory]
    [InlineData(1000, 2450, "indexed")]
    [InlineData(700, 2450, "loaded")]
    public void CappedSearchCount_LabelsOnlyTheRowsActuallyAvailable(int loaded, int reported, string suffix)
    {
        string formatted = MeWorkItemCountFormatter.Format(loaded, reported, 1000);

        Assert.EndsWith(suffix, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(reported.ToString(System.Globalization.CultureInfo.CurrentCulture), formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteSearchCount_DoesNotAddAnIndexedQualifier()
    {
        Assert.Equal(
            45.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            MeWorkItemCountFormatter.Format(45, 45, 1000));
    }

    [Theory]
    [InlineData(PagedDataCompleteness.Complete, "45")]
    [InlineData(PagedDataCompleteness.Loading, "45 loading")]
    [InlineData(PagedDataCompleteness.Partial, "45 loaded (partial)")]
    [InlineData(PagedDataCompleteness.ApiLimited, "45 indexed (GitHub API limit)")]
    public void SearchCount_TruthfullyLabelsEveryCompletenessOutcome(
        PagedDataCompleteness completeness,
        string expected)
    {
        Assert.Equal(expected, MeWorkItemCountFormatter.Format(45, 45, 1000, completeness));
    }

    [Fact]
    public void ChangedQuery_EmptyCacheMissKeepsRowsAndDetailUntilAuthoritativeResult()
    {
        MeWorkItemProjectionDecision cachedMiss = MeWorkItemProjectionPolicy.Evaluate(
            queryChanged: true,
            queryAlreadyCommitted: false,
            hasExistingRows: true,
            incomingRowCount: 0,
            isAuthoritative: false,
            isFinal: false,
            completeness: PagedDataCompleteness.Loading);
        MeWorkItemProjectionDecision authoritativeEmpty = MeWorkItemProjectionPolicy.Evaluate(
            queryChanged: true,
            queryAlreadyCommitted: false,
            hasExistingRows: true,
            incomingRowCount: 0,
            isAuthoritative: true,
            isFinal: true,
            completeness: PagedDataCompleteness.Complete);

        Assert.False(cachedMiss.Apply);
        Assert.True(authoritativeEmpty.Apply);
        Assert.True(authoritativeEmpty.RemoveMissing);
        Assert.True(authoritativeEmpty.CommitsQuery);
    }

    [Fact]
    public void ChangedQuery_NonAuthoritativeFinalEmptyStillKeepsRowsAndDetail()
    {
        MeWorkItemProjectionDecision decision = MeWorkItemProjectionPolicy.Evaluate(
            queryChanged: true,
            queryAlreadyCommitted: false,
            hasExistingRows: true,
            incomingRowCount: 0,
            isAuthoritative: false,
            isFinal: true,
            completeness: PagedDataCompleteness.Partial);

        Assert.False(decision.Apply);
        Assert.False(decision.RemoveMissing);
        Assert.False(decision.CommitsQuery);
    }

    [Fact]
    public void ChangedQuery_NonEmptyCachedProjectionCanReplacePriorRowsImmediately()
    {
        MeWorkItemProjectionDecision decision = MeWorkItemProjectionPolicy.Evaluate(
            queryChanged: true,
            queryAlreadyCommitted: false,
            hasExistingRows: true,
            incomingRowCount: 3,
            isAuthoritative: false,
            isFinal: false,
            completeness: PagedDataCompleteness.Loading);

        Assert.True(decision.Apply);
        Assert.False(decision.RemoveMissing);
        Assert.True(decision.CommitsQuery);
    }

    [Theory]
    [InlineData(PagedDataCompleteness.Partial)]
    [InlineData(PagedDataCompleteness.ApiLimited)]
    public void IncompleteFinalProjection_NeverPrunesVisibleTail(PagedDataCompleteness completeness)
    {
        MeWorkItemProjectionDecision decision = MeWorkItemProjectionPolicy.Evaluate(
            queryChanged: false,
            queryAlreadyCommitted: true,
            hasExistingRows: true,
            incomingRowCount: 100,
            isAuthoritative: false,
            isFinal: true,
            completeness: completeness);

        Assert.True(decision.Apply);
        Assert.False(decision.RemoveMissing);
        Assert.False(decision.CommitsQuery);
    }

    [Fact]
    public void ApiLimitedSharedProjection_UpdatesPrefixAndPreservesPublishedInstancesInTail()
    {
        GitHubIssue refreshed = new() { Id = 1, Number = 1, Title = "Refreshed" };
        GitHubIssue publishedTail = new() { Id = 101, Number = 101, Title = "Visible tail" };

        GitHubIssue[] projection = PagedRefreshProjectionPolicy.Merge(
            [refreshed],
            [new GitHubIssue { Id = 1, Number = 1, Title = "Old" }, publishedTail],
            static issue => issue.Id.ToString(CultureInfo.InvariantCulture),
            PagedDataCompleteness.ApiLimited);

        Assert.Equal([1, 101], projection.Select(static issue => issue.Id));
        Assert.Same(refreshed, projection[0]);
        Assert.Same(publishedTail, projection[1]);
    }

    [Fact]
    public void LargeRootMarkdown_IsDeferredUntilExpandedWithoutLosingFullText()
    {
        string body = new('x', DeferredMarkdownBodyState.DefaultRealizationThreshold + 250);
        DeferredMarkdownBodyState state = new();

        state.Update(body);

        Assert.True(state.IsDeferred);
        Assert.False(state.IsMarkdownRealized);
        Assert.Equal(DeferredMarkdownBodyState.DefaultPreviewLength, state.PreviewText.Length);
        Assert.Equal(body, state.FullText);
        Assert.True(state.Expand());
        Assert.True(state.IsMarkdownRealized);
        Assert.Equal(body, state.PreviewText);
    }

    [Fact]
    public void IdentityResolution_PreservesOnlyTheCorrectAccountLogin()
    {
        Assert.Equal("octocat", MeWorkItemIdentityPolicy.ResolveLogin(null, "octocat", "42", "42"));
        Assert.Null(MeWorkItemIdentityPolicy.ResolveLogin(null, "octocat", "42", "84"));
        Assert.Null(MeWorkItemIdentityPolicy.ResolveLogin(null, null, null, "84"));
    }

    [Fact]
    public void PullRequestSectionRefresh_AcceptsOnlyTheLatestUncancelledGeneration()
    {
        Assert.True(MeWorkItemRequestGuard.IsCurrent(
            4, 4, 9, 9, false,
            PullRequestWorkspaceSection.Reviews,
            PullRequestWorkspaceSection.Reviews,
            "repo#12",
            "repo#12"));
        Assert.False(MeWorkItemRequestGuard.IsCurrent(
            4, 5, 9, 9, false,
            PullRequestWorkspaceSection.Reviews,
            PullRequestWorkspaceSection.Reviews,
            "repo#12",
            "repo#12"));
        Assert.False(MeWorkItemRequestGuard.IsCurrent(
            4, 4, 9, 9, true,
            PullRequestWorkspaceSection.Reviews,
            PullRequestWorkspaceSection.Reviews,
            "repo#12",
            "repo#12"));
    }

    [Fact]
    public void PullRequestSectionErrors_NameFailedSectionsWithoutDeveloperCopy()
    {
        PullRequestDetailAggregate aggregate = new(
            new GitHubPullRequest { Number = 12 },
            new GitHubIssue { Number = 12 },
            [],
            [],
            [],
            [],
            [],
            new PullRequestSectionState(CacheState.Fresh),
            new PullRequestSectionState(CacheState.Fresh),
            new PullRequestSectionState(CacheState.Miss, ErrorMessage: "comments failed"),
            new PullRequestSectionState(CacheState.Fresh),
            new PullRequestSectionState(CacheState.Miss, ErrorMessage: "reviews failed"),
            new PullRequestSectionState(CacheState.Fresh),
            new PullRequestSectionState(CacheState.Fresh));

        string message = PullRequestSectionProjectionPolicy.CreateErrorText(aggregate);

        Assert.Contains("comments", message, StringComparison.Ordinal);
        Assert.Contains("reviews", message, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PullRequestConversationErrors_IncludeStalePrimarySection()
    {
        PullRequestConversationAggregate aggregate = new(
            new GitHubPullRequest { Number = 12 },
            new GitHubIssue { Number = 12 },
            [],
            new PullRequestSectionState(CacheState.Stale, ErrorMessage: "Refresh failed."),
            new PullRequestSectionState(CacheState.Fresh),
            new PullRequestSectionState(CacheState.Fresh));

        string message = PullRequestSectionProjectionPolicy.CreateErrorText(aggregate);

        Assert.Contains("pull request", message, StringComparison.Ordinal);
        Assert.Contains("Existing content remains available", message, StringComparison.Ordinal);
    }
}
