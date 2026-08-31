using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.CodeViewer;

namespace JitHub.Services;

public static class AccountDataComponentIds
{
    public const string QueryCache = "query-cache";
    public const string ImageCache = "image-cache";
    public const string RepositoryFiles = "repository-files";
    public const string RepositoryTrees = "repository-trees";
    public const string StarsRuntime = "stars-runtime";
    public const string StarsLibrary = "stars-library";
    public const string StarsRecovery = "stars-recovery";
    public const string GistMutationJournal = "gist-mutation-journal";
    public const string RepositoryForkRecovery = "repository-fork-recovery";
    public const string IssueNavigation = "issue-navigation";
    public const string PullRequestNavigation = "pull-request-navigation";
    public const string CommitNavigation = "commit-navigation";
    public const string RepositoryIndex = "repository-index";
    public const string Credential = "credential";
}

public sealed record AccountDataRemovalFailure(
    string Component,
    string ErrorType,
    string Message);

public sealed record AccountDataRemovalResult(
    IReadOnlyList<string> ClearedComponents,
    IReadOnlyList<AccountDataRemovalFailure> Failures)
{
    public bool IsComplete => Failures.Count == 0;

    public string DiagnosticsDisposition =>
        "Local diagnostics are app-level and identifier-free, so they are retained.";
}

public interface IAccountDataRemovalCoordinator
{
    Task<AccountDataRemovalResult> RemoveAsync(
        string accountPartition,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountDataRemovalResult>> ResumePendingAsync(
        CancellationToken cancellationToken = default);

    void Resume(string accountPartition);
}

internal sealed record AccountDataRemovalStep(
    string Component,
    Func<string, CancellationToken, Task> RemoveAsync);

public sealed class AccountDataRemovalCoordinator : IAccountDataRemovalCoordinator
{
    private readonly IReadOnlyList<AccountDataRemovalStep> _steps;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly IAuthCredentialStore? _credentialStore;
    private readonly IAccountDataRemovalJournal _journal;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationGates = new(StringComparer.Ordinal);

    public AccountDataRemovalCoordinator(
        IGitHubCacheStore queryCache,
        IGitHubImageCacheStore imageCache,
        IRepoFileCacheService repositoryFiles,
        IStarLibraryStore stars,
        IStarLibraryRecoveryStore starsRecovery,
        IGitHubStarLibraryService starLibraryService,
        IGistMutationJournal gistMutationJournal,
        IRepositoryForkOwnershipStore repositoryForkRecovery,
        IIssueNavigationCache issueNavigation,
        IPullRequestNavigationCache pullRequestNavigation,
        ICommitNavigationCache commitNavigation,
        IGitHubRepositoryIndexService repositoryIndex,
        IAccountWorkQuiescence accountWork,
        IAuthCredentialStore credentialStore,
        IAccountDataRemovalJournal journal,
        IApplicationTaskCoordinator taskCoordinator,
        IRepoTreeService? repositoryTrees = null)
        : this(
        CreateRemovalSteps(
            queryCache,
            imageCache,
            repositoryFiles,
            stars,
            starsRecovery,
            starLibraryService,
            gistMutationJournal,
            repositoryForkRecovery,
            issueNavigation,
            pullRequestNavigation,
            commitNavigation,
            repositoryIndex,
            repositoryTrees),
        accountWork,
        credentialStore,
        journal,
        taskCoordinator)
    {
    }

    private static IReadOnlyList<AccountDataRemovalStep> CreateRemovalSteps(
        IGitHubCacheStore queryCache,
        IGitHubImageCacheStore imageCache,
        IRepoFileCacheService repositoryFiles,
        IStarLibraryStore stars,
        IStarLibraryRecoveryStore starsRecovery,
        IGitHubStarLibraryService starLibraryService,
        IGistMutationJournal gistMutationJournal,
        IRepositoryForkOwnershipStore repositoryForkRecovery,
        IIssueNavigationCache issueNavigation,
        IPullRequestNavigationCache pullRequestNavigation,
        ICommitNavigationCache commitNavigation,
        IGitHubRepositoryIndexService repositoryIndex,
        IRepoTreeService? repositoryTrees)
    {
        List<AccountDataRemovalStep> steps =
        [
            new(AccountDataComponentIds.QueryCache, queryCache.ClearPartitionAsync),
            new(AccountDataComponentIds.ImageCache, imageCache.ClearPartitionAsync),
            new(AccountDataComponentIds.RepositoryFiles, repositoryFiles.ClearPartitionAsync),
            new(AccountDataComponentIds.StarsRuntime, starLibraryService.ClearAccountStateAsync),
            new(AccountDataComponentIds.StarsLibrary, stars.ClearUserAsync),
            new(AccountDataComponentIds.StarsRecovery, starsRecovery.ClearUserAsync),
            new(AccountDataComponentIds.GistMutationJournal, gistMutationJournal.ClearAccountAsync),
            new(AccountDataComponentIds.RepositoryForkRecovery, repositoryForkRecovery.ClearAccountAsync),
            new(AccountDataComponentIds.IssueNavigation, issueNavigation.ClearPartitionAsync),
            new(AccountDataComponentIds.PullRequestNavigation, pullRequestNavigation.ClearPartitionAsync),
            new(AccountDataComponentIds.CommitNavigation, commitNavigation.ClearPartitionAsync),
            new(AccountDataComponentIds.RepositoryIndex, repositoryIndex.ClearPartitionAsync)
        ];
        if (repositoryTrees is not null)
        {
            steps.Insert(
                1,
                new AccountDataRemovalStep(
                    AccountDataComponentIds.RepositoryTrees,
                    repositoryTrees.ClearMemoryCacheAsync));
        }

        return steps;
    }

