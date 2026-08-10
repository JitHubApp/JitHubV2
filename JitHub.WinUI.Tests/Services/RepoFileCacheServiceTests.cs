using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public class RepoFileCacheServiceTests : IDisposable
{
    [Fact]
    public void ProductionCache_UsesThirtyDayImmutableBlobPolicy()
    {
        Assert.Equal(TimeSpan.FromDays(30), RepoFileCacheService.ImmutableBlobTtl);
    }

    private readonly string _diskRoot;

    public RepoFileCacheServiceTests()
    {
        _diskRoot = Path.Combine(Path.GetTempPath(), "JitHubTests", Guid.NewGuid().ToString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_diskRoot))
                Directory.Delete(_diskRoot, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private RepoFileCacheService CreateCache(
        int memMaxEntries = 256,
        long memMaxBytes = 64L * 1024 * 1024,
        long diskMaxBytes = 256L * 1024 * 1024,
        TimeSpan ttl = default)
    {
        if (ttl == default) ttl = TimeSpan.FromDays(7);
        return new RepoFileCacheService(memMaxEntries, memMaxBytes, diskMaxBytes, ttl, _diskRoot);
    }

    private static RepoFileCacheEntry MakeEntry(string sha, byte[] data, bool isBinary = false)
        => new RepoFileCacheEntry
        {
            Sha = sha,
            ByteLength = data.Length,
            IsBinary = isBinary,
            Bytes = data,
            Text = isBinary ? null : Encoding.UTF8.GetString(data),
            Encoding = "utf-8",
            CachedAt = DateTimeOffset.UtcNow,
        };

    private static RepoFileCacheKey Key(string sha, string owner = "owner", string repo = "repo")
        => new RepoFileCacheKey(owner, repo, sha);

    // ── TryGet ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_EmptyCache_ReturnsFalse()
    {
        var cache = CreateCache();
        var found = cache.TryGet(Key("abc123"), out var entry);
        Assert.False(found);
        Assert.Null(entry);
    }

    // ── PutAsync + TryGet ─────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_ThenTryGet_ReturnsEntry()
    {
        var cache = CreateCache();
        var data = Encoding.UTF8.GetBytes("hello");
        var key = Key("sha1");
        await cache.PutAsync(key, MakeEntry("sha1", data), CancellationToken.None);

        var found = cache.TryGet(key, out var entry);
        Assert.True(found);
        Assert.Equal("sha1", entry.Sha);
    }

    // ── PutAsync + GetAsync (memory fast path) ────────────────────────────────

    [Fact]
    public async Task PutAsync_ThenGetAsync_ReturnsFromMemory()
    {
        var cache = CreateCache();
        var data = Encoding.UTF8.GetBytes("hello world");
        var key = Key("sha2");
        await cache.PutAsync(key, MakeEntry("sha2", data), CancellationToken.None);

        var result = await cache.GetAsync(key, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("sha2", result!.Sha);
        Assert.Equal("hello world", result.Text);
    }

    // ── Disk load after memory eviction ──────────────────────────────────────

    [Fact]
    public async Task GetAsync_AfterMemoryEviction_LoadsFromDisk()
    {
        // Memory cap of 1 entry, so putting a second evicts the first
        var cache = CreateCache(memMaxEntries: 1, memMaxBytes: 64L * 1024 * 1024);
        var data = Encoding.UTF8.GetBytes("original data");
        var key1 = Key("sha-a");
        var key2 = Key("sha-b");

        await cache.PutAsync(key1, MakeEntry("sha-a", data), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("sha-b", Encoding.UTF8.GetBytes("second")), CancellationToken.None);

        // key1 was evicted from memory but should still be on disk
        var found = cache.TryGet(key1, out _);
        Assert.False(found);

        var result = await cache.GetAsync(key1, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("sha-a", result!.Sha);
        Assert.Equal("original data", result.Text);
    }

    // ── TTL expiry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_TtlExpired_ReturnsNull()
    {
        // Use a cache with 1 entry memory cap so it goes to disk, and 1 second TTL
        var cache = CreateCache(memMaxEntries: 1, ttl: TimeSpan.FromMilliseconds(1));

        var data = Encoding.UTF8.GetBytes("data");
        var key1 = Key("sha-ttl-a");
        var key2 = Key("sha-ttl-b");

        var entry1 = new RepoFileCacheEntry
        {
            Sha = "sha-ttl-a",
            ByteLength = data.Length,
            IsBinary = false,
            Bytes = data,
            Text = "data",
            Encoding = "utf-8",
            CachedAt = DateTimeOffset.UtcNow.AddDays(-8), // expired
        };

        await cache.PutAsync(key1, entry1, CancellationToken.None);
        // Put key2 to evict key1 from memory
        await cache.PutAsync(key2, MakeEntry("sha-ttl-b", Encoding.UTF8.GetBytes("b")), CancellationToken.None);

        var result = await cache.GetAsync(key1, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGet_TtlExpired_RemovesHotMemoryEntry()
    {
        RepoFileCacheService cache = CreateCache(ttl: TimeSpan.FromDays(1));
        RepoFileCacheKey key = Key("sha-hot-expired");
        byte[] data = Encoding.UTF8.GetBytes("expired");
        RepoFileCacheEntry entry = new()
        {
            Sha = key.Sha,
            ByteLength = data.Length,
            IsBinary = false,
            Bytes = data,
            Text = "expired",
            Encoding = "utf-8",
            CachedAt = DateTimeOffset.UtcNow.AddDays(-2),
        };

        await cache.PutAsync(key, entry, CancellationToken.None);

        Assert.False(cache.TryGet(key, out RepoFileCacheEntry? cached));
        Assert.Null(cached);
        Assert.Null(await cache.GetAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task PutAsync_DefaultTimestampIsNormalizedForMemoryAndDisk()
    {
        RepoFileCacheService cache = CreateCache(memMaxEntries: 1);
        RepoFileCacheKey key = Key("sha-default-time");
        byte[] data = Encoding.UTF8.GetBytes("current");
        RepoFileCacheEntry entry = new()
        {
            Sha = key.Sha,
            ByteLength = data.Length,
            IsBinary = false,
            Bytes = data,
            Text = "current",
            Encoding = "utf-8",
        };

        await cache.PutAsync(key, entry, CancellationToken.None);

        Assert.True(cache.TryGet(key, out RepoFileCacheEntry? hot));
        Assert.NotEqual(default, hot.CachedAt);

        await cache.PutAsync(Key("sha-evict-default-time"), MakeEntry("sha-evict-default-time", [1]), CancellationToken.None);
        RepoFileCacheEntry? disk = await cache.GetAsync(key, CancellationToken.None);
        Assert.NotNull(disk);
        Assert.NotEqual(default, disk!.CachedAt);
    }

    [Fact]
    public async Task GetAsync_TtlNotExpired_ReturnsEntry()
    {
        var cache = CreateCache(memMaxEntries: 1, ttl: TimeSpan.FromDays(30));
        var data = Encoding.UTF8.GetBytes("fresh data");
        var key1 = Key("sha-fresh-a");
        var key2 = Key("sha-fresh-b");

        await cache.PutAsync(key1, MakeEntry("sha-fresh-a", data), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("sha-fresh-b", Encoding.UTF8.GetBytes("b")), CancellationToken.None);

        var result = await cache.GetAsync(key1, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("sha-fresh-a", result!.Sha);
    }

    // ── LRU eviction by count ─────────────────────────────────────────────────

    [Fact]
    public async Task MemoryLru_EvictionByCount_OldestEvictedFromMemory()
    {
        var cache = CreateCache(memMaxEntries: 2, memMaxBytes: 64L * 1024 * 1024);
        var key1 = Key("lru-a");
        var key2 = Key("lru-b");
        var key3 = Key("lru-c");

        await cache.PutAsync(key1, MakeEntry("lru-a", Encoding.UTF8.GetBytes("a")), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("lru-b", Encoding.UTF8.GetBytes("b")), CancellationToken.None);
        await cache.PutAsync(key3, MakeEntry("lru-c", Encoding.UTF8.GetBytes("c")), CancellationToken.None);

        // key1 is oldest and should be evicted from memory
        Assert.False(cache.TryGet(key1, out _));
        // key2 and key3 should still be in memory
        Assert.True(cache.TryGet(key2, out _));
        Assert.True(cache.TryGet(key3, out _));
    }

    [Fact]
    public async Task MemoryLru_EvictedByCount_StillOnDisk()
    {
        var cache = CreateCache(memMaxEntries: 2, memMaxBytes: 64L * 1024 * 1024);
        var key1 = Key("disk-a");
        var key2 = Key("disk-b");
        var key3 = Key("disk-c");

        await cache.PutAsync(key1, MakeEntry("disk-a", Encoding.UTF8.GetBytes("a")), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("disk-b", Encoding.UTF8.GetBytes("b")), CancellationToken.None);
        await cache.PutAsync(key3, MakeEntry("disk-c", Encoding.UTF8.GetBytes("c")), CancellationToken.None);

        // key1 evicted from memory but still on disk
        var result = await cache.GetAsync(key1, CancellationToken.None);
        Assert.NotNull(result);
    }

    // ── LRU eviction by bytes ─────────────────────────────────────────────────

    [Fact]
    public async Task MemoryLru_EvictionByBytes_SmallEntryEvicted()
    {
        // Allow 2 entries but only 20 bytes — small entry gets evicted when larger one arrives
        var cache = CreateCache(memMaxEntries: 256, memMaxBytes: 20);

        var small = Encoding.UTF8.GetBytes("hi");     // 2 bytes
        var large = Encoding.UTF8.GetBytes("0123456789012345678"); // 19 bytes

        var key1 = Key("bytes-a");
        var key2 = Key("bytes-b");

        await cache.PutAsync(key1, MakeEntry("bytes-a", small), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("bytes-b", large), CancellationToken.None);

        // key1 should be evicted because 2+19=21 > 20 bytes cap
        Assert.False(cache.TryGet(key1, out _));
        Assert.True(cache.TryGet(key2, out _));
    }

    // ── LRU promotes MRU ─────────────────────────────────────────────────────

    [Fact]
    public async Task MemoryLru_AccessPromotesEntry_EvictsOtherInstead()
    {
        // cap=2, put A, put B, get A (promotes A to MRU), put C (evicts B not A)
        var cache = CreateCache(memMaxEntries: 2, memMaxBytes: 64L * 1024 * 1024);

        var keyA = Key("mru-a");
        var keyB = Key("mru-b");
        var keyC = Key("mru-c");

        await cache.PutAsync(keyA, MakeEntry("mru-a", Encoding.UTF8.GetBytes("a")), CancellationToken.None);
        await cache.PutAsync(keyB, MakeEntry("mru-b", Encoding.UTF8.GetBytes("b")), CancellationToken.None);

        // Promote A to MRU
        cache.TryGet(keyA, out _);

        // Now put C — B should be evicted (LRU), A should stay
        await cache.PutAsync(keyC, MakeEntry("mru-c", Encoding.UTF8.GetBytes("c")), CancellationToken.None);

        Assert.True(cache.TryGet(keyA, out _));
        Assert.False(cache.TryGet(keyB, out _));
        Assert.True(cache.TryGet(keyC, out _));
    }

    // ── Disk capacity ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DiskCap_Enforced_OldestEvictedFromDisk()
    {
        // Small disk cap (10 bytes). Two entries of 6 bytes each = 12 > 10.
        // Oldest (A) should be evicted after enforcement.
        var cache = CreateCache(memMaxEntries: 1, memMaxBytes: 64L * 1024 * 1024, diskMaxBytes: 10);

        var dataA = Encoding.UTF8.GetBytes("aaaaaa"); // 6 bytes, older
        var dataB = Encoding.UTF8.GetBytes("bbbbbb"); // 6 bytes, newer

        var keyA = new RepoFileCacheKey("owner", "repo", "cap-a");
        var keyB = new RepoFileCacheKey("owner", "repo", "cap-b");

        var entryA = new RepoFileCacheEntry
        {
            Sha = "cap-a",
            ByteLength = dataA.Length,
            IsBinary = false,
            Bytes = dataA,
            Text = "aaaaaa",
            Encoding = "utf-8",
            CachedAt = DateTimeOffset.UtcNow.AddMinutes(-10), // older
        };
        var entryB = new RepoFileCacheEntry
        {
            Sha = "cap-b",
            ByteLength = dataB.Length,
            IsBinary = false,
            Bytes = dataB,
            Text = "bbbbbb",
            Encoding = "utf-8",
            CachedAt = DateTimeOffset.UtcNow, // newer
        };

        await cache.PutAsync(keyA, entryA, CancellationToken.None);
        // keyB evicts keyA from memory (memMaxEntries=1)
        await cache.PutAsync(keyB, entryB, CancellationToken.None);

        // Explicitly enforce disk cap
        await cache.PurgeAsync(CancellationToken.None);

        // keyA should be evicted from disk (it's older and 6+6=12 > 10 bytes cap)
        // keyA is not in memory, so GetAsync goes to disk → should be null
        var result = await cache.GetAsync(keyA, CancellationToken.None);
        Assert.Null(result);
    }

    // ── PurgeAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeAsync_RemovesExpiredEntries()
    {
        var cache = CreateCache(memMaxEntries: 1, ttl: TimeSpan.FromDays(7));

        var data = Encoding.UTF8.GetBytes("old data");
        var keyExpired = Key("purge-expired");
        var keyOther = Key("purge-other");

        var expiredEntry = new RepoFileCacheEntry
        {
            Sha = "purge-expired",
            ByteLength = data.Length,
            IsBinary = false,
            Bytes = data,
            Text = "old data",
            Encoding = "utf-8",
            CachedAt = DateTimeOffset.UtcNow.AddDays(-8), // expired (8 > 7 days TTL)
        };
        await cache.PutAsync(keyExpired, expiredEntry, CancellationToken.None);
        // Evict expired entry from memory by putting another entry
        await cache.PutAsync(keyOther, MakeEntry("purge-other", Encoding.UTF8.GetBytes("x")), CancellationToken.None);

        // Purge should remove the expired disk entry
        await cache.PurgeAsync(CancellationToken.None);

        // After purge, GetAsync should not find expired entry (not in memory, deleted from disk)
        var result = await cache.GetAsync(keyExpired, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task PurgeAsync_KeepsValidEntries()
    {
        var cache = CreateCache(memMaxEntries: 1, ttl: TimeSpan.FromDays(7));

        var data = Encoding.UTF8.GetBytes("valid data");
        var key1 = Key("purge-valid");
        var key2 = Key("purge-evict-mem");

        await cache.PutAsync(key1, MakeEntry("purge-valid", data), CancellationToken.None);
        // Evict key1 from memory
        await cache.PutAsync(key2, MakeEntry("purge-evict-mem", Encoding.UTF8.GetBytes("x")), CancellationToken.None);

        await cache.PurgeAsync(CancellationToken.None);

        // key1 should still be retrievable from disk
        var result = await cache.GetAsync(key1, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("purge-valid", result!.Sha);
    }

    // ── Same SHA updates ──────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_SameSha_UpdatesCorrectly()
    {
        var cache = CreateCache();
        var key = Key("update-sha");

        await cache.PutAsync(key, MakeEntry("update-sha", Encoding.UTF8.GetBytes("v1")), CancellationToken.None);
        await cache.PutAsync(key, MakeEntry("update-sha", Encoding.UTF8.GetBytes("v2")), CancellationToken.None);

        var result = await cache.GetAsync(key, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("v2", result!.Text);
    }

    // ── Different owner/repo same SHA ─────────────────────────────────────────

    [Fact]
    public async Task PutAsync_DifferentOwnerRepoSameSha_SeparateCacheEntries()
    {
        var cache = CreateCache();

        var key1 = new RepoFileCacheKey("ownerA", "repo", "same-sha");
        var key2 = new RepoFileCacheKey("ownerB", "repo", "same-sha");

        await cache.PutAsync(key1, MakeEntry("same-sha", Encoding.UTF8.GetBytes("data-A")), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("same-sha", Encoding.UTF8.GetBytes("data-B")), CancellationToken.None);

        cache.TryGet(key1, out var entry1);
        cache.TryGet(key2, out var entry2);

        Assert.Equal("data-A", entry1.Text);
        Assert.Equal("data-B", entry2.Text);
    }

    [Fact]
    public async Task PutAsync_DifferentUsersSameRepositoryAndSha_ArePartitioned()
    {
        var cache = CreateCache();
        RepoFileCacheKey firstUser = new("owner", "private-repo", "same-sha", "user-1");
        RepoFileCacheKey secondUser = new("owner", "private-repo", "same-sha", "user-2");

        await cache.PutAsync(firstUser, MakeEntry("same-sha", Encoding.UTF8.GetBytes("first")), CancellationToken.None);
        await cache.PutAsync(secondUser, MakeEntry("same-sha", Encoding.UTF8.GetBytes("second")), CancellationToken.None);

        Assert.Equal("first", (await cache.GetAsync(firstUser, CancellationToken.None))!.Text);
        Assert.Equal("second", (await cache.GetAsync(secondUser, CancellationToken.None))!.Text);
    }

    // ── Text and binary entries ───────────────────────────────────────────────

    [Fact]
    public async Task TextEntry_DecodedCorrectly()
    {
        var cache = CreateCache(memMaxEntries: 1);
        var text = "Hello, 世界!";
        var data = Encoding.UTF8.GetBytes(text);
        var key1 = Key("text-entry");
        var key2 = Key("text-evict");

        await cache.PutAsync(key1, MakeEntry("text-entry", data, isBinary: false), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("text-evict", Encoding.UTF8.GetBytes("x")), CancellationToken.None);

        var result = await cache.GetAsync(key1, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result!.IsBinary);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public async Task BinaryEntry_BytesPreserved()
    {
        var cache = CreateCache(memMaxEntries: 1);
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var key1 = Key("binary-entry");
        var key2 = Key("binary-evict");

        await cache.PutAsync(key1, MakeEntry("binary-entry", binaryData, isBinary: true), CancellationToken.None);
        await cache.PutAsync(key2, MakeEntry("binary-evict", Encoding.UTF8.GetBytes("x")), CancellationToken.None);

        var result = await cache.GetAsync(key1, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result!.IsBinary);
        Assert.Equal(binaryData, result.Bytes);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task GetAsync_CancelledWhileWaitingForMaintenance_ReleasesPerKeyLock()
    {
        RepoFileCacheService cache = CreateCache();
        RepoFileCacheKey key = Key("cancelled-get");
        SemaphoreSlim maintenanceLock = GetPrivateSemaphore(cache, "_maintenanceLock");
        await maintenanceLock.WaitAsync();
        try
        {
            using CancellationTokenSource cancellation = new();
            Task<RepoFileCacheEntry?> pending = cache.GetAsync(key, cancellation.Token);
            await WaitUntilAsync(() => GetKeyLockCount(cache) == 1);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await WaitUntilAsync(() => GetKeyLockCount(cache) == 0);
        }
        finally
        {
            maintenanceLock.Release();
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        Assert.Null(await cache.GetAsync(key, timeout.Token));
    }

    [Fact]
    public async Task PutAsync_CancelledWhileWaitingForMaintenance_ReleasesPerKeyLock()
    {
        RepoFileCacheService cache = CreateCache();
        RepoFileCacheKey key = Key("cancelled-put");
        RepoFileCacheEntry entry = MakeEntry(key.Sha, Encoding.UTF8.GetBytes("content"));
        SemaphoreSlim maintenanceLock = GetPrivateSemaphore(cache, "_maintenanceLock");
        await maintenanceLock.WaitAsync();
        try
        {
            using CancellationTokenSource cancellation = new();
            Task pending = cache.PutAsync(key, entry, cancellation.Token);
            await WaitUntilAsync(() => GetKeyLockCount(cache) == 1);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await WaitUntilAsync(() => GetKeyLockCount(cache) == 0);
        }
        finally
        {
            maintenanceLock.Release();
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        await cache.PutAsync(key, entry, timeout.Token);
        Assert.Equal("content", (await cache.GetAsync(key, timeout.Token))!.Text);
    }

    [Fact]
    public async Task CancelledUniqueKeyWaiters_DoNotAccumulateKeyLocks()
    {
        RepoFileCacheService cache = CreateCache();
        SemaphoreSlim maintenanceLock = GetPrivateSemaphore(cache, "_maintenanceLock");
        await maintenanceLock.WaitAsync();
        try
        {
            using CancellationTokenSource cancellation = new();
            Task<RepoFileCacheEntry?>[] pending = new Task<RepoFileCacheEntry?>[32];
            for (int index = 0; index < pending.Length; index++)
            {
                pending[index] = cache.GetAsync(Key($"cancelled-{index}"), cancellation.Token);
            }

            await WaitUntilAsync(() => GetKeyLockCount(cache) == pending.Length);
            cancellation.Cancel();
            await Task.WhenAll(pending.Select(async task =>
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task)));
            await WaitUntilAsync(() => GetKeyLockCount(cache) == 0);
        }
        finally
        {
            maintenanceLock.Release();
        }
    }

    [Fact]
    public async Task AccountQuiescence_RejectsLateWriterAndPartitionClearRemovesExistingBlob()
    {
        AccountWorkQuiescence accountWork = new();
        RepoFileCacheService cache = new(
            16,
            4 * 1024 * 1024,
            16 * 1024 * 1024,
            TimeSpan.FromDays(7),
            _diskRoot,
            accountWork);
        RepoFileCacheKey existingKey = new("owner", "repo", "existing", "42");
        RepoFileCacheKey lateKey = new("owner", "repo", "late", "42");
        await cache.PutAsync(
            existingKey,
            MakeEntry(existingKey.Sha, Encoding.UTF8.GetBytes("existing")),
            CancellationToken.None);

        using IAccountWorkLease activeProducer = accountWork.Enter("42");
        Task quiesce = accountWork.QuiesceAsync("42");
        await Task.Delay(20);
        Assert.False(quiesce.IsCompleted);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.PutAsync(
            lateKey,
            MakeEntry(lateKey.Sha, Encoding.UTF8.GetBytes("late")),
            CancellationToken.None));

        activeProducer.Dispose();
        await quiesce.WaitAsync(TimeSpan.FromSeconds(2));
        await cache.ClearPartitionAsync("42");

        Assert.False(cache.TryGet(existingKey, out _));
        Assert.False(cache.TryGet(lateKey, out _));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_diskRoot, "*", SearchOption.AllDirectories),
            path => path.Contains($"{Path.DirectorySeparatorChar}42{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspection_DetectsCorruptEntryMetadata()
    {
        RepoFileCacheService cache = CreateCache();
        await cache.PutAsync(
            Key("inspect-corrupt"),
            MakeEntry("inspect-corrupt", Encoding.UTF8.GetBytes("payload")),
            CancellationToken.None);
        string metadataPath = Assert.Single(
            Directory.GetFiles(_diskRoot, "*.json", SearchOption.AllDirectories),
            path => !string.Equals(Path.GetFileName(path), "index.json", StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(metadataPath, "{not-json}");

        CacheStoreInspection inspection = await cache.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Contains("corrupt", inspection.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspection_ReportsUnindexedFilesAsDegradedAndCountsPhysicalBytes()
    {
        RepoFileCacheService cache = CreateCache();
        await cache.PutAsync(
            Key("inspect-orphan"),
            MakeEntry("inspect-orphan", Encoding.UTF8.GetBytes("payload")),
            CancellationToken.None);
        string orphan = Path.Combine(_diskRoot, "orphan.tmp");
        await File.WriteAllTextAsync(orphan, "orphan");

        CacheStoreInspection inspection = await cache.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Degraded, inspection.Health);
        Assert.True(inspection.OrphanBytes >= new FileInfo(orphan).Length);
        Assert.True(inspection.PhysicalBytes >= inspection.LogicalBytes + inspection.OrphanBytes);
    }

    [Theory]
    [InlineData("..\\..\\outside", "repo", "safe-sha", "current")]
    [InlineData("owner", "..\\outside", "safe-sha", "current")]
    [InlineData("owner", "repo", "..\\..\\outside", "current")]
    [InlineData("owner", "repo", "safe-sha", "..\\outside")]
    [InlineData("CON", "repo", "safe-sha", "current")]
    public async Task PublicOperations_RejectTraversalSegmentsWithoutReadingOrWritingOutsideRoot(
        string owner,
        string repo,
        string sha,
        string userId)
    {
        RepoFileCacheService cache = CreateCache();
        string outsidePath = Path.Combine(Path.GetDirectoryName(_diskRoot)!, "outside-cache.bin");
        await File.WriteAllTextAsync(outsidePath, "do-not-touch");
        RepoFileCacheKey malicious = new(owner, repo, sha, userId);

        Assert.Throws<ArgumentException>(() => cache.TryGet(malicious, out _));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync(malicious, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.PutAsync(
            malicious,
            MakeEntry(sha, Encoding.UTF8.GetBytes("malicious")),
            CancellationToken.None));

        Assert.Equal("do-not-touch", await File.ReadAllTextAsync(outsidePath));
        Assert.Empty(Directory.EnumerateFiles(_diskRoot, "*.bin", SearchOption.AllDirectories));
        File.Delete(outsidePath);
    }

    [Fact]
    public async Task GetAsync_TraversalShaCannotReadMatchingPayloadOutsideCacheRoot()
    {
        RepoFileCacheService cache = CreateCache();
        string outsideDirectory = Path.GetDirectoryName(_diskRoot)!;
        string outsidePayload = Path.Combine(outsideDirectory, "outside-readable.bin");
        string outsideMetadata = Path.Combine(outsideDirectory, "outside-readable.json");
        byte[] secret = Encoding.UTF8.GetBytes("outside-secret");
        await File.WriteAllBytesAsync(outsidePayload, secret);
        await File.WriteAllTextAsync(
            outsideMetadata,
            $$"""
            {
              "byteLength": {{secret.Length}},
              "isBinary": false,
              "encoding": "utf-8",
              "cachedAt": "{{DateTimeOffset.UtcNow:O}}"
            }
            """);

        RepoFileCacheKey malicious = new("owner", "repo", "..\\..\\outside-readable", "current");
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync(malicious, CancellationToken.None));

        Assert.Equal(secret, await File.ReadAllBytesAsync(outsidePayload));
        Assert.Contains("outside-secret", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(outsidePayload)));
        File.Delete(outsidePayload);
        File.Delete(outsideMetadata);
    }

    [Fact]
    public async Task Purge_DropsTraversalEntriesFromCorruptIndexWithoutDeletingOutsideRoot()
    {
        RepoFileCacheService cache = CreateCache(diskMaxBytes: 1);
        string outsidePath = Path.Combine(Path.GetDirectoryName(_diskRoot)!, "outside-victim.bin");
        await File.WriteAllTextAsync(outsidePath, "still-owned-by-test");
        string indexPath = Path.Combine(_diskRoot, "index.json");
        string cachedAt = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        await File.WriteAllTextAsync(
            indexPath,
            $$"""
            {
              "entries": [
                { "userId": "current", "owner": "owner", "repo": "repo", "sha": "..\\..\\outside-victim", "byteLength": 4096, "cachedAt": "{{cachedAt}}" },
                { "userId": "..\\outside", "owner": "owner", "repo": "repo", "sha": "safe", "byteLength": 4096, "cachedAt": "{{cachedAt}}" }
              ]
            }
            """);

        await cache.PurgeAsync(CancellationToken.None);

        Assert.Equal("still-owned-by-test", await File.ReadAllTextAsync(outsidePath));
        using JsonDocument sanitizedIndex = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        Assert.Empty(sanitizedIndex.RootElement.GetProperty("entries").EnumerateArray());
        File.Delete(outsidePath);
    }

    [Fact]
    public async Task Purge_ReplacesMalformedIndexWithoutTouchingOutsideFiles()
    {
        RepoFileCacheService cache = CreateCache();
        string outsidePath = Path.Combine(Path.GetDirectoryName(_diskRoot)!, "malformed-index-victim.txt");
        await File.WriteAllTextAsync(outsidePath, "untouched");
        string indexPath = Path.Combine(_diskRoot, "index.json");
        await File.WriteAllTextAsync(indexPath, "{ definitely-not-json");

        await cache.PurgeAsync(CancellationToken.None);

        Assert.Equal("untouched", await File.ReadAllTextAsync(outsidePath));
        using JsonDocument sanitizedIndex = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        Assert.Empty(sanitizedIndex.RootElement.GetProperty("entries").EnumerateArray());
        File.Delete(outsidePath);
    }

    [Fact]
    public async Task Purge_NormalizesNullEntriesCollectionWithoutTouchingOutsideFiles()
    {
        RepoFileCacheService cache = CreateCache();
        string outsidePath = Path.Combine(Path.GetDirectoryName(_diskRoot)!, "null-index-victim.txt");
        await File.WriteAllTextAsync(outsidePath, "untouched");
        string indexPath = Path.Combine(_diskRoot, "index.json");
        await File.WriteAllTextAsync(indexPath, "{\"entries\":null}");

        await cache.PurgeAsync(CancellationToken.None);

        Assert.Equal("untouched", await File.ReadAllTextAsync(outsidePath));
        using JsonDocument sanitizedIndex = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        Assert.Empty(sanitizedIndex.RootElement.GetProperty("entries").EnumerateArray());
        File.Delete(outsidePath);
    }

    [Fact]
    public async Task DiskCap_SaturatesCorruptByteLengthsAndDropsOnlyOwnedEntries()
    {
        RepoFileCacheService cache = CreateCache(diskMaxBytes: 1);
        string outsidePath = Path.Combine(Path.GetDirectoryName(_diskRoot)!, "overflow-index-victim.txt");
        await File.WriteAllTextAsync(outsidePath, "untouched");
        string indexPath = Path.Combine(_diskRoot, "index.json");
        string cachedAt = DateTimeOffset.UtcNow.ToString("O");
        await File.WriteAllTextAsync(
            indexPath,
            $$"""
            {
              "entries": [
                { "userId": "current", "owner": "owner", "repo": "repo", "sha": "first", "byteLength": {{long.MaxValue}}, "cachedAt": "{{cachedAt}}" },
                { "userId": "current", "owner": "owner", "repo": "repo", "sha": "second", "byteLength": {{long.MaxValue}}, "cachedAt": "{{cachedAt}}" }
              ]
            }
            """);

        await cache.PurgeAsync(CancellationToken.None);

        Assert.Equal("untouched", await File.ReadAllTextAsync(outsidePath));
        using JsonDocument sanitizedIndex = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
        Assert.Empty(sanitizedIndex.RootElement.GetProperty("entries").EnumerateArray());
        File.Delete(outsidePath);
    }

    private static SemaphoreSlim GetPrivateSemaphore(RepoFileCacheService cache, string fieldName) =>
        (SemaphoreSlim)(typeof(RepoFileCacheService)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache) ?? throw new InvalidOperationException($"Missing {fieldName}."));

    private static int GetKeyLockCount(RepoFileCacheService cache)
    {
        object keyLocks = typeof(RepoFileCacheService)
            .GetField("_keyLocks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache) ?? throw new InvalidOperationException("Missing per-key locks.");
        return (int)(keyLocks.GetType().GetProperty("Count")?.GetValue(keyLocks) ?? -1);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
