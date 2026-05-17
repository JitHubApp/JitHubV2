using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Renders a GFM pipe table. Each cell is an <see cref="InlineContainerBox"/>
/// so hit-testing, selection, and source-accurate copy all work out of the box.
/// </summary>
internal sealed class TableBox : BlockBox
{
    internal enum CellAlignment
    {
        Default,
        Left,
        Center,
        Right,
    }

    internal readonly record struct CellInfo(InlineContainerBox Box, int Row, int Column, bool IsHeader);

    private readonly MarkdownLayoutContext _context;
    private readonly InlineContainerBox[][] _headerCells;  // [row][col]
    private readonly InlineContainerBox[][] _bodyCells;    // [row][col]
    private readonly CellAlignment[] _columnAlignments;
    private readonly int _colCount;

    private float[]? _colWidths;
    private float[]? _rowHeights;  // header rows first, then body rows

    public TableBox(
        MarkdownLayoutContext context,
        InlineContainerBox[][] headerCells,
        InlineContainerBox[][] bodyCells,
        IReadOnlyList<CellAlignment>? columnAlignments = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _headerCells = headerCells ?? Array.Empty<InlineContainerBox[]>();
        _bodyCells = bodyCells ?? Array.Empty<InlineContainerBox[]>();
        _columnAlignments = columnAlignments is null ? Array.Empty<CellAlignment>() : [.. columnAlignments];
        Margin = GetTableStyle().Margin;

        foreach (var cell in GetCellBoxes())
        {
            cell.DrawContainerChrome = false;
            cell.UseContainerPadding = false;
            cell.UseContainerMargin = false;
            cell.Margin = default;
        }

        if (_headerCells.Length > 0 && _headerCells[0].Length > 0)
            _colCount = _headerCells[0].Length;
        else if (_bodyCells.Length > 0 && _bodyCells[0].Length > 0)
            _colCount = _bodyCells[0].Length;
    }

    public int HeaderRowCount => _headerCells.Length;
    public int BodyRowCount => _bodyCells.Length;
    public int RowCount => _headerCells.Length + _bodyCells.Length;
    public int ColumnCount => _colCount;

    /// <summary>All cell boxes (header rows first, then body rows), left-to-right within each row.</summary>
    public IEnumerable<InlineContainerBox> GetCellBoxes()
    {
        foreach (var row in _headerCells) foreach (var c in row) yield return c;
        foreach (var row in _bodyCells) foreach (var c in row) yield return c;
    }

    /// <summary>All cell boxes with their logical row/column coordinates.</summary>
    public IEnumerable<CellInfo> GetCellInfos()
    {
        for (int r = 0; r < _headerCells.Length; r++)
        {
            for (int c = 0; c < _headerCells[r].Length; c++)
                yield return new CellInfo(_headerCells[r][c], r, c, IsHeader: true);
        }

        for (int r = 0; r < _bodyCells.Length; r++)
        {
            int logicalRow = _headerCells.Length + r;
            for (int c = 0; c < _bodyCells[r].Length; c++)
                yield return new CellInfo(_bodyCells[r][c], logicalRow, c, IsHeader: false);
        }
    }

