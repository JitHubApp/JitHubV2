using System;
using Windows.UI;

namespace MarkdownRenderer.Theming;

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
