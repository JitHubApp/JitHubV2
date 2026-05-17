using System;
using System.Collections.Generic;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>
/// An immutable, thread-safe snapshot of resolved <see cref="ElementStyle"/> values
/// captured from <see cref="ThemeResolver"/> on the UI thread before layout work is
/// dispatched to a background thread.
/// </summary>
public sealed class ThemeSnapshot
{
    private readonly IReadOnlyDictionary<string, ElementStyle> _styles;
    private readonly IReadOnlyDictionary<string, ElementStyleOverride> _overrides;

    internal ThemeSnapshot(
        IReadOnlyDictionary<string, ElementStyle> styles,
        IReadOnlyDictionary<string, ElementStyleOverride> overrides,
        Color surfaceColor,
        Color selectionHighlightColor,
        Color selectionForegroundColor,
        Color focusVisualColor,
        bool isHighContrast)
    {
        _styles = styles ?? throw new ArgumentNullException(nameof(styles));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        SurfaceColor = surfaceColor;
        SelectionHighlightColor = selectionHighlightColor;
        SelectionForegroundColor = selectionForegroundColor;
        FocusVisualColor = focusVisualColor;
        IsHighContrast = isHighContrast;
    }

    /// <summary>Resolved document surface color.</summary>
    public Color SurfaceColor { get; }

    /// <summary>Resolved selection highlight color.</summary>
    public Color SelectionHighlightColor { get; }

    /// <summary>Resolved selected-text foreground color.</summary>
    public Color SelectionForegroundColor { get; }

    /// <summary>Resolved keyboard focus visual color.</summary>
    public Color FocusVisualColor { get; }

    /// <summary>Gets whether the snapshot was captured in a high-contrast theme.</summary>
    public bool IsHighContrast { get; }

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
    {
        var style = GetBaseStyle(elementKey);
        style = ApplyOverride(style, elementKey);

        if (contextKeys is not null)
        {
            for (int i = 0; i < contextKeys.Count; i++)
            {
                var contextKey = contextKeys[i];
                style = ApplyOverride(style, MarkdownElementKeys.Context(contextKey, elementKey));
            }
        }

        if (aliasKeys is not null)
        {
            for (int i = 0; i < aliasKeys.Count; i++)
            {
                var aliasKey = aliasKeys[i];
                style = ApplyOverride(style, aliasKey);

                if (contextKeys is not null)
                {
                    for (int c = 0; c < contextKeys.Count; c++)
                        style = ApplyOverride(style, MarkdownElementKeys.Context(contextKeys[c], aliasKey));
                }
            }
        }

        return style;
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
}
