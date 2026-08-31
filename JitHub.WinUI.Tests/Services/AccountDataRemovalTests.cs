using System.Net;
using System.Net.Http;
using JitHub.Models.CodeViewer;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AccountDataRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHubAccountRemovalTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProductionCoordinator_ClearsAllNavigationAndRepositoryIndexPartitions()
    {
        IGitHubCacheStore queryCache = Substitute.For<IGitHubCacheStore>();
        IGitHubImageCacheStore imageCache = Substitute.For<IGitHubImageCacheStore>();
        IRepoFileCacheService repositoryFiles = Substitute.For<IRepoFileCacheService>();
        IStarLibraryStore stars = Substitute.For<IStarLibraryStore>();
        IStarLibraryRecoveryStore starsRecovery = Substitute.For<IStarLibraryRecoveryStore>();
        IGitHubStarLibraryService starLibraryService = Substitute.For<IGitHubStarLibraryService>();
        IGistMutationJournal gistJournal = Substitute.For<IGistMutationJournal>();
        IRepositoryForkOwnershipStore forkRecovery = Substitute.For<IRepositoryForkOwnershipStore>();
        IIssueNavigationCache issues = Substitute.For<IIssueNavigationCache>();
        IPullRequestNavigationCache pullRequests = Substitute.For<IPullRequestNavigationCache>();
        ICommitNavigationCache commits = Substitute.For<ICommitNavigationCache>();
        IGitHubRepositoryIndexService repositoryIndex = Substitute.For<IGitHubRepositoryIndexService>();
        IRepoTreeService repositoryTrees = Substitute.For<IRepoTreeService>();
        IAuthCredentialStore credentials = Substitute.For<IAuthCredentialStore>();
        queryCache.ClearPartitionAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        imageCache.ClearPartitionAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        repositoryFiles.ClearPartitionAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        stars.ClearUserAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        starsRecovery.ClearUserAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        starLibraryService.ClearAccountStateAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        gistJournal.ClearAccountAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        forkRecovery.ClearAccountAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        issues.ClearPartitionAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        pullRequests.ClearPartitionAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        commits.ClearPartitionAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        repositoryIndex.ClearPartitionAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        repositoryTrees.ClearMemoryCacheAsync("101", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        credentials.GetAccountToken(101).Returns((string?)null);
        using ApplicationTaskCoordinator tasks = new();
        AccountWorkQuiescence accountWork = new(tasks);
        AccountDataRemovalCoordinator coordinator = new(
            queryCache,
            imageCache,
            repositoryFiles,
            stars,
            starsRecovery,
            starLibraryService,
            gistJournal,
            forkRecovery,
            issues,
            pullRequests,
            commits,
            repositoryIndex,
            accountWork,
            credentials,
            new InMemoryAccountDataRemovalJournal(),
            tasks,
            repositoryTrees);

        AccountDataRemovalResult result = await coordinator.RemoveAsync("101");

        Assert.True(result.IsComplete);
        await issues.Received(1).ClearPartitionAsync("101", Arg.Any<CancellationToken>());
        await pullRequests.Received(1).ClearPartitionAsync("101", Arg.Any<CancellationToken>());
        await commits.Received(1).ClearPartitionAsync("101", Arg.Any<CancellationToken>());
        await repositoryIndex.Received(1).ClearPartitionAsync("101", Arg.Any<CancellationToken>());
        await repositoryTrees.Received(1).ClearMemoryCacheAsync("101", Arg.Any<CancellationToken>());
        await starLibraryService.Received(1).ClearAccountStateAsync("101", Arg.Any<CancellationToken>());
        credentials.Received(1).RemoveAccountToken(101);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Coordinator_ReportsPartialFailure_ContinuesAndRetriesIdempotently()
    {
        AccountWorkQuiescence accountWork = new();
        MemoryCredentialVaultBackend backend = new();
        AuthCredentialStore credentials = new(backend, new TestAppConfig());
        credentials.SaveAccountToken(101, "token-101");
        int flakyAttempts = 0;
        int stableAttempts = 0;
        AccountDataRemovalCoordinator coordinator = new(
        [
            new AccountDataRemovalStep("flaky", (_, _) =>
            {
                flakyAttempts++;
                return flakyAttempts == 1
                    ? Task.FromException(new IOException("locked"))
                    : Task.CompletedTask;
            }),
            new AccountDataRemovalStep("stable", (_, _) =>
            {
                stableAttempts++;
                return Task.CompletedTask;
            })
        ],
        accountWork,
        credentials);

        AccountDataRemovalResult first = await coordinator.RemoveAsync("101");
        Assert.Equal("token-101", credentials.GetAccountToken(101));
        Assert.True(accountWork.IsQuiesced("101"));
        Assert.Throws<OperationCanceledException>(() =>
        {
            accountWork.Enter("101");
        });
        AccountDataRemovalResult retry = await coordinator.RemoveAsync("101");

        Assert.False(first.IsComplete);
        Assert.Equal("flaky", Assert.Single(first.Failures).Component);
        Assert.Contains("stable", first.ClearedComponents);
        Assert.True(retry.IsComplete);
        Assert.Equal(2, flakyAttempts);
        Assert.Equal(1, stableAttempts);
        Assert.Contains("identifier-free", retry.DiagnosticsDisposition, StringComparison.Ordinal);
        Assert.Null(credentials.GetAccountToken(101));
        Assert.Contains(AccountDataComponentIds.Credential, retry.ClearedComponents);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task DurableJournal_RestartResumesOnlyPendingStepsAndDeletesJournalAfterCredential()
    {
        string journalRoot = Path.Combine(_root, "account-removal");
        AccountDataRemovalJournal firstJournal = new(journalRoot);
        MemoryCredentialVaultBackend backend = new();
        AuthCredentialStore credentials = new(backend, new TestAppConfig());
        credentials.SaveAccountToken(101, "token-101");
        int completedStepAttempts = 0;
        int pendingStepAttempts = 0;

        AccountDataRemovalCoordinator firstCoordinator = new(
        [
            new AccountDataRemovalStep("completed", (_, _) =>
            {
                completedStepAttempts++;
                return Task.CompletedTask;
            }),
            new AccountDataRemovalStep("pending", (_, _) =>
            {
                pendingStepAttempts++;
                return Task.FromException(new IOException("simulated crash boundary"));
            })
        ],
        new AccountWorkQuiescence(),
        credentials,
        firstJournal);

        AccountDataRemovalResult interrupted = await firstCoordinator.RemoveAsync("101");

        Assert.False(interrupted.IsComplete);
        Assert.Equal("token-101", credentials.GetAccountToken(101));
        Assert.Single(await firstJournal.ReadPendingAsync());

        AccountDataRemovalJournal reopenedJournal = new(journalRoot);
        AccountDataRemovalCoordinator restartedCoordinator = new(
        [
            new AccountDataRemovalStep("completed", (_, _) =>
            {
                completedStepAttempts++;
                return Task.CompletedTask;
            }),
            new AccountDataRemovalStep("pending", (_, _) =>
            {
                pendingStepAttempts++;
                return Task.CompletedTask;
            })
        ],
        new AccountWorkQuiescence(),
        credentials,
        reopenedJournal);

        AccountDataRemovalResult resumed = Assert.Single(await restartedCoordinator.ResumePendingAsync());

        Assert.True(resumed.IsComplete);
        Assert.Equal(1, completedStepAttempts);
        Assert.Equal(2, pendingStepAttempts);
        Assert.Null(credentials.GetAccountToken(101));
        Assert.Empty(await reopenedJournal.ReadPendingAsync());
        Assert.Empty(Directory.GetFiles(journalRoot, "*.json"));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task DurableJournal_PersistsIntentAndPerStoreProgressBeforeRestart()
    {
        string journalRoot = Path.Combine(_root, "journal-progress");
        AccountDataRemovalJournal journal = new(journalRoot);
        await journal.BeginOrReadAsync("101", ["query-cache", "image-cache", "credential"]);
        await journal.MarkCompletedAsync("101", "query-cache");

        AccountDataRemovalJournal reopened = new(journalRoot);
        AccountDataRemovalJournalEntry pending = Assert.Single(await reopened.ReadPendingAsync());

        Assert.Equal("101", pending.AccountPartition);
        Assert.Equal(["query-cache", "image-cache", "credential"], pending.RequestedComponents);
        Assert.Equal(["query-cache"], pending.CompletedComponents);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task ConcurrentRetries_AreSerializedWithoutOverlappingDestructiveWork()
    {
        int attempts = 0;
        int active = 0;
        int maxActive = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountDataRemovalCoordinator coordinator = new(
        [
            new AccountDataRemovalStep("store", async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref attempts);
                int currentActive = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, currentActive);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref active);
            })
        ]);
        Task<AccountDataRemovalResult> first = coordinator.RemoveAsync("101");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<AccountDataRemovalResult> second = coordinator.RemoveAsync("101");

        release.TrySetResult();
        AccountDataRemovalResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, static result => Assert.True(result.IsComplete));
        Assert.Equal(2, attempts);
        Assert.Equal(1, maxActive);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task StarSynchronization_IsDrainedBeforeAccountStoreClear_AndCannotRestartAfterRemoval()
    {
        AccountWorkQuiescence accountWork = new();
        SqliteStarLibraryStore store = new(Path.Combine(_root, "late-star-writer.db"));
        await store.InitializeAsync();
        await store.CreateCategoryAsync("101", "Remove me", "#45B8AC");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IGitHubStarQueryService query = Substitute.For<IGitHubStarQueryService>();
        query.GetPageAsync(
                Arg.Any<string>(),
                "101",
                Arg.Any<int>(),
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<GitHubRequestPriority>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();
                CancellationToken token = call.ArgAt<CancellationToken>(5);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new CachedResult<GitHubStarredRepository[]>(
                    [],
                    CacheState.Miss,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    false,
                    null);
            });
        GitHubStarLibraryService service = new(
            store,
            query,
            Substitute.For<IGitHubClientService>(),
            Substitute.For<IGitHubQueryService>(),
            new StarLibraryRecoveryStore(Path.Combine(_root, "late-star-recovery.json")),
            Substitute.For<ITelemetryService>(),
            accountWork);
        Task<StarSyncState> synchronization = service.SynchronizeAsync("token", "101");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        AccountDataRemovalCoordinator coordinator = new(
            [new AccountDataRemovalStep(AccountDataComponentIds.StarsLibrary, store.ClearUserAsync)],
            accountWork);

        AccountDataRemovalResult removal = await coordinator.RemoveAsync("101");

        Assert.True(removal.IsComplete);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronization);
        Assert.Empty(await store.GetCategoriesAsync("101"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SynchronizeAsync("token", "101"));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Coordinator_DrainsInFlightRepopulationBeforeCleanupAndPreservesOtherAccount()
    {
        AccountWorkQuiescence accountWork = new();
        GitHubRequestQueue queue = new(accountWork);
        MemoryCredentialVaultBackend backend = new();
        AuthCredentialStore credentials = new(backend, new TestAppConfig());
        credentials.SaveAccountToken(101, "token-101");
        credentials.SaveAccountToken(202, "token-202");
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["101"] = "old",
            ["202"] = "keep"
        };
        bool lateWriteFinished = false;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> inFlight = queue.EnqueueForAccountAsync(
            "101",
            "101:late-write",
            GitHubRequestPriority.BackgroundRefresh,
            async token =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    values["101"] = "late-repopulation";
                }

                lateWriteFinished = true;
                return true;
            });
        await started.Task;

        AccountDataRemovalCoordinator coordinator = new(
        [
            new AccountDataRemovalStep("memory-store", (partition, _) =>
            {
                Assert.True(lateWriteFinished);
                values.Remove(partition);
                Assert.False(values.ContainsKey(partition));
                return Task.CompletedTask;
            })
        ],
        accountWork,
        credentials);

        AccountDataRemovalResult result = await coordinator.RemoveAsync("101");

        Assert.True(result.IsComplete);
        Assert.True(await inFlight);
        Assert.False(values.ContainsKey("101"));
        Assert.Equal("keep", values["202"]);
        Assert.Null(credentials.GetAccountToken(101));
        Assert.Equal("token-202", credentials.GetAccountToken(202));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueForAccountAsync(
            "101",
            "101:repopulate-after-removal",
            GitHubRequestPriority.BackgroundRefresh,
            _ => Task.FromResult(true)));

        using IAccountWorkLease otherAccount = accountWork.Enter("202");
        Assert.False(otherAccount.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task Coordinator_VerifiesCredentialIsActuallyAbsentAndAllowsRetry()
    {
        MemoryCredentialVaultBackend backend = new() { IgnoreNextRemoval = true };
        AuthCredentialStore credentials = new(backend, new TestAppConfig());
        credentials.SaveAccountToken(101, "token-101");
        AccountDataRemovalCoordinator coordinator = new(
            [],
            new AccountWorkQuiescence(),
            credentials);

        AccountDataRemovalResult first = await coordinator.RemoveAsync("101");
        AccountDataRemovalResult retry = await coordinator.RemoveAsync("101");

        Assert.False(first.IsComplete);
        Assert.Equal(AccountDataComponentIds.Credential, Assert.Single(first.Failures).Component);
        Assert.True(retry.IsComplete);
        Assert.Null(credentials.GetAccountToken(101));
    }

    [Fact]
    public async Task QueryCache_ClearPartition_RemovesOnlyRequestedAccountAndPayload()
    {
        string payloadRoot = Path.Combine(_root, "payloads");
        SqliteGitHubCacheStore store = new(
            Path.Combine(_root, "query-cache.db"),
            payloadRoot,
            GitHubCachePolicy.Default);
        GitHubQuery<Phase0TestPayload> userOne = CreateQuery("101", "shared") with { CacheKey = "same-key" };
        GitHubQuery<Phase0TestPayload> userTwo = CreateQuery("202", "shared") with { CacheKey = "same-key" };
        await store.PutAsync(userOne, CreateResponse("one", new string('a', 140_000)));
        await store.PutAsync(userTwo, CreateResponse("two", new string('b', 140_000)));

        await store.ClearPartitionAsync("101");

        Assert.Null(await store.TryGetAsync(userOne));
        Assert.Equal("two", (await store.TryGetAsync(userTwo))?.Value?.Name);
        Assert.Single(Directory.GetFiles(payloadRoot, "*.json"));
    }

    [Fact]
    public async Task ImageCache_ClearPartition_RemovesOnlyAttributedAccount()
    {
        string imageRoot = Path.Combine(_root, "images");
        GitHubImageCacheStore store = new(imageRoot, GitHubCachePolicy.Default);
        await store.PutAsync("101:https://images.example.test/one.png", [1, 2, 3], ".png");
        await store.PutAsync("202:https://images.example.test/two.png", [4, 5, 6], ".png");

        await store.ClearPartitionAsync("101");

        Assert.Null(await store.TryGetAsync("101:https://images.example.test/one.png"));
        Assert.NotNull(await store.TryGetAsync("202:https://images.example.test/two.png"));
    }

    [Fact]
    public async Task RepoAndStarsStores_ClearPartition_PreserveOtherAccount()
    {
        RepoFileCacheService files = new(16, 1_000_000, 10_000_000, TimeSpan.FromDays(30), Path.Combine(_root, "repo-files"));
        RepoFileCacheEntry entryOne = CreateRepoFileEntry("sha-one", "one");
        RepoFileCacheEntry entryTwo = CreateRepoFileEntry("sha-two", "two");
        RepoFileCacheKey keyOne = new("owner", "repo", "sha-one", "101");
        RepoFileCacheKey keyTwo = new("owner", "repo", "sha-two", "202");
        await files.PutAsync(keyOne, entryOne, CancellationToken.None);
        await files.PutAsync(keyTwo, entryTwo, CancellationToken.None);

        SqliteStarLibraryStore stars = new(Path.Combine(_root, "stars.db"));
        await stars.InitializeAsync();
        await stars.CreateCategoryAsync("101", "Remove me", "#45B8AC");
        await stars.CreateCategoryAsync("202", "Keep me", "#7E57C2");

        StarLibraryRecoveryStore recovery = new(Path.Combine(_root, "stars-recovery.json"));
        await recovery.EnqueueAsync(new StarLibraryRecoveryEntry(
            "one", "101", "owner/one", null, false, DateTimeOffset.UtcNow, 0, string.Empty));
        await recovery.EnqueueAsync(new StarLibraryRecoveryEntry(
            "two", "202", "owner/two", null, false, DateTimeOffset.UtcNow, 0, string.Empty));

        await files.ClearPartitionAsync("101");
        await stars.ClearUserAsync("101");
        await recovery.ClearUserAsync("101");

        Assert.Null(await files.GetAsync(keyOne, CancellationToken.None));
        Assert.NotNull(await files.GetAsync(keyTwo, CancellationToken.None));
        Assert.Empty(await stars.GetCategoriesAsync("101"));
        Assert.Single(await stars.GetCategoriesAsync("202"));
        Assert.Empty(await recovery.ReadAsync("101"));
        Assert.Single(await recovery.ReadAsync("202"));
    }

    [Fact]
    public async Task GistAndForkRecoveryStores_ClearPartition_PreserveOtherAccount()
    {
        GistMutationJournal gists = new(Path.Combine(_root, "gist-mutations.json"));
        await gists.RecordUpsertAsync("101", "gist-one", new GitHubGist { Id = "gist-one" }, isCreate: true);
        await gists.RecordUpsertAsync("202", "gist-two", new GitHubGist { Id = "gist-two" }, isCreate: true);

        RepositoryForkOwnershipStore forks = new(Path.Combine(_root, "fork-ownership.json"));
        RepositoryForkOwnershipState first = CreateForkState("fork-one", "101", 1);
        RepositoryForkOwnershipState second = CreateForkState("fork-two", "202", 2);
        await forks.UpsertAsync(first);
        await forks.UpsertAsync(second);

        await gists.ClearAccountAsync("101");
        await forks.ClearAccountAsync("101");

        Assert.Empty(await gists.ReadAsync("101"));
        Assert.Single(await gists.ReadAsync("202"));
        Assert.Null(await forks.GetAsync(first.Key));
        Assert.NotNull(await forks.GetAsync(second.Key));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static GitHubQuery<Phase0TestPayload> CreateQuery(string userId, string path) => new(
        GitHubAuthenticationConstants.PublicAccessToken,
        userId,
        HttpMethod.Get,
        path,
        GitHubQueryKeys.Create(userId, HttpMethod.Get, path),
        GitHubCachePolicy.MutableResource,
        TimeSpan.FromMinutes(5),
        Phase0TestJsonContext.Default.Phase0TestPayload,
        ["account-test"],
        GitHubRequestPriority.Visible);

    private static GitHubRestResponse<Phase0TestPayload> CreateResponse(string name, string body) => new(
        HttpStatusCode.OK,
        new Phase0TestPayload { Name = name, Body = body },
        false,
        "etag",
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.UtcNow);

    private static RepoFileCacheEntry CreateRepoFileEntry(string sha, string text) => new()
    {
        Sha = sha,
        ByteLength = text.Length,
        IsBinary = false,
        Bytes = System.Text.Encoding.UTF8.GetBytes(text),
        Text = text,
        Encoding = "utf-8",
        CachedAt = DateTimeOffset.UtcNow
    };

    private static RepositoryForkOwnershipState CreateForkState(
        string key,
        string accountUserId,
        long sourceRepositoryId) => new(
        key,
        accountUserId,
        sourceRepositoryId,
        "source-owner",
        "source-repo",
        "target-owner",
        "target-repo",
        RepositoryForkOwnershipStatus.Accepted,
        sourceRepositoryId + 100,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class TestAppConfig : JitHub.Services.IAppConfig
    {
        public JitHub.Models.Credential Credential { get; } = new() { ClientId = "account-removal-test" };
    }

    private sealed class MemoryCredentialVaultBackend : ICredentialVaultBackend
    {
        private readonly Dictionary<(string Resource, string UserName), string> _values = [];

        public bool IgnoreNextRemoval { get; set; }

        public string? Retrieve(string resource, string userName) =>
            _values.TryGetValue((resource, userName), out string? value) ? value : null;

        public void Store(string resource, string userName, string secret) =>
            _values[(resource, userName)] = secret;

        public void Remove(string resource, string userName)
        {
            if (IgnoreNextRemoval)
            {
                IgnoreNextRemoval = false;
                return;
            }

            _values.Remove((resource, userName));
        }
    }
}
