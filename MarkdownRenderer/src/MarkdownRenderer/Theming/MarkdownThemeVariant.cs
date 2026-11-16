namespace MarkdownRenderer.Theming;

/// <summary>
/// A coarse color theme: light, dark or contrast.
/// </summary>
/// <remarks>
/// <see cref="Controls.MarkdownRendererControl.RenderedThemeVariant"/> reports
/// the theme a document was rendered for, and
/// <see cref="CodeBlocks.CodeBlockHighlightRequest.ThemeVariant"/> tells a
/// syntax highlighter which colors fit the code it highlights.
/// </remarks>
public enum MarkdownThemeVariant
{
    /// <summary>Light color theme.</summary>
    Light,

    /// <summary>Dark color theme.</summary>
    Dark,

    /// <summary>
    /// A Windows contrast theme, or colors forced with
    /// <see cref="MarkdownTheme.ForcedContrastColors"/>.
    /// </summary>
    HighContrast,
}
