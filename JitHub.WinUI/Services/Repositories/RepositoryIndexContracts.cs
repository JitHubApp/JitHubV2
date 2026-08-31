using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed record AccountRepositoryIndexSnapshot(
    string UserId,
    IReadOnlyList<GitHubRepository> Repositories,
    bool IsComplete,
    bool IsSynchronizing,
    int PagesLoaded,
    CacheState CacheState,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage)
{
    public static AccountRepositoryIndexSnapshot Empty(string userId) =>
        new(userId, [], false, false, 0, CacheState.Miss, null, null);

    public int IndexedCount => Repositories.Count;
}

public sealed class AccountRepositoryIndexChangedEventArgs : EventArgs
{
    public AccountRepositoryIndexChangedEventArgs(AccountRepositoryIndexSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public AccountRepositoryIndexSnapshot Snapshot { get; }
}

public interface IGitHubRepositoryIndexService
{
    event EventHandler<AccountRepositoryIndexChangedEventArgs>? Changed;

    AccountRepositoryIndexSnapshot GetSnapshot(string userId);

    Task<AccountRepositoryIndexSnapshot> InitializeAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken = default);

    Task<AccountRepositoryIndexSnapshot> SynchronizeAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false);

    Task RemoveRepositoriesAsync(
        string userId,
        IReadOnlyCollection<long> repositoryIds,
        CancellationToken cancellationToken = default);

    Task ClearPartitionAsync(
        string userId,
        CancellationToken cancellationToken = default);
}
