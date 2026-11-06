using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Maps a table column's authored alignment to the text alignment of one of
/// its cells: explicit left, center and right are kept, a default column
/// follows the reading direction, and a default header column is centered
/// when the theme asks for centered default headers.
/// </summary>
internal static class TableCellTextAlignment
{
    internal static CanvasHorizontalAlignment Resolve(
        TableBox.CellAlignment[] columnAlignments,
        int column,
        bool isHeader,
        bool rightToLeft,
        MarkdownLayoutContext context)
    {
        TableBox.CellAlignment alignment = column >= 0 && column < columnAlignments.Length
            ? columnAlignments[column]
            : TableBox.CellAlignment.Default;
        if (isHeader &&
            alignment == TableBox.CellAlignment.Default &&
            context.ThemeSnapshot.CenterDefaultTableHeaders)
        {
            return CanvasHorizontalAlignment.Center;
        }

        return alignment switch
        {
            TableBox.CellAlignment.Left => CanvasHorizontalAlignment.Left,
            TableBox.CellAlignment.Center => CanvasHorizontalAlignment.Center,
            TableBox.CellAlignment.Right => CanvasHorizontalAlignment.Right,
            _ => rightToLeft
                ? CanvasHorizontalAlignment.Right
                : CanvasHorizontalAlignment.Left,
        };
    }
}
