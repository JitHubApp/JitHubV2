using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Paints a table: its surfaces, borders and header fill, the visible cells
/// clipped to the horizontal viewport, selection foreground and the
/// horizontal scrollbar.
/// </summary>
internal static class TablePainter
{
    internal static void Paint(TableBox table, CanvasDrawingSession ds, Rect viewport)
    {
        if (table.ColumnWidths is null || table.RowHeights is null) return;

        var tableStyle = table.GetTableStyle();
        var headerStyle = table.GetHeaderStyle();
        var bodyStyle = table.GetBodyStyle();
        var bodyBgColor = bodyStyle.Background ?? tableStyle.Background ?? table.Context.ThemeSnapshot.SurfaceColor;
        var headerBgColor = headerStyle.Background ?? bodyBgColor;
        var borderColor = tableStyle.BorderBrush
            ?? bodyStyle.BorderBrush
            ?? headerStyle.BorderBrush
            ?? WithAlpha(bodyStyle.Foreground, table.Context.ThemeSnapshot.IsHighContrast ? (byte)0xFF : (byte)0x30);
        float borderThickness = tableStyle.BorderThickness > 0 ? tableStyle.BorderThickness : 1f;
        float radius = Math.Max(0, tableStyle.CornerRadius);

        float viewportX = (float)(table.Bounds.X + table.Margin.Left);
        float innerWidth = Math.Max(0, table.ViewportWidth);
        bool rtl = table.Context.FlowDirection == FlowDirection.RightToLeft;
        float contentX = rtl
            ? viewportX + innerWidth - table.ContentWidth + (float)table.HorizontalOffset
            : viewportX - (float)table.HorizontalOffset;
        float headerStartY = (float)(table.Bounds.Y + table.Margin.Top);
        float tableH = TableColumnWidths.Sum(table.RowHeights);
        var viewportRect = new Rect(viewportX, headerStartY, innerWidth, tableH);
        var contentRect = new Rect(contentX, headerStartY, table.ContentWidth, tableH);

        float headerTotalH = 0;
        for (int i = 0; i < table.HeaderCells.Length; i++) headerTotalH += table.RowHeights[i];

        NativePaintSessionResources.LayerLease viewportLayer =
            table.Context.RenderResources.PaintSessions.CreateLayer(ds, 1.0f, viewportRect);
        try
        {
            if (radius > 0)
            {
                PaintRoundedTableContent(
                    table, ds, viewport, viewportRect, radius, contentRect,
                    bodyBgColor, headerBgColor, borderColor, headerTotalH, tableH);
            }
            else
            {
                PaintTableContent(table, ds, viewport, contentRect, bodyBgColor, headerBgColor, borderColor, headerTotalH, tableH);
            }
        }
        catch (Exception primary)
        {
            viewportLayer.ClosePreservingPrimary(primary);
            throw;
        }
        viewportLayer.Close();

        if (borderThickness > 0)
            DrawTableBorder(ds, viewportRect, radius, borderColor, borderThickness);

        PaintHorizontalScrollbar(table, ds, borderColor);
    }

    // Clips the content to the rounded viewport. The clip geometry is attached
    // to its reserved owner before the layer takes it, and the layer closes
    // before Paint closes the outer viewport layer.
    private static void PaintRoundedTableContent(
        TableBox table,
        CanvasDrawingSession ds,
        Rect viewport,
        Rect viewportRect,
        float radius,
        Rect contentRect,
        Color bodyBgColor,
        Color headerBgColor,
        Color borderColor,
        float headerTotalH,
        float tableH)
    {
        NativePaintSessionResources.AuxiliaryLease clipOwner =
            table.Context.RenderResources.PaintSessions.ReserveAuxiliary(ds);
        CanvasGeometry clip;
        try
        {
            clip = CanvasGeometry.CreateRoundedRectangle(table.Context.ResourceCreator, viewportRect, radius, radius);
            clipOwner.Attach(clip);
        }
        catch
        {
            clipOwner.CancelFactory();
            throw;
        }

        NativePaintSessionResources.LayerLease roundedLayer =
            table.Context.RenderResources.PaintSessions.CreateLayer(ds, 1.0f, clip, clipOwner);
        try
        {
            PaintTableContent(table, ds, viewport, contentRect, bodyBgColor, headerBgColor, borderColor, headerTotalH, tableH);
        }
        catch (Exception primary)
        {
            roundedLayer.ClosePreservingPrimary(primary);
            throw;
        }
        roundedLayer.Close();
    }

    private static void DrawTableBorder(
        CanvasDrawingSession ds,
        Rect viewportRect,
        float radius,
        Color borderColor,
        float borderThickness)
    {
        float inset = borderThickness / 2f;
        ds.DrawRoundedRectangle(
            new Rect(
                viewportRect.X + inset,
                viewportRect.Y + inset,
                Math.Max(0, viewportRect.Width - borderThickness),
                Math.Max(0, viewportRect.Height - borderThickness)),
            radius,
            radius,
            borderColor,
            borderThickness);
    }

