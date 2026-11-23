using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using JitHub.Core.Models.Activation;
using JitHub.Core.Models.Startup;
using JitHub.Core.Services.Startup;
using JitHub.Core.Services.Theming;
using JitHub.WinUI.Services.Settings;
using Microsoft.Win32;
using static JitHub.WinUI.Services.Startup.StartupSplashInterop;

namespace JitHub.WinUI.Services.Startup;

/// <summary>
/// A plain Win32 window showing the logo on the app canvas. It runs on its own
/// thread and needs no XAML or GPU device, so it can paint while the UI thread
/// is still blocked on graphics-driver initialization.
/// </summary>
internal sealed unsafe class NativeStartupSplash
{
    private const string WindowClassName = "JitHubStartupSplashWindow";
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int LogoSizeDip = 96;
    private const uint DismissMessage = WmApp + 1;
    private const uint FailsafeTimerId = 1;
    private const uint FailsafeMilliseconds = 20_000;
    private const uint BaseDpi = 96;

    private readonly LaunchOptions _launchOptions;
    private readonly Action _failsafeElapsed;
    private volatile StrongBox<StartupWindowBounds>? _windowBounds;
    private volatile bool _dismissRequested;
    private nint _window;
    private nint _backgroundBrush;
    private nint _logo;
    private int _logoSize;

    public NativeStartupSplash(LaunchOptions launchOptions, Action failsafeElapsed)
    {
        _launchOptions = launchOptions;
        _failsafeElapsed = failsafeElapsed;
        Thread thread = new(Run)
        {
            IsBackground = true,
            Name = "JitHub startup splash"
        };
        thread.Start();
    }

    /// <summary>Gets the splash rectangle once it is known, so the main window can match it.</summary>
    public StartupWindowBounds? WindowBounds => _windowBounds?.Value;

    public void Dismiss()
    {
        _dismissRequested = true;
        nint window = Volatile.Read(ref _window);
        if (window != 0)
        {
            _ = PostMessage(window, DismissMessage, 0, 0);
        }
    }

    private void Run()
    {
        GCHandle self = GCHandle.Alloc(this);
        nint instance = GetModuleHandle(null);
        nint owner = 0;
        bool classRegistered = false;
        try
        {
            (uint background, bool useDarkFrame) = ResolveAppearance();
            _backgroundBrush = CreateSolidBrush(background);
            classRegistered = RegisterWindowClass(instance);
            if (!classRegistered)
            {
                return;
            }

            // An owned window has no taskbar button and is never reported as the
            // process's main window, so automation and the shell keep tracking MainWindow.
            owner = CreateWindow(0, WindowClassName, null, WsPopup, 0, 0, 0, 0, 0, 0, instance, 0);
            nint window = CreateWindow(
                WsExNoActivate,
                WindowClassName,
                "JitHub",
                WsCaption | WsThickFrame,
                CwUseDefault,
                CwUseDefault,
                CwUseDefault,
                CwUseDefault,
                owner,
                0,
                instance,
                GCHandle.ToIntPtr(self));
            if (window == 0)
            {
                return;
            }

            Volatile.Write(ref _window, window);
            ConfigureFrame(window, useDarkFrame);
            PlaceAndLoadLogo(window);
            if (_dismissRequested)
            {
                _ = DestroyWindow(window);
                return;
            }

            _ = ShowWindow(window, SwShowNoActivate);
            _ = UpdateWindow(window);
            Program.LogStartupPhase("splash.painted");
            _ = SetTimer(window, FailsafeTimerId, FailsafeMilliseconds, 0);
            PumpMessages();
        }
        catch (Exception exception)
        {
            // An exception on this thread would end the process, and the splash is
            // optional: startup continues and the main window appears normally.
            Program.LogStartupPhase($"splash.failed:{exception.GetType().Name}:0x{exception.HResult:X8}");
        }
        finally
        {
            ReleaseResources(owner, instance, classRegistered);
            self.Free();
        }
    }

    private (uint Background, bool UseDarkFrame) ResolveAppearance()
    {
        ThemeService themes = new(new SettingService());
        bool useDark = StartupSplashPalette.UsesDarkTheme(
            _launchOptions.Theme,
            themes.GetTheme(),
            SystemUsesDarkTheme());
        if (IsHighContrastActive())
        {
            return (GetSysColor(ColorWindow), useDark);
        }

        string? palette = string.IsNullOrWhiteSpace(_launchOptions.Palette)
            ? themes.GetPalette()
            : _launchOptions.Palette;
        return (StartupSplashPalette.ResolveCanvasColorRef(palette, useDark), useDark);
    }

    private static bool SystemUsesDarkTheme() =>
        Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int usesLightTheme && usesLightTheme == 0;

    private static bool IsHighContrastActive()
    {
        HighContrast highContrast = new() { Size = (uint)sizeof(HighContrast) };
        return SystemParametersInfo(SpiGetHighContrast, highContrast.Size, &highContrast, 0) &&
            (highContrast.Flags & HcfHighContrastOn) != 0;
    }

