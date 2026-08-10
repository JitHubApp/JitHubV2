using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;

namespace JitHub.Services;

public static class CacheOwnerIds
{
    public const string GitHubQuery = "github-query";
    public const string GitHubImages = "github-images";
    public const string RepositoryFiles = "repository-files";
    public const string StarsLibrary = "stars-library";
}

public sealed record CacheOwnerSnapshot(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Paths,
    long Bytes,
    long? SoftCapBytes,
    string TtlPolicy,
    string AccountPartition,
    string ClearSemantics,
    bool IsDurableUserData,
    CacheOwnerHealth Health,
    string? HealthDetail = null,
    IReadOnlyList<CacheOwnerCap>? Caps = null,
    long LogicalBytes = 0,
    long OrphanBytes = 0,
    IReadOnlyDictionary<string, long>? Components = null);

public interface ICacheRegistry
{
    Task<IReadOnlyList<CacheOwnerSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(string ownerId, CancellationToken cancellationToken = default);

    Task ClearEvictableAsync(CancellationToken cancellationToken = default);
}

public sealed class CacheRegistry : ICacheRegistry
{
    private readonly IAppStoragePathProvider _paths;
    private readonly IGitHubCacheStore _queryCache;
    private readonly IGitHubImageCacheStore _imageCache;
    private readonly IRepoFileCacheService _repoFileCache;
    private readonly IRepoTreeService? _repoTreeCache;
    private readonly IStarLibraryStore _starLibrary;
    private readonly IStarLibraryRecoveryStore _starRecovery;
    private readonly GitHubCachePolicy _policy = GitHubCachePolicy.Default;

    public CacheRegistry(
        IAppStoragePathProvider paths,
        IGitHubCacheStore queryCache,
        IGitHubImageCacheStore imageCache,
        IRepoFileCacheService repoFileCache,
        IStarLibraryStore starLibrary,
        IStarLibraryRecoveryStore starRecovery,
        IRepoTreeService? repoTreeCache = null)
    {
        _paths = paths;
        _queryCache = queryCache;
        _imageCache = imageCache;
        _repoFileCache = repoFileCache;
        _repoTreeCache = repoTreeCache;
        _starLibrary = starLibrary;
        _starRecovery = starRecovery;
    }

    public async Task<IReadOnlyList<CacheOwnerSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Task<CacheOwnerSnapshot> query = CaptureAsync(
            CacheOwnerIds.GitHubQuery,
            "GitHub query cache",
            [_paths.CacheDatabasePath, _paths.PayloadRootPath],
            softCapBytes: null,
            _policy.DescribeQueryTtlPolicy(),
            "Authenticated GitHub user ID",
            "Cleared with GitHub query cache or Clear all cache data",
            isDurableUserData: false,
            [
                new CacheOwnerCap("SQLite metadata and inline payloads", _policy.MetadataSoftCapBytes),
                new CacheOwnerCap("Logical JSON/blob/diff payloads", _policy.PayloadSoftCapBytes)
            ],
            _queryCache.InspectAsync,
            cancellationToken);

        Task<CacheOwnerSnapshot> images = CaptureAsync(
            CacheOwnerIds.GitHubImages,
            "Avatar and image cache",
            [_paths.ImageRootPath],
            _policy.AvatarImageSoftCapBytes,
            GitHubCachePolicy.FormatDuration(GitHubCachePolicy.TtlForResource(GitHubCachePolicy.AvatarImageResource)),
            "Authenticated GitHub user ID or public partition, plus canonical HTTPS image identity",
            "Cleared with image cache or Clear all cache data",
            isDurableUserData: false,
            [new CacheOwnerCap("Physical image payloads", _policy.AvatarImageSoftCapBytes)],
            _imageCache.InspectAsync,
            cancellationToken);

        Task<CacheOwnerSnapshot> repoFiles = CaptureAsync(
            CacheOwnerIds.RepositoryFiles,
            "Repository file cache",
            [_repoFileCache.RootPath],
            _repoFileCache.DiskSoftCapBytes,
            FormatTtl(_repoFileCache.Ttl),
            "Authenticated GitHub user ID or public unauthenticated partition, repository owner/name, and immutable blob SHA",
            "Cleared with repository file cache or Clear all cache data",
            isDurableUserData: false,
            [new CacheOwnerCap("Physical repository file payloads", _repoFileCache.DiskSoftCapBytes)],
            _repoFileCache.InspectAsync,
            cancellationToken);

        Task<CacheOwnerSnapshot> stars = CaptureAsync(
            CacheOwnerIds.StarsLibrary,
            "Stars library and categories",
            [_starLibrary.DatabasePath, _starRecovery.FilePath],
            softCapBytes: null,
            "Durable until remote reconciliation or explicit deletion",
            "Authenticated GitHub user ID",
            "Separate destructive confirmation; never included in Clear all cache data",
            isDurableUserData: true,
            [],
            InspectStarsAsync,
            cancellationToken);

