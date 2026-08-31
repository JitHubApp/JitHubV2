using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using CommunityToolkit.Mvvm.Input;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class DashboardPage : Page
{
    private const double ShyHeaderStartOffset = 56;
    private const double ShyHeaderRestoreOffset = 8;
    private const double ShyHeaderRevealTravel = 64;
    private const double ShyHeaderRehideTravel = 24;
    private const double OverviewShyStartInset = 56;
    private const double OverviewShyRestoreInset = 8;
    private const double ScrollDirectionEpsilon = 0.5;
    private const double SideDrawerFallbackWidth = 360;
    private const int VkShift = 0x10;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;
    private static readonly TimeSpan ShyHeaderDuration = AppMotionTokens.MediumDuration;
    private static readonly IScalingCalculator HeaderGreetingScaling = new TextScalingCalculator();

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);

    private readonly ModalService _modalService;
    private readonly IGitHubStarLibraryService _starLibraryService;
    private readonly NotificationInboxState _notificationInboxState;
    private readonly SlideDrawerAnimator _sideDrawerAnimator;
    private readonly TransitionHelper _headerTransition;
    private TransitionHelper? _overviewTransition;
    private FrameworkElement? _overviewMorphSource;
    private bool _initialized;
    private bool _isCustomizeDialogShowing;
    private ModalSession? _customizeModalSession;
    private bool _isSideDrawerOpen;
    private bool _isSideDrawerShiftPressed;
    private Control? _sideDrawerRestoreTarget;
    private long _verticalOffsetCallbackToken;
    private long _scrollableHeightCallbackToken;
    private bool _scrollCallbacksRegistered;
    private double _lastScrollOffset;
    private double _upwardRevealTravel;
    private double _downwardRehideTravel;
    private bool _headerRevealedByUpwardScroll;
    private bool _isScrollHeaderShy;
    private bool _isHeaderShy;
    private int _headerTransitionGeneration;
    private long _overviewVerticalOffsetCallbackToken;
    private long _overviewScrollableHeightCallbackToken;
    private bool _overviewScrollCallbacksRegistered;
    private bool _isOverviewScrollHeaderShy;
    private bool _isOverviewShy;
    private int _overviewTransitionGeneration;
    private ProductPerformanceScrollProbe? _performanceScrollProbe;

    public DashboardPageViewModel ViewModel { get; }

    public DashboardPage()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<DashboardPageViewModel>();
        _modalService = ((App)Application.Current).GetService<ModalService>();
        _starLibraryService = ((App)Application.Current).GetService<IGitHubStarLibraryService>();
        _notificationInboxState = ((App)Application.Current).GetService<NotificationInboxState>();
        InitializeComponent();
        _headerTransition = new TransitionHelper
        {
            Source = DashboardHeaderGrid,
            Target = DashboardShyHeaderSurface,
            Duration = ShyHeaderDuration,
            ReverseDuration = ShyHeaderDuration,
            SourceToggleMethod = VisualStateToggleMethod.ByVisibility,
            TargetToggleMethod = VisualStateToggleMethod.ByVisibility,
            Configs =
            [
                new TransitionConfig
                {
                    Id = "DashboardHeaderChrome",
                    ScaleMode = ScaleMode.Scale,
                    EnableClipAnimation = true
                },
                new TransitionConfig
                {
                    Id = "DashboardHeaderGreeting",
                    ScaleMode = ScaleMode.Custom,
                    CustomScalingCalculator = HeaderGreetingScaling
                },
                new TransitionConfig
                {
                    Id = "DashboardHeaderCustomize",
                    ScaleMode = ScaleMode.Scale,
                    EnableClipAnimation = true
                },
                new TransitionConfig
                {
                    Id = "DashboardHeaderOverview",
                    ScaleMode = ScaleMode.Scale,
                    EnableClipAnimation = true
                }
            ]
        };
        DashboardSideDrawer.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(DashboardSideDrawer_KeyDown), true);
        DashboardSideDrawer.AddHandler(PreviewKeyUpEvent, new KeyEventHandler(DashboardSideDrawer_KeyUp), true);
        _sideDrawerAnimator = new SlideDrawerAnimator(
            DashboardSideDrawerTransform,
            SlideDrawerEdge.Right,
            GetSideDrawerWidth);
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _headerTransitionGeneration++;
        MorphTransitionSafety.TryResetVisibilityState(
            _headerTransition,
            DashboardHeaderGrid,
            DashboardShyHeaderSurface,
            toInitialState: !_isHeaderShy);
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = ProductPerformanceScrollProbe.TryStart(
            DashboardMainRailScrollViewer,
            DashboardMainRailScrollViewer);
        UiTaskGuard.Run(async () =>
        {
            FocusManager.GettingFocus -= FocusManager_GettingFocus;
            FocusManager.GettingFocus += FocusManager_GettingFocus;
            FocusManager.LosingFocus -= FocusManager_LosingFocus;
            FocusManager.LosingFocus += FocusManager_LosingFocus;
            _starLibraryService.Changed -= StarLibraryService_Changed;
            _starLibraryService.Changed += StarLibraryService_Changed;
            _notificationInboxState.PropertyChanged -= NotificationInboxState_PropertyChanged;
            _notificationInboxState.PropertyChanged += NotificationInboxState_PropertyChanged;
            ViewModel.ApplySharedNotificationReadStates();
            try
            {
                ApplyResponsiveLayout(ActualWidth);
                AttachHeaderScrollTracking();
                AttachOverviewScrollTracking();
                if (_initialized)
                {
                    UpdateOverviewForScroll(DashboardSideRailScrollViewer);
                    CommitPerformanceReadiness();
                    return;
                }

                _initialized = true;
                await ViewModel.InitializeAsync();
                UpdateHeaderForScroll(DashboardMainRailScrollViewer);
                UpdateOverviewForScroll(DashboardSideRailScrollViewer);
                CommitPerformanceReadiness();
            }
            catch (Exception ex)
            {
                JitHub.WinUI.App.LogHandledException(ex, "ui-dashboard-page-initialize");
            }
        }, "ui-dashboard-page");
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void DashboardCustomizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsCustomizeDialogOpen && ViewModel.OpenCustomizeCommand.CanExecute(null))
        {
            ViewModel.OpenCustomizeCommand.Execute(null);
        }
    }

    private void CommitPerformanceReadiness() =>
        ProductPerformanceReadiness.CommitRoute(
            "home",
            ProductPerformanceReadiness.CountIdentity(ViewModel.MainWidgets.Count + ViewModel.SideWidgets.Count));

    private void DashboardOverviewDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        _sideDrawerRestoreTarget = sender as Control ?? TryGetFocusedControl();
        SetSideDrawerOpen(true);
    }

    private void DashboardSideDrawerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        SetSideDrawerOpen(false);
    }

    private void ApplyResponsiveLayout(double fallbackWidth)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(
            fallbackWidth,
            WorkspaceChromeContracts.Dashboard);
        WorkspaceChromeVisuals.ApplyRoot(DashboardRoot, chrome);
        WorkspaceChromeVisuals.ApplyHeader(DashboardHeaderGrid, chrome);
        double availableWidth = GetVisibleContentWidth(fallbackWidth);
        double boardWidth = Math.Min(1160, availableWidth);
        DashboardWidgetBoard.Width = boardWidth;

        bool compact = chrome.Mode != WorkspaceChromeMode.Wide;
        bool showOverviewDrawerButton = compact;
        bool hideGreeting = boardWidth < 600;
        bool iconOnlyActions = !chrome.ShowActionLabels;

        ViewModel.SetCompactSideRail(compact);
        if (compact)
        {
            SetOverviewShy(false, animate: false);
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(() => UpdateOverviewForScroll(DashboardSideRailScrollViewer));
        }

        DashboardSideColumn.Width = compact ? new GridLength(0) : new GridLength(344);
        DashboardWidgetBoard.ColumnSpacing = compact ? 0 : 16;
        double mainRailWidth = compact ? boardWidth : Math.Max(0, boardWidth - 344 - 16);
        ViewModel.SetDashboardCardWidths(mainRailWidth);

        if (!showOverviewDrawerButton && (_isSideDrawerOpen || ViewModel.IsSideRailDrawerOpen))
        {
            SetSideDrawerOpen(false, animate: false);
        }

        Grid.SetColumn(DashboardHeaderActions, 1);
        Grid.SetRow(DashboardHeaderActions, 0);
        DashboardHeaderActions.HorizontalAlignment = HorizontalAlignment.Right;
        DashboardHeaderActions.Margin = new Thickness(0);
        DashboardGreetingStack.Visibility = hideGreeting ? Visibility.Collapsed : Visibility.Visible;
        DashboardShyGreetingText.Visibility = hideGreeting ? Visibility.Collapsed : Visibility.Visible;
        DashboardOverviewDrawerButton.Visibility = showOverviewDrawerButton ? Visibility.Visible : Visibility.Collapsed;
        DashboardShyOverviewDrawerButton.Visibility = showOverviewDrawerButton ? Visibility.Visible : Visibility.Collapsed;
        DashboardOverviewButtonText.Visibility = Visibility.Collapsed;
        WorkspaceChromeVisuals.ApplyActionLabel(DashboardCustomizeButtonText, chrome);
        DashboardOverviewDrawerButton.MinWidth = 38;
        DashboardOverviewDrawerButton.Width = 38;
        DashboardOverviewDrawerButton.Padding = new Thickness(0);
        WorkspaceChromeVisuals.ApplyActionButton(
            DashboardCustomizeButton,
            chrome,
            hasVisibleLabel: !iconOnlyActions);

        if (!_sideDrawerAnimator.IsAnimating)
        {
            _sideDrawerAnimator.SyncToCurrentState();
        }

    }

    private void AttachHeaderScrollTracking()
    {
        if (_scrollCallbacksRegistered)
        {
            UpdateHeaderForScroll(DashboardMainRailScrollViewer);
            return;
        }

        _verticalOffsetCallbackToken = DashboardMainRailScrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            DashboardMainRailScrollPropertyChanged);
        _scrollableHeightCallbackToken = DashboardMainRailScrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            DashboardMainRailScrollPropertyChanged);
        _scrollCallbacksRegistered = true;
        _lastScrollOffset = DashboardMainRailScrollViewer.VerticalOffset;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        _headerRevealedByUpwardScroll = false;
        _isScrollHeaderShy = DashboardMainRailScrollViewer.ScrollableHeight > 0 &&
            DashboardMainRailScrollViewer.VerticalOffset >= ShyHeaderStartOffset;
        SetHeaderShy(_isScrollHeaderShy, animate: false);
    }

    private void DetachHeaderScrollTracking()
    {
        if (!_scrollCallbacksRegistered)
        {
            return;
        }

        DashboardMainRailScrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            _verticalOffsetCallbackToken);
        DashboardMainRailScrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            _scrollableHeightCallbackToken);
        _verticalOffsetCallbackToken = 0;
        _scrollableHeightCallbackToken = 0;
        _scrollCallbacksRegistered = false;
    }

    private void AttachOverviewScrollTracking()
    {
        if (_overviewScrollCallbacksRegistered)
        {
            UpdateOverviewForScroll(DashboardSideRailScrollViewer);
            return;
        }

        _overviewVerticalOffsetCallbackToken = DashboardSideRailScrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            DashboardSideRailScrollPropertyChanged);
        _overviewScrollableHeightCallbackToken = DashboardSideRailScrollViewer.RegisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            DashboardSideRailScrollPropertyChanged);
        _overviewScrollCallbacksRegistered = true;
        UpdateOverviewForScroll(DashboardSideRailScrollViewer);
    }

    private void DetachOverviewScrollTracking()
    {
        if (!_overviewScrollCallbacksRegistered)
        {
            return;
        }

        DashboardSideRailScrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.VerticalOffsetProperty,
            _overviewVerticalOffsetCallbackToken);
        DashboardSideRailScrollViewer.UnregisterPropertyChangedCallback(
            ScrollViewer.ScrollableHeightProperty,
            _overviewScrollableHeightCallbackToken);
        _overviewVerticalOffsetCallbackToken = 0;
        _overviewScrollableHeightCallbackToken = 0;
        _overviewScrollCallbacksRegistered = false;
    }

    private void DashboardMainRailScrollViewer_ViewChanged(
        object? sender,
        ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateHeaderForScroll(scrollViewer);
        }
    }

    private void DashboardMainRailScrollPropertyChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateHeaderForScroll(scrollViewer);
        }
    }

    private void DashboardSideRailScrollViewer_ViewChanged(
        object? sender,
        ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateOverviewForScroll(scrollViewer);
        }
    }

    private void DashboardSideRailScrollPropertyChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateOverviewForScroll(scrollViewer);
        }
    }

    private void DashboardWidgetCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } card &&
            string.Equals(id, DashboardWidgetIds.Overview, StringComparison.Ordinal) &&
            IsWithin(card, DashboardSideRailItems))
        {
            ConfigureOverviewMorphSource(card);
        }
    }

    private void DashboardWidgetCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _overviewMorphSource))
        {
            ClearOverviewMorphSource();
        }
    }

    private void ConfigureOverviewMorphSource(FrameworkElement source)
    {
        if (ReferenceEquals(_overviewMorphSource, source))
        {
            UpdateOverviewForScroll(DashboardSideRailScrollViewer);
            return;
        }

        ClearOverviewMorphSource();
        _overviewMorphSource = source;
        _overviewTransition = new TransitionHelper
        {
            Source = source,
            Target = DashboardOverviewShySurface,
            Duration = ShyHeaderDuration,
            ReverseDuration = ShyHeaderDuration,
            DefaultOpacityTransitionProgressKey = AppMotionTokens.ShyHeaderOpacityTransitionProgressKey,
            SourceToggleMethod = VisualStateToggleMethod.ByIsVisible,
            TargetToggleMethod = VisualStateToggleMethod.ByIsVisible,
            Configs =
            [
                new TransitionConfig { Id = "DashboardOverviewMetricRepositories", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "DashboardOverviewMetricIssues", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "DashboardOverviewMetricPullRequests", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true },
                new TransitionConfig { Id = "DashboardOverviewMetricFollowers", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }
            ]
        };
        _isOverviewScrollHeaderShy = false;
        _isOverviewShy = false;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_overviewMorphSource, source))
            {
                UpdateOverviewForScroll(DashboardSideRailScrollViewer);
            }
        });
    }

    private void ClearOverviewMorphSource()
    {
        _overviewTransitionGeneration++;
        if (_overviewTransition is TransitionHelper transition)
        {
            MorphTransitionSafety.TryStop(transition);
            if (_overviewMorphSource is FrameworkElement source)
            {
                MorphTransitionSafety.TryReset(
                    transition,
                    source,
                    DashboardOverviewShySurface,
                    toInitialState: true);
            }
        }

        _overviewTransition = null;
        _overviewMorphSource = null;
        _isOverviewScrollHeaderShy = false;
        _isOverviewShy = false;
        DashboardOverviewShySurface.Visibility = Visibility.Collapsed;
    }

    private void UpdateOverviewForScroll(ScrollViewer scrollViewer)
    {
        if (!ReferenceEquals(scrollViewer, DashboardSideRailScrollViewer))
        {
            return;
        }

        FrameworkElement? source = _overviewMorphSource;
        if (_overviewTransition is null ||
            source is not { IsLoaded: true } ||
            !ViewModel.IsSideRailExpanded ||
            scrollViewer.ScrollableHeight <= 0)
        {
            _isOverviewScrollHeaderShy = false;
            SetOverviewShy(false, animate: true);
            return;
        }

        double sourceTop;
        try
        {
            sourceTop = source.TransformToVisual(DashboardSideRailItems).TransformPoint(new Point()).Y;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException)
        {
            SetOverviewShy(false, animate: false);
            return;
        }

        double offset = scrollViewer.VerticalOffset;
        double restoreOffset = Math.Max(0, sourceTop + OverviewShyRestoreInset);
        double startOffset = Math.Max(restoreOffset, sourceTop + OverviewShyStartInset);

        if (_isOverviewScrollHeaderShy)
        {
            if (offset <= restoreOffset)
            {
                _isOverviewScrollHeaderShy = false;
                SetOverviewShy(false, animate: true);
            }

            return;
        }

        if (offset >= startOffset)
        {
            _isOverviewScrollHeaderShy = true;
            SetOverviewShy(true, animate: true);
        }
    }

    private void SetOverviewShy(bool isShy, bool animate)
    {
        TransitionHelper? transition = _overviewTransition;
        if (transition is null || _isOverviewShy == isShy)
        {
            return;
        }

        _isOverviewShy = isShy;
        int generation = ++_overviewTransitionGeneration;
        bool targetWasCollapsed = DashboardOverviewShySurface.Visibility != Visibility.Visible;
        if (isShy && targetWasCollapsed)
        {
            DashboardOverviewShySurface.Visibility = Visibility.Visible;
            DashboardSideRailHost.UpdateLayout();
            MorphTransitionSafety.TryReset(
                transition,
                _overviewMorphSource!,
                DashboardOverviewShySurface,
                toInitialState: true);
        }

        if (!animate || !DashboardSideRailHost.IsLoaded || !AreAnimationsEnabled())
        {
            MorphTransitionSafety.TryReset(
                transition,
                _overviewMorphSource!,
                DashboardOverviewShySurface,
                toInitialState: !isShy);
            if (!isShy)
            {
                DashboardOverviewShySurface.Visibility = Visibility.Collapsed;
            }

            return;
        }

        UiTaskGuard.Observe(AnimateOverviewAsync(transition, isShy, generation), "ui-dashboard-page");
    }

    private async Task AnimateOverviewAsync(TransitionHelper transition, bool isShy, int generation)
    {
        try
        {
            FrameworkElement? source = _overviewMorphSource;
            if (source is null)
            {
                return;
            }

            if (isShy)
            {
                await transition.StartAsync(forceUpdateAnimatedElements: true);
            }
            else
            {
                await transition.ReverseAsync(forceUpdateAnimatedElements: true);
            }

            if (generation != _overviewTransitionGeneration)
            {
                return;
            }

            MorphTransitionSafety.TrySetStableState(
                transition,
                source,
                DashboardOverviewShySurface,
                isTargetState: isShy);
            if (!isShy)
            {
                DashboardOverviewShySurface.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (generation != _overviewTransitionGeneration)
        {
        }
        catch (Exception ex) when (generation == _overviewTransitionGeneration)
        {
            JitHub.WinUI.App.LogHandledException(ex, "ui-dashboard-overview-morph");
            if (_overviewMorphSource is FrameworkElement source)
            {
                MorphTransitionSafety.TryReset(
                    transition,
                    source,
                    DashboardOverviewShySurface,
                    toInitialState: !isShy);
            }

            if (!isShy)
            {
                DashboardOverviewShySurface.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateHeaderForScroll(ScrollViewer scrollViewer)
    {
        if (!ReferenceEquals(scrollViewer, DashboardMainRailScrollViewer))
        {
            return;
        }

        if (scrollViewer.ScrollableHeight <= 0)
        {
            _lastScrollOffset = 0;
            _upwardRevealTravel = 0;
            _downwardRehideTravel = 0;
            _headerRevealedByUpwardScroll = false;
            _isScrollHeaderShy = false;
            SetHeaderShy(false, animate: true);
            return;
        }

        double offset = scrollViewer.VerticalOffset;
        double delta = offset - _lastScrollOffset;
        _lastScrollOffset = offset;

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
        SetHeaderShy(false, animate: true);
    }

    private void HideScrollHeader()
    {
        _isScrollHeaderShy = true;
        _headerRevealedByUpwardScroll = false;
        _upwardRevealTravel = 0;
        _downwardRehideTravel = 0;
        SetHeaderShy(true, animate: true);
    }

    private void SetHeaderShy(bool isShy, bool animate)
    {
        if (_isHeaderShy == isShy)
        {
            return;
        }

        _isHeaderShy = isShy;
        int generation = ++_headerTransitionGeneration;
        if (!animate || !DashboardHeaderGrid.IsLoaded || !AreAnimationsEnabled())
        {
            if (MorphTransitionSafety.TryResetVisibilityState(
                _headerTransition,
                DashboardHeaderGrid,
                DashboardShyHeaderSurface,
                toInitialState: !isShy))
            {
                if (!isShy)
                {
                    DashboardShyHeaderSurface.Visibility = Visibility.Collapsed;
                }
            }

            return;
        }

        UiTaskGuard.Observe(AnimateHeaderAsync(isShy, generation), "ui-dashboard-page");
    }

    private async Task AnimateHeaderAsync(bool isShy, int generation)
    {
        try
        {
            Task headerAnimation = isShy
                ? _headerTransition.StartAsync(forceUpdateAnimatedElements: true)
                : _headerTransition.ReverseAsync(forceUpdateAnimatedElements: true);

            await headerAnimation;
            if (generation != _headerTransitionGeneration)
            {
                return;
            }

            MorphTransitionSafety.TrySetStableState(
                _headerTransition,
                DashboardHeaderGrid,
                DashboardShyHeaderSurface,
                isTargetState: isShy);
            if (!isShy)
            {
                DashboardShyHeaderSurface.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (generation != _headerTransitionGeneration)
        {
        }
        catch (Exception ex) when (generation == _headerTransitionGeneration)
        {
            JitHub.WinUI.App.LogHandledException(ex, "ui-dashboard-header-morph");
            if (MorphTransitionSafety.TryResetVisibilityState(
                _headerTransition,
                DashboardHeaderGrid,
                DashboardShyHeaderSurface,
                toInitialState: !isShy))
            {
                if (!isShy)
                {
                    DashboardShyHeaderSurface.Visibility = Visibility.Collapsed;
                }
            }
        }
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

    private double GetVisibleContentWidth(double fallbackWidth)
    {
        double width = fallbackWidth > 0
            ? fallbackWidth
            : DashboardRoot.ActualWidth > 0 ? DashboardRoot.ActualWidth : 0;

        try
        {
            if (DashboardRoot.XamlRoot is { Size.Width: > 0 } xamlRoot)
            {
                UIElement? rootVisual = xamlRoot.Content as UIElement;
                Point origin = rootVisual is null
                    ? DashboardRoot.TransformToVisual(null).TransformPoint(new Point(0, 0))
                    : DashboardRoot.TransformToVisual(rootVisual).TransformPoint(new Point(0, 0));
                double rootWidth = Math.Max(0, xamlRoot.Size.Width - origin.X);
                width = width > 0 ? Math.Min(width, rootWidth) : rootWidth;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            width = fallbackWidth;
        }

        return Math.Max(0, width - DashboardRoot.Padding.Left - DashboardRoot.Padding.Right);
    }

    private void DashboardSideDrawer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && (IsWithin(source, DashboardSideDrawerPanel) || IsWithin(source, DashboardSideDrawerCloseButton)))
        {
            return;
        }

        SetSideDrawerOpen(false);

        e.Handled = true;
    }

    private void SetSideDrawerOpen(bool open, bool animate = true)
    {
        if (open && GetActiveOverviewDrawerButton().Visibility != Visibility.Visible)
        {
            return;
        }

        if (open && !ViewModel.IsSideRailDrawerOpen && ViewModel.OpenSideRailCommand.CanExecute(null))
        {
            ViewModel.OpenSideRailCommand.Execute(null);
        }
        else if (!open && ViewModel.IsSideRailDrawerOpen && ViewModel.CloseSideRailCommand.CanExecute(null))
        {
            ViewModel.CloseSideRailCommand.Execute(null);
        }

        bool isFreshOpen = open && !_isSideDrawerOpen;
        _isSideDrawerOpen = open;

        if (open)
        {
            if (DashboardSideDrawer.Visibility != Visibility.Visible)
            {
                _sideDrawerAnimator.SetOpen(false, animate: false);
            }

            DashboardSideDrawer.Visibility = Visibility.Visible;
            DashboardSideDrawerCloseButton.Visibility = Visibility.Visible;
            if (isFreshOpen)
            {
                ResetSideDrawerViewport();
            }
        }

        _sideDrawerAnimator.SetOpen(open, animate, () => CompleteSideDrawerAnimation(open));
    }

    private void CompleteSideDrawerAnimation(bool isOpen)
    {
        if (!isOpen)
        {
            DashboardSideDrawer.Visibility = Visibility.Collapsed;
            DashboardSideDrawerCloseButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            DashboardSideDrawer.Visibility = Visibility.Visible;
            DashboardSideDrawerCloseButton.Visibility = Visibility.Visible;
            _ = DispatcherQueue.TryEnqueue(() => DashboardSideDrawerCloseButton.Focus(FocusState.Keyboard));
        }

        if (!isOpen)
        {
            _ = DispatcherQueue.TryEnqueue(RestoreSideDrawerFocus);
        }
    }

    private void DashboardSideDrawer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isSideDrawerOpen)
        {
            return;
        }

        if (IsShiftKey(e.Key))
        {
            _isSideDrawerShiftPressed = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SetSideDrawerOpen(false);
            return;
        }

        if (e.Key != VirtualKey.Tab)
        {
            return;
        }

        bool moveBackward = _isSideDrawerShiftPressed || IsShiftPressed();
        DependencyObject? first = FocusManager.FindFirstFocusableElement(DashboardSideDrawerPanel);
        DependencyObject? last = FocusManager.FindLastFocusableElement(DashboardSideDrawerPanel);
        Control? focused = TryGetFocusedControl();
        DependencyObject? boundary = moveBackward ? first : last;
        DependencyObject? target = moveBackward ? last : first;
        if (focused is null || target is not Control targetControl ||
            boundary is not null && !ReferenceEquals(focused, boundary))
        {
            return;
        }

        e.Handled = true;
        _ = targetControl.Focus(FocusState.Keyboard);
    }

    private void DashboardSideDrawer_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (IsShiftKey(e.Key))
        {
            _isSideDrawerShiftPressed = false;
        }
    }

    private void DashboardSideDrawerStartFocusSentinel_GotFocus(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(() => FocusDrawerBoundary(last: true));
    }

    private void DashboardSideDrawerEndFocusSentinel_GotFocus(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(() => FocusDrawerBoundary(last: false));
    }

    private void FocusDrawerBoundary(bool last)
    {
        IReadOnlyList<Control> controls = FindDrawerFocusableControls();
        if (last)
        {
            for (int index = controls.Count - 1; index >= 0; index--)
            {
                if (controls[index].Focus(FocusState.Keyboard))
                {
                    return;
                }
            }
        }
        else
        {
            foreach (Control control in controls)
            {
                if (control.Focus(FocusState.Keyboard))
                {
                    return;
                }
            }
        }

        _ = DashboardSideDrawerCloseButton.Focus(FocusState.Keyboard);
    }

    private IReadOnlyList<Control> FindDrawerFocusableControls()
    {
        List<Control> controls = [];
        CollectDrawerFocusableControls(DashboardSideDrawerPanel, controls);
        return controls;
    }

    private void CollectDrawerFocusableControls(DependencyObject root, List<Control> controls)
    {
        if (root is Control control &&
            !ReferenceEquals(control, DashboardSideDrawerStartFocusSentinel) &&
            !ReferenceEquals(control, DashboardSideDrawerEndFocusSentinel) &&
            IsUsableFocusTarget(control))
        {
            controls.Add(control);
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            CollectDrawerFocusableControls(VisualTreeHelper.GetChild(root, index), controls);
        }
    }

    private void FocusManager_GettingFocus(object? sender, GettingFocusEventArgs e)
    {
        if (!_isSideDrawerOpen)
        {
            return;
        }

        if (e.NewFocusedElement is DependencyObject newFocus && IsWithin(newFocus, DashboardSideDrawerPanel))
        {
            return;
        }

        DependencyObject? wrappedTarget = e.Direction == FocusNavigationDirection.Previous
            ? FocusManager.FindLastFocusableElement(DashboardSideDrawerPanel)
            : FocusManager.FindFirstFocusableElement(DashboardSideDrawerPanel);
        wrappedTarget ??= DashboardSideDrawerCloseButton;
        if (e.TrySetNewFocusedElement(wrappedTarget))
        {
            e.Handled = true;
        }
    }

    private void FocusManager_LosingFocus(object? sender, LosingFocusEventArgs e)
    {
        if (!_isSideDrawerOpen ||
            e.OldFocusedElement is not DependencyObject oldFocus ||
            !IsWithin(oldFocus, DashboardSideDrawerPanel) ||
            e.NewFocusedElement is DependencyObject newFocus && IsWithin(newFocus, DashboardSideDrawerPanel))
        {
            return;
        }

        DependencyObject? wrappedTarget = e.Direction == FocusNavigationDirection.Previous
            ? FocusManager.FindLastFocusableElement(DashboardSideDrawerPanel)
            : FocusManager.FindFirstFocusableElement(DashboardSideDrawerPanel);
        wrappedTarget ??= DashboardSideDrawerCloseButton;
        if (e.TrySetNewFocusedElement(wrappedTarget) || e.TryCancel())
        {
            e.Handled = true;
        }
    }

    private void RestoreSideDrawerFocus()
    {
        Control? restoreTarget = _sideDrawerRestoreTarget;
        _sideDrawerRestoreTarget = null;
        if (IsUsableFocusTarget(restoreTarget))
        {
            _ = restoreTarget!.Focus(FocusState.Keyboard);
        }
        else if (IsUsableFocusTarget(GetActiveOverviewDrawerButton()))
        {
            _ = GetActiveOverviewDrawerButton().Focus(FocusState.Keyboard);
        }
    }

    private Button GetActiveOverviewDrawerButton() =>
        _isHeaderShy ? DashboardShyOverviewDrawerButton : DashboardOverviewDrawerButton;

    private Control? TryGetFocusedControl()
    {
        try
        {
            return XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsUsableFocusTarget(Control? control) =>
        control is { IsEnabled: true, IsTabStop: true, Visibility: Visibility.Visible } && control.XamlRoot is not null;

    private static bool IsShiftPressed() =>
        IsNativeKeyDown(VkShift) || IsNativeKeyDown(VkLeftShift) || IsNativeKeyDown(VkRightShift);

    private static bool IsShiftKey(VirtualKey key) => (int)key is VkShift or VkLeftShift or VkRightShift;

    private static bool IsNativeKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    private double GetSideDrawerWidth()
    {
        if (DashboardSideDrawerPanel.ActualWidth > 0)
        {
            return DashboardSideDrawerPanel.ActualWidth;
        }

        return DashboardSideDrawerPanel.Width > 0 ? DashboardSideDrawerPanel.Width : SideDrawerFallbackWidth;
    }

    private void ResetSideDrawerViewport()
    {
        DashboardSideDrawerScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isSideDrawerOpen)
            {
                DashboardSideDrawerScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            }
        });
    }

    private void DashboardHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (double.IsFinite(e.NewSize.Height) && e.NewSize.Height > 0)
        {
            DashboardHeaderScrollSpacer.Height = e.NewSize.Height;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        FocusManager.GettingFocus -= FocusManager_GettingFocus;
        FocusManager.LosingFocus -= FocusManager_LosingFocus;
        _starLibraryService.Changed -= StarLibraryService_Changed;
        _notificationInboxState.PropertyChanged -= NotificationInboxState_PropertyChanged;
        DetachHeaderScrollTracking();
        DetachOverviewScrollTracking();
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
        _headerTransitionGeneration++;
        MorphTransitionSafety.TryStop(_headerTransition);
        ClearOverviewMorphSource();
        _sideDrawerAnimator.Stop();
        CloseCustomizeDialog(cancelChanges: true);
    }

    private void NotificationInboxState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            !string.Equals(e.PropertyName, nameof(NotificationInboxState.ReadStateVersion), StringComparison.Ordinal))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(ViewModel.ApplySharedNotificationReadStates);
    }

    private void StarLibraryService_Changed(object? sender, StarLibraryChangedEventArgs e)
    {
        if (e.Kind != StarLibraryChangeKind.ProjectionInvalidated)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => ViewModel.NotifyStarLibraryChanged(e.UserId));
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!string.Equals(args.PropertyName, nameof(ViewModel.IsCustomizeDialogOpen), StringComparison.Ordinal))
        {
            return;
        }

        if (ViewModel.IsCustomizeDialogOpen)
        {
            ShowCustomizeDialog();
        }
        else
        {
            HideCustomizeDialog();
        }
    }

    private void ShowCustomizeDialog()
    {
        if (_isCustomizeDialogShowing)
        {
            return;
        }

        DashboardWidgetCustomizeDialog dialog = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DataContext = ViewModel
        };
        _customizeModalSession = _modalService.TryOpenSession(
            string.Empty,
            dialog,
            useHeader: true,
            callback: new RelayCommand(OnCustomizeModalClosed));
        _isCustomizeDialogShowing = _customizeModalSession is not null;
        if (!_isCustomizeDialogShowing && ViewModel.CancelCustomizeCommand.CanExecute(null))
        {
            ViewModel.CancelCustomizeCommand.Execute(null);
        }
    }

    private void HideCustomizeDialog()
    {
        if (!_isCustomizeDialogShowing)
        {
            return;
        }

        CloseCustomizeDialog(cancelChanges: false);
    }

    private void CloseCustomizeDialog(bool cancelChanges)
    {
        if (cancelChanges && ViewModel.IsCustomizeDialogOpen &&
            ViewModel.CancelCustomizeCommand.CanExecute(null))
        {
            ViewModel.CancelCustomizeCommand.Execute(null);
        }

        ModalSession? session = _customizeModalSession;
        _customizeModalSession = null;
        _isCustomizeDialogShowing = false;
        _ = session?.TryClose();
    }

    private void OnCustomizeModalClosed()
    {
        _customizeModalSession = null;
        _isCustomizeDialogShowing = false;
        if (ViewModel.IsCustomizeDialogOpen && ViewModel.CancelCustomizeCommand.CanExecute(null))
        {
            ViewModel.CancelCustomizeCommand.Execute(null);
        }
    }

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
