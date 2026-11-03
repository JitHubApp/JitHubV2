using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
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
    private static readonly RelevantResourceKeyCache<ResourceDictionary> RelevantResourceKeys = new(
        static resources => resources.Keys.Count,
        ThemeResourceValues.EnumerateStringKeys,
        ThemeResourceScope.IncludeScopedRendererResource);

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
        MarkdownElementKeys.List, MarkdownElementKeys.ListMarker, MarkdownElementKeys.ThematicBreak,
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

    public ThemeResolver(FrameworkElement host, MarkdownTheme theme)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        // Application.Current and its Resources projection are stable for the
        // lifetime of one resolver. Capture the root once instead of crossing
        // the WinUI projection for every role/property lookup in the snapshot.
        _applicationResources = Application.Current?.Resources;
        _systemTheme = theme.ForcedContrastColors is { } forcedContrastColors
            ? new ForcedMarkdownSystemThemeProvider(forcedContrastColors)
            : new MarkdownSystemThemeProvider(_host);
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
        HashSet<string> allKeys = new(BuiltInElementKeys, StringComparer.Ordinal);
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
        var snapshot = new ThemeSnapshot(
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
        return snapshot;
    }

    private ElementStyle GetDefault(string key, bool resolveAppResourceOverrides = true)
    {
        bool isDark = !_isHighContrast && _host.ActualTheme == ElementTheme.Dark;
        PlatformThemeColors platformColors = _isHighContrast ? default : GetPlatformThemeColors();
        var mandatory = ThemeDefaultStyles.CreateDefaultStyle(
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
            ThemeResourceValues.TryExtractTextDecorations(value, out TextDecorations decorations)
                ? decorations
                : null;

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

    private Color ResolveSelectionHighlightColor()
    {
        var nativeSelection = ResolveNativeSelectionHighlightColor();
        if (_isHighContrast)
            return ThemeColorMath.WithAlpha(nativeSelection, 0xFF);

        if (ResolveColorResource(MarkdownResourceKeys.SelectionBackgroundBrush) is { } configuredSelection)
        {
            return configuredSelection.A == byte.MaxValue
                ? configuredSelection
                : ThemeColorMath.CompositeOver(configuredSelection, ResolveDocumentSurfaceColor());
        }

        // TextBox paints the selection highlight over a clean text-control
        // surface after replacing the selected glyph foreground. Our canvas has
        // already painted the unselected glyphs underneath, so use the same
        // native resource but pre-composite translucent brushes over the
        // markdown surface. The resulting opaque color matches the native
        // visual while preventing old dark glyphs from bleeding through.
        return ThemeColorMath.CompositeOver(nativeSelection, ResolveDocumentSurfaceColor());
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
            return ThemeColorMath.WithAlpha(configuredForeground, byte.MaxValue);

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

    private bool TryResolveResourceColor(string resourceKey, out Color color)
    {
        if (TryResolveResourceValue(resourceKey, out var value) &&
            ThemeResourceValues.TryExtractColor(value, out color))
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
            ThemeResourceValues.TryExtractColor(value, out color))
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
                : ThemeResourceScope.IncludeScopedRendererResource;
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
            ThemeResourceScope.IsScopedPlatformResourceKey(resourceKey))
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
                if (ThemeResourceScope.IsScopedPlatformResourceKey(resourceKey))
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
        if (ThemeResourceScope.IsScopedPlatformResourceKey(resourceKey))
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
        => ThemeResourceScope.GetThemeDictionaryKeys(_isHighContrast, _host.ActualTheme);

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

    private bool IncludeScopedResourceForSnapshot(string key)
        => _scopedResourceStyleRoles is { } styleRoles &&
            ThemeResourceScope.IsScopedResourceRequiredForSnapshot(key, styleRoles);

    internal static void InvalidateResourceKeyCache()
        => RelevantResourceKeys.Invalidate();

    private static IEnumerable<string> EnumerateRelevantResourceKeys(ResourceDictionary resources)
        => RelevantResourceKeys.GetRelevantKeys(resources);

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
        return PlatformThemeColors.Resolve(_host.ActualTheme, scoped);
    }

    private PlatformThemeColorOverrides ResolveScopedPlatformThemeColorOverrides()
    {
        Color? Resolve(params string[] resourceKeys)
        {
            foreach (string resourceKey in resourceKeys)
            {
                if (TryResolveExplicitScopedResourceValue(resourceKey, out object value) &&
                    ThemeResourceValues.TryExtractColor(value, out Color color))
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

}
