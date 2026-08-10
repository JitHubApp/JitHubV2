using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class Phase1SettingsDiagnosticsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubPhase1DiagnosticsTests", Guid.NewGuid().ToString());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Snapshot_ReportsPathsSizesSchemaAndStoreStatus()
    {
        TestHarness harness = CreateHarness(storeAvailable: true);
        await harness.CacheStore.PutAsync(CreateQuery("snapshot", TimeSpan.FromMinutes(5)), CreateResponse("snapshot", "body"));
        await harness.ImageCacheStore.PutAsync("avatar", [1, 2, 3, 4], ".png");
        await harness.DiagnosticsStore.AppendAsync(CreateDiagnostic("settings.opened"));

        SettingsDiagnosticsSnapshot snapshot = await harness.Service.GetSnapshotAsync();

        Assert.Equal(harness.Paths.CacheDatabasePath, snapshot.Cache.DatabasePath);
        Assert.Equal(harness.Paths.PayloadRootPath, snapshot.Cache.PayloadPath);
        Assert.Equal(harness.Paths.ImageRootPath, snapshot.Cache.ImagePath);
        Assert.Equal(harness.Paths.DiagnosticsPath, snapshot.Diagnostics.Path);
        Assert.Equal(2, snapshot.Cache.SchemaVersion);
        Assert.True(snapshot.Cache.MetadataBytes > 0);
        Assert.True(snapshot.Cache.ImageBytes > 0);
        Assert.True(snapshot.Diagnostics.Bytes > 0);
        Assert.Equal("Available", snapshot.StoreTelemetry.Status);
        Assert.True(snapshot.StoreTelemetry.IsAvailable);
        Assert.True(snapshot.StoreTelemetryEnabled);
        Assert.True(snapshot.DiagnosticsEnabled);
        Assert.NotNull(snapshot.StarLibrary);
        Assert.Equal(harness.Paths.StarLibraryDatabasePath, snapshot.StarLibrary!.DatabasePath);
        Assert.Equal(harness.Paths.StarLibraryRecoveryPath, snapshot.StarLibrary.RecoveryJournalPath);
        Assert.NotNull(snapshot.RepoFiles);
        Assert.Equal(harness.RepoFileCache.RootPath, snapshot.RepoFiles!.RootPath);
        Assert.Collection(
            snapshot.CacheOwners!,
            owner => Assert.Equal(CacheOwnerIds.GitHubQuery, owner.Id),
            owner => Assert.Equal(CacheOwnerIds.GitHubImages, owner.Id),
            owner => Assert.Equal(CacheOwnerIds.RepositoryFiles, owner.Id),
            owner => Assert.Equal(CacheOwnerIds.StarsLibrary, owner.Id));
    }

    [Fact]
    public async Task ClearDiagnostics_RemovesDiagnosticsFileAndKeepsCache()
    {
        TestHarness harness = CreateHarness(storeAvailable: true);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("cache", TimeSpan.FromMinutes(5));
        await harness.CacheStore.PutAsync(query, CreateResponse("cache", "body"));
        await harness.DiagnosticsStore.AppendAsync(CreateDiagnostic("settings.clear"));

        await harness.Service.ClearDiagnosticsAsync();

        Assert.Equal(0, (await harness.Service.GetSnapshotAsync()).Diagnostics.Bytes);
        Assert.NotNull(await harness.CacheStore.TryGetAsync(query));
    }

    [Fact]
    public async Task ClearQueryCache_RemovesMetadataEntriesAndPayloadFiles()
    {
        TestHarness harness = CreateHarness(storeAvailable: true);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("large", TimeSpan.FromMinutes(5));
        await harness.CacheStore.PutAsync(query, CreateResponse("large", new string('x', 140_000)));
        Assert.NotEmpty(Directory.GetFiles(harness.Paths.PayloadRootPath, "*.json"));

        await harness.Service.ClearQueryCacheAsync();

        Assert.Null(await harness.CacheStore.TryGetAsync(query));
        Assert.Empty(Directory.GetFiles(harness.Paths.PayloadRootPath, "*.json"));
        SettingsDiagnosticsSnapshot snapshot = await harness.Service.GetSnapshotAsync();
        CacheOwnerSnapshot queryOwner = snapshot.CacheOwners!.Single(owner => owner.Id == CacheOwnerIds.GitHubQuery);
        Assert.Equal(queryOwner.Components![CacheMetricKeys.DatabasePhysicalBytes], snapshot.Cache.MetadataBytes);
        Assert.Equal(0, snapshot.Cache.PayloadBytes);
        Assert.Equal(0, queryOwner.LogicalBytes);
        Assert.Equal(2, snapshot.Cache.SchemaVersion);
    }

    [Fact]
    public async Task ClearImageCache_RemovesImageFiles()
    {
        TestHarness harness = CreateHarness(storeAvailable: true);
        await harness.ImageCacheStore.PutAsync("avatar", [1, 2, 3, 4], ".png");

        await harness.Service.ClearImageCacheAsync();

        Assert.Null(await harness.ImageCacheStore.TryGetAsync("avatar"));
        Assert.Equal(0, (await harness.Service.GetSnapshotAsync()).Cache.ImageBytes);
    }

    [Fact]
    public async Task ClearRepoFileCache_RemovesShaPayloadsAndRefreshesSnapshot()
    {
        TestHarness harness = CreateHarness(storeAvailable: true);
        RepoFileCacheKey key = new("owner", "repo", "abc123");
        await harness.RepoFileCache.PutAsync(key, CreateRepoFileEntry("abc123"), default);
        Assert.True((await harness.Service.GetSnapshotAsync()).Cache.RepositoryFileBytes > 0);

        await harness.Service.ClearRepoFileCacheAsync();

        Assert.Null(await harness.RepoFileCache.GetAsync(key, default));
        SettingsDiagnosticsSnapshot snapshot = await harness.Service.GetSnapshotAsync();
        Assert.Equal(0, snapshot.Cache.RepositoryFileBytes);
        Assert.Equal(0, snapshot.RepoFiles!.Bytes);
    }

    [Fact]
    public async Task ClearAllCache_ClearsQueryAndImagesWithoutTouchingDiagnostics()
    {
        TestHarness harness = CreateHarness(storeAvailable: true);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("all", TimeSpan.FromMinutes(5));
        await harness.CacheStore.PutAsync(query, CreateResponse("all", new string('x', 140_000)));
        await harness.ImageCacheStore.PutAsync("avatar", [1, 2, 3, 4], ".png");
        RepoFileCacheKey fileKey = new("owner", "repo", "clear-all");
        await harness.RepoFileCache.PutAsync(fileKey, CreateRepoFileEntry("clear-all"), default);
        await harness.DiagnosticsStore.AppendAsync(CreateDiagnostic("settings.cache.clear_all"));

        await harness.Service.ClearAllCacheAsync();

        SettingsDiagnosticsSnapshot snapshot = await harness.Service.GetSnapshotAsync();
        Assert.Null(await harness.CacheStore.TryGetAsync(query));
        Assert.Equal(0, snapshot.Cache.PayloadBytes);
        Assert.Equal(0, snapshot.Cache.ImageBytes);
        Assert.Equal(0, snapshot.Cache.RepositoryFileBytes);
        Assert.Null(await harness.RepoFileCache.GetAsync(fileKey, default));
        Assert.True(snapshot.Diagnostics.Bytes > 0);
    }

    [Fact]
    public async Task TelemetrySettings_DefaultByStoreAvailabilityAndRespectSavedValues()
    {
        TestHarness available = CreateHarness(storeAvailable: true);
        SettingsDiagnosticsSnapshot availableSnapshot = await available.Service.GetSnapshotAsync();
        Assert.True(availableSnapshot.DiagnosticsEnabled);
        Assert.True(availableSnapshot.StoreTelemetryEnabled);

        TestHarness unavailable = CreateHarness(storeAvailable: false);
        SettingsDiagnosticsSnapshot unavailableSnapshot = await unavailable.Service.GetSnapshotAsync();
        Assert.True(unavailableSnapshot.DiagnosticsEnabled);
        Assert.False(unavailableSnapshot.StoreTelemetryEnabled);
        Assert.True(unavailableSnapshot.StoreTelemetry.IsDisabledByCompatibility);

        TestHarness disabled = CreateHarness(storeAvailable: true);
        disabled.Settings.Save(SettingsKeys.DiagnosticsEnabled, false);
        disabled.Settings.Save(SettingsKeys.StoreTelemetryEnabled, false);
        SettingsDiagnosticsSnapshot disabledSnapshot = await disabled.Service.GetSnapshotAsync();
        Assert.False(disabledSnapshot.DiagnosticsEnabled);
        Assert.False(disabledSnapshot.StoreTelemetryEnabled);
    }

    [Fact]
    public async Task ExportDiagnostics_WritesSameNdjsonEntries()
    {
        TestHarness harness = CreateHarness(storeAvailable: true);
        await harness.DiagnosticsStore.AppendAsync(CreateDiagnostic("settings.export.one"));
        await harness.DiagnosticsStore.AppendAsync(CreateDiagnostic("settings.export.two"));
        string exportPath = Path.Combine(_root, "export", "diagnostics.ndjson");

        await harness.Service.ExportDiagnosticsAsync(exportPath);

        string[] lines = await File.ReadAllLinesAsync(exportPath);
        Assert.Equal(2, lines.Length);
        LocalDiagnosticEvent? first = JsonSerializer.Deserialize<LocalDiagnosticEvent>(lines[0]);
        LocalDiagnosticEvent? second = JsonSerializer.Deserialize<LocalDiagnosticEvent>(lines[1]);
        Assert.Equal("settings.export.one", first?.Name);
        Assert.Equal("settings.export.two", second?.Name);
    }

    [Fact]
    public async Task Snapshot_UsesRegistryFaultIsolationWhenOneOwnerIsUnavailable()
    {
        string localCache = Path.Combine(_root, "fault-isolation", "cache");
        string localFolder = Path.Combine(_root, "fault-isolation", "local");
        AppStoragePathProvider paths = new(localCache, localFolder);
        SqliteGitHubCacheStore cacheStore = new(paths.CacheDatabasePath, paths.PayloadRootPath, GitHubCachePolicy.Default);
        SqliteStarLibraryStore starLibrary = new(paths.StarLibraryDatabasePath);
        RepoFileCacheService repoFiles = new(16, 1024 * 1024, 1024 * 1024, TimeSpan.FromDays(7), Path.Combine(localCache, "RepoFileCache"));
        await using LocalDiagnosticsStore diagnostics = new(paths.DiagnosticsPath, 1024 * 1024, TimeSpan.FromDays(14));
        StarLibraryRecoveryStore recovery = new(paths.StarLibraryRecoveryPath);
        CacheRegistry registry = new(paths, cacheStore, new UnavailableImageCache(), repoFiles, starLibrary, recovery);
        SettingsDiagnosticsService service = new(
            paths,
            diagnostics,
            new FakeStoreTelemetrySink(true),
            new MemorySettingService(),
            registry);

        SettingsDiagnosticsSnapshot snapshot = await service.GetSnapshotAsync();

        Assert.Equal(0, snapshot.Cache.ImageBytes);
        Assert.Equal(
            CacheOwnerHealth.Unavailable,
            snapshot.CacheOwners!.Single(owner => owner.Id == CacheOwnerIds.GitHubImages).Health);
        Assert.Equal(
            CacheOwnerHealth.Healthy,
            snapshot.CacheOwners!.Single(owner => owner.Id == CacheOwnerIds.GitHubQuery).Health);
    }

    private TestHarness CreateHarness(bool storeAvailable)
    {
        string localCache = Path.Combine(_root, Guid.NewGuid().ToString(), "cache");
        string localFolder = Path.Combine(_root, Guid.NewGuid().ToString(), "local");
        AppStoragePathProvider paths = new(localCache, localFolder);
        SqliteGitHubCacheStore cacheStore = new(paths.CacheDatabasePath, paths.PayloadRootPath, GitHubCachePolicy.Default);
        GitHubImageCacheStore imageStore = new(paths.ImageRootPath, GitHubCachePolicy.Default);
        SqliteStarLibraryStore starLibraryStore = new(paths.StarLibraryDatabasePath);
        RepoFileCacheService repoFileCache = new(
            memMaxEntries: 16,
            memMaxBytes: 1024 * 1024,
            diskMaxBytes: 1024 * 1024,
            ttl: TimeSpan.FromDays(7),
            diskRoot: Path.Combine(localCache, "RepoFileCache"));
        LocalDiagnosticsStore diagnosticsStore = new(paths.DiagnosticsPath, 1024 * 1024, TimeSpan.FromDays(14));
        MemorySettingService settings = new();
        StarLibraryRecoveryStore recoveryStore = new(paths.StarLibraryRecoveryPath);
        CacheRegistry cacheRegistry = new(paths, cacheStore, imageStore, repoFileCache, starLibraryStore, recoveryStore);
        SettingsDiagnosticsService service = new(
            paths,
            diagnosticsStore,
            new FakeStoreTelemetrySink(storeAvailable),
            settings,
            cacheRegistry);

        return new TestHarness(paths, cacheStore, imageStore, repoFileCache, diagnosticsStore, settings, service);
    }

    private static GitHubQuery<Phase0TestPayload> CreateQuery(string key, TimeSpan ttl) =>
        new(
            GitHubAuthenticationConstants.PublicAccessToken,
            "u1",
            HttpMethod.Get,
            $"test/{key}",
            GitHubQueryKeys.Create("u1", HttpMethod.Get, $"test/{key}"),
            GitHubCachePolicy.MutableResource,
            ttl,
            Phase0TestJsonContext.Default.Phase0TestPayload,
            ["settings"],
            GitHubRequestPriority.Visible);

    private static GitHubRestResponse<Phase0TestPayload> CreateResponse(string name, string body) =>
        new(
            HttpStatusCode.OK,
            new Phase0TestPayload { Name = name, Body = body },
            IsNotModified: false,
            ETag: "\"etag-settings\"",
            LastModified: DateTimeOffset.UtcNow,
            Link: null,
            RateLimitRemaining: 100,
            RateLimitReset: null,
            RetryAfter: null,
            FetchedAt: DateTimeOffset.UtcNow);

    private static LocalDiagnosticEvent CreateDiagnostic(string name) =>
        new(
            DateTimeOffset.UtcNow,
            "event",
            name,
            new Dictionary<string, string> { ["feature"] = "settings" });

    private static RepoFileCacheEntry CreateRepoFileEntry(string sha) => new()
    {
        Sha = sha,
        ByteLength = 4,
        IsBinary = false,
        Bytes = [1, 2, 3, 4],
        Text = "test",
        Encoding = "utf-8",
        CachedAt = DateTimeOffset.UtcNow
    };

    private sealed record TestHarness(
        AppStoragePathProvider Paths,
        SqliteGitHubCacheStore CacheStore,
        GitHubImageCacheStore ImageCacheStore,
        RepoFileCacheService RepoFileCache,
        LocalDiagnosticsStore DiagnosticsStore,
        MemorySettingService Settings,
        SettingsDiagnosticsService Service);

    private sealed class FakeStoreTelemetrySink : IStoreTelemetrySink
    {
        public FakeStoreTelemetrySink(bool isAvailable)
        {
            IsAvailable = isAvailable;
            AvailabilityStatus = isAvailable ? "available" : "store_engagement_type_unavailable";
        }

        public bool IsAvailable { get; }

        public string AvailabilityStatus { get; }

        public void TrackEvent(string name)
        {
        }
    }

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

}
