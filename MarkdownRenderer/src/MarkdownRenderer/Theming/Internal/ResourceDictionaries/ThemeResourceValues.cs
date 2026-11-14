using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace MarkdownRenderer.Theming.Internal.ResourceDictionaries;

/// <summary>Reads typed values out of WinUI resource dictionary entries.</summary>
internal static class ThemeResourceValues
{
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

    internal static bool TryExtractColor(object value, out Color color)
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

    internal static IEnumerable<string> EnumerateStringKeys(ResourceDictionary resources)
    {
        foreach (object key in resources.Keys)
        {
            if (key is string text)
                yield return text;
        }
    }
}