    private static void PaintTableContent(
        TableBox table,
        CanvasDrawingSession ds,
        Rect viewport,
        Rect contentRect,
        Color bodyBgColor,
        Color headerBgColor,
        Color borderColor,
        float headerTotalH,
        float tableH)
    {
        PaintTableSurfaces(table, ds, viewport, contentRect, bodyBgColor, headerBgColor, headerTotalH);

        int firstVisibleRow = table.FindFirstVisibleRow(viewport.Top);
        for (int row = firstVisibleRow; row < table.RowCount; row++)
        {
            if (table.GetRowTop(row) > viewport.Bottom)
                break;
            foreach (InlineContainerBox cell in table.GetRowCells(row))
                cell.Paint(ds, viewport);
        }

        float sepY = (float)contentRect.Y + headerTotalH;
        if (table.HeaderCells.Length > 0 && sepY >= viewport.Top && sepY <= viewport.Bottom)
        {
            var sep = table.Context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0xC0);
            ds.DrawLine((float)contentRect.Left, sepY, (float)contentRect.Right, sepY, sep, 1f);
        }

        int firstBodyRow = Math.Max(table.HeaderCells.Length, firstVisibleRow);
        for (int logicalRow = firstBodyRow; logicalRow < table.RowCount; logicalRow++)
        {
            float rowY = (float)table.RowBottomEdges[logicalRow];
            if (rowY > viewport.Bottom)
                break;
            if (rowY < viewport.Top)
                continue;
            var rowSep = table.Context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0x70);
            ds.DrawLine((float)contentRect.Left, rowY, (float)contentRect.Right, rowY, rowSep, 0.5f);
        }

        var colSep = table.Context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0x40);
        float colSepX = (float)contentRect.Left;
        bool rtl = table.Context.FlowDirection == FlowDirection.RightToLeft;
        for (int visualColumn = 0; visualColumn < table.ColumnCount - 1; visualColumn++)
        {
            int logicalColumn = rtl ? table.ColumnCount - 1 - visualColumn : visualColumn;
            colSepX += table.GetColumnWidth(logicalColumn);
            ds.DrawLine(colSepX, (float)contentRect.Top, colSepX, (float)contentRect.Top + tableH, colSep, 0.5f);
        }
    }

    private static void PaintTableSurfaces(
        TableBox table,
        CanvasDrawingSession ds,
        Rect viewport,
        Rect tableRect,
        Color bodyBgColor,
        Color headerBgColor,
        float headerTotalH)
    {
        ds.FillRectangle(tableRect, bodyBgColor);

        if (table.HeaderCells.Length > 0 && headerTotalH > 0)
            ds.FillRectangle(new Rect(tableRect.X, tableRect.Y, tableRect.Width, headerTotalH), headerBgColor);

        if (table.Context.ThemeSnapshot.IsHighContrast)
            return;

        var stripe = WithAlpha(table.GetBodyStyle().Foreground, 0x08);
        int firstVisibleRow = Math.Max(table.HeaderCells.Length, table.FindFirstVisibleRow(viewport.Top));
        for (int logicalRow = firstVisibleRow; logicalRow < table.RowCount; logicalRow++)
        {
            float rowY = (float)table.GetRowTop(logicalRow);
            if (rowY > viewport.Bottom)
                break;
            float rowH = table.RowHeights![logicalRow];
            int bodyRow = logicalRow - table.HeaderCells.Length;
            if (bodyRow % 2 == 1)
                ds.FillRectangle(new Rect(tableRect.X, rowY, tableRect.Width, rowH), stripe);
        }
    }

    internal static void PaintSelectionForeground(
        TableBox table,
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
    {
        NativePaintSessionResources.LayerLease clip =
            table.Context.RenderResources.PaintSessions.CreateLayer(ds, 1.0f, table.HorizontalViewportBounds);
        try
        {
            int firstVisibleRow = table.FindFirstVisibleRow(viewport.Top);
            for (int row = firstVisibleRow; row < table.RowCount; row++)
            {
                if (table.GetRowTop(row) > viewport.Bottom)
                    break;
                foreach (InlineContainerBox cell in table.GetRowCells(row))
                    cell.PaintSelectionForeground(ds, range, color, viewport);
            }
        }
        catch (Exception primary)
        {
            clip.ClosePreservingPrimary(primary);
            throw;
        }
        clip.Close();
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static void PaintHorizontalScrollbar(TableBox table, CanvasDrawingSession ds, Color foreground)
    {
        HorizontalOverflowVisual.Paint(
            ds,
            table.HorizontalScrollTrackBounds,
            table.HorizontalScrollThumbBounds,
            table.Context.ThemeSnapshot,
            foreground);
    }
}
