using System;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class MyPullRequestsPage : Page
{
    private const string PseudoLongLabelsScenario = "my-pull-requests-pseudo-long-labels";
    private const string PseudoOpenStateLabel = "Currently open pull requests involving the authenticated account";
    private const string PseudoClosedStateLabel = "Previously closed pull requests involving the authenticated account";
    private const string PseudoAllStateLabel = "All pull requests involving the authenticated account";
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private bool _syncingFilterControls;
    private ListViewScrollAnchor? _pendingRefreshAnchor;

    public MyPullRequestsPageViewModel ViewModel { get; }

    public string PullRequestOpenStateLabel => IsPseudoLongLabelsScenario
        ? PseudoOpenStateLabel
        : ViewModel.PullRequestOpenStateLabel;

    public string PullRequestClosedStateLabel => IsPseudoLongLabelsScenario
        ? PseudoClosedStateLabel
        : ViewModel.PullRequestClosedStateLabel;

    public string PullRequestAllStateLabel => IsPseudoLongLabelsScenario
        ? PseudoAllStateLabel
        : ViewModel.PullRequestAllStateLabel;

    private static bool IsPseudoLongLabelsScenario =>
        string.Equals(Program.CurrentLaunchOptions.Scenario, PseudoLongLabelsScenario, StringComparison.OrdinalIgnoreCase);

    public MyPullRequestsPage()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<MyPullRequestsPageViewModel>();
        InitializeComponent();
        PullRequestSectionSelector.SelectedItem = ConversationSectionItem;
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
        UpdatePullRequestFilterLayout(PullRequestFilterHost.ActualWidth);
        try
        {
            await ViewModel.InitializeAsync();
            CommitPerformanceReadiness();
            ApplyPseudoLongLabelsForAutomation();
            UpdatePullRequestFilterLayout(PullRequestFilterHost.ActualWidth);
            UpdatePaneButtonVisibility();
            MaybeOpenInitialPullRequestListDrawer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load pull requests page: {ex}");
        }
    }

    private void CommitPerformanceReadiness() =>
        ProductPerformanceReadiness.CommitRoute(
            "my_pull_requests",
            ProductPerformanceReadiness.CountIdentity(ViewModel.Items.Count));

    private void PullRequestStateSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncStateFilterSelection(PullRequestStateSegmented.SelectedIndex);
        GitHubMeWorkItemState state = PullRequestStateSegmented.SelectedIndex switch
        {
            1 => GitHubMeWorkItemState.Closed,
            2 => GitHubMeWorkItemState.All,
            _ => GitHubMeWorkItemState.Open
        };
        ViewModel.SetWorkItemState(state);
    }

    private void PullRequestStateCompactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncStateFilterSelection(PullRequestStateCompactPicker.SelectedIndex);
        ViewModel.SetWorkItemState(PullRequestStateCompactPicker.SelectedIndex switch
        {
            1 => GitHubMeWorkItemState.Closed,
            2 => GitHubMeWorkItemState.All,
            _ => GitHubMeWorkItemState.Open
        });
    }

    private void SyncStateFilterSelection(int selectedIndex)
    {
        _syncingFilterControls = true;
        try
        {
            int normalizedIndex = Math.Clamp(selectedIndex, 0, 2);
            PullRequestStateSegmented.SelectedIndex = normalizedIndex;
            PullRequestStateCompactPicker.SelectedIndex = normalizedIndex;
        }
        finally
        {
            _syncingFilterControls = false;
        }
    }

    private void PullRequestFilterHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdatePullRequestFilterLayout(e.NewSize.Width);

    private void UpdatePullRequestFilterLayout(double availableWidth)
    {
        string[] stateLabels =
        [
            OpenStateSegment.Content?.ToString() ?? string.Empty,
            ClosedStateSegment.Content?.ToString() ?? string.Empty,
            AllStateSegment.Content?.ToString() ?? string.Empty
        ];
        bool useCompact = MyIssuesFilterLayoutPolicy.ShouldUseCompact(
            availableWidth,
            [],
            stateLabels);
        PullRequestStateSegmented.Visibility = useCompact ? Visibility.Collapsed : Visibility.Visible;
        PullRequestStateCompactPicker.Visibility = useCompact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPseudoLongLabelsForAutomation()
    {
        if (!IsPseudoLongLabelsScenario)
        {
            return;
        }

        ConversationSectionItem.Text = "Conversation and discussion";
        CommitsSectionItem.Text = "Commits included in this pull request";
        ReviewsSectionItem.Text = "Reviews and reviewer feedback";
        TimelineSectionItem.Text = "Complete pull request timeline";
    }

    private void PullRequestSectionSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        PullRequestWorkspaceSection section = ReferenceEquals(sender.SelectedItem, CommitsSectionItem)
            ? PullRequestWorkspaceSection.Commits
            : ReferenceEquals(sender.SelectedItem, ReviewsSectionItem)
                ? PullRequestWorkspaceSection.Reviews
                : ReferenceEquals(sender.SelectedItem, TimelineSectionItem)
                    ? PullRequestWorkspaceSection.Timeline
                    : PullRequestWorkspaceSection.Conversation;
        ViewModel.SetPullRequestSection(section);
    }

    private void PullRequestsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MeWorkItemViewItem item)
        {
            ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(PullRequestsList, GetPullRequestItemKey);
            PullRequestsWorkspace.CloseDrawer();
            anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
        }
    }

    private void PullRequestsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PullRequestsList.SelectedItem is MeWorkItemViewItem item)
        {
            ProductPerformanceReadiness.CommitTraversal("my_pull_requests", item.AutomationId);
            _ = DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (ReferenceEquals(PullRequestsList.SelectedItem, item))
                    {
                        ViewModel.SelectedItem = item;
                    }
                });
        }
    }

    private void PullRequestsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is MeWorkItemViewItem item)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, item.AutomationId);
            AutomationProperties.SetName(args.ItemContainer, item.AutomationName);
        }
    }

    private void PullRequestDetailList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is null)
        {
            return;
        }

        (string? automationId, string? automationName) = args.Item switch
        {
            MeIssueCommentViewItem comment => (comment.AutomationId, comment.AutomationName),
            MePullRequestCommitViewItem commit => (commit.AutomationId, commit.AutomationName),
            MePullRequestReviewViewItem review => (review.AutomationId, review.AutomationName),
            MePullRequestTimelineViewItem timeline => (timeline.AutomationId, timeline.AutomationName),
            _ => (null, null)
        };
        if (!string.IsNullOrWhiteSpace(automationId) && !string.IsNullOrWhiteSpace(automationName))
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, automationId);
            AutomationProperties.SetName(args.ItemContainer, automationName);
        }
    }

    private void OnListSnapshotApplying(object? sender, EventArgs e)
    {
        if (PullRequestsList.Items.Count > 0 && PullRequestsList.XamlRoot is not null)
        {
            _pendingRefreshAnchor = ListViewScrollAnchor.Capture(PullRequestsList, GetPullRequestItemKey);
        }
    }

    private void OnListSnapshotApplied(object? sender, EventArgs e)
    {
        ListViewScrollAnchor? anchor = _pendingRefreshAnchor;
        _pendingRefreshAnchor = null;
        anchor?.RestoreAfterCollectionChange(DispatcherQueue);
    }

    private static string? GetPullRequestItemKey(object item) =>
        item is MeWorkItemViewItem pullRequest ? pullRequest.StableKey : null;

    private void PullRequestsWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState e)
    {
        UpdatePaneButtonVisibility();
        QueuePullRequestFilterLayoutUpdate();
        MaybeOpenInitialPullRequestListDrawer();
    }

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e)
    {
        PullRequestsWorkspace.OpenLeadingPane();
        QueuePullRequestFilterLayoutUpdate();
    }

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e)
        => PullRequestsWorkspace.OpenTrailingPane();

    private void CloseWorkspaceDrawerButton_Click(object sender, RoutedEventArgs e)
        => PullRequestsWorkspace.CloseDrawer();

    private void UpdatePaneButtonVisibility()
    {
        AdaptiveWorkspaceState? state = PullRequestsWorkspace.State;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;
        MyPullRequestsOpenListPaneButton.Visibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        MyPullRequestsCloseListPaneButton.Visibility = isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        MyPullRequestsOpenInspectorPaneButton.Visibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        MyPullRequestsCloseInspectorPaneButton.Visibility = isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void QueuePullRequestFilterLayoutUpdate()
    {
        DispatcherQueue.TryEnqueue(() =>
            UpdatePullRequestFilterLayout(PullRequestFilterHost.ActualWidth));
    }

    private void MaybeOpenInitialPullRequestListDrawer()
    {
        if (_openedInitialListDrawer ||
            !_initialized ||
            ViewModel.HasSelectedIssue ||
            PullRequestsWorkspace.State is not { ShouldShowLeadingPaneButton: true })
        {
            return;
        }

        _openedInitialListDrawer = true;
        PullRequestsWorkspace.OpenLeadingPane();
        QueuePullRequestFilterLayoutUpdate();
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
