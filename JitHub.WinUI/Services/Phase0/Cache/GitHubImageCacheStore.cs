using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record GitHubImageCacheEntry(
    string CacheKey,
    string FilePath,
    long ByteLength,
    DateTimeOffset CachedAt,
    DateTimeOffset LastAccessedAt,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    string? ContentType = null);

public sealed record GitHubImageCacheWriteMetadata(
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentType);

public sealed record GitHubImageCacheRead(
    GitHubImageCacheEntry Entry,
    byte[] Bytes);

internal sealed record GitHubImageCacheManifest(
    string? PayloadFileName,
    GitHubImageCacheWriteMetadata Metadata,
    string? AccountPartition = null);

public interface IGitHubImageCacheStore
{
    Task<GitHubImageCacheEntry?> TryGetAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task<GitHubImageCacheRead?> TryReadAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task<GitHubImageCacheEntry> PutAsync(
        string cacheKey,
        byte[] bytes,
        string extension,
        CancellationToken cancellationToken = default);

    Task<GitHubImageCacheEntry> PutAsync(
        string cacheKey,
        byte[] bytes,
        string extension,
        GitHubImageCacheWriteMetadata metadata,
        CancellationToken cancellationToken = default);

    Task MarkFreshAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task ClearAllAsync(CancellationToken cancellationToken = default);

    Task ClearPartitionAsync(string accountPartition, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "This image cache store does not support account-partition removal."));

    Task EnforceCapAsync(CancellationToken cancellationToken = default);

    Task<long> GetTotalBytesAsync(CancellationToken cancellationToken = default);

    Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CacheStoreInspection.Unavailable("Integrity inspection is not implemented by this image cache store."));
}

public sealed class GitHubImageCacheStore : IGitHubImageCacheStore
{
    private readonly string _imageRootPath;
    private readonly GitHubCachePolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubImageCacheStore(IAppStoragePathProvider pathProvider)
        : this(pathProvider.ImageRootPath, GitHubCachePolicy.Default)
    {
    }

    internal GitHubImageCacheStore(string imageRootPath, GitHubCachePolicy policy)
    {
        _imageRootPath = imageRootPath;
        _policy = policy;
        Directory.CreateDirectory(_imageRootPath);
    }

