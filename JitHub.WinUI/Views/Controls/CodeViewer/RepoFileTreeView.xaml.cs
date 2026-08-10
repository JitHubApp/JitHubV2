using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

/// <summary>
/// File-tree panel for the native code viewer.
/// DataContext must be set to a <see cref="RepoFileTreeViewModel"/> by the owner.
///
/// Uses TreeView in TreeViewNode mode (RootNodes collection, not ItemsSource).
/// This avoids the WinUI 3 ItemsSource-binding bug where {Binding Children} on
/// TreeViewItem.ItemsSource is unreliable and never reveals child items.
/// </summary>
public sealed partial class RepoFileTreeView : UserControl
{
    private RepoFileTreeViewModel? _subscribedViewModel;
    private CancellationTokenSource? _treeUpdateCts;
    private CancellationTokenSource? _lifetimeCts;
    private readonly object _ownedTaskGate = new();
    private Task _ownedTask = Task.CompletedTask;
    private string? _pendingSelectionPath;

    public event EventHandler? FileInvoked;
    public event EventHandler<RepoFileTreeTabNavigationEventArgs>? TabNavigationRequested;

    public RepoFileTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        var previewKeyHandler = new KeyEventHandler(DrawerTabStop_PreviewKeyDown);
        FileFilter.AddHandler(PreviewKeyDownEvent, previewKeyHandler, handledEventsToo: true);
        FileTreeView.AddHandler(PreviewKeyDownEvent, previewKeyHandler, handledEventsToo: true);
    }

    // Typed accessor for x:Bind expressions in the XAML.
    private RepoFileTreeViewModel? ViewModel => DataContext as RepoFileTreeViewModel;

    public bool FocusTree() => FileTreeView.Focus(FocusState.Programmatic);

    public bool FocusFilter() => FileFilter.Focus(FocusState.Programmatic);

    private void DrawerTabStop_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab)
        {
            return;
        }

        CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var args = new RepoFileTreeTabNavigationEventArgs(
            (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down);
        TabNavigationRequested?.Invoke(this, args);
        e.Handled = args.Handled;
    }

    // ── DataContext management ────────────────────────────────────────

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // Unsubscribe from the old VM.
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        // Subscribe to the new VM and refresh x:Bind expressions.
        if (ViewModel is { } vm)
        {
            _subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;

            // DataContext is commonly assigned before Loaded. Starting a render in
            // that state would use the intentionally-cancelled lifetime token and
            // leave the UIA readiness state at "building" forever.
            if (!vm.IsLoading && Volatile.Read(ref _lifetimeCts) is { IsCancellationRequested: false })
                UpdateTreeView(vm);
            UpdateTreeStatus(vm);
        }

        Bindings.Update();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource nextLifetime = new();
        CancellationTokenSource? previousLifetime = Interlocked.Exchange(ref _lifetimeCts, nextLifetime);
        previousLifetime?.Cancel();
        if (previousLifetime is not null)
        {
            DisposeAfterOwnedWork(previousLifetime);
        }

        if (ViewModel is { } viewModel)
        {
            if (!ReferenceEquals(_subscribedViewModel, viewModel))
            {
                _subscribedViewModel?.PropertyChanged -= OnViewModelPropertyChanged;
                _subscribedViewModel = viewModel;
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            // A responsive reparent can unload and reload the same control without
            // changing DataContext. Always re-apply the current keyed snapshot so
            // rows and readiness are deterministic after returning from a drawer.
            if (!viewModel.IsLoading)
                UpdateTreeView(viewModel);
            UpdateTreeStatus(viewModel);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RetireTreeUpdate();
        CancellationTokenSource? lifetime = Interlocked.Exchange(ref _lifetimeCts, null);
        lifetime?.Cancel();
        if (lifetime is not null)
        {
            DisposeAfterOwnedWork(lifetime);
        }
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RepoFileTreeViewModel vm) return;

        if (e.PropertyName == nameof(RepoFileTreeViewModel.IsLoading) && !vm.IsLoading)
        {
            UpdateTreeView(vm);
        }
        else if (e.PropertyName == nameof(RepoFileTreeViewModel.FilteredRootNodes))
        {
            UpdateTreeView(vm);
        }
        else if (e.PropertyName == nameof(RepoFileTreeViewModel.ErrorMessage))
        {
            UpdateTreeStatus(vm);
        }
    }

    // ── TreeViewNode construction ─────────────────────────────────────

    /// <summary>
    /// Reconciles TreeView nodes by repository path so refresh keeps realized rows,
    /// expansion state, selection, and scroll anchors stable.
    /// When a filter is active, shows a flat list of matching files.
    /// When no filter, shows the full hierarchical tree.
    /// Runs on the UI thread. Nodes are created lazily (children populated on expand).
    /// </summary>
    private void UpdateTreeView(RepoFileTreeViewModel vm)
    {
        RetireTreeUpdate();
        CancellationToken lifetimeToken = Volatile.Read(ref _lifetimeCts)?.Token ?? new CancellationToken(canceled: true);
        CancellationTokenSource request = vm.CreateContextLinkedSource(lifetimeToken);
        _treeUpdateCts = request;
        bool hasFilter = !string.IsNullOrWhiteSpace(vm.FilterText);
        IReadOnlyList<RepoTreeNodeViewModel> source = (hasFilter ? vm.FilteredRootNodes : vm.RootNodes).ToList();
        bool hasDeterministicSource = vm.FindNodeByPath("src/App.cs") is not null;
        AutomationProperties.SetItemStatus(FileTreeView, "building");
        OwnTask(UpdateTreeViewObservedAsync(source, hasFilter, hasDeterministicSource, request));
    }

    private async Task UpdateTreeViewObservedAsync(
        IReadOnlyList<RepoTreeNodeViewModel> source,
        bool flat,
        bool hasDeterministicSource,
        CancellationTokenSource request)
    {
        try
        {
            await ApplyTreeViewNodesAsync(
                FileTreeView.RootNodes,
                source,
                flat,
                new UiWorkBudget(),
                request.Token);
            if (ReferenceEquals(Volatile.Read(ref _treeUpdateCts), request))
            {
                AutomationProperties.SetItemStatus(
                    FileTreeView,
                    hasDeterministicSource ? "ready:path:src/App.cs" : "ready");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _treeUpdateCts, null, request);
            request.Dispose();
        }
    }

    private static async Task ApplyTreeViewNodesAsync(
        IList<TreeViewNode> target,
        IEnumerable<RepoTreeNodeViewModel> source,
        bool flat,
        UiWorkBudget budget,
        CancellationToken ct)
    {
        Dictionary<string, TreeViewNode> existing = target
            .Where(static node => node.Content is RepoTreeNodeViewModel)
            .GroupBy(static node => ((RepoTreeNodeViewModel)node.Content).Path, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        List<TreeViewNode> desired = [];

        foreach (RepoTreeNodeViewModel viewModel in source)
        {
            ct.ThrowIfCancellationRequested();
            TreeViewNode treeNode = existing.TryGetValue(viewModel.Path, out TreeViewNode? current)
                ? current
                : CreateTreeViewNode(viewModel);
            treeNode.Content = viewModel;

            if (flat)
            {
                treeNode.Children.Clear();
                treeNode.HasUnrealizedChildren = false;
                treeNode.IsExpanded = false;
            }
            else
            {
                if (treeNode.Children.Count > 0 || viewModel.IsExpanded)
                {
                    await ApplyTreeViewNodesAsync(
                        treeNode.Children,
                        viewModel.Children,
                        flat: false,
                        budget,
                        ct);
                }

                treeNode.HasUnrealizedChildren = viewModel.IsDirectory &&
                    !viewModel.ChildrenLoaded &&
                    treeNode.Children.Count == 0;
                treeNode.IsExpanded = viewModel.IsExpanded;
            }

            desired.Add(treeNode);
            await YieldIfNeededAsync(budget, ct);
        }

        for (int index = 0; index < desired.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            TreeViewNode item = desired[index];
            int currentIndex = target.IndexOf(item);
            if (currentIndex < 0)
            {
                target.Insert(index, item);
            }
            else if (currentIndex != index)
            {
                target.RemoveAt(currentIndex);
                target.Insert(index, item);
            }
            await YieldIfNeededAsync(budget, ct);
        }

        while (target.Count > desired.Count)
        {
            ct.ThrowIfCancellationRequested();
            target.RemoveAt(target.Count - 1);
            await YieldIfNeededAsync(budget, ct);
        }
    }

    private static async Task YieldIfNeededAsync(UiWorkBudget budget, CancellationToken ct)
    {
        if (!budget.ShouldYield()) return;
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        budget.Restart();
    }

    private void RetireTreeUpdate()
    {
        CancellationTokenSource? request = Interlocked.Exchange(ref _treeUpdateCts, null);
        request?.Cancel();
    }

    private void OwnTask(Task task)
    {
        lock (_ownedTaskGate)
        {
            _ownedTask = ObserveOwnedTasksAsync(_ownedTask, task);
        }
    }

    private static async Task ObserveOwnedTasksAsync(Task previous, Task current)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await current.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void DisposeAfterOwnedWork(CancellationTokenSource source)
    {
        Task pending;
        lock (_ownedTaskGate)
        {
            pending = _ownedTask;
        }

        _ = DisposeSourceAfterAsync(source, pending);
    }

    private static async Task DisposeSourceAfterAsync(CancellationTokenSource source, Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    private void UpdateTreeStatus(RepoFileTreeViewModel viewModel)
    {
        TreeStatus.Message = viewModel.ErrorMessage ?? string.Empty;
        TreeStatus.IsOpen = !string.IsNullOrWhiteSpace(viewModel.ErrorMessage);
    }

    private static TreeViewNode CreateTreeViewNode(RepoTreeNodeViewModel nodeVm)
    {
        return new TreeViewNode
        {
            Content = nodeVm,
            // Show the expand chevron for directories even before children are loaded.
            HasUnrealizedChildren = nodeVm.IsDirectory,
        };
    }

    private void OnTreeItemContentLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RepoTreeNodeViewModel node } element)
        {
            return;
        }

        if (FindTreeViewItem(element) is TreeViewItem container)
        {
            AutomationProperties.SetAutomationId(container, node.AutomationId);
            AutomationProperties.SetName(container, node.AutomationName);
            AutomationProperties.SetItemStatus(container, $"path:{node.Path}");
        }
    }

    // ── TreeView event handlers ───────────────────────────────────────

    private void OnSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not RepoTreeNodeViewModel { IsDirectory: false } nodeVm ||
            string.Equals(ViewModel?.SelectedNode?.Path, nodeVm.Path, StringComparison.Ordinal))
        {
            return;
        }

        SelectFileNode(nodeVm);
    }

    private void OnTreeItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RepoTreeNodeViewModel { IsDirectory: false } nodeVm } element ||
            e.GetCurrentPoint(element).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        if (FindTreeViewItem(element) is { IsSelected: false } container)
        {
            container.IsSelected = true;
        }

        if (string.Equals(ViewModel?.SelectedNode?.Path, nodeVm.Path, StringComparison.Ordinal))
        {
            FileInvoked?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Native file explorers select on pointer press. Starting here keeps the
        // visible selection and cached preview path ahead of TreeView's slower
        // release/invocation gesture without swallowing the event from TreeView.
        SelectFileNode(nodeVm);
    }

    private static TreeViewItem? FindTreeViewItem(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current as TreeViewItem;
    }

    private void OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode treeNode) return;
        if (treeNode.Content is not RepoTreeNodeViewModel nodeVm) return;

        if (nodeVm.IsDirectory)
        {
            // Toggle expand / collapse on the row click.
            treeNode.IsExpanded = !treeNode.IsExpanded;
        }
        else if (!string.Equals(
                     ViewModel?.SelectedNode?.Path,
                     nodeVm.Path,
                     StringComparison.Ordinal))
        {
            // SelectionChanged normally starts file navigation on pointer/key selection.
            // Keep invocation as an accessibility fallback for providers that invoke
            // a row without first moving TreeView selection.
            SelectFileNode(nodeVm, requireVisualSelection: false);
        }
        else
        {
            FileInvoked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SelectFileNode(RepoTreeNodeViewModel nodeVm, bool requireVisualSelection = true)
    {
        if (string.Equals(_pendingSelectionPath, nodeVm.Path, StringComparison.Ordinal))
        {
            return;
        }

        _pendingSelectionPath = nodeVm.Path;
        ProductPerformanceReadiness.CommitTraversal("repo_code", nodeVm.AutomationId);
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!string.Equals(_pendingSelectionPath, nodeVm.Path, StringComparison.Ordinal))
                {
                    return;
                }

                _pendingSelectionPath = null;
                if (requireVisualSelection &&
                    (FileTreeView.SelectedNode?.Content is not RepoTreeNodeViewModel selectedNode ||
                     !string.Equals(selectedNode.Path, nodeVm.Path, StringComparison.Ordinal)))
                {
                    return;
                }

                ViewModel?.SelectNodeCommand.Execute(nodeVm);
                FileInvoked?.Invoke(this, EventArgs.Empty);
            });
    }

    private void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not RepoTreeNodeViewModel nodeVm) return;

        nodeVm.IsExpanded = true;
        RepoFileTreeViewModel? viewModel = ViewModel;
        CancellationToken lifetimeToken = Volatile.Read(ref _lifetimeCts)?.Token ?? new CancellationToken(canceled: true);
        if (viewModel is null || lifetimeToken.IsCancellationRequested) return;

        CancellationTokenSource request = viewModel.CreateContextLinkedSource(lifetimeToken);
        OwnTask(ExpandNodeObservedAsync(args.Node, nodeVm, viewModel, request));
    }

    private async Task ExpandNodeObservedAsync(
        TreeViewNode treeNode,
        RepoTreeNodeViewModel nodeVm,
        RepoFileTreeViewModel viewModel,
        CancellationTokenSource request)
    {
        using (request)
        {
            try
            {
                CancellationToken token = request.Token;

                // For the truncated-tree fallback, lazy-load directory children first.
                if (!nodeVm.ChildrenLoaded && nodeVm.IsDirectory)
                {
                    await viewModel.LoadDirectoryAsync(nodeVm, token);
                }

                token.ThrowIfCancellationRequested();
                await ApplyTreeViewNodesAsync(
                    treeNode.Children,
                    nodeVm.Children,
                    flat: false,
                    new UiWorkBudget(),
                    token);
                treeNode.HasUnrealizedChildren = nodeVm.IsDirectory && !nodeVm.ChildrenLoaded;
                if (viewModel.FindNodeByPath("src/App.cs") is not null)
                {
                    AutomationProperties.SetItemStatus(FileTreeView, "ready:path:src/App.cs");
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
            }
        }
    }

    private void OnCollapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Node.Content is RepoTreeNodeViewModel nodeVm)
            nodeVm.IsExpanded = false;
    }

    // ── Static helpers for x:Bind function calls inside DataTemplate ──
    // Must be public so the x:Bind–generated code can call them via the
    // "local:RepoFileTreeView.Method()" syntax.

    public static Visibility FolderOpenVis(bool isDirectory, bool isExpanded)
        => (isDirectory && isExpanded) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility FolderClosedVis(bool isDirectory, bool isExpanded)
        => (isDirectory && !isExpanded) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility FileVis(bool isDirectory)
        => isDirectory ? Visibility.Collapsed : Visibility.Visible;

    // ── Instance helper for top-level x:Bind expressions ─────────────

    public Visibility BoolToVis(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class RepoFileTreeTabNavigationEventArgs(bool moveBackward) : EventArgs
{
    public bool MoveBackward { get; } = moveBackward;
    public bool Handled { get; set; }
}

