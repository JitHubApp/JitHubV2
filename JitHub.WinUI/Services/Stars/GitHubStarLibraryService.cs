using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed partial class GitHubStarLibraryService : IGitHubStarLibraryService, IDisposable
{
    private static readonly TimeSpan CompleteReconciliationInterval = TimeSpan.FromHours(24);
    internal static readonly IReadOnlyList<string> StarMutationCacheTags =
        ["me-stars", "star-library", "profile-stars", "dashboard-starred-repos"];
    private readonly IStarLibraryStore _store;
    private readonly IGitHubStarQueryService _queryService;
    private readonly IGitHubRepositoryQueryService _repositoryQueryService;
    private readonly IGitHubClientService _clientService;
    private readonly IGitHubQueryService _cacheQueryService;
    private readonly IStarLibraryRecoveryStore _recoveryStore;
    private readonly ITelemetryService _telemetry;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly ConcurrentDictionary<string, StarSyncRequestCoordinator> _syncCoordinators = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutationGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StarLibraryDegradedState> _degradedStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _scheduledRecoveryRetries = new(StringComparer.Ordinal);
    private int _disposed;

    public GitHubStarLibraryService(
        IStarLibraryStore store,
        IGitHubStarQueryService queryService,
        IGitHubClientService clientService,
        IGitHubQueryService cacheQueryService,
        IStarLibraryRecoveryStore recoveryStore,
        ITelemetryService telemetry,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator)
        : this(
            store,
            queryService,
            new GitHubRepositoryQueryService(cacheQueryService),
            clientService,
            cacheQueryService,
            recoveryStore,
            telemetry,
            accountWork,
            taskCoordinator)
    {
    }

    public GitHubStarLibraryService(
        IStarLibraryStore store,
        IGitHubStarQueryService queryService,
        IGitHubRepositoryQueryService repositoryQueryService,
        IGitHubClientService clientService,
        IGitHubQueryService cacheQueryService,
        ITelemetryService telemetry)
        : this(
            store,
            queryService,
            repositoryQueryService,
            clientService,
            cacheQueryService,
            new StarLibraryRecoveryStore(
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(store.DatabasePath) ?? AppContext.BaseDirectory,
                    "repository-action-recovery.json")),
            telemetry,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator())
    {
    }

    public GitHubStarLibraryService(
        IStarLibraryStore store,
        IGitHubStarQueryService queryService,
        IGitHubClientService clientService,
        IGitHubQueryService cacheQueryService,
        ITelemetryService telemetry)
        : this(
            store,
            queryService,
            new GitHubRepositoryQueryService(cacheQueryService),
            clientService,
            cacheQueryService,
            new StarLibraryRecoveryStore(
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(store.DatabasePath) ?? AppContext.BaseDirectory,
                    "repository-action-recovery.json")),
            telemetry,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator())
    {
    }

    public GitHubStarLibraryService(
        IStarLibraryStore store,
        IGitHubStarQueryService queryService,
        IGitHubClientService clientService,
        IGitHubQueryService cacheQueryService,
        IStarLibraryRecoveryStore recoveryStore,
        ITelemetryService telemetry)
        : this(
            store,
            queryService,
            new GitHubRepositoryQueryService(cacheQueryService),
            clientService,
            cacheQueryService,
            recoveryStore,
            telemetry,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator())
    {
    }

    public GitHubStarLibraryService(
        IStarLibraryStore store,
        IGitHubStarQueryService queryService,
        IGitHubClientService clientService,
        IGitHubQueryService cacheQueryService,
        IStarLibraryRecoveryStore recoveryStore,
        ITelemetryService telemetry,
        IAccountWorkQuiescence accountWork)
        : this(
            store,
            queryService,
            new GitHubRepositoryQueryService(cacheQueryService),
            clientService,
            cacheQueryService,
            recoveryStore,
            telemetry,
            accountWork,
            new ApplicationTaskCoordinator())
    {
    }

    public GitHubStarLibraryService(
        IStarLibraryStore store,
        IGitHubStarQueryService queryService,
        IGitHubRepositoryQueryService repositoryQueryService,
        IGitHubClientService clientService,
        IGitHubQueryService cacheQueryService,
        IStarLibraryRecoveryStore recoveryStore,
        ITelemetryService telemetry,
        IAccountWorkQuiescence accountWork,
        IApplicationTaskCoordinator taskCoordinator)
    {
        _store = store;
        _queryService = queryService;
        _repositoryQueryService = repositoryQueryService;
        _clientService = clientService;
        _cacheQueryService = cacheQueryService;
        _recoveryStore = recoveryStore;
        _telemetry = SafeTelemetryService.Wrap(telemetry);
        _accountWork = accountWork;
        _taskCoordinator = taskCoordinator;
    }

    public event EventHandler<StarLibraryChangedEventArgs>? Changed;

    public StarLibraryDegradedState GetDegradedState(string userId) =>
        _degradedStates.TryGetValue(userId, out StarLibraryDegradedState? state)
            ? state
            : StarLibraryDegradedState.Healthy;

    public Task ClearAccountStateAsync(string userId, CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(userId, nameof(userId));
        cancellationToken.ThrowIfCancellationRequested();

        if (_syncCoordinators.TryRemove(partition, out StarSyncRequestCoordinator? syncCoordinator))
        {
            syncCoordinator.Dispose();
        }

        if (_mutationGates.TryRemove(partition, out SemaphoreSlim? mutationGate))
        {
            mutationGate.Dispose();
        }

        _degradedStates.TryRemove(partition, out _);
        _scheduledRecoveryRetries.TryRemove(partition, out _);
        return Task.CompletedTask;
    }

    public async Task<StarLibraryPage> LoadCachedPageAsync(
        string accessToken,
        string userId,
        StarLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        return await LoadCachedPageCoreAsync(accessToken, userId, query, lease.CancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StarLibrarySnapshot> InitializeAsync(
        string accessToken,
        string userId,
        StarLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        CancellationToken operationToken = lease.CancellationToken;
        StarLibraryPage page = await LoadCachedPageCoreAsync(
            accessToken,
            userId,
            query,
            operationToken).ConfigureAwait(false);
        Task<IReadOnlyList<StarCategory>> categoriesTask = _store.GetCategoriesAsync(userId, operationToken);
        Task<IReadOnlyList<string>> languagesTask = _store.GetFacetValuesAsync(userId, "languages", operationToken);
        Task<IReadOnlyList<string>> ownersTask = _store.GetFacetValuesAsync(userId, "owners", operationToken);
        Task<IReadOnlyList<string>> topicsTask = _store.GetFacetValuesAsync(userId, "topics", operationToken);
        Task<IReadOnlyDictionary<StarSmartList, int>> smartListCountsTask = _store.GetSmartListCountsAsync(userId, operationToken);
        await Task.WhenAll(categoriesTask, languagesTask, ownersTask, topicsTask, smartListCountsTask);
        return new StarLibrarySnapshot(
            page,
            categoriesTask.Result,
            languagesTask.Result,
            ownersTask.Result,
            topicsTask.Result,
            smartListCountsTask.Result);
    }

    private async Task<StarLibraryPage> LoadCachedPageCoreAsync(
        string accessToken,
        string userId,
        StarLibraryQuery query,
        CancellationToken operationToken)
    {
        await _store.InitializeAsync(operationToken);
        await StarLibraryClearCoordinator.RecoverAsync(_store, _recoveryStore, operationToken)
            .ConfigureAwait(false);
        await TryReplayRecoveryAsync(accessToken, userId, operationToken);
        return await _store.QueryAsync(query, operationToken).ConfigureAwait(false);
    }

    public async Task<StarLibraryPage> QueryAsync(StarLibraryQuery query, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(query.UserId, cancellationToken);
        return await _store.QueryAsync(query, lease.CancellationToken).ConfigureAwait(false);
    }

    public async Task<StarSyncState> SynchronizeAsync(
        string accessToken,
        string userId,
        bool forceFull = false,
        CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        StarSyncRequestCoordinator coordinator = _syncCoordinators.GetOrAdd(
            userId,
            static _ => new StarSyncRequestCoordinator());
        long requestVersion = coordinator.Request(forceFull);
        await coordinator.EnterAsync(cancellationToken);
        try
        {
            StarSyncState result = await _store.GetSyncStateAsync(userId, cancellationToken);
            if (coordinator.TryTake(requestVersion, out StarSyncRequestBatch batch))
            {
                try
                {
                    await TryReplayRecoveryAsync(accessToken, userId, cancellationToken);
                    result = await SynchronizeCoreAsync(
                        accessToken,
                        userId,
                        batch.ForceFull,
                        cancellationToken);
                    coordinator.Complete(batch);
                }
                catch (StarSynchronizationFailedException ex)
                {
                    coordinator.Abandon(batch);
                    result = ex.State;
                }
                catch
                {
                    coordinator.Abandon(batch);
                    throw;
                }
            }

            return result;
        }
        finally
        {
            coordinator.Exit();
        }
    }

    private async Task<StarSyncState> SynchronizeCoreAsync(
        string accessToken,
        string userId,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        StarSyncState previous = await _store.GetSyncStateAsync(userId, cancellationToken);
        bool fullSync = forceFull || previous.LastFullSync is null || DateTimeOffset.UtcNow - previous.LastFullSync >= CompleteReconciliationInterval;
        bool reconciliationPageApplied = false;
        string generation = Guid.NewGuid().ToString("N");
        StarSyncState syncing = previous with { IsSyncing = true, ErrorMessage = string.Empty };
        await _store.SaveSyncStateAsync(syncing, cancellationToken);
        RaiseChanged(userId, StarLibraryChangeKind.Sync);

        try
        {
            int page = 1;
            while (true)
            {
                GitHubRequestPriority priority = page == 1 ? GitHubRequestPriority.Visible : GitHubRequestPriority.BackgroundRefresh;
                CachedResult<GitHubStarredRepository[]> result = await _queryService.GetPageAsync(
                    accessToken,
                    userId,
                    page,
                    QueryFetchPolicy.NetworkOnly,
                    priority,
                    cancellationToken);
                GitHubStarredRepository[] repositories = result.Value ?? [];
                await _store.UpsertPageAsync(userId, repositories, generation, cancellationToken);
                reconciliationPageApplied = true;
                RaiseChanged(userId, StarLibraryChangeKind.Items);

                if (!fullSync || repositories.Length < GitHubStarQueryService.PageSize)
                {
                    break;
                }

                page++;
            }

            if (fullSync)
            {
                await _store.CompleteFullSyncAsync(userId, generation, cancellationToken);
            }

            // Reconcile the remote page before flushing the local outbox. Pending unstars are
            // excluded from page upserts, while pending stars protect their local rows from a
            // full-sync prune until the idempotent remote mutation succeeds.
            await FlushPendingMutationsAsync(accessToken, userId, cancellationToken);

            StarLibraryPage all = await _store.QueryAsync(
                new StarLibraryQuery(userId, StarSmartList.All, null, string.Empty, StarLibraryFilter.Empty, StarLibrarySort.RecentlyStarred, 0, 1),
                cancellationToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            StarSyncState completed = new(
                userId,
                now,
                fullSync ? now : previous.LastFullSync,
                fullSync || previous.IsComplete,
                false,
                all.TotalCount,
                string.Empty);
            await _store.SaveSyncStateAsync(completed, cancellationToken);
            TrackEventSafely("stars.sync.completed", new Dictionary<string, string?>
            {
                ["result"] = TelemetryTaxonomy.Results.Success,
                ["source"] = fullSync ? TelemetryTaxonomy.Sources.Full : TelemetryTaxonomy.Sources.Incremental,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(stopwatch.Elapsed),
                ["count_bucket"] = CountBucket(all.TotalCount)
            });
            RaiseChanged(userId, StarLibraryChangeKind.Sync);
            return completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StarLibraryPage existing = await _store.QueryAsync(
                new StarLibraryQuery(userId, StarSmartList.All, null, string.Empty, StarLibraryFilter.Empty, StarLibrarySort.RecentlyStarred, 0, 1),
                CancellationToken.None);
            StarSyncState failed = previous with
            {
                IsComplete = fullSync && reconciliationPageApplied ? false : previous.IsComplete,
                IsSyncing = false,
                IndexedCount = existing.TotalCount,
                ErrorMessage = JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "stars-sync")
            };
            await _store.SaveSyncStateAsync(failed, CancellationToken.None);
            TrackEventSafely("stars.sync.completed", new Dictionary<string, string?>
            {
                ["result"] = TelemetryTaxonomy.Results.Error,
                ["source"] = fullSync ? TelemetryTaxonomy.Sources.Full : TelemetryTaxonomy.Sources.Incremental,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(stopwatch.Elapsed),
                ["count_bucket"] = CountBucket(existing.TotalCount)
            });
            RaiseChanged(userId, StarLibraryChangeKind.Sync);
            throw new StarSynchronizationFailedException(failed, ex);
        }
        catch (OperationCanceledException)
        {
            StarSyncState canceled = previous with
            {
                IsComplete = fullSync && reconciliationPageApplied ? false : previous.IsComplete,
                IsSyncing = false
            };
            await _store.SaveSyncStateAsync(canceled, CancellationToken.None);
            TrackEventSafely("stars.sync.completed", new Dictionary<string, string?>
            {
                ["result"] = TelemetryTaxonomy.Results.Cancelled,
                ["source"] = fullSync ? TelemetryTaxonomy.Sources.Full : TelemetryTaxonomy.Sources.Incremental,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(stopwatch.Elapsed),
                ["count_bucket"] = CountBucket(canceled.IndexedCount)
            });
            RaiseChanged(userId, StarLibraryChangeKind.Sync);
            throw;
        }
    }

    public async Task<StarCategory> CreateCategoryAsync(string userId, string name, string color, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        StarCategory category = await _store.CreateCategoryAsync(userId, name, color, lease.CancellationToken);
        TrackCategory("stars.category.created", TelemetryTaxonomy.Actions.Create);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
        return category;
    }

    public async Task<StarCategory> UpdateCategoryAsync(string userId, string categoryId, string name, string color, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        StarCategory category = await _store.UpdateCategoryAsync(userId, categoryId, name, color, lease.CancellationToken);
        TrackCategory("stars.category.updated", TelemetryTaxonomy.Actions.Update);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
        return category;
    }

    public async Task DeleteCategoryAsync(string userId, string categoryId, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        await _store.DeleteCategoryAsync(userId, categoryId, lease.CancellationToken);
        TrackCategory("stars.category.deleted", TelemetryTaxonomy.Actions.Delete);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
        RaiseChanged(userId, StarLibraryChangeKind.Items);
    }

    public async Task ReorderCategoryAsync(string userId, string categoryId, int targetPosition, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        await _store.ReorderCategoryAsync(userId, categoryId, targetPosition, lease.CancellationToken);
        TrackCategory("stars.category.updated", TelemetryTaxonomy.Actions.Reorder);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
    }

    public async Task AddToCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        await _store.AddToCategoryAsync(userId, categoryId, repositoryIds, lease.CancellationToken);
        TrackMembership(TelemetryTaxonomy.Actions.Add, repositoryIds.Count);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
        RaiseChanged(userId, StarLibraryChangeKind.Items);
    }

    public async Task RemoveFromCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        await _store.RemoveFromCategoryAsync(userId, categoryId, repositoryIds, lease.CancellationToken);
        TrackMembership(TelemetryTaxonomy.Actions.Remove, repositoryIds.Count);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
        RaiseChanged(userId, StarLibraryChangeKind.Items);
    }

    public async Task UnstarAsync(string accessToken, string userId, StarLibraryItem item, CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        StarPendingMutation mutation = CreatePendingMutation(userId, item, desiredStarred: false);
        await _store.ApplyPendingUnstarAsync(mutation, cancellationToken);
        await InvalidateStarViewsAsync(userId, cancellationToken);
        TrackEventSafely(
            "stars.action.executed",
            new Dictionary<string, string?>
            {
                ["action"] = TelemetryTaxonomy.Actions.Unstar,
                ["result"] = TelemetryTaxonomy.Results.Queued
            });
        RaiseChanged(userId, StarLibraryChangeKind.Items);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
        ScheduleMutationFlush(accessToken, userId);
    }

    public async Task RestoreStarAsync(
        string accessToken,
        string userId,
        StarLibraryItem item,
        IReadOnlyList<string> categoryIds,
        CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        StarPendingMutation mutation = CreatePendingMutation(userId, item, desiredStarred: true);
        await _store.ApplyPendingRestoreAsync(
            mutation,
            new GitHubStarredRepository { Repository = item.Repository, StarredAt = item.StarredAt },
            categoryIds,
            cancellationToken);

        await InvalidateStarViewsAsync(userId, cancellationToken);
        TrackEventSafely(
            "stars.action.executed",
            new Dictionary<string, string?>
            {
                ["action"] = TelemetryTaxonomy.Actions.UndoUnstar,
                ["result"] = TelemetryTaxonomy.Results.Queued
            });
        RaiseChanged(userId, StarLibraryChangeKind.Items);
        RaiseChanged(userId, StarLibraryChangeKind.Categories);
        ScheduleMutationFlush(accessToken, userId);
    }

    public async Task FlushPendingMutationsAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        SemaphoreSlim gate = _mutationGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<StarPendingMutation> mutations = await _store.GetPendingMutationsAsync(userId, cancellationToken);
            foreach (StarPendingMutation mutation in mutations)
            {
                try
                {
                    if (!GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
                    {
                        if (mutation.DesiredStarred)
                        {
                            await _clientService.StarRepositoryAsync(
                                accessToken,
                                mutation.Owner,
                                mutation.RepositoryName,
                                cancellationToken);
                        }
                        else
                        {
                            await _clientService.UnstarRepositoryAsync(
                                accessToken,
                                mutation.Owner,
                                mutation.RepositoryName,
                                cancellationToken);
                        }
                    }

                    await _store.RemovePendingMutationAsync(
                        userId,
                        mutation.RepositoryId,
                        mutation.DesiredStarred,
                        cancellationToken);
                    TrackEventSafely("stars.action.executed", new Dictionary<string, string?>
                    {
                        ["action"] = mutation.DesiredStarred
                            ? TelemetryTaxonomy.Actions.SyncStar
                            : TelemetryTaxonomy.Actions.SyncUnstar,
                        ["result"] = TelemetryTaxonomy.Results.Success
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await _store.RecordPendingMutationFailureAsync(
                        userId,
                        mutation.RepositoryId,
                        mutation.DesiredStarred,
                        JitHub.WinUI.Helpers.UserFacingError.For(
                            ex,
                            JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                            "stars-mutation"),
                        CancellationToken.None);
                    TrackEventSafely("stars.action.executed", new Dictionary<string, string?>
                    {
                        ["action"] = mutation.DesiredStarred
                            ? TelemetryTaxonomy.Actions.SyncStar
                            : TelemetryTaxonomy.Actions.SyncUnstar,
                        ["result"] = TelemetryTaxonomy.Results.Deferred
                    });
                    break;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task NotifyRepositoryStarStateChangedAsync(
        string accessToken,
        string userId,
        string fullName,
        bool isStarred,
        CancellationToken cancellationToken = default)
    {
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        cancellationToken = lease.CancellationToken;
        if (!isStarred)
        {
            await ApplyRepositoryStarStateChangeAsync(
                accessToken,
                userId,
                fullName,
                repository: null,
                isStarred: false,
                cancellationToken);
            return;
        }

        GitHubRepository? repository = null;
        if (TrySplitFullName(fullName, out string owner, out string name))
        {
            try
            {
                CachedResult<GitHubRepository> result = await _repositoryQueryService.GetRepositoryAsync(
                    accessToken,
                    userId,
                    owner,
                    name,
                    QueryFetchPolicy.StaleFirst,
                    GitHubRequestPriority.BackgroundRefresh,
                    cancellationToken);
                repository = result.Value;
                TrackEventSafely("stars.action.executed", new Dictionary<string, string?>
                {
                    ["action"] = TelemetryTaxonomy.Actions.Hydrate,
                    ["result"] = repository is null
                        ? TelemetryTaxonomy.Results.Empty
                        : TelemetryTaxonomy.Results.Success,
                    ["cache_state"] = result.CacheState.ToString().ToLowerInvariant()
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not hydrate the newly starred repository before synchronization: {ex}");
                TrackEventSafely("stars.action.executed", new Dictionary<string, string?>
                {
                    ["action"] = TelemetryTaxonomy.Actions.Hydrate,
                    ["result"] = TelemetryTaxonomy.Results.Error
                });
            }
        }

        await ApplyRepositoryStarStateChangeAsync(
            accessToken,
            userId,
            fullName,
            repository,
            isStarred: true,
            cancellationToken);
    }

    public async Task NotifyRepositoryStarStateChangedAsync(
        string accessToken,
        string userId,
        GitHubRepository repository,
        bool isStarred,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
        string fullName = string.IsNullOrWhiteSpace(repository.FullName)
            ? $"{repository.Owner.Login}/{repository.Name}"
            : repository.FullName;
        await ApplyRepositoryStarStateChangeAsync(
            accessToken,
            userId,
            fullName,
            repository,
            isStarred,
            lease.CancellationToken).ConfigureAwait(false);
    }

    private Task InvalidateStarViewsAsync(string userId, CancellationToken cancellationToken) =>
        _cacheQueryService.InvalidateTagsAsync(
            GitHubAccountPartition.Require(userId),
            StarMutationCacheTags,
            cancellationToken);

    private async Task ApplyRepositoryStarStateChangeAsync(
        string accessToken,
        string userId,
        string fullName,
        GitHubRepository? repository,
        bool isStarred,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ApplyLocalRepositoryStarStateAsync(
                userId,
                fullName,
                repository,
                isStarred,
                cancellationToken);
            await RemoveSupersededRecoveryAsync(userId, fullName, cancellationToken);
            await PublishRepositoryStarStateChangeAsync(accessToken, userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not StarLibraryDegradedException and not OperationCanceledException)
        {
            StarLibraryRecoveryEntry recovery = new(
                Guid.NewGuid().ToString("N"),
                userId,
                fullName,
                repository,
                isStarred,
                DateTimeOffset.UtcNow,
                0,
                JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "stars-recovery"));
            try
            {
                await _recoveryStore.EnqueueAsync(recovery, CancellationToken.None);
            }
            catch (Exception recoveryException)
            {
                throw new StarLibraryDegradedException(
                    "GitHub updated the star, but JitHub could not persist its local recovery record.",
                    new AggregateException(ex, recoveryException));
            }

            SetDegradedState(
                userId,
                new StarLibraryDegradedState(
                    true,
                    (await _recoveryStore.ReadAsync(userId, CancellationToken.None)).Count,
                    "GitHub updated the star. The local Stars library update is queued for retry."));
            await PublishRepositoryStarStateChangeAsync(accessToken, userId, cancellationToken);
            ScheduleRecoveryRetry(accessToken, userId);
            throw new StarLibraryDegradedException(
                "GitHub updated the star. The local Stars library update is queued for retry.",
                ex);
        }
    }

    private async Task ApplyLocalRepositoryStarStateAsync(
        string userId,
        string fullName,
        GitHubRepository? repository,
        bool isStarred,
        CancellationToken cancellationToken)
    {
        if (!isStarred)
        {
            if (repository is { Id: > 0 })
            {
                await _store.ApplyPendingUnstarAsync(
                    new StarPendingMutation(
                        userId,
                        repository.Id,
                        repository.Owner.Login,
                        repository.Name,
                        false,
                        DateTimeOffset.UtcNow,
                        0,
                        string.Empty),
                    cancellationToken);
            }
            else
            {
                await _store.RemoveRepositoryByFullNameAsync(userId, fullName, cancellationToken);
            }

            return;
        }

        if (repository is not { Id: > 0 })
        {
            throw new InvalidOperationException("The newly starred repository is not available for local indexing yet.");
        }

        IReadOnlyList<string> categoryIds = await _store.GetCategoryIdsAsync(
            userId,
            repository.Id,
            cancellationToken);
        StarPendingMutation mutation = new(
            userId,
            repository.Id,
            repository.Owner.Login,
            repository.Name,
            true,
            DateTimeOffset.UtcNow,
            0,
            string.Empty);
        await _store.ApplyPendingRestoreAsync(
            mutation,
            new GitHubStarredRepository
            {
                Repository = repository,
                StarredAt = DateTimeOffset.UtcNow
            },
            categoryIds,
            cancellationToken);
    }

    private async Task PublishRepositoryStarStateChangeAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await InvalidateStarViewsAsync(userId, cancellationToken);
        }
        finally
        {
            // Invalidate first so visible Home/Profile subscribers cannot race back into a
            // still-valid projection after receiving the change notification.
            RaiseChanged(userId, StarLibraryChangeKind.Items);
            RaiseChanged(userId, StarLibraryChangeKind.Categories);
            RaiseChanged(userId, StarLibraryChangeKind.ProjectionInvalidated);
            ScheduleSynchronization(accessToken, userId);
        }
    }

    private async Task<bool> TryReplayRecoveryAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StarLibraryRecoveryEntry> entries = await _recoveryStore.ReadAsync(userId, cancellationToken);
        if (entries.Count == 0)
        {
            SetDegradedState(userId, StarLibraryDegradedState.Healthy);
            return true;
        }

        foreach (StarLibraryRecoveryEntry entry in entries)
        {
            try
            {
                await ApplyLocalRepositoryStarStateAsync(
                    entry.UserId,
                    entry.FullName,
                    entry.Repository,
                    entry.DesiredStarred,
                    cancellationToken);
                await _recoveryStore.RemoveAsync(entry.Id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _recoveryStore.EnqueueAsync(
                    entry with
                    {
                        AttemptCount = entry.AttemptCount + 1,
                        LastError = JitHub.WinUI.Helpers.UserFacingError.For(
                            ex,
                            JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                            "stars-recovery")
                    },
                    CancellationToken.None);
                SetDegradedState(
                    userId,
                    new StarLibraryDegradedState(
                        true,
                        entries.Count,
                        "A local Stars library update is waiting to be retried."));
                ScheduleRecoveryRetry(accessToken, userId);
                return false;
            }
        }

        SetDegradedState(userId, StarLibraryDegradedState.Healthy);
        await PublishRepositoryStarStateChangeAsync(accessToken, userId, cancellationToken);
        return true;
    }

    private async Task RemoveSupersededRecoveryAsync(
        string userId,
        string fullName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StarLibraryRecoveryEntry> entries = await _recoveryStore.ReadAsync(userId, cancellationToken);
        foreach (StarLibraryRecoveryEntry entry in entries.Where(entry =>
            string.Equals(entry.FullName, fullName, StringComparison.OrdinalIgnoreCase)))
        {
            await _recoveryStore.RemoveAsync(entry.Id, cancellationToken);
        }

        IReadOnlyList<StarLibraryRecoveryEntry> remaining = await _recoveryStore.ReadAsync(userId, cancellationToken);
        SetDegradedState(
            userId,
            remaining.Count == 0
                ? StarLibraryDegradedState.Healthy
                : new StarLibraryDegradedState(true, remaining.Count, "Local Stars library updates are waiting to be retried."));
    }

    private void SetDegradedState(string userId, StarLibraryDegradedState state)
    {
        _degradedStates[userId] = state;
        RaiseChanged(userId, StarLibraryChangeKind.Degraded);
    }

    private void ScheduleRecoveryRetry(string accessToken, string userId)
    {
        if (!_scheduledRecoveryRetries.TryAdd(userId, 0))
        {
            return;
        }

        _ = _taskCoordinator.RunAsync(
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    using IAccountWorkLease lease = EnterAccountWork(userId, cancellationToken);
                    await TryReplayRecoveryAsync(accessToken, userId, lease.CancellationToken);
                }
                finally
                {
                    _scheduledRecoveryRetries.TryRemove(userId, out _);
                }
            },
            new ApplicationTaskOptions("stars.recovery_retry", userId));
    }

    private void ScheduleSynchronization(string accessToken, string userId) =>
        _ = _taskCoordinator.RunAsync(
            token => SynchronizeAsync(accessToken, userId, forceFull: false, token),
            new ApplicationTaskOptions("stars.synchronize", userId));

    private static bool TrySplitFullName(string fullName, out string owner, out string name)
    {
        string[] parts = fullName.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        owner = parts.Length == 2 ? parts[0] : string.Empty;
        name = parts.Length == 2 ? parts[1] : string.Empty;
        return parts.Length == 2;
    }

    private void RaiseChanged(string userId, StarLibraryChangeKind kind) =>
        Changed?.Invoke(this, new StarLibraryChangedEventArgs(userId, kind));

    private void ScheduleMutationFlush(string accessToken, string userId) =>
        _ = _taskCoordinator.RunAsync(
            token => FlushPendingMutationsAsync(accessToken, userId, token),
            new ApplicationTaskOptions("stars.mutation_flush", userId));

    private static StarPendingMutation CreatePendingMutation(string userId, StarLibraryItem item, bool desiredStarred) =>
        new(
            userId,
            item.Repository.Id,
            item.Repository.Owner.Login,
            item.Repository.Name,
            desiredStarred,
            DateTimeOffset.UtcNow,
            0,
            string.Empty);

    private void TrackCategory(string eventName, string action)
    {
        TrackEventSafely(
            eventName,
            new Dictionary<string, string?>
            {
                ["action"] = action,
                ["result"] = TelemetryTaxonomy.Results.Success
            });
    }

    private void TrackMembership(string action, int count) =>
        TrackEventSafely("stars.membership.changed", new Dictionary<string, string?>
        {
            ["action"] = action,
            ["result"] = TelemetryTaxonomy.Results.Success,
            ["count_bucket"] = CountBucket(count)
        });

    private void TrackEventSafely(string eventName, IReadOnlyDictionary<string, string?> properties)
    {
        try
        {
            _telemetry.TrackEvent(eventName, properties);
        }
        catch
        {
            // Store state is authoritative; telemetry must never change an operation's result.
        }
    }

    private IAccountWorkLease EnterAccountWork(string userId, CancellationToken cancellationToken) =>
        Volatile.Read(ref _disposed) != 0
            ? throw new ObjectDisposedException(nameof(GitHubStarLibraryService))
            : _accountWork.Enter(GitHubAccountPartition.Require(userId), cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (KeyValuePair<string, StarSyncRequestCoordinator> entry in _syncCoordinators.ToArray())
        {
            if (_syncCoordinators.TryRemove(entry.Key, out StarSyncRequestCoordinator? coordinator))
            {
                coordinator.Dispose();
            }
        }

        foreach (KeyValuePair<string, SemaphoreSlim> entry in _mutationGates.ToArray())
        {
            if (_mutationGates.TryRemove(entry.Key, out SemaphoreSlim? gate))
            {
                gate.Dispose();
            }
        }

        _degradedStates.Clear();
        _scheduledRecoveryRetries.Clear();
    }

    private static string CountBucket(int count) => TelemetryTaxonomy.CountBucket(count);

    private sealed class StarSynchronizationFailedException : Exception
    {
        public StarSynchronizationFailedException(StarSyncState state, Exception innerException)
            : base("Stars synchronization failed.", innerException)
        {
            State = state;
        }

        public StarSyncState State { get; }
    }
}
