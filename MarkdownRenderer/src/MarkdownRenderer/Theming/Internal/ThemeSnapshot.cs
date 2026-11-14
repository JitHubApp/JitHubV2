using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.Theming.Internal;

/// <summary>
/// An immutable, thread-safe snapshot of resolved <see cref="ElementStyle"/> values
/// captured from <see cref="ThemeResolver"/> on the UI thread before layout work is
/// dispatched to a background thread.
/// </summary>
internal sealed class ThemeSnapshot
{
    // Win2D/DirectWrite font sizes must remain positive. Nested relative script
    // scopes can otherwise underflow when a caller intentionally chooses a
    // very small factor; this floor applies only to policy-derived sizes.
    internal const float MinimumRelativeScriptFontSize = 0.1f;

    private const int MaximumContextualStyleCacheEntries = 4_096;
    private const double MaximumDocumentPadding = 4_096;
    private const double MaximumBlockSpacing = 1_024;

    private readonly FrozenDictionary<string, ElementStyle> _styles;
    private readonly FrozenDictionary<string, ElementStyleOverride> _overrides;
    private readonly CompiledMarkdownStyleSheet _compiledStyleSheet;
    private readonly FrozenDictionary<string, ElementStyle> _simpleStyles;
    private readonly ConcurrentDictionary<ThemeStyleCacheKey, ElementStyle> _contextualStyles = new();
    private int _contextualStyleCacheEntryCount;
    private readonly float _textScaleFactor;
    private readonly float _minimumInteractiveSize;
    private readonly float _blockSpacing;
    private readonly Color? _overflowIndicatorColor;

    internal ThemeSnapshot(
        IReadOnlyDictionary<string, ElementStyle> styles,
        IReadOnlyDictionary<string, ElementStyleOverride> overrides,
        Color surfaceColor,
        Color selectionHighlightColor,
        Color selectionForegroundColor,
        Color focusVisualColor,
        bool isDark,
        bool isHighContrast,
        double textScaleFactor,
        MarkdownStyleSheet? styleSheet = null,
        double minimumInteractiveSize = 40,
        Thickness documentPadding = default,
        double blockSpacing = 0,
        Color? overflowIndicatorColor = null)
    {
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(overrides);

        _styles = styles.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        _overrides = overrides.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        SurfaceColor = surfaceColor;
        SelectionHighlightColor = selectionHighlightColor;
        SelectionForegroundColor = selectionForegroundColor;
        FocusVisualColor = focusVisualColor;
        IsDark = isDark;
        IsHighContrast = isHighContrast;
        _textScaleFactor = (float)Math.Clamp(
            double.IsFinite(textScaleFactor) ? textScaleFactor : 1.0,
            0.5,
            3.0);
        _compiledStyleSheet = (styleSheet ?? MarkdownStyleSheet.Empty).Compile();
        _minimumInteractiveSize = (float)Math.Clamp(
            double.IsFinite(minimumInteractiveSize) ? minimumInteractiveSize : 40,
            24,
            128);
        DocumentPadding = SanitizeDocumentPadding(documentPadding);
        _blockSpacing = (float)Math.Clamp(
            double.IsFinite(blockSpacing) ? blockSpacing : 0,
            0,
            MaximumBlockSpacing);
        _overflowIndicatorColor = isHighContrast
            ? ResolveMandatoryOverflowIndicatorColor(_styles, focusVisualColor)
            : overflowIndicatorColor;
        _simpleStyles = CompileSimpleStyles();
        LayoutFingerprint = ThemeLayoutFingerprint.Compute(
            _simpleStyles,
            _minimumInteractiveSize,
            DocumentPadding,
            _blockSpacing,
            _compiledStyleSheet.LayoutPolicy,
            _compiledStyleSheet.HasRelativeScriptFontSizeRules,
            _compiledStyleSheet.HasRelativeInlineCodeFontSizeRules);
    }

    /// <summary>Resolved document surface color.</summary>
    public readonly Color SurfaceColor;

