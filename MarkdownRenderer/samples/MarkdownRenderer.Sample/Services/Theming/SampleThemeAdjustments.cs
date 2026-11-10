using System;
using System.Collections.Generic;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Sample.Services.Theming;

/// <summary>
/// The theme changes the sample's display commands apply to a renderer's
/// <see cref="MarkdownTheme"/>.
/// </summary>
internal static class SampleThemeAdjustments
{
    /// <summary>
    /// Forces high-contrast colors with a custom accent, which checks that the
    /// contrast colors win over the accent.
    /// </summary>
    internal static void ApplyForcedContrast(MarkdownTheme theme)
    {
        using (theme.BeginUpdate())
        {
            theme.ForcedContrastColors = new MarkdownContrastColors(
                windowTextColor: Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                windowColor: Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
                hotlightColor: Windows.UI.Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF),
                highlightColor: Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00),
                highlightTextColor: Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
            theme.AccentColor = Windows.UI.Color.FromArgb(0xFF, 0x80, 0x00, 0x80);
        }
    }

    internal static void ClearForcedContrast(MarkdownTheme theme)
    {
        using (theme.BeginUpdate())
        {
            theme.ForcedContrastColors = null;
            theme.AccentColor = null;
        }
    }

    /// <summary>
    /// Scales the font size of every element by <paramref name="scale"/>. Only
    /// font sizes change; margins and line heights keep the theme defaults.
    /// </summary>
    internal static void ApplyTextScale(MarkdownTheme theme, float scale)
    {
        var sizes = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Heading1] = 32,
            [MarkdownElementKeys.Heading2] = 26,
            [MarkdownElementKeys.Heading3] = 22,
            [MarkdownElementKeys.Heading4] = 18,
            [MarkdownElementKeys.Heading5] = 15,
            [MarkdownElementKeys.Heading6] = 14,
            [MarkdownElementKeys.Body] = 14,
            [MarkdownElementKeys.CodeInline] = 12,
            [MarkdownElementKeys.CodeBlock] = 13,
            [MarkdownElementKeys.CodeBlockLanguage] = 12,
            [MarkdownElementKeys.CodeBlockLineNumber] = 12,
            [MarkdownElementKeys.Link] = 14,
            [MarkdownElementKeys.Strong] = 14,
            [MarkdownElementKeys.Emphasis] = 14,
            [MarkdownElementKeys.Strikethrough] = 14,
            [MarkdownElementKeys.ListMarker] = 14,
            [MarkdownElementKeys.TableHeader] = 14,
            [MarkdownElementKeys.TableCell] = 14,
            [MarkdownElementKeys.ImageCaption] = 12,
            [MarkdownElementKeys.Diagram] = 13,
            [MarkdownElementKeys.Math] = 14,
        };

        using (theme.BeginUpdate())
        {
            foreach (var (key, baseSize) in sizes)
                theme.Overrides[key] = new ElementStyleOverride { FontSize = baseSize * scale };
        }
    }
}
