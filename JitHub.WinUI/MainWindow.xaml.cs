using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private const int SwRestore = 9;
    private readonly UISettings _uiSettings = new();
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private bool _followSystemTheme;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
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

    public void ConfigureTheme(bool followSystemTheme)
    {
        _followSystemTheme = followSystemTheme;
        RootLayout.RequestedTheme = followSystemTheme
            ? GetCurrentSystemTheme()
            : ElementTheme.Default;
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

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        if (!_followSystemTheme)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => RootLayout.RequestedTheme = GetCurrentSystemTheme());
    }

    private static ElementTheme GetCurrentSystemTheme()
    {
        return ThemeService.GetSystemThemeStatic() == ApplicationTheme.Dark
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
