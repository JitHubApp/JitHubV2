using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed record IssueSectionState(
    CacheState CacheState,
    bool IsRefreshInProgress = false,
    string? ErrorMessage = null,
    PagedDataCompleteness Completeness = PagedDataCompleteness.Complete,
    int LoadedItemCount = 0,
    int LoadedPageCount = 0);

public sealed record IssuePagedSection<T>(
    T[] Items,
    IssueSectionState State)
    where T : class;

public sealed record IssueDetailAggregate(
    GitHubIssue Issue,
    GitHubIssueComment[] Comments,
    GitHubIssueEvent[] TimelineEvents,
    IssueSectionState IssueState,
    IssueSectionState CommentsState,
    IssueSectionState TimelineState);

public sealed record IssueRepositoryMetadata(
    GitHubActor[] Assignees,
    GitHubLabel[] Labels,
    GitHubMilestone[] Milestones,
    IssueSectionState AssigneesState,
    IssueSectionState LabelsState,
    IssueSectionState MilestonesState);

public interface IGitHubIssueQueryService
{
    Task<CachedResult<GitHubIssue[]>> GetIssuesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IssuePagedSection<GitHubIssue>> GetAllIssuesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        CancellationToken cancellationToken = default);

    async Task<IssuePagedSection<GitHubIssue>> GetAllIssuesProgressivelyAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        Func<IssuePagedSection<GitHubIssue>, CancellationToken, Task> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        IssuePagedSection<GitHubIssue> result = await GetAllIssuesAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            queryOptions,
            cancellationToken);
        await progress(result, cancellationToken);
        return result;
    }

    Task<CachedResult<GitHubIssue>> GetIssueAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssue>> RefreshIssueAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IssuePagedSection<GitHubIssueComment>> GetAllIssueCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    async Task<IssuePagedSection<GitHubIssueComment>> GetAllIssueCommentsProgressivelyAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        Func<IssuePagedSection<GitHubIssueComment>, CancellationToken, Task> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        IssuePagedSection<GitHubIssueComment> result = await GetAllIssueCommentsAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            issueNumber,
            cancellationToken);
        await progress(result, cancellationToken);
        return result;
    }

    async Task<IssuePrefetchAggregate> GetIssuePrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubIssue> issue = await GetIssueAsync(
            accessToken, userId, owner, repositoryName, issueNumber, cancellationToken);
        IssuePagedSection<GitHubIssueComment> comments = await GetAllIssueCommentsAsync(
            accessToken, userId, owner, repositoryName, issueNumber, cancellationToken);
        return new IssuePrefetchAggregate(
            issue.Value ?? throw new InvalidOperationException("The issue prefetch returned no issue."),
            comments.Items);
    }

    Task<CachedResult<GitHubIssueEvent[]>> GetIssueEventsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IssuePagedSection<GitHubIssueEvent>> GetAllIssueEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<IssueDetailAggregate?> GetIssueDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<IssueRepositoryMetadata> GetRepositoryMetadataAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubReaction[]>> GetIssueReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<IssuePagedSection<GitHubReaction>> GetAllIssueReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubReaction[]>> GetIssueCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default);

    Task<IssuePagedSection<GitHubReaction>> GetAllIssueCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default);

    Task InvalidateIssueAsync(
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task InvalidateRepositoryIssuesAsync(
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default);
}