    private static bool RegisterWindowClass(nint instance)
    {
        fixed (char* className = WindowClassName)
        {
            WindowClass windowClass = new()
            {
                Size = (uint)sizeof(WindowClass),
                WindowProc = &WindowProc,
                Instance = instance,
                Cursor = LoadCursor(0, IdcAppStarting),
                ClassName = className
            };
            return RegisterClass(&windowClass) != 0;
        }
    }

    private static void ConfigureFrame(nint window, bool useDarkFrame)
    {
        int darkFrame = useDarkFrame ? 1 : 0;
        _ = DwmSetWindowAttribute(window, DwmwaUseImmersiveDarkMode, &darkFrame, sizeof(int));
        int corners = DwmwcpRound;
        _ = DwmSetWindowAttribute(window, DwmwaWindowCornerPreference, &corners, sizeof(int));
    }

    private void PlaceAndLoadLogo(nint window)
    {
        uint dpi = GetDpiForWindow(window);
        MonitorInfo monitor = new() { Size = (uint)sizeof(MonitorInfo) };
        if (GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToPrimary), &monitor))
        {
            StartupWindowBounds bounds = StartupWindowPlacement.Calculate(
                new StartupWindowBounds(
                    monitor.Work.Left,
                    monitor.Work.Top,
                    monitor.Work.Right - monitor.Work.Left,
                    monitor.Work.Bottom - monitor.Work.Top),
                dpi);
            _ = SetWindowPos(
                window,
                0,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            _windowBounds = new StrongBox<StartupWindowBounds>(bounds);
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _logoSize = (int)Math.Round(LogoSizeDip * (dpi / (double)BaseDpi));
        _logo = LoadImage(0, iconPath, ImageIcon, _logoSize, _logoSize, LrLoadFromFile);
    }

    private static void PumpMessages()
    {
        Message message;
        while (GetMessage(&message, 0, 0, 0) > 0)
        {
            _ = DispatchMessage(&message);
        }
    }

    private void ReleaseResources(nint owner, nint instance, bool classRegistered)
    {
        Volatile.Write(ref _window, 0);
        if (owner != 0)
        {
            _ = DestroyWindow(owner);
        }

        if (classRegistered)
        {
            _ = UnregisterClass(WindowClassName, instance);
        }

        if (_logo != 0)
        {
            _ = DestroyIcon(_logo);
            _logo = 0;
        }

        if (_backgroundBrush != 0)
        {
            _ = DeleteObject(_backgroundBrush);
            _backgroundBrush = 0;
        }
    }

    private void Paint(nint window)
    {
        PaintStruct paint;
        nint deviceContext = BeginPaint(window, &paint);
        if (deviceContext == 0)
        {
            return;
        }

        Rect client;
        if (GetClientRect(window, &client))
        {
            _ = FillRect(deviceContext, &client, _backgroundBrush);
            if (_logo != 0)
            {
                _ = DrawIconEx(
                    deviceContext,
                    (client.Right - _logoSize) / 2,
                    (client.Bottom - _logoSize) / 2,
                    _logo,
                    _logoSize,
                    _logoSize,
                    0,
                    0,
                    DiNormal);
            }
        }

        _ = EndPaint(window, &paint);
    }

    /// <summary>
    /// Keeps the standard invisible resize borders on the left, right and bottom
    /// and removes the caption, matching the main window's extended title bar.
    /// </summary>
    private static void ApplyFrameInsets(nint window, nint lParam)
    {
        uint dpi = GetDpiForWindow(window);
        int padded = GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        int frameX = GetSystemMetricsForDpi(SmCxSizeFrame, dpi) + padded;
        int frameY = GetSystemMetricsForDpi(SmCySizeFrame, dpi) + padded;
        Rect* proposed = (Rect*)lParam;
        proposed->Left += frameX;
        proposed->Right -= frameX;
        proposed->Bottom -= frameY;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmNcCreate)
        {
            nint parameter = ((CreateStruct*)lParam)->CreateParams;
            if (parameter != 0)
            {
                _ = SetWindowLongPtr(window, GwlpUserData, parameter);
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        nint state = GetWindowLongPtr(window, GwlpUserData);
        if (state == 0 || GCHandle.FromIntPtr(state).Target is not NativeStartupSplash splash)
        {
            return DefWindowProc(window, message, wParam, lParam);
        }

        switch (message)
        {
            case WmNcCalcSize when wParam != 0:
                ApplyFrameInsets(window, lParam);
                return 0;
            case WmNcHitTest:
                return HtClient;
            case WmMouseActivate:
                return MaNoActivate;
            case WmEraseBkgnd:
                return 1;
            case WmPaint:
                splash.Paint(window);
                return 0;
            case WmTimer when wParam == (nint)FailsafeTimerId:
                splash._failsafeElapsed();
                return 0;
            case DismissMessage:
                _ = DestroyWindow(window);
                return 0;
            case WmDestroy:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }
}
