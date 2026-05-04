using System;
using Microsoft.UI.Xaml;

namespace CommunityToolkit.WinUI.UI.Helpers;

public sealed class ThemeListener
{
    private FrameworkElement? _rootElement;

    public ThemeListener()
    {
        EnsureAttached();
    }

    public ApplicationTheme CurrentTheme
    {
        get
        {
            EnsureAttached();
            return _rootElement is null
                ? GetRequestedTheme()
                : ToApplicationTheme(_rootElement.ActualTheme);
        }
    }

    public event Action<ThemeListener>? ThemeChanged;

    private void EnsureAttached()
    {
        if (_rootElement is not null)
        {
            return;
        }

        if (Application.Current is not JitHub.WinUI.App app ||
            app.CurrentMainWindow?.Content is not FrameworkElement rootElement)
        {
            return;
        }

        _rootElement = rootElement;
        _rootElement.ActualThemeChanged += RootElement_ActualThemeChanged;
    }

    private void RootElement_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ThemeChanged?.Invoke(this);
    }

    private static ApplicationTheme GetRequestedTheme()
    {
        if (Application.Current is null)
        {
            return ApplicationTheme.Light;
        }

        return Application.Current.RequestedTheme switch
        {
            ApplicationTheme.Dark => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light
        };
    }

    private static ApplicationTheme ToApplicationTheme(ElementTheme theme)
    {
        return theme switch
        {
            ElementTheme.Dark => ApplicationTheme.Dark,
            ElementTheme.Light => ApplicationTheme.Light,
            _ => GetRequestedTheme()
        };
    }
}
