using System;
using JitHub.Services.Layout;
using JitHub.Services;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class MyIssuesPage : Page
{
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private ListViewScrollAnchor? _pendingRefreshAnchor;
    private bool _syncingFilterControls;

    public MyIssuesPageViewModel ViewModel { get; }

    public MyIssuesPage()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<MyIssuesPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.ListSnapshotApplying += OnListSnapshotApplying;
        ViewModel.ListSnapshotApplied += OnListSnapshotApplied;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            CommitPerformanceReadiness();
            return;
        }

        _initialized = true;
        ApplyPseudoLongLabelsForAutomation();
        UpdateFilterPresentation(IssueFilterHost.ActualWidth);
        try
        {
            await ViewModel.InitializeAsync();
            CommitPerformanceReadiness();
            UpdateFilterPresentation(IssueFilterHost.ActualWidth);
            UpdatePaneButtonVisibility();
            MaybeOpenInitialIssueListDrawer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load issues page: {ex}");
        }
    }

    private void CommitPerformanceReadiness() =>
        ProductPerformanceReadiness.CommitRoute(
            "my_issues",
            ProductPerformanceReadiness.CountIdentity(ViewModel.Items.Count));

    private void IssueScopeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (_syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(IssueScopeSegmented.SelectedIndex, IssueStateSegmented.SelectedIndex);
        GitHubMeIssueFilter filter = IssueScopeSegmented.SelectedIndex switch
        {
            1 => GitHubMeIssueFilter.Created,
            2 => GitHubMeIssueFilter.Mentioned,
            _ => GitHubMeIssueFilter.Assigned
        };
        ViewModel.SetIssueFilter(filter);
    }

    private void IssueStateSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (_syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(IssueScopeSegmented.SelectedIndex, IssueStateSegmented.SelectedIndex);
        GitHubMeWorkItemState state = IssueStateSegmented.SelectedIndex switch
        {
            1 => GitHubMeWorkItemState.Closed,
            2 => GitHubMeWorkItemState.All,
            _ => GitHubMeWorkItemState.Open
        };
        ViewModel.SetWorkItemState(state);
    }

    private void IssueScopeCompactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(IssueScopeCompactPicker.SelectedIndex, IssueStateCompactPicker.SelectedIndex);
        ViewModel.SetIssueFilter(IssueScopeCompactPicker.SelectedIndex switch
        {
            1 => GitHubMeIssueFilter.Created,
            2 => GitHubMeIssueFilter.Mentioned,
            _ => GitHubMeIssueFilter.Assigned
        });
    }

    private void IssueStateCompactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(IssueScopeCompactPicker.SelectedIndex, IssueStateCompactPicker.SelectedIndex);
        ViewModel.SetWorkItemState(IssueStateCompactPicker.SelectedIndex switch
        {
            1 => GitHubMeWorkItemState.Closed,
            2 => GitHubMeWorkItemState.All,
            _ => GitHubMeWorkItemState.Open
        });
    }

    private void SyncFilterSelection(int scopeIndex, int stateIndex)
    {
        _syncingFilterControls = true;
        try
        {
            IssueScopeSegmented.SelectedIndex = Math.Clamp(scopeIndex, 0, 2);
            IssueScopeCompactPicker.SelectedIndex = Math.Clamp(scopeIndex, 0, 2);
            IssueStateSegmented.SelectedIndex = Math.Clamp(stateIndex, 0, 2);
            IssueStateCompactPicker.SelectedIndex = Math.Clamp(stateIndex, 0, 2);
        }
        finally
        {
            _syncingFilterControls = false;
        }
    }

    private void IssueFilterHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateFilterPresentation(e.NewSize.Width);

    private void UpdateFilterPresentation(double availableWidth)
    {
        string[] scopeLabels =
        [
            AssignedScopeSegment.Content?.ToString() ?? string.Empty,
            CreatedScopeSegment.Content?.ToString() ?? string.Empty,
            MentionedScopeSegment.Content?.ToString() ?? string.Empty
        ];
        string[] stateLabels =
        [
            OpenStateSegment.Content?.ToString() ?? string.Empty,
            ClosedStateSegment.Content?.ToString() ?? string.Empty,
            AllStateSegment.Content?.ToString() ?? string.Empty
        ];
        bool useCompact = MyIssuesFilterLayoutPolicy.ShouldUseCompact(availableWidth, scopeLabels, stateLabels);
        ExpandedIssueFilters.Visibility = useCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactIssueFilters.Visibility = useCompact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPseudoLongLabelsForAutomation()
    {
        if (!string.Equals(Program.CurrentLaunchOptions.Scenario, "my-issues-pseudo-long-labels", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        const string assigned = "Assigned to the authenticated account";
        const string created = "Created by the authenticated account";
        const string mentioned = "Mentioning the authenticated account";
        const string open = "Currently open work items";
        const string closed = "Previously closed work items";
        const string all = "All available work item states";
        AssignedScopeSegment.Content = AssignedScopeCompactItem.Content = assigned;
        CreatedScopeSegment.Content = CreatedScopeCompactItem.Content = created;
        MentionedScopeSegment.Content = MentionedScopeCompactItem.Content = mentioned;
        OpenStateSegment.Content = OpenStateCompactItem.Content = open;
        ClosedStateSegment.Content = ClosedStateCompactItem.Content = closed;
        AllStateSegment.Content = AllStateCompactItem.Content = all;
    }

    private void IssuesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MeWorkItemViewItem item)
        {
            ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(IssuesList, GetIssueItemKey);
            IssuesWorkspace.CloseDrawer();
            anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
        }
    }

    private void IssuesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IssuesList.SelectedItem is MeWorkItemViewItem item)
        {
            ProductPerformanceReadiness.CommitTraversal("my_issues", item.AutomationId);
            _ = DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (ReferenceEquals(IssuesList.SelectedItem, item))
                    {
                        ViewModel.SelectedItem = item;
                    }
                });
        }
    }

    private void IssuesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is MeWorkItemViewItem item)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, item.AutomationId);
            AutomationProperties.SetName(args.ItemContainer, item.AutomationName);
        }
    }

    private void CommentsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is MeIssueCommentViewItem comment)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, comment.AutomationId);
            AutomationProperties.SetName(args.ItemContainer, comment.AutomationName);
        }
    }

    private void OnListSnapshotApplying(object? sender, EventArgs e)
    {
        if (IssuesList.Items.Count > 0 && IssuesList.XamlRoot is not null)
        {
            _pendingRefreshAnchor = ListViewScrollAnchor.Capture(IssuesList, GetIssueItemKey);
        }
    }

    private void OnListSnapshotApplied(object? sender, EventArgs e)
    {
        ListViewScrollAnchor? anchor = _pendingRefreshAnchor;
        _pendingRefreshAnchor = null;
        anchor?.RestoreAfterCollectionChange(DispatcherQueue);
    }

    private static string? GetIssueItemKey(object item) =>
        item is MeWorkItemViewItem issue ? issue.StableKey : null;

    private void IssuesWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState e)
    {
        UpdatePaneButtonVisibility();
        MaybeOpenInitialIssueListDrawer();
    }

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e)
        => IssuesWorkspace.OpenLeadingPane();

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e)
        => IssuesWorkspace.OpenTrailingPane();

    private void CloseWorkspaceDrawerButton_Click(object sender, RoutedEventArgs e)
        => IssuesWorkspace.CloseDrawer();

    private void UpdatePaneButtonVisibility()
    {
        AdaptiveWorkspaceState? state = IssuesWorkspace.State;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;
        MyIssuesOpenListPaneButton.Visibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        MyIssuesCloseListPaneButton.Visibility = isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        MyIssuesOpenInspectorPaneButton.Visibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        MyIssuesCloseInspectorPaneButton.Visibility = isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MaybeOpenInitialIssueListDrawer()
    {
        if (_openedInitialListDrawer ||
            !_initialized ||
            ViewModel.HasSelectedIssue ||
            IssuesWorkspace.State is not { ShouldShowLeadingPaneButton: true })
        {
            return;
        }

        _openedInitialListDrawer = true;
        IssuesWorkspace.OpenLeadingPane();
    }

    private void OpenSelectedInRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.OpenSelectedInRepositoryCommand.CanExecute(null))
        {
            ViewModel.OpenSelectedInRepositoryCommand.Execute(null);
        }
    }

    private void OpenSelectedInRepositoryButton_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.PrefetchSelectedIssueForNavigation();
    }
}
