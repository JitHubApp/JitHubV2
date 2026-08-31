using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CacheRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubCacheRegistryTests", Guid.NewGuid().ToString());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Snapshot_AccountsForEveryPersistentCacheOwner()
    {
        Harness harness = CreateHarness();
        await harness.QueryCache.PutAsync(CreateQuery(), CreateResponse());
        await harness.ImageCache.PutAsync("avatar", [1, 2, 3], ".png");
        await harness.RepoFiles.PutAsync(new RepoFileCacheKey("owner", "repo", "sha"), CreateFileEntry(), default);
        await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");
        await harness.StarRecovery.EnqueueAsync(CreateRecoveryEntry());

        var owners = await harness.Registry.GetSnapshotAsync();

        Assert.Equal(
            [CacheOwnerIds.GitHubQuery, CacheOwnerIds.GitHubImages, CacheOwnerIds.RepositoryFiles, CacheOwnerIds.StarsLibrary],
            owners.Select(owner => owner.Id));
        Assert.True(
            owners.All(owner => owner.Health == CacheOwnerHealth.Healthy),
            string.Join(
                Environment.NewLine,
                owners.Select(owner => $"{owner.Id}: {owner.Health}: {owner.HealthDetail}")) +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(Path.GetDirectoryName(harness.StarRecovery.FilePath)!)
                    .Select(path => $"{Path.GetFileName(path)} ({new FileInfo(path).Length} bytes)")));
        Assert.True(owners.Single(owner => owner.Id == CacheOwnerIds.RepositoryFiles).Bytes > 0);
        Assert.True(owners.Single(owner => owner.Id == CacheOwnerIds.StarsLibrary).IsDurableUserData);

        CacheOwnerSnapshot query = owners.Single(owner => owner.Id == CacheOwnerIds.GitHubQuery);
        Assert.Null(query.SoftCapBytes);
        Assert.Equal(2, query.Caps!.Count);
        Assert.Contains(
            query.Caps,
            cap => cap.Name == "SQLite metadata and inline payloads" &&
                cap.Bytes == GitHubCachePolicy.Default.MetadataSoftCapBytes);
        Assert.Contains("30 minutes repository", query.TtlPolicy, StringComparison.Ordinal);
        Assert.Equal(
            query.Components![CacheMetricKeys.DatabasePhysicalBytes] +
            query.Components[CacheMetricKeys.PayloadDirectoryPhysicalBytes],
            query.Bytes);
        CacheOwnerSnapshot images = owners.Single(owner => owner.Id == CacheOwnerIds.GitHubImages);
        Assert.DoesNotContain("normalized remote-content", images.AccountPartition, StringComparison.Ordinal);
        Assert.Contains("canonical HTTPS image identity", images.AccountPartition, StringComparison.Ordinal);
        Assert.Contains("public unauthenticated", owners.Single(owner => owner.Id == CacheOwnerIds.RepositoryFiles).AccountPartition, StringComparison.Ordinal);
        Assert.Contains(
            "never included in Clear all cache data",
            owners.Single(owner => owner.Id == CacheOwnerIds.StarsLibrary).ClearSemantics,
            StringComparison.Ordinal);
        CacheOwnerSnapshot starOwner = owners.Single(owner => owner.Id == CacheOwnerIds.StarsLibrary);
        Assert.Contains(harness.StarRecovery.FilePath, starOwner.Paths);
        Assert.Equal(1, starOwner.Components![CacheMetricKeys.RecoveryEntryCount]);
        Assert.True(starOwner.Components[CacheMetricKeys.RecoveryJournalPhysicalBytes] > 0);
    }

    [Fact]
    public void InspectionDetail_DeduplicatesAndBoundsCorruptionDescriptions()
    {
        string[] messages =
        [
            "duplicate",
            "duplicate",
            "two",
            "three",
            "four",
            "five",
            "six",
            "seven",
            "eight",
            "nine",
            "ten"
        ];

        string detail = Assert.IsType<string>(CacheInspectionDetail.Format(messages));

        Assert.Equal(1, CountOccurrences(detail, "duplicate"));
        Assert.Contains("2 additional problem(s)", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearEvictable_ClearsNetworkCachesButPreservesStarsUserData()
    {
        Harness harness = CreateHarness();
        GitHubQuery<Phase0TestPayload> query = CreateQuery();
        await harness.QueryCache.PutAsync(query, CreateResponse());
        await harness.ImageCache.PutAsync("avatar", [1, 2, 3], ".png");
        RepoFileCacheKey fileKey = new("owner", "repo", "sha");
        await harness.RepoFiles.PutAsync(fileKey, CreateFileEntry(), default);
        StarCategory category = await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");

        await harness.Registry.ClearEvictableAsync();

        Assert.Null(await harness.QueryCache.TryGetAsync(query));
        Assert.Null(await harness.ImageCache.TryGetAsync("avatar"));
        Assert.Null(await harness.RepoFiles.GetAsync(fileKey, default));
        Assert.Contains(await harness.Stars.GetCategoriesAsync("u1"), item => item.Id == category.Id);
    }

    [Fact]
    public async Task ClearQueryCache_AlsoClearsProjectedRepositoryTrees()
    {
        Harness harness = CreateHarness();
        RecordingTreeCache trees = new();
        CacheRegistry registry = new(
            new AppStoragePathProvider(Path.Combine(_root, "cache-tree"), Path.Combine(_root, "local-tree")),
            harness.QueryCache,
            harness.ImageCache,
            harness.RepoFiles,
            harness.Stars,
            harness.StarRecovery,
            trees);

        await registry.ClearAsync(CacheOwnerIds.GitHubQuery);

        Assert.Equal(1, trees.ClearCount);
        Assert.Null(trees.LastPartition);
    }

    [Fact]
    public async Task ClearStars_ClearsDatabaseAndAtomicRecoveryJournalAcrossRelaunch()
    {
        Harness harness = CreateHarness();
        await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");
        await harness.StarRecovery.EnqueueAsync(CreateRecoveryEntry());

        await harness.Registry.ClearAsync(CacheOwnerIds.StarsLibrary);

        Assert.Empty(await harness.Stars.GetCategoriesAsync("u1"));
        StarLibraryRecoveryStore relaunchedRecovery = new(harness.StarRecovery.FilePath);
        Assert.Empty(await relaunchedRecovery.ReadAsync("u1"));
        CacheStoreInspection recoveryInspection = await relaunchedRecovery.InspectAsync();
        Assert.Equal(CacheOwnerHealth.Healthy, recoveryInspection.Health);
        Assert.Equal(0, recoveryInspection.Components[CacheMetricKeys.RecoveryEntryCount]);
    }

    [Fact]
    public async Task ClearStars_LockedRecoverySidecarReportsFailureAndPreservesDurableDatabase()
    {
        Harness harness = CreateHarness();
        StarCategory category = await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");
        await harness.StarRecovery.EnqueueAsync(CreateRecoveryEntry());
        string sidecar = harness.StarRecovery.FilePath + ".orphan.tmp";
        await File.WriteAllTextAsync(sidecar, "orphan recovery generation");
        File.SetAttributes(sidecar, File.GetAttributes(sidecar) | FileAttributes.ReadOnly);

        try
        {
            CacheOwnerSnapshot before = (await harness.Registry.GetSnapshotAsync())
                .Single(owner => owner.Id == CacheOwnerIds.StarsLibrary);
            Assert.Equal(CacheOwnerHealth.Degraded, before.Health);
            Assert.True(before.OrphanBytes > 0);

            CacheClearException exception = await Assert.ThrowsAsync<CacheClearException>(
                () => harness.Registry.ClearAsync(CacheOwnerIds.StarsLibrary));

            Assert.Equal("stars-recovery", Assert.Single(exception.Failures).OwnerId);
            Assert.Contains(await harness.Stars.GetCategoriesAsync("u1"), item => item.Id == category.Id);
            Assert.Single(await new StarLibraryRecoveryStore(harness.StarRecovery.FilePath).ReadAsync("u1"));
            Assert.Contains(
                await new SqliteStarLibraryStore(harness.Stars.DatabasePath).GetCategoriesAsync("u1"),
                item => item.Id == category.Id);
        }
        finally
        {
            if (File.Exists(sidecar))
            {
                File.SetAttributes(sidecar, FileAttributes.Normal);
            }

            await harness.Registry.ClearAsync(CacheOwnerIds.StarsLibrary);
        }
    }

    [Fact]
    public async Task ClearStars_SqliteFailureAfterJournalStagingRollsBackBothStoresAcrossRelaunch()
    {
        AppStoragePathProvider paths = new(Path.Combine(_root, "cache"), Path.Combine(_root, "local"));
        SqliteStarLibraryStore seed = new(paths.StarLibraryDatabasePath);
        StarCategory category = await seed.CreateCategoryAsync("u1", "Work", "#00AA00");
        StarLibraryRecoveryStore recovery = new(paths.StarLibraryRecoveryPath);
        await recovery.EnqueueAsync(CreateRecoveryEntry());
        SqliteStarLibraryStore failing = new(
            paths.StarLibraryDatabasePath,
            (stage, _) => stage == StarLibraryClearStage.AfterItems
                ? Task.FromException(new IOException("injected SQLite clear failure"))
                : Task.CompletedTask);
        CacheRegistry registry = new(
            paths,
            new SqliteGitHubCacheStore(paths.CacheDatabasePath, paths.PayloadRootPath, GitHubCachePolicy.Default),
            new GitHubImageCacheStore(paths.ImageRootPath, GitHubCachePolicy.Default),
            new RepoFileCacheService(16, 1024 * 1024, 1024 * 1024, TimeSpan.FromDays(7), Path.Combine(_root, "repo-files")),
            failing,
            recovery);

        CacheClearException exception = await Assert.ThrowsAsync<CacheClearException>(
            () => registry.ClearAsync(CacheOwnerIds.StarsLibrary));

        Assert.Equal(CacheOwnerIds.StarsLibrary, Assert.Single(exception.Failures).OwnerId);
        SqliteStarLibraryStore relaunchedStore = new(paths.StarLibraryDatabasePath);
        StarLibraryRecoveryStore relaunchedRecovery = new(paths.StarLibraryRecoveryPath);
        await StarLibraryClearCoordinator.RecoverAsync(relaunchedStore, relaunchedRecovery);
        Assert.Contains(await relaunchedStore.GetCategoriesAsync("u1"), item => item.Id == category.Id);
        Assert.Single(await relaunchedRecovery.ReadAsync("u1"));
        Assert.Null(await relaunchedRecovery.GetPendingClearAsync());
        Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task ClearStars_ExceptionAfterSqliteCommit_FinalizesInsteadOfRestoringJournal()
    {
        AppStoragePathProvider paths = new(Path.Combine(_root, "after-commit-cache"), Path.Combine(_root, "after-commit-local"));
        SqliteStarLibraryStore seed = new(paths.StarLibraryDatabasePath);
        await seed.CreateCategoryAsync("u1", "Work", "#00AA00");
        StarLibraryRecoveryStore recovery = new(paths.StarLibraryRecoveryPath);
        await recovery.EnqueueAsync(CreateRecoveryEntry());
        SqliteStarLibraryStore throwing = new(
            paths.StarLibraryDatabasePath,
            (stage, _) => stage == StarLibraryClearStage.AfterCommit
                ? Task.FromException(new IOException("injected exception after durable commit"))
                : Task.CompletedTask);

        await StarLibraryClearCoordinator.ClearAsync(throwing, recovery);

        SqliteStarLibraryStore relaunchedStore = new(paths.StarLibraryDatabasePath);
        StarLibraryRecoveryStore relaunchedRecovery = new(paths.StarLibraryRecoveryPath);
        Assert.Empty(await relaunchedStore.GetCategoriesAsync("u1"));
        Assert.Empty(await relaunchedRecovery.ReadAsync("u1"));
        Assert.Null(await relaunchedRecovery.GetPendingClearAsync());
        Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task ClearStars_MarkerQueryFailurePreservesIndeterminateTransactionForRelaunch()
    {
        AppStoragePathProvider paths = new(Path.Combine(_root, "marker-query-cache"), Path.Combine(_root, "marker-query-local"));
        SqliteStarLibraryStore seed = new(paths.StarLibraryDatabasePath);
        await seed.CreateCategoryAsync("u1", "Work", "#00AA00");
        StarLibraryRecoveryStore recovery = new(paths.StarLibraryRecoveryPath);
        await recovery.EnqueueAsync(CreateRecoveryEntry());
        SqliteStarLibraryStore unavailable = new(
            paths.StarLibraryDatabasePath,
            (stage, _) => stage switch
            {
                StarLibraryClearStage.AfterCommit =>
                    Task.FromException(new IOException("injected exception after durable commit")),
                StarLibraryClearStage.BeforeMarkerQuery =>
                    Task.FromException(new IOException("injected marker query failure")),
                _ => Task.CompletedTask
            });

        StarLibraryClearCoordinationException exception =
            await Assert.ThrowsAsync<StarLibraryClearCoordinationException>(
                () => StarLibraryClearCoordinator.ClearAsync(unavailable, recovery));

        Assert.Equal(CacheOwnerIds.StarsLibrary, exception.Component);
        Assert.NotNull(await recovery.GetPendingClearAsync());
        SqliteStarLibraryStore relaunchedStore = new(paths.StarLibraryDatabasePath);
        StarLibraryRecoveryStore relaunchedRecovery = new(paths.StarLibraryRecoveryPath);
        await StarLibraryClearCoordinator.RecoverAsync(relaunchedStore, relaunchedRecovery);
        Assert.Empty(await relaunchedStore.GetCategoriesAsync("u1"));
        Assert.Empty(await relaunchedRecovery.ReadAsync("u1"));
        Assert.Null(await relaunchedRecovery.GetPendingClearAsync());
        Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task ClearStars_MarkerFinalizationFailureRelaunchDoesNotRestoreClearedData()
    {
        AppStoragePathProvider paths = new(Path.Combine(_root, "marker-finalize-cache"), Path.Combine(_root, "marker-finalize-local"));
        SqliteStarLibraryStore seed = new(paths.StarLibraryDatabasePath);
        await seed.CreateCategoryAsync("u1", "Work", "#00AA00");
        StarLibraryRecoveryStore recovery = new(paths.StarLibraryRecoveryPath);
        await recovery.EnqueueAsync(CreateRecoveryEntry());
        SqliteStarLibraryStore failing = new(
            paths.StarLibraryDatabasePath,
            (stage, _) => stage == StarLibraryClearStage.BeforeMarkerFinalization
                ? Task.FromException(new IOException("injected marker finalization failure"))
                : Task.CompletedTask);

        StarLibraryClearCoordinationException exception =
            await Assert.ThrowsAsync<StarLibraryClearCoordinationException>(
                () => StarLibraryClearCoordinator.ClearAsync(failing, recovery));

        Assert.Equal(CacheOwnerIds.StarsLibrary, exception.Component);
        Assert.Null(await recovery.GetPendingClearAsync());
        SqliteStarLibraryStore relaunchedStore = new(paths.StarLibraryDatabasePath);
        StarLibraryRecoveryStore relaunchedRecovery = new(paths.StarLibraryRecoveryPath);
        await StarLibraryClearCoordinator.RecoverAsync(relaunchedStore, relaunchedRecovery);
        Assert.Empty(await relaunchedStore.GetCategoriesAsync("u1"));
        Assert.Empty(await relaunchedRecovery.ReadAsync("u1"));
        Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task InterruptedClearBeforeSqliteCommit_RelaunchRestoresJournalAndDatabase()
    {
        Harness harness = CreateHarness();
        StarCategory category = await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");
        await harness.StarRecovery.EnqueueAsync(CreateRecoveryEntry());

        await using (IStarLibraryRecoveryClearTransaction transaction =
                     await harness.StarRecovery.BeginClearAsync())
        {
            // Simulate process loss after journal staging but before SQLite begins.
        }

        SqliteStarLibraryStore relaunchedStore = new(harness.Stars.DatabasePath);
        StarLibraryRecoveryStore relaunchedRecovery = new(harness.StarRecovery.FilePath);
        await StarLibraryClearCoordinator.RecoverAsync(relaunchedStore, relaunchedRecovery);

        Assert.Contains(await relaunchedStore.GetCategoriesAsync("u1"), item => item.Id == category.Id);
        Assert.Single(await relaunchedRecovery.ReadAsync("u1"));
        Assert.Null(await relaunchedRecovery.GetPendingClearAsync());
        Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task InterruptedClearAfterSqliteCommit_RelaunchFinishesBothStores()
    {
        Harness harness = CreateHarness();
        await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");
        await harness.StarRecovery.EnqueueAsync(CreateRecoveryEntry());

        await using (IStarLibraryRecoveryClearTransaction transaction =
                     await harness.StarRecovery.BeginClearAsync())
        {
            await harness.Stars.ClearAllAsync(transaction.TransactionId);
            // Simulate process loss before journal and SQLite marker finalization.
        }

        SqliteStarLibraryStore relaunchedStore = new(harness.Stars.DatabasePath);
        StarLibraryRecoveryStore relaunchedRecovery = new(harness.StarRecovery.FilePath);
        await StarLibraryClearCoordinator.RecoverAsync(relaunchedStore, relaunchedRecovery);

        Assert.Empty(await relaunchedStore.GetCategoriesAsync("u1"));
        Assert.Empty(await relaunchedRecovery.ReadAsync("u1"));
        Assert.Null(await relaunchedRecovery.GetPendingClearAsync());
        Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task JournalFinalizationFailureAfterSqliteCommit_PreservesBackupAndFinishesOnRelaunch()
    {
        Harness harness = CreateHarness();
        await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");
        await harness.StarRecovery.EnqueueAsync(CreateRecoveryEntry());
        string manifestPath = harness.StarRecovery.FilePath + ".clear-transaction.json";

        await using (IStarLibraryRecoveryClearTransaction transaction =
                     await harness.StarRecovery.BeginClearAsync())
        {
            await harness.Stars.ClearAllAsync(transaction.TransactionId);
            string backupPath = Assert.Single(
                Directory.EnumerateFiles(
                    Path.GetDirectoryName(harness.StarRecovery.FilePath)!,
                    Path.GetFileName(harness.StarRecovery.FilePath) + ".clear-*.backup"));
            await using (FileStream manifestLock = new(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                await Assert.ThrowsAsync<CacheClearPostconditionException>(
                    () => transaction.CommitAsync());
                Assert.True(File.Exists(manifestPath));
                Assert.True(File.Exists(backupPath));
            }
        }

        SqliteStarLibraryStore relaunchedStore = new(harness.Stars.DatabasePath);
        StarLibraryRecoveryStore relaunchedRecovery = new(harness.StarRecovery.FilePath);
        await StarLibraryClearCoordinator.RecoverAsync(relaunchedStore, relaunchedRecovery);

        Assert.Empty(await relaunchedStore.GetCategoriesAsync("u1"));
        Assert.Empty(await relaunchedRecovery.ReadAsync("u1"));
        Assert.False(File.Exists(manifestPath));
        Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task ManifestDeletedButBackupDeleteFails_RelaunchTreatsBackupAsHarmlessOrphan()
    {
        Harness harness = CreateHarness();
        await harness.Stars.CreateCategoryAsync("u1", "Work", "#00AA00");
        await harness.StarRecovery.EnqueueAsync(CreateRecoveryEntry());
        string manifestPath = harness.StarRecovery.FilePath + ".clear-transaction.json";
        string? backupPath = null;

        try
        {
            await using (IStarLibraryRecoveryClearTransaction transaction =
                         await harness.StarRecovery.BeginClearAsync())
            {
                await harness.Stars.ClearAllAsync(transaction.TransactionId);
                backupPath = Assert.Single(
                    Directory.EnumerateFiles(
                        Path.GetDirectoryName(harness.StarRecovery.FilePath)!,
                        Path.GetFileName(harness.StarRecovery.FilePath) + ".clear-*.backup"));
                File.SetAttributes(backupPath, File.GetAttributes(backupPath) | FileAttributes.ReadOnly);

                await Assert.ThrowsAsync<CacheClearPostconditionException>(
                    () => transaction.CommitAsync());
                Assert.False(File.Exists(manifestPath));
                Assert.True(File.Exists(backupPath));
            }

            SqliteStarLibraryStore relaunchedStore = new(harness.Stars.DatabasePath);
            StarLibraryRecoveryStore relaunchedRecovery = new(harness.StarRecovery.FilePath);
            await StarLibraryClearCoordinator.RecoverAsync(relaunchedStore, relaunchedRecovery);

            Assert.Empty(await relaunchedStore.GetCategoriesAsync("u1"));
            Assert.Empty(await relaunchedRecovery.ReadAsync("u1"));
            Assert.Null(await relaunchedRecovery.GetPendingClearAsync());
            Assert.Empty(await relaunchedStore.GetCommittedClearTransactionsAsync());
            Assert.True(File.Exists(backupPath));
            CacheStoreInspection inspection = await relaunchedRecovery.InspectAsync();
            Assert.Equal(CacheOwnerHealth.Degraded, inspection.Health);
            Assert.True(inspection.OrphanBytes > 0);
        }
        finally
        {
            if (backupPath is not null && File.Exists(backupPath))
            {
                File.SetAttributes(backupPath, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task Snapshot_IsolatesUnavailableOwnerWithoutHidingHealthyOwners()
    {
        AppStoragePathProvider paths = new(Path.Combine(_root, "unavailable-cache"), Path.Combine(_root, "unavailable-local"));
        SqliteGitHubCacheStore query = new(paths.CacheDatabasePath, paths.PayloadRootPath, GitHubCachePolicy.Default);
        RepoFileCacheService files = new(16, 1024 * 1024, 1024 * 1024, TimeSpan.FromDays(7), Path.Combine(_root, "unavailable-files"));
        SqliteStarLibraryStore stars = new(paths.StarLibraryDatabasePath);
        StarLibraryRecoveryStore recovery = new(paths.StarLibraryRecoveryPath);
        CacheRegistry registry = new(paths, query, new UnavailableImageCache(), files, stars, recovery);

        IReadOnlyList<CacheOwnerSnapshot> owners = await registry.GetSnapshotAsync();

        Assert.Equal(CacheOwnerHealth.Unavailable, owners.Single(owner => owner.Id == CacheOwnerIds.GitHubImages).Health);
        Assert.All(
            owners.Where(owner => owner.Id != CacheOwnerIds.GitHubImages),
            owner => Assert.NotEqual(CacheOwnerHealth.Unavailable, owner.Health));
    }

    [Fact]
    public async Task ClearEvictable_AttemptsEveryOwnerAndReportsPartialFailure()
    {
        Harness harness = CreateHarness();
        GitHubQuery<Phase0TestPayload> query = CreateQuery();
        await harness.QueryCache.PutAsync(query, CreateResponse());
        await harness.ImageCache.PutAsync("avatar", [1, 2, 3], ".png");
        RepoFileCacheKey fileKey = new("owner", "repo", "sha");
        await harness.RepoFiles.PutAsync(fileKey, CreateFileEntry(), default);
        AppStoragePathProvider paths = new(Path.Combine(_root, "cache"), Path.Combine(_root, "local"));
        CacheRegistry registry = new(
            paths,
            new FailingClearQueryCache(harness.QueryCache),
            harness.ImageCache,
            harness.RepoFiles,
            harness.Stars,
            harness.StarRecovery);

        CacheClearException exception = await Assert.ThrowsAsync<CacheClearException>(
            () => registry.ClearEvictableAsync());

        CacheClearFailure failure = Assert.Single(exception.Failures);
        Assert.Equal(CacheOwnerIds.GitHubQuery, failure.OwnerId);
        Assert.NotNull(await harness.QueryCache.TryGetAsync(query));
        Assert.Null(await harness.ImageCache.TryGetAsync("avatar"));
        Assert.Null(await harness.RepoFiles.GetAsync(fileKey, default));
    }

    private Harness CreateHarness()
    {
        AppStoragePathProvider paths = new(Path.Combine(_root, "cache"), Path.Combine(_root, "local"));
        SqliteGitHubCacheStore query = new(paths.CacheDatabasePath, paths.PayloadRootPath, GitHubCachePolicy.Default);
        GitHubImageCacheStore images = new(paths.ImageRootPath, GitHubCachePolicy.Default);
        RepoFileCacheService files = new(16, 1024 * 1024, 1024 * 1024, TimeSpan.FromDays(7), Path.Combine(_root, "repo-files"));
        SqliteStarLibraryStore stars = new(paths.StarLibraryDatabasePath);
        StarLibraryRecoveryStore recovery = new(paths.StarLibraryRecoveryPath);
        return new Harness(
            query,
            images,
            files,
            stars,
            recovery,
            new CacheRegistry(paths, query, images, files, stars, recovery));
    }

    private static GitHubQuery<Phase0TestPayload> CreateQuery() => new(
        "token",
        "u1",
        HttpMethod.Get,
        "test/cache-owner",
        GitHubQueryKeys.Create("u1", HttpMethod.Get, "test/cache-owner"),
        GitHubCachePolicy.MutableResource,
        TimeSpan.FromMinutes(5),
        Phase0TestJsonContext.Default.Phase0TestPayload,
        ["registry"],
        GitHubRequestPriority.Visible);

    private static GitHubRestResponse<Phase0TestPayload> CreateResponse() => new(
        HttpStatusCode.OK,
        new Phase0TestPayload { Name = "registry", Body = "body" },
        IsNotModified: false,
        ETag: "\"registry\"",
        LastModified: DateTimeOffset.UtcNow,
        Link: null,
        RateLimitRemaining: 100,
        RateLimitReset: null,
        RetryAfter: null,
        FetchedAt: DateTimeOffset.UtcNow);

    private static StarLibraryRecoveryEntry CreateRecoveryEntry() => new(
        "recovery-1",
        "u1",
        "owner/repo",
        null,
        false,
        DateTimeOffset.UtcNow,
        0,
        "pending");

    private static RepoFileCacheEntry CreateFileEntry() => new()
    {
        Sha = "sha",
        ByteLength = 4,
        IsBinary = false,
        Bytes = [1, 2, 3, 4],
        Text = "test",
        Encoding = "utf-8",
        CachedAt = DateTimeOffset.UtcNow
    };

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }

    private sealed record Harness(
        SqliteGitHubCacheStore QueryCache,
        GitHubImageCacheStore ImageCache,
        RepoFileCacheService RepoFiles,
        SqliteStarLibraryStore Stars,
        StarLibraryRecoveryStore StarRecovery,
        CacheRegistry Registry);

    private sealed class UnavailableImageCache : IGitHubImageCacheStore
    {
        public Task<GitHubImageCacheEntry?> TryGetAsync(string cacheKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubImageCacheRead?> TryReadAsync(string cacheKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubImageCacheEntry> PutAsync(string cacheKey, byte[] bytes, string extension, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubImageCacheEntry> PutAsync(string cacheKey, byte[] bytes, string extension, GitHubImageCacheWriteMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkFreshAsync(string cacheKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnforceCapAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> GetTotalBytesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingClearQueryCache : IGitHubCacheStore
    {
        private readonly IGitHubCacheStore _inner;

        public FailingClearQueryCache(IGitHubCacheStore inner)
        {
            _inner = inner;
        }

        public Task<CachedResult<T>?> TryGetAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default) where T : class =>
            _inner.TryGetAsync(query, cancellationToken);
        public Task PutAsync<T>(GitHubQuery<T> query, GitHubRestResponse<T> response, CancellationToken cancellationToken = default) where T : class =>
            _inner.PutAsync(query, response, cancellationToken);
        public Task MarkRevalidatedAsync<T>(GitHubQuery<T> query, GitHubRestResponse<T> response, CancellationToken cancellationToken = default) where T : class =>
            _inner.MarkRevalidatedAsync(query, response, cancellationToken);
        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => _inner.InvalidateAsync(cacheKey, cancellationToken);
        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => _inner.InvalidateTagsAsync(tags, cancellationToken);
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => Task.FromException(new IOException("simulated clear failure"));
        public Task<long> GetTotalPayloadBytesAsync(CancellationToken cancellationToken = default) => _inner.GetTotalPayloadBytesAsync(cancellationToken);
        public Task<long> GetTotalMetadataBytesAsync(CancellationToken cancellationToken = default) => _inner.GetTotalMetadataBytesAsync(cancellationToken);
        public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) => _inner.GetSchemaVersionAsync(cancellationToken);
        public Task EnforceCapsAsync(CancellationToken cancellationToken = default) => _inner.EnforceCapsAsync(cancellationToken);
        public Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default) => _inner.InspectAsync(cancellationToken);
    }

    private sealed class RecordingTreeCache : IRepoTreeService
    {
        public int ClearCount { get; private set; }

        public string? LastPartition { get; private set; }

        public Task ClearMemoryCacheAsync(
            string? accountPartition = null,
            CancellationToken cancellationToken = default)
        {
            ClearCount++;
            LastPartition = accountPartition;
            return Task.CompletedTask;
        }

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner,
            string name,
            string refOrSha,
            CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner,
            string name,
            string path,
            string refOrSha,
            CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner,
            string name,
            string sha,
            CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();
    }
}
