using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class MyPullRequestsPage : Page
{
    private const string PseudoLongLabelsScenario = "my-pull-requests-pseudo-long-labels";
    private const string PseudoInvolvedScopeLabel = "Pull requests involving the authenticated account";
    private const string PseudoReviewRequestedScopeLabel = "Pull requests requesting review from the authenticated account";
    private const string PseudoAuthoredScopeLabel = "Pull requests created by the authenticated account";
    private const string PseudoAssignedScopeLabel = "Pull requests assigned to the authenticated account";
    private const string PseudoOpenStateLabel = "Currently open pull requests involving the authenticated account";
    private const string PseudoClosedStateLabel = "Previously closed pull requests involving the authenticated account";
    private const string PseudoAllStateLabel = "All pull requests involving the authenticated account";
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private bool _syncingFilterControls;
    private ListViewScrollAnchor? _pendingRefreshAnchor;
    private long _selectionRenderGeneration;
    private bool _pointerSelectionInProgress;
    private long _readinessRenderGeneration;
    private bool _performanceReadinessCommitted;
    private ProductPerformanceScrollProbe? _performanceScrollProbe;

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
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _performanceReadinessCommitted = false;
        Interlocked.Increment(ref _readinessRenderGeneration);
        UiTaskGuard.Run(async () =>
        {
            AttachPerformanceScrollProbe();
            if (_initialized)
            {
                SchedulePerformanceReadinessAfterRender();
                return;
            }

            _initialized = true;
            UpdatePullRequestFilterLayout(PullRequestFilterHost.ActualWidth);
            try
            {
                await ViewModel.InitializeAsync();
                SchedulePerformanceReadinessAfterRender();
                ApplyPseudoLongLabelsForAutomation();
                UpdatePullRequestFilterLayout(PullRequestFilterHost.ActualWidth);
                UpdatePaneButtonVisibility();
                MaybeOpenInitialPullRequestListDrawer();
            }
            catch (Exception ex)
            {
                JitHub.WinUI.App.LogHandledException(ex, "ui-my-pull-requests-page-initialize");
            }
        }, "ui-my-pull-requests-page");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _selectionRenderGeneration);
        Interlocked.Increment(ref _readinessRenderGeneration);
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
    }

    private void AttachPerformanceScrollProbe()
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = ProductPerformanceReadiness.IsEnabled &&
            FindDescendant<ScrollViewer>(PullRequestsList) is ScrollViewer scrollViewer
                ? ProductPerformanceScrollProbe.TryStart(PullRequestsList, scrollViewer)
                : null;
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

    private void CommitPerformanceReadiness() =>
        ProductPerformanceReadiness.CommitRoute(
            "my_pull_requests",
            ProductPerformanceReadiness.CountIdentity(ViewModel.Items.Count));

    private void SchedulePerformanceReadinessAfterRender()
    {
        if (!ProductPerformanceReadiness.IsEnabled || _performanceReadinessCommitted)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _readinessRenderGeneration);
        ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
            this,
            () => IsLoaded &&
                generation == Volatile.Read(ref _readinessRenderGeneration) &&
                !_performanceReadinessCommitted,
            static () => true,
            () =>
            {
                _performanceReadinessCommitted = true;
                CommitPerformanceReadiness();
            });
    }

    private void PullRequestScopeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(PullRequestScopeSegmented.SelectedIndex, PullRequestStateSegmented.SelectedIndex);
        ViewModel.SetPullRequestFilter(ToPullRequestFilter(PullRequestScopeSegmented.SelectedIndex));
    }

    private void PullRequestStateSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(PullRequestScopeSegmented.SelectedIndex, PullRequestStateSegmented.SelectedIndex);
        GitHubMeWorkItemState state = PullRequestStateSegmented.SelectedIndex switch
        {
            1 => GitHubMeWorkItemState.Closed,
            2 => GitHubMeWorkItemState.All,
            _ => GitHubMeWorkItemState.Open
        };
        ViewModel.SetWorkItemState(state);
    }

    private void PullRequestScopeCompactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(PullRequestScopeCompactPicker.SelectedIndex, PullRequestStateCompactPicker.SelectedIndex);
        ViewModel.SetPullRequestFilter(ToPullRequestFilter(PullRequestScopeCompactPicker.SelectedIndex));
    }

    private void PullRequestStateCompactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingFilterControls)
        {
            return;
        }

        SyncFilterSelection(PullRequestScopeCompactPicker.SelectedIndex, PullRequestStateCompactPicker.SelectedIndex);
        ViewModel.SetWorkItemState(PullRequestStateCompactPicker.SelectedIndex switch
        {
            1 => GitHubMeWorkItemState.Closed,
            2 => GitHubMeWorkItemState.All,
            _ => GitHubMeWorkItemState.Open
        });
    }

    private static GitHubMePullRequestFilter ToPullRequestFilter(int selectedIndex) => selectedIndex switch
    {
        1 => GitHubMePullRequestFilter.ReviewRequested,
        2 => GitHubMePullRequestFilter.Authored,
        3 => GitHubMePullRequestFilter.Assigned,
        _ => GitHubMePullRequestFilter.Involves
    };

    private void SyncFilterSelection(int scopeIndex, int stateIndex)
    {
        _syncingFilterControls = true;
        try
        {
            int normalizedScopeIndex = Math.Clamp(scopeIndex, 0, 3);
            int normalizedStateIndex = Math.Clamp(stateIndex, 0, 2);
            PullRequestScopeSegmented.SelectedIndex = normalizedScopeIndex;
            PullRequestScopeCompactPicker.SelectedIndex = normalizedScopeIndex;
            PullRequestStateSegmented.SelectedIndex = normalizedStateIndex;
            PullRequestStateCompactPicker.SelectedIndex = normalizedStateIndex;
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
        string[] scopeLabels =
        [
            InvolvedScopeSegment.Content?.ToString() ?? string.Empty,
            ReviewRequestedScopeSegment.Content?.ToString() ?? string.Empty,
            AuthoredScopeSegment.Content?.ToString() ?? string.Empty,
            AssignedScopeSegment.Content?.ToString() ?? string.Empty
        ];
        string[] stateLabels =
        [
            OpenStateSegment.Content?.ToString() ?? string.Empty,
            ClosedStateSegment.Content?.ToString() ?? string.Empty,
            AllStateSegment.Content?.ToString() ?? string.Empty
        ];
        bool useCompact = MyIssuesFilterLayoutPolicy.ShouldUseCompact(
            availableWidth,
            scopeLabels,
            stateLabels);
        ExpandedPullRequestFilters.Visibility = useCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactPullRequestFilters.Visibility = useCompact ? Visibility.Visible : Visibility.Collapsed;
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
        InvolvedScopeSegment.Content = InvolvedScopeCompactItem.Content = PseudoInvolvedScopeLabel;
        ReviewRequestedScopeSegment.Content = ReviewRequestedScopeCompactItem.Content = PseudoReviewRequestedScopeLabel;
        AuthoredScopeSegment.Content = AuthoredScopeCompactItem.Content = PseudoAuthoredScopeLabel;
        AssignedScopeSegment.Content = AssignedScopeCompactItem.Content = PseudoAssignedScopeLabel;
        OpenStateSegment.Content = OpenStateCompactItem.Content = PseudoOpenStateLabel;
        ClosedStateSegment.Content = ClosedStateCompactItem.Content = PseudoClosedStateLabel;
        AllStateSegment.Content = AllStateCompactItem.Content = PseudoAllStateLabel;
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
        if (e.ClickedItem is MeWorkItemViewItem item && PullRequestsWorkspace.IsLeadingDrawerOpen)
        {
            ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(PullRequestsList, GetPullRequestItemKey);
            PullRequestsWorkspace.CloseDrawer();
            anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
        }
    }

    private void PullRequestsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pointerSelectionInProgress ||
            PullRequestsList.SelectedItem is not MeWorkItemViewItem item)
        {
            return;
        }

        long generation = BeginPullRequestTraversal(item);
        PrimePullRequestSelection(item);
        ProductPerformanceReadiness.RecordTraversalStage("my_pull_requests.selection.primed");
        ScheduleSelectedPullRequestCommit(item, generation);
        SchedulePullRequestSelection(item, generation);
    }

    private void PullRequestListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        MeWorkItemViewItem? item = sender switch
        {
            ListViewItem { Content: MeWorkItemViewItem containerItem } => containerItem,
            FrameworkElement { DataContext: MeWorkItemViewItem templateItem } => templateItem,
            _ => null
        };
        if (e.Handled ||
            item is null ||
            sender is not UIElement pointerRoot ||
            e.GetCurrentPoint(pointerRoot).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        long generation = BeginPullRequestTraversal(item);
        ProductPerformanceReadiness.RecordTraversalStage("my_pull_requests.pointer.selected");
        PrimePullRequestSelection(item);
        ProductPerformanceReadiness.RecordTraversalStage("my_pull_requests.selection.primed");
        ScheduleSelectedPullRequestCommit(item, generation);

        _pointerSelectionInProgress = true;
        try
        {
            PullRequestsList.SelectedItem = item;
        }
        finally
        {
            _pointerSelectionInProgress = false;
        }

        ProductPerformanceReadiness.RecordTraversalStage("my_pull_requests.list.selected");
        e.Handled = true;
        SchedulePullRequestSelection(item, generation, focusSelection: true);
        ProductPerformanceReadiness.RecordTraversalStage("my_pull_requests.hydration.scheduled");

        if (PullRequestsWorkspace.IsLeadingDrawerOpen)
        {
            ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(PullRequestsList, GetPullRequestItemKey);
            PullRequestsWorkspace.CloseDrawer();
            anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
        }
    }

    private long BeginPullRequestTraversal(MeWorkItemViewItem item)
    {
        long generation = Interlocked.Increment(ref _selectionRenderGeneration);
        ProductPerformanceReadiness.BeginTraversal(
            "my_pull_requests",
            item.AutomationId,
            "my_pull_requests");
        return generation;
    }

    private void PrimePullRequestSelection(MeWorkItemViewItem item)
    {
        if (!string.Equals(MyPullRequestsDetailTitleText.Text, item.Title, StringComparison.Ordinal))
        {
            MyPullRequestsDetailTitleText.Text = item.Title;
        }
    }

    private void ScheduleSelectedPullRequestCommit(
        MeWorkItemViewItem item,
        long generation)
    {
        if (!ProductPerformanceReadiness.IsEnabled)
        {
            return;
        }

        ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
            this,
            () => IsLoaded &&
                generation == Volatile.Read(ref _selectionRenderGeneration) &&
                ReferenceEquals(PullRequestsList.SelectedItem, item),
            () => string.Equals(
                MyPullRequestsDetailTitleText.Text,
                item.Title,
                StringComparison.Ordinal),
            () =>
            {
                ProductPerformanceReadiness.RecordTraversalStage("my_pull_requests.render.committed");
                ProductPerformanceReadiness.CommitTraversal(
                    "my_pull_requests",
                    item.AutomationId);
            });
    }

    private void SchedulePullRequestSelection(
        MeWorkItemViewItem item,
        long generation,
        bool focusSelection = false)
    {
        DeferredFrameAction.Schedule(
            this,
            () => IsLoaded &&
                generation == Volatile.Read(ref _selectionRenderGeneration) &&
                ReferenceEquals(PullRequestsList.SelectedItem, item),
            () =>
            {
                ViewModel.SelectedItem = item;
                if (focusSelection && PullRequestsList.ContainerFromItem(item) is Control container)
                {
                    container.Focus(FocusState.Pointer);
                }
            });
    }

    private void PullRequestsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        container.RemoveHandler(
            PointerPressedEvent,
            new PointerEventHandler(PullRequestListItem_PointerPressed));
        if (args.InRecycleQueue)
        {
            return;
        }

        container.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(PullRequestListItem_PointerPressed),
            handledEventsToo: true);
        if (args.Item is MeWorkItemViewItem item)
        {
            AutomationProperties.SetAutomationId(container, item.AutomationId);
            AutomationProperties.SetName(container, item.AutomationName);
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
        SchedulePerformanceReadinessAfterRender();
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
