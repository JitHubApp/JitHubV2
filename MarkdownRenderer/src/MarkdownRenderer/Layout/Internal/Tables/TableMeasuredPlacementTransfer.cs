using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Placement;

namespace MarkdownRenderer.Layout.Internal.Tables;

/// <summary>
/// Captures a live table and its detached private clone before the clone is
/// measured or arranged. The stable table and cell objects remain installed
/// in the source snapshot; only their measured native placements and the
/// table's scalar/array geometry move at commit.
/// </summary>
internal sealed class TableMeasuredPlacementTransfer
{
    private readonly TableBox _source;
    private readonly TableBox _prepared;
    private readonly float _expectedRootWidth;
    private readonly MarkdownLayoutContext _sourceContext;
    private readonly MarkdownLayoutContext _preparedContext;
    private readonly InlineContainerBox[][] _sourceHeaderOuter;
    private readonly InlineContainerBox[][] _sourceBodyOuter;
    private readonly InlineContainerBox[][] _preparedHeaderOuter;
    private readonly InlineContainerBox[][] _preparedBodyOuter;
    private readonly TableMeasuredRowFence[] _rows;
    private readonly TableMeasuredCellFence[] _cells;
    private readonly InlineMeasuredPlacementTransfer[] _cellTransfers;
    private readonly TableBox.CellAlignment[] _sourceAlignments;
    private readonly TableBox.CellAlignment[] _preparedAlignments;
    private readonly TableBox.CellAlignment[] _alignmentValues;
    private readonly float[] _sourceColumnWidths;
    private readonly float[] _sourceRowHeights;
    private readonly double[] _sourceRowBottomEdges;
    private readonly float[] _sourceColumnWidthValues;
    private readonly float[] _sourceRowHeightValues;
    private readonly double[] _sourceRowBottomEdgeValues;
    private readonly Thickness _headerPadding;
    private readonly Thickness _bodyPadding;
    private readonly Rect _sourceBounds;
    private readonly Thickness _sourceMargin;
    private readonly Thickness _sourceRawMargin;
    private readonly double _sourceCollapsedTopMargin;
    private readonly bool _sourceSuppressTopMargin;
    private readonly bool _sourceSuppressBottomMargin;
    private readonly bool _sourceIsDirty;
    private readonly int _sourceBlockIndex;
    private readonly int? _sourceSafeHtmlInlineFlowScopeId;
    private readonly int _sourceColumnCount;
    private readonly int _sourceHeaderRowCount;
    private readonly int _sourceBodyRowCount;
    private readonly float _sourceContentWidth;
    private readonly float _sourceViewportWidth;
    private readonly double _sourceHorizontalOffset;
    private readonly float _sourceArrangedX;
    private readonly float _sourceArrangedY;
    private readonly float _sourceArrangedWidth;
    private readonly bool _sourceHasArrangement;

    internal TableMeasuredPlacementTransfer(
        TableBox source,
        TableBox prepared,
        float expectedRootWidth,
        TableMeasuredRowFence[] rows,
        TableMeasuredCellFence[] cells,
        InlineMeasuredPlacementTransfer[] cellTransfers,
        TableBox.CellAlignment[] alignmentValues,
        Thickness headerPadding,
        Thickness bodyPadding)
    {
        _source = source;
        _prepared = prepared;
        _expectedRootWidth = expectedRootWidth;
        _sourceContext = source.Context;
        _preparedContext = prepared.Context;
        _sourceHeaderOuter = source.HeaderCells;
        _sourceBodyOuter = source.BodyCells;
        _preparedHeaderOuter = prepared.HeaderCells;
        _preparedBodyOuter = prepared.BodyCells;
        _rows = rows;
        _cells = cells;
        _cellTransfers = cellTransfers;
        _sourceAlignments = source.ColumnAlignments;
        _preparedAlignments = prepared.ColumnAlignments;
        _alignmentValues = alignmentValues;
        _sourceColumnWidths = source.ColumnWidths!;
        _sourceRowHeights = source.RowHeights!;
        _sourceRowBottomEdges = source.RowBottomEdges;
        _sourceColumnWidthValues = (float[])_sourceColumnWidths.Clone();
        _sourceRowHeightValues = (float[])_sourceRowHeights.Clone();
        _sourceRowBottomEdgeValues = (double[])_sourceRowBottomEdges.Clone();
        _headerPadding = headerPadding;
        _bodyPadding = bodyPadding;
        _sourceBounds = source.Bounds;
        _sourceMargin = source.Margin;
        _sourceRawMargin = source.RawPlacementMargin;
        _sourceCollapsedTopMargin = source.CollapsedTopMargin;
        _sourceSuppressTopMargin = source.SuppressTopMargin;
        _sourceSuppressBottomMargin = source.SuppressBottomMargin;
        _sourceIsDirty = source.IsDirty;
        _sourceBlockIndex = source.BlockIndex;
        _sourceSafeHtmlInlineFlowScopeId = source.SafeHtmlInlineFlowScopeId;
        _sourceColumnCount = source.ColumnCount;
        _sourceHeaderRowCount = source.HeaderCells.Length;
        _sourceBodyRowCount = source.BodyCells.Length;
        _sourceContentWidth = source.ContentWidth;
        _sourceViewportWidth = source.ViewportWidth;
        _sourceHorizontalOffset = source.HorizontalOffset;
        _sourceArrangedX = source.ArrangedX;
        _sourceArrangedY = source.ArrangedY;
        _sourceArrangedWidth = source.ArrangedWidth;
        _sourceHasArrangement = source.HasArrangement;
    }

