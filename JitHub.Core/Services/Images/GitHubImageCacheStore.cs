using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;

namespace JitHub.Services;

public sealed class GitHubImageCacheStore : IGitHubImageCacheStore
{
    private readonly string _imageRootPath;
    private readonly GitHubCachePolicy _policy;
    private readonly GitHubImageCacheGates _gates = new();
    private readonly GitHubImageCacheManifestFile _manifests;
    private readonly GitHubImageCachePayloadEstimate _estimate;
    private readonly GitHubImageCacheInspector _inspector;
    private readonly GitHubImageCacheEviction _eviction;

    internal event Action? CacheContentInvalidated;

    public GitHubImageCacheStore(IAppStoragePathProvider pathProvider)
        : this(pathProvider.ImageRootPath, GitHubCachePolicy.Default)
    {
    }

    internal GitHubImageCacheStore(string imageRootPath, GitHubCachePolicy policy)
    {
        _imageRootPath = imageRootPath;
        _policy = policy;
        _manifests = new GitHubImageCacheManifestFile(imageRootPath);
        _estimate = new GitHubImageCachePayloadEstimate(imageRootPath);
        _inspector = new GitHubImageCacheInspector(imageRootPath, _manifests);
        _eviction = new GitHubImageCacheEviction(imageRootPath, policy, _manifests);
        Directory.CreateDirectory(_imageRootPath);
    }

    public async Task<GitHubImageCacheEntry?> TryGetAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim gate = _gates.ForKey(cacheKey);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await TryGetCoreAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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

        _ = GitHubImageCacheFiles.NormalizeExtension(extension);
        string prefix = GitHubImageCacheFiles.HashKey(cacheKey);
        GitHubImageCacheEntry stored;
        bool enforceCap;

        SemaphoreSlim gate = _gates.ForPrefix(prefix);
        await gate.WaitAsync(cancellationToken);
        try
        {
            _ = _estimate.GetOrInitialize();
            string filePath = await WriteGenerationAsync(cacheKey, prefix, bytes, metadata, cancellationToken)
                .ConfigureAwait(false);
            stored = CreateEntry(cacheKey, new FileInfo(filePath), metadata);
            enforceCap = _estimate.Current > _policy.AvatarImageSoftCapBytes;
        }
        finally
        {
            gate.Release();
        }

        // Cap enforcement needs an exclusive cache view. Run it only after releasing the
        // key stripe so unrelated downloads can commit concurrently and the exclusive
        // operation cannot deadlock while acquiring every stripe.
        if (enforceCap)
        {
            await EnforceCapAsync(cancellationToken).ConfigureAwait(false);
        }

