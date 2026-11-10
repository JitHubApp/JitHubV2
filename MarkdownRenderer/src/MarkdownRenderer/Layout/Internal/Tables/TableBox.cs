using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Tables;

/// <summary>
/// Renders a GFM pipe table. Each cell is an <see cref="InlineContainerBox"/>
/// so hit-testing, selection, and source-accurate copy all work out of the box.
/// </summary>
internal sealed class TableBox : BlockBox, IHorizontalOverflowBox
{
    internal const int MaximumPrivateRasterPlacementCells = 256;

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
    internal CellAlignment[] ColumnAlignments => _columnAlignments;
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
    internal float ArrangedX => _arrangedX;
    private float _arrangedY;
    internal float ArrangedY => _arrangedY;
    private float _arrangedWidth;
    internal float ArrangedWidth => _arrangedWidth;
    private bool _hasArrangement;
    internal bool HasArrangement => _hasArrangement;

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
            double height = _rowHeights is null ? 0 : TableColumnWidths.Sum(_rowHeights);
            return new Rect(
                Bounds.X + Margin.Left,
                Bounds.Y + Margin.Top,
                Math.Max(0, _viewportWidth),
                Math.Max(0, height));
        }
    }

    public Rect HorizontalScrollTrackBounds => HorizontalOverflowScrollbar.GetTrackBelowViewport(this);

    public Rect HorizontalScrollThumbBounds => HorizontalOverflowScrollbar.GetThumbBounds(this);

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
        _colWidths = TableColumnWidths.Resolve(
            _viewportWidth,
            TableColumnWidths.MeasureIntrinsic(
                _headerCells, _bodyCells, _colCount, headerPadding, bodyPadding,
                _context.CancellationToken));
        _contentWidth = TableColumnWidths.Sum(_colWidths);
        _horizontalOffset = Math.Clamp(
            _horizontalOffset,
            0,
            Math.Max(0, _contentWidth - _viewportWidth));

        int totalRows = _headerCells.Length + _bodyCells.Length;
        _rowHeights = new float[totalRows];

        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
        MeasureRows(_headerCells, headerPadding, firstRow: 0, isHeader: true, rtl);
        MeasureRows(_bodyCells, bodyPadding, firstRow: _headerCells.Length, isHeader: false, rtl);

        float totalHeight = (float)(Margin.Top + Margin.Bottom);
        foreach (var h in _rowHeights) totalHeight += h;
        if (CanScrollHorizontally)
            totalHeight += HorizontalOverflowScrollbar.Height;

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

        rowY = ArrangeRows(_headerCells, headerPadding, firstRow: 0, isHeader: true, rowY, contentOrigin, rtl);
        _ = ArrangeRows(_bodyCells, bodyPadding, firstRow: _headerCells.Length, isHeader: false, rowY, contentOrigin, rtl);

        RefreshRowBottomEdges();
    }

    private void MeasureRows(
        InlineContainerBox[][] rows,
        Thickness padding,
        int firstRow,
        bool isHeader,
        bool rtl)
    {
        for (int r = 0; r < rows.Length; r++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            float maxH = 0;
            for (int c = 0; c < rows[r].Length; c++)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                rows[r][c].TextAlignment = TableCellTextAlignment.Resolve(
                    _columnAlignments, c, isHeader, rtl, _context);
                float cellMeasureWidth = Math.Max(1f, GetColumnWidth(c) - (float)(padding.Left + padding.Right));
                maxH = Math.Max(maxH, rows[r][c].Measure(cellMeasureWidth));
            }
            _rowHeights![firstRow + r] = maxH + (float)(padding.Top + padding.Bottom);
        }
    }

    // Arranges each row's cells from rowY and returns the y below the last row.
    private float ArrangeRows(
        InlineContainerBox[][] rows,
        Thickness padding,
        int firstRow,
        bool isHeader,
        float rowY,
        float contentOrigin,
        bool rtl)
    {
        for (int r = 0; r < rows.Length; r++)
        {
            float rh = _rowHeights![firstRow + r];
            for (int c = 0; c < rows[r].Length; c++)
            {
                float colWidth = GetColumnWidth(c);
                float colX = contentOrigin + GetColumnStart(c, rtl);
                rows[r][c].TextAlignment = TableCellTextAlignment.Resolve(
                    _columnAlignments, c, isHeader, rtl, _context);
                rows[r][c].Arrange(
                    colX + (float)padding.Left,
                    rowY + (float)padding.Top,
                    Math.Max(1f, colWidth - (float)(padding.Left + padding.Right)));
            }
            rowY += rh;
        }

        return rowY;
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
        => TableHitTesting.HitTest(this, point, out position);

    /// <summary>
    /// Captures a stable source table and a freshly cloned private table before
    /// any candidate measurement or arrangement. Cell placement transfers are
    /// captured in row-major order at the exact source column widths; the
    /// prepared widths may change when its private table is measured.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransfer(
        TableBox prepared,
        float expectedRootWidth,
        out TableMeasuredPlacementTransfer? transfer)
        => TableMeasuredPlacementCapture.TryCaptureMeasuredPlacementTransfer(
            this,
            prepared,
            expectedRootWidth,
            out transfer);

    /// <summary>
    /// Root-local image-completion transfer for a private candidate table.
    /// Candidate vectors are measurement-only and their geometry is remapped
    /// to stable source runs; no candidate table or run is published.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
        TableBox prepared,
        float expectedRootWidth,
        out TableMeasuredPlacementTransfer? transfer)
        => TableMeasuredPlacementCapture.TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
            this,
            prepared,
            expectedRootWidth,
            out transfer);

    /// <summary>
    /// No-throw exchange of this source table's measured column, row, scroll
    /// and arrangement state with its private measured candidate. Called by
    /// <see cref="TableMeasuredPlacementTransfer.SwapPlacementNoThrow"/> after the
    /// cell transfers have swapped, once every fence has succeeded.
    /// </summary>
    /// <param name="prepared">The private measured candidate table.</param>
    internal void SwapMeasuredPlacementStateNoThrow(TableBox prepared)
    {
        TableBox source = this;
        (source._colWidths, prepared._colWidths) = (prepared._colWidths, source._colWidths);
        (source._rowHeights, prepared._rowHeights) = (prepared._rowHeights, source._rowHeights);
        (source._rowBottomEdges, prepared._rowBottomEdges) =
            (prepared._rowBottomEdges, source._rowBottomEdges);
        (source._contentWidth, prepared._contentWidth) = (prepared._contentWidth, source._contentWidth);
        (source._viewportWidth, prepared._viewportWidth) = (prepared._viewportWidth, source._viewportWidth);
        (source._horizontalOffset, prepared._horizontalOffset) =
            (prepared._horizontalOffset, source._horizontalOffset);
        (source._arrangedX, prepared._arrangedX) = (prepared._arrangedX, source._arrangedX);
        (source._arrangedY, prepared._arrangedY) = (prepared._arrangedY, source._arrangedY);
        (source._arrangedWidth, prepared._arrangedWidth) = (prepared._arrangedWidth, source._arrangedWidth);
        (source._hasArrangement, prepared._hasArrangement) = (prepared._hasArrangement, source._hasArrangement);
        (source.Bounds, prepared.Bounds) = (prepared.Bounds, source.Bounds);
    }

    internal override void ThrowIfCancellationRequested()
        => _context.CancellationToken.ThrowIfCancellationRequested();

    internal ElementStyle GetTableStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Table);

    internal ElementStyle GetHeaderStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableHeader);

    internal ElementStyle GetBodyStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableCell);

    internal static Thickness EffectiveCellPadding(ElementStyle style)
        => style.Padding is { Left: 0, Top: 0, Right: 0, Bottom: 0 }
            ? new Thickness(12, 9, 12, 9)
            : style.Padding;

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

    public override void Dispose()
    {
        var cleanup = new BlockBoxCleanupScope();
        cleanup.TryDisposeRows(_headerCells);
        cleanup.TryDisposeRows(_bodyCells);
        cleanup.ThrowIfAny("disposing table cells");
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        var cleanup = new BlockBoxCleanupScope();
        cleanup.TryCollectRetirementTasks(_headerCells, pendingTasks);
        cleanup.TryCollectRetirementTasks(_bodyCells, pendingTasks);
        cleanup.ThrowIfAny("collecting table-cell retirement tasks");
    }
}
