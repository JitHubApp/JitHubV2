using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum GistMutationKind
{
    Created,
    Updated,
    Deleted
}

public sealed class GistMutationJournalEntry
{
    public string AccountPartition { get; set; } = string.Empty;

    public string GistId { get; set; } = string.Empty;

    public GistMutationKind Kind { get; set; }

    public GitHubGist? Gist { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}

public interface IGistMutationJournal
{
    Task<IReadOnlyList<GistMutationJournalEntry>> ReadAsync(
        string accountPartition,
        CancellationToken cancellationToken = default);

    Task RecordUpsertAsync(
        string accountPartition,
        string gistId,
        GitHubGist gist,
        bool isCreate,
        CancellationToken cancellationToken = default);

    Task RecordDeleteAsync(
        string accountPartition,
        string gistId,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string accountPartition,
        string gistId,
        CancellationToken cancellationToken = default);

    Task ClearAccountAsync(
        string accountPartition,
        CancellationToken cancellationToken = default);
}

public sealed class GistMutationJournal : IGistMutationJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GistMutationJournal(IAppStoragePathProvider pathProvider)
        : this(pathProvider.GistMutationJournalPath)
    {
    }

    public GistMutationJournal(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<GistMutationJournalEntry>> ReadAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadCoreAsync(cancellationToken).ConfigureAwait(false))
                .Where(entry => string.Equals(entry.AccountPartition, partition, StringComparison.Ordinal))
                .OrderBy(entry => entry.RecordedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RecordUpsertAsync(
        string accountPartition,
        string gistId,
        GitHubGist gist,
        bool isCreate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gist);
        return RecordAsync(
            accountPartition,
            gistId,
            isCreate ? GistMutationKind.Created : GistMutationKind.Updated,
            gist,
            cancellationToken);
    }

    public Task RecordDeleteAsync(
        string accountPartition,
        string gistId,
        CancellationToken cancellationToken = default) =>
        RecordAsync(accountPartition, gistId, GistMutationKind.Deleted, null, cancellationToken);

    public async Task RemoveAsync(
        string accountPartition,
        string gistId,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        string id = NormalizeGistId(gistId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<GistMutationJournalEntry> entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (entries.RemoveAll(entry =>
                    string.Equals(entry.AccountPartition, partition, StringComparison.Ordinal) &&
                    string.Equals(entry.GistId, id, StringComparison.Ordinal)) > 0)
            {
                await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAccountAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(FilePath))
            {
                List<GistMutationJournalEntry> entries = await ReadCoreStrictAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (entries.RemoveAll(entry =>
                        string.Equals(entry.AccountPartition, partition, StringComparison.Ordinal)) > 0)
                {
                    await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
                }

                if ((await ReadCoreStrictAsync(cancellationToken).ConfigureAwait(false)).Any(entry =>
                        string.Equals(entry.AccountPartition, partition, StringComparison.Ordinal)))
                {
                    throw new IOException("The Gist mutation journal still contains data for the removed account.");
                }
            }

            string directory = Path.GetDirectoryName(FilePath)!;
            string temporaryPattern = Path.GetFileName(FilePath) + ".*.tmp";
            foreach (string temporaryPath in Directory.EnumerateFiles(directory, temporaryPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecordAsync(
        string accountPartition,
        string gistId,
        GistMutationKind kind,
        GitHubGist? gist,
        CancellationToken cancellationToken)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        string id = NormalizeGistId(gistId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<GistMutationJournalEntry> entries = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            entries.RemoveAll(entry =>
                string.Equals(entry.AccountPartition, partition, StringComparison.Ordinal) &&
                string.Equals(entry.GistId, id, StringComparison.Ordinal));
            entries.Add(new GistMutationJournalEntry
            {
                AccountPartition = partition,
                GistId = id,
                Kind = kind,
                Gist = gist,
                RecordedAt = DateTimeOffset.UtcNow
            });
            await WriteCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<GistMutationJournalEntry>> ReadCoreAsync(CancellationToken cancellationToken)
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
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<GistMutationJournalEntry>>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<List<GistMutationJournalEntry>> ReadCoreStrictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<GistMutationJournalEntry>>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Gist mutation journal is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Gist mutation journal could not be classified safely for account removal.",
                exception);
        }
    }

    private async Task WriteCoreAsync(
        IReadOnlyCollection<GistMutationJournalEntry> entries,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    entries,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeGistId(string gistId) =>
        string.IsNullOrWhiteSpace(gistId)
            ? throw new ArgumentException("A stable Gist id is required.", nameof(gistId))
            : gistId.Trim();
}
