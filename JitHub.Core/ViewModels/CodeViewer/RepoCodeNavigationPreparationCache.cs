using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.CodeViewer;

namespace JitHub.WinUI.ViewModels.CodeViewer;

/// <summary>
/// Warms the non-visual repository tree projection before navigation. Prepared
/// view models are transferred to one destination page and are never shared.
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
            entry = new Entry(
                PrepareAsync(owner, name, gitRef, workCancellation.Token),
                node,
                workCancellation);
            _entries[key] = entry;
            TrimToBudget(evicted);
        }

        foreach (Entry retired in evicted)
        {
            Retire(retired, cancel: true);
        }

        return (key, entry);
    }

    private async Task<PreparedRepoCodeNavigation> PrepareAsync(
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken)
    {
        RepoCodeLoadResult<Models.CodeViewer.RepoTree> result =
            await _treeService.LoadTreeAsync(
                owner,
                name,
                gitRef,
                cancellationToken).ConfigureAwait(false);
        RepoFileTreeViewModel.PreparedTree prepared = await Task.Run(
            () => RepoFileTreeViewModel.PrepareTree(
                result.Value,
                _languageResolver,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new PreparedRepoCodeNavigation(result, prepared);
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
            LinkedListNode<string> node,
            CancellationTokenSource workCancellation)
        {
            Preparation = preparation;
            Node = node;
            _workCancellation = workCancellation;
        }

        public Task<PreparedRepoCodeNavigation> Preparation { get; }

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
        RepoFileTreeViewModel.PreparedTree PreparedTree);
}
