using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace JitHub.Services;

public sealed class SqliteGitHubCacheStore : IGitHubCacheStore
{
    private const int CurrentUserVersion = 2;
    private const int InlinePayloadThresholdBytes = 128 * 1024;

    private readonly string _databasePath;
    private readonly string _payloadRootPath;
    private readonly GitHubCachePolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteGitHubCacheStore(IAppStoragePathProvider pathProvider)
        : this(pathProvider.CacheDatabasePath, pathProvider.PayloadRootPath, GitHubCachePolicy.Default)
    {
    }

    internal SqliteGitHubCacheStore(string databasePath, string payloadRootPath, GitHubCachePolicy policy)
    {
        _databasePath = databasePath;
        _payloadRootPath = payloadRootPath;
        _policy = policy;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        Directory.CreateDirectory(_payloadRootPath);
    }

    public Task<CachedResult<T>?> TryGetAsync<T>(
        GitHubQuery<T> query,
        CancellationToken cancellationToken = default)
        where T : class
    {
        return Task.Run(
            () => TryGetCoreAsync(query, cancellationToken),
            cancellationToken);
    }

    private async Task<CachedResult<T>?> TryGetCoreAsync<T>(
        GitHubQuery<T> query,
        CancellationToken cancellationToken)
        where T : class
    {
        CacheEntrySnapshot? entry = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_json, payload_file, etag, last_modified_utc, fetched_at_utc, stale_after_utc,
                       byte_length
                FROM cache_entries
                WHERE cache_key = $cache_key AND user_id = $user_id;
                """;
            command.Parameters.AddWithValue("$cache_key", query.CacheKey);
            command.Parameters.AddWithValue("$user_id", query.UserId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entry = new CacheEntrySnapshot(
                    query.UserId,
                    query.CacheKey,
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ReadDate(reader, 3),
                    ReadDate(reader, 4) ?? DateTimeOffset.MinValue,
                    ReadDate(reader, 5) ?? DateTimeOffset.MinValue,
                    reader.GetString(4),
                    reader.GetInt64(6));
            }
        }
        finally
        {
            _gate.Release();
        }

        if (entry is null)
        {
            return null;
        }

        try
        {
            string? payloadJson = entry.PayloadJson;
            if (payloadJson is null)
            {
                if (string.IsNullOrWhiteSpace(entry.PayloadFile) ||
                    !TryResolvePayloadPath(entry.PayloadFile, out string payloadPath))
                {
                    throw new InvalidDataException("The cached payload identity is invalid.");
                }

                payloadJson = await File.ReadAllTextAsync(payloadPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            T? value = JsonSerializer.Deserialize(payloadJson, query.JsonTypeInfo);
            if (value is null)
            {
                throw new JsonException("The cached payload deserialized to null.");
            }

            await TouchEntryAsync(entry.UserId, entry.CacheKey, cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset staleAfter = entry.StaleAfter == DateTimeOffset.MinValue
                ? entry.FetchedAt
                : entry.StaleAfter;
            CacheState cacheState = staleAfter > DateTimeOffset.UtcNow
                ? CacheState.Fresh
                : CacheState.Stale;

            return new CachedResult<T>(
                value,
                cacheState,
                entry.FetchedAt,
                staleAfter,
                ETag: entry.ETag,
                LastModified: entry.LastModified);
        }
        catch (Exception exception) when (IsRecoverablePayloadFailure(exception))
        {
            await QuarantineEntryGenerationAsync(entry, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    public async Task PutAsync<T>(
        GitHubQuery<T> query,
        GitHubRestResponse<T> response,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (response.Payload is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(response.Payload, query.JsonTypeInfo);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(json);
        DateTimeOffset fetchedAt = response.FetchedAt;
        DateTimeOffset staleAfter = fetchedAt.Add(query.Ttl);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            string? existingPayloadFile = await GetPayloadFileAsync(
                connection,
                query.UserId,
                query.CacheKey,
                cancellationToken);
            string? payloadJson = null;
            string? payloadFile = null;
            string? newPayloadPath = null;
            bool payloadGenerationCommitted = false;

            try
            {
                if (payloadBytes.Length <= InlinePayloadThresholdBytes)
                {
                    payloadJson = json;
                }
                else
                {
                    payloadFile = $"{HashKey(query.UserId + "\n" + query.CacheKey)}.{Guid.NewGuid():N}.json";
                    newPayloadPath = Path.Combine(_payloadRootPath, payloadFile);
                    await WritePayloadGenerationAsync(newPayloadPath, payloadBytes, cancellationToken)
                        .ConfigureAwait(false);
                }

                await using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                INSERT INTO cache_entries (
                    cache_key, user_id, method, path, resource_kind, payload_json, payload_file,
                    etag, last_modified_utc, fetched_at_utc, stale_after_utc, byte_length, last_accessed_utc)
                VALUES (
                    $cache_key, $user_id, $method, $path, $resource_kind, $payload_json, $payload_file,
                    $etag, $last_modified_utc, $fetched_at_utc, $stale_after_utc, $byte_length, $last_accessed_utc)
                ON CONFLICT(user_id, cache_key) DO UPDATE SET
                    method = excluded.method,
                    path = excluded.path,
                    resource_kind = excluded.resource_kind,
                    payload_json = excluded.payload_json,
                    payload_file = excluded.payload_file,
                    etag = excluded.etag,
                    last_modified_utc = excluded.last_modified_utc,
                    fetched_at_utc = excluded.fetched_at_utc,
                    stale_after_utc = excluded.stale_after_utc,
                    byte_length = excluded.byte_length,
                    last_accessed_utc = excluded.last_accessed_utc;
                """;
                AddEntryParameters(upsert, query, payloadJson, payloadFile, response.ETag, response.LastModified, fetchedAt, staleAfter, payloadBytes.Length);
                await upsert.ExecuteNonQueryAsync(cancellationToken);

                await ReplaceTagsAsync(
                    connection,
                    transaction,
                    query.UserId,
                    query.CacheKey,
                    query.Tags,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                payloadGenerationCommitted = true;
            }
            finally
            {
                if (!payloadGenerationCommitted && newPayloadPath is not null)
                {
                    TryDeleteFile(newPayloadPath);
                }
            }

            DeletePayloadFile(existingPayloadFile, payloadFile);
            await CleanupUnreferencedPayloadsAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnforceCapsCoreAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkRevalidatedAsync<T>(
        GitHubQuery<T> query,
        GitHubRestResponse<T> response,
        CancellationToken cancellationToken = default)
        where T : class
    {
        DateTimeOffset fetchedAt = response.FetchedAt;
        DateTimeOffset staleAfter = fetchedAt.Add(query.Ttl);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE cache_entries
                SET fetched_at_utc = $fetched_at_utc,
                    stale_after_utc = $stale_after_utc,
                    etag = COALESCE($etag, etag),
                    last_modified_utc = COALESCE($last_modified_utc, last_modified_utc),
                    last_accessed_utc = $last_accessed_utc
                WHERE cache_key = $cache_key AND user_id = $user_id;
                """;
            command.Parameters.AddWithValue("$cache_key", query.CacheKey);
            command.Parameters.AddWithValue("$user_id", query.UserId);
            command.Parameters.AddWithValue("$fetched_at_utc", FormatDate(fetchedAt));
            command.Parameters.AddWithValue("$stale_after_utc", FormatDate(staleAfter));
            command.Parameters.AddWithValue("$etag", (object?)response.ETag ?? DBNull.Value);
            command.Parameters.AddWithValue("$last_modified_utc", response.LastModified.HasValue ? FormatDate(response.LastModified.Value) : DBNull.Value);
            command.Parameters.AddWithValue("$last_accessed_utc", FormatDate(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
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
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            IReadOnlyList<string> payloadFiles = await GetPayloadFilesAsync(connection, cacheKey, cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand deleteTags = connection.CreateCommand();
            deleteTags.Transaction = transaction;
            deleteTags.CommandText = "DELETE FROM cache_tags WHERE cache_key = $cache_key;";
            deleteTags.Parameters.AddWithValue("$cache_key", cacheKey);
            await deleteTags.ExecuteNonQueryAsync(cancellationToken);

            await using SqliteCommand deleteEntry = connection.CreateCommand();
            deleteEntry.Transaction = transaction;
            deleteEntry.CommandText = "DELETE FROM cache_entries WHERE cache_key = $cache_key;";
            deleteEntry.Parameters.AddWithValue("$cache_key", cacheKey);
            await deleteEntry.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            foreach (string payloadFile in payloadFiles)
            {
                DeletePayloadFile(payloadFile, replacementPayloadFile: null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task InvalidateTagsAsync(
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default) =>
        InvalidateTagsCoreAsync(userId: null, tags, cancellationToken);

    public Task InvalidateTagsAsync(
        string userId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default) =>
        InvalidateTagsCoreAsync(
            GitHubAccountPartition.Require(userId),
            tags,
            cancellationToken);

    private async Task InvalidateTagsCoreAsync(
        string? userId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return;
        }

        List<string> payloadFiles = [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            HashSet<(string UserId, string CacheKey)> partitions = [];
            foreach (string tag in tags)
            {
                await using SqliteCommand select = connection.CreateCommand();
                select.CommandText = userId is null
                    ? "SELECT user_id, cache_key FROM cache_tags WHERE tag = $tag;"
                    : "SELECT user_id, cache_key FROM cache_tags WHERE user_id = $user_id AND tag = $tag;";
                select.Parameters.AddWithValue("$tag", tag);
                if (userId is not null)
                {
                    select.Parameters.AddWithValue("$user_id", userId);
                }

                await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    partitions.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach ((string partitionUserId, string cacheKey) in partitions)
            {
                string? payloadFile = await GetPayloadFileAsync(
                    connection,
                    partitionUserId,
                    cacheKey,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(payloadFile))
                {
                    payloadFiles.Add(payloadFile);
                }
            }

            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach ((string partitionUserId, string cacheKey) in partitions)
            {
                await using SqliteCommand deleteEntry = connection.CreateCommand();
                deleteEntry.Transaction = transaction;
                deleteEntry.CommandText = "DELETE FROM cache_entries WHERE user_id = $user_id AND cache_key = $cache_key;";
                deleteEntry.Parameters.AddWithValue("$user_id", partitionUserId);
                deleteEntry.Parameters.AddWithValue("$cache_key", cacheKey);
                await deleteEntry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (string payloadFile in payloadFiles.Distinct(StringComparer.Ordinal))
        {
            DeletePayloadFile(payloadFile, replacementPayloadFile: null);
        }
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);

            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using (SqliteCommand deleteTags = connection.CreateCommand())
            {
                deleteTags.Transaction = transaction;
                deleteTags.CommandText = "DELETE FROM cache_tags;";
                await deleteTags.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (SqliteCommand deleteEntries = connection.CreateCommand())
            {
                deleteEntries.Transaction = transaction;
                deleteEntries.CommandText = "DELETE FROM cache_entries;";
                await deleteEntries.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            List<CacheClearResidual> residuals = DeletePayloadDirectoryContents(cancellationToken);
            await using (SqliteCommand verify = connection.CreateCommand())
            {
                verify.CommandText =
                    "SELECT (SELECT COUNT(*) FROM cache_entries) + (SELECT COUNT(*) FROM cache_tags);";
                long remainingRows = Convert.ToInt64(
                    await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (remainingRows != 0)
                {
                    residuals.Add(new CacheClearResidual(
                        _databasePath,
                        $"SQLite contains {remainingRows} cache row(s) after clear."));
                }
            }

            foreach (string residualPath in EnumeratePayloadFilesSafely(residuals))
            {
                if (!residuals.Any(residual => string.Equals(
                        residual.Identity,
                        residualPath,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    residuals.Add(new CacheClearResidual(residualPath, "The payload file still exists after clear."));
                }
            }

            if (residuals.Count > 0)
            {
                throw new CacheClearPostconditionException(CacheOwnerIds.GitHubQuery, residuals);
            }

            await CheckpointAndVacuumAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetTotalPayloadBytesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            return await GetTotalPayloadBytesCoreAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetTotalMetadataBytesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            return await GetTotalMetadataBytesCoreAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            object? result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnforceCapsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await CleanupUnreferencedPayloadsAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnforceCapsCoreAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearPartitionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(userId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            List<string> payloadFiles = [];
            await using (SqliteCommand select = connection.CreateCommand())
            {
                select.CommandText =
                    "SELECT payload_file FROM cache_entries WHERE user_id = $user_id AND payload_file IS NOT NULL;";
                select.Parameters.AddWithValue("$user_id", partition);
                await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    payloadFiles.Add(reader.GetString(0));
                }
            }

            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using (SqliteCommand deleteTags = connection.CreateCommand())
            {
                deleteTags.Transaction = transaction;
                deleteTags.CommandText = "DELETE FROM cache_tags WHERE user_id = $user_id;";
                deleteTags.Parameters.AddWithValue("$user_id", partition);
                await deleteTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand deleteEntries = connection.CreateCommand())
            {
                deleteEntries.Transaction = transaction;
                deleteEntries.CommandText = "DELETE FROM cache_entries WHERE user_id = $user_id;";
                deleteEntries.Parameters.AddWithValue("$user_id", partition);
                await deleteEntries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand verify = connection.CreateCommand())
            {
                verify.Transaction = transaction;
                verify.CommandText =
                    "SELECT (SELECT COUNT(*) FROM cache_entries WHERE user_id = $user_id) + " +
                    "(SELECT COUNT(*) FROM cache_tags WHERE user_id = $user_id);";
                verify.Parameters.AddWithValue("$user_id", partition);
                long remaining = Convert.ToInt64(
                    await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (remaining != 0)
                {
                    throw new InvalidDataException(
                        $"Query cache partition clear left {remaining} database row(s)." );
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            List<CacheClearResidual> residuals = [];
            foreach (string payloadFile in payloadFiles.Distinct(StringComparer.Ordinal))
            {
                if (!IsSafePayloadFileName(payloadFile))
                {
                    residuals.Add(new CacheClearResidual(payloadFile, "The payload identity is unsafe."));
                    continue;
                }

                DeleteFileStrict(Path.Combine(_payloadRootPath, payloadFile), residuals);
            }

            foreach (string path in await GetUnreferencedPayloadsAsync(
                         connection,
                         residuals,
                         cancellationToken).ConfigureAwait(false))
            {
                DeleteFileStrict(path, residuals);
            }

            if (residuals.Count > 0)
            {
                throw new CacheClearPostconditionException(CacheOwnerIds.GitHubQuery, residuals);
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
            long databaseBytes = GetDatabasePhysicalBytes();
            long payloadDirectoryBytes = GetDirectoryPhysicalBytes(_payloadRootPath, cancellationToken);
            if (!File.Exists(_databasePath))
            {
                CacheOwnerHealth missingHealth = payloadDirectoryBytes == 0
                    ? CacheOwnerHealth.Healthy
                    : CacheOwnerHealth.Degraded;
                return new CacheStoreInspection(
                    missingHealth,
                    payloadDirectoryBytes,
                    LogicalBytes: 0,
                    OrphanBytes: payloadDirectoryBytes,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.DatabasePhysicalBytes] = 0,
                        [CacheMetricKeys.DatabaseExists] = 0,
                        [CacheMetricKeys.PayloadDirectoryPhysicalBytes] = payloadDirectoryBytes,
                        [CacheMetricKeys.OrphanBytes] = payloadDirectoryBytes,
                        [CacheMetricKeys.SchemaVersion] = 0
                    },
                    payloadDirectoryBytes == 0
                        ? "The GitHub query cache database has not been created yet."
                        : "The GitHub query cache database is missing while payload files remain.");
            }

            try
            {
                await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(cancellationToken).ConfigureAwait(false);
                List<string> integrityProblems = [];
                List<string> degradedProblems = [];

                await using (SqliteCommand quickCheck = connection.CreateCommand())
                {
                    quickCheck.CommandText = "PRAGMA quick_check;";
                    string result = Convert.ToString(
                        await quickCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        integrityProblems.Add("SQLite quick_check failed.");
                    }
                }

                int schemaVersion = await GetSchemaVersionCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                if (schemaVersion != CurrentUserVersion)
                {
                    integrityProblems.Add($"Schema version {schemaVersion} does not match {CurrentUserVersion}.");
                }

                string[] requiredTables = ["cache_entries", "cache_tags", "cache_meta"];
                HashSet<string> existingTables = new(StringComparer.Ordinal);
                await using (SqliteCommand objects = connection.CreateCommand())
                {
                    objects.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IS NOT NULL;";
                    await using SqliteDataReader reader = await objects.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        existingTables.Add(reader.GetString(0));
                    }
                }

                foreach (string requiredTable in requiredTables)
                {
                    if (!existingTables.Contains(requiredTable))
                    {
                        integrityProblems.Add($"Required query cache table '{requiredTable}' is missing.");
                    }
                }

                bool hasRequiredTables = requiredTables.All(existingTables.Contains);
                if (hasRequiredTables &&
                    !await HasPartitionedPrimaryKeyAsync(connection, "cache_entries", cancellationToken).ConfigureAwait(false))
                {
                    integrityProblems.Add("The query cache does not enforce the user/account partition in its primary key.");
                }

                if (hasRequiredTables &&
                    !await HasPartitionedPrimaryKeyAsync(connection, "cache_tags", cancellationToken).ConfigureAwait(false))
                {
                    integrityProblems.Add("The query cache tag index does not enforce the user/account partition.");
                }

                if (!hasRequiredTables)
                {
                    return new CacheStoreInspection(
                        CacheOwnerHealth.Unhealthy,
                        databaseBytes + payloadDirectoryBytes,
                        LogicalBytes: 0,
                        OrphanBytes: 0,
                        new Dictionary<string, long>
                        {
                            [CacheMetricKeys.DatabasePhysicalBytes] = databaseBytes,
                            [CacheMetricKeys.DatabaseExists] = 1,
                            [CacheMetricKeys.PayloadDirectoryPhysicalBytes] = payloadDirectoryBytes,
                            [CacheMetricKeys.OrphanBytes] = 0,
                            [CacheMetricKeys.SchemaVersion] = schemaVersion
                        },
                        CacheInspectionDetail.Format(integrityProblems));
                }

                HashSet<string> referencedPayloads = new(StringComparer.OrdinalIgnoreCase);
                long logicalPayloadBytes = 0;
                await using (SqliteCommand entries = connection.CreateCommand())
                {
                    entries.CommandText = """
                        SELECT payload_json IS NOT NULL, payload_file, byte_length,
                               CASE WHEN payload_json IS NULL THEN NULL
                                    ELSE LENGTH(CAST(payload_json AS BLOB)) END
                        FROM cache_entries;
                        """;
                    await using SqliteDataReader reader = await entries.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        bool hasInlinePayload = reader.GetInt64(0) != 0;
                        string? payloadFile = reader.IsDBNull(1) ? null : reader.GetString(1);
                        long byteLength = reader.GetInt64(2);
                        logicalPayloadBytes += Math.Max(0, byteLength);

                        if (hasInlinePayload == !string.IsNullOrWhiteSpace(payloadFile))
                        {
                            integrityProblems.Add("An entry must reference exactly one payload representation.");
                            continue;
                        }

                        if (hasInlinePayload)
                        {
                            long inlineBytes = reader.IsDBNull(3) ? -1 : reader.GetInt64(3);
                            if (inlineBytes != byteLength)
                            {
                                integrityProblems.Add("An inline payload length does not match its metadata.");
                            }

                            continue;
                        }

                        if (!IsSafePayloadFileName(payloadFile))
                        {
                            integrityProblems.Add("An entry contains an unsafe payload file name.");
                            continue;
                        }

                        referencedPayloads.Add(payloadFile!);
                        string path = Path.Combine(_payloadRootPath, payloadFile!);
                        if (!File.Exists(path))
                        {
                            integrityProblems.Add("An external payload file is missing.");
                        }
                        else if (TryGetFileLength(path) != byteLength)
                        {
                            integrityProblems.Add("An external payload length does not match its metadata.");
                        }
                    }
                }

                long orphanBytes = 0;
                if (Directory.Exists(_payloadRootPath))
                {
                    foreach (string path in Directory.EnumerateFiles(_payloadRootPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!referencedPayloads.Contains(Path.GetFileName(path)))
                        {
                            orphanBytes += TryGetFileLength(path);
                        }
                    }
                }

                if (orphanBytes > 0)
                {
                    degradedProblems.Add("Unreferenced payload generations are awaiting cleanup.");
                }

                await using (SqliteCommand orphanTags = connection.CreateCommand())
                {
                    orphanTags.CommandText = """
                        SELECT COUNT(*) FROM cache_tags t
                        LEFT JOIN cache_entries e ON e.user_id = t.user_id AND e.cache_key = t.cache_key
                        WHERE e.cache_key IS NULL;
                        """;
                    long count = Convert.ToInt64(
                        await orphanTags.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture);
                    if (count > 0)
                    {
                        degradedProblems.Add($"{count} orphan cache tag row(s) were found.");
                    }
                }

                long logicalMetadataBytes = await GetTotalMetadataBytesCoreAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
                databaseBytes = GetDatabasePhysicalBytes();
                payloadDirectoryBytes = GetDirectoryPhysicalBytes(_payloadRootPath, cancellationToken);
                CacheOwnerHealth health = integrityProblems.Count > 0
                    ? CacheOwnerHealth.Unhealthy
                    : degradedProblems.Count > 0
                        ? CacheOwnerHealth.Degraded
                        : CacheOwnerHealth.Healthy;
                string? detail = CacheInspectionDetail.Format(integrityProblems.Concat(degradedProblems));

                return new CacheStoreInspection(
                    health,
                    databaseBytes + payloadDirectoryBytes,
                    logicalMetadataBytes + logicalPayloadBytes,
                    orphanBytes,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.DatabasePhysicalBytes] = databaseBytes,
                        [CacheMetricKeys.DatabaseExists] = 1,
                        [CacheMetricKeys.PayloadDirectoryPhysicalBytes] = payloadDirectoryBytes,
                        [CacheMetricKeys.LogicalMetadataBytes] = logicalMetadataBytes,
                        [CacheMetricKeys.LogicalPayloadBytes] = logicalPayloadBytes,
                        [CacheMetricKeys.OrphanBytes] = orphanBytes,
                        [CacheMetricKeys.SchemaVersion] = schemaVersion
                    },
                    string.IsNullOrWhiteSpace(detail) ? null : detail);
            }
            catch (SqliteException exception)
            {
                return new CacheStoreInspection(
                    CacheOwnerHealth.Unhealthy,
                    databaseBytes + payloadDirectoryBytes,
                    LogicalBytes: 0,
                    OrphanBytes: 0,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.DatabasePhysicalBytes] = databaseBytes,
                        [CacheMetricKeys.DatabaseExists] = 1,
                        [CacheMetricKeys.PayloadDirectoryPhysicalBytes] = payloadDirectoryBytes,
                        [CacheMetricKeys.OrphanBytes] = 0
                    },
                    $"SQLite integrity inspection failed: {exception.SqliteErrorCode}.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        int version = await GetSchemaVersionCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version > CurrentUserVersion)
        {
            throw new InvalidDataException(
                $"GitHub cache schema {version} is newer than supported schema {CurrentUserVersion}.");
        }

        bool hasEntries = await TableExistsAsync(connection, "cache_entries", cancellationToken).ConfigureAwait(false);
        bool legacyIdentity = hasEntries &&
            !await HasPartitionedPrimaryKeyAsync(connection, "cache_entries", cancellationToken).ConfigureAwait(false);
        if (version == 1 || legacyIdentity)
        {
            await MigrateV1ToV2Async(connection, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await EnsureV2SchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        _initialized = true;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async Task<SqliteConnection> OpenReadOnlyConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        };
        SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 1000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureV2SchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction();
        string[] commands =
        [
            """
            CREATE TABLE IF NOT EXISTS cache_entries (
                user_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                method TEXT NOT NULL,
                path TEXT NOT NULL,
                resource_kind TEXT NOT NULL,
                payload_json TEXT NULL,
                payload_file TEXT NULL,
                etag TEXT NULL,
                last_modified_utc TEXT NULL,
                fetched_at_utc TEXT NOT NULL,
                stale_after_utc TEXT NOT NULL,
                byte_length INTEGER NOT NULL,
                last_accessed_utc TEXT NOT NULL,
                PRIMARY KEY(user_id, cache_key)
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_cache_entries_user_path ON cache_entries(user_id, method, path);",
            """
            CREATE TABLE IF NOT EXISTS cache_tags (
                user_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY(user_id, cache_key, tag),
                FOREIGN KEY(user_id, cache_key) REFERENCES cache_entries(user_id, cache_key) ON DELETE CASCADE
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_cache_tags_tag ON cache_tags(tag, user_id);",
            """
            CREATE TABLE IF NOT EXISTS cache_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """,
            "INSERT OR REPLACE INTO cache_meta(key, value) VALUES('schema_version', '2');",
            $"PRAGMA user_version = {CurrentUserVersion};"
        ];
        foreach (string commandText in commands)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateV1ToV2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            ALTER TABLE cache_entries RENAME TO cache_entries_v1;
            ALTER TABLE cache_tags RENAME TO cache_tags_v1;

            CREATE TABLE cache_entries (
                user_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                method TEXT NOT NULL,
                path TEXT NOT NULL,
                resource_kind TEXT NOT NULL,
                payload_json TEXT NULL,
                payload_file TEXT NULL,
                etag TEXT NULL,
                last_modified_utc TEXT NULL,
                fetched_at_utc TEXT NOT NULL,
                stale_after_utc TEXT NOT NULL,
                byte_length INTEGER NOT NULL,
                last_accessed_utc TEXT NOT NULL,
                PRIMARY KEY(user_id, cache_key)
            );
            INSERT INTO cache_entries
                (user_id, cache_key, method, path, resource_kind, payload_json, payload_file,
                 etag, last_modified_utc, fetched_at_utc, stale_after_utc, byte_length, last_accessed_utc)
            SELECT user_id, cache_key, method, path, resource_kind, payload_json, payload_file,
                   etag, last_modified_utc, fetched_at_utc, stale_after_utc, byte_length, last_accessed_utc
            FROM cache_entries_v1;

            CREATE TABLE cache_tags (
                user_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY(user_id, cache_key, tag),
                FOREIGN KEY(user_id, cache_key) REFERENCES cache_entries(user_id, cache_key) ON DELETE CASCADE
            );
            INSERT INTO cache_tags(user_id, cache_key, tag)
            SELECT e.user_id, t.cache_key, t.tag
            FROM cache_tags_v1 t
            JOIN cache_entries_v1 e ON e.cache_key = t.cache_key;

            DROP TABLE cache_tags_v1;
            DROP TABLE cache_entries_v1;
            CREATE INDEX ix_cache_entries_user_path ON cache_entries(user_id, method, path);
            CREATE INDEX ix_cache_tags_tag ON cache_tags(tag, user_id);
            CREATE TABLE IF NOT EXISTS cache_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR REPLACE INTO cache_meta(key, value) VALUES('schema_version', '2');
            PRAGMA user_version = {CurrentUserVersion};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> HasPartitionedPrimaryKeyAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        Dictionary<string, int> primaryKeyColumns = new(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int position = reader.GetInt32(5);
            if (position > 0)
            {
                primaryKeyColumns[reader.GetString(1)] = position;
            }
        }

        return primaryKeyColumns.TryGetValue("user_id", out int userPosition) && userPosition == 1 &&
            primaryKeyColumns.TryGetValue("cache_key", out int keyPosition) && keyPosition == 2;
    }

    private static void AddEntryParameters<T>(
        SqliteCommand command,
        GitHubQuery<T> query,
        string? payloadJson,
        string? payloadFile,
        string? etag,
        DateTimeOffset? lastModified,
        DateTimeOffset fetchedAt,
        DateTimeOffset staleAfter,
        long byteLength)
        where T : class
    {
        command.Parameters.AddWithValue("$cache_key", query.CacheKey);
        command.Parameters.AddWithValue("$user_id", query.UserId);
        command.Parameters.AddWithValue("$method", query.Method.Method);
        command.Parameters.AddWithValue("$path", query.RelativePath);
        command.Parameters.AddWithValue("$resource_kind", query.ResourceKind);
        command.Parameters.AddWithValue("$payload_json", (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_file", (object?)payloadFile ?? DBNull.Value);
        command.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_modified_utc", lastModified.HasValue ? FormatDate(lastModified.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$fetched_at_utc", FormatDate(fetchedAt));
        command.Parameters.AddWithValue("$stale_after_utc", FormatDate(staleAfter));
        command.Parameters.AddWithValue("$byte_length", byteLength);
        command.Parameters.AddWithValue("$last_accessed_utc", FormatDate(DateTimeOffset.UtcNow));
    }

    private static async Task ReplaceTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string cacheKey,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand deleteTags = connection.CreateCommand();
        deleteTags.Transaction = transaction;
        deleteTags.CommandText = "DELETE FROM cache_tags WHERE user_id = $user_id AND cache_key = $cache_key;";
        deleteTags.Parameters.AddWithValue("$user_id", userId);
        deleteTags.Parameters.AddWithValue("$cache_key", cacheKey);
        await deleteTags.ExecuteNonQueryAsync(cancellationToken);

        if (tags is null)
        {
            return;
        }

        foreach (string tag in tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.Ordinal))
        {
            await using SqliteCommand insertTag = connection.CreateCommand();
            insertTag.Transaction = transaction;
            insertTag.CommandText = "INSERT OR IGNORE INTO cache_tags(user_id, cache_key, tag) VALUES($user_id, $cache_key, $tag);";
            insertTag.Parameters.AddWithValue("$user_id", userId);
            insertTag.Parameters.AddWithValue("$cache_key", cacheKey);
            insertTag.Parameters.AddWithValue("$tag", tag);
            await insertTag.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task TouchAsync(
        SqliteConnection connection,
        string userId,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE cache_entries SET last_accessed_utc = $last_accessed_utc WHERE user_id = $user_id AND cache_key = $cache_key;";
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        command.Parameters.AddWithValue("$last_accessed_utc", FormatDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TouchEntryAsync(
        string userId,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await TouchAsync(connection, userId, cacheKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task QuarantineEntryGenerationAsync(
        CacheEntrySnapshot entry,
        CancellationToken cancellationToken)
    {
        bool deleted = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM cache_entries
                WHERE user_id = $user_id
                  AND cache_key = $cache_key
                  AND fetched_at_utc = $fetched_at_utc
                  AND byte_length = $byte_length
                  AND ((payload_file IS NULL AND $payload_file IS NULL) OR payload_file = $payload_file);
                """;
            delete.Parameters.AddWithValue("$user_id", entry.UserId);
            delete.Parameters.AddWithValue("$cache_key", entry.CacheKey);
            delete.Parameters.AddWithValue("$fetched_at_utc", entry.FetchedAtStorageValue);
            delete.Parameters.AddWithValue("$byte_length", entry.ByteLength);
            delete.Parameters.AddWithValue("$payload_file", (object?)entry.PayloadFile ?? DBNull.Value);
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _gate.Release();
        }

        if (deleted)
        {
            DeletePayloadFile(entry.PayloadFile, replacementPayloadFile: null);
        }
    }

    private static bool IsRecoverablePayloadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidDataException;

    private async Task EnforcePayloadCapAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        long totalBytes = await GetTotalPayloadBytesCoreAsync(connection, cancellationToken);
        if (totalBytes <= _policy.PayloadSoftCapBytes)
        {
            return;
        }

        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = """
            SELECT user_id, cache_key, payload_file, byte_length
            FROM cache_entries
            ORDER BY last_accessed_utc ASC;
            """;
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        List<(string UserId, string CacheKey, string? PayloadFile, long ByteLength)> entries = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3)));
        }

        foreach ((string userId, string cacheKey, string? payloadFile, long byteLength) in entries)
        {
            if (totalBytes <= _policy.PayloadSoftCapBytes)
            {
                break;
            }

            await DeleteEntryAsync(connection, userId, cacheKey, cancellationToken).ConfigureAwait(false);
            DeletePayloadFile(payloadFile, replacementPayloadFile: null);
            totalBytes -= byteLength;
        }
    }

    private async Task EnforceCapsCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnforcePayloadCapAsync(connection, cancellationToken);
        await EnforceMetadataCapAsync(connection, cancellationToken);
    }

    private async Task EnforceMetadataCapAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        long totalBytes = await GetTotalSqliteBackedBytesCoreAsync(connection, cancellationToken);
        if (totalBytes <= _policy.MetadataSoftCapBytes)
        {
            return;
        }

        List<(string UserId, string CacheKey, string? PayloadFile, long MetadataBytes)> entries = await GetEntriesForEvictionAsync(connection, cancellationToken);
        int remainingEntries = entries.Count;
        foreach ((string userId, string cacheKey, string? payloadFile, long metadataBytes) in entries)
        {
            if (totalBytes <= _policy.MetadataSoftCapBytes || remainingEntries <= 1)
            {
                break;
            }

            await DeleteEntryAsync(connection, userId, cacheKey, cancellationToken);
            DeletePayloadFile(payloadFile, replacementPayloadFile: null);
            totalBytes -= metadataBytes;
            remainingEntries--;
        }

        await CheckpointAndVacuumAsync(connection, cancellationToken);
    }

    private static async Task<long> GetTotalPayloadBytesCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(byte_length), 0) FROM cache_entries;";
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? value : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> GetTotalMetadataBytesCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(
                LENGTH(cache_key) +
                LENGTH(user_id) +
                LENGTH(method) +
                LENGTH(path) +
                LENGTH(resource_kind) +
                COALESCE(LENGTH(payload_file), 0) +
                COALESCE(LENGTH(etag), 0) +
                COALESCE(LENGTH(last_modified_utc), 0) +
                LENGTH(fetched_at_utc) +
                LENGTH(stale_after_utc) +
                LENGTH(last_accessed_utc) +
                64), 0)
            FROM cache_entries;
            """;
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? value : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> GetTotalSqliteBackedBytesCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        long metadataBytes = await GetTotalMetadataBytesCoreAsync(connection, cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(LENGTH(CAST(payload_json AS BLOB))), 0) FROM cache_entries;";
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        long inlinePayloadBytes = result is long value
            ? value
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        return metadataBytes + inlinePayloadBytes;
    }

    private static async Task<List<(string UserId, string CacheKey, string? PayloadFile, long MetadataBytes)>> GetEntriesForEvictionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                user_id,
                cache_key,
                payload_file,
                LENGTH(cache_key) +
                LENGTH(user_id) +
                LENGTH(method) +
                LENGTH(path) +
                LENGTH(resource_kind) +
                COALESCE(LENGTH(CAST(payload_json AS BLOB)), 0) +
                COALESCE(LENGTH(payload_file), 0) +
                COALESCE(LENGTH(etag), 0) +
                COALESCE(LENGTH(last_modified_utc), 0) +
                LENGTH(fetched_at_utc) +
                LENGTH(stale_after_utc) +
                LENGTH(last_accessed_utc) +
                64 AS metadata_bytes
            FROM cache_entries
            ORDER BY last_accessed_utc ASC;
            """;

        List<(string UserId, string CacheKey, string? PayloadFile, long MetadataBytes)> entries = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3)));
        }

        return entries;
    }

    private static async Task DeleteEntryAsync(
        SqliteConnection connection,
        string userId,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand deleteTags = connection.CreateCommand();
        deleteTags.CommandText = "DELETE FROM cache_tags WHERE user_id = $user_id AND cache_key = $cache_key;";
        deleteTags.Parameters.AddWithValue("$user_id", userId);
        deleteTags.Parameters.AddWithValue("$cache_key", cacheKey);
        await deleteTags.ExecuteNonQueryAsync(cancellationToken);

        await using SqliteCommand deleteEntry = connection.CreateCommand();
        deleteEntry.CommandText = "DELETE FROM cache_entries WHERE user_id = $user_id AND cache_key = $cache_key;";
        deleteEntry.Parameters.AddWithValue("$user_id", userId);
        deleteEntry.Parameters.AddWithValue("$cache_key", cacheKey);
        await deleteEntry.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CheckpointAndVacuumAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        string[] commands = ["PRAGMA wal_checkpoint(TRUNCATE);", "VACUUM;"];
        foreach (string commandText in commands)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset value)
            ? value
            : null;
    }

    private static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string HashKey(string cacheKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string?> GetPayloadFileAsync(
        SqliteConnection connection,
        string userId,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT payload_file FROM cache_entries WHERE user_id = $user_id AND cache_key = $cache_key;";
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string fileName ? fileName : null;
    }

    private static async Task<IReadOnlyList<string>> GetPayloadFilesAsync(
        SqliteConnection connection,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT payload_file FROM cache_entries WHERE cache_key = $cache_key AND payload_file IS NOT NULL;";
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        List<string> files = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            files.Add(reader.GetString(0));
        }

        return files;
    }

    private static async Task WritePayloadGenerationAsync(
        string payloadPath,
        byte[] payloadBytes,
        CancellationToken cancellationToken)
    {
        string temporaryPath = payloadPath + ".tmp";
        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, payloadPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task CleanupUnreferencedPayloadsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload_file FROM cache_entries WHERE payload_file IS NOT NULL;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string payloadFile = reader.GetString(0);
                if (IsSafePayloadFileName(payloadFile))
                {
                    referenced.Add(payloadFile);
                }
            }
        }

        if (!Directory.Exists(_payloadRootPath))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(_payloadRootPath, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!referenced.Contains(Path.GetFileName(path)))
            {
                TryDeleteFile(path);
            }
        }
    }

    private static bool IsSafePayloadFileName(string? payloadFile) =>
        !string.IsNullOrWhiteSpace(payloadFile) &&
        string.Equals(Path.GetFileName(payloadFile), payloadFile, StringComparison.Ordinal) &&
        payloadFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private bool TryResolvePayloadPath(string? payloadFile, out string payloadPath)
    {
        payloadPath = string.Empty;
        if (!IsSafePayloadFileName(payloadFile))
        {
            return false;
        }

        string root = Path.GetFullPath(_payloadRootPath);
        string candidate = Path.GetFullPath(Path.Combine(root, payloadFile!));
        string rootedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        payloadPath = candidate;
        return true;
    }

    private async Task<IReadOnlyList<string>> GetUnreferencedPayloadsAsync(
        SqliteConnection connection,
        List<CacheClearResidual> residuals,
        CancellationToken cancellationToken)
    {
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload_file FROM cache_entries WHERE payload_file IS NOT NULL;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string payloadFile = reader.GetString(0);
                if (IsSafePayloadFileName(payloadFile))
                {
                    referenced.Add(payloadFile);
                }
            }
        }

        try
        {
            return Directory.Exists(_payloadRootPath)
                ? Directory.EnumerateFiles(_payloadRootPath, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(path => !referenced.Contains(Path.GetFileName(path)))
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            residuals.Add(new CacheClearResidual(
                _payloadRootPath,
                $"Could not inspect unreferenced payloads: {exception.GetType().Name}: {exception.Message}"));
            return [];
        }
    }

    private static void DeleteFileStrict(string path, List<CacheClearResidual> residuals)
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

    private async Task<int> GetSchemaVersionCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private long GetDatabasePhysicalBytes() =>
        TryGetFileLength(_databasePath) +
        TryGetFileLength(_databasePath + "-wal") +
        TryGetFileLength(_databasePath + "-shm");

    private static long GetDirectoryPhysicalBytes(string directoryPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        long bytes = 0;
        foreach (string path in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += TryGetFileLength(path);
        }

        return bytes;
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void DeletePayloadFile(string? payloadFile, string? replacementPayloadFile)
    {
        if (string.IsNullOrWhiteSpace(payloadFile) ||
            string.Equals(payloadFile, replacementPayloadFile, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (!TryResolvePayloadPath(payloadFile, out string payloadPath))
            {
                return;
            }
            if (File.Exists(payloadPath))
            {
                File.Delete(payloadPath);
            }
        }
        catch
        {
        }
    }

    private List<CacheClearResidual> DeletePayloadDirectoryContents(CancellationToken cancellationToken)
    {
        List<CacheClearResidual> residuals = [];
        try
        {
            if (!Directory.Exists(_payloadRootPath))
            {
                return residuals;
            }

            foreach (string payloadPath in Directory.EnumerateFiles(_payloadRootPath, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(payloadPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    residuals.Add(new CacheClearResidual(
                        payloadPath,
                        $"{exception.GetType().Name}: {exception.Message}"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            residuals.Add(new CacheClearResidual(
                _payloadRootPath,
                $"{exception.GetType().Name}: {exception.Message}"));
        }

        return residuals;
    }

    private IEnumerable<string> EnumeratePayloadFilesSafely(List<CacheClearResidual> residuals)
    {
        try
        {
            return Directory.Exists(_payloadRootPath)
                ? Directory.EnumerateFiles(_payloadRootPath, "*", SearchOption.TopDirectoryOnly).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            residuals.Add(new CacheClearResidual(
                _payloadRootPath,
                $"Postcondition verification failed: {exception.GetType().Name}: {exception.Message}"));
            return [];
        }
    }

    private sealed record CacheEntrySnapshot(
        string UserId,
        string CacheKey,
        string? PayloadJson,
        string? PayloadFile,
        string? ETag,
        DateTimeOffset? LastModified,
        DateTimeOffset FetchedAt,
        DateTimeOffset StaleAfter,
        string FetchedAtStorageValue,
        long ByteLength);
}
