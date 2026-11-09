using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

/// <summary>
/// Removes image cache files for clear, account-partition removal and soft-cap
/// enforcement. Every operation needs an exclusive cache view: the caller holds
/// all store gates and owns the payload estimate and invalidation event.
/// </summary>
internal sealed class GitHubImageCacheEviction
{
    private readonly string _imageRootPath;
    private readonly GitHubCachePolicy _policy;
    private readonly GitHubImageCacheManifestFile _manifests;

    internal GitHubImageCacheEviction(
        string imageRootPath,
        GitHubCachePolicy policy,
        GitHubImageCacheManifestFile manifests)
    {
        _imageRootPath = imageRootPath;
        _policy = policy;
        _manifests = manifests;
    }

    /// <summary>
    /// Deletes every cache file and returns what still exists afterwards.
    /// Throws <see cref="CacheClearPostconditionException"/> if the directory cannot be listed.
    /// </summary>
    internal List<CacheClearResidual> DeleteAll(CancellationToken cancellationToken)
    {
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
                (CacheClearResidual[])[new CacheClearResidual(_imageRootPath, $"{exception.GetType().Name}: {exception.Message}")]);
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

        AddRemainingFiles(residuals);
        return residuals;
    }

    /// <summary>Deletes the entries whose manifest names the partition and returns what remains.</summary>
    internal async Task<List<CacheClearResidual>> DeletePartitionAsync(
        string partition,
        CancellationToken cancellationToken)
    {
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
                manifest = await GitHubImageCacheManifestFile.ReadStrictAsync(manifestPath, cancellationToken)
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
                GitHubImageCacheFiles.TryDeleteStrict(path, residuals);
            }
        }

        return residuals;
    }

    /// <summary>
    /// Brings the payload bytes under the soft cap: orphaned generations go first,
    /// then the least recently accessed active entries, always keeping one.
    /// </summary>
    internal async Task EnforceSoftCapAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, string> manifestByActivePayload =
            await MapActivePayloadsAsync(cancellationToken).ConfigureAwait(false);

        List<FileInfo> files = Directory
            .EnumerateFiles(_imageRootPath)
            .Where(static path => GitHubImageCacheFiles.IsPayloadPath(path))
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
            GitHubImageCacheFiles.TryDelete(file.FullName);
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
            GitHubImageCacheFiles.TryDelete(file.FullName);
            GitHubImageCacheFiles.TryDelete(manifestPath);
            activeEntryCount--;
            await Task.Yield();
        }
    }

    /// <summary>Maps each active payload to its manifest and deletes manifests whose payload is gone.</summary>
    private async Task<Dictionary<string, string>> MapActivePayloadsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> manifestByActivePayload = new(StringComparer.OrdinalIgnoreCase);
        foreach (string manifestPath in Directory.EnumerateFiles(_imageRootPath, "*.meta", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prefix = Path.GetFileNameWithoutExtension(manifestPath);
            GitHubImageCacheManifest manifest = await _manifests.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
            string? payloadPath = _manifests.ResolvePayloadPath(prefix, manifest);
            if (payloadPath is not null && File.Exists(payloadPath))
            {
                manifestByActivePayload[payloadPath] = manifestPath;
            }
            else
            {
                GitHubImageCacheFiles.TryDelete(manifestPath);
            }
        }

        return manifestByActivePayload;
    }

    private void AddRemainingFiles(List<CacheClearResidual> residuals)
    {
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
    }
}
