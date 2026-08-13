using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoCommitsPage : Page
{
    private const string LargeCommitAutomationVariable = "JITHUB_AUTOMATION_LARGE_COMMIT";
    private bool _initialized;
    private bool _openedInitialListDrawer;
    private CancellationTokenSource? _filterDebounce;
    private CommitWorkspacePerformanceMonitor? _performanceMonitor;
    private int _selectionRenderGeneration;

    public RepoCommitsPageViewModel ViewModel { get; }

    public RepoCommitsPage()
    {
        ViewModel = ((App)Application.Current).GetService<RepoCommitsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        AddHandler(KeyDownEvent, new KeyEventHandler(RepoCommitsPage_KeyDown), true);
        Loaded += RepoCommitsPage_Loaded;
        Unloaded += RepoCommitsPage_Unloaded;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _initialized = false;
        _openedInitialListDrawer = false;
        CommitPageNavArg? arg = e.Parameter as CommitPageNavArg;
        await ViewModel.InitializeAsync(arg);
        ProductPerformanceReadiness.CommitRoute(
            "repo_commits",
            $"{ProductPerformanceReadiness.CountIdentity(ViewModel.Commits.Count)};selected={ViewModel.SelectedCommit?.Sha ?? "none"}");
        _initialized = true;
        UpdatePaneButtonVisibility();
        MaybeOpenInitialCommitListDrawer();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelPredictivePrefetches();
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = null;
        base.OnNavigatedFrom(e);
    }

    private async void BranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        await ViewModel.ApplyFiltersAsync();
    }

    private async void CommitFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
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
    }

    private async void CommitDateFilter_DateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        ViewModel.SinceFilterDate = SinceFilterPicker.Date;
        ViewModel.UntilFilterDate = UntilFilterPicker.Date;
        await ViewModel.ApplyFiltersAsync();
    }

    private void ClearSinceFilterButton_Click(object sender, RoutedEventArgs e)
        => SinceFilterPicker.Date = null;

    private void ClearUntilFilterButton_Click(object sender, RoutedEventArgs e)
        => UntilFilterPicker.Date = null;

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
        int renderGeneration = ++_selectionRenderGeneration;
        CommitSelectionAfterRenderedFrame(commit, renderGeneration);
        ViewModel.SelectedCommit = commit;
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
        if (_performanceMonitor is not null ||
            !string.Equals(
                Environment.GetEnvironmentVariable(LargeCommitAutomationVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        _performanceMonitor = new CommitWorkspacePerformanceMonitor(CommitsWorkspace);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void RepoCommitsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _selectionRenderGeneration++;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _performanceMonitor?.Dispose();
        _performanceMonitor = null;
    }

    private void CommitSelectionAfterRenderedFrame(GitHubCommit commit, int generation)
    {
        ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
            this,
            () => generation == _selectionRenderGeneration &&
                IsLoaded &&
                string.Equals(ViewModel.SelectedCommit?.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase),
            () =>
                ViewModel.IsCommitDetailCoherent(commit) &&
                string.Equals(CommitDetailTitle.Text, commit.SummaryMessage, StringComparison.Ordinal),
            () => ProductPerformanceReadiness.CommitTraversal("repo_commits", commit.AutomationId));
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        => _performanceMonitor?.ObserveProperty(e.PropertyName);

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

        RepoCommitsOpenListPaneButton.Visibility = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoCommitsCloseListPaneButton.Visibility = isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoCommitsOpenInspectorPaneButton.Visibility = state?.ShouldShowTrailingPaneButton == true && !isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        RepoCommitsCloseInspectorPaneButton.Visibility = isTrailingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    private void CommitSectionSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ViewModel.SetSection(Math.Clamp(CommitSectionSegmented.SelectedIndex, 0, 3) switch
        {
            1 => CommitWorkspaceSection.Comments,
            2 => CommitWorkspaceSection.Checks,
            3 => CommitWorkspaceSection.Compare,
            _ => CommitWorkspaceSection.Diff
        });
    }

    private void CopyShaButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCommit is null)
        {
            return;
        }

        try
        {
            DataPackage package = new();
            package.SetText(ViewModel.SelectedCommit.Sha);
            Clipboard.SetContent(package);
            ViewModel.TrackCommitAction(CommitActionKind.CopySha, CommitActionOutcome.Success);
        }
        catch
        {
            ViewModel.TrackCommitAction(CommitActionKind.CopySha, CommitActionOutcome.Failure);
        }
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

    private async void CommentButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddCommitCommentAsync();
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunCompareAsync();
    }
}

internal sealed class CommitWorkspacePerformanceMonitor : IDisposable
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