    /// <summary>
    /// Cell transfers participate in the snapshot-wide native-layout alias
    /// check. The source and prepared cell identities are preserved when
    /// this table placement is committed.
    /// </summary>
    internal ReadOnlySpan<InlineMeasuredPlacementTransfer> CellTransfers
        => _cellTransfers;

    /// <summary>
    /// Revalidates the table, every row/cell mapping, all old source
    /// geometry, and the complete newly measured candidate. The caller
    /// holds the snapshot's exclusive layout gate. This path reads managed
    /// references and scalars only; all native measurement happened before
    /// this call.
    /// </summary>
    internal bool IsCurrentAndPrepared()
    {
        TableBox source = _source;
        TableBox prepared = _prepared;
        if (!IsCurrentStructure(source, isSource: true) ||
            !IsCurrentStructure(prepared, isSource: false) ||
            !IsCurrentSourceGeometry(source) ||
            !HasCompatiblePlacementContext(source.Context, prepared.Context) ||
            !ReferenceEquals(source.Context, _sourceContext) ||
            !ReferenceEquals(prepared.Context, _preparedContext) ||
            !MatchesCapturedAlignment(source.ColumnAlignments, _sourceAlignments, _alignmentValues) ||
            !MatchesCapturedAlignment(prepared.ColumnAlignments, _preparedAlignments, _alignmentValues) ||
            !SameCapturedMetadata(source, prepared) ||
            !SameSourceMargins(source) ||
            !TryValidatePreparedGeometry(prepared))
        {
            return false;
        }

        for (int index = 0; index < _cells.Length; index++)
        {
            TableMeasuredCellFence cell = _cells[index];
            if (!ReferenceEquals(cell.Transfer.Source, cell.Source) ||
                !ReferenceEquals(cell.Transfer.Prepared, cell.Prepared) ||
                !cell.Transfer.IsCurrentAndPrepared() ||
                !float.IsFinite(cell.Transfer.PreparedAvailableWidth) ||
                cell.Transfer.PreparedAvailableWidth != cell.Prepared.Bounds.Width ||
                !IsCellAtTablePlacement(
                    prepared,
                    cell.Prepared,
                    cell.IsHeader,
                    cell.RowIndex,
                    cell.ColumnIndex,
                    _headerPadding,
                    _bodyPadding))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// No-throw placement commit. All source, shape, width, row geometry,
    /// image-lease, and native-alias fences must have passed immediately
    /// beforehand under the same snapshot gate.
    /// </summary>
    internal void SwapPlacementNoThrow()
    {
        // Keep the cell objects and their runs installed in the source
        // table. The detached cells receive the retired source layouts and
        // become the cleanup owner after this call.
        for (int index = 0; index < _cellTransfers.Length; index++)
            _cellTransfers[index].SwapPlacementNoThrow();

        _source.SwapMeasuredPlacementStateNoThrow(_prepared);
    }

    private bool IsCurrentStructure(TableBox table, bool isSource)
    {
        InlineContainerBox[][] expectedHeaderOuter = isSource ? _sourceHeaderOuter : _preparedHeaderOuter;
        InlineContainerBox[][] expectedBodyOuter = isSource ? _sourceBodyOuter : _preparedBodyOuter;
        TableBox.CellAlignment[] expectedAlignments = isSource ? _sourceAlignments : _preparedAlignments;
        if (!ReferenceEquals(table.HeaderCells, expectedHeaderOuter) ||
            !ReferenceEquals(table.BodyCells, expectedBodyOuter) ||
            table.ColumnCount != _sourceColumnCount ||
            table.HeaderCells.Length != _sourceHeaderRowCount ||
            table.BodyCells.Length != _sourceBodyRowCount ||
            !MatchesCapturedAlignment(table.ColumnAlignments, expectedAlignments, _alignmentValues))
        {
            return false;
        }

        for (int index = 0; index < _rows.Length; index++)
        {
            TableMeasuredRowFence row = _rows[index];
            InlineContainerBox[][] currentRows = row.IsHeader ? table.HeaderCells : table.BodyCells;
            if (row.RowIndex < 0 || row.RowIndex >= currentRows.Length ||
                !ReferenceEquals(currentRows[row.RowIndex], isSource ? row.SourceRow : row.PreparedRow))
            {
                return false;
            }
        }

        for (int index = 0; index < _cells.Length; index++)
        {
            TableMeasuredCellFence cell = _cells[index];
            InlineContainerBox[][] currentRows = cell.IsHeader ? table.HeaderCells : table.BodyCells;
            if (cell.RowIndex < 0 || cell.RowIndex >= currentRows.Length ||
                cell.ColumnIndex < 0 || cell.ColumnIndex >= currentRows[cell.RowIndex].Length ||
                !ReferenceEquals(
                    currentRows[cell.RowIndex][cell.ColumnIndex],
                    isSource ? cell.Source : cell.Prepared))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsCurrentSourceGeometry(TableBox source)
    {
        return !source.IsDirty && source.IsDirty == _sourceIsDirty &&
            SameRect(source.Bounds, _sourceBounds) &&
            ReferenceEquals(source.ColumnWidths, _sourceColumnWidths) &&
            ReferenceEquals(source.RowHeights, _sourceRowHeights) &&
            ReferenceEquals(source.RowBottomEdges, _sourceRowBottomEdges) &&
            SameValues(source.ColumnWidths, _sourceColumnWidthValues) &&
            SameValues(source.RowHeights, _sourceRowHeightValues) &&
            SameValues(source.RowBottomEdges, _sourceRowBottomEdgeValues) &&
            source.ContentWidth == _sourceContentWidth &&
            source.ViewportWidth == _sourceViewportWidth &&
            source.HorizontalOffset == _sourceHorizontalOffset &&
            source.ArrangedX == _sourceArrangedX &&
            source.ArrangedY == _sourceArrangedY &&
            source.ArrangedWidth == _sourceArrangedWidth &&
            source.HasArrangement == _sourceHasArrangement &&
            _sourceHasArrangement &&
            _sourceArrangedWidth == _expectedRootWidth &&
            source.Bounds.Width == _expectedRootWidth &&
            source.Bounds.X == _sourceArrangedX &&
            source.Bounds.Y == _sourceArrangedY &&
            TableMeasuredGeometryFence.IsValidMeasuredGeometry(source, _expectedRootWidth, _headerPadding, _bodyPadding);
    }

    private bool SameSourceMargins(TableBox source)
        => SameThickness(source.Margin, _sourceMargin) &&
           SameThickness(source.RawPlacementMargin, _sourceRawMargin) &&
           SameThickness(_prepared.RawPlacementMargin, _sourceRawMargin) &&
           source.CollapsedTopMargin == _sourceCollapsedTopMargin &&
           source.SuppressTopMargin == _sourceSuppressTopMargin &&
           source.SuppressBottomMargin == _sourceSuppressBottomMargin &&
           SameThickness(_prepared.Margin, _sourceMargin) &&
           _prepared.CollapsedTopMargin == _sourceCollapsedTopMargin &&
           _prepared.SuppressTopMargin == _sourceSuppressTopMargin &&
           _prepared.SuppressBottomMargin == _sourceSuppressBottomMargin;

    private bool SameCapturedMetadata(TableBox source, TableBox prepared)
        => source.BlockIndex == _sourceBlockIndex &&
           prepared.BlockIndex == _sourceBlockIndex &&
           source.SafeHtmlInlineFlowScopeId == _sourceSafeHtmlInlineFlowScopeId &&
           prepared.SafeHtmlInlineFlowScopeId == _sourceSafeHtmlInlineFlowScopeId &&
           source.Bounds.Width == _expectedRootWidth &&
           ReferenceEquals(source.Context, _sourceContext) &&
           ReferenceEquals(prepared.Context, _preparedContext);

    private bool TryValidatePreparedGeometry(TableBox prepared)
    {
        if (prepared.IsDirty || !prepared.HasArrangement ||
            prepared.ColumnWidths is null || prepared.RowHeights is null ||
            prepared.ColumnWidths.Length != _sourceColumnCount ||
            prepared.RowHeights.Length != _sourceHeaderRowCount + _sourceBodyRowCount ||
            prepared.RowBottomEdges.Length != prepared.RowHeights.Length ||
            ReferenceEquals(prepared.ColumnWidths, _sourceColumnWidths) ||
            ReferenceEquals(prepared.RowHeights, _sourceRowHeights) ||
            ReferenceEquals(prepared.ColumnWidths, _sourceRowHeights) ||
            ReferenceEquals(prepared.RowHeights, _sourceColumnWidths) ||
            ReferenceEquals(prepared.ColumnWidths, prepared.RowHeights) ||
            ReferenceEquals(_sourceColumnWidths, _sourceRowHeights) ||
            ReferenceEquals(prepared.ColumnWidths, _sourceRowHeights) ||
            ReferenceEquals(prepared.RowHeights, _sourceColumnWidths) ||
            ReferenceEquals(prepared.RowBottomEdges, _sourceRowBottomEdges) ||
            ReferenceEquals(prepared.HeaderCells, _sourceHeaderOuter) ||
            ReferenceEquals(prepared.BodyCells, _sourceBodyOuter) ||
            !TableMeasuredGeometryFence.IsValidMeasuredGeometry(prepared, _expectedRootWidth, _headerPadding, _bodyPadding) ||
            !IsFiniteNonnegative(prepared.Bounds) ||
            prepared.Bounds.Width != _expectedRootWidth ||
            prepared.Bounds.X != prepared.ArrangedX ||
            prepared.Bounds.Y != prepared.ArrangedY ||
            prepared.ArrangedWidth != _expectedRootWidth ||
            prepared.HorizontalOffset < 0 ||
            prepared.HorizontalOffset > Math.Max(0, prepared.ContentWidth - prepared.ViewportWidth) ||
            prepared.HorizontalOffset != Math.Clamp(
                _sourceHorizontalOffset,
                0,
                Math.Max(0, prepared.ContentWidth - prepared.ViewportWidth)))
        {
            return false;
        }

        for (int index = 0; index < prepared.ColumnWidths.Length; index++)
        {
            float width = prepared.ColumnWidths[index];
            if (!float.IsFinite(width) || width <= 0)
                return false;
        }

        for (int index = 0; index < prepared.RowHeights.Length; index++)
        {
            float height = prepared.RowHeights[index];
            if (!float.IsFinite(height) || height <= 0)
                return false;
        }

        return true;
    }

    private bool IsCellAtTablePlacement(
        TableBox table,
        InlineContainerBox cell,
        bool isHeader,
        int rowIndex,
        int columnIndex,
        Thickness headerPadding,
        Thickness bodyPadding)
    {
        InlineContainerBox[][] rows = isHeader ? table.HeaderCells : table.BodyCells;
        float[]? columnWidths = table.ColumnWidths;
        float[]? rowHeights = table.RowHeights;
        if (columnWidths is null || rowHeights is null || rowIndex < 0 || rowIndex >= rows.Length ||
            columnIndex < 0 || columnIndex >= rows[rowIndex].Length)
        {
            return false;
        }

        int logicalRow = isHeader ? rowIndex : _sourceHeaderRowCount + rowIndex;
        if (logicalRow < 0 || logicalRow >= rowHeights.Length || columnIndex >= columnWidths.Length)
            return false;

        Thickness padding = isHeader ? headerPadding : bodyPadding;
        float innerWidth = Math.Max(1f, columnWidths[columnIndex] - (float)(padding.Left + padding.Right));
        if (!IsFiniteNonnegative(cell.Bounds) || cell.Bounds.Width != innerWidth)
            return false;

        bool rtl = table.Context.FlowDirection == FlowDirection.RightToLeft;
        float viewportWidth = Math.Max(
            1f,
            table.ArrangedWidth - (float)(table.Margin.Left + table.Margin.Right));
        float contentOrigin = rtl
            ? table.ArrangedX + (float)table.Margin.Left + viewportWidth - table.ContentWidth + (float)table.HorizontalOffset
            : table.ArrangedX + (float)table.Margin.Left - (float)table.HorizontalOffset;
        float cellX = contentOrigin + GetColumnStartFor(columnWidths, columnIndex, rtl) + (float)padding.Left;
        float rowY = table.ArrangedY + (float)table.Margin.Top;
        for (int index = 0; index < logicalRow; index++)
            rowY += rowHeights[index];
        float cellY = rowY + (float)padding.Top;
        float paddedCellHeight = (float)cell.Bounds.Height + (float)(padding.Top + padding.Bottom);
        return cell.Bounds.X == cellX && cell.Bounds.Y == cellY &&
            float.IsFinite(paddedCellHeight) && paddedCellHeight >= 0 &&
            paddedCellHeight <= rowHeights[logicalRow];
    }

    private static float GetColumnStartFor(float[] widths, int logicalColumn, bool rtl)
    {
        float start = 0;
        if (rtl)
        {
            for (int column = widths.Length - 1; column > logicalColumn; column--)
                start += widths[column];
        }
        else
        {
            for (int column = 0; column < logicalColumn; column++)
                start += widths[column];
        }
        return start;
    }

    private static bool MatchesCapturedAlignment(
        TableBox.CellAlignment[] current,
        TableBox.CellAlignment[] capturedArray,
        TableBox.CellAlignment[] capturedValues)
    {
        if (!ReferenceEquals(current, capturedArray) || current.Length != capturedValues.Length)
            return false;
        for (int index = 0; index < current.Length; index++)
        {
            if (current[index] != capturedValues[index])
                return false;
        }
        return true;
    }

    private static bool SameThickness(Thickness left, Thickness right)
        => left.Left == right.Left && left.Top == right.Top &&
           left.Right == right.Right && left.Bottom == right.Bottom;

    private static bool IsFiniteNonnegative(Rect rect)
        => double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
           double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
           rect.Width >= 0 && rect.Height >= 0;

    internal static bool SameRect(Rect left, Rect right)
        => left.X == right.X && left.Y == right.Y &&
           left.Width == right.Width && left.Height == right.Height;

    private static bool SameValues(float[]? left, float[] expected)
    {
        if (left is null || left.Length != expected.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] != expected[index])
                return false;
        }
        return true;
    }

    private static bool SameValues(double[] left, double[] expected)
    {
        if (left.Length != expected.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] != expected[index])
                return false;
        }
        return true;
    }

