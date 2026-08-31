using System;
using System.Collections.Generic;
using System.Globalization;
using JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JitHub.Services;

public sealed class ThemePaletteChangedEventArgs : EventArgs
{
    public ThemePaletteChangedEventArgs(string previousPaletteId, string paletteId)
    {
        PreviousPaletteId = previousPaletteId;
        PaletteId = paletteId;
    }

    public string PreviousPaletteId { get; }

    public string PaletteId { get; }
}

public static class ThemePaletteRuntime
{
    private static readonly string[] AppearanceKeys = ["Default", "Light", "Dark"];

    private static readonly string[] RequiredAppearanceKeys = ["Default", "Light", "Dark", "HighContrast"];

    private static readonly string[] SemanticColorTokenKeys =
    [
        "AppCanvasColor",
        "AppCanvasMaterialTintColor",
        "AppCanvasRaisedColor",
        "AppCanvasInsetColor",
        "AppRailColor",
        "AppRailMaterialTintColor",
        "AppSurfaceColor",
        "AppSurfaceSubtleColor",
        "AppCardColor",
        "AppInputColor",
        "AppInputHoverColor",
        "AppRowColor",
        "AppRowHoverColor",
        "AppRowPressedColor",
        "AppRowSelectedColor",
        "AppRowHoverForegroundColor",
        "AppRowPressedForegroundColor",
        "AppRowSelectedForegroundColor",
        "AppSelectionColor",
        "AppSelectionForegroundColor",
        "AppOverlayColor",
        "AppTransparentColor",
        "AppPopupSurfaceColor",
        "AppPopupBorderColor",
        "AppReactionChipBackgroundColor",
        "AppReactionChipHoverColor",
        "AppReactionChipPressedColor",
        "AppReactionChipBorderColor",
        "AppReactionChipForegroundColor",
        "AppReactionChipSelectedColor",
        "AppInkColor",
        "AppInkMutedColor",
        "AppInkSubtleColor",
        "AppInkInverseColor",
        "AppLabelDarkTextColor",
        "AppLabelLightTextColor",
        "AppOutlineColor",
        "AppOutlineStrongColor",
        "AppHairlineColor",
        "AppAccentColor",
        "AppAccentHoverColor",
        "AppAccentPressedColor",
        "AppAccentForegroundColor",
        "AppWarmAccentColor",
        "AppWarmAccentForegroundColor",
        "AppDangerColor",
        "AppDangerForegroundColor",
        "AppSuccessColor",
        "AppSuccessForegroundColor",
        "AppShadowColor",
        "AppSmokeColor"
    ];

