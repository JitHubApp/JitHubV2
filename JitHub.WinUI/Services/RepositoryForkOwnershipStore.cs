using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public enum RepositoryForkOwnershipStatus
{
    Uncertain,
    Accepted
}

public sealed record RepositoryForkOwnershipState(
    string Key,
    string AccountUserId,
    long SourceRepositoryId,
    string SourceOwner,
    string SourceName,
    string TargetOwner,
    string TargetName,
    RepositoryForkOwnershipStatus Status,
    long? TargetRepositoryId,
    int ReconciliationAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IRepositoryForkOwnershipStore
{
    Task<RepositoryForkOwnershipState?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task UpsertAsync(RepositoryForkOwnershipState state, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    Task ClearAccountAsync(string accountUserId, CancellationToken cancellationToken = default);
}

public sealed partial class RepositoryForkOwnershipStore : IRepositoryForkOwnershipStore
{
    private const int CurrentVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _quarantineGuardPath;

    public RepositoryForkOwnershipStore(IAppStoragePathProvider pathProvider)
        : this(Path.Combine(
            ResolveLocalFolder(pathProvider),
            "RepositoryActions",
            "v1",
            "repository-fork-ownership.json"))
    {
    }

    internal RepositoryForkOwnershipStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _quarantineGuardPath = _path + ".quarantine-guard";
    }

    private static string ResolveLocalFolder(IAppStoragePathProvider pathProvider)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        string? versionRoot = Path.GetDirectoryName(pathProvider.StarLibraryRecoveryPath);
        DirectoryInfo? localFolder = versionRoot is null
            ? null
            : Directory.GetParent(versionRoot)?.Parent;
        return localFolder?.FullName
            ?? throw new InvalidOperationException("The local app-data root is unavailable.");
    }

    public async Task<RepositoryForkOwnershipState?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RepositoryForkOwnershipDocument document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            RepositoryForkOwnershipState? state = document.Items.FirstOrDefault(
                item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (state is not null || !document.HasConservativeQuarantine)
            {
                return state;
            }

            return RepositoryForkOwnershipKey.TryCreateConservativeState(key, out RepositoryForkOwnershipState? conservative)
                ? conservative
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(
        RepositoryForkOwnershipState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RepositoryForkOwnershipDocument document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            int index = document.Items.FindIndex(item => string.Equals(item.Key, state.Key, StringComparison.Ordinal));
            if (index >= 0)
            {
                document.Items[index] = state;
            }
            else
            {
                document.Items.Add(state);
            }

            await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RepositoryForkOwnershipDocument document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (document.Items.RemoveAll(item => string.Equals(item.Key, key, StringComparison.Ordinal)) > 0)
            {
                await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAccountAsync(
        string accountUserId,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountUserId, nameof(accountUserId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The repository fork state directory is unavailable.");
            Directory.CreateDirectory(directory);
            string fileName = Path.GetFileName(_path);
            string[] paths = Directory
                .EnumerateFiles(directory, fileName + ".quarantine-*")
                .Where(path => !string.Equals(path, _quarantineGuardPath, StringComparison.OrdinalIgnoreCase))
                .Prepend(_path)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RepositoryForkOwnershipDocument document = await ReadDocumentStrictAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                document.Items ??= [];
                if (document.Items.RemoveAll(item =>
                        string.Equals(item.AccountUserId, partition, StringComparison.Ordinal)) > 0)
                {
                    await WriteDocumentToPathAsync(path, document, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (string path in paths)
            {
                RepositoryForkOwnershipDocument document = await ReadDocumentStrictAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (document.Items.Any(item =>
                        string.Equals(item.AccountUserId, partition, StringComparison.Ordinal)))
                {
                    throw new IOException("Repository fork recovery data still contains the removed account.");
                }
            }

            if (File.Exists(_path + ".tmp"))
            {
                File.Delete(_path + ".tmp");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RepositoryForkOwnershipDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new RepositoryForkOwnershipDocument
            {
                HasConservativeQuarantine = File.Exists(_quarantineGuardPath)
            };
        }

        try
        {
            RepositoryForkOwnershipDocument? document;
            await using (FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                document = await JsonSerializer.DeserializeAsync(
                    stream,
                    RepositoryForkOwnershipJsonContext.Default.Document,
                    cancellationToken).ConfigureAwait(false);
            }

            if (document is { Version: CurrentVersion })
            {
                document.Items ??= [];
                if (document.Items.Any(item =>
                    !IsUsableState(item) ||
                    !Enum.IsDefined(item.Status)))
                {
                    return await QuarantineAsync(
                        document.Items.Where(IsUsableState),
                        "invalid-state").ConfigureAwait(false);
                }

                document.HasConservativeQuarantine = File.Exists(_quarantineGuardPath);
                return document;
            }

            return await QuarantineAsync(
                document?.Items,
                "unsupported-version").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return await QuarantineAsync(null, "malformed-json").ConfigureAwait(false);
        }
    }

    private async Task<RepositoryForkOwnershipDocument> QuarantineAsync(
        IEnumerable<RepositoryForkOwnershipState>? salvageableItems,
        string reason)
    {
        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The repository fork state directory is unavailable.");
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            _quarantineGuardPath,
            $"{DateTimeOffset.UtcNow:O}|{reason}",
            CancellationToken.None).ConfigureAwait(false);

        if (File.Exists(_path))
        {
            string quarantinePath = $"{_path}.quarantine-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            File.Move(_path, quarantinePath);
        }

        RepositoryForkOwnershipDocument recovered = new()
        {
            HasConservativeQuarantine = true,
            Items = salvageableItems?
                .Where(IsUsableState)
                .Select(NormalizeConservativeStatus)
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
                .ToList() ?? []
        };
        await WriteDocumentAsync(recovered, CancellationToken.None).ConfigureAwait(false);
        return recovered;
    }

    private static bool IsUsableState(RepositoryForkOwnershipState? state) =>
        state is not null &&
        !string.IsNullOrWhiteSpace(state.Key) &&
        !string.IsNullOrWhiteSpace(state.AccountUserId) &&
        state.SourceRepositoryId > 0 &&
        !string.IsNullOrWhiteSpace(state.SourceOwner) &&
        !string.IsNullOrWhiteSpace(state.SourceName) &&
        !string.IsNullOrWhiteSpace(state.TargetOwner);

    private static RepositoryForkOwnershipState NormalizeConservativeStatus(
        RepositoryForkOwnershipState state) =>
        Enum.IsDefined(state.Status)
            ? state
            : state with { Status = RepositoryForkOwnershipStatus.Accepted };

    private async Task WriteDocumentAsync(
        RepositoryForkOwnershipDocument document,
        CancellationToken cancellationToken) =>
        await WriteDocumentToPathAsync(_path, document, cancellationToken).ConfigureAwait(false);

    private static async Task<RepositoryForkOwnershipDocument> ReadDocumentStrictAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync(
                stream,
                RepositoryForkOwnershipJsonContext.Default.Document,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The repository fork recovery document is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Repository fork recovery data could not be classified safely for account removal.",
                exception);
        }
    }

    private static async Task WriteDocumentToPathAsync(
        string path,
        RepositoryForkOwnershipDocument document,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The repository fork state directory is unavailable.");
        Directory.CreateDirectory(directory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    RepositoryForkOwnershipJsonContext.Default.Document,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class RepositoryForkOwnershipDocument
    {
        public int Version { get; set; } = CurrentVersion;

        public List<RepositoryForkOwnershipState> Items { get; set; } = [];

        [JsonIgnore]
        public bool HasConservativeQuarantine { get; set; }
    }

    [JsonSerializable(typeof(RepositoryForkOwnershipDocument), TypeInfoPropertyName = "Document")]
    private sealed partial class RepositoryForkOwnershipJsonContext : JsonSerializerContext
    {
    }
}

public static class RepositoryForkOwnershipKey
{
    public static string Create(
        string accountUserId,
        long sourceRepositoryId,
        string sourceOwner,
        string sourceName,
        string targetOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetOwner);
        return string.Join(
            '|',
            accountUserId.Trim().ToLowerInvariant(),
            sourceRepositoryId,
            sourceOwner.Trim().ToLowerInvariant(),
            sourceName.Trim().ToLowerInvariant(),
            targetOwner.Trim().ToLowerInvariant());
    }

    internal static bool TryCreateConservativeState(
        string key,
        out RepositoryForkOwnershipState? state)
    {
        string[] parts = key.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 5 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            !long.TryParse(parts[1], out long sourceRepositoryId) ||
            sourceRepositoryId <= 0 ||
            string.IsNullOrWhiteSpace(parts[2]) ||
            string.IsNullOrWhiteSpace(parts[3]) ||
            string.IsNullOrWhiteSpace(parts[4]))
        {
            state = null;
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        state = new RepositoryForkOwnershipState(
            key,
            parts[0],
            sourceRepositoryId,
            parts[2],
            parts[3],
            parts[4],
            parts[3],
            RepositoryForkOwnershipStatus.Accepted,
            TargetRepositoryId: null,
            ReconciliationAttempts: 0,
            CreatedAt: now,
            UpdatedAt: now);
        return true;
    }
}
