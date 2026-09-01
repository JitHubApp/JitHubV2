using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PullRequestPagedDataIntegrityTests
{
    [Theory]
    [InlineData(PagedDataCompleteness.Complete)]
    [InlineData(PagedDataCompleteness.ApiLimited)]
    public void TerminalListResult_AcceptsOnlyUsableCompletedStates(PagedDataCompleteness completeness)
    {
        Assert.True(PullRequestSectionProjectionPolicy.IsTerminalListResult(
            new PullRequestSectionState(CacheState.Fresh, Completeness: completeness)));
        Assert.False(PullRequestSectionProjectionPolicy.IsTerminalListResult(
            new PullRequestSectionState(CacheState.Fresh, IsRefreshInProgress: true, Completeness: completeness)));
        Assert.False(PullRequestSectionProjectionPolicy.IsTerminalListResult(
            new PullRequestSectionState(CacheState.Error, ErrorMessage: "failed", Completeness: completeness)));
    }

    [Theory]
    [InlineData(PagedDataCompleteness.Loading)]
    [InlineData(PagedDataCompleteness.Partial)]
    public void TerminalListResult_RejectsIncompletePublications(PagedDataCompleteness completeness)
    {
        Assert.False(PullRequestSectionProjectionPolicy.IsTerminalListResult(
            new PullRequestSectionState(CacheState.Fresh, Completeness: completeness)));
    }

    [Fact]
    public async Task CreateAndMetadataOptions_AutoPageBeyondOneHundred()
    {
        RepositoryMetadataQueryService queryService = new();
        GitHubPullRequestQueryService service = new(queryService);

        PullRequestPagedSection<GitHubBranch> branches = await service.GetAllRepositoryBranchesAsync(
            "token", "42", "owner", "repo");
        PullRequestPagedSection<GitHubActor> collaborators = await service.GetAllRepositoryCollaboratorsAsync(
            "token", "42", "owner", "repo");
        PullRequestPagedSection<GitHubActor> assignees = await service.GetAllRepositoryAssigneesAsync(
            "token", "42", "owner", "repo");
        PullRequestPagedSection<GitHubLabel> labels = await service.GetAllRepositoryLabelsAsync(
            "token", "42", "owner", "repo");
        PullRequestPagedSection<GitHubMilestone> milestones = await service.GetAllRepositoryMilestonesAsync(
            "token", "42", "owner", "repo");

        Assert.All(
            new[] { branches.Items.Length, collaborators.Items.Length, assignees.Items.Length, labels.Items.Length, milestones.Items.Length },
            static count => Assert.Equal(101, count));
        Assert.All(
            new[] { branches.State, collaborators.State, assignees.State, labels.State, milestones.State },
            static state =>
            {
                Assert.Equal(PagedDataCompleteness.Complete, state.Completeness);
                Assert.Equal(101, state.LoadedItemCount);
            });
        Assert.Contains(queryService.Paths, static path => path.Contains("/branches?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/collaborators?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/assignees?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/labels?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/milestones?state=all&per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(GitHubRequestPriority.BackgroundRefresh, queryService.Priorities);
    }

    [Fact]
    public async Task MetadataLaterPageFailure_ReturnsUsablePartialPrefix()
    {
        RepositoryMetadataQueryService queryService = new(failSecondPage: true);
        GitHubPullRequestQueryService service = new(queryService);

        PullRequestPagedSection<GitHubLabel> result = await service.GetAllRepositoryLabelsAsync(
            "token", "42", "owner", "repo");

        Assert.Equal(100, result.Items.Length);
        Assert.Equal(PagedDataCompleteness.Partial, result.Completeness);
        Assert.Equal(PagedDataCompleteness.Partial, result.State.Completeness);
        Assert.False(string.IsNullOrWhiteSpace(result.State.ErrorMessage));
    }

    [Fact]
    public async Task PullRequestReactionReads_AutoPageThroughSharedQueryFacade()
    {
        RepositoryMetadataQueryService queryService = new();
        GitHubPullRequestQueryService service = new(queryService);

        PullRequestPagedSection<GitHubReaction> pullRequest = await service.GetAllPullRequestReactionsAsync(
            "token", "42", "owner", "repo", 17);
        PullRequestPagedSection<GitHubReaction> issueComment = await service.GetAllPullRequestCommentReactionsAsync(
            "token", "42", "owner", "repo", 701);
        PullRequestPagedSection<GitHubReaction> reviewComment = await service.GetAllPullRequestReviewCommentReactionsAsync(
            "token", "42", "owner", "repo", 702);

        Assert.All(
            new[] { pullRequest, issueComment, reviewComment },
            static section =>
            {
                Assert.Equal(101, section.Items.Length);
                Assert.Equal(PagedDataCompleteness.Complete, section.Completeness);
            });
        Assert.Contains(queryService.Paths, static path => path.Contains("/issues/17/reactions?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/issues/comments/701/reactions?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(queryService.Paths, static path => path.Contains("/pulls/comments/702/reactions?per_page=100&page=2", StringComparison.Ordinal));
        Assert.Contains(GitHubRequestPriority.BackgroundRefresh, queryService.Priorities);
    }

    [Fact]
    public async Task PullRequestListLaterPageFailure_PublishesPartialWithoutDroppingExistingTail()
    {
        RepositoryMetadataQueryService queryService = new(failSecondPage: true);
        GitHubPullRequestQueryService service = new(queryService);
        GitHubPullRequest[] published =
        [
            new GitHubPullRequest { Number = 1, Title = "Published first" },
            new GitHubPullRequest { Number = 101, Title = "Published tail" }
        ];

        PullRequestPagedSection<GitHubPullRequest> result = await service.GetAllPullRequestsAsync(
            "token",
            "42",
            "owner",
            "repo",
            new GitHubPullRequestQueryOptions(),
            progress: progress => published = PagedRefreshProjectionPolicy.Merge(
                progress.Items,
                published,
                static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture),
                progress.Completeness));

        Assert.Equal(PagedDataCompleteness.Partial, result.Completeness);
        Assert.Equal(101, published.Length);
        Assert.Contains(published, static pullRequest => pullRequest.Number == 101);
    }

    [Theory]
    [InlineData(PagedDataCompleteness.Loading)]
    [InlineData(PagedDataCompleteness.Partial)]
    public void PullRequestProgressAndPartialFinal_PreservePublishedTail(
        PagedDataCompleteness completeness)
    {
        GitHubPullRequest refreshed = new() { Number = 1, Title = "Refreshed" };
        GitHubPullRequest publishedTail = new() { Number = 101, Title = "Published tail" };

        GitHubPullRequest[] projection = PagedRefreshProjectionPolicy.Merge(
            [refreshed],
            [new GitHubPullRequest { Number = 1, Title = "Old" }, publishedTail],
            static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture),
            completeness);

        Assert.Equal([1, 101], projection.Select(static pullRequest => pullRequest.Number));
        Assert.Same(refreshed, projection[0]);
        Assert.Same(publishedTail, projection[1]);
    }

    [Fact]
    public void CompletePullRequestRefresh_RemovesRowsMissingFromAuthoritativeResult()
    {
        GitHubPullRequest[] projection = PagedRefreshProjectionPolicy.Merge(
            [new GitHubPullRequest { Number = 1 }],
            [new GitHubPullRequest { Number = 1 }, new GitHubPullRequest { Number = 101 }],
            static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture),
            PagedDataCompleteness.Complete);

        Assert.Equal(1, Assert.Single(projection).Number);
    }

    [Fact]
    public void PullRequestListTelemetry_ReportsCompletenessTruthfully()
    {
        Assert.Equal("success", PullRequestSectionProjectionPolicy.CreateListTelemetryResult(PagedDataCompleteness.Complete));
        Assert.Equal("partial", PullRequestSectionProjectionPolicy.CreateListTelemetryResult(PagedDataCompleteness.Partial));
        Assert.Equal("api_limited", PullRequestSectionProjectionPolicy.CreateListTelemetryResult(PagedDataCompleteness.ApiLimited));
    }

    [Fact]
    public void PullRequestSectionProjection_SurfacesNonErrorPartialAndCommitApiLimit()
    {
        string partial = PullRequestSectionProjectionPolicy.CreateSectionErrorText(
            PullRequestWorkspaceSection.Reviews,
            new PullRequestSectionState(
                CacheState.Fresh,
                Completeness: PagedDataCompleteness.Partial,
                LoadedItemCount: 100));
        string limited = PullRequestSectionProjectionPolicy.CreateSectionErrorText(
            PullRequestWorkspaceSection.Commits,
            new PullRequestSectionState(
                CacheState.Fresh,
                Completeness: PagedDataCompleteness.ApiLimited,
                LoadedItemCount: 250,
                ApiLimit: 250));

        Assert.Contains("partially loaded", partial, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("250", limited, StringComparison.Ordinal);
        Assert.Contains("API limit", limited, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PagedDataCompleteness.Partial)]
    [InlineData(PagedDataCompleteness.ApiLimited)]
    public void IncompletePullRequestDetailSection_UpdatesPrefixAndPreservesPublishedTail(
        PagedDataCompleteness completeness)
    {
        GitHubIssueComment refreshed = new() { Id = 1, Body = "refreshed" };
        GitHubIssueComment cachedTail = new() { Id = 2, Body = "cached tail" };

        GitHubIssueComment[] projection = PullRequestSectionProjectionPolicy.ProjectSection(
            [refreshed],
            [new GitHubIssueComment { Id = 1, Body = "old" }, cachedTail],
            new PullRequestSectionState(CacheState.Fresh, Completeness: completeness, LoadedItemCount: 1),
            static item => item.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Equal([1, 2], projection.Select(static item => item.Id));
        Assert.Same(refreshed, projection[0]);
        Assert.Same(cachedTail, projection[1]);
    }

    [Fact]
    public void CompletePullRequestDetailSection_RemovesMissingRows()
    {
        GitHubIssueComment refreshed = new() { Id = 1, Body = "refreshed" };

        GitHubIssueComment[] projection = PullRequestSectionProjectionPolicy.ProjectSection(
            [refreshed],
            [new GitHubIssueComment { Id = 1 }, new GitHubIssueComment { Id = 2 }],
            new PullRequestSectionState(CacheState.Fresh),
            static item => item.Id.ToString(CultureInfo.InvariantCulture));

        Assert.Same(refreshed, Assert.Single(projection));
    }

    [Fact]
    public void PartialPullRequestDiff_PreservesPublishedFileTail()
    {
        CommitDiffFile refreshed = CreateDiffFile("src/updated.cs", additions: 2);
        CommitDiffFile cachedTail = CreateDiffFile("src/tail.cs", additions: 1);
        CommitDiffDocument incoming = new([refreshed]);
        CommitDiffDocument published = new([CreateDiffFile("src/updated.cs", additions: 1), cachedTail]);

        CommitDiffDocument projection = PullRequestSectionProjectionPolicy.ProjectDiffDocument(
            incoming,
            published,
            new PullRequestSectionState(
                CacheState.Fresh,
                Completeness: PagedDataCompleteness.Partial,
                LoadedItemCount: 1));

        Assert.Equal(["src/updated.cs", "src/tail.cs"], projection.Files.Select(static file => file.Filename));
        Assert.Same(refreshed, projection.Files[0]);
        Assert.Same(cachedTail, projection.Files[1]);
    }

    [Fact]
    public void CompositeReviewProjection_PreservesTailWhenEitherSourceIsIncomplete()
    {
        GitHubPullRequestReview refreshed = new() { Id = 1, State = "approved" };
        GitHubPullRequestReview cachedTail = new() { Id = 2, State = "commented" };

        GitHubPullRequestReview[] projection = PullRequestSectionProjectionPolicy.ProjectSection(
            [refreshed],
            [new GitHubPullRequestReview { Id = 1, State = "pending" }, cachedTail],
            static review => review.Id.ToString(CultureInfo.InvariantCulture),
            new PullRequestSectionState(CacheState.Fresh),
            new PullRequestSectionState(
                CacheState.Fresh,
                Completeness: PagedDataCompleteness.Partial,
                LoadedItemCount: 1));

        Assert.Equal([1, 2], projection.Select(static review => review.Id));
        Assert.Same(refreshed, projection[0]);
        Assert.Same(cachedTail, projection[1]);
    }

    private static CommitDiffFile CreateDiffFile(string path, int additions) =>
        new(path, null, "modified", additions, 0, additions, []);

    private sealed class RepositoryMetadataQueryService(bool failSecondPage = false) : IGitHubQueryService
    {
        public List<string> Paths { get; } = [];

        public List<GitHubRequestPriority> Priorities { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            Paths.Add(query.RelativePath);
            Priorities.Add(query.Priority);
            bool secondPage = query.RelativePath.Contains("page=2", StringComparison.Ordinal);
            if (secondPage && failSecondPage)
            {
                throw new HttpRequestException("metadata page 2 unavailable");
            }

            int start = secondPage ? 101 : 1;
            int count = secondPage ? 1 : 100;
            object payload = typeof(T) switch
            {
                Type type when type == typeof(GitHubBranch[]) => Enumerable.Range(start, count)
                    .Select(static index => new GitHubBranch { Name = $"branch-{index}" })
                    .ToArray(),
                Type type when type == typeof(GitHubActor[]) => Enumerable.Range(start, count)
                    .Select(static index => new GitHubActor { Id = index, Login = $"user-{index}" })
                    .ToArray(),
                Type type when type == typeof(GitHubLabel[]) => Enumerable.Range(start, count)
                    .Select(static index => new GitHubLabel { Id = index, Name = $"label-{index}" })
                    .ToArray(),
                Type type when type == typeof(GitHubMilestone[]) => Enumerable.Range(start, count)
                    .Select(static index => new GitHubMilestone { Number = index, Title = $"milestone-{index}" })
                    .ToArray(),
                Type type when type == typeof(GitHubPullRequest[]) => Enumerable.Range(start, count)
                    .Select(static index => new GitHubPullRequest { Id = index, Number = index, Title = $"PR {index}" })
                    .ToArray(),
                Type type when type == typeof(GitHubReaction[]) => Enumerable.Range(start, count)
                    .Select(static index => new GitHubReaction { Id = index, Content = "+1" })
                    .ToArray(),
                _ => throw new InvalidOperationException($"Unexpected query type {typeof(T).Name}.")
            };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>(
                (T)payload,
                CacheState.Fresh,
                now,
                now.AddMinutes(30)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
