using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoFileTreeViewModel : ObservableObject
{
    private readonly IRepoTreeService _treeService;
    private readonly ILanguageIdResolver _languageResolver;

    // Owner/repo/ref stored for the truncated-tree directory load fallback.
    private string _owner = string.Empty;
    private string _repo = string.Empty;
    private string _ref = string.Empty;
    private long _contextGeneration;
    private CancellationScope _directoryScope = new();
    private ConcurrentDictionary<string, RepoTreeNodeViewModel> _leafIndex = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _directoryRequestGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _directorySourceVersions = new(StringComparer.Ordinal);
    private readonly object _reconciliationTaskGate = new();
    private long _sourceRequestGeneration;
    private long _latestCommittedSourceGeneration;
    private Task _rootReconciliationTask = Task.CompletedTask;
    private Task _ownedReconciliationTask = Task.CompletedTask;

    public ObservableCollection<RepoTreeNodeViewModel> RootNodes { get; } = [];

    [ObservableProperty]
    public partial RepoTreeNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    public partial bool IsTruncated { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    public bool IsRootAuthoritative { get; private set; }

    /// <summary>
    /// Filtered view of RootNodes by FilterText (case-insensitive substring match on Path).
    /// Updated asynchronously (debounced + off-thread) whenever FilterText changes.
    /// </summary>
    public IEnumerable<RepoTreeNodeViewModel> FilteredRootNodes => _filteredRootNodes;

    private IEnumerable<RepoTreeNodeViewModel> _filteredRootNodes = [];

    // Each new keystroke creates a fresh CTS; the previous in-flight filter is cancelled.
    private CancellationTokenSource _filterCts = new();

    // Callback wired by page VM so SelectNodeCommand routes to it.
    public Func<RepoTreeNodeViewModel, CancellationToken, Task>? OnSelectNode { get; set; }

    // Predictive file-content warm-up. The page owns the cache and transport;
    // the tree only forwards likely navigation intent.
    public Func<RepoTreeNodeViewModel, CancellationToken, Task>? OnPrefetchNode { get; set; }

    // Selection takes priority over prediction. The page owns the scheduler,
    // so the tree forwards cancellation through the same typed boundary.
    public Action? OnCancelPrefetch { get; set; }

    // Notifies the page after contents API data authoritatively changes a folder.
    public Action? OnAuthoritativeTreeChanged { get; set; }

    public RepoFileTreeViewModel(IRepoTreeService treeService, ILanguageIdResolver languageResolver)
    {
        _treeService = treeService;
        _languageResolver = languageResolver;
    }

    partial void OnFilterTextChanged(string value)
    {
        _ = RebuildFilterObservedAsync(value);
    }

    private async Task RebuildFilterObservedAsync(string filterText)
    {
        try
        {
            await RebuildFilterAsync(filterText);
        }
        catch (Exception exception)
        {
            _ = UserFacingError.For(
                exception,
                UserFacingErrorKind.Action,
                "repository-file-filter");
            ErrorMessage = LocalizedResourceText.GetString(
                "RepoCode/Error/FilterFailedSafe",
                "Could not filter repository files.");
        }
    }

    private async Task RebuildFilterAsync(string filterText)
    {
        // Cancel any previous in-flight search and start a new one.
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _filterCts, cts);
        old.Cancel();
        old.Dispose();

        try
        {
            // Debounce: wait for typing to pause before doing any work.
            await Task.Delay(150, cts.Token);

            string filter = filterText?.Trim() ?? string.Empty;

            IEnumerable<RepoTreeNodeViewModel> result;
            if (string.IsNullOrEmpty(filter))
            {
                result = RootNodes;
            }
            else
            {
                var flat = await Task.Run(
                    () => _leafIndex.Values
                        .Where(node => node.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(static node => node.Path, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    cts.Token);

                cts.Token.ThrowIfCancellationRequested();
                result = flat;
            }

            // Continuations after 'await' resume on the UI SynchronizationContext,
            // so this PropertyChanged notification is safe to fire directly.
            _filteredRootNodes = result;
            OnPropertyChanged(nameof(FilteredRootNodes));
        }
        catch (OperationCanceledException)
        {
            // A newer filter superseded this one — nothing to do.
        }
    }

    [RelayCommand]
    private async Task ToggleExpandAsync(RepoTreeNodeViewModel? node, CancellationToken ct)
    {
        if (node is null) return;

        if (!node.IsExpanded)
        {
            // Expand: load children if needed (truncated fallback).
            if (!node.ChildrenLoaded && node.IsDirectory)
            {
                await LoadDirectoryAsync(node, ct);
            }
            node.IsExpanded = true;
        }
        else
        {
            node.IsExpanded = false;
        }
    }

    [RelayCommand]
    private async Task SelectNodeAsync(RepoTreeNodeViewModel? node, CancellationToken ct)
    {
        if (node is null) return;
        SelectedNode = node;
        ct.ThrowIfCancellationRequested();
        if (OnSelectNode is not null)
        {
            await OnSelectNode(node, ct).ConfigureAwait(false);
        }
    }

    internal Task<PreparedTree> PrepareLoadAsync(RepoTree tree, CancellationToken ct) =>
        Task.Run(() => PrepareTree(tree, _languageResolver, ct), ct);

    internal long BeginSourceRequest() =>
        Interlocked.Increment(ref _sourceRequestGeneration);

    internal Task<bool> LoadIncrementallyAsync(
        PreparedTree prepared,
        string owner,
        string repo,
        string @ref,
        long sourceGeneration,
        bool sourceIsAuthoritative,
        CancellationToken ct) =>
        LoadCoreAsync(
            prepared,
            owner,
            repo,
            @ref,
            sourceGeneration,
            sourceIsAuthoritative,
            ct);

    private async Task<bool> LoadCoreAsync(
        PreparedTree prepared,
        string owner,
        string repo,
        string @ref,
        long sourceGeneration,
        bool sourceIsAuthoritative,
        CancellationToken ct)
    {
        RepoTree tree = prepared.Tree;
        bool contextChanged =
            !string.Equals(_owner, owner, StringComparison.Ordinal) ||
            !string.Equals(_repo, repo, StringComparison.Ordinal) ||
            !string.Equals(_ref, @ref, StringComparison.Ordinal);
        if (sourceGeneration < Volatile.Read(ref _latestCommittedSourceGeneration))
        {
            return false;
        }

        if (!contextChanged && !sourceIsAuthoritative)
        {
            MergeNonAuthoritativeTree(RootNodes, tree.Root.Children, parent: null);
            IsTruncated |= tree.Truncated;
            NotifyTreeChanged();
            return true;
        }

        long generation = Interlocked.Increment(ref _contextGeneration);
        CancellationScope nextDirectoryScope = new();
        CancellationScope previousDirectoryScope = Interlocked.Exchange(ref _directoryScope, nextDirectoryScope);
        previousDirectoryScope.Retire();
        _directoryRequestGenerations.Clear();
        _owner = owner;
        _repo = repo;
        _ref = @ref;

        if (!contextChanged && !tree.Truncated &&
            _directorySourceVersions.TryGetValue(string.Empty, out long currentRootSource) &&
            sourceGeneration < currentRootSource)
        {
            return false;
        }

        string? selectedPath = SelectedNode?.Path;
        if (contextChanged)
        {
            _leafIndex = new ConcurrentDictionary<string, RepoTreeNodeViewModel>(StringComparer.Ordinal);
            _directorySourceVersions.Clear();
        }

        if (contextChanged && !tree.Truncated)
        {
            RootNodes.Clear();
            foreach (RepoTreeNodeViewModel root in prepared.RootNodes)
            {
                RootNodes.Add(root);
            }

            _leafIndex = prepared.LeafNodesByPath;
        }
        else
        {
            UiWorkBudget budget = new();
            await ApplyKeyedNodesIncrementallyAsync(
                RootNodes,
                tree.Root.Children,
                parent: null,
                tree.Truncated ? TreeApplyMode.PartialRecursive : TreeApplyMode.CompleteRecursive,
                replaceTarget: contextChanged || !tree.Truncated,
                reuseExisting: !contextChanged,
                prepared.NodesByPath,
                budget,
                ct);
        }
        SelectedNode = string.IsNullOrEmpty(selectedPath)
            ? null
            : FindByPath(RootNodes, selectedPath);

        IsTruncated = tree.Truncated;
        IsRootAuthoritative = !tree.Truncated;
        RememberCommittedSourceGeneration(sourceGeneration);
        if (!tree.Truncated)
        {
            RememberCompleteTreeVersion(prepared, sourceGeneration);
        }
        NotifyTreeChanged();

        if (tree.Truncated)
        {
            ct.ThrowIfCancellationRequested();
            CancellationTokenSource rootRequest = nextDirectoryScope.CreateLinkedSource(ct);
            _rootReconciliationTask = ReconcileRootObservedAsync(
                generation,
                owner,
                repo,
                @ref,
                rootRequest);
            OwnReconciliationTask(_rootReconciliationTask);
        }
        else
        {
            _rootReconciliationTask = Task.CompletedTask;
        }

        return true;
    }

    internal Task RootReconciliationTask => _rootReconciliationTask;

    internal Task PendingReconciliationTask
    {
        get
        {
            lock (_reconciliationTaskGate)
            {
                return _ownedReconciliationTask;
            }
        }
    }

    internal Task PrefetchNodeAsync(RepoTreeNodeViewModel node, CancellationToken ct) =>
        OnPrefetchNode?.Invoke(node, ct) ?? Task.CompletedTask;

    internal void CancelPrefetch() => OnCancelPrefetch?.Invoke();

    public void CancelPendingRequests()
    {
        Interlocked.Increment(ref _contextGeneration);
        CancellationScope next = new();
        CancellationScope previous = Interlocked.Exchange(ref _directoryScope, next);
        previous.Retire();
        _directoryRequestGenerations.Clear();
    }

    internal CancellationTokenSource CreateContextLinkedSource(CancellationToken externalToken) =>
        Volatile.Read(ref _directoryScope).CreateLinkedSource(externalToken);

    public RepoTreeNodeViewModel? FindNodeByPath(string path) =>
        FindByPath(RootNodes, path);

    public async Task<RepoTreeNodeViewModel?> EnsurePathAvailableAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path)) return null;

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string currentPath = string.Empty;
        RepoTreeNodeViewModel? current = null;
        for (int index = 0; index < segments.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            currentPath = currentPath.Length == 0
                ? segments[index]
                : $"{currentPath}/{segments[index]}";

            RepoTreeNodeViewModel? next = FindNodeByPath(currentPath);
            if (next is null && current is { IsDirectory: true, ChildrenLoaded: false })
            {
                await LoadDirectoryAsync(current, ct);
                next = FindNodeByPath(currentPath);
            }

            if (next is null) return null;
            current = next;
            if (current.IsDirectory && index < segments.Length - 1)
            {
                if (!current.ChildrenLoaded)
                {
                    await LoadDirectoryAsync(current, ct);
                }

                current.IsExpanded = true;
            }
        }

        return current;
    }

    /// <summary>Truncated-tree fallback: load children of a directory node via the REST API.</summary>
    public async Task LoadDirectoryAsync(RepoTreeNodeViewModel parent, CancellationToken ct)
    {
        if (parent.ChildrenLoaded) return;

        long generation = Volatile.Read(ref _contextGeneration);
        string owner = _owner;
        string repository = _repo;
        string gitRef = _ref;
        long requestGeneration = _directoryRequestGenerations.AddOrUpdate(
            parent.Path,
            static _ => 1,
            static (_, current) => current + 1);
        long sourceGeneration = BeginSourceRequest();
        CancellationScope scope = Volatile.Read(ref _directoryScope);
        CancellationTokenSource requestCts = scope.CreateLinkedSource(ct);
        CancellationToken requestToken = requestCts.Token;
        bool refreshOwnsRequest = false;
        parent.IsLoadingChildren = true;
        try
        {
            RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> result =
                await _treeService.LoadDirectoryAsync(owner, repository, parent.Path, gitRef, requestToken);
            if (!IsCurrentDirectoryRequest(
                parent,
                generation,
                requestGeneration,
                owner,
                repository,
                gitRef)) return;
            bool immutableRef = GitReferencePolicy.IsImmutableObjectId(gitRef);
            ApplyDirectoryResult(parent, result, sourceGeneration, immutableRef);

            if (!immutableRef)
            {
                refreshOwnsRequest = true;
                long refreshSourceGeneration = BeginSourceRequest();
                Task refreshTask = RefreshDirectoryObservedAsync(
                    parent,
                    generation,
                    requestGeneration,
                    owner,
                    repository,
                    gitRef,
                    refreshSourceGeneration,
                    requestCts);
                OwnReconciliationTask(refreshTask);
            }
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
        }
        catch (Exception exception)
        {
            if (IsCurrentDirectoryRequest(
                parent,
                generation,
                requestGeneration,
                owner,
                repository,
                gitRef))
            {
                _ = UserFacingError.For(
                    exception,
                    UserFacingErrorKind.Refresh,
                    "repository-folder");
                ErrorMessage = LocalizedResourceText.GetString(
                    "RepoCode/Error/FolderRefreshFailedSafe",
                    "Could not refresh this folder.");
            }
        }
        finally
        {
            if (IsCurrentDirectoryRequest(
                parent,
                generation,
                requestGeneration,
                owner,
                repository,
                gitRef))
            {
                parent.IsLoadingChildren = false;
            }

            if (!refreshOwnsRequest)
            {
                requestCts.Dispose();
            }
        }
    }

    private async Task RefreshDirectoryObservedAsync(
        RepoTreeNodeViewModel parent,
        long generation,
        long requestGeneration,
        string owner,
        string repository,
        string gitRef,
        long sourceGeneration,
        CancellationTokenSource requestCts)
    {
        CancellationToken ct = requestCts.Token;
        using (requestCts)
        {
            try
            {
                RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> refreshed =
                    await _treeService.LoadDirectoryAsync(
                        owner,
                        repository,
                        parent.Path,
                        gitRef,
                        ct,
                        global::JitHub.Services.QueryFetchPolicy.NetworkOnly);
                if (!IsCurrentDirectoryRequest(
                    parent,
                    generation,
                    requestGeneration,
                    owner,
                    repository,
                    gitRef)) return;
                ApplyDirectoryResult(parent, refreshed, sourceGeneration, sourceIsAuthoritative: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The repository/ref changed or the page was closed.
            }
            catch (Exception exception)
            {
                if (IsCurrentDirectoryRequest(
                    parent,
                    generation,
                    requestGeneration,
                    owner,
                    repository,
                    gitRef))
                {
                    _ = UserFacingError.For(
                        exception,
                        UserFacingErrorKind.Refresh,
                        "repository-folder-cached");
                    ErrorMessage = LocalizedResourceText.GetString(
                        "RepoCode/Error/CachedFolderRefreshFailedSafe",
                        "Showing cached folder contents. Refresh failed.");
                }
            }
        }
    }

    private async Task ReconcileRootObservedAsync(
        long generation,
        string owner,
        string repository,
        string gitRef,
        CancellationTokenSource requestCts)
    {
        CancellationToken ct = requestCts.Token;
        using (requestCts)
        {
            await Task.Yield();
            try
            {
                long sourceGeneration = BeginSourceRequest();
                RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> result =
                    await _treeService.LoadDirectoryAsync(owner, repository, string.Empty, gitRef, ct);
                if (!IsCurrentContext(generation, owner, repository, gitRef)) return;
                bool immutableRef = GitReferencePolicy.IsImmutableObjectId(gitRef);
                ApplyRootDirectoryResult(result, sourceGeneration, immutableRef);

                if (!immutableRef)
                {
                    long refreshSourceGeneration = BeginSourceRequest();
                    RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> refreshed =
                        await _treeService.LoadDirectoryAsync(
                            owner,
                            repository,
                            string.Empty,
                            gitRef,
                            ct,
                            global::JitHub.Services.QueryFetchPolicy.NetworkOnly);
                    if (!IsCurrentContext(generation, owner, repository, gitRef)) return;
                    ApplyRootDirectoryResult(refreshed, refreshSourceGeneration, sourceIsAuthoritative: true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The repository/ref changed or the page was closed.
            }
            catch (Exception exception)
            {
                if (IsCurrentContext(generation, owner, repository, gitRef))
                {
                    _ = UserFacingError.For(
                        exception,
                        UserFacingErrorKind.Refresh,
                        "repository-tree-partial");
                    ErrorMessage = LocalizedResourceText.GetString(
                        "RepoCode/Error/PartialTreeRefreshFailedSafe",
                        "Showing the partial repository tree. Folder refresh failed.");
                }
            }
        }
    }

    private void ApplyDirectoryResult(
        RepoTreeNodeViewModel parent,
        RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> result,
        long sourceGeneration,
        bool sourceIsAuthoritative)
    {
        bool authoritative = CanReplaceDirectory(parent.Path, result, sourceGeneration, sourceIsAuthoritative);
        if (authoritative)
        {
            ApplyKeyedNodes(
                parent.Children,
                result.Value,
                parent,
                TreeApplyMode.DirectoryListing,
                replaceTarget: true,
                reuseExisting: true,
                preparedNodes: null);
            parent.ChildrenLoaded = true;
            RememberDirectoryVersion(parent.Path, sourceGeneration);
            RememberCommittedSourceGeneration(sourceGeneration);
        }
        else
        {
            MergeCachedNodes(parent.Children, result.Value, parent);
        }

        ErrorMessage = result.RefreshError;
        NotifyTreeChanged();
        if (authoritative)
        {
            OnAuthoritativeTreeChanged?.Invoke();
        }
    }

    private void ApplyRootDirectoryResult(
        RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> result,
        long sourceGeneration,
        bool sourceIsAuthoritative)
    {
        bool authoritative = CanReplaceDirectory(string.Empty, result, sourceGeneration, sourceIsAuthoritative);
        if (authoritative)
        {
            ApplyKeyedNodes(
                RootNodes,
                result.Value,
                parent: null,
                TreeApplyMode.DirectoryListing,
                replaceTarget: true,
                reuseExisting: true,
                preparedNodes: null);
            IsRootAuthoritative = true;
            RememberDirectoryVersion(string.Empty, sourceGeneration);
            RememberCommittedSourceGeneration(sourceGeneration);
        }
        else
        {
            MergeCachedNodes(RootNodes, result.Value, parent: null);
        }

        ErrorMessage = result.RefreshError;
        NotifyTreeChanged();
        if (authoritative)
        {
            OnAuthoritativeTreeChanged?.Invoke();
        }
    }

    private void MergeCachedNodes(
        ObservableCollection<RepoTreeNodeViewModel> target,
        IEnumerable<RepoTreeNode> models,
        RepoTreeNodeViewModel? parent)
    {
        Dictionary<string, RepoTreeNodeViewModel> existing = target
            .GroupBy(static node => node.Path, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        foreach (RepoTreeNode model in models)
        {
            if (existing.ContainsKey(model.Path))
            {
                continue;
            }

            RepoTreeNodeViewModel item = BuildNodeVm(
                model,
                parent,
                descendantsAreAuthoritative: false);
            target.Add(item);
            existing[item.Path] = item;
            IndexSubtree(item);
        }
    }

    private void MergeNonAuthoritativeTree(
        ObservableCollection<RepoTreeNodeViewModel> target,
        IEnumerable<RepoTreeNode> models,
        RepoTreeNodeViewModel? parent)
    {
        Dictionary<string, RepoTreeNodeViewModel> existing = target
            .GroupBy(static node => node.Path, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        foreach (RepoTreeNode model in models)
        {
            if (existing.TryGetValue(model.Path, out RepoTreeNodeViewModel? current))
            {
                if (current.IsDirectory && model.IsDirectory)
                {
                    MergeNonAuthoritativeTree(current.Children, model.Children, current);
                }

                continue;
            }

            RepoTreeNodeViewModel item = BuildNodeVm(model, parent, descendantsAreAuthoritative: false);
            target.Add(item);
            existing[item.Path] = item;
            IndexSubtree(item);
        }
    }

    internal static PreparedTree PrepareTree(
        RepoTree tree,
        ILanguageIdResolver languageResolver,
        CancellationToken ct)
    {
        Dictionary<string, RepoTreeNodeViewModel> nodesByPath = new(StringComparer.Ordinal);
        ConcurrentDictionary<string, RepoTreeNodeViewModel> leafNodesByPath = new(StringComparer.Ordinal);
        List<RepoTreeNodeViewModel> roots = [];
        bool descendantsAreAuthoritative = !tree.Truncated;
        foreach (RepoTreeNode child in tree.Root.Children)
        {
            ct.ThrowIfCancellationRequested();
            roots.Add(BuildPreparedNodeVm(
                child,
                parent: null,
                descendantsAreAuthoritative,
                languageResolver,
                nodesByPath,
                leafNodesByPath,
                ct));
        }

        return new PreparedTree(tree, roots, nodesByPath, leafNodesByPath);
    }

    private static RepoTreeNodeViewModel BuildPreparedNodeVm(
        RepoTreeNode model,
        RepoTreeNodeViewModel? parent,
        bool descendantsAreAuthoritative,
        ILanguageIdResolver languageResolver,
        IDictionary<string, RepoTreeNodeViewModel> nodesByPath,
        ConcurrentDictionary<string, RepoTreeNodeViewModel> leafNodesByPath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RepoTreeNodeViewModel vm = new(model, languageResolver, parent);
        nodesByPath[model.Path] = vm;
        if (!model.IsDirectory)
        {
            leafNodesByPath[model.Path] = vm;
        }

        foreach (RepoTreeNode child in model.Children)
        {
            vm.Children.Add(BuildPreparedNodeVm(
                child,
                vm,
                descendantsAreAuthoritative,
                languageResolver,
                nodesByPath,
                leafNodesByPath,
                ct));
        }

        vm.ChildrenLoaded = !model.IsDirectory || descendantsAreAuthoritative;
        return vm;
    }

    private RepoTreeNodeViewModel BuildNodeVm(
        RepoTreeNode model,
        RepoTreeNodeViewModel? parent,
        bool descendantsAreAuthoritative,
        IDictionary<string, RepoTreeNodeViewModel>? nodesByPath = null,
        ConcurrentDictionary<string, RepoTreeNodeViewModel>? leafNodesByPath = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RepoTreeNodeViewModel vm = new(model, _languageResolver, parent);
        nodesByPath?[model.Path] = vm;
        if (!model.IsDirectory)
        {
            leafNodesByPath?[model.Path] = vm;
        }
        foreach (RepoTreeNode child in model.Children)
        {
            vm.Children.Add(BuildNodeVm(
                child,
                vm,
                descendantsAreAuthoritative,
                nodesByPath,
                leafNodesByPath,
                ct));
        }

        vm.ChildrenLoaded = !model.IsDirectory || descendantsAreAuthoritative;
        return vm;
    }

    private void ApplyKeyedNodes(
        ObservableCollection<RepoTreeNodeViewModel> target,
        IEnumerable<RepoTreeNode> models,
        RepoTreeNodeViewModel? parent,
        TreeApplyMode mode,
        bool replaceTarget,
        bool reuseExisting,
        IReadOnlyDictionary<string, RepoTreeNodeViewModel>? preparedNodes)
    {
        Dictionary<string, RepoTreeNodeViewModel> existing = target
            .GroupBy(static node => node.Path, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        List<RepoTreeNodeViewModel> desired = [];
        foreach (RepoTreeNode model in models)
        {
            RepoTreeNodeViewModel item;
            if (reuseExisting &&
                existing.TryGetValue(model.Path, out RepoTreeNodeViewModel? current) &&
                current.IsDirectory == model.IsDirectory)
            {
                item = current;
                string previousSha = item.Sha;
                item.UpdateMetadata(model);

                if (!item.IsDirectory)
                {
                    _leafIndex[item.Path] = item;
                }
                else if (mode == TreeApplyMode.CompleteRecursive)
                {
                    ApplyKeyedNodes(
                        item.Children,
                        model.Children,
                        item,
                        TreeApplyMode.CompleteRecursive,
                        replaceTarget: true,
                        reuseExisting: true,
                        preparedNodes);

                    item.ChildrenLoaded = true;
                }
                else if (mode == TreeApplyMode.PartialRecursive)
                {
                    if (!string.Equals(previousSha, item.Sha, StringComparison.Ordinal))
                    {
                        item.ChildrenLoaded = false;
                    }

                    if (model.Children.Count > 0)
                    {
                        ApplyKeyedNodes(
                            item.Children,
                            model.Children,
                            item,
                            TreeApplyMode.PartialRecursive,
                            replaceTarget: false,
                            reuseExisting: true,
                            preparedNodes);
                    }
                }
                else if (!string.Equals(previousSha, item.Sha, StringComparison.Ordinal))
                {
                    // A parent listing knows this directory exists, but not whether its
                    // previously loaded descendants are still current.
                    item.ChildrenLoaded = false;
                }
            }
            else
            {
                RepoTreeNodeViewModel? prepared = null;
                bool canUsePrepared = preparedNodes is not null &&
                    preparedNodes.TryGetValue(model.Path, out prepared) &&
                    ReferenceEquals(prepared.Parent, parent);
                item = canUsePrepared
                    ? prepared!
                    : BuildNodeVm(
                        model,
                        parent,
                        mode == TreeApplyMode.CompleteRecursive);
                IndexSubtree(item);
            }

            desired.Add(item);
        }

        if (replaceTarget)
        {
            HashSet<RepoTreeNodeViewModel> desiredSet = new(desired);
            for (int index = target.Count - 1; index >= 0; index--)
            {
                if (!desiredSet.Contains(target[index]))
                {
                    RemoveIndexedSubtree(target[index]);
                    target.RemoveAt(index);
                }
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            RepoTreeNodeViewModel item = desired[index];
            int currentIndex = target.IndexOf(item);
            if (currentIndex < 0)
            {
                target.Insert(Math.Min(index, target.Count), item);
            }
            else if (currentIndex != index)
            {
                target.Move(currentIndex, index);
            }
        }
    }

    private async Task ApplyKeyedNodesIncrementallyAsync(
        ObservableCollection<RepoTreeNodeViewModel> target,
        IEnumerable<RepoTreeNode> models,
        RepoTreeNodeViewModel? parent,
        TreeApplyMode mode,
        bool replaceTarget,
        bool reuseExisting,
        IReadOnlyDictionary<string, RepoTreeNodeViewModel>? preparedNodes,
        UiWorkBudget budget,
        CancellationToken ct)
    {
        Dictionary<string, RepoTreeNodeViewModel> existing = target
            .GroupBy(static node => node.Path, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        List<RepoTreeNodeViewModel> desired = [];
        foreach (RepoTreeNode model in models)
        {
            ct.ThrowIfCancellationRequested();
            RepoTreeNodeViewModel item;
            if (reuseExisting &&
                existing.TryGetValue(model.Path, out RepoTreeNodeViewModel? current) &&
                current.IsDirectory == model.IsDirectory)
            {
                item = current;
                string previousSha = item.Sha;
                item.UpdateMetadata(model);

                if (!item.IsDirectory)
                {
                    _leafIndex[item.Path] = item;
                }
                else if (mode == TreeApplyMode.CompleteRecursive)
                {
                    await ApplyKeyedNodesIncrementallyAsync(
                        item.Children,
                        model.Children,
                        item,
                        TreeApplyMode.CompleteRecursive,
                        replaceTarget: true,
                        reuseExisting: true,
                        preparedNodes,
                        budget,
                        ct);
                    item.ChildrenLoaded = true;
                }
                else if (mode == TreeApplyMode.PartialRecursive)
                {
                    if (!string.Equals(previousSha, item.Sha, StringComparison.Ordinal))
                    {
                        item.ChildrenLoaded = false;
                    }

                    if (model.Children.Count > 0)
                    {
                        await ApplyKeyedNodesIncrementallyAsync(
                            item.Children,
                            model.Children,
                            item,
                            TreeApplyMode.PartialRecursive,
                            replaceTarget: false,
                            reuseExisting: true,
                            preparedNodes,
                            budget,
                            ct);
                    }
                }
                else if (!string.Equals(previousSha, item.Sha, StringComparison.Ordinal))
                {
                    item.ChildrenLoaded = false;
                }
            }
            else
            {
                RepoTreeNodeViewModel? prepared = null;
                bool canUsePrepared = preparedNodes is not null &&
                    preparedNodes.TryGetValue(model.Path, out prepared) &&
                    ReferenceEquals(prepared.Parent, parent);
                item = canUsePrepared
                    ? prepared!
                    : BuildNodeVm(model, parent, mode == TreeApplyMode.CompleteRecursive);
                IndexSubtree(item);
            }

            desired.Add(item);
            await YieldIfNeededAsync(budget, ct);
        }

        if (replaceTarget)
        {
            HashSet<RepoTreeNodeViewModel> desiredSet = new(desired);
            for (int index = target.Count - 1; index >= 0; index--)
            {
                ct.ThrowIfCancellationRequested();
                if (!desiredSet.Contains(target[index]))
                {
                    RemoveIndexedSubtree(target[index]);
                    target.RemoveAt(index);
                }

                await YieldIfNeededAsync(budget, ct);
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            RepoTreeNodeViewModel item = desired[index];
            int currentIndex = target.IndexOf(item);
            if (currentIndex < 0)
            {
                target.Insert(Math.Min(index, target.Count), item);
            }
            else if (currentIndex != index)
            {
                target.Move(currentIndex, index);
            }

            await YieldIfNeededAsync(budget, ct);
        }
    }

    private static async Task YieldIfNeededAsync(
        UiWorkBudget budget,
        CancellationToken ct)
    {
        if (!budget.ShouldYield()) return;
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        budget.Restart();
    }

    private void IndexSubtree(RepoTreeNodeViewModel node)
    {
        if (!node.IsDirectory)
        {
            _leafIndex[node.Path] = node;
            return;
        }

        foreach (RepoTreeNodeViewModel child in node.Children)
        {
            IndexSubtree(child);
        }
    }

    private void RemoveIndexedSubtree(RepoTreeNodeViewModel node)
    {
        if (!node.IsDirectory)
        {
            _leafIndex.TryRemove(node.Path, out _);
            return;
        }

        foreach (RepoTreeNodeViewModel child in node.Children)
        {
            RemoveIndexedSubtree(child);
        }
    }

    private void NotifyTreeChanged()
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            _filteredRootNodes = RootNodes;
            OnPropertyChanged(nameof(FilteredRootNodes));
        }
        else
        {
            _ = RebuildFilterObservedAsync(FilterText);
        }
    }

    private bool IsCurrentContext(long generation, string owner, string repository, string gitRef) =>
        generation == Volatile.Read(ref _contextGeneration) &&
        string.Equals(owner, _owner, StringComparison.Ordinal) &&
        string.Equals(repository, _repo, StringComparison.Ordinal) &&
        string.Equals(gitRef, _ref, StringComparison.Ordinal);

    private bool IsCurrentDirectoryRequest(
        RepoTreeNodeViewModel parent,
        long contextGeneration,
        long requestGeneration,
        string owner,
        string repository,
        string gitRef) =>
        IsCurrentContext(contextGeneration, owner, repository, gitRef) &&
        _directoryRequestGenerations.TryGetValue(parent.Path, out long currentRequest) &&
        currentRequest == requestGeneration &&
        ReferenceEquals(FindNodeByPath(parent.Path), parent);

    private bool CanReplaceDirectory(
        string path,
        RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> result,
        long sourceGeneration,
        bool sourceIsAuthoritative)
    {
        if (!sourceIsAuthoritative || result.CacheState != CacheState.Fresh) return false;

        return !_directorySourceVersions.TryGetValue(path, out long visibleSource) ||
            sourceGeneration >= visibleSource;
    }

    private void RememberDirectoryVersion(string path, long sourceGeneration)
    {
        if (!_directorySourceVersions.TryGetValue(path, out long current) || sourceGeneration >= current)
        {
            _directorySourceVersions[path] = sourceGeneration;
            RememberCommittedSourceGeneration(sourceGeneration);
        }
    }

    private void RememberCommittedSourceGeneration(long sourceGeneration)
    {
        long current = Volatile.Read(ref _latestCommittedSourceGeneration);
        while (sourceGeneration > current)
        {
            long observed = Interlocked.CompareExchange(
                ref _latestCommittedSourceGeneration,
                sourceGeneration,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private void RememberCompleteTreeVersion(PreparedTree prepared, long sourceGeneration)
    {
        RememberDirectoryVersion(string.Empty, sourceGeneration);
        foreach ((string path, RepoTreeNodeViewModel node) in prepared.NodesByPath)
        {
            if (node.IsDirectory)
            {
                RememberDirectoryVersion(path, sourceGeneration);
            }
        }
    }

    private void OwnReconciliationTask(Task task)
    {
        lock (_reconciliationTaskGate)
        {
            _ownedReconciliationTask = ObserveReconciliationTasksAsync(_ownedReconciliationTask, task);
        }
    }

    private static async Task ObserveReconciliationTasksAsync(Task previous, Task current)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // The originating operation publishes any current-generation error itself.
        }

        try
        {
            await current.ConfigureAwait(false);
        }
        catch
        {
            // Keep the aggregate task non-faulting so later reconciliations remain observable.
        }
    }

    private static RepoTreeNodeViewModel? FindByPath(
        IEnumerable<RepoTreeNodeViewModel> nodes,
        string path)
    {
        foreach (RepoTreeNodeViewModel node in nodes)
        {
            if (string.Equals(node.Path, path, StringComparison.Ordinal))
            {
                return node;
            }

            RepoTreeNodeViewModel? child = FindByPath(node.Children, path);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    internal sealed record PreparedTree(
        RepoTree Tree,
        IReadOnlyList<RepoTreeNodeViewModel> RootNodes,
        IReadOnlyDictionary<string, RepoTreeNodeViewModel> NodesByPath,
        ConcurrentDictionary<string, RepoTreeNodeViewModel> LeafNodesByPath);

    private enum TreeApplyMode
    {
        CompleteRecursive,
        PartialRecursive,
        DirectoryListing
    }

    private sealed class CancellationScope
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source = new();

        public CancellationTokenSource CreateLinkedSource(CancellationToken externalToken)
        {
            lock (_gate)
            {
                if (_source is not null)
                {
                    return CancellationTokenSource.CreateLinkedTokenSource(externalToken, _source.Token);
                }

                CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
                cancelled.Cancel();
                return cancelled;
            }
        }

        public void Retire()
        {
            lock (_gate)
            {
                CancellationTokenSource? source = _source;
                _source = null;
                if (source is null)
                {
                    return;
                }

                source.Cancel();
                source.Dispose();
            }
        }
    }
}
