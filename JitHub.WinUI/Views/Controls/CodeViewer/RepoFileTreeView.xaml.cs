using System;
using System.ComponentModel;
using System.Threading;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    public RepoFileTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Typed accessor for x:Bind expressions in the XAML.
    private RepoFileTreeViewModel? ViewModel => DataContext as RepoFileTreeViewModel;

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

            // If the tree is already loaded, populate immediately.
            if (!vm.IsLoading)
                RebuildTreeView(vm);
        }

        Bindings.Update();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RepoFileTreeViewModel vm) return;

        if (e.PropertyName == nameof(RepoFileTreeViewModel.IsLoading) && !vm.IsLoading)
        {
            RebuildTreeView(vm);
        }
        else if (e.PropertyName == nameof(RepoFileTreeViewModel.FilteredRootNodes))
        {
            RebuildTreeView(vm);
        }
    }

    // ── TreeViewNode construction ─────────────────────────────────────

    /// <summary>
    /// Clears and rebuilds the TreeView root nodes from the VM's nodes.
    /// When a filter is active, shows a flat list of matching files.
    /// When no filter, shows the full hierarchical tree.
    /// Runs on the UI thread. Nodes are created lazily (children populated on expand).
    /// </summary>
    private void RebuildTreeView(RepoFileTreeViewModel vm)
    {
        FileTreeView.RootNodes.Clear();

        bool hasFilter = !string.IsNullOrWhiteSpace(vm.FilterText);
        if (hasFilter)
        {
            // Flat filtered results — no expand chevron needed.
            foreach (RepoTreeNodeViewModel nodeVm in vm.FilteredRootNodes)
            {
                FileTreeView.RootNodes.Add(new TreeViewNode
                {
                    Content = nodeVm,
                    HasUnrealizedChildren = false,
                });
            }
        }
        else
        {
            foreach (RepoTreeNodeViewModel rootVm in vm.RootNodes)
                FileTreeView.RootNodes.Add(CreateTreeViewNode(rootVm));
        }
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

    // ── TreeView event handlers ───────────────────────────────────────

    private void OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode treeNode) return;
        if (treeNode.Content is not RepoTreeNodeViewModel nodeVm) return;

        if (nodeVm.IsDirectory)
        {
            // Toggle expand / collapse on the row click.
            treeNode.IsExpanded = !treeNode.IsExpanded;
        }
        else
        {
            ViewModel?.SelectNodeCommand.Execute(nodeVm);
        }
    }

    private async void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not RepoTreeNodeViewModel nodeVm) return;

        nodeVm.IsExpanded = true;

        // For the truncated-tree fallback, lazy-load directory children first.
        if (!nodeVm.ChildrenLoaded && nodeVm.IsDirectory && ViewModel != null)
            await ViewModel.LoadDirectoryAsync(nodeVm, CancellationToken.None);

        // Populate TreeViewNode children from the (now loaded) VM children.
        // Guard with Count == 0 so re-expanding doesn't duplicate nodes.
        if (args.Node.Children.Count == 0)
        {
            foreach (RepoTreeNodeViewModel childVm in nodeVm.Children)
                args.Node.Children.Add(CreateTreeViewNode(childVm));

            args.Node.HasUnrealizedChildren = false;
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