    private static readonly (string BrushKey, string ColorTokenKey)[] SemanticBrushTokenMappings =
    [
        ("AppCanvasBrush", "AppCanvasColor"),
        ("AppWindowBackgroundBrush", "AppCanvasColor"),
        ("AppCanvasRaisedBrush", "AppCanvasRaisedColor"),
        ("AppCanvasInsetBrush", "AppCanvasInsetColor"),
        ("AppRailBrush", "AppRailColor"),
        ("AppSurfaceBrush", "AppSurfaceColor"),
        ("AppSurfaceSubtleBrush", "AppSurfaceSubtleColor"),
        ("AppCardBrush", "AppCardColor"),
        ("AppInputBrush", "AppInputColor"),
        ("AppInputHoverBrush", "AppInputHoverColor"),
        ("AppRowBrush", "AppRowColor"),
        ("AppRowHoverBrush", "AppRowHoverColor"),
        ("AppRowPressedBrush", "AppRowPressedColor"),
        ("AppRowSelectedBrush", "AppRowSelectedColor"),
        ("AppRowHoverForegroundBrush", "AppRowHoverForegroundColor"),
        ("AppRowPressedForegroundBrush", "AppRowPressedForegroundColor"),
        ("AppRowSelectedForegroundBrush", "AppRowSelectedForegroundColor"),
        ("AppSelectionBrush", "AppSelectionColor"),
        ("AppSelectionForegroundBrush", "AppSelectionForegroundColor"),
        ("AppOverlayBrush", "AppOverlayColor"),
        ("AppTransparentBrush", "AppTransparentColor"),
        ("AppPopupSurfaceBrush", "AppPopupSurfaceColor"),
        ("AppPopupBorderBrush", "AppPopupBorderColor"),
        ("AppReactionChipBackgroundBrush", "AppReactionChipBackgroundColor"),
        ("AppReactionChipHoverBrush", "AppReactionChipHoverColor"),
        ("AppReactionChipPressedBrush", "AppReactionChipPressedColor"),
        ("AppReactionChipBorderBrush", "AppReactionChipBorderColor"),
        ("AppReactionChipForegroundBrush", "AppReactionChipForegroundColor"),
        ("AppReactionChipSelectedBrush", "AppReactionChipSelectedColor"),
        ("AppInkBrush", "AppInkColor"),
        ("AppInkMutedBrush", "AppInkMutedColor"),
        ("AppInkSubtleBrush", "AppInkSubtleColor"),
        ("AppInkInverseBrush", "AppInkInverseColor"),
        ("AppLabelDarkTextBrush", "AppLabelDarkTextColor"),
        ("AppLabelLightTextBrush", "AppLabelLightTextColor"),
        ("AppOutlineBrush", "AppOutlineColor"),
        ("AppOutlineStrongBrush", "AppOutlineStrongColor"),
        ("AppHairlineBrush", "AppHairlineColor"),
        ("AppAccentBrush", "AppAccentColor"),
        ("AppAccentHoverBrush", "AppAccentHoverColor"),
        ("AppAccentPressedBrush", "AppAccentPressedColor"),
        ("AppAccentForegroundBrush", "AppAccentForegroundColor"),
        ("AppAccentSelectionMutedBrush", "AppAccentColor"),
        ("AppAccentForegroundSecondaryBrush", "AppAccentForegroundColor"),
        ("AppAccentLowBrush", "AppAccentPressedColor"),
        ("AppAccentMediumBrush", "AppAccentColor"),
        ("AppPlaceholderBrush", "AppRowHoverForegroundColor"),
        ("AppPlaceholderStrongBrush", "AppRowHoverForegroundColor"),
        ("AppPlaceholderSelectedStrongBrush", "AppRowSelectedForegroundColor"),
        ("AppWarmAccentBrush", "AppWarmAccentColor"),
        ("AppWarmAccentForegroundBrush", "AppWarmAccentForegroundColor"),
        ("AppDangerBrush", "AppDangerColor"),
        ("AppDangerForegroundBrush", "AppDangerForegroundColor"),
        ("AppSuccessBrush", "AppSuccessColor"),
        ("AppSuccessForegroundBrush", "AppSuccessForegroundColor"),
        ("AppShadowBrush", "AppShadowColor"),
        ("AppSmokeBrush", "AppSmokeColor")
    ];

    private static readonly (string BrushKey, string TintColorTokenKey, string FallbackColorTokenKey)[] AcrylicBrushTokenMappings =
    [
        ("AppTransientOverlayBrush", "AppOverlayColor", "AppOverlayColor"),
        ("AppCanvasTransientOverlayBrush", "AppCanvasMaterialTintColor", "AppCanvasColor"),
        ("AppTitleBarMaterialBrush", "AppCanvasMaterialTintColor", "AppCanvasRaisedColor"),
        ("AppRailTransientOverlayBrush", "AppRailMaterialTintColor", "AppRailColor")
    ];

    private static string _currentPaletteId = ThemePaletteIds.JitHub;

    public static event EventHandler<ThemePaletteChangedEventArgs>? PaletteChanged;

    public static string CurrentPaletteId => _currentPaletteId;

    public static void SetMaterialEffectsEnabled(
        ResourceDictionary applicationResources,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);

