using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed partial class StarLibraryRecoveryStore : IStarLibraryRecoveryStore
{
    private const string ClearManifestSuffix = ".clear-transaction.json";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StarLibraryRecoveryStore(IAppStoragePathProvider pathProvider)
        : this(pathProvider.StarLibraryRecoveryPath)
    {
    }

    public StarLibraryRecoveryStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    }

    public string FilePath { get; }

    private string ClearManifestPath => FilePath + ClearManifestSuffix;

    public async Task EnqueueAsync(StarLibraryRecoveryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<StarLibraryRecoveryEntry> entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            entries.RemoveAll(candidate =>
                string.Equals(candidate.UserId, entry.UserId, StringComparison.Ordinal) &&
                string.Equals(candidate.FullName, entry.FullName, StringComparison.OrdinalIgnoreCase));
            entries.Add(entry);
            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StarLibraryRecoveryEntry>> ReadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(entry => string.Equals(entry.UserId, userId, StringComparison.Ordinal))
                .OrderBy(entry => entry.CreatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string entryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<StarLibraryRecoveryEntry> entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (entries.RemoveAll(entry => string.Equals(entry.Id, entryId, StringComparison.Ordinal)) > 0)
            {
                await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using IStarLibraryRecoveryClearTransaction transaction =
            await BeginClearAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(userId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<StarLibraryRecoveryEntry> entries = await ReadCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            if (entries.RemoveAll(entry =>
                    string.Equals(entry.UserId, partition, StringComparison.Ordinal)) > 0)
            {
                await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            }

            List<StarLibraryRecoveryEntry> persisted = await ReadCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            if (persisted.Any(entry =>
                    string.Equals(entry.UserId, partition, StringComparison.Ordinal)))
            {
                throw new IOException("The Stars recovery journal still contains the cleared account partition.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IStarLibraryRecoveryClearTransaction> BeginClearAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ClearManifest? manifest = null;
        bool ownershipTransferred = false;
        try
        {
            if (await ReadClearManifestCoreAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException(
                    "An interrupted Stars clear transaction must be recovered before another clear can begin.");
            }

            DeleteSidecarsStrict();
            cancellationToken.ThrowIfCancellationRequested();

            string transactionId = Guid.NewGuid().ToString("N");
            string backupFileName = $"{Path.GetFileName(FilePath)}.clear-{transactionId}.backup";
            string backupPath = Path.Combine(Path.GetDirectoryName(FilePath)!, backupFileName);
            bool hadJournal = File.Exists(FilePath);
            byte[] journalBytes = hadJournal
                ? await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false)
                : [];

            await WriteBytesAtomicAsync(backupPath, journalBytes, cancellationToken).ConfigureAwait(false);
            manifest = new ClearManifest(transactionId, backupFileName, hadJournal);
            await WriteBytesAtomicAsync(
                    ClearManifestPath,
                    JsonSerializer.SerializeToUtf8Bytes(
                        manifest,
                        StarLibraryRecoveryJsonContext.Default.ClearManifest),
                    cancellationToken)
                .ConfigureAwait(false);

            // The durable backup and manifest exist before the active journal becomes empty.
            // A crash from this point is recoverable using SQLite's matching commit marker.
            await WriteCoreAsync([], cancellationToken).ConfigureAwait(false);
            ownershipTransferred = true;
            return new RecoveryClearTransaction(this, manifest);
        }
        catch (Exception stageException)
        {
            if (manifest is not null)
            {
                try
                {
                    await RollbackClearCoreAsync(manifest, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(stageException, rollbackException);
                }
            }

            throw;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                _gate.Release();
            }
        }
    }

    public async Task<StarLibraryClearRecoveryState?> GetPendingClearAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearManifest? manifest = await ReadClearManifestCoreAsync(cancellationToken).ConfigureAwait(false);
            return manifest is null ? null : new StarLibraryClearRecoveryState(manifest.TransactionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CommitPendingClearAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearManifest manifest = await GetRequiredClearManifestCoreAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
            await CommitClearCoreAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackPendingClearAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearManifest manifest = await GetRequiredClearManifestCoreAsync(transactionId, cancellationToken)
                .ConfigureAwait(false);
            await RollbackClearCoreAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryGetLength(FilePath) + EnumerateSidecars().Sum(TryGetLength);
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
            cancellationToken.ThrowIfCancellationRequested();
            string[] sidecars = EnumerateSidecars();
            long orphanBytes = sidecars.Sum(TryGetLength);
            long journalBytes = TryGetLength(FilePath);
            long physicalBytes = journalBytes + orphanBytes;
            if (!File.Exists(FilePath))
            {
                return new CacheStoreInspection(
                    orphanBytes == 0 ? CacheOwnerHealth.Healthy : CacheOwnerHealth.Degraded,
                    PhysicalBytes: physicalBytes,
                    LogicalBytes: 0,
                    OrphanBytes: orphanBytes,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.RecoveryJournalPhysicalBytes] = physicalBytes,
                        [CacheMetricKeys.RecoveryEntryCount] = 0
                    },
                    orphanBytes == 0
                        ? "The Stars recovery journal has not been created yet."
                        : "Recovery sidecar generations exist without an active journal.");
            }

            try
            {
                await using FileStream stream = new(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                List<StarLibraryRecoveryEntry> entries =
                    await JsonSerializer.DeserializeAsync(
                        stream,
                        StarLibraryRecoveryJsonContext.Default.RecoveryEntries,
                        cancellationToken).ConfigureAwait(false) ?? [];
                List<string> problems = [];
                if (orphanBytes > 0)
                {
                    problems.Add("Recovery sidecar generations are awaiting cleanup.");
                }
                bool hasIncompleteEntry = entries.Any(static entry =>
                        string.IsNullOrWhiteSpace(entry.Id) ||
                        string.IsNullOrWhiteSpace(entry.UserId) ||
                        string.IsNullOrWhiteSpace(entry.FullName));
                if (hasIncompleteEntry)
                {
                    problems.Add("The Stars recovery journal contains an incomplete entry.");
                }

                bool hasDuplicateEntry = entries
                    .GroupBy(static entry => entry.Id, StringComparer.Ordinal)
                    .Any(static group => group.Count() > 1);
                if (hasDuplicateEntry)
                {
                    problems.Add("The Stars recovery journal contains duplicate entry identifiers.");
                }

                return new CacheStoreInspection(
                    problems.Count == 0
                        ? CacheOwnerHealth.Healthy
                        : hasIncompleteEntry || hasDuplicateEntry
                                ? CacheOwnerHealth.Unhealthy
                                : CacheOwnerHealth.Degraded,
                    physicalBytes,
                    LogicalBytes: journalBytes,
                    OrphanBytes: orphanBytes,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.RecoveryJournalPhysicalBytes] = physicalBytes,
                        [CacheMetricKeys.RecoveryEntryCount] = entries.Count
                    },
                    CacheInspectionDetail.Format(problems));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                return new CacheStoreInspection(
                    CacheOwnerHealth.Unhealthy,
                    physicalBytes,
                    LogicalBytes: 0,
                    OrphanBytes: 0,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.RecoveryJournalPhysicalBytes] = physicalBytes,
                        [CacheMetricKeys.RecoveryEntryCount] = 0
                    },
                    $"The Stars recovery journal could not be read: {exception.GetType().Name}: {exception.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<StarLibraryRecoveryEntry>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            await using FileStream stream = new(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync(
                stream,
                StarLibraryRecoveryJsonContext.Default.RecoveryEntries,
                cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            string corruptPath = $"{FilePath}.corrupt-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            File.Move(FilePath, corruptPath, overwrite: true);
            return [];
        }
    }

    private async Task WriteCoreAsync(
        IReadOnlyCollection<StarLibraryRecoveryEntry> entries,
        CancellationToken cancellationToken) =>
        await WriteBytesAtomicAsync(
                FilePath,
                JsonSerializer.SerializeToUtf8Bytes(
                    entries.ToList(),
                    StarLibraryRecoveryJsonContext.Default.RecoveryEntries),
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task WriteBytesAtomicAsync(
        string targetPath,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<ClearManifest?> ReadClearManifestCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ClearManifestPath))
        {
            return null;
        }

        await using FileStream stream = new(
            ClearManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ClearManifest manifest = await JsonSerializer.DeserializeAsync(
                stream,
                StarLibraryRecoveryJsonContext.Default.ClearManifest,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The Stars clear transaction manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    private async Task<ClearManifest> GetRequiredClearManifestCoreAsync(
        string transactionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ClearManifest manifest = await ReadClearManifestCoreAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No interrupted Stars clear transaction exists.");
        if (!string.Equals(manifest.TransactionId, transactionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stars clear transaction '{transactionId}' does not match pending transaction '{manifest.TransactionId}'.");
        }

        return manifest;
    }

    private async Task CommitClearCoreAsync(ClearManifest manifest, CancellationToken cancellationToken)
    {
        await WriteCoreAsync([], cancellationToken).ConfigureAwait(false);
        // Remove the manifest first. If cleanup is interrupted, an orphan backup is safe;
        // SQLite's committed marker still proves that the logical clear won.
        // These deletes are intentionally separate. If the manifest cannot be removed,
        // its backup must remain available for the next recovery attempt.
        DeletePathsStrict((string[])[ClearManifestPath]);
        DeletePathsStrict((string[])[GetBackupPath(manifest)]);
        DeleteSidecarsStrict();
    }

    private async Task RollbackClearCoreAsync(ClearManifest manifest, CancellationToken cancellationToken)
    {
        string backupPath = GetBackupPath(manifest);
        if (!File.Exists(backupPath))
        {
            throw new InvalidDataException(
                $"The backup for Stars clear transaction '{manifest.TransactionId}' is missing.");
        }

        if (manifest.HadJournal)
        {
            byte[] original = await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
            await WriteBytesAtomicAsync(FilePath, original, cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }

        // Once the active journal is restored, remove the manifest before the backup. A
        // crash can leave an orphan backup, but never an instruction to restore missing data.
        DeletePathsStrict((string[])[ClearManifestPath]);
        DeletePathsStrict((string[])[backupPath]);
    }

    private string GetBackupPath(ClearManifest manifest) =>
        Path.Combine(Path.GetDirectoryName(FilePath)!, manifest.BackupFileName);

    private static void ValidateManifest(ClearManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.TransactionId) ||
            string.IsNullOrWhiteSpace(manifest.BackupFileName) ||
            !string.Equals(Path.GetFileName(manifest.BackupFileName), manifest.BackupFileName, StringComparison.Ordinal) ||
            !manifest.BackupFileName.Contains(manifest.TransactionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Stars clear transaction manifest is invalid.");
        }
    }

    private void DeleteSidecarsStrict() => DeletePathsStrict(EnumerateSidecars());

    private static void DeletePathsStrict(IEnumerable<string> paths)
    {
        List<CacheClearResidual> residuals = [];
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                residuals.Add(new CacheClearResidual(path, $"{exception.GetType().Name}: {exception.Message}"));
            }
        }

        if (residuals.Count > 0)
        {
            throw new CacheClearPostconditionException(CacheOwnerIds.StarsLibrary, residuals);
        }
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private string[] EnumerateSidecars()
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            string sidecarPrefix = Path.GetFileName(FilePath) + ".";
            return Directory.EnumerateFiles(
                    directory,
                    Path.GetFileName(FilePath) + "*",
                    SearchOption.TopDirectoryOnly)
                // Win32 wildcard semantics allow "name.*" to match "name". Keep the
                // active journal out of physical/orphan accounting explicitly.
                .Where(path => Path.GetFileName(path).StartsWith(
                    sidecarPrefix,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ClearManifest(
        string TransactionId,
        string BackupFileName,
        bool HadJournal);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(List<StarLibraryRecoveryEntry>), TypeInfoPropertyName = "RecoveryEntries")]
    [JsonSerializable(typeof(ClearManifest), TypeInfoPropertyName = "ClearManifest")]
    private sealed partial class StarLibraryRecoveryJsonContext : JsonSerializerContext
    {
    }

    private sealed class RecoveryClearTransaction : IStarLibraryRecoveryClearTransaction
    {
        private readonly StarLibraryRecoveryStore _owner;
        private readonly ClearManifest _manifest;
        private int _completed;

        public RecoveryClearTransaction(StarLibraryRecoveryStore owner, ClearManifest manifest)
        {
            _owner = owner;
            _manifest = manifest;
        }

        public string TransactionId => _manifest.TransactionId;

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            CompleteAsync(commit: true, cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            CompleteAsync(commit: false, cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                // Durable manifest + backup remain for relaunch recovery. Disposing an
                // unresolved transaction must never guess which store committed.
                _owner._gate.Release();
            }

            return ValueTask.CompletedTask;
        }

        private async Task CompleteAsync(bool commit, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                throw new InvalidOperationException("The Stars recovery clear transaction is already complete.");
            }

            try
            {
                if (commit)
                {
                    await _owner.CommitClearCoreAsync(_manifest, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _owner.RollbackClearCoreAsync(_manifest, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _owner._gate.Release();
            }
        }
    }
}
