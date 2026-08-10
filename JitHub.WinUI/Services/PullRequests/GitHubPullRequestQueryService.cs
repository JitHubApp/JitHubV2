using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.WinUI;

namespace JitHub.Services;

public sealed class GitHubPullRequestQueryService : IGitHubPullRequestQueryService
{
    private const int SectionPageSize = 100;
    private const int PullRequestListApiLimit = 5000;
    private const int PullRequestCommitApiLimit = 250;
    private readonly IGitHubQueryService _queryService;

    public GitHubPullRequestQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public Task<PullRequestPagedSection<GitHubBranch>> GetAllRepositoryBranchesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateMetadataPreviewSection(
                new[] { new GitHubBranch { Name = "main" }, new GitHubBranch { Name = "feature/native-pr" } }));
        }

        return LoadRepositoryMetadataSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            page => $"repos/{Escape(owner)}/{Escape(repositoryName)}/branches?per_page={SectionPageSize}&page={page}",
            Phase0GitHubJsonSerializerContext.Default.GitHubBranchArray,
            static branch => branch.Name,
            "pull-request-branches",
            cancellationToken);
    }

    public Task<PullRequestPagedSection<GitHubActor>> GetAllRepositoryCollaboratorsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        LoadRepositoryActorMetadataSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            "collaborators",
            "pull-request-collaborators",
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubActor>> GetAllRepositoryAssigneesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        LoadRepositoryActorMetadataSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            "assignees",
            "pull-request-assignees",
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubLabel>> GetAllRepositoryLabelsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateMetadataPreviewSection(
                new[] { new GitHubLabel { Id = 1, Name = "ui", Color = "7bc7a6" } }));
        }

        return LoadRepositoryMetadataSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            page => $"repos/{Escape(owner)}/{Escape(repositoryName)}/labels?per_page={SectionPageSize}&page={page}",
            Phase0GitHubJsonSerializerContext.Default.GitHubLabelArray,
            static label => label.Name,
            "pull-request-labels",
            cancellationToken);
    }

    public Task<PullRequestPagedSection<GitHubMilestone>> GetAllRepositoryMilestonesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateMetadataPreviewSection(
                new[] { new GitHubMilestone { Number = 1, Title = "vNext", State = "open" } }));
        }

        return LoadRepositoryMetadataSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            page => $"repos/{Escape(owner)}/{Escape(repositoryName)}/milestones?state=all&per_page={SectionPageSize}&page={page}",
            Phase0GitHubJsonSerializerContext.Default.GitHubMilestoneArray,
            static milestone => milestone.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "pull-request-milestones",
            cancellationToken);
    }

    public Task<CachedResult<GitHubPullRequest[]>> GetPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubPullRequestQueryOptions queryOptions,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewPullRequests(owner, repositoryName)));
        }

        string path = $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls?state={Escape(queryOptions.State)}&sort={Escape(queryOptions.Sort)}&direction={Escape(queryOptions.Direction)}&per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}";
        if (!string.IsNullOrWhiteSpace(queryOptions.Head))
        {
            path += $"&head={Escape(queryOptions.Head)}";
        }

        if (!string.IsNullOrWhiteSpace(queryOptions.Base))
        {
            path += $"&base={Escape(queryOptions.Base)}";
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                path,
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestArray,
                ["pull-requests", "pull-request-list", CreateRepositoryTag(owner, repositoryName)]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public Task<PullRequestPagedSection<GitHubPullRequest>> GetAllPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubPullRequestQueryOptions queryOptions,
        Action<PullRequestPagedSection<GitHubPullRequest>>? progress = null,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetPullRequestsAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                queryOptions,
                SectionPageSize,
                page,
                token),
            (page, token) => RefreshPullRequestsPageAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                queryOptions,
                page,
                token),
            static pullRequest => pullRequest.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PullRequestListApiLimit,
            progress,
            cancellationToken);

    public Task<CachedResult<GitHubPullRequest>> GetPullRequestAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewPullRequest(owner, repositoryName, pullRequestNumber)));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequest,
                ["pull-requests", "pull-request-detail", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public Task<CachedResult<GitHubIssue>> GetPullRequestIssueAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewIssue(owner, repositoryName, pullRequestNumber)));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
                ["pull-requests", "pull-request-issue", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public Task<CachedResult<GitHubIssueComment[]>> GetPullRequestCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewComments(pullRequestNumber)));
        }

        return ReadPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/comments?sort=created&direction=asc&per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
            ["pull-requests", "pull-request-comments", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.Visible,
            cancellationToken);
    }

    public Task<CachedResult<GitHubCommit[]>> GetPullRequestCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewCommits()));
        }

        return ReadPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/commits?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubCommitArray,
            ["pull-requests", "pull-request-commits", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.Visible,
            cancellationToken);
    }

    public Task<CachedResult<GitHubPullRequestReview[]>> GetPullRequestReviewsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewReviews(IsReplyIdentityAutomationScenario())));
        }

        return ReadPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/reviews?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestReviewArray,
            ["pull-requests", "pull-request-reviews", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.Visible,
            cancellationToken);
    }

    public Task<CachedResult<GitHubPullRequestReviewComment[]>> GetPullRequestReviewCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewReviewComments(IsReplyIdentityAutomationScenario())));
        }

        return ReadPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/comments?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestReviewCommentArray,
            ["pull-requests", "pull-request-review-comments", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.Visible,
            cancellationToken);
    }

    public Task<CachedResult<GitHubIssueEvent[]>> GetPullRequestTimelineEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewEvents()));
        }

        return ReadPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/events?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueEventArray,
            ["pull-requests", "pull-request-timeline", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.Visible,
            cancellationToken);
    }

    public Task<CachedResult<GitHubCommitFile[]>> GetPullRequestFilesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewFiles()));
        }

        return ReadPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/files?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubCommitFileArray,
            ["pull-requests", "pull-request-files", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
            QueryFetchPolicy.StaleFirst,
            pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh,
            cancellationToken);
    }

    public Task<PullRequestPagedSection<GitHubCommitFile>> GetAllPullRequestFilesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetPullRequestFilesAsync(
                accessToken, userId, owner, repositoryName, pullRequestNumber, SectionPageSize, page, token),
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/files?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubCommitFileArray,
                ["pull-requests", "pull-request-files", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.BackgroundRefresh,
                token),
            static file => file.Filename,
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubIssueComment>> GetAllPullRequestCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetPullRequestCommentsAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                SectionPageSize,
                page,
                token),
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/comments?sort=created&direction=asc&per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
                ["pull-requests", "pull-request-comments", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.BackgroundRefresh,
                token),
            static comment => comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubCommit>> GetAllPullRequestCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetPullRequestCommitsAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                SectionPageSize,
                page,
                token),
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/commits?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubCommitArray,
                ["pull-requests", "pull-request-commits", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.BackgroundRefresh,
                token),
            static commit => commit.Sha,
            PullRequestCommitApiLimit,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubPullRequestReview>> GetAllPullRequestReviewsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetPullRequestReviewsAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                SectionPageSize,
                page,
                token),
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/reviews?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestReviewArray,
                ["pull-requests", "pull-request-reviews", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.BackgroundRefresh,
                token),
            static review => review.Id > 0
                ? review.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> GetAllPullRequestReviewCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetPullRequestReviewCommentsAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                SectionPageSize,
                page,
                token),
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/comments?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestReviewCommentArray,
                ["pull-requests", "pull-request-review-comments", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.BackgroundRefresh,
                token),
            static comment => comment.Id > 0
                ? comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubIssueEvent>> GetAllPullRequestTimelineEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetPullRequestTimelineEventsAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                SectionPageSize,
                page,
                token),
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/events?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubIssueEventArray,
                ["pull-requests", "pull-request-timeline", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.BackgroundRefresh,
                token),
            static timelineEvent => timelineEvent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubReaction>> GetAllPullRequestReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadReactionSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            page => $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/reactions?per_page={SectionPageSize}&page={page}",
            ["pull-requests", "pull-request-reactions", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubReaction>> GetAllPullRequestCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default) =>
        LoadReactionSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            page => $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/comments/{commentId}/reactions?per_page={SectionPageSize}&page={page}",
            ["pull-requests", "pull-request-comment-reactions", $"issue-comment:{commentId}"],
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubReaction>> GetAllPullRequestReviewCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default) =>
        LoadReactionSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            page => $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/comments/{commentId}/reactions?per_page={SectionPageSize}&page={page}",
            ["pull-requests", "pull-request-review-comment-reactions", $"review-comment:{commentId}"],
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubIssueComment>> RefreshAllPullRequestCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/comments?sort=created&direction=asc&per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
                ["pull-requests", "pull-request-comments", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.Visible,
                token),
            refreshPageAsync: null,
            static comment => comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubCommit>> RefreshAllPullRequestCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/commits?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubCommitArray,
                ["pull-requests", "pull-request-commits", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.Visible,
                token),
            refreshPageAsync: null,
            static commit => commit.Sha,
            PullRequestCommitApiLimit,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubPullRequestReview>> RefreshAllPullRequestReviewsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/reviews?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestReviewArray,
                ["pull-requests", "pull-request-reviews", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.Visible,
                token),
            refreshPageAsync: null,
            static review => review.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> RefreshAllPullRequestReviewCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}/comments?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestReviewCommentArray,
                ["pull-requests", "pull-request-review-comments", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.Visible,
                token),
            refreshPageAsync: null,
            static comment => comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            apiLimit: null,
            progress: null,
            cancellationToken);

    public Task<PullRequestPagedSection<GitHubIssueEvent>> RefreshAllPullRequestTimelineEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => RefreshPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/events?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubIssueEventArray,
                ["pull-requests", "pull-request-timeline", CreatePullRequestTag(owner, repositoryName, pullRequestNumber)],
                GitHubRequestPriority.Visible,
                token),
            refreshPageAsync: null,
            static timelineEvent => timelineEvent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            apiLimit: null,
            progress: null,
            cancellationToken);

    public async Task<PullRequestOverviewAggregate?> GetPullRequestOverviewAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        Task<CachedResult<GitHubPullRequest>?> pullRequestTask = TryReadSectionAsync(
            () => GetPullRequestAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken));
        Task<CachedResult<GitHubIssue>?> issueTask = TryReadSectionAsync(
            () => GetPullRequestIssueAsync(accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken));
        await Task.WhenAll(pullRequestTask, issueTask);

        CachedResult<GitHubPullRequest>? pullRequest = await pullRequestTask;
        if (pullRequest?.Value is null)
        {
            return null;
        }

        CachedResult<GitHubIssue>? issue = await issueTask;
        return new PullRequestOverviewAggregate(
            pullRequest.Value,
            issue?.Value,
            ToState(pullRequest),
            ToState(issue));
    }

    public async Task<PullRequestDetailAggregate?> GetPullRequestDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        PullRequestOverviewAggregate? overview = await GetPullRequestOverviewAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            pullRequestNumber,
            cancellationToken);
        if (overview is null)
        {
            return null;
        }

        Task<PullRequestPagedSection<GitHubIssueComment>> commentsTask = GetAllPullRequestCommentsAsync(
            accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);
        Task<PullRequestPagedSection<GitHubCommit>> commitsTask = GetAllPullRequestCommitsAsync(
            accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);
        Task<PullRequestPagedSection<GitHubPullRequestReview>> reviewsTask = GetAllPullRequestReviewsAsync(
            accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);
        Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> reviewCommentsTask = GetAllPullRequestReviewCommentsAsync(
            accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);
        Task<PullRequestPagedSection<GitHubIssueEvent>> timelineTask = GetAllPullRequestTimelineEventsAsync(
            accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);
        Task<PullRequestPagedSection<GitHubCommitFile>> filesTask = GetAllPullRequestFilesAsync(
            accessToken, userId, owner, repositoryName, pullRequestNumber, cancellationToken);

        await Task.WhenAll(commentsTask, commitsTask, reviewsTask, reviewCommentsTask, timelineTask, filesTask);

        PullRequestPagedSection<GitHubIssueComment> comments = await commentsTask;
        PullRequestPagedSection<GitHubCommit> commits = await commitsTask;
        PullRequestPagedSection<GitHubPullRequestReview> reviews = await reviewsTask;
        PullRequestPagedSection<GitHubPullRequestReviewComment> reviewComments = await reviewCommentsTask;
        PullRequestPagedSection<GitHubIssueEvent> timeline = await timelineTask;
        PullRequestPagedSection<GitHubCommitFile> files = await filesTask;

        return new PullRequestDetailAggregate(
            overview.PullRequest,
            overview.Issue,
            comments.Items,
            commits.Items,
            reviews.Items,
            reviewComments.Items,
            timeline.Items,
            overview.PullRequestState,
            overview.IssueState,
            comments.State,
            commits.State,
            reviews.State,
            reviewComments.State,
            timeline.State,
            files.Items,
            files.State);
    }

    public async Task<PullRequestConversationAggregate?> GetPullRequestConversationAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        PullRequestOverviewAggregate? overview = await GetPullRequestOverviewAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            pullRequestNumber,
            cancellationToken);
        if (overview is null)
        {
            return null;
        }

        PullRequestPagedSection<GitHubIssueComment> comments = await GetAllPullRequestCommentsAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            pullRequestNumber,
            cancellationToken);
        return new PullRequestConversationAggregate(
            overview.PullRequest,
            overview.Issue,
            comments.Items,
            overview.PullRequestState,
            overview.IssueState,
            comments.State);
    }

    public async Task<PullRequestConversationAggregate?> GetPullRequestPrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return await GetPullRequestConversationAsync(
                accessToken,
                userId,
                owner,
                repositoryName,
                pullRequestNumber,
                cancellationToken);
        }

        string pullRequestTag = CreatePullRequestTag(owner, repositoryName, pullRequestNumber);
        Task<CachedResult<GitHubPullRequest>?> pullRequestTask = TryReadSectionAsync(() => _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequest,
                ["pull-requests", "pull-request-detail", pullRequestTag],
                GitHubRequestPriority.Prefetch),
            QueryFetchPolicy.StaleFirst,
            cancellationToken));
        Task<CachedResult<GitHubIssue>?> issueTask = TryReadSectionAsync(() => _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
                ["pull-requests", "pull-request-issue", pullRequestTag],
                GitHubRequestPriority.Prefetch),
            QueryFetchPolicy.StaleFirst,
            cancellationToken));
        Task<CachedResult<GitHubIssueComment[]>?> commentsTask = TryReadSectionAsync(() => ReadPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}/comments?sort=created&direction=asc&per_page={SectionPageSize}&page=1",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
            ["pull-requests", "pull-request-comments", pullRequestTag],
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.Prefetch,
            cancellationToken));
        await Task.WhenAll(pullRequestTask, issueTask, commentsTask);

        CachedResult<GitHubPullRequest>? pullRequest = await pullRequestTask;
        if (pullRequest?.Value is null)
        {
            return null;
        }

        CachedResult<GitHubIssue>? issue = await issueTask;
        CachedResult<GitHubIssueComment[]>? comments = await commentsTask;
        return new PullRequestConversationAggregate(
            pullRequest.Value,
            issue?.Value,
            comments?.Value ?? [],
            ToState(pullRequest),
            ToState(issue),
            ToState(comments));
    }

    public Task InvalidatePullRequestAsync(
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default) =>
        _queryService.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            [
                CreatePullRequestTag(owner, repositoryName, pullRequestNumber),
                CreateRepositoryTag(owner, repositoryName)
            ],
            cancellationToken);

    public async Task<PullRequestCapabilitySnapshot?> RefreshPullRequestCapabilitiesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return new PullRequestCapabilitySnapshot(
                CreatePreviewRepository(owner, repositoryName),
                CreatePreviewPullRequest(owner, repositoryName, pullRequestNumber),
                CreatePreviewIssue(owner, repositoryName, pullRequestNumber));
        }

        string repositoryTag = CreateRepositoryTag(owner, repositoryName);
        string pullRequestTag = CreatePullRequestTag(owner, repositoryName, pullRequestNumber);
        Task<CachedResult<GitHubRepository>> repositoryTask = _queryService.RefreshAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}",
                GitHubCachePolicy.RepositoryMetadataResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubRepository,
                ["repository", "repository-metadata", repositoryTag]),
            cancellationToken);
        Task<CachedResult<GitHubPullRequest>> pullRequestTask = _queryService.RefreshAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls/{pullRequestNumber}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequest,
                ["pull-requests", "pull-request-detail", pullRequestTag]),
            cancellationToken);
        Task<CachedResult<GitHubIssue>> issueTask = _queryService.RefreshAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{pullRequestNumber}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
                ["pull-requests", "pull-request-issue", pullRequestTag]),
            cancellationToken);

        await Task.WhenAll(repositoryTask, pullRequestTask, issueTask);
        CachedResult<GitHubRepository> repository = await repositoryTask;
        CachedResult<GitHubPullRequest> pullRequest = await pullRequestTask;
        CachedResult<GitHubIssue> issue = await issueTask;
        return repository.Value is null || pullRequest.Value is null || issue.Value is null
            ? null
            : new PullRequestCapabilitySnapshot(repository.Value, pullRequest.Value, issue.Value);
    }

    private static async Task<PullRequestPagedSection<T>> LoadPagedSectionAsync<T>(
        Func<int, CancellationToken, Task<CachedResult<T[]>>> getPageAsync,
        Func<int, CancellationToken, Task<CachedResult<T[]>>>? refreshPageAsync,
        Func<T, string> keySelector,
        int? apiLimit,
        Action<PullRequestPagedSection<T>>? progress,
        CancellationToken cancellationToken)
        where T : class
    {
        List<T> items = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        PullRequestSectionState? combinedState = null;
        int loadedPageCount = 0;
        PagedDataCompleteness completeness = PagedDataCompleteness.Partial;

        for (int pageNumber = 1; ; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CachedResult<T[]> page;
            try
            {
                page = await getPageAsync(pageNumber, cancellationToken);
                if (refreshPageAsync is not null && GitHubPagedReconciler.RequiresAuthoritativeRefresh(page))
                {
                    page = await refreshPageAsync(pageNumber, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                combinedState = MergeSectionStates(
                    combinedState,
                    new PullRequestSectionState(CacheState.Error, ErrorMessage: "Refresh failed."));
                break;
            }

            loadedPageCount = pageNumber;
            combinedState = MergeSectionStates(combinedState, ToState(page));
            T[] pageItems = page.Value ?? [];
            int previousItemCount = items.Count;
            foreach (T item in pageItems)
            {
                string key = keySelector(item);
                // GitHub IDs are authoritative when present. Preserve identityless rows in
                // page order so the presentation layer can assign an owner/ordinal identity.
                if (string.IsNullOrWhiteSpace(key) || keys.Add(key))
                {
                    items.Add(item);
                }
            }

            int effectiveApiLimit = apiLimit ?? int.MaxValue;
            bool apiLimited = items.Count >= effectiveApiLimit;
            if (apiLimited && items.Count > effectiveApiLimit)
            {
                items.RemoveRange(effectiveApiLimit, items.Count - effectiveApiLimit);
            }

            bool pageComplete = pageItems.Length < SectionPageSize;
            bool duplicatePage = items.Count == previousItemCount;
            PagedDataCompleteness progressCompleteness = apiLimited
                ? PagedDataCompleteness.ApiLimited
                : pageComplete
                    ? PagedDataCompleteness.Complete
                    : duplicatePage
                        ? PagedDataCompleteness.Partial
                        : PagedDataCompleteness.Loading;
            progress?.Invoke(CreatePagedSection(
                items,
                combinedState,
                loadedPageCount,
                progressCompleteness,
                apiLimit));

            if (apiLimited)
            {
                completeness = PagedDataCompleteness.ApiLimited;
                break;
            }

            if (pageComplete)
            {
                completeness = PagedDataCompleteness.Complete;
                break;
            }

            if (duplicatePage)
            {
                completeness = PagedDataCompleteness.Partial;
                break;
            }
        }

        PagedDataCompleteness finalCompleteness = combinedState?.ErrorMessage is not null
            ? PagedDataCompleteness.Partial
            : completeness;
        PullRequestPagedSection<T> finalResult = CreatePagedSection(
            items,
            combinedState,
            loadedPageCount,
            finalCompleteness,
            apiLimit);
        progress?.Invoke(finalResult);
        return finalResult;
    }

    private Task<PullRequestPagedSection<GitHubActor>> LoadRepositoryActorMetadataSectionAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string endpoint,
        string tag,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateMetadataPreviewSection(
                new[] { new GitHubActor { Id = 1, Login = "preview-author" } }));
        }

        return LoadRepositoryMetadataSectionAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            page => $"repos/{Escape(owner)}/{Escape(repositoryName)}/{endpoint}?per_page={SectionPageSize}&page={page}",
            Phase0GitHubJsonSerializerContext.Default.GitHubActorArray,
            static actor => actor.Login,
            tag,
            cancellationToken);
    }

    private Task<PullRequestPagedSection<GitHubReaction>> LoadReactionSectionAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        Func<int, string> pathFactory,
        string[] tags,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateMetadataPreviewSection(Array.Empty<GitHubReaction>()));
        }

        return LoadPagedSectionAsync(
            (page, token) => ReadPageAsync(
                accessToken,
                userId,
                pathFactory(page),
                Phase0GitHubJsonSerializerContext.Default.GitHubReactionArray,
                [.. tags, CreateRepositoryTag(owner, repositoryName)],
                QueryFetchPolicy.StaleFirst,
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh,
                token),
            (page, token) => ReadPageAsync(
                accessToken,
                userId,
                pathFactory(page),
                Phase0GitHubJsonSerializerContext.Default.GitHubReactionArray,
                [.. tags, CreateRepositoryTag(owner, repositoryName)],
                QueryFetchPolicy.NetworkOnly,
                GitHubRequestPriority.BackgroundRefresh,
                token),
            static reaction => reaction.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            apiLimit: null,
            progress: null,
            cancellationToken);
    }

    private Task<PullRequestPagedSection<T>> LoadRepositoryMetadataSectionAsync<T>(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        Func<int, string> pathFactory,
        JsonTypeInfo<T[]> jsonTypeInfo,
        Func<T, string> keySelector,
        string tag,
        CancellationToken cancellationToken)
        where T : class =>
        LoadPagedSectionAsync(
            (page, token) => ReadPageAsync(
                accessToken,
                userId,
                pathFactory(page),
                jsonTypeInfo,
                ["pull-requests", "pull-request-metadata", tag, CreateRepositoryTag(owner, repositoryName)],
                QueryFetchPolicy.StaleFirst,
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh,
                token),
            (page, token) => ReadPageAsync(
                accessToken,
                userId,
                pathFactory(page),
                jsonTypeInfo,
                ["pull-requests", "pull-request-metadata", tag, CreateRepositoryTag(owner, repositoryName)],
                QueryFetchPolicy.NetworkOnly,
                GitHubRequestPriority.BackgroundRefresh,
                token),
            keySelector,
            PullRequestListApiLimit,
            progress: null,
            cancellationToken);

    private static PullRequestPagedSection<T> CreateMetadataPreviewSection<T>(T[] items)
        where T : class =>
        new(
            items,
            new PullRequestSectionState(
                CacheState.Fresh,
                Completeness: PagedDataCompleteness.Complete,
                LoadedItemCount: items.Length),
            1,
            PagedDataCompleteness.Complete);

    private static PullRequestPagedSection<T> CreatePagedSection<T>(
        IReadOnlyCollection<T> items,
        PullRequestSectionState? combinedState,
        int loadedPageCount,
        PagedDataCompleteness completeness,
        int? apiLimit)
        where T : class
    {
        PullRequestSectionState state = (combinedState ??
            new PullRequestSectionState(CacheState.Miss, ErrorMessage: "Refresh failed.")) with
        {
            Completeness = completeness,
            LoadedItemCount = items.Count,
            ApiLimit = apiLimit
        };
        return new PullRequestPagedSection<T>(
            items.ToArray(),
            state,
            loadedPageCount,
            completeness,
            apiLimit);
    }

    private Task<CachedResult<GitHubPullRequest[]>> RefreshPullRequestsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubPullRequestQueryOptions queryOptions,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        string path = $"repos/{Escape(owner)}/{Escape(repositoryName)}/pulls?state={Escape(queryOptions.State)}&sort={Escape(queryOptions.Sort)}&direction={Escape(queryOptions.Direction)}&per_page={SectionPageSize}&page={Math.Max(1, pageNumber)}";
        if (!string.IsNullOrWhiteSpace(queryOptions.Head))
        {
            path += $"&head={Escape(queryOptions.Head)}";
        }

        if (!string.IsNullOrWhiteSpace(queryOptions.Base))
        {
            path += $"&base={Escape(queryOptions.Base)}";
        }

        return RefreshPageAsync(
            accessToken,
            userId,
            path,
            Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestArray,
            ["pull-requests", "pull-request-list", CreateRepositoryTag(owner, repositoryName)],
            pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh,
            cancellationToken);
    }

    private Task<CachedResult<T>> ReadPageAsync<T>(
        string accessToken,
        string userId,
        string relativePath,
        JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        QueryFetchPolicy fetchPolicy,
        GitHubRequestPriority priority,
        CancellationToken cancellationToken)
        where T : class
    {
        GitHubQuery<T> query = CreateQuery(
            accessToken,
            userId,
            relativePath,
            GitHubCachePolicy.MutableResource,
            jsonTypeInfo,
            tags,
            priority);
        return fetchPolicy == QueryFetchPolicy.NetworkOnly
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, fetchPolicy, cancellationToken);
    }

    private Task<CachedResult<T>> RefreshPageAsync<T>(
        string accessToken,
        string userId,
        string relativePath,
        JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        GitHubRequestPriority priority,
        CancellationToken cancellationToken)
        where T : class =>
        ReadPageAsync(
            accessToken,
            userId,
            relativePath,
            jsonTypeInfo,
            tags,
            QueryFetchPolicy.NetworkOnly,
            priority,
            cancellationToken);

    private static PullRequestSectionState MergeSectionStates(
        PullRequestSectionState? current,
        PullRequestSectionState incoming)
    {
        if (current is null)
        {
            return incoming;
        }

        string? errorMessage = current.ErrorMessage ?? incoming.ErrorMessage;
        CacheState cacheState = errorMessage is not null
            ? CacheState.Error
            : GetLessCurrentCacheState(current.CacheState, incoming.CacheState);
        return new PullRequestSectionState(
            cacheState,
            current.IsRefreshInProgress || incoming.IsRefreshInProgress,
            errorMessage);
    }

    private static CacheState GetLessCurrentCacheState(CacheState left, CacheState right)
    {
        static int Rank(CacheState state) => state switch
        {
            CacheState.Error => 5,
            CacheState.Miss => 4,
            CacheState.Stale => 3,
            CacheState.Refreshing => 2,
            _ => 1
        };

        return Rank(left) >= Rank(right) ? left : right;
    }

    private static async Task<CachedResult<T>?> TryReadSectionAsync<T>(Func<Task<CachedResult<T>>> read)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static PullRequestSectionState ToState<T>(CachedResult<T>? result)
        where T : class =>
        result is null
            ? new PullRequestSectionState(CacheState.Miss, ErrorMessage: "Refresh failed.")
            : new PullRequestSectionState(
                result.CacheState,
                result.IsRefreshInProgress,
                result.RefreshError is null
                    ? null
                    : JitHub.WinUI.Helpers.UserFacingError.For(
                        result.RefreshError,
                        JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                        "pull-request-section"));

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible)
        where T : class
    {
        string normalizedUserId = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            normalizedUserId,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(normalizedUserId, HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            priority);
    }

    private static CachedResult<T> CreateCached<T>(T value)
        where T : class =>
        new(value, CacheState.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 100);

    private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string CreateRepositoryTag(string owner, string repositoryName) =>
        $"repo:{owner}/{repositoryName}";

    private static string CreatePullRequestTag(string owner, string repositoryName, int pullRequestNumber) =>
        $"pr:{owner}/{repositoryName}#{pullRequestNumber}";

    private static GitHubPullRequest[] CreatePreviewPullRequests(string owner, string repositoryName) =>
        ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.CreatePullRequests(
                owner,
                repositoryName,
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.WorkItemCount))
            :
            [
                CreatePreviewPullRequest(owner, repositoryName, 12),
                CreatePreviewPullRequest(owner, repositoryName, 11),
                CreatePreviewPullRequest(owner, repositoryName, 10)
            ];

    private static GitHubRepository CreatePreviewRepository(string owner, string repositoryName) => new()
    {
        Name = repositoryName,
        FullName = $"{owner}/{repositoryName}",
        Owner = new GitHubRepositoryOwner { Login = owner },
        Permissions = new GitHubRepositoryPermissions { Pull = true }
    };

    private static GitHubPullRequest CreatePreviewPullRequest(string owner, string repositoryName, int number) =>
        new()
        {
            Id = number,
            Number = number,
            Title = number switch
            {
                12 => "Polish adaptive pull request workspace",
                11 => "Keep cached review threads visible during refresh",
                _ => "Add compact inspector drawer"
            },
            Body = "This preview pull request mirrors the production layout without calling GitHub.",
            State = "open",
            HtmlUrl = $"https://github.com/{owner}/{repositoryName}/pull/{number}",
            Comments = number % 4,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-number),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-number),
            User = new GitHubActor { Login = "preview-author" },
            Head = new GitHubPullRequestBranch { GitRef = "feature/adaptive-pr", Label = $"{owner}:feature/adaptive-pr" },
            Base = new GitHubPullRequestBranch { GitRef = "main", Label = $"{owner}:main" },
            Mergeable = true,
            MergeableState = "clean",
            RequestedReviewers = [new GitHubActor { Login = "reviewer" }]
        };

    private static GitHubIssue CreatePreviewIssue(string owner, string repositoryName, int number) =>
        new()
        {
            Id = number,
            Number = number,
            Title = CreatePreviewPullRequest(owner, repositoryName, number).Title,
            Body = "Preview mirrored issue metadata for this pull request.",
            State = "open",
            HtmlUrl = $"https://github.com/{owner}/{repositoryName}/pull/{number}",
            Comments = 2,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            User = new GitHubActor { Login = "preview-author" },
            Assignees = [new GitHubActor { Login = "preview-author" }],
            Labels = [new GitHubLabel { Name = "ui", Color = "7bc7a6" }],
            Reactions = new GitHubReactionSummary()
        };

    private static GitHubIssueComment[] CreatePreviewComments(int pullRequestNumber)
    {
        int count = ProductPerformanceReadiness.IsEnabled ? 8 : 1;
        return Enumerable.Range(0, count)
            .Select(index => new GitHubIssueComment
            {
                Id = (pullRequestNumber * 100) + index,
                Body = index == 0
                    ? "The drawer alignment looks good in the compact breakpoint."
                    : $"Performance conversation reply {index}: cached content remains stable while the detail workspace scrolls.",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-(index + 3)),
                UpdatedAt = DateTimeOffset.UtcNow.AddHours(-(index + 3)),
                User = new GitHubActor { Login = index % 2 == 0 ? "reviewer" : "maintainer" }
            })
            .ToArray();
    }

    private static GitHubCommit[] CreatePreviewCommits() =>
    [
        new()
        {
            Sha = "3f9a1c2",
            Commit = new GitHubCommitInfo
            {
                Message = "Polish pull request workspace",
                Author = new GitHubCommitSignature { Name = "preview-author", Date = DateTimeOffset.UtcNow.AddHours(-2) }
            }
        }
    ];

    private static GitHubCommitFile[] CreatePreviewFiles() =>
    [
        new()
        {
            Filename = "JitHub.WinUI/Views/Pages/RepoPullRequestPage.xaml",
            Status = "modified",
            Additions = 3,
            Deletions = 1,
            Changes = 4,
            Patch = "@@ -10,3 +10,5 @@\n <Grid>\n-  <TextBlock />\n+  <commit:CommitDiffViewer />\n+  <Button Content=\"Submit review\" />\n </Grid>"
        }
    ];

    private static GitHubPullRequestReview[] CreatePreviewReviews(bool includeIdentitylessItems)
    {
        List<GitHubPullRequestReview> reviews =
        [
            new()
            {
                Id = 1,
                Body = "Looks good after the responsive pass.",
                State = "APPROVED",
                SubmittedAt = DateTimeOffset.UtcNow.AddHours(-1),
                User = new GitHubActor { Login = "reviewer" }
            }
        ];
        if (includeIdentitylessItems)
        {
            reviews.InsertRange(
                0,
                [
                    new GitHubPullRequestReview
                    {
                        Id = 0,
                        NodeId = null,
                        Body = "",
                        State = "COMMENTED",
                        SubmittedAt = null,
                        User = null!
                    },
                    new GitHubPullRequestReview
                    {
                        Id = 0,
                        NodeId = null,
                        Body = "",
                        State = "PENDING",
                        SubmittedAt = null,
                        User = null!
                    }
                ]);
        }

        return [.. reviews];
    }

    private static GitHubPullRequestReviewComment[] CreatePreviewReviewComments(bool includeIdentitylessItems)
    {
        List<GitHubPullRequestReviewComment> comments =
        [
            new()
            {
                Id = 200,
                PullRequestReviewId = 1,
                Body = "This control now keeps the detail pane stable.",
                Path = "JitHub.WinUI/Views/Pages/RepoPullRequestPage.xaml",
                DiffHunk = "@@ -1,3 +1,3 @@\n+ AdaptiveWorkspace",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                User = new GitHubActor { Login = "reviewer" }
            }
        ];
        if (includeIdentitylessItems)
        {
            comments.InsertRange(
                0,
                [
                    new GitHubPullRequestReviewComment
                    {
                        Id = 0,
                        NodeId = null,
                        PullRequestReviewId = null,
                        Body = "Identityless review fixture A.",
                        Path = "fixture/a.cs",
                        CreatedAt = default,
                        User = null!
                    },
                    new GitHubPullRequestReviewComment
                    {
                        Id = 0,
                        NodeId = null,
                        PullRequestReviewId = null,
                        Body = "Identityless review fixture B.",
                        Path = "fixture/b.cs",
                        CreatedAt = default,
                        User = null!
                    }
                ]);
        }

        return [.. comments];
    }

    private static bool IsReplyIdentityAutomationScenario() =>
        AppDataPathPolicy.TryGetAutomationRoots(out _, out _) &&
        string.Equals(
            Program.CurrentLaunchOptions.Scenario,
            "pr-reply-identities",
            StringComparison.OrdinalIgnoreCase);

    private static GitHubIssueEvent[] CreatePreviewEvents() =>
    [
        new()
        {
            Id = 1,
            Event = "review_requested",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-4),
            Actor = new GitHubActor { Login = "preview-author" },
            RequestedReviewer = new GitHubActor { Login = "reviewer" }
        }
    ];
}
