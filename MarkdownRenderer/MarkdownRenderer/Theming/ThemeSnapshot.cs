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

    internal ThemeSnapshot(
        IReadOnlyDictionary<string, ElementStyle> styles,
        Color surfaceColor,
        Color selectionHighlightColor,
        Color selectionForegroundColor,
        Color focusVisualColor,
        bool isHighContrast)
    {
        _styles = styles ?? throw new ArgumentNullException(nameof(styles));
        SurfaceColor = surfaceColor;
        SelectionHighlightColor = selectionHighlightColor;
        SelectionForegroundColor = selectionForegroundColor;
        FocusVisualColor = focusVisualColor;
        IsHighContrast = isHighContrast;
    }

    public Color SurfaceColor { get; }
    public Color SelectionHighlightColor { get; }
    public Color SelectionForegroundColor { get; }
    public Color FocusVisualColor { get; }
    public bool IsHighContrast { get; }

    public ElementStyle GetStyle(string elementKey)
    {
        if (_styles.TryGetValue(elementKey, out var s)) return s;
        // Fallback: body style
        if (_styles.TryGetValue(MarkdownElementKeys.Body, out var body)) return body;
        return new ElementStyle();
    }
}
