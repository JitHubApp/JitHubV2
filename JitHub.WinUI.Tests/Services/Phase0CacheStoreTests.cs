using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class Phase0CacheStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubPhase0CacheTests", Guid.NewGuid().ToString());

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
    public async Task PutThenGet_ReturnsFreshEntry()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("u1", "test/items?b=2&a=1", TimeSpan.FromMinutes(5));
        Phase0TestPayload payload = new() { Name = "one", Body = "body" };

        await store.PutAsync(query, CreateResponse(payload));

        CachedResult<Phase0TestPayload>? result = await store.TryGetAsync(query);
        Assert.NotNull(result);
        Assert.Equal(CacheState.Fresh, result!.CacheState);
        Assert.Equal("one", result.Value!.Name);
        Assert.Equal("\"etag-1\"", result.ETag);
    }

    [Fact]
    public async Task Initialize_CreatesSchemaVersion()
    {
        SqliteGitHubCacheStore store = CreateStore();

        int schemaVersion = await store.GetSchemaVersionAsync();

        Assert.Equal(2, schemaVersion);
    }

    [Fact]
    public async Task ExpiredTtl_ReturnsStaleEntry()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("u1", "test/stale", TimeSpan.FromMilliseconds(-1));

        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "stale" }));

        CachedResult<Phase0TestPayload>? result = await store.TryGetAsync(query);
        Assert.NotNull(result);
        Assert.Equal(CacheState.Stale, result!.CacheState);
    }

    [Fact]
    public async Task DifferentUserPartition_DoesNotShareEntry()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> userOne = CreateQuery("u1", "test/shared", TimeSpan.FromMinutes(5));
        GitHubQuery<Phase0TestPayload> userTwo = CreateQuery("u2", "test/shared", TimeSpan.FromMinutes(5));

        await store.PutAsync(userOne, CreateResponse(new Phase0TestPayload { Name = "private" }));

        Assert.Null(await store.TryGetAsync(userTwo));
    }

    [Fact]
    public async Task SameStorageKeyForTwoUsers_PersistsBothAccountPartitions()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> userOne = CreateQuery("u1", "test/shared-key", TimeSpan.FromMinutes(5)) with
        {
            CacheKey = "deliberately-identical"
        };
        GitHubQuery<Phase0TestPayload> userTwo = CreateQuery("u2", "test/shared-key", TimeSpan.FromMinutes(5)) with
        {
            CacheKey = "deliberately-identical"
        };

        await store.PutAsync(userOne, CreateResponse(new Phase0TestPayload { Name = "one" }));
        await store.PutAsync(userTwo, CreateResponse(new Phase0TestPayload { Name = "two" }));

        Assert.Equal("one", (await store.TryGetAsync(userOne))?.Value?.Name);
        Assert.Equal("two", (await store.TryGetAsync(userTwo))?.Value?.Name);
        await using SqliteConnection connection = new($"Data Source={Path.Combine(_root, "cache.db")}");
        await connection.OpenAsync();
        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM cache_entries WHERE cache_key = 'deliberately-identical';";
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task VersionOneSchema_MigratesToPartitionedVersionTwoWithoutLosingData()
    {
        Directory.CreateDirectory(_root);
        string databasePath = Path.Combine(_root, "cache.db");
        string payloadRoot = Path.Combine(_root, "payloads");
        Directory.CreateDirectory(payloadRoot);
        string json = JsonSerializer.Serialize(
            new Phase0TestPayload { Name = "legacy", Body = "body" },
            Phase0TestJsonContext.Default.Phase0TestPayload);
        await using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE cache_entries (
                    cache_key TEXT PRIMARY KEY, user_id TEXT NOT NULL, method TEXT NOT NULL,
                    path TEXT NOT NULL, resource_kind TEXT NOT NULL, payload_json TEXT NULL,
                    payload_file TEXT NULL, etag TEXT NULL, last_modified_utc TEXT NULL,
                    fetched_at_utc TEXT NOT NULL, stale_after_utc TEXT NOT NULL,
                    byte_length INTEGER NOT NULL, last_accessed_utc TEXT NOT NULL);
                CREATE TABLE cache_tags (cache_key TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY(cache_key, tag));
                CREATE TABLE cache_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO cache_entries VALUES(
                    'legacy-key', 'u1', 'GET', 'legacy/path', 'mutable', $json, NULL, NULL, NULL,
                    '2026-07-28T00:00:00.0000000Z', '2099-07-28T00:00:00.0000000Z', $length,
                    '2026-07-28T00:00:00.0000000Z');
                INSERT INTO cache_tags VALUES('legacy-key', 'legacy-tag');
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$length", System.Text.Encoding.UTF8.GetByteCount(json));
            await command.ExecuteNonQueryAsync();
        }

        SqliteGitHubCacheStore store = new(databasePath, payloadRoot, GitHubCachePolicy.Default);
        GitHubQuery<Phase0TestPayload> query = CreateQuery("u1", "legacy/path", TimeSpan.FromMinutes(5)) with
        {
            CacheKey = "legacy-key"
        };

        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal("legacy", (await store.TryGetAsync(query))?.Value?.Name);
        Assert.Equal(CacheOwnerHealth.Healthy, (await store.InspectAsync()).Health);
    }

    [Fact]
    public async Task Inspection_MissingDatabaseDoesNotCreateOrMigrateIt()
    {
        SqliteGitHubCacheStore store = CreateStore();
        string databasePath = Path.Combine(_root, "cache.db");

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Healthy, inspection.Health);
        Assert.Equal(0, inspection.Components[CacheMetricKeys.DatabaseExists]);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task Inspection_MissingCurrentSchemaObjectDoesNotRepairIt()
    {
        Directory.CreateDirectory(_root);
        string databasePath = Path.Combine(_root, "cache.db");
        await ExecuteSqlAsync(databasePath, "CREATE TABLE cache_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); PRAGMA user_version = 2;");
        SqliteGitHubCacheStore store = new(databasePath, Path.Combine(_root, "payloads"), GitHubCachePolicy.Default);

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Contains("cache_entries", inspection.Detail, StringComparison.Ordinal);
        Assert.Equal(0L, await ExecuteScalarAsync(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'cache_entries';"));
    }

    [Fact]
    public async Task Inspection_FutureSchemaDoesNotDowngradeAndInitializationRejectsIt()
    {
        Directory.CreateDirectory(_root);
        string databasePath = Path.Combine(_root, "cache.db");
        await ExecuteSqlAsync(databasePath, "PRAGMA user_version = 99;");
        SqliteGitHubCacheStore store = new(databasePath, Path.Combine(_root, "payloads"), GitHubCachePolicy.Default);

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Contains("99", inspection.Detail, StringComparison.Ordinal);
        Assert.Equal(99L, await ExecuteScalarAsync(databasePath, "PRAGMA user_version;"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task ClearAll_LockedPayloadReportsPartialFailureAndPassesAfterRelease()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("u1", "test/locked-clear", TimeSpan.FromMinutes(5));
        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "large", Body = new string('x', 140_000) }));
        string payload = Assert.Single(Directory.GetFiles(Path.Combine(_root, "payloads"), "*.json"));

        await using (FileStream locked = new(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            CacheClearPostconditionException exception = await Assert.ThrowsAsync<CacheClearPostconditionException>(
                () => store.ClearAllAsync());
            Assert.Contains(exception.Residuals, residual =>
                string.Equals(residual.Identity, payload, StringComparison.OrdinalIgnoreCase));
            Assert.Null(await store.TryGetAsync(query));
        }

        await store.ClearAllAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "payloads")));
    }

    [Fact]
    public async Task InvalidateTags_RemovesMatchingEntries()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("u1", "test/tagged", TimeSpan.FromMinutes(5), ["repo"]);

        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "tagged" }));
        await store.InvalidateTagsAsync(["repo"]);

        Assert.Null(await store.TryGetAsync(query));
    }

    [Fact]
    public async Task PayloadCap_EvictsOldestPayloadFile()
    {
        SqliteGitHubCacheStore store = CreateStore(payloadSoftCapBytes: 180_000);
        GitHubQuery<Phase0TestPayload> first = CreateQuery("u1", "test/large-first", TimeSpan.FromMinutes(5));
        GitHubQuery<Phase0TestPayload> second = CreateQuery("u1", "test/large-second", TimeSpan.FromMinutes(5));
        string largeBody = new('x', 140_000);

        await store.PutAsync(first, CreateResponse(new Phase0TestPayload { Name = "first", Body = largeBody }));
        await store.PutAsync(second, CreateResponse(new Phase0TestPayload { Name = "second", Body = largeBody }));

        Assert.Null(await store.TryGetAsync(first));
        Assert.NotNull(await store.TryGetAsync(second));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "payloads"), "*.json"));
    }

    [Fact]
    public async Task MetadataCap_EvictsOldestInlineEntry()
    {
        SqliteGitHubCacheStore store = CreateStore(metadataSoftCapBytes: 6_000);
        GitHubQuery<Phase0TestPayload> first = CreateQuery("u1", "test/metadata-first", TimeSpan.FromMinutes(5));
        GitHubQuery<Phase0TestPayload> second = CreateQuery("u1", "test/metadata-second", TimeSpan.FromMinutes(5));
        string body = new('m', 4_000);

        await store.PutAsync(first, CreateResponse(new Phase0TestPayload { Name = "first", Body = body }));
        await Task.Delay(5);
        await store.PutAsync(second, CreateResponse(new Phase0TestPayload { Name = "second", Body = body }));

        Assert.Null(await store.TryGetAsync(first));
        Assert.NotNull(await store.TryGetAsync(second));
        Assert.True(await store.GetTotalMetadataBytesAsync() <= 6_000);
    }

    [Fact]
    public async Task OverwriteLargePayloadWithInlinePayload_RemovesOldPayloadFile()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("u1", "test/overwrite", TimeSpan.FromMinutes(5));

        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "large", Body = new string('x', 140_000) }));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "payloads"), "*.json"));

        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "small", Body = "small" }));

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "payloads"), "*.json"));
        CachedResult<Phase0TestPayload>? result = await store.TryGetAsync(query);
        Assert.Equal("small", result?.Value?.Body);
    }

    [Fact]
    public async Task LargePayload_UpdateFailureKeepsCommittedGenerationAndRemovesNewGeneration()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery("u1", "test/atomic", TimeSpan.FromMinutes(5));
        string originalBody = new('a', 140_000);
        await store.PutAsync(query, CreateResponse(new Phase0TestPayload { Name = "original", Body = originalBody }));
        string originalFile = Assert.Single(Directory.GetFiles(Path.Combine(_root, "payloads"), "*.json"));

        await using (SqliteConnection connection = new($"Data Source={Path.Combine(_root, "cache.db")}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_payload_generation
                BEFORE UPDATE OF payload_file ON cache_entries
                WHEN NEW.payload_file <> OLD.payload_file
                BEGIN
                    SELECT RAISE(ABORT, 'simulated commit failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => store.PutAsync(
            query,
            CreateResponse(new Phase0TestPayload { Name = "replacement", Body = new string('b', 140_000) })));

        Assert.Equal(originalFile, Assert.Single(Directory.GetFiles(Path.Combine(_root, "payloads"), "*.json")));
        CachedResult<Phase0TestPayload>? visible = await store.TryGetAsync(query);
        Assert.Equal("original", visible?.Value?.Name);
        Assert.Equal(originalBody, visible?.Value?.Body);
    }

    [Fact]
    public async Task Inspection_DetectsAndMaintenanceRemovesOrphanPayloadGeneration()
    {
        SqliteGitHubCacheStore store = CreateStore();
        await store.PutAsync(
            CreateQuery("u1", "test/orphan", TimeSpan.FromMinutes(5)),
            CreateResponse(new Phase0TestPayload { Name = "active", Body = new string('x', 140_000) }));
        string orphanPath = Path.Combine(_root, "payloads", $"orphan.{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(orphanPath, "orphan");

        CacheStoreInspection degraded = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Degraded, degraded.Health);
        Assert.True(degraded.OrphanBytes > 0);
        Assert.True(degraded.PhysicalBytes >= degraded.OrphanBytes);

        await store.EnforceCapsAsync();
        Assert.False(File.Exists(orphanPath));
        Assert.Equal(CacheOwnerHealth.Healthy, (await store.InspectAsync()).Health);
    }

    [Fact]
    public async Task Inspection_ReportsMissingReferencedPayloadAsUnhealthy()
    {
        SqliteGitHubCacheStore store = CreateStore();
        await store.PutAsync(
            CreateQuery("u1", "test/missing", TimeSpan.FromMinutes(5)),
            CreateResponse(new Phase0TestPayload { Name = "missing", Body = new string('x', 140_000) }));
        File.Delete(Assert.Single(Directory.GetFiles(Path.Combine(_root, "payloads"), "*.json")));

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Contains("missing", inspection.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task UnsafePayloadIdentity_IsQuarantinedWithoutReadingOrDeletingOutsideRoot()
    {
        SqliteGitHubCacheStore store = CreateStore();
        GitHubQuery<Phase0TestPayload> query = CreateQuery(
            "u1",
            "test/unsafe-payload",
            TimeSpan.FromMinutes(5));
        await store.PutAsync(
            query,
            CreateResponse(new Phase0TestPayload { Name = "large", Body = new string('x', 140_000) }));
        string outsidePath = Path.Combine(_root, "outside.json");
        await File.WriteAllTextAsync(outsidePath, "outside-sentinel");
        string databasePath = Path.Combine(_root, "cache.db");
        await using (SqliteConnection connection = new($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "UPDATE cache_entries SET payload_file = '..\\outside.json' WHERE user_id = $user_id AND cache_key = $cache_key;";
            command.Parameters.AddWithValue("$user_id", query.UserId);
            command.Parameters.AddWithValue("$cache_key", query.CacheKey);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.TryGetAsync(query));
        Assert.Equal("outside-sentinel", await File.ReadAllTextAsync(outsidePath));
        Assert.Equal(
            0L,
            await ExecuteScalarAsync(
                databasePath,
                $"SELECT COUNT(*) FROM cache_entries WHERE cache_key = '{query.CacheKey.Replace("'", "''", StringComparison.Ordinal)}';"));

        await store.InvalidateAsync(query.CacheKey);
        Assert.Equal("outside-sentinel", await File.ReadAllTextAsync(outsidePath));
    }

    [Fact]
    public async Task Inspection_SeparatesLogicalMetadataFromInlinePayloadAndReportsPhysicalOverhead()
    {
        SqliteGitHubCacheStore store = CreateStore();
        await store.PutAsync(
            CreateQuery("u1", "test/accounting", TimeSpan.FromMinutes(5)),
            CreateResponse(new Phase0TestPayload { Name = "inline", Body = new string('x', 100_000) }));

        CacheStoreInspection inspection = await store.InspectAsync();

        long metadata = inspection.Components[CacheMetricKeys.LogicalMetadataBytes];
        long payload = inspection.Components[CacheMetricKeys.LogicalPayloadBytes];
        long database = inspection.Components[CacheMetricKeys.DatabasePhysicalBytes];
        Assert.InRange(metadata, 1, 10_000);
        Assert.True(payload > 100_000);
        Assert.True(database > 0);
        Assert.Equal(database, inspection.PhysicalBytes);
        Assert.Equal(metadata + payload, inspection.LogicalBytes);
    }

    [Fact]
    public async Task ImageCacheCap_EvictsOldestImage()
    {
        string imageRoot = Path.Combine(_root, "images");
        GitHubImageCacheStore imageStore = new(
            imageRoot,
            new GitHubCachePolicy(avatarImageSoftCapBytes: 6));

        GitHubImageCacheEntry first = await imageStore.PutAsync("avatar-one", [1, 2, 3, 4], ".png");
        await Task.Delay(5);
        GitHubImageCacheEntry second = await imageStore.PutAsync("avatar-two", [5, 6, 7, 8], ".png");

        Assert.False(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
        Assert.Null(await imageStore.TryGetAsync("avatar-one"));
        Assert.NotNull(await imageStore.TryGetAsync("avatar-two"));
        Assert.True(await imageStore.GetTotalBytesAsync() <= 6 || Directory.GetFiles(imageRoot).Length == 1);
    }

    private SqliteGitHubCacheStore CreateStore(
        long metadataSoftCapBytes = 128L * 1024L * 1024L,
        long payloadSoftCapBytes = 2L * 1024L * 1024L * 1024L)
    {
        Directory.CreateDirectory(_root);
        string payloads = Path.Combine(_root, "payloads");
        return new SqliteGitHubCacheStore(
            Path.Combine(_root, "cache.db"),
            payloads,
            new GitHubCachePolicy(
                metadataSoftCapBytes: metadataSoftCapBytes,
                payloadSoftCapBytes: payloadSoftCapBytes));
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(string databasePath, string sql)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static GitHubQuery<Phase0TestPayload> CreateQuery(
        string userId,
        string path,
        TimeSpan ttl,
        string[]? tags = null) =>
        new(
            GitHubAuthenticationConstants.PublicAccessToken,
            userId,
            HttpMethod.Get,
            path,
            GitHubQueryKeys.Create(
                userId,
                HttpMethod.Get,
                path,
                acceptMediaType: null,
                Phase0TestJsonContext.Default.Phase0TestPayload.Type),
            GitHubCachePolicy.MutableResource,
            ttl,
            Phase0TestJsonContext.Default.Phase0TestPayload,
            tags,
            GitHubRequestPriority.Visible);

    private static GitHubRestResponse<Phase0TestPayload> CreateResponse(Phase0TestPayload payload) =>
        new(
            HttpStatusCode.OK,
            payload,
            IsNotModified: false,
            ETag: "\"etag-1\"",
            LastModified: DateTimeOffset.UtcNow,
            Link: null,
            RateLimitRemaining: 100,
            RateLimitReset: null,
            RetryAfter: null,
            FetchedAt: DateTimeOffset.UtcNow);
}
