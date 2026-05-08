using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.CodeViewer;
using JitHub.Services.CodeViewer;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoFileTreeViewModel : ObservableObject
{
    private readonly IRepoTreeService _treeService;
    private readonly ILanguageIdResolver _languageResolver;

    // Owner/repo/ref stored for the truncated-tree directory load fallback.
    private string _owner = string.Empty;
    private string _repo = string.Empty;
    private string _ref = string.Empty;

    public ObservableCollection<RepoTreeNodeViewModel> RootNodes { get; } = [];

    [ObservableProperty]
    public partial RepoTreeNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    public partial bool IsTruncated { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

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

    public RepoFileTreeViewModel(IRepoTreeService treeService, ILanguageIdResolver languageResolver)
    {
        _treeService = treeService;
        _languageResolver = languageResolver;
    }

    partial void OnFilterTextChanged(string value)
    {
        _ = RebuildFilterAsync(value);
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
                // Snapshot the node list on the UI thread before going off-thread.
                var snapshot = RootNodes.ToList();
                var flat = await Task.Run(
                    () => FlattenLeaves(snapshot, filter).ToList(),
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
        if (OnSelectNode is not null)
        {
            await OnSelectNode(node, ct);
        }
    }

    /// <summary>Converts a RepoTree into VM nodes (full recursive build up-front).</summary>
    public void Load(RepoTree tree, string owner, string repo, string @ref)
    {
        _owner = owner;
        _repo = repo;
        _ref = @ref;

        RootNodes.Clear();
        foreach (RepoTreeNode child in tree.Root.Children)
        {
            RootNodes.Add(BuildNodeVm(child, parent: null));
        }

        IsTruncated = tree.Truncated;

        // Trigger a filter rebuild with current FilterText (typically empty on first load).
        _ = RebuildFilterAsync(FilterText);
    }

    /// <summary>Truncated-tree fallback: load children of a directory node via the REST API.</summary>
    public async Task LoadDirectoryAsync(RepoTreeNodeViewModel parent, CancellationToken ct)
    {
        if (parent.ChildrenLoaded) return;

        parent.IsLoadingChildren = true;
        try
        {
            IReadOnlyList<JitHub.Models.RepoContentNode> nodes =
                await _treeService.LoadDirectoryAsync(_owner, _repo, parent.Path, _ref, ct);

            parent.Children.Clear();
            foreach (JitHub.Models.RepoContentNode n in nodes)
            {
                var model = new RepoTreeNode
                {
                    Name = n.Name ?? string.Empty,
                    Path = n.Path ?? string.Empty,
                    Sha = n.Sha,
                    IsDirectory = n.IsDir,
                };
                parent.Children.Add(new RepoTreeNodeViewModel(model, _languageResolver, parent));
            }
            parent.ChildrenLoaded = true;
        }
        finally
        {
            parent.IsLoadingChildren = false;
        }
    }

    private RepoTreeNodeViewModel BuildNodeVm(RepoTreeNode model, RepoTreeNodeViewModel? parent)
    {
        var vm = new RepoTreeNodeViewModel(model, _languageResolver, parent);
        foreach (RepoTreeNode child in model.Children)
        {
            vm.Children.Add(BuildNodeVm(child, vm));
        }
        vm.ChildrenLoaded = model.Children.Count > 0 || !model.IsDirectory;
        return vm;
    }

    private static IEnumerable<RepoTreeNodeViewModel> FlattenLeaves(
        IEnumerable<RepoTreeNodeViewModel> nodes,
        string filter)
    {
        foreach (RepoTreeNodeViewModel node in nodes)
        {
            if (!node.IsDirectory)
            {
                if (node.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    yield return node;
            }
            else
            {
                foreach (RepoTreeNodeViewModel child in FlattenLeaves(node.Children, filter))
                    yield return child;
            }
        }
    }
}