        foreach (var mapping in AcrylicBrushTokenMappings)
        {
            string brushKey = mapping.BrushKey;
            if (!applicationResources.ContainsKey(brushKey) ||
                applicationResources[brushKey] is not AcrylicBrush brush)
            {
                throw new InvalidOperationException(
                    $"The material brush contract is missing '{brushKey}'.");
            }

            brush.AlwaysUseFallback = !enabled;
        }
    }

    public static void RefreshActiveBrushes(
        ResourceDictionary applicationResources,
        ElementTheme activeTheme,
        bool isHighContrast)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);

        int paletteDictionaryIndex = FindPaletteDictionaryIndex(applicationResources);
        if (paletteDictionaryIndex < 0)
        {
            throw new InvalidOperationException("The application color-token dictionary is unavailable.");
        }

        string appearanceKey = isHighContrast
            ? "HighContrast"
            : activeTheme == ElementTheme.Dark ? "Dark" : "Light";
        ResourceDictionary palette = applicationResources.MergedDictionaries[paletteDictionaryIndex];
        ResourceDictionary appearance = GetAppearanceDictionary(palette, appearanceKey);

        foreach ((string brushKey, string colorTokenKey) in SemanticBrushTokenMappings)
        {
            if (!applicationResources.ContainsKey(brushKey) ||
                applicationResources[brushKey] is not SolidColorBrush brush ||
                appearance[colorTokenKey] is not Color color)
            {
                throw new InvalidOperationException(
                    $"The live theme brush contract is missing '{brushKey}' or '{colorTokenKey}'.");
            }

            brush.Color = color;
        }

        foreach ((string brushKey, string tintColorTokenKey, string fallbackColorTokenKey) in AcrylicBrushTokenMappings)
        {
            RefreshAcrylicBrush(
                applicationResources,
                appearance,
                brushKey,
                tintColorTokenKey,
                fallbackColorTokenKey);
        }
    }

    public static bool TryApply(
        ResourceDictionary applicationResources,
        string? paletteId,
        out Exception? error)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);

        ThemePaletteDefinition requested = ThemePaletteCatalog.Find(paletteId);
        int paletteDictionaryIndex = FindPaletteDictionaryIndex(applicationResources);
        if (paletteDictionaryIndex < 0)
        {
            error = new InvalidOperationException("The application color-token dictionary is unavailable.");
            return false;
        }

        ResourceDictionary currentDictionary = applicationResources.MergedDictionaries[paletteDictionaryIndex];
        if (string.Equals(_currentPaletteId, requested.Id, StringComparison.Ordinal))
        {
            _currentPaletteId = requested.Id;
            error = null;
            return true;
        }

        string previous = _currentPaletteId;
        try
        {
            ResourceDictionary replacement = CreatePaletteDictionary(requested);
            ValidatePaletteDictionary(replacement, requested.Id);

            object[][] previousValues = CaptureAppearanceValues(currentDictionary);
            try
            {
                ApplyAppearanceValues(currentDictionary, replacement);
            }
            catch
            {
                RestoreAppearanceValues(currentDictionary, previousValues);
                throw;
            }

            _currentPaletteId = requested.Id;
            error = null;
        }
        catch (Exception exception)
        {
            error = exception;
            return false;
        }

        RaisePaletteChanged(previous, requested.Id);
        return true;
    }

    private static int FindPaletteDictionaryIndex(ResourceDictionary resources)
    {
        for (int index = 0; index < resources.MergedDictionaries.Count; index++)
        {
            string? source = resources.MergedDictionaries[index].Source?.OriginalString;
            foreach (ThemePaletteDefinition palette in ThemePaletteCatalog.All)
            {
                if (palette.ResourceUri is not null &&
                    string.Equals(source, palette.ResourceUri, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static ResourceDictionary CreatePaletteDictionary(ThemePaletteDefinition palette)
    {
        if (palette.ResourceUri is not null)
        {
            return new ResourceDictionary
            {
                Source = new Uri(palette.ResourceUri, UriKind.Absolute)
            };
        }

        if (palette.GeneratedLight is null || palette.GeneratedDark is null)
        {
            throw new InvalidOperationException(
                $"Theme palette '{palette.Id}' has neither a packaged resource nor generated semantic colors.");
        }

        ResourceDictionary dictionary = new();
        dictionary.ThemeDictionaries["Default"] = CreateAppearanceDictionary(palette.GeneratedLight);
        dictionary.ThemeDictionaries["Light"] = CreateAppearanceDictionary(palette.GeneratedLight);
        dictionary.ThemeDictionaries["Dark"] = CreateAppearanceDictionary(palette.GeneratedDark);

        ResourceDictionary highContrast = new();
        highContrast.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "ms-appx:///Styles/Foundation/Palettes/Tokens.Colors.HighContrast.xaml",
                UriKind.Absolute)
        });
        dictionary.ThemeDictionaries["HighContrast"] = highContrast;
        return dictionary;
    }

    private static ResourceDictionary CreateAppearanceDictionary(ThemePaletteTokenSet tokenSet)
    {
        ResourceDictionary appearance = new();
        foreach ((string key, string value) in tokenSet.Colors)
        {
            appearance[key] = ParseHexColor(value);
        }

        return appearance;
    }

    private static Color ParseHexColor(string value)
    {
        ReadOnlySpan<char> hex = value.AsSpan();
        if (hex.Length > 0 && hex[0] == '#')
        {
            hex = hex[1..];
        }

        byte alpha;
        if (hex.Length == 8)
        {
            alpha = byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            hex = hex[2..];
        }
        else if (hex.Length == 6)
        {
            alpha = byte.MaxValue;
        }
        else
        {
            throw new FormatException($"Theme color '{value}' must be #RRGGBB or #AARRGGBB.");
        }

        byte red = byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte green = byte.Parse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte blue = byte.Parse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(alpha, red, green, blue);
    }

    private static void ValidatePaletteDictionary(ResourceDictionary dictionary, string paletteId)
    {
        foreach (string theme in RequiredAppearanceKeys)
        {
            if (!dictionary.ThemeDictionaries.ContainsKey(theme) ||
                dictionary.ThemeDictionaries[theme] is not ResourceDictionary themeResources)
            {
                throw new InvalidOperationException(
                    $"Theme palette '{paletteId}' does not define the required '{theme}' semantic colors.");
            }

            foreach (string tokenKey in SemanticColorTokenKeys)
            {
                if (!themeResources.ContainsKey(tokenKey))
                {
                    throw new InvalidOperationException(
                        $"Theme palette '{paletteId}' does not define '{tokenKey}' for '{theme}'.");
                }
            }
        }
    }

    private static object[][] CaptureAppearanceValues(ResourceDictionary dictionary)
    {
        object[][] values = new object[AppearanceKeys.Length][];
        for (int appearanceIndex = 0; appearanceIndex < AppearanceKeys.Length; appearanceIndex++)
        {
            ResourceDictionary appearance = GetAppearanceDictionary(dictionary, AppearanceKeys[appearanceIndex]);
            values[appearanceIndex] = new object[SemanticColorTokenKeys.Length];
            for (int tokenIndex = 0; tokenIndex < SemanticColorTokenKeys.Length; tokenIndex++)
            {
                values[appearanceIndex][tokenIndex] = appearance[SemanticColorTokenKeys[tokenIndex]];
            }
        }

        return values;
    }

    private static void ApplyAppearanceValues(ResourceDictionary destination, ResourceDictionary source)
    {
        for (int appearanceIndex = 0; appearanceIndex < AppearanceKeys.Length; appearanceIndex++)
        {
            string appearanceKey = AppearanceKeys[appearanceIndex];
            ResourceDictionary destinationAppearance = GetAppearanceDictionary(destination, appearanceKey);
            ResourceDictionary sourceAppearance = GetAppearanceDictionary(source, appearanceKey);
            foreach (string tokenKey in SemanticColorTokenKeys)
            {
                destinationAppearance[tokenKey] = sourceAppearance[tokenKey];
            }
        }
    }

    private static void RestoreAppearanceValues(ResourceDictionary destination, object[][] values)
    {
        for (int appearanceIndex = 0; appearanceIndex < AppearanceKeys.Length; appearanceIndex++)
        {
            ResourceDictionary appearance = GetAppearanceDictionary(destination, AppearanceKeys[appearanceIndex]);
            for (int tokenIndex = 0; tokenIndex < SemanticColorTokenKeys.Length; tokenIndex++)
            {
                appearance[SemanticColorTokenKeys[tokenIndex]] = values[appearanceIndex][tokenIndex];
            }
        }
    }

    private static ResourceDictionary GetAppearanceDictionary(ResourceDictionary dictionary, string appearanceKey)
    {
        if (dictionary.ThemeDictionaries[appearanceKey] is ResourceDictionary appearance)
        {
            return appearance;
        }

        throw new InvalidOperationException($"The '{appearanceKey}' color-token dictionary is unavailable.");
    }

    private static void RefreshAcrylicBrush(
        ResourceDictionary applicationResources,
        ResourceDictionary appearance,
        string brushKey,
        string tintColorTokenKey,
        string fallbackColorTokenKey)
    {
        if (!applicationResources.ContainsKey(brushKey) ||
            applicationResources[brushKey] is not AcrylicBrush brush ||
            appearance[tintColorTokenKey] is not Color tintColor ||
            appearance[fallbackColorTokenKey] is not Color fallbackColor)
        {
            throw new InvalidOperationException(
                $"The live acrylic brush contract is missing '{brushKey}', '{tintColorTokenKey}', or '{fallbackColorTokenKey}'.");
        }

        brush.TintColor = tintColor;
        brush.FallbackColor = fallbackColor;
    }

    private static void RaisePaletteChanged(string previousPaletteId, string paletteId)
    {
        EventHandler<ThemePaletteChangedEventArgs>? handlers = PaletteChanged;
        if (handlers is null)
        {
            return;
        }

        ThemePaletteChangedEventArgs args = new(previousPaletteId, paletteId);
        foreach (EventHandler<ThemePaletteChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(null, args);
            }
            catch (Exception exception)
            {
                JitHub.WinUI.App.LogHandledException(exception, "theme-palette-notification");
            }
        }
    }
}