    public async Task<GitHubImageCacheEntry?> TryGetAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await TryGetCoreAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GitHubImageCacheEntry> PutAsync(
        string cacheKey,
        byte[] bytes,
        string extension,
        CancellationToken cancellationToken = default)
    {
        return await PutAsync(
            cacheKey,
            bytes,
            extension,
            new GitHubImageCacheWriteMetadata(null, null, null),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubImageCacheEntry> PutAsync(
        string cacheKey,
        byte[] bytes,
        string extension,
        GitHubImageCacheWriteMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(bytes);

        _ = NormalizeExtension(extension);
        string prefix = HashKey(cacheKey);
        string generation = Guid.NewGuid().ToString("N");
        string payloadFileName = $"{prefix}.{generation}.img";
        string filePath = Path.Combine(_imageRootPath, payloadFileName);
        string temporaryPath = filePath + ".tmp";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            bool manifestCommitted = false;
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, filePath);
                DateTime now = DateTime.UtcNow;
                File.SetCreationTimeUtc(filePath, now);
                File.SetLastWriteTimeUtc(filePath, now);
                File.SetLastAccessTimeUtc(filePath, now);

                // The manifest is the atomic commit point. Until its replace succeeds, readers
                // continue to resolve the prior generation and its matching validators.
                await WriteManifestAtomicallyAsync(
                        prefix,
                        new GitHubImageCacheManifest(
                            payloadFileName,
                            metadata,
                            ExtractAccountPartition(cacheKey)),
                        cancellationToken)
                    .ConfigureAwait(false);
                manifestCommitted = true;
            }
            finally
            {
                TryDelete(temporaryPath);
                if (!manifestCommitted)
                {
                    TryDelete(filePath);
                }
            }

            await EnforceCapCoreAsync(cancellationToken);
            return CreateEntry(cacheKey, new FileInfo(filePath), metadata);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkFreshAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string prefix = HashKey(cacheKey);
            GitHubImageCacheManifest manifest = await ReadManifestAsync(prefix, cancellationToken)
                .ConfigureAwait(false);
            string? filePath = ResolvePayloadPath(prefix, manifest);
            if (filePath is null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(filePath, now);
            File.SetLastAccessTimeUtc(filePath, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GitHubImageCacheRead?> TryReadAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GitHubImageCacheEntry? entry = await TryGetCoreAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return null;
            }

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(entry.FilePath, cancellationToken)
                    .ConfigureAwait(false);
                return bytes.Length == 0 ? null : new GitHubImageCacheRead(entry, bytes);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string prefix = HashKey(cacheKey);
            foreach (string existing in Directory.EnumerateFiles(_imageRootPath, $"{prefix}.*"))
            {
                TryDelete(existing);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(_imageRootPath))
            {
                return;
            }

            List<CacheClearResidual> residuals = [];
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(_imageRootPath, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CacheClearPostconditionException(
                    CacheOwnerIds.GitHubImages,
                    [new CacheClearResidual(_imageRootPath, $"{exception.GetType().Name}: {exception.Message}")]);
            }

            foreach (string existing in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(existing);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    residuals.Add(new CacheClearResidual(
                        existing,
                        $"{exception.GetType().Name}: {exception.Message}"));
                }
            }

            try
            {
                foreach (string existing in Directory.EnumerateFiles(_imageRootPath, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!residuals.Any(residual => string.Equals(
                            residual.Identity,
                            existing,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        residuals.Add(new CacheClearResidual(existing, "The image cache file still exists after clear."));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                residuals.Add(new CacheClearResidual(
                    _imageRootPath,
                    $"Postcondition verification failed: {exception.GetType().Name}: {exception.Message}"));
            }

            if (residuals.Count > 0)
            {
                throw new CacheClearPostconditionException(CacheOwnerIds.GitHubImages, residuals);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnforceCapAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnforceCapCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetTotalBytesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Directory.Exists(_imageRootPath)
                ? Directory
                    .EnumerateFiles(_imageRootPath)
                    .Where(static path => IsPayloadPath(path))
                    .Sum(static path => TryGetLength(path))
                : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearPartitionAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_imageRootPath))
            {
                return;
            }

            List<CacheClearResidual> residuals = [];
            foreach (string manifestPath in Directory.EnumerateFiles(
                         _imageRootPath,
                         "*.meta",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                GitHubImageCacheManifest manifest;
                try
                {
                    manifest = await ReadManifestStrictAsync(manifestPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or FormatException or InvalidDataException)
                {
                    residuals.Add(new CacheClearResidual(
                        manifestPath,
                        $"The manifest could not be classified safely: {exception.GetType().Name}."));
                    continue;
                }

                if (!string.Equals(manifest.AccountPartition, partition, StringComparison.Ordinal))
                {
                    continue;
                }

                string prefix = Path.GetFileNameWithoutExtension(manifestPath);
                foreach (string path in Directory.EnumerateFiles(
                             _imageRootPath,
                             $"{prefix}.*",
                             SearchOption.TopDirectoryOnly))
                {
                    TryDeleteStrict(path, residuals);
                }
            }

            if (residuals.Count > 0)
            {
                throw new CacheClearPostconditionException(CacheOwnerIds.GitHubImages, residuals);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_imageRootPath))
            {
                return new CacheStoreInspection(
                    CacheOwnerHealth.Healthy,
                    PhysicalBytes: 0,
                    LogicalBytes: 0,
                    OrphanBytes: 0,
                    new Dictionary<string, long>());
            }

            HashSet<string> activeFiles = new(StringComparer.OrdinalIgnoreCase);
            List<string> integrityProblems = [];
            long activePayloadBytes = 0;
            long manifestBytes = 0;

            foreach (string manifestPath in Directory.EnumerateFiles(_imageRootPath, "*.meta", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                manifestBytes += TryGetLength(manifestPath);
                activeFiles.Add(manifestPath);
                string prefix = Path.GetFileNameWithoutExtension(manifestPath);
                try
                {
                    GitHubImageCacheManifest manifest = await ReadManifestStrictAsync(manifestPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (manifest.PayloadFileName is not null &&
                        !IsSafePayloadFileName(prefix, manifest.PayloadFileName))
                    {
                        integrityProblems.Add("An image manifest contains an unsafe payload identity.");
                        continue;
                    }

                    string? payloadPath = ResolvePayloadPath(prefix, manifest);
                    if (payloadPath is null || !File.Exists(payloadPath))
                    {
                        integrityProblems.Add("An image manifest references a missing payload.");
                        continue;
                    }

                    activeFiles.Add(payloadPath);
                    activePayloadBytes += TryGetLength(payloadPath);
                }
                catch (Exception exception) when (exception is IOException or FormatException or InvalidDataException)
                {
                    integrityProblems.Add("An image manifest is corrupt.");
                }
            }

            long physicalBytes = 0;
            long orphanBytes = 0;
            foreach (string path in Directory.EnumerateFiles(_imageRootPath, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length = TryGetLength(path);
                physicalBytes += length;
                if (!activeFiles.Contains(path))
                {
                    orphanBytes += length;
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
                        ? ["Unreferenced image generations are awaiting cleanup."]
                        : []));

            return new CacheStoreInspection(
                health,
                physicalBytes,
                activePayloadBytes,
                orphanBytes,
                new Dictionary<string, long>
                {
                    [CacheMetricKeys.ActivePayloadBytes] = activePayloadBytes,
                    [CacheMetricKeys.ManifestBytes] = manifestBytes,
                    [CacheMetricKeys.OrphanBytes] = orphanBytes
                },
                detail);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    private async Task EnforceCapCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, string> manifestByActivePayload = new(StringComparer.OrdinalIgnoreCase);
        foreach (string manifestPath in Directory.EnumerateFiles(_imageRootPath, "*.meta", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prefix = Path.GetFileNameWithoutExtension(manifestPath);
            GitHubImageCacheManifest manifest = await ReadManifestAsync(prefix, cancellationToken).ConfigureAwait(false);
            string? payloadPath = ResolvePayloadPath(prefix, manifest);
            if (payloadPath is not null && File.Exists(payloadPath))
            {
                manifestByActivePayload[payloadPath] = manifestPath;
            }
            else
            {
                TryDelete(manifestPath);
            }
        }

        List<FileInfo> files = Directory
            .EnumerateFiles(_imageRootPath)
            .Where(static path => IsPayloadPath(path))
            .Select(static path => new FileInfo(path))
            .OrderBy(static file => file.LastAccessTimeUtc)
            .ToList();
        long totalBytes = files.Sum(static file => file.Length);

        // Superseded or interrupted generations have no active manifest. Reclaim them before
        // evicting a current entry, but only when the cache is over its soft cap so a control
        // still finishing an old file load is not disturbed during ordinary refresh.
        foreach (FileInfo file in files.Where(file => !manifestByActivePayload.ContainsKey(file.FullName)))
        {
            if (totalBytes <= _policy.AvatarImageSoftCapBytes)
            {
                break;
            }

            totalBytes -= file.Length;
            TryDelete(file.FullName);
            await Task.Yield();
        }

        int activeEntryCount = manifestByActivePayload.Count;
        foreach (FileInfo file in files)
        {
            if (totalBytes <= _policy.AvatarImageSoftCapBytes || activeEntryCount <= 1)
            {
                break;
            }

            if (!manifestByActivePayload.TryGetValue(file.FullName, out string? manifestPath))
            {
                continue;
            }

            totalBytes -= file.Length;
            TryDelete(file.FullName);
            TryDelete(manifestPath);
            activeEntryCount--;
            await Task.Yield();
        }
    }

    private async Task<GitHubImageCacheEntry?> TryGetCoreAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        string prefix = HashKey(cacheKey);
        GitHubImageCacheManifest manifest = await ReadManifestAsync(prefix, cancellationToken)
            .ConfigureAwait(false);
        string? filePath = ResolvePayloadPath(prefix, manifest);
        if (filePath is null || !File.Exists(filePath))
        {
            return null;
        }

        FileInfo file = new(filePath)
        {
            LastAccessTimeUtc = DateTime.UtcNow
        };
        return CreateEntry(cacheKey, file, manifest.Metadata);
    }

    private static GitHubImageCacheEntry CreateEntry(
        string cacheKey,
        FileInfo file,
        GitHubImageCacheWriteMetadata metadata) =>
        new(
            cacheKey,
            file.FullName,
            file.Length,
            file.LastWriteTimeUtc == DateTime.MinValue ? DateTimeOffset.UtcNow : new DateTimeOffset(file.LastWriteTimeUtc),
            file.LastAccessTimeUtc == DateTime.MinValue ? DateTimeOffset.UtcNow : new DateTimeOffset(file.LastAccessTimeUtc),
            metadata.ETag,
            metadata.LastModified,
            metadata.ContentType);

    private async Task<GitHubImageCacheManifest> ReadManifestAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_imageRootPath, $"{prefix}.meta");
        if (!File.Exists(path))
        {
            return new GitHubImageCacheManifest(null, new GitHubImageCacheWriteMetadata(null, null, null));
        }

        try
        {
            string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(lines.ElementAtOrDefault(0), "v3", StringComparison.Ordinal))
            {
                string? payloadFileName = Decode(lines.ElementAtOrDefault(1));
                return new GitHubImageCacheManifest(
                    IsSafePayloadFileName(prefix, payloadFileName) ? payloadFileName : null,
                    new GitHubImageCacheWriteMetadata(
                        Decode(lines.ElementAtOrDefault(2)),
                        DateTimeOffset.TryParse(lines.ElementAtOrDefault(3), out DateTimeOffset modified) ? modified : null,
                        Decode(lines.ElementAtOrDefault(4))),
                    Decode(lines.ElementAtOrDefault(5)));
            }

            if (string.Equals(lines.ElementAtOrDefault(0), "v2", StringComparison.Ordinal))
            {
                string? payloadFileName = Decode(lines.ElementAtOrDefault(1));
                return new GitHubImageCacheManifest(
                    IsSafePayloadFileName(prefix, payloadFileName) ? payloadFileName : null,
                    new GitHubImageCacheWriteMetadata(
                        Decode(lines.ElementAtOrDefault(2)),
                        DateTimeOffset.TryParse(lines.ElementAtOrDefault(3), out DateTimeOffset modified) ? modified : null,
                        Decode(lines.ElementAtOrDefault(4))));
            }

            // Legacy v1 sidecars did not include a payload pointer. Keep them readable while
            // new writes move to generation manifests.
            return new GitHubImageCacheManifest(
                null,
                new GitHubImageCacheWriteMetadata(
                    Decode(lines.ElementAtOrDefault(0)),
                    DateTimeOffset.TryParse(lines.ElementAtOrDefault(1), out DateTimeOffset legacyModified) ? legacyModified : null,
                    Decode(lines.ElementAtOrDefault(2))));
        }
        catch (IOException)
        {
            return new GitHubImageCacheManifest(null, new GitHubImageCacheWriteMetadata(null, null, null));
        }
        catch (FormatException)
        {
            return new GitHubImageCacheManifest(null, new GitHubImageCacheWriteMetadata(null, null, null));
        }
    }

    private static async Task<GitHubImageCacheManifest> ReadManifestStrictAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (string.Equals(lines.ElementAtOrDefault(0), "v3", StringComparison.Ordinal))
        {
            if (lines.Length < 6)
            {
                throw new InvalidDataException("The partitioned image manifest is incomplete.");
            }

            return new GitHubImageCacheManifest(
                Decode(lines[1]),
                new GitHubImageCacheWriteMetadata(
                    Decode(lines[2]),
                    DateTimeOffset.TryParse(lines[3], out DateTimeOffset modified) ? modified : null,
                    Decode(lines[4])),
                Decode(lines[5]));
        }

        if (string.Equals(lines.ElementAtOrDefault(0), "v2", StringComparison.Ordinal))
        {
            if (lines.Length < 5)
            {
                throw new InvalidDataException("The image manifest is incomplete.");
            }

            return new GitHubImageCacheManifest(
                Decode(lines[1]),
                new GitHubImageCacheWriteMetadata(
                    Decode(lines[2]),
                    DateTimeOffset.TryParse(lines[3], out DateTimeOffset modified) ? modified : null,
                    Decode(lines[4])));
        }

        if (lines.Length < 3)
        {
            throw new InvalidDataException("The legacy image manifest is incomplete.");
        }

        return new GitHubImageCacheManifest(
            null,
            new GitHubImageCacheWriteMetadata(
                Decode(lines[0]),
                DateTimeOffset.TryParse(lines[1], out DateTimeOffset legacyModified) ? legacyModified : null,
                Decode(lines[2])));
    }

    private async Task WriteManifestAtomicallyAsync(
        string prefix,
        GitHubImageCacheManifest manifest,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_imageRootPath, $"{prefix}.meta");
        string temporaryPath = Path.Combine(_imageRootPath, $"{prefix}.{Guid.NewGuid():N}.meta.tmp");
        string[] lines =
        [
            "v3",
            Encode(manifest.PayloadFileName),
            Encode(manifest.Metadata.ETag),
            manifest.Metadata.LastModified?.ToString("O") ?? string.Empty,
            Encode(manifest.Metadata.ContentType),
            Encode(manifest.AccountPartition)
        ];
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (StreamWriter writer = new(stream, Encoding.UTF8))
            {
                foreach (string line in lines)
                {
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static bool IsPayloadPath(string path) =>
        !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private string? ResolvePayloadPath(string prefix, GitHubImageCacheManifest manifest)
    {
        if (IsSafePayloadFileName(prefix, manifest.PayloadFileName))
        {
            return Path.Combine(_imageRootPath, manifest.PayloadFileName!);
        }

        string stablePath = Path.Combine(_imageRootPath, $"{prefix}.img");
        if (File.Exists(stablePath))
        {
            return stablePath;
        }

        return Directory
            .EnumerateFiles(_imageRootPath, $"{prefix}.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                IsPayloadPath(path) &&
                string.Equals(Path.GetFileNameWithoutExtension(path), prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafePayloadFileName(string prefix, string? payloadFileName) =>
        !string.IsNullOrWhiteSpace(payloadFileName) &&
        string.Equals(Path.GetFileName(payloadFileName), payloadFileName, StringComparison.Ordinal) &&
        payloadFileName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) &&
        payloadFileName.EndsWith(".img", StringComparison.OrdinalIgnoreCase);

    private static string Encode(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string? Decode(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static string? ExtractAccountPartition(string cacheKey)
    {
        int separator = cacheKey.IndexOf(':');
        return separator > 0 ? cacheKey[..separator] : null;
    }

    private static void TryDeleteStrict(string path, List<CacheClearResidual> residuals)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(path))
            {
                residuals.Add(new CacheClearResidual(path, "The file still exists after deletion."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            residuals.Add(new CacheClearResidual(
                path,
                $"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".img";
        }

        string normalized = extension.Trim();
        if (!normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = "." + normalized;
        }

        return normalized.Length > 12 ? ".img" : normalized.ToLowerInvariant();
    }

    private static string HashKey(string cacheKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
