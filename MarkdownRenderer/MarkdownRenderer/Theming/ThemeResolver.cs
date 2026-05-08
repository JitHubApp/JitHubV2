using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

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

    public ThemeResolver(FrameworkElement host, MarkdownTheme theme)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
    }

    public ElementStyle GetEffectiveStyle(string elementKey)
    {
        var defaults = GetDefault(elementKey);
        if (_theme.Overrides.TryGetValue(elementKey, out var ov))
        {
            return new ElementStyle
            {
                FontFamily = ov.FontFamily ?? defaults.FontFamily,
                FontSize = ov.FontSize > 0 ? ov.FontSize : defaults.FontSize,
                FontWeight = ov.FontWeight.Weight > 0 ? ov.FontWeight : defaults.FontWeight,
                // Known limitation: cannot explicitly override FontStyle back to Normal via theme overrides.
                FontStyle = ov.FontStyle != Windows.UI.Text.FontStyle.Normal ? ov.FontStyle : defaults.FontStyle,
                Foreground = ov.Foreground.A == 0 ? defaults.Foreground : ov.Foreground,
                Background = ov.Background ?? defaults.Background,
                AccentBar = ov.AccentBar ?? defaults.AccentBar,
                Underline = ov.Underline || defaults.Underline,
                Strikethrough = ov.Strikethrough || defaults.Strikethrough,
                Margin = ov.Margin,
                Padding = ov.Padding,
                LineHeightMultiplier = ov.LineHeightMultiplier > 0 ? ov.LineHeightMultiplier : defaults.LineHeightMultiplier
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
            MarkdownElementKeys.TableHeader, MarkdownElementKeys.TableCell,
            MarkdownElementKeys.AlertNote, MarkdownElementKeys.AlertTip,
            MarkdownElementKeys.AlertImportant, MarkdownElementKeys.AlertWarning,
            MarkdownElementKeys.AlertCaution,
        };
        var dict = new Dictionary<string, ElementStyle>(allKeys.Length);
        foreach (var k in allKeys)
            dict[k] = GetEffectiveStyle(k);
        return new ThemeSnapshot(dict);
    }

    private ElementStyle GetDefault(string key)
    {
        bool isDark = _host.ActualTheme == ElementTheme.Dark;
        var fg = ResolveBrush("TextFillColorPrimaryBrush",
            isDark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Colors.Black);
        var fgSecondary = ResolveBrush("TextFillColorSecondaryBrush",
            isDark ? Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xAA, 0, 0, 0));
        var accent = _theme.AccentColor ?? ResolveBrush("AccentTextFillColorPrimaryBrush",
            Color.FromArgb(0xFF, 0x00, 0x67, 0xC0));
        var codeBg = ResolveBrush("ControlFillColorSecondaryBrush",
            isDark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0, 0, 0));
        var quoteBar = ResolveBrush("ControlStrongFillColorDefaultBrush",
            isDark ? Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x60, 0, 0, 0));

        // Font family includes emoji fallback for all elements.
        const string font = "Segoe UI Variable, Segoe UI Emoji, Segoe UI Symbol";
        const string mono = "Cascadia Mono, Consolas, Courier New";

        // Win11 typography ramp. LineHeightMultiplier is stored for reference but
        // InlineContainerBox uses CanvasLineSpacingMode.Default (font-native metrics).
        // Inter-block spacing comes from Margin only.
        return key switch
        {
            MarkdownElementKeys.Heading1 => new ElementStyle { FontFamily = font, FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 16, 0, 8) },
            MarkdownElementKeys.Heading2 => new ElementStyle { FontFamily = font, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 14, 0, 6) },
            MarkdownElementKeys.Heading3 => new ElementStyle { FontFamily = font, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 12, 0, 4) },
            MarkdownElementKeys.Heading4 => new ElementStyle { FontFamily = font, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 10, 0, 4) },
            MarkdownElementKeys.Heading5 => new ElementStyle { FontFamily = font, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 8, 0, 2) },
            MarkdownElementKeys.Heading6 => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fgSecondary, Margin = new Thickness(0, 6, 0, 2) },
            // Body: bottom margin for inter-paragraph spacing, zero top so list items align.
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 0, 0, 8) },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = codeBg, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 8, 12, 8) },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = codeBg },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, AccentBar = quoteBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = accent, Underline = true },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = true, Foreground = fgSecondary },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = quoteBar, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Background = codeBg },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg },
            MarkdownElementKeys.AlertNote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertTip => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertImportant => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertWarning => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertCaution => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 0, 0, 4) }
        };
    }

    private Color ResolveBrush(string resourceKey, Color fallback)
    {
        try
        {
            // Respect the host element's actual theme (handles per-element RequestedTheme override).
            var themeKey = _host.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";

            // 1. Look in the correct App ThemeDictionary for the host's current theme.
            if (Application.Current?.Resources?.ThemeDictionaries != null &&
                Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var td) &&
                td is ResourceDictionary themeDict &&
                themeDict.TryGetValue(resourceKey, out var r1) && r1 is SolidColorBrush b1)
                return b1.Color;

            // 2. Host element's local resources.
            if (_host.Resources?.TryGetValue(resourceKey, out var r2) == true && r2 is SolidColorBrush b2)
                return b2.Color;

            // 3. Global application resources (may reflect system theme, not element theme).
            if (Application.Current?.Resources?.TryGetValue(resourceKey, out var r3) == true && r3 is SolidColorBrush b3)
                return b3.Color;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThemeResolver] ResolveBrush failed for '{resourceKey}': {ex.Message}"); }
        return fallback;
    }
}