        return stored;
    }

    /// <summary>
    /// Writes a new immutable payload generation and commits it through the
    /// manifest. The caller holds the key's stripe gate. Returns the payload path.
    /// </summary>
    private async Task<string> WriteGenerationAsync(
        string cacheKey,
        string prefix,
        byte[] bytes,
        GitHubImageCacheWriteMetadata metadata,
        CancellationToken cancellationToken)
    {
        string payloadFileName = $"{prefix}.{Guid.NewGuid():N}.img";
        string filePath = Path.Combine(_imageRootPath, payloadFileName);
        string temporaryPath = filePath + ".tmp";
        bool manifestCommitted = false;
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath);
            DateTime now = DateTime.UtcNow;
            File.SetCreationTimeUtc(filePath, now);
            File.SetLastWriteTimeUtc(filePath, now);
            File.SetLastAccessTimeUtc(filePath, now);

            // The manifest is the atomic commit point. Until its replace succeeds, readers
            // continue to resolve the prior generation and its matching validators.
            await _manifests.WriteAtomicallyAsync(
                    prefix,
                    new GitHubImageCacheManifest(
                        payloadFileName,
                        metadata,
                        GitHubImageCacheFiles.ExtractAccountPartition(cacheKey)),
                    cancellationToken)
                .ConfigureAwait(false);
            manifestCommitted = true;
            _estimate.Add(bytes.LongLength);
            return filePath;
        }
        finally
        {
            GitHubImageCacheFiles.TryDelete(temporaryPath);
            if (!manifestCommitted)
            {
                GitHubImageCacheFiles.TryDelete(filePath);
            }
        }
    }

    public async Task MarkFreshAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = _gates.ForKey(cacheKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string prefix = GitHubImageCacheFiles.HashKey(cacheKey);
            GitHubImageCacheManifest manifest = await _manifests.ReadAsync(prefix, cancellationToken)
                .ConfigureAwait(false);
            string? filePath = _manifests.ResolvePayloadPath(prefix, manifest);
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
            gate.Release();
        }
    }

    public async Task<GitHubImageCacheRead?> TryReadAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim gate = _gates.ForKey(cacheKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            gate.Release();
        }
    }

    public async Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = _gates.ForKey(cacheKey);
        await gate.WaitAsync(cancellationToken);
        try
        {
            string prefix = GitHubImageCacheFiles.HashKey(cacheKey);
            long removedPayloadBytes = 0;
            foreach (string existing in Directory.EnumerateFiles(_imageRootPath, $"{prefix}.*"))
            {
                if (GitHubImageCacheFiles.IsPayloadPath(existing))
                {
                    removedPayloadBytes += GitHubImageCacheFiles.TryGetLength(existing);
                }

                GitHubImageCacheFiles.TryDelete(existing);
            }

            _estimate.Add(-removedPayloadBytes);
            CacheContentInvalidated?.Invoke();
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<AdmittedGitHubImageCacheRead?> TryReadAdmittedAsync(
        string cacheKey,
        IMarkdownImageSourceByteAdmission admission,
        long maximumImageBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Never wait for a byte grant while holding the cache stripe gate:
            // an active writer may need that gate before it can release its own
            // source-byte lease. Immutable payload generations let us recheck
            // the selected path and exact length after the grant arrives.
            GitHubImageCacheEntry? expected = await TryGetAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
            if (expected is null || expected.ByteLength <= 0 || expected.ByteLength > maximumImageBytes)
                return null;

            IDisposable lease = await admission.ReserveAsync(expected.ByteLength, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                GitHubImageCacheRead? read = await TryReadMatchingAsync(
                    cacheKey, expected, cancellationToken).ConfigureAwait(false);
                if (read is not null)
                    return new AdmittedGitHubImageCacheRead(read, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            lease.Dispose();
        }
    }

    private async Task<GitHubImageCacheRead?> TryReadMatchingAsync(
        string cacheKey,
        GitHubImageCacheEntry expected,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _gates.ForKey(cacheKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GitHubImageCacheEntry? current = await TryGetCoreAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
            if (current is null ||
                current.ByteLength != expected.ByteLength ||
                !string.Equals(current.FilePath, expected.FilePath, StringComparison.Ordinal))
                return null;

            try
            {
                await using FileStream stream = new(
                    current.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, FileOptions.Asynchronous);
                if (stream.Length != expected.ByteLength)
                    return null;

                byte[] bytes = new byte[checked((int)expected.ByteLength)];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return new GitHubImageCacheRead(current, bytes);
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
            gate.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        int acquiredGates = await _gates.AcquireAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_imageRootPath))
            {
                return;
            }

            List<CacheClearResidual> residuals = _eviction.DeleteAll(cancellationToken);
            if (residuals.Count > 0)
            {
                _estimate.Invalidate();
                throw new CacheClearPostconditionException(CacheOwnerIds.GitHubImages, residuals);
            }

            _estimate.Set(0);
            CacheContentInvalidated?.Invoke();
        }
        finally
        {
            _gates.Release(acquiredGates);
        }
    }

    public async Task EnforceCapAsync(CancellationToken cancellationToken = default)
    {
        int acquiredGates = await _gates.AcquireAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_estimate.GetOrInitialize() > _policy.AvatarImageSoftCapBytes)
            {
                await _eviction.EnforceSoftCapAsync(cancellationToken).ConfigureAwait(false);
                _estimate.Set(GitHubImageCacheFiles.CalculatePayloadBytes(_imageRootPath));
            }
        }
        finally
        {
            _gates.Release(acquiredGates);
        }
    }

    public async Task<long> GetTotalBytesAsync(CancellationToken cancellationToken = default)
    {
        int acquiredGates = await _gates.AcquireAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long total = GitHubImageCacheFiles.CalculatePayloadBytes(_imageRootPath);
            _estimate.Set(total);
            return total;
        }
        finally
        {
            _gates.Release(acquiredGates);
        }
    }

    public async Task ClearPartitionAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition);
        int acquiredGates = await _gates.AcquireAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_imageRootPath))
            {
                return;
            }

            List<CacheClearResidual> residuals = await _eviction.DeletePartitionAsync(partition, cancellationToken)
                .ConfigureAwait(false);
            _estimate.Invalidate();
            if (residuals.Count > 0)
            {
                throw new CacheClearPostconditionException(CacheOwnerIds.GitHubImages, residuals);
            }

            CacheContentInvalidated?.Invoke();
        }
        finally
        {
            _gates.Release(acquiredGates);
        }
    }

    public async Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        int acquiredGates = await _gates.AcquireAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _inspector.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gates.Release(acquiredGates);
        }
    }

    private async Task<GitHubImageCacheEntry?> TryGetCoreAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        string prefix = GitHubImageCacheFiles.HashKey(cacheKey);
        GitHubImageCacheManifest manifest = await _manifests.ReadAsync(prefix, cancellationToken)
            .ConfigureAwait(false);
        string? filePath = _manifests.ResolvePayloadPath(prefix, manifest);
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
}
