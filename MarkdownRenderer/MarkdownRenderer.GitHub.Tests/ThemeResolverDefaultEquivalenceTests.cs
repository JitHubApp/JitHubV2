using System.Reflection;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Theming;
using Windows.UI;
using Windows.UI.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ThemeResolverDefaultEquivalenceTests
{
    private static readonly string[] BuiltInRoles =
    [
        MarkdownElementKeys.Heading1, MarkdownElementKeys.Heading2,
        MarkdownElementKeys.Heading3, MarkdownElementKeys.Heading4,
        MarkdownElementKeys.Heading5, MarkdownElementKeys.Heading6,
        MarkdownElementKeys.Body, MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock, MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.CodeBlockLanguage, MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.CodeBlockLineNumber, MarkdownElementKeys.Quote,
        MarkdownElementKeys.Link, MarkdownElementKeys.Strong,
        MarkdownElementKeys.Emphasis, MarkdownElementKeys.Strikethrough,
        MarkdownElementKeys.Subscript, MarkdownElementKeys.Superscript,
        MarkdownElementKeys.Inserted, MarkdownElementKeys.Marked,
        MarkdownElementKeys.Abbreviation, MarkdownElementKeys.ListMarker,
        MarkdownElementKeys.ThematicBreak, MarkdownElementKeys.ImageCaption,
        MarkdownElementKeys.Figure, MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.Diagram, MarkdownElementKeys.Math,
        MarkdownElementKeys.DefinitionTerm, MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Table, MarkdownElementKeys.TableHeader,
        MarkdownElementKeys.TableCell, MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip, MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning, MarkdownElementKeys.AlertCaution,
    ];

    private static readonly PropertyInfo[] StyleProperties =
        typeof(ElementStyle).GetProperties(BindingFlags.Instance | BindingFlags.Public);
    private static readonly Windows.UI.Text.FontWeight SemiBold = new() { Weight = 600 };

    [Fact]
    public void BuiltInDefaultsMatchThePreConsolidationGoldenForEveryRoleAndTheme()
    {
        var light = new ThemeResolver.PlatformThemeColors(
            Color.FromArgb(0xFF, 0x12, 0x34, 0x56),
            Color.FromArgb(0xFF, 0x23, 0x45, 0x67),
            Color.FromArgb(0xFF, 0x34, 0x56, 0x78),
            Color.FromArgb(0xFF, 0x45, 0x67, 0x89));
        var dark = new ThemeResolver.PlatformThemeColors(
            Color.FromArgb(0xFF, 0x56, 0x34, 0x12),
            Color.FromArgb(0xFF, 0x67, 0x45, 0x23),
            Color.FromArgb(0xFF, 0x78, 0x56, 0x34),
            Color.FromArgb(0xFF, 0x89, 0x67, 0x45));
        var systemTheme = new ForcedMarkdownSystemThemeProvider
        {
            WindowTextColor = Color.FromArgb(0xFF, 0x10, 0x21, 0x32),
            WindowColor = Color.FromArgb(0xFF, 0x43, 0x54, 0x65),
            HotlightColor = Color.FromArgb(0xFF, 0x76, 0x87, 0x98),
            HighlightColor = Color.FromArgb(0xFF, 0xA9, 0xBA, 0xCB),
            HighlightTextColor = Color.FromArgb(0xFF, 0xDC, 0xED, 0xFE),
        };
        Color customAccent = Color.FromArgb(0xFF, 0x0A, 0xBC, 0xDE);

        foreach (bool isHighContrast in new[] { false, true })
        foreach (bool isDark in new[] { false, true })
        foreach (string role in BuiltInRoles.Append("ExtensionRole"))
        {
            ThemeResolver.PlatformThemeColors platform = isDark ? dark : light;
            ElementStyle expected = LegacyDefault(
                role, isDark, isHighContrast, customAccent, platform, systemTheme);
            ElementStyle actual = ThemeResolver.CreateDefaultStyle(
                role,
                isDark,
                isHighContrast,
                customAccent,
                platform,
                SemiBold,
                isHighContrast ? systemTheme : null);

            AssertEveryStyleProperty(expected, actual, $"{role}, dark={isDark}, HC={isHighContrast}");
        }
    }

    private static ElementStyle LegacyDefault(
        string key,
        bool isDark,
        bool isHighContrast,
        Color? userAccent,
        ThemeResolver.PlatformThemeColors platformColors,
        IMarkdownSystemThemeProvider systemTheme)
    {
        if (isHighContrast)
        {
            MarkdownHighContrastStyleRoles roles = MarkdownHighContrastDefaults.Resolve(key);
            Color fg = LegacyHighContrastColor(roles.Foreground, systemTheme);
            Color? bg = roles.Background is { } backgroundRole
                ? LegacyHighContrastColor(backgroundRole, systemTheme)
                : null;
            Color? accentBar = roles.AccentBar is { } accentRole
                ? LegacyHighContrastColor(accentRole, systemTheme)
                : null;
            const string font = "Segoe UI Variable Text";
            const string mono = "Consolas";

            return key switch
            {
                MarkdownElementKeys.Heading1 => new ElementStyle { FontFamily = font, FontSize = 32, FontWeight = SemiBold, Foreground = fg, Margin = new Thickness(0, 16, 0, 8) },
                MarkdownElementKeys.Heading2 => new ElementStyle { FontFamily = font, FontSize = 26, FontWeight = SemiBold, Foreground = fg, Margin = new Thickness(0, 14, 0, 6) },
                MarkdownElementKeys.Heading3 => new ElementStyle { FontFamily = font, FontSize = 22, FontWeight = SemiBold, Foreground = fg, Margin = new Thickness(0, 12, 0, 4) },
                MarkdownElementKeys.Heading4 => new ElementStyle { FontFamily = font, FontSize = 18, FontWeight = SemiBold, Foreground = fg, Margin = new Thickness(0, 10, 0, 4) },
                MarkdownElementKeys.Heading5 => new ElementStyle { FontFamily = font, FontSize = 15, FontWeight = SemiBold, Foreground = fg, Margin = new Thickness(0, 8, 0, 2) },
                MarkdownElementKeys.Heading6 => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = SemiBold, Foreground = fg, Margin = new Thickness(0, 6, 0, 2) },
                MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 0, 0, 8) },
                MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = bg, AccentBar = accentBar, BorderBrush = accentBar, BorderThickness = accentBar is null ? 0 : 1, CornerRadius = 0, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 10, 12, 10) },
                MarkdownElementKeys.CodeBlockHeader => new ElementStyle { FontFamily = font, FontSize = 12, Foreground = fg, Background = bg, BorderBrush = accentBar },
                MarkdownElementKeys.CodeBlockLanguage => new ElementStyle { FontFamily = font, FontSize = 12, FontWeight = SemiBold, Foreground = fg },
                MarkdownElementKeys.CodeBlockGutter => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = bg, BorderBrush = accentBar },
                MarkdownElementKeys.CodeBlockLineNumber => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg },
                MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = bg, Padding = new Thickness(2, 0, 2, 0) },
                MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
                MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, HoverForeground = fg, FocusForeground = fg, Underline = roles.Underline },
                MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = SemiBold, Foreground = fg },
                MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
                MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = roles.Strikethrough, Foreground = fg },
                MarkdownElementKeys.Subscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
                MarkdownElementKeys.Superscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
                MarkdownElementKeys.Inserted => new ElementStyle { FontFamily = font, FontSize = 14, Underline = roles.Underline, Foreground = fg },
                MarkdownElementKeys.Marked => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg },
                MarkdownElementKeys.Abbreviation => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Underline = roles.Underline },
                MarkdownElementKeys.DefinitionTerm => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = SemiBold, Foreground = fg, Margin = new Thickness(0, 4, 0, 0) },
                MarkdownElementKeys.DefinitionDescription => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(18, 0, 0, 6) },
                MarkdownElementKeys.Figure => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 8, 0, 10) },
                MarkdownElementKeys.FigureCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fg, Background = bg, Margin = new Thickness(0, 2, 0, 8) },
                MarkdownElementKeys.Diagram => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = bg, AccentBar = accentBar, BorderBrush = accentBar, BorderThickness = accentBar is null ? 0 : 1, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 8) },
                MarkdownElementKeys.Math => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(1, 0, 1, 0) },
                MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, ListIndent = 22f, NestedListIndent = 0f },
                MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = fg, Margin = new Thickness(0, 12, 0, 12) },
                MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fg, Margin = new Thickness(0, 2, 0, 8) },
                MarkdownElementKeys.Table => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, BorderBrush = accentBar, BorderThickness = accentBar is null ? 0 : 1, Margin = new Thickness(0, 8, 0, 12) },
                MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = SemiBold, Foreground = fg, Background = bg, BorderBrush = accentBar, Padding = new Thickness(12, 9, 12, 9) },
                MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, BorderBrush = accentBar, Padding = new Thickness(12, 9, 12, 9) },
                MarkdownElementKeys.AlertNote or MarkdownElementKeys.AlertTip or MarkdownElementKeys.AlertImportant or MarkdownElementKeys.AlertWarning or MarkdownElementKeys.AlertCaution => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
                _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 0, 0, 4) },
            };
        }

        Color foreground = platformColors.PrimaryText;
        Color secondary = platformColors.SecondaryText;
        Color accent = userAccent ?? platformColors.AccentText;
        Color linkHover = LegacyAdjustColor(accent, isDark ? 0.18f : -0.12f);
        Color codeBg = isDark ? Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E) : Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA);
        Color codeHeaderBg = isDark ? Color.FromArgb(0xFF, 0x25, 0x25, 0x26) : Color.FromArgb(0xFF, 0xF0, 0xF2, 0xF5);
        Color codeBorder = isDark ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C) : Color.FromArgb(0xFF, 0xD0, 0xD7, 0xDE);
        Color codeMuted = isDark ? Color.FromArgb(0xFF, 0x85, 0x85, 0x85) : Color.FromArgb(0xFF, 0x6E, 0x77, 0x81);
        Color tableBg = isDark ? Color.FromArgb(0xFF, 0x25, 0x25, 0x25) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color tableHeaderBg = isDark ? Color.FromArgb(0xFF, 0x2D, 0x2D, 0x30) : Color.FromArgb(0xFF, 0xF7, 0xF8, 0xFA);
        Color tableBorder = isDark ? Color.FromArgb(0xFF, 0x3D, 0x3D, 0x40) : Color.FromArgb(0xFF, 0xD8, 0xDC, 0xE0);
        Color quoteBar = isDark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x50, 0x00, 0x00, 0x00);
        const string normalFont = "Segoe UI Variable Text";
        const string monoFont = "Consolas";

        return key switch
        {
            MarkdownElementKeys.Heading1 => new ElementStyle { FontFamily = normalFont, FontSize = 32, FontWeight = SemiBold, Foreground = foreground, Margin = new Thickness(0, 16, 0, 8) },
            MarkdownElementKeys.Heading2 => new ElementStyle { FontFamily = normalFont, FontSize = 26, FontWeight = SemiBold, Foreground = foreground, Margin = new Thickness(0, 14, 0, 6) },
            MarkdownElementKeys.Heading3 => new ElementStyle { FontFamily = normalFont, FontSize = 22, FontWeight = SemiBold, Foreground = foreground, Margin = new Thickness(0, 12, 0, 4) },
            MarkdownElementKeys.Heading4 => new ElementStyle { FontFamily = normalFont, FontSize = 18, FontWeight = SemiBold, Foreground = foreground, Margin = new Thickness(0, 10, 0, 4) },
            MarkdownElementKeys.Heading5 => new ElementStyle { FontFamily = normalFont, FontSize = 15, FontWeight = SemiBold, Foreground = foreground, Margin = new Thickness(0, 8, 0, 2) },
            MarkdownElementKeys.Heading6 => new ElementStyle { FontFamily = normalFont, FontSize = 14, FontWeight = SemiBold, Foreground = secondary, Margin = new Thickness(0, 6, 0, 2) },
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, Margin = new Thickness(0, 0, 0, 8), ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = monoFont, FontSize = 13, Foreground = foreground, Background = codeBg, BorderBrush = codeBorder, BorderThickness = 1, CornerRadius = 6, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 10, 12, 10) },
            MarkdownElementKeys.CodeBlockHeader => new ElementStyle { FontFamily = normalFont, FontSize = 12, Foreground = codeMuted, Background = codeHeaderBg, BorderBrush = codeBorder },
            MarkdownElementKeys.CodeBlockLanguage => new ElementStyle { FontFamily = normalFont, FontSize = 12, FontWeight = SemiBold, Foreground = codeMuted },
            MarkdownElementKeys.CodeBlockGutter => new ElementStyle { FontFamily = monoFont, FontSize = 12, Foreground = codeMuted, Background = codeHeaderBg },
            MarkdownElementKeys.CodeBlockLineNumber => new ElementStyle { FontFamily = monoFont, FontSize = 12, Foreground = codeMuted },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = monoFont, FontSize = 12, Foreground = foreground, Background = codeBg, CornerRadius = 3, Padding = new Thickness(2, 0, 2, 0) },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = secondary, AccentBar = quoteBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = accent, HoverForeground = linkHover, FocusForeground = linkHover, Underline = true },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = normalFont, FontSize = 14, FontWeight = SemiBold, Foreground = foreground },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = normalFont, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = foreground },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = normalFont, FontSize = 14, Strikethrough = true, Foreground = secondary },
            MarkdownElementKeys.Subscript => new ElementStyle { FontFamily = normalFont, FontSize = 11, Foreground = foreground },
            MarkdownElementKeys.Superscript => new ElementStyle { FontFamily = normalFont, FontSize = 11, Foreground = foreground },
            MarkdownElementKeys.Inserted => new ElementStyle { FontFamily = normalFont, FontSize = 14, Underline = true, Foreground = foreground },
            MarkdownElementKeys.Marked => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, Background = isDark ? Color.FromArgb(0x45, 0xFF, 0xD8, 0x66) : Color.FromArgb(0x66, 0xFF, 0xE5, 0x8A), CornerRadius = 3 },
            MarkdownElementKeys.Abbreviation => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, AccentBar = accent, Underline = true },
            MarkdownElementKeys.DefinitionTerm => new ElementStyle { FontFamily = normalFont, FontSize = 14, FontWeight = SemiBold, Foreground = foreground, Margin = new Thickness(0, 4, 0, 0) },
            MarkdownElementKeys.DefinitionDescription => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = secondary, Margin = new Thickness(18, 0, 0, 6) },
            MarkdownElementKeys.Figure => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, Margin = new Thickness(0, 8, 0, 10) },
            MarkdownElementKeys.FigureCaption => new ElementStyle { FontFamily = normalFont, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = secondary, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Diagram => new ElementStyle { FontFamily = monoFont, FontSize = 13, Foreground = foreground, Background = codeBg, BorderBrush = quoteBar, BorderThickness = 1, CornerRadius = 4, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 8) },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = secondary, ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = normalFont, Foreground = quoteBar, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = normalFont, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = secondary, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Table => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, Background = tableBg, BorderBrush = tableBorder, BorderThickness = 1, CornerRadius = 6, Margin = new Thickness(0, 8, 0, 12) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = normalFont, FontSize = 14, FontWeight = SemiBold, Foreground = foreground, Background = tableHeaderBg, BorderBrush = tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, Background = tableBg, BorderBrush = tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.AlertNote => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, AccentBar = Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertTip => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, AccentBar = Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertImportant => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, AccentBar = Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertWarning => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, AccentBar = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertCaution => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, AccentBar = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            _ => new ElementStyle { FontFamily = normalFont, FontSize = 14, Foreground = foreground, Margin = new Thickness(0, 0, 0, 4) },
        };
    }

    private static Color LegacyHighContrastColor(
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

    private static Color LegacyAdjustColor(Color color, float amount)
    {
        static byte Adjust(byte channel, float adjustment)
        {
            float value = adjustment >= 0
                ? channel + (255 - channel) * adjustment
                : channel * (1 + adjustment);
            return (byte)System.Math.Clamp((int)System.Math.Round(value), 0, 255);
        }

        return Color.FromArgb(
            color.A,
            Adjust(color.R, amount),
            Adjust(color.G, amount),
            Adjust(color.B, amount));
    }

    private static void AssertEveryStyleProperty(ElementStyle expected, ElementStyle actual, string context)
    {
        foreach (PropertyInfo property in StyleProperties)
        {
            Assert.True(
                Equals(property.GetValue(expected), property.GetValue(actual)),
                $"{context}: property {property.Name} expected '{property.GetValue(expected)}', " +
                $"actual '{property.GetValue(actual)}'.");
        }
    }

}