    /// <summary>Resolved selection highlight color.</summary>
    public readonly Color SelectionHighlightColor;

    /// <summary>Resolved selected-text foreground color.</summary>
    public readonly Color SelectionForegroundColor;

    /// <summary>Resolved keyboard focus visual color.</summary>
    public readonly Color FocusVisualColor;

    /// <summary>Gets whether the snapshot was captured from a dark theme.</summary>
    public readonly bool IsDark;

    /// <summary>Gets whether the snapshot was captured in a high-contrast theme.</summary>
    public readonly bool IsHighContrast;

    /// <summary>Gets the text-scale factor compiled into this snapshot.</summary>
    public double TextScaleFactor => _textScaleFactor;

    /// <summary>Gets the compiled minimum target size for interactive renderer controls.</summary>
    public double MinimumInteractiveSize => _minimumInteractiveSize;

    /// <summary>Gets the sanitized padding around the document's top-level content.</summary>
    public readonly Thickness DocumentPadding;

    /// <summary>Gets the sanitized spacing inserted between adjacent top-level blocks.</summary>
    public double BlockSpacing => _blockSpacing;

    internal byte LayoutPolicy => _compiledStyleSheet.LayoutPolicy;
    internal bool HasRelativeScriptFontSizeRules => _compiledStyleSheet.HasRelativeScriptFontSizeRules;
    internal bool HasRelativeInlineCodeFontSizeRules => _compiledStyleSheet.HasRelativeInlineCodeFontSizeRules;
    internal bool CenterDefaultTableHeaders => _compiledStyleSheet.CenterDefaultTableHeaders;
    internal bool MiddleAlignInlineImages => _compiledStyleSheet.MiddleAlignInlineImages;

