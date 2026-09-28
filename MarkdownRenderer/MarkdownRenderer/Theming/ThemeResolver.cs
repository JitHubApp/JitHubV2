using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Resolves <see cref="ElementStyle"/> values for the current theme &amp; FrameworkElement,
/// merging Win11 defaults with theme overrides. Exposes <see cref="GetEffectiveStyle"/>
/// which is the only call sites needed to consume.
/// </summary>
internal sealed class ThemeResolver
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

    private static readonly RelevantResourceKeyCache<ResourceDictionary> RelevantResourceKeys = new(
        static resources => resources.Keys.Count,
        EnumerateStringKeys,
        IncludeScopedRendererResource);

    private static readonly string[] BuiltInElementKeys =
    [
        MarkdownElementKeys.Heading1, MarkdownElementKeys.Heading2,
        MarkdownElementKeys.Heading3, MarkdownElementKeys.Heading4,
        MarkdownElementKeys.Heading5, MarkdownElementKeys.Heading6,
        MarkdownElementKeys.Body, MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock, MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.CodeBlockLanguage, MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.CodeBlockLineNumber, MarkdownElementKeys.Quote,
        MarkdownElementKeys.Link, MarkdownElementKeys.Strong,
        MarkdownElementKeys.Emphasis, MarkdownElementKeys.Strikethrough,
        MarkdownElementKeys.Subscript, MarkdownElementKeys.Superscript,
        MarkdownElementKeys.Inserted, MarkdownElementKeys.Marked,
        MarkdownElementKeys.Abbreviation,
        MarkdownElementKeys.ListMarker, MarkdownElementKeys.ThematicBreak,
        MarkdownElementKeys.ImageCaption, MarkdownElementKeys.Figure,
        MarkdownElementKeys.FigureCaption, MarkdownElementKeys.Diagram,
        MarkdownElementKeys.Math,
        MarkdownElementKeys.DefinitionTerm, MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Table, MarkdownElementKeys.TableHeader, MarkdownElementKeys.TableCell,
        MarkdownElementKeys.AlertNote, MarkdownElementKeys.AlertTip,
        MarkdownElementKeys.AlertImportant, MarkdownElementKeys.AlertWarning,
        MarkdownElementKeys.AlertCaution,
    ];
    private readonly FrameworkElement _host;
    private readonly MarkdownTheme _theme;
    private readonly ResourceDictionary? _applicationResources;
    private readonly IMarkdownSystemThemeProvider _systemTheme;
    private readonly bool _isHighContrast;
    private PlatformThemeColors? _platformThemeColors;
    private Dictionary<string, object>? _resolvedResourceValues;
    private HashSet<string>? _missingResourceKeys;
    private HashSet<string>? _scopedResourceStyleRoles;
    // Scoped dictionaries are small and explicit. Capture their relevant
    // values once per synchronous snapshot; never enumerate the app dictionary.
    private Dictionary<string, object>? _scopedResourceValues;

    internal static IMarkdownSystemThemeProvider? SystemThemeProviderOverride { get; set; }

    public ThemeResolver(FrameworkElement host, MarkdownTheme theme)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        // Application.Current and its Resources projection are stable for the
        // lifetime of one resolver. Capture the root once instead of crossing
        // the WinUI projection for every role/property lookup in the snapshot.
        _applicationResources = Application.Current?.Resources;
        _systemTheme = SystemThemeProviderOverride ?? new MarkdownSystemThemeProvider(_host);
        _isHighContrast = _systemTheme.IsHighContrast;
    }

    public ElementStyle GetEffectiveStyle(string elementKey)
    {
        var defaults = GetDefault(elementKey);
        var style = _theme.Overrides.TryGetValue(elementKey, out var ov)
            ? ThemeSnapshot.ApplyOverride(defaults, ov)
            : defaults;
        return _isHighContrast
            ? ThemeSnapshot.EnforceHighContrast(style, defaults)
            : style;
    }

    /// <summary>
    /// Captures all known element styles into an immutable <see cref="ThemeSnapshot"/>
    /// that can safely be read from background threads.
    /// Must be called on the UI thread.
    /// </summary>
    public ThemeSnapshot CreateSnapshot(
        MarkdownStyleSheet? styleSheet = null,
        double textScaleFactor = 1.0,
        IReadOnlyCollection<string>? additionalElementKeys = null,
        ulong? builtInStyleRoleDemandMask = null)
    {
        // ResourceDictionary.Keys is a WinRT projection. Enumerating an app-level
        // dictionary also projects the very large XamlControlsResources graph and
        // can block the UI thread for seconds. Capture only scoped dictionaries;
        // keep app-level resolution to memoized point lookups for finite keys.
        _resolvedResourceValues = new Dictionary<string, object>(StringComparer.Ordinal);
        _missingResourceKeys = new HashSet<string>(StringComparer.Ordinal);
        _scopedResourceValues = null;
        var overrides = _theme.GetOverridesSnapshot();
        var allKeys = new HashSet<string>(BuiltInElementKeys, StringComparer.Ordinal);
        foreach (string key in overrides.Keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                allKeys.Add(key);
        }

        if (styleSheet is not null)
        {
            foreach (MarkdownStyleRule rule in styleSheet.Rules)
                allKeys.Add(rule.Selector.Role.Name);
        }

        if (additionalElementKeys is not null)
        {
            foreach (string elementKey in additionalElementKeys)
            {
                if (!string.IsNullOrWhiteSpace(elementKey))
                    allKeys.Add(elementKey);
            }
        }

        var dict = new Dictionary<string, ElementStyle>(allKeys.Count, StringComparer.Ordinal);
        HashSet<string> scopedResourceStyleRoles = GetScopedResourceStyleRoles(allKeys, builtInStyleRoleDemandMask);
        _scopedResourceStyleRoles = scopedResourceStyleRoles;
        foreach (string k in allKeys)
        {
            // Canonical roles in this set are exactly the ones whose defaults
            // may resolve role-scoped resources. Invalid legacy aliases never
            // project resource roles, so skipping that no-op lookup is safe.
            dict[k] = GetDefault(k, scopedResourceStyleRoles.Contains(k));
        }

        Color surfaceColor = ResolveDocumentSurfaceColor();
        bool isDark = _host.ActualTheme == ElementTheme.Dark;
        Color selectionHighlightColor = ResolveSelectionHighlightColor();
        Color selectionForegroundColor = ResolveSelectionForegroundColor();
        Color focusVisualColor = ResolveFocusVisualColor();
        float minimumInteractiveSize = ResolveFloatResource(MarkdownResourceKeys.MinimumInteractiveSize) ?? 40f;
        Thickness documentPadding = ResolveThicknessResource(MarkdownResourceKeys.DocumentPadding) ?? default;
        float blockSpacing = ResolveFloatResource(MarkdownResourceKeys.BlockSpacing) ?? 0;
        Color? overflowIndicatorColor = ResolveColorResource(MarkdownResourceKeys.OverflowIndicatorBrush);

        // Resource capture is complete before construction; clearing the
        // snapshot-only filter here also keeps a reused resolver safe if the
        // immutable snapshot constructor throws.
        _scopedResourceStyleRoles = null;
        _scopedResourceValues = null;
        return new ThemeSnapshot(
            dict,
            overrides,
            surfaceColor,
            selectionHighlightColor,
            selectionForegroundColor,
            focusVisualColor,
            isDark,
            _isHighContrast,
            textScaleFactor,
            styleSheet: styleSheet,
            minimumInteractiveSize: minimumInteractiveSize,
            documentPadding: documentPadding,
            blockSpacing: blockSpacing,
            overflowIndicatorColor: overflowIndicatorColor);
    }

    private ElementStyle GetDefault(string key, bool resolveAppResourceOverrides = true)
    {
        bool isDark = !_isHighContrast && _host.ActualTheme == ElementTheme.Dark;
        PlatformThemeColors platformColors = _isHighContrast ? default : GetPlatformThemeColors();
        var mandatory = CreateDefaultStyle(
            key,
            isDark,
            _isHighContrast,
            _theme.AccentColor,
            platformColors,
            FontWeights.SemiBold,
            _isHighContrast ? _systemTheme : null);
        var style = resolveAppResourceOverrides ? ApplyAppResourceOverrides(key, mandatory) : mandatory;
        return _isHighContrast ? ThemeSnapshot.EnforceHighContrast(style, mandatory) : style;
    }

    internal static ElementStyle CreateDefaultStyle(
        string key,
        bool isDark,
        bool isHighContrast,
        Color? accentColor,
        PlatformThemeColors platformColors,
        Windows.UI.Text.FontWeight semiBoldFontWeight,
        IMarkdownSystemThemeProvider? systemTheme)
    {
        MarkdownHighContrastStyleRoles roles = isHighContrast
            ? MarkdownHighContrastDefaults.Resolve(key)
            : default;
        Color highContrastForeground = isHighContrast
            ? ResolveHighContrastRole(roles.Foreground, systemTheme!)
            : default;
        Color? highContrastBackground = isHighContrast && roles.Background is { } backgroundRole
            ? ResolveHighContrastRole(backgroundRole, systemTheme!)
            : null;
        Color? highContrastAccent = isHighContrast && roles.AccentBar is { } accentRole
            ? ResolveHighContrastRole(accentRole, systemTheme!)
            : null;

        // Hardcoded Win11 design token equivalents — bypasses the XAML resource system
        // which only works reliably for the app-level theme, not per-element themes.
        var fg = isHighContrast ? highContrastForeground : platformColors.PrimaryText;
        var fgSecondary = isHighContrast ? highContrastForeground : platformColors.SecondaryText;
        // Accent: try to get the user's accent color, fall back to Win11 blue.
        var accent      = isHighContrast ? highContrastForeground : accentColor ?? platformColors.AccentText;
        var linkHover   = isHighContrast ? highContrastForeground : AdjustColor(accent, isDark ? 0.18f : -0.12f);
        var codeBg      = isDark ? Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E) : Color.FromArgb(0xFF, 0xF6, 0xF8, 0xFA);
        var codeHeaderBg = isDark ? Color.FromArgb(0xFF, 0x25, 0x25, 0x26) : Color.FromArgb(0xFF, 0xF0, 0xF2, 0xF5);
        var codeBorder = isDark ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C) : Color.FromArgb(0xFF, 0xD0, 0xD7, 0xDE);
        var codeMuted = isDark ? Color.FromArgb(0xFF, 0x85, 0x85, 0x85) : Color.FromArgb(0xFF, 0x6E, 0x77, 0x81);
        var tableBg = isDark ? Color.FromArgb(0xFF, 0x25, 0x25, 0x25) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var tableHeaderBg = isDark ? Color.FromArgb(0xFF, 0x2D, 0x2D, 0x30) : Color.FromArgb(0xFF, 0xF7, 0xF8, 0xFA);
        var tableBorder = isDark ? Color.FromArgb(0xFF, 0x3D, 0x3D, 0x40) : Color.FromArgb(0xFF, 0xD8, 0xDC, 0xE0);
        var quoteBar    = isDark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x50, 0x00, 0x00, 0x00);

        // Single font family names for Win2D/DirectWrite. DirectWrite does NOT support
        // CSS-style comma-seuarated font stacks; the system font fallback maps emojn
        // code-points to Segoe UI Emoji automatically on Windows 10/11.
        const string font = "Segoe UI Variable Text";
        const string mono = "Consolas";

        return key switch
        {
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 => CreateHeadingStyle(key, font, fg, fgSecondary, semiBoldFontWeight),
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = new Thickness(0, 0, 0, 8), ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = isHighContrast ? highContrastBackground : codeBg, AccentBar = highContrastAccent, BorderBrush = isHighContrast ? highContrastAccent : codeBorder, BorderThickness = isHighContrast ? highContrastAccent is null ? 0 : 1 : 1, CornerRadius = isHighContrast ? 0 : 6, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 10, 12, 10) },
            MarkdownElementKeys.CodeBlockHeader or MarkdownElementKeys.CodeBlockGutter => new ElementStyle
            {
                FontFamily = key == MarkdownElementKeys.CodeBlockGutter ? mono : font,
                FontSize = 12,
                Foreground = isHighContrast ? fg : codeMuted,
                Background = isHighContrast ? highContrastBackground : codeHeaderBg,
                BorderBrush = key == MarkdownElementKeys.CodeBlockHeader && !isHighContrast ? codeBorder : highContrastAccent,
            },
            MarkdownElementKeys.CodeBlockLanguage => new ElementStyle { FontFamily = font, FontSize = 12, FontWeight = semiBoldFontWeight, Foreground = isHighContrast ? fg : codeMuted },
            MarkdownElementKeys.CodeBlockLineNumber => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = isHighContrast ? fg : codeMuted },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = isHighContrast ? highContrastBackground : codeBg, CornerRadius = isHighContrast ? 0 : 3, Padding = new Thickness(2, 0, 2, 0) },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, AccentBar = isHighContrast ? highContrastAccent : quoteBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = isHighContrast ? fg : accent, HoverForeground = linkHover, FocusForeground = linkHover, Underline = isHighContrast ? roles.Underline : true },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = semiBoldFontWeight, Foreground = fg },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = isHighContrast ? roles.Strikethrough : true, Foreground = fgSecondary },
            MarkdownElementKeys.Subscript or MarkdownElementKeys.Superscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
            MarkdownElementKeys.Inserted => new ElementStyle { FontFamily = font, FontSize = 14, Underline = isHighContrast ? roles.Underline : true, Foreground = fg },
            MarkdownElementKeys.Marked => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = isHighContrast ? highContrastBackground : isDark ? Color.FromArgb(0x45, 0xFF, 0xD8, 0x66) : Color.FromArgb(0x66, 0xFF, 0xE5, 0x8A), CornerRadius = isHighContrast ? 0 : 3 },
            MarkdownElementKeys.Abbreviation => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = isHighContrast ? highContrastAccent : accent, Underline = isHighContrast ? roles.Underline : true },
            MarkdownElementKeys.DefinitionTerm => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = semiBoldFontWeight, Foreground = fg, Margin = new Thickness(0, 4, 0, 0) },
            MarkdownElementKeys.DefinitionDescription => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, Background = highContrastBackground, Margin = new Thickness(18, 0, 0, 6) },
            MarkdownElementKeys.Figure => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = new Thickness(0, 8, 0, 10) },
            MarkdownElementKeys.FigureCaption or MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fgSecondary, Background = key == MarkdownElementKeys.FigureCaption ? highContrastBackground : null, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Diagram => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = isHighContrast ? highContrastBackground : codeBg, AccentBar = highContrastAccent, BorderBrush = isHighContrast ? highContrastAccent : quoteBar, BorderThickness = isHighContrast ? highContrastAccent is null ? 0 : 1 : 1, CornerRadius = isHighContrast ? 0 : 4, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 8) },
            MarkdownElementKeys.Math => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = isHighContrast ? new Thickness(1, 0, 1, 0) : new Thickness(0, 0, 0, 4) },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = isHighContrast ? fg : quoteBar, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.Table => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = isHighContrast ? highContrastBackground : tableBg, BorderBrush = isHighContrast ? highContrastAccent : tableBorder, BorderThickness = isHighContrast ? highContrastAccent is null ? 0 : 1 : 1, CornerRadius = isHighContrast ? 0 : 6, Margin = new Thickness(0, 8, 0, 12) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = semiBoldFontWeight, Foreground = fg, Background = isHighContrast ? highContrastBackground : tableHeaderBg, BorderBrush = isHighContrast ? highContrastAccent : tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = isHighContrast ? highContrastBackground : tableBg, BorderBrush = isHighContrast ? highContrastAccent : tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.AlertNote or MarkdownElementKeys.AlertTip or MarkdownElementKeys.AlertImportant or MarkdownElementKeys.AlertWarning or MarkdownElementKeys.AlertCaution => new ElementStyle
            {
                FontFamily = font,
                FontSize = 14,
                Foreground = fg,
                AccentBar = isHighContrast ? highContrastAccent : key switch
                {
                    MarkdownElementKeys.AlertNote => Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9),
                    MarkdownElementKeys.AlertTip => Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E),
                    MarkdownElementKeys.AlertImportant => Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7),
                    MarkdownElementKeys.AlertWarning => Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B),
                    _ => Color.FromArgb(0xFF, 0xEF, 0x44, 0x44),
                },
                Padding = new Thickness(12, 2, 8, 2),
                Margin = new Thickness(0, 4, 0, 8),
            },
            _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = highContrastBackground, Margin = new Thickness(0, 0, 0, 4) }
        };
    }

    private static ElementStyle CreateHeadingStyle(
        string key,
        string font,
        Color foreground,
        Color secondaryForeground,
        Windows.UI.Text.FontWeight semiBoldFontWeight)
    {
        var (fontSize, marginTop, marginBottom) = key switch
        {
            MarkdownElementKeys.Heading1 => (32f, 16d, 8d),
            MarkdownElementKeys.Heading2 => (26f, 14d, 6d),
            MarkdownElementKeys.Heading3 => (22f, 12d, 4d),
            MarkdownElementKeys.Heading4 => (18f, 10d, 4d),
            MarkdownElementKeys.Heading5 => (15f, 8d, 2d),
            _ => (14f, 6d, 2d),
        };
        return new ElementStyle
        {
            FontFamily = font,
            FontSize = fontSize,
            FontWeight = semiBoldFontWeight,
            Foreground = key == MarkdownElementKeys.Heading6 ? secondaryForeground : foreground,
            Margin = new Thickness(0, marginTop, 0, marginBottom),
        };
    }

    private ElementStyle ApplyAppResourceOverrides(string elementKey, ElementStyle style)
    {
        // MarkdownTheme.Overrides predates typed resource roles and supports
        // arbitrary extension/context/attribute aliases. Only canonical role
        // identifiers participate in MarkdownRenderer.<role>.<property>
        // resource lookup; an alias that cannot be projected still receives its
        // legacy theme override later in ThemeSnapshot.
        if (!MarkdownStyleRole.TryCreateCanonical(elementKey, out MarkdownStyleRole role))
            return style;

        Color? foreground = ResolveRoleColor(role, MarkdownStyleProperty.ForegroundBrush);
        Color? hoverForeground = ResolveRoleColor(role, MarkdownStyleProperty.HoverForegroundBrush);
        Color? focusForeground = ResolveRoleColor(role, MarkdownStyleProperty.FocusForegroundBrush);
        Color? background = ResolveRoleColor(role, MarkdownStyleProperty.BackgroundBrush);
        Color? accent = ResolveRoleColor(role, MarkdownStyleProperty.AccentBrush);
        Color? border = ResolveRoleColor(role, MarkdownStyleProperty.BorderBrush);
        string? fontFamily = ResolveRoleFontFamily(role);
        float? fontSize = ResolveRoleFloat(role, MarkdownStyleProperty.FontSize);
        Windows.UI.Text.FontWeight? fontWeight = ResolveRoleFontWeight(role);
        FontStyle? fontStyle = ResolveRoleFontStyle(role);
        Thickness? margin = ResolveRoleThickness(role, MarkdownStyleProperty.Margin);
        Thickness? padding = ResolveRoleThickness(role, MarkdownStyleProperty.Padding);
        float? borderThickness = ResolveRoleFloat(role, MarkdownStyleProperty.BorderThickness);
        float? cornerRadius = ResolveRoleFloat(role, MarkdownStyleProperty.CornerRadius);
        float? lineHeight = ResolveRoleFloat(role, MarkdownStyleProperty.LineHeightMultiplier);
        float? listIndent = ResolveRoleFloat(role, MarkdownStyleProperty.ListIndent);
        float? nestedListIndent = ResolveRoleFloat(role, MarkdownStyleProperty.NestedListIndent);
        TextDecorations? textDecorations = ResolveRoleTextDecorations(role);

        // Convenience aliases remain stable even if role names evolve.
        if (elementKey == MarkdownElementKeys.Body)
        {
            foreground ??= ResolveColorResource(MarkdownResourceKeys.BodyForegroundBrush);
            fontFamily ??= ResolveFontFamilyResource(MarkdownResourceKeys.BodyFontFamily);
            fontSize ??= ResolveFloatResource(MarkdownResourceKeys.BodyFontSize);
        }
        else if (elementKey == MarkdownElementKeys.Link)
        {
            foreground ??= ResolveColorResource(MarkdownResourceKeys.LinkForegroundBrush);
            hoverForeground ??= ResolveColorResource(MarkdownResourceKeys.LinkHoverForegroundBrush);
        }
        else if (elementKey is MarkdownElementKeys.CodeBlock or MarkdownElementKeys.CodeInline)
        {
            fontFamily ??= ResolveFontFamilyResource(MarkdownResourceKeys.CodeFontFamily);
            if (elementKey == MarkdownElementKeys.CodeBlock)
                background ??= ResolveColorResource(MarkdownResourceKeys.CodeBlockBackgroundBrush);
        }
        else if (elementKey == MarkdownElementKeys.Quote)
        {
            accent ??= ResolveColorResource(MarkdownResourceKeys.QuoteAccentBrush);
        }
        else if (elementKey is MarkdownElementKeys.Table or MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell)
        {
            border ??= ResolveColorResource(MarkdownResourceKeys.TableBorderBrush);
            if (elementKey is MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell)
                padding ??= ResolveThicknessResource(MarkdownResourceKeys.TableCellPadding);
        }

        return ThemeSnapshot.ApplyOverride(style, new ElementStyleOverride
        {
            Foreground = foreground,
            HoverForeground = hoverForeground,
            FocusForeground = focusForeground,
            Background = background,
            AccentBar = accent,
            BorderBrush = border,
            FontFamily = fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            FontStyle = fontStyle,
            Margin = margin,
            Padding = padding,
            BorderThickness = borderThickness,
            CornerRadius = cornerRadius,
            LineHeightMultiplier = lineHeight,
            ListIndent = listIndent,
            NestedListIndent = nestedListIndent,
            Underline = textDecorations is { } decorations
                ? (decorations & TextDecorations.Underline) != 0
                : null,
            Strikethrough = textDecorations is { } resolvedDecorations
                ? (resolvedDecorations & TextDecorations.Strikethrough) != 0
                : null,
        });
    }

    private Color? ResolveRoleColor(MarkdownStyleRole role, MarkdownStyleProperty property)
        => ResolveColorResource(MarkdownResourceKeys.ForRole(role, property));

    private string? ResolveRoleFontFamily(MarkdownStyleRole role)
        => ResolveFontFamilyResource(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontFamily));

    private float? ResolveRoleFloat(MarkdownStyleRole role, MarkdownStyleProperty property)
        => ResolveFloatResource(MarkdownResourceKeys.ForRole(role, property));

    private Windows.UI.Text.FontWeight? ResolveRoleFontWeight(MarkdownStyleRole role)
        => TryResolveResourceValue(
                MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontWeight),
                out var value) && value is Windows.UI.Text.FontWeight fontWeight
            ? fontWeight
            : null;

    private FontStyle? ResolveRoleFontStyle(MarkdownStyleRole role)
        => TryResolveResourceValue(
                MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontStyle),
                out var value) && value is FontStyle fontStyle
            ? fontStyle
            : null;

    private TextDecorations? ResolveRoleTextDecorations(MarkdownStyleRole role)
        => TryResolveResourceValue(
                MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.TextDecorations),
                out object value) &&
            TryExtractTextDecorations(value, out TextDecorations decorations)
                ? decorations
                : null;

    internal static bool TryExtractTextDecorations(object? value, out TextDecorations decorations)
    {
        if (value is TextDecorations typed)
        {
            decorations = typed;
        }
        else if (value is string text &&
                 Enum.TryParse(text.Trim(), ignoreCase: true, out TextDecorations parsed))
        {
            decorations = parsed;
        }
        else
        {
            decorations = TextDecorations.None;
            return false;
        }

        const TextDecorations supported = TextDecorations.Underline | TextDecorations.Strikethrough;
        if ((decorations & ~supported) == 0)
            return true;

        decorations = TextDecorations.None;
        return false;
    }

    private Thickness? ResolveRoleThickness(MarkdownStyleRole role, MarkdownStyleProperty property)
        => ResolveThicknessResource(MarkdownResourceKeys.ForRole(role, property));

    private Color? ResolveColorResource(string key)
        => TryResolveResourceColor(key, out var color) ? color : null;

    private string? ResolveFontFamilyResource(string key)
    {
        if (!TryResolveResourceValue(key, out var value))
            return null;

        return value switch
        {
            FontFamily family => family.Source,
            string name when !string.IsNullOrWhiteSpace(name) => name.Trim(),
            _ => null,
        };
    }

    private float? ResolveFloatResource(string key)
    {
        if (!TryResolveResourceValue(key, out var value))
            return null;

        double number = value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            _ => double.NaN,
        };
        return double.IsFinite(number) && number >= 0 && number <= float.MaxValue
            ? (float)number
            : null;
    }

    private Thickness? ResolveThicknessResource(string key)
        => TryResolveResourceValue(key, out var value) && value is Thickness thickness
            ? thickness
            : null;

    private static Color ResolveHighContrastRole(
        MarkdownHighContrastColorRole role,
        IMarkdownSystemThemeProvider systemTheme) => role switch
    {
        MarkdownHighContrastColorRole.WindowText => systemTheme.WindowTextColor,
        MarkdownHighContrastColorRole.Window => systemTheme.WindowColor,
        MarkdownHighContrastColorRole.Hotlight => systemTheme.HotlightColor,
        MarkdownHighContrastColorRole.Highlight => systemTheme.HighlightColor,
        MarkdownHighContrastColorRole.HighlightText => systemTheme.HighlightTextColor,
        _ => systemTheme.WindowTextColor,
    };

    private Color ResolveSelectionHighlightColor()
    {
        var nativeSelection = ResolveNativeSelectionHighlightColor();
        if (_isHighContrast)
            return WithAlpha(nativeSelection, 0xFF);

        if (ResolveColorResource(MarkdownResourceKeys.SelectionBackgroundBrush) is { } configuredSelection)
        {
            return configuredSelection.A == byte.MaxValue
                ? configuredSelection
                : CompositeOver(configuredSelection, ResolveDocumentSurfaceColor());
        }

        // TextBox paints the selection highlight over a clean text-control
        // surface after replacing the selected glyph foreground. Our canvas has
        // already painted the unselected glyphs underneath, so use the same
        // native resource but pre-composite translucent brushes over the
        // markdown surface. The resulting opaque color matches the native
        // visual while preventing old dark glyphs from bleeding through.
        return CompositeOver(nativeSelection, ResolveDocumentSurfaceColor());
    }

    private Color ResolveNativeSelectionHighlightColor()
    {
        if (TryResolvePlatformResourceColor("TextControlSelectionHighlightColor", out var color) ||
            TryResolvePlatformResourceColor("AccentFillColorSelectedTextBackgroundBrush", out color) ||
            TryResolvePlatformResourceColor("SystemControlHighlightAccentBrush", out color) ||
            TryResolvePlatformResourceColor("SystemColorHighlightColorBrush", out color) ||
            TryResolvePlatformResourceColor("SystemColorHighlightColor", out color))
        {
            return color;
        }

        return _isHighContrast
            ? _systemTheme.HighlightColor
            : ResolvePlatformBrush("AccentFillColorDefaultBrush",
                ResolvePlatformBrush("AccentTextFillColorPrimaryBrush", GetPlatformThemeColors().AccentText));
    }

    private Color ResolveSelectionForegroundColor()
    {
        if (_isHighContrast)
            return _systemTheme.HighlightTextColor;

        if (ResolveColorResource(MarkdownResourceKeys.SelectionForegroundBrush) is { } configuredForeground)
            return WithAlpha(configuredForeground, byte.MaxValue);

        if (TryResolvePlatformResourceColor("TextOnAccentFillColorSelectedTextBrush", out var color) ||
            TryResolvePlatformResourceColor("TextOnAccentFillColorSelectedText", out color) ||
            TryResolvePlatformResourceColor("SystemColorHighlightTextColorBrush", out color) ||
            TryResolvePlatformResourceColor("SystemColorHighlightTextColor", out color))
        {
            return Color.FromArgb(0xFF, color.R, color.G, color.B);
        }

        return Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private Color ResolveFocusVisualColor()
    {
        if (_isHighContrast)
            return _systemTheme.HighlightColor;

        if (ResolveColorResource(MarkdownResourceKeys.FocusVisualBrush) is { } configuredFocus)
            return configuredFocus;

        return ResolvePlatformBrush("SystemControlFocusVisualPrimaryBrush",
            ResolvePlatformBrush("FocusVisualPrimaryBrush",
                ResolvePlatformBrush("AccentTextFillColorPrimaryBrush", GetPlatformThemeColors().AccentText)));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color AdjustColor(Color color, float amount)
    {
        byte Adjust(byte channel)
        {
            float value = amount >= 0
                ? channel + (255 - channel) * amount
                : channel * (1 + amount);
            return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }

        return Color.FromArgb(color.A, Adjust(color.R), Adjust(color.G), Adjust(color.B));
    }

    private static Color CompositeOver(Color top, Color bottom)
    {
        double topA = top.A / 255.0;
        double bottomA = bottom.A / 255.0;
        double outA = topA + bottomA * (1.0 - topA);
        if (outA <= 0.0)
            return Color.FromArgb(0, 0, 0, 0);

        byte a = (byte)Math.Clamp((int)Math.Round(outA * 255.0), 0, 255);
        byte r = CompositeChannel(top.R, topA, bottom.R, bottomA, outA);
        byte g = CompositeChannel(top.G, topA, bottom.G, bottomA, outA);
        byte b = CompositeChannel(top.B, topA, bottom.B, bottomA, outA);
        return Color.FromArgb(a, r, g, b);
    }

    private static byte CompositeChannel(byte top, double topA, byte bottom, double bottomA, double outA)
    {
        var value = (top * topA + bottom * bottomA * (1.0 - topA)) / outA;
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private bool TryResolveResourceColor(string resourceKey, out Color color)
    {
        if (TryResolveResourceValue(resourceKey, out var value) &&
            TryExtractColor(value, out color))
            return true;

        color = default;
        return false;
    }

    /// <summary>
    /// Resolves a platform Fluent token without consulting Application.Resources.
    /// WinUI projects merged platform dictionaries through that scope using the
    /// app's effective palette, which is not reliably described by
    /// Application.RequestedTheme and can therefore disagree with a descendant's
    /// ActualTheme. Element and ancestor dictionaries are scoped to the host and
    /// remain valid customizations. Stable MarkdownRenderer.* resources use the
    /// unrestricted lookup path above and therefore retain app-level support.
    /// </summary>
    private bool TryResolvePlatformResourceColor(string resourceKey, out Color color)
    {
        if (TryResolveExplicitScopedResourceValue(resourceKey, out object value) &&
            TryExtractColor(value, out color))
        {
            return true;
        }

        color = default;
        return false;
    }

    private bool TryResolveResourceValue(string resourceKey, out object value)
    {
        if (_resolvedResourceValues is { } resolvedResources &&
            resourceKey.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
        {
            if (resolvedResources.TryGetValue(resourceKey, out value!))
                return true;
            if (_missingResourceKeys?.Contains(resourceKey) == true)
            {
                value = null!;
                return false;
            }
        }

        try
        {
            if (TryResolveScopedResourceValue(resourceKey, out value) ||
                TryResolveApplicationResourceValue(resourceKey, out value))
            {
                if (resourceKey.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
                    _resolvedResourceValues?[resourceKey] = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Resource lookup failed for '{resourceKey}': {ex.Message}");
        }

        value = null!;
        if (resourceKey.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
            _missingResourceKeys?.Add(resourceKey);
        return false;
    }

    private bool TryResolveScopedResourceValue(string resourceKey, out object value)
    {
        if (_scopedResourceValues is not { } scopedValues)
        {
            scopedValues = new Dictionary<string, object>(StringComparer.Ordinal);
            // An exceptional or reentrant lookup must not retry the graph walk
            // for every style property in this same snapshot.
            _scopedResourceValues = scopedValues;
            var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
            IReadOnlyList<string> themeKeys = GetThemeDictionaryKeys();
            Predicate<string> includeResource = _scopedResourceStyleRoles is not null
                ? IncludeScopedResourceForSnapshot
                : IncludeScopedRendererResource;
            for (DependencyObject? current = _host; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement element)
                {
                    ResourceDictionaryGraphResolver.CaptureResolvedValues(
                        element.Resources,
                        themeKeys,
                        visited,
                        static (dictionary, key) =>
                            dictionary.ThemeDictionaries.TryGetValue(key, out object? selected) &&
                            selected is ResourceDictionary selectedDictionary
                                ? selectedDictionary
                                : null,
                        EnumerateRelevantResourceKeys,
                        static (ResourceDictionary dictionary, string key, out object result) =>
                            dictionary.TryGetValue(key, out result),
                        static dictionary => dictionary.MergedDictionaries.Count,
                        static (dictionary, index) => dictionary.MergedDictionaries[index],
                        includeResource,
                        scopedValues);
                }
            }
        }

        return scopedValues.TryGetValue(resourceKey, out value!);
    }

    private bool TryResolveExplicitScopedResourceValue(string resourceKey, out object value)
    {
        if (_resolvedResourceValues is { } resolvedResources &&
            IsScopedPlatformResourceKey(resourceKey))
        {
            if (resolvedResources.TryGetValue(resourceKey, out value!))
                return true;
            if (_missingResourceKeys?.Contains(resourceKey) == true)
            {
                value = null!;
                return false;
            }
        }

        try
        {
            if (TryResolveScopedResourceValue(resourceKey, out value))
            {
                if (IsScopedPlatformResourceKey(resourceKey))
                    _resolvedResourceValues?[resourceKey] = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Explicit scoped resource lookup failed for '{resourceKey}': {ex.Message}");
        }

        value = null!;
        if (IsScopedPlatformResourceKey(resourceKey))
            _missingResourceKeys?.Add(resourceKey);
        return false;
    }

    private bool TryResolveApplicationResourceValue(string resourceKey, out object value)
    {
        if (_applicationResources is { } applicationResources)
        {
            // ResourceDictionary performs its own indexed lookup across merged
            // dictionaries and the active application theme. Avoid manually
            // walking and enumerating the app graph for every renderer snapshot.
            return applicationResources.TryGetValue(resourceKey, out value);
        }

        value = null!;
        return false;
    }

    private IReadOnlyList<string> GetThemeDictionaryKeys()
        => GetThemeDictionaryKeysForTesting(_isHighContrast, _host.ActualTheme);

    internal static IReadOnlyList<string> GetThemeDictionaryKeysForTesting(
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

    internal static HashSet<string> GetScopedResourceStyleRoles(
        IEnumerable<string> elementKeys,
        ulong? builtInStyleRoleDemandMask)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in elementKeys)
        {
            int roleDemandIndex = Array.IndexOf(BuiltInElementKeys, key);
            if ((builtInStyleRoleDemandMask is null || roleDemandIndex < 0 ||
                 (builtInStyleRoleDemandMask.Value & (1UL << roleDemandIndex)) != 0) &&
                MarkdownStyleRole.TryCreateCanonical(key, out MarkdownStyleRole role))
            {
                roles.Add(role.Name);
            }
        }

        return roles;
    }

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

    private bool IncludeScopedResourceForSnapshot(string key)
        => _scopedResourceStyleRoles is { } styleRoles &&
            IsScopedResourceRequiredForSnapshot(key, styleRoles);

    private static bool IncludeScopedRendererResource(string key)
        => key.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal) ||
            IsScopedPlatformResourceKey(key);

    internal static void InvalidateResourceKeyCache()
        => RelevantResourceKeys.Invalidate();

    private static IEnumerable<string> EnumerateRelevantResourceKeys(ResourceDictionary resources)
        => RelevantResourceKeys.GetRelevantKeys(resources);

    private static IEnumerable<string> EnumerateStringKeys(ResourceDictionary resources)
    {
        foreach (object key in resources.Keys)
        {
            if (key is string text)
                yield return text;
        }
    }

    private static bool TryExtractColor(object value, out Color color)
    {
        switch (value)
        {
            case Color c:
                color = c;
                return true;
            case SolidColorBrush brush:
                var alpha = (byte)Math.Clamp((int)Math.Round(brush.Color.A * brush.Opacity), 0, 255);
                color = Color.FromArgb(alpha, brush.Color.R, brush.Color.G, brush.Color.B);
                return true;
            default:
                color = default;
                return false;
        }
    }

    private Color ResolveSurfaceColor()
    {
        if (_isHighContrast)
            return _systemTheme.WindowColor;

        return GetPlatformThemeColors().Surface;
    }

    private Color ResolveDocumentSurfaceColor()
    {
        if (_isHighContrast)
            return ResolveSurfaceColor();

        return _theme.SurfaceColor ??
               ResolveColorResource(MarkdownResourceKeys.DocumentSurfaceBrush) ??
               ResolveSurfaceColor();
    }

    private PlatformThemeColors GetPlatformThemeColors()
        => _platformThemeColors ??= ResolvePlatformThemeColors();

    private PlatformThemeColors ResolvePlatformThemeColors()
    {
        PlatformThemeColorOverrides scoped = ResolveScopedPlatformThemeColorOverrides();
        return ResolvePlatformThemeColorsForTesting(_host.ActualTheme, scoped);
    }

    private PlatformThemeColorOverrides ResolveScopedPlatformThemeColorOverrides()
    {
        Color? Resolve(params string[] resourceKeys)
        {
            foreach (string resourceKey in resourceKeys)
            {
                if (TryResolveExplicitScopedResourceValue(resourceKey, out object value) &&
                    TryExtractColor(value, out Color color))
                {
                    return color;
                }
            }

            return null;
        }

        return new PlatformThemeColorOverrides(
            PrimaryText: Resolve(
                "TextControlForegroundFocused",
                "TextControlForeground",
                "TextFillColorPrimaryBrush",
                "TextFillColorPrimary"),
            SecondaryText: Resolve(
                "TextFillColorSecondaryBrush",
                "TextFillColorSecondary",
                "TextControlPlaceholderForeground"),
            AccentText: Resolve(
                "AccentTextFillColorPrimaryBrush",
                "AccentTextFillColorPrimary",
                "SystemControlForegroundAccentBrush",
                "AccentFillColorDefaultBrush"),
            Surface: Resolve(
                "ApplicationPageBackgroundThemeBrush",
                "SolidBackgroundFillColorBaseBrush",
                "LayerFillColorDefaultBrush"));
    }

    internal static PlatformThemeColors ResolvePlatformThemeColorsForTesting(
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
        static Color Resolve(Color? scopedColor, Color fallbackColor)
            => scopedColor ?? fallbackColor;

        Color surface = Resolve(scoped.Surface, fallback.Surface);
        surface = surface.A == byte.MaxValue ? surface : CompositeOver(surface, fallback.Surface);

        return new PlatformThemeColors(
            Resolve(scoped.PrimaryText, fallback.PrimaryText),
            Resolve(scoped.SecondaryText, fallback.SecondaryText),
            Resolve(scoped.AccentText, fallback.AccentText),
            surface);
    }

    private Color ResolvePlatformBrush(string resourceKey, Color fallback)
    {
        try
        {
            if (TryResolvePlatformResourceColor(resourceKey, out Color color))
                return color;
        }
        catch (Exception ex) { MarkdownDiagnostics.WriteLine($"[ThemeResolver] ResolvePlatformBrush failed for '{resourceKey}': {ex.Message}"); }
        return fallback;
    }

    internal readonly record struct PlatformThemeColorOverrides(
        Color? PrimaryText = null,
        Color? SecondaryText = null,
        Color? AccentText = null,
        Color? Surface = null);

    internal readonly record struct PlatformThemeColors(
        Color PrimaryText,
        Color SecondaryText,
        Color AccentText,
        Color Surface);

}
