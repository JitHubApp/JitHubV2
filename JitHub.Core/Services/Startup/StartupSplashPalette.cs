using System;
using System.Globalization;
using JitHub.Core.Models.Theming;
using JitHub.Shared.Theming;

namespace JitHub.Core.Services.Startup;

/// <summary>Resolves the splash background from the user's theme and palette.</summary>
public static class StartupSplashPalette
{
    /// <summary>Applies the same precedence as the app: launch override, stored theme, then system.</summary>
    public static bool UsesDarkTheme(string? launchTheme, string? storedTheme, bool systemUsesDarkTheme)
    {
        string? theme = string.IsNullOrWhiteSpace(launchTheme) ? storedTheme : launchTheme;
        if (string.Equals(theme, ThemeConst.Dark, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.Equals(theme, ThemeConst.Light, StringComparison.OrdinalIgnoreCase) &&
            systemUsesDarkTheme;
    }

    /// <summary>Returns the palette's canvas as a Win32 COLORREF (0x00BBGGRR).</summary>
    public static uint ResolveCanvasColorRef(string? paletteId, bool useDarkTheme)
    {
        ThemePaletteDefinition palette = ThemePaletteCatalog.Find(paletteId);
        ReadOnlySpan<char> hex = (useDarkTheme ? palette.Dark : palette.Light).Canvas.AsSpan(1);
        uint red = byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        uint green = byte.Parse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        uint blue = byte.Parse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return red | (green << 8) | (blue << 16);
    }
}
