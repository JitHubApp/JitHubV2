using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using Microsoft.Data.Sqlite;

namespace JitHub.Services;

internal enum StarLibraryClearStage
{
    AfterMemberships,
    AfterCategories,
    AfterItems,
    AfterPendingMutations,
    AfterSyncState,
    BeforeCommit,
    AfterCommit,
    BeforeMarkerQuery,
    BeforeMarkerFinalization
}

public sealed class SqliteStarLibraryStore : IStarLibraryStore
{
    private const int CurrentSchemaVersion = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<StarLibraryClearStage, CancellationToken, Task>? _clearStageHook;
    private bool _initialized;

    public SqliteStarLibraryStore(IAppStoragePathProvider pathProvider)
        : this(pathProvider.StarLibraryDatabasePath)
    {
    }

    internal SqliteStarLibraryStore(
        string databasePath,
        Func<StarLibraryClearStage, CancellationToken, Task>? clearStageHook = null)
    {
        DatabasePath = databasePath;
        _clearStageHook = clearStageHook;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StarLibraryPage> QueryAsync(StarLibraryQuery query, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            SqlQueryParts parts = BuildQueryParts(query);

            await using SqliteCommand countCommand = connection.CreateCommand();
            countCommand.CommandText = $"SELECT COUNT(*) FROM star_items i {parts.JoinClause} WHERE {parts.WhereClause};";
            AddQueryParameters(countCommand, query, parts);
            int totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            await using SqliteCommand itemsCommand = connection.CreateCommand();
            itemsCommand.CommandText = $"""
                SELECT i.repository_id, i.name, i.full_name, i.owner_login, i.owner_avatar_url,
                       i.description, i.default_branch, i.html_url, i.is_private, i.is_fork,
                       i.archived, i.stargazers_count, i.watchers_count, i.forks_count,
                       i.open_issues_count, i.language, i.visibility, i.topics_json,
                       i.updated_at_utc, i.pushed_at_utc, i.starred_at_utc
                FROM star_items i
                {parts.JoinClause}
                WHERE {parts.WhereClause}
                ORDER BY {GetOrderBy(query.Sort)}
                LIMIT $limit OFFSET $offset;
                """;
            AddQueryParameters(itemsCommand, query, parts);
            itemsCommand.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 500));
            itemsCommand.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

