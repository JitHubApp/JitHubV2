using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.Text;
using MarkdownRenderer.Theming.Internal.Environment;
using MarkdownRenderer.Theming.Internal.ResourceDictionaries;

namespace MarkdownRenderer.Theming.Internal;

/// <summary>
/// Resolves <see cref="ElementStyle"/> values for the current theme &amp; FrameworkElement,
/// merging Win11 defaults with theme overrides. Exposes <see cref="GetEffectiveStyle"/>
/// which is the only call sites needed to consume.
/// </summary>
internal sealed class ThemeResolver
{
    /// <summary>
    /// The built-in style roles in the fixed order of the style-role demand
    /// mask: bit <c>i</c> of a demand mask stands for the role at index
    /// <c>i</c>, and <c>MarkdownStyleRoleDemand</c> names the same bits.
    /// </summary>
    internal static IReadOnlyList<string> BuiltInStyleRoleOrder => BuiltInElementKeys;

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
    private readonly IMarkdownSystemThemeProvider _systemTheme;
    private readonly ThemeResourceLookup _resources;
    private readonly bool _isHighContrast;
    private PlatformThemeColors? _platformThemeColors;
    public ThemeResolver(
        FrameworkElement host,
        MarkdownTheme theme,
        RelevantResourceKeyCache<ResourceDictionary> relevantResourceKeys)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        // Application.Current and its Resources projection are stable for the
        // lifetime of one resolver. Capture the root once instead of crossing
        // the WinUI projection for every role/property lookup in the snapshot.
        ResourceDictionary? applicationResources = Application.Current?.Resources;
        _systemTheme = theme.ForcedContrastColors is { } forcedContrastColors
            ? new ForcedMarkdownSystemThemeProvider(forcedContrastColors)
            : new MarkdownSystemThemeProvider(_host);
        _isHighContrast = _systemTheme.IsHighContrast;
        _resources = new ThemeResourceLookup(
            _host, applicationResources, _isHighContrast, relevantResourceKeys);
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
        _resources.BeginSnapshot();
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
        _resources.UseScopedResourceStyleRoles(scopedResourceStyleRoles);
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
        float minimumInteractiveSize = _resources.ResolveFloatResource(MarkdownResourceKeys.MinimumInteractiveSize) ?? 40f;
        Thickness documentPadding = _resources.ResolveThicknessResource(MarkdownResourceKeys.DocumentPadding) ?? default;
        float blockSpacing = _resources.ResolveFloatResource(MarkdownResourceKeys.BlockSpacing) ?? 0;
        Color? overflowIndicatorColor = _resources.ResolveColorResource(MarkdownResourceKeys.OverflowIndicatorBrush);

        // Resource capture is complete before construction; clearing the
        // snapshot-only filter here also keeps a reused resolver safe if the
        // immutable snapshot constructor throws.
        _resources.EndSnapshotCapture();
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

