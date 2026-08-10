using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class DashboardPage : Page
{
    private const double SideDrawerFallbackWidth = 360;
    private const int VkShift = 0x10;
    private const int VkLeftShift = 0xA0;
    private const int VkRightShift = 0xA1;

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);

    private readonly ModalService _modalService;
    private readonly IGitHubStarLibraryService _starLibraryService;
    private readonly NotificationInboxState _notificationInboxState;
    private readonly SlideDrawerAnimator _sideDrawerAnimator;
    private bool _initialized;
    private bool _isCustomizeDialogShowing;
    private ModalSession? _customizeModalSession;
    private bool _isSideDrawerOpen;
    private bool _isSideDrawerShiftPressed;
    private Control? _sideDrawerRestoreTarget;

    public DashboardPageViewModel ViewModel { get; }

    public DashboardPage()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<DashboardPageViewModel>();
        _modalService = ((App)Application.Current).GetService<ModalService>();
        _starLibraryService = ((App)Application.Current).GetService<IGitHubStarLibraryService>();
        _notificationInboxState = ((App)Application.Current).GetService<NotificationInboxState>();
        InitializeComponent();
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
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
            if (_initialized)
            {
                CommitPerformanceReadiness();
                return;
            }

            _initialized = true;
            ApplyResponsiveLayout(ActualWidth);
            await ViewModel.InitializeAsync();
            CommitPerformanceReadiness();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load dashboard: {ex}");
        }
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
        DashboardOverviewDrawerButton.Visibility = showOverviewDrawerButton ? Visibility.Visible : Visibility.Collapsed;
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

        UpdateSideDrawerCloseButtonPlacement();
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
        if (open && DashboardOverviewDrawerButton.Visibility != Visibility.Visible)
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

        _isSideDrawerOpen = open;

        if (open)
        {
            if (DashboardSideDrawer.Visibility != Visibility.Visible)
            {
                _sideDrawerAnimator.SetOpen(false, animate: false);
            }

            DashboardSideDrawer.Visibility = Visibility.Visible;
            DashboardSideDrawerCloseButton.Visibility = Visibility.Visible;
            UpdateSideDrawerCloseButtonPlacement();
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
            UpdateSideDrawerCloseButtonPlacement();
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
        else if (IsUsableFocusTarget(DashboardOverviewDrawerButton))
        {
            _ = DashboardOverviewDrawerButton.Focus(FocusState.Keyboard);
        }
    }

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

    private void UpdateSideDrawerCloseButtonPlacement()
    {
        DashboardSideDrawerCloseButton.Margin = new Thickness(0);
        DashboardSideDrawerCloseButton.Width = 38;
        DashboardSideDrawerCloseButton.Height = 36;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        FocusManager.GettingFocus -= FocusManager_GettingFocus;
        FocusManager.LosingFocus -= FocusManager_LosingFocus;
        _starLibraryService.Changed -= StarLibraryService_Changed;
        _notificationInboxState.PropertyChanged -= NotificationInboxState_PropertyChanged;
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
}
