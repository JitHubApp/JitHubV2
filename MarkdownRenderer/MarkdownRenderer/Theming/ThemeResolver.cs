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
        var fg = ResolveBrush("TextFillColorPrimaryBrush", Colors.Black);
        var fgSecondary = ResolveBrush("TextFillColorSecondaryBrush", Color.FromArgb(0xCC, 0, 0, 0));
        var accent = _theme.AccentColor ?? ResolveBrush("AccentTextFillColorPrimaryBrush", Color.FromArgb(0xFF, 0x00, 0x67, 0xC0));
        var codeBg = ResolveBrush("ControlFillColorSecondaryBrush", Color.FromArgb(0x10, 0, 0, 0));
        var quoteBar = ResolveBrush("ControlStrongFillColorDefaultBrush", Color.FromArgb(0x40, 0, 0, 0));

        // Win11 typography ramp (approximate; actual styles come from
        // Microsoft.UI.Xaml resources but we hardcode metrics for the canvas
        // text path so we do not need to resolve XAML TextBlock styles).
        return key switch
        {
            MarkdownElementKeys.Heading1 => new ElementStyle { FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 12, 0, 8), LineHeightMultiplier = 1.25f },
            MarkdownElementKeys.Heading2 => new ElementStyle { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 10, 0, 6), LineHeightMultiplier = 1.3f },
            MarkdownElementKeys.Heading3 => new ElementStyle { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 8, 0, 4), LineHeightMultiplier = 1.3f },
            MarkdownElementKeys.Heading4 => new ElementStyle { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 6, 0, 4), LineHeightMultiplier = 1.35f },
            MarkdownElementKeys.Heading5 => new ElementStyle { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 4, 0, 2), LineHeightMultiplier = 1.4f },
            MarkdownElementKeys.Heading6 => new ElementStyle { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = fgSecondary, Margin = new Thickness(0, 4, 0, 2), LineHeightMultiplier = 1.4f },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = "Cascadia Mono, Consolas", FontSize = 13, Foreground = fg, Background = codeBg, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(12, 8, 12, 8), LineHeightMultiplier = 1.45f },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = "Cascadia Mono, Consolas", FontSize = 13, Foreground = fg, Background = codeBg },
            MarkdownElementKeys.Quote => new ElementStyle { FontSize = 14, Foreground = fgSecondary, AccentBar = quoteBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 4, 8, 4), LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.Link => new ElementStyle { FontSize = 14, Foreground = accent, Underline = true, LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.Strong => new ElementStyle { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg, LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontSize = 14, Strikethrough = true, Foreground = fgSecondary, LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontSize = 14, Foreground = fgSecondary, LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { Foreground = quoteBar, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Background = codeBg, LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.TableCell => new ElementStyle { FontSize = 14, Foreground = fg, LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.AlertNote => new ElementStyle { FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9), Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 4, 0, 4), LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.AlertTip => new ElementStyle { FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E), Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 4, 0, 4), LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.AlertImportant => new ElementStyle { FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7), Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 4, 0, 4), LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.AlertWarning => new ElementStyle { FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 4, 0, 4), LineHeightMultiplier = 1.5f },
            MarkdownElementKeys.AlertCaution => new ElementStyle { FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44), Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 4, 0, 4), LineHeightMultiplier = 1.5f },
            _ => new ElementStyle { FontSize = 14, Foreground = fg, Margin = new Thickness(0, 4, 0, 4), LineHeightMultiplier = 1.5f }
        };
    }

    private Color ResolveBrush(string resourceKey, Color fallback)
    {
        try
        {
            object? r = null;
            if (_host.Resources?.TryGetValue(resourceKey, out r) == true && r is SolidColorBrush b1)
                return b1.Color;
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(resourceKey, out r) && r is SolidColorBrush b2)
                return b2.Color;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThemeResolver] ResolveBrush failed for '{resourceKey}': {ex.Message}"); }
        return fallback;
    }
}
