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
internal sealed class TableBox : BlockBox, IHorizontalOverflowBox
{
    private const float HorizontalScrollbarHeight = 12f;
    private const float MinimumScrollbarThumbWidth = 24f;
    private const float MinimumColumnWidth = 48f;
    private const float MaximumPreferredColumnWidth = 720f;

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
    private double[] _rowBottomEdges = Array.Empty<double>();
    private float _contentWidth;
    private float _viewportWidth;
    private double _horizontalOffset;
    private float _arrangedX;
    private float _arrangedY;
    private float _arrangedWidth;
    private bool _hasArrangement;

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

        foreach (var row in _headerCells)
            _colCount = Math.Max(_colCount, row.Length);
        foreach (var row in _bodyCells)
            _colCount = Math.Max(_colCount, row.Length);
    }

    public int HeaderRowCount => _headerCells.Length;
    public int BodyRowCount => _bodyCells.Length;
    public int RowCount => _headerCells.Length + _bodyCells.Length;
    public int ColumnCount => _colCount;
    public double HorizontalOffset => _horizontalOffset;
    public double HorizontalExtent => _contentWidth;
    public double HorizontalViewport => _viewportWidth;
    public bool CanScrollHorizontally => _contentWidth > _viewportWidth + 0.5f;
    public bool IsRightToLeft => _context.FlowDirection == FlowDirection.RightToLeft;

    public Rect HorizontalViewportBounds
    {
        get
        {
            double height = _rowHeights is null ? 0 : Sum(_rowHeights);
            return new Rect(
                Bounds.X + Margin.Left,
                Bounds.Y + Margin.Top,
                Math.Max(0, _viewportWidth),
                Math.Max(0, height));
        }
    }

    public Rect HorizontalScrollTrackBounds
    {
        get
        {
            if (!CanScrollHorizontally)
                return Rect.Empty;

            Rect viewport = HorizontalViewportBounds;
            return new Rect(
                viewport.X,
                viewport.Bottom,
                viewport.Width,
                HorizontalScrollbarHeight);
        }
    }

    public Rect HorizontalScrollThumbBounds
    {
        get
        {
            Rect track = HorizontalScrollTrackBounds;
            if (track.IsEmpty || HorizontalExtent <= 0)
                return Rect.Empty;

            double thumbWidth = Math.Clamp(
                track.Width * HorizontalViewport / HorizontalExtent,
                Math.Min(MinimumScrollbarThumbWidth, track.Width),
                track.Width);
            double travel = Math.Max(0, track.Width - thumbWidth);
            double maximum = Math.Max(0, HorizontalExtent - HorizontalViewport);
            double fraction = maximum <= 0 ? 0 : HorizontalOffset / maximum;
            double x = _context.FlowDirection == FlowDirection.RightToLeft
                ? track.Right - thumbWidth - travel * fraction
                : track.Left + travel * fraction;
            return new Rect(x, track.Y, thumbWidth, track.Height);
        }
    }

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

        _viewportWidth = Math.Max(1f, availableWidth - (float)(Margin.Left + Margin.Right));
        _colWidths = ResolveColumnWidths(
            _viewportWidth,
            MeasureIntrinsicColumns(headerPadding, bodyPadding));
        _contentWidth = Sum(_colWidths);
        _horizontalOffset = Math.Clamp(
            _horizontalOffset,
            0,
            Math.Max(0, _contentWidth - _viewportWidth));

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
                float headerCellMeasureWidth = Math.Max(
                    1f,
                    GetColumnWidth(c) - (float)(headerPadding.Left + headerPadding.Right));
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
                float bodyCellMeasureWidth = Math.Max(
                    1f,
                    GetColumnWidth(c) - (float)(bodyPadding.Left + bodyPadding.Right));
                maxH = Math.Max(maxH, _bodyCells[r][c].Measure(bodyCellMeasureWidth));
            }
            _rowHeights[_headerCells.Length + r] = maxH + (float)(bodyPadding.Top + bodyPadding.Bottom);
        }

        float totalHeight = (float)(Margin.Top + Margin.Bottom);
        foreach (var h in _rowHeights) totalHeight += h;
        if (CanScrollHorizontally)
            totalHeight += HorizontalScrollbarHeight;

        Bounds = new Rect(0, 0, availableWidth, totalHeight);
        return totalHeight;
    }

    public override void Arrange(float x, float y, float width)
    {
        base.Arrange(x, y, width);
        if (_colWidths is null || _rowHeights is null) return;

        _arrangedX = x;
        _arrangedY = y;
        _arrangedWidth = width;
        _hasArrangement = true;
        ArrangeCells();
    }

    private void ArrangeCells()
    {
        if (_colWidths is null || _rowHeights is null || !_hasArrangement)
            return;

        var headerPadding = EffectiveCellPadding(GetHeaderStyle());
        var bodyPadding = EffectiveCellPadding(GetBodyStyle());
        float rowY = _arrangedY + (float)Margin.Top;
        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
        float innerW = Math.Max(1f, _arrangedWidth - (float)(Margin.Left + Margin.Right));
        float contentOrigin = rtl
            ? _arrangedX + (float)Margin.Left + innerW - _contentWidth + (float)_horizontalOffset
            : _arrangedX + (float)Margin.Left - (float)_horizontalOffset;

        for (int r = 0; r < _headerCells.Length; r++)
        {
            float rh = _rowHeights[r];
            int nCols = _headerCells[r].Length;
            for (int c = 0; c < nCols; c++)
            {
                float colWidth = GetColumnWidth(c);
                float colX = contentOrigin + GetColumnStart(c, rtl);
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
                float colWidth = GetColumnWidth(c);
                float colX = contentOrigin + GetColumnStart(c, rtl);
                _bodyCells[r][c].TextAlignment = ToCanvasAlignment(GetColumnAlignment(c), rtl);
                _bodyCells[r][c].Arrange(
                    colX + (float)bodyPadding.Left,
                    rowY + (float)bodyPadding.Top,
                    Math.Max(1f, colWidth - (float)(bodyPadding.Left + bodyPadding.Right)));
            }
            rowY += rh;
        }

        RefreshRowBottomEdges();
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

        float viewportX = (float)(Bounds.X + Margin.Left);
        float innerWidth = Math.Max(0, _viewportWidth);
        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
        float contentX = rtl
            ? viewportX + innerWidth - _contentWidth + (float)_horizontalOffset
            : viewportX - (float)_horizontalOffset;
        float headerStartY = (float)(Bounds.Y + Margin.Top);
        float tableH = Sum(_rowHeights);
        var viewportRect = new Rect(viewportX, headerStartY, innerWidth, tableH);
        var contentRect = new Rect(contentX, headerStartY, _contentWidth, tableH);

        float headerTotalH = 0;
        for (int i = 0; i < _headerCells.Length; i++) headerTotalH += _rowHeights[i];

        using (ds.CreateLayer(1.0f, viewportRect))
        {
            if (radius > 0)
            {
                using var clip = CanvasGeometry.CreateRoundedRectangle(_context.ResourceCreator, viewportRect, radius, radius);
                using (ds.CreateLayer(1.0f, clip))
                    PaintTableContent(ds, viewport, contentRect, bodyBgColor, headerBgColor, borderColor, headerTotalH, tableH);
            }
            else
            {
                PaintTableContent(ds, viewport, contentRect, bodyBgColor, headerBgColor, borderColor, headerTotalH, tableH);
            }
        }

        if (borderThickness > 0)
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

        PaintHorizontalScrollbar(ds, borderColor);
    }

    private void PaintTableContent(
        CanvasDrawingSession ds,
        Rect viewport,
        Rect contentRect,
        Color bodyBgColor,
        Color headerBgColor,
        Color borderColor,
        float headerTotalH,
        float tableH)
    {
        PaintTableSurfaces(ds, viewport, contentRect, bodyBgColor, headerBgColor, headerTotalH);

        int firstVisibleRow = FindFirstVisibleRow(viewport.Top);
        for (int row = firstVisibleRow; row < RowCount; row++)
        {
            if (GetRowTop(row) > viewport.Bottom)
                break;
            foreach (InlineContainerBox cell in GetRowCells(row))
                cell.Paint(ds, viewport);
        }

        float sepY = (float)contentRect.Y + headerTotalH;
        if (_headerCells.Length > 0 && sepY >= viewport.Top && sepY <= viewport.Bottom)
        {
            var sep = _context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0xC0);
            ds.DrawLine((float)contentRect.Left, sepY, (float)contentRect.Right, sepY, sep, 1f);
        }

        int firstBodyRow = Math.Max(_headerCells.Length, firstVisibleRow);
        for (int logicalRow = firstBodyRow; logicalRow < RowCount; logicalRow++)
        {
            float rowY = (float)_rowBottomEdges[logicalRow];
            if (rowY > viewport.Bottom)
                break;
            if (rowY < viewport.Top)
                continue;
            var rowSep = _context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0x70);
            ds.DrawLine((float)contentRect.Left, rowY, (float)contentRect.Right, rowY, rowSep, 0.5f);
        }

        var colSep = _context.ThemeSnapshot.IsHighContrast ? borderColor : WithAlpha(borderColor, 0x40);
        float colSepX = (float)contentRect.Left;
        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
        for (int visualColumn = 0; visualColumn < _colCount - 1; visualColumn++)
        {
            int logicalColumn = rtl ? _colCount - 1 - visualColumn : visualColumn;
            colSepX += GetColumnWidth(logicalColumn);
            ds.DrawLine(colSepX, (float)contentRect.Top, colSepX, (float)contentRect.Top + tableH, colSep, 0.5f);
        }
    }

    private void PaintTableSurfaces(
        CanvasDrawingSession ds,
        Rect viewport,
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

        var stripe = WithAlpha(GetBodyStyle().Foreground, 0x08);
        int firstVisibleRow = Math.Max(_headerCells.Length, FindFirstVisibleRow(viewport.Top));
        for (int logicalRow = firstVisibleRow; logicalRow < RowCount; logicalRow++)
        {
            float rowY = (float)GetRowTop(logicalRow);
            if (rowY > viewport.Bottom)
                break;
            float rowH = _rowHeights![logicalRow];
            int bodyRow = logicalRow - _headerCells.Length;
            if (bodyRow % 2 == 1)
                ds.FillRectangle(new Rect(tableRect.X, rowY, tableRect.Width, rowH), stripe);
        }
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
    {
        using var clip = ds.CreateLayer(1.0f, HorizontalViewportBounds);
        int firstVisibleRow = FindFirstVisibleRow(viewport.Top);
        for (int row = firstVisibleRow; row < RowCount; row++)
        {
            if (GetRowTop(row) > viewport.Bottom)
                break;
            foreach (InlineContainerBox cell in GetRowCells(row))
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
        if (!HorizontalViewportBounds.Contains(point))
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return false;
        }
        int logicalRow = FindFirstVisibleRow(point.Y);
        if (logicalRow < RowCount && GetRowTop(logicalRow) <= point.Y)
        {
            foreach (InlineContainerBox cell in GetRowCells(logicalRow))
            {
                if (cell.HitTest(point, out position)) return true;
            }
        }

        if (TryHitTestNearestCell(point, logicalRow, out position))
            return true;

        position = new DocumentPosition(BlockIndex, 0, 0);
        return false;
    }

    private bool TryHitTestNearestCell(Point point, int logicalRow, out DocumentPosition position)
    {
        InlineContainerBox? nearest = null;
        double nearestDistance = double.PositiveInfinity;

        if (logicalRow < 0 || logicalRow >= RowCount)
        {
            position = default;
            return false;
        }

        foreach (InlineContainerBox cell in GetRowCells(logicalRow))
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

    public bool ScrollHorizontal(double delta)
        => SetHorizontalOffset(HorizontalOffset + delta);

    public bool SetHorizontalOffset(double offset)
    {
        double maximum = Math.Max(0, HorizontalExtent - HorizontalViewport);
        double next = Math.Clamp(offset, 0, maximum);
        if (Math.Abs(next - _horizontalOffset) <= 0.1)
            return false;

        _horizontalOffset = next;
        ArrangeCells();
        return true;
    }

    private int FindFirstVisibleRow(double viewportTop)
        => VerticalViewportIndex.FindFirstIntersecting(_rowBottomEdges, viewportTop);

    internal (int First, int EndExclusive) GetVisibleRowRange(
        double viewportTop,
        double viewportBottom)
    {
        int first = FindFirstVisibleRow(viewportTop);
        int end = first;
        while (end < RowCount && GetRowTop(end) <= viewportBottom)
            end++;
        return (first, end);
    }

    private double GetRowTop(int logicalRow)
        => logicalRow <= 0
            ? _arrangedY + Margin.Top
            : _rowBottomEdges[logicalRow - 1];

    private InlineContainerBox[] GetRowCells(int logicalRow)
        => logicalRow < _headerCells.Length
            ? _headerCells[logicalRow]
            : _bodyCells[logicalRow - _headerCells.Length];

    private void RefreshRowBottomEdges()
    {
        int count = RowCount;
        if (_rowBottomEdges.Length != count)
            _rowBottomEdges = new double[count];

        double bottom = _arrangedY + Margin.Top;
        for (int row = 0; row < count; row++)
        {
            bottom += _rowHeights![row];
            _rowBottomEdges[row] = bottom;
        }
    }

    private (float[] Minimum, float[] Preferred) MeasureIntrinsicColumns(
        Thickness headerPadding,
        Thickness bodyPadding)
    {
        var minimum = new float[_colCount];
        var preferred = new float[_colCount];
        for (int c = 0; c < _colCount; c++)
        {
            minimum[c] = MinimumColumnWidth;
            preferred[c] = MinimumColumnWidth;
        }

        MeasureIntrinsicRows(_headerCells, headerPadding, minimum, preferred);
        MeasureIntrinsicRows(_bodyCells, bodyPadding, minimum, preferred);
        return (minimum, preferred);
    }

    private void MeasureIntrinsicRows(
        InlineContainerBox[][] rows,
        Thickness padding,
        float[] minimum,
        float[] preferred)
    {
        float horizontalPadding = (float)(padding.Left + padding.Right);
        foreach (var row in rows)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(row.Length, _colCount);
            for (int c = 0; c < count; c++)
            {
                IntrinsicWidthMetrics metrics = row[c].MeasureIntrinsicWidths(MaximumPreferredColumnWidth);
                minimum[c] = Math.Max(minimum[c], Math.Min(MaximumPreferredColumnWidth, metrics.Minimum + horizontalPadding));
                preferred[c] = Math.Max(preferred[c], Math.Min(MaximumPreferredColumnWidth, metrics.Preferred + horizontalPadding));
            }
        }
    }

    private static float[] ResolveColumnWidths(
        float availableWidth,
        (float[] Minimum, float[] Preferred) intrinsic)
    {
        int count = intrinsic.Minimum.Length;
        var result = new float[count];
        float minimumTotal = Sum(intrinsic.Minimum);
        float preferredTotal = Sum(intrinsic.Preferred);

        if (preferredTotal <= availableWidth)
        {
            float extra = availableWidth - preferredTotal;
            float weightTotal = Math.Max(1f, preferredTotal);
            for (int c = 0; c < count; c++)
                result[c] = intrinsic.Preferred[c] + extra * intrinsic.Preferred[c] / weightTotal;
            return result;
        }

        if (minimumTotal >= availableWidth)
        {
            Array.Copy(intrinsic.Minimum, result, count);
            return result;
        }

        float distributable = availableWidth - minimumTotal;
        float flexibility = Math.Max(0.001f, preferredTotal - minimumTotal);
        for (int c = 0; c < count; c++)
        {
            float columnFlex = Math.Max(0, intrinsic.Preferred[c] - intrinsic.Minimum[c]);
            result[c] = intrinsic.Minimum[c] + distributable * columnFlex / flexibility;
        }
        return result;
    }

    private float GetColumnWidth(int column)
        => _colWidths is not null && column >= 0 && column < _colWidths.Length
            ? _colWidths[column]
            : 1f;

    private float GetColumnStart(int logicalColumn, bool rtl)
    {
        float start = 0;
        if (rtl)
        {
            for (int c = _colCount - 1; c > logicalColumn; c--)
                start += GetColumnWidth(c);
        }
        else
        {
            for (int c = 0; c < logicalColumn; c++)
                start += GetColumnWidth(c);
        }
        return start;
    }

    private void PaintHorizontalScrollbar(CanvasDrawingSession ds, Color foreground)
    {
        HorizontalOverflowVisual.Paint(
            ds,
            HorizontalScrollTrackBounds,
            HorizontalScrollThumbBounds,
            _context.ThemeSnapshot,
            foreground);
    }

    private static float Sum(float[] values)
    {
        float sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        return sum;
    }

    public override void Dispose()
    {
        foreach (var cell in GetCellBoxes()) cell.Dispose();
    }
}

