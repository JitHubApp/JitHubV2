using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Resolves safe-HTML alignment and converts it to layout alignments.
/// </summary>
internal static class HtmlAlignments
{
    internal static SafeHtmlAlignment EffectiveAlignment(
        SafeHtmlElement element,
        SafeHtmlAlignment inherited)
    {
        if (element.Name == "center")
        {
            return SafeHtmlAlignment.Center;
        }

        SafeHtmlAlignment own = SafeHtmlParser.GetAlignment(element);
        return own == SafeHtmlAlignment.Inherit ? inherited : own;
    }

    internal static CanvasHorizontalAlignment ToCanvasAlignment(SafeHtmlAlignment alignment) => alignment switch
    {
        SafeHtmlAlignment.Center => CanvasHorizontalAlignment.Center,
        SafeHtmlAlignment.Right => CanvasHorizontalAlignment.Right,
        _ => CanvasHorizontalAlignment.Left,
    };

    internal static TableBox.CellAlignment ToCellAlignment(SafeHtmlAlignment alignment) => alignment switch
    {
        SafeHtmlAlignment.Left => TableBox.CellAlignment.Left,
        SafeHtmlAlignment.Center => TableBox.CellAlignment.Center,
        SafeHtmlAlignment.Right => TableBox.CellAlignment.Right,
        _ => TableBox.CellAlignment.Default,
    };
}
