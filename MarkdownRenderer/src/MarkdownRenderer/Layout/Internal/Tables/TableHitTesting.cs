using System;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Inlines;

namespace MarkdownRenderer.Layout.Internal.Tables;

/// <summary>
/// Hit-tests a table, mapping a point to the nearest cell inside the
/// horizontal viewport.
/// </summary>
internal static class TableHitTesting
{
    internal static bool HitTest(TableBox table, Point point, out DocumentPosition position)
    {
        if (!table.Bounds.Contains(point))
        {
            position = new DocumentPosition(table.BlockIndex, 0, 0);
            return false;
        }
        if (!table.HorizontalViewportBounds.Contains(point))
        {
            position = new DocumentPosition(table.BlockIndex, 0, 0);
            return false;
        }
        int logicalRow = table.FindFirstVisibleRow(point.Y);
        if (logicalRow < table.RowCount && table.GetRowTop(logicalRow) <= point.Y)
        {
            foreach (InlineContainerBox cell in table.GetRowCells(logicalRow))
            {
                if (cell.HitTest(point, out position)) return true;
            }
        }

        if (TryHitTestNearestCell(table, point, logicalRow, out position))
            return true;

        position = new DocumentPosition(table.BlockIndex, 0, 0);
        return false;
    }

    private static bool TryHitTestNearestCell(TableBox table, Point point, int logicalRow, out DocumentPosition position)
    {
        InlineContainerBox? nearest = null;
        double nearestDistance = double.PositiveInfinity;

        if (logicalRow < 0 || logicalRow >= table.RowCount)
        {
            position = default;
            return false;
        }

        foreach (InlineContainerBox cell in table.GetRowCells(logicalRow))
        {
            var r = cell.Bounds;
            if (r.Width <= 0 || r.Height <= 0)
                continue;

            double x = Clamp(point.X, r.Left, r.Right);
            double y = Clamp(point.Y, r.Top, r.Bottom);
            double dx = point.X - x;
            double dy = point.Y - y;
            double distance = dx * dx + dy * dy;
            if (distance < nearestDistance)
            {
                nearest = cell;
                nearestDistance = distance;
            }
        }

        if (nearest is null)
        {
            position = default;
            return false;
        }

        var bounds = nearest.Bounds;
        const double edgeInset = 0.5;
        double left = bounds.Left;
        double right = bounds.Right;
        double top = bounds.Top;
        double bottom = bounds.Bottom;

        if (bounds.Width > edgeInset * 2)
        {
            left += edgeInset;
            right -= edgeInset;
        }

        if (bounds.Height > edgeInset * 2)
        {
            top += edgeInset;
            bottom -= edgeInset;
        }

        var clampedPoint = new Point(
            Clamp(point.X, left, right),
            Clamp(point.Y, top, bottom));
        return nearest.HitTest(clampedPoint, out position);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (min > max)
            return (min + max) / 2.0;
        return Math.Max(min, Math.Min(max, value));
    }
}
