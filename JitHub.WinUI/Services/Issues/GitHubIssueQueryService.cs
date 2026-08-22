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

public sealed class GitHubIssueQueryService : IGitHubIssueQueryService
{
    private const int PageSize = 100;
    private const int MaximumListItems = 5000;
    private readonly IGitHubQueryService _queryService;

    public GitHubIssueQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public async Task<CachedResult<GitHubIssue[]>> GetIssuesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        CachedResult<GitHubIssue[]> result = await GetRawIssuesPageAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            queryOptions,
            pageSize,
            pageNumber,
            refresh: false,
            cancellationToken);
        GitHubIssue[] issuesOnly = (result.Value ?? []).Where(static issue => !issue.IsPullRequest).ToArray();
        return result with { Value = issuesOnly };
    }

    public Task<IssuePagedSection<GitHubIssue>> GetAllIssuesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        CancellationToken cancellationToken = default) =>
        LoadAllIssuesAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            queryOptions,
            progress: null,
            cancellationToken);

    public Task<IssuePagedSection<GitHubIssue>> GetAllIssuesProgressivelyAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        Func<IssuePagedSection<GitHubIssue>, CancellationToken, Task> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return LoadAllIssuesAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            queryOptions,
            progress,
            cancellationToken);
    }

    private async Task<IssuePagedSection<GitHubIssue>> LoadAllIssuesAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        Func<IssuePagedSection<GitHubIssue>, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        SortedDictionary<int, GitHubIssue[]> pages = [];
        IReadOnlyList<GitHubIssue> issues = [];
        PagedDataCompleteness completeness = PagedDataCompleteness.Partial;
        int loadedPages = 0;
        CacheState cacheState = CacheState.Miss;
        bool refreshInProgress = false;
        string? errorMessage = null;
        for (int page = 1; page <= MaximumListItems / PageSize; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int previousIssueCount = issues.Count;
            CachedResult<GitHubIssue[]> rawPage;
            try
            {
                rawPage = await GetRawIssuesPageAsync(
                    accessToken, userId, owner, repositoryName, queryOptions, PageSize, page, refresh: false, cancellationToken);
                pages[page] = rawPage.Value ?? [];
                issues = FlattenIssuePages(pages);
                loadedPages = page;
                cacheState = rawPage.CacheState;
                refreshInProgress |= rawPage.IsRefreshInProgress;
                await PublishPagedProgressAsync(
                    progress,
                    issues,
                    ToState(rawPage) with
                    {
                        Completeness = PagedDataCompleteness.Loading,
                        LoadedItemCount = issues.Count,
                        LoadedPageCount = loadedPages
                    },
                    cancellationToken);

                if (GitHubPagedReconciler.RequiresAuthoritativeRefresh(rawPage))
                {
                    rawPage = await GetRawIssuesPageAsync(
                        accessToken, userId, owner, repositoryName, queryOptions, PageSize, page, refresh: true, cancellationToken);
                    pages[page] = rawPage.Value ?? [];
                    issues = FlattenIssuePages(pages);
                    cacheState = rawPage.CacheState;
                    refreshInProgress |= rawPage.IsRefreshInProgress;
                    await PublishPagedProgressAsync(
                        progress,
                        issues,
                        ToState(rawPage) with
                        {
                            Completeness = PagedDataCompleteness.Loading,
                            LoadedItemCount = issues.Count,
                            LoadedPageCount = loadedPages
                        },
                        cancellationToken);
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
                errorMessage = JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "issue-list");
                completeness = PagedDataCompleteness.Partial;
                break;
            }

            GitHubIssue[] rawItems = rawPage.Value ?? [];
            int rawCount = rawItems.Length;

            if (rawCount < PageSize)
            {
                completeness = PagedDataCompleteness.Complete;
                break;
            }

            if (issues.Count == previousIssueCount && rawItems.All(static item => !item.IsPullRequest))
            {
                completeness = PagedDataCompleteness.Partial;
                break;
            }

            if (page == MaximumListItems / PageSize)
            {
                completeness = PagedDataCompleteness.ApiLimited;
            }
        }

        var result = new IssuePagedSection<GitHubIssue>(
            issues.ToArray(),
            new IssueSectionState(
                cacheState,
                refreshInProgress,
                errorMessage,
                completeness,
                issues.Count,
                loadedPages));
        await PublishPagedProgressAsync(progress, result.Items, result.State, cancellationToken);
        return result;
    }

    public Task<CachedResult<GitHubIssue>> GetIssueAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{issueNumber}",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
            ["issues", "issue-detail", IssueTag(owner, repositoryName, issueNumber)],
            cancellationToken);

    public Task<CachedResult<GitHubIssue>> RefreshIssueAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default) =>
        RefreshAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{issueNumber}",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
            ["issues", "issue-detail", IssueTag(owner, repositoryName, issueNumber)],
            cancellationToken);

    public Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default) =>
        GetIssueCommentsPageAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            issueNumber,
            pageSize,
            pageNumber,
            refresh: false,
            cancellationToken);

    private Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken) =>
        ReadPageAsync(
            refresh,
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{issueNumber}/comments?sort=created&direction=asc&per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
            ["issues", "issue-comments", IssueTag(owner, repositoryName, issueNumber)],
            cancellationToken,
            pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);

    public Task<IssuePagedSection<GitHubIssueComment>> GetAllIssueCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, refresh, token) => GetIssueCommentsPageAsync(
                accessToken, userId, owner, repositoryName, issueNumber, PageSize, page, refresh, token),
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken,
            progress: null);

    public Task<IssuePagedSection<GitHubIssueComment>> GetAllIssueCommentsProgressivelyAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        Func<IssuePagedSection<GitHubIssueComment>, CancellationToken, Task> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return LoadPagedSectionAsync(
            (page, refresh, token) => GetIssueCommentsPageAsync(
                accessToken, userId, owner, repositoryName, issueNumber, PageSize, page, refresh, token),
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken,
            progress);
    }

    public async Task<IssuePrefetchAggregate> GetIssuePrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        string issueTag = IssueTag(owner, repositoryName, issueNumber);
        Task<CachedResult<GitHubIssue>> issueTask = ReadAsync(
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{issueNumber}",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
            ["issues", "issue-detail", issueTag],
            cancellationToken,
            GitHubRequestPriority.Prefetch);
        Task<CachedResult<GitHubIssueComment[]>> commentsTask = ReadPageAsync(
            refresh: false,
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{issueNumber}/comments?sort=created&direction=asc&per_page={PageSize}&page=1",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
            ["issues", "issue-comments", issueTag],
            cancellationToken,
            GitHubRequestPriority.Prefetch);
        await Task.WhenAll(issueTask, commentsTask);

        CachedResult<GitHubIssue> issue = await issueTask;
        return new IssuePrefetchAggregate(
            issue.Value ?? throw new InvalidOperationException("The issue prefetch returned no issue."),
            (await commentsTask).Value ?? []);
    }

    public Task<CachedResult<GitHubIssueEvent[]>> GetIssueEventsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default) =>
        GetIssueEventsPageAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            issueNumber,
            pageSize,
            pageNumber,
            refresh: false,
            cancellationToken);

    private Task<CachedResult<GitHubIssueEvent[]>> GetIssueEventsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken) =>
        ReadPageAsync(
            refresh,
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{issueNumber}/events?per_page={ClampPageSize(pageSize)}&page={Math.Max(1, pageNumber)}",
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueEventArray,
            ["issues", "issue-timeline", IssueTag(owner, repositoryName, issueNumber)],
            cancellationToken,
            pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);

    public Task<IssuePagedSection<GitHubIssueEvent>> GetAllIssueEventsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, refresh, token) => GetIssueEventsPageAsync(
                accessToken, userId, owner, repositoryName, issueNumber, PageSize, page, refresh, token),
            static item => item.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public async Task<IssueDetailAggregate?> GetIssueDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        Task<CachedResult<GitHubIssue>?> issueTask = CaptureAsync(() => GetIssueAsync(
            accessToken, userId, owner, repositoryName, issueNumber, cancellationToken));
        Task<IssuePagedSection<GitHubIssueComment>?> commentsTask = CaptureAsync(() => GetAllIssueCommentsAsync(
            accessToken, userId, owner, repositoryName, issueNumber, cancellationToken));
        Task<IssuePagedSection<GitHubIssueEvent>?> eventsTask = CaptureAsync(() => GetAllIssueEventsAsync(
            accessToken, userId, owner, repositoryName, issueNumber, cancellationToken));
        await Task.WhenAll(issueTask, commentsTask, eventsTask);

        CachedResult<GitHubIssue>? issue = await issueTask;
        if (issue?.Value is null)
        {
            return null;
        }

        IssuePagedSection<GitHubIssueComment> comments = await commentsTask ?? FailedSection<GitHubIssueComment>();
        IssuePagedSection<GitHubIssueEvent> events = await eventsTask ?? FailedSection<GitHubIssueEvent>();
        return new IssueDetailAggregate(
            issue.Value,
            comments.Items,
            events.Items,
            ToState(issue),
            comments.State,
            events.State);
    }

    public async Task<IssueRepositoryMetadata> GetRepositoryMetadataAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default)
    {
        Task<IssuePagedSection<GitHubActor>> assigneesTask = LoadPagedSectionAsync(
            (page, refresh, token) => ReadPageAsync(
                refresh,
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/assignees?per_page={PageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubActorArray,
                ["issues", "issue-metadata", RepositoryTag(owner, repositoryName)],
                token,
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh,
                GitHubCachePolicy.RepositoryMetadataResource),
            static actor => actor.Id > 0 ? actor.Id.ToString(CultureInfo.InvariantCulture) : actor.Login,
            cancellationToken);
        Task<IssuePagedSection<GitHubLabel>> labelsTask = LoadPagedSectionAsync(
            (page, refresh, token) => ReadPageAsync(
                refresh,
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/labels?per_page={PageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubLabelArray,
                ["issues", "issue-metadata", RepositoryTag(owner, repositoryName)],
                token,
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh,
                GitHubCachePolicy.RepositoryMetadataResource),
            static label => label.Id > 0 ? label.Id.ToString(CultureInfo.InvariantCulture) : label.Name,
            cancellationToken);
        Task<IssuePagedSection<GitHubMilestone>> milestonesTask = LoadPagedSectionAsync(
            (page, refresh, token) => ReadPageAsync(
                refresh,
                accessToken,
                userId,
                $"repos/{Escape(owner)}/{Escape(repositoryName)}/milestones?state=all&per_page={PageSize}&page={page}",
                Phase0GitHubJsonSerializerContext.Default.GitHubMilestoneArray,
                ["issues", "issue-metadata", RepositoryTag(owner, repositoryName)],
                token,
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh,
                GitHubCachePolicy.RepositoryMetadataResource),
            static milestone => milestone.Number.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        await Task.WhenAll(assigneesTask, labelsTask, milestonesTask);
        IssuePagedSection<GitHubActor> assignees = await assigneesTask;
        IssuePagedSection<GitHubLabel> labels = await labelsTask;
        IssuePagedSection<GitHubMilestone> milestones = await milestonesTask;
        return new IssueRepositoryMetadata(
            assignees.Items,
            labels.Items,
            milestones.Items,
            assignees.State,
            labels.State,
            milestones.State);
    }

    public Task<CachedResult<GitHubReaction[]>> GetIssueReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default) =>
        GetIssueReactionsPageAsync(accessToken, userId, owner, repositoryName, issueNumber, 1, refresh: false, cancellationToken);

    public Task<IssuePagedSection<GitHubReaction>> GetAllIssueReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, refresh, token) => GetIssueReactionsPageAsync(
                accessToken, userId, owner, repositoryName, issueNumber, page, refresh, token),
            static reaction => reaction.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public Task<CachedResult<GitHubReaction[]>> GetIssueCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default) =>
        GetIssueCommentReactionsPageAsync(accessToken, userId, owner, repositoryName, commentId, 1, refresh: false, cancellationToken);

    public Task<IssuePagedSection<GitHubReaction>> GetAllIssueCommentReactionsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        CancellationToken cancellationToken = default) =>
        LoadPagedSectionAsync(
            (page, refresh, token) => GetIssueCommentReactionsPageAsync(
                accessToken, userId, owner, repositoryName, commentId, page, refresh, token),
            static reaction => reaction.Id.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    private Task<CachedResult<GitHubReaction[]>> GetIssueReactionsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken) =>
        ReadReactionPageAsync(
            refresh,
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/{issueNumber}/reactions?per_page={PageSize}&page={pageNumber}",
            ["issues", "issue-reactions", IssueTag(owner, repositoryName, issueNumber)],
            pageNumber,
            cancellationToken);

    private Task<CachedResult<GitHubReaction[]>> GetIssueCommentReactionsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        long commentId,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken) =>
        ReadReactionPageAsync(
            refresh,
            accessToken,
            userId,
            $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues/comments/{commentId}/reactions?per_page={PageSize}&page={pageNumber}",
            ["issues", "issue-comment-reactions", RepositoryTag(owner, repositoryName)],
            pageNumber,
            cancellationToken);

    private Task<CachedResult<GitHubReaction[]>> ReadReactionPageAsync(
        bool refresh,
        string accessToken,
        string userId,
        string relativePath,
        string[] tags,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        GitHubRequestPriority priority = pageNumber == 1
            ? GitHubRequestPriority.Visible
            : GitHubRequestPriority.BackgroundRefresh;
        return refresh
            ? RefreshAsync(
                accessToken,
                userId,
                relativePath,
                Phase0GitHubJsonSerializerContext.Default.GitHubReactionArray,
                tags,
                cancellationToken,
                priority,
                acceptMediaType: "application/vnd.github+json")
            : ReadAsync(
                accessToken,
                userId,
                relativePath,
                Phase0GitHubJsonSerializerContext.Default.GitHubReactionArray,
                tags,
                cancellationToken,
                priority,
                acceptMediaType: "application/vnd.github+json");
    }

    public Task InvalidateIssueAsync(
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default) =>
        _queryService.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            [IssueTag(owner, repositoryName, issueNumber), RepositoryTag(owner, repositoryName)],
            cancellationToken);

    public Task InvalidateRepositoryIssuesAsync(
        string userId,
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default) =>
        _queryService.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            [RepositoryTag(owner, repositoryName)],
            cancellationToken);

    private async Task<CachedResult<GitHubIssue[]>> RefreshIssuesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        int pageSize,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        CachedResult<GitHubIssue[]> result = await GetRawIssuesPageAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            queryOptions,
            pageSize,
            pageNumber,
            refresh: true,
            cancellationToken);
        return result with { Value = (result.Value ?? []).Where(static issue => !issue.IsPullRequest).ToArray() };
    }

    private Task<CachedResult<GitHubIssue[]>> GetRawIssuesPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions queryOptions,
        int pageSize,
        int pageNumber,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(pageNumber == 1 ? CreatePreviewIssues(owner, repositoryName) : []));
        }

        GitHubQuery<GitHubIssue[]> query = CreateQuery(
            accessToken,
            userId,
            BuildIssueListPath(owner, repositoryName, queryOptions, pageSize, pageNumber),
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueArray,
            ["issues", "issue-list", RepositoryTag(owner, repositoryName)],
            pageNumber == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);
        return refresh
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    private async Task<IssuePagedSection<T>> LoadPagedSectionAsync<T>(
        Func<int, bool, CancellationToken, Task<CachedResult<T[]>>> getPageAsync,
        Func<T, string> keySelector,
        CancellationToken cancellationToken,
        Func<IssuePagedSection<T>, CancellationToken, Task>? progress = null)
        where T : class
    {
        SortedDictionary<int, T[]> pages = [];
        IReadOnlyList<T> items = [];
        IssueSectionState? state = null;
        PagedDataCompleteness completeness = PagedDataCompleteness.Partial;
        int loadedPages = 0;
        for (int page = 1; page <= MaximumListItems / PageSize; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int countBeforePage = items.Count;
            CachedResult<T[]> result;
            try
            {
                result = await getPageAsync(page, false, cancellationToken);
                pages[page] = result.Value ?? [];
                items = FlattenPages(pages, keySelector);
                loadedPages = page;
                await PublishPagedProgressAsync(
                    progress,
                    items,
                    ToState(result) with
                    {
                        Completeness = PagedDataCompleteness.Loading,
                        LoadedItemCount = items.Count,
                        LoadedPageCount = loadedPages
                    },
                    cancellationToken);
                if (GitHubPagedReconciler.RequiresAuthoritativeRefresh(result))
                {
                    try
                    {
                        result = await getPageAsync(page, true, cancellationToken);
                        pages[page] = result.Value ?? [];
                        items = FlattenPages(pages, keySelector);
                        await PublishPagedProgressAsync(
                            progress,
                            items,
                            ToState(result) with
                            {
                                Completeness = PagedDataCompleteness.Loading,
                                LoadedItemCount = items.Count,
                                LoadedPageCount = loadedPages
                            },
                            cancellationToken);
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
                        state = ToState(result) with
                        {
                            ErrorMessage = JitHub.WinUI.Helpers.UserFacingError.For(
                                ex,
                                JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                                "issue-section"),
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
                state = new IssueSectionState(
                    CacheState.Error,
                    ErrorMessage: JitHub.WinUI.Helpers.UserFacingError.For(
                        ex,
                        items.Count > 0
                            ? JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh
                            : JitHub.WinUI.Helpers.UserFacingErrorKind.Loading,
                        "issue-section"),
                    Completeness: PagedDataCompleteness.Partial,
                    LoadedItemCount: items.Count,
                    LoadedPageCount: loadedPages);
                break;
            }

            T[] pageItems = result.Value ?? [];

            state = ToState(result) with
            {
                LoadedItemCount = items.Count,
                LoadedPageCount = loadedPages
            };
            if (pageItems.Length < PageSize)
            {
                completeness = PagedDataCompleteness.Complete;
                break;
            }

            if (pageItems.Length > 0 && items.Count == countBeforePage)
            {
                completeness = PagedDataCompleteness.Partial;
                break;
            }

            if (items.Count >= MaximumListItems)
            {
                completeness = PagedDataCompleteness.ApiLimited;
                break;
            }
        }

        state = (state ?? new IssueSectionState(CacheState.Miss, ErrorMessage: "No issue data is available.")) with
        {
            Completeness = completeness,
            LoadedItemCount = items.Count,
            LoadedPageCount = loadedPages
        };
        var resultSection = new IssuePagedSection<T>(items.ToArray(), state);
        await PublishPagedProgressAsync(progress, resultSection.Items, resultSection.State, cancellationToken);
        return resultSection;
    }

    private static async Task PublishPagedProgressAsync<T>(
        Func<IssuePagedSection<T>, CancellationToken, Task>? progress,
        IReadOnlyList<T> items,
        IssueSectionState state,
        CancellationToken cancellationToken)
        where T : class
    {
        if (progress is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await progress(new IssuePagedSection<T>(items.ToArray(), state), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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
                    if (items.Count >= MaximumListItems)
                    {
                        return items;
                    }
                }
            }
        }

        return items;
    }

    private static IReadOnlyList<GitHubIssue> FlattenIssuePages(
        IEnumerable<KeyValuePair<int, GitHubIssue[]>> pages)
    {
        List<GitHubIssue> issues = [];
        HashSet<long> keys = [];
        foreach (KeyValuePair<int, GitHubIssue[]> page in pages)
        {
            foreach (GitHubIssue issue in page.Value.Where(static item => !item.IsPullRequest))
            {
                long key = issue.Id != 0 ? issue.Id : issue.Number;
                if (keys.Add(key))
                {
                    issues.Add(issue);
                    if (issues.Count >= MaximumListItems)
                    {
                        return issues;
                    }
                }
            }
        }

        return issues;
    }

    private Task<CachedResult<T[]>> ReadPageAsync<T>(
        bool refresh,
        string accessToken,
        string userId,
        string relativePath,
        JsonTypeInfo<T[]> jsonTypeInfo,
        string[] tags,
        CancellationToken cancellationToken,
        GitHubRequestPriority priority,
        string resourceKind = GitHubCachePolicy.MutableResource) where T : class =>
        refresh
            ? RefreshAsync(accessToken, userId, relativePath, jsonTypeInfo, tags, cancellationToken, priority, resourceKind: resourceKind)
            : ReadAsync(accessToken, userId, relativePath, jsonTypeInfo, tags, cancellationToken, priority, resourceKind: resourceKind);

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

    private static IssuePagedSection<T> FailedSection<T>()
        where T : class =>
        new([], new IssueSectionState(
            CacheState.Error,
            ErrorMessage: "Refresh failed.",
            Completeness: PagedDataCompleteness.Partial));

    private Task<CachedResult<T>> ReadAsync<T>(
        string accessToken,
        string userId,
        string relativePath,
        JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        CancellationToken cancellationToken,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        string? acceptMediaType = null,
        string resourceKind = GitHubCachePolicy.MutableResource)
        where T : class
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewValue<T>()));
        }

        return _queryService.GetAsync(
            CreateQuery(accessToken, userId, relativePath, resourceKind, jsonTypeInfo, tags, priority, acceptMediaType),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    private Task<CachedResult<T>> RefreshAsync<T>(
        string accessToken,
        string userId,
        string relativePath,
        JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        CancellationToken cancellationToken,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        string? acceptMediaType = null,
        string resourceKind = GitHubCachePolicy.MutableResource)
        where T : class
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewValue<T>()));
        }

        return _queryService.RefreshAsync(
            CreateQuery(accessToken, userId, relativePath, resourceKind, jsonTypeInfo, tags, priority, acceptMediaType),
            cancellationToken);
    }

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        string? acceptMediaType = null)
        where T : class
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            partition,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(partition, HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            priority,
            acceptMediaType);
    }

    private static IssueSectionState ToState<T>(CachedResult<T> result)
        where T : class =>
        new(
            result.CacheState,
            result.IsRefreshInProgress,
            result.RefreshError is null
                ? null
                : JitHub.WinUI.Helpers.UserFacingError.For(
                    result.RefreshError,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "issue-section"));

    private static string BuildIssueListPath(
        string owner,
        string repositoryName,
        GitHubIssueQueryOptions options,
        int pageSize,
        int pageNumber)
    {
        List<string> query =
        [
            $"state={Escape(options.State)}",
            $"sort={Escape(options.Sort)}",
            $"direction={Escape(options.Direction)}",
            $"per_page={ClampPageSize(pageSize)}",
            $"page={Math.Max(1, pageNumber)}"
        ];
        AddOptional(query, "since", options.Since?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        AddOptional(query, "labels", options.Labels);
        AddOptional(query, "milestone", options.Milestone);
        AddOptional(query, "assignee", options.Assignee);
        AddOptional(query, "creator", options.Creator);
        AddOptional(query, "mentioned", options.Mentioned);
        AddOptional(query, "filter", options.Filter);
        return $"repos/{Escape(owner)}/{Escape(repositoryName)}/issues?{string.Join("&", query)}";
    }

    private static void AddOptional(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Escape(value)}");
        }
    }

    private static T CreatePreviewValue<T>() where T : class
    {
        object value = typeof(T) switch
        {
            Type type when type == typeof(GitHubIssue) => CreatePreviewIssues("JitHubApp", "JitHubV2")[0],
            Type type when type == typeof(GitHubIssueComment[]) => CreatePreviewComments(),
            Type type when type == typeof(GitHubIssueEvent[]) => Array.Empty<GitHubIssueEvent>(),
            Type type when type == typeof(GitHubReaction[]) => Array.Empty<GitHubReaction>(),
            Type type when type == typeof(GitHubActor[]) => Array.Empty<GitHubActor>(),
            Type type when type == typeof(GitHubLabel[]) => Array.Empty<GitHubLabel>(),
            Type type when type == typeof(GitHubMilestone[]) => Array.Empty<GitHubMilestone>(),
            _ => throw new InvalidOperationException($"No public preview value exists for {typeof(T).Name}.")
        };
        return (T)value;
    }

    private static GitHubIssue[] CreatePreviewIssues(string owner, string repositoryName) =>
        ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.CreateIssues(
                owner,
                repositoryName,
                pullRequests: false,
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.WorkItemCount))
            :
            [
                CreatePreviewIssue(owner, repositoryName, 3, "Improve cached issue navigation"),
                CreatePreviewIssue(owner, repositoryName, 2, "Polish responsive issue inspector"),
                CreatePreviewIssue(owner, repositoryName, 1, "Keep issue refreshes quiet")
            ];

    private static GitHubIssueComment[] CreatePreviewComments() =>
    [
        new GitHubIssueComment
        {
            Id = 301,
            HtmlUrl = "https://github.com/JitHubApp/JitHubV2/issues/3#issuecomment-301",
            Body = "The cached issue detail stays visible while its discussion refreshes.",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Reactions = new GitHubReactionSummary
            {
                TotalCount = 5,
                PlusOne = 3,
                Heart = 2
            },
            User = new GitHubActor
            {
                Login = "jithub-reviewer",
                AvatarUrl = "ms-appx:///Assets/Octocat.png"
            }
        }
    ];

    private static GitHubIssue CreatePreviewIssue(string owner, string repositoryName, int number, string title) =>
        new()
        {
            Id = number,
            Number = number,
            Title = title,
            Body = "This public preview issue demonstrates cached, responsive repository issue navigation.",
            State = "open",
            HtmlUrl = $"https://github.com/{owner}/{repositoryName}/issues/{number}",
            RepositoryUrl = $"https://api.github.com/repos/{owner}/{repositoryName}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-number),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-number),
            User = new GitHubActor { Login = "jithub-preview", AvatarUrl = "ms-appx:///Assets/Octocat.png" }
        };

    private static CachedResult<T> CreateCached<T>(T value) where T : class =>
        new(value, CacheState.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, PageSize);

    private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);

    private static string RepositoryTag(string owner, string repositoryName) => $"repo:{owner}/{repositoryName}";

    private static string IssueTag(string owner, string repositoryName, int issueNumber) =>
        $"issue:{owner}/{repositoryName}#{issueNumber}";
}