    internal AccountDataRemovalCoordinator(
        IReadOnlyList<AccountDataRemovalStep> steps,
        IAccountWorkQuiescence? accountWork = null,
        IAuthCredentialStore? credentialStore = null,
        IAccountDataRemovalJournal? journal = null,
        IApplicationTaskCoordinator? taskCoordinator = null)
    {
        _steps = steps;
        _accountWork = accountWork ?? new AccountWorkQuiescence();
        _credentialStore = credentialStore;
        _journal = journal ?? new InMemoryAccountDataRemovalJournal();
        _taskCoordinator = taskCoordinator ?? new ApplicationTaskCoordinator();
    }

    public async Task<AccountDataRemovalResult> RemoveAsync(
        string accountPartition,
        CancellationToken cancellationToken = default)
    {
        string partition = GitHubAccountPartition.Require(accountPartition);
        SemaphoreSlim operationGate = _operationGates.GetOrAdd(partition, static _ => new SemaphoreSlim(1, 1));
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RemoveCoreAsync(partition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<AccountDataRemovalResult> RemoveCoreAsync(
        string partition,
        CancellationToken cancellationToken)
    {
        string[] requestedComponents = _steps
            .Select(static step => step.Component)
            .Append(AccountDataComponentIds.Credential)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AccountDataRemovalJournalEntry journal = await _journal.BeginOrReadAsync(
            partition,
            requestedComponents,
            cancellationToken).ConfigureAwait(false);
        await _taskCoordinator.CancelAccountAsync(partition, cancellationToken).ConfigureAwait(false);
        await _accountWork.QuiesceAsync(partition, cancellationToken).ConfigureAwait(false);
        HashSet<string> completed = new(journal.CompletedComponents, StringComparer.Ordinal);
        List<string> cleared = [.. journal.CompletedComponents];
        List<AccountDataRemovalFailure> failures = [];
        HashSet<string> knownComponents = new(requestedComponents, StringComparer.Ordinal);
        foreach (string unavailable in journal.RequestedComponents.Where(component => !knownComponents.Contains(component)))
        {
            failures.Add(new AccountDataRemovalFailure(
                unavailable,
                nameof(InvalidOperationException),
                "The cleanup handler for this recorded component is unavailable."));
        }

        foreach (AccountDataRemovalStep step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Contains(step.Component))
            {
                continue;
            }

            try
            {
                await step.RemoveAsync(partition, cancellationToken).ConfigureAwait(false);
                await _journal.MarkCompletedAsync(partition, step.Component, cancellationToken)
                    .ConfigureAwait(false);
                completed.Add(step.Component);
                cleared.Add(step.Component);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(new AccountDataRemovalFailure(
                    step.Component,
                    exception.GetType().Name,
                    exception.Message));
            }
        }

        bool storesComplete = _steps.All(step => completed.Contains(step.Component));
        if (storesComplete && !completed.Contains(AccountDataComponentIds.Credential))
        {
            try
            {
                if (_credentialStore is null)
                {
                    await _journal.MarkCompletedAsync(
                        partition,
                        AccountDataComponentIds.Credential,
                        cancellationToken).ConfigureAwait(false);
                    completed.Add(AccountDataComponentIds.Credential);
                    cleared.Add(AccountDataComponentIds.Credential);
                }
                else
                {
                    if (!long.TryParse(partition, NumberStyles.None, CultureInfo.InvariantCulture, out long userId) ||
                        userId <= 0)
                    {
                        throw new InvalidOperationException("The account partition cannot identify a credential.");
                    }

                    _credentialStore.RemoveAccountToken(userId);
                    if (_credentialStore.GetAccountToken(userId) is not null)
                    {
                        throw new IOException("The account credential remained present after removal.");
                    }

                    await _journal.MarkCompletedAsync(
                        partition,
                        AccountDataComponentIds.Credential,
                        cancellationToken).ConfigureAwait(false);
                    completed.Add(AccountDataComponentIds.Credential);
                    cleared.Add(AccountDataComponentIds.Credential);
                }
            }
            catch (Exception exception)
            {
                failures.Add(new AccountDataRemovalFailure(
                    AccountDataComponentIds.Credential,
                    exception.GetType().Name,
                    exception.Message));
            }
        }

        bool operationComplete = journal.RequestedComponents.All(completed.Contains);
        if (operationComplete)
        {
            await _journal.DeleteAsync(partition, cancellationToken).ConfigureAwait(false);
        }

        return new AccountDataRemovalResult(cleared, failures);
    }

    public async Task<IReadOnlyList<AccountDataRemovalResult>> ResumePendingAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AccountDataRemovalJournalEntry> pending = await _journal
            .ReadPendingAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AccountDataRemovalResult> results = new(pending.Count);
        foreach (AccountDataRemovalJournalEntry entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RemoveAsync(entry.AccountPartition, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public void Resume(string accountPartition)
    {
        _accountWork.Activate(GitHubAccountPartition.Require(accountPartition));
    }
}
