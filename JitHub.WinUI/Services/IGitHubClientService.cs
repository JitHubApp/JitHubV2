using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public interface IGitHubClientService
{
    Uri CreateLoginUri(string clientId, string? state = null, string? redirectUri = null);

    Task<GitHubUser> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default);

    Task<GitHubRepository> GetRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task<GitHubRepository> GetRepositoryAsync(
        string token,
        long repositoryId,
        CancellationToken cancellationToken = default);

    Task<GitHubRepository> CreateRepositoryAsync(
        string token,
        GitHubRepositoryCreateOptions options,
        CancellationToken cancellationToken = default);

    Task<bool> IsRepositoryStarredAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task StarRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task UnstarRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task<bool> IsRepositoryWatchedAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task WatchRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task UnwatchRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task<GitHubRepository> ForkRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task DeleteRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubRepository>> GetRepositoriesForCurrentUserAsync(
        string token,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubActor>> GetStargazersAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubRepository>> GetStarredRepositoriesForUserAsync(
        string token,
        string userName,
        int pageSize = 100,
        int pageNumber = 1,
        string? sort = null,
        string? direction = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubRepository>> GetStarredRepositoriesForCurrentUserAsync(
        string token,
        int pageSize = 100,
        int pageNumber = 1,
        string? sort = null,
        string? direction = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubActivityEvent>> GetUserEventsAsync(
        string token,
        string userName,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubActivityEvent>> GetReceivedEventsAsync(
        string token,
        string userName,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubBranch>> GetBranchesAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubActor>> GetAssigneesAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubActor>> GetCollaboratorsAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<bool> IsCollaboratorAsync(
        string token,
        string owner,
        string name,
        string login,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubLabel>> GetLabelsAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubMilestone>> GetMilestonesAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubIssue>> GetIssuesAsync(
        string token,
        string owner,
        string name,
        int pageSize,
        int pageNumber = 1,
        GitHubIssueQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubIssue>> GetCurrentUserIssuesAsync(
        string token,
        int pageSize,
        int pageNumber = 1,
        GitHubIssueQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = default);

    Task<GitHubIssue> GetIssueAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubIssueComment>> GetIssueCommentsAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubIssueEvent>> GetIssueEventsAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<GitHubIssue> CreateIssueAsync(
        string token,
        string owner,
        string name,
        string title,
        string? body,
        CancellationToken cancellationToken = default);

    Task<GitHubIssue> UpdateIssueAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        string? title,
        string? body,
        string? state = null,
        CancellationToken cancellationToken = default);

    Task<GitHubIssue> UpdateIssueMetadataAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        IReadOnlyList<string> assignees,
        IReadOnlyList<string> labels,
        int? milestone,
        CancellationToken cancellationToken = default);

    Task<GitHubIssueComment> CreateIssueCommentAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        string body,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubReaction>> GetIssueReactionsAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubReaction>> GetIssueCommentReactionsAsync(
        string token,
        string owner,
        string name,
        long commentId,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task ReactToIssueAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        string reactionContent,
        CancellationToken cancellationToken = default);

    Task ReactToIssueCommentAsync(
        string token,
        string owner,
        string name,
        long commentId,
        string reactionContent,
        CancellationToken cancellationToken = default);

    Task DeleteIssueReactionAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        long reactionId,
        CancellationToken cancellationToken = default);

    Task DeleteIssueCommentReactionAsync(
        string token,
        string owner,
        string name,
        long commentId,
        long reactionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubPullRequest>> GetPullRequestsAsync(
        string token,
        string owner,
        string name,
        int pageSize,
        int pageNumber = 1,
        GitHubPullRequestQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequest> GetPullRequestAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubCommit>> GetPullRequestCommitsAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubPullRequestReview>> GetPullRequestReviewsAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubPullRequestReviewComment>> GetPullRequestReviewCommentsAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequestReviewComment> ReplyToPullRequestReviewCommentAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        long commentId,
        string body,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequest> CreatePullRequestAsync(
        string token,
        string owner,
        string name,
        string title,
        string head,
        string @base,
        string? body,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequest> UpdatePullRequestAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        string? title,
        string? body,
        string? state = null,
        CancellationToken cancellationToken = default);

    Task AddPullRequestReviewersAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        IReadOnlyList<string> reviewers,
        CancellationToken cancellationToken = default);

    Task RemovePullRequestReviewersAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        IReadOnlyList<string> reviewers,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubReaction>> GetPullRequestReviewCommentReactionsAsync(
        string token,
        string owner,
        string name,
        long commentId,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task ReactToPullRequestReviewCommentAsync(
        string token,
        string owner,
        string name,
        long commentId,
        string reactionContent,
        CancellationToken cancellationToken = default);

    Task DeletePullRequestReviewCommentReactionAsync(
        string token,
        string owner,
        string name,
        long commentId,
        long reactionId,
        CancellationToken cancellationToken = default);

    Task<GitHubPullRequestMergeResult> MergePullRequestAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        string mergeMethod,
        string? commitTitle,
        string? commitMessage,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubCommit>> GetCommitsAsync(
        string token,
        string owner,
        string name,
        string? gitRef,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<GitHubCommit> GetCommitAsync(
        string token,
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubCommitComment>> GetCommitCommentsAsync(
        string token,
        string owner,
        string name,
        string gitRef,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<GitHubRepositoryContent> GetRepositoryContentAsync(
        string token,
        string owner,
        string name,
        string path,
        string? gitRef = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubRepositoryContent>> GetRepositoryContentsAsync(
        string token,
        string owner,
        string name,
        string? path,
        string? gitRef = null,
        CancellationToken cancellationToken = default);

    Task<GitHubBlob> GetBlobAsync(
        string token,
        string owner,
        string name,
        string sha,
        CancellationToken cancellationToken = default);

    Task<GitHubCompareResult> CompareCommitsAsync(
        string token,
        string owner,
        string name,
        string @base,
        string head,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubCheckRun>> GetCheckRunsAsync(
        string token,
        string owner,
        string name,
        string gitRef,
        int pageSize = 100,
        int pageNumber = 1,
        string? checkName = null,
        string? status = null,
        string? filter = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubRepository>> SearchRepositoriesAsync(
        string token,
        string query,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);
}
