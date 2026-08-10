using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed class SettingsDiagnosticsService : ISettingsDiagnosticsService
{
    private readonly IAppStoragePathProvider _pathProvider;
    private readonly ILocalDiagnosticsStore _diagnosticsStore;
    private readonly IStoreTelemetrySink _storeTelemetrySink;
    private readonly ISettingService _settingService;
    private readonly ICacheRegistry _cacheRegistry;

    public SettingsDiagnosticsService(
        IAppStoragePathProvider pathProvider,
        ILocalDiagnosticsStore diagnosticsStore,
        IStoreTelemetrySink storeTelemetrySink,
        ISettingService settingService,
        ICacheRegistry cacheRegistry)
    {
        _pathProvider = pathProvider;
        _diagnosticsStore = diagnosticsStore;
        _storeTelemetrySink = storeTelemetrySink;
        _settingService = settingService;
        _cacheRegistry = cacheRegistry;
    }

    public async Task<SettingsDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CacheOwnerSnapshot> cacheOwners = await _cacheRegistry.GetSnapshotAsync(cancellationToken);
        CacheOwnerSnapshot? query = FindOwner(cacheOwners, CacheOwnerIds.GitHubQuery);
        CacheOwnerSnapshot? images = FindOwner(cacheOwners, CacheOwnerIds.GitHubImages);
        CacheOwnerSnapshot? repoFiles = cacheOwners.FirstOrDefault(
            static owner => string.Equals(owner.Id, CacheOwnerIds.RepositoryFiles, StringComparison.Ordinal));
        CacheOwnerSnapshot? stars = FindOwner(cacheOwners, CacheOwnerIds.StarsLibrary);
        (long diagnosticsBytes, bool diagnosticsAvailable, string? diagnosticsError) =
            await CaptureDiagnosticsSizeAsync(cancellationToken).ConfigureAwait(false);

        bool diagnosticsEnabled = GetEnabledSetting(SettingsKeys.DiagnosticsEnabled, defaultValue: true);
        bool storeTelemetryEnabled = _storeTelemetrySink.IsAvailable &&
            GetEnabledSetting(SettingsKeys.StoreTelemetryEnabled, defaultValue: true);

        return new SettingsDiagnosticsSnapshot(
            new CacheStorageSummary(
                _pathProvider.CacheDatabasePath,
                _pathProvider.PayloadRootPath,
                _pathProvider.ImageRootPath,
                GetMetric(query, CacheMetricKeys.DatabasePhysicalBytes),
                GetMetric(query, CacheMetricKeys.PayloadDirectoryPhysicalBytes),
                images?.Bytes ?? 0,
                checked((int)GetMetric(query, CacheMetricKeys.SchemaVersion)),
                repoFiles?.Bytes ?? 0),
            new DiagnosticsStorageSummary(
                _pathProvider.DiagnosticsPath,
                diagnosticsBytes,
                diagnosticsAvailable,
                diagnosticsError),
            new StoreTelemetrySummary(
                GetStoreTelemetryStatus(),
                _storeTelemetrySink.IsAvailable,
                storeTelemetryEnabled,
                IsDisabledByCompatibility()),
            diagnosticsEnabled,
            storeTelemetryEnabled,
            new StarLibraryStorageSummary(
                stars?.Paths.FirstOrDefault() ?? _pathProvider.StarLibraryDatabasePath,
                stars?.Paths.Skip(1).FirstOrDefault() ?? _pathProvider.StarLibraryRecoveryPath,
                GetMetric(stars, CacheMetricKeys.DatabasePhysicalBytes),
                GetMetric(stars, CacheMetricKeys.RecoveryJournalPhysicalBytes)),
            repoFiles is null
                ? null
                : new RepoFileCacheStorageSummary(repoFiles.Paths.FirstOrDefault() ?? string.Empty, repoFiles.Bytes),
            cacheOwners);
    }

    public Task ClearDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        _diagnosticsStore.ClearAsync(cancellationToken);

    public Task ClearQueryCacheAsync(CancellationToken cancellationToken = default) =>
        _cacheRegistry.ClearAsync(CacheOwnerIds.GitHubQuery, cancellationToken);

    public Task ClearImageCacheAsync(CancellationToken cancellationToken = default) =>
        _cacheRegistry.ClearAsync(CacheOwnerIds.GitHubImages, cancellationToken);

    public Task ClearRepoFileCacheAsync(CancellationToken cancellationToken = default) =>
        _cacheRegistry.ClearAsync(CacheOwnerIds.RepositoryFiles, cancellationToken);

    public async Task ClearAllCacheAsync(CancellationToken cancellationToken = default)
    {
        await _cacheRegistry.ClearEvictableAsync(cancellationToken);
    }

    public Task ClearStarLibraryAsync(CancellationToken cancellationToken = default) =>
        _cacheRegistry.ClearAsync(CacheOwnerIds.StarsLibrary, cancellationToken);

    public async Task ExportDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var events = await _diagnosticsStore.ReadAsync(cancellationToken);
        await using FileStream stream = new(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        await using StreamWriter writer = new(stream);

        foreach (LocalDiagnosticEvent diagnosticEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(JsonSerializer.Serialize(diagnosticEvent));
        }
    }

    private bool GetEnabledSetting(string key, bool defaultValue) =>
        _settingService.Contains(key) ? _settingService.Get<bool>(key) : defaultValue;

    private async Task<(long Bytes, bool IsAvailable, string? Error)> CaptureDiagnosticsSizeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return (await _diagnosticsStore.GetSizeAsync(cancellationToken).ConfigureAwait(false), true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (0, false, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static CacheOwnerSnapshot? FindOwner(
        IReadOnlyList<CacheOwnerSnapshot> owners,
        string ownerId) =>
        owners.FirstOrDefault(owner => string.Equals(owner.Id, ownerId, StringComparison.Ordinal));

    private static long GetMetric(CacheOwnerSnapshot? owner, string metric) =>
        owner?.Components is not null && owner.Components.TryGetValue(metric, out long value)
            ? value
            : 0;

    private string GetStoreTelemetryStatus()
    {
        if (_storeTelemetrySink.IsAvailable)
        {
            return "Available";
        }

        return IsDisabledByCompatibility()
            ? "Disabled by compatibility"
            : "Unavailable";
    }

    private bool IsDisabledByCompatibility() =>
        _storeTelemetrySink.AvailabilityStatus.Contains("type_unavailable", StringComparison.OrdinalIgnoreCase) ||
        _storeTelemetrySink.AvailabilityStatus.Contains("logger_unavailable", StringComparison.OrdinalIgnoreCase) ||
        _storeTelemetrySink.AvailabilityStatus.Contains("compat", StringComparison.OrdinalIgnoreCase);
}
