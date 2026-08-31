using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class StarLibraryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubStarLibraryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Initialize_CreatesVersionedFtsSchema()
    {
        SqliteStarLibraryStore store = CreateStore();

        await store.InitializeAsync();

        await using SqliteConnection connection = new($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, (long)(await version.ExecuteScalarAsync())!);
        await using SqliteCommand tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('star_items','star_categories','star_category_memberships','star_items_fts','star_sync_state','star_pending_mutations','star_clear_transactions');";
        Assert.Equal(7L, (long)(await tables.ExecuteScalarAsync())!);
        Assert.Equal(CacheOwnerHealth.Healthy, (await store.InspectAsync()).Health);
    }

    [Fact]
    public async Task Inspection_ReportsMissingSchemaObjectAsUnhealthy()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        await ExecuteSqlAsync(store.DatabasePath, "DROP TRIGGER star_items_ai;");

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Contains("star_items_ai", inspection.Detail, StringComparison.Ordinal);
        Assert.True(inspection.PhysicalBytes > 0);
        Assert.Equal(3, inspection.Components[CacheMetricKeys.SchemaVersion]);
    }

    [Fact]
    public async Task Inspection_MissingDatabaseDoesNotCreateIt()
    {
        SqliteStarLibraryStore store = CreateStore();

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Healthy, inspection.Health);
        Assert.Equal(0, inspection.Components[CacheMetricKeys.DatabaseExists]);
        Assert.False(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task Inspection_MissingCurrentSchemaObjectDoesNotRepairIt()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        await ExecuteSqlAsync(store.DatabasePath, "DROP TRIGGER star_items_ai;");

        CacheStoreInspection inspection = await new SqliteStarLibraryStore(store.DatabasePath).InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Equal(0L, await ExecuteScalarAsync(
            store.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'star_items_ai';"));
    }

    [Fact]
    public async Task Inspection_FutureSchemaDoesNotDowngradeAndInitializationRejectsIt()
    {
        Directory.CreateDirectory(_root);
        string databasePath = Path.Combine(_root, "future-stars.db");
        await ExecuteSqlAsync(databasePath, "PRAGMA user_version = 99;");
        SqliteStarLibraryStore store = new(databasePath);

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Contains("99", inspection.Detail, StringComparison.Ordinal);
        Assert.Equal(99L, await ExecuteScalarAsync(databasePath, "PRAGMA user_version;"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync());
    }

    [Fact]
    public async Task ClearAll_FailureBeforeCommitRollsBackEveryDurableTable()
    {
        Directory.CreateDirectory(_root);
        string databasePath = Path.Combine(_root, "rollback-stars.db");
        SqliteStarLibraryStore seed = new(databasePath);
        await SeedClearFixtureAsync(seed);
        SqliteStarLibraryStore failing = new(
            databasePath,
            (stage, _) => stage == StarLibraryClearStage.AfterItems
                ? Task.FromException(new IOException("injected clear failure"))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => failing.ClearAllAsync());

        await AssertClearFixturePresentAsync(new SqliteStarLibraryStore(databasePath));
        Assert.Empty(await new SqliteStarLibraryStore(databasePath).GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task ClearAll_CancellationBeforeCommitRollsBackEveryDurableTable()
    {
        Directory.CreateDirectory(_root);
        string databasePath = Path.Combine(_root, "cancel-stars.db");
        SqliteStarLibraryStore seed = new(databasePath);
        await SeedClearFixtureAsync(seed);
        using CancellationTokenSource cancellation = new();
        SqliteStarLibraryStore canceling = new(
            databasePath,
            (stage, token) =>
            {
                if (stage == StarLibraryClearStage.BeforeCommit)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceling.ClearAllAsync(cancellation.Token));

        await AssertClearFixturePresentAsync(new SqliteStarLibraryStore(databasePath));
        Assert.Empty(await new SqliteStarLibraryStore(databasePath).GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task ClearAll_CommitsEveryDurableTableTogether()
    {
        SqliteStarLibraryStore store = CreateStore();
        await SeedClearFixtureAsync(store);

        await store.ClearAllAsync();

        Assert.Empty((await store.QueryAsync(Query("u1"))).Items);
        Assert.Empty(await store.GetCategoriesAsync("u1"));
        Assert.Empty(await store.GetPendingMutationsAsync("u1"));
        Assert.Equal(0L, await ExecuteScalarAsync(
            store.DatabasePath,
            "SELECT (SELECT COUNT(*) FROM star_sync_state) + (SELECT COUNT(*) FROM star_items_fts_docsize);"));
        Assert.Empty(await store.GetCommittedClearTransactionsAsync());
    }

    [Fact]
    public async Task Query_IsAccountPartitionedAndSearchesAllIndexedFields()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.UpsertPageAsync("user-one", [Star(1, "microsoft", "terminal", "Windows console", "C++", ["windows", "terminal"])], "g1");
        await store.UpsertPageAsync("user-two", [Star(2, "octo", "private-notes", "other account", "Rust", ["notes"])], "g2");

        StarLibraryPage byTopic = await store.QueryAsync(Query("user-one", search: "windows"));
        StarLibraryPage byLanguage = await store.QueryAsync(Query("user-one", search: "C++"));
        StarLibraryPage otherPartition = await store.QueryAsync(Query("user-one", search: "private-notes"));

        Assert.Single(byTopic.Items);
        Assert.Single(byLanguage.Items);
        Assert.Empty(otherPartition.Items);
        Assert.Equal("microsoft/terminal", byTopic.Items[0].Repository.FullName);
    }

    [Fact]
    public async Task Categories_AreManyToManyAndDeleteDoesNotDeleteStars()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.UpsertPageAsync("u1", [Star(1, "a", "one"), Star(2, "a", "two")], "g1");
        StarCategory first = await store.CreateCategoryAsync("u1", "Work", "#74BEA7");
        StarCategory second = await store.CreateCategoryAsync("u1", "Read later", "#5B9BD5");

        await store.AddToCategoryAsync("u1", first.Id, [1, 2]);
        await store.AddToCategoryAsync("u1", second.Id, [1]);

        StarLibraryPage work = await store.QueryAsync(Query("u1", categoryId: first.Id));
        StarLibraryPage readLater = await store.QueryAsync(Query("u1", categoryId: second.Id));
        Assert.Equal(2, work.TotalCount);
        Assert.Single(readLater.Items);
        Assert.Equal(2, readLater.Items[0].Categories.Count);

        await store.DeleteCategoryAsync("u1", first.Id);

        StarLibraryPage all = await store.QueryAsync(Query("u1"));
        Assert.Equal(2, all.TotalCount);
        Assert.DoesNotContain(all.Items.SelectMany(static item => item.Categories), category => category.Id == first.Id);
    }

    [Fact]
    public async Task ReorderCategory_ProducesStableContiguousOrder()
    {
        SqliteStarLibraryStore store = CreateStore();
        StarCategory one = await store.CreateCategoryAsync("u1", "One", "#74BEA7");
        await store.CreateCategoryAsync("u1", "Two", "#5B9BD5");
        StarCategory three = await store.CreateCategoryAsync("u1", "Three", "#A77BD8");

        await store.ReorderCategoryAsync("u1", three.Id, 0);

        StarCategory[] categories = (await store.GetCategoriesAsync("u1")).ToArray();
        Assert.Equal(["Three", "One", "Two"], categories.Select(static category => category.Name).ToArray());
        Assert.Equal([0, 1, 2], categories.Select(static category => category.Position).ToArray());
        Assert.Equal(one.Id, categories[1].Id);
    }

    [Fact]
    public async Task FullReconciliation_PrunesOnlyAfterSuccessfulCompletion()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.UpsertPageAsync("u1", [Star(1, "a", "one"), Star(2, "a", "two")], "generation-one");
        await store.CompleteFullSyncAsync("u1", "generation-one");
        await store.UpsertPageAsync("u1", [Star(1, "a", "one")], "generation-two");

        Assert.Equal(2, (await store.QueryAsync(Query("u1"))).TotalCount);

        await store.CompleteFullSyncAsync("u1", "generation-two");

        StarLibraryPage reconciled = await store.QueryAsync(Query("u1"));
        Assert.Single(reconciled.Items);
        Assert.Equal(1, reconciled.Items[0].Repository.Id);
    }

    [Fact]
    public async Task FiltersAndSort_ComposeWithoutMutatingRows()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.UpsertPageAsync("u1",
        [
            Star(1, "z", "archived", language: "C#", archived: true, stars: 4),
            Star(2, "a", "popular", language: "Rust", archived: false, stars: 900)
        ], "g1");

        StarLibraryPage active = await store.QueryAsync(Query(
            "u1",
            filter: new StarLibraryFilter([], [], [], IsArchived: false),
            sort: StarLibrarySort.MostStars));
        StarLibraryPage archived = await store.QueryAsync(Query("u1", smartList: StarSmartList.Archived));

        Assert.Single(active.Items);
        Assert.Equal("a/popular", active.Items[0].Repository.FullName);
        Assert.Single(archived.Items);
        Assert.Equal("z/archived", archived.Items[0].Repository.FullName);
    }

    [Fact]
    public async Task SmartListCounts_AreScopedAndAccountPartitioned()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.UpsertPageAsync("u1",
        [
            Star(1, "a", "active"),
            Star(2, "a", "archived", archived: true)
        ], "g1");
        await store.UpsertPageAsync("u2", [Star(3, "b", "other")], "g2");
        StarCategory category = await store.CreateCategoryAsync("u1", "Work", "#74BEA7");
        await store.AddToCategoryAsync("u1", category.Id, [1]);

        var counts = await store.GetSmartListCountsAsync("u1");

        Assert.Equal(2, counts[StarSmartList.All]);
        Assert.Equal(1, counts[StarSmartList.Uncategorized]);
        Assert.Equal(1, counts[StarSmartList.Archived]);
        Assert.Equal(2, counts[StarSmartList.RecentlyStarred]);
        Assert.Equal(2, counts[StarSmartList.RecentlyActive]);
    }

    [Fact]
    public async Task RecentSmartLists_UseTheSameThirtyDayWindowAsTheirCounts()
    {
        SqliteStarLibraryStore store = CreateStore();
        GitHubStarredRepository recent = Star(1, "a", "recent");
        GitHubStarredRepository old = Star(2, "a", "old");
        old.StarredAt = DateTimeOffset.UtcNow.AddDays(-60);
        old.Repository.PushedAt = DateTimeOffset.UtcNow.AddDays(-60);
        old.Repository.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-60);
        await store.UpsertPageAsync("u1", [recent, old], "g1");

        StarLibraryPage recentlyStarred = await store.QueryAsync(Query("u1", smartList: StarSmartList.RecentlyStarred));
        StarLibraryPage recentlyActive = await store.QueryAsync(Query("u1", smartList: StarSmartList.RecentlyActive));
        IReadOnlyDictionary<StarSmartList, int> counts = await store.GetSmartListCountsAsync("u1");

        Assert.Single(recentlyStarred.Items);
        Assert.Single(recentlyActive.Items);
        Assert.Equal(recent.Repository.Id, recentlyStarred.Items[0].Repository.Id);
        Assert.Equal(recent.Repository.Id, recentlyActive.Items[0].Repository.Id);
        Assert.Equal(recentlyStarred.TotalCount, counts[StarSmartList.RecentlyStarred]);
        Assert.Equal(recentlyActive.TotalCount, counts[StarSmartList.RecentlyActive]);
    }

    [Fact]
    public async Task PendingMutations_ArePartitionedLastWriteWinsAndConditionallyRemoved()
    {
        SqliteStarLibraryStore store = CreateStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.SavePendingMutationAsync(new("u1", 1, "owner", "repo", false, now, 0, string.Empty));
        await store.SavePendingMutationAsync(new("u2", 1, "owner", "repo", false, now, 0, string.Empty));
        await store.SavePendingMutationAsync(new("u1", 1, "owner", "repo", true, now.AddSeconds(1), 0, string.Empty));

        StarPendingMutation pending = Assert.Single(await store.GetPendingMutationsAsync("u1"));
        Assert.True(pending.DesiredStarred);
        await store.RemovePendingMutationAsync("u1", 1, desiredStarred: false);
        Assert.Single(await store.GetPendingMutationsAsync("u1"));
        await store.RecordPendingMutationFailureAsync("u1", 1, desiredStarred: true, "offline");

        pending = Assert.Single(await store.GetPendingMutationsAsync("u1"));
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal("offline", pending.LastError);
        Assert.Single(await store.GetPendingMutationsAsync("u2"));
    }

    [Fact]
    public async Task PendingLocalIntent_IsNotOverwrittenByRemoteReconciliation()
    {
        SqliteStarLibraryStore store = CreateStore();
        GitHubStarredRepository repository = Star(1, "owner", "repo");
        await store.UpsertPageAsync("u1", [repository], "initial");
        await store.SavePendingMutationAsync(new("u1", 1, "owner", "repo", false, DateTimeOffset.UtcNow, 0, string.Empty));
        await store.RemoveRepositoryAsync("u1", 1);

        await store.UpsertPageAsync("u1", [repository], "remote-stale");
        Assert.Empty((await store.QueryAsync(Query("u1"))).Items);

        await store.SavePendingMutationAsync(new("u1", 1, "owner", "repo", true, DateTimeOffset.UtcNow, 0, string.Empty));
        await store.UpsertPageAsync("u1", [repository], "local-restore");
        await store.CompleteFullSyncAsync("u1", "different-generation");
        Assert.Single((await store.QueryAsync(Query("u1"))).Items);
    }

    [Fact]
    public async Task ApplyPendingUnstar_RollsBackIntentAndProjectionWhenDeleteFails()
    {
        SqliteStarLibraryStore store = CreateStore();
        GitHubStarredRepository repository = Star(1, "owner", "repo");
        await store.UpsertPageAsync("u1", [repository], "initial");
        StarCategory category = await store.CreateCategoryAsync("u1", "Work", "#74BEA7");
        await store.AddToCategoryAsync("u1", category.Id, [1]);
        await ExecuteSqlAsync(store.DatabasePath, """
            CREATE TRIGGER fail_star_delete BEFORE DELETE ON star_items
            BEGIN SELECT RAISE(ABORT, 'forced delete failure'); END;
            """);

        StarPendingMutation mutation = new("u1", 1, "owner", "repo", false, DateTimeOffset.UtcNow, 0, string.Empty);
        await Assert.ThrowsAsync<SqliteException>(() => store.ApplyPendingUnstarAsync(mutation));

        StarLibraryItem visible = Assert.Single((await store.QueryAsync(Query("u1"))).Items);
        Assert.Contains(visible.Categories, candidate => candidate.Id == category.Id);
        Assert.Empty(await store.GetPendingMutationsAsync("u1"));
    }

    [Fact]
    public async Task ApplyPendingRestore_RollsBackInverseIntentWhenProjectionRestoreFails()
    {
        SqliteStarLibraryStore store = CreateStore();
        GitHubStarredRepository repository = Star(1, "owner", "repo");
        await store.UpsertPageAsync("u1", [repository], "initial");
        StarCategory category = await store.CreateCategoryAsync("u1", "Work", "#74BEA7");
        await store.AddToCategoryAsync("u1", category.Id, [1]);
        await store.ApplyPendingUnstarAsync(new("u1", 1, "owner", "repo", false, DateTimeOffset.UtcNow, 0, string.Empty));
        await ExecuteSqlAsync(store.DatabasePath, """
            CREATE TRIGGER fail_star_insert BEFORE INSERT ON star_items
            BEGIN SELECT RAISE(ABORT, 'forced insert failure'); END;
            """);

        StarPendingMutation restore = new("u1", 1, "owner", "repo", true, DateTimeOffset.UtcNow, 0, string.Empty);
        await Assert.ThrowsAsync<SqliteException>(() => store.ApplyPendingRestoreAsync(restore, repository, [category.Id]));

        Assert.Empty((await store.QueryAsync(Query("u1"))).Items);
        StarPendingMutation pending = Assert.Single(await store.GetPendingMutationsAsync("u1"));
        Assert.False(pending.DesiredStarred);
    }

    private static async Task ExecuteSqlAsync(string databasePath, string commandText)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(string databasePath, string commandText)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task SeedClearFixtureAsync(SqliteStarLibraryStore store)
    {
        await store.UpsertPageAsync("u1", [Star(1, "owner", "repo")], "g1");
        StarCategory category = await store.CreateCategoryAsync("u1", "Work", "#74BEA7");
        await store.AddToCategoryAsync("u1", category.Id, [1]);
        await store.SavePendingMutationAsync(new(
            "u1",
            1,
            "owner",
            "repo",
            false,
            DateTimeOffset.UtcNow,
            0,
            string.Empty));
        await store.SaveSyncStateAsync(new(
            "u1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            true,
            false,
            1,
            string.Empty));
    }

    private static async Task AssertClearFixturePresentAsync(SqliteStarLibraryStore store)
    {
        Assert.Single((await store.QueryAsync(Query("u1"))).Items);
        Assert.Single(await store.GetCategoriesAsync("u1"));
        Assert.Single(await store.GetPendingMutationsAsync("u1"));
        Assert.Equal(1L, await ExecuteScalarAsync(store.DatabasePath, "SELECT COUNT(*) FROM star_sync_state;"));
    }

    private SqliteStarLibraryStore CreateStore()
    {
        Directory.CreateDirectory(_root);
        return new SqliteStarLibraryStore(Path.Combine(_root, "stars.db"));
    }

    private static StarLibraryQuery Query(
        string userId,
        string search = "",
        string? categoryId = null,
        StarSmartList smartList = StarSmartList.All,
        StarLibraryFilter? filter = null,
        StarLibrarySort sort = StarLibrarySort.RecentlyStarred) =>
        new(userId, smartList, categoryId, search, filter ?? StarLibraryFilter.Empty, sort, 0, 100);

    private static GitHubStarredRepository Star(
        long id,
        string owner,
        string name,
        string description = "description",
        string language = "C#",
        string[]? topics = null,
        bool archived = false,
        int stars = 10) => new()
        {
            StarredAt = DateTimeOffset.UtcNow.AddMinutes(-id),
            Repository = new GitHubRepository
            {
                Id = id,
                Name = name,
                FullName = $"{owner}/{name}",
                Description = description,
                HtmlUrl = $"https://github.com/{owner}/{name}",
                Language = language,
                Topics = topics ?? [],
                Archived = archived,
                StargazersCount = stars,
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-id),
                PushedAt = DateTimeOffset.UtcNow.AddDays(-id),
                Owner = new GitHubRepositoryOwner { Login = owner }
            }
        };
}
