using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum PullRequestWorkspaceSection
{
    Conversation,
    Files,
    Commits,
    Reviews,
    Timeline
}

public enum PullRequestReviewDecision
{
    Comment,
    Approve,
    RequestChanges
}

public sealed record PullRequestReviewSubmission(
    PullRequestReviewDecision Decision,
    string? Body);

public sealed record PullRequestCapabilitySnapshot(
    GitHubRepository Repository,
    GitHubPullRequest PullRequest,
    GitHubIssue? Issue);

public sealed record PullRequestSectionState(
    CacheState CacheState,
    bool IsRefreshInProgress = false,
    string? ErrorMessage = null,
    PagedDataCompleteness Completeness = PagedDataCompleteness.Complete,
    int LoadedItemCount = 0,
    int? ApiLimit = null);

public sealed record PullRequestPagedSection<T>(
    T[] Items,
    PullRequestSectionState State,
    int LoadedPageCount,
    PagedDataCompleteness Completeness = PagedDataCompleteness.Complete,
    int? ApiLimit = null)
    where T : class;

public sealed record PullRequestOverviewAggregate(
    GitHubPullRequest PullRequest,
    GitHubIssue? Issue,
    PullRequestSectionState PullRequestState,
    PullRequestSectionState IssueState);

public sealed record PullRequestDetailAggregate(
    GitHubPullRequest PullRequest,
    GitHubIssue? Issue,
    GitHubIssueComment[] Comments,
    GitHubCommit[] Commits,
    GitHubPullRequestReview[] Reviews,
    GitHubPullRequestReviewComment[] ReviewComments,
    GitHubIssueEvent[] TimelineEvents,
    PullRequestSectionState PullRequestState,
    PullRequestSectionState IssueState,
    PullRequestSectionState CommentsState,
    PullRequestSectionState CommitsState,
    PullRequestSectionState ReviewsState,
    PullRequestSectionState ReviewCommentsState,
    PullRequestSectionState TimelineState,
    GitHubCommitFile[]? Files = null,
    PullRequestSectionState? FilesState = null)
{
    public GitHubCommitFile[] ChangedFiles => Files ?? [];

    public PullRequestSectionState ChangedFilesState => FilesState ??
        new PullRequestSectionState(CacheState.Miss, ErrorMessage: "Changed files are unavailable.");
}

public sealed record PullRequestConversationAggregate(
    GitHubPullRequest PullRequest,
    GitHubIssue? Issue,
    GitHubIssueComment[] Comments,
    PullRequestSectionState PullRequestState,
    PullRequestSectionState IssueState,
    PullRequestSectionState CommentsState);

