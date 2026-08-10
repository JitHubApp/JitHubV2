using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal static readonly TimeSpan ImmutableBlobTtl = TimeSpan.FromDays(30);

    private readonly int _memMaxEntries;
    private readonly long _memMaxBytes;
    private readonly long _diskMaxBytes;
    private readonly TimeSpan _ttl;
    private readonly string _diskRoot;
    private readonly IAccountWorkQuiescence? _accountWork;

    // ── In-memory LRU ────────────────────────────────────────────────────────
    private long _memCurrentBytes;
    private readonly LinkedList<MemoryLruEntry> _lruList = new();
    private readonly Dictionary<string, LinkedListNode<MemoryLruEntry>> _memIndex = new(StringComparer.Ordinal);
    private readonly object _memLock = new();

    // ── Concurrency ──────────────────────────────────────────────────────────
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, KeyLockEntry> _keyLocks = new(StringComparer.Ordinal);
    private readonly Task _startupMaintenanceTask = Task.CompletedTask;

    public RepoFileCacheService()
        : this(DefaultMemMaxEntries, DefaultMemMaxBytes, DefaultDiskMaxBytes, ImmutableBlobTtl, new ApplicationTaskCoordinator(), null) { }

    public RepoFileCacheService(IApplicationTaskCoordinator taskCoordinator)
        : this(DefaultMemMaxEntries, DefaultMemMaxBytes, DefaultDiskMaxBytes, ImmutableBlobTtl, taskCoordinator, null) { }

    public RepoFileCacheService(
        IApplicationTaskCoordinator taskCoordinator,
        IAccountWorkQuiescence accountWork)
        : this(
            DefaultMemMaxEntries,
            DefaultMemMaxBytes,
            DefaultDiskMaxBytes,
            ImmutableBlobTtl,
            taskCoordinator,
            accountWork)
    {
    }

    public RepoFileCacheService(int memMaxEntries, long memMaxBytes, long diskMaxBytes, TimeSpan ttl)
        : this(memMaxEntries, memMaxBytes, diskMaxBytes, ttl, new ApplicationTaskCoordinator(), null)
    {
    }

    private RepoFileCacheService(
        int memMaxEntries,
        long memMaxBytes,
        long diskMaxBytes,
        TimeSpan ttl,
        IApplicationTaskCoordinator taskCoordinator,
        IAccountWorkQuiescence? accountWork)
    {
        _memMaxEntries = memMaxEntries;
        _memMaxBytes = memMaxBytes;
        _diskMaxBytes = diskMaxBytes;
        _ttl = ttl;
        _accountWork = accountWork;

        _diskRoot = PrepareOwnedRoot(GetDefaultDiskRoot());

        _startupMaintenanceTask = taskCoordinator.RunAsync(
            PurgeAsync,
            new ApplicationTaskOptions("repo_file_cache.startup_maintenance"));
    }

    internal RepoFileCacheService(
        int memMaxEntries,
        long memMaxBytes,
        long diskMaxBytes,
        TimeSpan ttl,
        string diskRoot,
        IAccountWorkQuiescence? accountWork = null)
    {
        _memMaxEntries = memMaxEntries;
        _memMaxBytes = memMaxBytes;
        _diskMaxBytes = diskMaxBytes;
        _ttl = ttl;
        _accountWork = accountWork;
        _diskRoot = PrepareOwnedRoot(diskRoot);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public string RootPath => _diskRoot;

    public long DiskSoftCapBytes => _diskMaxBytes;

    public TimeSpan Ttl => _ttl;

    internal Task StartupMaintenanceTask => _startupMaintenanceTask;

    private static string GetDefaultDiskRoot()
    {
        if (AppDataPathPolicy.TryGetAutomationRoots(out _, out string localCachePath))
        {
            return Path.Combine(localCachePath, "RepoFileCache");
        }

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

    private static string PrepareOwnedRoot(string root)
    {
        string fullPath = Path.GetFullPath(root);
        Directory.CreateDirectory(fullPath);
        DirectoryInfo directory = new(fullPath);
        if ((directory.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
        {
            FileSystemInfo? target = directory.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                throw new InvalidDataException("The repository cache root could not be resolved.");
            }

            fullPath = Path.GetFullPath(target.FullName);
        }

        string? pathRoot = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public bool TryGet(RepoFileCacheKey key, out RepoFileCacheEntry entry)
    {
        ValidateKey(key);
        string mk = MemKey(key);
        lock (_memLock)
        {
            if (_memIndex.TryGetValue(mk, out var node))
            {
                if (IsExpired(node.Value.Entry))
                {
                    RemoveMemoryNode(node);
                    entry = null!;
                    return false;
                }

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
        ValidateKey(key);
        using IAccountWorkLease? lease = EnterAccountWork(key, ct);
        ct = lease?.CancellationToken ?? ct;
        if (TryGet(key, out var cached))
            return cached;

        using KeyLockLease keyLock = await AcquireKeyLockAsync(key, ct).ConfigureAwait(false);
        bool maintenanceLockAcquired = false;
        try
        {
            await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
            maintenanceLockAcquired = true;

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
            if (maintenanceLockAcquired)
            {
                _maintenanceLock.Release();
            }
        }
    }

    public async Task PutAsync(RepoFileCacheKey key, RepoFileCacheEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateKey(key);
        using IAccountWorkLease? lease = EnterAccountWork(key, ct);
        ct = lease?.CancellationToken ?? ct;
        ct.ThrowIfCancellationRequested();
        RepoFileCacheEntry effectiveEntry = NormalizeCachedAt(entry);
        PromoteToMemory(MemKey(key), effectiveEntry);

        using KeyLockLease keyLock = await AcquireKeyLockAsync(key, ct).ConfigureAwait(false);
        bool maintenanceLockAcquired = false;
        try
        {
            await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
            maintenanceLockAcquired = true;

            string binPath = BinPath(key);
            string metaPath = MetaPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);

            await File.WriteAllBytesAsync(binPath, effectiveEntry.Bytes, ct).ConfigureAwait(false);

            var meta = new DiskEntryMeta
            {
                ByteLength = effectiveEntry.ByteLength,
                IsBinary = effectiveEntry.IsBinary,
                Encoding = effectiveEntry.Encoding,
                CachedAt = effectiveEntry.CachedAt,
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

            await EnforceDiskCapAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (maintenanceLockAcquired)
            {
                _maintenanceLock.Release();
            }
        }
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PurgeExpiredAsync(ct).ConfigureAwait(false);
            await EnforceDiskCapAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task<long> GetTotalBytesAsync(CancellationToken ct = default)
    {
        await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_diskRoot))
            {
                return 0;
            }

            return await Task.Run(
                () => EnumerateOwnedFiles(_diskRoot)
                    .Sum(path =>
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            return new FileInfo(path).Length;
                        }
                        catch (FileNotFoundException)
                        {
                            return 0L;
                        }
                    }),
                ct).ConfigureAwait(false);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task<CacheStoreInspection> InspectAsync(CancellationToken ct = default)
    {
        await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _indexLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(_diskRoot))
                {
                    return new CacheStoreInspection(
                        CacheOwnerHealth.Healthy,
                        PhysicalBytes: 0,
                        LogicalBytes: 0,
                        OrphanBytes: 0,
                        new Dictionary<string, long>());
                }

                long physicalBytes = GetDirectoryBytes(_diskRoot, ct);
                string indexPath = IndexPath();
                DiskCacheIndex index;
                try
                {
                    index = await LoadIndexStrictAsync(indexPath, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                {
                    return new CacheStoreInspection(
                        CacheOwnerHealth.Unhealthy,
                        physicalBytes,
                        LogicalBytes: 0,
                        OrphanBytes: 0,
                        new Dictionary<string, long>
                        {
                            [CacheMetricKeys.OrphanBytes] = 0
                        },
                        "The repository file cache index is corrupt.");
                }

                HashSet<string> activePaths = new(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(indexPath))
                {
                    activePaths.Add(indexPath);
                }

                HashSet<string> uniqueEntries = new(StringComparer.Ordinal);
                List<string> integrityProblems = [];
                long logicalBytes = 0;
                foreach (DiskIndexEntry indexed in index.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!TryCreateValidatedKey(indexed, out RepoFileCacheKey key))
                    {
                        integrityProblems.Add("The repository file index contains an invalid entry.");
                        continue;
                    }

                    if (!uniqueEntries.Add(DiskKey(key)))
                    {
                        integrityProblems.Add("The repository file index contains a duplicate entry.");
                    }

                    string binPath = BinPath(key);
                    string metaPath = MetaPath(key);
                    activePaths.Add(binPath);
                    activePaths.Add(metaPath);
                    if (!File.Exists(binPath) || !File.Exists(metaPath))
                    {
                        integrityProblems.Add("A repository file cache entry is missing its payload or metadata.");
                        continue;
                    }

                    try
                    {
                        await using FileStream stream = new(
                            metaPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            4096,
                            FileOptions.Asynchronous);
                        DiskEntryMeta? metadata = await JsonSerializer.DeserializeAsync(
                            stream,
                            RepoFileCacheJsonContext.Default.DiskEntryMeta,
                            ct).ConfigureAwait(false);
                        long payloadBytes = TryGetOwnedFileLength(binPath);
                        if (metadata is null ||
                            metadata.ByteLength != indexed.ByteLength ||
                            metadata.ByteLength != payloadBytes)
                        {
                            integrityProblems.Add("A repository file cache length does not match its index.");
                        }

                        logicalBytes += Math.Max(0, payloadBytes);
                    }
                    catch (Exception exception) when (exception is IOException or JsonException)
                    {
                        integrityProblems.Add("A repository file cache metadata record is corrupt.");
                    }
                }

                long orphanBytes = 0;
                foreach (string path in EnumerateOwnedFiles(_diskRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!activePaths.Contains(path))
                    {
                        orphanBytes += TryGetOwnedFileLength(path);
                    }
                }

                CacheOwnerHealth health = integrityProblems.Count > 0
                    ? CacheOwnerHealth.Unhealthy
                    : orphanBytes > 0
                        ? CacheOwnerHealth.Degraded
                        : CacheOwnerHealth.Healthy;
                string? detail = CacheInspectionDetail.Format(
                    integrityProblems.Concat(
                        orphanBytes > 0
                            ? ["Unindexed repository file cache payloads are awaiting cleanup."]
                            : []));
                return new CacheStoreInspection(
                    health,
                    physicalBytes,
                    logicalBytes,
                    orphanBytes,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.ActivePayloadBytes] = logicalBytes,
                        [CacheMetricKeys.OrphanBytes] = orphanBytes
                    },
                    detail);
            }
            finally
            {
                _indexLock.Release();
            }
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_memLock)
            {
                _memIndex.Clear();
                _lruList.Clear();
                _memCurrentBytes = 0;
            }

            if (Directory.Exists(_diskRoot))
            {
                foreach (string file in EnumerateOwnedFiles(_diskRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Delete(EnsureOwnedPath(file));
                }

                foreach (string directory in EnumerateOwnedDirectories(_diskRoot)
                    .OrderByDescending(static path => path.Length))
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.Delete(EnsureOwnedPath(directory), recursive: false);
                }
            }

            Directory.CreateDirectory(_diskRoot);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task ClearPartitionAsync(string userId, CancellationToken ct = default)
    {
        string partition = GitHubAccountPartition.Require(userId);
        _ = ValidatePathSegment(partition, nameof(userId));
        await _maintenanceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_memLock)
            {
                string prefix = partition + "/";
                foreach (LinkedListNode<MemoryLruEntry> node in _lruList
                             .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                             .Select(entry => _memIndex[entry.Key])
                             .ToArray())
                {
                    RemoveMemoryNode(node);
                }
            }

            List<CacheClearResidual> residuals = [];
            await _indexLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DiskCacheIndex index = await LoadIndexStrictAsync(IndexPath(), ct).ConfigureAwait(false);
                RemoveInvalidIndexEntries(index);
                index.Entries.RemoveAll(entry =>
                    string.Equals(entry.UserId, partition, StringComparison.Ordinal));

                string partitionRoot = GetPartitionRoot(partition);
                if (Directory.Exists(partitionRoot))
                {
                    foreach (string path in EnumerateOwnedFiles(partitionRoot))
                    {
                        ct.ThrowIfCancellationRequested();
                        TryDeleteFileStrict(path, residuals);
                    }

                    foreach (string directory in EnumerateOwnedDirectories(partitionRoot)
                             .OrderByDescending(static path => path.Length))
                    {
                        TryDeleteDirectoryStrict(directory, residuals);
                    }

                    TryDeleteDirectoryStrict(partitionRoot, residuals);
                }

                await SaveIndexAsync(index, ct).ConfigureAwait(false);
                if (index.Entries.Any(entry =>
                        string.Equals(entry.UserId, partition, StringComparison.Ordinal)))
                {
                    residuals.Add(new CacheClearResidual(
                        partition,
                        "The repository file index still contains this account partition."));
                }
            }
            finally
            {
                _indexLock.Release();
            }

            if (residuals.Count > 0)
            {
                throw new CacheClearPostconditionException(CacheOwnerIds.RepositoryFiles, residuals);
            }
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private IAccountWorkLease? EnterAccountWork(RepoFileCacheKey key, CancellationToken cancellationToken)
    {
        if (_accountWork is null ||
            string.IsNullOrWhiteSpace(key.UserId) ||
            key.UserId.Equals("current", StringComparison.OrdinalIgnoreCase) ||
            key.UserId.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _accountWork.Enter(key.UserId, cancellationToken);
    }

    // ── Disk helpers ─────────────────────────────────────────────────────────

    private static string SanitizedOwnerRepo(RepoFileCacheKey key)
    {
        return $"{ValidatePathSegment(key.Owner, nameof(key.Owner))}_{ValidatePathSegment(key.Repo, nameof(key.Repo))}";
    }

    private string BinPath(RepoFileCacheKey key)
    {
        ValidateKey(key);
        string sha = ValidatePathSegment(key.Sha, nameof(key.Sha));
        string prefix = sha.Length >= 2 ? sha.Substring(0, 2) : sha;
        return ResolveOwnedPath(
            ValidatePathSegment(key.UserId, nameof(key.UserId)),
            SanitizedOwnerRepo(key),
            prefix,
            sha + ".bin");
    }

    private string MetaPath(RepoFileCacheKey key)
    {
        ValidateKey(key);
        string sha = ValidatePathSegment(key.Sha, nameof(key.Sha));
        string prefix = sha.Length >= 2 ? sha.Substring(0, 2) : sha;
        return ResolveOwnedPath(
            ValidatePathSegment(key.UserId, nameof(key.UserId)),
            SanitizedOwnerRepo(key),
            prefix,
            sha + ".json");
    }

    private static string ValidatePathSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Repository cache path segments cannot be empty.", parameterName);
        }

        string normalized = value.Trim();
        if (normalized.Length > 255 ||
            normalized is "." or ".." ||
            !string.Equals(normalized, value, StringComparison.Ordinal) ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains(Path.DirectorySeparatorChar) ||
            normalized.Contains(Path.AltDirectorySeparatorChar) ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Repository cache path segments contain invalid characters.", parameterName);
        }

        if (IsReservedWindowsName(normalized))
        {
            throw new ArgumentException("Repository cache path segments cannot use reserved Windows names.", parameterName);
        }

        return normalized;
    }

    private string IndexPath() => ResolveOwnedPath("index.json");

    private string GetPartitionRoot(string userId)
    {
        return ResolveOwnedPath(ValidatePathSegment(userId, nameof(userId)));
    }

    private void TryDeleteFileStrict(string path, List<CacheClearResidual> residuals)
    {
        try
        {
            File.Delete(EnsureOwnedPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            residuals.Add(new CacheClearResidual(path, $"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    private void TryDeleteDirectoryStrict(string path, List<CacheClearResidual> residuals)
    {
        try
        {
            path = EnsureOwnedPath(path);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }

            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                residuals.Add(new CacheClearResidual(path, "The directory still contains cache data."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            residuals.Add(new CacheClearResidual(path, $"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    private void DeleteDiskFiles(RepoFileCacheKey key)
    {
        TryDeleteOwnedFile(BinPath(key));
        TryDeleteOwnedFile(MetaPath(key));
    }

    // ── Index management ─────────────────────────────────────────────────────

    private async Task<DiskCacheIndex> LoadIndexAsync(CancellationToken ct)
    {
        string path = IndexPath();
        if (!File.Exists(path))
            return new DiskCacheIndex();

        try
        {
            DiskCacheIndex index;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            {
                index = await JsonSerializer.DeserializeAsync(
                    stream,
                    RepoFileCacheJsonContext.Default.DiskCacheIndex,
                    ct).ConfigureAwait(false) ?? new DiskCacheIndex();
            }

            if (RemoveInvalidIndexEntries(index))
            {
                await SaveIndexAsync(index, ct).ConfigureAwait(false);
            }

            return index;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            DiskCacheIndex replacement = new();
            TryDeleteOwnedFile(path);
            await SaveIndexAsync(replacement, ct).ConfigureAwait(false);
            return replacement;
        }
    }

    private async Task SaveIndexAsync(DiskCacheIndex index, CancellationToken ct)
    {
        RemoveInvalidIndexEntries(index);
        string path = IndexPath();
        await using var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(s, index, RepoFileCacheJsonContext.Default.DiskCacheIndex, ct).ConfigureAwait(false);
    }

    private async Task AppendIndexEntryAsync(RepoFileCacheKey key, DiskEntryMeta meta, CancellationToken ct)
    {
        ValidateKey(key);
        // Already called under _indexLock.
        var index = await LoadIndexAsync(ct).ConfigureAwait(false);

        // Remove any existing entry for this sha to avoid duplicates.
        index.Entries.RemoveAll(e => string.Equals(e.UserId, key.UserId, StringComparison.Ordinal)
                                   && string.Equals(e.Owner, key.Owner, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.Repo, key.Repo, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(e.Sha, key.Sha, StringComparison.OrdinalIgnoreCase));

        index.Entries.Add(new DiskIndexEntry
        {
            UserId = key.UserId,
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
                if (TryCreateValidatedKey(e, out RepoFileCacheKey key))
                {
                    DeleteDiskFiles(key);
                }

                index.Entries.Remove(e);
            }

            if (expired.Count > 0)
                await SaveIndexAsync(index, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            // Cache maintenance is best-effort, but cancellation must remain observable.
        }
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
            long total = CalculateTotalBytes(index.Entries);

            if (total <= _diskMaxBytes)
                return;

            // Sort by oldest first, evict until under cap.
            index.Entries.Sort((a, b) => a.CachedAt.CompareTo(b.CachedAt));
            while (total > _diskMaxBytes && index.Entries.Count > 0)
            {
                var victim = index.Entries[0];
                index.Entries.RemoveAt(0);
                total = CalculateTotalBytes(index.Entries);
                if (TryCreateValidatedKey(victim, out RepoFileCacheKey key))
                {
                    DeleteDiskFiles(key);
                }
            }

            await SaveIndexAsync(index, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            // Cache maintenance is best-effort, but cancellation must remain observable.
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private static async Task<DiskCacheIndex> LoadIndexStrictAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new DiskCacheIndex();
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        DiskCacheIndex index = await JsonSerializer.DeserializeAsync(
            stream,
            RepoFileCacheJsonContext.Default.DiskCacheIndex,
            ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The repository file cache index is empty.");
        if (index.Entries is null)
        {
            throw new InvalidDataException("The repository file cache index has no entries collection.");
        }

        return index;
    }

    private long GetDirectoryBytes(string root, CancellationToken cancellationToken)
    {
        long bytes = 0;
        foreach (string path in EnumerateOwnedFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += TryGetOwnedFileLength(path);
        }

        return bytes;
    }

    private long TryGetOwnedFileLength(string path)
    {
        try
        {
            path = EnsureOwnedPath(path);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 0;
        }
    }

    private string ResolveOwnedPath(params string[] segments)
    {
        string candidate = _diskRoot;
        foreach (string segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        return EnsureOwnedPath(candidate);
    }

    private IEnumerable<string> EnumerateOwnedFiles(string root)
    {
        root = EnsureOwnedPath(root, allowRoot: true);
        return Directory.EnumerateFiles(root, "*", CreateOwnedEnumerationOptions())
            .Select(EnsureOwnedPath);
    }

    private IEnumerable<string> EnumerateOwnedDirectories(string root)
    {
        root = EnsureOwnedPath(root, allowRoot: true);
        return Directory.EnumerateDirectories(root, "*", CreateOwnedEnumerationOptions())
            .Select(EnsureOwnedPath);
    }

    private static EnumerationOptions CreateOwnedEnumerationOptions() => new()
    {
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = System.IO.FileAttributes.ReparsePoint
    };

    private string EnsureOwnedPath(string path) => EnsureOwnedPath(path, allowRoot: false);

    private string EnsureOwnedPath(string path, bool allowRoot)
    {
        string candidate = Path.GetFullPath(path);
        if (!IsOwnedPath(candidate, allowRoot))
        {
            throw new InvalidDataException("The repository cache path escaped its storage root.");
        }

        EnsureExistingReparsePointsStayOwned(candidate);
        return candidate;
    }

    private bool IsOwnedPath(string candidate, bool allowRoot)
    {
        if (allowRoot && string.Equals(candidate, _diskRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootPrefix = _diskRoot.EndsWith(Path.DirectorySeparatorChar) ||
            _diskRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? _diskRoot
                : _diskRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureExistingReparsePointsStayOwned(string candidate)
    {
        string relativePath = Path.GetRelativePath(_diskRoot, candidate);
        string current = _diskRoot;
        foreach (string segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            System.IO.FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & System.IO.FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo link = (attributes & System.IO.FileAttributes.Directory) != 0
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            FileSystemInfo? target = link.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !IsOwnedPath(Path.GetFullPath(target.FullName), allowRoot: true))
            {
                throw new InvalidDataException("The repository cache path resolves outside its storage root.");
            }
        }
    }

    private void TryDeleteOwnedFile(string path)
    {
        try
        {
            File.Delete(EnsureOwnedPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Cache cleanup is best-effort. Invalid paths are never followed outside the owned root.
        }
    }

    private static void ValidateKey(RepoFileCacheKey key)
    {
        _ = ValidatePathSegment(key.UserId, nameof(key.UserId));
        _ = ValidatePathSegment(key.Owner, nameof(key.Owner));
        _ = ValidatePathSegment(key.Repo, nameof(key.Repo));
        _ = ValidatePathSegment(key.Sha, nameof(key.Sha));
    }

    private static bool TryCreateValidatedKey(DiskIndexEntry entry, out RepoFileCacheKey key)
    {
        key = new RepoFileCacheKey(entry.Owner, entry.Repo, entry.Sha, entry.UserId);
        try
        {
            ValidateKey(key);
            return entry.ByteLength >= 0 && entry.CachedAt != default;
        }
        catch (ArgumentException)
        {
            key = default;
            return false;
        }
    }

    private static bool RemoveInvalidIndexEntries(DiskCacheIndex index)
    {
        if (index.Entries is null)
        {
            index.Entries = [];
            return true;
        }

        HashSet<string> keys = new(StringComparer.Ordinal);
        int removed = index.Entries.RemoveAll(entry =>
            entry is null ||
            !TryCreateValidatedKey(entry, out RepoFileCacheKey key) ||
            !keys.Add(DiskKey(key)));
        return removed > 0;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
        {
            return left;
        }

        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static long CalculateTotalBytes(IEnumerable<DiskIndexEntry> entries)
    {
        long total = 0;
        foreach (DiskIndexEntry entry in entries)
        {
            total = SaturatingAdd(total, entry.ByteLength);
        }

        return total;
    }

    private static bool IsReservedWindowsName(string value)
    {
        string fileName = value.Split('.')[0];
        return fileName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (fileName.Length == 4 &&
             (fileName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              fileName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             fileName[3] is >= '1' and <= '9');
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

    private void RemoveMemoryNode(LinkedListNode<MemoryLruEntry> node)
    {
        _lruList.Remove(node);
        _memIndex.Remove(node.Value.Key);
        _memCurrentBytes -= node.Value.Entry.ByteLength;
    }

    private bool IsExpired(RepoFileCacheEntry entry) =>
        DateTimeOffset.UtcNow - entry.CachedAt > _ttl;

    private static RepoFileCacheEntry NormalizeCachedAt(RepoFileCacheEntry entry)
    {
        if (entry.CachedAt != default)
        {
            return entry;
        }

        return new RepoFileCacheEntry
        {
            Sha = entry.Sha,
            ByteLength = entry.ByteLength,
            IsBinary = entry.IsBinary,
            Bytes = entry.Bytes,
            Text = entry.Text,
            Encoding = entry.Encoding,
            CachedAt = DateTimeOffset.UtcNow,
        };
    }

    private async ValueTask<KeyLockLease> AcquireKeyLockAsync(RepoFileCacheKey key, CancellationToken ct)
    {
        string cacheKey = DiskKey(key);
        while (true)
        {
            KeyLockEntry entry = _keyLocks.GetOrAdd(cacheKey, static _ => new KeyLockEntry());
            if (!entry.TryAddReference())
            {
                TryRemoveKeyLock(cacheKey, entry);
                continue;
            }

            try
            {
                await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
                return new KeyLockLease(this, cacheKey, entry);
            }
            catch
            {
                ReleaseKeyLockReference(cacheKey, entry);
                throw;
            }
        }
    }

    private void ReleaseKeyLock(string cacheKey, KeyLockEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseKeyLockReference(cacheKey, entry);
    }

    private void ReleaseKeyLockReference(string cacheKey, KeyLockEntry entry)
    {
        if (entry.ReleaseReference())
        {
            TryRemoveKeyLock(cacheKey, entry);
            entry.Semaphore.Dispose();
        }
    }

    private void TryRemoveKeyLock(string cacheKey, KeyLockEntry entry) =>
        ((ICollection<KeyValuePair<string, KeyLockEntry>>)_keyLocks)
            .Remove(new KeyValuePair<string, KeyLockEntry>(cacheKey, entry));

    private static string MemKey(RepoFileCacheKey key)
        => $"{key.UserId}/{key.Owner}/{key.Repo}/{key.Sha}";

    private static string DiskKey(RepoFileCacheKey key)
        => $"{key.UserId}/{key.Owner.ToLowerInvariant()}/{key.Repo.ToLowerInvariant()}/{key.Sha.ToLowerInvariant()}";

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

    private sealed class KeyLockEntry
    {
        private readonly object _gate = new();
        private int _references;
        private bool _retired;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddReference()
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _references++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_gate)
            {
                _references--;
                if (_references != 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private sealed class KeyLockLease : IDisposable
    {
        private RepoFileCacheService? _owner;
        private readonly string _cacheKey;
        private readonly KeyLockEntry _entry;

        public KeyLockLease(RepoFileCacheService owner, string cacheKey, KeyLockEntry entry)
        {
            _owner = owner;
            _cacheKey = cacheKey;
            _entry = entry;
        }

        public void Dispose()
        {
            RepoFileCacheService? owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseKeyLock(_cacheKey, _entry);
        }
    }
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
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "current";

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
    public List<DiskIndexEntry> Entries { get; set; } = new();
}

[JsonSerializable(typeof(DiskEntryMeta))]
[JsonSerializable(typeof(DiskIndexEntry))]
[JsonSerializable(typeof(DiskCacheIndex))]
internal partial class RepoFileCacheJsonContext : JsonSerializerContext
{
}