            List<(GitHubRepository Repository, DateTimeOffset StarredAt)> rows = [];
            await using (SqliteDataReader reader = await itemsCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add((ReadRepository(reader), ReadDate(reader, 20) ?? DateTimeOffset.MinValue));
                }
            }

            Dictionary<long, List<StarCategory>> categories = await ReadCategoriesForRepositoriesAsync(
                connection,
                query.UserId,
                rows.Select(static row => row.Repository.Id).ToArray(),
                cancellationToken);

            List<StarLibraryItem> items = rows.Select(row => new StarLibraryItem(
                row.Repository,
                row.StarredAt,
                categories.TryGetValue(row.Repository.Id, out List<StarCategory>? values) ? values : []))
                .ToList();
            StarSyncState syncState = await ReadSyncStateAsync(connection, query.UserId, cancellationToken);
            return new StarLibraryPage(items, totalCount, query.Offset + items.Count < totalCount, syncState);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StarCategory>> GetCategoriesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            return await ReadCategoriesAsync(connection, userId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetFacetValuesAsync(string userId, string facet, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            if (string.Equals(facet, "topics", StringComparison.OrdinalIgnoreCase))
            {
                await using SqliteCommand topicsCommand = connection.CreateCommand();
                topicsCommand.CommandText = "SELECT topics_json FROM star_items WHERE user_id = $user_id;";
                topicsCommand.Parameters.AddWithValue("$user_id", userId);
                HashSet<string> topics = new(StringComparer.OrdinalIgnoreCase);
                await using SqliteDataReader reader = await topicsCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    foreach (string topic in ParseTopics(reader.IsDBNull(0) ? "[]" : reader.GetString(0)))
                    {
                        if (!string.IsNullOrWhiteSpace(topic))
                        {
                            topics.Add(topic);
                        }
                    }
                }

                return topics.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            string column = string.Equals(facet, "owners", StringComparison.OrdinalIgnoreCase)
                ? "owner_login"
                : "language";
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT {column} FROM star_items WHERE user_id = $user_id AND {column} <> '' ORDER BY {column} COLLATE NOCASE;";
            command.Parameters.AddWithValue("$user_id", userId);
            List<string> values = [];
            await using SqliteDataReader valueReader = await command.ExecuteReaderAsync(cancellationToken);
            while (await valueReader.ReadAsync(cancellationToken))
            {
                values.Add(valueReader.GetString(0));
            }

            return values;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<StarSmartList, int>> GetSmartListCountsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COUNT(*),
                    COALESCE(SUM(CASE WHEN NOT EXISTS (
                        SELECT 1 FROM star_category_memberships cm WHERE cm.item_key = i.item_key
                    ) THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN i.starred_at_utc >= $recent_since THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN COALESCE(NULLIF(i.pushed_at_utc, ''), i.updated_at_utc) >= $recent_since THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN i.archived = 1 THEN 1 ELSE 0 END), 0)
                FROM star_items i
                WHERE i.user_id = $user_id;
                """;
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$recent_since", DateTimeOffset.UtcNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new Dictionary<StarSmartList, int>();
            }

            return new Dictionary<StarSmartList, int>
            {
                [StarSmartList.All] = reader.GetInt32(0),
                [StarSmartList.Uncategorized] = reader.GetInt32(1),
                [StarSmartList.RecentlyStarred] = reader.GetInt32(2),
                [StarSmartList.RecentlyActive] = reader.GetInt32(3),
                [StarSmartList.Archived] = reader.GetInt32(4)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertPageAsync(
        string userId,
        IReadOnlyList<GitHubStarredRepository> repositories,
        string syncGeneration,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (GitHubStarredRepository starred in repositories)
            {
                await UpsertRepositoryCoreAsync(
                    connection,
                    transaction,
                    userId,
                    starred,
                    syncGeneration,
                    respectPendingUnstar: true,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteFullSyncAsync(string userId, string syncGeneration, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM star_items
                WHERE user_id = $user_id
                  AND sync_generation <> $sync_generation
                  AND NOT EXISTS (
                      SELECT 1 FROM star_pending_mutations pending
                      WHERE pending.user_id = star_items.user_id
                        AND pending.repository_id = star_items.repository_id
                        AND pending.desired_starred = 1);
                """;
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$sync_generation", syncGeneration);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StarSyncState> GetSyncStateAsync(string userId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            return await ReadSyncStateAsync(connection, userId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSyncStateAsync(StarSyncState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO star_sync_state (
                    user_id, last_incremental_utc, last_full_utc, is_complete, is_syncing, indexed_count, error_message)
                VALUES ($user_id, $last_incremental_utc, $last_full_utc, $is_complete, $is_syncing, $indexed_count, $error_message)
                ON CONFLICT(user_id) DO UPDATE SET
                    last_incremental_utc = excluded.last_incremental_utc,
                    last_full_utc = excluded.last_full_utc,
                    is_complete = excluded.is_complete,
                    is_syncing = excluded.is_syncing,
                    indexed_count = excluded.indexed_count,
                    error_message = excluded.error_message;
                """;
            command.Parameters.AddWithValue("$user_id", state.UserId);
            command.Parameters.AddWithValue("$last_incremental_utc", DbDate(state.LastIncrementalSync));
            command.Parameters.AddWithValue("$last_full_utc", DbDate(state.LastFullSync));
            command.Parameters.AddWithValue("$is_complete", state.IsComplete ? 1 : 0);
            command.Parameters.AddWithValue("$is_syncing", state.IsSyncing ? 1 : 0);
            command.Parameters.AddWithValue("$indexed_count", state.IndexedCount);
            command.Parameters.AddWithValue("$error_message", state.ErrorMessage ?? string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StarCategory> CreateCategoryAsync(string userId, string name, string color, CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeCategoryName(name);
        string id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO star_categories (id, user_id, name, color, position, created_at_utc, updated_at_utc)
                VALUES ($id, $user_id, $name, $color,
                    COALESCE((SELECT MAX(position) + 1 FROM star_categories WHERE user_id = $user_id), 0),
                    $created_at_utc, $updated_at_utc);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$name", normalizedName);
            command.Parameters.AddWithValue("$color", NormalizeColor(color));
            command.Parameters.AddWithValue("$created_at_utc", FormatDate(now));
            command.Parameters.AddWithValue("$updated_at_utc", FormatDate(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return (await ReadCategoriesAsync(connection, userId, cancellationToken)).Single(category => category.Id == id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StarCategory> UpdateCategoryAsync(string userId, string categoryId, string name, string color, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE star_categories
                SET name = $name, color = $color, updated_at_utc = $updated_at_utc
                WHERE user_id = $user_id AND id = $id;
                """;
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$id", categoryId);
            command.Parameters.AddWithValue("$name", NormalizeCategoryName(name));
            command.Parameters.AddWithValue("$color", NormalizeColor(color));
            command.Parameters.AddWithValue("$updated_at_utc", FormatDate(DateTimeOffset.UtcNow));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("The star category no longer exists.");
            }

            return (await ReadCategoriesAsync(connection, userId, cancellationToken)).Single(category => category.Id == categoryId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteCategoryAsync(string userId, string categoryId, CancellationToken cancellationToken = default) =>
        ExecuteCategoryCommandAsync(
            "DELETE FROM star_categories WHERE user_id = $user_id AND id = $id;",
            userId,
            categoryId,
            cancellationToken);

    public async Task ReorderCategoryAsync(string userId, string categoryId, int targetPosition, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            List<StarCategory> categories = (await ReadCategoriesAsync(connection, userId, cancellationToken)).ToList();
            int sourceIndex = categories.FindIndex(category => category.Id == categoryId);
            if (sourceIndex < 0)
            {
                return;
            }

            StarCategory moving = categories[sourceIndex];
            categories.RemoveAt(sourceIndex);
            categories.Insert(Math.Clamp(targetPosition, 0, categories.Count), moving);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            for (int index = 0; index < categories.Count; index++)
            {
                await using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE star_categories SET position = $position, updated_at_utc = $updated_at_utc WHERE user_id = $user_id AND id = $id;";
                update.Parameters.AddWithValue("$position", index);
                update.Parameters.AddWithValue("$updated_at_utc", FormatDate(DateTimeOffset.UtcNow));
                update.Parameters.AddWithValue("$user_id", userId);
                update.Parameters.AddWithValue("$id", categories[index].Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task AddToCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default) =>
        UpdateMembershipsAsync(userId, categoryId, repositoryIds, add: true, cancellationToken);

    public Task RemoveFromCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default) =>
        UpdateMembershipsAsync(userId, categoryId, repositoryIds, add: false, cancellationToken);

    public async Task<IReadOnlyList<string>> GetCategoryIdsAsync(string userId, long repositoryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.category_id
                FROM star_category_memberships m
                JOIN star_items i ON i.item_key = m.item_key
                WHERE i.user_id = $user_id AND i.repository_id = $repository_id;
                """;
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$repository_id", repositoryId);
            List<string> ids = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }

            return ids;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RemoveRepositoryAsync(string userId, long repositoryId, CancellationToken cancellationToken = default) =>
        RemoveRepositoryCoreAsync(userId, "repository_id = $value", repositoryId, cancellationToken);

    public Task RemoveRepositoryByFullNameAsync(string userId, string fullName, CancellationToken cancellationToken = default) =>
        RemoveRepositoryCoreAsync(userId, "full_name = $value COLLATE NOCASE", fullName, cancellationToken);

    public async Task ApplyPendingUnstarAsync(
        StarPendingMutation mutation,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await SavePendingMutationCoreAsync(connection, transaction, mutation, cancellationToken);
            await using SqliteCommand remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM star_items WHERE user_id = $user_id AND repository_id = $repository_id;";
            remove.Parameters.AddWithValue("$user_id", mutation.UserId);
            remove.Parameters.AddWithValue("$repository_id", mutation.RepositoryId);
            await remove.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyPendingRestoreAsync(
        StarPendingMutation mutation,
        GitHubStarredRepository repository,
        IReadOnlyList<string> categoryIds,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await SavePendingMutationCoreAsync(connection, transaction, mutation, cancellationToken);
            await UpsertRepositoryCoreAsync(
                connection,
                transaction,
                mutation.UserId,
                repository,
                "local-restore",
                respectPendingUnstar: false,
                cancellationToken);

            foreach (string categoryId in categoryIds.Distinct(StringComparer.Ordinal))
            {
                await using SqliteCommand membership = connection.CreateCommand();
                membership.Transaction = transaction;
                membership.CommandText = """
                    INSERT OR IGNORE INTO star_category_memberships(category_id, item_key, added_at_utc)
                    SELECT c.id, i.item_key, $added_at_utc
                    FROM star_categories c
                    JOIN star_items i ON i.user_id = c.user_id
                    WHERE c.user_id = $user_id AND c.id = $category_id AND i.repository_id = $repository_id;
                    """;
                membership.Parameters.AddWithValue("$added_at_utc", FormatDate(DateTimeOffset.UtcNow));
                membership.Parameters.AddWithValue("$user_id", mutation.UserId);
                membership.Parameters.AddWithValue("$category_id", categoryId);
                membership.Parameters.AddWithValue("$repository_id", mutation.RepositoryId);
                await membership.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePendingMutationAsync(StarPendingMutation mutation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await SavePendingMutationCoreAsync(connection, null, mutation, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StarPendingMutation>> GetPendingMutationsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT user_id, repository_id, owner_login, repository_name, desired_starred,
                       created_at_utc, attempt_count, last_error
                FROM star_pending_mutations
                WHERE user_id = $user_id
                ORDER BY created_at_utc, repository_id;
                """;
            command.Parameters.AddWithValue("$user_id", userId);
            List<StarPendingMutation> mutations = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                mutations.Add(new StarPendingMutation(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4) != 0,
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    reader.GetInt32(6),
                    reader.GetString(7)));
            }

            return mutations;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemovePendingMutationAsync(
        string userId,
        long repositoryId,
        bool desiredStarred,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM star_pending_mutations
                WHERE user_id = $user_id AND repository_id = $repository_id AND desired_starred = $desired_starred;
                """;
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$repository_id", repositoryId);
            command.Parameters.AddWithValue("$desired_starred", desiredStarred ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordPendingMutationFailureAsync(
        string userId,
        long repositoryId,
        bool desiredStarred,
        string error,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE star_pending_mutations
                SET attempt_count = attempt_count + 1, last_error = $last_error
                WHERE user_id = $user_id AND repository_id = $repository_id AND desired_starred = $desired_starred;
                """;
            command.Parameters.AddWithValue("$last_error", (error ?? string.Empty).Trim()[..Math.Min((error ?? string.Empty).Trim().Length, 512)]);
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$repository_id", repositoryId);
            command.Parameters.AddWithValue("$desired_starred", desiredStarred ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long size = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;
        size += File.Exists(DatabasePath + "-wal") ? new FileInfo(DatabasePath + "-wal").Length : 0;
        size += File.Exists(DatabasePath + "-shm") ? new FileInfo(DatabasePath + "-shm").Length : 0;
        return Task.FromResult(size);
    }

    public async Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long physicalBytes = GetDatabasePhysicalBytes();
            if (!File.Exists(DatabasePath))
            {
                return new CacheStoreInspection(
                    CacheOwnerHealth.Healthy,
                    PhysicalBytes: 0,
                    LogicalBytes: 0,
                    OrphanBytes: 0,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.DatabasePhysicalBytes] = 0,
                        [CacheMetricKeys.DatabaseExists] = 0,
                        [CacheMetricKeys.SchemaVersion] = 0
                    },
                    "The Stars database has not been created yet.");
            }

            try
            {
                await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(cancellationToken).ConfigureAwait(false);
                List<string> problems = [];

                await using (SqliteCommand quickCheck = connection.CreateCommand())
                {
                    quickCheck.CommandText = "PRAGMA quick_check;";
                    string result = Convert.ToString(
                        await quickCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add("SQLite quick_check failed.");
                    }
                }

                int schemaVersion;
                await using (SqliteCommand version = connection.CreateCommand())
                {
                    version.CommandText = "PRAGMA user_version;";
                    schemaVersion = Convert.ToInt32(
                        await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture);
                }

                if (schemaVersion != CurrentSchemaVersion)
                {
                    problems.Add($"Schema version {schemaVersion} does not match {CurrentSchemaVersion}.");
                }

                string[] requiredObjects =
                [
                    "star_items",
                    "star_categories",
                    "star_category_memberships",
                    "star_sync_state",
                    "star_pending_mutations",
                    "star_clear_transactions",
                    "star_items_fts",
                    "star_items_ai",
                    "star_items_ad",
                    "star_items_au"
                ];
                bool hasAllRequiredObjects = true;
                await using (SqliteCommand objects = connection.CreateCommand())
                {
                    objects.CommandText = "SELECT name FROM sqlite_master WHERE name IS NOT NULL;";
                    HashSet<string> existing = new(StringComparer.Ordinal);
                    await using SqliteDataReader reader = await objects.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        existing.Add(reader.GetString(0));
                    }

                    foreach (string required in requiredObjects)
                    {
                        if (!existing.Contains(required))
                        {
                            hasAllRequiredObjects = false;
                            problems.Add($"Required Stars schema object '{required}' is missing.");
                        }
                    }
                }

                if (hasAllRequiredObjects)
                {
                    await using SqliteCommand foreignKeys = connection.CreateCommand();
                    foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                    await using SqliteDataReader reader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        problems.Add("Stars category relationships violate foreign keys.");
                    }
                }

                long pendingClearTransactions = 0;
                if (hasAllRequiredObjects)
                {
                    await using SqliteCommand pendingClears = connection.CreateCommand();
                    pendingClears.CommandText = "SELECT COUNT(*) FROM star_clear_transactions;";
                    pendingClearTransactions = Convert.ToInt64(
                        await pendingClears.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                        CultureInfo.InvariantCulture);
                    if (pendingClearTransactions > 0)
                    {
                        problems.Add(
                            $"{pendingClearTransactions} committed Stars clear transaction(s) await cross-store finalization.");
                    }
                }

                if (hasAllRequiredObjects &&
                    (problems.Count == 0 || (problems.Count == 1 && pendingClearTransactions > 0)))
                {
                    await using SqliteCommand counts = connection.CreateCommand();
                    counts.CommandText = "SELECT (SELECT COUNT(*) FROM star_items), (SELECT COUNT(*) FROM star_items_fts_docsize);";
                    await using SqliteDataReader reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt64(0) != reader.GetInt64(1))
                    {
                        problems.Add("The Stars full-text index is out of sync.");
                    }
                }

                physicalBytes = GetDatabasePhysicalBytes();
                return new CacheStoreInspection(
                    problems.Count == 0
                        ? CacheOwnerHealth.Healthy
                        : problems.Count == 1 && pendingClearTransactions > 0
                            ? CacheOwnerHealth.Degraded
                            : CacheOwnerHealth.Unhealthy,
                    physicalBytes,
                    LogicalBytes: physicalBytes,
                    OrphanBytes: 0,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.DatabasePhysicalBytes] = physicalBytes,
                        [CacheMetricKeys.DatabaseExists] = 1,
                        [CacheMetricKeys.SchemaVersion] = schemaVersion
                    },
                CacheInspectionDetail.Format(problems));
            }
            catch (SqliteException exception)
            {
                return new CacheStoreInspection(
                    CacheOwnerHealth.Unhealthy,
                    physicalBytes,
                    LogicalBytes: 0,
                    OrphanBytes: 0,
                    new Dictionary<string, long>
                    {
                        [CacheMetricKeys.DatabasePhysicalBytes] = physicalBytes,
                        [CacheMetricKeys.DatabaseExists] = 1
                    },
                    $"Stars SQLite integrity inspection failed ({exception.SqliteErrorCode}): {exception.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        string transactionId = Guid.NewGuid().ToString("N");
        await ClearAllAsync(transactionId, cancellationToken).ConfigureAwait(false);
        await CompleteClearTransactionAsync(transactionId, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task ClearUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(userId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            (string Sql, string Parameter)[] commands =
            [
                ("""
                 DELETE FROM star_category_memberships
                 WHERE category_id IN (SELECT id FROM star_categories WHERE user_id = $user_id)
                    OR item_key IN (SELECT item_key FROM star_items WHERE user_id = $user_id);
                 """, partition),
                ("DELETE FROM star_categories WHERE user_id = $user_id;", partition),
                ("DELETE FROM star_items WHERE user_id = $user_id;", partition),
                ("DELETE FROM star_pending_mutations WHERE user_id = $user_id;", partition),
                ("DELETE FROM star_sync_state WHERE user_id = $user_id;", partition)
            ];
            foreach ((string commandText, string parameter) in commands)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = commandText;
                command.Parameters.AddWithValue("$user_id", parameter);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand verify = connection.CreateCommand())
            {
                verify.Transaction = transaction;
                verify.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM star_categories WHERE user_id = $user_id) +
                        (SELECT COUNT(*) FROM star_items WHERE user_id = $user_id) +
                        (SELECT COUNT(*) FROM star_pending_mutations WHERE user_id = $user_id) +
                        (SELECT COUNT(*) FROM star_sync_state WHERE user_id = $user_id);
                    """;
                verify.Parameters.AddWithValue("$user_id", partition);
                long remaining = Convert.ToInt64(
                    await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (remaining != 0)
                {
                    throw new InvalidDataException(
                        $"Stars account clear left {remaining} partitioned row(s)." );
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAllAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            (string Sql, StarLibraryClearStage Stage)[] commands =
            [
                ("DELETE FROM star_category_memberships;", StarLibraryClearStage.AfterMemberships),
                ("DELETE FROM star_categories;", StarLibraryClearStage.AfterCategories),
                ("DELETE FROM star_items;", StarLibraryClearStage.AfterItems),
                ("DELETE FROM star_pending_mutations;", StarLibraryClearStage.AfterPendingMutations),
                ("DELETE FROM star_sync_state;", StarLibraryClearStage.AfterSyncState)
            ];
            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach ((string commandText, StarLibraryClearStage stage) in commands)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = commandText;
                await command.ExecuteNonQueryAsync(cancellationToken);
                await InvokeClearStageHookAsync(stage, cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand verify = connection.CreateCommand())
            {
                verify.Transaction = transaction;
                verify.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM star_category_memberships) +
                        (SELECT COUNT(*) FROM star_categories) +
                        (SELECT COUNT(*) FROM star_items) +
                        (SELECT COUNT(*) FROM star_pending_mutations) +
                        (SELECT COUNT(*) FROM star_sync_state);
                    """;
                long remaining = Convert.ToInt64(
                    await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (remaining != 0)
                {
                    throw new InvalidDataException($"Stars clear verification found {remaining} remaining row(s).");
                }
            }

            await using (SqliteCommand marker = connection.CreateCommand())
            {
                marker.Transaction = transaction;
                marker.CommandText = """
                    INSERT INTO star_clear_transactions(transaction_id, committed_at_utc)
                    VALUES ($transaction_id, $committed_at_utc)
                    ON CONFLICT(transaction_id) DO UPDATE SET committed_at_utc = excluded.committed_at_utc;
                    """;
                marker.Parameters.AddWithValue("$transaction_id", transactionId);
                marker.Parameters.AddWithValue("$committed_at_utc", FormatDate(DateTimeOffset.UtcNow));
                await marker.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InvokeClearStageHookAsync(StarLibraryClearStage.BeforeCommit, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await InvokeClearStageHookAsync(StarLibraryClearStage.AfterCommit, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsClearTransactionCommittedAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken).ConfigureAwait(false);
            await InvokeClearStageHookAsync(StarLibraryClearStage.BeforeMarkerQuery, cancellationToken)
                .ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM star_clear_transactions WHERE transaction_id = $transaction_id);";
            command.Parameters.AddWithValue("$transaction_id", transactionId);
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetCommittedClearTransactionsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT transaction_id FROM star_clear_transactions ORDER BY committed_at_utc, transaction_id;";
            List<string> transactionIds = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                transactionIds.Add(reader.GetString(0));
            }

            return transactionIds;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteClearTransactionAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken).ConfigureAwait(false);
            await InvokeClearStageHookAsync(StarLibraryClearStage.BeforeMarkerFinalization, cancellationToken)
                .ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM star_clear_transactions WHERE transaction_id = $transaction_id;";
            command.Parameters.AddWithValue("$transaction_id", transactionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
        int existingVersion = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (existingVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Stars database schema {existingVersion} is newer than supported schema {CurrentSchemaVersion}.");
        }
        string[] commands =
        [
            """
            CREATE TABLE IF NOT EXISTS star_items (
                item_key INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT NOT NULL,
                repository_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                full_name TEXT NOT NULL,
                owner_login TEXT NOT NULL,
                owner_avatar_url TEXT NOT NULL,
                description TEXT NOT NULL,
                default_branch TEXT NOT NULL,
                html_url TEXT NOT NULL,
                is_private INTEGER NOT NULL,
                is_fork INTEGER NOT NULL,
                archived INTEGER NOT NULL,
                stargazers_count INTEGER NOT NULL,
                watchers_count INTEGER NOT NULL,
                forks_count INTEGER NOT NULL,
                open_issues_count INTEGER NOT NULL,
                language TEXT NOT NULL,
                visibility TEXT NOT NULL,
                topics_json TEXT NOT NULL,
                updated_at_utc TEXT NULL,
                pushed_at_utc TEXT NULL,
                starred_at_utc TEXT NOT NULL,
                sync_generation TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                UNIQUE(user_id, repository_id)
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_star_items_user_starred ON star_items(user_id, starred_at_utc DESC);",
            "CREATE INDEX IF NOT EXISTS ix_star_items_user_active ON star_items(user_id, pushed_at_utc DESC);",
            """
            CREATE TABLE IF NOT EXISTS star_categories (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                name TEXT NOT NULL COLLATE NOCASE,
                color TEXT NOT NULL,
                position INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                UNIQUE(user_id, name)
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_star_categories_user_position ON star_categories(user_id, position);",
            """
            CREATE TABLE IF NOT EXISTS star_category_memberships (
                category_id TEXT NOT NULL,
                item_key INTEGER NOT NULL,
                added_at_utc TEXT NOT NULL,
                PRIMARY KEY(category_id, item_key),
                FOREIGN KEY(category_id) REFERENCES star_categories(id) ON DELETE CASCADE,
                FOREIGN KEY(item_key) REFERENCES star_items(item_key) ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS star_sync_state (
                user_id TEXT PRIMARY KEY,
                last_incremental_utc TEXT NULL,
                last_full_utc TEXT NULL,
                is_complete INTEGER NOT NULL,
                is_syncing INTEGER NOT NULL,
                indexed_count INTEGER NOT NULL,
                error_message TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS star_pending_mutations (
                user_id TEXT NOT NULL,
                repository_id INTEGER NOT NULL,
                owner_login TEXT NOT NULL,
                repository_name TEXT NOT NULL,
                desired_starred INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(user_id, repository_id)
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_star_pending_mutations_user_created ON star_pending_mutations(user_id, created_at_utc);",
            """
            CREATE TABLE IF NOT EXISTS star_clear_transactions (
                transaction_id TEXT PRIMARY KEY,
                committed_at_utc TEXT NOT NULL
            );
            """,
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS star_items_fts USING fts5(
                owner_login, name, full_name, description, language, topics,
                content='star_items', content_rowid='item_key', tokenize='unicode61 remove_diacritics 2'
            );
            """,
            """
            CREATE TRIGGER IF NOT EXISTS star_items_ai AFTER INSERT ON star_items BEGIN
                INSERT INTO star_items_fts(rowid, owner_login, name, full_name, description, language, topics)
                VALUES (new.item_key, new.owner_login, new.name, new.full_name, new.description, new.language, new.topics_json);
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS star_items_ad AFTER DELETE ON star_items BEGIN
                INSERT INTO star_items_fts(star_items_fts, rowid, owner_login, name, full_name, description, language, topics)
                VALUES ('delete', old.item_key, old.owner_login, old.name, old.full_name, old.description, old.language, old.topics_json);
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS star_items_au AFTER UPDATE ON star_items BEGIN
                INSERT INTO star_items_fts(star_items_fts, rowid, owner_login, name, full_name, description, language, topics)
                VALUES ('delete', old.item_key, old.owner_login, old.name, old.full_name, old.description, old.language, old.topics_json);
                INSERT INTO star_items_fts(rowid, owner_login, name, full_name, description, language, topics)
                VALUES (new.item_key, new.owner_login, new.name, new.full_name, new.description, new.language, new.topics_json);
            END;
            """,
            $"PRAGMA user_version = {CurrentSchemaVersion};"
        ];

        foreach (string commandText in commands)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _initialized = true;
    }

    private long GetDatabasePhysicalBytes()
    {
        static long Length(string path)
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

        return Length(DatabasePath) + Length(DatabasePath + "-wal") + Length(DatabasePath + "-shm");
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new($"Data Source={DatabasePath}");
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async Task<SqliteConnection> OpenReadOnlyConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        };
        SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 1000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private Task InvokeClearStageHookAsync(
        StarLibraryClearStage stage,
        CancellationToken cancellationToken) =>
        _clearStageHook?.Invoke(stage, cancellationToken) ?? Task.CompletedTask;

    private static SqlQueryParts BuildQueryParts(StarLibraryQuery query)
    {
        List<string> clauses = ["i.user_id = $user_id"];
        bool usesFts = !string.IsNullOrWhiteSpace(BuildFtsQuery(query.SearchText));
        string join = usesFts ? "JOIN star_items_fts f ON f.rowid = i.item_key" : string.Empty;
        if (usesFts)
        {
            clauses.Add("f.star_items_fts MATCH $search");
        }

        if (!string.IsNullOrWhiteSpace(query.CategoryId))
        {
            clauses.Add("EXISTS (SELECT 1 FROM star_category_memberships cm WHERE cm.item_key = i.item_key AND cm.category_id = $category_id)");
        }

        switch (query.SmartList)
        {
            case StarSmartList.Uncategorized:
                clauses.Add("NOT EXISTS (SELECT 1 FROM star_category_memberships cm WHERE cm.item_key = i.item_key)");
                break;
            case StarSmartList.RecentlyStarred:
                clauses.Add("i.starred_at_utc >= $recent_since");
                break;
            case StarSmartList.RecentlyActive:
                clauses.Add("COALESCE(NULLIF(i.pushed_at_utc, ''), i.updated_at_utc) >= $recent_since");
                break;
            case StarSmartList.Archived:
                clauses.Add("i.archived = 1");
                break;
        }

        AddArrayClause(clauses, "i.language", "$language", query.Filter.Languages);
        AddArrayClause(clauses, "i.owner_login", "$owner", query.Filter.Owners);
        if (query.Filter.Topics.Length > 0)
        {
            clauses.Add("(" + string.Join(" OR ", query.Filter.Topics.Select((_, index) => $"i.topics_json LIKE $topic{index} ESCAPE '\\'")) + ")");
        }

        AddNullableBooleanClause(clauses, "i.is_private", query.Filter.IsPrivate, "private");
        AddNullableBooleanClause(clauses, "i.is_fork", query.Filter.IsFork, "fork");
        AddNullableBooleanClause(clauses, "i.archived", query.Filter.IsArchived, "archived");
        if (query.Filter.IsCategorized is bool categorized)
        {
            clauses.Add(categorized
                ? "EXISTS (SELECT 1 FROM star_category_memberships cm WHERE cm.item_key = i.item_key)"
                : "NOT EXISTS (SELECT 1 FROM star_category_memberships cm WHERE cm.item_key = i.item_key)");
        }

        return new SqlQueryParts(join, string.Join(" AND ", clauses), usesFts);
    }

    private static void AddQueryParameters(SqliteCommand command, StarLibraryQuery query, SqlQueryParts parts)
    {
        command.Parameters.AddWithValue("$user_id", query.UserId);
        if (parts.UsesFts)
        {
            command.Parameters.AddWithValue("$search", BuildFtsQuery(query.SearchText));
        }

        if (!string.IsNullOrWhiteSpace(query.CategoryId))
        {
            command.Parameters.AddWithValue("$category_id", query.CategoryId);
        }

        if (query.SmartList is StarSmartList.RecentlyStarred or StarSmartList.RecentlyActive)
        {
            command.Parameters.AddWithValue("$recent_since", DateTimeOffset.UtcNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture));
        }

        AddArrayParameters(command, "$language", query.Filter.Languages);
        AddArrayParameters(command, "$owner", query.Filter.Owners);
        for (int index = 0; index < query.Filter.Topics.Length; index++)
        {
            command.Parameters.AddWithValue($"$topic{index}", $"%{EscapeLike(query.Filter.Topics[index])}%");
        }
    }

    private static void AddArrayClause(List<string> clauses, string column, string parameterPrefix, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        clauses.Add($"{column} IN ({string.Join(", ", values.Select((_, index) => parameterPrefix + index))})");
    }

    private static void AddArrayParameters(SqliteCommand command, string prefix, IReadOnlyList<string> values)
    {
        for (int index = 0; index < values.Count; index++)
        {
            command.Parameters.AddWithValue(prefix + index, values[index]);
        }
    }

    private static void AddNullableBooleanClause(List<string> clauses, string column, bool? value, string parameterName)
    {
        if (value is bool selected)
        {
            clauses.Add($"{column} = ${parameterName}");
            // Values are constants to keep the query shape simple and injection-safe.
            clauses[^1] = $"{column} = {(selected ? 1 : 0)}";
        }
    }

    private static string GetOrderBy(StarLibrarySort sort) => sort switch
    {
        StarLibrarySort.RecentlyActive => "COALESCE(i.pushed_at_utc, i.updated_at_utc, '') DESC, i.full_name COLLATE NOCASE",
        StarLibrarySort.MostStars => "i.stargazers_count DESC, i.full_name COLLATE NOCASE",
        StarLibrarySort.Name => "i.full_name COLLATE NOCASE ASC",
        StarLibrarySort.LeastRecentlyActive => "COALESCE(i.pushed_at_utc, i.updated_at_utc, '') ASC, i.full_name COLLATE NOCASE",
        _ => "i.starred_at_utc DESC, i.full_name COLLATE NOCASE"
    };

    private static string BuildFtsQuery(string searchText)
    {
        string[] tokens = searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => new string(token.Where(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray()))
            .Where(static token => token.Length > 0)
            .Take(12)
            .ToArray();
        return tokens.Length == 0 ? string.Empty : string.Join(" AND ", tokens.Select(static token => $"\"{token.Replace("\"", "\"\"")}\"*"));
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task SavePendingMutationCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        StarPendingMutation mutation,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO star_pending_mutations (
                user_id, repository_id, owner_login, repository_name, desired_starred,
                created_at_utc, attempt_count, last_error)
            VALUES (
                $user_id, $repository_id, $owner_login, $repository_name, $desired_starred,
                $created_at_utc, 0, '')
            ON CONFLICT(user_id, repository_id) DO UPDATE SET
                owner_login = excluded.owner_login,
                repository_name = excluded.repository_name,
                desired_starred = excluded.desired_starred,
                created_at_utc = excluded.created_at_utc,
                attempt_count = 0,
                last_error = '';
            """;
        command.Parameters.AddWithValue("$user_id", mutation.UserId);
        command.Parameters.AddWithValue("$repository_id", mutation.RepositoryId);
        command.Parameters.AddWithValue("$owner_login", mutation.Owner);
        command.Parameters.AddWithValue("$repository_name", mutation.RepositoryName);
        command.Parameters.AddWithValue("$desired_starred", mutation.DesiredStarred ? 1 : 0);
        command.Parameters.AddWithValue("$created_at_utc", FormatDate(mutation.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRepositoryCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        GitHubStarredRepository starred,
        string syncGeneration,
        bool respectPendingUnstar,
        CancellationToken cancellationToken)
    {
        GitHubRepository repository = starred.Repository;
        if (respectPendingUnstar)
        {
            await using SqliteCommand pending = connection.CreateCommand();
            pending.Transaction = transaction;
            pending.CommandText = """
                SELECT COUNT(*) FROM star_pending_mutations
                WHERE user_id = $user_id AND repository_id = $repository_id AND desired_starred = 0;
                """;
            pending.Parameters.AddWithValue("$user_id", userId);
            pending.Parameters.AddWithValue("$repository_id", repository.Id);
            if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO star_items (
                user_id, repository_id, name, full_name, owner_login, owner_avatar_url,
                description, default_branch, html_url, is_private, is_fork, archived,
                stargazers_count, watchers_count, forks_count, open_issues_count, language,
                visibility, topics_json, updated_at_utc, pushed_at_utc, starred_at_utc,
                sync_generation, last_seen_utc)
            VALUES (
                $user_id, $repository_id, $name, $full_name, $owner_login, $owner_avatar_url,
                $description, $default_branch, $html_url, $is_private, $is_fork, $archived,
                $stargazers_count, $watchers_count, $forks_count, $open_issues_count, $language,
                $visibility, $topics_json, $updated_at_utc, $pushed_at_utc, $starred_at_utc,
                $sync_generation, $last_seen_utc)
            ON CONFLICT(user_id, repository_id) DO UPDATE SET
                name = excluded.name,
                full_name = excluded.full_name,
                owner_login = excluded.owner_login,
                owner_avatar_url = excluded.owner_avatar_url,
                description = excluded.description,
                default_branch = excluded.default_branch,
                html_url = excluded.html_url,
                is_private = excluded.is_private,
                is_fork = excluded.is_fork,
                archived = excluded.archived,
                stargazers_count = excluded.stargazers_count,
                watchers_count = excluded.watchers_count,
                forks_count = excluded.forks_count,
                open_issues_count = excluded.open_issues_count,
                language = excluded.language,
                visibility = excluded.visibility,
                topics_json = excluded.topics_json,
                updated_at_utc = excluded.updated_at_utc,
                pushed_at_utc = excluded.pushed_at_utc,
                starred_at_utc = excluded.starred_at_utc,
                sync_generation = excluded.sync_generation,
                last_seen_utc = excluded.last_seen_utc;
            """;
        AddRepositoryParameters(command, userId, starred, syncGeneration);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddRepositoryParameters(SqliteCommand command, string userId, GitHubStarredRepository starred, string syncGeneration)
    {
        GitHubRepository repository = starred.Repository;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$repository_id", repository.Id);
        command.Parameters.AddWithValue("$name", repository.Name ?? string.Empty);
        command.Parameters.AddWithValue("$full_name", repository.FullName ?? string.Empty);
        command.Parameters.AddWithValue("$owner_login", repository.Owner?.Login ?? string.Empty);
        command.Parameters.AddWithValue("$owner_avatar_url", repository.Owner?.AvatarUrl ?? string.Empty);
        command.Parameters.AddWithValue("$description", repository.Description ?? string.Empty);
        command.Parameters.AddWithValue("$default_branch", repository.DefaultBranch ?? string.Empty);
        command.Parameters.AddWithValue("$html_url", repository.HtmlUrl ?? string.Empty);
        command.Parameters.AddWithValue("$is_private", repository.Private ? 1 : 0);
        command.Parameters.AddWithValue("$is_fork", repository.Fork ? 1 : 0);
        command.Parameters.AddWithValue("$archived", repository.Archived ? 1 : 0);
        command.Parameters.AddWithValue("$stargazers_count", repository.StargazersCount);
        command.Parameters.AddWithValue("$watchers_count", repository.WatchersCount);
        command.Parameters.AddWithValue("$forks_count", repository.ForksCount);
        command.Parameters.AddWithValue("$open_issues_count", repository.OpenIssuesCount);
        command.Parameters.AddWithValue("$language", repository.Language ?? string.Empty);
        command.Parameters.AddWithValue("$visibility", repository.Visibility ?? string.Empty);
        command.Parameters.AddWithValue("$topics_json", JsonSerializer.Serialize(repository.Topics ?? []));
        command.Parameters.AddWithValue("$updated_at_utc", DbDate(repository.UpdatedAt));
        command.Parameters.AddWithValue("$pushed_at_utc", DbDate(repository.PushedAt));
        command.Parameters.AddWithValue("$starred_at_utc", FormatDate(starred.StarredAt));
        command.Parameters.AddWithValue("$sync_generation", syncGeneration);
        command.Parameters.AddWithValue("$last_seen_utc", FormatDate(DateTimeOffset.UtcNow));
    }

    private static GitHubRepository ReadRepository(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        FullName = reader.GetString(2),
        Owner = new GitHubRepositoryOwner { Login = reader.GetString(3), AvatarUrl = reader.GetString(4) },
        Description = reader.GetString(5),
        DefaultBranch = reader.GetString(6),
        HtmlUrl = reader.GetString(7),
        Private = reader.GetInt64(8) != 0,
        Fork = reader.GetInt64(9) != 0,
        Archived = reader.GetInt64(10) != 0,
        StargazersCount = reader.GetInt32(11),
        WatchersCount = reader.GetInt32(12),
        ForksCount = reader.GetInt32(13),
        OpenIssuesCount = reader.GetInt32(14),
        Language = reader.GetString(15),
        Visibility = reader.GetString(16),
        Topics = ParseTopics(reader.GetString(17)),
        UpdatedAt = ReadDate(reader, 18),
        PushedAt = ReadDate(reader, 19)
    };

    private static async Task<IReadOnlyList<StarCategory>> ReadCategoriesAsync(SqliteConnection connection, string userId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.user_id, c.name, c.color, c.position, COUNT(m.item_key), c.created_at_utc, c.updated_at_utc
            FROM star_categories c
            LEFT JOIN star_category_memberships m ON m.category_id = c.id
            WHERE c.user_id = $user_id
            GROUP BY c.id
            ORDER BY c.position, c.name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        List<StarCategory> categories = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            categories.Add(ReadCategory(reader));
        }

        return categories;
    }

    private static async Task<Dictionary<long, List<StarCategory>>> ReadCategoriesForRepositoriesAsync(
        SqliteConnection connection,
        string userId,
        IReadOnlyList<long> repositoryIds,
        CancellationToken cancellationToken)
    {
        Dictionary<long, List<StarCategory>> result = [];
        if (repositoryIds.Count == 0)
        {
            return result;
        }

        await using SqliteCommand command = connection.CreateCommand();
        string[] parameters = repositoryIds.Select((_, index) => $"$repository{index}").ToArray();
        command.CommandText = $"""
            SELECT i.repository_id, c.id, c.user_id, c.name, c.color, c.position,
                   (SELECT COUNT(*) FROM star_category_memberships x WHERE x.category_id = c.id),
                   c.created_at_utc, c.updated_at_utc
            FROM star_category_memberships m
            JOIN star_items i ON i.item_key = m.item_key
            JOIN star_categories c ON c.id = m.category_id
            WHERE i.user_id = $user_id AND i.repository_id IN ({string.Join(", ", parameters)})
            ORDER BY c.position, c.name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        for (int index = 0; index < repositoryIds.Count; index++)
        {
            command.Parameters.AddWithValue(parameters[index], repositoryIds[index]);
        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long repositoryId = reader.GetInt64(0);
            if (!result.TryGetValue(repositoryId, out List<StarCategory>? list))
            {
                list = [];
                result[repositoryId] = list;
            }

            list.Add(new StarCategory(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                ReadDate(reader, 7) ?? DateTimeOffset.MinValue,
                ReadDate(reader, 8) ?? DateTimeOffset.MinValue));
        }

        return result;
    }

    private static StarCategory ReadCategory(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        ReadDate(reader, 6) ?? DateTimeOffset.MinValue,
        ReadDate(reader, 7) ?? DateTimeOffset.MinValue);

    private static async Task<StarSyncState> ReadSyncStateAsync(SqliteConnection connection, string userId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_incremental_utc, last_full_utc, is_complete, is_syncing, indexed_count, error_message
            FROM star_sync_state WHERE user_id = $user_id;
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StarSyncState(
                userId,
                ReadDate(reader, 0),
                ReadDate(reader, 1),
                reader.GetInt64(2) != 0,
                reader.GetInt64(3) != 0,
                reader.GetInt32(4),
                reader.GetString(5))
            : StarSyncState.Empty(userId);
    }

    private async Task ExecuteCategoryCommandAsync(string commandText, string userId, string categoryId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$id", categoryId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateMembershipsAsync(
        string userId,
        string categoryId,
        IReadOnlyCollection<long> repositoryIds,
        bool add,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (long repositoryId in repositoryIds.Distinct())
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = add
                    ? """
                      INSERT OR IGNORE INTO star_category_memberships(category_id, item_key, added_at_utc)
                      SELECT $category_id, item_key, $added_at_utc
                      FROM star_items WHERE user_id = $user_id AND repository_id = $repository_id;
                      """
                    : """
                      DELETE FROM star_category_memberships
                      WHERE category_id = $category_id AND item_key IN (
                          SELECT item_key FROM star_items WHERE user_id = $user_id AND repository_id = $repository_id);
                      """;
                command.Parameters.AddWithValue("$category_id", categoryId);
                command.Parameters.AddWithValue("$user_id", userId);
                command.Parameters.AddWithValue("$repository_id", repositoryId);
                command.Parameters.AddWithValue("$added_at_utc", FormatDate(DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveRepositoryCoreAsync<T>(string userId, string predicate, T value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedCoreAsync(cancellationToken);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM star_items WHERE user_id = $user_id AND {predicate};";
            command.Parameters.AddWithValue("$user_id", userId);
            command.Parameters.AddWithValue("$value", value!);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void AddArrayClause(List<string> clauses, string column, string parameterPrefix, string[] values) =>
        AddArrayClause(clauses, column, parameterPrefix, (IReadOnlyList<string>)values);

    private static string NormalizeCategoryName(string name)
    {
        string normalized = (name ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 64)
        {
            throw new ArgumentException("Category names must contain between 1 and 64 characters.", nameof(name));
        }

        return normalized;
    }

    private static string NormalizeColor(string color)
    {
        string normalized = string.IsNullOrWhiteSpace(color) ? "#74BEA7" : color.Trim().ToUpperInvariant();
        return normalized.Length == 7 && normalized[0] == '#' && normalized[1..].All(Uri.IsHexDigit)
            ? normalized
            : "#74BEA7";
    }

    private static string[] ParseTopics(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;
    }

    private static object DbDate(DateTimeOffset? value) => value.HasValue ? FormatDate(value.Value) : DBNull.Value;

    private static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private sealed record SqlQueryParts(string JoinClause, string WhereClause, bool UsesFts);
}
