using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GitHubCommitQueryService : IGitHubCommitQueryService
{
    private const int SectionPageSize = 100;
    private const int MaximumPagedItems = 5000;
    private readonly IGitHubQueryService _queryService;

    public GitHubCommitQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public Task<CachedResult<GitHubBranch[]>> GetBranchesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pageSize,
        CancellationToken cancellationToken = default)
        => GetBranchesPageAsync(accessToken, userId, owner, repositoryName, pageSize, 1, cancellationToken);

    public Task<CachedResult<GitHubBranch[]>> GetBranchesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(pageNumber == 1 ? CreatePreviewBranches() : []));
        }

        return QueryBranchesPageAsync(
            accessToken, userId, owner, repositoryName, pageSize, pageNumber, refresh: false, cancellationToken);
    }

    public Task<CommitPagedSection<GitHubBranch>> GetAllBranchesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetBranchesPageAsync(
                accessToken, userId, owner, repositoryName, SectionPageSize, page, token),
            (page, token) => QueryBranchesPageAsync(
                accessToken, userId, owner, repositoryName, SectionPageSize, page, refresh: true, token),
            static branch => branch.Name,
            cancellationToken);

    public Task<CachedResult<GitHubCommit[]>> GetCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CommitListQueryOptions options,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        return QueryCommitsPageAsync(
            accessToken, userId, owner, repositoryName, options, pageSize, pageNumber, refresh: false, cancellationToken);
    }

    public Task<CommitPagedSection<GitHubCommit>> GetAllCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CommitListQueryOptions options,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => QueryCommitsPageAsync(
                accessToken, userId, owner, repositoryName, options, SectionPageSize, page, refresh: false, token),
            (page, token) => QueryCommitsPageAsync(
                accessToken, userId, owner, repositoryName, options, SectionPageSize, page, refresh: true, token),
            static commit => commit.Sha,
            cancellationToken);

    public Task<CachedResult<GitHubCommit>> GetCommitAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewDetailedCommit(gitRef)));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}",
                LooksLikeSha(gitRef) ? GitHubCachePolicy.ImmutableShaResource : GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubCommit,
                ["commits", "commit-detail", CreateCommitTag(owner, repositoryName, gitRef)]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public Task<CachedResult<GitHubCommitComment[]>> GetCommitCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return GetCommitCommentsPageAsync(
            accessToken, userId, owner, repositoryName, gitRef, pageSize, 1, cancellationToken);
    }

    public Task<CachedResult<GitHubCommitComment[]>> GetCommitCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        QueryArrayPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/comments?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubCommitCommentArray,
            ["commits", "commit-comments", CreateCommitTag(owner, repositoryName, gitRef)],
            pageNumber,
            refresh: false,
            GitHubAuthenticationConstants.IsPublicAccessToken(accessToken)
                ? pageNumber == 1 ? CreatePreviewComments(gitRef) : []
                : null,
            cancellationToken);

    public Task<CommitPagedSection<GitHubCommitComment>> GetAllCommitCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetCommitCommentsPageAsync(
                accessToken, userId, owner, repositoryName, gitRef, SectionPageSize, page, token),
            (page, token) => QueryArrayPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/comments?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubCommitCommentArray,
                ["commits", "commit-comments", CreateCommitTag(owner, repositoryName, gitRef)],
                page,
                refresh: true,
                previewValue: null,
                token),
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public Task<CachedResult<GitHubCombinedStatus>> GetCombinedStatusAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewCombinedStatus(gitRef)));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/status",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubCombinedStatus,
                ["commits", "commit-status", CreateCommitTag(owner, repositoryName, gitRef)]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public Task<CachedResult<GitHubCheckRun[]>> GetCheckRunsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return GetCheckRunsPageAsync(
            accessToken, userId, owner, repositoryName, gitRef, pageSize, 1, cancellationToken);
    }

    public Task<CachedResult<GitHubCheckRun[]>> GetCheckRunsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        QueryCheckRunsPageAsync(
            accessToken, userId, owner, repositoryName, gitRef, pageSize, pageNumber, refresh: false, cancellationToken);

    public Task<CommitPagedSection<GitHubCheckRun>> GetAllCheckRunsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => QueryCheckRunsPageAsync(
                accessToken, userId, owner, repositoryName, gitRef, SectionPageSize, page, refresh: false, token),
            (page, token) => QueryCheckRunsPageAsync(
                accessToken, userId, owner, repositoryName, gitRef, SectionPageSize, page, refresh: true, token),
            static check => check.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public Task<CachedResult<GitHubPullRequest[]>> GetAssociatedPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return GetAssociatedPullRequestsPageAsync(
            accessToken, userId, owner, repositoryName, gitRef, pageSize, 1, cancellationToken);
    }

    public Task<CachedResult<GitHubPullRequest[]>> GetAssociatedPullRequestsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken = default) =>
        QueryArrayPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/pulls?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestArray,
            ["commits", "commit-pulls", CreateCommitTag(owner, repositoryName, gitRef)],
            pageNumber,
            refresh: false,
            GitHubAuthenticationConstants.IsPublicAccessToken(accessToken)
                ? pageNumber == 1 ? CreatePreviewAssociatedPullRequests(gitRef) : []
                : null,
            cancellationToken);

    public Task<CommitPagedSection<GitHubPullRequest>> GetAllAssociatedPullRequestsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, token) => GetAssociatedPullRequestsPageAsync(
                accessToken, userId, owner, repositoryName, gitRef, SectionPageSize, page, token),
            (page, token) => QueryArrayPageAsync(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/pulls?per_page={SectionPageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestArray,
                ["commits", "commit-pulls", CreateCommitTag(owner, repositoryName, gitRef)],
                page,
                refresh: true,
                previewValue: null,
                token),
            static pullRequest => pullRequest.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public Task<CachedResult<GitHubCompareResult>> CompareCommitsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string @base,
        string head,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewCompare(@base, head)));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/compare/{Escape(@base)}...{Escape(head)}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubCompareResult,
                ["commits", "commit-compare", CreateRepositoryTag(owner, repositoryName)]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public async Task<CommitDetailAggregate?> GetCommitDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubCommit> commitResult = await GetCommitAsync(
            accessToken, userId, owner, repositoryName, gitRef, cancellationToken);
        if (commitResult.Value is null)
        {
            return null;
        }

        Task<CommitPagedSection<GitHubCommitComment>?> commentsTask = CaptureAsync(() =>
            GetAllCommitCommentsAsync(accessToken, userId, owner, repositoryName, gitRef, cancellationToken));
        Task<CachedResult<GitHubCombinedStatus>?> statusTask = CaptureAsync(() =>
            GetCombinedStatusAsync(accessToken, userId, owner, repositoryName, gitRef, cancellationToken));
        Task<CommitPagedSection<GitHubCheckRun>?> checkRunsTask = CaptureAsync(() =>
            GetAllCheckRunsAsync(accessToken, userId, owner, repositoryName, gitRef, cancellationToken));
        Task<CommitPagedSection<GitHubPullRequest>?> pullRequestsTask = CaptureAsync(() =>
            GetAllAssociatedPullRequestsAsync(accessToken, userId, owner, repositoryName, gitRef, cancellationToken));
        await Task.WhenAll(commentsTask, statusTask, checkRunsTask, pullRequestsTask);

        CommitPagedSection<GitHubCommitComment> comments = await commentsTask ?? FailedSection<GitHubCommitComment>();
        CachedResult<GitHubCombinedStatus>? statusResult = await statusTask;
        CommitPagedSection<GitHubCheckRun> checkRuns = await checkRunsTask ?? FailedSection<GitHubCheckRun>();
        CommitPagedSection<GitHubPullRequest> pullRequests = await pullRequestsTask ?? FailedSection<GitHubPullRequest>();

        return new CommitDetailAggregate(
            commitResult.Value,
            comments.Items,
            statusResult?.Value,
            checkRuns.Items,
            pullRequests.Items,
            CreateState(commitResult),
            comments.State,
            statusResult is null ? FailedState() : CreateState(statusResult),
            checkRuns.State,
            pullRequests.State);
    }

    public async Task<CommitDetailAggregate?> GetCommitPrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return await GetCommitDetailAsync(
                accessToken, userId, owner, repositoryName, gitRef, cancellationToken);
        }

        string commitTag = CreateCommitTag(owner, repositoryName, gitRef);
        Task<CachedResult<GitHubCommit>?> commitTask = CaptureAsync(() => _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}",
                LooksLikeSha(gitRef) ? GitHubCachePolicy.ImmutableShaResource : GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubCommit,
                ["commits", "commit-detail", commitTag],
                GitHubRequestPriority.Prefetch),
            QueryFetchPolicy.StaleFirst,
            cancellationToken));
        Task<CachedResult<GitHubCommitComment[]>?> commentsTask = CaptureAsync(() => QueryArrayPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/comments?per_page={SectionPageSize}&page=1",
            Phase0GitHubJsonSerializerContext.Default.GitHubCommitCommentArray,
            ["commits", "commit-comments", commitTag],
            pageNumber: 1,
            refresh: false,
            previewValue: null,
            cancellationToken,
            GitHubRequestPriority.Prefetch));
        Task<CachedResult<GitHubCombinedStatus>?> statusTask = CaptureAsync(() => _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/status",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubCombinedStatus,
                ["commits", "commit-status", commitTag],
                GitHubRequestPriority.Prefetch),
            QueryFetchPolicy.StaleFirst,
            cancellationToken));
        Task<CachedResult<GitHubCheckRun[]>?> checkRunsTask = CaptureAsync(() => QueryCheckRunsPageAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            gitRef,
            SectionPageSize,
            pageNumber: 1,
            refresh: false,
            cancellationToken,
            GitHubRequestPriority.Prefetch));
        Task<CachedResult<GitHubPullRequest[]>?> pullRequestsTask = CaptureAsync(() => QueryArrayPageAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/pulls?per_page={SectionPageSize}&page=1",
            Phase0GitHubJsonSerializerContext.Default.GitHubPullRequestArray,
            ["commits", "commit-pulls", commitTag],
            pageNumber: 1,
            refresh: false,
            previewValue: null,
            cancellationToken,
            GitHubRequestPriority.Prefetch));

        await Task.WhenAll(commitTask, commentsTask, statusTask, checkRunsTask, pullRequestsTask);
        CachedResult<GitHubCommit>? commit = await commitTask;
        if (commit?.Value is null)
        {
            return null;
        }

        CachedResult<GitHubCommitComment[]>? comments = await commentsTask;
        CachedResult<GitHubCombinedStatus>? status = await statusTask;
        CachedResult<GitHubCheckRun[]>? checkRuns = await checkRunsTask;
        CachedResult<GitHubPullRequest[]>? pullRequests = await pullRequestsTask;
        return new CommitDetailAggregate(
            commit.Value,
            comments?.Value ?? [],
            status?.Value,
            checkRuns?.Value ?? [],
            pullRequests?.Value ?? [],
            CreateState(commit),
            comments is null ? FailedState() : CreateState(comments),
            status is null ? FailedState() : CreateState(status),
            checkRuns is null ? FailedState() : CreateState(checkRuns),
            pullRequests is null ? FailedState() : CreateState(pullRequests));
    }

    private Task<CachedResult<GitHubCommit[]>> QueryCommitsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CommitListQueryOptions options,
        int pageSize,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(pageNumber == 1 ? CreatePreviewCommits() : []));
        }

        List<string> queryParts =
        [
            $"per_page={ClampPageSize(pageSize)}",
            $"page={Math.Max(1, pageNumber)}"
        ];
        AddOptionalQueryParameter(queryParts, "sha", options.GitRef);
        AddOptionalQueryParameter(queryParts, "path", options.Path);
        AddOptionalQueryParameter(queryParts, "author", options.Author);
        AddOptionalQueryParameter(queryParts, "since", options.Since?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        AddOptionalQueryParameter(queryParts, "until", options.Until?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        GitHubQuery<GitHubCommit[]> query = CreateQuery(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits?{string.Join("&", queryParts)}",
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubCommitArray,
            ["commits", "commit-list", CreateRepositoryTag(owner, repositoryName)],
            pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);
        return refresh
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    private Task<CachedResult<GitHubBranch[]>> QueryBranchesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int pageSize,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(pageNumber == 1 ? CreatePreviewBranches() : []));
        }

        GitHubQuery<GitHubBranch[]> query = CreateQuery(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/branches?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            GitHubCachePolicy.RepositoryMetadataResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubBranchArray,
            ["commits", "branches", CreateRepositoryTag(owner, repositoryName)],
            pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);
        return refresh
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    private Task<CachedResult<T[]>> QueryArrayPageAsync<T>(
        string accessToken,
        string userId,
        string relativePath,
        JsonTypeInfo<T[]> jsonTypeInfo,
        string[] tags,
        int pageNumber,
        bool refresh,
        T[]? previewValue,
        CancellationToken cancellationToken,
        GitHubRequestPriority? priorityOverride = null)
        where T : class
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(previewValue ?? []));
        }

        GitHubQuery<T[]> query = CreateQuery(
            accessToken,
            userId,
            relativePath,
            GitHubCachePolicy.MutableResource,
            jsonTypeInfo,
            tags,
            priorityOverride ??
                (pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh));
        return refresh
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    private async Task<CachedResult<GitHubCheckRun[]>> QueryCheckRunsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        string gitRef,
        int pageSize,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken,
        GitHubRequestPriority? priorityOverride = null)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return CreateCached(pageNumber == 1 ? CreatePreviewCheckRuns(gitRef) : []);
        }

        GitHubQuery<GitHubCheckRunResponse> query = CreateQuery(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/commits/{Escape(gitRef)}/check-runs?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubCheckRunResponse,
            ["commits", "commit-check-runs", CreateCommitTag(owner, repositoryName, gitRef)],
            priorityOverride ??
                (pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh));
        CachedResult<GitHubCheckRunResponse> result = refresh
            ? await _queryService.RefreshAsync(query, cancellationToken)
            : await _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
        return new CachedResult<GitHubCheckRun[]>(
            result.Value?.CheckRuns ?? [],
            result.CacheState,
            result.FetchedAt,
            result.StaleAfter,
            result.IsRefreshInProgress,
            result.RefreshError,
            result.ETag,
            result.LastModified);
    }

    private static async Task<CommitPagedSection<T>> LoadPagedSectionAsync<T>(
        Func<int, CancellationToken, Task<CachedResult<T[]>>> getPageAsync,
        Func<int, CancellationToken, Task<CachedResult<T[]>>> refreshPageAsync,
        Func<T, string> keySelector,
        CancellationToken cancellationToken)
        where T : class
    {
        SortedDictionary<int, T[]> pages = [];
        IReadOnlyList<T> items = [];
        CommitSectionState state = new(CacheState.Miss, Completeness: PagedDataCompleteness.Partial);
        PagedDataCompleteness completeness = PagedDataCompleteness.Partial;
        int loadedPages = 0;
        for (int page = 1; page <= MaximumPagedItems / SectionPageSize; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int countBeforePage = items.Count;
            CachedResult<T[]> result;
            try
            {
                result = await getPageAsync(page, cancellationToken);
                pages[page] = result.Value ?? [];
                items = FlattenPages(pages, keySelector);
                if (GitHubPagedReconciler.RequiresAuthoritativeRefresh(result))
                {
                    try
                    {
                        result = await refreshPageAsync(page, cancellationToken);
                        pages[page] = result.Value ?? [];
                        items = FlattenPages(pages, keySelector);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (GitHubAuthenticationException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        state = CreateState(result) with
                        {
                            ErrorMessage = JitHub.WinUI.Helpers.UserFacingError.For(
                                ex,
                                JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                                "commit-section"),
                            Completeness = PagedDataCompleteness.Partial,
                            LoadedItemCount = items.Count,
                            LoadedPageCount = page
                        };
                        loadedPages = page;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GitHubAuthenticationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                state = state with
                {
                    CacheState = CacheState.Error,
                    ErrorMessage = JitHub.WinUI.Helpers.UserFacingError.For(
                        ex,
                        JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                        "commit-section")
                };
                break;
            }

            loadedPages = page;
            T[] pageItems = result.Value ?? [];

            state = CreateState(result);
            if (pageItems.Length < SectionPageSize)
            {
                completeness = PagedDataCompleteness.Complete;
                break;
            }

            if (pageItems.Length > 0 && items.Count == countBeforePage)
            {
                completeness = PagedDataCompleteness.Partial;
                break;
            }

            if (page == MaximumPagedItems / SectionPageSize)
            {
                completeness = PagedDataCompleteness.ApiLimited;
            }
        }

        state = state with
        {
            Completeness = state.ErrorMessage is null ? completeness : PagedDataCompleteness.Partial,
            LoadedItemCount = items.Count,
            LoadedPageCount = loadedPages
        };
        return new CommitPagedSection<T>(items.ToArray(), state);
    }

    private static IReadOnlyList<T> FlattenPages<T>(
        IEnumerable<KeyValuePair<int, T[]>> pages,
        Func<T, string> keySelector)
        where T : class
    {
        List<T> items = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (KeyValuePair<int, T[]> page in pages)
        {
            foreach (T item in page.Value)
            {
                string key = keySelector(item);
                if (!string.IsNullOrWhiteSpace(key) && keys.Add(key))
                {
                    items.Add(item);
                    if (items.Count >= MaximumPagedItems)
                    {
                        return items;
                    }
                }
            }
        }

        return items;
    }

    private static async Task<T?> CaptureAsync<T>(Func<Task<T>> operation)
        where T : class
    {
        try
        {
            return await operation();
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

    private static CommitPagedSection<T> FailedSection<T>()
        where T : class =>
        new([], FailedState());

    private static CommitSectionState FailedState() =>
        new(CacheState.Error, ErrorMessage: "Refresh failed.", Completeness: PagedDataCompleteness.Partial);

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

    private static CommitSectionState CreateState<T>(CachedResult<T> result)
        where T : class =>
        new(
            result.CacheState,
            result.IsRefreshInProgress,
            result.RefreshError is null
                ? null
                : JitHub.WinUI.Helpers.UserFacingError.For(
                    result.RefreshError,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "commit-section"));

    private static void AddOptionalQueryParameter(List<string> queryParts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            queryParts.Add($"{name}={Escape(value)}");
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 100);

    private static string CreateRepositoryTag(string owner, string repositoryName) =>
        $"repo:{owner.Trim().ToLowerInvariant()}/{repositoryName.Trim().ToLowerInvariant()}";

    private static string CreateCommitTag(string owner, string repositoryName, string sha) =>
        $"{CreateRepositoryTag(owner, repositoryName)}:commit:{sha.Trim().ToLowerInvariant()}";

    private static bool LooksLikeSha(string value) =>
        value.Length is >= 7 and <= 40 && value.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static CachedResult<T> CreateCached<T>(T value)
        where T : class =>
        new(value, CacheState.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

    private static GitHubBranch[] CreatePreviewBranches() =>
    [
        new() { Name = "main" },
        new() { Name = "release/vnext" },
        new() { Name = "feature/native-diff" }
    ];

    private static GitHubCommit[] CreatePreviewCommits() =>
        ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.CreateCommits(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.CommitCount))
            :
            [
                CreatePreviewDetailedCommit("3f9a1c2"),
                CreatePreviewCommit("a7d4b91", "Keep repository navigation responsive during refresh", "maria", -4),
                CreatePreviewCommit("9c8fb77", "Add keyboard navigation to the CSV table", "devon", -6),
                CreatePreviewCommit("8e3d1aa", "Refresh contribution history in the background", "sam", -25),
                CreatePreviewCommit("1b2c3d4", "Handle stale pull request comments", "renan", -28)
            ];

    private static GitHubCommit CreatePreviewCommit(string sha, string message, string author, int hoursOffset) =>
        new()
        {
            Sha = sha,
            HtmlUrl = $"https://github.com/JitHubApp/JitHubV2/commit/{sha}",
            Author = new GitHubActor { Login = author, AvatarUrl = "ms-appx:///Assets/Octocat.png" },
            Commit = new GitHubCommitInfo
            {
                Message = message,
                Author = new GitHubCommitSignature { Name = author, Date = DateTimeOffset.UtcNow.AddHours(hoursOffset) },
                Committer = new GitHubCommitSignature { Name = author, Date = DateTimeOffset.UtcNow.AddHours(hoursOffset) },
                Verification = new GitHubCommitVerification { Verified = true, Reason = "valid", VerifiedAt = DateTimeOffset.UtcNow.AddHours(hoursOffset) }
            },
            Stats = new GitHubCommitStats { Additions = 24, Deletions = 5, Total = 29 },
            Parents = [new GitHubCommitParent { Sha = "8a7b6c1" }]
        };

    private static GitHubCommit CreatePreviewDetailedCommit(string gitRef)
    {
        GitHubCommit commit = CreatePreviewCommit(
            string.IsNullOrWhiteSpace(gitRef) ? "3f9a1c2" : gitRef,
            "Resolve repository Markdown images against the current file\n\nKeep relative image paths correct when Markdown is opened from nested folders.",
            "RenanYoy",
            -2);
        commit.Files =
        [
            new GitHubCommitFile
            {
                Filename = "JitHub.WinUI/Services/Markdown/GitHubMarkdownImageUrlResolver.cs",
                Status = "modified",
                Additions = 12,
                Deletions = 2,
                Changes = 14,
                Patch = "@@ -42,7 +42,12 @@ internal static Uri Resolve(string source, Uri repositoryBaseUri)\n   if (Uri.TryCreate(source, UriKind.Absolute, out Uri? absolute))\n   {\n     return absolute;\n   }\n-  return new Uri(repositoryBaseUri, source);\n+  Uri contentBaseUri = GitHubContentUri.ForCurrentFile(\n+      repositoryBaseUri);\n+  return new Uri(contentBaseUri, source);\n }"
            },
            new GitHubCommitFile
            {
                Filename = "JitHub.WinUI.Tests/Services/GitHubMarkdownImageUrlResolverTests.cs",
                Status = "modified",
                Additions = 9,
                Deletions = 0,
                Changes = 9,
                Patch = "@@ -18,6 +18,15 @@ public sealed class GitHubMarkdownImageUrlResolverTests\n+  [Fact]\n+  public void Relative_image_uses_the_current_file_directory()\n+  {\n+    Uri resolved = Resolve(\"../assets/preview.png\");\n+    Assert.Equal(ExpectedPreviewUri, resolved);\n+  }"
            }
        ];
        commit.Stats = new GitHubCommitStats { Additions = 21, Deletions = 2, Total = 23 };
        if (CommitDiffPerformanceFixture.IsEnabled)
        {
            commit.Files = CommitDiffPerformanceFixture.CreateFiles(commit.Sha);
            commit.Stats = CommitDiffPerformanceFixture.CreateStats(commit.Files);
        }

        return commit;
    }

    private static GitHubCommitComment[] CreatePreviewComments(string gitRef) =>
    [
        new()
        {
            Id = 1,
            CommitId = gitRef,
            Body = "This also fixes images in README files opened from nested folders.",
            Path = "JitHub.WinUI/Services/Markdown/GitHubMarkdownImageUrlResolver.cs",
            Position = 5,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            User = new GitHubActor { Login = "renanyoy", AvatarUrl = "ms-appx:///Assets/Octocat.png" }
        }
    ];

    private static GitHubCombinedStatus CreatePreviewCombinedStatus(string gitRef) =>
        new()
        {
            Sha = gitRef,
            State = "success",
            TotalCount = 2,
            Statuses =
            [
                new GitHubCommitStatus { Id = 1, Context = "ci/windows", State = "success", Description = "Build passed", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-40) },
                new GitHubCommitStatus { Id = 2, Context = "ci/tests", State = "success", Description = "Tests passed", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-35) }
            ]
        };

    private static GitHubCheckRun[] CreatePreviewCheckRuns(string gitRef) =>
    [
        new() { Id = 1, HeadSha = gitRef, Name = "Windows Debug", Status = "completed", Conclusion = "success", StartedAt = DateTimeOffset.UtcNow.AddHours(-1), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-40), App = new GitHubCheckRunApp { Name = "GitHub Actions" } },
        new() { Id = 2, HeadSha = gitRef, Name = "Unit tests", Status = "completed", Conclusion = "success", StartedAt = DateTimeOffset.UtcNow.AddHours(-1), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-35), App = new GitHubCheckRunApp { Name = "GitHub Actions" } }
    ];

    private static GitHubPullRequest[] CreatePreviewAssociatedPullRequests(string gitRef) =>
    [
        new()
        {
            Id = 139563,
            Number = 139563,
            Title = "Impeller: improve texture upload performance",
            State = "closed",
            HtmlUrl = "https://github.com/flutter/flutter/pull/139563",
            User = new GitHubActor { Login = "AlexDurham", AvatarUrl = "ms-appx:///Assets/Octocat.png" },
            Head = new GitHubPullRequestBranch { GitRef = "texture-mtl-retain" },
            Base = new GitHubPullRequestBranch { GitRef = "main" },
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Merged = true
        }
    ];

    private static GitHubCompareResult CreatePreviewCompare(string @base, string head) =>
        new()
        {
            Status = "ahead",
            AheadBy = 2,
            BehindBy = 0,
            TotalCommits = 2,
            Commits = CreatePreviewCommits(),
            Files = CreatePreviewDetailedCommit(head).Files
        };
}
