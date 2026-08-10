using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepositoryIndexServiceTests
{
    [Fact]
    public void RepositoryPermissions_DeserializeAdminCapabilityForDestructiveActions()
    {
        GitHubRepository? repository = JsonSerializer.Deserialize(
            """{"id":42,"name":"sample","full_name":"owner/sample","owner":{"login":"owner"},"permissions":{"admin":true,"maintain":true,"push":true,"triage":true,"pull":true}}""",
            Phase0GitHubJsonSerializerContext.Default.GitHubRepository);

        Assert.NotNull(repository);
        Assert.NotNull(repository.Permissions);
        Assert.True(repository.Permissions.Admin);
        Assert.True(repository.Permissions.Maintain);
    }

    [Fact]
    public async Task Initialize_RestoresAllContiguousCachedPagesAndReportsCompleteScope()
    {
        MemoryCacheStore cache = new();
        cache.Pages[1] = CreateRepositories(1, 100);
        cache.Pages[2] = CreateRepositories(101, 2);
        RecordingQueryService query = new();
        GitHubRepositoryIndexService service = new(query, cache, Substitute.For<ITelemetryService>());

        AccountRepositoryIndexSnapshot snapshot = await service.InitializeAsync("token", "42");

        Assert.True(snapshot.IsComplete);
        Assert.Equal(2, snapshot.PagesLoaded);
        Assert.Equal(102, snapshot.IndexedCount);
        Assert.Equal("owner/repo-1", snapshot.Repositories[0].FullName);
        Assert.Equal("owner/repo-102", snapshot.Repositories[^1].FullName);
        Assert.Empty(query.RequestedPages);
    }

    [Fact]
    public async Task PublicPreview_UsesDeterministicCompleteFixtureWithoutNetworkReads()
    {
        MemoryCacheStore cache = new();
        RecordingQueryService query = new()
        {
            RefreshHandler = _ => throw new InvalidOperationException("Public preview must not use the network.")
        };
        GitHubRepositoryIndexService service = new(query, cache, Substitute.For<ITelemetryService>());

        AccountRepositoryIndexSnapshot initialized = await service.InitializeAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "ignored-account");
        AccountRepositoryIndexSnapshot synchronized = await service.SynchronizeAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "ignored-account");

        Assert.True(initialized.IsComplete);
        Assert.True(synchronized.IsComplete);
        Assert.Equal(6, synchronized.IndexedCount);
        Assert.Contains(synchronized.Repositories, static repository => repository.Fork);
        Assert.Contains(synchronized.Repositories, static repository => repository.Archived);
        Assert.Empty(query.RequestedPages);
    }

    [Fact]
    public async Task Synchronize_PublishesEachPageBeforeTheNextPageCompletes()
    {
        MemoryCacheStore cache = new();
        TaskCompletionSource<CachedResult<GitHubRepository[]>> secondPage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingQueryService query = new()
        {
            RefreshHandler = page => page == 1
                ? Task.FromResult(CreateResult(CreateRepositories(1, 100)))
                : secondPage.Task
        };
        GitHubRepositoryIndexService service = new(query, cache, Substitute.For<ITelemetryService>());
        TaskCompletionSource<AccountRepositoryIndexSnapshot> firstPagePublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, args) =>
        {
            if (args.Snapshot.IndexedCount == 100)
            {
                firstPagePublished.TrySetResult(args.Snapshot);
            }
        };

        Task<AccountRepositoryIndexSnapshot> synchronization = service.SynchronizeAsync("token", "42");
        AccountRepositoryIndexSnapshot intermediate = await firstPagePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(intermediate.IsSynchronizing);
        Assert.Equal(100, intermediate.IndexedCount);
        Assert.False(synchronization.IsCompleted);

        secondPage.SetResult(CreateResult(CreateRepositories(101, 3)));
        AccountRepositoryIndexSnapshot completed = await synchronization;
        Assert.True(completed.IsComplete);
        Assert.Equal(103, completed.IndexedCount);
    }

    [Fact]
    public async Task Synchronize_FailurePreservesCachedRowsAndMarksScopeStale()
    {
        MemoryCacheStore cache = new();
        cache.Pages[1] = CreateRepositories(1, 100);
        cache.Pages[2] = CreateRepositories(101, 1);
        RecordingQueryService query = new()
        {
            RefreshHandler = page => page == 1
                ? Task.FromResult(CreateResult(CreateRepositories(1, 100)))
                : throw new InvalidOperationException("offline")
        };
        GitHubRepositoryIndexService service = new(query, cache, Substitute.For<ITelemetryService>());
        await service.InitializeAsync("token", "42");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync("token", "42"));
        AccountRepositoryIndexSnapshot snapshot = service.GetSnapshot("42");

        Assert.Equal(101, snapshot.IndexedCount);
        Assert.Contains(snapshot.Repositories, static repository => repository.Id == 101);
        Assert.Equal(CacheState.Stale, snapshot.CacheState);
        Assert.Equal(
            "JitHub could not refresh this content. Existing data is still available.",
            snapshot.ErrorMessage);
        Assert.DoesNotContain("offline", snapshot.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Synchronize_ReusesRecentCompleteIndexUnlessMutationForcesRefresh()
    {
        MemoryCacheStore cache = new();
        RecordingQueryService query = new()
        {
            RefreshHandler = _ => Task.FromResult(CreateResult(CreateRepositories(1, 2)))
        };
        GitHubRepositoryIndexService service = new(query, cache, Substitute.For<ITelemetryService>());

        await service.SynchronizeAsync("token", "42");
        await service.SynchronizeAsync("token", "42");
        await service.SynchronizeAsync("token", "42", forceRefresh: true);

        Assert.Equal([1, 1], query.RequestedPages);
    }

    [Fact]
    public async Task ConcurrentSynchronizationCallersJoinTheActiveReconciliation()
    {
        MemoryCacheStore cache = new();
        TaskCompletionSource<CachedResult<GitHubRepository[]>> secondPage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingQueryService query = new()
        {
            RefreshHandler = page => page == 1
                ? Task.FromResult(CreateResult(CreateRepositories(1, 100)))
                : secondPage.Task
        };
        GitHubRepositoryIndexService service = new(query, cache, Substitute.For<ITelemetryService>());
        TaskCompletionSource firstPagePublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, args) =>
        {
            if (args.Snapshot.IsSynchronizing && args.Snapshot.PagesLoaded == 1)
            {
                firstPagePublished.TrySetResult();
            }
        };

        Task<AccountRepositoryIndexSnapshot> first = service.SynchronizeAsync("token", "42");
        await firstPagePublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<AccountRepositoryIndexSnapshot> joined = service.SynchronizeAsync("token", "42");

        Assert.False(joined.IsCompleted);
        secondPage.SetResult(CreateResult(CreateRepositories(101, 2)));

        AccountRepositoryIndexSnapshot[] snapshots = await Task.WhenAll(first, joined);
        Assert.All(snapshots, static snapshot =>
        {
            Assert.True(snapshot.IsComplete);
            Assert.Equal(102, snapshot.IndexedCount);
            Assert.False(snapshot.IsSynchronizing);
        });
        Assert.Equal([1, 2], query.RequestedPages);
    }

    [Fact]
    public async Task AccountsRemainPartitionedAndDeletionInvalidatesOnlyTheCurrentAccountTag()
    {
        MemoryCacheStore cache = new();
        cache.PagesByUser["42"] = new Dictionary<int, GitHubRepository[]> { [1] = CreateRepositories(1, 2) };
        cache.PagesByUser["84"] = new Dictionary<int, GitHubRepository[]> { [1] = CreateRepositories(100, 2) };
        RecordingQueryService query = new();
        GitHubRepositoryIndexService service = new(query, cache, Substitute.For<ITelemetryService>());
        await service.InitializeAsync("token-a", "42");
        await service.InitializeAsync("token-b", "84");

        await service.RemoveRepositoriesAsync("42", [1]);

        Assert.Single(service.GetSnapshot("42").Repositories);
        Assert.Equal(2, service.GetSnapshot("84").Repositories.Count);
        Assert.Contains("account-repositories:42", query.InvalidatedTags);
        Assert.DoesNotContain("account-repositories:84", query.InvalidatedTags);
    }

    [Fact]
    public async Task ClearPartition_RemovesOnlyRequestedAccountMemory()
    {
        MemoryCacheStore cache = new();
        cache.PagesByUser["42"] = new Dictionary<int, GitHubRepository[]> { [1] = CreateRepositories(1, 2) };
        cache.PagesByUser["84"] = new Dictionary<int, GitHubRepository[]> { [1] = CreateRepositories(100, 2) };
        GitHubRepositoryIndexService service = new(
            new RecordingQueryService(),
            cache,
            Substitute.For<ITelemetryService>());
        await service.InitializeAsync("token-a", "42");
        await service.InitializeAsync("token-b", "84");

        await service.ClearPartitionAsync("42");

        Assert.Empty(service.GetSnapshot("42").Repositories);
        Assert.Equal(2, service.GetSnapshot("84").Repositories.Count);
    }

    [Fact]
    public async Task Quiescence_DrainsSynchronizationBeforeClearAndPreventsLatePublication()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CachedResult<GitHubRepository[]>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingQueryService query = new()
        {
            RefreshHandler = _ =>
            {
                entered.TrySetResult();
                return release.Task;
            }
        };
        AccountWorkQuiescence accountWork = new();
        GitHubRepositoryIndexService service = new(
            query,
            new MemoryCacheStore(),
            Substitute.For<ITelemetryService>(),
            accountWork);

        Task<AccountRepositoryIndexSnapshot> synchronization = service.SynchronizeAsync("token", "42");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task quiesce = accountWork.QuiesceAsync("42");
        await Task.Delay(30);
        Assert.False(quiesce.IsCompleted);

        release.SetResult(CreateResult(CreateRepositories(1, 2)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronization);
        await quiesce.WaitAsync(TimeSpan.FromSeconds(2));
        await service.ClearPartitionAsync("42");

        Assert.Empty(service.GetSnapshot("42").Repositories);
    }

    [Fact]
    public void Projection_SearchesMetadataAndAppliesScopeAndSortWithoutDuplicates()
    {
        GitHubRepository publicRepo = CreateRepository(1, "alpha");
        publicRepo.Description = "Desktop productivity";
        publicRepo.Language = "C#";
        publicRepo.StargazersCount = 3;
        GitHubRepository privateRepo = CreateRepository(2, "beta");
        privateRepo.Private = true;
        privateRepo.Topics = ["winui"];
        privateRepo.StargazersCount = 20;
        GitHubRepository fork = CreateRepository(3, "gamma");
        fork.Fork = true;

        IReadOnlyList<GitHubRepository> searched = RepositoryLibraryProjection.Apply(
            [publicRepo, privateRepo, fork, publicRepo],
            "winui",
            RepositoryLibraryFilter.All,
            RepositoryLibrarySort.MostStars);
        IReadOnlyList<GitHubRepository> sources = RepositoryLibraryProjection.Apply(
            [publicRepo, privateRepo, fork],
            null,
            RepositoryLibraryFilter.Public,
            RepositoryLibrarySort.Name);

        Assert.Single(searched);
        Assert.Same(privateRepo, searched[0]);
        Assert.Single(sources);
        Assert.Same(publicRepo, sources[0]);
    }

    private static CachedResult<GitHubRepository[]> CreateResult(GitHubRepository[] repositories) =>
        new(repositories, CacheState.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30));

    private static GitHubRepository[] CreateRepositories(int start, int count) =>
        Enumerable.Range(start, count).Select(index => CreateRepository(index, $"repo-{index}")).ToArray();

    private static GitHubRepository CreateRepository(long id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            FullName = $"owner/{name}",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-id),
            Owner = new GitHubRepositoryOwner { Login = "owner" }
        };

    private sealed class MemoryCacheStore : IGitHubCacheStore
    {
        public Dictionary<int, GitHubRepository[]> Pages { get; } = [];

        public Dictionary<string, Dictionary<int, GitHubRepository[]>> PagesByUser { get; } = [];

        public Task<CachedResult<T>?> TryGetAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class
        {
            int page = ParsePage(query.RelativePath);
            Dictionary<int, GitHubRepository[]> pages = PagesByUser.TryGetValue(query.UserId, out Dictionary<int, GitHubRepository[]>? account)
                ? account
                : Pages;
            CachedResult<T>? result = pages.TryGetValue(page, out GitHubRepository[]? repositories)
                ? (CachedResult<T>)(object)CreateResult(repositories)
                : null;
            return Task.FromResult(result);
        }

        public Task PutAsync<T>(GitHubQuery<T> query, GitHubRestResponse<T> response, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task MarkRevalidatedAsync<T>(GitHubQuery<T> query, GitHubRestResponse<T> response, CancellationToken cancellationToken = default) where T : class => Task.CompletedTask;
        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<long> GetTotalPayloadBytesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<long> GetTotalMetadataBytesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task EnforceCapsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingQueryService : IGitHubQueryService
    {
        public Func<int, Task<CachedResult<GitHubRepository[]>>> RefreshHandler { get; set; } =
            _ => Task.FromResult(CreateResult([]));

        public List<int> RequestedPages { get; } = [];

        public List<string> InvalidatedTags { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(GitHubQuery<T> query, QueryFetchPolicy fetchPolicy, CancellationToken cancellationToken = default)
            where T : class => RefreshAsync(query, cancellationToken);

        public async Task<CachedResult<T>> RefreshAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class
        {
            int page = ParsePage(query.RelativePath);
            RequestedPages.Add(page);
            return (CachedResult<T>)(object)await RefreshHandler(page);
        }

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
        {
            InvalidatedTags.AddRange(tags);
            return Task.CompletedTask;
        }
    }

    private static int ParsePage(string path)
    {
        string marker = "page=";
        int start = path.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int end = path.IndexOf('&', start);
        string value = end < 0 ? path[start..] : path[start..end];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
