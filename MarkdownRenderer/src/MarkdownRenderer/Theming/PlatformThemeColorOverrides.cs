using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>Platform colors found in dictionaries scoped to the host element; null keeps the built-in value.</summary>
internal readonly struct PlatformThemeColorOverrides
{
    public PlatformThemeColorOverrides(
        Color? PrimaryText = null,
        Color? SecondaryText = null,
        Color? AccentText = null,
        Color? Surface = null)
    {
        this.PrimaryText = PrimaryText;
        this.SecondaryText = SecondaryText;
        this.AccentText = AccentText;
        this.Surface = Surface;
    }

    public readonly Color? PrimaryText;

    public readonly Color? SecondaryText;

    public readonly Color? AccentText;

    public readonly Color? Surface;
}
