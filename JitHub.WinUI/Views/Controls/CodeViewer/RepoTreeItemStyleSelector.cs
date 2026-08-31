using System;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

internal sealed partial class RepoTreeItemStyleSelector : StyleSelector
{
    private readonly Action<TreeViewItem, RepoTreeNodeViewModel> _configureContainer;

    internal RepoTreeItemStyleSelector(
        Action<TreeViewItem, RepoTreeNodeViewModel> configureContainer)
    {
        _configureContainer = configureContainer;
    }

    protected override Style SelectStyleCore(object item, DependencyObject container)
    {
        RepoTreeNodeViewModel? node = item switch
        {
            TreeViewNode { Content: RepoTreeNodeViewModel treeNode } => treeNode,
            RepoTreeNodeViewModel directNode => directNode,
            _ => null,
        };

        if (container is TreeViewItem treeViewItem && node is not null)
        {
            _configureContainer(treeViewItem, node);
        }

        return base.SelectStyleCore(item, container);
    }
}
