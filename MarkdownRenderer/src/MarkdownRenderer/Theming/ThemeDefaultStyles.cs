using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.Text;

namespace MarkdownRenderer.Theming;

/// <summary>
/// The built-in Windows 11 style for each markdown element role, for light,
/// dark and High Contrast, before app resources and theme overrides apply.
/// </summary>
internal static class ThemeDefaultStyles
{
    internal static ElementStyle CreateDefaultStyle(
        string key,
        bool isDark,
        bool isHighContrast,
        Color? accentColor,
        PlatformThemeColors platformColors,
        Windows.UI.Text.FontWeight semiBoldFontWeight,
        IMarkdownSystemThemeProvider? systemTheme)
    {
        MarkdownHighContrastStyleRoles roles = isHighContrast
            ? MarkdownHighContrastDefaults.Resolve(key)
            : default;
        Color highContrastForeground = isHighContrast
            ? ResolveHighContrastRole(roles.Foreground, systemTheme!)
            : default;
        Color? highContrastBackground = isHighContrast && roles.Background is { } backgroundRole
            ? ResolveHighContrastRole(backgroundRole, systemTheme!)
            : null;
        Color? highContrastAccent = isHighContrast && roles.AccentBar is { } accentRole
            ? ResolveHighContrastRole(accentRole, systemTheme!)
            : null;

        // Hardcoded Win11 design token equivalents — bypasses the XAML resource system
        // which only works reliably for the app-level theme, not per-element themes.
        var fg = isHighContrast ? highContrastForeground : platformColors.PrimaryText;
        var fgSecondary = isHighContrast ? highContrastForeground : platformColors.SecondaryText;
        // Accent: try to get the user's accent color, fall back to Win11 blue.
        var accent      = isHighContrast ? highContrastForeground : accentColor ?? platformColors.AccentText;
        var linkHover   = isHighContrast ? highContrastForeground : ThemeColorMath.AdjustColor(accent, isDark ? 0.18f : -0.12f);
        var codeBg      = isDark ? Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E) : Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA);
        var codeHeaderBg = isDark ? Color.FromArgb(0xFF, 0x25, 0x25, 0x26) : Color.FromArgb(0xFF, 0xF0, 0xF2, 0xF5);
        var codeBorder = isDark ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C) : Color.FromArgb(0xFF, 0xD0, 0xD7, 0xDE);
        var codeMuted = isDark ? Color.FromArgb(0xFF, 0x85, 0x85, 0x85) : Color.FromArgb(0xFF, 0x6E, 0x77, 0x81);
        var tableBg = isDark ? Color.FromArgb(0xFF, 0x25, 0x25, 0x25) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var tableHeaderBg = isDark ? Color.FromArgb(0xFF, 0x2D, 0x2D, 0x30) : Color.FromArgb(0xFF, 0xF7, 0xF8, 0xFA);
        var tableBorder = isDark ? Color.FromArgb(0xFF, 0x3D, 0x3D, 0x40) : Color.FromArgb(0xFF, 0xD8, 0xDC, 0xE0);
        var quoteBar    = isDark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x50, 0x00, 0x00, 0x00);

        // Single font family names for Win2D/DirectWrite. DirectWrite does NOT support
        // CSS-style comma-separated font stacks; the system font fallback maps emoji
        // code-points to Segoe UI Emoji automatically on Windows 10/11.
        const string font = "Segoe UI Variable Text";
        const string mono = "Consolas";

        return key switch
        {
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 => CreateHeadingStyle(key, font, fg, fgSecondary, semiBoldFontWeight),
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = new Thickness(0, 0, 0, 8), ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = isHighContrast ? highContrastBackground : codeBg, AccentBar = highContrastAccent, BorderBrush = isHighContrast ? highContrastAccent : codeBorder, BorderThickness = isHighContrast ? highContrastAccent is null ? 0 : 1 : 1, CornerRadius = isHighContrast ? 0 : 6, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 10, 12, 10) },
            MarkdownElementKeys.CodeBlockHeader or MarkdownElementKeys.CodeBlockGutter => new ElementStyle
            {
                FontFamily = key == MarkdownElementKeys.CodeBlockGutter ? mono : font,
                FontSize = 12,
                Foreground = isHighContrast ? fg : codeMuted,
                Background = isHighContrast ? highContrastBackground : codeHeaderBg,
                BorderBrush = key == MarkdownElementKeys.CodeBlockHeader && !isHighContrast ? codeBorder : highContrastAccent,
            },
            MarkdownElementKeys.CodeBlockLanguage => new ElementStyle { FontFamily = font, FontSize = 12, FontWeight = semiBoldFontWeight, Foreground = isHighContrast ? fg : codeMuted },
            MarkdownElementKeys.CodeBlockLineNumber => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = isHighContrast ? fg : codeMuted },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = isHighContrast ? highContrastBackground : codeBg, CornerRadius = isHighContrast ? 0 : 3, Padding = new Thickness(2, 0, 2, 0) },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, AccentBar = isHighContrast ? highContrastAccent : quoteBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = isHighContrast ? fg : accent, HoverForeground = linkHover, FocusForeground = linkHover, Underline = isHighContrast ? roles.Underline : true },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = semiBoldFontWeight, Foreground = fg },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = isHighContrast ? roles.Strikethrough : true, Foreground = fgSecondary },
            MarkdownElementKeys.Subscript or MarkdownElementKeys.Superscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
            MarkdownElementKeys.Inserted => new ElementStyle { FontFamily = font, FontSize = 14, Underline = isHighContrast ? roles.Underline : true, Foreground = fg },
            MarkdownElementKeys.Marked => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = isHighContrast ? highContrastBackground : isDark ? Color.FromArgb(0x45, 0xFF, 0xD8, 0x66) : Color.FromArgb(0x66, 0xFF, 0xE5, 0x8A), CornerRadius = isHighContrast ? 0 : 3 },
            MarkdownElementKeys.Abbreviation => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = isHighContrast ? highContrastAccent : accent, Underline = isHighContrast ? roles.Underline : true },
            MarkdownElementKeys.DefinitionTerm => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = semiBoldFontWeight, Foreground = fg, Margin = new Thickness(0, 4, 0, 0) },
            MarkdownElementKeys.DefinitionDescription => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, Background = highContrastBackground, Margin = new Thickness(18, 0, 0, 6) },
            MarkdownElementKeys.Figure => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = new Thickness(0, 8, 0, 10) },
            MarkdownElementKeys.FigureCaption or MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fgSecondary, Background = key == MarkdownElementKeys.FigureCaption ? highContrastBackground : null, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Diagram => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = isHighContrast ? highContrastBackground : codeBg, AccentBar = highContrastAccent, BorderBrush = isHighContrast ? highContrastAccent : quoteBar, BorderThickness = isHighContrast ? highContrastAccent is null ? 0 : 1 : 1, CornerRadius = isHighContrast ? 0 : 4, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 8) },
            MarkdownElementKeys.Math => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = isHighContrast ? new Thickness(1, 0, 1, 0) : new Thickness(0, 0, 0, 4) },
            MarkdownElementKeys.List => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = isHighContrast ? fg : quoteBar, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.Table => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = isHighContrast ? highContrastBackground : tableBg, BorderBrush = isHighContrast ? highContrastAccent : tableBorder, BorderThickness = isHighContrast ? highContrastAccent is null ? 0 : 1 : 1, CornerRadius = isHighContrast ? 0 : 6, Margin = new Thickness(0, 8, 0, 12) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = semiBoldFontWeight, Foreground = fg, Background = isHighContrast ? highContrastBackground : tableHeaderBg, BorderBrush = isHighContrast ? highContrastAccent : tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = isHighContrast ? highContrastBackground : tableBg, BorderBrush = isHighContrast ? highContrastAccent : tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.AlertNote or MarkdownElementKeys.AlertTip or MarkdownElementKeys.AlertImportant or MarkdownElementKeys.AlertWarning or MarkdownElementKeys.AlertCaution => new ElementStyle
            {
                FontFamily = font,
                FontSize = 14,
                Foreground = fg,
                AccentBar = isHighContrast ? highContrastAccent : key switch
                {
                    MarkdownElementKeys.AlertNote => Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9),
                    MarkdownElementKeys.AlertTip => Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E),
                    MarkdownElementKeys.AlertImportant => Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7),
                    MarkdownElementKeys.AlertWarning => Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B),
                    _ => Color.FromArgb(0xFF, 0xEF, 0x44, 0x44),
                },
                Padding = new Thickness(12, 2, 8, 2),
                Margin = new Thickness(0, 4, 0, 8),
            },
            _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = new Thickness(0, 0, 0, 4) }
        };
    }

    private static ElementStyle CreateHeadingStyle(
        string key,
        string font,
        Color foreground,
        Color secondaryForeground,
        Windows.UI.Text.FontWeight semiBoldFontWeight)
    {
        var (fontSize, marginTop, marginBottom) = key switch
        {
            MarkdownElementKeys.Heading1 => (32f, 16d, 8d),
            MarkdownElementKeys.Heading2 => (26f, 14d, 6d),
            MarkdownElementKeys.Heading3 => (22f, 12d, 4d),
            MarkdownElementKeys.Heading4 => (18f, 10d, 4d),
            MarkdownElementKeys.Heading5 => (15f, 8d, 2d),
            _ => (14f, 6d, 2d),
        };
        return new ElementStyle
        {
            FontFamily = font,
            FontSize = fontSize,
            FontWeight = semiBoldFontWeight,
            Foreground = key == MarkdownElementKeys.Heading6 ? secondaryForeground : foreground,
            Margin = new Thickness(0, marginTop, 0, marginBottom),
        };
    }

    private static Color ResolveHighContrastRole(
        MarkdownHighContrastColorRole role,
        IMarkdownSystemThemeProvider systemTheme) => role switch
    {
        MarkdownHighContrastColorRole.WindowText => systemTheme.WindowTextColor,
        MarkdownHighContrastColorRole.Window => systemTheme.WindowColor,
        MarkdownHighContrastColorRole.Hotlight => systemTheme.HotlightColor,
        MarkdownHighContrastColorRole.Highlight => systemTheme.HighlightColor,
        MarkdownHighContrastColorRole.HighlightText => systemTheme.HighlightTextColor,
        _ => systemTheme.WindowTextColor,
    };
}
