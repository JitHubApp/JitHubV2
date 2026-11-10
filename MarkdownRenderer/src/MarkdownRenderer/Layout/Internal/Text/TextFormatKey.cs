using Microsoft.Graphics.Canvas.Text;
using Windows.UI.Text;

namespace MarkdownRenderer.Layout.Internal.Text;

/// <summary>Normalized identity of one shared CanvasTextFormat descriptor.</summary>
internal readonly struct TextFormatKey
{
    internal TextFormatKey(
        string fontFamily,
        float fontSize,
        FontWeight fontWeight,
        FontStyle fontStyle,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment,
        CanvasTextDirection direction,
        CanvasVerticalAlignment verticalAlignment,
        float lineSpacing,
        string localeName)
    {
        FontFamily = fontFamily;
        FontSize = fontSize;
        FontWeight = fontWeight;
        FontStyle = fontStyle;
        WordWrapping = wordWrapping;
        HorizontalAlignment = horizontalAlignment;
        Direction = direction;
        VerticalAlignment = verticalAlignment;
        LineSpacing = lineSpacing;
        LocaleName = localeName;
    }

    internal readonly string FontFamily;
    internal readonly float FontSize;
    internal readonly FontWeight FontWeight;
    internal readonly FontStyle FontStyle;
    internal readonly CanvasWordWrapping WordWrapping;
    internal readonly CanvasHorizontalAlignment HorizontalAlignment;
    internal readonly CanvasTextDirection Direction;
    internal readonly CanvasVerticalAlignment VerticalAlignment;
    internal readonly float LineSpacing;
    internal readonly string LocaleName;
}