        RoleResourceValues values = ResolveRoleResourceValues(role);
        ApplyConvenienceAliases(elementKey, ref values);
        return ThemeSnapshot.ApplyOverride(style, values.ToOverride());
    }

    /// <summary>Reads every MarkdownRenderer.&lt;role&gt;.&lt;property&gt; resource of <paramref name="role"/>.</summary>
    private RoleResourceValues ResolveRoleResourceValues(MarkdownStyleRole role) => new()
    {
        Foreground = _resources.ResolveRoleColor(role, MarkdownStyleProperty.ForegroundBrush),
        HoverForeground = _resources.ResolveRoleColor(role, MarkdownStyleProperty.HoverForegroundBrush),
        FocusForeground = _resources.ResolveRoleColor(role, MarkdownStyleProperty.FocusForegroundBrush),
        Background = _resources.ResolveRoleColor(role, MarkdownStyleProperty.BackgroundBrush),
        Accent = _resources.ResolveRoleColor(role, MarkdownStyleProperty.AccentBrush),
        Border = _resources.ResolveRoleColor(role, MarkdownStyleProperty.BorderBrush),
        FontFamily = _resources.ResolveRoleFontFamily(role),
        FontSize = _resources.ResolveRoleFloat(role, MarkdownStyleProperty.FontSize),
        FontWeight = _resources.ResolveRoleFontWeight(role),
        FontStyle = _resources.ResolveRoleFontStyle(role),
        Margin = _resources.ResolveRoleThickness(role, MarkdownStyleProperty.Margin),
        Padding = _resources.ResolveRoleThickness(role, MarkdownStyleProperty.Padding),
        BorderThickness = _resources.ResolveRoleFloat(role, MarkdownStyleProperty.BorderThickness),
        CornerRadius = _resources.ResolveRoleFloat(role, MarkdownStyleProperty.CornerRadius),
        LineHeight = _resources.ResolveRoleFloat(role, MarkdownStyleProperty.LineHeightMultiplier),
        ListIndent = _resources.ResolveRoleFloat(role, MarkdownStyleProperty.ListIndent),
        NestedListIndent = _resources.ResolveRoleFloat(role, MarkdownStyleProperty.NestedListIndent),
        TextDecorations = _resources.ResolveRoleTextDecorations(role),
    };

    /// <summary>
    /// Fills values the role resources left unset from the convenience
    /// resource keys, which stay stable even if role names evolve.
    /// </summary>
    private void ApplyConvenienceAliases(string elementKey, ref RoleResourceValues values)
    {
        if (elementKey == MarkdownElementKeys.Body)
        {
            values.Foreground ??= _resources.ResolveColorResource(MarkdownResourceKeys.BodyForegroundBrush);
            values.FontFamily ??= _resources.ResolveFontFamilyResource(MarkdownResourceKeys.BodyFontFamily);
            values.FontSize ??= _resources.ResolveFloatResource(MarkdownResourceKeys.BodyFontSize);
        }
        else if (elementKey == MarkdownElementKeys.Link)
        {
            values.Foreground ??= _resources.ResolveColorResource(MarkdownResourceKeys.LinkForegroundBrush);
            values.HoverForeground ??= _resources.ResolveColorResource(MarkdownResourceKeys.LinkHoverForegroundBrush);
        }
        else if (elementKey is MarkdownElementKeys.CodeBlock or MarkdownElementKeys.CodeInline)
        {
            values.FontFamily ??= _resources.ResolveFontFamilyResource(MarkdownResourceKeys.CodeFontFamily);
            if (elementKey == MarkdownElementKeys.CodeBlock)
                values.Background ??= _resources.ResolveColorResource(MarkdownResourceKeys.CodeBlockBackgroundBrush);
        }
        else if (elementKey == MarkdownElementKeys.Quote)
        {
            values.Accent ??= _resources.ResolveColorResource(MarkdownResourceKeys.QuoteAccentBrush);
        }
        else if (elementKey is MarkdownElementKeys.Table or MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell)
        {
            values.Border ??= _resources.ResolveColorResource(MarkdownResourceKeys.TableBorderBrush);
            if (elementKey is MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell)
                values.Padding ??= _resources.ResolveThicknessResource(MarkdownResourceKeys.TableCellPadding);
        }
    }

    private Color ResolveSelectionHighlightColor()
    {
        var nativeSelection = ResolveNativeSelectionHighlightColor();
        if (_isHighContrast)
            return ThemeColorMath.WithAlpha(nativeSelection, 0xFF);

        if (_resources.ResolveColorResource(MarkdownResourceKeys.SelectionBackgroundBrush) is { } configuredSelection)
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
        if (_resources.TryResolvePlatformResourceColor("TextControlSelectionHighlightColor", out var color) ||
            _resources.TryResolvePlatformResourceColor("AccentFillColorSelectedTextBackgroundBrush", out color) ||
            _resources.TryResolvePlatformResourceColor("SystemControlHighlightAccentBrush", out color) ||
            _resources.TryResolvePlatformResourceColor("SystemColorHighlightColorBrush", out color) ||
            _resources.TryResolvePlatformResourceColor("SystemColorHighlightColor", out color))
        {
            return color;
        }

        return _isHighContrast
            ? _systemTheme.HighlightColor
            : _resources.ResolvePlatformBrush("AccentFillColorDefaultBrush",
                _resources.ResolvePlatformBrush("AccentTextFillColorPrimaryBrush", GetPlatformThemeColors().AccentText));
    }

    private Color ResolveSelectionForegroundColor()
    {
        if (_isHighContrast)
            return _systemTheme.HighlightTextColor;

        if (_resources.ResolveColorResource(MarkdownResourceKeys.SelectionForegroundBrush) is { } configuredForeground)
            return ThemeColorMath.WithAlpha(configuredForeground, byte.MaxValue);

        if (_resources.TryResolvePlatformResourceColor("TextOnAccentFillColorSelectedTextBrush", out var color) ||
            _resources.TryResolvePlatformResourceColor("TextOnAccentFillColorSelectedText", out color) ||
            _resources.TryResolvePlatformResourceColor("SystemColorHighlightTextColorBrush", out color) ||
            _resources.TryResolvePlatformResourceColor("SystemColorHighlightTextColor", out color))
        {
            return Color.FromArgb(0xFF, color.R, color.G, color.B);
        }

        return Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private Color ResolveFocusVisualColor()
    {
        if (_isHighContrast)
            return _systemTheme.HighlightColor;

        if (_resources.ResolveColorResource(MarkdownResourceKeys.FocusVisualBrush) is { } configuredFocus)
            return configuredFocus;

        return _resources.ResolvePlatformBrush("SystemControlFocusVisualPrimaryBrush",
            _resources.ResolvePlatformBrush("FocusVisualPrimaryBrush",
                _resources.ResolvePlatformBrush("AccentTextFillColorPrimaryBrush", GetPlatformThemeColors().AccentText)));
    }

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
               _resources.ResolveColorResource(MarkdownResourceKeys.DocumentSurfaceBrush) ??
               ResolveSurfaceColor();
    }

    private PlatformThemeColors GetPlatformThemeColors()
        => _platformThemeColors ??= ResolvePlatformThemeColors();

    private PlatformThemeColors ResolvePlatformThemeColors()
    {
        PlatformThemeColorOverrides scoped = _resources.ResolveScopedPlatformThemeColorOverrides();
        return PlatformThemeColors.Resolve(_host.ActualTheme, scoped);
    }

    /// <summary>
    /// The app resource values of one style role, before they become an
    /// <see cref="ElementStyleOverride"/>. A null value leaves the style as is.
    /// </summary>
    private struct RoleResourceValues
    {
        public Color? Foreground;
        public Color? HoverForeground;
        public Color? FocusForeground;
        public Color? Background;
        public Color? Accent;
        public Color? Border;
        public string? FontFamily;
        public float? FontSize;
        public Windows.UI.Text.FontWeight? FontWeight;
        public FontStyle? FontStyle;
        public Thickness? Margin;
        public Thickness? Padding;
        public float? BorderThickness;
        public float? CornerRadius;
        public float? LineHeight;
        public float? ListIndent;
        public float? NestedListIndent;
        public TextDecorations? TextDecorations;

        public readonly ElementStyleOverride ToOverride() => new()
        {
            Foreground = Foreground,
            HoverForeground = HoverForeground,
            FocusForeground = FocusForeground,
            Background = Background,
            AccentBar = Accent,
            BorderBrush = Border,
            FontFamily = FontFamily,
            FontSize = FontSize,
            FontWeight = FontWeight,
            FontStyle = FontStyle,
            Margin = Margin,
            Padding = Padding,
            BorderThickness = BorderThickness,
            CornerRadius = CornerRadius,
            LineHeightMultiplier = LineHeight,
            ListIndent = ListIndent,
            NestedListIndent = NestedListIndent,
            Underline = TextDecorations is { } decorations
                ? (decorations & Windows.UI.Text.TextDecorations.Underline) != 0
                : null,
            Strikethrough = TextDecorations is { } resolvedDecorations
                ? (resolvedDecorations & Windows.UI.Text.TextDecorations.Strikethrough) != 0
                : null,
        };
    }
}