    public override float Measure(float availableWidth)
    {
        ThrowIfCancellationRequested();
        if (_colCount == 0) { Bounds = new Rect(0, 0, availableWidth, 0); return 0; }

        var tableStyle = GetTableStyle();
        var headerStyle = GetHeaderStyle();
        var bodyStyle = GetBodyStyle();
        var headerPadding = EffectiveCellPadding(headerStyle);
        var bodyPadding = EffectiveCellPadding(bodyStyle);
        Margin = tableStyle.Margin;

        float innerWidth = availableWidth - (float)(Margin.Left + Margin.Right);
        float colWidth = Math.Max(1f, innerWidth / _colCount);

        _colWidths = new float[_colCount];
        for (int i = 0; i < _colCount; i++) _colWidths[i] = colWidth;

        float headerCellMeasureWidth = Math.Max(1f, colWidth - (float)(headerPadding.Left + headerPadding.Right));
        float bodyCellMeasureWidth = Math.Max(1f, colWidth - (float)(bodyPadding.Left + bodyPadding.Right));
        int totalRows = _headerCells.Length + _bodyCells.Length;
        _rowHeights = new float[totalRows];

        for (int r = 0; r < _headerCells.Length; r++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            float maxH = 0;
            for (int c = 0; c < _headerCells[r].Length; c++)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                _headerCells[r][c].TextAlignment = ToCanvasAlignment(GetColumnAlignment(c), _context.FlowDirection == FlowDirection.RightToLeft);
                maxH = Math.Max(maxH, _headerCells[r][c].Measure(headerCellMeasureWidth));
            }
            _rowHeights[r] = maxH + (float)(headerPadding.Top + headerPadding.Bottom);
        }
        for (int r = 0; r < _bodyCells.Length; r++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            float maxH = 0;
            for (int c = 0; c < _bodyCells[r].Length; c++)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                _bodyCells[r][c].TextAlignment = ToCanvasAlignment(GetColumnAlignment(c), _context.FlowDirection == FlowDirection.RightToLeft);
                maxH = Math.Max(maxH, _bodyCells[r][c].Measure(bodyCellMeasureWidth));
            }
            _rowHeights[_headerCells.Length + r] = maxH + (float)(bodyPadding.Top + bodyPadding.Bottom);
        }

        float totalHeight = (float)(Margin.Top + Margin.Bottom);
        foreach (var h in _rowHeights) totalHeight += h;

        Bounds = new Rect(0, 0, availableWidth, totalHeight);
        return totalHeight;
    }

    public override void Arrange(float x, float y, float width)
    {
        base.Arrange(x, y, width);
        if (_colWidths is null || _rowHeights is null) return;

        var headerPadding = EffectiveCellPadding(GetHeaderStyle());
        var bodyPadding = EffectiveCellPadding(GetBodyStyle());
        float colWidth = _colWidths[0];
        float rowY = y + (float)Margin.Top;
        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
        float innerW = (float)(width - Margin.Left - Margin.Right);

        for (int r = 0; r < _headerCells.Length; r++)
        {
            float rh = _rowHeights[r];
            int nCols = _headerCells[r].Length;
            for (int c = 0; c < nCols; c++)
            {
                int visCol = rtl ? (nCols - 1 - c) : c;
                // In RTL, anchor the table to the right edge so the rightmost
                // logical column sits flush with innerW when the total column
                // span is narrower than innerW. Otherwise the table appears
                // flush-left, which is incorrect for RTL.
                float colX = rtl
                    ? x + (float)Margin.Left + innerW - (nCols - visCol) * colWidth
                    : x + (float)Margin.Left + visCol * colWidth;
                _headerCells[r][c].TextAlignment = ToCanvasAlignment(GetColumnAlignment(c), rtl);
                _headerCells[r][c].Arrange(
                    colX + (float)headerPadding.Left,
                    rowY + (float)headerPadding.Top,
                    Math.Max(1f, colWidth - (float)(headerPadding.Left + headerPadding.Right)));
            }
            rowY += rh;
        }
        for (int r = 0; r < _bodyCells.Length; r++)
        {
            float rh = _rowHeights[_headerCells.Length + r];
            int nCols = _bodyCells[r].Length;
            for (int c = 0; c < nCols; c++)
            {
                int visCol = rtl ? (nCols - 1 - c) : c;
                float colX = rtl
                    ? x + (float)Margin.Left + innerW - (nCols - visCol) * colWidth
                    : x + (float)Margin.Left + visCol * colWidth;
                _bodyCells[r][c].TextAlignment = ToCanvasAlignment(GetColumnAlignment(c), rtl);
                _bodyCells[r][c].Arrange(
                    colX + (float)bodyPadding.Left,
                    rowY + (float)bodyPadding.Top,
                    Math.Max(1f, colWidth - (float)(bodyPadding.Left + bodyPadding.Right)));
            }
            rowY += rh;
        }
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (_colWidths is null || _rowHeights is null) return;

        var tableStyle = GetTableStyle();
        var headerStyle = GetHeaderStyle();
        var bodyStyle = GetBodyStyle();
        var bodyBgColor = bodyStyle.Background ?? tableStyle.Background ?? _context.ThemeSnapshot.SurfaceColor;
        var headerBgColor = headerStyle.Background ?? bodyBgColor;
        var borderColor = tableStyle.BorderBrush
            ?? bodyStyle.BorderBrush
            ?? headerStyle.BorderBrush
            ?? WithAlpha(bodyStyle.Foreground, _context.ThemeSnapshot.IsHighContrast ? (byte)0xFF : (byte)0x30);
        float borderThickness = tableStyle.BorderThickness > 0 ? tableStyle.BorderThickness : 1f;
        float radius = Math.Max(0, tableStyle.CornerRadius);

        float startX = (float)(Bounds.X + Margin.Left);
        float innerWidth = (float)(Bounds.Width - Margin.Left - Margin.Right);
        float colWidth = _colWidths[0];
        float headerStartY = (float)(Bounds.Y + Margin.Top);
        float tableH = (float)(Bounds.Height - Margin.Top - Margin.Bottom);
        var tableRect = new Rect(startX, headerStartY, innerWidth, tableH);

        float headerTotalH = 0;
        for (int i = 0; i < _headerCells.Length; i++) headerTotalH += _rowHeights[i];

        if (radius > 0)
        {
            using var clip = CanvasGeometry.CreateRoundedRectangle(_context.ResourceCreator, tableRect, radius, radius);
            using (ds.CreateLayer(1.0f, clip))
                PaintTableSurfaces(ds, tableRect, bodyBgColor, headerBgColor, headerTotalH);
        }
        else
        {
            PaintTableSurfaces(ds, tableRect, bodyBgColor, headerBgColor, headerTotalH);
        }

        // Paint all cell text layouts.
        foreach (var cell in GetCellBoxes())
            cell.Paint(ds, viewport);

        // Separator after header.
        float sepY = headerStartY + headerTotalH;
        if (_headerCells.Length > 0)
        {
            var sep = _context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0xC0);
            ds.DrawLine(startX, sepY, startX + innerWidth, sepY, sep, 1f);
        }

        // Body row separators.
        float rowY = sepY;
        for (int r = 0; r < _bodyCells.Length; r++)
        {
            rowY += _rowHeights[_headerCells.Length + r];
            var rowSep = _context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0x70);
            ds.DrawLine(startX, rowY, startX + innerWidth, rowY, rowSep, 0.5f);
        }

        // Column separators.
        var colSep = _context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0x40);
        float colSepX = startX;
        for (int c = 1; c < _colCount; c++)
        {
            colSepX += colWidth;
            ds.DrawLine(colSepX, headerStartY, colSepX, headerStartY + tableH, colSep, 0.5f);
        }

        if (borderThickness > 0)
        {
            float inset = borderThickness / 2f;
            ds.DrawRoundedRectangle(
                new Rect(
                    tableRect.X + inset,
                    tableRect.Y + inset,
                    Math.Max(0, tableRect.Width - borderThickness),
                    Math.Max(0, tableRect.Height - borderThickness)),
                radius,
                radius,
                borderColor,
                borderThickness);
        }
    }

    private void PaintTableSurfaces(
        CanvasDrawingSession ds,
        Rect tableRect,
        Color bodyBgColor,
        Color headerBgColor,
        float headerTotalH)
    {
        ds.FillRectangle(tableRect, bodyBgColor);

        if (_headerCells.Length > 0 && headerTotalH > 0)
            ds.FillRectangle(new Rect(tableRect.X, tableRect.Y, tableRect.Width, headerTotalH), headerBgColor);

        if (_context.ThemeSnapshot.IsHighContrast)
            return;

        float rowY = (float)(tableRect.Y + headerTotalH);
        var stripe = WithAlpha(GetBodyStyle().Foreground, 0x08);
        for (int r = 0; r < _bodyCells.Length; r++)
        {
            float rowH = _rowHeights![_headerCells.Length + r];
            if (r % 2 == 1)
                ds.FillRectangle(new Rect(tableRect.X, rowY, tableRect.Width, rowH), stripe);
            rowY += rowH;
        }
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
    {
        foreach (var cell in GetCellBoxes())
        {
            if (cell.Bounds.Bottom < viewport.Top || cell.Bounds.Top > viewport.Bottom) continue;
            cell.PaintSelectionForeground(ds, range, color, viewport);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        if (!Bounds.Contains(point))
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return false;
        }
        foreach (var cell in GetCellBoxes())
        {
            if (cell.HitTest(point, out position)) return true;
        }

        if (TryHitTestNearestCell(point, out position))
            return true;

        position = new DocumentPosition(BlockIndex, 0, 0);
        return false;
    }

    private bool TryHitTestNearestCell(Point point, out DocumentPosition position)
    {
        InlineContainerBox? nearest = null;
        double nearestDistance = double.PositiveInfinity;

        foreach (var cell in GetCellBoxes())
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

    internal override void ThrowIfCancellationRequested()
        => _context.CancellationToken.ThrowIfCancellationRequested();

    private ElementStyle GetTableStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Table);

    private ElementStyle GetHeaderStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableHeader);

    private ElementStyle GetBodyStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableCell);

    private static Thickness EffectiveCellPadding(ElementStyle style)
        => IsZero(style.Padding) ? new Thickness(12, 9, 12, 9) : style.Padding;

    private static bool IsZero(Thickness thickness)
        => thickness.Left == 0 &&
           thickness.Top == 0 &&
           thickness.Right == 0 &&
           thickness.Bottom == 0;

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private CellAlignment GetColumnAlignment(int column)
        => column >= 0 && column < _columnAlignments.Length
            ? _columnAlignments[column]
            : CellAlignment.Default;

    private static Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment ToCanvasAlignment(CellAlignment alignment, bool rtl)
        => alignment switch
        {
            CellAlignment.Left => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
            CellAlignment.Center => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            CellAlignment.Right => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Right,
            _ => rtl
                ? Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Right
                : Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
        };

    public override void Dispose()
    {
        foreach (var cell in GetCellBoxes()) cell.Dispose();
    }
}

