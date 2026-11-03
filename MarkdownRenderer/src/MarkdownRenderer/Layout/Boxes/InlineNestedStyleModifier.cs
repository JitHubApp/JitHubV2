using System;
using Windows.UI;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Composes the style of a nested inline role (link, emphasis, inline code,
/// scripts and similar) onto the style of the text that contains it.
/// </summary>
internal static class InlineNestedStyleModifier
{
    private const float MinimumRelativeInlineFontSize = ThemeSnapshot.MinimumRelativeScriptFontSize;

    internal static ElementStyle ApplyNestedStyleModifier(
        ElementStyle basis,
        ElementStyle modifier,
        string modifierKey,
        bool preserveCodeFontSize = false,
        bool deferRelativeInlineCodeFontSize = false)
    {
        Windows.UI.Text.FontWeight fontWeight = basis.FontWeight;
        Windows.UI.Text.FontStyle fontStyle = basis.FontStyle;
        float fontSize = basis.FontSize;
        bool underline = basis.Underline;
        bool strikethrough = basis.Strikethrough;
        Color? background = basis.Background;
        float cornerRadius = basis.CornerRadius;

        switch (modifierKey)
        {
            case MarkdownElementKeys.Link:
                return new ElementStyle
                {
                    FontFamily = basis.FontFamily,
                    FontSize = basis.FontSize,
                    FontWeight = basis.FontWeight,
                    FontStyle = basis.FontStyle,
                    Foreground = modifier.Foreground,
                    HoverForeground = modifier.HoverForeground,
                    FocusForeground = modifier.FocusForeground,
                    Background = basis.Background,
                    AccentBar = basis.AccentBar,
                    BorderBrush = basis.BorderBrush,
                    BorderThickness = basis.BorderThickness,
                    CornerRadius = basis.CornerRadius,
                    ListIndent = basis.ListIndent,
                    NestedListIndent = basis.NestedListIndent,
                    Underline = modifier.Underline,
                    Strikethrough = basis.Strikethrough,
                    Margin = basis.Margin,
                    Padding = basis.Padding,
                    LineHeightMultiplier = basis.LineHeightMultiplier,
                };
            case MarkdownElementKeys.Strong:
                fontWeight = modifier.FontWeight;
                break;
            case MarkdownElementKeys.Emphasis:
                fontStyle = modifier.FontStyle;
                break;
            case MarkdownElementKeys.Strikethrough:
                strikethrough |= modifier.Strikethrough;
                break;
            case MarkdownElementKeys.Subscript:
            case MarkdownElementKeys.Superscript:
                fontSize = modifier.FontSize;
                break;
            case MarkdownElementKeys.Inserted:
                underline |= modifier.Underline;
                break;
            case MarkdownElementKeys.Marked:
                background = modifier.Background;
                cornerRadius = modifier.CornerRadius;
                break;
            case MarkdownElementKeys.CodeInline:
                // Inline code contributes its configured typeface, nominal
                // size, and chip treatment while retaining surrounding text
                // weight/style/color (notably bold links and link hover color).
                // Relative code rules use the current containing style, while
                // legacy sheets keep the historical full heading size.
                return new ElementStyle
                {
                    FontFamily = modifier.FontFamily,
                    FontSize = !deferRelativeInlineCodeFontSize &&
                               modifier.RelativeFontSizeScale is float inlineCodeScale
                        ? ResolveRelativeInlineCodeFontSize(basis.FontSize, inlineCodeScale)
                        : preserveCodeFontSize ? basis.FontSize : modifier.FontSize,
                    FontWeight = basis.FontWeight,
                    FontStyle = basis.FontStyle,
                    Foreground = basis.Foreground,
                    HoverForeground = basis.HoverForeground,
                    FocusForeground = basis.FocusForeground,
                    Background = modifier.Background ?? basis.Background,
                    AccentBar = basis.AccentBar,
                    BorderBrush = basis.BorderBrush,
                    BorderThickness = basis.BorderThickness,
                    CornerRadius = modifier.Background is null
                        ? basis.CornerRadius
                        : modifier.CornerRadius,
                    ListIndent = basis.ListIndent,
                    NestedListIndent = basis.NestedListIndent,
                    Underline = basis.Underline || modifier.Underline,
                    Strikethrough = basis.Strikethrough || modifier.Strikethrough,
                    Margin = basis.Margin,
                    Padding = modifier.Padding,
                    LineHeightMultiplier = basis.LineHeightMultiplier,
                };
            case MarkdownElementKeys.Abbreviation:
                underline |= modifier.Underline;
                strikethrough |= modifier.Strikethrough;
                break;
        }

        return new ElementStyle
        {
            FontFamily = basis.FontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            FontStyle = fontStyle,
            Foreground = basis.Foreground,
            HoverForeground = basis.HoverForeground,
            FocusForeground = basis.FocusForeground,
            Background = background,
            AccentBar = basis.AccentBar,
            BorderBrush = basis.BorderBrush,
            BorderThickness = basis.BorderThickness,
            CornerRadius = cornerRadius,
            ListIndent = basis.ListIndent,
            NestedListIndent = basis.NestedListIndent,
            Underline = underline,
            Strikethrough = strikethrough,
            Margin = basis.Margin,
            Padding = basis.Padding,
            LineHeightMultiplier = basis.LineHeightMultiplier,
        };
    }

    internal static float ResolveRelativeInlineCodeFontSize(float containingFontSize, float scale)
        => Math.Max(MinimumRelativeInlineFontSize, containingFontSize * scale);
}
