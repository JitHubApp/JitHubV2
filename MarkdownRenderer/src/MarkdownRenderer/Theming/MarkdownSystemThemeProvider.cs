using System;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MarkdownRenderer.Theming;

internal interface IMarkdownSystemThemeProvider
{
    bool IsHighContrast { get; }
    Color WindowTextColor { get; }
    Color WindowColor { get; }
    Color HotlightColor { get; }
    Color HighlightColor { get; }
    Color HighlightTextColor { get; }
}

internal sealed class MarkdownSystemThemeProvider : IMarkdownSystemThemeProvider
{
    private readonly FrameworkElement _host;
    private readonly ThemeSettings? _themeSettings;

    public MarkdownSystemThemeProvider(FrameworkElement host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        try { _themeSettings = CreateThemeSettings(host); }
        catch { }
    }

    public bool IsHighContrast
    {
        get
        {
            try { return _themeSettings?.HighContrast == true; }
            catch { return false; }
        }
    }

    public Color WindowTextColor => ResolveSystemColor("SystemColorWindowTextColor", "SystemColorWindowTextBrush", UIColorType.Foreground);
    public Color WindowColor => ResolveSystemColor("SystemColorWindowColor", "SystemColorWindowBrush", UIColorType.Background);
    public Color HotlightColor => ResolveSystemColor("SystemColorHotlightColor", "SystemColorHotlightBrush", UIColorType.Accent);
    public Color HighlightColor => ResolveSystemColor("SystemColorHighlightColor", "SystemColorHighlightBrush", UIColorType.Accent);
    public Color HighlightTextColor => ResolveSystemColor("SystemColorHighlightTextColor", "SystemColorHighlightTextBrush", UIColorType.Background);

    private Color ResolveSystemColor(string colorKey, string brushKey, UIColorType fallbackType)
    {
        if (TryResolveResourceColor(colorKey, out var color) ||
            TryResolveResourceColor(brushKey, out color))
        {
            return color;
        }

        try { return new UISettings().GetColorValue(fallbackType); }
        catch { return fallbackType == UIColorType.Background ? Color.FromArgb(0xFF, 0, 0, 0) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF); }
    }

    private static ThemeSettings? CreateThemeSettings(FrameworkElement host)
    {
        var xamlRoot = host.XamlRoot;
        if (xamlRoot?.ContentIslandEnvironment is null) return null;
        return ThemeSettings.CreateForWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId);
    }

    private bool TryResolveResourceColor(string key, out Color color)
    {
        try
        {
            if (_host.Resources?.TryGetValue(key, out var hostValue) == true &&
                TryExtractColor(hostValue, out color))
            {
                return true;
            }

            if (Application.Current?.Resources?.TryGetValue(key, out var appValue) == true &&
                TryExtractColor(appValue, out color))
            {
                return true;
            }

            if (Application.Current?.Resources?.ThemeDictionaries != null)
            {
                foreach (var dictionary in Application.Current.Resources.ThemeDictionaries.Values)
                {
                    if (dictionary is ResourceDictionary rd &&
                        rd.TryGetValue(key, out var themeValue) &&
                        TryExtractColor(themeValue, out color))
                    {
                        return true;
                    }
                }
            }
        }
        catch { }

        color = default;
        return false;
    }

    private static bool TryExtractColor(object value, out Color color)
    {
        switch (value)
        {
            case Color c:
                color = c;
                return true;
            case SolidColorBrush brush:
                color = brush.Color;
                return true;
            default:
                color = default;
                return false;
        }
    }
}

internal sealed class ForcedMarkdownSystemThemeProvider : IMarkdownSystemThemeProvider
{
    public bool IsHighContrast { get; init; }
    public Color WindowTextColor { get; init; } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    public Color WindowColor { get; init; } = Color.FromArgb(0xFF, 0, 0, 0);
    public Color HotlightColor { get; init; } = Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF);
    public Color HighlightColor { get; init; } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00);
    public Color HighlightTextColor { get; init; } = Color.FromArgb(0xFF, 0, 0, 0);
}
