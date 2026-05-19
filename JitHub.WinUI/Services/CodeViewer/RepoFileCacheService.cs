using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using Windows.Storage;

namespace JitHub.Services.CodeViewer;

/// <summary>
/// Two-tier (in-memory LRU + disk) cache for repository file blobs.
/// </summary>
public sealed class RepoFileCacheService : IRepoFileCacheService
{
    // ── Configuration ────────────────────────────────────────────────────────
    private const int DefaultMemMaxEntries = 256;
    private const long DefaultMemMaxBytes = 64L * 1024 * 1024;   // 64 MB
    private const long DefaultDiskMaxBytes = 256L * 1024 * 1024; // 256 MB
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private readonly int _memMaxEntries;
    private readonly long _memMaxBytes;
    private readonly long _diskMaxBytes;
    private readonly TimeSpan _ttl;
    private readonly string _diskRoot;

    // ── In-memory LRU ────────────────────────────────────────────────────────
    private long _memCurrentBytes;
    private readonly LinkedList<MemoryLruEntry> _lruList = new();
    private readonly Dictionary<string, LinkedListNode<MemoryLruEntry>> _memIndex = new(StringComparer.Ordinal);
    private readonly object _memLock = new();

    // ── Concurrency ──────────────────────────────────────────────────────────
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);

    public RepoFileCacheService()
        : this(DefaultMemMaxEntries, DefaultMemMaxBytes, DefaultDiskMaxBytes, DefaultTtl) { }

    public RepoFileCacheService(int memMaxEntries, long memMaxBytes, long diskMaxBytes, TimeSpan ttl)
    {
        _memMaxEntries = memMaxEntries;
        _memMaxBytes = memMaxBytes;
        _diskMaxBytes = diskMaxBytes;
        _ttl = ttl;

        _diskRoot = GetDefaultDiskRoot();
        Directory.CreateDirectory(_diskRoot);

        // Background startup purge — do not block the constructor.
        _ = Task.Run(() => PurgeAsync(CancellationToken.None));
    }

    internal RepoFileCacheService(
        int memMaxEntries,
        long memMaxBytes,
        long diskMaxBytes,
        TimeSpan ttl,
        string diskRoot)
    {
        _memMaxEntries = memMaxEntries;
        _memMaxBytes = memMaxBytes;
        _diskMaxBytes = diskMaxBytes;
        _ttl = ttl;
        _diskRoot = diskRoot;
        Directory.CreateDirectory(_diskRoot);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    private static string GetDefaultDiskRoot()
    {
        try
        {
            return Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "RepoFileCache");
        }
        catch (InvalidOperationException)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Path.GetTempPath();

            return Path.Combine(localAppData, "JitHub", "RepoFileCache");
        }
    }

    public bool TryGet(RepoFileCacheKey key, out RepoFileCacheEntry entry)
    {
        string mk = MemKey(key);
        lock (_memLock)
        {
            if (_memIndex.TryGetValue(mk, out var node))
            {
                // Promote to front (most-recently-used).
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                entry = node.Value.Entry;
                return true;
            }
        }
        entry = null!;
        return false;
    }

    public async Task<RepoFileCacheEntry?> GetAsync(RepoFileCacheKey key, CancellationToken ct)
    {
        if (TryGet(key, out var cached))
            return cached;

        var sem = GetKeyLock(key);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-checked: another task may have populated memory while we waited.
            if (TryGet(key, out var cached2))
                return cached2;

            string binPath = BinPath(key);
            string metaPath = MetaPath(key);

            if (!File.Exists(binPath) || !File.Exists(metaPath))
                return null;

            DiskEntryMeta? meta;
            {
                await using var ms = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                meta = await JsonSerializer.DeserializeAsync(ms, RepoFileCacheJsonContext.Default.DiskEntryMeta, ct).ConfigureAwait(false);
            }

            if (meta is null)
                return null;

            if (DateTimeOffset.UtcNow - meta.CachedAt > _ttl)
            {
                DeleteDiskFiles(key);
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(binPath, ct).ConfigureAwait(false);

            var entry = BuildEntry(key.Sha, meta, bytes);
            PromoteToMemory(MemKey(key), entry);
            return entry;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task PutAsync(RepoFileCacheKey key, RepoFileCacheEntry entry, CancellationToken ct)
    {
        PromoteToMemory(MemKey(key), entry);

        var sem = GetKeyLock(key);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string binPath = BinPath(key);
            string metaPath = MetaPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);

            await File.WriteAllBytesAsync(binPath, entry.Bytes, ct).ConfigureAwait(false);

            var meta = new DiskEntryMeta
            {
                ByteLength = entry.ByteLength,
                IsBinary = entry.IsBinary,
                Encoding = entry.Encoding,
                CachedAt = entry.CachedAt == default ? DateTimeOffset.UtcNow : entry.CachedAt,
            };

            await _indexLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var ms = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await JsonSerializer.SerializeAsync(ms, meta, RepoFileCacheJsonContext.Default.DiskEntryMeta, ct).ConfigureAwait(false);

                await AppendIndexEntryAsync(key, meta, ct).ConfigureAwait(false);
            }
            finally
            {
                _indexLock.Release();
            }
        }
        finally
        {
            sem.Release();
        }

        _ = Task.Run(() => EnforceDiskCapAsync(CancellationToken.None));
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        await PurgeExpiredAsync(ct).ConfigureAwait(false);
        await EnforceDiskCapAsync(ct).ConfigureAwait(false);
    }

    // ── Disk helpers ─────────────────────────────────────────────────────────

    private string SanitizedOwnerRepo(RepoFileCacheKey key)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string raw = $"{key.Owner}_{key.Repo}";
        return string.Create(raw.Length, (raw, invalid), static (span, state) =>
        {
            ReadOnlySpan<char> src = state.raw.AsSpan();
            ReadOnlySpan<char> inv = state.invalid.AsSpan();
            for (int i = 0; i < src.Length; i++)
                span[i] = inv.Contains(src[i]) ? '_' : src[i];
        });
    }

    private string BinPath(RepoFileCacheKey key)
    {
        string sha = key.Sha;
        string prefix = sha.Length >= 2 ? sha.Substring(0, 2) : sha;
        return Path.Combine(_diskRoot, SanitizedOwnerRepo(key), prefix, sha + ".bin");
    }

    private string MetaPath(RepoFileCacheKey key)
    {
        string sha = key.Sha;
        string prefix = sha.Length >= 2 ? sha.Substring(0, 2) : sha;
        return Path.Combine(_diskRoot, SanitizedOwnerRepo(key), prefix, sha + ".json");
    }

    private string IndexPath() => Path.Combine(_diskRoot, "index.json");

    private void DeleteDiskFiles(RepoFileCacheKey key)
    {
        try { File.Delete(BinPath(key)); } catch { }
        try { File.Delete(MetaPath(key)); } catch { }
    }

    // ── Index management ─────────────────────────────────────────────────────

    private async Task<DiskCacheIndex> LoadIndexAsync(CancellationToken ct)
    {
        string path = IndexPath();
        if (!File.Exists(path))
            return new DiskCacheIndex();

        try
        {
            await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return await JsonSerializer.DeserializeAsync(s, RepoFileCacheJsonContext.Default.DiskCacheIndex, ct).ConfigureAwait(false)
                   ?? new DiskCacheIndex();
        }
        catch
        {
            return new DiskCacheIndex();
        }
    }

    private async Task SaveIndexAsync(DiskCacheIndex index, CancellationToken ct)
    {
        string path = IndexPath();
        await using var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(s, index, RepoFileCacheJsonContext.Default.DiskCacheIndex, ct).ConfigureAwait(false);
    }

    private async Task AppendIndexEntryAsync(RepoFileCacheKey key, DiskEntryMeta meta, CancellationToken ct)
    {
        // Already called under _indexLock.
        var index = await LoadIndexAsync(ct).ConfigureAwait(false);

        // Remove any existing entry for this sha to avoid duplicates.
        index.Entries.RemoveAll(e => string.Equals(e.Owner, key.Owner, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.Repo, key.Repo, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.Sha, key.Sha, StringComparison.OrdinalIgnoreCase));

        index.Entries.Add(new DiskIndexEntry
        {
            Owner = key.Owner,
            Repo = key.Repo,
            Sha = key.Sha,
            ByteLength = meta.ByteLength,
            CachedAt = meta.CachedAt,
        });

        await SaveIndexAsync(index, ct).ConfigureAwait(false);
    }

    private async Task PurgeExpiredAsync(CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexAsync(ct).ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow - _ttl;

            var expired = index.Entries.FindAll(e => e.CachedAt < cutoff);
            foreach (var e in expired)
            {
                DeleteDiskFiles(new RepoFileCacheKey(e.Owner, e.Repo, e.Sha));
                index.Entries.Remove(e);
            }

            if (expired.Count > 0)
                await SaveIndexAsync(index, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task EnforceDiskCapAsync(CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexAsync(ct).ConfigureAwait(false);
            long total = 0;
            foreach (var e in index.Entries) total += e.ByteLength;

            if (total <= _diskMaxBytes)
                return;

            // Sort by oldest first, evict until under cap.
            index.Entries.Sort((a, b) => a.CachedAt.CompareTo(b.CachedAt));
            while (total > _diskMaxBytes && index.Entries.Count > 0)
            {
                var victim = index.Entries[0];
                index.Entries.RemoveAt(0);
                total -= victim.ByteLength;
                DeleteDiskFiles(new RepoFileCacheKey(victim.Owner, victim.Repo, victim.Sha));
            }

            await SaveIndexAsync(index, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        finally
        {
            _indexLock.Release();
        }
    }

    // ── In-memory LRU helpers ─────────────────────────────────────────────────

    private void PromoteToMemory(string mk, RepoFileCacheEntry entry)
    {
        lock (_memLock)
        {
            if (_memIndex.TryGetValue(mk, out var existing))
            {
                _lruList.Remove(existing);
                _memCurrentBytes -= existing.Value.Entry.ByteLength;
                _memIndex.Remove(mk);
            }

            // Evict LRU entries until both caps are satisfied.
            while ((_memIndex.Count >= _memMaxEntries || _memCurrentBytes + entry.ByteLength > _memMaxBytes)
                   && _lruList.Last is { } last)
            {
                _memCurrentBytes -= last.Value.Entry.ByteLength;
                _memIndex.Remove(last.Value.Key);
                _lruList.RemoveLast();
            }

            var node = new LinkedListNode<MemoryLruEntry>(new MemoryLruEntry(mk, entry));
            _lruList.AddFirst(node);
            _memIndex[mk] = node;
            _memCurrentBytes += entry.ByteLength;
        }
    }

    private SemaphoreSlim GetKeyLock(RepoFileCacheKey key)
        => _keyLocks.GetOrAdd(DiskKey(key), _ => new SemaphoreSlim(1, 1));

    private static string MemKey(RepoFileCacheKey key)
        => $"{key.Owner}/{key.Repo}/{key.Sha}";

    private static string DiskKey(RepoFileCacheKey key)
        => $"{key.Owner.ToLowerInvariant()}/{key.Repo.ToLowerInvariant()}/{key.Sha.ToLowerInvariant()}";

    private static RepoFileCacheEntry BuildEntry(string sha, DiskEntryMeta meta, byte[] bytes)
    {
        string? text = meta.IsBinary ? null : TryDecodeText(bytes, meta.Encoding);
        return new RepoFileCacheEntry
        {
            Sha = sha,
            ByteLength = meta.ByteLength,
            IsBinary = meta.IsBinary,
            Bytes = bytes,
            Text = text,
            Encoding = meta.Encoding,
            CachedAt = meta.CachedAt,
        };
    }

    private static string? TryDecodeText(byte[] bytes, string? encoding)
    {
        try
        {
            var enc = string.Equals(encoding, "utf-8", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8
                : Encoding.UTF8;
            return enc.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    // ── Nested types ──────────────────────────────────────────────────────────

    private sealed record MemoryLruEntry(string Key, RepoFileCacheEntry Entry);
}

// ── JSON DTOs (disk) ──────────────────────────────────────────────────────────

internal sealed class DiskEntryMeta
{
    [JsonPropertyName("byteLength")]
    public long ByteLength { get; init; }

    [JsonPropertyName("isBinary")]
    public bool IsBinary { get; init; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; init; }
}

internal sealed class DiskIndexEntry
{
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("byteLength")]
    public long ByteLength { get; set; }

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}

internal sealed class DiskCacheIndex
{
    [JsonPropertyName("entries")]
    public List<DiskIndexEntry> Entries { get; init; } = new();
}

[JsonSerializable(typeof(DiskEntryMeta))]
[JsonSerializable(typeof(DiskIndexEntry))]
[JsonSerializable(typeof(DiskCacheIndex))]
internal partial class RepoFileCacheJsonContext : JsonSerializerContext
{
}
