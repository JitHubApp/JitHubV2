using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class GitHubMeQueryService : IGitHubMeQueryService
{
    private readonly IGitHubQueryService _queryService;

    public GitHubMeQueryService(IGitHubQueryService queryService)
    {
        _queryService = queryService;
    }

    public Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetIssuesAsync(accessToken, userId, login, filter, pageSize, GitHubMeWorkItemState.Open, cancellationToken);

    public Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default) =>
        GetIssuesPageAsync(accessToken, userId, login, filter, pageSize, 1, state, cancellationToken);

    public Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default)
        => QueryIssuesPageAsync(accessToken, userId, login, filter, pageSize, page, state, refresh: false, cancellationToken);

    public Task<CachedResult<GitHubSearchIssuesResponse>> RefreshIssuesPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default)
        => QueryIssuesPageAsync(accessToken, userId, login, filter, pageSize, page, state, refresh: true, cancellationToken);

    private Task<CachedResult<GitHubSearchIssuesResponse>> QueryIssuesPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMeIssueFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(page == 1
                ? CreatePreviewIssues(login, isPullRequest: false, state)
                : new GitHubSearchIssuesResponse()));
        }

        string qualifier = filter switch
        {
            GitHubMeIssueFilter.Created => $"author:{login}",
            GitHubMeIssueFilter.Mentioned => $"mentions:{login}",
            _ => $"assignee:{login}"
        };
        string search = BuildSearchQuery("issue", qualifier, state);
        GitHubQuery<GitHubSearchIssuesResponse> query = CreateQuery(
                accessToken,
                userId,
                $"search/issues?q={Uri.EscapeDataString(search)}&sort=updated&order=desc&per_page={ClampPageSize(pageSize)}&page={ClampPage(page)}",
                GitHubCachePolicy.SearchResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubSearchIssuesResponse,
                ["me-issues", "search"],
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);
        return refresh
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    public Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetPullRequestsAsync(accessToken, userId, login, filter, pageSize, GitHubMeWorkItemState.Open, cancellationToken);

    public Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default) =>
        GetPullRequestsPageAsync(accessToken, userId, login, filter, pageSize, 1, state, cancellationToken);

    public Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default)
        => QueryPullRequestsPageAsync(accessToken, userId, login, filter, pageSize, page, state, refresh: false, cancellationToken);

    public Task<CachedResult<GitHubSearchIssuesResponse>> RefreshPullRequestsPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        CancellationToken cancellationToken = default)
        => QueryPullRequestsPageAsync(accessToken, userId, login, filter, pageSize, page, state, refresh: true, cancellationToken);

    private Task<CachedResult<GitHubSearchIssuesResponse>> QueryPullRequestsPageAsync(
        string accessToken,
        string userId,
        string login,
        GitHubMePullRequestFilter filter,
        int pageSize,
        int page,
        GitHubMeWorkItemState state,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(page == 1
                ? CreatePreviewIssues(login, isPullRequest: true, state)
                : new GitHubSearchIssuesResponse()));
        }

        string qualifier = filter switch
        {
            GitHubMePullRequestFilter.ReviewRequested => $"review-requested:{login}",
            GitHubMePullRequestFilter.Authored => $"author:{login}",
            GitHubMePullRequestFilter.Assigned => $"assignee:{login}",
            _ => $"involves:{login}"
        };
        string search = BuildSearchQuery("pr", qualifier, state);
        GitHubQuery<GitHubSearchIssuesResponse> query = CreateQuery(
                accessToken,
                userId,
                $"search/issues?q={Uri.EscapeDataString(search)}&sort=updated&order=desc&per_page={ClampPageSize(pageSize)}&page={ClampPage(page)}",
                GitHubCachePolicy.SearchResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubSearchIssuesResponse,
                ["me-pull-requests", "search"],
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);
        return refresh
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    public Task<CachedResult<GitHubIssue>> GetIssueDetailAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewIssue(
                issueNumber,
                $"{owner}/{repositoryName}",
                "Track dashboard responsive edge cases",
                "preview-user",
                isPullRequest: false,
                GitHubMeWorkItemState.Open)));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}/issues/{issueNumber}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
                ["me-issues", "issue-detail"]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetIssueCommentsPageAsync(
            accessToken,
            userId,
            owner,
            repositoryName,
            issueNumber,
            pageSize,
            1,
            cancellationToken);

    public Task<CachedResult<GitHubIssueComment[]>> GetIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int page,
        CancellationToken cancellationToken = default)
        => QueryIssueCommentsPageAsync(accessToken, userId, owner, repositoryName, issueNumber, pageSize, page, refresh: false, cancellationToken);

    public Task<CachedResult<GitHubIssueComment[]>> RefreshIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int page,
        CancellationToken cancellationToken = default)
        => QueryIssueCommentsPageAsync(accessToken, userId, owner, repositoryName, issueNumber, pageSize, page, refresh: true, cancellationToken);

    private Task<CachedResult<GitHubIssueComment[]>> QueryIssueCommentsPageAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        int pageSize,
        int page,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(page == 1
                ? CreatePreviewComments(owner, repositoryName, issueNumber)
                : []));
        }

        GitHubQuery<GitHubIssueComment[]> query = CreateQuery(
                accessToken,
                userId,
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}/issues/{issueNumber}/comments?sort=created&direction=asc&per_page={ClampPageSize(pageSize)}&page={ClampPage(page)}",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
                ["me-issues", "issue-comments"],
                page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh);
        return refresh
            ? _queryService.RefreshAsync(query, cancellationToken)
            : _queryService.GetAsync(query, QueryFetchPolicy.StaleFirst, cancellationToken);
    }

    public async Task<IssuePrefetchAggregate> GetIssuePrefetchAsync(
        string accessToken,
        string userId,
        string owner,
        string repositoryName,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return new IssuePrefetchAggregate(
                CreatePreviewIssue(issueNumber, $"{owner}/{repositoryName}", "Preview issue", "preview-user", false, GitHubMeWorkItemState.Open),
                CreatePreviewComments(owner, repositoryName, issueNumber));
        }

        GitHubQuery<GitHubIssue> issueQuery = CreateQuery(
            accessToken,
            userId,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}/issues/{issueNumber}",
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubIssue,
            ["me-issues", "issue-detail"],
            GitHubRequestPriority.Prefetch);
        GitHubQuery<GitHubIssueComment[]> commentsQuery = CreateQuery(
            accessToken,
            userId,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}/issues/{issueNumber}/comments?sort=created&direction=asc&per_page=50&page=1",
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
            ["me-issues", "issue-comments"],
            GitHubRequestPriority.Prefetch);
        Task<CachedResult<GitHubIssue>> issueTask = _queryService.GetAsync(
            issueQuery, QueryFetchPolicy.StaleFirst, cancellationToken);
        Task<CachedResult<GitHubIssueComment[]>> commentsTask = _queryService.GetAsync(
            commentsQuery, QueryFetchPolicy.StaleFirst, cancellationToken);
        await Task.WhenAll(issueTask, commentsTask);
        return new IssuePrefetchAggregate(
            (await issueTask).Value,
            (await commentsTask).Value ?? []);
    }

    public Task<CachedResult<GitHubRepository[]>> GetStarredRepositoriesAsync(
        string accessToken,
        string userId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewRepositories()));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"user/starred?sort=updated&direction=desc&per_page={ClampPageSize(pageSize)}&page=1",
                GitHubCachePolicy.RepositoryResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
                ["me-stars", "repo"]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    public Task<CachedResult<GitHubGist[]>> GetGistsAsync(
        string accessToken,
        string userId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return Task.FromResult(CreateCached(CreatePreviewGists()));
        }

        return _queryService.GetAsync(
            CreateQuery(
                accessToken,
                userId,
                $"gists?per_page={ClampPageSize(pageSize)}&page=1",
                GitHubCachePolicy.MutableResource,
                Phase0GitHubJsonSerializerContext.Default.GitHubGistArray,
                ["me-gists"]),
            QueryFetchPolicy.StaleFirst,
            cancellationToken);
    }

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
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

    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 100);

    private static int ClampPage(int page) => Math.Max(1, page);

    private static string BuildSearchQuery(string kind, string qualifier, GitHubMeWorkItemState state)
    {
        string stateQualifier = state switch
        {
            GitHubMeWorkItemState.Closed => "is:closed",
            GitHubMeWorkItemState.All => string.Empty,
            _ => "is:open"
        };

        return string.IsNullOrWhiteSpace(stateQualifier)
            ? $"is:{kind} {qualifier}"
            : $"is:{kind} {stateQualifier} {qualifier}";
    }

    private static CachedResult<T> CreateCached<T>(T value)
        where T : class =>
        new(value, CacheState.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

    private static GitHubSearchIssuesResponse CreatePreviewIssues(string login, bool isPullRequest, GitHubMeWorkItemState state)
    {
        if (ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled)
        {
            GitHubIssue[] largeItems = ProductPerformanceLargeAccountFixture
                .CreateIssues(
                    "performance-owner",
                    "performance-repo",
                    isPullRequest,
                    ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.WorkItemCount))
                .Where(item => state == GitHubMeWorkItemState.All ||
                    string.Equals(
                        item.State,
                        state == GitHubMeWorkItemState.Closed ? "closed" : "open",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return new GitHubSearchIssuesResponse
            {
                TotalCount = largeItems.Length,
                Items = largeItems
            };
        }

        GitHubIssue[] items =
        [
            CreatePreviewIssue(1, "JitHubApp/JitHubV2", isPullRequest ? "Polish shell widget board" : "Track dashboard responsive edge cases", login, isPullRequest, state),
            CreatePreviewIssue(2, "JitHubApp/open-ui", isPullRequest ? "Add compact rail primitives" : "Audit keyboard navigation states", login, isPullRequest, state),
            CreatePreviewIssue(3, "JitHubApp/JitHubV2", isPullRequest ? "Cache recommendations snapshot" : "Improve local diagnostics export", login, isPullRequest, state)
        ];
        return new GitHubSearchIssuesResponse
        {
            TotalCount = items.Length,
            Items = items
        };
    }

    private static GitHubIssue CreatePreviewIssue(int number, string repository, string title, string login, bool isPullRequest, GitHubMeWorkItemState state) =>
        new()
        {
            Id = number,
            Number = number,
            Title = title,
            Body = $"This preview item represents the Phase 4 My Issues workflow for `{repository}`.\n\n- Cached rows render first\n- Details load without blanking the list\n- Inspector data stays visible while refresh runs",
            State = state == GitHubMeWorkItemState.Closed ? "closed" : "open",
            HtmlUrl = $"https://github.com/{repository}/{(isPullRequest ? "pull" : "issues")}/{number}",
            RepositoryUrl = $"https://api.github.com/repos/{repository}",
            Comments = number,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-number),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-number),
            User = new GitHubActor { Login = login, AvatarUrl = "ms-appx:///Assets/Octocat.png" },
            Assignees = [new GitHubActor { Login = login, AvatarUrl = "ms-appx:///Assets/Octocat.png" }],
            Labels =
            [
                new GitHubLabel { Name = "vNext", Color = "7BD7A8" },
                new GitHubLabel { Name = "phase-4", Color = "6EA8FE" }
            ],
            Milestone = new GitHubMilestone { Number = 4, Title = "Phase 4" },
            PullRequest = isPullRequest ? new GitHubIssuePullRequestMarker { HtmlUrl = $"https://github.com/{repository}/pull/{number}" } : null
        };

    private static GitHubIssueComment[] CreatePreviewComments(string owner, string repositoryName, int issueNumber) =>
    [
        new()
        {
            Id = issueNumber * 10L + 1,
            HtmlUrl = $"https://github.com/{owner}/{repositoryName}/issues/{issueNumber}#issuecomment-preview-1",
            Body = "The cached detail path is working. This comment is local preview data so the page can be tested without a network dependency.",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-3),
            User = new GitHubActor { Login = "jitHub-preview", AvatarUrl = "ms-appx:///Assets/Octocat.png" }
        },
        new()
        {
            Id = issueNumber * 10L + 2,
            HtmlUrl = $"https://github.com/{owner}/{repositoryName}/issues/{issueNumber}#issuecomment-preview-2",
            Body = "The list should stay visible while this detail section refreshes in the background.",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            User = new GitHubActor { Login = "jitHub-preview", AvatarUrl = "ms-appx:///Assets/Octocat.png" }
        }
    ];

    private static GitHubRepository[] CreatePreviewRepositories() =>
        ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.CreateRepositories(
                ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.RepositoryCount))
            :
            [
                CreatePreviewRepository(1, "JitHubApp/JitHubV2", "Native Windows GitHub client built with WinUI.", "C#", 420),
                CreatePreviewRepository(2, "microsoft/vscode", "Code editing. Redefined.", "TypeScript", 150_000),
                CreatePreviewRepository(3, "vercel/next.js", "The React Framework.", "JavaScript", 125_000)
            ];

    private static GitHubRepository CreatePreviewRepository(long id, string fullName, string description, string language, int stars)
    {
        string[] parts = fullName.Split('/', 2);
        return new GitHubRepository
        {
            Id = id,
            Name = parts[1],
            FullName = fullName,
            Description = description,
            Language = language,
            StargazersCount = stars,
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{fullName}",
            Owner = new GitHubRepositoryOwner { Login = parts[0] },
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-id)
        };
    }

    private static GitHubGist[] CreatePreviewGists() =>
    [
        new()
        {
            Id = "preview-1",
            Description = "Shell layout notes",
            HtmlUrl = "https://gist.github.com/preview-1",
            Public = false,
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-4),
            Files = new Dictionary<string, GitHubGistFile>
            {
                ["jithub-shell.md"] = new() { Filename = "jithub-shell.md", Language = "Markdown", Size = 2048 }
            }
        },
        new()
        {
            Id = "preview-2",
            Description = "Cache policy scratchpad",
            HtmlUrl = "https://gist.github.com/preview-2",
            Public = true,
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-8),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
            Files = new Dictionary<string, GitHubGistFile>
            {
                ["cache-policy.cs"] = new() { Filename = "cache-policy.cs", Language = "C#", Size = 4096 }
            }
        }
    ];
}
