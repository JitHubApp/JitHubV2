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
    private readonly IMarkdownSystemThemeProvider _systemTheme;
    private readonly bool _isHighContrast;
    private PlatformThemeColors? _platformThemeColors;
    private Dictionary<string, object>? _capturedMarkdownResources;

    internal static IMarkdownSystemThemeProvider? SystemThemeProviderOverride { get; set; }

    public ThemeResolver(FrameworkElement host, MarkdownTheme theme)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
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
        double textScaleFactor = 1.0)
    {
        _capturedMarkdownResources = CaptureMarkdownResources();
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

        if (_capturedMarkdownResources is { } capturedResources)
        {
            foreach (string resourceKey in capturedResources.Keys)
            {
                if (MarkdownResourceKeys.TryGetStyleRoleName(resourceKey, out string roleName))
                    allKeys.Add(roleName);
            }
        }
        else
        {
            TryCollectResourceRoleNames(allKeys);
        }

        var dict = new Dictionary<string, ElementStyle>(allKeys.Count, StringComparer.Ordinal);
        foreach (string k in allKeys)
            dict[k] = GetDefault(k);
        Color surfaceColor = ResolveDocumentSurfaceColor();
        bool isDark = _host.ActualTheme == ElementTheme.Dark;
        return new ThemeSnapshot(
            dict,
            overrides,
            surfaceColor,
            ResolveSelectionHighlightColor(),
            ResolveSelectionForegroundColor(),
            ResolveFocusVisualColor(),
            isDark,
            _isHighContrast,
            textScaleFactor,
            styleSheet: styleSheet,
            minimumInteractiveSize: ResolveFloatResource(MarkdownResourceKeys.MinimumInteractiveSize) ?? 40f,
            documentPadding: ResolveThicknessResource(MarkdownResourceKeys.DocumentPadding) ?? default,
            blockSpacing: ResolveFloatResource(MarkdownResourceKeys.BlockSpacing) ?? 0,
            overflowIndicatorColor: ResolveColorResource(MarkdownResourceKeys.OverflowIndicatorBrush));
    }

    private ElementStyle GetDefault(string key)
    {
        if (_isHighContrast)
        {
            var mandatory = GetHighContrastDefault(key);
            return ThemeSnapshot.EnforceHighContrast(
                ApplyAppResourceOverrides(key, mandatory),
                mandatory);
        }

        bool isDark = _host.ActualTheme == ElementTheme.Dark;

        // Hardcoded Win11 design token equivalents — bypasses the XAML resource system
        // which only works reliably for the app-level theme, not per-element themes.
        PlatformThemeColors platformColors = GetPlatformThemeColors();
        var fg = platformColors.PrimaryText;
        var fgSecondary = platformColors.SecondaryText;
        // Accent: try to get the user's accent color, fall back to Win11 blue.
        var accent      = _theme.AccentColor ?? platformColors.AccentText;
        var linkHover   = AdjustColor(accent, isDark ? 0.18f : -0.12f);
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

        var fluentStyle = key switch
        {
            MarkdownElementKeys.Heading1 => new ElementStyle { FontFamily = font, FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 16, 0, 8) },
            MarkdownElementKeys.Heading2 => new ElementStyle { FontFamily = font, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 14, 0, 6) },
            MarkdownElementKeys.Heading3 => new ElementStyle { FontFamily = font, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 12, 0, 4) },
            MarkdownElementKeys.Heading4 => new ElementStyle { FontFamily = font, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 10, 0, 4) },
            MarkdownElementKeys.Heading5 => new ElementStyle { FontFamily = font, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 8, 0, 2) },
            MarkdownElementKeys.Heading6 => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fgSecondary, Margin = new Thickness(0, 6, 0, 2) },
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 0, 0, 8), ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = codeBg, BorderBrush = codeBorder, BorderThickness = 1, CornerRadius = 6, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 10, 12, 10) },
            MarkdownElementKeys.CodeBlockHeader => new ElementStyle { FontFamily = font, FontSize = 12, Foreground = codeMuted, Background = codeHeaderBg, BorderBrush = codeBorder },
            MarkdownElementKeys.CodeBlockLanguage => new ElementStyle { FontFamily = font, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = codeMuted },
            MarkdownElementKeys.CodeBlockGutter => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = codeMuted, Background = codeHeaderBg },
            MarkdownElementKeys.CodeBlockLineNumber => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = codeMuted },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = codeBg, CornerRadius = 3, Padding = new Thickness(2, 0, 2, 0) },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, AccentBar = quoteBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = accent, HoverForeground = linkHover, FocusForeground = linkHover, Underline = true },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = true, Foreground = fgSecondary },
            MarkdownElementKeys.Subscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
            MarkdownElementKeys.Superscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
            MarkdownElementKeys.Inserted => new ElementStyle { FontFamily = font, FontSize = 14, Underline = true, Foreground = fg },
            MarkdownElementKeys.Marked => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = isDark ? Color.FromArgb(0x45, 0xFF, 0xD8, 0x66) : Color.FromArgb(0x66, 0xFF, 0xE5, 0x8A), CornerRadius = 3 },
            MarkdownElementKeys.Abbreviation => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accent, Underline = true },
            MarkdownElementKeys.DefinitionTerm => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 4, 0, 0) },
            MarkdownElementKeys.DefinitionDescription => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, Margin = new Thickness(18, 0, 0, 6) },
            MarkdownElementKeys.Figure => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 8, 0, 10) },
            MarkdownElementKeys.FigureCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fgSecondary, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Diagram => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = codeBg, BorderBrush = quoteBar, BorderThickness = 1, CornerRadius = 4, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 8) },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fgSecondary, ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = quoteBar, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fgSecondary, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Table => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = tableBg, BorderBrush = tableBorder, BorderThickness = 1, CornerRadius = 6, Margin = new Thickness(0, 8, 0, 12) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Background = tableHeaderBg, BorderBrush = tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = tableBg, BorderBrush = tableBorder, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.AlertNote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertTip => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertImportant => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xA8, 0x55, 0xF7), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertWarning => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            MarkdownElementKeys.AlertCaution => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44), Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 0, 0, 4) }
        };

        // Stable renderer resource keys are resolved once into the immutable
        // environment snapshot. Painting never performs resource lookups.
        return ApplyAppResourceOverrides(key, fluentStyle);
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

    private ElementStyle GetHighContrastDefault(string key)
    {
        var roles = MarkdownHighContrastDefaults.Resolve(key);
        var fg = ResolveHighContrastRole(roles.Foreground);
        var bg = roles.Background is { } backgroundRole ? ResolveHighContrastRole(backgroundRole) : (Color?)null;
        var accentBar = roles.AccentBar is { } accentRole ? ResolveHighContrastRole(accentRole) : (Color?)null;

        const string font = "Segoe UI Variable Text";
        const string mono = "Consolas";

        return key switch
        {
            MarkdownElementKeys.Heading1 => new ElementStyle { FontFamily = font, FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 16, 0, 8) },
            MarkdownElementKeys.Heading2 => new ElementStyle { FontFamily = font, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 14, 0, 6) },
            MarkdownElementKeys.Heading3 => new ElementStyle { FontFamily = font, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 12, 0, 4) },
            MarkdownElementKeys.Heading4 => new ElementStyle { FontFamily = font, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 10, 0, 4) },
            MarkdownElementKeys.Heading5 => new ElementStyle { FontFamily = font, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 8, 0, 2) },
            MarkdownElementKeys.Heading6 => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 6, 0, 2) },
            MarkdownElementKeys.Body => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 0, 0, 8) },
            MarkdownElementKeys.CodeBlock => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = bg, AccentBar = accentBar, BorderBrush = accentBar, BorderThickness = accentBar is null ? 0 : 1, CornerRadius = 0, Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 10, 12, 10) },
            MarkdownElementKeys.CodeBlockHeader => new ElementStyle { FontFamily = font, FontSize = 12, Foreground = fg, Background = bg, BorderBrush = accentBar },
            MarkdownElementKeys.CodeBlockLanguage => new ElementStyle { FontFamily = font, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = fg },
            MarkdownElementKeys.CodeBlockGutter => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = bg, BorderBrush = accentBar },
            MarkdownElementKeys.CodeBlockLineNumber => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg },
            MarkdownElementKeys.CodeInline => new ElementStyle { FontFamily = mono, FontSize = 12, Foreground = fg, Background = bg, Padding = new Thickness(2, 0, 2, 0) },
            MarkdownElementKeys.Quote => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(12, 2, 8, 2) },
            MarkdownElementKeys.Link => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, HoverForeground = fg, FocusForeground = fg, Underline = roles.Underline },
            MarkdownElementKeys.Strong => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg },
            MarkdownElementKeys.Emphasis => new ElementStyle { FontFamily = font, FontSize = 14, FontStyle = FontStyle.Italic, Foreground = fg },
            MarkdownElementKeys.Strikethrough => new ElementStyle { FontFamily = font, FontSize = 14, Strikethrough = roles.Strikethrough, Foreground = fg },
            MarkdownElementKeys.Subscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
            MarkdownElementKeys.Superscript => new ElementStyle { FontFamily = font, FontSize = 11, Foreground = fg },
            MarkdownElementKeys.Inserted => new ElementStyle { FontFamily = font, FontSize = 14, Underline = roles.Underline, Foreground = fg },
            MarkdownElementKeys.Marked => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg },
            MarkdownElementKeys.Abbreviation => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Underline = roles.Underline },
            MarkdownElementKeys.DefinitionTerm => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(0, 4, 0, 0) },
            MarkdownElementKeys.DefinitionDescription => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(18, 0, 0, 6) },
            MarkdownElementKeys.Figure => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 8, 0, 10) },
            MarkdownElementKeys.FigureCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fg, Background = bg, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Diagram => new ElementStyle { FontFamily = mono, FontSize = 13, Foreground = fg, Background = bg, AccentBar = accentBar, BorderBrush = accentBar, BorderThickness = accentBar is null ? 0 : 1, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 8) },
            MarkdownElementKeys.Math => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(1, 0, 1, 0) },
            MarkdownElementKeys.ListMarker => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, ListIndent = 22f, NestedListIndent = 0f },
            MarkdownElementKeys.ThematicBreak => new ElementStyle { FontFamily = font, Foreground = fg, Margin = new Thickness(0, 12, 0, 12) },
            MarkdownElementKeys.ImageCaption => new ElementStyle { FontFamily = font, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = fg, Margin = new Thickness(0, 2, 0, 8) },
            MarkdownElementKeys.Table => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, BorderBrush = accentBar, BorderThickness = accentBar is null ? 0 : 1, Margin = new Thickness(0, 8, 0, 12) },
            MarkdownElementKeys.TableHeader => new ElementStyle { FontFamily = font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Background = bg, BorderBrush = accentBar, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.TableCell => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, BorderBrush = accentBar, Padding = new Thickness(12, 9, 12, 9) },
            MarkdownElementKeys.AlertNote or MarkdownElementKeys.AlertTip or MarkdownElementKeys.AlertImportant or
            MarkdownElementKeys.AlertWarning or MarkdownElementKeys.AlertCaution => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, AccentBar = accentBar, Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 4, 0, 8) },
            _ => new ElementStyle { FontFamily = font, FontSize = 14, Foreground = fg, Background = bg, Margin = new Thickness(0, 0, 0, 4) }
        };
    }

    private Color ResolveHighContrastRole(MarkdownHighContrastColorRole role) => role switch
    {
        MarkdownHighContrastColorRole.WindowText => _systemTheme.WindowTextColor,
        MarkdownHighContrastColorRole.Window => _systemTheme.WindowColor,
        MarkdownHighContrastColorRole.Hotlight => _systemTheme.HotlightColor,
        MarkdownHighContrastColorRole.Highlight => _systemTheme.HighlightColor,
        MarkdownHighContrastColorRole.HighlightText => _systemTheme.HighlightTextColor,
        _ => _systemTheme.WindowTextColor,
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
        if (_capturedMarkdownResources is { } capturedResources &&
            resourceKey.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
        {
            return capturedResources.TryGetValue(resourceKey, out value!);
        }

        try
        {
            if (TryResolveScopedResourceValue(resourceKey, out value) ||
                TryResolveApplicationResourceValue(resourceKey, out value))
                return true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Resource lookup failed for '{resourceKey}': {ex.Message}");
        }

        value = null!;
        return false;
    }

    private bool TryResolveScopedResourceValue(string resourceKey, out object value)
    {
        IReadOnlyList<string> themeKeys = GetThemeDictionaryKeys();
        var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
        DependencyObject? current = _host;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                TryResolveExplicitFromDictionary(element.Resources, themeKeys, resourceKey, visited, out value))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        value = null!;
        return false;
    }

    private bool TryResolveExplicitScopedResourceValue(string resourceKey, out object value)
    {
        if (_capturedMarkdownResources is { } capturedResources &&
            IsScopedPlatformResourceKey(resourceKey))
        {
            return capturedResources.TryGetValue(resourceKey, out value!);
        }

        try
        {
            IReadOnlyList<string> themeKeys = GetThemeDictionaryKeys();
            var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
            DependencyObject? current = _host;
            while (current is not null)
            {
                if (current is FrameworkElement element &&
                    TryResolveExplicitFromDictionary(
                        element.Resources,
                        themeKeys,
                        resourceKey,
                        visited,
                        out value))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Explicit scoped resource lookup failed for '{resourceKey}': {ex.Message}");
        }

        value = null!;
        return false;
    }

    private bool TryResolveApplicationResourceValue(string resourceKey, out object value)
    {
        if (Application.Current?.Resources is { } applicationResources)
        {
            IReadOnlyList<string> themeKeys = GetThemeDictionaryKeys();
            var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
            return TryResolveExplicitFromDictionary(
                applicationResources,
                themeKeys,
                resourceKey,
                visited,
                out value);
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

    private static IReadOnlyList<string> CreateThemeDictionaryKeyFallbacks(string themeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themeKey);
        return string.Equals(themeKey, "Default", StringComparison.Ordinal)
            ? ["Default"]
            : [themeKey, "Default"];
    }

    internal static bool TryResolveFromDictionaryForTesting<TNode>(
        TNode resources,
        string themeKey,
        string resourceKey,
        Func<TNode, string, TNode?> getThemeDictionary,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        out object value)
        where TNode : class
        => TryResolveFromDictionaryForTesting(
            resources,
            CreateThemeDictionaryKeyFallbacks(themeKey),
            resourceKey,
            getThemeDictionary,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            out value);

    internal static bool TryResolveFromDictionaryForTesting<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        Func<TNode, string, TNode?> getThemeDictionary,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        out object value)
        where TNode : class
    {
        var visited = new HashSet<TNode>(ReferenceEqualityComparer.Instance);
        return ResourceDictionaryGraphResolver.TryResolve(
            resources,
            themeKeys,
            resourceKey,
            visited,
            getThemeDictionary,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            out value);
    }

    internal static bool TryResolveExplicitFromDictionaryForTesting<TNode>(
        TNode resources,
        string themeKey,
        string resourceKey,
        Func<TNode, string, TNode?> getThemeDictionary,
        Func<TNode, string, bool> hasExplicitLocalValue,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        out object value)
        where TNode : class
        => TryResolveExplicitFromDictionaryForTesting(
            resources,
            CreateThemeDictionaryKeyFallbacks(themeKey),
            resourceKey,
            getThemeDictionary,
            hasExplicitLocalValue,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            out value);

    internal static bool TryResolveExplicitFromDictionaryForTesting<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        Func<TNode, string, TNode?> getThemeDictionary,
        Func<TNode, string, bool> hasExplicitLocalValue,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        out object value)
        where TNode : class
    {
        var visited = new HashSet<TNode>(ReferenceEqualityComparer.Instance);
        return ResourceDictionaryGraphResolver.TryResolveExplicit(
            resources,
            themeKeys,
            resourceKey,
            visited,
            getThemeDictionary,
            hasExplicitLocalValue,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            out value);
    }

    private static bool TryResolveExplicitFromDictionary(
        ResourceDictionary resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        HashSet<ResourceDictionary> visited,
        out object value)
    {
        return ResourceDictionaryGraphResolver.TryResolveExplicit(
            resources,
            themeKeys,
            resourceKey,
            visited,
            static (dictionary, key) =>
                dictionary.ThemeDictionaries.TryGetValue(key, out object? selected) &&
                selected is ResourceDictionary selectedDictionary
                    ? selectedDictionary
                    : null,
            static (dictionary, key) => HasExplicitResourceKey(dictionary, key),
            static (ResourceDictionary dictionary, string key, out object result) =>
                dictionary.TryGetValue(key, out result),
            static dictionary => dictionary.MergedDictionaries.Count,
            static (dictionary, index) => dictionary.MergedDictionaries[index],
            out value);
    }

    private static bool HasExplicitResourceKey(ResourceDictionary resources, string resourceKey)
    {
        foreach (object candidate in resources.Keys)
        {
            if (candidate is string key && string.Equals(key, resourceKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private Dictionary<string, object>? CaptureMarkdownResources()
    {
        try
        {
            IReadOnlyList<string> themeKeys = GetThemeDictionaryKeys();
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
            DependencyObject? current = _host;
            while (current is not null)
            {
                if (current is FrameworkElement element)
                {
                    CaptureMarkdownResources(
                        element.Resources,
                        themeKeys,
                        visited,
                        values,
                        IncludeScopedRendererResource);
                }
                current = VisualTreeHelper.GetParent(current);
            }

            if (Application.Current?.Resources is { } applicationResources)
            {
                CaptureMarkdownResources(
                    applicationResources,
                    themeKeys,
                    visited,
                    values,
                    IncludeMarkdownResource);
            }

            return values;
        }
        catch (Exception ex)
        {
            // A custom ResourceDictionary projection can fail while it is being
            // mutated. Preserve the prior per-key resolver as a correctness
            // fallback for this one immutable snapshot.
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Bulk markdown-resource capture failed: {ex.Message}");
            return null;
        }
    }

    private static void CaptureMarkdownResources(
        ResourceDictionary resources,
        IReadOnlyList<string> themeKeys,
        HashSet<ResourceDictionary> visited,
        IDictionary<string, object> values,
        Predicate<string> includeKey)
    {
        ResourceDictionaryGraphResolver.CaptureResolvedValues(
            resources,
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
            includeKey,
            values);
    }

    private static bool IncludeMarkdownResource(string key)
        => key.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal);

    private static bool IncludeScopedRendererResource(string key)
        => IncludeMarkdownResource(key) || IsScopedPlatformResourceKey(key);

    internal static bool IsScopedPlatformResourceKey(string key)
        => key is
            "TextControlForegroundFocused" or
            "TextControlForeground" or
            "TextFillColorPrimaryBrush" or
            "TextFillColorPrimary" or
            "TextFillColorSecondaryBrush" or
            "TextFillColorSecondary" or
            "TextControlPlaceholderForeground" or
            "AccentTextFillColorPrimaryBrush" or
            "AccentTextFillColorPrimary" or
            "SystemControlForegroundAccentBrush" or
            "AccentFillColorDefaultBrush" or
            "ApplicationPageBackgroundThemeBrush" or
            "SolidBackgroundFillColorBaseBrush" or
            "LayerFillColorDefaultBrush" or
            "TextControlSelectionHighlightColor" or
            "AccentFillColorSelectedTextBackgroundBrush" or
            "SystemControlHighlightAccentBrush" or
            "SystemColorHighlightColorBrush" or
            "SystemColorHighlightColor" or
            "TextOnAccentFillColorSelectedTextBrush" or
            "TextOnAccentFillColorSelectedText" or
            "SystemColorHighlightTextColorBrush" or
            "SystemColorHighlightTextColor" or
            "SystemControlFocusVisualPrimaryBrush" or
            "FocusVisualPrimaryBrush";

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

    private void TryCollectResourceRoleNames(HashSet<string> roles)
    {
        try
        {
            IReadOnlyList<string> themeKeys = GetThemeDictionaryKeys();
            var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
            DependencyObject? current = _host;
            while (current is not null)
            {
                if (current is FrameworkElement element)
                    CollectResourceRoleNames(element.Resources, themeKeys, roles, visited);
                current = VisualTreeHelper.GetParent(current);
            }

            if (Application.Current?.Resources is { } applicationResources)
                CollectResourceRoleNames(applicationResources, themeKeys, roles, visited);
        }
        catch (Exception ex)
        {
            // Keep the built-in, theme-override, and style-sheet roles already
            // collected. Per-key lookup is independently guarded, so a custom
            // dictionary being mutated cannot abort the whole snapshot.
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Markdown resource-role discovery failed: {ex.Message}");
        }
    }

    private static void CollectResourceRoleNames(
        ResourceDictionary resources,
        IReadOnlyList<string> themeKeys,
        HashSet<string> roles,
        HashSet<ResourceDictionary> visited)
    {
        // Resource dictionaries may be shared by several ancestor scopes and
        // custom hosts can construct cyclic merged-dictionary graphs in code.
        // Walk each dictionary once so one restyle remains bounded by the
        // number of distinct dictionaries rather than paths through the graph.
        if (!visited.Add(resources))
            return;

        foreach (object key in resources.Keys)
        {
            if (key is string resourceKey &&
                MarkdownResourceKeys.TryGetStyleRoleName(resourceKey, out string roleName))
                roles.Add(roleName);
        }

        // Match WinUI's dictionary search: local, reverse merged, then exactly
        // one active theme dictionary selected from the ordered fallback keys.
        for (int index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            CollectResourceRoleNames(
                resources.MergedDictionaries[index],
                themeKeys,
                roles,
                visited);
        }

        ResourceDictionary? themeDictionary = null;
        for (int index = 0; index < themeKeys.Count; index++)
        {
            if (resources.ThemeDictionaries.TryGetValue(themeKeys[index], out object? selectedTheme) &&
                selectedTheme is ResourceDictionary selectedDictionary)
            {
                themeDictionary = selectedDictionary;
                break;
            }
        }

        if (themeDictionary is not null)
            CollectResourceRoleNames(themeDictionary, themeKeys, roles, visited);
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
