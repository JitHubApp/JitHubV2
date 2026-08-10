using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JitHub.WinUI.Views.Controls.Issue;

public sealed partial class RepoIssueListPane : UserControl
{
    private bool _initialized;
    private CancellationTokenSource? _searchDebounce;

    public RepoIssuePageViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler? NewIssueRequested;
    public event EventHandler? IssueSelected;

    public RepoIssueListPane(RepoIssuePageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => _initialized = true;
    }

    public void SetDrawerOpen(bool isOpen) =>
        CloseListPaneButton.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

    public void CancelPendingWork()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
    }

    private async void IssueStateSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || sender is not CommunityToolkit.WinUI.Controls.Segmented segmented)
        {
            return;
        }

        int selectedIndex = Math.Clamp(segmented.SelectedIndex, 0, ViewModel.StateOptions.Count - 1);
        ViewModel.SelectedStateOption = ViewModel.StateOptions[selectedIndex];
        await ViewModel.ApplyFiltersAsync();
    }

    private async void IssueFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            await ViewModel.ApplyFiltersAsync();
        }
    }

    private async void IssueSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || sender is not TextBox textBox)
        {
            return;
        }

        ViewModel.SearchText = textBox.Text;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        CancellationTokenSource debounce = new();
        _searchDebounce = debounce;
        try
        {
            await Task.Delay(220, debounce.Token);
            await ViewModel.ApplyFiltersAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void IssuesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GitHubIssue issue || sender is not ListView list)
        {
            return;
        }

        ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(list);
        ViewModel.SelectedIssue = issue;
        ProductPerformanceReadiness.CommitTraversal("repo_issues", issue.AutomationId);
        IssueSelected?.Invoke(this, EventArgs.Empty);
        anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
    }

    private void IssuesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        container.GotFocus -= IssueListItemContainer_GotFocus;
        if (args.InRecycleQueue)
        {
            return;
        }

        container.GotFocus += IssueListItemContainer_GotFocus;
        if (args.Item is GitHubIssue issue)
        {
            AutomationProperties.SetAutomationId(container, issue.AutomationId);
            AutomationProperties.SetName(container, issue.AutomationName);
        }
    }

    private void IssueListItemContainer_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem { Content: GitHubIssue issue })
        {
            ViewModel.PrefetchIssue(issue, IssuePrefetchReason.Hover);
        }
    }

    private void IssueListItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitHubIssue issue })
        {
            ViewModel.PrefetchIssue(issue, IssuePrefetchReason.Hover);
        }
    }

    private void CloseListPaneButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void NewIssueButton_Click(object sender, RoutedEventArgs e) =>
        NewIssueRequested?.Invoke(this, EventArgs.Empty);
}