public interface IGitHubPullRequestQueryService
{
    Task<PullRequestPagedSection<GitHubBranch>> GetAllRepositoryBranchesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubBranch>());

    Task<PullRequestPagedSection<GitHubActor>> GetAllRepositoryCollaboratorsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubActor>());

    Task<PullRequestPagedSection<GitHubActor>> GetAllRepositoryAssigneesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubActor>());

    Task<PullRequestPagedSection<GitHubLabel>> GetAllRepositoryLabelsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubLabel>());

    Task<PullRequestPagedSection<GitHubMilestone>> GetAllRepositoryMilestonesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubMilestone>());

    Task<CachedResult<GitHubPullRequest[]>> GetPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubPullRequestQueryOptions queryOptions,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    async Task<PullRequestPagedSection<GitHubPullRequest>> GetAllPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubPullRequestQueryOptions queryOptions,
        Action<PullRequestPagedSection<GitHubPullRequest>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubPullRequest[]> page = await GetPullRequestsAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            queryOptions,
            100,
            1,
            cancellationToken);
        GitHubPullRequest[] items = page.Value ?? [];
        PagedDataCompleteness completeness = items.Length < 100
            ? PagedDataCompleteness.Complete
            : PagedDataCompleteness.Partial;
        var result = new PullRequestPagedSection<GitHubPullRequest>(
            items,
            new PullRequestSectionState(
                page.CacheState,
                page.IsRefreshInProgress,
                SafeRefreshError(page.RefreshError),
                completeness,
                items.Length),
            1,
            completeness);
        progress?.Invoke(result);
        return result;
    }

    Task<CachedResult<GitHubPullRequest>> GetPullRequestAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssue>> GetPullRequestIssueAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssueComment[]>> GetPullRequestCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubCommit[]>> GetPullRequestCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubPullRequestReview[]>> GetPullRequestReviewsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubPullRequestReviewComment[]>> GetPullRequestReviewCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubIssueEvent[]>> GetPullRequestTimelineEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubCommitFile[]>> GetPullRequestFilesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CachedResult<GitHubCommitFile[]>(
            [],
            CacheState.Miss,
            System.DateTimeOffset.UtcNow,
            System.DateTimeOffset.UtcNow));

    Task<PullRequestPagedSection<GitHubCommitFile>> GetAllPullRequestFilesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PullRequestPagedSection<GitHubCommitFile>(
            [],
            new PullRequestSectionState(CacheState.Miss, ErrorMessage: "Changed files are unavailable."),
            0,
            PagedDataCompleteness.Partial));

    Task<PullRequestPagedSection<GitHubIssueComment>> GetAllPullRequestCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<PullRequestPagedSection<GitHubCommit>> GetAllPullRequestCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<PullRequestPagedSection<GitHubPullRequestReview>> GetAllPullRequestReviewsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> GetAllPullRequestReviewCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<PullRequestPagedSection<GitHubIssueEvent>> GetAllPullRequestTimelineEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<PullRequestPagedSection<GitHubReaction>> GetAllPullRequestReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubReaction>());

    Task<PullRequestPagedSection<GitHubReaction>> GetAllPullRequestCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubReaction>());

    Task<PullRequestPagedSection<GitHubReaction>> GetAllPullRequestReviewCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateUnavailableRepositoryMetadataSection<GitHubReaction>());

    Task<PullRequestPagedSection<GitHubIssueComment>> RefreshAllPullRequestCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        GetAllPullRequestCommentsAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);

    Task<PullRequestPagedSection<GitHubCommit>> RefreshAllPullRequestCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        GetAllPullRequestCommitsAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);

    Task<PullRequestPagedSection<GitHubPullRequestReview>> RefreshAllPullRequestReviewsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        GetAllPullRequestReviewsAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);

    Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> RefreshAllPullRequestReviewCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        GetAllPullRequestReviewCommentsAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);

    Task<PullRequestPagedSection<GitHubIssueEvent>> RefreshAllPullRequestTimelineEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        GetAllPullRequestTimelineEventsAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);

    Task<PullRequestConversationAggregate?> GetPullRequestPrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        GetPullRequestConversationAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);

    Task<PullRequestOverviewAggregate?> GetPullRequestOverviewAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<PullRequestDetailAggregate?> GetPullRequestDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task<PullRequestConversationAggregate?> GetPullRequestConversationAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);

    Task InvalidatePullRequestAsync(
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task<PullRequestCapabilitySnapshot?> RefreshPullRequestCapabilitiesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) => Task.FromResult<PullRequestCapabilitySnapshot?>(null);

    private static string? SafeRefreshError(Exception? error) => error is null
        ? null
        : JitHub.WinUI.Helpers.UserFacingError.For(
            error,
            JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
            "pull-request-section");

    private static PullRequestPagedSection<T> CreateUnavailableRepositoryMetadataSection<T>()
        where T : class =>
        new(
            [],
            new PullRequestSectionState(
                CacheState.Miss,
                ErrorMessage: "Repository metadata is unavailable.",
                Completeness: PagedDataCompleteness.Partial),
            0,
            PagedDataCompleteness.Partial);
}
