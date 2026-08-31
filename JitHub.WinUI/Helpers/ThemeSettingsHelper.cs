using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;

namespace JitHub.WinUI.Helpers;

internal static class ThemeSettingsHelper
{
    public static AppThemeSettingsMonitor? TryGetFor(FrameworkElement element) =>
        AppThemeSettingsMonitor.TryGetFor(element);

    public static bool IsHighContrastActive(AppThemeSettingsMonitor? monitor) =>
        monitor?.IsHighContrastActive ?? false;
}

/// <summary>
/// Owns the one native ThemeSettings subscription for a window. WinAppSDK can
/// fail-fast when Changed is removed after its window feature is detached, so
/// monitors are intentionally rooted for the remaining process lifetime.
/// </summary>
internal sealed class AppThemeSettingsMonitor
{
    private static readonly object Gate = new();
    private static readonly Dictionary<WindowId, AppThemeSettingsMonitor> Monitors = [];
    private static int _createFailureReported;
    private static int _subscribeFailureReported;
    private static int _readFailureReported;

    private readonly ThemeSettings _settings;

    private AppThemeSettingsMonitor(ThemeSettings settings)
    {
        _settings = settings;
        try
        {
            _settings.Changed += ThemeSettings_Changed;
        }
        catch (Exception exception)
        {
            ReportOnce(ref _subscribeFailureReported, exception, "ui-theme-settings-subscribe");
        }
    }

    public event EventHandler? Changed;

    public bool IsHighContrastActive
    {
        get
        {
            try
            {
                return _settings.HighContrast;
            }
            catch (Exception exception)
            {
                ReportOnce(ref _readFailureReported, exception, "ui-theme-settings-read");
                return false;
            }
        }
    }

    public static AppThemeSettingsMonitor? TryGetFor(FrameworkElement element)
    {
        try
        {
            XamlRoot? xamlRoot = element.XamlRoot;
            if (xamlRoot is null)
            {
                return null;
            }

            WindowId windowId = xamlRoot.ContentIslandEnvironment.AppWindowId;
            lock (Gate)
            {
                if (!Monitors.TryGetValue(windowId, out AppThemeSettingsMonitor? monitor))
                {
                    monitor = new AppThemeSettingsMonitor(ThemeSettings.CreateForWindowId(windowId));
                    Monitors.Add(windowId, monitor);
                }

                return monitor;
            }
        }
        catch (Exception exception)
        {
            ReportOnce(ref _createFailureReported, exception, "ui-theme-settings-create");
            return null;
        }
    }

    private void ThemeSettings_Changed(ThemeSettings sender, object args)
    {
        EventHandler? handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)subscriber)(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                HandledFailureReporter.Report(exception, "ui-theme-settings-listener");
            }
        }
    }

    private static void ReportOnce(ref int reported, Exception exception, string category)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
        {
            HandledFailureReporter.Report(exception, category);
        }
    }
}