    internal static ElementStyle WithFontSize(ElementStyle style, float fontSize)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new ElementStyle
        {
            FontFamily = style.FontFamily,
            FontSize = fontSize,
            RelativeFontSizeScale = null,
            FontWeight = style.FontWeight,
            FontStyle = style.FontStyle,
            Foreground = style.Foreground,
            HoverForeground = style.HoverForeground,
            FocusForeground = style.FocusForeground,
            Background = style.Background,
            AccentBar = style.AccentBar,
            AccentBarThickness = style.AccentBarThickness,
            AccentBarAffectsContentLayout = style.AccentBarAffectsContentLayout,
            BorderBrush = style.BorderBrush,
            HeadingBorderBottomOnly = style.HeadingBorderBottomOnly,
            BorderThickness = style.BorderThickness,
            CornerRadius = style.CornerRadius,
            ListIndent = style.ListIndent,
            NestedListIndent = style.NestedListIndent,
            Underline = style.Underline,
            Strikethrough = style.Strikethrough,
            Margin = style.Margin,
            Padding = style.Padding,
            LineHeightMultiplier = style.LineHeightMultiplier,
        };
    }

    /// <summary>
    /// Resolves the shared local-overflow colors without consulting WinUI resources on
    /// the paint path. High Contrast uses one mandatory system-role color.
    /// </summary>
    internal (Color Track, Color Thumb) ResolveOverflowIndicatorColors(Color fallback)
    {
        Color indicator = _overflowIndicatorColor ?? fallback;
        if (IsHighContrast)
            return (indicator, indicator);

        return (
            ScaleAlpha(indicator, 0x28),
            ScaleAlpha(indicator, 0x90));
    }

    /// <summary>
    /// Stable-in-process fingerprint of resolved typography and geometry. Color
    /// values are deliberately excluded so brush-only environment changes can
    /// reuse device-independent layout metrics.
    /// </summary>
    internal readonly ulong LayoutFingerprint;

    /// <summary>Gets the effective style for an element key.</summary>
    public ElementStyle GetStyle(string elementKey)
        => GetStyle(elementKey, null, null);

    /// <summary>
    /// Gets the effective style for an element key with context and attribute aliases applied.
    /// </summary>
    public ElementStyle GetStyle(
        string elementKey,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys)
        => GetStyle(elementKey, contextKeys, aliasKeys, language: null, state: null);

    /// <summary>
    /// Gets the effective style for an element key with semantic context,
    /// language, and state qualifiers applied.
    /// </summary>
    public ElementStyle GetStyle(
        string elementKey,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys,
        string? language,
        string? state)
    {
        ArgumentNullException.ThrowIfNull(elementKey);

        if (IsSimpleQuery(contextKeys, aliasKeys, language, state) &&
            _simpleStyles.TryGetValue(elementKey, out ElementStyle? simpleStyle))
        {
            return simpleStyle;
        }

        var lookupKey = ThemeStyleCacheKey.CreateLookup(
            elementKey,
            contextKeys,
            aliasKeys,
            language,
            state);
        if (_contextualStyles.TryGetValue(lookupKey, out ElementStyle? cachedStyle))
            return cachedStyle;

        ElementStyle resolvedStyle = ResolveStyleCore(
            elementKey,
            contextKeys,
            aliasKeys,
            language,
            state);

        int reservation = Interlocked.Increment(ref _contextualStyleCacheEntryCount);
        if (reservation > MaximumContextualStyleCacheEntries)
        {
            Interlocked.Decrement(ref _contextualStyleCacheEntryCount);
            return resolvedStyle;
        }

        if (_contextualStyles.TryAdd(lookupKey.CreateStableCopy(), resolvedStyle))
            return resolvedStyle;

        Interlocked.Decrement(ref _contextualStyleCacheEntryCount);
        return _contextualStyles.TryGetValue(lookupKey, out cachedStyle)
            ? cachedStyle
            : resolvedStyle;
    }

    private FrozenDictionary<string, ElementStyle> CompileSimpleStyles()
    {
        var elementKeys = new HashSet<string>(_styles.Keys, StringComparer.Ordinal);
        elementKeys.UnionWith(_overrides.Keys);
        elementKeys.UnionWith(_compiledStyleSheet.RoleNames);
        elementKeys.Add(MarkdownElementKeys.Body);

        var result = new Dictionary<string, ElementStyle>(elementKeys.Count, StringComparer.Ordinal);
        foreach (string elementKey in elementKeys)
            result[elementKey] = ResolveStyleCore(elementKey, null, null, null, null);

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static Thickness SanitizeDocumentPadding(Thickness padding)
        => new(
            SanitizeDocumentGeometryValue(padding.Left),
            SanitizeDocumentGeometryValue(padding.Top),
            SanitizeDocumentGeometryValue(padding.Right),
            SanitizeDocumentGeometryValue(padding.Bottom));

    private static double SanitizeDocumentGeometryValue(double value)
        => Math.Clamp(double.IsFinite(value) ? value : 0, 0, MaximumDocumentPadding);

    private static Color ResolveMandatoryOverflowIndicatorColor(
        IReadOnlyDictionary<string, ElementStyle> styles,
        Color fallback)
        => styles.TryGetValue(MarkdownElementKeys.Body, out ElementStyle? body)
            ? body.Foreground
            : fallback;

    private static Color ScaleAlpha(Color color, byte opacity)
    {
        byte alpha = (byte)((color.A * opacity + 127) / byte.MaxValue);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private ElementStyle ResolveStyleCore(
        string elementKey,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys,
        string? language,
        string? state)
    {
        ElementStyle mandatoryStyle = GetBaseStyle(elementKey, contextKeys);
        ElementStyle style = ApplyOverride(mandatoryStyle, elementKey);

        if (contextKeys is not null)
        {
            for (int i = 0; i < contextKeys.Count; i++)
                style = ApplyOverride(style, MarkdownElementKeys.Context(contextKeys[i], elementKey));
        }

        if (aliasKeys is not null)
        {
            for (int i = 0; i < aliasKeys.Count; i++)
            {
                string aliasKey = aliasKeys[i];
                style = ApplyOverride(style, aliasKey);

                if (contextKeys is not null)
                {
                    for (int c = 0; c < contextKeys.Count; c++)
                        style = ApplyOverride(style, MarkdownElementKeys.Context(contextKeys[c], aliasKey));
                }
            }
        }

        if (_compiledStyleSheet.HasRules)
        {
            style = _compiledStyleSheet.Resolve(
                style,
                elementKey,
                GetListNestingDepth(contextKeys),
                language,
                state,
                contextKeys,
                aliasKeys);
        }

        // Consumer typography and geometry remain customizable in High
        // Contrast. System color roles and contrast-critical decorations are
        // enforced last so neither a theme override nor a semantic rule can
        // replace WindowText/Hotlight/Highlight or remove a required underline.
        if (IsHighContrast)
            style = EnforceHighContrast(style, mandatoryStyle);

        return ApplyTextScale(style);
    }

    private ElementStyle ApplyTextScale(ElementStyle style)
    {
        if (Math.Abs(_textScaleFactor - 1f) < 0.0001f)
            return style;

        return new ElementStyle
        {
            FontFamily = style.FontFamily,
            FontSize = style.FontSize * _textScaleFactor,
            RelativeFontSizeScale = style.RelativeFontSizeScale,
            FontWeight = style.FontWeight,
            FontStyle = style.FontStyle,
            Foreground = style.Foreground,
            HoverForeground = style.HoverForeground,
            FocusForeground = style.FocusForeground,
            Background = style.Background,
            AccentBar = style.AccentBar,
            AccentBarThickness = style.AccentBarThickness,
            AccentBarAffectsContentLayout = style.AccentBarAffectsContentLayout,
            BorderBrush = style.BorderBrush,
            HeadingBorderBottomOnly = style.HeadingBorderBottomOnly,
            BorderThickness = style.BorderThickness,
            CornerRadius = style.CornerRadius,
            ListIndent = style.ListIndent,
            NestedListIndent = style.NestedListIndent,
            Underline = style.Underline,
            Strikethrough = style.Strikethrough,
            Margin = style.Margin,
            Padding = style.Padding,
            LineHeightMultiplier = style.LineHeightMultiplier,
        };
    }

    private static bool IsSimpleQuery(
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys,
        string? language,
        string? state)
        => (contextKeys is null || contextKeys.Count == 0) &&
           (aliasKeys is null || aliasKeys.Count == 0) &&
           string.IsNullOrWhiteSpace(language) &&
           string.IsNullOrWhiteSpace(state);

    private static int GetListNestingDepth(IReadOnlyList<string>? contextKeys)
    {
        if (contextKeys is null)
            return 0;

        int depth = 0;
        for (int i = 0; i < contextKeys.Count; i++)
        {
            if (MarkdownStyleContextKeys.TryParseListNestingDepth(contextKeys[i], out int parsed))
            {
                depth = Math.Max(depth, parsed);
            }
        }

        return depth;
    }

    private ElementStyle GetBaseStyle(string elementKey, IReadOnlyList<string>? contextKeys)
    {
        if (_styles.TryGetValue(elementKey, out var s))
        {
            // A list's block gap belongs to the outermost list. As in HTML's
            // user-agent style sheet, a list nested in another list's item has
            // no vertical margin of its own, so it cannot widen the gap between
            // its parent's items. Theme overrides and style-sheet rules apply
            // on top and can still give nested lists a margin.
            if (elementKey == MarkdownElementKeys.List && GetListNestingDepth(contextKeys) > 1)
            {
                Thickness margin = s.Margin;
                return ApplyOverride(s, new ElementStyleOverride
                {
                    Margin = new Thickness(margin.Left, 0, margin.Right, 0),
                });
            }

            return s;
        }
        // Lists are structural containers, not body paragraphs. Sparse custom
        // snapshots historically lay them out with zero margin, so do not let
        // the generic Body fallback introduce paragraph margins at the list edge.
        if (elementKey == MarkdownElementKeys.List) return new ElementStyle();
        if (_styles.TryGetValue(MarkdownElementKeys.Body, out var body)) return body;
        return new ElementStyle();
    }

    private ElementStyle ApplyOverride(ElementStyle style, string key)
        => _overrides.TryGetValue(key, out var ov)
            ? ApplyOverride(style, ov)
            : style;

    internal static ElementStyle ApplyOverride(ElementStyle defaults, ElementStyleOverride ov)
    {
        return new ElementStyle
        {
            FontFamily = ov.FontFamily ?? defaults.FontFamily,
            FontSize = ov.FontSize ?? defaults.FontSize,
            RelativeFontSizeScale = ov.RelativeFontSizeScale ??
                (ov.FontSize.HasValue ? null : defaults.RelativeFontSizeScale),
            FontWeight = ov.FontWeight ?? defaults.FontWeight,
            FontStyle = ov.FontStyle ?? defaults.FontStyle,
            Foreground = ov.Foreground ?? defaults.Foreground,
            HoverForeground = ov.HoverForeground ?? defaults.HoverForeground,
            FocusForeground = ov.FocusForeground ?? defaults.FocusForeground,
            Background = ov.Background ?? defaults.Background,
            AccentBar = ov.AccentBar ?? defaults.AccentBar,
            AccentBarThickness = ov.AccentBarThickness ?? defaults.AccentBarThickness,
            AccentBarAffectsContentLayout = ov.AccentBarAffectsContentLayout ?? defaults.AccentBarAffectsContentLayout,
            BorderBrush = ov.BorderBrush ?? defaults.BorderBrush,
            HeadingBorderBottomOnly = ov.HeadingBorderBottomOnly ??
                (ov.BorderBrush is not null || ov.BorderThickness is not null
                    ? false
                    : defaults.HeadingBorderBottomOnly),
            BorderThickness = ov.BorderThickness ?? defaults.BorderThickness,
            CornerRadius = ov.CornerRadius ?? defaults.CornerRadius,
            ListIndent = ov.ListIndent ?? defaults.ListIndent,
            NestedListIndent = ov.NestedListIndent ?? defaults.NestedListIndent,
            Underline = ov.Underline ?? defaults.Underline,
            Strikethrough = ov.Strikethrough ?? defaults.Strikethrough,
            Margin = ov.Margin ?? defaults.Margin,
            Padding = ov.Padding ?? defaults.Padding,
            LineHeightMultiplier = ov.LineHeightMultiplier ?? defaults.LineHeightMultiplier
        };
    }

    internal static ElementStyle EnforceHighContrast(
        ElementStyle customized,
        ElementStyle mandatory)
    {
        return new ElementStyle
        {
            FontFamily = customized.FontFamily,
            FontSize = customized.FontSize,
            RelativeFontSizeScale = customized.RelativeFontSizeScale,
            FontWeight = customized.FontWeight,
            FontStyle = customized.FontStyle,
            Foreground = mandatory.Foreground,
            HoverForeground = mandatory.HoverForeground,
            FocusForeground = mandatory.FocusForeground,
            Background = mandatory.Background,
            AccentBar = mandatory.AccentBar,
            AccentBarThickness = customized.AccentBarThickness,
            AccentBarAffectsContentLayout = customized.AccentBarAffectsContentLayout,
            BorderBrush = mandatory.BorderBrush,
            HeadingBorderBottomOnly = customized.HeadingBorderBottomOnly,
            BorderThickness = Math.Max(customized.BorderThickness, mandatory.BorderThickness),
            CornerRadius = customized.CornerRadius,
            ListIndent = customized.ListIndent,
            NestedListIndent = customized.NestedListIndent,
            Underline = customized.Underline || mandatory.Underline,
            Strikethrough = customized.Strikethrough || mandatory.Strikethrough,
            Margin = customized.Margin,
            Padding = customized.Padding,
            LineHeightMultiplier = customized.LineHeightMultiplier,
        };
    }
}
