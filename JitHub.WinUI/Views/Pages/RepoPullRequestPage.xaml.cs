using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Dialogs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoPullRequestPage : Page
{
    private const string ReplyIdentityAutomationScenario = "pr-reply-identities";
    private const double ShyHeaderStartOffset = 56;
    private const double ShyHeaderRestoreOffset = 8;
    private const double ShyHeaderRevealTravel = 64;
    private const double ShyHeaderRehideTravel = 24;
    private const double ScrollDirectionEpsilon = 0.5;
    private const double CompactShyHeaderContentInset = 104;
    private static readonly TimeSpan ShyHeaderForwardDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ShyHeaderReverseDuration = TimeSpan.FromMilliseconds(220);
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private CancellationTokenSource? _searchDebounce;
    private ProductPerformanceScrollProbe? _performanceScrollProbe;
    private int _selectionRenderGeneration;
    private bool _pointerSelectionInProgress;
    private bool _initializingFilterFlyout;
    private int? _pendingPointerHydrationNumber;
    private readonly Dictionary<ScrollViewer, long> _sectionScrollViewers = [];
    private readonly TransitionHelper _headerTransition;
    private ScrollViewer? _activeShyHeaderScrollViewer;
    private double _lastShyHeaderScrollOffset;
    private double _upwardRevealTravel;
    private double _downwardRehideTravel;
    private bool _headerRevealedByUpwardScroll;
    private bool _isScrollHeaderShy;
    private bool _isDetailHeaderShy;
    private bool _synchronizingSectionSelection;
    private int _headerTransitionGeneration;

    public RepoPullRequestPageViewModel ViewModel { get; }

    public RepoPullRequestPage()
    {
        ViewModel = ((App)Application.Current).GetService<RepoPullRequestPageViewModel>();
        InitializeComponent();
        _headerTransition = new TransitionHelper
        {
            Source = PullRequestExpandedHeaderSurface,
            Target = PullRequestShyHeaderSurface,
            Duration = ShyHeaderForwardDuration,
            ReverseDuration = ShyHeaderReverseDuration,
            SourceToggleMethod = VisualStateToggleMethod.ByVisibility,
            TargetToggleMethod = VisualStateToggleMethod.ByVisibility,
            Configs =
            [
                new TransitionConfig { Id = "PullRequestHeaderSurface", ScaleMode = ScaleMode.ScaleY, EnableClipAnimation = true },
                new TransitionConfig { Id = "PullRequestTitle" },
                new TransitionConfig { Id = "PullRequestSectionSelector", ScaleMode = ScaleMode.ScaleX, EnableClipAnimation = true },
                new TransitionConfig { Id = "PullRequestListButton" },
                new TransitionConfig { Id = "PullRequestActions" }
            ]
        };
        DataContext = ViewModel;
        PullRequestContentScrollViewer.Loaded += PullRequestContentScrollViewer_Loaded;
        PullRequestContentScrollViewer.Unloaded += PullRequestContentScrollViewer_Unloaded;
        PullRequestDetailHost.SizeChanged += PullRequestDetailHost_SizeChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _initialized = false;
        _openedInitialListDrawer = false;
        PullRequestPageNavArg? arg = e.Parameter as PullRequestPageNavArg;
        bool isReplyIdentityAutomationScenario = string.Equals(
            Program.CurrentLaunchOptions.Scenario,
            ReplyIdentityAutomationScenario,
            StringComparison.OrdinalIgnoreCase);
        if (isReplyIdentityAutomationScenario)
        {
            PullRequestSectionSegmented.SelectedIndex = 3;
            PullRequestSectionComboBox.SelectedIndex = 3;
            PullRequestShySectionComboBox.SelectedIndex = 3;
            ViewModel.SetSection(PullRequestWorkspaceSection.Reviews);
        }

        await ViewModel.InitializeAsync(arg);
        if (DialogMatrixAutomationScenario.IsEnabled)
        {
            bool hasSelection = ViewModel.SelectedPullRequest is not null;
            ViewModel.CanEditPullRequest = hasSelection;
            ViewModel.CanManagePullRequestMetadata = hasSelection;
            ViewModel.CanReactToPullRequest = hasSelection;
            ViewModel.CanSubmitReviewComment = hasSelection;
            ViewModel.CanApprovePullRequest = hasSelection;
            ViewModel.CanRequestPullRequestChanges = hasSelection;
            ViewModel.IsMergeEnabled = hasSelection;
            ViewModel.CanMergeWithMergeCommit = hasSelection;
            ViewModel.CanMergeWithSquash = hasSelection;
            ViewModel.CanMergeWithRebase = hasSelection;
            ViewModel.ArePullRequestActionsEnabled = hasSelection;
        }
        ProductPerformanceReadiness.CommitRoute(
            "repo_pull_requests",
            $"{ProductPerformanceReadiness.CountIdentity(ViewModel.PullRequests.Count)};selected={ViewModel.SelectedPullRequest?.Id ?? 0}");
        if (isReplyIdentityAutomationScenario)
        {
            PullRequestSectionSegmented.SelectedIndex = 3;
            PullRequestSectionComboBox.SelectedIndex = 3;
            PullRequestShySectionComboBox.SelectedIndex = 3;
            ViewModel.SetSection(PullRequestWorkspaceSection.Reviews);
        }

        _initialized = true;
        UpdatePaneButtonVisibility();
        MaybeOpenInitialPullRequestListDrawer();
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AttachPerformanceScrollProbe);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _initialized = false;
        _selectionRenderGeneration++;
        _pendingPointerHydrationNumber = null;
        ViewModel.CancelPredictivePrefetches();
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        foreach ((ScrollViewer scrollViewer, long callbackToken) in _sectionScrollViewers)
        {
            scrollViewer.ViewChanged -= PullRequestSectionScrollViewer_ViewChanged;
            scrollViewer.UnregisterPropertyChangedCallback(ScrollViewer.VerticalOffsetProperty, callbackToken);
        }

        _sectionScrollViewers.Clear();
        _activeShyHeaderScrollViewer = null;
        base.OnNavigatedFrom(e);
    }

    private void PullRequestContentScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AttachPerformanceScrollProbe);
    }

    private void AttachPerformanceScrollProbe()
    {
        if (!_initialized || !PullRequestContentScrollViewer.IsLoaded)
        {
            return;
        }

        _performanceScrollProbe?.Dispose();
        IReadOnlyList<ScrollViewer> scrollViewers = FindScrollViewers(PullRequestContentScrollViewer);
        foreach (ScrollViewer candidate in scrollViewers)
        {
            AttachShyHeaderScrollViewer(candidate);
        }

        if (scrollViewers.FirstOrDefault() is ScrollViewer scrollViewer)
        {
            _performanceScrollProbe = ProductPerformanceScrollProbe.TryStart(PullRequestContentHost, scrollViewer);
        }
        else
        {
            _performanceScrollProbe = null;
        }
    }

    private void PullRequestContentScrollViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
        DetachShyHeaderScrollViewer(PullRequestContentScrollViewer);
    }

    private void PullRequestScrollableSection_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject owner)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_initialized && owner is FrameworkElement { IsLoaded: true })
                {
                    AttachShyHeaderScrollSources(owner);
                }
            });
    }

    private void PullRequestScrollableSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not DependencyObject owner)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_initialized && owner is FrameworkElement { IsLoaded: true })
                {
                    AttachShyHeaderScrollSources(owner);
                }
            });
    }

    private void PullRequestScrollableSection_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject owner)
        {
            DetachShyHeaderScrollViewer(owner);
        }
    }

    private void AttachShyHeaderScrollSources(DependencyObject owner)
    {
        IReadOnlyList<ScrollViewer> scrollViewers = FindScrollViewers(owner);
        foreach (ScrollViewer scrollViewer in scrollViewers)
        {
            AttachShyHeaderScrollViewer(scrollViewer);
        }

        ScrollViewer? primary = scrollViewers.FirstOrDefault(IsShyHeaderScrollCandidate);
        if (primary is not null)
        {
            ActivateShyHeaderScrollViewer(primary);
        }
    }

    private void AttachShyHeaderScrollViewer(ScrollViewer scrollViewer)
    {
        if (!_sectionScrollViewers.ContainsKey(scrollViewer))
        {
            scrollViewer.ViewChanged += PullRequestSectionScrollViewer_ViewChanged;
            long callbackToken = scrollViewer.RegisterPropertyChangedCallback(
                ScrollViewer.VerticalOffsetProperty,
                PullRequestSectionScrollViewer_VerticalOffsetChanged);
            _sectionScrollViewers.Add(scrollViewer, callbackToken);
        }
    }

    private void DetachShyHeaderScrollViewer(DependencyObject owner)
    {
        foreach (ScrollViewer scrollViewer in FindScrollViewers(owner))
        {
            if (_sectionScrollViewers.Remove(scrollViewer, out long callbackToken))
            {
                scrollViewer.ViewChanged -= PullRequestSectionScrollViewer_ViewChanged;
                scrollViewer.UnregisterPropertyChangedCallback(ScrollViewer.VerticalOffsetProperty, callbackToken);
            }
        }
    }

    private void PullRequestSectionScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (PullRequestsWorkspace.State?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact ||
            sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        UpdateDetailHeaderForScrollViewer(scrollViewer);
    }

    private void PullRequestSectionScrollViewer_VerticalOffsetChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (PullRequestsWorkspace.State?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact ||
            sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        UpdateDetailHeaderForScrollViewer(scrollViewer);
    }

    private static bool IsShyHeaderScrollCandidate(ScrollViewer scrollViewer) =>
        scrollViewer.IsLoaded &&
        scrollViewer.ActualHeight >= 96 &&
        scrollViewer.ScrollableHeight > 0;

    private void ActivateShyHeaderScrollViewer(ScrollViewer scrollViewer)
    {
        if (ReferenceEquals(_activeShyHeaderScrollViewer, scrollViewer))
        {
            return;
        }

        _activeShyHeaderScrollViewer = scrollViewer;
        _lastShyHeaderScrollOffset = scrollViewer.VerticalOffset;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        _headerRevealedByUpwardScroll = false;
        _isScrollHeaderShy = scrollViewer.VerticalOffset >= ShyHeaderStartOffset;
        SetDetailHeaderShy(IsCompactWorkspace || _isScrollHeaderShy, animate: false);
    }

    private void UpdateDetailHeaderForScrollViewer(ScrollViewer scrollViewer)
    {
        if (!IsShyHeaderScrollCandidate(scrollViewer))
        {
            return;
        }

        if (!ReferenceEquals(_activeShyHeaderScrollViewer, scrollViewer))
        {
            ActivateShyHeaderScrollViewer(scrollViewer);
            return;
        }

        double offset = scrollViewer.VerticalOffset;
        double delta = offset - _lastShyHeaderScrollOffset;
        _lastShyHeaderScrollOffset = offset;

        if (_isScrollHeaderShy)
        {
            if (offset <= ShyHeaderRestoreOffset)
            {
                RevealScrollHeader(revealedByUpwardScroll: false);
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _upwardRevealTravel += -delta;
                if (_upwardRevealTravel >= ShyHeaderRevealTravel)
                {
                    RevealScrollHeader(revealedByUpwardScroll: true);
                }
            }
            else if (delta > ScrollDirectionEpsilon)
            {
                _upwardRevealTravel = 0;
            }

            return;
        }

        if (offset <= ShyHeaderRestoreOffset)
        {
            _headerRevealedByUpwardScroll = false;
            _downwardRehideTravel = 0;
        }
        else if (_headerRevealedByUpwardScroll)
        {
            if (delta > ScrollDirectionEpsilon)
            {
                _downwardRehideTravel += delta;
                if (_downwardRehideTravel >= ShyHeaderRehideTravel)
                {
                    HideScrollHeader();
                }
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _downwardRehideTravel = 0;
            }
        }
        else if (offset >= ShyHeaderStartOffset)
        {
            HideScrollHeader();
        }
    }

    private void RevealScrollHeader(bool revealedByUpwardScroll)
    {
        _isScrollHeaderShy = false;
        _headerRevealedByUpwardScroll = revealedByUpwardScroll;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        SetDetailHeaderShy(IsCompactWorkspace, animate: true);
    }

    private void HideScrollHeader()
    {
        _isScrollHeaderShy = true;
        _headerRevealedByUpwardScroll = false;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        SetDetailHeaderShy(true, animate: true);
    }

    private void PullRequestDetailHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_initialized)
        {
            UpdatePaneButtonVisibility();
        }
    }

    private static IReadOnlyList<ScrollViewer> FindScrollViewers(DependencyObject root)
    {
        List<ScrollViewer> candidates = [];
        CollectScrollViewers(root, candidates);
        return candidates
            .OrderByDescending(static scrollViewer => scrollViewer.ScrollableHeight)
            .ThenByDescending(static scrollViewer => scrollViewer.ActualHeight)
            .ToArray();
    }

    private static void CollectScrollViewers(DependencyObject root, ICollection<ScrollViewer> candidates)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer)
            {
                candidates.Add(scrollViewer);
            }

            CollectScrollViewers(child, candidates);
        }
    }

    private async void PullRequestStateSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        int selectedIndex = Math.Clamp(PullRequestStateSegmented.SelectedIndex, 0, ViewModel.StateOptions.Count - 1);
        ViewModel.SelectedStateOption = ViewModel.StateOptions[selectedIndex];
        await ViewModel.ApplyFiltersAsync();
    }

    private async void PullRequestFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _initializingFilterFlyout)
        {
            return;
        }

        ViewModel.SelectedSortOption = GetSelectedOption(ViewModel.SortOptions, PullRequestSortComboBox.SelectedIndex);
        ViewModel.SelectedDirectionOption = GetSelectedOption(ViewModel.DirectionOptions, PullRequestDirectionComboBox.SelectedIndex);
        await ViewModel.ApplyFiltersAsync();
    }

    private void PullRequestFiltersFlyout_Opened(object sender, object e)
    {
        _initializingFilterFlyout = true;
        try
        {
            PullRequestSortComboBox.SelectedIndex = GetSelectedIndex(ViewModel.SortOptions, ViewModel.SelectedSortOption);
            PullRequestDirectionComboBox.SelectedIndex = GetSelectedIndex(ViewModel.DirectionOptions, ViewModel.SelectedDirectionOption);
        }
        finally
        {
            _initializingFilterFlyout = false;
        }
    }

    private static QueryOption? GetSelectedOption(IReadOnlyList<QueryOption> options, int index) =>
        index >= 0 && index < options.Count ? options[index] : null;

    private static int GetSelectedIndex(IReadOnlyList<QueryOption> options, QueryOption? selected)
    {
        if (selected is null)
        {
            return 0;
        }

        for (int index = 0; index < options.Count; index++)
        {
            if (options[index] == selected)
            {
                return index;
            }
        }

        return 0;
    }

    private async void PullRequestSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ViewModel.SearchText = PullRequestSearchBox.Text;
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

    private void PullRequestsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GitHubPullRequest pullRequest)
        {
            if (PullRequestsWorkspace.IsLeadingDrawerOpen)
            {
                ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(PullRequestsList);
                PullRequestsWorkspace.CloseDrawer();
                anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
            }

            if (_pendingPointerHydrationNumber == pullRequest.Number)
            {
                return;
            }

            ViewModel.SelectedPullRequest = pullRequest;
        }
    }

    private void PullRequestsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: GitHubPullRequest pullRequest })
        {
            return;
        }

        if (_pointerSelectionInProgress)
        {
            return;
        }

        int generation = BeginPullRequestTraversal(pullRequest);
        PrimePullRequestSelection(pullRequest);
        SchedulePullRequestSelection(pullRequest, generation);
        if (ProductPerformanceReadiness.IsEnabled)
        {
            SchedulePullRequestTraversalCommit(pullRequest, generation);
        }
    }

    private void PullRequestListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        GitHubPullRequest? pullRequest = sender switch
        {
            ListViewItem { Content: GitHubPullRequest item } => item,
            FrameworkElement { DataContext: GitHubPullRequest item } => item,
            _ => null
        };
        if (pullRequest is null ||
            sender is not UIElement pointerRoot ||
            e.GetCurrentPoint(pointerRoot).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        int generation = BeginPullRequestTraversal(pullRequest);
        ProductPerformanceReadiness.RecordTraversalStage("repo_pull_requests.pointer.selected");
        PrimePullRequestSelection(pullRequest);
        _pendingPointerHydrationNumber = pullRequest.Number;
        _pointerSelectionInProgress = true;
        try
        {
            PullRequestsList.SelectedItem = pullRequest;
        }
        finally
        {
            _pointerSelectionInProgress = false;
        }

        e.Handled = true;
        SchedulePullRequestSelection(pullRequest, generation, focusSelection: true);
        if (ProductPerformanceReadiness.IsEnabled)
        {
            SchedulePullRequestTraversalCommit(pullRequest, generation);
        }
        if (PullRequestsWorkspace.IsLeadingDrawerOpen)
        {
            ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(PullRequestsList);
            PullRequestsWorkspace.CloseDrawer();
            anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
        }
    }

    private void PrimePullRequestSelection(GitHubPullRequest pullRequest)
    {
        if (!string.Equals(PullRequestDetailTitle.Text, pullRequest.Title, StringComparison.Ordinal))
        {
            PullRequestDetailTitle.Text = pullRequest.Title;
        }

        if (!string.Equals(PullRequestShyDetailTitle.Text, pullRequest.Title, StringComparison.Ordinal))
        {
            PullRequestShyDetailTitle.Text = pullRequest.Title;
        }
    }

    private int BeginPullRequestTraversal(GitHubPullRequest pullRequest)
    {
        int generation = ++_selectionRenderGeneration;
        if (!ProductPerformanceReadiness.IsEnabled)
        {
            return generation;
        }

        ProductPerformanceReadiness.BeginTraversal(
            "repo_pull_requests",
            pullRequest.AutomationId,
            "repo_pull_requests");
        return generation;
    }

    private void SchedulePullRequestTraversalCommit(
        GitHubPullRequest pullRequest,
        int generation)
    {
        ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
            this,
            () => generation == _selectionRenderGeneration &&
                IsLoaded &&
                PullRequestsList.SelectedItem is GitHubPullRequest selected &&
                selected.Number == pullRequest.Number,
            () =>
                string.Equals(
                    PullRequestDetailTitle.Text,
                    pullRequest.Title,
                    StringComparison.Ordinal),
            () => ProductPerformanceReadiness.CommitTraversal(
                "repo_pull_requests",
                pullRequest.AutomationId));
    }

    private void SchedulePullRequestSelection(
        GitHubPullRequest pullRequest,
        int generation,
        bool focusSelection = false)
    {
        DeferredFrameAction.Schedule(
            this,
            () => generation == _selectionRenderGeneration &&
                IsLoaded &&
                PullRequestsList.SelectedItem is GitHubPullRequest current &&
                current.Number == pullRequest.Number,
            () =>
            {
                _pendingPointerHydrationNumber = null;
                ViewModel.SelectedPullRequest = pullRequest;
                if (focusSelection &&
                    PullRequestsList.ContainerFromItem(pullRequest) is Control container)
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

        container.GotFocus -= PullRequestListItemContainer_GotFocus;
        container.RemoveHandler(
            PointerPressedEvent,
            new PointerEventHandler(PullRequestListItem_PointerPressed));
        if (args.InRecycleQueue)
        {
            return;
        }

        container.GotFocus += PullRequestListItemContainer_GotFocus;
        container.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(PullRequestListItem_PointerPressed),
            handledEventsToo: true);
        if (args.Item is GitHubPullRequest pullRequest)
        {
            AutomationProperties.SetAutomationId(container, pullRequest.AutomationId);
            AutomationProperties.SetName(container, pullRequest.AutomationName);
        }
    }

    private void PullRequestDetailList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            AutomationProperties.SetAutomationId(container, string.Empty);
            AutomationProperties.SetName(container, string.Empty);
            return;
        }

        (string? automationId, string? automationName) = args.Item switch
        {
            GitHubIssueComment comment =>
                (comment.MarkdownAutomationId, $"Pull request comment by {comment.AuthorDisplayName}"),
            GitHubCommit commit => (commit.AutomationId, commit.AutomationName),
            RepoPullRequestPageViewModel.PullRequestReviewItem review =>
                (review.AutomationId, $"Review by {review.ReviewerLogin}: {review.StateText}"),
            GitHubIssueEvent timelineEvent =>
                (timelineEvent.ActorAutomationId, $"{timelineEvent.Summary}. {timelineEvent.MetaText}"),
            _ => (null, null)
        };

        AutomationProperties.SetAutomationId(container, automationId ?? string.Empty);
        AutomationProperties.SetName(container, automationName ?? string.Empty);
    }

    private void PullRequestListItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitHubPullRequest pullRequest })
        {
            ViewModel.PrefetchPullRequest(pullRequest, PullRequestPrefetchReason.Hover);
        }
    }

    private void PullRequestListItemContainer_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem { Content: GitHubPullRequest pullRequest })
        {
            ViewModel.PrefetchPullRequest(pullRequest, PullRequestPrefetchReason.Hover);
        }
    }

    private void PullRequestsWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState e)
    {
        UpdatePaneButtonVisibility();
        MaybeOpenInitialPullRequestListDrawer();
    }

    public void OpenPullRequestListPane()
        => PullRequestsWorkspace.OpenLeadingPane();

    public void OpenPullRequestInspectorPane()
        => PullRequestsWorkspace.OpenTrailingPane();

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenPullRequestListPane();

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenPullRequestInspectorPane();

    private void CloseWorkspaceDrawerButton_Click(object sender, RoutedEventArgs e)
        => PullRequestsWorkspace.CloseDrawer();

    private void UpdatePaneButtonVisibility()
    {
        AdaptiveWorkspaceState? state = PullRequestsWorkspace.State;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;
        Visibility listButtonVisibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoPullRequestsOpenListPaneButton.Visibility = listButtonVisibility;
        RepoPullRequestsShyOpenListPaneButton.Visibility = listButtonVisibility;
        RepoPullRequestsCloseListPaneButton.Visibility = isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        Visibility inspectorButtonVisibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoPullRequestsOpenInspectorPaneButton.Visibility = inspectorButtonVisibility;
        RepoPullRequestsShyOpenInspectorPaneButton.Visibility = inspectorButtonVisibility;
        RepoPullRequestsCloseInspectorPaneButton.Visibility = isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetDetailHeaderShy(IsCompactWorkspace || _isScrollHeaderShy, animate: false);
        Thickness sectionPadding = IsCompactWorkspace
            ? new Thickness(12, CompactShyHeaderContentInset, 12, 12)
            : new Thickness(18);
        PullRequestContentScrollViewer.Padding = sectionPadding;
        if (PullRequestCommitsSection is not null)
        {
            PullRequestCommitsSection.Padding = sectionPadding;
        }

        if (PullRequestReviewsSection is not null)
        {
            PullRequestReviewsSection.Padding = sectionPadding;
        }

        if (PullRequestTimelineSection is not null)
        {
            PullRequestTimelineSection.Padding = sectionPadding;
        }

        if (PullRequestFilesSection is not null)
        {
            PullRequestFilesSection.Padding = IsCompactWorkspace
                ? new Thickness(12, CompactShyHeaderContentInset, 12, 12)
                : new Thickness(18, 12, 18, 18);
        }
        PullRequestCommentFormHost.Padding = IsCompactWorkspace
            ? new Thickness(10, 6, 10, 6)
            : new Thickness(12, 8, 12, 8);
    }

    private bool IsCompactWorkspace =>
        PullRequestsWorkspace.State?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact;

    private void SetDetailHeaderShy(bool isShy, bool animate)
    {
        ApplyDetailHeaderChrome();
        if (_isDetailHeaderShy == isShy)
        {
            return;
        }

        _isDetailHeaderShy = isShy;
        int generation = ++_headerTransitionGeneration;
        if (!animate || !PullRequestExpandedHeaderSurface.IsLoaded || !AreAnimationsEnabled())
        {
            _headerTransition.Reset(toInitialState: !isShy);
            PullRequestDetailLayout.UpdateLayout();
            ResetContentReflow();
            return;
        }

        _ = AnimateDetailHeaderAsync(isShy, generation);
    }

    private async Task AnimateDetailHeaderAsync(bool isShy, int generation)
    {
        try
        {
            FrameworkElement visibleContent = GetVisibleDetailContentSurface();
            bool reverseFromSettledShyState =
                !isShy && _headerTransition.IsTargetState && !_headerTransition.IsAnimating;
            double previousContentTop = reverseFromSettledShyState
                ? GetElementTop(visibleContent, PullRequestDetailLayout)
                : 0;
            Task headerAnimation = isShy
                ? _headerTransition.StartAsync(forceUpdateAnimatedElements: true)
                : _headerTransition.ReverseAsync(forceUpdateAnimatedElements: true);

            PullRequestDetailLayout.UpdateLayout();
            if (isShy)
            {
                double reclaimedHeight = Math.Max(
                    0,
                    PullRequestExpandedHeaderSurface.ActualHeight - PullRequestShyHeaderSurface.ActualHeight);
                AnimateContentReflow(
                    new Vector3(0, (float)-reclaimedHeight, 0),
                    ShyHeaderForwardDuration);
            }
            else if (reverseFromSettledShyState)
            {
                double expandedContentTop = GetElementTop(visibleContent, PullRequestDetailLayout);
                SetContentReflowImmediately(new Vector3(0, (float)(previousContentTop - expandedContentTop), 0));
                AnimateContentReflow(Vector3.Zero, ShyHeaderReverseDuration);
            }

            else
            {
                AnimateContentReflow(Vector3.Zero, ShyHeaderReverseDuration);
            }

            await headerAnimation;
            if (generation != _headerTransitionGeneration)
            {
                return;
            }

            PullRequestDetailLayout.UpdateLayout();
            ResetContentReflow();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (generation != _headerTransitionGeneration)
        {
        }
        catch when (generation == _headerTransitionGeneration)
        {
            _headerTransition.Reset(toInitialState: !isShy);
            PullRequestDetailLayout.UpdateLayout();
            ResetContentReflow();
        }
    }

    private FrameworkElement GetVisibleDetailContentSurface() =>
        PullRequestFilesSection is { IsLoaded: true, Visibility: Visibility.Visible }
            ? PullRequestFilesSection
            : PullRequestContentHost;

    private IReadOnlyList<UIElement> GetDetailContentSurfaces()
    {
        List<UIElement> surfaces = [PullRequestContentHost];
        if (PullRequestFilesSection is not null)
        {
            surfaces.Add(PullRequestFilesSection);
        }

        return surfaces;
    }

    private void AnimateContentReflow(Vector3 translation, TimeSpan duration)
    {
        foreach (UIElement surface in GetDetailContentSurfaces())
        {
            surface.TranslationTransition = new Vector3Transition
            {
                Components = Vector3TransitionComponents.Y,
                Duration = duration
            };
            surface.Translation = translation;
        }
    }

    private void SetContentReflowImmediately(Vector3 translation)
    {
        foreach (UIElement surface in GetDetailContentSurfaces())
        {
            surface.TranslationTransition = null;
            surface.Translation = translation;
        }
    }

    private void ResetContentReflow() =>
        SetContentReflowImmediately(Vector3.Zero);

    private static double GetElementTop(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformPoint(new Windows.Foundation.Point()).Y;

    private void ApplyDetailHeaderChrome()
    {
        bool useCompactActionOverflow =
            PullRequestsWorkspace.State is { Mode: not AdaptiveWorkspaceMode.Wide };

        PullRequestSectionSegmented.Visibility = !IsCompactWorkspace
            ? Visibility.Visible
            : Visibility.Collapsed;
        PullRequestSectionComboBox.Visibility = IsCompactWorkspace
            ? Visibility.Visible
            : Visibility.Collapsed;
        PullRequestShySectionComboBox.Visibility = !IsCompactWorkspace
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoPullRequestsInlineActions.Visibility = useCompactActionOverflow
            ? Visibility.Collapsed
            : Visibility.Visible;
        RepoPullRequestsCompactActionsButton.Visibility = useCompactActionOverflow
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool AreAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            return false;
        }
    }

    private void MaybeOpenInitialPullRequestListDrawer()
    {
        if (_openedInitialListDrawer ||
            !_initialized ||
            ViewModel.HasSelectedPullRequest ||
            PullRequestsWorkspace.State is not { ShouldShowLeadingPaneButton: true })
        {
            return;
        }

        _openedInitialListDrawer = true;
        PullRequestsWorkspace.OpenLeadingPane();
    }

    private void PullRequestSectionSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePullRequestSectionSelection(PullRequestSectionSegmented.SelectedIndex);
    }

    private void PullRequestSectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePullRequestSectionSelection(PullRequestSectionComboBox.SelectedIndex);
    }

    private void PullRequestShySectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePullRequestSectionSelection(PullRequestShySectionComboBox.SelectedIndex);
    }

    private void UpdatePullRequestSectionSelection(int selectedIndex)
    {
        if (!_initialized || _synchronizingSectionSelection || selectedIndex < 0)
        {
            return;
        }

        _synchronizingSectionSelection = true;
        try
        {
            PullRequestSectionSegmented.SelectedIndex = selectedIndex;
            PullRequestSectionComboBox.SelectedIndex = selectedIndex;
            PullRequestShySectionComboBox.SelectedIndex = selectedIndex;
        }
        finally
        {
            _synchronizingSectionSelection = false;
        }

        ViewModel.SetSection(PullRequestSectionSelectionPolicy.FromIndex(selectedIndex));
        _activeShyHeaderScrollViewer = null;
        _lastShyHeaderScrollOffset = 0;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        _headerRevealedByUpwardScroll = false;
        _isScrollHeaderShy = false;
        SetDetailHeaderShy(IsCompactWorkspace, animate: false);
        _ = DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => AttachActiveSectionScrollSources(selectedIndex));
    }

    private void AttachActiveSectionScrollSources(int selectedIndex)
    {
        FrameworkElement? activeSection = selectedIndex switch
        {
            0 => PullRequestContentScrollViewer,
            1 => PullRequestFilesSection,
            2 => PullRequestCommitsSection,
            3 => PullRequestReviewsSection,
            4 => PullRequestTimelineSection,
            _ => null
        };

        if (_initialized && activeSection is { IsLoaded: true })
        {
            AttachShyHeaderScrollSources(activeSection);
        }
    }

    private async void TogglePullRequestStateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleSelectedPullRequestStateAsync();
    }

    private async void CommentButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddPullRequestCommentAsync();
        if (string.IsNullOrWhiteSpace(ViewModel.PullRequestCommentDraft))
        {
            PullRequestCommentFlyout.Hide();
        }
    }

    private void CompactCommentFlyout_Opened(object sender, object e)
    {
        _ = DispatcherQueue.TryEnqueue(() => PullRequestCompactCommentForm.FocusEditor());
    }

    private void CompactCommentFlyout_Closed(object sender, object e)
    {
        RepoPullRequestsOpenCompactCommentButton.Focus(FocusState.Programmatic);
    }

    private async void SubmitReviewButton_Click(object sender, RoutedEventArgs e)
    {
        RadioButton commentOption = CreateReviewDecisionOption(
            "RepoPullRequestsReviewDecisionComment",
            L("RepoPullRequests/Dialogs/Review/DecisionComment", "Comment"),
            ViewModel.CanSubmitReviewComment);
        RadioButton approveOption = CreateReviewDecisionOption(
            "RepoPullRequestsReviewDecisionApprove",
            L("RepoPullRequests/Dialogs/Review/DecisionApprove", "Approve"),
            ViewModel.CanApprovePullRequest);
        RadioButton requestChangesOption = CreateReviewDecisionOption(
            "RepoPullRequestsReviewDecisionRequestChanges",
            L("RepoPullRequests/Dialogs/Review/DecisionRequestChanges", "Request changes"),
            ViewModel.CanRequestPullRequestChanges);
        RadioButton? initialOption = new[] { commentOption, approveOption, requestChangesOption }
            .FirstOrDefault(option => option.IsEnabled);
        if (initialOption is null)
        {
            return;
        }

        initialOption.IsChecked = true;
        MarkdownForm reviewForm = new()
        {
            EditorHeight = 180,
            DocumentSource = ViewModel.PullRequestCommentMarkdownSource,
            Text = string.Empty
        };
        AutomationProperties.SetAutomationId(reviewForm, "RepoPullRequestsReviewBody");
        AutomationProperties.SetName(reviewForm, L("RepoPullRequests/Dialogs/Review/BodyAutomationName", "Pull request review body"));

        TextBlock validationText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsReviewValidationText");
        AutomationProperties.SetAutomationId(validationText, "RepoPullRequestsReviewValidationText");
        AutomationProperties.SetName(validationText, L("RepoPullRequests/Dialogs/Review/ValidationAutomationName", "Review validation message"));

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L("RepoPullRequests/Dialogs/Review/Title", "Submit review"),
            Content = new StackPanel
            {
                MaxWidth = 520,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = L("RepoPullRequests/Dialogs/Review/DecisionHeader", "Review decision"),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    },
                    commentOption,
                    approveOption,
                    requestChangesOption,
                    reviewForm,
                    validationText
                }
            },
            PrimaryButtonText = L("RepoPullRequests/Dialogs/Review/Primary", "Submit review"),
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsSubmitReviewDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Review/AutomationName", "Submit pull request review"));
        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                PullRequestReviewSubmission submission = CreateReviewSubmission(
                    commentOption,
                    approveOption,
                    reviewForm.Text);
                try
                {
                    PullRequestReviewSubmissionPolicy.Validate(submission);
                }
                catch (ArgumentException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Pull request review validation failed: {ex}");
                    return DialogMutationResult.Failure(L(
                        "RepoPullRequests/Dialogs/Review/CommentRequired",
                        "Enter a review comment before commenting or requesting changes."));
                }

                string previousStatus = ViewModel.StatusText;
                await ViewModel.SubmitPullRequestReviewAsync(submission.Decision, submission.Body);
                return ResolvePullRequestMutationResult(
                    previousStatus,
                    ViewModel.SelectedSection == PullRequestWorkspaceSection.Reviews,
                    "submitted");
            },
            validationText);
    }

    private static RadioButton CreateReviewDecisionOption(
        string automationId,
        string label,
        bool isEnabled)
    {
        RadioButton option = new()
        {
            Content = label,
            GroupName = "PullRequestReviewDecision",
            IsEnabled = isEnabled
        };
        AutomationProperties.SetAutomationId(option, automationId);
        AutomationProperties.SetName(option, label);
        return option;
    }

    private static PullRequestReviewSubmission CreateReviewSubmission(
        RadioButton commentOption,
        RadioButton approveOption,
        string body)
    {
        PullRequestReviewDecision decision = approveOption.IsChecked == true
            ? PullRequestReviewDecision.Approve
            : commentOption.IsChecked == true
                ? PullRequestReviewDecision.Comment
                : PullRequestReviewDecision.RequestChanges;
        return new PullRequestReviewSubmission(decision, body);
    }

    private async void ReviewReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RepoPullRequestPageViewModel.PullRequestReviewThreadItem thread })
        {
            await ViewModel.ReplyToReviewCommentAsync(thread);
        }
    }

    private async void CommentInteractionBar_ActionRequested(object? sender, CommentActionRequestedEventArgs e)
    {
        if (sender is not CommentInteractionBar bar)
        {
            return;
        }

        bool isPullRequestBody = e.TargetKind == CommentTargetKind.PullRequest;
        bool isReviewComment = e.TargetKind == CommentTargetKind.PullRequestReviewComment;
        switch (e.Action)
        {
            case CommentActionKind.ToggleReaction when !string.IsNullOrWhiteSpace(e.Value):
                if (isPullRequestBody)
                {
                    await ToggleSelectedPullRequestReactionAsync(e.Value);
                }
                else
                {
                    await TogglePullRequestCommentReactionAsync(e.TargetKind, e.TargetId, e.Value);
                }
                break;
            case CommentActionKind.QuoteReply:
                QuotePullRequestComment(e.TargetKind, e.TargetId, bar.Body);
                break;
            case CommentActionKind.CopyLink:
                PlatformHelper.CopyString(bar.HtmlUrl);
                break;
            case CommentActionKind.CopyMarkdown:
                PlatformHelper.CopyString(bar.Body);
                break;
            case CommentActionKind.Edit:
                if (isPullRequestBody)
                {
                    EditPullRequestButton_Click(bar, new RoutedEventArgs());
                }
                else
                {
                    await ShowPullRequestCommentEditDialogAsync(e.TargetId, bar.Body, isReviewComment);
                }
                break;
            case CommentActionKind.Hide when !string.IsNullOrWhiteSpace(e.Value):
                await ViewModel.SetPullRequestCommentMinimizedAsync(bar.NodeId, e.Value);
                break;
            case CommentActionKind.Unhide:
                await ViewModel.SetPullRequestCommentMinimizedAsync(bar.NodeId, classifier: null);
                break;
            case CommentActionKind.Delete:
                await ShowPullRequestCommentDeleteDialogAsync(e.TargetId, isReviewComment);
                break;
        }
    }

    private async Task ToggleSelectedPullRequestReactionAsync(string content)
    {
        IReadOnlyList<GitHubReaction>? reactions = await ViewModel.GetSelectedPullRequestReactionsAsync();
        if (reactions is null)
        {
            return;
        }

        Dictionary<string, long> viewerReactionIds = reactions
            .Where(reaction => string.Equals(reaction.User.Login, ViewModel.AuthenticatedLogin, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static reaction => reaction.Content, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> selected = viewerReactionIds.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.Add(content))
        {
            selected.Remove(content);
        }

        await ViewModel.ApplySelectedPullRequestReactionSelectionAsync(selected, viewerReactionIds);
    }

    private async Task TogglePullRequestCommentReactionAsync(
        CommentTargetKind targetKind,
        long commentId,
        string content)
    {
        IReadOnlyList<GitHubReaction>? reactions = targetKind == CommentTargetKind.PullRequestReviewComment
            ? await ViewModel.GetReviewCommentReactionsAsync(commentId)
            : await ViewModel.GetPullRequestCommentReactionsAsync(commentId);
        if (reactions is null)
        {
            return;
        }

        Dictionary<string, long> viewerReactionIds = reactions
            .Where(reaction => string.Equals(reaction.User.Login, ViewModel.AuthenticatedLogin, StringComparison.OrdinalIgnoreCase))
            .GroupBy(static reaction => reaction.Content, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        HashSet<string> selected = viewerReactionIds.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selected.Add(content))
        {
            selected.Remove(content);
        }

        if (targetKind == CommentTargetKind.PullRequestReviewComment)
        {
            await ViewModel.ApplyReviewCommentReactionSelectionAsync(commentId, selected, viewerReactionIds);
        }
        else
        {
            await ViewModel.ApplyPullRequestCommentReactionSelectionAsync(commentId, selected, viewerReactionIds);
        }
    }

    private void QuotePullRequestComment(CommentTargetKind targetKind, long commentId, string body)
    {
        if (targetKind == CommentTargetKind.PullRequestReviewComment)
        {
            RepoPullRequestPageViewModel.PullRequestReviewThreadItem? thread = ViewModel.PullRequestReviews
                .SelectMany(static review => review.Threads)
                .FirstOrDefault(item => item.CommentId == commentId || item.Replies.Any(reply => reply.Id == commentId));
            if (thread is not null)
            {
                thread.ReplyText = CommentMarkdownFormatter.AppendQuote(thread.ReplyText, body);
            }

            return;
        }

        ViewModel.PullRequestCommentDraft = CommentMarkdownFormatter.AppendQuote(
            ViewModel.PullRequestCommentDraft,
            body);
        PullRequestCommentFlyout.ShowAt(RepoPullRequestsOpenCompactCommentButton);
    }

    private async Task ShowPullRequestCommentEditDialogAsync(long commentId, string body, bool isReviewComment)
    {
        MarkdownForm form = new()
        {
            Text = body,
            EditorHeight = 420
        };
        AutomationProperties.SetAutomationId(form, $"RepoPullRequestsComment_{commentId}_EditForm");
        AutomationProperties.SetName(form, L("RepoPullRequests/Dialogs/CommentEdit/BodyAutomationName", "Comment Markdown"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("RepoPullRequestsCommentEditDialogError");
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L("RepoPullRequests/Dialogs/CommentEdit/Title", "Edit comment"),
            Content = AppDialogStyleCatalog.CreateContentPanel(form, errorText),
            PrimaryButtonText = L("Common/Save", "Save"),
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsCommentEditDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/CommentEdit/AutomationName", "Edit pull request comment"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () => await ViewModel.UpdatePullRequestCommentAsync(commentId, form.Text, isReviewComment)
                ? DialogMutationResult.Success()
                : DialogMutationResult.Failure(ViewModel.StatusText),
            errorText,
            layoutKind: AppDialogLayoutKind.Editor);
    }

    private async Task ShowPullRequestCommentDeleteDialogAsync(long commentId, bool isReviewComment)
    {
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("RepoPullRequestsCommentDeleteDialogError");
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L("RepoPullRequests/Dialogs/CommentDelete/Title", "Delete comment?"),
            Content = AppDialogStyleCatalog.CreateContentPanel(
                new TextBlock
                {
                    Text = L("RepoPullRequests/Dialogs/CommentDelete/Message", "This comment will be permanently deleted."),
                    TextWrapping = TextWrapping.Wrap
                },
                errorText),
            PrimaryButtonText = L("Common/Delete", "Delete"),
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsCommentDeleteDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/CommentDelete/AutomationName", "Delete pull request comment"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () => await ViewModel.DeletePullRequestCommentAsync(commentId, isReviewComment)
                ? DialogMutationResult.Success()
                : DialogMutationResult.Failure(ViewModel.StatusText),
            errorText);
    }

    private async void NewPullRequestButton_Click(object sender, RoutedEventArgs e)
    {
        RepoPullRequestPageViewModel.PullRequestCreateDialogData? data = await ViewModel.LoadCreateDialogDataAsync();
        if (data is null)
        {
            return;
        }

        TextBox titleBox = new()
        {
            Header = ViewModel.TitleHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/TitlePlaceholder", "Pull request title"),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoPullRequestsCreateTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoPullRequests/Dialogs/TitleAutomationName", "Pull request title"));
        TextBox headBox = new()
        {
            Header = ViewModel.HeadBranchHeaderText,
            PlaceholderText = ViewModel.HeadBranchDialogPlaceholderText,
            Text = data.DefaultHead
        };
        AutomationProperties.SetAutomationId(headBox, "RepoPullRequestsCreateHeadBranchBox");
        AutomationProperties.SetName(headBox, L("RepoPullRequests/Dialogs/Create/HeadAutomationName", "Pull request head branch"));
        TextBox baseBox = new()
        {
            Header = ViewModel.BaseBranchHeaderText,
            Text = data.DefaultBase
        };
        AutomationProperties.SetAutomationId(baseBox, "RepoPullRequestsCreateBaseBranchBox");
        AutomationProperties.SetName(baseBox, L("RepoPullRequests/Dialogs/Create/BaseAutomationName", "Pull request base branch"));
        TextBox bodyBox = new()
        {
            Header = ViewModel.DescriptionHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/DescriptionPlaceholder", "Add a description..."),
            AcceptsReturn = true,
            Height = 180,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(bodyBox, "RepoPullRequestsCreateBodyBox");
        AutomationProperties.SetName(bodyBox, L("RepoPullRequests/Dialogs/DescriptionAutomationName", "Pull request description"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsCreateDialogError");
        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                errorText,
                titleBox,
                headBox,
                baseBox,
                bodyBox
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.NewPullRequestDialogTitle,
            Content = content,
            PrimaryButtonText = ViewModel.CreateButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsCreateDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Create/AutomationName", "Create pull request"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(titleBox.Text) ||
                    string.IsNullOrWhiteSpace(headBox.Text) ||
                    string.IsNullOrWhiteSpace(baseBox.Text))
                {
                    return DialogMutationResult.Failure(
                        L("RepoPullRequests/Dialogs/Create/RequiredFields", "Title, head branch, and base branch are required."));
                }

                string previousStatus = ViewModel.StatusText;
                await ViewModel.CreatePullRequestAsync(
                    titleBox.Text.Trim(),
                    headBox.Text.Trim(),
                    baseBox.Text.Trim(),
                    bodyBox.Text);
                return ResolvePullRequestMutationResult(
                    previousStatus,
                    string.Equals(ViewModel.SelectedPullRequest?.Title, titleBox.Text.Trim(), StringComparison.Ordinal),
                    "created pull request");
            },
            errorText,
            layoutKind: AppDialogLayoutKind.Editor);
    }

    private async void EditPullRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPullRequest is null)
        {
            return;
        }

        TextBox titleBox = new()
        {
            Header = ViewModel.TitleHeaderText,
            Text = ViewModel.SelectedPullRequest.Title,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoPullRequestsEditTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoPullRequests/Dialogs/TitleAutomationName", "Pull request title"));
        MarkdownForm bodyForm = new()
        {
            Text = ViewModel.PullRequestBodyText,
            DocumentSource = ViewModel.PullRequestBodyMarkdownSource,
            EditorHeight = 360
        };
        AutomationProperties.SetAutomationId(bodyForm, "RepoPullRequestsEditBodyForm");
        AutomationProperties.SetName(bodyForm, L("RepoPullRequests/Dialogs/DescriptionAutomationName", "Pull request description"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsEditDialogError");
        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                titleBox,
                bodyForm,
                errorText
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.FormatEditPullRequestDialogTitle(ViewModel.SelectedPullRequest.Number),
            Content = content,
            PrimaryButtonText = ViewModel.SaveButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsEditDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Edit/AutomationName", "Edit pull request"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(titleBox.Text))
                {
                    titleBox.Focus(FocusState.Programmatic);
                    return DialogMutationResult.Failure(L("RepoPullRequests/Dialogs/Edit/TitleRequired", "Pull request title is required."));
                }

                string previousStatus = ViewModel.StatusText;
                await ViewModel.UpdateSelectedPullRequestAsync(titleBox.Text.Trim(), bodyForm.Text);
                return ResolvePullRequestMutationResult(
                    previousStatus,
                    string.Equals(ViewModel.SelectedPullRequest?.Title, titleBox.Text.Trim(), StringComparison.Ordinal),
                    "updated");
            },
            errorText,
            layoutKind: AppDialogLayoutKind.Editor);
    }

    private async void MetadataButton_Click(object sender, RoutedEventArgs e)
    {
        RepoPullRequestPageViewModel.PullRequestMetadataDialogData? data = await ViewModel.LoadSelectedPullRequestMetadataDialogDataAsync();
        if (data is null || ViewModel.SelectedPullRequest is null)
        {
            return;
        }

        TextBox reviewersBox = new()
        {
            Header = ViewModel.RequestedReviewersSectionTitle,
            Text = string.Join(", ", ViewModel.RequestedReviewers.Select(reviewer => reviewer.Login)),
            PlaceholderText = L("RepoPullRequests/Dialogs/Metadata/UsersPlaceholder", "user1, user2")
        };
        AutomationProperties.SetAutomationId(reviewersBox, "RepoPullRequestsMetadataReviewersBox");
        AutomationProperties.SetName(reviewersBox, L("RepoPullRequests/Dialogs/Metadata/ReviewersAutomationName", "Requested reviewers"));
        TextBox assigneesBox = new()
        {
            Header = ViewModel.AssigneesSectionTitle,
            Text = string.Join(", ", ViewModel.SelectedAssignees.Select(assignee => assignee.Login)),
            PlaceholderText = L("RepoPullRequests/Dialogs/Metadata/UsersPlaceholder", "user1, user2")
        };
        AutomationProperties.SetAutomationId(assigneesBox, "RepoPullRequestsMetadataAssigneesBox");
        AutomationProperties.SetName(assigneesBox, L("RepoPullRequests/Dialogs/Metadata/AssigneesAutomationName", "Pull request assignees"));
        TextBox labelsBox = new()
        {
            Header = ViewModel.LabelsSectionTitle,
            Text = string.Join(", ", ViewModel.SelectedLabels.Select(label => label.Name)),
            PlaceholderText = L("RepoPullRequests/Dialogs/Metadata/LabelsPlaceholder", "bug, ui")
        };
        AutomationProperties.SetAutomationId(labelsBox, "RepoPullRequestsMetadataLabelsBox");
        AutomationProperties.SetName(labelsBox, L("RepoPullRequests/Dialogs/Metadata/LabelsAutomationName", "Pull request labels"));
        ComboBox milestoneBox = new()
        {
            Header = ViewModel.MilestoneHeaderText,
            DisplayMemberPath = nameof(GitHubMilestone.Title),
            ItemsSource = data.AvailableMilestones,
            SelectedItem = data.AvailableMilestones.FirstOrDefault(milestone => milestone.Title == ViewModel.MilestoneTitle)
        };
        AutomationProperties.SetAutomationId(milestoneBox, "RepoPullRequestsMetadataMilestonePicker");
        AutomationProperties.SetName(milestoneBox, L("RepoPullRequests/Dialogs/Metadata/MilestoneAutomationName", "Pull request milestone"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsMetadataDialogError");
        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                reviewersBox,
                assigneesBox,
                labelsBox,
                milestoneBox,
                errorText
            }
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.FormatMetadataDialogTitle(ViewModel.SelectedPullRequest.Number),
            Content = content,
            PrimaryButtonText = ViewModel.SaveButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsMetadataDialog");
        AutomationProperties.SetName(dialog, L("RepoPullRequests/Dialogs/Metadata/AutomationName", "Edit pull request metadata"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                GitHubMilestone? milestone = milestoneBox.SelectedItem as GitHubMilestone;
                string previousStatus = ViewModel.StatusText;
                await ViewModel.UpdateSelectedPullRequestMetadataAsync(new RepoPullRequestPageViewModel.PullRequestMetadataUpdate(
                    SplitCsv(reviewersBox.Text),
                    SplitCsv(assigneesBox.Text),
                    SplitCsv(labelsBox.Text),
                    milestone?.Number));
                return ResolvePullRequestMutationResult(previousStatus, false, "updated");
            },
            errorText);
    }

    private void PreviousDiffMatchButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.MovePullRequestDiffSearchMatch(-1);

    private void NextDiffMatchButton_Click(object sender, RoutedEventArgs e)
        => ViewModel.MovePullRequestDiffSearchMatch(1);

    private async void MergeCommitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMergeDialogAsync("merge");
    }

    private async void SquashMergeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMergeDialogAsync("squash");
    }

    private async void RebaseMergeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMergeDialogAsync("rebase");
    }

    private async Task ShowMergeDialogAsync(string mergeMethod)
    {
        string operationTitle = ViewModel.FormatMergeOperationTitle(mergeMethod);
        TextBox titleBox = new()
        {
            Header = ViewModel.CommitTitleHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/Merge/TitlePlaceholder", "Optional merge commit title"),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(titleBox, "RepoPullRequestsMergeTitleBox");
        AutomationProperties.SetName(titleBox, L("RepoPullRequests/Dialogs/Merge/TitleAutomationName", "Merge commit title"));
        TextBox messageBox = new()
        {
            Header = ViewModel.CommitMessageHeaderText,
            PlaceholderText = L("RepoPullRequests/Dialogs/Merge/MessagePlaceholder", "Optional merge commit message"),
            AcceptsReturn = true,
            Height = 160,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(messageBox, "RepoPullRequestsMergeMessageBox");
        AutomationProperties.SetName(messageBox, L("RepoPullRequests/Dialogs/Merge/MessageAutomationName", "Merge commit message"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
            "RepoPullRequestsMergeDialogError");
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = operationTitle,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    titleBox,
                    messageBox,
                    errorText
                }
            },
            PrimaryButtonText = ViewModel.MergeButtonText,
            CloseButtonText = ViewModel.CancelButtonText,
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "RepoPullRequestsMergeDialog");
        AutomationProperties.SetName(
            dialog,
            LF("RepoPullRequests/Dialogs/Merge/AutomationNameFormat", "{0} pull request", operationTitle));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () =>
            {
                string previousStatus = ViewModel.StatusText;
                await ViewModel.MergeSelectedPullRequestAsync(
                    mergeMethod,
                    operationTitle,
                    string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text,
                    string.IsNullOrWhiteSpace(messageBox.Text) ? null : messageBox.Text);
                return ResolvePullRequestMutationResult(previousStatus, false, "merged");
            },
            errorText);
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);

    private static string[] SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private DialogMutationResult ResolvePullRequestMutationResult(
        string previousStatus,
        bool observableSuccess,
        string successText)
    {
        string currentStatus = ViewModel.StatusText ?? string.Empty;
        DialogMutationOutcome outcome = DialogMutationOutcomePolicy.Resolve(
            previousStatus,
            currentStatus,
            observableSuccess,
            successText,
            "JitHub could not complete this pull request action.");
        return outcome.Succeeded
            ? DialogMutationResult.Success()
            : DialogMutationResult.Failure(outcome.ErrorMessage);
    }
}
