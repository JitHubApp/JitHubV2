using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.ViewModels.CodeViewer;

/// <summary>
/// Warms the non-visual repository root projection before navigation. Prepared
/// view models are transferred to one destination page and are never shared.
/// Directories are deliberately lazy: rendering a README must not download or
/// project the repository's complete recursive Git tree.
/// </summary>
public sealed partial class RepoCodeNavigationPreparationCache
{
    private const int MaximumPreparedRepositoryCount = 8;
    private readonly IRepoTreeService _treeService;
    private readonly ILanguageIdResolver _languageResolver;
    private readonly IAccountService _accountService;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();

    public RepoCodeNavigationPreparationCache(
        IRepoTreeService treeService,
        ILanguageIdResolver languageResolver,
        IAccountService accountService)
    {
        _treeService = treeService;
        _languageResolver = languageResolver;
        _accountService = accountService;
    }

    public async Task PrefetchAsync(
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string key, Entry entry) = GetOrStart(owner, name, gitRef);
        try
        {
            _ = await entry.Preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveAbandonedWaitAsync(key, entry);
            throw;
        }
        catch
        {
            RemoveIfCurrent(key, entry, cancel: false);
            throw;
        }
    }

    public async Task PrefetchRouteAsync(
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string key, Entry entry) = GetOrStart(owner, name, gitRef);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => CancelPrefetchEntry(key, entry));
        try
        {
            _ = await entry.Preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelPrefetchEntry(key, entry);
            throw;
        }
        catch
        {
            RemoveIfCurrent(key, entry, cancel: false);
            throw;
        }
    }

    internal async Task<PreparedRepoCodeNavigation> TakeOrPrepareAsync(
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string key, Entry entry) = GetOrStart(owner, name, gitRef);
        entry.ClaimForeground();
        try
        {
            PreparedRepoCodeNavigation prepared =
                await entry.Preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
            RemoveIfCurrent(key, entry, cancel: false);
            return prepared;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RemoveIfCurrent(key, entry, cancel: true);
            throw;
        }
        catch
        {
            RemoveIfCurrent(key, entry, cancel: false);
            throw;
        }
    }

    internal async Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?> GetReadmeAsync(
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (_, Entry entry) = GetOrStart(owner, name, gitRef);
        entry.ClaimForeground();
        return await entry.Readme.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private (string Key, Entry Entry) GetOrStart(string owner, string name, string gitRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);

        string key = CreateKey(owner, name, gitRef);
        List<Entry> evicted = [];
        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? existing))
            {
                Touch(existing);
                return (key, existing);
            }

            CancellationTokenSource workCancellation = new();
            LinkedListNode<string> node = _lru.AddFirst(key);
            PreparationWork work = StartPreparation(owner, name, gitRef, workCancellation.Token);
            entry = new Entry(work.Preparation, work.Readme, node, workCancellation);
            _entries[key] = entry;
            TrimToBudget(evicted);
        }

        foreach (Entry retired in evicted)
        {
            Retire(retired, cancel: true);
        }

        return (key, entry);
    }

    private PreparationWork StartPreparation(
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken)
    {
        Task<RepoCodeLoadResult<IReadOnlyList<Models.CodeViewer.RepoTreeNode>>> rootTask =
            _treeService.LoadDirectoryAsync(
                owner,
                name,
                string.Empty,
                gitRef,
                cancellationToken);
        Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?> readmeTask =
            TryLoadReadmeAsync(owner, name, gitRef, cancellationToken);
        return new PreparationWork(
            CompletePreparationAsync(rootTask, readmeTask, cancellationToken),
            readmeTask);
    }

    private async Task<PreparedRepoCodeNavigation> CompletePreparationAsync(
        Task<RepoCodeLoadResult<IReadOnlyList<Models.CodeViewer.RepoTreeNode>>> rootTask,
        Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?> readmeTask,
        CancellationToken cancellationToken)
    {
        RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>? readme =
            await readmeTask.ConfigureAwait(false);
        RepoCodeLoadResult<Models.CodeViewer.RepoTree> result;
        bool rootListingUnavailable = false;
        try
        {
            RepoCodeLoadResult<IReadOnlyList<Models.CodeViewer.RepoTreeNode>> root =
                await rootTask.ConfigureAwait(false);
            result = RepoRootTreeProjection.Create(root);
        }
        catch (GitHubRateLimitException exception) when (
            readme is { CacheState: CacheState.Fresh } &&
            readme.Value.Blob.Text is not null &&
            RepoTreeService.IsAnonymousPublicDataFallbackCandidate(exception))
        {
            // Some public organizations deny the authenticated root-listing API
            // to the current IP even while the canonical README is available.
            // Keep that fetched document usable, but never present its one file
            // as an authoritative or complete repository tree.
            Models.CodeViewer.RepoReadmeFile file = readme.Value;
            result = new RepoCodeLoadResult<Models.CodeViewer.RepoTree>(
                new Models.CodeViewer.RepoTree
                {
                    Truncated = true,
                    RootIsAuthoritative = false,
                    Root = new Models.CodeViewer.RepoTreeNode
                    {
                        Name = string.Empty,
                        Path = string.Empty,
                        IsDirectory = true,
                        Children =
                        [
                            new Models.CodeViewer.RepoTreeNode
                            {
                                Name = file.Name,
                                Path = file.Path,
                                Sha = file.Blob.Sha,
                                Size = file.Blob.Bytes?.Length ?? 0,
                                IsDirectory = false,
                                Children = []
                            }
                        ]
                    }
                },
                CacheState.Error,
                RefreshError: UserFacingError.For(
                    exception,
                    UserFacingErrorKind.Loading,
                    "repository-code-root-listing"));
            rootListingUnavailable = true;
        }
        RepoFileTreeViewModel.PreparedTree prepared = await Task.Run(
            () => RepoFileTreeViewModel.PrepareTree(
                result.Value,
                _languageResolver,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new PreparedRepoCodeNavigation(result, prepared, readme, rootListingUnavailable);
    }

    private async Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?> TryLoadReadmeAsync(
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken)
    {
        try
        {
            // The dedicated endpoint returns the canonical root README and its content in
            // one response. It runs beside the root listing so initial rendering does not
            // pay a list-root-then-fetch-blob network waterfall.
            Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?>? request =
                _treeService.LoadReadmeAsync(owner, name, gitRef, cancellationToken);
            return request is null ? null : await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is GitHubApiException or HttpRequestException or InvalidDataException or FormatException or NotSupportedException)
        {
            // This is an optimization, never a new availability dependency. The page can
            // still discover the root README and use the ordinary immutable blob path.
            return null;
        }
    }

    private string CreateKey(string owner, string name, string gitRef)
    {
        long accountId = _accountService.GetUser();
        string partition = accountId > 0
            ? accountId.ToString(CultureInfo.InvariantCulture)
            : "current";
        return string.Join(
            ':',
            partition,
            owner.Trim().ToLowerInvariant(),
            name.Trim().ToLowerInvariant(),
            gitRef.Trim());
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void TrimToBudget(List<Entry> evicted)
    {
        LinkedListNode<string>? candidate = _lru.Last;
        while (_entries.Count > MaximumPreparedRepositoryCount && candidate is not null)
        {
            LinkedListNode<string>? previous = candidate.Previous;
            if (_entries.TryGetValue(candidate.Value, out Entry? entry) &&
                !entry.HasForegroundConsumer)
            {
                _lru.Remove(candidate);
                _entries.Remove(candidate.Value);
                evicted.Add(entry);
            }

            candidate = previous;
        }
    }

    private void CancelPrefetchEntry(string key, Entry entry)
    {
        bool removed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? current) &&
                ReferenceEquals(current, entry) &&
                !entry.HasForegroundConsumer)
            {
                _entries.Remove(key);
                _lru.Remove(entry.Node);
                removed = true;
            }
        }

        if (removed)
        {
            Retire(entry, cancel: true);
        }
    }

    private void RemoveIfCurrent(string key, Entry entry, bool cancel)
    {
        bool removed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                _lru.Remove(entry.Node);
                removed = true;
            }
        }

        if (removed)
        {
            Retire(entry, cancel);
        }
    }

    private static void Retire(Entry entry, bool cancel)
    {
        if (cancel)
        {
            entry.Cancel();
        }

        if (entry.Preparation.IsCompleted)
        {
            entry.Dispose();
            return;
        }

        _ = DisposeWhenPreparationCompletesAsync(entry);
    }

    private static async Task DisposeWhenPreparationCompletesAsync(Entry entry)
    {
        try
        {
            _ = await entry.Preparation.ConfigureAwait(false);
        }
        catch
        {
            // Preparation failures are observed by the route/foreground caller.
        }
        finally
        {
            entry.Dispose();
        }
    }

    private async Task ObserveAbandonedWaitAsync(string key, Entry entry)
    {
        try
        {
            _ = await entry.Preparation.ConfigureAwait(false);
        }
        catch
        {
            RemoveIfCurrent(key, entry, cancel: false);
        }
    }

    private sealed partial class Entry : IDisposable
    {
        private readonly CancellationTokenSource _workCancellation;
        private int _hasForegroundConsumer;

        public Entry(
            Task<PreparedRepoCodeNavigation> preparation,
            Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?> readme,
            LinkedListNode<string> node,
            CancellationTokenSource workCancellation)
        {
            Preparation = preparation;
            Readme = readme;
            Node = node;
            _workCancellation = workCancellation;
        }

        public Task<PreparedRepoCodeNavigation> Preparation { get; }

        public Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?> Readme { get; }

        public LinkedListNode<string> Node { get; }

        public bool HasForegroundConsumer => Volatile.Read(ref _hasForegroundConsumer) != 0;

        public void ClaimForeground() => Interlocked.Exchange(ref _hasForegroundConsumer, 1);

        public void Cancel()
        {
            try
            {
                _workCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _workCancellation.Dispose();
    }

    internal sealed record PreparedRepoCodeNavigation(
        RepoCodeLoadResult<Models.CodeViewer.RepoTree> Result,
        RepoFileTreeViewModel.PreparedTree PreparedTree,
        RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>? Readme,
        bool RootListingUnavailable);

    private sealed record PreparationWork(
        Task<PreparedRepoCodeNavigation> Preparation,
        Task<RepoCodeLoadResult<Models.CodeViewer.RepoReadmeFile>?> Readme);
}
