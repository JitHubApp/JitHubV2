using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using JitHub.Models;
using JitHub.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.Activation;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace JitHub.WinUI;

public sealed partial class MainWindow : Window
{
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint WmSetIcon = 0x0080;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const int VkControl = 0x11;
    private const int VkK = 0x4B;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const int SwRestore = 9;
    private static readonly nuint KeyboardSubclassId = 0x4A484B31;
    private readonly UISettings _uiSettings = new();
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly SubclassProc _keyboardSubclassProc;
    private readonly nint _hwnd;
    private nint _largeIconHandle;
    private nint _smallIconHandle;
    private string _configuredTheme = ThemeConst.System;
    private bool _followSystemTheme;

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

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc subclassProc, nuint subclassId, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc subclassProc, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);

    public event EventHandler? SearchShortcutRequested;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _hwnd = WindowNative.GetWindowHandle(this);
        _keyboardSubclassProc = KeyboardSubclassProc;
        _ = SetWindowSubclass(_hwnd, _keyboardSubclassProc, KeyboardSubclassId, 0);

        ConfigureWindowIcon();
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        Closed += (_, _) =>
        {
            _ = RemoveWindowSubclass(_hwnd, _keyboardSubclassProc, KeyboardSubclassId);
            ReleaseWindowIcons();
        };
    }

    public void ProcessActivation()
    {
        ActivateAndForeground();
        ActivationStatusHost.Visibility = Visibility.Collapsed;
    }

    public void ShowActivationError(string message)
    {
        ActivateAndForeground();
        ActivationStatusText.Text = $"Activation failed: {message}";
        ActivationStatusHost.Visibility = Visibility.Visible;
    }

    public void ConfigureTheme(string? theme)
    {
        _configuredTheme = string.IsNullOrWhiteSpace(theme) ? ThemeConst.System : theme;
        _followSystemTheme = string.Equals(_configuredTheme, ThemeConst.System, StringComparison.OrdinalIgnoreCase);
        RootLayout.RequestedTheme = ResolveElementTheme(_configuredTheme);
    }

    public Frame ContentFrameHost => ContentFrame;

    public void SetPageTitleBar(UIElement? titleBar)
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar ?? AppTitleBar);
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

        _ = DispatcherQueue.TryEnqueue(() => RootLayout.RequestedTheme = ResolveElementTheme(_configuredTheme));
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
