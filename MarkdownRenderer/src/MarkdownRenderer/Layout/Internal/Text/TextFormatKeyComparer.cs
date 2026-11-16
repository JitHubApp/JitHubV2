using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Internal.Text;

/// <summary>Ordinal family and case-insensitive locale equality for <see cref="TextFormatKey"/>.</summary>
internal sealed class TextFormatKeyComparer : IEqualityComparer<TextFormatKey>
{
    internal static readonly TextFormatKeyComparer Instance = new();

    public bool Equals(TextFormatKey left, TextFormatKey right) =>
        StringComparer.Ordinal.Equals(left.FontFamily, right.FontFamily) &&
        left.FontSize.Equals(right.FontSize) &&
        left.FontWeight == right.FontWeight &&
        left.FontStyle == right.FontStyle &&
        left.WordWrapping == right.WordWrapping &&
        left.HorizontalAlignment == right.HorizontalAlignment &&
        left.Direction == right.Direction &&
        left.VerticalAlignment == right.VerticalAlignment &&
        left.LineSpacing.Equals(right.LineSpacing) &&
        StringComparer.OrdinalIgnoreCase.Equals(left.LocaleName, right.LocaleName);

    public int GetHashCode(TextFormatKey key) => HashCode.Combine(
        HashCode.Combine(
            key.FontFamily,
            key.FontSize,
            key.FontWeight,
            key.FontStyle,
            key.WordWrapping,
            key.HorizontalAlignment,
            key.Direction,
            key.VerticalAlignment),
        key.LineSpacing,
        StringComparer.OrdinalIgnoreCase.GetHashCode(key.LocaleName));
}
