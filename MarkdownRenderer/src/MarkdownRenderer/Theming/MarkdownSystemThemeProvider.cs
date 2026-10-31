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

/// <summary>
/// The contrast theme a <see cref="MarkdownTheme.ForcedContrastColors"/> value
/// forces: contrast is on and the colors are the theme's, not the system's.
/// </summary>
internal sealed class ForcedMarkdownSystemThemeProvider(MarkdownContrastColors colors) : IMarkdownSystemThemeProvider
{
    private readonly MarkdownContrastColors _colors = colors ?? throw new ArgumentNullException(nameof(colors));

    public bool IsHighContrast => true;
    public Color WindowTextColor => _colors.WindowTextColor;
    public Color WindowColor => _colors.WindowColor;
    public Color HotlightColor => _colors.HotlightColor;
    public Color HighlightColor => _colors.HighlightColor;
    public Color HighlightTextColor => _colors.HighlightTextColor;
}
