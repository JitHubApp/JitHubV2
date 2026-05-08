using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
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
public sealed class TableBox : BlockBox
{
    private readonly MarkdownLayoutContext _context;
    private readonly InlineContainerBox[][] _headerCells;  // [row][col]
    private readonly InlineContainerBox[][] _bodyCells;    // [row][col]
    private readonly int _colCount;

    private float[]? _colWidths;
    private float[]? _rowHeights;  // header rows first, then body rows

    private const float CellPadH = 8f;
    private const float CellPadV = 6f;

    public TableBox(MarkdownLayoutContext context, InlineContainerBox[][] headerCells, InlineContainerBox[][] bodyCells)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _headerCells = headerCells ?? Array.Empty<InlineContainerBox[]>();
        _bodyCells = bodyCells ?? Array.Empty<InlineContainerBox[]>();
        Margin = new Thickness(0, 6, 0, 6);

        if (_headerCells.Length > 0 && _headerCells[0].Length > 0)
            _colCount = _headerCells[0].Length;
        else if (_bodyCells.Length > 0 && _bodyCells[0].Length > 0)
            _colCount = _bodyCells[0].Length;
    }

    /// <summary>All cell boxes (header rows first, then body rows), left-to-right within each row.</summary>
    public IEnumerable<InlineContainerBox> GetCellBoxes()
    {
        foreach (var row in _headerCells) foreach (var c in row) yield return c;
        foreach (var row in _bodyCells) foreach (var c in row) yield return c;
    }

    public override float Measure(float availableWidth)
    {
        if (_colCount == 0) { Bounds = new Rect(0, 0, availableWidth, 0); return 0; }

        float innerWidth = availableWidth - (float)(Margin.Left + Margin.Right);
        float colWidth = Math.Max(1f, innerWidth / _colCount);

        _colWidths = new float[_colCount];
        for (int i = 0; i < _colCount; i++) _colWidths[i] = colWidth;

        float cellMeasureWidth = Math.Max(1f, colWidth - CellPadH * 2);
        int totalRows = _headerCells.Length + _bodyCells.Length;
        _rowHeights = new float[totalRows];

        for (int r = 0; r < _headerCells.Length; r++)
        {
            float maxH = 0;
            foreach (var cell in _headerCells[r])
                maxH = Math.Max(maxH, cell.Measure(cellMeasureWidth));
            _rowHeights[r] = maxH + CellPadV * 2;
        }
        for (int r = 0; r < _bodyCells.Length; r++)
        {
            float maxH = 0;
            foreach (var cell in _bodyCells[r])
                maxH = Math.Max(maxH, cell.Measure(cellMeasureWidth));
            _rowHeights[_headerCells.Length + r] = maxH + CellPadV * 2;
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

        float colWidth = _colWidths[0];
        float rowY = y + (float)Margin.Top;

        for (int r = 0; r < _headerCells.Length; r++)
        {
            float rh = _rowHeights[r];
            float colX = x + (float)Margin.Left;
            for (int c = 0; c < _headerCells[r].Length; c++)
            {
                _headerCells[r][c].Arrange(colX + CellPadH, rowY + CellPadV, colWidth - CellPadH * 2);
                colX += colWidth;
            }
            rowY += rh;
        }
        for (int r = 0; r < _bodyCells.Length; r++)
        {
            float rh = _rowHeights[_headerCells.Length + r];
            float colX = x + (float)Margin.Left;
            for (int c = 0; c < _bodyCells[r].Length; c++)
            {
                _bodyCells[r][c].Arrange(colX + CellPadH, rowY + CellPadV, colWidth - CellPadH * 2);
                colX += colWidth;
            }
            rowY += rh;
        }
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (_colWidths is null || _rowHeights is null) return;

        var headerStyle = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableHeader);
        var bodyStyle = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableCell);
        var codeBgColor = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.CodeBlock).Background;

        float startX = (float)(Bounds.X + Margin.Left);
        float innerWidth = (float)(Bounds.Width - Margin.Left - Margin.Right);
        float colWidth = _colWidths[0];
        float headerStartY = (float)(Bounds.Y + Margin.Top);

        // Full-width header row background (uses code-block bg as a subtle tint).
        float headerTotalH = 0;
        for (int i = 0; i < _headerCells.Length; i++) headerTotalH += _rowHeights[i];
        if (_headerCells.Length > 0 && codeBgColor is { } hBg)
            ds.FillRectangle(new Rect(startX, headerStartY, innerWidth, headerTotalH), hBg);

        // Paint all cell text layouts.
        foreach (var cell in GetCellBoxes())
            cell.Paint(ds, viewport);

        // Separator after header.
        float sepY = headerStartY + headerTotalH;
        if (_headerCells.Length > 0)
        {
            var sep = Color.FromArgb(100, bodyStyle.Foreground.R, bodyStyle.Foreground.G, bodyStyle.Foreground.B);
            ds.DrawLine(startX, sepY, startX + innerWidth, sepY, sep, 1f);
        }

        // Body row separators.
        float rowY = sepY;
        for (int r = 0; r < _bodyCells.Length; r++)
        {
            rowY += _rowHeights[_headerCells.Length + r];
            var rowSep = Color.FromArgb(30, bodyStyle.Foreground.R, bodyStyle.Foreground.G, bodyStyle.Foreground.B);
            ds.DrawLine(startX, rowY, startX + innerWidth, rowY, rowSep, 0.5f);
        }

        // Column separators.
        float tableH = (float)(Bounds.Height - Margin.Top - Margin.Bottom);
        var colSep = Color.FromArgb(30, bodyStyle.Foreground.R, bodyStyle.Foreground.G, bodyStyle.Foreground.B);
        float colSepX = startX;
        for (int c = 1; c < _colCount; c++)
        {
            colSepX += colWidth;
            ds.DrawLine(colSepX, headerStartY, colSepX, headerStartY + tableH, colSep, 0.5f);
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
        position = new DocumentPosition(BlockIndex, 0, 0);
        return true;
    }

    public override void Dispose()
    {
        foreach (var cell in GetCellBoxes()) cell.Dispose();
    }
}

