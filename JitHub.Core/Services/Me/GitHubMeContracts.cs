using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum GitHubMeIssueFilter
{
    Assigned,
    Created,
    Mentioned
}

public enum GitHubMePullRequestFilter
{
    Involves,
    ReviewRequested,
    Authored,
    Assigned
}

public enum GitHubMeWorkItemState
{
    Open,
    Closed,
    All
}

public sealed record IssuePrefetchAggregate(
    GitHubIssue? Issue,
    GitHubIssueComment[] Comments);

public interface IGitHubMeQueryService
{
    Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubSearchIssuesResponse>> RefreshIssuesPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubSearchIssuesResponse>> RefreshPullRequestsPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssue>> GetIssueDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int page,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssueComment[]>> RefreshIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int page,
        CancellationToken cancellationToken = default);

    async Task<IssuePrefetchAggregate> GetIssuePrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubIssue> issue = await GetIssueDetailAsync(
            accessToken, userId, owner, repositoryName, issueNumber, cancellationToken);
        CachedResult<GitHubIssueComment[]> comments = await GetIssueCommentsAsync(
            accessToken, userId, owner, repositoryName, issueNumber, 50, cancellationToken);
        return new IssuePrefetchAggregate(issue.Value, comments.Value ?? []);
    }

    Task<CachedResult<GitHubRepository[]>> GetStarredRepositoriesAsync(
        string accessToken,
        string userId,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubGist[]>> GetGistsAsync(
        string accessToken,
        string userId,
        int pageSize,
        CancellationToken cancellationToken = default);
}
