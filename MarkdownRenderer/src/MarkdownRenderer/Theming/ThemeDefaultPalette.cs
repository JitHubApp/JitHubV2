using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// The colors one element's built-in style is drawn from, resolved for a
/// light, dark or High Contrast theme by <see cref="ThemeDefaultStyles"/>.
/// </summary>
internal readonly record struct ThemeDefaultPalette(
    MarkdownHighContrastStyleRoles Roles,
    Color? HighContrastBackground,
    Color? HighContrastAccent,
    Color Foreground,
    Color SecondaryForeground,
    Color Accent,
    Color LinkHover,
    Color CodeBackground,
    Color CodeHeaderBackground,
    Color CodeBorder,
    Color CodeMuted,
    Color TableBackground,
    Color TableHeaderBackground,
    Color TableBorder,
    Color QuoteBar);