using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// The colors of a Windows contrast theme, as a renderer uses them. Each color
/// matches the system color resource of the same name, such as
/// <c>SystemColorWindowTextColor</c>.
/// </summary>
/// <remarks>
/// Assign an instance to <see cref="MarkdownTheme.ForcedContrastColors"/> to
/// render with these colors whatever the system setting.
/// </remarks>
public sealed class MarkdownContrastColors
{
    /// <summary>Initializes a new set of contrast colors.</summary>
    /// <param name="windowTextColor">The text color.</param>
    /// <param name="windowColor">The background color.</param>
    /// <param name="hotlightColor">The hyperlink color.</param>
    /// <param name="highlightColor">The highlight color, used behind selected text.</param>
    /// <param name="highlightTextColor">The text color on the highlight color.</param>
    public MarkdownContrastColors(
        Color windowTextColor,
        Color windowColor,
        Color hotlightColor,
        Color highlightColor,
        Color highlightTextColor)
    {
        WindowTextColor = windowTextColor;
        WindowColor = windowColor;
        HotlightColor = hotlightColor;
        HighlightColor = highlightColor;
        HighlightTextColor = highlightTextColor;
    }

    /// <summary>Gets the text color.</summary>
    public Color WindowTextColor { get; }

    /// <summary>Gets the background color.</summary>
    public Color WindowColor { get; }

    /// <summary>Gets the hyperlink color.</summary>
    public Color HotlightColor { get; }

    /// <summary>Gets the highlight color, used behind selected text.</summary>
    public Color HighlightColor { get; }

    /// <summary>Gets the text color on the highlight color.</summary>
    public Color HighlightTextColor { get; }
}
