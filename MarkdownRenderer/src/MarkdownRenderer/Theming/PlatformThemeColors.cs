using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// The Fluent text and surface colors a non-High-Contrast snapshot builds its
/// default styles from, after host-scoped overrides fill in the built-in palette.
/// </summary>
internal readonly struct PlatformThemeColors
{
    public PlatformThemeColors(
        Color PrimaryText,
        Color SecondaryText,
        Color AccentText,
        Color Surface)
    {
        this.PrimaryText = PrimaryText;
        this.SecondaryText = SecondaryText;
        this.AccentText = AccentText;
        this.Surface = Surface;
    }

    public readonly Color PrimaryText;

    public readonly Color SecondaryText;

    public readonly Color AccentText;

    public readonly Color Surface;

    internal static PlatformThemeColors Resolve(
        ElementTheme hostTheme,
        PlatformThemeColorOverrides scoped)
    {
        bool isDark = hostTheme == ElementTheme.Dark;
        PlatformThemeColors fallback = isDark
            ? new PlatformThemeColors(
                PrimaryText: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                SecondaryText: Color.FromArgb(0xFF, 0x9D, 0x9D, 0x9D),
                AccentText: Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF),
                Surface: Color.FromArgb(0xFF, 0x20, 0x20, 0x20))
            : new PlatformThemeColors(
                PrimaryText: Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
                SecondaryText: Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A),
                AccentText: Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8),
                Surface: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        static Color Choose(Color? scopedColor, Color fallbackColor)
            => scopedColor ?? fallbackColor;

        Color surface = Choose(scoped.Surface, fallback.Surface);
        surface = surface.A == byte.MaxValue ? surface : ThemeColorMath.CompositeOver(surface, fallback.Surface);

        return new PlatformThemeColors(
            Choose(scoped.PrimaryText, fallback.PrimaryText),
            Choose(scoped.SecondaryText, fallback.SecondaryText),
            Choose(scoped.AccentText, fallback.AccentText),
            surface);
    }
}