    private static bool HasCompatiblePlacementContext(
        MarkdownLayoutContext source,
        MarkdownLayoutContext target)
        => ReferenceEquals(source.ResourceCreator, target.ResourceCreator) &&
           ReferenceEquals(source.ThemeSnapshot, target.ThemeSnapshot) &&
           ReferenceEquals(source.Registry, target.Registry) &&
           ReferenceEquals(source.Dispatcher, target.Dispatcher) &&
           ReferenceEquals(source.ImageResolver, target.ImageResolver) &&
           ReferenceEquals(source.SvgRenderer, target.SvgRenderer) &&
           ReferenceEquals(source.PerformanceSession, target.PerformanceSession) &&
           ReferenceEquals(source.ImagePreparationOwner, target.ImagePreparationOwner) &&
           ReferenceEquals(source.ImageDocumentSource, target.ImageDocumentSource) &&
           ReferenceEquals(source.SourceMap, target.SourceMap) &&
           source.FlowDirection == target.FlowDirection &&
           source.RasterizationScale == target.RasterizationScale &&
           source.AllowThirdPartyRemoteImages == target.AllowThirdPartyRemoteImages &&
           string.Equals(source.Language, target.Language, StringComparison.Ordinal) &&
           string.Equals(source.ImageDocumentPath, target.ImageDocumentPath, StringComparison.Ordinal) &&
           Equals(source.ImageBaseUri, target.ImageBaseUri) &&
           ReferenceEquals(source.StringProvider, target.StringProvider) &&
           ReferenceEquals(source.CommandProvider, target.CommandProvider) &&
           ReferenceEquals(source.DisclosureStates, target.DisclosureStates);
}
