using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum StarSmartList
{
    All,
    Uncategorized,
    RecentlyStarred,
    RecentlyActive,
    Archived
}

public enum StarLibrarySort
{
    RecentlyStarred,
    RecentlyActive,
    MostStars,
    Name,
    LeastRecentlyActive
}

public sealed record StarLibraryFilter(
    string[] Languages,
    string[] Owners,
    string[] Topics,
    bool? IsPrivate = null,
    bool? IsFork = null,
    bool? IsArchived = null,
    bool? IsCategorized = null)
{
    public static StarLibraryFilter Empty { get; } = new([], [], []);

    public bool IsEmpty =>
        Languages.Length == 0 && Owners.Length == 0 && Topics.Length == 0 &&
        IsPrivate is null && IsFork is null && IsArchived is null && IsCategorized is null;
}

public sealed record StarLibraryQuery(
    string UserId,
    StarSmartList SmartList,
    string? CategoryId,
    string SearchText,
    StarLibraryFilter Filter,
    StarLibrarySort Sort,
    int Offset,
    int Limit);

public sealed record StarCategory(
    string Id,
    string UserId,
    string Name,
    string Color,
    int Position,
    int RepositoryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StarCategoryMembership(string CategoryId, long RepositoryId);

public sealed record StarPendingMutation(
    string UserId,
    long RepositoryId,
    string Owner,
    string RepositoryName,
    bool DesiredStarred,
    DateTimeOffset CreatedAt,
    int AttemptCount,
    string LastError);

public sealed record StarLibraryRecoveryEntry(
    string Id,
    string UserId,
    string FullName,
    GitHubRepository? Repository,
    bool DesiredStarred,
    DateTimeOffset CreatedAt,
    int AttemptCount,
    string LastError);

public sealed record StarLibraryClearRecoveryState(string TransactionId);

public interface IStarLibraryRecoveryClearTransaction : IAsyncDisposable
{
    string TransactionId { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public sealed record StarLibraryDegradedState(
    bool IsDegraded,
    int PendingRecoveryCount,
    string Message)
{
    public static StarLibraryDegradedState Healthy { get; } = new(false, 0, string.Empty);
}

public sealed class StarLibraryDegradedException : Exception
{
    public StarLibraryDegradedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record StarLibraryItem(
    GitHubRepository Repository,
    DateTimeOffset StarredAt,
    IReadOnlyList<StarCategory> Categories)
{
    public string Key => Repository.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record StarLibraryPage(
    IReadOnlyList<StarLibraryItem> Items,
    int TotalCount,
    bool HasMore,
    StarSyncState SyncState);

public sealed record StarSyncState(
    string UserId,
    DateTimeOffset? LastIncrementalSync,
    DateTimeOffset? LastFullSync,
    bool IsComplete,
    bool IsSyncing,
    int IndexedCount,
    string ErrorMessage)
{
    public static StarSyncState Empty(string userId) =>
        new(userId, null, null, false, false, 0, string.Empty);
}

public sealed record StarLibrarySnapshot(
    StarLibraryPage Page,
    IReadOnlyList<StarCategory> Categories,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> Topics,
    IReadOnlyDictionary<StarSmartList, int> SmartListCounts);

public enum StarLibraryChangeKind
{
    Items,
    Categories,
    Sync,
    ProjectionInvalidated,
    Degraded
}

public sealed record StarLibraryChangedEventArgs(string UserId, StarLibraryChangeKind Kind);

public sealed class StarLibrarySessionState
{
    public string SearchText { get; set; } = string.Empty;
    public string SelectedNavigationId { get; set; } = "smart:all";
    public StarLibrarySort Sort { get; set; } = StarLibrarySort.RecentlyStarred;
    public string Language { get; set; } = "All languages";
    public string Owner { get; set; } = "All owners";
    public string Topic { get; set; } = "All topics";
    public string Visibility { get; set; } = "All visibility";
    public string RepositoryKind { get; set; } = "All repositories";
    public string Activity { get; set; } = "Active and archived";
    public string CategoryState { get; set; } = "Any category";
    public HashSet<long> SelectedRepositoryIds { get; } = [];
    public double ScrollOffset { get; set; }
}

public interface IStarLibraryStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<StarLibraryPage> QueryAsync(StarLibraryQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StarCategory>> GetCategoriesAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetFacetValuesAsync(string userId, string facet, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<StarSmartList, int>> GetSmartListCountsAsync(string userId, CancellationToken cancellationToken = default);

    Task UpsertPageAsync(
        string userId,
        IReadOnlyList<GitHubStarredRepository> repositories,
        string syncGeneration,
        CancellationToken cancellationToken = default);

    Task CompleteFullSyncAsync(string userId, string syncGeneration, CancellationToken cancellationToken = default);

    Task<StarSyncState> GetSyncStateAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveSyncStateAsync(StarSyncState state, CancellationToken cancellationToken = default);

    Task<StarCategory> CreateCategoryAsync(string userId, string name, string color, CancellationToken cancellationToken = default);

    Task<StarCategory> UpdateCategoryAsync(string userId, string categoryId, string name, string color, CancellationToken cancellationToken = default);

    Task DeleteCategoryAsync(string userId, string categoryId, CancellationToken cancellationToken = default);

    Task ReorderCategoryAsync(string userId, string categoryId, int targetPosition, CancellationToken cancellationToken = default);

    Task AddToCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default);

    Task RemoveFromCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetCategoryIdsAsync(string userId, long repositoryId, CancellationToken cancellationToken = default);

    Task RemoveRepositoryAsync(string userId, long repositoryId, CancellationToken cancellationToken = default);

    Task RemoveRepositoryByFullNameAsync(string userId, string fullName, CancellationToken cancellationToken = default);

    Task ApplyPendingUnstarAsync(StarPendingMutation mutation, CancellationToken cancellationToken = default);

    Task ApplyPendingRestoreAsync(
        StarPendingMutation mutation,
        GitHubStarredRepository repository,
        IReadOnlyList<string> categoryIds,
        CancellationToken cancellationToken = default);

    Task SavePendingMutationAsync(StarPendingMutation mutation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StarPendingMutation>> GetPendingMutationsAsync(string userId, CancellationToken cancellationToken = default);

    Task RemovePendingMutationAsync(string userId, long repositoryId, bool desiredStarred, CancellationToken cancellationToken = default);

    Task RecordPendingMutationFailureAsync(
        string userId,
        long repositoryId,
        bool desiredStarred,
        string error,
        CancellationToken cancellationToken = default);

    Task<long> GetSizeAsync(CancellationToken cancellationToken = default);

    Task ClearAllAsync(CancellationToken cancellationToken = default);

    Task ClearUserAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "This Stars library store does not support account-partition removal."));

    Task ClearAllAsync(string transactionId, CancellationToken cancellationToken = default);

    Task<bool> IsClearTransactionCommittedAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetCommittedClearTransactionsAsync(
        CancellationToken cancellationToken = default);

    Task CompleteClearTransactionAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CacheStoreInspection.Unavailable("Integrity inspection is not implemented by this Stars store."));
}

public interface IStarLibraryRecoveryStore
{
    string FilePath { get; }

    Task EnqueueAsync(StarLibraryRecoveryEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StarLibraryRecoveryEntry>> ReadAsync(string userId, CancellationToken cancellationToken = default);

    Task RemoveAsync(string entryId, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);

    Task ClearUserAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "This Stars recovery store does not support account-partition removal."));

    Task<IStarLibraryRecoveryClearTransaction> BeginClearAsync(
        CancellationToken cancellationToken = default);

    Task<StarLibraryClearRecoveryState?> GetPendingClearAsync(
        CancellationToken cancellationToken = default);

    Task CommitPendingClearAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task RollbackPendingClearAsync(
        string transactionId,
        CancellationToken cancellationToken = default);

    Task<long> GetSizeAsync(CancellationToken cancellationToken = default);

    Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken = default);
}

public interface IGitHubStarQueryService
{
    Task<CachedResult<GitHubStarredRepository[]>> GetPageAsync(
        string accessToken,
        string userId,
        int page,
        QueryFetchPolicy fetchPolicy,
        GitHubRequestPriority priority,
        CancellationToken cancellationToken = default);
}

public interface IGitHubStarLibraryService
{
    event EventHandler<StarLibraryChangedEventArgs>? Changed;

    StarLibraryDegradedState GetDegradedState(string userId);

    Task ClearAccountStateAsync(string userId, CancellationToken cancellationToken = default);

    Task<StarLibraryPage> LoadCachedPageAsync(string accessToken, string userId, StarLibraryQuery query, CancellationToken cancellationToken = default);

    Task<StarLibrarySnapshot> InitializeAsync(string accessToken, string userId, StarLibraryQuery query, CancellationToken cancellationToken = default);

    Task<StarLibraryPage> QueryAsync(StarLibraryQuery query, CancellationToken cancellationToken = default);

    Task<StarSyncState> SynchronizeAsync(string accessToken, string userId, bool forceFull = false, CancellationToken cancellationToken = default);

    Task<StarCategory> CreateCategoryAsync(string userId, string name, string color, CancellationToken cancellationToken = default);

    Task<StarCategory> UpdateCategoryAsync(string userId, string categoryId, string name, string color, CancellationToken cancellationToken = default);

    Task DeleteCategoryAsync(string userId, string categoryId, CancellationToken cancellationToken = default);

    Task ReorderCategoryAsync(string userId, string categoryId, int targetPosition, CancellationToken cancellationToken = default);

    Task AddToCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default);

    Task RemoveFromCategoryAsync(string userId, string categoryId, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken = default);

    Task UnstarAsync(string accessToken, string userId, StarLibraryItem item, CancellationToken cancellationToken = default);

    Task RestoreStarAsync(string accessToken, string userId, StarLibraryItem item, IReadOnlyList<string> categoryIds, CancellationToken cancellationToken = default);

    Task FlushPendingMutationsAsync(string accessToken, string userId, CancellationToken cancellationToken = default);

    Task NotifyRepositoryStarStateChangedAsync(string accessToken, string userId, string fullName, bool isStarred, CancellationToken cancellationToken = default);

    Task NotifyRepositoryStarStateChangedAsync(
        string accessToken,
        string userId,
        GitHubRepository repository,
        bool isStarred,
        CancellationToken cancellationToken = default);
}
