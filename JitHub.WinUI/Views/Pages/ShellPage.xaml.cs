using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.Services.Layout;
using JitHub.WinUI.Views.Controls.App;
using JitHub.WinUI.Views.Dialogs;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class ShellPage : Page
{
    private const double SearchSuggestionsTopOffset = 8;
    private const double RepositoryShyHeaderStartOffset = 56;
    private const double RepositoryShyHeaderRestoreOffset = 8;
    private const double RepositoryShyHeaderRevealTravel = 40;
    private const double RepositoryShyHeaderRehideTravel = 24;
    private const double RepositoryScrollDirectionEpsilon = 0.5;
    private const string SearchSuggestionsScenario = "search-suggestions";
    private static readonly TimeSpan RepositoryShyHeaderDuration = AppMotionTokens.MediumDuration;
    private static readonly IScalingCalculator RepositoryHeaderTitleScaling = new TextScalingCalculator();
    private static readonly string[] ProductPerformanceRoutes =
    [
        "home",
        "settings",
        "profile",
        "my_issues",
        "my_pull_requests",
        "stars",
        "gists",
        "notifications",
        "repo_manage",
        "repo_search",
        "repo_code",
        "repo_issues",
        "repo_pull_requests",
        "repo_commits"
    ];
    private readonly Dictionary<string, FrameworkElement> _productPerformanceMarkers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _productPerformanceTraversalMarkers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<FrameworkElement, int> _productPerformanceMarkerGenerations = [];
    private readonly Dictionary<string, long> _productPerformanceRouteStartedTimestamps =
        new(StringComparer.Ordinal);
    private readonly object _productPerformanceTraversalTraceGate = new();
    private ProductPerformanceTraversalStart? _pendingProductPerformanceTraversal;
    private long _productPerformanceTraversalTraceStartedTimestamp;
    private string _productPerformanceTraversalTraceValue = string.Empty;
    private string _productPerformanceRouteInputValue = string.Empty;
    private string _productPerformanceTraversalInputValue = string.Empty;
    private CancellationTokenSource? _notificationLifetime;
    private bool _suppressSearchSuggestionsUntilTextChanges;
    private bool _updatingSearchSelectionFromKeyboard;
    private bool _isSearchPointerOver;
    private readonly SlideDrawerAnimator _shellRailDrawerAnimator;
    private readonly TransitionHelper _repositoryHeaderTransition;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private CancellationTokenSource? _pageLifetime;
    private Control? _searchRestoreTarget;
    private Control? _shellRailDrawerRestoreTarget;
    private Control? _modalRestoreTarget;
    private readonly DialogFocusRestorationGate _modalFocusRestorationGate = DialogFocusRestorationGate.Shared;
    private readonly Dictionary<Control, bool> _modalBackgroundTabStops = [];
    private long _activeModalGeneration;
    private bool _isShellRailCompact;
    private bool _canShellRailInline;
    private bool _isShellRailCollapsedByUser;
    private bool _isShellSearchCompact;
    private bool _searchSuggestionsDismissedUntilInput;
    private bool _initialized;
    private bool _hasPendingLaunchRoute;
    private bool _pendingRepositoryLaunch;
    private string? _pendingShellLaunchPage;
    private int _routeStateRestoreVersion;
    private int _lastHistoryMouseButton;
    private long _lastHistoryMouseButtonTimestamp;
    private ScrollViewer? _repositoryScrollViewer;
    private long _repositoryVerticalOffsetCallbackToken;
    private long _repositoryScrollableHeightCallbackToken;
    private double _lastRepositoryScrollOffset;
    private double _repositoryUpwardRevealTravel;
    private double _repositoryDownwardRehideTravel;
    private bool _repositoryHeaderRevealedByUpwardScroll;
    private bool _isRepositoryScrollHeaderShy;
    private bool _isRepositoryHeaderShy;
    private bool _synchronizingRepositoryFilters;
    private bool _repositoryFiltersInitialized;
    private int _repositoryHeaderTransitionGeneration;
    private bool _isRepositoryHeaderLayoutTransitionActive;

    public ShellPage()
    {
        ViewModel = ((App)Application.Current).GetService<ShellPageViewModel>();
        _taskCoordinator = ((App)Application.Current).GetService<IApplicationTaskCoordinator>();
        InitializeComponent();
        _repositoryHeaderTransition = new TransitionHelper
        {
            Source = RepositoryExpandedHeaderSurface,
            Target = RepositoryShyHeaderSurface,
            Duration = RepositoryShyHeaderDuration,
            ReverseDuration = RepositoryShyHeaderDuration,
            DefaultOpacityTransitionProgressKey = AppMotionTokens.ShyHeaderOpacityTransitionProgressKey,
            SourceToggleMethod = VisualStateToggleMethod.ByVisibility,
            TargetToggleMethod = VisualStateToggleMethod.ByVisibility,
            Configs =
            [
                new TransitionConfig
                {
                    Id = "RepositoryHeaderTitle",
                    ScaleMode = ScaleMode.Custom,
                    CustomScalingCalculator = RepositoryHeaderTitleScaling
                },
                new TransitionConfig { Id = "RepositoryHeaderFilterSurface", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "RepositoryHeaderFilterPublic", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "RepositoryHeaderFilterPrivate", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "RepositoryHeaderFilterForked", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }
            ]
        };
        _repositoryFiltersInitialized = true;
        Modal.AddHandler(KeyDownEvent, new KeyEventHandler(Modal_KeyDown), handledEventsToo: true);
        Modal.AddHandler(PointerPressedEvent, new PointerEventHandler(ModalScrim_PointerPressed), handledEventsToo: true);
        _shellRailDrawerAnimator = new SlideDrawerAnimator(
            ShellRailDrawerTransform,
            SlideDrawerEdge.Left,
            GetShellRailDrawerWidth);
        DataContext = ViewModel;
        ViewModel.InitializeContentFrame(ShellContentFrame);
        ViewModel.InitializeRouteStatePersistence(CaptureCurrentRouteViewState, RestoreRouteViewState);
        ShellRoot.AddHandler(KeyDownEvent, new KeyEventHandler(ShellRoot_KeyDown), true);
        ShellRoot.AddHandler(PointerPressedEvent, new PointerEventHandler(Page_HistoryPointer), true);
        ShellRoot.AddHandler(PointerReleasedEvent, new PointerEventHandler(Page_HistoryPointer), true);
        ShellRoot.AddHandler(PointerReleasedEvent, new PointerEventHandler(Page_PointerReleased), true);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.CommandSearchFocusRequested += ViewModel_CommandSearchFocusRequested;
        ViewModel.SignOutRequested += ViewModel_SignOutRequested;
        ViewModel.LoadApplication(new RelayCommand(OpenModal), new RelayCommand(CloseModal));
        ViewModel.InitializeDesktopIntegration(((App)Application.Current).CurrentMainWindow);
        ((App)Application.Current)
            .GetService<INotificationService>()
            .Register(new RelayCommand<string?>(PushNotification));
        InitializeProductPerformanceBridge();
        Loaded += ShellPage_Loaded;
    }

    public ShellPageViewModel ViewModel { get; }

    private static void ShellPage_Loaded(object sender, RoutedEventArgs e) =>
        ProductPerformanceReadiness.CommitApplicationInteractive();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageLifetime?.Dispose();
        _pageLifetime = new CancellationTokenSource();

        MainWindow mainWindow = ((App)Application.Current).CurrentMainWindow;
        mainWindow.SearchShortcutRequested -= MainWindow_SearchShortcutRequested;
        mainWindow.SearchShortcutRequested += MainWindow_SearchShortcutRequested;
        mainWindow.SetPageTitleBar(TitleBarHost);
        QueueTitleBarPassthroughUpdate(mainWindow);

        ConnectedAnimation? animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("AppLogoAnimation");
        animation?.TryStart(AppLogoShellPage);

        bool useAutomationSearchResults = string.Equals(e.Parameter as string, SearchSuggestionsScenario, StringComparison.OrdinalIgnoreCase);
        if (useAutomationSearchResults)
        {
            ViewModel.SetAutomationSearchResults(CreateAutomationSearchRepositories());
        }

        QueueLaunchRoute(useAutomationSearchResults ? null : e.Parameter as string);

        QueueShellWork("shell.initialize", InitializeShellAsync);
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        MainWindow mainWindow = ((App)Application.Current).CurrentMainWindow;
        mainWindow.SearchShortcutRequested -= MainWindow_SearchShortcutRequested;
        mainWindow.ClearTitleBarPassthroughRegions();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.CommandSearchFocusRequested -= ViewModel_CommandSearchFocusRequested;
        ViewModel.SignOutRequested -= ViewModel_SignOutRequested;
        ProductPerformanceReadiness.RouteCommitted -= ProductPerformanceReadiness_RouteCommitted;
        ProductPerformanceReadiness.TraversalCommitted -= ProductPerformanceReadiness_TraversalCommitted;
        ProductPerformanceReadiness.TraversalStarted -= ProductPerformanceReadiness_TraversalStarted;
        ProductPerformanceReadiness.TraversalStageRecorded -= ProductPerformanceReadiness_TraversalStageRecorded;
        CancelAllPerformanceMarkerSettlements();
        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = null;
        DetachRepositoryScrollViewer();
        _shellRailDrawerAnimator.Stop();
        ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("AppLogoLogoutAnimation", AppLogoShellPage);
    }

    private void InitializeProductPerformanceBridge()
    {
        if (!ProductPerformanceReadiness.IsEnabled)
        {
            return;
        }

        ProductPerformanceBridge.Visibility = Visibility.Visible;
        foreach (string route in ProductPerformanceRoutes)
        {
            TextBlock marker = new()
            {
                Width = 2,
                Height = 2,
                Text = route
            };
            AutomationProperties.SetAccessibilityView(marker, AccessibilityView.Control);
            AutomationProperties.SetAutomationId(marker, $"ProductPerformanceRouteReady_{route}");
            AutomationProperties.SetName(marker, $"{route} cached data ready");
            AutomationProperties.SetItemStatus(marker, "pending");
            ProductPerformanceMarkerHost.Children.Add(marker);
            _productPerformanceMarkers.Add(route, marker);

            TextBlock traversalMarker = new()
            {
                Width = 2,
                Height = 2,
                Text = route
            };
            AutomationProperties.SetAccessibilityView(traversalMarker, AccessibilityView.Control);
            AutomationProperties.SetAutomationId(traversalMarker, $"ProductPerformanceTraversalReady_{route}");
            AutomationProperties.SetName(traversalMarker, $"{route} exact traversal ready");
            AutomationProperties.SetItemStatus(traversalMarker, "pending");
            ProductPerformanceMarkerHost.Children.Add(traversalMarker);
            _productPerformanceTraversalMarkers.Add(route, traversalMarker);
        }

        ProductPerformanceReadiness.RouteCommitted -= ProductPerformanceReadiness_RouteCommitted;
        ProductPerformanceReadiness.RouteCommitted += ProductPerformanceReadiness_RouteCommitted;
        ProductPerformanceReadiness.TraversalCommitted -= ProductPerformanceReadiness_TraversalCommitted;
        ProductPerformanceReadiness.TraversalCommitted += ProductPerformanceReadiness_TraversalCommitted;
        ProductPerformanceReadiness.TraversalStarted -= ProductPerformanceReadiness_TraversalStarted;
        ProductPerformanceReadiness.TraversalStarted += ProductPerformanceReadiness_TraversalStarted;
        ProductPerformanceReadiness.TraversalStageRecorded -= ProductPerformanceReadiness_TraversalStageRecorded;
        ProductPerformanceReadiness.TraversalStageRecorded += ProductPerformanceReadiness_TraversalStageRecorded;
    }

    private void ProductPerformanceReadiness_TraversalCommitted(
        object? sender,
        ProductPerformanceRouteCommit commit)
    {
        string traceValue = string.Empty;
        bool publishTrace = false;
        lock (_productPerformanceTraversalTraceGate)
        {
            if (commit.StartedTimestamp is long startedTimestamp &&
                startedTimestamp == _productPerformanceTraversalTraceStartedTimestamp)
            {
                traceValue = _productPerformanceTraversalTraceValue;
                _productPerformanceTraversalTraceStartedTimestamp = 0;
                publishTrace = true;
            }
        }

        RunOnUiThread(() =>
        {
            if (publishTrace)
            {
                AutomationProperties.SetItemStatus(
                    ProductPerformanceTraversalTrace,
                    traceValue);
            }

            if (_pendingProductPerformanceTraversal is { } pending &&
                string.Equals(commit.Route, pending.Route, StringComparison.Ordinal) &&
                string.Equals(commit.Identity, pending.Identity, StringComparison.Ordinal))
            {
                _pendingProductPerformanceTraversal = null;
            }

            if (_productPerformanceTraversalMarkers.TryGetValue(commit.Route, out FrameworkElement? marker))
            {
                SchedulePerformanceMarkerSettlement(marker, commit);
            }
        });
    }

    private void ProductPerformanceReadiness_RouteCommitted(
        object? sender,
        ProductPerformanceRouteCommit commit)
    {
        RunOnUiThread(() =>
        {
            if (_productPerformanceMarkers.TryGetValue(commit.Route, out FrameworkElement? marker))
            {
                long startedTimestamp = _productPerformanceRouteStartedTimestamps.Remove(
                    commit.Route,
                    out long routeStartedTimestamp)
                        ? routeStartedTimestamp
                        : commit.CommittedTimestamp;
                ScheduleRouteMarkerSettlement(marker, commit, startedTimestamp);
            }

            if (_pendingProductPerformanceTraversal is { } pending &&
                string.Equals(commit.Route, pending.ExpectedDestinationRoute, StringComparison.Ordinal))
            {
                _pendingProductPerformanceTraversal = null;
                ProductPerformanceReadiness.CommitTraversal(
                    pending.Route,
                    pending.Identity,
                    pending.StartedTimestamp);
            }
        });
    }

    private void ProductPerformanceReadiness_TraversalStarted(
        object? sender,
        ProductPerformanceTraversalStart commit)
    {
        lock (_productPerformanceTraversalTraceGate)
        {
            _productPerformanceTraversalTraceStartedTimestamp = commit.StartedTimestamp;
            _productPerformanceTraversalTraceValue = string.Empty;
        }

        RunOnUiThread(() =>
        {
            _pendingProductPerformanceTraversal = commit;
        });
    }

    private void ProductPerformanceReadiness_TraversalStageRecorded(
        object? sender,
        ProductPerformanceTraversalStage stage)
    {
        string elapsed = Stopwatch.GetElapsedTime(stage.StartedTimestamp, stage.RecordedTimestamp)
            .TotalMilliseconds
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        lock (_productPerformanceTraversalTraceGate)
        {
            if (stage.StartedTimestamp != _productPerformanceTraversalTraceStartedTimestamp)
            {
                return;
            }

            _productPerformanceTraversalTraceValue = string.IsNullOrEmpty(_productPerformanceTraversalTraceValue)
                ? $"{stage.Stage}={elapsed}"
                : $"{_productPerformanceTraversalTraceValue};{stage.Stage}={elapsed}";
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => action());
    }

    private void SchedulePerformanceMarkerSettlement(
        FrameworkElement marker,
        ProductPerformanceRouteCommit commit)
    {
        CancelPerformanceMarkerSettlement(marker);
        int generation = _productPerformanceMarkerGenerations.GetValueOrDefault(marker) + 1;
        _productPerformanceMarkerGenerations[marker] = generation;
        AutomationProperties.SetItemStatus(
            marker,
            ProductPerformanceReadiness.FormatStatus(
                commit.Route,
                commit.Identity,
                commit.StartedTimestamp ?? commit.CommittedTimestamp,
                commit.CommittedTimestamp,
                settledTimestamp: null));
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!_productPerformanceMarkerGenerations.TryGetValue(marker, out int currentGeneration) ||
                    currentGeneration != generation)
                {
                    return;
                }

                AutomationProperties.SetItemStatus(
                    marker,
                    ProductPerformanceReadiness.FormatStatus(
                        commit.Route,
                        commit.Identity,
                        commit.StartedTimestamp ?? commit.CommittedTimestamp,
                        commit.CommittedTimestamp,
                        Stopwatch.GetTimestamp()));
            });
    }

    private void ScheduleRouteMarkerSettlement(
        FrameworkElement marker,
        ProductPerformanceRouteCommit commit,
        long startedTimestamp)
    {
        CancelPerformanceMarkerSettlement(marker);
        int generation = _productPerformanceMarkerGenerations.GetValueOrDefault(marker) + 1;
        _productPerformanceMarkerGenerations[marker] = generation;
        AutomationProperties.SetItemStatus(
            marker,
            ProductPerformanceReadiness.FormatStatus(
                commit.Route,
                commit.Identity,
                startedTimestamp,
                commit.CommittedTimestamp,
                settledTimestamp: null));
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () =>
            {
                if (!_productPerformanceMarkerGenerations.TryGetValue(marker, out int currentGeneration) ||
                    currentGeneration != generation)
                {
                    return;
                }

                AutomationProperties.SetItemStatus(
                    marker,
                    ProductPerformanceReadiness.FormatStatus(
                        commit.Route,
                        commit.Identity,
                        startedTimestamp,
                        commit.CommittedTimestamp,
                        Stopwatch.GetTimestamp()));
            });
    }

    private void CancelPerformanceMarkerSettlement(FrameworkElement marker)
    {
        _productPerformanceMarkerGenerations[marker] =
            _productPerformanceMarkerGenerations.GetValueOrDefault(marker) + 1;
    }

    private void CancelAllPerformanceMarkerSettlements()
    {
        _productPerformanceMarkerGenerations.Clear();
    }

    private void ProductPerformanceNavigateButton_Click(object sender, RoutedEventArgs e)
    {
        string route = _productPerformanceRouteInputValue.Trim();
        _productPerformanceRouteStartedTimestamps[route] = Stopwatch.GetTimestamp();
        if (_productPerformanceMarkers.TryGetValue(route, out FrameworkElement? marker))
        {
            CancelPerformanceMarkerSettlement(marker);
            AutomationProperties.SetItemStatus(marker, "pending");
        }

        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            () => NavigateProductPerformanceRoute(route));
    }

    private void NavigateProductPerformanceRoute(string route)
    {
        switch (route)
        {
            case "repo_search":
                ViewModel.OpenSearchQuery("performance");
                break;
            case "repo_code":
                ViewModel.OpenRepositoryPage(
                    Program.CurrentLaunchOptions.RepositoryFullName,
                    "repo-code",
                    Program.CurrentLaunchOptions.Branch);
                break;
            case "repo_issues":
                ViewModel.OpenRepositoryPage(
                    Program.CurrentLaunchOptions.RepositoryFullName,
                    "repo-issues",
                    Program.CurrentLaunchOptions.Branch);
                break;
            case "repo_pull_requests":
                ViewModel.OpenRepositoryPage(
                    Program.CurrentLaunchOptions.RepositoryFullName,
                    "repo-pull-requests",
                    Program.CurrentLaunchOptions.Branch);
                break;
            case "repo_commits":
                ViewModel.OpenRepositoryPage(
                    Program.CurrentLaunchOptions.RepositoryFullName,
                    "repo-commits",
                    Program.CurrentLaunchOptions.Branch);
                break;
            default:
                OpenLaunchShellPage(route switch
                {
                    "my_issues" => "my-issues",
                    "my_pull_requests" => "my-pull-requests",
                    "repo_manage" => "repositories",
                    _ => route
                });
                break;
        }
    }

    private void ProductPerformanceArmTraversalButton_Click(object sender, RoutedEventArgs e)
    {
        string route = _productPerformanceRouteInputValue.Trim();
        string expectedIdentity = _productPerformanceTraversalInputValue.Trim();
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(expectedIdentity))
        {
            return;
        }

        if (_productPerformanceTraversalMarkers.TryGetValue(route, out FrameworkElement? marker))
        {
            CancelPerformanceMarkerSettlement(marker);
            AutomationProperties.SetItemStatus(marker, $"pending;expected={expectedIdentity}");
        }

        ProductPerformanceReadiness.ArmTraversalMeasurement();
    }

    private void ProductPerformanceRouteInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _productPerformanceRouteInputValue = ProductPerformanceRouteInput.Text;
        AutomationProperties.SetItemStatus(
            ProductPerformanceRouteInput,
            _productPerformanceRouteInputValue);
    }

    private void ProductPerformanceTraversalInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _productPerformanceTraversalInputValue = ProductPerformanceTraversalInput.Text;
        AutomationProperties.SetItemStatus(
            ProductPerformanceTraversalInput,
            _productPerformanceTraversalInputValue);
    }

    private async Task InitializeShellAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync(cancellationToken);
    }

    private async Task OpenLaunchRepositoryPageAsync()
    {
        if (!MarkdownLifecycleAutomationBridge.IsEnabled)
        {
            await Task.Delay(350);
        }

        _ = DispatcherQueue.TryEnqueue(() =>
            ViewModel.OpenRepositoryPage(
                Program.CurrentLaunchOptions.RepositoryFullName,
                Program.CurrentLaunchOptions.Page,
                Program.CurrentLaunchOptions.Branch));
    }

    private void QueueLaunchRoute(string? requestedPage)
    {
        _pendingRepositoryLaunch = Program.CurrentLaunchOptions.IsRepositoryPageOverride;
        _pendingShellLaunchPage = requestedPage;
        _hasPendingLaunchRoute = true;
        TryOpenPendingLaunchRoute();
    }

    private void TryOpenPendingLaunchRoute()
    {
        if (!_hasPendingLaunchRoute || !IsLoaded || XamlRoot is null)
        {
            return;
        }

        bool openRepository = _pendingRepositoryLaunch;
        string? requestedPage = _pendingShellLaunchPage;
        _hasPendingLaunchRoute = false;
        _pendingRepositoryLaunch = false;
        _pendingShellLaunchPage = null;

        if (openRepository)
        {
            UiTaskGuard.Observe(OpenLaunchRepositoryPageAsync(), "ui-shell-page");
            return;
        }

        OpenLaunchShellPage(requestedPage);
    }

    private void OpenLaunchShellPage(string? requestedPage = null)
    {
        string? page = string.IsNullOrWhiteSpace(requestedPage)
            ? Program.CurrentLaunchOptions.Page
            : requestedPage;
        if (string.IsNullOrWhiteSpace(page))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            switch (page.Trim().ToLowerInvariant())
            {
                case "my-issues":
                    ViewModel.OpenMyIssuesPage();
                    break;
                case "my-pull-requests":
                    ViewModel.OpenMyPullRequestsPage();
                    break;
                case "profile":
                    ViewModel.OpenAuthenticatedProfile();
                    break;
                case "stars":
                    ViewModel.OpenStarsPage();
                    break;
                case "gists":
                    ViewModel.OpenGistsPage();
                    break;
                case "notifications":
                    ViewModel.OpenNotificationsPage();
                    break;
                case "repositories":
                    ViewModel.OpenManageRepositories();
                    break;
                case "settings":
                    ViewModel.GoToSettingsPage();
                    break;
                default:
                    ViewModel.GoHome();
                    break;
            }
        });
    }

    private void OpenModal()
    {
        _activeModalGeneration = _modalFocusRestorationGate.BeginSession();
        _modalRestoreTarget = null;
        if (TryGetFocusedElement() is Control focusedControl &&
            !IsWithin(focusedControl, ModalContent))
        {
            _modalRestoreTarget = focusedControl;
        }

        UpdateModalLayout(new Size(ActualWidth, ActualHeight));
        bool contentOwnsScrolling = ViewModel.Content is IModalContentLayout { OwnsScrolling: true };
        if (ViewModel.Content is IModalContentLayout modalContentLayout)
        {
            modalContentLayout.SetModalViewport(ModalContent.MaxWidth, ModalContent.MaxHeight);
        }
        ShellModalScrollViewer.VerticalScrollMode = contentOwnsScrolling
            ? ScrollMode.Disabled
            : ScrollMode.Auto;
        ShellModalScrollViewer.VerticalScrollBarVisibility = contentOwnsScrolling
            ? ScrollBarVisibility.Hidden
            : ScrollBarVisibility.Auto;
        Modal.Visibility = Visibility.Visible;
        TitleBarHost.IsHitTestVisible = false;
        SuspendModalBackgroundTabStops(TitleBarHost);
        ShellRail.IsHitTestVisible = false;
        SuspendModalBackgroundTabStops(ShellRail);
        ShellContentFrame.IsHitTestVisible = false;
        ShellContentFrame.IsEnabled = false;
        SearchTextBox.IsEnabled = false;
        SearchSubmitButton.IsEnabled = false;
        HideSearchSuggestions();
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                ModalContent.UpdateLayout();
                FocusFirstModalControl();
            });
    }

    private void CloseModal()
    {
        long closingGeneration = _activeModalGeneration;
        Control? restoreTarget = _modalRestoreTarget;
        _activeModalGeneration = 0;
        _modalRestoreTarget = null;
        Modal.Visibility = Visibility.Collapsed;
        TitleBarHost.IsHitTestVisible = true;
        ShellRail.IsHitTestVisible = true;
        ShellContentFrame.IsHitTestVisible = true;
        ShellContentFrame.IsEnabled = true;
        RestoreModalBackgroundTabStops();
        SearchTextBox.IsEnabled = true;
        SearchSubmitButton.IsEnabled = true;
        _ = DispatcherQueue.TryEnqueue(() => RestoreFocusAfterModal(closingGeneration, restoreTarget));
    }

    private void ModalCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RequestCloseModal();
    }

    private void Modal_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab)
        {
            MoveModalFocus(
                (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
                    CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down);
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        ViewModel.RequestCloseModal();
        e.Handled = true;
    }

    private void ModalScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsWithin(source, ModalContent))
        {
            return;
        }

        // Shell-hosted forms follow ContentDialog semantics: the scrim is modal, not light-dismiss.
        FocusFirstModalControl();
        e.Handled = true;
    }

    private void FocusFirstModalControl()
    {
        Control? firstControl = FindFirstFocusableControl(ModalContent);
        if (firstControl?.Focus(FocusState.Programmatic) == true)
        {
            return;
        }

        _ = ModalCloseButton.Focus(FocusState.Programmatic);
    }

    private void MoveModalFocus(bool reverse)
    {
        List<Control> focusableControls = [];
        CollectFocusableControls(ModalContent, focusableControls);
        if (focusableControls.Count == 0)
        {
            return;
        }

        DependencyObject? focused = TryGetFocusedElement();
        int currentIndex = focusableControls.FindIndex(control => IsWithin(focused, control));
        int nextIndex = reverse
            ? (currentIndex <= 0 ? focusableControls.Count - 1 : currentIndex - 1)
            : (currentIndex < 0 || currentIndex == focusableControls.Count - 1 ? 0 : currentIndex + 1);
        _ = focusableControls[nextIndex].Focus(FocusState.Keyboard);
    }

    private static void CollectFocusableControls(DependencyObject root, ICollection<Control> controls)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Control { IsEnabled: true, IsTabStop: true, Visibility: Visibility.Visible } control &&
                control is not ScrollViewer)
            {
                controls.Add(control);
            }

            CollectFocusableControls(child, controls);
        }
    }

    private void RestoreFocusAfterModal(long closingGeneration, Control? restoreTarget)
    {
        if (!_modalFocusRestorationGate.CanRestore(
                closingGeneration,
                Modal.Visibility == Visibility.Visible))
        {
            return;
        }

        if (restoreTarget is not null && restoreTarget.Focus(FocusState.Programmatic))
        {
            return;
        }

        MoveFocusToSearchSink();
    }

    private static Control? FindFirstFocusableControl(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Control { IsEnabled: true, IsTabStop: true, Visibility: Visibility.Visible } control &&
                control is not ScrollViewer)
            {
                return control;
            }

            Control? descendant = FindFirstFocusableControl(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void SuspendModalBackgroundTabStops(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Control control && !_modalBackgroundTabStops.ContainsKey(control))
            {
                _modalBackgroundTabStops[control] = control.IsTabStop;
                control.IsTabStop = false;
            }

            SuspendModalBackgroundTabStops(child);
        }
    }

    private void RestoreModalBackgroundTabStops()
    {
        foreach ((Control control, bool wasTabStop) in _modalBackgroundTabStops)
        {
            control.IsTabStop = wasTabStop;
        }

        _modalBackgroundTabStops.Clear();
    }

    private ShellRouteViewState? CaptureCurrentRouteViewState()
    {
        Interlocked.Increment(ref _routeStateRestoreVersion);
        if (!TryGetCurrentRouteRoot(out DependencyObject pageRoot))
        {
            return null;
        }

        ListViewBase? selection = null;
        ScrollViewer? scroll = null;
        if (string.Equals(ViewModel.CurrentRoutePage, "settings", StringComparison.Ordinal))
        {
            selection = FindDescendantByAutomationId<ListViewBase>(pageRoot, "SettingsSectionList");
            scroll = FindDescendantByAutomationId<ScrollViewer>(pageRoot, "SettingsContentScrollViewer");
        }
        else if (string.Equals(ViewModel.CurrentRoutePage, "home", StringComparison.Ordinal))
        {
            scroll = FindDescendantByAutomationId<ScrollViewer>(pageRoot, "DashboardMainRailScrollViewer");
        }

        string? selectionId = selection is null
            ? null
            : Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(selection);
        string? scrollId = scroll is null
            ? null
            : Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(scroll);
        FrameworkElement? focusedElement = TryGetFocusedElement() as FrameworkElement;
        string? focusId = focusedElement is not null && IsWithin(focusedElement, pageRoot)
            ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(focusedElement)
            : null;
        if (string.IsNullOrWhiteSpace(focusId))
        {
            focusId = null;
        }

        return new ShellRouteViewState(
            selection?.SelectedIndex,
            scroll?.VerticalOffset ?? 0,
            scroll?.HorizontalOffset ?? 0,
            selectionId,
            scrollId,
            focusId);
    }

    private void RestoreRouteViewState(ShellRouteViewState viewState)
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        if (lifetime is null || lifetime.IsCancellationRequested)
        {
            return;
        }

        int restoreVersion = Interlocked.Increment(ref _routeStateRestoreVersion);
        UiTaskGuard.Observe(
            RestoreRouteViewStateAsync(viewState, restoreVersion, lifetime.Token),
            "ui-shell-page");
    }

    private async Task RestoreRouteViewStateAsync(
        ShellRouteViewState viewState,
        int restoreVersion,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(attempt == 0 ? 16 : 50, cancellationToken);
            if (restoreVersion != Volatile.Read(ref _routeStateRestoreVersion))
            {
                return;
            }

            if (RestoreRouteViewStateCore(viewState))
            {
                return;
            }
        }
    }

    private bool RestoreRouteViewStateCore(ShellRouteViewState viewState)
    {
        if (!TryGetCurrentRouteRoot(out DependencyObject pageRoot))
        {
            return false;
        }

        bool selectionRestored = string.IsNullOrWhiteSpace(viewState.SelectionTargetId);
        if (!string.IsNullOrWhiteSpace(viewState.SelectionTargetId) && viewState.SelectedIndex is int selectedIndex)
        {
            ListViewBase? selection = FindDescendantByAutomationId<ListViewBase>(pageRoot, viewState.SelectionTargetId);
            if (selection is not null && selectedIndex >= -1 && selectedIndex < selection.Items.Count)
            {
                selection.SelectedIndex = selectedIndex;
                selectionRestored = selection.SelectedIndex == selectedIndex;
            }
        }

        bool scrollRestored = string.IsNullOrWhiteSpace(viewState.ScrollTargetId);
        if (!string.IsNullOrWhiteSpace(viewState.ScrollTargetId))
        {
            ScrollViewer? scroll = FindDescendantByAutomationId<ScrollViewer>(pageRoot, viewState.ScrollTargetId);
            if (scroll is not null)
            {
                _ = scroll.ChangeView(
                    viewState.HorizontalOffset,
                    viewState.VerticalOffset,
                    zoomFactor: null,
                    disableAnimation: true);
                scrollRestored = Math.Abs(scroll.VerticalOffset - viewState.VerticalOffset) <= 1 &&
                    Math.Abs(scroll.HorizontalOffset - viewState.HorizontalOffset) <= 1;
            }
        }

        bool focusRestored = string.IsNullOrWhiteSpace(viewState.FocusTargetId);
        if (!string.IsNullOrWhiteSpace(viewState.FocusTargetId))
        {
            Control? focusTarget = FindDescendantByAutomationId<Control>(pageRoot, viewState.FocusTargetId);
            if (focusTarget is not null)
            {
                focusRestored = focusTarget.Focus(FocusState.Programmatic);
            }
        }

        return selectionRestored && scrollRestored && focusRestored;
    }

    private bool TryGetCurrentRouteRoot(out DependencyObject pageRoot)
    {
        pageRoot = null!;
        if (!IsLoaded || XamlRoot is null)
        {
            return false;
        }

        try
        {
            if (ShellContentFrame.Content is FrameworkElement { IsLoaded: true } root)
            {
                pageRoot = root;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            pageRoot = null!;
            return false;
        }
    }

    private static T? FindDescendantByAutomationId<T>(DependencyObject root, string automationId)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate &&
                IsAvailableForRouteStateRestoration(candidate, root) &&
                string.Equals(
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(candidate),
                    automationId,
                    StringComparison.Ordinal))
            {
                return candidate;
            }

            T? descendant = FindDescendantByAutomationId<T>(child, automationId);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool IsAvailableForRouteStateRestoration(FrameworkElement element, DependencyObject root)
    {
        if (!element.IsLoaded || element.Visibility != Visibility.Visible ||
            element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        DependencyObject? current = element;
        while (current is FrameworkElement ancestor)
        {
            if (ancestor.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (ReferenceEquals(current, root))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void PushNotification(string? message)
    {
        ViewModel.ShowNotification(message);
        StartNotificationTimer();
    }

    private void StartNotificationTimer()
    {
        _notificationLifetime?.Cancel();
        _notificationLifetime?.Dispose();
        CancellationTokenSource lifetime = new();
        _notificationLifetime = lifetime;
        UiTaskGuard.Observe(CloseNotificationAsync(lifetime.Token), "ui-shell-page");
    }

    private async Task CloseNotificationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ViewModel.IsNotificationOpen = false;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellPageViewModel.SearchResults))
        {
            _ = DispatcherQueue.TryEnqueue(UpdateSearchSuggestionsState);
            return;
        }

        if (e.PropertyName is nameof(ShellPageViewModel.AreRepositoriesVisible))
        {
            if (ViewModel.AreRepositoriesVisible)
            {
                _ = DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    AttachRepositoryScrollViewer);
            }
            else
            {
                _isRepositoryScrollHeaderShy = false;
                _repositoryHeaderRevealedByUpwardScroll = false;
                _repositoryUpwardRevealTravel = 0;
                _repositoryDownwardRehideTravel = 0;
                SetRepositoryHeaderShy(false, animate: true);
            }

            return;
        }

        if (e.PropertyName is nameof(ShellPageViewModel.IsNotificationOpen) && ViewModel.IsNotificationOpen)
        {
            StartNotificationTimer();
        }
    }

    private void ViewModel_CommandSearchFocusRequested(object? sender, EventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        FocusSearchBox();
    }

    private void ViewModel_SignOutRequested(object? sender, EventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            if (XamlRoot is not null)
            {
                await AccountSignOutDialogFlow.ShowAsync(XamlRoot);
            }
        }, "ui-shell-page");
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateModalLayout(e.NewSize);

        if (e.NewSize.Width > 1200)
        {
            VisualStateManager.GoToState(this, "WideLayout", false);
        }
        else if (e.NewSize.Width > 900)
        {
            VisualStateManager.GoToState(this, "MediumLayout", false);
        }
        else
        {
            VisualStateManager.GoToState(this, "NarrowLayout", false);
        }

        ApplyShellResponsiveLayout(e.NewSize.Width);

        ShellContentFrame.ClearValue(WidthProperty);

        QueueTitleBarPassthroughUpdate(((App)Application.Current).CurrentMainWindow);

        if (SearchSuggestionsHost.Visibility == Visibility.Visible)
        {
            if (IsFocusWithinSearchBox())
            {
                UpdateSearchSuggestionsLayout();
            }
            else
            {
                HideSearchSuggestions();
            }
        }
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyShellResponsiveLayout(ActualWidth);
        MoveFocusToSearchSink();
        TryOpenPendingLaunchRoute();
    }

    private void SearchTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSearchTextAlignment();
        QueueTitleBarPassthroughUpdate(((App)Application.Current).CurrentMainWindow);
    }

    private void SearchTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SetSearchFocusVisual(true);
        UpdateSearchTextAlignment();
        QueueSearchSuggestionsRefresh(forceImmediate: true);
    }

    private void SearchControl_LostFocus(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(() => SetSearchFocusVisual(IsFocusWithinSearchBox()));
    }

    private void SearchTextBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(SearchTextBox).Properties.IsRightButtonPressed)
        {
            return;
        }

    }

    private void SearchBoxContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isSearchPointerOver = true;
        VisualStateManager.GoToState(this, "SearchPointerOver", true);
    }

    private void SearchBoxContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isSearchPointerOver = false;
        VisualStateManager.GoToState(this, "SearchPointerNormal", true);
    }

    private void SearchBoxContainer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(SearchBoxContainer).Properties.IsRightButtonPressed)
        {
            VisualStateManager.GoToState(this, "SearchPointerPressed", true);
        }
    }

    private void SearchBoxContainer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, _isSearchPointerOver ? "SearchPointerOver" : "SearchPointerNormal", true);
    }

    private void SearchTextBox_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        HideSearchSuggestions();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchSuggestionsUntilTextChanges)
        {
            return;
        }

        _searchSuggestionsDismissedUntilInput = false;
        SearchSuggestionsList.SelectedItem = null;
        UpdateSearchTextAlignment();
        QueueSearchSuggestionsRefresh(forceImmediate: false);
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab)
        {
            SearchSuggestionsList.SelectedItem = null;
            HideSearchSuggestions();
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            MoveSearchSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Up)
        {
            MoveSearchSelection(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            SubmitSearch(preferSelectedSuggestion: true);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            DismissSearchFocus();
            e.Handled = true;
        }
    }

    private void SearchSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        SubmitSearch(preferSelectedSuggestion: false);
    }

    private void SearchSubmitButton_GotFocus(object sender, RoutedEventArgs e)
    {
        SetSearchFocusVisual(true);
    }

    private void SearchControl_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        DismissSearchFocus();
        e.Handled = true;
    }

    private void ShellRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Modal.Visibility == Visibility.Visible)
        {
            if (e.Key == VirtualKey.Escape)
            {
                ViewModel.RequestCloseModal();
                e.Handled = true;
            }

            return;
        }

        if (IsAltKeyDown() && e.Key == VirtualKey.Left && ViewModel.GoBackCommand.CanExecute(null))
        {
            ViewModel.GoBackCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (IsAltKeyDown() && e.Key == VirtualKey.Right && ViewModel.GoForwardCommand.CanExecute(null))
        {
            ViewModel.GoForwardCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.K && IsControlKeyDown())
        {
            FocusSearchBox();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape &&
            (SearchSuggestionsHost.Visibility == Visibility.Visible || IsFocusWithinSearchBox()))
        {
            DismissSearchFocus();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && ShellRailDrawerOverlay.Visibility == Visibility.Visible)
        {
            CloseShellRailDrawer();
            e.Handled = true;
        }
    }

    private void UpdateModalLayout(Size viewport)
    {
        double titleBarSafeHeight = AppDialogStyleCatalog.GetTitleBarSafeHeight();
        double contentHeight = Math.Max(0, viewport.Height - titleBarSafeHeight);
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(
            viewport.Width,
            contentHeight,
            AppDialogStyleCatalog.GetLayoutTokens());
        ModalContent.Width = metrics.MaximumWidth;
        ModalContent.MaxWidth = metrics.MaximumWidth;
        ModalContent.MaxHeight = metrics.MaximumHeight;
        ModalContent.Margin = new Thickness(
            metrics.OuterMargin,
            metrics.OuterMargin + titleBarSafeHeight,
            metrics.OuterMargin,
            metrics.OuterMargin);
    }

    private void SearchKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        FocusSearchBox();
        args.Handled = true;
    }

    private void MainWindow_SearchShortcutRequested(object? sender, EventArgs e)
    {
        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        if (!SearchTextBox.IsEnabled)
        {
            return;
        }

        if (!IsFocusWithinSearchBox() && TryGetFocusedElement() is Control control)
        {
            _searchRestoreTarget = control;
        }

        _searchSuggestionsDismissedUntilInput = false;
        ViewModel.TrackCommandSearchOpened();
        SearchTextBox.Focus(FocusState.Keyboard);
        SearchTextBox.SelectAll();
        UpdateSearchTextAlignment();
        QueueSearchSuggestionsRefresh(forceImmediate: true);
    }

    private void QueueSearchSuggestionsRefresh(bool forceImmediate) =>
        QueueShellWork(
            "shell.command_search",
            token => RefreshSearchSuggestionsAsync(forceImmediate, token));

    private async Task RefreshSearchSuggestionsAsync(
        bool forceImmediate,
        CancellationToken cancellationToken)
    {
        await ViewModel.UpdateCommandSearchAsync(
            SearchTextBox.Text,
            forceImmediate,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLoaded)
        {
            return;
        }

        UpdateSearchSuggestionsState();
    }

    private void QueueShellWork(
        string taskName,
        Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource? lifetime = _pageLifetime;
        if (lifetime is null || lifetime.IsCancellationRequested)
        {
            return;
        }

        UiTaskGuard.Observe(_taskCoordinator.RunAsync(operation, new ApplicationTaskOptions(taskName, GetActiveAccountPartition()), lifetime.Token), "ui-shell-page");
    }

    private string? GetActiveAccountPartition()
    {
        App app = (App)Application.Current;
        IAuthService authService = app.GetService<IAuthService>();
        IAccountService accountService = app.GetService<IAccountService>();
        long userId = authService.AuthenticatedUser?.Id ?? accountService.GetUser();
        string? token = authService.GetToken(userId);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (GitHubAuthenticationConstants.IsPublicAccessToken(token))
        {
            return "public";
        }

        return userId > 0
            ? userId.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private void SearchSuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ShellCommandSearchResult result)
        {
            ExecuteSearchResult(result);
        }
    }

    private void SearchSuggestionsList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = TryOpenSearchSuggestionFromSource(e.OriginalSource);
    }

    private void SearchSuggestionsList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = TryOpenSearchSuggestionFromSource(e.OriginalSource);
    }

    private bool TryOpenSearchSuggestionFromSource(object source)
    {
        if (SearchSuggestionsHost.Visibility != Visibility.Visible || source is not DependencyObject dependencyObject)
        {
            return false;
        }

        ShellCommandSearchResult? result = FindSearchSuggestionResult(dependencyObject);
        if (result is null)
        {
            return false;
        }

        ExecuteSearchResult(result);
        return true;
    }

    private static ShellCommandSearchResult? FindSearchSuggestionResult(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: ShellCommandSearchResult result })
            {
                return result;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void ExecuteSearchResult(ShellCommandSearchResult result)
    {
        _suppressSearchSuggestionsUntilTextChanges = true;
        HideSearchSuggestions();
        ViewModel.ExecuteSearchResult(result);
        SearchTextBox.Text = string.Empty;
        _suppressSearchSuggestionsUntilTextChanges = false;
        DismissSearchFocus();
    }

    private void SubmitSearch(bool preferSelectedSuggestion)
    {
        if (preferSelectedSuggestion && SearchSuggestionsList.SelectedItem is ShellCommandSearchResult result)
        {
            ExecuteSearchResult(result);
            return;
        }

        string query = SearchTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            _suppressSearchSuggestionsUntilTextChanges = true;
            HideSearchSuggestions();
            ViewModel.OpenSearchQuery(query);
            SearchTextBox.Text = string.Empty;
            _suppressSearchSuggestionsUntilTextChanges = false;
            DismissSearchFocus();
        }
    }

    private void UpdateSearchTextAlignment()
    {
        SearchTextBox.TextAlignment = SearchTextBox.FlowDirection == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;
    }

    private void UpdateSearchSuggestionsState()
    {
        bool shouldOpen = SearchTextBox.IsEnabled
            && !_searchSuggestionsDismissedUntilInput
            && IsFocusWithinSearchBox()
            && ViewModel.SearchResults.Count > 0;

        if (!shouldOpen)
        {
            HideSearchSuggestions();
            return;
        }

        UpdateSearchSuggestionsLayout();
        SearchSuggestionsHost.Visibility = Visibility.Visible;
    }

    private void UpdateSearchSuggestionsLayout()
    {
        if (SearchBoxContainer.ActualWidth <= 0)
        {
            return;
        }

        Point containerOrigin = SearchBoxContainer.TransformToVisual(ShellRoot).TransformPoint(new Point(0, 0));
        Point containerPoint = SearchBoxContainer.TransformToVisual(ShellRoot)
            .TransformPoint(new Point(0, SearchBoxContainer.ActualHeight + SearchSuggestionsTopOffset));

        SearchSuggestionsHost.Width = SearchBoxContainer.ActualWidth;
        SearchSuggestionsHost.MaxWidth = SearchBoxContainer.ActualWidth;
        SearchSuggestionsHost.MaxHeight = 420;
        Canvas.SetLeft(SearchSuggestionsHost, containerOrigin.X);
        Canvas.SetTop(SearchSuggestionsHost, containerPoint.Y);
    }

    private void MoveSearchSelection(int step)
    {
        if (ViewModel.SearchResults.Count == 0)
        {
            return;
        }

        if (SearchSuggestionsHost.Visibility != Visibility.Visible)
        {
            UpdateSearchSuggestionsState();
        }

        int count = SearchSuggestionsList.Items.Count;
        if (count == 0)
        {
            return;
        }

        int index = SearchSuggestionsList.SelectedIndex;
        index = index < 0
            ? step > 0 ? 0 : count - 1
            : Math.Clamp(index + step, 0, count - 1);

        try
        {
            _updatingSearchSelectionFromKeyboard = true;
            SearchSuggestionsList.SelectedIndex = index;
        }
        finally
        {
            _updatingSearchSelectionFromKeyboard = false;
        }

        if (SearchSuggestionsList.SelectedItem is object item)
        {
            SearchSuggestionsList.ScrollIntoView(item);
        }
    }

    private void HideSearchSuggestions()
    {
        SearchSuggestionsHost.Visibility = Visibility.Collapsed;
        if (!_updatingSearchSelectionFromKeyboard)
        {
            SearchSuggestionsList.SelectedItem = null;
        }
    }

    private void DismissSearchFocus()
    {
        _searchSuggestionsDismissedUntilInput = true;
        HideSearchSuggestions();

        if (_searchRestoreTarget is not null && !IsWithin(_searchRestoreTarget, SearchBoxContainer))
        {
            _ = _searchRestoreTarget.Focus(FocusState.Programmatic);
        }
        else
        {
            MoveFocusToSearchSink();
        }

        SetSearchFocusVisual(IsFocusWithinSearchBox());
    }

    private void MoveFocusToSearchSink()
    {
        _ = SearchFocusSink.Focus(FocusState.Programmatic);
    }

    private void SetSearchFocusVisual(bool focused)
    {
        VisualStateManager.GoToState(this, focused ? "SearchFocused" : "SearchUnfocused", true);
        if (_isShellSearchCompact)
        {
            SearchShortcutBadge.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsFocusWithinSearchBox()
    {
        DependencyObject? focusedElement = TryGetFocusedElement();
        return IsWithin(focusedElement, SearchBoxContainer);
    }

    private void Page_HistoryPointer(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerUpdateKind updateKind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        int historyButton = updateKind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed or
            Microsoft.UI.Input.PointerUpdateKind.XButton1Released => 1,
            Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed or
            Microsoft.UI.Input.PointerUpdateKind.XButton2Released => 2,
            _ => 0
        };
        if (historyButton == 0)
        {
            return;
        }

        long timestamp = Environment.TickCount64;
        bool duplicatePair = historyButton == _lastHistoryMouseButton &&
            timestamp - _lastHistoryMouseButtonTimestamp < 350;
        _lastHistoryMouseButton = historyButton;
        _lastHistoryMouseButtonTimestamp = timestamp;
        if (!duplicatePair)
        {
            RelayCommand command = historyButton == 1 ? ViewModel.GoBackCommand : ViewModel.GoForwardCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        }

        e.Handled = true;
    }

    private void SearchSuggestionsList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is ShellCommandSearchResult result)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, result.AutomationId);
            AutomationProperties.SetName(args.ItemContainer, result.AutomationName);
        }
    }

    private void ShellRepositoryList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is ShellRepositoryItem repository)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, repository.AutomationId);
            AutomationProperties.SetName(args.ItemContainer, repository.AutomationName);
        }
    }

    private void ShellRepositoryList_Loaded(object sender, RoutedEventArgs e)
    {
        _repositoryHeaderTransitionGeneration++;
        MorphTransitionSafety.TryResetVisibilityState(
            _repositoryHeaderTransition,
            RepositoryExpandedHeaderSurface,
            RepositoryShyHeaderSurface,
            toInitialState: !_isRepositoryHeaderShy);
        ResetRepositoryListReflow();
        ShellRepositoryList.LayoutUpdated -= ShellRepositoryList_LayoutUpdated;
        ShellRepositoryList.LayoutUpdated += ShellRepositoryList_LayoutUpdated;
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AttachRepositoryScrollViewer);
    }

    private void ShellRepositoryList_Unloaded(object sender, RoutedEventArgs e)
    {
        _repositoryHeaderTransitionGeneration++;
        MorphTransitionSafety.TryStop(_repositoryHeaderTransition);
        ShellRepositoryList.LayoutUpdated -= ShellRepositoryList_LayoutUpdated;
        DetachRepositoryScrollViewer();
    }

    private void ShellRepositoryList_LayoutUpdated(object? sender, object e) =>
        AttachRepositoryScrollViewer();

    private void ShellRepositoryList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _ = DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                AttachRepositoryScrollViewer);
        }
    }

    private void AttachRepositoryScrollViewer()
    {
        if (!ShellRepositoryList.IsLoaded)
        {
            return;
        }

        ShellRepositoryList.ApplyTemplate();
        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(ShellRepositoryList);
        if (scrollViewer is null)
        {
            return;
        }

        ShellRepositoryList.LayoutUpdated -= ShellRepositoryList_LayoutUpdated;

        if (ReferenceEquals(_repositoryScrollViewer, scrollViewer))
        {
            UpdateRepositoryHeaderForScroll(scrollViewer);
            return;
        }

        DetachRepositoryScrollViewer();
        _repositoryScrollViewer = scrollViewer;
        scrollViewer.ViewChanged += RepositoryScrollViewer_ViewChanged;
        _repositoryVerticalOffsetCallbackToken = scrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            RepositoryScrollViewer_ScrollPropertyChanged);
        _repositoryScrollableHeightCallbackToken = scrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            RepositoryScrollViewer_ScrollPropertyChanged);

        _lastRepositoryScrollOffset = scrollViewer.VerticalOffset;
        _repositoryUpwardRevealTravel = 0;
        _repositoryDownwardRehideTravel = 0;
        _repositoryHeaderRevealedByUpwardScroll = false;
        _isRepositoryScrollHeaderShy =
            scrollViewer.ScrollableHeight > 0 &&
            scrollViewer.VerticalOffset >= RepositoryShyHeaderStartOffset;
        SetRepositoryHeaderShy(_isRepositoryScrollHeaderShy, animate: false);
    }

    private void DetachRepositoryScrollViewer()
    {
        if (_repositoryScrollViewer is not ScrollViewer scrollViewer)
        {
            return;
        }

        scrollViewer.ViewChanged -= RepositoryScrollViewer_ViewChanged;
        scrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            _repositoryVerticalOffsetCallbackToken);
        scrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            _repositoryScrollableHeightCallbackToken);
        _repositoryScrollViewer = null;
        _repositoryVerticalOffsetCallbackToken = 0;
        _repositoryScrollableHeightCallbackToken = 0;
    }

    private void RepositoryScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateRepositoryHeaderForScroll(scrollViewer);
        }
    }

    private void RepositoryScrollViewer_ScrollPropertyChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateRepositoryHeaderForScroll(scrollViewer);
        }
    }

    private void UpdateRepositoryHeaderForScroll(ScrollViewer scrollViewer)
    {
        if (!ReferenceEquals(_repositoryScrollViewer, scrollViewer))
        {
            return;
        }

        if (_isRepositoryHeaderLayoutTransitionActive)
        {
            _lastRepositoryScrollOffset = scrollViewer.VerticalOffset;
            return;
        }

        if (scrollViewer.ScrollableHeight <= 0)
        {
            _lastRepositoryScrollOffset = 0;
            _repositoryUpwardRevealTravel = 0;
            _repositoryDownwardRehideTravel = 0;
            _repositoryHeaderRevealedByUpwardScroll = false;
            _isRepositoryScrollHeaderShy = false;
            SetRepositoryHeaderShy(false, animate: true);
            return;
        }

        double offset = scrollViewer.VerticalOffset;
        double delta = offset - _lastRepositoryScrollOffset;
        _lastRepositoryScrollOffset = offset;

        if (_isRepositoryScrollHeaderShy)
        {
            if (offset <= RepositoryShyHeaderRestoreOffset)
            {
                RevealRepositoryScrollHeader(revealedByUpwardScroll: false);
            }
            else if (delta < -RepositoryScrollDirectionEpsilon)
            {
                _repositoryUpwardRevealTravel += -delta;
                if (_repositoryUpwardRevealTravel >= RepositoryShyHeaderRevealTravel)
                {
                    RevealRepositoryScrollHeader(revealedByUpwardScroll: true);
                }
            }
            else if (delta > RepositoryScrollDirectionEpsilon)
            {
                _repositoryUpwardRevealTravel = Math.Max(0, _repositoryUpwardRevealTravel - delta);
            }

            return;
        }

        if (offset <= RepositoryShyHeaderRestoreOffset)
        {
            _repositoryHeaderRevealedByUpwardScroll = false;
            _repositoryDownwardRehideTravel = 0;
        }
        else if (_repositoryHeaderRevealedByUpwardScroll)
        {
            if (delta > RepositoryScrollDirectionEpsilon)
            {
                _repositoryDownwardRehideTravel += delta;
                if (_repositoryDownwardRehideTravel >= RepositoryShyHeaderRehideTravel)
                {
                    HideRepositoryScrollHeader();
                }
            }
            else if (delta < -RepositoryScrollDirectionEpsilon)
            {
                _repositoryDownwardRehideTravel = 0;
            }
        }
        else if (offset >= RepositoryShyHeaderStartOffset)
        {
            HideRepositoryScrollHeader();
        }
    }

    private void RevealRepositoryScrollHeader(bool revealedByUpwardScroll)
    {
        _isRepositoryScrollHeaderShy = false;
        _repositoryHeaderRevealedByUpwardScroll = revealedByUpwardScroll;
        _repositoryUpwardRevealTravel = 0;
        _repositoryDownwardRehideTravel = 0;
        SetRepositoryHeaderShy(false, animate: true);
    }

    private void HideRepositoryScrollHeader()
    {
        _isRepositoryScrollHeaderShy = true;
        _repositoryHeaderRevealedByUpwardScroll = false;
        _repositoryUpwardRevealTravel = 0;
        _repositoryDownwardRehideTravel = 0;
        SetRepositoryHeaderShy(true, animate: true);
    }

    private void SetRepositoryHeaderShy(bool isShy, bool animate)
    {
        if (_isRepositoryHeaderShy == isShy)
        {
            return;
        }

        _isRepositoryHeaderShy = isShy;
        int generation = ++_repositoryHeaderTransitionGeneration;
        if (!animate || !RepositoryExpandedHeaderSurface.IsLoaded || !AreAnimationsEnabled())
        {
            if (MorphTransitionSafety.TryResetVisibilityState(
                _repositoryHeaderTransition,
                RepositoryExpandedHeaderSurface,
                RepositoryShyHeaderSurface,
                toInitialState: !isShy))
            {
                RepositoryRailLayout.UpdateLayout();
                ResetRepositoryListReflow();
            }

            return;
        }

        UiTaskGuard.Observe(AnimateRepositoryHeaderAsync(isShy, generation), "ui-shell-page");
    }

    private async Task AnimateRepositoryHeaderAsync(bool isShy, int generation)
    {
        try
        {
            _isRepositoryHeaderLayoutTransitionActive = true;
            bool reverseFromSettledShyState =
                !isShy && _repositoryHeaderTransition.IsTargetState && !_repositoryHeaderTransition.IsAnimating;
            double previousListTop = reverseFromSettledShyState
                ? GetElementTop(RepositoryListHost, RepositoryRailLayout)
                : 0;

            Task headerAnimation = isShy
                ? _repositoryHeaderTransition.StartAsync(forceUpdateAnimatedElements: true)
                : _repositoryHeaderTransition.ReverseAsync(forceUpdateAnimatedElements: true);

            if (isShy)
            {
                double reclaimedHeight = Math.Max(
                    0,
                    RepositoryExpandedHeaderSurface.ActualHeight - RepositoryShyHeaderSurface.ActualHeight);
                AnimateRepositoryListReflow(
                    new Vector3(0, (float)-reclaimedHeight, 0),
                    RepositoryShyHeaderDuration);
            }
            else if (reverseFromSettledShyState)
            {
                double expandedListTop = GetElementTop(RepositoryListHost, RepositoryRailLayout);
                SetRepositoryListReflowImmediately(
                    new Vector3(0, (float)(previousListTop - expandedListTop), 0));
                AnimateRepositoryListReflow(Vector3.Zero, RepositoryShyHeaderDuration);
            }
            else
            {
                AnimateRepositoryListReflow(Vector3.Zero, RepositoryShyHeaderDuration);
            }

            await headerAnimation;
            if (generation != _repositoryHeaderTransitionGeneration)
            {
                return;
            }

            MorphTransitionSafety.TrySetStableState(
                _repositoryHeaderTransition,
                RepositoryExpandedHeaderSurface,
                RepositoryShyHeaderSurface,
                isTargetState: isShy);
            RepositoryRailLayout.UpdateLayout();
            ResetRepositoryListReflow();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (generation != _repositoryHeaderTransitionGeneration)
        {
        }
        catch (Exception ex) when (generation == _repositoryHeaderTransitionGeneration)
        {
            JitHub.WinUI.App.LogHandledException(ex, "ui-shell-repository-header-morph");
            if (MorphTransitionSafety.TryResetVisibilityState(
                _repositoryHeaderTransition,
                RepositoryExpandedHeaderSurface,
                RepositoryShyHeaderSurface,
                toInitialState: !isShy))
            {
                RepositoryRailLayout.UpdateLayout();
                ResetRepositoryListReflow();
            }
        }
        finally
        {
            if (generation == _repositoryHeaderTransitionGeneration)
            {
                _isRepositoryHeaderLayoutTransitionActive = false;
                if (_repositoryScrollViewer is ScrollViewer scrollViewer)
                {
                    _lastRepositoryScrollOffset = scrollViewer.VerticalOffset;
                }
            }
        }
    }

    private void AnimateRepositoryListReflow(Vector3 translation, TimeSpan duration)
    {
        RepositoryListHost.TranslationTransition = new Vector3Transition
        {
            Components = Vector3TransitionComponents.Y,
            Duration = duration
        };
        RepositoryListHost.Translation = translation;
    }

    private void SetRepositoryListReflowImmediately(Vector3 translation)
    {
        RepositoryListHost.TranslationTransition = null;
        RepositoryListHost.Translation = translation;
    }

    private void ResetRepositoryListReflow() =>
        SetRepositoryListReflowImmediately(Vector3.Zero);

    private static double GetElementTop(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformPoint(new Point()).Y;

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

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
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

    private void Page_PointerReleased(object sender, PointerRoutedEventArgs e)
    {

        if (SearchSuggestionsHost.Visibility != Visibility.Visible)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            HideSearchSuggestions();
            return;
        }

        if (IsWithin(source, SearchBoxContainer) || IsWithin(source, SearchSuggestionsHost))
        {
            return;
        }

        HideSearchSuggestions();
    }

    private void RepositoryFilterSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_repositoryFiltersInitialized ||
            _synchronizingRepositoryFilters ||
            sender is not Segmented segmented)
        {
            return;
        }

        int selectedIndex = Math.Clamp(segmented.SelectedIndex, 0, 2);
        _synchronizingRepositoryFilters = true;
        try
        {
            if (ShellRepositoryExpandedFilter.SelectedIndex != selectedIndex)
            {
                ShellRepositoryExpandedFilter.SelectedIndex = selectedIndex;
            }

            if (ShellRepositoryCompactFilter.SelectedIndex != selectedIndex)
            {
                ShellRepositoryCompactFilter.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            _synchronizingRepositoryFilters = false;
        }

        string filter = selectedIndex switch
        {
            1 => "Private",
            2 => "Forked",
            _ => "Public"
        };

        if (ViewModel.SetRepositoryFilterCommand.CanExecute(filter))
        {
            ViewModel.SetRepositoryFilterCommand.Execute(filter);
        }
    }

    private void RepositoryFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.RepositoryFilterText = sender is TextBox textBox ? textBox.Text : string.Empty;
    }

    private void RefreshRepositoriesButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await ViewModel.RefreshRepositoryRailAsync();
        }, "ui-shell-page");
    }

    private void ShowMoreRepositoriesButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenManageRepositories();
    }

    private void ShellRailDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_canShellRailInline)
        {
            _isShellRailCollapsedByUser = !_isShellRailCompact;
            ViewModel.TrackShellCommand(
                TelemetryTaxonomy.Actions.Drawer,
                _isShellRailCollapsedByUser ? "collapsed" : "expanded");
            ApplyShellResponsiveLayout(ActualWidth);
            _ = ShellRailDrawerButton.Focus(FocusState.Programmatic);
            return;
        }

        if (ShellRailDrawerOverlay.Visibility == Visibility.Visible && _shellRailDrawerAnimator.IsOpen)
        {
            ViewModel.TrackShellCommand(TelemetryTaxonomy.Actions.Drawer, "dismissed");
            CloseShellRailDrawer();
            return;
        }

        ViewModel.TrackShellCommand(TelemetryTaxonomy.Actions.Drawer, TelemetryTaxonomy.Results.Opened);
        OpenShellRailDrawer();
    }

    private void ShellRailDrawerOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        CloseShellRailDrawer();
        e.Handled = true;
    }

    private void ShellRailDrawerOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsWithin(source, ShellRailDrawer))
        {
            return;
        }

        CloseShellRailDrawer();
        e.Handled = true;
    }

    private void ShellRailDrawer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsShellNavigationOrRepositorySource(source))
        {
            QueueCloseShellRailDrawer();
        }
    }

    private void ShellRailNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShellNavigationItem item } &&
            item.Command.CanExecute(null))
        {
            item.Command.Execute(null);
        }

        QueueCloseShellRailDrawer();
    }

    private void ShellRailRepositoryButton_Click(object sender, RoutedEventArgs e)
        => QueueCloseShellRailDrawer();

    private void OpenShellRailDrawer()
    {
        if (!_isShellRailCompact)
        {
            UpdateShellRailPlacement(isCompact: true);
        }

        if (TryGetFocusedElement() is Control focusedControl &&
            !IsWithin(focusedControl, ShellRailDrawerOverlay))
        {
            _shellRailDrawerRestoreTarget = focusedControl;
        }

        if (ShellRailDrawerOverlay.Visibility != Visibility.Visible)
        {
            _shellRailDrawerAnimator.SetOpen(false, animate: false);
        }

        ShellRailDrawerOverlay.Visibility = Visibility.Visible;
        _shellRailDrawerAnimator.SetOpen(true);
        UpdateShellRailToggleAffordance();
        _ = ShellRailDrawer.Focus(FocusState.Programmatic);
    }

    private void CloseShellRailDrawer(bool restoreFocus = true, bool animate = true)
    {
        if (ShellRailDrawerOverlay.Visibility != Visibility.Visible && !_shellRailDrawerAnimator.IsAnimating)
        {
            return;
        }

        _shellRailDrawerAnimator.SetOpen(false, animate, () => CompleteShellRailDrawerClose(restoreFocus));
    }

    private void CompleteShellRailDrawerClose(bool restoreFocus)
    {
        if (_shellRailDrawerAnimator.IsOpen)
        {
            return;
        }

        ShellRailDrawerOverlay.Visibility = Visibility.Collapsed;
        UpdateShellRailToggleAffordance();
        if (!restoreFocus)
        {
            _shellRailDrawerRestoreTarget = null;
            return;
        }

        if (_shellRailDrawerRestoreTarget is not null)
        {
            _ = _shellRailDrawerRestoreTarget.Focus(FocusState.Programmatic);
            _shellRailDrawerRestoreTarget = null;
        }
        else
        {
            MoveFocusToSearchSink();
        }
    }

    private void QueueCloseShellRailDrawer()
    {
        if (ShellRailDrawerOverlay.Visibility == Visibility.Visible)
        {
            _ = DispatcherQueue.TryEnqueue(() => CloseShellRailDrawer());
        }
    }

    private void UpdateShellRailPlacement(bool isCompact)
    {
        ShellRail.Width = ShellResponsiveLayout.RailWidth;
        ShellRail.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        ShellRailDrawerButton.Visibility = Visibility.Visible;
        if (_isShellRailCompact == isCompact &&
            ((isCompact && ReferenceEquals(ShellRailDrawerPresenter.Content, ShellRailContent)) ||
             (!isCompact && ReferenceEquals(ShellRail.Child, ShellRailContent))))
        {
            if (_canShellRailInline)
            {
                CloseShellRailDrawer(restoreFocus: false, animate: false);
            }
            else
            {
                _shellRailDrawerAnimator.SyncToCurrentState();
            }

            return;
        }

        _isShellRailCompact = isCompact;
        if (isCompact)
        {
            ShellRail.Child = null;
            ShellRailDrawerPresenter.Content = ShellRailContent;
            _shellRailDrawerAnimator.SetOpen(false, animate: false);
            ShellRailDrawerOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        CloseShellRailDrawer(restoreFocus: false, animate: false);
        ShellRailDrawerPresenter.Content = null;
        ShellRail.Child = ShellRailContent;
    }

    private void UpdateShellRailToggleAffordance()
    {
        string label;
        if (_canShellRailInline)
        {
            label = _isShellRailCompact
                ? LocalizedResourceText.GetString("Shell.Navigation.ExpandPane", "Expand navigation pane")
                : LocalizedResourceText.GetString("Shell.Navigation.CollapsePane", "Collapse navigation pane");
        }
        else
        {
            bool closesDrawer = ShellRailDrawerOverlay.Visibility == Visibility.Visible &&
                _shellRailDrawerAnimator.IsOpen;
            label = closesDrawer
                ? LocalizedResourceText.GetString("Shell.Navigation.Close", "Close navigation")
                : LocalizedResourceText.GetString("Shell.Navigation.Open", "Open navigation");
        }

        AutomationProperties.SetName(ShellRailDrawerButton, label);
        ToolTipService.SetToolTip(ShellRailDrawerButton, label);
    }

    private void ApplyShellResponsiveLayout(double windowWidth)
    {
        ShellResponsiveState state = ShellResponsiveLayout.Calculate(windowWidth, _isShellRailCollapsedByUser);
        _canShellRailInline = state.CanRailInline;
        _isShellSearchCompact = windowWidth <= 900;
        TitleLogoColumn.Width = new GridLength(state.TitleAreaWidth);
        AppLogoShellPage.Visibility = state.IsRailInline ? Visibility.Visible : Visibility.Collapsed;
        ShellTitleText.Visibility = state.IsRailInline ? Visibility.Visible : Visibility.Collapsed;
        SearchSubmitButton.Visibility = _isShellSearchCompact ? Visibility.Collapsed : Visibility.Visible;
        SearchShortcutBadge.Visibility = _isShellSearchCompact || IsFocusWithinSearchBox()
            ? Visibility.Collapsed
            : Visibility.Visible;
        string searchPlaceholder = _isShellSearchCompact
            ? LocalizedResourceText.GetString("Shell.Search.CompactPlaceholder", SearchTextBox.PlaceholderText)
            : LocalizedResourceText.GetString("Shell.Search.Placeholder", SearchTextBox.PlaceholderText);
        SearchTextBox.PlaceholderText = searchPlaceholder;
        AutomationProperties.SetHelpText(SearchTextBox, searchPlaceholder);
        UpdateShellRailPlacement(!state.IsRailInline);
        UpdateShellRailToggleAffordance();
    }

    private double GetShellRailDrawerWidth()
    {
        Border? drawer = ShellRailDrawer;
        if (drawer?.ActualWidth > 0)
        {
            return drawer.ActualWidth;
        }

        return drawer?.Width > 0 ? drawer.Width : 286;
    }

    private static bool IsShellNavigationOrRepositorySource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement frameworkElement)
            {
                string automationId = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(frameworkElement);
                if (automationId.StartsWith("ShellNav_", StringComparison.Ordinal) ||
                    automationId.StartsWith("ShellRepo_", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void QueueTitleBarPassthroughUpdate(MainWindow mainWindow)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            mainWindow.SetTitleBarPassthroughRegions(
                SearchBoxContainer,
                SearchSubmitButton,
                ShellRailDrawerButton,
                NewRepositoryButton,
                NotificationsTopButton,
                SettingsTopButton,
                ProfileTopButton);
        });
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject? ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private DependencyObject? TryGetFocusedElement()
    {
        XamlRoot? xamlRoot = XamlRoot;
        if (xamlRoot is null)
        {
            return null;
        }

        try
        {
            return FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070057))
        {
            return null;
        }
    }

    private static bool IsControlKeyDown()
    {
        CoreVirtualKeyStates controlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (controlState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static bool IsAltKeyDown()
    {
        CoreVirtualKeyStates menuState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        return (menuState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static List<GitHubRepository> CreateAutomationSearchRepositories() =>
    [
        CreateAutomationRepository(1, "flutter", "flutter", "Flutter makes it easy and fast to build beautiful apps."),
        CreateAutomationRepository(2, "flutter", "plugins", "Plugins for Flutter maintained by the Flutter team."),
        CreateAutomationRepository(3, "iampawan", "FlutterExampleApps", "Example Flutter apps for UI and architecture testing."),
        CreateAutomationRepository(4, "Solido", "awesome-flutter", "A curated list of Flutter resources."),
        CreateAutomationRepository(5, "wger-project", "flutter", "Flutter client for wger."),
        CreateAutomationRepository(6, "kaina404", "FlutterDouBan", "DouBan client written in Flutter."),
        CreateAutomationRepository(7, "toly1994328", "FlutterUnit", "Flutter samples and widgets."),
        CreateAutomationRepository(8, "flutter", "engine", "The Flutter engine.")
    ];

    private static GitHubRepository CreateAutomationRepository(long id, string owner, string name, string description) =>
        new()
        {
            Id = id,
            Name = name,
            FullName = $"{owner}/{name}",
            Description = description,
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{owner}/{name}",
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = $"https://github.com/{owner}"
            }
        };

    private sealed class TextScalingCalculator : IScalingCalculator
    {
        public Vector2 GetScaling(UIElement source, UIElement target)
        {
            if (source is not TextBlock sourceText ||
                target is not TextBlock targetText ||
                sourceText.FontSize <= 0)
            {
                return Vector2.One;
            }

            float scale = (float)(targetText.FontSize / sourceText.FontSize);
            return float.IsFinite(scale) && scale > 0 ? new Vector2(scale) : Vector2.One;
        }
    }
}
