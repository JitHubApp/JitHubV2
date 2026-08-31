using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubStarLibraryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubStarServiceTests", Guid.NewGuid().ToString("N"));

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
    public async Task LoadCachedPage_ReturnsIndexedRowsWithoutStartingRemoteSynchronization()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.UpsertPageAsync("u1", [Star(1, "owner", "repo")], "initial");
        IGitHubStarQueryService remote = Substitute.For<IGitHubStarQueryService>();
        GitHubStarLibraryService service = CreateService(
            store,
            Substitute.For<IGitHubClientService>(),
            remote);

        StarLibraryPage page = await service.LoadCachedPageAsync(
            "token",
            "u1",
            Query("u1"));

        Assert.Single(page.Items);
        await remote.DidNotReceiveWithAnyArgs().GetPageAsync(
            default!,
            default!,
            default,
            default,
            default,
            default);
    }

    [Fact]
    public async Task OfflineUnstarAndUndo_UpdateLocalLibraryAndKeepDurableIntent()
    {
        SqliteStarLibraryStore store = CreateStore();
        GitHubStarredRepository starred = Star(1, "owner", "repo");
        await store.UpsertPageAsync("u1", [starred], "initial");
        StarCategory category = await store.CreateCategoryAsync("u1", "Work", "#74BEA7");
        await store.AddToCategoryAsync("u1", category.Id, [1]);
        StarLibraryItem item = Assert.Single((await store.QueryAsync(Query("u1"))).Items);

        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.UnstarRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("offline")));
        client.StarRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("offline")));
        GitHubStarLibraryService service = CreateService(store, client);

        await service.UnstarAsync("token", "u1", item);

        Assert.Empty((await store.QueryAsync(Query("u1"))).Items);
        await WaitUntilAsync(async () =>
            (await store.GetPendingMutationsAsync("u1")).SingleOrDefault() is { DesiredStarred: false, AttemptCount: > 0 });

        await service.RestoreStarAsync("token", "u1", item, [category.Id]);

        StarLibraryItem restored = Assert.Single((await store.QueryAsync(Query("u1"))).Items);
        Assert.Contains(restored.Categories, candidate => candidate.Id == category.Id);
        await WaitUntilAsync(async () =>
            (await store.GetPendingMutationsAsync("u1")).SingleOrDefault() is { DesiredStarred: true, AttemptCount: > 0 });
    }

    [Fact]
    public async Task FlushPendingMutation_DeliversRemoteUnstarAndClearsOutbox()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.SavePendingMutationAsync(new(
            "u1",
            42,
            "owner",
            "repo",
            false,
            DateTimeOffset.UtcNow,
            0,
            string.Empty));
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.UnstarRepositoryAsync("token", "owner", "repo", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        GitHubStarLibraryService service = CreateService(store, client);

        await service.FlushPendingMutationsAsync("token", "u1");

        await client.Received(1).UnstarRepositoryAsync("token", "owner", "repo", Arg.Any<CancellationToken>());
        Assert.Empty(await store.GetPendingMutationsAsync("u1"));
    }

    [Fact]
    public async Task RepositoryActionStarMutation_InvalidatesLibraryProfileAndDashboardCaches()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        IGitHubQueryService cache = Substitute.For<IGitHubQueryService>();
        cache.InvalidateTagsAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        GitHubStarLibraryService service = new(
            store,
            Substitute.For<IGitHubStarQueryService>(),
            Substitute.For<IGitHubClientService>(),
            cache,
            Substitute.For<ITelemetryService>());

        await service.NotifyRepositoryStarStateChangedAsync(
            "token",
            "u1",
            "owner/repo",
            isStarred: false);

        await cache.Received(1).InvalidateTagsAsync(
            "u1",
            Arg.Is<IReadOnlyCollection<string>>(tags =>
                tags.Contains("me-stars") &&
                tags.Contains("star-library") &&
                tags.Contains("profile-stars") &&
                tags.Contains("dashboard-starred-repos")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CategoryMutations_EmitCanonicalIdentifierFreeTelemetry()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertPageAsync("u1", [Star(42, "owner", "repo")], "initial");
        ITelemetryService telemetry = Substitute.For<ITelemetryService>();
        GitHubStarLibraryService service = new(
            store,
            Substitute.For<IGitHubStarQueryService>(),
            Substitute.For<IGitHubClientService>(),
            Substitute.For<IGitHubQueryService>(),
            telemetry);

        StarCategory category = await service.CreateCategoryAsync("u1", "Private category name", "#74BEA7");
        await service.UpdateCategoryAsync("u1", category.Id, "Renamed private category", "#5E9ED6");
        await service.ReorderCategoryAsync("u1", category.Id, 0);
        await service.AddToCategoryAsync("u1", category.Id, [42]);
        await service.RemoveFromCategoryAsync("u1", category.Id, [42]);
        await service.DeleteCategoryAsync("u1", category.Id);

        telemetry.Received(1).TrackEvent(
            "stars.category.created",
            Arg.Is<IReadOnlyDictionary<string, string?>>(properties => IsSafeCategoryTelemetry(properties, "create")));
        telemetry.Received(1).TrackEvent(
            "stars.category.updated",
            Arg.Is<IReadOnlyDictionary<string, string?>>(properties => IsSafeCategoryTelemetry(properties, "update")));
        telemetry.Received(1).TrackEvent(
            "stars.category.updated",
            Arg.Is<IReadOnlyDictionary<string, string?>>(properties => IsSafeCategoryTelemetry(properties, "reorder")));
        telemetry.Received(1).TrackEvent(
            "stars.category.deleted",
            Arg.Is<IReadOnlyDictionary<string, string?>>(properties => IsSafeCategoryTelemetry(properties, "delete")));
        telemetry.Received(1).TrackEvent(
            "stars.membership.changed",
            Arg.Is<IReadOnlyDictionary<string, string?>>(properties => IsSafeMembershipTelemetry(properties, "add")));
        telemetry.Received(1).TrackEvent(
            "stars.membership.changed",
            Arg.Is<IReadOnlyDictionary<string, string?>>(properties => IsSafeMembershipTelemetry(properties, "remove")));
    }

    [Fact]
    public async Task CategoryMutations_RemainCommittedWhenTelemetryThrows()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        ITelemetryService telemetry = Substitute.For<ITelemetryService>();
        telemetry
            .When(service => service.TrackEvent(
                Arg.Is<string>(name => name.StartsWith("stars.category.", StringComparison.Ordinal)),
                Arg.Any<IReadOnlyDictionary<string, string?>>()))
            .Do(_ => throw new InvalidOperationException("Injected telemetry failure."));
        GitHubStarLibraryService service = new(
            store,
            Substitute.For<IGitHubStarQueryService>(),
            Substitute.For<IGitHubClientService>(),
            Substitute.For<IGitHubQueryService>(),
            telemetry);

        StarCategory category = await service.CreateCategoryAsync("u1", "Created", "#74BEA7");
        Assert.Contains(await store.GetCategoriesAsync("u1"), item => item.Id == category.Id && item.Name == "Created");

        StarCategory later = await service.CreateCategoryAsync("u1", "Later", "#D17AA6");
        StarCategory updated = await service.UpdateCategoryAsync("u1", category.Id, "Updated", "#5E9ED6");
        IReadOnlyList<StarCategory> afterUpdate = await store.GetCategoriesAsync("u1");
        Assert.Contains(afterUpdate, item => item.Id == category.Id && item.Name == "Updated" && item.Color == "#5E9ED6");

        await service.ReorderCategoryAsync("u1", later.Id, 0);
        IReadOnlyList<StarCategory> afterReorder = await store.GetCategoriesAsync("u1");

        Assert.Equal("Updated", updated.Name);
        Assert.Equal(later.Id, afterReorder[0].Id);
        Assert.Contains(afterReorder, item => item.Id == category.Id);

        await service.DeleteCategoryAsync("u1", category.Id);

        Assert.DoesNotContain(await store.GetCategoriesAsync("u1"), item => item.Id == category.Id);
        Assert.Contains(await store.GetCategoriesAsync("u1"), item => item.Id == later.Id);
    }

    [Fact]
    public async Task RepositoryActionStarMutation_UpsertsImmediatelyAndNotifiesVisibleProjections()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        BlockingStarQueryService queryService = new();
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.StarRepositoryAsync("token", "owner", "repo", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        IGitHubQueryService cache = Substitute.For<IGitHubQueryService>();
        bool cacheInvalidated = false;
        cache.InvalidateTagsAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cacheInvalidated = true;
                return Task.CompletedTask;
            });
        GitHubStarLibraryService service = new(
            store,
            queryService,
            client,
            cache,
            Substitute.For<ITelemetryService>());
        ConcurrentBag<StarLibraryChangeKind> changes = [];
        bool projectionNotificationObservedAfterInvalidation = false;
        service.Changed += (_, args) =>
        {
            changes.Add(args.Kind);
            if (args.Kind == StarLibraryChangeKind.ProjectionInvalidated)
            {
                projectionNotificationObservedAfterInvalidation = cacheInvalidated;
            }
        };
        GitHubRepository repository = Star(42, "owner", "repo").Repository;

        await service.NotifyRepositoryStarStateChangedAsync("token", "u1", repository, isStarred: true);

        StarLibraryItem immediate = Assert.Single((await store.QueryAsync(Query("u1"))).Items);
        Assert.Equal(repository.Id, immediate.Repository.Id);
        Assert.Contains(StarLibraryChangeKind.Items, changes);
        Assert.Contains(StarLibraryChangeKind.Categories, changes);
        Assert.Contains(StarLibraryChangeKind.ProjectionInvalidated, changes);
        Assert.True(projectionNotificationObservedAfterInvalidation);
        await cache.Received(1).InvalidateTagsAsync(
            "u1",
            Arg.Is<IReadOnlyCollection<string>>(tags =>
                tags.Contains("profile-stars") && tags.Contains("dashboard-starred-repos")),
            Arg.Any<CancellationToken>());

        await queryService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queryService.Release([]);
        await WaitUntilAsync(async () => !(await store.GetSyncStateAsync("u1")).IsSyncing);

        Assert.Single((await store.QueryAsync(Query("u1"))).Items);
    }

    [Fact]
    public async Task FullNameStarNotification_HydratesThroughBackgroundStaleFirstQuery()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        IGitHubRepositoryQueryService repositoryQuery = Substitute.For<IGitHubRepositoryQueryService>();
        GitHubRepository repository = Star(42, "owner", "repo").Repository;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        repositoryQuery.GetRepositoryAsync(
                "token",
                "u1",
                "owner",
                "repo",
                QueryFetchPolicy.StaleFirst,
                GitHubRequestPriority.BackgroundRefresh,
                Arg.Any<CancellationToken>())
            .Returns(new CachedResult<GitHubRepository>(
                repository,
                CacheState.Stale,
                now.AddMinutes(-10),
                now.AddMinutes(-5),
                IsRefreshInProgress: true));
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        IGitHubQueryService cache = Substitute.For<IGitHubQueryService>();
        cache.InvalidateTagsAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        ITelemetryService telemetry = Substitute.For<ITelemetryService>();
        GitHubStarLibraryService service = new(
            store,
            Substitute.For<IGitHubStarQueryService>(),
            repositoryQuery,
            client,
            cache,
            telemetry);

        await service.NotifyRepositoryStarStateChangedAsync(
            "token",
            "u1",
            "owner/repo",
            isStarred: true);

        StarLibraryItem item = Assert.Single((await store.QueryAsync(Query("u1"))).Items);
        Assert.Equal(repository.Id, item.Repository.Id);
        await repositoryQuery.Received(1).GetRepositoryAsync(
            "token",
            "u1",
            "owner",
            "repo",
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.BackgroundRefresh,
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetRepositoryAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        telemetry.Received(1).TrackEvent(
            "stars.action.executed",
            Arg.Is<IReadOnlyDictionary<string, string?>>(properties =>
                properties.Count == 3 &&
                properties["action"] == "hydrate" &&
                properties["result"] == "success" &&
                properties["cache_state"] == "stale" &&
                !properties.Values.Any(value =>
                    value != null && value.Contains("owner", StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public async Task SynchronizeAsync_CoalescesRequestsArrivingDuringActiveSyncIntoOneFollowUp()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        BlockingStarQueryService queryService = new();
        GitHubStarLibraryService service = CreateService(
            store,
            Substitute.For<IGitHubClientService>(),
            queryService);

        Task<StarSyncState> first = service.SynchronizeAsync("token", "u1", forceFull: true);
        await queryService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<StarSyncState> second = service.SynchronizeAsync("token", "u1");
        Task<StarSyncState> third = service.SynchronizeAsync("token", "u1");

        queryService.Release([]);
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, queryService.CallCount);
    }

    [Fact]
    public async Task SynchronizeAsync_CancellationEmitsExactlyOneTerminalOutcome()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        BlockingStarQueryService queryService = new();
        RecordingTelemetryService telemetry = new();
        GitHubStarLibraryService service = CreateService(
            store,
            Substitute.For<IGitHubClientService>(),
            queryService,
            telemetry);
        using CancellationTokenSource cancellation = new();

        Task<StarSyncState> sync = service.SynchronizeAsync(
            "token",
            "u1",
            forceFull: true,
            cancellation.Token);
        await queryService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        RecordedTelemetryEvent terminal = Assert.Single(
            telemetry.Events,
            entry => entry.Name == "stars.sync.completed");
        Assert.Equal(TelemetryTaxonomy.Results.Cancelled, terminal.Properties["result"]);
        Assert.Equal(TelemetryTaxonomy.Sources.Full, terminal.Properties["source"]);
    }

    [Fact]
    public async Task RepositoryActionStarDuringActiveSync_QueuesFollowUpAndKeepsImmediateRow()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        BlockingStarQueryService queryService = new();
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.StarRepositoryAsync("token", "owner", "repo", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        GitHubStarLibraryService service = CreateService(store, client, queryService);

        Task<StarSyncState> activeSync = service.SynchronizeAsync("token", "u1");
        await queryService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        GitHubRepository repository = Star(42, "owner", "repo").Repository;
        await service.NotifyRepositoryStarStateChangedAsync("token", "u1", repository, isStarred: true);
        Assert.Single((await store.QueryAsync(Query("u1"))).Items);

        queryService.Release([]);
        await activeSync.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => Task.FromResult(queryService.CallCount >= 2));
        await WaitUntilAsync(async () => !(await store.GetSyncStateAsync("u1")).IsSyncing);

        Assert.Equal(2, queryService.CallCount);
        Assert.Single((await store.QueryAsync(Query("u1"))).Items);
        Assert.Empty(await store.GetPendingMutationsAsync("u1"));
        await client.Received(1).StarRepositoryAsync("token", "owner", "repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryActionUnstar_StaysRemovedWhenFollowUpSyncSeesStaleRemotePage()
    {
        SqliteStarLibraryStore store = CreateStore();
        GitHubStarredRepository starred = Star(42, "owner", "repo");
        await store.UpsertPageAsync("u1", [starred], "initial");
        BlockingStarQueryService queryService = new();
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.UnstarRepositoryAsync("token", "owner", "repo", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        GitHubStarLibraryService service = CreateService(store, client, queryService);

        await service.NotifyRepositoryStarStateChangedAsync(
            "token",
            "u1",
            starred.Repository,
            isStarred: false);

        Assert.Empty((await store.QueryAsync(Query("u1"))).Items);
        await queryService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queryService.Release([starred]);
        await WaitUntilAsync(async () => !(await store.GetSyncStateAsync("u1")).IsSyncing);

        Assert.Empty((await store.QueryAsync(Query("u1"))).Items);
        Assert.Empty(await store.GetPendingMutationsAsync("u1"));
        await client.Received(1).UnstarRepositoryAsync("token", "owner", "repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCoordinator_AbandonedForceFullBatchRemainsPendingForNextOwner()
    {
        StarSyncRequestCoordinator coordinator = new();
        long requestVersion = coordinator.Request(forceFull: true);
        await coordinator.EnterAsync(CancellationToken.None);
        Assert.True(coordinator.TryTake(requestVersion, out StarSyncRequestBatch abandoned));
        coordinator.Abandon(abandoned);
        coordinator.Exit();

        await coordinator.EnterAsync(CancellationToken.None);
        Assert.True(coordinator.TryTake(requestVersion, out StarSyncRequestBatch retry));
        Assert.True(retry.ForceFull);
        Assert.Equal(abandoned.Version, retry.Version);
        coordinator.Complete(retry);
        Assert.False(coordinator.TryTake(requestVersion, out _));
        coordinator.Exit();
    }

    [Fact]
    public void SyncCoordinator_CoalescesStateWithoutRetainingAccessTokens()
    {
        StarSyncRequestCoordinator coordinator = new();
        long firstVersion = coordinator.Request(forceFull: false);
        Assert.True(coordinator.TryTake(firstVersion, out StarSyncRequestBatch first));

        long followUpVersion = coordinator.Request(forceFull: true);
        coordinator.Complete(first);

        Assert.True(coordinator.TryTake(followUpVersion, out StarSyncRequestBatch followUp));
        Assert.True(followUp.ForceFull);
        Assert.DoesNotContain(
            typeof(StarSyncRequestCoordinator).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(string));
        Assert.DoesNotContain(
            typeof(StarSyncRequestBatch).GetProperties(),
            property => property.PropertyType == typeof(string));

        coordinator.Dispose();
    }

    [Fact]
    public async Task SyncCoordinator_DisposeCancelsQueuedOwner()
    {
        StarSyncRequestCoordinator coordinator = new();
        await coordinator.EnterAsync(CancellationToken.None);
        Task queued = coordinator.EnterAsync(CancellationToken.None);

        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public async Task ClearAccountState_AfterAccountDrainRemovesCoordinatorAndCancelsSynchronization()
    {
        SqliteStarLibraryStore store = CreateStore();
        BlockingStarQueryService queryService = new();
        using ApplicationTaskCoordinator tasks = new();
        AccountWorkQuiescence accountWork = new(tasks);
        IGitHubQueryService cache = Substitute.For<IGitHubQueryService>();
        cache.InvalidateTagsAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        GitHubStarLibraryService service = new(
            store,
            queryService,
            Substitute.For<IGitHubRepositoryQueryService>(),
            Substitute.For<IGitHubClientService>(),
            cache,
            Substitute.For<IStarLibraryRecoveryStore>(),
            Substitute.For<ITelemetryService>(),
            accountWork,
            tasks);

        Task<StarSyncState> synchronization = service.SynchronizeAsync("sensitive-token", "u1");
        await queryService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Task drain = accountWork.QuiesceAsync("u1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronization);
        await drain;
        await service.ClearAccountStateAsync("u1");

        var coordinators = (ConcurrentDictionary<string, StarSyncRequestCoordinator>)typeof(GitHubStarLibraryService)
            .GetField("_syncCoordinators", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        Assert.False(coordinators.ContainsKey("u1"));
        service.Dispose();
    }

    [Fact]
    public async Task FailedSynchronizationRemainsPendingAndRetriesOnNextCaller()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        FailOnceStarQueryService queryService = new();
        GitHubStarLibraryService service = CreateService(
            store,
            Substitute.For<IGitHubClientService>(),
            queryService);

        StarSyncState failed = await service.SynchronizeAsync("old-token", "u1", forceFull: true);
        StarSyncState recovered = await service.SynchronizeAsync("new-token", "u1", forceFull: false);

        Assert.NotEmpty(failed.ErrorMessage);
        Assert.Empty(recovered.ErrorMessage);
        Assert.True(recovered.IsComplete);
        Assert.Equal(["old-token", "new-token"], queryService.AccessTokens);
    }

    [Fact]
    public async Task FailedFullReconciliationAfterPageOne_MarksIndexIncompleteAndNeverPrunesTail()
    {
        SqliteStarLibraryStore store = CreateStore();
        await store.InitializeAsync();
        GitHubStarredRepository[] original = Enumerable.Range(1, 101)
            .Select(index => Star(index, "owner", $"repo-{index}"))
            .ToArray();
        await store.UpsertPageAsync("u1", original, "initial");
        await store.CompleteFullSyncAsync("u1", "initial");
        DateTimeOffset previousFullSync = DateTimeOffset.UtcNow.AddDays(-1);
        await store.SaveSyncStateAsync(new StarSyncState(
            "u1",
            previousFullSync,
            previousFullSync,
            IsComplete: true,
            IsSyncing: false,
            IndexedCount: original.Length,
            ErrorMessage: string.Empty));
        GitHubStarLibraryService service = CreateService(
            store,
            Substitute.For<IGitHubClientService>(),
            new FailSecondFullSyncPageQueryService());

        StarSyncState failed = await service.SynchronizeAsync("token", "u1", forceFull: true);
        StarLibraryPage all = await store.QueryAsync(Query("u1"));
        StarLibraryPage tail = await store.QueryAsync(new StarLibraryQuery(
            "u1",
            StarSmartList.All,
            null,
            string.Empty,
            StarLibraryFilter.Empty,
            StarLibrarySort.RecentlyStarred,
            100,
            100));

        Assert.False(failed.IsComplete);
        Assert.NotEmpty(failed.ErrorMessage);
        Assert.Equal(101, all.TotalCount);
        Assert.Equal(101, Assert.Single(tail.Items).Repository.Id);
    }

    [Fact]
    public async Task RepositoryActionLocalFailurePersistsRecoveryAndExposesDegradedStateUntilReplay()
    {
        string recoveryPath = Path.Combine(_root, "durable", "recovery.json");
        StarLibraryRecoveryStore recoveryStore = new(recoveryPath);
        IStarLibraryStore failingStore = Substitute.For<IStarLibraryStore>();
        failingStore.GetCategoryIdsAsync("u1", 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        failingStore.ApplyPendingRestoreAsync(
                Arg.Any<StarPendingMutation>(),
                Arg.Any<GitHubStarredRepository>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk unavailable")));
        IGitHubQueryService cache = Substitute.For<IGitHubQueryService>();
        cache.InvalidateTagsAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        GitHubStarLibraryService failingService = new(
            failingStore,
            Substitute.For<IGitHubStarQueryService>(),
            Substitute.For<IGitHubClientService>(),
            cache,
            recoveryStore,
            Substitute.For<ITelemetryService>());
        GitHubRepository repository = Star(42, "owner", "repo").Repository;

        await Assert.ThrowsAsync<StarLibraryDegradedException>(() =>
            failingService.NotifyRepositoryStarStateChangedAsync("token", "u1", repository, isStarred: true));

        StarLibraryDegradedState degraded = failingService.GetDegradedState("u1");
        Assert.True(degraded.IsDegraded);
        Assert.Equal(1, degraded.PendingRecoveryCount);
        Assert.Single(await recoveryStore.ReadAsync("u1"));

        SqliteStarLibraryStore recoveredStore = CreateStore();
        GitHubStarLibraryService recoveredService = new(
            recoveredStore,
            Substitute.For<IGitHubStarQueryService>(),
            Substitute.For<IGitHubClientService>(),
            cache,
            recoveryStore,
            Substitute.For<ITelemetryService>());
        StarLibrarySnapshot snapshot = await recoveredService.InitializeAsync("token", "u1", Query("u1"));

        Assert.Single(snapshot.Page.Items);
        Assert.Empty(await recoveryStore.ReadAsync("u1"));
        Assert.False(recoveredService.GetDegradedState("u1").IsDegraded);
    }

    [Fact]
    public async Task RemoteOfflineReplayReconciliationAndRelaunch_AreConsistentForOneAccount()
    {
        const string userId = "u1";
        const string accessToken = "token";
        GitHubStarredRepository remoteRepository = Star(84, "owner", "repo");
        bool isOnline = true;
        bool isRemoteStarred = true;
        SqliteStarLibraryStore store = CreateStore();
        StatefulStarQueryService queryService = new(() => isRemoteStarred ? [remoteRepository] : []);
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.UnstarRepositoryAsync(accessToken, "owner", "repo", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!isOnline)
                {
                    throw new HttpRequestException("offline");
                }

                isRemoteStarred = false;
                return Task.CompletedTask;
            });
        client.StarRepositoryAsync(accessToken, "owner", "repo", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!isOnline)
                {
                    throw new HttpRequestException("offline");
                }

                isRemoteStarred = true;
                return Task.CompletedTask;
            });
        GitHubStarLibraryService service = CreateService(store, client, queryService);

        StarSyncState initialSync = await service.SynchronizeAsync(accessToken, userId, forceFull: true);
        Assert.True(initialSync.IsComplete);
        StarCategory work = await service.CreateCategoryAsync(userId, "Work", "#74BEA7");
        StarCategory later = await service.CreateCategoryAsync(userId, "Read later", "#5B9BD5");
        await service.ReorderCategoryAsync(userId, later.Id, 0);
        await service.AddToCategoryAsync(userId, work.Id, [remoteRepository.Repository.Id]);
        await service.AddToCategoryAsync(userId, later.Id, [remoteRepository.Repository.Id]);
        StarLibraryItem item = Assert.Single((await service.QueryAsync(Query(userId))).Items);

        isOnline = false;
        await service.UnstarAsync(accessToken, userId, item);
        Assert.Empty((await service.QueryAsync(Query(userId))).Items);
        await WaitUntilAsync(async () =>
            (await store.GetPendingMutationsAsync(userId)).SingleOrDefault() is
            {
                DesiredStarred: false,
                AttemptCount: > 0
            });

        isOnline = true;
        await service.FlushPendingMutationsAsync(accessToken, userId);
        Assert.False(isRemoteStarred);
        Assert.Empty(await store.GetPendingMutationsAsync(userId));
        await service.SynchronizeAsync(accessToken, userId, forceFull: true);
        Assert.Empty((await service.QueryAsync(Query(userId))).Items);

        await service.RestoreStarAsync(accessToken, userId, item, [work.Id, later.Id]);
        await service.FlushPendingMutationsAsync(accessToken, userId);
        Assert.True(isRemoteStarred);
        Assert.Empty(await store.GetPendingMutationsAsync(userId));
        StarLibraryItem restored = Assert.Single((await service.QueryAsync(Query(userId))).Items);
        Assert.Equal(2, restored.Categories.Count);

        await service.DeleteCategoryAsync(userId, work.Id);
        await service.ReorderCategoryAsync(userId, later.Id, 0);

        SqliteStarLibraryStore reopenedStore = new(store.DatabasePath);
        GitHubStarLibraryService reopenedService = CreateService(reopenedStore, client, queryService);
        StarLibrarySnapshot reopened = await reopenedService.InitializeAsync(accessToken, userId, Query(userId));

        StarLibraryItem reopenedItem = Assert.Single(reopened.Page.Items);
        StarCategory reopenedCategory = Assert.Single(reopened.Categories);
        Assert.Equal(later.Id, reopenedCategory.Id);
        Assert.Equal(0, reopenedCategory.Position);
        Assert.Equal(later.Id, Assert.Single(reopenedItem.Categories).Id);
        Assert.Empty(await reopenedStore.GetPendingMutationsAsync(userId));
    }

    private GitHubStarLibraryService CreateService(
        SqliteStarLibraryStore store,
        IGitHubClientService client,
        IGitHubStarQueryService? queryService = null,
        ITelemetryService? telemetry = null)
    {
        IGitHubQueryService cache = Substitute.For<IGitHubQueryService>();
        cache.InvalidateTagsAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return new(
            store,
            queryService ?? Substitute.For<IGitHubStarQueryService>(),
            client,
            cache,
            telemetry ?? Substitute.For<ITelemetryService>());
    }

    private SqliteStarLibraryStore CreateStore()
    {
        Directory.CreateDirectory(_root);
        return new SqliteStarLibraryStore(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"));
    }

    private static StarLibraryQuery Query(string userId) =>
        new(userId, StarSmartList.All, null, string.Empty, StarLibraryFilter.Empty, StarLibrarySort.RecentlyStarred, 0, 100);

    private static GitHubStarredRepository Star(long id, string owner, string name) => new()
    {
        StarredAt = DateTimeOffset.UtcNow,
        Repository = new GitHubRepository
        {
            Id = id,
            Name = name,
            FullName = $"{owner}/{name}",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Owner = new GitHubRepositoryOwner { Login = owner }
        }
    };

    private static bool IsSafeCategoryTelemetry(IReadOnlyDictionary<string, string?> properties, string action) =>
        properties.Count == 2 &&
        properties.TryGetValue("action", out string? actualAction) &&
        string.Equals(actualAction, action, StringComparison.Ordinal) &&
        properties.TryGetValue("result", out string? result) &&
        string.Equals(result, "success", StringComparison.Ordinal) &&
        !properties.Values.Any(value => value?.Contains("category", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsSafeMembershipTelemetry(IReadOnlyDictionary<string, string?> properties, string action) =>
        properties.Count == 3 &&
        properties.TryGetValue("action", out string? actualAction) &&
        string.Equals(actualAction, action, StringComparison.Ordinal) &&
        properties.TryGetValue("result", out string? result) &&
        string.Equals(result, "success", StringComparison.Ordinal) &&
        properties.TryGetValue("count_bucket", out string? countBucket) &&
        string.Equals(countBucket, "1", StringComparison.Ordinal) &&
        !properties.Values.Any(value => value?.Contains("category", StringComparison.OrdinalIgnoreCase) == true);

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!await predicate())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class StatefulStarQueryService(Func<GitHubStarredRepository[]> getRepositories) : IGitHubStarQueryService
    {
        public Task<CachedResult<GitHubStarredRepository[]>> GetPageAsync(
            string accessToken,
            string userId,
            int page,
            QueryFetchPolicy fetchPolicy,
            GitHubRequestPriority priority,
            CancellationToken cancellationToken = default)
        {
            GitHubStarredRepository[] repositories = page == 1 ? getRepositories() : [];
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<GitHubStarredRepository[]>(
                repositories,
                CacheState.Fresh,
                now,
                now.AddMinutes(30)));
        }
    }

    private sealed class BlockingStarQueryService : IGitHubStarQueryService
    {
        private readonly TaskCompletionSource<GitHubStarredRepository[]> _firstResult =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public void Release(GitHubStarredRepository[] repositories) =>
            _firstResult.TrySetResult(repositories);

        public async Task<CachedResult<GitHubStarredRepository[]>> GetPageAsync(
            string accessToken,
            string userId,
            int page,
            QueryFetchPolicy fetchPolicy,
            GitHubRequestPriority priority,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _callCount);
            GitHubStarredRepository[] repositories;
            if (call == 1)
            {
                Started.TrySetResult();
                repositories = await _firstResult.Task.WaitAsync(cancellationToken);
            }
            else
            {
                repositories = [];
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new CachedResult<GitHubStarredRepository[]>(
                repositories,
                CacheState.Fresh,
                now,
                now.AddMinutes(30));
        }
    }

    private sealed class FailOnceStarQueryService : IGitHubStarQueryService
    {
        private int _calls;

        public List<string> AccessTokens { get; } = [];

        public Task<CachedResult<GitHubStarredRepository[]>> GetPageAsync(
            string accessToken,
            string userId,
            int page,
            QueryFetchPolicy fetchPolicy,
            GitHubRequestPriority priority,
            CancellationToken cancellationToken = default)
        {
            AccessTokens.Add(accessToken);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new HttpRequestException("offline");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<GitHubStarredRepository[]>(
                [],
                CacheState.Fresh,
                now,
                now.AddMinutes(30)));
        }
    }

    private sealed class FailSecondFullSyncPageQueryService : IGitHubStarQueryService
    {
        public Task<CachedResult<GitHubStarredRepository[]>> GetPageAsync(
            string accessToken,
            string userId,
            int page,
            QueryFetchPolicy fetchPolicy,
            GitHubRequestPriority priority,
            CancellationToken cancellationToken = default)
        {
            if (page == 2)
            {
                throw new HttpRequestException("stars page 2 unavailable");
            }

            GitHubStarredRepository[] repositories = Enumerable.Range(1, 100)
                .Select(index => Star(index, "owner", $"updated-{index}"))
                .ToArray();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<GitHubStarredRepository[]>(
                repositories,
                CacheState.Fresh,
                now,
                now.AddMinutes(30)));
        }
    }
}
