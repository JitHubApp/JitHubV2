using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Commit;
using JitHub.WinUI.Views.Controls.Common;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoCommitsPage : Page
{
    private const string LargeCommitAutomationVariable = "JITHUB_AUTOMATION_LARGE_COMMIT";
    private const double ShyHeaderStartOffset = 24;
    private const double ShyHeaderRestoreOffset = 8;
    private const double ShyHeaderRevealTravel = 64;
    private const double ShyHeaderRehideTravel = 24;
    private const double ScrollDirectionEpsilon = 0.5;
    private const double CompactShyHeaderContentInset = 58;
    private const double DiffFilePaneOverlayBreakpoint = 760;
    private static readonly TimeSpan ShyHeaderDuration = AppMotionTokens.MediumDuration;
    private readonly TransitionHelper _listHeaderTransition;
    private readonly TransitionHelper _detailHeaderTransition;
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private CancellationTokenSource? _filterDebounce;
    private CommitWorkspacePerformanceMonitor? _performanceMonitor;
    private int _selectionRenderGeneration;
    private bool _suppressCommitFilterEvents;
    private bool _synchronizingSectionSelection;
    private bool _diffFilePaneUserClosed;
    private bool _isDiffFilePaneOverlay;
    private string? _pendingDiffFileToReveal;
    private ScrollViewer? _commitListScrollViewer;
    private long _commitListVerticalOffsetCallbackToken;
    private long _commitListScrollableHeightCallbackToken;
    private double _lastCommitListScrollOffset;
    private double _commitListUpwardRevealTravel;
    private double _commitListDownwardRehideTravel;
    private bool _commitListHeaderRevealedByUpwardScroll;
    private bool _isCommitListScrollHeaderShy;
    private bool _isCommitListHeaderShy;
    private bool _isCommitListHeaderLayoutTransitionActive;
    private int _commitListHeaderTransitionGeneration;
    private ScrollViewer? _activeDetailScrollViewer;
    private long _detailVerticalOffsetCallbackToken;
    private long _detailScrollableHeightCallbackToken;
    private double _lastDetailScrollOffset;
    private double _detailUpwardRevealTravel;
    private double _detailDownwardRehideTravel;
    private bool _detailHeaderRevealedByUpwardScroll;
    private bool _isDetailScrollHeaderShy;
    private bool _isDetailHeaderShy;
    private bool _isDetailHeaderLayoutTransitionActive;
    private int _detailHeaderTransitionGeneration;

    public RepoCommitsPageViewModel ViewModel { get; }

    private void CommitDiffViewer_ActionCompleted(
        object sender,
        CommitDiffActionCompletedEventArgs e)
    {
        CommitActionKind? action = e.Action switch
        {
            TelemetryTaxonomy.Actions.CopyDiff => CommitActionKind.CopyDiff,
            TelemetryTaxonomy.Actions.CopyPath => CommitActionKind.CopyPath,
            _ => null
        };
        if (action is not null)
        {
            ViewModel.TrackCommitAction(
                action.Value,
                e.Result == TelemetryTaxonomy.Results.Success
                    ? CommitActionOutcome.Success
                    : CommitActionOutcome.Failure);
        }
    }

    public RepoCommitsPage()
    {
        ViewModel = ((App)Application.Current).GetService<RepoCommitsPageViewModel>();
        InitializeComponent();
        _listHeaderTransition = new TransitionHelper
        {
            Source = CommitListExpandedHeaderSurface,
            Target = CommitListShyHeaderSurface,
            Duration = ShyHeaderDuration,
            ReverseDuration = ShyHeaderDuration,
            DefaultOpacityTransitionProgressKey = AppMotionTokens.ShyHeaderOpacityTransitionProgressKey,
            SourceToggleMethod = VisualStateToggleMethod.ByVisibility,
            TargetToggleMethod = VisualStateToggleMethod.ByVisibility,
            Configs =
            [
                 new TransitionConfig { Id = "CommitListHeaderChrome", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                 new TransitionConfig { Id = "CommitListHeaderTitle", ScaleMode = ScaleMode.ScaleY },
                 new TransitionConfig { Id = "CommitListHeaderBranch", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                 new TransitionConfig { Id = "CommitListHeaderSearch", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                 new TransitionConfig { Id = "CommitListHeaderFilters", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }
            ]
        };
        _detailHeaderTransition = new TransitionHelper
        {
            Source = CommitDetailExpandedHeaderSurface,
            Target = CommitDetailShyHeaderSurface,
            Duration = ShyHeaderDuration,
            ReverseDuration = ShyHeaderDuration,
            DefaultOpacityTransitionProgressKey = AppMotionTokens.ShyHeaderOpacityTransitionProgressKey,
            SourceToggleMethod = VisualStateToggleMethod.ByVisibility,
            TargetToggleMethod = VisualStateToggleMethod.ByVisibility,
            Configs =
            [
                new TransitionConfig { Id = "CommitDetailHeaderChrome", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "CommitDetailListButton", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "CommitDetailTitle", ScaleMode = ScaleMode.ScaleY },
                new TransitionConfig { Id = "CommitDetailTabs", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "CommitDetailActions", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }
            ]
        };
        DataContext = ViewModel;
        AddHandler(KeyDownEvent, new KeyEventHandler(RepoCommitsPage_KeyDown), true);
        Loaded += RepoCommitsPage_Loaded;
        Unloaded += RepoCommitsPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            _initialized = false;
            _openedInitialListDrawer = false;
            CommitPageNavArg? arg = e.Parameter as CommitPageNavArg;
            await ViewModel.InitializeAsync(arg);
            ProductPerformanceReadiness.CommitRoute("repo_commits", $"{ProductPerformanceReadiness.CountIdentity(ViewModel.Commits.Count)};selected={ViewModel.SelectedCommit?.Sha ?? "none"}");
            _initialized = true;
            UpdatePaneButtonVisibility();
            UpdateResponsiveDensity();
            MaybeOpenInitialCommitListDrawer();
            _ = DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                AttachActiveDetailScrollViewer);
        }, "ui-repo-commits-page");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelPredictivePrefetches();
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = null;
        base.OnNavigatedFrom(e);
    }

    private void BranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            if (!_initialized || _suppressCommitFilterEvents)
            {
                return;
            }

            await ViewModel.ApplyFiltersAsync();
        }, "ui-repo-commits-page");
    }

    private void CommitFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            if (!_initialized || _suppressCommitFilterEvents)
            {
                return;
            }

            ViewModel.SearchText = CommitSearchBox.Text;
            ViewModel.PathFilterText = PathFilterBox.Text;
            ViewModel.AuthorFilterText = AuthorFilterBox.Text;
            _filterDebounce?.Cancel();
            _filterDebounce?.Dispose();
            CancellationTokenSource debounce = new();
            _filterDebounce = debounce;
            try
            {
                await Task.Delay(220, debounce.Token);
                await ViewModel.ApplyFiltersAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }, "ui-repo-commits-page");
    }

    private void CommitDateFilter_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        UiTaskGuard.Run(async () =>
        {
            if (!_initialized || _suppressCommitFilterEvents)
            {
                return;
            }

            ViewModel.SinceFilterDate = SinceFilterPicker.Date;
            ViewModel.UntilFilterDate = UntilFilterPicker.Date;
            await ViewModel.ApplyFiltersAsync();
        }, "ui-repo-commits-page");
    }

    private void ClearSinceFilterButton_Click(object sender, RoutedEventArgs e)
        => SinceFilterPicker.Date = null;

    private void ClearUntilFilterButton_Click(object sender, RoutedEventArgs e)
        => UntilFilterPicker.Date = null;

    private void CommitFiltersFlyout_Opened(object sender, object e) =>
        ViewModel.TrackCommitAction(CommitActionKind.ShowFilters, CommitActionOutcome.Success);

    private void CommitSearchFlyout_Opened(object sender, object e)
    {
        ViewModel.TrackCommitAction(CommitActionKind.ShowSearchTools, CommitActionOutcome.Success);
        CommitSearchBox.Focus(FocusState.Programmatic);
        CommitSearchBox.SelectAll();
    }

    private void CommitSearchFlyout_Closed(object sender, object e) =>
        ViewModel.TrackCommitAction(CommitActionKind.HideSearchTools, CommitActionOutcome.Success);

    private void CompactCommitSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement placementTarget)
        {
            CommitSearchFlyout.ShowAt(placementTarget);
        }
    }

    private void CompactCommitFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement placementTarget)
        {
            CommitFiltersFlyout.ShowAt(placementTarget);
        }
    }

    private void ClearCommitFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            _suppressCommitFilterEvents = true;
            try
            {
                PathFilterBox.Text = string.Empty;
                AuthorFilterBox.Text = string.Empty;
                SinceFilterPicker.Date = null;
                UntilFilterPicker.Date = null;
                ViewModel.PathFilterText = string.Empty;
                ViewModel.AuthorFilterText = string.Empty;
                ViewModel.SinceFilterDate = null;
                ViewModel.UntilFilterDate = null;
            }
            finally
            {
                _suppressCommitFilterEvents = false;
            }

            await ViewModel.ApplyFiltersAsync();
        }, "ui-repo-commits-page");
    }

    private void CommitsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GitHubCommit commit)
        {
            ListViewScrollAnchor anchor = ListViewScrollAnchor.Capture(CommitsList);
            CommitsWorkspace.CloseDrawer();
            anchor.RestoreAcrossLayoutPasses(DispatcherQueue);
        }
    }

    private void CommitListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewItem { Content: GitHubCommit commit } container ||
            e.GetCurrentPoint(container).Properties.PointerUpdateKind !=
                PointerUpdateKind.LeftButtonPressed ||
            FindAncestorButton(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _performanceMonitor?.BeginSelection();
        CommitsList.SelectedItem = commit;
    }

    private void CommitsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommitsList.SelectedItem is not GitHubCommit commit)
        {
            return;
        }

        _performanceMonitor?.BeginSelection();
        ProductPerformanceReadiness.BeginTraversal(
            "repo_commits",
            commit.AutomationId,
            "repo_commits");
        ProductPerformanceReadiness.RecordTraversalStage("repo_commits.selection.started");
        int renderGeneration = ++_selectionRenderGeneration;
        CommitSelectionAfterRenderedFrame(commit, renderGeneration);
        ViewModel.SelectedCommit = commit;
        ProductPerformanceReadiness.RecordTraversalStage("repo_commits.selection.primed");
    }

    private void CommitsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        container.GotFocus -= CommitListItemContainer_GotFocus;
        container.RemoveHandler(
            PointerPressedEvent,
            new PointerEventHandler(CommitListItem_PointerPressed));
        if (args.InRecycleQueue)
        {
            return;
        }

        container.GotFocus += CommitListItemContainer_GotFocus;
        container.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(CommitListItem_PointerPressed),
            handledEventsToo: true);
        if (args.Item is GitHubCommit commit)
        {
            AutomationProperties.SetAutomationId(container, commit.AutomationId);
            AutomationProperties.SetName(container, commit.AutomationName);
        }
    }

    private void CommitListItemContainer_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem { Content: GitHubCommit commit })
        {
            ViewModel.PrefetchCommit(commit, CommitPrefetchReason.Hover);
        }
    }

    private void DiffSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _performanceMonitor?.BeginSearch(textBox.Text);
        }
    }

    private void RepoCommitsPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Space) ||
            FindAncestorButton(e.OriginalSource as DependencyObject) is not { } button)
        {
            return;
        }

        var command = (button.Tag as string) switch
        {
            "previous" => ViewModel.PreviousDiffMatchCommand,
            "next" => ViewModel.NextDiffMatchCommand,
            _ => null
        };
        if (command is null)
        {
            return;
        }

        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private static Button? FindAncestorButton(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is Button button)
            {
                return button;
            }
        }

        return null;
    }

    private void RepoCommitsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _commitListHeaderTransitionGeneration++;
        _detailHeaderTransitionGeneration++;
        MorphTransitionSafety.TryResetVisibilityState(
            _listHeaderTransition,
            CommitListExpandedHeaderSurface,
            CommitListShyHeaderSurface,
            toInitialState: !_isCommitListHeaderShy);
        MorphTransitionSafety.TryResetVisibilityState(
            _detailHeaderTransition,
            CommitDetailExpandedHeaderSurface,
            CommitDetailShyHeaderSurface,
            toInitialState: !_isDetailHeaderShy);
        ResetCommitListReflow();
        ResetDetailContentReflow();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SynchronizeDiffViewerProjections();
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                AttachCommitListScrollViewer();
                AttachActiveDetailScrollViewer();
                UpdateDiffFilePaneDisplayMode();
            });

        if (_performanceMonitor is not null ||
            !string.Equals(
                Environment.GetEnvironmentVariable(LargeCommitAutomationVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        _performanceMonitor = new CommitWorkspacePerformanceMonitor(CommitsWorkspace);
    }

    private void RepoCommitsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _selectionRenderGeneration++;
        _commitListHeaderTransitionGeneration++;
        _detailHeaderTransitionGeneration++;
        MorphTransitionSafety.TryStop(_listHeaderTransition);
        MorphTransitionSafety.TryStop(_detailHeaderTransition);
        CommitsList.LayoutUpdated -= CommitsList_LayoutUpdated;
        DetachCommitListScrollViewer();
        DetachDetailScrollViewer();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _performanceMonitor?.Dispose();
        _performanceMonitor = null;
    }

    private void CommitDiffSearchFlyout_Opened(object sender, object e)
    {
        ViewModel.TrackCommitAction(CommitActionKind.ShowSearchTools, CommitActionOutcome.Success);
        CommitDiffSearchBox.Focus(FocusState.Programmatic);
        CommitDiffSearchBox.SelectAll();
    }

    private void CommitDiffSearchFlyout_Closed(object sender, object e) =>
        ViewModel.TrackCommitAction(CommitActionKind.HideSearchTools, CommitActionOutcome.Success);

    private void CommitCompareSearchFlyout_Opened(object sender, object e)
    {
        ViewModel.TrackCommitAction(CommitActionKind.ShowSearchTools, CommitActionOutcome.Success);
        RepoCommitsCompareDiffSearchBox.Focus(FocusState.Programmatic);
        RepoCommitsCompareDiffSearchBox.SelectAll();
    }

    private void CommitCompareSearchFlyout_Closed(object sender, object e) =>
        ViewModel.TrackCommitAction(CommitActionKind.HideSearchTools, CommitActionOutcome.Success);

    private void CommitsList_Loaded(object sender, RoutedEventArgs e)
    {
        CommitsList.LayoutUpdated -= CommitsList_LayoutUpdated;
        CommitsList.LayoutUpdated += CommitsList_LayoutUpdated;
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AttachCommitListScrollViewer);
    }

    private void CommitsList_LayoutUpdated(object? sender, object e) =>
        AttachCommitListScrollViewer();

    private void AttachCommitListScrollViewer()
    {
        if (!CommitsList.IsLoaded)
        {
            return;
        }

        CommitsList.ApplyTemplate();
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(CommitsList);
        if (scrollViewer is null)
        {
            return;
        }

        CommitsList.LayoutUpdated -= CommitsList_LayoutUpdated;
        if (ReferenceEquals(_commitListScrollViewer, scrollViewer))
        {
            UpdateCommitListHeaderForScroll(scrollViewer);
            return;
        }

        DetachCommitListScrollViewer();
        _commitListScrollViewer = scrollViewer;
        scrollViewer.ViewChanged += CommitListScrollViewer_ViewChanged;
        _commitListVerticalOffsetCallbackToken = scrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            CommitListScrollViewer_ScrollPropertyChanged);
        _commitListScrollableHeightCallbackToken = scrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            CommitListScrollViewer_ScrollPropertyChanged);
        _lastCommitListScrollOffset = scrollViewer.VerticalOffset;
        _commitListUpwardRevealTravel = 0;
        _commitListDownwardRehideTravel = 0;
        _commitListHeaderRevealedByUpwardScroll = false;
        _isCommitListScrollHeaderShy =
            scrollViewer.VerticalOffset >= ShyHeaderStartOffset &&
            CanHideCommitListHeader(scrollViewer);
        SetCommitListHeaderShy(_isCommitListScrollHeaderShy, animate: false);
    }

    private void DetachCommitListScrollViewer()
    {
        if (_commitListScrollViewer is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.ViewChanged -= CommitListScrollViewer_ViewChanged;
        scrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            _commitListVerticalOffsetCallbackToken);
        scrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            _commitListScrollableHeightCallbackToken);
        _commitListScrollViewer = null;
        _commitListVerticalOffsetCallbackToken = 0;
        _commitListScrollableHeightCallbackToken = 0;
    }

    private void CommitListScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateCommitListHeaderForScroll(scrollViewer);
        }
    }

    private void CommitListScrollViewer_ScrollPropertyChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateCommitListHeaderForScroll(scrollViewer);
        }
    }

    private void UpdateCommitListHeaderForScroll(ScrollViewer scrollViewer)
    {
        if (!ReferenceEquals(_commitListScrollViewer, scrollViewer))
        {
            return;
        }

        if (_isCommitListHeaderLayoutTransitionActive)
        {
            _lastCommitListScrollOffset = scrollViewer.VerticalOffset;
            return;
        }

        if (!CanHideCommitListHeader(scrollViewer))
        {
            _lastCommitListScrollOffset = scrollViewer.VerticalOffset;
            _commitListUpwardRevealTravel = 0;
            _commitListDownwardRehideTravel = 0;
            _commitListHeaderRevealedByUpwardScroll = false;
            _isCommitListScrollHeaderShy = false;
            SetCommitListHeaderShy(false, animate: true);
            return;
        }

        double offset = scrollViewer.VerticalOffset;
        double delta = offset - _lastCommitListScrollOffset;
        _lastCommitListScrollOffset = offset;

        if (_isCommitListScrollHeaderShy)
        {
            if (offset <= ShyHeaderRestoreOffset)
            {
                RevealCommitListHeader(revealedByUpwardScroll: false);
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _commitListUpwardRevealTravel += -delta;
                if (_commitListUpwardRevealTravel >= ShyHeaderRevealTravel)
                {
                    RevealCommitListHeader(revealedByUpwardScroll: true);
                }
            }
            else if (delta > ScrollDirectionEpsilon)
            {
                _commitListUpwardRevealTravel = Math.Max(0, _commitListUpwardRevealTravel - delta);
            }

            return;
        }

        if (offset <= ShyHeaderRestoreOffset)
        {
            _commitListHeaderRevealedByUpwardScroll = false;
            _commitListDownwardRehideTravel = 0;
        }
        else if (_commitListHeaderRevealedByUpwardScroll)
        {
            if (delta > ScrollDirectionEpsilon)
            {
                _commitListDownwardRehideTravel += delta;
                if (_commitListDownwardRehideTravel >= ShyHeaderRehideTravel)
                {
                    HideCommitListHeader();
                }
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _commitListDownwardRehideTravel = 0;
            }
        }
        else if (offset >= ShyHeaderStartOffset)
        {
            HideCommitListHeader();
        }
    }

    private void RevealCommitListHeader(bool revealedByUpwardScroll)
    {
        _isCommitListScrollHeaderShy = false;
        _commitListHeaderRevealedByUpwardScroll = revealedByUpwardScroll;
        _commitListUpwardRevealTravel = 0;
        _commitListDownwardRehideTravel = 0;
        SetCommitListHeaderShy(false, animate: true);
    }

    private void HideCommitListHeader()
    {
        if (!CanHideCommitListHeader(_commitListScrollViewer))
        {
            return;
        }

        _isCommitListScrollHeaderShy = true;
        _commitListHeaderRevealedByUpwardScroll = false;
        _commitListUpwardRevealTravel = 0;
        _commitListDownwardRehideTravel = 0;
        SetCommitListHeaderShy(true, animate: true);
    }

    private bool CanHideCommitListHeader(ScrollViewer? scrollViewer) =>
        scrollViewer is not null &&
        ShyHeaderScrollPolicy.CanCollapse(
            scrollViewer.ScrollableHeight,
            CommitListExpandedHeaderSurface.ActualHeight,
            ShyHeaderRestoreOffset);

    private void SetCommitListHeaderShy(bool isShy, bool animate)
    {
        if (_isCommitListHeaderShy == isShy)
        {
            return;
        }

        _isCommitListHeaderShy = isShy;
        int generation = ++_commitListHeaderTransitionGeneration;
        if (!animate || !CommitListExpandedHeaderSurface.IsLoaded || !AreAnimationsEnabled())
        {
            if (MorphTransitionSafety.TryResetVisibilityState(
                _listHeaderTransition,
                CommitListExpandedHeaderSurface,
                CommitListShyHeaderSurface,
                toInitialState: !isShy))
            {
                CommitListRailLayout.UpdateLayout();
                ResetCommitListReflow();
            }

            return;
        }

        UiTaskGuard.Observe(
            AnimateCommitListHeaderAsync(isShy, generation),
            "ui-repo-commits-list-header-morph");
    }

    private async Task AnimateCommitListHeaderAsync(bool isShy, int generation)
    {
        try
        {
            _isCommitListHeaderLayoutTransitionActive = true;
            bool reverseFromSettledShyState =
                !isShy && _listHeaderTransition.IsTargetState && !_listHeaderTransition.IsAnimating;
            double previousListTop = reverseFromSettledShyState
                ? GetElementTop(CommitListHost, CommitListRailLayout)
                : 0;
            Task headerAnimation = isShy
                ? _listHeaderTransition.StartAsync(forceUpdateAnimatedElements: true)
                : _listHeaderTransition.ReverseAsync(forceUpdateAnimatedElements: true);

            if (isShy)
            {
                double reclaimedHeight = Math.Max(
                    0,
                    CommitListExpandedHeaderSurface.ActualHeight - CommitListShyHeaderSurface.ActualHeight);
                AnimateCommitListReflow(
                    new Vector3(0, (float)-reclaimedHeight, 0),
                    ShyHeaderDuration);
            }
            else if (reverseFromSettledShyState)
            {
                double expandedListTop = GetElementTop(CommitListHost, CommitListRailLayout);
                SetCommitListReflowImmediately(
                    new Vector3(0, (float)(previousListTop - expandedListTop), 0));
                AnimateCommitListReflow(Vector3.Zero, ShyHeaderDuration);
            }
            else
            {
                AnimateCommitListReflow(Vector3.Zero, ShyHeaderDuration);
            }

            await headerAnimation;
            if (generation != _commitListHeaderTransitionGeneration)
            {
                return;
            }

            MorphTransitionSafety.TrySetStableState(
                _listHeaderTransition,
                CommitListExpandedHeaderSurface,
                CommitListShyHeaderSurface,
                isTargetState: isShy);
            CommitListRailLayout.UpdateLayout();
            ResetCommitListReflow();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (generation != _commitListHeaderTransitionGeneration)
        {
        }
        catch (Exception ex)
        {
            App.LogHandledException(ex, "ui-repo-commits-list-header-morph");
            MorphTransitionSafety.TryResetVisibilityState(
                _listHeaderTransition,
                CommitListExpandedHeaderSurface,
                CommitListShyHeaderSurface,
                toInitialState: !isShy);
            CommitListRailLayout.UpdateLayout();
            ResetCommitListReflow();
        }
        finally
        {
            if (generation == _commitListHeaderTransitionGeneration)
            {
                _isCommitListHeaderLayoutTransitionActive = false;
                if (_commitListScrollViewer is ScrollViewer scrollViewer)
                {
                    _lastCommitListScrollOffset = scrollViewer.VerticalOffset;
                }
            }
        }
    }

    private void AnimateCommitListReflow(Vector3 translation, TimeSpan duration)
    {
        CommitListHost.TranslationTransition = new Vector3Transition
        {
            Components = Vector3TransitionComponents.Y,
            Duration = duration
        };
        CommitListHost.Translation = translation;
    }

    private void SetCommitListReflowImmediately(Vector3 translation)
    {
        CommitListHost.TranslationTransition = null;
        CommitListHost.Translation = translation;
    }

    private void ResetCommitListReflow() =>
        SetCommitListReflowImmediately(Vector3.Zero);

    private void AttachActiveDetailScrollViewer()
    {
        ScrollViewer? scrollViewer = ViewModel.SelectedSection switch
        {
            CommitWorkspaceSection.Comments when CommitCommentsSection is not null => CommitCommentsSection,
            CommitWorkspaceSection.Checks when CommitChecksSection is not null => CommitChecksSection,
            CommitWorkspaceSection.Compare when CompareCommitDiffViewer is not null => CompareCommitDiffViewer.ScrollViewport,
            _ when CommitDiffViewer is not null => CommitDiffViewer.ScrollViewport,
            _ => null
        };

        if (scrollViewer is null)
        {
            DetachDetailScrollViewer();
            _isDetailScrollHeaderShy = false;
            SetDetailHeaderShy(IsCompactWorkspace, animate: false);
            return;
        }

        AttachDetailScrollViewer(scrollViewer);
    }

    private void AttachDetailScrollViewer(ScrollViewer scrollViewer)
    {
        if (ReferenceEquals(_activeDetailScrollViewer, scrollViewer))
        {
            UpdateDetailHeaderForScroll(scrollViewer);
            return;
        }

        DetachDetailScrollViewer();
        _activeDetailScrollViewer = scrollViewer;
        scrollViewer.ViewChanged += DetailScrollViewer_ViewChanged;
        _detailVerticalOffsetCallbackToken = scrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            DetailScrollViewer_ScrollPropertyChanged);
        _detailScrollableHeightCallbackToken = scrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            DetailScrollViewer_ScrollPropertyChanged);
        _lastDetailScrollOffset = scrollViewer.VerticalOffset;
        _detailUpwardRevealTravel = 0;
        _detailDownwardRehideTravel = 0;
        _detailHeaderRevealedByUpwardScroll = false;
        _isDetailScrollHeaderShy =
            scrollViewer.VerticalOffset >= ShyHeaderStartOffset &&
            CanHideDetailHeader(scrollViewer);
        SetDetailHeaderShy(IsCompactWorkspace || _isDetailScrollHeaderShy, animate: false);
    }

    private void DetachDetailScrollViewer()
    {
        if (_activeDetailScrollViewer is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.ViewChanged -= DetailScrollViewer_ViewChanged;
        scrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            _detailVerticalOffsetCallbackToken);
        scrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            _detailScrollableHeightCallbackToken);
        _activeDetailScrollViewer = null;
        _detailVerticalOffsetCallbackToken = 0;
        _detailScrollableHeightCallbackToken = 0;
    }

    private void DetailScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!IsCompactWorkspace && sender is ScrollViewer scrollViewer)
        {
            UpdateDetailHeaderForScroll(scrollViewer);
        }
    }

    private void DetailScrollViewer_ScrollPropertyChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (!IsCompactWorkspace && sender is ScrollViewer scrollViewer)
        {
            UpdateDetailHeaderForScroll(scrollViewer);
        }
    }

    private void UpdateDetailHeaderForScroll(ScrollViewer scrollViewer)
    {
        if (!ReferenceEquals(_activeDetailScrollViewer, scrollViewer))
        {
            return;
        }

        if (_isDetailHeaderLayoutTransitionActive)
        {
            _lastDetailScrollOffset = scrollViewer.VerticalOffset;
            return;
        }

        if (!CanHideDetailHeader(scrollViewer))
        {
            _lastDetailScrollOffset = scrollViewer.VerticalOffset;
            _detailUpwardRevealTravel = 0;
            _detailDownwardRehideTravel = 0;
            _detailHeaderRevealedByUpwardScroll = false;
            _isDetailScrollHeaderShy = false;
            SetDetailHeaderShy(IsCompactWorkspace, animate: true);
            return;
        }

        double offset = scrollViewer.VerticalOffset;
        double delta = offset - _lastDetailScrollOffset;
        _lastDetailScrollOffset = offset;

        if (_isDetailScrollHeaderShy)
        {
            if (offset <= ShyHeaderRestoreOffset)
            {
                RevealDetailScrollHeader(revealedByUpwardScroll: false);
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _detailUpwardRevealTravel += -delta;
                if (_detailUpwardRevealTravel >= ShyHeaderRevealTravel)
                {
                    RevealDetailScrollHeader(revealedByUpwardScroll: true);
                }
            }
            else if (delta > ScrollDirectionEpsilon)
            {
                _detailUpwardRevealTravel = Math.Max(0, _detailUpwardRevealTravel - delta);
            }

            return;
        }

        if (offset <= ShyHeaderRestoreOffset)
        {
            _detailHeaderRevealedByUpwardScroll = false;
            _detailDownwardRehideTravel = 0;
        }
        else if (_detailHeaderRevealedByUpwardScroll)
        {
            if (delta > ScrollDirectionEpsilon)
            {
                _detailDownwardRehideTravel += delta;
                if (_detailDownwardRehideTravel >= ShyHeaderRehideTravel)
                {
                    HideDetailScrollHeader();
                }
            }
            else if (delta < -ScrollDirectionEpsilon)
            {
                _detailDownwardRehideTravel = 0;
            }
        }
        else if (offset >= ShyHeaderStartOffset)
        {
            HideDetailScrollHeader();
        }
    }

    private void RevealDetailScrollHeader(bool revealedByUpwardScroll)
    {
        _isDetailScrollHeaderShy = false;
        _detailHeaderRevealedByUpwardScroll = revealedByUpwardScroll;
        _detailUpwardRevealTravel = 0;
        _detailDownwardRehideTravel = 0;
        SetDetailHeaderShy(IsCompactWorkspace, animate: true);
    }

    private void HideDetailScrollHeader()
    {
        if (!CanHideDetailHeader(_activeDetailScrollViewer))
        {
            return;
        }

        _isDetailScrollHeaderShy = true;
        _detailHeaderRevealedByUpwardScroll = false;
        _detailUpwardRevealTravel = 0;
        _detailDownwardRehideTravel = 0;
        SetDetailHeaderShy(true, animate: true);
    }

    private bool CanHideDetailHeader(ScrollViewer? scrollViewer) =>
        scrollViewer is not null &&
        ShyHeaderScrollPolicy.CanCollapse(
            scrollViewer.ScrollableHeight,
            CommitDetailExpandedHeaderSurface.ActualHeight,
            ShyHeaderRestoreOffset);

    private bool IsCompactWorkspace =>
        CommitsWorkspace.State?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact;

    private void SetDetailHeaderShy(bool isShy, bool animate)
    {
        if (_isDetailHeaderShy == isShy)
        {
            return;
        }

        _isDetailHeaderShy = isShy;
        int generation = ++_detailHeaderTransitionGeneration;
        if (!animate || !CommitDetailExpandedHeaderSurface.IsLoaded || !AreAnimationsEnabled())
        {
            if (MorphTransitionSafety.TryResetVisibilityState(
                _detailHeaderTransition,
                CommitDetailExpandedHeaderSurface,
                CommitDetailShyHeaderSurface,
                toInitialState: !isShy))
            {
                CommitDetailLayout.UpdateLayout();
                ResetDetailContentReflow();
            }

            return;
        }

        UiTaskGuard.Observe(
            AnimateDetailHeaderAsync(isShy, generation),
            "ui-repo-commits-detail-header-morph");
    }

    private async Task AnimateDetailHeaderAsync(bool isShy, int generation)
    {
        try
        {
            _isDetailHeaderLayoutTransitionActive = true;
            bool reverseFromSettledShyState =
                !isShy && _detailHeaderTransition.IsTargetState && !_detailHeaderTransition.IsAnimating;
            double previousContentTop = reverseFromSettledShyState
                ? GetElementTop(CommitDetailContentHost, CommitDetailLayout)
                : 0;
            Task headerAnimation = isShy
                ? _detailHeaderTransition.StartAsync(forceUpdateAnimatedElements: true)
                : _detailHeaderTransition.ReverseAsync(forceUpdateAnimatedElements: true);

            if (isShy)
            {
                double reclaimedHeight = Math.Max(
                    0,
                    CommitDetailExpandedHeaderSurface.ActualHeight - CommitDetailShyHeaderSurface.ActualHeight);
                AnimateDetailContentReflow(
                    new Vector3(0, (float)-reclaimedHeight, 0),
                    ShyHeaderDuration);
            }
            else if (reverseFromSettledShyState)
            {
                double expandedContentTop = GetElementTop(CommitDetailContentHost, CommitDetailLayout);
                SetDetailContentReflowImmediately(
                    new Vector3(0, (float)(previousContentTop - expandedContentTop), 0));
                AnimateDetailContentReflow(Vector3.Zero, ShyHeaderDuration);
            }
            else
            {
                AnimateDetailContentReflow(Vector3.Zero, ShyHeaderDuration);
            }

            await headerAnimation;
            if (generation != _detailHeaderTransitionGeneration)
            {
                return;
            }

            MorphTransitionSafety.TrySetStableState(
                _detailHeaderTransition,
                CommitDetailExpandedHeaderSurface,
                CommitDetailShyHeaderSurface,
                isTargetState: isShy);
            CommitDetailLayout.UpdateLayout();
            ResetDetailContentReflow();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (generation != _detailHeaderTransitionGeneration)
        {
        }
        catch (Exception ex)
        {
            App.LogHandledException(ex, "ui-repo-commits-detail-header-morph");
            if (MorphTransitionSafety.TryResetVisibilityState(
                _detailHeaderTransition,
                CommitDetailExpandedHeaderSurface,
                CommitDetailShyHeaderSurface,
                toInitialState: !isShy))
            {
                CommitDetailLayout.UpdateLayout();
                ResetDetailContentReflow();
            }
        }
        finally
        {
            if (generation == _detailHeaderTransitionGeneration)
            {
                _isDetailHeaderLayoutTransitionActive = false;
                if (_activeDetailScrollViewer is ScrollViewer scrollViewer)
                {
                    _lastDetailScrollOffset = scrollViewer.VerticalOffset;
                }
            }
        }
    }

    private void AnimateDetailContentReflow(Vector3 translation, TimeSpan duration)
    {
        CommitDetailContentHost.TranslationTransition = new Vector3Transition
        {
            Components = Vector3TransitionComponents.Y,
            Duration = duration
        };
        CommitDetailContentHost.Translation = translation;
    }

    private void SetDetailContentReflowImmediately(Vector3 translation)
    {
        CommitDetailContentHost.TranslationTransition = null;
        CommitDetailContentHost.Translation = translation;
    }

    private void ResetDetailContentReflow() =>
        SetDetailContentReflowImmediately(Vector3.Zero);

    private static double GetElementTop(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformPoint(new Windows.Foundation.Point()).Y;

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is T descendant)
            {
                return descendant;
            }
        }

        return null;
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

    private void CommitSelectionAfterRenderedFrame(GitHubCommit commit, int generation)
    {
        ProductPerformanceReadiness.RecordTraversalStage("repo_commits.render.scheduled");
        ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
            this,
            () => generation == _selectionRenderGeneration &&
                IsLoaded &&
                string.Equals(ViewModel.SelectedCommit?.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase),
            () => string.Equals(CommitDetailTitle.Text, commit.SummaryMessage, StringComparison.Ordinal),
            () =>
            {
                ProductPerformanceReadiness.RecordTraversalStage("repo_commits.render.committed");
                ProductPerformanceReadiness.CommitTraversal("repo_commits", commit.AutomationId);
            });
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _performanceMonitor?.ObserveProperty(e.PropertyName);
        if (string.Equals(e.PropertyName, nameof(ViewModel.DiffRowProjection), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ViewModel.CompareDiffRowProjection), StringComparison.Ordinal))
        {
            SynchronizeDiffViewerProjections();
        }

        if (string.Equals(e.PropertyName, nameof(ViewModel.DiffRowProjection), StringComparison.Ordinal) &&
            _pendingDiffFileToReveal is string fileName)
        {
            _pendingDiffFileToReveal = null;
            _ = DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => CommitDiffViewer.BringFileIntoView(fileName));
        }
    }

    private void SynchronizeDiffViewerProjections()
    {
        CommitDiffViewer.RowProjection = ViewModel.DiffRowProjection;
        if (CompareCommitDiffViewer is not null)
        {
            CompareCommitDiffViewer.RowProjection = ViewModel.CompareDiffRowProjection;
        }
    }

    private void CommitListItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitHubCommit commit })
        {
            ViewModel.PrefetchCommit(commit, CommitPrefetchReason.Hover);
        }
    }

    private void CommitsWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState e)
    {
        UpdatePaneButtonVisibility();
        UpdateResponsiveDensity(e);
        MaybeOpenInitialCommitListDrawer();
    }

    public void OpenCommitListPane()
        => CommitsWorkspace.OpenLeadingPane();

    public void OpenCommitInspectorPane()
        => CommitsWorkspace.OpenTrailingPane();

    private void OpenListPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenCommitListPane();

    private void OpenInspectorPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenCommitInspectorPane();

    private void CloseWorkspaceDrawerButton_Click(object sender, RoutedEventArgs e)
        => CommitsWorkspace.CloseDrawer();

    private void UpdatePaneButtonVisibility()
    {
        AdaptiveWorkspaceState? state = CommitsWorkspace.State;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        bool isTrailingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;

        Visibility listButtonVisibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoCommitsOpenListPaneButton.Visibility = listButtonVisibility;
        RepoCommitsShyOpenListPaneButton.Visibility = listButtonVisibility;
        RepoCommitsCloseListPaneButton.Visibility = isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoCommitsOpenInspectorPaneButton.Visibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoCommitsCloseInspectorPaneButton.Visibility = isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetDetailHeaderShy(IsCompactWorkspace || _isDetailScrollHeaderShy, animate: false);
    }

    private void MaybeOpenInitialCommitListDrawer()
    {
        if (_openedInitialListDrawer ||
            !_initialized ||
            ViewModel.HasSelectedCommit ||
            CommitsWorkspace.State is not { ShouldShowLeadingPaneButton: true })
        {
            return;
        }

        _openedInitialListDrawer = true;
        CommitsWorkspace.OpenLeadingPane();
    }

    private void UpdateResponsiveDensity(AdaptiveWorkspaceState? state = null)
    {
        state ??= CommitsWorkspace.State;
        bool isCompact = state?.Mode is AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact;
        CommitDetailTitle.FontSize = (double)Application.Current.Resources[
            isCompact ? "AppFontSize18" : "AppFontSize25"];
        CommitDetailTitle.MaxLines = isCompact ? 1 : 2;
        CommitDetailMetadata.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        CommitDetailContentHost.Padding = isCompact
            ? new Thickness(12, CompactShyHeaderContentInset, 12, 12)
            : new Thickness(12);
        SetDetailHeaderShy(isCompact || _isDetailScrollHeaderShy, animate: false);
        UpdateDiffFilePaneDisplayMode();

    }

    private void CommitCompareSection_Loaded(object sender, RoutedEventArgs e)
    {
        CompareCommitDiffViewer.RowProjection = ViewModel.CompareDiffRowProjection;
        UpdateResponsiveDensity();
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AttachActiveDetailScrollViewer);
    }

    private void CommitSectionSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _synchronizingSectionSelection)
        {
            return;
        }

        int selectedIndex = ReferenceEquals(sender, CommitShySectionSegmented)
            ? CommitShySectionSegmented.SelectedIndex
            : CommitSectionSegmented.SelectedIndex;
        selectedIndex = Math.Clamp(selectedIndex, 0, 3);
        CommitWorkspaceSection selectedSection = selectedIndex switch
        {
            1 => CommitWorkspaceSection.Comments,
            2 => CommitWorkspaceSection.Checks,
            3 => CommitWorkspaceSection.Compare,
            _ => CommitWorkspaceSection.Diff
        };
        _synchronizingSectionSelection = true;
        try
        {
            CommitSectionSegmented.SelectedIndex = selectedIndex;
            CommitShySectionSegmented.SelectedIndex = selectedIndex;
        }
        finally
        {
            _synchronizingSectionSelection = false;
        }

        if (ViewModel.SelectedSection == selectedSection)
        {
            return;
        }

        ViewModel.SetSection(selectedSection);
        _isDetailScrollHeaderShy = false;
        _detailHeaderRevealedByUpwardScroll = false;
        _detailUpwardRevealTravel = 0;
        _detailDownwardRehideTravel = 0;
        SetDetailHeaderShy(IsCompactWorkspace, animate: false);
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AttachActiveDetailScrollViewer);
    }

    private void CopyShaButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCommit is null)
        {
            return;
        }

        bool copied = PlatformHelper.CopyString(ViewModel.SelectedCommit.Sha);
        ViewModel.TrackCommitAction(
            CommitActionKind.CopySha,
            copied ? CommitActionOutcome.Success : CommitActionOutcome.Failure);
    }

    private void OpenCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NavigationArgs?.Repo is null || ViewModel.SelectedCommit is null)
        {
            return;
        }

        bool opened = Frame.Navigate(
            typeof(RepoCodePage),
            CodeViewerNavArg.CreateWithGitRef(ViewModel.NavigationArgs.Repo, ViewModel.SelectedCommit.Sha),
            new SuppressNavigationTransitionInfo());
        ViewModel.TrackCommitAction(
            CommitActionKind.BrowseFiles,
            opened ? CommitActionOutcome.Success : CommitActionOutcome.Failure);
    }

    private void CompactCommitMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (CommitMessageButton.Flyout is FlyoutBase flyout)
        {
            flyout.ShowAt(CommitDetailShyOverflowButton);
        }
    }

    private void CompactCommitDiffSearchMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => CommitDiffSearchFlyout.ShowAt(CommitDetailShyOverflowButton));
    }

    private void CommitDiffFilesButton_Click(object sender, RoutedEventArgs e)
    {
        bool isOpening = !CommitDiffSplitView.IsPaneOpen;
        CommitDiffSplitView.IsPaneOpen = isOpening;
        _diffFilePaneUserClosed = !isOpening;
        ViewModel.TrackCommitAction(
            CommitActionKind.ToggleFileNavigator,
            CommitActionOutcome.Success);
    }

    private void CommitDiffSplitView_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDiffFilePaneDisplayMode();

    private void UpdateDiffFilePaneDisplayMode()
    {
        if (CommitDiffSplitView is null)
        {
            return;
        }

        double availableWidth = CommitDiffSection?.ActualWidth > 0
            ? CommitDiffSection.ActualWidth
            : CommitDetailContentHost.ActualWidth;
        bool useOverlay = availableWidth > 0 && availableWidth < DiffFilePaneOverlayBreakpoint;
        if (_isDiffFilePaneOverlay == useOverlay)
        {
            return;
        }

        _isDiffFilePaneOverlay = useOverlay;
        CommitDiffSplitView.DisplayMode = useOverlay
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;
        if (useOverlay)
        {
            CommitDiffSplitView.IsPaneOpen = false;
        }
        else if (!_diffFilePaneUserClosed)
        {
            CommitDiffSplitView.IsPaneOpen = true;
        }
    }

    private void CommitDiffFileTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not CommitDiffTreeNode { IsFile: true } file)
        {
            return;
        }

        bool projectionWillChange = ViewModel.ExpandDiffFile(file.FullPath);
        if (projectionWillChange)
        {
            _pendingDiffFileToReveal = file.FullPath;
        }
        else
        {
            CommitDiffViewer.BringFileIntoView(file.FullPath);
        }

        if (_isDiffFilePaneOverlay)
        {
            CommitDiffSplitView.IsPaneOpen = false;
        }
    }

    private void CollapseAllDiffFilesButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CollapseAllDiffFiles();
        ViewModel.TrackCommitAction(
            CommitActionKind.CollapseDiffFile,
            CommitActionOutcome.Success);
    }

    private void ExpandAllDiffFilesButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandAllDiffFiles();
        ViewModel.TrackCommitAction(
            CommitActionKind.ExpandDiffFile,
            CommitActionOutcome.Success);
    }

    private void CommitDiffViewer_FileExpansionRequested(
        object sender,
        CommitDiffFileExpansionRequestedEventArgs e)
    {
        bool isNowCollapsed = ViewModel.ToggleDiffFileCollapsed(e.FileName);
        if (!isNowCollapsed)
        {
            _pendingDiffFileToReveal = e.FileName;
        }

        ViewModel.TrackCommitAction(
            isNowCollapsed ? CommitActionKind.CollapseDiffFile : CommitActionKind.ExpandDiffFile,
            CommitActionOutcome.Success);
    }

    private void CommitCommentFlyout_Opened(object sender, object e) =>
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.IsCommentsSectionVisible && CommitCommentForm is { IsLoaded: true } form)
            {
                form.FocusEditor();
            }
        });

    private void CommitCommentFlyout_Closed(object sender, object e) =>
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.IsCommentsSectionVisible && RepoCommitsOpenCommentButton is { IsLoaded: true } button)
            {
                button.Focus(FocusState.Programmatic);
            }
        });

    private void CommentButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await ViewModel.AddCommitCommentAsync();
            if (string.IsNullOrWhiteSpace(ViewModel.CommentText) &&
                ViewModel.IsCommentsSectionVisible &&
                CommitCommentFlyout is not null)
            {
                CommitCommentFlyout.Hide();
            }
        }, "ui-repo-commits-page");
    }

    private void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await ViewModel.RunCompareAsync();
        }, "ui-repo-commits-page");
    }

    private void CompareRefBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.CanRunCompare)
        {
            return;
        }

        e.Handled = true;
        CompareButton_Click(sender, e);
    }

    private void SwapCompareRefsButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SwapCompareReferences();
}

