using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Theming;

/// <summary>
/// An immutable, thread-safe snapshot of resolved <see cref="ElementStyle"/> values
/// captured from <see cref="ThemeResolver"/> on the UI thread before layout work is
/// dispatched to a background thread.
/// </summary>
public sealed class ThemeSnapshot
{
    private readonly IReadOnlyDictionary<string, ElementStyle> _styles;

    internal ThemeSnapshot(IReadOnlyDictionary<string, ElementStyle> styles)
    {
        _styles = styles ?? throw new ArgumentNullException(nameof(styles));
    }

    public ElementStyle GetStyle(string elementKey)
    {
        if (_styles.TryGetValue(elementKey, out var s)) return s;
        // Fallback: body style
        if (_styles.TryGetValue(MarkdownElementKeys.Body, out var body)) return body;
        return new ElementStyle();
    }
}
