using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Layout;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace JitHub.WinUI.Views.Controls.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AdaptiveWorkspace : UserControl
{
    public static readonly DependencyProperty LeadingPaneProperty = DependencyProperty.Register(
        nameof(LeadingPane), typeof(object), typeof(AdaptiveWorkspace), new PropertyMetadata(null, OnPaneChanged));

    public static readonly DependencyProperty PrimaryPaneProperty = DependencyProperty.Register(
        nameof(PrimaryPane), typeof(object), typeof(AdaptiveWorkspace), new PropertyMetadata(null, OnPaneChanged));

    public static readonly DependencyProperty TrailingPaneProperty = DependencyProperty.Register(
        nameof(TrailingPane), typeof(object), typeof(AdaptiveWorkspace), new PropertyMetadata(null, OnPaneChanged));

    public static readonly DependencyProperty LeadingPaneLabelProperty = DependencyProperty.Register(
        nameof(LeadingPaneLabel), typeof(string), typeof(AdaptiveWorkspace), new PropertyMetadata("List"));

    public static readonly DependencyProperty TrailingPaneLabelProperty = DependencyProperty.Register(
        nameof(TrailingPaneLabel), typeof(string), typeof(AdaptiveWorkspace), new PropertyMetadata("Inspector"));

    public static readonly DependencyProperty LeadingPaneWidthProperty = DependencyProperty.Register(
        nameof(LeadingPaneWidth), typeof(double), typeof(AdaptiveWorkspace), new PropertyMetadata(336d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty TrailingPaneWidthProperty = DependencyProperty.Register(
        nameof(TrailingPaneWidth), typeof(double), typeof(AdaptiveWorkspace), new PropertyMetadata(260d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty WideBreakpointProperty = DependencyProperty.Register(
        nameof(WideBreakpoint), typeof(double), typeof(AdaptiveWorkspace), new PropertyMetadata(1260d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MediumBreakpointProperty = DependencyProperty.Register(
        nameof(MediumBreakpoint), typeof(double), typeof(AdaptiveWorkspace), new PropertyMetadata(980d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty NarrowBreakpointProperty = DependencyProperty.Register(
        nameof(NarrowBreakpoint), typeof(double), typeof(AdaptiveWorkspace), new PropertyMetadata(720d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty AutomationIdPrefixProperty = DependencyProperty.Register(
        nameof(AutomationIdPrefix), typeof(string), typeof(AdaptiveWorkspace), new PropertyMetadata("AdaptiveWorkspace", OnAutomationIdPrefixChanged));

    public static readonly DependencyProperty ShowPaneButtonsProperty = DependencyProperty.Register(
        nameof(ShowPaneButtons), typeof(bool), typeof(AdaptiveWorkspace), new PropertyMetadata(true, OnLayoutPropertyChanged));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(AdaptiveWorkspaceState), typeof(AdaptiveWorkspace), new PropertyMetadata(null));

    private readonly SlideDrawerAnimator _leftDrawerAnimator;
    private readonly SlideDrawerAnimator _rightDrawerAnimator;
    private readonly KeyEventHandler _drawerPreviewKeyHandler;
    private readonly HashSet<UIElement> _drawerKeyHandlerElements = [];
    private Control? _restoreFocusTarget;
    private int _drawerFocusRequestVersion;
    private int _containedDrawerFocusVersion;
    private Guid? _lastContainedDrawerFocusCorrelation;
    private DispatcherQueueTimer? _drawerFocusTimer;
    private bool _isTransferringDrawerFocus;

    public AdaptiveWorkspace()
    {
        InitializeComponent();
        _drawerPreviewKeyHandler = DrawerControl_PreviewKeyDown;
        DrawerOverlay.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(DrawerOverlay_PointerPressed),
            handledEventsToo: true);
        KeyboardAccelerators.Add(CreateDrawerTabAccelerator(VirtualKeyModifiers.None));
        KeyboardAccelerators.Add(CreateDrawerTabAccelerator(VirtualKeyModifiers.Shift));
        _leftDrawerAnimator = new SlideDrawerAnimator(
            LeftDrawerTransform,
            SlideDrawerEdge.Left,
            () => CalculateDrawerWidth(LeadingPaneWidth));
        _rightDrawerAnimator = new SlideDrawerAnimator(
            RightDrawerTransform,
            SlideDrawerEdge.Right,
            () => CalculateDrawerWidth(TrailingPaneWidth));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateAutomationIds();
    }

    private KeyboardAccelerator CreateDrawerTabAccelerator(VirtualKeyModifiers modifiers)
    {
        KeyboardAccelerator accelerator = new()
        {
            Key = VirtualKey.Tab,
            Modifiers = modifiers
        };
        accelerator.Invoked += DrawerTabAccelerator_Invoked;
        return accelerator;
    }

    private void DrawerTabAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        if (visibleDrawer == AdaptiveWorkspaceDrawer.None)
        {
            return;
        }

        MoveFocusWithinDrawer(
            visibleDrawer,
            (sender.Modifiers & VirtualKeyModifiers.Shift) == VirtualKeyModifiers.Shift);
        args.Handled = true;
    }

    public event EventHandler<AdaptiveWorkspaceState>? ModeChanged;

    public object? LeadingPane
    {
        get => GetValue(LeadingPaneProperty);
        set => SetValue(LeadingPaneProperty, value);
    }

    public object? PrimaryPane
    {
        get => GetValue(PrimaryPaneProperty);
        set => SetValue(PrimaryPaneProperty, value);
    }

    public object? TrailingPane
    {
        get => GetValue(TrailingPaneProperty);
        set => SetValue(TrailingPaneProperty, value);
    }

    public string LeadingPaneLabel
    {
        get => (string)GetValue(LeadingPaneLabelProperty);
        set => SetValue(LeadingPaneLabelProperty, value);
    }

    public string TrailingPaneLabel
    {
        get => (string)GetValue(TrailingPaneLabelProperty);
        set => SetValue(TrailingPaneLabelProperty, value);
    }

    public double LeadingPaneWidth
    {
        get => (double)GetValue(LeadingPaneWidthProperty);
        set => SetValue(LeadingPaneWidthProperty, value);
    }

    public double TrailingPaneWidth
    {
        get => (double)GetValue(TrailingPaneWidthProperty);
        set => SetValue(TrailingPaneWidthProperty, value);
    }

    public double WideBreakpoint
    {
        get => (double)GetValue(WideBreakpointProperty);
        set => SetValue(WideBreakpointProperty, value);
    }

    public double MediumBreakpoint
    {
        get => (double)GetValue(MediumBreakpointProperty);
        set => SetValue(MediumBreakpointProperty, value);
    }

    public double NarrowBreakpoint
    {
        get => (double)GetValue(NarrowBreakpointProperty);
        set => SetValue(NarrowBreakpointProperty, value);
    }

    public string AutomationIdPrefix
    {
        get => (string)GetValue(AutomationIdPrefixProperty);
        set => SetValue(AutomationIdPrefixProperty, value);
    }

    public bool ShowPaneButtons
    {
        get => (bool)GetValue(ShowPaneButtonsProperty);
        set => SetValue(ShowPaneButtonsProperty, value);
    }

    public AdaptiveWorkspaceState? State
    {
        get => (AdaptiveWorkspaceState?)GetValue(StateProperty);
        private set => SetValue(StateProperty, value);
    }

    public bool IsLeadingDrawerOpen => State?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;

    public bool IsTrailingDrawerOpen => State?.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing;

    public void OpenLeadingPane()
        => OpenDrawer(AdaptiveWorkspaceDrawer.Leading);

    public void OpenTrailingPane()
        => OpenDrawer(AdaptiveWorkspaceDrawer.Trailing);

    public bool TryMoveFocusWithinOpenDrawer(bool moveBackward)
    {
        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        if (visibleDrawer == AdaptiveWorkspaceDrawer.None)
        {
            return false;
        }

        MoveFocusWithinDrawer(visibleDrawer, moveBackward);
        return true;
    }

    public void CloseDrawer()
    {
        if (State?.VisibleDrawer == AdaptiveWorkspaceDrawer.None)
        {
            return;
        }

        _drawerFocusRequestVersion++;
        StopDrawerFocusTimer();
        DetachDrawerKeyHandlers();
        ApplyLayout(AdaptiveWorkspaceDrawer.None);
    }

    private static void OnPaneChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        => ((AdaptiveWorkspace)dependencyObject).ApplyLayout();

    private static void OnLayoutPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        => ((AdaptiveWorkspace)dependencyObject).ApplyLayout();

    private static void OnAutomationIdPrefixChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        => ((AdaptiveWorkspace)dependencyObject).UpdateAutomationIds();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WorkspaceRoot.AddHandler(KeyDownEvent, new KeyEventHandler(WorkspaceRoot_KeyDown), true);
        FocusManager.GotFocus += FocusManager_GotFocus;
        FocusManager.GettingFocus += FocusManager_GettingFocus;
        FocusManager.LosingFocus += FocusManager_LosingFocus;
        FocusManager.LostFocus += FocusManager_LostFocus;
        UpdateWorkspaceClip(ActualWidth, ActualHeight);
        ApplyLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopDrawerFocusTimer();
        DetachDrawerKeyHandlers();
        _leftDrawerAnimator.Stop();
        _rightDrawerAnimator.Stop();
        FocusManager.GotFocus -= FocusManager_GotFocus;
        FocusManager.GettingFocus -= FocusManager_GettingFocus;
        FocusManager.LosingFocus -= FocusManager_LosingFocus;
        FocusManager.LostFocus -= FocusManager_LostFocus;
        WorkspaceRoot.RemoveHandler(KeyDownEvent, new KeyEventHandler(WorkspaceRoot_KeyDown));
        ClearAllPresenters();
    }

    private void FocusManager_GotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        Border? activeDrawer = visibleDrawer switch
        {
            AdaptiveWorkspaceDrawer.Leading => LeftDrawer,
            AdaptiveWorkspaceDrawer.Trailing => RightDrawer,
            _ => null
        };
        if (activeDrawer is null ||
            e.NewFocusedElement is not DependencyObject newFocus ||
            !IsWithin(newFocus, activeDrawer))
        {
            return;
        }

        _lastContainedDrawerFocusCorrelation = e.CorrelationId;
        _containedDrawerFocusVersion++;
        AttachDrawerKeyHandler(newFocus);
    }

    private void FocusManager_GettingFocus(object? sender, GettingFocusEventArgs e)
    {
        if (_isTransferringDrawerFocus || IsAppModalPresenting())
        {
            return;
        }

        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        if (visibleDrawer == AdaptiveWorkspaceDrawer.None)
        {
            return;
        }

        Border activeDrawer = visibleDrawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        if (e.NewFocusedElement is DependencyObject newFocus && IsWithin(newFocus, activeDrawer))
        {
            AttachDrawerKeyHandler(newFocus);
            return;
        }

        // At the last tab stop WinUI can report a null NewFocusedElement when
        // focus would leave the XAML island. Treat that exactly like any other
        // escape attempt and wrap inside the modal drawer.
        DependencyObject? wrappedTarget = e.Direction == FocusNavigationDirection.Previous
            ? FocusManager.FindLastFocusableElement(activeDrawer)
            : FocusManager.FindFirstFocusableElement(activeDrawer);
        wrappedTarget ??= activeDrawer;
        if (e.TrySetNewFocusedElement(wrappedTarget))
        {
            e.Handled = true;
        }
    }

    private void FocusManager_LosingFocus(object? sender, LosingFocusEventArgs e)
    {
        if (IsAppModalPresenting())
        {
            return;
        }

        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        if (visibleDrawer == AdaptiveWorkspaceDrawer.None)
        {
            return;
        }

        Border activeDrawer = visibleDrawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        if (e.OldFocusedElement is not DependencyObject oldFocus ||
            !IsWithin(oldFocus, activeDrawer) ||
            e.NewFocusedElement is DependencyObject newFocus && IsWithin(newFocus, activeDrawer))
        {
            return;
        }

        DependencyObject? wrappedTarget = e.Direction == FocusNavigationDirection.Previous
            ? FindLastUsableTabStop(activeDrawer)
            : FindFirstUsableTabStop(activeDrawer);
        wrappedTarget ??= activeDrawer;

        if (e.TrySetNewFocusedElement(wrappedTarget) || e.TryCancel())
        {
            e.Handled = true;
        }
    }

    private void FocusManager_LostFocus(object? sender, FocusManagerLostFocusEventArgs e)
    {
        if (_isTransferringDrawerFocus || !IsLoaded || IsAppModalPresenting())
        {
            return;
        }

        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        Border? activeDrawer = visibleDrawer switch
        {
            AdaptiveWorkspaceDrawer.Leading => LeftDrawer,
            AdaptiveWorkspaceDrawer.Trailing => RightDrawer,
            _ => null
        };
        if (activeDrawer is null ||
            e.OldFocusedElement is not DependencyObject oldFocus ||
            !IsWithin(oldFocus, activeDrawer))
        {
            return;
        }

        // LosingFocus/GettingFocus do not cover every transition from a WinUI
        // desktop island to its native root. LostFocus runs after that boundary
        // transition, while Shift is still held for reverse traversal.
        bool moveBackward = IsShiftPressed();
        Guid correlationId = e.CorrelationId;
        int containedFocusVersion = _containedDrawerFocusVersion;
        int requestVersion = _drawerFocusRequestVersion;
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            if (!IsLoaded ||
                IsAppModalPresenting() ||
                requestVersion != _drawerFocusRequestVersion ||
                State?.VisibleDrawer != visibleDrawer)
            {
                return;
            }

            if (_lastContainedDrawerFocusCorrelation == correlationId ||
                _containedDrawerFocusVersion != containedFocusVersion)
            {
                return;
            }

            UIElement target = (moveBackward
                ? FindLastUsableTabStop(activeDrawer)
                : FindFirstUsableTabStop(activeDrawer)) as UIElement
                ?? activeDrawer;
            _ = await TryFocusDrawerElementAsync(target);
        });
    }

    private void WorkspaceRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWorkspaceClip(e.NewSize.Width, e.NewSize.Height);
        ApplyLayout(State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None);
    }

    private void OpenLeadingPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenLeadingPane();

    private void OpenTrailingPaneButton_Click(object sender, RoutedEventArgs e)
        => OpenTrailingPane();

    private void WorkspaceRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        if (visibleDrawer == AdaptiveWorkspaceDrawer.None)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            CloseDrawer();
            e.Handled = true;
        }
    }

    private void WorkspaceRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        if (visibleDrawer == AdaptiveWorkspaceDrawer.None || e.Key != VirtualKey.Tab)
        {
            return;
        }

        MoveFocusWithinDrawer(visibleDrawer, IsShiftPressed());
        e.Handled = true;
    }

    private void DrawerControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        AdaptiveWorkspaceDrawer visibleDrawer = State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None;
        if (visibleDrawer == AdaptiveWorkspaceDrawer.None || e.Key != VirtualKey.Tab)
        {
            return;
        }

        MoveFocusWithinDrawer(visibleDrawer, IsShiftPressed());
        e.Handled = true;
    }

    private void LeftDrawerStartFocusSentinel_GotFocus(object sender, RoutedEventArgs e)
        => RedirectDrawerSentinel(AdaptiveWorkspaceDrawer.Leading, moveBackward: true);

    private void LeftDrawerEndFocusSentinel_GotFocus(object sender, RoutedEventArgs e)
        => RedirectDrawerSentinel(AdaptiveWorkspaceDrawer.Leading, moveBackward: false);

    private void RightDrawerStartFocusSentinel_GotFocus(object sender, RoutedEventArgs e)
        => RedirectDrawerSentinel(AdaptiveWorkspaceDrawer.Trailing, moveBackward: true);

    private void RightDrawerEndFocusSentinel_GotFocus(object sender, RoutedEventArgs e)
        => RedirectDrawerSentinel(AdaptiveWorkspaceDrawer.Trailing, moveBackward: false);

    private void RedirectDrawerSentinel(AdaptiveWorkspaceDrawer drawer, bool moveBackward)
    {
        if (State?.VisibleDrawer != drawer)
        {
            return;
        }

        Border activeDrawer = drawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        DependencyObject? target = moveBackward
            ? FindLastUsableTabStop(activeDrawer)
            : FindFirstUsableTabStop(activeDrawer);

        if (target is Control control && TryFocusDrawerControl(control))
        {
            return;
        }

        _ = activeDrawer.Focus(FocusState.Keyboard);
    }

    private void MoveFocusWithinDrawer(AdaptiveWorkspaceDrawer drawer, bool moveBackward)
    {
        Border activeDrawer = drawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        List<Control> tabStops = [];
        CollectTabStops(activeDrawer, tabStops, ancestorsVisible: true);

        if (tabStops.Count == 0)
        {
            _ = activeDrawer.Focus(FocusState.Keyboard);
            return;
        }

        object? focusedElement = FocusManager.GetFocusedElement(XamlRoot);
        int currentIndex = focusedElement is Control focusedControl
            ? tabStops.IndexOf(focusedControl)
            : -1;
        int nextIndex = currentIndex < 0
            ? moveBackward ? tabStops.Count - 1 : 0
            : moveBackward
                ? (currentIndex - 1 + tabStops.Count) % tabStops.Count
                : (currentIndex + 1) % tabStops.Count;

        if (!TryFocusDrawerControl(tabStops[nextIndex]))
        {
            _ = activeDrawer.Focus(FocusState.Keyboard);
        }
    }

    private DependencyObject? FindFirstUsableTabStop(DependencyObject parent)
    {
        List<Control> tabStops = [];
        CollectTabStops(parent, tabStops, ancestorsVisible: true);
        return tabStops.Count > 0 ? tabStops[0] : null;
    }

    private DependencyObject? FindLastUsableTabStop(DependencyObject parent)
    {
        List<Control> tabStops = [];
        CollectTabStops(parent, tabStops, ancestorsVisible: true);
        return tabStops.Count > 0 ? tabStops[^1] : null;
    }

    private void CollectTabStops(
        DependencyObject parent,
        List<Control> tabStops,
        bool ancestorsVisible)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            bool isVisible = ancestorsVisible &&
                (child is not UIElement element || element.Visibility == Visibility.Visible);
            if (!isVisible)
            {
                continue;
            }

            if (child is Control control &&
                !IsDrawerFocusSentinel(control) &&
                !IsStructuralFocusContainer(control) &&
                control.IsTabStop &&
                control.IsEnabled &&
                control.IsLoaded &&
                control.ActualWidth > 0.5 &&
                control.ActualHeight > 0.5)
            {
                tabStops.Add(control);
            }

            CollectTabStops(child, tabStops, isVisible);
        }
    }

    private bool IsDrawerFocusSentinel(Control control)
        => ReferenceEquals(control, LeftDrawerStartFocusSentinel) ||
           ReferenceEquals(control, LeftDrawerEndFocusSentinel) ||
           ReferenceEquals(control, RightDrawerStartFocusSentinel) ||
           ReferenceEquals(control, RightDrawerEndFocusSentinel);

    private static bool IsStructuralFocusContainer(Control control)
        => control is UserControl || control.GetType() == typeof(ContentControl);

    private static bool IsShiftPressed()
    {
        CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private void DrawerOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (IsWithin(source, LeftDrawer) || IsWithin(source, RightDrawer)))
        {
            return;
        }

        CloseDrawer();
        e.Handled = true;
    }

    private void OpenDrawer(AdaptiveWorkspaceDrawer drawer)
    {
        if (drawer is AdaptiveWorkspaceDrawer.Leading &&
            State?.LeadingPanePlacement != AdaptivePanePlacement.LeftDrawer)
        {
            return;
        }

        if (drawer is AdaptiveWorkspaceDrawer.Trailing &&
            State?.TrailingPanePlacement != AdaptivePanePlacement.RightDrawer)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(XamlRoot) is Control focusedControl &&
            !IsWithin(focusedControl, DrawerOverlay))
        {
            _restoreFocusTarget = focusedControl;
        }

        ApplyLayout(drawer);
        if (State?.VisibleDrawer == drawer)
        {
            FocusDrawerImmediately(drawer);
            QueueDrawerFocus(drawer);
        }
    }

    private void ApplyLayout()
        => ApplyLayout(State?.VisibleDrawer ?? AdaptiveWorkspaceDrawer.None);

    private void ApplyLayout(AdaptiveWorkspaceDrawer requestedDrawer)
    {
        if (!IsLoaded && ActualWidth <= 0)
        {
            return;
        }

        double windowWidth = XamlRoot?.Size.Width ?? ActualWidth;
        bool hadState = State is not null;
        AdaptiveWorkspaceState previousState = State
            ?? AdaptiveWorkspaceLayout.CalculateForShell(
                windowWidth,
                ActualWidth,
                LeadingPane is not null,
                TrailingPane is not null,
                CreateBreakpoints());

        AdaptiveWorkspaceState nextState = AdaptiveWorkspaceLayout.CalculateForShell(
            windowWidth,
            ActualWidth,
            LeadingPane is not null,
            TrailingPane is not null,
            CreateBreakpoints(),
            requestedDrawer);

        State = nextState;
        ApplyInlineColumns(nextState);
        MovePaneContent(ShouldKeepPreviousDrawerContent(previousState, nextState) ? previousState : nextState);
        ApplyDrawerState(nextState, previousState.VisibleDrawer);
        ApplyPaneButtons(nextState);

        if (!hadState ||
            previousState.Mode != nextState.Mode ||
            previousState.LeadingPanePlacement != nextState.LeadingPanePlacement ||
            previousState.TrailingPanePlacement != nextState.TrailingPanePlacement ||
            previousState.VisibleDrawer != nextState.VisibleDrawer)
        {
            ModeChanged?.Invoke(this, nextState);
        }
    }

    private void ApplyInlineColumns(AdaptiveWorkspaceState state)
    {
        LeadingColumn.Width = state.LeadingPanePlacement == AdaptivePanePlacement.Inline
            ? new GridLength(LeadingPaneWidth)
            : new GridLength(0);
        TrailingColumn.Width = state.TrailingPanePlacement == AdaptivePanePlacement.Inline
            ? new GridLength(TrailingPaneWidth)
            : new GridLength(0);
        PrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        PrimaryPresenter.Margin = new Thickness(
            state.LeadingPanePlacement == AdaptivePanePlacement.Inline ? 12 : 0,
            0,
            state.TrailingPanePlacement == AdaptivePanePlacement.Inline ? 12 : 0,
            0);
    }

    private void MovePaneContent(AdaptiveWorkspaceState state)
    {
        PlacePaneContent(PrimaryPane, PrimaryPresenter);
        PlacePaneContent(
            LeadingPane,
            state.LeadingPanePlacement == AdaptivePanePlacement.Inline
                ? LeadingInlinePresenter
                : state.LeadingPanePlacement == AdaptivePanePlacement.LeftDrawer
                    ? LeadingDrawerPresenter
                    : null);
        PlacePaneContent(
            TrailingPane,
            state.TrailingPanePlacement == AdaptivePanePlacement.Inline
                ? TrailingInlinePresenter
                : state.TrailingPanePlacement == AdaptivePanePlacement.RightDrawer
                    ? TrailingDrawerPresenter
                    : null);
    }

    private static bool ShouldKeepPreviousDrawerContent(AdaptiveWorkspaceState previousState, AdaptiveWorkspaceState nextState)
        => previousState.VisibleDrawer != AdaptiveWorkspaceDrawer.None &&
           nextState.VisibleDrawer == AdaptiveWorkspaceDrawer.None;

    private void ApplyDrawerState(AdaptiveWorkspaceState state, AdaptiveWorkspaceDrawer previousDrawer)
    {
        DrawerOverlay.Width = GetVisibleOverlayWidth();
        LeftDrawer.Width = CalculateDrawerWidth(LeadingPaneWidth);
        RightDrawer.Width = CalculateDrawerWidth(TrailingPaneWidth);

        if (state.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading)
        {
            OpenDrawerVisual(AdaptiveWorkspaceDrawer.Leading, animate: previousDrawer != AdaptiveWorkspaceDrawer.Leading);
            return;
        }

        if (state.VisibleDrawer == AdaptiveWorkspaceDrawer.Trailing)
        {
            OpenDrawerVisual(AdaptiveWorkspaceDrawer.Trailing, animate: previousDrawer != AdaptiveWorkspaceDrawer.Trailing);
            return;
        }

        if (previousDrawer == AdaptiveWorkspaceDrawer.Leading)
        {
            CloseDrawerVisual(AdaptiveWorkspaceDrawer.Leading, animate: true);
            return;
        }

        if (previousDrawer == AdaptiveWorkspaceDrawer.Trailing)
        {
            CloseDrawerVisual(AdaptiveWorkspaceDrawer.Trailing, animate: true);
            return;
        }

        CollapseDrawerOverlay();
        RestoreDrawerFocus();
    }

    private void OpenDrawerVisual(AdaptiveWorkspaceDrawer drawer, bool animate)
    {
        DrawerOverlay.Visibility = Visibility.Visible;
        if (drawer == AdaptiveWorkspaceDrawer.Leading)
        {
            RightDrawer.Visibility = Visibility.Collapsed;
            _rightDrawerAnimator.SetOpen(false, animate: false);
            LeftDrawer.Visibility = Visibility.Visible;
            if (animate)
            {
                _leftDrawerAnimator.SetOpen(false, animate: false);
            }

            _leftDrawerAnimator.SetOpen(true, animate, () => QueueDrawerFocus(drawer));
            return;
        }

        LeftDrawer.Visibility = Visibility.Collapsed;
        _leftDrawerAnimator.SetOpen(false, animate: false);
        RightDrawer.Visibility = Visibility.Visible;
        if (animate)
        {
            _rightDrawerAnimator.SetOpen(false, animate: false);
        }

        _rightDrawerAnimator.SetOpen(true, animate, () => QueueDrawerFocus(drawer));
    }

    private void FocusDrawerImmediately(AdaptiveWorkspaceDrawer drawer)
    {
        if (!TryFocusFirstDrawerElement(drawer))
        {
            _ = TryFocusDrawerContainer(drawer);
        }
    }

    private void CloseDrawerVisual(AdaptiveWorkspaceDrawer drawer, bool animate)
    {
        DrawerOverlay.Visibility = Visibility.Visible;
        if (drawer == AdaptiveWorkspaceDrawer.Leading)
        {
            LeftDrawer.Visibility = Visibility.Visible;
            RightDrawer.Visibility = Visibility.Collapsed;
            _leftDrawerAnimator.SetOpen(false, animate, () => CompleteDrawerClose(drawer));
            return;
        }

        RightDrawer.Visibility = Visibility.Visible;
        LeftDrawer.Visibility = Visibility.Collapsed;
        _rightDrawerAnimator.SetOpen(false, animate, () => CompleteDrawerClose(drawer));
    }

    private void CompleteDrawerClose(AdaptiveWorkspaceDrawer drawer)
    {
        if (State?.VisibleDrawer != AdaptiveWorkspaceDrawer.None)
        {
            return;
        }

        if (State is { } state)
        {
            MovePaneContent(state);
        }
        else if (drawer == AdaptiveWorkspaceDrawer.Leading)
        {
            LeadingDrawerPresenter.Content = null;
        }
        else if (drawer == AdaptiveWorkspaceDrawer.Trailing)
        {
            TrailingDrawerPresenter.Content = null;
        }

        CollapseDrawerOverlay();
        RestoreDrawerFocus();
    }

    private void CollapseDrawerOverlay()
    {
        DrawerOverlay.Visibility = Visibility.Collapsed;
        LeftDrawer.Visibility = Visibility.Collapsed;
        RightDrawer.Visibility = Visibility.Collapsed;
        _leftDrawerAnimator.SyncToCurrentState();
        _rightDrawerAnimator.SyncToCurrentState();
    }

    private void RestoreDrawerFocus()
    {
        if (_restoreFocusTarget is not null)
        {
            _ = _restoreFocusTarget.Focus(FocusState.Programmatic);
            _restoreFocusTarget = null;
        }
    }

    private void QueueDrawerFocus(AdaptiveWorkspaceDrawer drawer)
    {
        StopDrawerFocusTimer();
        int requestVersion = ++_drawerFocusRequestVersion;
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            if (requestVersion != _drawerFocusRequestVersion || State?.VisibleDrawer != drawer)
            {
                return;
            }

            if (!await TryFocusFirstDrawerElementAsync(drawer))
            {
                // Content can be reparented into the drawer while its slide animation
                // starts. Keep focus in the modal surface until a child accepts it.
                _ = await TryFocusDrawerContainerAsync(drawer);
            }

            DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
            _drawerFocusTimer = timer;
            timer.Interval = TimeSpan.FromMilliseconds(50);
            bool focusAttemptInProgress = false;
            timer.Tick += async (_, _) =>
            {
                if (requestVersion != _drawerFocusRequestVersion ||
                    State?.VisibleDrawer != drawer)
                {
                    timer.Stop();
                    if (ReferenceEquals(_drawerFocusTimer, timer)) _drawerFocusTimer = null;
                    return;
                }

                if (focusAttemptInProgress)
                {
                    return;
                }

                Border targetDrawer = drawer == AdaptiveWorkspaceDrawer.Leading
                    ? LeftDrawer
                    : RightDrawer;
                if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focusedElement &&
                    IsWithin(focusedElement, targetDrawer))
                {
                    timer.Stop();
                    if (ReferenceEquals(_drawerFocusTimer, timer)) _drawerFocusTimer = null;
                    return;
                }

                focusAttemptInProgress = true;
                try
                {
                    if (!await TryFocusFirstDrawerElementAsync(drawer))
                    {
                        _ = await TryFocusDrawerContainerAsync(drawer);
                    }
                }
                finally
                {
                    focusAttemptInProgress = false;
                }
            };
            timer.Start();
        });
    }

    private async Task<bool> TryFocusFirstDrawerElementAsync(AdaptiveWorkspaceDrawer drawer)
    {
        Border targetDrawer = drawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        targetDrawer.UpdateLayout();
        AttachDrawerKeyHandlers(targetDrawer);
        return FindFirstUsableTabStop(targetDrawer) is UIElement focusTarget &&
            await TryFocusDrawerElementAsync(focusTarget) &&
            FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused &&
            IsWithin(focused, targetDrawer);
    }

    private Task<bool> TryFocusDrawerContainerAsync(AdaptiveWorkspaceDrawer drawer)
    {
        Border targetDrawer = drawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        return TryFocusDrawerElementAsync(targetDrawer);
    }

    private async Task<bool> TryFocusDrawerElementAsync(UIElement element)
    {
        if (IsAppModalPresenting())
        {
            return false;
        }

        AttachDrawerKeyHandler(element);
        _isTransferringDrawerFocus = true;
        try
        {
            FocusMovementResult result = await FocusManager.TryFocusAsync(element, FocusState.Keyboard);
            return result.Succeeded;
        }
        finally
        {
            _isTransferringDrawerFocus = false;
        }
    }

    private static bool IsAppModalPresenting()
    {
        try
        {
            return Application.Current is global::JitHub.WinUI.App app &&
                app.GetService<DialogPresentationCoordinator>().IsPresenting;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Adaptive workspace modal ownership check failed: {ex}");
            return false;
        }
    }

    private bool TryFocusFirstDrawerElement(AdaptiveWorkspaceDrawer drawer)
    {
        Border targetDrawer = drawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        targetDrawer.UpdateLayout();
        AttachDrawerKeyHandlers(targetDrawer);
        if (FindFirstUsableTabStop(targetDrawer) is not UIElement focusTarget ||
            !TryFocusDrawerElement(focusTarget))
        {
            return false;
        }

        return FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused &&
            IsWithin(focused, targetDrawer);
    }

    private bool TryFocusDrawerContainer(AdaptiveWorkspaceDrawer drawer)
    {
        Border targetDrawer = drawer == AdaptiveWorkspaceDrawer.Leading
            ? LeftDrawer
            : RightDrawer;
        return TryFocusDrawerElement(targetDrawer);
    }

    private bool TryFocusDrawerControl(Control control) => TryFocusDrawerElement(control);

    private bool TryFocusDrawerElement(UIElement element)
    {
        AttachDrawerKeyHandler(element);
        _isTransferringDrawerFocus = true;
        try
        {
            return element.Focus(FocusState.Keyboard);
        }
        finally
        {
            _isTransferringDrawerFocus = false;
        }
    }

    private void StopDrawerFocusTimer()
    {
        _drawerFocusTimer?.Stop();
        _drawerFocusTimer = null;
    }

    private void AttachDrawerKeyHandlers(DependencyObject drawer)
        => AttachDrawerKeyHandler(drawer);

    private void AttachDrawerKeyHandler(DependencyObject focusedElement)
    {
        UIElement? drawerRoot = IsWithin(focusedElement, LeftDrawer)
            ? LeftDrawer
            : IsWithin(focusedElement, RightDrawer)
                ? RightDrawer
                : null;
        if (drawerRoot is not null && _drawerKeyHandlerElements.Add(drawerRoot))
        {
            drawerRoot.AddHandler(PreviewKeyDownEvent, _drawerPreviewKeyHandler, handledEventsToo: true);
        }
    }

    private void DetachDrawerKeyHandlers()
    {
        foreach (UIElement element in _drawerKeyHandlerElements)
        {
            element.RemoveHandler(PreviewKeyDownEvent, _drawerPreviewKeyHandler);
        }

        _drawerKeyHandlerElements.Clear();
    }

    private void ApplyPaneButtons(AdaptiveWorkspaceState state)
    {
        bool showLeadingButton = ShowPaneButtons && state.ShouldShowLeadingPaneButton;
        bool showTrailingButton = ShowPaneButtons && state.ShouldShowTrailingPaneButton;

        OpenLeadingPaneButton.Visibility = showLeadingButton ? Visibility.Visible : Visibility.Collapsed;
        OpenTrailingPaneButton.Visibility = showTrailingButton ? Visibility.Visible : Visibility.Collapsed;
        PaneButtonHost.Visibility = showLeadingButton || showTrailingButton
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (PaneButtonHost.Visibility != Visibility.Visible)
        {
            InlineGrid.Margin = new Thickness(0);
            return;
        }

        bool anchorLeft = state.ShouldShowLeadingPaneButton;
        PaneButtonHost.HorizontalAlignment = anchorLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PaneButtonHost.Margin = anchorLeft
            ? new Thickness(4, 10, 0, 0)
            : new Thickness(0, 10, 4, 0);
        InlineGrid.Margin = anchorLeft
            ? new Thickness(48, 0, 0, 0)
            : new Thickness(0, 0, 48, 0);
    }

    private void ClearAllPresenters()
    {
        LeadingInlinePresenter.Content = null;
        PrimaryPresenter.Content = null;
        TrailingInlinePresenter.Content = null;
        LeadingDrawerPresenter.Content = null;
        TrailingDrawerPresenter.Content = null;
    }

    private void PlacePaneContent(object? content, ContentPresenter? targetPresenter)
    {
        ContentPresenter[] presenters =
        [
            LeadingInlinePresenter,
            PrimaryPresenter,
            TrailingInlinePresenter,
            LeadingDrawerPresenter,
            TrailingDrawerPresenter
        ];

        foreach (ContentPresenter presenter in presenters)
        {
            if (ReferenceEquals(presenter, targetPresenter))
            {
                continue;
            }

            if (ReferenceEquals(presenter.Content, content))
            {
                presenter.Content = null;
            }
        }

        if (targetPresenter is not null && !ReferenceEquals(targetPresenter.Content, content))
        {
            targetPresenter.Content = content;
        }
    }

    private double CalculateDrawerWidth(double requestedWidth)
    {
        double visibleOverlayWidth = GetVisibleOverlayWidth();
        double availableWidth = visibleOverlayWidth <= 0 || double.IsNaN(visibleOverlayWidth)
            ? requestedWidth
            : visibleOverlayWidth;
        double maxWidth = Math.Max(280, availableWidth - 44);
        return Math.Min(Math.Max(280, requestedWidth), maxWidth);
    }

    private double GetVisibleOverlayWidth()
    {
        double actualWidth = ActualWidth <= 0 || double.IsNaN(ActualWidth)
            ? 0
            : ActualWidth;
        double rootWidth = XamlRoot?.Size.Width ?? actualWidth;
        if (rootWidth <= 0 || double.IsNaN(rootWidth))
        {
            return actualWidth;
        }

        return actualWidth <= 0 ? rootWidth : Math.Min(actualWidth, rootWidth);
    }

    private AdaptiveWorkspaceBreakpoints CreateBreakpoints()
        => new(WideBreakpoint, MediumBreakpoint, NarrowBreakpoint);

    private void UpdateWorkspaceClip(double width, double height)
    {
        double safeWidth = double.IsFinite(width) ? Math.Max(0, width) : 0;
        double safeHeight = double.IsFinite(height) ? Math.Max(0, height) : 0;
        WorkspaceClip.Rect = new Windows.Foundation.Rect(0, 0, safeWidth, safeHeight);
    }

    private void UpdateAutomationIds()
    {
        string prefix = string.IsNullOrWhiteSpace(AutomationIdPrefix) ? "AdaptiveWorkspace" : AutomationIdPrefix;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(OpenLeadingPaneButton, $"{prefix}LeadingPaneButton");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(OpenTrailingPaneButton, $"{prefix}TrailingPaneButton");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(DrawerOverlay, $"{prefix}DrawerOverlay");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(LeftDrawer, $"{prefix}LeftDrawer");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(RightDrawer, $"{prefix}RightDrawer");
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

    protected override AutomationPeer OnCreateAutomationPeer()
        => new FrameworkElementAutomationPeer(this);
}
