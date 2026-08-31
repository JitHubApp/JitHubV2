using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubPagedReconcilerTests
{
    [Fact]
    public async Task Search_RefreshesStaleShortFirstPageBeforeDecidingPagination()
    {
        List<int> requestedPages = [];
        List<int> refreshedPages = [];
        List<int> publishedCounts = [];

        GitHubPagedLoadResult<GitHubIssue> result = await GitHubPagedReconciler.LoadAsync<GitHubSearchIssuesResponse, GitHubIssue>(
            (page, _) =>
            {
                requestedPages.Add(page);
                GitHubSearchIssuesResponse response = page switch
                {
                    1 => CreateIssuePage(30, 30, 0),
                    2 => CreateIssuePage(50, 150, 100),
                    _ => CreateIssuePage(0, 150, 0)
                };
                CacheState state = page == 1 ? CacheState.Stale : CacheState.Fresh;
                return Task.FromResult(CreateResult(response, state, state == CacheState.Stale));
            },
            (page, _) =>
            {
                refreshedPages.Add(page);
                GitHubSearchIssuesResponse response = page == 1
                    ? CreateIssuePage(100, 150, 0)
                    : CreateIssuePage(0, 150, 0);
                return Task.FromResult(CreateResult(response, CacheState.Fresh));
            },
            static response => response.Items,
            static response => response.TotalCount,
            static issue => issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize: 100,
            maximumItemCount: 1000,
            progress: update => publishedCounts.Add(update.Items.Count));

        Assert.Equal(150, result.Items.Count);
        Assert.Equal(150, result.TotalCount);
        Assert.Equal(PagedDataCompleteness.Complete, result.Completeness);
        Assert.Equal([1, 2], requestedPages);
        Assert.Equal([1], refreshedPages);
        Assert.Equal(30, publishedCounts[0]);
        Assert.Contains(100, publishedCounts);
        Assert.Equal(150, publishedCounts[^1]);
    }

    [Fact]
    public async Task Comments_RefreshesStaleShortFirstPageThenUsesFullPageEvidence()
    {
        List<int> requestedPages = [];
        List<int> refreshedPages = [];

        GitHubPagedLoadResult<GitHubIssueComment> result = await GitHubPagedReconciler.LoadAsync<GitHubIssueComment[], GitHubIssueComment>(
            (page, _) =>
            {
                requestedPages.Add(page);
                GitHubIssueComment[] comments = page switch
                {
                    1 => CreateComments(20, 0),
                    2 => CreateComments(25, 100),
                    _ => []
                };
                CacheState state = page == 1 ? CacheState.Stale : CacheState.Fresh;
                return Task.FromResult(CreateResult(comments, state, state == CacheState.Stale));
            },
            (page, _) =>
            {
                refreshedPages.Add(page);
                return Task.FromResult(CreateResult(CreateComments(100, 0), CacheState.Fresh));
            },
            static comments => comments,
            totalCountSelector: null,
            static comment => comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize: 100,
            maximumItemCount: 1000);

        Assert.Equal(125, result.Items.Count);
        Assert.Equal(PagedDataCompleteness.Complete, result.Completeness);
        Assert.Equal([1, 2], requestedPages);
        Assert.Equal([1], refreshedPages);
    }

    [Fact]
    public async Task Search_NeverTraversesPastGitHubThousandResultCap()
    {
        int pageCalls = 0;
        List<GitHubPagedLoadProgress<GitHubIssue>> updates = [];

        GitHubPagedLoadResult<GitHubIssue> result = await GitHubPagedReconciler.LoadAsync<GitHubSearchIssuesResponse, GitHubIssue>(
            (page, _) =>
            {
                pageCalls++;
                return Task.FromResult(CreateResult(CreateIssuePage(100, 5000, (page - 1) * 100), CacheState.Fresh));
            },
            (_, _) => throw new InvalidOperationException("Fresh pages must not be refreshed."),
            static response => response.Items,
            static response => response.TotalCount,
            static issue => issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize: 100,
            maximumItemCount: 1000,
            progress: updates.Add);

        Assert.Equal(1000, result.Items.Count);
        Assert.Equal(10, pageCalls);
        Assert.Equal(PagedDataCompleteness.ApiLimited, result.Completeness);
        Assert.All(updates, static update => Assert.False(update.IsAuthoritative));
        Assert.True(updates[^1].IsFinal);
        Assert.Equal(PagedDataCompleteness.ApiLimited, updates[^1].Completeness);
    }

    [Fact]
    public async Task Comments_PaginatesUntilApiExhaustionWithoutAnAppDetailCap()
    {
        int pageCalls = 0;

        GitHubPagedLoadResult<GitHubIssueComment> result = await GitHubPagedReconciler.LoadAsync<GitHubIssueComment[], GitHubIssueComment>(
            (page, _) =>
            {
                pageCalls++;
                return Task.FromResult(CreateResult(
                    page <= 12 ? CreateComments(100, (page - 1) * 100) : CreateComments(7, 1200),
                    CacheState.Fresh));
            },
            (_, _) => throw new InvalidOperationException("Fresh pages must not be refreshed."),
            static comments => comments,
            totalCountSelector: null,
            static comment => comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize: 100,
            maximumItemCount: int.MaxValue);

        Assert.Equal(1207, result.Items.Count);
        Assert.Equal(13, pageCalls);
        Assert.Equal(PagedDataCompleteness.Complete, result.Completeness);
    }

    [Fact]
    public async Task StaleCacheMiss_IsPublishedAsNonAuthoritativeBeforeNetworkPromotion()
    {
        List<GitHubPagedLoadProgress<GitHubIssue>> updates = [];

        await GitHubPagedReconciler.LoadAsync<GitHubSearchIssuesResponse, GitHubIssue>(
            (_, _) => Task.FromResult(CreateResult(
                CreateIssuePage(0, 0, 0),
                CacheState.Miss,
                refreshInProgress: true)),
            (_, _) => Task.FromResult(CreateResult(
                CreateIssuePage(2, 2, 0),
                CacheState.Fresh)),
            static response => response.Items,
            static response => response.TotalCount,
            static issue => issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize: 100,
            maximumItemCount: 1000,
            progress: updates.Add);

        Assert.False(updates[0].IsAuthoritative);
        Assert.Empty(updates[0].Items);
        Assert.False(updates[1].IsAuthoritative);
        Assert.Equal(2, updates[1].Items.Count);
        Assert.True(updates[^1].IsFinal);
        Assert.True(updates[^1].IsAuthoritative);
        Assert.Equal(PagedDataCompleteness.Complete, updates[^1].Completeness);
    }

    [Fact]
    public async Task DuplicatePageTermination_IsPartialAndNeverAuthoritative()
    {
        List<GitHubPagedLoadProgress<GitHubIssue>> updates = [];

        GitHubPagedLoadResult<GitHubIssue> result = await GitHubPagedReconciler.LoadAsync<GitHubSearchIssuesResponse, GitHubIssue>(
            (_, _) => Task.FromResult(CreateResult(CreateIssuePage(100, 250, 0), CacheState.Fresh)),
            (_, _) => throw new InvalidOperationException("Fresh pages must not be refreshed."),
            static response => response.Items,
            static response => response.TotalCount,
            static issue => issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize: 100,
            maximumItemCount: 1000,
            progress: updates.Add);

        Assert.Equal(100, result.Items.Count);
        Assert.Equal(2, result.LoadedPageCount);
        Assert.Equal(PagedDataCompleteness.Partial, result.Completeness);
        Assert.All(updates, static update => Assert.False(update.IsAuthoritative));
        Assert.True(updates[^1].IsFinal);
        Assert.Equal(PagedDataCompleteness.Partial, updates[^1].Completeness);
    }

    [Fact]
    public async Task CompleteReconciliation_MarksOnlyFinalProjectionAuthoritative()
    {
        List<GitHubPagedLoadProgress<GitHubIssue>> updates = [];

        GitHubPagedLoadResult<GitHubIssue> result = await GitHubPagedReconciler.LoadAsync<GitHubSearchIssuesResponse, GitHubIssue>(
            (page, _) => Task.FromResult(CreateResult(
                page == 1 ? CreateIssuePage(100, 125, 0) : CreateIssuePage(25, 125, 100),
                CacheState.Fresh)),
            (_, _) => throw new InvalidOperationException("Fresh pages must not be refreshed."),
            static response => response.Items,
            static response => response.TotalCount,
            static issue => issue.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageSize: 100,
            maximumItemCount: 1000,
            progress: updates.Add);

        Assert.Equal(PagedDataCompleteness.Complete, result.Completeness);
        Assert.All(updates.Take(updates.Count - 1), static update => Assert.False(update.IsAuthoritative));
        Assert.True(updates[^1].IsAuthoritative);
        Assert.True(updates[^1].IsFinal);
    }

    private static GitHubSearchIssuesResponse CreateIssuePage(int count, int totalCount, int offset) => new()
    {
        TotalCount = totalCount,
        Items = Enumerable.Range(offset + 1, count)
            .Select(id => new GitHubIssue { Id = id, Number = id })
            .ToArray()
    };

    private static GitHubIssueComment[] CreateComments(int count, int offset) =>
        Enumerable.Range(offset + 1, count)
            .Select(id => new GitHubIssueComment { Id = id, Body = $"Comment {id}" })
            .ToArray();

    private static CachedResult<T> CreateResult<T>(T value, CacheState state, bool refreshInProgress = false)
        where T : class =>
        new(value, state, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), refreshInProgress);
}