internal sealed partial class CommitWorkspacePerformanceMonitor : IDisposable
{
    private readonly FrameworkElement _automationOwner;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _dispatcherTimer;
    private readonly Stopwatch _selection = new();
    private readonly Stopwatch _search = new();
    private long _lastDispatcherTick;
    private long _lastRenderTick;
    private long _lastPublishedTick;
    private double _maxDispatcherGapMilliseconds;
    private double _maxRenderGapMilliseconds;
    private double _firstDiffRowsMilliseconds = -1;
    private double _searchIndexMilliseconds = -1;
    private int _renderCount;
    private bool _awaitingDiffRows;
    private bool _awaitingSearchIndex;
    private bool _disposed;

    public CommitWorkspacePerformanceMonitor(FrameworkElement automationOwner)
    {
        _automationOwner = automationOwner;
        _dispatcherTimer = automationOwner.DispatcherQueue.CreateTimer();
        _dispatcherTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dispatcherTimer.IsRepeating = true;
        _dispatcherTimer.Tick += DispatcherTimer_Tick;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += CompositionTarget_Rendering;
        ResetClocks();
        _dispatcherTimer.Start();
        Publish();
    }

    public void BeginSelection()
    {
        _selection.Restart();
        _firstDiffRowsMilliseconds = -1;
        _searchIndexMilliseconds = -1;
        _maxDispatcherGapMilliseconds = 0;
        _maxRenderGapMilliseconds = 0;
        _renderCount = 0;
        _awaitingDiffRows = true;
        _awaitingSearchIndex = false;
        ResetClocks();
        Publish();
    }

