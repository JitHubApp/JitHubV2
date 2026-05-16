using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Resolves <see cref="ElementStyle"/> values for the current theme &amp; FrameworkElement,
/// merging Win11 defaults with theme overrides. Exposes <see cref="GetEffectiveStyle"/>
/// which is the only call sites needed to consume.
/// </summary>
public sealed class ThemeResolver
{
    private readonly FrameworkElement _host;
    private readonly MarkdownTheme _theme;
    private readonly IMarkdownSystemThemeProvider _systemTheme;

    internal static IMarkdownSystemThemeProvider? SystemThemeProviderOverride { get; set; }

    public ThemeResolver(FrameworkElement host, MarkdownTheme theme)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _systemTheme = SystemThemeProviderOverride ?? new MarkdownSystemThemeProvider(_host);
    }

    public ElementStyle GetEffectiveStyle(string elementKey)
    {
        var defaults = GetDefault(elementKey);
        if (_theme.Overrides.TryGetValue(elementKey, out var ov))
        {
            return new ElementStyle
            {
                FontFamily = ov.FontFamily ?? defaults.FontFamily,
                FontSize = ov.FontSize ?? defaults.FontSize,
                FontWeight = ov.FontWeight ?? defaults.FontWeight,
                FontStyle = ov.FontStyle ?? defaults.FontStyle,
                Foreground = ov.Foreground ?? defaults.Foreground,
                // Background/AccentBar are nullable Colors on both sides; '??' resolves
                // to the override value if set, else to the default value (which itself
                // may be null and therefore have no fill / no bar).
                Background = ov.Background ?? defaults.Background,
                AccentBar = ov.AccentBar ?? defaults.AccentBar,
                Underline = ov.Underline ?? defaults.Underline,
                Strikethrough = ov.Strikethrough ?? defaults.Strikethrough,
                Margin = ov.Margin ?? defaults.Margin,
                Padding = ov.Padding ?? defaults.Padding,
                LineHeightMultiplier = ov.LineHeightMultiplier ?? defaults.LineHeightMultiplier
            };
        }
        return defaults;
    }

    /// <summary>
    /// Captures all known element styles into an immutable <see cref="ThemeSnapshot"/>
    /// that can safely be read from background threads.
    /// Must be called on the UI thread.
    /// </summary>
    public ThemeSnapshot CreateSnapshot()
    {
        var allKeys = new[]
        {
            MarkdownElementKeys.Heading1, MarkdownElementKeys.Heading2,
            MarkdownElementKeys.Heading3, MarkdownElementKeys.Heading4,
            MarkdownElementKeys.Heading5, MarkdownElementKeys.Heading6,
            MarkdownElementKeys.Body,     MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.CodeBlock,MarkdownElementKeys.Quote,
            MarkdownElementKeys.Link,     MarkdownElementKeys.Strong,
            MarkdownElementKeys.Emphasis, MarkdownElementKeys.Strikethrough,
            MarkdownElementKeys.ListMarker, MarkdownElementKeys.ThematicBreak,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.TableHeader, MarkdownElementKeys.TableCell,
            MarkdownElementKeys.AlertNote, MarkdownElementKeys.AlertTip,
            MarkdownElementKeys.AlertImportant, MarkdownElementKeys.AlertWarning,
            MarkdownElementKeys.AlertCaution,
        };
        var dict = new Dictionary<string, ElementStyle>(allKeys.Length);
        foreach (var k in allKeys)
            dict[k] = GetEffectiveStyle(k);
        return new ThemeSnapshot(
            dict,
            ResolveSurfaceColor(),
            ResolveSelectionHighlightColor(),
            ResolveSelectionForegroundColor(),
            ResolveFocusVisualColor(),
            _systemTheme.IsHighContrast);
    }

    private ElementStyle GetDefault(string key)
    {
        if (_systemTheme.IsHighContrast)
            return GetHighContrastDefault(key);

        bool isDark = _host.ActualTheme == ElementTheme.Dark;

        // Hardcoded Win11 design token equivalents — bypasses the XAML resource system
        // which only works reliably for the app-level theme, not per-element themes.
        var fg = ResolvePrimaryTextColor(isDark);
        var fgSecondary = ResolveSecondaryTextColor(isDark);
        // Accent: try to get the user's accent color, fall back to Win11 blue.
        var accent      = _theme.AccentColor ?? ResolveAccentTextColor(isDark);
        var codeBg      = isDark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        var quoteBar    = isDark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x50, 0x00, 0x00, 0x00);

        // Single font family names for Win2D/DirectWrite. DirectWrite does NOT support
        // CSS-style comma-separated font stacks; the system font fallback maps emoji
        // code-points to Segoe UI Emoji automatically on Windows 10/11.
        const string font = "Segoe UI Variable";
        const string mono = "Consolas";

        return key switch
        {
            MarkdownElementKeys.Heading1 => new ElementStyle { FontFamily = font, FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 16, 0, 8) },
            MarkdownElementKeys.Heading2 => new ElementStyle { FontFamily = font, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 14, 0, 6) },
            MarkdownElementKeys.Heading3 => new ElementStyle { FontFamily = font, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 12, 0, 4) },
            MarkdownElementKeys.Heading4 => new ElementStyle { FontFamily = font, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 10, 0, 4) },
            MarkdownElementKeys.Heading5 => new ElementStyle { FontFamily = font, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 8, 0, 2) },
            MarkdownElementKeys.Heading6 => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fgSecondary, Margin = new Thickness(0, 6, 0, 2) },
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 0, 0, 8) },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = codeBg, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 8, 12, 8) },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = codeBg, Padding = new Thickness(2, 0, 2, 0) },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, AccentBar = quoteBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = accent, Underline = true },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = true, Foreground = fgSecondary },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = quoteBar, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fgSecondary, Margin = new Thickness(0, 2, 0, 8) },
            // Table styles: no Background — TableBox draws header row bg directly.
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg },
            MarkdownElementKeys.AlertNote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertTip => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertImportant => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertWarning => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertCaution => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 0, 0, 4) }
        };
    }

    private ElementStyle GetHighContrastDefault(string key)
    {
        var roles = MarkdownHighContrastDefaults.Resolve(key);
        var fg = ResolveHighContrastRole(roles.Foreground);
        var bg = roles.Background is { } backgroundRole ? ResolveHighContrastRole(backgroundRole) : (Color?)null;
        var accentBar = roles.AccentBar is { } accentRole ? ResolveHighContrastRole(accentRole) : (Color?)null;

        const string font = "Segoe UI Variable";
        const string mono = "Consolas";

        return key switch
        {
            MarkdownElementKeys.Heading1 => new ElementStyle { FontFamily = font, FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 16, 0, 8) },
            MarkdownElementKeys.Heading2 => new ElementStyle { FontFamily = font, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 14, 0, 6) },
            MarkdownElementKeys.Heading3 => new ElementStyle { FontFamily = font, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 12, 0, 4) },
            MarkdownElementKeys.Heading4 => new ElementStyle { FontFamily = font, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 10, 0, 4) },
            MarkdownElementKeys.Heading5 => new ElementStyle { FontFamily = font, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 8, 0, 2) },
            MarkdownElementKeys.Heading6 => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 6, 0, 2) },
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 0, 0, 8) },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = bg, AccentBar = accentBar, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 8, 12, 8) },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = bg, Padding = new Thickness(2, 0, 2, 0) },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Underline = roles.Underline },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = roles.Strikethrough, Foreground = fg },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = fg, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fg, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Background = bg },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg },
            MarkdownElementKeys.AlertNote or MarkdownElementKeys.AlertTip or MarkdownElementKeys.AlertImportant or
            MarkdownElementKeys.AlertWarning or MarkdownElementKeys.AlertCaution => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 0, 0, 4) }
        };
    }

    private Color ResolveHighContrastRole(MarkdownHighContrastColorRole role) => role switch
    {
        MarkdownHighContrastColorRole.WindowText => _systemTheme.WindowTextColor,
        MarkdownHighContrastColorRole.Window => _systemTheme.WindowColor,
        MarkdownHighContrastColorRole.Hotlight => _systemTheme.HotlightColor,
        MarkdownHighContrastColorRole.Highlight => _systemTheme.HighlightColor,
        MarkdownHighContrastColorRole.HighlightText => _systemTheme.HighlightTextColor,
        _ => _systemTheme.WindowTextColor,
    };

    private Color ResolveSelectionHighlightColor()
    {
        var nativeSelection = ResolveNativeSelectionHighlightColor();
        if (_systemTheme.IsHighContrast)
            return WithAlpha(nativeSelection, 0xFF);

        // TextBox paints the selection highlight over a clean text-control
        // surface after replacing the selected glyph foreground. Our canvas has
        // already painted the unselected glyphs underneath, so use the same
        // native resource but pre-composite translucent brushes over the
        // markdown surface. The resulting opaque color matches the native
        // visual while preventing old dark glyphs from bleeding through.
        return CompositeOver(nativeSelection, ResolveSurfaceColor());
    }

    private Color ResolveNativeSelectionHighlightColor()
    {
        if (TryResolveResourceColor("TextControlSelectionHighlightColor", out var color) ||
            TryResolveResourceColor("AccentFillColorSelectedTextBackgroundBrush", out color) ||
            TryResolveResourceColor("SystemControlHighlightAccentBrush", out color) ||
            TryResolveResourceColor("SystemColorHighlightColorBrush", out color) ||
            TryResolveResourceColor("SystemColorHighlightColor", out color))
        {
            return color;
        }

        return _systemTheme.IsHighContrast
            ? _systemTheme.HighlightColor
            : ResolveBrush("AccentFillColorDefaultBrush",
                ResolveBrush("AccentTextFillColorPrimaryBrush", Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)));
    }

    private Color ResolveSelectionForegroundColor()
    {
        if (_systemTheme.IsHighContrast)
            return _systemTheme.HighlightTextColor;

        if (TryResolveResourceColor("TextOnAccentFillColorSelectedTextBrush", out var color) ||
            TryResolveResourceColor("TextOnAccentFillColorSelectedText", out color) ||
            TryResolveResourceColor("SystemColorHighlightTextColorBrush", out color) ||
            TryResolveResourceColor("SystemColorHighlightTextColor", out color))
        {
            return Color.FromArgb(0xFF, color.R, color.G, color.B);
        }

        return Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private Color ResolveFocusVisualColor()
    {
        if (_systemTheme.IsHighContrast)
            return _systemTheme.HighlightColor;

        return ResolveBrush("SystemControlFocusVisualPrimaryBrush",
            ResolveBrush("FocusVisualPrimaryBrush",
                ResolveBrush("AccentTextFillColorPrimaryBrush", Color.FromArgb(0xFF, 0x00, 0x78, 0xD4))));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color CompositeOver(Color top, Color bottom)
    {
        double topA = top.A / 255.0;
        double bottomA = bottom.A / 255.0;
        double outA = topA + bottomA * (1.0 - topA);
        if (outA <= 0.0)
            return Color.FromArgb(0, 0, 0, 0);

        byte a = (byte)Math.Clamp((int)Math.Round(outA * 255.0), 0, 255);
        byte r = CompositeChannel(top.R, topA, bottom.R, bottomA, outA);
        byte g = CompositeChannel(top.G, topA, bottom.G, bottomA, outA);
        byte b = CompositeChannel(top.B, topA, bottom.B, bottomA, outA);
        return Color.FromArgb(a, r, g, b);
    }

    private static byte CompositeChannel(byte top, double topA, byte bottom, double bottomA, double outA)
    {
        var value = (top * topA + bottom * bottomA * (1.0 - topA)) / outA;
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private bool TryResolveResourceColor(string resourceKey, out Color color)
    {
        try
        {
            if (_host.Resources?.TryGetValue(resourceKey, out var hostValue) == true &&
                TryExtractColor(hostValue, out color))
            {
                return true;
            }

            var themeKey = _host.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            if (Application.Current?.Resources?.ThemeDictionaries != null &&
                Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var td) &&
                td is ResourceDictionary themeDict &&
                themeDict.TryGetValue(resourceKey, out var themeValue) &&
                TryExtractColor(themeValue, out color))
            {
                return true;
            }

            if (Application.Current?.Resources?.TryGetValue(resourceKey, out var appValue) == true &&
                TryExtractColor(appValue, out color))
            {
                return true;
            }
        }
        catch (Exception ex) { MarkdownDiagnostics.WriteLine($"[ThemeResolver] TryResolveResourceColor failed for '{resourceKey}': {ex.Message}"); }

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
                var alpha = (byte)Math.Clamp((int)Math.Round(brush.Color.A * brush.Opacity), 0, 255);
                color = Color.FromArgb(alpha, brush.Color.R, brush.Color.G, brush.Color.B);
                return true;
            default:
                color = default;
                return false;
        }
    }

    private Color ResolveSurfaceColor()
    {
        if (_systemTheme.IsHighContrast)
            return _systemTheme.WindowColor;

        if (TryResolveResourceColor("TextControlBackgroundFocused", out var color) ||
            TryResolveResourceColor("TextControlBackground", out color) ||
            TryResolveResourceColor("LayerFillColorDefaultBrush", out color) ||
            TryResolveResourceColor("SolidBackgroundFillColorBaseBrush", out color) ||
            TryResolveResourceColor("ApplicationPageBackgroundThemeBrush", out color))
        {
            return CompositeOver(color, _host.ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
                : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        }

        return _host.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private Color ResolvePrimaryTextColor(bool isDark)
    {
        if (TryResolveResourceColor("TextControlForegroundFocused", out var color) ||
            TryResolveResourceColor("TextControlForeground", out color) ||
            TryResolveResourceColor("TextFillColorPrimaryBrush", out color) ||
            TryResolveResourceColor("TextFillColorPrimary", out color))
        {
            return color;
        }

        return isDark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
    }

    private Color ResolveSecondaryTextColor(bool isDark)
    {
        if (TryResolveResourceColor("TextFillColorSecondaryBrush", out var color) ||
            TryResolveResourceColor("TextFillColorSecondary", out color) ||
            TryResolveResourceColor("TextControlPlaceholderForeground", out color))
        {
            return color;
        }

        return isDark
            ? Color.FromArgb(0xFF, 0x9D, 0x9D, 0x9D)
            : Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A);
    }

    private Color ResolveAccentTextColor(bool isDark)
    {
        if (TryResolveResourceColor("AccentTextFillColorPrimaryBrush", out var color) ||
            TryResolveResourceColor("AccentTextFillColorPrimary", out color) ||
            TryResolveResourceColor("SystemControlForegroundAccentBrush", out color) ||
            TryResolveResourceColor("AccentFillColorDefaultBrush", out color))
        {
            return color;
        }

        return isDark
            ? Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF)
            : Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);
    }

    private Color ResolveBrush(string resourceKey, Color fallback)
    {
        try
        {
            var themeKey = _host.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";

            // Look in the correct App ThemeDictionary for the host element's current theme.
            // NOTE: We intentionally skip the global Application.Current.Resources fallback
            // because it returns the app-level theme color (not the element-level theme),
            // which breaks per-element RequestedTheme overrides.
            if (Application.Current?.Resources?.ThemeDictionaries != null &&
                Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var td) &&
                td is ResourceDictionary themeDict &&
                themeDict.TryGetValue(resourceKey, out var r1) && r1 is SolidColorBrush b1)
                return b1.Color;

            if (_host.Resources?.TryGetValue(resourceKey, out var r2) == true && r2 is SolidColorBrush b2)
                return b2.Color;
        }
        catch (Exception ex) { MarkdownDiagnostics.WriteLine($"[ThemeResolver] ResolveBrush failed for '{resourceKey}': {ex.Message}"); }
        return fallback;
    }
}
