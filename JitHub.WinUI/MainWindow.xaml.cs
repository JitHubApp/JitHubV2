using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Services;
using JitHub.WinUI.Performance;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.Activation;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace JitHub.WinUI;

public sealed partial class MainWindow : Window
{
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint WmClose = 0x0010;
    private const uint WmSetIcon = 0x0080;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const int VkControl = 0x11;
    private const int VkEscape = 0x1B;
    private const int VkK = 0x4B;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const int SwRestore = 9;
    private const int DefaultLaunchWidthDip = 1360;
    private const int DefaultLaunchHeightDip = 900;
    private const int MinimumLaunchWidthDip = 960;
    private const int MinimumLaunchHeightDip = 640;
    private static readonly nuint KeyboardSubclassId = 0x4A484B31;
    private readonly UISettings _uiSettings = new();
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly SubclassProc _keyboardSubclassProc;
    private readonly nint _hwnd;
    private readonly Grid _rootLayout;
    private readonly Border _appTitleBar;
    private readonly Frame _contentFrame;
    private readonly Grid _contentDialogHost;
    private readonly Button _dialogFocusSentinel;
    private readonly Border _activationStatusHost;
    private readonly TextBlock _activationStatusText;
    private readonly ProductPerformanceVisualProbe? _productPerformanceVisualProbe;
    private nint _largeIconHandle;
    private nint _smallIconHandle;
    private string _configuredTheme = ThemeConst.System;
    private bool _followSystemTheme;
    private bool _allowCloseAfterDiagnostics;
    private bool _closingRequestedRaised;
    private Task? _diagnosticsCloseTask;
    private ContentDialog? _activeContentDialog;

    private delegate nint SubclassProc(nint hWnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImage(nint hInstance, string name, uint type, int desiredWidth, int desiredHeight, uint loadFlags);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static partial nint SendMessage(nint hWnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hWnd);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc subclassProc, nuint subclassId, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc subclassProc, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);

    public event EventHandler? SearchShortcutRequested;

    public event EventHandler? ClosingRequested;

    public MainWindow()
    {
        InitializeComponent();

        _rootLayout = ResolveRequiredElement(RootLayout, nameof(RootLayout));
        _appTitleBar = ResolveRequiredElement(AppTitleBar, nameof(AppTitleBar));
        _contentFrame = ResolveRequiredElement(ContentFrame, nameof(ContentFrame));
        _contentDialogHost = ResolveRequiredElement(ContentDialogHost, nameof(ContentDialogHost));
        _dialogFocusSentinel = ResolveRequiredElement(DialogFocusSentinel, nameof(DialogFocusSentinel));
        _activationStatusHost = ResolveRequiredElement(ActivationStatusHost, nameof(ActivationStatusHost));
        _activationStatusText = ResolveRequiredElement(ActivationStatusText, nameof(ActivationStatusText));
        AutomationProperties.SetAccessibilityView(
            _rootLayout,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Control);
        AutomationProperties.SetAutomationId(_rootLayout, "JitHubMainWindowRoot");
        AutomationProperties.SetName(_rootLayout, "JitHub application window");
        KeyboardAccelerator dialogEscapeAccelerator = new() { Key = VirtualKey.Escape };
        dialogEscapeAccelerator.Invoked += DialogEscapeAccelerator_Invoked;
        _rootLayout.KeyboardAccelerators.Add(dialogEscapeAccelerator);
        _productPerformanceVisualProbe = ProductPerformanceVisualProbe.TryStart(_rootLayout);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_appTitleBar);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _hwnd = WindowNative.GetWindowHandle(this);
        _keyboardSubclassProc = KeyboardSubclassProc;
        _ = SetWindowSubclass(_hwnd, _keyboardSubclassProc, KeyboardSubclassId, 0);