    public void BeginSearch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _awaitingSearchIndex = false;
            return;
        }

        _search.Restart();
        _searchIndexMilliseconds = -1;
        _awaitingSearchIndex = true;
        Publish();
    }

    public void ObserveProperty(string? propertyName)
    {
        if (_awaitingDiffRows && string.Equals(propertyName, "DiffRowProjection", StringComparison.Ordinal))
        {
            _awaitingDiffRows = false;
            _firstDiffRowsMilliseconds = _selection.Elapsed.TotalMilliseconds;
        }

        if (_awaitingSearchIndex && string.Equals(propertyName, "DiffSearchMatchCount", StringComparison.Ordinal))
        {
            _awaitingSearchIndex = false;
            _searchIndexMilliseconds = _search.Elapsed.TotalMilliseconds;
        }

        Publish();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatcherTimer.Stop();
        _dispatcherTimer.Tick -= DispatcherTimer_Tick;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CompositionTarget_Rendering;
    }

    private void DispatcherTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double latenessMilliseconds = CommitDiffPerformanceBudget.CalculateDispatcherLateness(
            Stopwatch.GetElapsedTime(_lastDispatcherTick, now),
            _dispatcherTimer.Interval);
        _maxDispatcherGapMilliseconds = Math.Max(
            _maxDispatcherGapMilliseconds,
            latenessMilliseconds);
        _lastDispatcherTick = now;
        if (Stopwatch.GetElapsedTime(_lastPublishedTick, now) >= TimeSpan.FromMilliseconds(200))
        {
            _lastPublishedTick = now;
            Publish();
        }
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        if (_selection.IsRunning)
        {
            _renderCount++;
            _maxRenderGapMilliseconds = Math.Max(
                _maxRenderGapMilliseconds,
                Stopwatch.GetElapsedTime(_lastRenderTick, now).TotalMilliseconds);
        }

        _lastRenderTick = now;
    }

    private void ResetClocks()
    {
        long now = Stopwatch.GetTimestamp();
        _lastDispatcherTick = now;
        _lastRenderTick = now;
        _lastPublishedTick = now;
    }

    private void Publish()
    {
        AutomationProperties.SetItemStatus(
            _automationOwner,
            FormattableString.Invariant(
                $"elapsed_ms={_selection.Elapsed.TotalMilliseconds:F1};first_diff_ms={_firstDiffRowsMilliseconds:F1};search_ms={_searchIndexMilliseconds:F1};dispatcher_max_gap_ms={_maxDispatcherGapMilliseconds:F1};render_max_gap_ms={_maxRenderGapMilliseconds:F1};render_count={_renderCount}"));
    }
}
