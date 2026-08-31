using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public interface ISettingsDiagnosticsService
{
    Task<SettingsDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task ClearDiagnosticsAsync(CancellationToken cancellationToken = default);

    Task ClearQueryCacheAsync(CancellationToken cancellationToken = default);

    Task ClearImageCacheAsync(CancellationToken cancellationToken = default);

    Task ClearRepoFileCacheAsync(CancellationToken cancellationToken = default);

    Task ClearAllCacheAsync(CancellationToken cancellationToken = default);

    Task ClearStarLibraryAsync(CancellationToken cancellationToken = default);

    Task ExportDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default);
}

public sealed record SettingsDiagnosticsSnapshot(
    CacheStorageSummary Cache,
    DiagnosticsStorageSummary Diagnostics,
    StoreTelemetrySummary StoreTelemetry,
    bool DiagnosticsEnabled,
    bool StoreTelemetryEnabled,
    StarLibraryStorageSummary? StarLibrary = null,
    RepoFileCacheStorageSummary? RepoFiles = null,
    IReadOnlyList<CacheOwnerSnapshot>? CacheOwners = null);

public sealed record StarLibraryStorageSummary(
    string DatabasePath,
    string RecoveryJournalPath,
    long DatabaseBytes,
    long RecoveryJournalBytes)
{
    public long Bytes => DatabaseBytes + RecoveryJournalBytes;
}

public sealed record RepoFileCacheStorageSummary(string RootPath, long Bytes);

public sealed record CacheStorageSummary(
    string DatabasePath,
    string PayloadPath,
    string ImagePath,
    long MetadataBytes,
    long PayloadBytes,
    long ImageBytes,
    int SchemaVersion,
    long RepositoryFileBytes = 0)
{
    public long TotalBytes => MetadataBytes + PayloadBytes + ImageBytes + RepositoryFileBytes;
}

public sealed record DiagnosticsStorageSummary(
    string Path,
    long Bytes,
    bool IsAvailable = true,
    string? Error = null);

public sealed record StoreTelemetrySummary(
    string Status,
    bool IsAvailable,
    bool IsEnabled,
    bool IsDisabledByCompatibility);
