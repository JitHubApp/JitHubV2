using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// An immutable, thread-safe snapshot of resolved <see cref="ElementStyle"/> values
/// captured from <see cref="ThemeResolver"/> on the UI thread before layout work is
/// dispatched to a background thread.
/// </summary>
internal sealed class ThemeSnapshot
{
    private const int MaximumContextualStyleCacheEntries = 4_096;
    private const double MaximumDocumentPadding = 4_096;
    private const double MaximumBlockSpacing = 1_024;

    private readonly FrozenDictionary<string, ElementStyle> _styles;
    private readonly FrozenDictionary<string, ElementStyleOverride> _overrides;
    private readonly CompiledMarkdownStyleSheet _compiledStyleSheet;
    private readonly FrozenDictionary<string, ElementStyle> _simpleStyles;
    private readonly ConcurrentDictionary<StyleCacheKey, ElementStyle> _contextualStyles = new();
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
        LayoutFingerprint = ComputeLayoutFingerprint(
            _simpleStyles,
            _minimumInteractiveSize,
            DocumentPadding,
            _blockSpacing);
    }

    /// <summary>Resolved document surface color.</summary>
    public Color SurfaceColor { get; }

    /// <summary>Resolved selection highlight color.</summary>
    public Color SelectionHighlightColor { get; }

    /// <summary>Resolved selected-text foreground color.</summary>
    public Color SelectionForegroundColor { get; }

    /// <summary>Resolved keyboard focus visual color.</summary>
    public Color FocusVisualColor { get; }

    /// <summary>Gets whether the snapshot was captured from a dark theme.</summary>
    public bool IsDark { get; }

    /// <summary>Gets whether the snapshot was captured in a high-contrast theme.</summary>
    public bool IsHighContrast { get; }

    /// <summary>Gets the text-scale factor compiled into this snapshot.</summary>
    public double TextScaleFactor => _textScaleFactor;

    /// <summary>Gets the compiled minimum target size for interactive renderer controls.</summary>
    public double MinimumInteractiveSize => _minimumInteractiveSize;

    /// <summary>Gets the sanitized padding around the document's top-level content.</summary>
    public Thickness DocumentPadding { get; }

    /// <summary>Gets the sanitized spacing inserted between adjacent top-level blocks.</summary>
    public double BlockSpacing => _blockSpacing;

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
    internal ulong LayoutFingerprint { get; }

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

        var lookupKey = StyleCacheKey.CreateLookup(
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

    private static ulong ComputeLayoutFingerprint(
        IReadOnlyDictionary<string, ElementStyle> styles,
        float minimumInteractiveSize,
        Thickness documentPadding,
        float blockSpacing)
    {
        const ulong offset = 14695981039346656037UL;
        var keys = new List<string>(styles.Keys);
        keys.Sort(StringComparer.Ordinal);

        ulong hash = Mix(offset, BitConverter.SingleToUInt32Bits(minimumInteractiveSize));
        hash = Mix(hash, documentPadding);
        hash = Mix(hash, BitConverter.SingleToUInt32Bits(blockSpacing));
        foreach (string key in keys)
        {
            hash = Mix(hash, key);
            ElementStyle style = styles[key];
            hash = Mix(hash, style.FontFamily);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.FontSize));
            hash = Mix(hash, style.FontWeight.Weight);
            hash = Mix(hash, (uint)style.FontStyle);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.BorderThickness));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.CornerRadius));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.ListIndent));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.NestedListIndent));
            hash = Mix(hash, style.Underline ? 1u : 0u);
            hash = Mix(hash, style.Strikethrough ? 1u : 0u);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(style.LineHeightMultiplier));
            hash = Mix(hash, style.Margin);
            hash = Mix(hash, style.Padding);
        }

        return hash;
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

    private static ulong Mix(ulong hash, string? value)
    {
        value ??= string.Empty;
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= 1099511628211UL;
        }

        // Preserve sequence boundaries ("ab", "c") != ("a", "bc").
        hash ^= 0xff;
        return hash * 1099511628211UL;
    }

    private static ulong Mix(ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong hash, Thickness value)
    {
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(value.Left));
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(value.Top));
        hash = Mix(hash, BitConverter.DoubleToUInt64Bits(value.Right));
        return Mix(hash, BitConverter.DoubleToUInt64Bits(value.Bottom));
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private ElementStyle ResolveStyleCore(
        string elementKey,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys,
        string? language,
        string? state)
    {
        ElementStyle mandatoryStyle = GetBaseStyle(elementKey);
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
            FontWeight = style.FontWeight,
            FontStyle = style.FontStyle,
            Foreground = style.Foreground,
            HoverForeground = style.HoverForeground,
            FocusForeground = style.FocusForeground,
            Background = style.Background,
            AccentBar = style.AccentBar,
            BorderBrush = style.BorderBrush,
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

        const string prefix = "ListDepth";
        int depth = 0;
        for (int i = 0; i < contextKeys.Count; i++)
        {
            string key = contextKeys[i];
            if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(
                    key.AsSpan(prefix.Length),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsed))
            {
                depth = Math.Max(depth, parsed);
            }
        }

        return depth;
    }

    private readonly struct StyleCacheKey : IEquatable<StyleCacheKey>
    {
        private readonly int _hashCode;

        private StyleCacheKey(
            string elementKey,
            IReadOnlyList<string>? contextKeys,
            IReadOnlyList<string>? aliasKeys,
            string? language,
            string? state)
        {
            ElementKey = elementKey;
            ContextKeys = contextKeys;
            AliasKeys = aliasKeys;
            Language = language;
            State = state;
            _hashCode = ComputeHashCode(elementKey, contextKeys, aliasKeys, language, state);
        }

        private string ElementKey { get; }

        private IReadOnlyList<string>? ContextKeys { get; }

        private IReadOnlyList<string>? AliasKeys { get; }

        private string? Language { get; }

        private string? State { get; }

        internal static StyleCacheKey CreateLookup(
            string elementKey,
            IReadOnlyList<string>? contextKeys,
            IReadOnlyList<string>? aliasKeys,
            string? language,
            string? state)
            => new(elementKey, contextKeys, aliasKeys, language, state);

        internal StyleCacheKey CreateStableCopy()
            => new(
                ElementKey,
                CopySequence(ContextKeys),
                CopySequence(AliasKeys),
                Language,
                State);

        public bool Equals(StyleCacheKey other)
            => string.Equals(ElementKey, other.ElementKey, StringComparison.Ordinal) &&
               QualifierEquals(Language, other.Language) &&
               QualifierEquals(State, other.State) &&
               SequenceEqual(ContextKeys, other.ContextKeys) &&
               SequenceEqual(AliasKeys, other.AliasKeys);

        public override bool Equals(object? obj)
            => obj is StyleCacheKey other && Equals(other);

        public override int GetHashCode() => _hashCode;

        private static IReadOnlyList<string>? CopySequence(IReadOnlyList<string>? values)
        {
            if (values is null || values.Count == 0)
                return null;

            var copy = new string[values.Count];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = values[i];
            return copy;
        }

        private static bool SequenceEqual(
            IReadOnlyList<string>? left,
            IReadOnlyList<string>? right)
        {
            int leftCount = left?.Count ?? 0;
            int rightCount = right?.Count ?? 0;
            if (leftCount != rightCount)
                return false;

            for (int i = 0; i < leftCount; i++)
            {
                if (!string.Equals(left![i], right![i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static int ComputeHashCode(
            string elementKey,
            IReadOnlyList<string>? contextKeys,
            IReadOnlyList<string>? aliasKeys,
            string? language,
            string? state)
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(elementKey);
                hash = (hash * 397) ^ GetSequenceHashCode(contextKeys);
                hash = (hash * 397) ^ GetSequenceHashCode(aliasKeys);
                hash = (hash * 397) ^ GetQualifierHashCode(language);
                hash = (hash * 397) ^ GetQualifierHashCode(state);
                return hash;
            }
        }

        private static bool QualifierEquals(string? left, string? right)
            => TrimQualifier(left).Equals(TrimQualifier(right), StringComparison.OrdinalIgnoreCase);

        private static int GetQualifierHashCode(string? value)
        {
            ReadOnlySpan<char> normalized = TrimQualifier(value);
            return normalized.IsEmpty
                ? 0
                : string.GetHashCode(normalized, StringComparison.OrdinalIgnoreCase);
        }

        private static ReadOnlySpan<char> TrimQualifier(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return ReadOnlySpan<char>.Empty;

            ReadOnlySpan<char> span = value.AsSpan();
            int start = 0;
            while (start < span.Length && char.IsWhiteSpace(span[start]))
                start++;

            int end = span.Length;
            while (end > start && char.IsWhiteSpace(span[end - 1]))
                end--;

            return span[start..end];
        }

        private static int GetSequenceHashCode(IReadOnlyList<string>? values)
        {
            int count = values?.Count ?? 0;
            unchecked
            {
                int hash = count;
                for (int i = 0; i < count; i++)
                {
                    string? value = values![i];
                    hash = (hash * 397) ^ (value is null ? 0 : StringComparer.Ordinal.GetHashCode(value));
                }

                return hash;
            }
        }
    }

    private ElementStyle GetBaseStyle(string elementKey)
    {
        if (_styles.TryGetValue(elementKey, out var s)) return s;
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
            FontWeight = ov.FontWeight ?? defaults.FontWeight,
            FontStyle = ov.FontStyle ?? defaults.FontStyle,
            Foreground = ov.Foreground ?? defaults.Foreground,
            HoverForeground = ov.HoverForeground ?? defaults.HoverForeground,
            FocusForeground = ov.FocusForeground ?? defaults.FocusForeground,
            Background = ov.Background ?? defaults.Background,
            AccentBar = ov.AccentBar ?? defaults.AccentBar,
            BorderBrush = ov.BorderBrush ?? defaults.BorderBrush,
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
            FontWeight = customized.FontWeight,
            FontStyle = customized.FontStyle,
            Foreground = mandatory.Foreground,
            HoverForeground = mandatory.HoverForeground,
            FocusForeground = mandatory.FocusForeground,
            Background = mandatory.Background,
            AccentBar = mandatory.AccentBar,
            BorderBrush = mandatory.BorderBrush,
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
