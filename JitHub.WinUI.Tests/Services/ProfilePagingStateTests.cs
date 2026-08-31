using System;
using System.Linq;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProfilePagingStateTests
{
    [Fact]
    public void FullPageAdvancesAndShortPageCompletes()
    {
        ProfilePagingState state = new();

        Assert.True(state.TryBegin(out int firstPage));
        Assert.Equal(1, firstPage);
        state.Complete(firstPage, GitHubProfilePageSizes.Repositories, GitHubProfilePageSizes.Repositories);

        Assert.True(state.TryBegin(out int secondPage));
        Assert.Equal(2, secondPage);
        state.Complete(secondPage, 7, GitHubProfilePageSizes.Repositories);

        Assert.True(state.IsComplete);
        Assert.False(state.TryBegin(out _));
    }

    [Fact]
    public void FailureKeepsTheSamePageRetryable()
    {
        ProfilePagingState state = new();

        Assert.True(state.TryBegin(out int page));
        state.Fail();

        Assert.True(state.TryBegin(out int retry));
        Assert.Equal(page, retry);
    }

    [Fact]
    public void ApiLimitedFullPageStopsPagingAtTheTerminalBoundary()
    {
        ProfilePagingState state = new();

        Assert.True(state.TryBegin(out int page));
        state.Complete(
            page,
            GitHubProfilePageSizes.Activity,
            GitHubProfilePageSizes.Activity,
            isTerminal: true);

        Assert.True(state.IsComplete);
        Assert.False(state.TryBegin(out _));
    }

    [Fact]
    public void MismatchedCompletionFailsClosed()
    {
        ProfilePagingState state = new();
        Assert.True(state.TryBegin(out _));

        Assert.Throws<InvalidOperationException>(() => state.Complete(2, 1, 50));
    }

    [Fact]
    public void FailedFirstPage_PreservesPublishedRowsAndKeepsPageRetryable()
    {
        ProfileSectionPageDecision decision = ProfileSectionLoadPolicy.Decide(
            page: 1,
            publishedCount: 75,
            returnedCount: 0,
            hasError: true);

        Assert.False(decision.ApplyPage);
        Assert.False(decision.CompletePage);
        Assert.False(decision.MarkModeLoaded);
        Assert.Equal(
            "Showing 75 loaded public repositories; refresh is incomplete.",
            ProfileSectionLoadPolicy.FormatStatus(
                "public repositories",
                visibleCount: 75,
                resultItemCount: 0,
                CacheState.Error,
                hasError: true,
                PagedDataCompleteness.Partial));
    }

    [Fact]
    public void FailedInitialCachedPage_IsVisibleButDoesNotCompletePagination()
    {
        ProfileSectionPageDecision decision = ProfileSectionLoadPolicy.Decide(
            page: 1,
            publishedCount: 0,
            returnedCount: 50,
            hasError: true);

        Assert.True(decision.ApplyPage);
        Assert.False(decision.CompletePage);
        Assert.Equal(
            "Showing 50 cached public stars; refresh is incomplete.",
            ProfileSectionLoadPolicy.FormatStatus(
                "public stars",
                visibleCount: 50,
                resultItemCount: 50,
                CacheState.Stale,
                hasError: true,
                PagedDataCompleteness.Partial,
                visibleRowsAreCached: true));
    }

    [Theory]
    [InlineData("followers")]
    [InlineData("following")]
    [InlineData("public activity")]
    public void FailedPeopleAndActivityPages_NeverAdvanceOrComplete(string section)
    {
        ProfilePagingState state = new();
        Assert.True(state.TryBegin(out int page));
        ProfileSectionPageDecision decision = ProfileSectionLoadPolicy.Decide(
            page,
            publishedCount: 50,
            returnedCount: 0,
            hasError: true);

        Assert.False(decision.ApplyPage);
        Assert.False(decision.CompletePage);
        Assert.False(decision.MarkModeLoaded);
        state.Fail();

        Assert.True(state.TryBegin(out int retryPage));
        Assert.Equal(page, retryPage);
        Assert.Equal(
            $"Showing 50 loaded {section}; refresh is incomplete.",
            ProfileSectionLoadPolicy.FormatStatus(
                section,
                visibleCount: 50,
                resultItemCount: 0,
                CacheState.Error,
                hasError: true,
                PagedDataCompleteness.Partial));
    }

    [Fact]
    public void PartialOrganizationRefresh_MergesPrefixWithoutDroppingPublishedTail()
    {
        string[] merged = ProfileSectionLoadPolicy.MergePartialSnapshot(
            published: ["org-1-old", "org-2", "org-3"],
            refreshedPrefix: ["org-1", "org-2"],
            static item => item.StartsWith("org-1", StringComparison.Ordinal) ? "org-1" : item)
            .ToArray();

        Assert.Equal(["org-1", "org-2", "org-3"], merged);
        Assert.Equal(
            "Showing 3 loaded organizations (partial).",
            ProfileSectionLoadPolicy.FormatStatus(
                "organizations",
                visibleCount: 3,
                resultItemCount: 2,
                CacheState.Fresh,
                hasError: false,
                PagedDataCompleteness.Partial));
    }

    [Fact]
    public void ApiLimitedOrganizationScope_IsExplicit()
    {
        Assert.Equal(
            "Showing 5000 organizations (GitHub API limit).",
            ProfileSectionLoadPolicy.FormatStatus(
                "organizations",
                visibleCount: 5000,
                resultItemCount: 5000,
                CacheState.Fresh,
                hasError: false,
                PagedDataCompleteness.ApiLimited));
    }
}
