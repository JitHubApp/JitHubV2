using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
internal sealed partial class TableBox : BlockBox, IHorizontalOverflowBox
{
    internal const int MaximumPrivateRasterPlacementCells = 256;
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

    internal readonly struct CellInfo
    {
        public CellInfo(InlineContainerBox Box, int Row, int Column, bool IsHeader)
        {
            this.Box = Box;
            this.Row = Row;
            this.Column = Column;
            this.IsHeader = IsHeader;
        }

        public readonly InlineContainerBox Box;
        public readonly int Row;
        public readonly int Column;
        public readonly bool IsHeader;
    }

    private readonly MarkdownLayoutContext _context;
    private readonly InlineContainerBox[][] _headerCells;  // [row][col]
    internal InlineContainerBox[][] HeaderCells => _headerCells;
    private readonly InlineContainerBox[][] _bodyCells;    // [row][col]
    internal InlineContainerBox[][] BodyCells => _bodyCells;
    private readonly CellAlignment[] _columnAlignments;
    private readonly int _colCount;

    private float[]? _colWidths;
    internal float[]? ColumnWidths => _colWidths;
    private float[]? _rowHeights;  // header rows first, then body rows
    internal float[]? RowHeights => _rowHeights;
    private double[] _rowBottomEdges = Array.Empty<double>();
    internal double[] RowBottomEdges => _rowBottomEdges;
    private float _contentWidth;
    internal float ContentWidth => _contentWidth;
    private float _viewportWidth;
    internal float ViewportWidth => _viewportWidth;
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
    internal MarkdownLayoutContext Context => _context;
    public double HorizontalOffset => _horizontalOffset;
    public double HorizontalExtent => _contentWidth;
    public double HorizontalViewport => _viewportWidth;
    public bool CanScrollHorizontally => _contentWidth > _viewportWidth + 0.5f;
    public bool IsRightToLeft => _context.FlowDirection == FlowDirection.RightToLeft;

    /// <summary>
    /// Builds a fresh table shell around cell roots cloned by the bounded
    /// private-placement visitor. The map must be an exact one-to-one mapping
    /// for this table; ownership of the cloned cells transfers only on success.
    /// No source measurement, arrangement, or mutable geometry is reused.
    /// </summary>
    internal bool TryCreatePrivateRasterPlacementClone(
        MarkdownLayoutContext targetContext,
        IReadOnlyDictionary<InlineContainerBox, InlineContainerBox> clonedCells,
        out TableBox? clone)
        => TablePrivatePlacementCloner.TryCreatePrivateRasterPlacementClone(
            this,
            targetContext,
            clonedCells,
            out clone);

    /// <summary>
    /// Creates the unmeasured private placement table around already cloned
    /// cell rows, carrying this table's identity, scroll offset and margins.
    /// </summary>
    internal TableBox CreatePrivatePlacementShell(
        MarkdownLayoutContext targetContext,
        InlineContainerBox[][] header,
        InlineContainerBox[][] body)
    {
        var candidate = new TableBox(targetContext, header, body, _columnAlignments)
        {
            BlockIndex = BlockIndex,
            SafeHtmlInlineFlowScopeId = SafeHtmlInlineFlowScopeId,
            _horizontalOffset = _horizontalOffset,
        };
        candidate.CopyPlacementMarginsFrom(this);
        return candidate;
    }

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
                _headerCells[r][c].TextAlignment = GetHeaderTextAlignment(c);
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
                _headerCells[r][c].TextAlignment = GetHeaderTextAlignment(c, rtl);
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
        => TablePainter.Paint(this, ds, viewport);

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
        => TablePainter.PaintSelectionForeground(this, ds, range, color, viewport);

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

    internal ElementStyle GetTableStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Table);

    internal ElementStyle GetHeaderStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableHeader);

    internal ElementStyle GetBodyStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableCell);

    private static Thickness EffectiveCellPadding(ElementStyle style)
        => IsZero(style.Padding) ? new Thickness(12, 9, 12, 9) : style.Padding;

    private static bool IsZero(Thickness thickness)
        => thickness.Left == 0 &&
           thickness.Top == 0 &&
           thickness.Right == 0 &&
           thickness.Bottom == 0;

    private CellAlignment GetColumnAlignment(int column)
        => column >= 0 && column < _columnAlignments.Length
            ? _columnAlignments[column]
            : CellAlignment.Default;

    private Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment GetHeaderTextAlignment(int column)
        => GetHeaderTextAlignment(column, _context.FlowDirection == FlowDirection.RightToLeft);

    private Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment GetHeaderTextAlignment(int column, bool rightToLeft)
    {
        CellAlignment alignment = GetColumnAlignment(column);
        if (alignment == CellAlignment.Default && _context.ThemeSnapshot.CenterDefaultTableHeaders)
            return Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center;

        return ToCanvasAlignment(alignment, rightToLeft);
    }

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

    internal int FindFirstVisibleRow(double viewportTop)
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

    internal IEnumerable<InlineContainerBox> GetCellBoxesInVerticalBand(
        double viewportTop,
        double viewportBottom)
    {
        (int first, int endExclusive) = GetVisibleRowRange(viewportTop, viewportBottom);
        for (int row = first; row < endExclusive; row++)
        {
            foreach (InlineContainerBox cell in GetRowCells(row))
                yield return cell;
        }
    }

    internal double GetRowTop(int logicalRow)
        => logicalRow <= 0
            ? _arrangedY + Margin.Top
            : _rowBottomEdges[logicalRow - 1];

    internal InlineContainerBox[] GetRowCells(int logicalRow)
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

    internal float GetColumnWidth(int column)
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

    internal static float Sum(float[] values)
    {
        float sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        return sum;
    }

    public override void Dispose()
    {
        var cleanup = new BlockBoxCleanupScope();
        DisposeCells(_headerCells, ref cleanup);
        DisposeCells(_bodyCells, ref cleanup);
        cleanup.ThrowIfAny("disposing table cells");
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        var cleanup = new BlockBoxCleanupScope();
        CollectCellRetirementTasks(_headerCells, pendingTasks, ref cleanup);
        CollectCellRetirementTasks(_bodyCells, pendingTasks, ref cleanup);
        cleanup.ThrowIfAny("collecting table-cell retirement tasks");
    }

    private static void DisposeCells(InlineContainerBox[][] rows, ref BlockBoxCleanupScope cleanup)
    {
        for (int row = 0; row < rows.Length; row++)
        {
            InlineContainerBox[] cells = rows[row];
            for (int column = 0; column < cells.Length; column++)
                cleanup.TryDispose(cells[column]);
        }
    }

    private static void CollectCellRetirementTasks(
        InlineContainerBox[][] rows,
        List<Task> pendingTasks,
        ref BlockBoxCleanupScope cleanup)
    {
        for (int row = 0; row < rows.Length; row++)
        {
            InlineContainerBox[] cells = rows[row];
            for (int column = 0; column < cells.Length; column++)
                cleanup.TryCollectRetirementTasks(cells[column], pendingTasks);
        }
    }
}

