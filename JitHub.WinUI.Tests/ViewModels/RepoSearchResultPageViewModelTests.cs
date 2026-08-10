using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepoSearchResultPageViewModelTests
{
    [Fact]
    public async Task ResetSearch_RestartsPaginationAtPageOne()
    {
        RecordingSearchService search = new((query, page, _, _) =>
            Result(totalCount: 4, Repository(page * 10 + 1), Repository(page * 10 + 2)));
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search);

        await viewModel.InitializeAsync("first");
        await viewModel.LoadNextPageAsync();
        viewModel.QueryText = "second";
        await viewModel.ApplySearchAsync();
        await viewModel.LoadNextPageAsync();

        Assert.Equal([1, 2, 1, 2], search.Requests.Select(static request => request.Page));
        Assert.Equal(["first", "first", "second", "second"], search.Requests.Select(static request => request.Query.Text));
    }

    [Theory]
    [InlineData("visibility")]
    [InlineData("fork")]
    [InlineData("archive")]
    public async Task FilterOnlySearch_IsSubmitted(string filter)
    {
        RecordingSearchService search = new((query, _, _, _) => Result(1, Repository(1)));
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search);
        switch (filter)
        {
            case "visibility": viewModel.SelectedVisibility = "Public"; break;
            case "fork": viewModel.SelectedForkScope = "Sources"; break;
            case "archive": viewModel.SelectedArchiveScope = "Archived"; break;
        }

        await viewModel.ApplySearchAsync();

        RepositorySearchQuery submitted = Assert.Single(search.Requests).Query;
        Assert.Single(viewModel.Results);
        Assert.True(
            submitted.Visibility != RepositorySearchVisibility.Any ||
            submitted.ForkScope != RepositorySearchForkScope.Any ||
            submitted.ArchiveScope != RepositorySearchArchiveScope.Any);
    }

    [Fact]
    public async Task DelayedFirstPageRefresh_PreservesAlreadyLoadedPages()
    {
        TaskCompletionSource<CachedResult<GitHubRepositorySearchResponse>> refresh =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSearchService search = new((_, page, forceRefresh, _) =>
        {
            if (forceRefresh)
            {
                return refresh.Task;
            }

            return Task.FromResult(page == 1
                ? Result(4, isRefreshInProgress: true, Repository(1), Repository(2))
                : Result(4, Repository(3), Repository(4)));
        });
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search);

        await viewModel.InitializeAsync("jithub");
        await viewModel.LoadNextPageAsync();
        refresh.SetResult(Result(4, Repository(1, "updated"), Repository(5)));
        await viewModel.PendingBackgroundRefresh;

        Assert.Contains(viewModel.Results, static item => item.Repository.Id == 3);
        Assert.Contains(viewModel.Results, static item => item.Repository.Id == 4);
        Assert.DoesNotContain(viewModel.Results, static item => item.Repository.Id == 2);
        Assert.Equal("updated", viewModel.Results.Single(static item => item.Repository.Id == 1).Repository.Description);
    }

    [Fact]
    public async Task InitializeAndSearch_EmitCanonicalIdentifierFreeTelemetry()
    {
        RecordingSearchService search = new((_, _, _, _) => Result(2, Repository(1), Repository(2)));
        RecordingTelemetryService telemetry = new();
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search, telemetry);

        await viewModel.InitializeAsync("secret-query-text");
        await viewModel.RefreshAsync();

        Assert.Contains(telemetry.Events, static entry => entry.Name == "repository_search.opened");
        RecordedTelemetryEvent[] loaded = telemetry.Events
            .Where(static entry => entry.Name == "repository_search.loaded")
            .ToArray();
        Assert.Equal(2, loaded.Length);
        Assert.All(loaded, static entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Properties["duration_bucket"]));
            Assert.Equal("fresh", entry.Properties["cache_state"]);
            Assert.DoesNotContain("secret-query-text", entry.Properties.Values);
        });
        Assert.Contains(telemetry.Events, static entry =>
            entry.Name == "repository_search.action.executed" && entry.Properties["action"] == "refresh");
    }

    [Fact]
    public async Task FailedSearch_EmitsCanonicalErrorWithoutQueryContent()
    {
        RecordingSearchService search = new((_, _, _, _) =>
            Task.FromException<CachedResult<GitHubRepositorySearchResponse>>(new HttpRequestException("offline")));
        RecordingTelemetryService telemetry = new();
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search, telemetry);

        await viewModel.InitializeAsync("private-owner/private-repo");

        RecordedTelemetryEvent error = Assert.Single(telemetry.Events, static entry =>
            entry.Name == "repository_search.error");
        Assert.Equal("network", error.Properties["error_kind"]);
        Assert.DoesNotContain("private-owner/private-repo", error.Properties.Values);
    }

    [Fact]
    public async Task RefreshAction_ReportsSuccessOnlyAfterSearchCompletes()
    {
        TaskCompletionSource<CachedResult<GitHubRepositorySearchResponse>> pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSearchService search = new((_, _, _, _) => pending.Task);
        RecordingTelemetryService telemetry = new();
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search, telemetry);
        viewModel.QueryText = "native";

        Task refresh = viewModel.RefreshAsync();

        Assert.DoesNotContain(telemetry.Events, static entry =>
            entry.Name == "repository_search.action.executed" &&
            entry.Properties.GetValueOrDefault("result") == TelemetryTaxonomy.Results.Success);

        pending.SetResult(Result(1, Repository(1)));
        await refresh;

        RecordedTelemetryEvent completed = Assert.Single(telemetry.Events, static entry =>
            entry.Name == "repository_search.action.executed" &&
            entry.Properties.GetValueOrDefault("action") == "refresh");
        Assert.Equal(TelemetryTaxonomy.Results.Success, completed.Properties["result"]);
        Assert.False(string.IsNullOrWhiteSpace(completed.Properties["duration_bucket"]));
    }

    [Fact]
    public async Task ThrowingTelemetry_DoesNotChangeSuccessfulSearchWorkflow()
    {
        RecordingSearchService search = new((_, _, _, _) => Result(1, Repository(1)));
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search, new ThrowingTelemetryService());

        await viewModel.InitializeAsync("native");

        Assert.Single(viewModel.Results);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task SearchSummary_DistinguishesLoadedPartialAndGitHubApiCappedScope()
    {
        RecordingSearchService search = new((_, _, _, _) => Result(
            totalCount: 1500,
            Repository(1),
            Repository(2)));
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search);

        await viewModel.InitializeAsync("native");

        Assert.True(viewModel.IsApiCapped);
        Assert.True(viewModel.IsResultSetPartial);
        Assert.Equal(1500, viewModel.ReportedResultCount);
        Assert.Equal(
            "2 of 1,000 accessible repositories - GitHub limits each search to 1,000 results",
            viewModel.ResultSummary);
    }

    [Fact]
    public async Task SearchSummary_ShowsLoadedCountAgainstUncappedTotal()
    {
        RecordingSearchService search = new((_, _, _, _) => Result(
            totalCount: 250,
            Repository(1),
            Repository(2)));
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search);

        await viewModel.InitializeAsync("native");

        Assert.False(viewModel.IsApiCapped);
        Assert.True(viewModel.IsResultSetPartial);
        Assert.Equal("2 of 250 repositories", viewModel.ResultSummary);
    }

    [Fact]
    public async Task LaterPageFailure_KeepsLoadedRowsAndTruthfulPartialSummary()
    {
        RecordingSearchService search = new((_, page, _, _) =>
            page == 1
                ? Task.FromResult(Result(4, Repository(1), Repository(2)))
                : Task.FromException<CachedResult<GitHubRepositorySearchResponse>>(
                    new HttpRequestException("page 2 unavailable")));
        using RepoSearchResultPageViewModel viewModel = CreateViewModel(search);

        await viewModel.InitializeAsync("native");
        await viewModel.LoadNextPageAsync();

        Assert.Equal([1L, 2L], viewModel.Results.Select(static item => item.Repository.Id));
        Assert.True(viewModel.HasError);
        Assert.Equal("2 of 4 repositories", viewModel.ResultSummary);
        Assert.Equal("GitHub could not load more results. Existing results remain available.", viewModel.ErrorText);
    }

    private static RepoSearchResultPageViewModel CreateViewModel(
        IGitHubRepositorySearchQueryService search,
        ITelemetryService? telemetry = null) =>
        new(
            new TestAuthService(),
            new TestAccountService(),
            search,
            telemetry ?? new RecordingTelemetryService());

    private static GitHubRepository Repository(long id, string? description = null) => new()
    {
        Id = id,
        Name = $"repo-{id}",
        FullName = $"owner/repo-{id}",
        Description = description,
        Owner = new GitHubRepositoryOwner { Login = "owner" }
    };

    private static CachedResult<GitHubRepositorySearchResponse> Result(
        int totalCount,
        params GitHubRepository[] repositories) =>
        Result(totalCount, isRefreshInProgress: false, repositories);

    private static CachedResult<GitHubRepositorySearchResponse> Result(
        int totalCount,
        bool isRefreshInProgress,
        params GitHubRepository[] repositories) =>
        new(
            new GitHubRepositorySearchResponse { TotalCount = totalCount, Items = repositories },
            CacheState.Fresh,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(15),
            isRefreshInProgress);

    private sealed class RecordingSearchService : IGitHubRepositorySearchQueryService
    {
        private readonly Func<RepositorySearchQuery, int, bool, CancellationToken, Task<CachedResult<GitHubRepositorySearchResponse>>> _handler;

        public RecordingSearchService(
            Func<RepositorySearchQuery, int, bool, CancellationToken, CachedResult<GitHubRepositorySearchResponse>> handler)
            : this((query, page, forceRefresh, cancellationToken) =>
                Task.FromResult(handler(query, page, forceRefresh, cancellationToken)))
        {
        }

        public RecordingSearchService(
            Func<RepositorySearchQuery, int, bool, CancellationToken, Task<CachedResult<GitHubRepositorySearchResponse>>> handler)
        {
            _handler = handler;
        }

        public List<(RepositorySearchQuery Query, int Page, bool ForceRefresh)> Requests { get; } = [];

        public Task<CachedResult<GitHubRepositorySearchResponse>> SearchAsync(
            string accessToken,
            string userId,
            RepositorySearchQuery query,
            int page,
            int pageSize,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((query, page, forceRefresh));
            return _handler(query, page, forceRefresh, cancellationToken);
        }
    }

    private sealed class TestAccountService : IAccountService
    {
        public void RemoveUser() { }
        public void SaveUser(long userId) { }
        public long GetUser() => 42;
    }

    private sealed class TestAuthService : IAuthService
    {
        public bool Authenticated { get; set; } = true;
        public GitHubUser? AuthenticatedUser { get; set; } = new() { Id = 42, Login = "viewer" };
        public AuthSessionRecoveryState RecoveryState => AuthSessionRecoveryState.None;
        public Task InitializeAsync() => Task.CompletedTask;
        public Task Authenticate() => Task.CompletedTask;
        public Task<bool> EnsureScopesAsync(params string[] scopes) => Task.FromResult(true);
        public Task<bool> Authorize(string response) => Task.FromResult(true);
        public Task<GitHubUser?> RefreshAuthenticatedUserAsync() => Task.FromResult(AuthenticatedUser);
        public string? GetToken(long userId) => "token";
        public bool CheckAuth(long userId) => true;
        public void SignOut() { }
    }
}
