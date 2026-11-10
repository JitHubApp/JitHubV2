using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Theming.Internal.ResourceDictionaries;

/// <summary>
/// Which resource keys and theme dictionaries a theme snapshot reads from the
/// host element and its ancestors, as opposed to the application dictionary.
/// </summary>
internal static class ThemeResourceScope
{
    private static readonly string[] LightThemeDictionaryKeys = ["Light", "Default"];

    private static readonly string[] DarkThemeDictionaryKeys = ["Dark", "Default"];

    private static readonly string[] HighContrastLightThemeDictionaryKeys =
        ["HighContrast", "Light", "Default"];

    private static readonly string[] HighContrastDarkThemeDictionaryKeys =
        ["HighContrast", "Dark", "Default"];

    private static readonly string[] ScopedPlatformResourceKeys = new string[]
    {
        "AccentFillColorDefaultBrush",
        "AccentFillColorSelectedTextBackgroundBrush",
        "AccentTextFillColorPrimary",
        "AccentTextFillColorPrimaryBrush",
        "ApplicationPageBackgroundThemeBrush",
        "FocusVisualPrimaryBrush",
        "LayerFillColorDefaultBrush",
        "SolidBackgroundFillColorBaseBrush",
        "SystemColorHighlightColor",
        "SystemColorHighlightColorBrush",
        "SystemColorHighlightTextColor",
        "SystemColorHighlightTextColorBrush",
        "SystemControlFocusVisualPrimaryBrush",
        "SystemControlForegroundAccentBrush",
        "SystemControlHighlightAccentBrush",
        "TextControlForeground",
        "TextControlForegroundFocused",
        "TextControlPlaceholderForeground",
        "TextControlSelectionHighlightColor",
        "TextFillColorPrimary",
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondary",
        "TextFillColorSecondaryBrush",
        "TextOnAccentFillColorSelectedText",
        "TextOnAccentFillColorSelectedTextBrush",
    };

    internal static IReadOnlyList<string> GetThemeDictionaryKeys(
        bool isHighContrast,
        ElementTheme actualTheme)
        => (isHighContrast, actualTheme == ElementTheme.Dark) switch
        {
            (true, true) => HighContrastDarkThemeDictionaryKeys,
            (true, false) => HighContrastLightThemeDictionaryKeys,
            (false, true) => DarkThemeDictionaryKeys,
            _ => LightThemeDictionaryKeys,
        };

    internal static bool IsScopedPlatformResourceKey(string key)
        => Array.BinarySearch(ScopedPlatformResourceKeys, key, StringComparer.Ordinal) >= 0;

    internal static bool IsScopedResourceRequiredForSnapshot(
        string key,
        HashSet<string> styleRoles)
    {
        if (IsScopedPlatformResourceKey(key) || MarkdownResourceKeys.IsGlobalResourceKey(key))
            return true;

        if (!key.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
            return false;

        int propertySeparator = key.LastIndexOf('.');
        if (propertySeparator <= MarkdownResourceKeys.Prefix.Length)
            return false;

        ReadOnlySpan<char> roleName = key.AsSpan(
            MarkdownResourceKeys.Prefix.Length,
            propertySeparator - MarkdownResourceKeys.Prefix.Length);
        return styleRoles.GetAlternateLookup<ReadOnlySpan<char>>().Contains(roleName);
    }

    internal static bool IncludeScopedRendererResource(string key)
        => key.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal) ||
            IsScopedPlatformResourceKey(key);
}