        return await Task.WhenAll(query, images, repoFiles, stars).ConfigureAwait(false);
    }

    public Task ClearAsync(string ownerId, CancellationToken cancellationToken = default) =>
        ownerId switch
        {
            CacheOwnerIds.GitHubQuery => ClearQueryCacheAsync(cancellationToken),
            CacheOwnerIds.GitHubImages => _imageCache.ClearAllAsync(cancellationToken),
            CacheOwnerIds.RepositoryFiles => _repoFileCache.ClearAllAsync(cancellationToken),
            CacheOwnerIds.StarsLibrary => ClearStarsAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(ownerId), ownerId, "Unknown cache owner.")
        };

    private async Task ClearQueryCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _queryCache.ClearAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_repoTreeCache is not null)
            {
                await _repoTreeCache.ClearMemoryCacheAsync(
                    accountPartition: null,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task ClearEvictableAsync(CancellationToken cancellationToken = default)
    {
        string[] ownerIds =
        [
            CacheOwnerIds.GitHubQuery,
            CacheOwnerIds.GitHubImages,
            CacheOwnerIds.RepositoryFiles
        ];
        List<CacheClearFailure> failures = [];
        foreach (string ownerId in ownerIds)
        {
            try
            {
                await ClearAsync(ownerId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new CacheClearFailure(ownerId, exception.GetType().Name, exception.Message));
            }
        }

        if (failures.Count > 0)
        {
            throw new CacheClearException(failures);
        }
    }

    private static async Task<CacheOwnerSnapshot> CaptureAsync(
        string id,
        string displayName,
        IReadOnlyList<string> paths,
        long? softCapBytes,
        string ttlPolicy,
        string accountPartition,
        string clearSemantics,
        bool isDurableUserData,
        IReadOnlyList<CacheOwnerCap> caps,
        Func<CancellationToken, Task<CacheStoreInspection>> inspectAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            CacheStoreInspection inspection = await Task.Run(
                    () => inspectAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return new CacheOwnerSnapshot(
                id,
                displayName,
                paths,
                inspection.PhysicalBytes,
                softCapBytes,
                ttlPolicy,
                accountPartition,
                clearSemantics,
                isDurableUserData,
                inspection.Health,
                inspection.Detail,
                caps,
                inspection.LogicalBytes,
                inspection.OrphanBytes,
                inspection.Components);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CacheOwnerSnapshot(
                id,
                displayName,
                paths,
                0,
                softCapBytes,
                ttlPolicy,
                accountPartition,
                clearSemantics,
                isDurableUserData,
                CacheOwnerHealth.Unavailable,
                $"{exception.GetType().Name}: {exception.Message}",
                caps);
        }
    }

    private static string FormatTtl(TimeSpan ttl) =>
        GitHubCachePolicy.FormatDuration(ttl);

    private async Task<CacheStoreInspection> InspectStarsAsync(CancellationToken cancellationToken)
    {
        await StarLibraryClearCoordinator.RecoverAsync(_starLibrary, _starRecovery, cancellationToken)
            .ConfigureAwait(false);
        CacheStoreInspection[] inspections = await Task.WhenAll(
                _starLibrary.InspectAsync(cancellationToken),
                _starRecovery.InspectAsync(cancellationToken))
            .ConfigureAwait(false);
        Dictionary<string, long> components = new(StringComparer.Ordinal);
        foreach (CacheStoreInspection inspection in inspections)
        {
            foreach ((string key, long value) in inspection.Components)
            {
                components[key] = components.TryGetValue(key, out long existing)
                    ? existing + value
                    : value;
            }
        }

        return new CacheStoreInspection(
            inspections.Max(static inspection => inspection.Health),
            inspections.Sum(static inspection => inspection.PhysicalBytes),
            inspections.Sum(static inspection => inspection.LogicalBytes),
            inspections.Sum(static inspection => inspection.OrphanBytes),
            components,
            CacheInspectionDetail.Format(inspections.Select(static inspection => inspection.Detail ?? string.Empty)));
    }

    private async Task ClearStarsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StarLibraryClearCoordinator.ClearAsync(_starLibrary, _starRecovery, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StarLibraryClearCoordinationException exception)
        {
            throw new CacheClearException(
                [new CacheClearFailure(exception.Component, exception.GetType().Name, exception.Message)]);
        }
        catch (Exception exception)
        {
            throw new CacheClearException(
                [new CacheClearFailure(CacheOwnerIds.StarsLibrary, exception.GetType().Name, exception.Message)]);
        }
    }
}
