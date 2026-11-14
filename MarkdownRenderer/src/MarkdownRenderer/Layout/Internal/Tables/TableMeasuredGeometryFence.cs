using System;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Layout.Internal.Boxes;

namespace MarkdownRenderer.Layout.Internal.Tables;

/// <summary>
/// Checks that a table's measured and arranged geometry is complete and
/// self-consistent at an expected root width: finite margins and padding,
/// column widths that sum to the content width, row heights that sum to the
/// bounds, and row bottom edges that follow from the arranged origin.
/// </summary>
internal static class TableMeasuredGeometryFence
{
    internal static bool IsValidMeasuredGeometry(
        TableBox table,
        float expectedWidth,
        Thickness headerPadding,
        Thickness bodyPadding)
    {
        if (!float.IsFinite(expectedWidth) || expectedWidth <= 0 ||
            !IsFiniteThickness(table.Margin) ||
            !double.IsFinite(table.CollapsedTopMargin) ||
            !float.IsFinite(table.ContentWidth) || table.ContentWidth <= 0 ||
            !float.IsFinite(table.ViewportWidth) || table.ViewportWidth <= 0 ||
            !float.IsFinite(table.ArrangedX) || !float.IsFinite(table.ArrangedY) ||
            !float.IsFinite(table.ArrangedWidth) || table.ArrangedWidth != expectedWidth ||
            table.ColumnWidths is null || table.RowHeights is null ||
            table.ColumnWidths.Length != table.ColumnCount ||
            table.RowHeights.Length != table.RowCount ||
            table.RowBottomEdges.Length != table.RowCount ||
            !IsFiniteThickness(headerPadding) || !IsFiniteThickness(bodyPadding))
        {
            return false;
        }

        float expectedViewportWidth = Math.Max(
            1f,
            expectedWidth - (float)(table.Margin.Left + table.Margin.Right));
        if (table.ViewportWidth != expectedViewportWidth ||
            table.ContentWidth != Sum(table.ColumnWidths))
        {
            return false;
        }

        float totalHeight = (float)(table.Margin.Top + table.Margin.Bottom);
        for (int index = 0; index < table.RowHeights.Length; index++)
        {
            float height = table.RowHeights[index];
            if (!float.IsFinite(height) || height <= 0)
                return false;
            totalHeight += height;
        }

        if (table.ContentWidth > table.ViewportWidth + 0.5f)
            totalHeight += HorizontalOverflowScrollbar.Height;

        if (!float.IsFinite(totalHeight) || table.Bounds.Height != totalHeight)
            return false;

        double bottom = table.ArrangedY + table.Margin.Top;
        for (int index = 0; index < table.RowHeights.Length; index++)
        {
            bottom += table.RowHeights[index];
            if (!double.IsFinite(bottom) || table.RowBottomEdges[index] != bottom)
                return false;
        }

        return true;
    }

    private static bool IsFiniteThickness(Thickness value)
        => double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
           double.IsFinite(value.Right) && double.IsFinite(value.Bottom);

    private static float Sum(float[] values)
    {
        float sum = 0;
        for (int index = 0; index < values.Length; index++)
            sum += values[index];
        return sum;
    }
}
