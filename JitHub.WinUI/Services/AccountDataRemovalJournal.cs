using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record AccountDataRemovalJournalEntry(
    string AccountPartition,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> RequestedComponents,
    IReadOnlyList<string> CompletedComponents);

public interface IAccountDataRemovalJournal
{
    Task<AccountDataRemovalJournalEntry> BeginOrReadAsync(
        string accountPartition,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountDataRemovalJournalEntry>> ReadPendingAsync(
        CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(
        string accountPartition,
        string component,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string accountPartition, CancellationToken cancellationToken = default);
}

public sealed class AccountDataRemovalJournal : IAccountDataRemovalJournal
{
    private readonly string _rootPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountDataRemovalJournal(IAppStoragePathProvider pathProvider)
        : this(pathProvider.AccountRemovalJournalRootPath)
    {
    }

    internal AccountDataRemovalJournal(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<AccountDataRemovalJournalEntry> BeginOrReadAsync(
        string accountPartition,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        string[] requested = requestedComponents
            .Where(static component => !string.IsNullOrWhiteSpace(component))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0)
        {
            throw new ArgumentException("At least one account-data component is required.", nameof(requestedComponents));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountDataRemovalJournalEntry? existing = await ReadCoreAsync(partition, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                string[] merged = existing.RequestedComponents
                    .Concat(requested)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (merged.Length == existing.RequestedComponents.Count)
                {
                    return existing;
                }

                AccountDataRemovalJournalEntry expanded = existing with { RequestedComponents = merged };
                await WriteCoreAsync(expanded, cancellationToken).ConfigureAwait(false);
                return expanded;
            }

            AccountDataRemovalJournalEntry created = new(
                partition,
                DateTimeOffset.UtcNow,
                requested,
                []);
            await WriteCoreAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AccountDataRemovalJournalEntry>> ReadPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AccountDataRemovalJournalEntry> entries = [];
            foreach (string path in Directory.EnumerateFiles(_rootPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                AccountDataRemovalJournalEntry? entry = await JsonSerializer.DeserializeAsync<AccountDataRemovalJournalEntry>(
                    stream,
                    AccountDataRemovalJournalJsonContext.Default.JournalEntry,
                    cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    throw new InvalidDataException($"Account-removal journal '{Path.GetFileName(path)}' is empty.");
                }

                _ = GitHubAccountPartition.Require(entry.AccountPartition, nameof(entry.AccountPartition));
                entries.Add(entry);
            }

            return entries.OrderBy(static entry => entry.CreatedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCompletedAsync(
        string accountPartition,
        string component,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountDataRemovalJournalEntry entry = await ReadCoreAsync(partition, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Account-removal progress cannot be recorded before intent is durable.");
            if (!entry.RequestedComponents.Contains(component, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Component '{component}' is not part of this removal operation.");
            }

            if (entry.CompletedComponents.Contains(component, StringComparer.Ordinal))
            {
                return;
            }

            AccountDataRemovalJournalEntry updated = entry with
            {
                CompletedComponents = entry.CompletedComponents.Append(component).ToArray()
            };
            await WriteCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string accountPartition, CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition, nameof(accountPartition));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(partition);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            string temporaryPath = GetTemporaryPath(partition);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AccountDataRemovalJournalEntry?> ReadCoreAsync(
        string partition,
        CancellationToken cancellationToken)
    {
        string path = GetPath(partition);
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        AccountDataRemovalJournalEntry? entry = await JsonSerializer.DeserializeAsync<AccountDataRemovalJournalEntry>(
            stream,
            AccountDataRemovalJournalJsonContext.Default.JournalEntry,
            cancellationToken).ConfigureAwait(false);
        if (entry is null || !string.Equals(entry.AccountPartition, partition, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The account-removal journal is invalid or belongs to another account.");
        }

        return entry;
    }

    private async Task WriteCoreAsync(
        AccountDataRemovalJournalEntry entry,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootPath);
        string path = GetPath(entry.AccountPartition);
        string temporaryPath = GetTemporaryPath(entry.AccountPartition);
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
                        entry,
                        AccountDataRemovalJournalJsonContext.Default.JournalEntry,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
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

    private string GetPath(string partition) => Path.Combine(_rootPath, $"{HashPartition(partition)}.json");

    private string GetTemporaryPath(string partition) => $"{GetPath(partition)}.tmp";

    private static string HashPartition(string partition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partition))).ToLowerInvariant();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AccountDataRemovalJournalEntry), TypeInfoPropertyName = "JournalEntry")]
internal sealed partial class AccountDataRemovalJournalJsonContext : JsonSerializerContext
{
}

internal sealed class InMemoryAccountDataRemovalJournal : IAccountDataRemovalJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountDataRemovalJournalEntry> _entries = new(StringComparer.Ordinal);

    public Task<AccountDataRemovalJournalEntry> BeginOrReadAsync(
        string accountPartition,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string partition = GitHubAccountPartition.Require(accountPartition);
        lock (_gate)
        {
            if (!_entries.TryGetValue(partition, out AccountDataRemovalJournalEntry? entry))
            {
                entry = new(partition, DateTimeOffset.UtcNow, requestedComponents.ToArray(), []);
                _entries[partition] = entry;
            }

            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<AccountDataRemovalJournalEntry>> ReadPendingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AccountDataRemovalJournalEntry>>(_entries.Values.ToArray());
        }
    }

    public Task MarkCompletedAsync(
        string accountPartition,
        string component,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            AccountDataRemovalJournalEntry entry = _entries[accountPartition];
            if (!entry.CompletedComponents.Contains(component, StringComparer.Ordinal))
            {
                _entries[accountPartition] = entry with
                {
                    CompletedComponents = entry.CompletedComponents.Append(component).ToArray()
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string accountPartition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Remove(accountPartition);
        }

        return Task.CompletedTask;
    }
}