        ApplyDefaultLaunchPlacement();
        ConfigureWindowIcon();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) =>
        {
            _productPerformanceVisualProbe?.Dispose();
            MarkdownRenderer.MarkdownRendererRuntime.Shutdown();
            _ = RemoveWindowSubclass(_hwnd, _keyboardSubclassProc, KeyboardSubclassId);
            ReleaseWindowIcons();
        };
    }

    public void ProcessActivation()
    {
        ActivateAndForeground();
        _activationStatusHost.Visibility = Visibility.Collapsed;
    }

    public void ShowActivationError(string message)
    {
        ShowStatus($"Activation failed: {message}");
    }

    public void ShowStatus(string message)
    {
        ActivateAndForeground();
        _activationStatusText.Text = message;
        _activationStatusHost.Visibility = Visibility.Visible;
    }

    public void ConfigureTheme(string? theme)
    {
        _configuredTheme = string.IsNullOrWhiteSpace(theme) ? ThemeConst.System : theme;
        _followSystemTheme = string.Equals(_configuredTheme, ThemeConst.System, StringComparison.OrdinalIgnoreCase);
        _rootLayout.RequestedTheme = ResolveElementTheme(_configuredTheme);
    }

    public Frame ContentFrameHost => _contentFrame;

    internal XamlRoot? DialogXamlRoot => _rootLayout.XamlRoot;

    internal async Task<ContentDialogResult> ShowContentDialogAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        _contentDialogHost.Visibility = Visibility.Visible;
        _contentDialogHost.IsHitTestVisible = true;
        _contentDialogHost.Children.Add(dialog);
        _activeContentDialog = dialog;
        bool contentFrameWasEnabled = _contentFrame.IsEnabled;
        bool contentFrameWasHitTestVisible = _contentFrame.IsHitTestVisible;
        _contentFrame.IsHitTestVisible = false;
        _contentFrame.IsEnabled = false;
        RoutedEventHandler loadedHandler = (_, _) => EnsureContentDialogFocus(dialog);
        void OpenedHandler(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
            EnsureContentDialogFocus(sender);
        KeyEventHandler escapeHandler = (_, args) =>
        {
            if (args.Key != VirtualKey.Escape)
            {
                return;
            }

            dialog.Hide();
            args.Handled = true;
        };
        dialog.Loaded += loadedHandler;
        dialog.Opened += OpenedHandler;
        dialog.PreviewKeyDown += escapeHandler;
        try
        {
            var showOperation = dialog.ShowAsync(ContentDialogPlacement.InPlace);
            EnsureContentDialogFocus(dialog);
            return await showOperation;
        }
        finally
        {
            dialog.Loaded -= loadedHandler;
            dialog.Opened -= OpenedHandler;
            dialog.PreviewKeyDown -= escapeHandler;
            if (ReferenceEquals(_activeContentDialog, dialog))
            {
                _activeContentDialog = null;
            }
            _contentFrame.IsEnabled = contentFrameWasEnabled;
            _contentFrame.IsHitTestVisible = contentFrameWasHitTestVisible;
            _contentDialogHost.Children.Remove(dialog);
            _dialogFocusSentinel.IsTabStop = false;
            _contentDialogHost.IsHitTestVisible = false;
            _contentDialogHost.Visibility = Visibility.Collapsed;
        }
    }

    internal void EnsureContentDialogFocus(ContentDialog dialog)
    {
        if (IsFocusWithinDialog(dialog))
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            async () =>
            {
                if (!IsFocusWithinDialog(dialog))
                {
                    await FocusFirstDialogElementAsync(dialog);
                }
            });
    }

    internal void ScheduleContentDialogFocusValidation(ContentDialog dialog)
    {
        _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _ = DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => EnsureContentDialogFocus(dialog));
            });
    }

    private bool IsFocusWithinDialog(ContentDialog dialog)
    {
        try
        {
            DependencyObject? focused = FocusManager.GetFocusedElement(_rootLayout.XamlRoot) as DependencyObject;
            while (focused is not null)
            {
                if (ReferenceEquals(focused, dialog))
                {
                    return true;
                }

                focused = VisualTreeHelper.GetParent(focused);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dialog focus inspection was skipped: {ex}");
        }

        return false;
    }

    private void DialogEscapeAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_activeContentDialog is not ContentDialog activeDialog)
        {
            return;
        }

        activeDialog.Hide();
        args.Handled = true;
    }

    private async Task FocusFirstDialogElementAsync(ContentDialog dialog)
    {
        dialog.UpdateLayout();
        _dialogFocusSentinel.IsTabStop = true;
        _contentFrame.IsEnabled = true;
        try
        {
            _ = await FocusManager.TryFocusAsync(_dialogFocusSentinel, FocusState.Programmatic);
        }
        finally
        {
            _contentFrame.IsEnabled = false;
        }
        Control[] focusableControls = EnumerateDescendantControls(dialog)
            .Where(static control =>
                control.IsTabStop &&
                control.IsEnabled &&
                control.Visibility == Visibility.Visible &&
                control.ActualWidth > 0 &&
                control.ActualHeight > 0 &&
                control is not ListView { Items.Count: 0 })
            .OrderBy(static control => GetDialogFocusPriority(control))
            .ToArray();
        foreach (Control focusable in focusableControls)
        {
            FocusMovementResult result = await FocusManager.TryFocusAsync(focusable, FocusState.Keyboard);
            if (result.Succeeded)
            {
                _dialogFocusSentinel.IsTabStop = false;
                return;
            }
        }

        _dialogFocusSentinel.IsTabStop = false;
    }

    private static IEnumerable<Control> EnumerateDescendantControls(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Control control)
            {
                yield return control;
            }

            foreach (Control descendant in EnumerateDescendantControls(child))
            {
                yield return descendant;
            }
        }
    }

    private static int GetDialogFocusPriority(Control control) => control switch
    {
        TextBox or PasswordBox => 0,
        ComboBox or ToggleSwitch or CheckBox or RadioButton => 1,
        ListView => 2,
        Button => 3,
        _ => 4
    };

    public void SetPageTitleBar(UIElement? titleBar)
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar ?? _appTitleBar);
    }

    public void SetTitleBarPassthroughRegions(params FrameworkElement[] interactiveElements)
    {
        _nonClientPointerSource.ClearRegionRects(NonClientRegionKind.Passthrough);

        RectInt32[] rects = interactiveElements
            .Select(TryGetElementRect)
            .Where(static rect => rect.HasValue)
            .Select(static rect => rect!.Value)
            .ToArray();

        if (rects.Length > 0)
        {
            _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, rects);
        }
    }

    public void ClearTitleBarPassthroughRegions()
    {
        _nonClientPointerSource.ClearRegionRects(NonClientRegionKind.Passthrough);
    }

    private void ActivateAndForeground()
    {
        Activate();

        nint hwnd = WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        _ = SetForegroundWindow(hwnd);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_closingRequestedRaised)
        {
            _closingRequestedRaised = true;
            ClosingRequested?.Invoke(this, EventArgs.Empty);
        }

        if (_allowCloseAfterDiagnostics)
        {
            return;
        }

        args.Cancel = true;
        _diagnosticsCloseTask ??= DrainDiagnosticsAndCloseAsync();
    }

    private async Task DrainDiagnosticsAndCloseAsync()
    {
        try
        {
            await DismissActiveContentDialogBeforeCloseAsync();
            App app = (App)Application.Current;
            app.QueueDiagnosticsCloseProbeIfRequested();
            await app.ShutdownBackgroundTasksAsync(TimeSpan.FromSeconds(5));
            await app.ShutdownDiagnosticsAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _allowCloseAfterDiagnostics = true;
            Close();
        }
    }

    private async Task DismissActiveContentDialogBeforeCloseAsync()
    {
        ContentDialog? dialog = _activeContentDialog;
        if (dialog is null)
        {
            return;
        }

        try
        {
            if (dialog.IsLoaded)
            {
                dialog.Hide();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"The active dialog could not be dismissed before close: {exception.Message}");
        }

        for (int attempt = 0; attempt < 50 && ReferenceEquals(_activeContentDialog, dialog); attempt++)
        {
            await Task.Delay(20);
        }
    }

    private void ApplyDefaultLaunchPlacement()
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;
        double scale = Math.Max(1d, GetDpiForWindow(_hwnd) / 96d);

        int preferredWidth = DipToPixels(DefaultLaunchWidthDip, scale);
        int preferredHeight = DipToPixels(DefaultLaunchHeightDip, scale);
        int minimumWidth = DipToPixels(MinimumLaunchWidthDip, scale);
        int minimumHeight = DipToPixels(MinimumLaunchHeightDip, scale);

        int maxWidth = Math.Max(1, (int)Math.Round(workArea.Width * 0.88));
        int maxHeight = Math.Max(1, (int)Math.Round(workArea.Height * 0.88));
        int width = Math.Clamp(preferredWidth, Math.Min(minimumWidth, maxWidth), maxWidth);
        int height = Math.Clamp(preferredHeight, Math.Min(minimumHeight, maxHeight), maxHeight);

        int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private static int DipToPixels(int dips, double scale)
        => Math.Max(1, (int)Math.Round(dips * scale));

    private void ConfigureWindowIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        AppWindow.SetIcon(iconPath);

        nint hwnd = WindowNative.GetWindowHandle(this);
        int smallIconWidth = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        int smallIconHeight = Math.Max(16, GetSystemMetrics(SmCySmIcon));
        int largeIconWidth = Math.Max(32, GetSystemMetrics(SmCxIcon));
        int largeIconHeight = Math.Max(32, GetSystemMetrics(SmCyIcon));

        _smallIconHandle = LoadImage(nint.Zero, iconPath, ImageIcon, smallIconWidth, smallIconHeight, LrLoadFromFile);
        _largeIconHandle = LoadImage(nint.Zero, iconPath, ImageIcon, largeIconWidth, largeIconHeight, LrLoadFromFile);

        if (_smallIconHandle != nint.Zero)
        {
            _ = SendMessage(hwnd, WmSetIcon, IconSmall, _smallIconHandle);
            _ = SendMessage(hwnd, WmSetIcon, IconSmall2, _smallIconHandle);
        }

        if (_largeIconHandle != nint.Zero)
        {
            _ = SendMessage(hwnd, WmSetIcon, IconBig, _largeIconHandle);
        }
    }

    private void ReleaseWindowIcons()
    {
        if (_smallIconHandle != nint.Zero)
        {
            _ = DestroyIcon(_smallIconHandle);
            _smallIconHandle = nint.Zero;
        }

        if (_largeIconHandle != nint.Zero)
        {
            _ = DestroyIcon(_largeIconHandle);
            _largeIconHandle = nint.Zero;
        }
    }

    private nint KeyboardSubclassProc(nint hWnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (message == WmClose)
        {
            MarkdownRenderer.MarkdownRendererRuntime.BeginShutdown();
        }

        if ((message == WmKeyDown || message == WmSysKeyDown) &&
            wParam == VkEscape &&
            _activeContentDialog is ContentDialog activeDialog)
        {
            _ = DispatcherQueue.TryEnqueue(activeDialog.Hide);
            return 0;
        }

        if ((message == WmKeyDown || message == WmSysKeyDown)
            && wParam == VkK
            && IsControlKeyPressed())
        {
            _ = DispatcherQueue.TryEnqueue(() => SearchShortcutRequested?.Invoke(this, EventArgs.Empty));
            return 0;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static bool IsControlKeyPressed()
    {
        return (GetKeyState(VkControl) & 0x8000) != 0;
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        if (!_followSystemTheme)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => _rootLayout.RequestedTheme = ResolveElementTheme(_configuredTheme));
    }

    private static ElementTheme GetCurrentSystemTheme()
    {
        return ThemeService.GetSystemThemeStatic() == ApplicationTheme.Dark
            ? ElementTheme.Dark
            : ElementTheme.Light;
    }

    private static ElementTheme ResolveElementTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme) || string.Equals(theme, ThemeConst.System, StringComparison.OrdinalIgnoreCase))
        {
            return GetCurrentSystemTheme();
        }

        return string.Equals(theme, ThemeConst.Dark, StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Dark
            : ElementTheme.Light;
    }

    private T ResolveRequiredElement<T>(T? generatedField, string name)
        where T : FrameworkElement
    {
        if (generatedField is not null)
        {
            return generatedField;
        }

        if (Content is T contentElement && string.Equals(name, nameof(RootLayout), StringComparison.Ordinal))
        {
            return contentElement;
        }

        if (Content is FrameworkElement contentRoot && contentRoot.FindName(name) is T namedElement)
        {
            return namedElement;
        }

        throw new InvalidOperationException(
            $"MainWindow XAML initialization did not create required {typeof(T).Name} '{name}'. " +
            "The app cannot activate without a complete Window namescope.");
    }

    private static RectInt32? TryGetElementRect(FrameworkElement? element)
    {
        if (element is null || element.XamlRoot is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        if (element.XamlRoot.Content is not UIElement root)
        {
            return null;
        }

        GeneralTransform transform = element.TransformToVisual(root);
        Point point = transform.TransformPoint(new Point(0, 0));
        double scale = element.XamlRoot.RasterizationScale;

        return new RectInt32(
            (int)Math.Round(point.X * scale),
            (int)Math.Round(point.Y * scale),
            Math.Max(1, (int)Math.Round(element.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(element.ActualHeight * scale)));
    }

}
