using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.Issue;

public sealed partial class RepoIssueListPane : UserControl
{
    private bool _initialized;
    private CancellationTokenSource? _searchDebounce;
    private ProductPerformanceScrollProbe? _performanceScrollProbe;
    private long _selectionPresentationGeneration;
    private bool _pointerSelectionInProgress;

    public RepoIssuePageViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler? NewIssueRequested;
    public event EventHandler<RepoIssueSelectedEventArgs>? IssueSelectionPriming;
    public event EventHandler<RepoIssueSelectedEventArgs>? IssueSelected;

    public ListViewScrollAnchor CaptureScrollAnchor() => ListViewScrollAnchor.Capture(IssuesList);

    public RepoIssueListPane(RepoIssuePageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initialized = true;
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = ProductPerformanceReadiness.IsEnabled &&
            FindDescendant<ScrollViewer>(IssuesList) is ScrollViewer scrollViewer
                ? ProductPerformanceScrollProbe.TryStart(IssuesList, scrollViewer)
                : null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _initialized = false;
        Interlocked.Increment(ref _selectionPresentationGeneration);
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is T nested)
            {
                return nested;
            }
        }

        return null;
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

        if (ViewModel.SelectedIssue?.Number == issue.Number)
        {
            IssueSelected?.Invoke(this, new RepoIssueSelectedEventArgs(issue));
        }
    }

    private void IssuesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: GitHubIssue issue } list)
        {
            return;
        }

        if (_pointerSelectionInProgress)
        {
            return;
        }

        PresentIssueSelection(list, issue, applyListSelection: false);
    }

    private void IssueListItemContainer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        GitHubIssue? issue = sender switch
        {
            ListViewItem { Content: GitHubIssue item } => item,
            FrameworkElement { DataContext: GitHubIssue item } => item,
            _ => null
        };
        if (issue is null || sender is not UIElement pointerRoot)
        {
            return;
        }

        bool traversalStarted = ProductPerformanceReadiness.IsEnabled;
        if (traversalStarted)
        {
            ProductPerformanceReadiness.BeginTraversal(
                "repo_issues",
                issue.AutomationId,
                "repo_issues");
        }

        if (e.GetCurrentPoint(pointerRoot).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            if (traversalStarted)
            {
                ProductPerformanceReadiness.CancelTraversal();
            }

            return;
        }

        PresentIssueSelection(
            IssuesList,
            issue,
            applyListSelection: true,
            traversalAlreadyStarted: traversalStarted);
        ProductPerformanceReadiness.RecordTraversalStage("repo_issues.pointer.selected");
        e.Handled = true;
    }

    private void PresentIssueSelection(
        ListView list,
        GitHubIssue issue,
        bool applyListSelection,
        bool traversalAlreadyStarted = false)
    {
        if (ProductPerformanceReadiness.IsEnabled && !traversalAlreadyStarted)
        {
            ProductPerformanceReadiness.BeginTraversal(
                "repo_issues",
                issue.AutomationId,
                "repo_issues");
        }

        long generation = Interlocked.Increment(ref _selectionPresentationGeneration);
        IssueSelectionPriming?.Invoke(this, new RepoIssueSelectedEventArgs(issue));
        ProductPerformanceReadiness.RecordTraversalStage("repo_issues.selection.primed");
        IssueSelected?.Invoke(this, new RepoIssueSelectedEventArgs(issue));
        ProductPerformanceReadiness.RecordTraversalStage("repo_issues.commit.scheduled");
        DeferredFrameAction.Schedule(
            this,
            () => generation == Volatile.Read(ref _selectionPresentationGeneration) &&
                (applyListSelection ||
                    list.SelectedItem is GitHubIssue selected && selected.Number == issue.Number),
            () =>
            {
                if (!_initialized ||
                    !IsLoaded ||
                    generation != Volatile.Read(ref _selectionPresentationGeneration) ||
                    !applyListSelection &&
                    (list.SelectedItem is not GitHubIssue current ||
                        current.Number != issue.Number))
                {
                    return;
                }

                if (applyListSelection)
                {
                    _pointerSelectionInProgress = true;
                    try
                    {
                        list.SelectedItem = issue;
                    }
                    finally
                    {
                        _pointerSelectionInProgress = false;
                    }

                    if (list.ContainerFromItem(issue) is Control container)
                    {
                        container.Focus(FocusState.Pointer);
                    }
                }
                ProductPerformanceReadiness.RecordTraversalStage("repo_issues.list.selected");
                ViewModel.SelectedIssue = issue;
            });
    }

    private void IssuesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        container.GotFocus -= IssueListItemContainer_GotFocus;
        container.RemoveHandler(
            PointerPressedEvent,
            new PointerEventHandler(IssueListItemContainer_PointerPressed));
        if (args.InRecycleQueue)
        {
            return;
        }

        container.GotFocus += IssueListItemContainer_GotFocus;
        container.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(IssueListItemContainer_PointerPressed),
            handledEventsToo: true);
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

public sealed record RepoIssueSelectedEventArgs(GitHubIssue Issue);
