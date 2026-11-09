using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum CommitWorkspaceSection
{
    Diff,
    Comments,
    Checks,
    Compare
}

public sealed class CommitListQueryOptions
{
    public string? GitRef { get; set; }

    public string? Path { get; set; }

    public string? Author { get; set; }

    public DateTimeOffset? Since { get; set; }

    public DateTimeOffset? Until { get; set; }
}

public sealed record CommitSectionState(
    CacheState CacheState,
    bool IsRefreshInProgress = false,
    string? ErrorMessage = null,
    PagedDataCompleteness Completeness = PagedDataCompleteness.Complete,
    int LoadedItemCount = 0,
    int LoadedPageCount = 0);

public sealed record CommitPagedSection<T>(
    T[] Items,
    CommitSectionState State)
    where T : class;

public sealed record CommitDetailAggregate(
    GitHubCommit Commit,
    GitHubCommitComment[] Comments,
    GitHubCombinedStatus? CombinedStatus,
    GitHubCheckRun[] CheckRuns,
    GitHubPullRequest[] AssociatedPullRequests,
    CommitSectionState CommitState,
    CommitSectionState CommentsState,
    CommitSectionState StatusState,
    CommitSectionState CheckRunsState,
    CommitSectionState AssociatedPullRequestsState);

public interface IGitHubCommitQueryService
{
    Task<CachedResult<GitHubBranch[]>> GetBranchesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubBranch[]>> GetBranchesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        pageNumber == 1
            ? GetBranchesAsync(accessToken, userId, owner, repositoryName, pageSize, cancellationToken)
            : Task.FromResult(new CachedResult<GitHubBranch[]>([], CacheState.Miss, null, null));

    async Task<CommitPagedSection<GitHubBranch>> GetAllBranchesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubBranch[]> page = await GetBranchesAsync(
            accessToken, userId, owner, repositoryName, 100, cancellationToken);
        GitHubBranch[] items = page.Value ?? [];
        return new CommitPagedSection<GitHubBranch>(
            items,
            new CommitSectionState(
                page.CacheState,
                page.IsRefreshInProgress,
                SafeRefreshError(page.RefreshError),
                items.Length < 100 ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial,
                items.Length,
                1));
    }

    Task<CachedResult<GitHubCommit[]>> GetCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CommitListQueryOptions options,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    async Task<CommitPagedSection<GitHubCommit>> GetAllCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CommitListQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubCommit[]> page = await GetCommitsAsync(
            accessToken, userId, owner, repositoryName, options, 100, 1, cancellationToken);
        GitHubCommit[] items = page.Value ?? [];
        return new CommitPagedSection<GitHubCommit>(
            items,
            new CommitSectionState(page.CacheState, page.IsRefreshInProgress, SafeRefreshError(page.RefreshError),
                items.Length < 100 ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial,
                items.Length,
                1));
    }

    Task<CachedResult<GitHubCommit>> GetCommitAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubCommitComment[]>> GetCommitCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubCommitComment[]>> GetCommitCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        pageNumber == 1
            ? GetCommitCommentsAsync(accessToken, userId, owner, repositoryName, gitRef, pageSize, cancellationToken)
            : Task.FromResult(new CachedResult<GitHubCommitComment[]>([], CacheState.Miss, null, null));

    async Task<CommitPagedSection<GitHubCommitComment>> GetAllCommitCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubCommitComment[]> page = await GetCommitCommentsAsync(
            accessToken, userId, owner, repositoryName, gitRef, 100, cancellationToken);
        GitHubCommitComment[] items = page.Value ?? [];
        return new CommitPagedSection<GitHubCommitComment>(items,
            new CommitSectionState(page.CacheState, page.IsRefreshInProgress, SafeRefreshError(page.RefreshError),
                items.Length < 100 ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial,
                items.Length,
                1));
    }

    Task<CachedResult<GitHubCombinedStatus>> GetCombinedStatusAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubCheckRun[]>> GetCheckRunsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubCheckRun[]>> GetCheckRunsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        pageNumber == 1
            ? GetCheckRunsAsync(accessToken, userId, owner, repositoryName, gitRef, pageSize, cancellationToken)
            : Task.FromResult(new CachedResult<GitHubCheckRun[]>([], CacheState.Miss, null, null));

    async Task<CommitPagedSection<GitHubCheckRun>> GetAllCheckRunsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubCheckRun[]> page = await GetCheckRunsAsync(
            accessToken, userId, owner, repositoryName, gitRef, 100, cancellationToken);
        GitHubCheckRun[] items = page.Value ?? [];
        return new CommitPagedSection<GitHubCheckRun>(items,
            new CommitSectionState(page.CacheState, page.IsRefreshInProgress, SafeRefreshError(page.RefreshError),
                items.Length < 100 ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial,
                items.Length,
                1));
    }

    Task<CachedResult<GitHubPullRequest[]>> GetAssociatedPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubPullRequest[]>> GetAssociatedPullRequestsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        pageNumber == 1
            ? GetAssociatedPullRequestsAsync(accessToken, userId, owner, repositoryName, gitRef, pageSize, cancellationToken)
            : Task.FromResult(new CachedResult<GitHubPullRequest[]>([], CacheState.Miss, null, null));

    async Task<CommitPagedSection<GitHubPullRequest>> GetAllAssociatedPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubPullRequest[]> page = await GetAssociatedPullRequestsAsync(
            accessToken, userId, owner, repositoryName, gitRef, 100, cancellationToken);
        GitHubPullRequest[] items = page.Value ?? [];
        return new CommitPagedSection<GitHubPullRequest>(items,
            new CommitSectionState(page.CacheState, page.IsRefreshInProgress, SafeRefreshError(page.RefreshError),
                items.Length < 100 ? PagedDataCompleteness.Complete : PagedDataCompleteness.Partial,
                items.Length,
                1));
    }

    Task<CachedResult<GitHubCompareResult>> CompareCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string @base,
        string head,
        CancellationToken cancellationToken = default);

    Task<CommitDetailAggregate?> GetCommitDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default);

    Task<CommitDetailAggregate?> GetCommitPrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default) =>
        GetCommitDetailAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            gitRef,
            cancellationToken);

    private static string? SafeRefreshError(Exception? error) => error is null
        ? null
        : JitHub.WinUI.Helpers.UserFacingError.For(
            error,
            JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
            "commit-section");
}
