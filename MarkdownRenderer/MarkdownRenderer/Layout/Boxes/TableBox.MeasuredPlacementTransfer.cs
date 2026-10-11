using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class TableBox
{
    /// <summary>
    /// Captures a live table and its detached private clone before the clone is
    /// measured or arranged. The stable table and cell objects remain installed
    /// in the source snapshot; only their measured native placements and the
    /// table's scalar/array geometry move at commit.
    /// </summary>
    internal sealed class MeasuredPlacementTransfer
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
        private readonly RowFence[] _rows;
        private readonly CellFence[] _cells;
        private readonly InlineContainerBox.MeasuredPlacementTransfer[] _cellTransfers;
        private readonly CellAlignment[] _sourceAlignments;
        private readonly CellAlignment[] _preparedAlignments;
        private readonly CellAlignment[] _alignmentValues;
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

        internal MeasuredPlacementTransfer(
            TableBox source,
            TableBox prepared,
            float expectedRootWidth,
            RowFence[] rows,
            CellFence[] cells,
            InlineContainerBox.MeasuredPlacementTransfer[] cellTransfers,
            CellAlignment[] alignmentValues,
            Thickness headerPadding,
            Thickness bodyPadding)
        {
            _source = source;
            _prepared = prepared;
            _expectedRootWidth = expectedRootWidth;
            _sourceContext = source._context;
            _preparedContext = prepared._context;
            _sourceHeaderOuter = source._headerCells;
            _sourceBodyOuter = source._bodyCells;
            _preparedHeaderOuter = prepared._headerCells;
            _preparedBodyOuter = prepared._bodyCells;
            _rows = rows;
            _cells = cells;
            _cellTransfers = cellTransfers;
            _sourceAlignments = source._columnAlignments;
            _preparedAlignments = prepared._columnAlignments;
            _alignmentValues = alignmentValues;
            _sourceColumnWidths = source._colWidths!;
            _sourceRowHeights = source._rowHeights!;
            _sourceRowBottomEdges = source._rowBottomEdges;
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
            _sourceColumnCount = source._colCount;
            _sourceHeaderRowCount = source._headerCells.Length;
            _sourceBodyRowCount = source._bodyCells.Length;
            _sourceContentWidth = source._contentWidth;
            _sourceViewportWidth = source._viewportWidth;
            _sourceHorizontalOffset = source._horizontalOffset;
            _sourceArrangedX = source._arrangedX;
            _sourceArrangedY = source._arrangedY;
            _sourceArrangedWidth = source._arrangedWidth;
            _sourceHasArrangement = source._hasArrangement;
        }

        /// <summary>
        /// Cell transfers participate in the snapshot-wide native-layout alias
        /// check. The source and prepared cell identities are preserved when
        /// this table placement is committed.
        /// </summary>
        internal ReadOnlySpan<InlineContainerBox.MeasuredPlacementTransfer> CellTransfers
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
            if (!IsCurrentSourceStructure(source) ||
                !IsCurrentPreparedStructure(prepared) ||
                !IsCurrentSourceGeometry(source) ||
                !HasCompatiblePlacementContext(source._context, prepared._context) ||
                !ReferenceEquals(source._context, _sourceContext) ||
                !ReferenceEquals(prepared._context, _preparedContext) ||
                !MatchesCapturedAlignment(source._columnAlignments, _sourceAlignments, _alignmentValues) ||
                !MatchesCapturedAlignment(prepared._columnAlignments, _preparedAlignments, _alignmentValues) ||
                !SameCapturedMetadata(source, prepared) ||
                !SameSourceMargins(source) ||
                !TryValidatePreparedGeometry(prepared))
            {
                return false;
            }

            for (int index = 0; index < _cells.Length; index++)
            {
                CellFence cell = _cells[index];
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
            TableBox source = _source;
            TableBox prepared = _prepared;

            // Keep the cell objects and their runs installed in the source
            // table. The detached cells receive the retired source layouts and
            // become the cleanup owner after this call.
            for (int index = 0; index < _cellTransfers.Length; index++)
                _cellTransfers[index].SwapPlacementNoThrow();

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

        private bool IsCurrentSourceStructure(TableBox source)
        {
            if (!ReferenceEquals(source._headerCells, _sourceHeaderOuter) ||
                !ReferenceEquals(source._bodyCells, _sourceBodyOuter) ||
                source._colCount != _sourceColumnCount ||
                source._headerCells.Length != _sourceHeaderRowCount ||
                source._bodyCells.Length != _sourceBodyRowCount ||
                !MatchesCapturedAlignment(source._columnAlignments, _sourceAlignments, _alignmentValues))
            {
                return false;
            }

            for (int index = 0; index < _rows.Length; index++)
            {
                RowFence row = _rows[index];
                InlineContainerBox[][] currentRows = row.IsHeader ? source._headerCells : source._bodyCells;
                if (row.RowIndex < 0 || row.RowIndex >= currentRows.Length ||
                    !ReferenceEquals(currentRows[row.RowIndex], row.SourceRow))
                {
                    return false;
                }
            }

            for (int index = 0; index < _cells.Length; index++)
            {
                CellFence cell = _cells[index];
                InlineContainerBox[][] currentRows = cell.IsHeader ? source._headerCells : source._bodyCells;
                if (cell.RowIndex < 0 || cell.RowIndex >= currentRows.Length ||
                    cell.ColumnIndex < 0 || cell.ColumnIndex >= currentRows[cell.RowIndex].Length ||
                    !ReferenceEquals(currentRows[cell.RowIndex][cell.ColumnIndex], cell.Source))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsCurrentPreparedStructure(TableBox prepared)
        {
            if (!ReferenceEquals(prepared._headerCells, _preparedHeaderOuter) ||
                !ReferenceEquals(prepared._bodyCells, _preparedBodyOuter) ||
                prepared._colCount != _sourceColumnCount ||
                prepared._headerCells.Length != _sourceHeaderRowCount ||
                prepared._bodyCells.Length != _sourceBodyRowCount ||
                !MatchesCapturedAlignment(prepared._columnAlignments, _preparedAlignments, _alignmentValues))
            {
                return false;
            }

            for (int index = 0; index < _rows.Length; index++)
            {
                RowFence row = _rows[index];
                InlineContainerBox[][] currentRows = row.IsHeader ? prepared._headerCells : prepared._bodyCells;
                if (row.RowIndex < 0 || row.RowIndex >= currentRows.Length ||
                    !ReferenceEquals(currentRows[row.RowIndex], row.PreparedRow))
                {
                    return false;
                }
            }

            for (int index = 0; index < _cells.Length; index++)
            {
                CellFence cell = _cells[index];
                InlineContainerBox[][] currentRows = cell.IsHeader ? prepared._headerCells : prepared._bodyCells;
                if (cell.RowIndex < 0 || cell.RowIndex >= currentRows.Length ||
                    cell.ColumnIndex < 0 || cell.ColumnIndex >= currentRows[cell.RowIndex].Length ||
                    !ReferenceEquals(currentRows[cell.RowIndex][cell.ColumnIndex], cell.Prepared))
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
                ReferenceEquals(source._colWidths, _sourceColumnWidths) &&
                ReferenceEquals(source._rowHeights, _sourceRowHeights) &&
                ReferenceEquals(source._rowBottomEdges, _sourceRowBottomEdges) &&
                SameValues(source._colWidths, _sourceColumnWidthValues) &&
                SameValues(source._rowHeights, _sourceRowHeightValues) &&
                SameValues(source._rowBottomEdges, _sourceRowBottomEdgeValues) &&
                source._contentWidth == _sourceContentWidth &&
                source._viewportWidth == _sourceViewportWidth &&
                source._horizontalOffset == _sourceHorizontalOffset &&
                source._arrangedX == _sourceArrangedX &&
                source._arrangedY == _sourceArrangedY &&
                source._arrangedWidth == _sourceArrangedWidth &&
                source._hasArrangement == _sourceHasArrangement &&
                _sourceHasArrangement &&
                _sourceArrangedWidth == _expectedRootWidth &&
                source.Bounds.Width == _expectedRootWidth &&
                source.Bounds.X == _sourceArrangedX &&
                source.Bounds.Y == _sourceArrangedY &&
                IsValidMeasuredGeometry(source, _expectedRootWidth, _headerPadding, _bodyPadding);
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
               ReferenceEquals(source._context, _sourceContext) &&
               ReferenceEquals(prepared._context, _preparedContext);

        private bool TryValidatePreparedGeometry(TableBox prepared)
        {
            if (prepared.IsDirty || !prepared._hasArrangement ||
                prepared._colWidths is null || prepared._rowHeights is null ||
                prepared._colWidths.Length != _sourceColumnCount ||
                prepared._rowHeights.Length != _sourceHeaderRowCount + _sourceBodyRowCount ||
                prepared._rowBottomEdges.Length != prepared._rowHeights.Length ||
                ReferenceEquals(prepared._colWidths, _sourceColumnWidths) ||
                ReferenceEquals(prepared._rowHeights, _sourceRowHeights) ||
                ReferenceEquals(prepared._colWidths, _sourceRowHeights) ||
                ReferenceEquals(prepared._rowHeights, _sourceColumnWidths) ||
                ReferenceEquals(prepared._colWidths, prepared._rowHeights) ||
                ReferenceEquals(_sourceColumnWidths, _sourceRowHeights) ||
                ReferenceEquals(prepared._colWidths, _sourceRowHeights) ||
                ReferenceEquals(prepared._rowHeights, _sourceColumnWidths) ||
                ReferenceEquals(prepared._rowBottomEdges, _sourceRowBottomEdges) ||
                ReferenceEquals(prepared._headerCells, _sourceHeaderOuter) ||
                ReferenceEquals(prepared._bodyCells, _sourceBodyOuter) ||
                !IsValidMeasuredGeometry(prepared, _expectedRootWidth, _headerPadding, _bodyPadding) ||
                !IsFiniteNonnegative(prepared.Bounds) ||
                prepared.Bounds.Width != _expectedRootWidth ||
                prepared.Bounds.X != prepared._arrangedX ||
                prepared.Bounds.Y != prepared._arrangedY ||
                prepared._arrangedWidth != _expectedRootWidth ||
                prepared._horizontalOffset < 0 ||
                prepared._horizontalOffset > Math.Max(0, prepared._contentWidth - prepared._viewportWidth) ||
                prepared._horizontalOffset != Math.Clamp(
                    _sourceHorizontalOffset,
                    0,
                    Math.Max(0, prepared._contentWidth - prepared._viewportWidth)))
            {
                return false;
            }

            for (int index = 0; index < prepared._colWidths.Length; index++)
            {
                float width = prepared._colWidths[index];
                if (!float.IsFinite(width) || width <= 0)
                    return false;
            }

            for (int index = 0; index < prepared._rowHeights.Length; index++)
            {
                float height = prepared._rowHeights[index];
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
            InlineContainerBox[][] rows = isHeader ? table._headerCells : table._bodyCells;
            float[]? columnWidths = table._colWidths;
            float[]? rowHeights = table._rowHeights;
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

            bool rtl = table._context.FlowDirection == FlowDirection.RightToLeft;
            float viewportWidth = Math.Max(
                1f,
                table._arrangedWidth - (float)(table.Margin.Left + table.Margin.Right));
            float contentOrigin = rtl
                ? table._arrangedX + (float)table.Margin.Left + viewportWidth - table._contentWidth + (float)table._horizontalOffset
                : table._arrangedX + (float)table.Margin.Left - (float)table._horizontalOffset;
            float cellX = contentOrigin + GetColumnStartFor(columnWidths, columnIndex, rtl) + (float)padding.Left;
            float rowY = table._arrangedY + (float)table.Margin.Top;
            for (int index = 0; index < logicalRow; index++)
                rowY += rowHeights[index];
            float cellY = rowY + (float)padding.Top;
            float paddedCellHeight = (float)cell.Bounds.Height + (float)(padding.Top + padding.Bottom);
            return cell.Bounds.X == cellX && cell.Bounds.Y == cellY &&
                float.IsFinite(paddedCellHeight) && paddedCellHeight >= 0 &&
                paddedCellHeight <= rowHeights[logicalRow];
        }

        internal static bool IsValidMeasuredGeometry(
            TableBox table,
            float expectedWidth,
            Thickness headerPadding,
            Thickness bodyPadding)
        {
            if (!float.IsFinite(expectedWidth) || expectedWidth <= 0 ||
                !IsFiniteThickness(table.Margin) ||
                !double.IsFinite(table.CollapsedTopMargin) ||
                !float.IsFinite(table._contentWidth) || table._contentWidth <= 0 ||
                !float.IsFinite(table._viewportWidth) || table._viewportWidth <= 0 ||
                !float.IsFinite(table._arrangedX) || !float.IsFinite(table._arrangedY) ||
                !float.IsFinite(table._arrangedWidth) || table._arrangedWidth != expectedWidth ||
                table._colWidths is null || table._rowHeights is null ||
                table._colWidths.Length != table._colCount ||
                table._rowHeights.Length != table.RowCount ||
                table._rowBottomEdges.Length != table.RowCount ||
                !IsFiniteThickness(headerPadding) || !IsFiniteThickness(bodyPadding))
            {
                return false;
            }

            float expectedViewportWidth = Math.Max(
                1f,
                expectedWidth - (float)(table.Margin.Left + table.Margin.Right));
            if (table._viewportWidth != expectedViewportWidth ||
                table._contentWidth != Sum(table._colWidths))
            {
                return false;
            }

            float totalHeight = (float)(table.Margin.Top + table.Margin.Bottom);
            for (int index = 0; index < table._rowHeights.Length; index++)
            {
                float height = table._rowHeights[index];
                if (!float.IsFinite(height) || height <= 0)
                    return false;
                totalHeight += height;
            }

            if (table._contentWidth > table._viewportWidth + 0.5f)
                totalHeight += HorizontalScrollbarHeight;

            if (!float.IsFinite(totalHeight) || table.Bounds.Height != totalHeight)
                return false;

            double bottom = table._arrangedY + table.Margin.Top;
            for (int index = 0; index < table._rowHeights.Length; index++)
            {
                bottom += table._rowHeights[index];
                if (!double.IsFinite(bottom) || table._rowBottomEdges[index] != bottom)
                    return false;
            }

            return true;
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
            CellAlignment[] current,
            CellAlignment[] capturedArray,
            CellAlignment[] capturedValues)
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

        private static bool IsFiniteThickness(Thickness value)
            => double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
               double.IsFinite(value.Right) && double.IsFinite(value.Bottom);

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

        private static float Sum(float[] values)
        {
            float sum = 0;
            for (int index = 0; index < values.Length; index++)
                sum += values[index];
            return sum;
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

    /// <summary>
    /// Captures a stable source table and a freshly cloned private table before
    /// any candidate measurement or arrangement. Cell placement transfers are
    /// captured in row-major order at the exact source column widths; the
    /// prepared widths may change when its private table is measured.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransfer(
        TableBox prepared,
        float expectedRootWidth,
        out MeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedRootWidth, out transfer, allowMeasuredVectorPlacement: false);

    /// <summary>
    /// Root-local image-completion transfer for a private candidate table.
    /// Candidate vectors are measurement-only and their geometry is remapped
    /// to stable source runs; no candidate table or run is published.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
        TableBox prepared,
        float expectedRootWidth,
        out MeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedRootWidth, out transfer, allowMeasuredVectorPlacement: true);

    private bool TryCaptureMeasuredPlacementTransferCore(
        TableBox prepared,
        float expectedRootWidth,
        out MeasuredPlacementTransfer? transfer,
        bool allowMeasuredVectorPlacement)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        transfer = null;
        if (!float.IsFinite(expectedRootWidth) || expectedRootWidth <= 0 ||
            SafeHtmlInlineFlowScopeId is not null ||
            prepared.SafeHtmlInlineFlowScopeId != SafeHtmlInlineFlowScopeId ||
            prepared.BlockIndex != BlockIndex ||
            !HasCompatiblePlacementContext(_context, prepared._context) ||
            !TryGetPrivatePlacementCellCount(out int cellCount) ||
            cellCount <= 0 || cellCount > MaximumPrivateRasterPlacementCells ||
            RowCount <= 0 || RowCount > MaximumPrivateRasterPlacementCells ||
            _colCount <= 0 || _colCount > MaximumPrivateRasterPlacementCells)
        {
            return false;
        }

        Thickness headerPadding = EffectiveCellPadding(GetHeaderStyle());
        Thickness bodyPadding = EffectiveCellPadding(GetBodyStyle());
        if (!IsFiniteThicknessForClone(headerPadding) || !IsFiniteThicknessForClone(bodyPadding) ||
            headerPadding.Left < 0 || headerPadding.Right < 0 ||
            headerPadding.Top < 0 || headerPadding.Bottom < 0 ||
            bodyPadding.Left < 0 || bodyPadding.Right < 0 ||
            bodyPadding.Top < 0 || bodyPadding.Bottom < 0)
        {
            return false;
        }

        if (!IsSourceTransferEligible(expectedRootWidth, headerPadding, bodyPadding) ||
            !TryValidateCloneBeforeMeasure(prepared) ||
            ReferenceEquals(prepared._columnAlignments, _columnAlignments))
        {
            return false;
        }

        if (!TryPreflightCellTransferBudget(
                prepared, cellCount, headerPadding, bodyPadding, allowMeasuredVectorPlacement))
            return false;

        int rowCount = _headerCells.Length + _bodyCells.Length;
        var rows = new RowFence[rowCount];
        int rowIndex = 0;
        for (int index = 0; index < _headerCells.Length; index++)
            rows[rowIndex++] = new RowFence(true, index, _headerCells[index], prepared._headerCells[index]);
        for (int index = 0; index < _bodyCells.Length; index++)
            rows[rowIndex++] = new RowFence(false, index, _bodyCells[index], prepared._bodyCells[index]);

        var cells = new CellFence[cellCount];
        var cellTransfers = new InlineContainerBox.MeasuredPlacementTransfer[cellCount];
        int cellIndex = 0;
        for (int row = 0; row < _headerCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = _headerCells[row];
            InlineContainerBox[] preparedRow = prepared._headerCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                float expectedSourceWidth = GetCellMeasureWidth(_colWidths!, column, headerPadding);
                if (!IsSourceCellAtTablePlacement(this, sourceRow[column], true, row, column, headerPadding))
                    return false;
                InlineContainerBox.MeasuredPlacementTransfer? cellTransfer;
                bool captured = allowMeasuredVectorPlacement
                    ? sourceRow[column].TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
                        preparedRow[column], expectedSourceWidth, out cellTransfer)
                    : sourceRow[column].TryCaptureTableCellMeasuredPlacementTransfer(
                        preparedRow[column], expectedSourceWidth, out cellTransfer);
                if (!captured || cellTransfer is null)
                {
                    return false;
                }

                cells[cellIndex] = new CellFence(true, row, column, sourceRow[column], preparedRow[column], cellTransfer);
                cellTransfers[cellIndex++] = cellTransfer;
            }
        }

        for (int row = 0; row < _bodyCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = _bodyCells[row];
            InlineContainerBox[] preparedRow = prepared._bodyCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                float expectedSourceWidth = GetCellMeasureWidth(_colWidths!, column, bodyPadding);
                if (!IsSourceCellAtTablePlacement(this, sourceRow[column], false, row, column, bodyPadding))
                    return false;
                InlineContainerBox.MeasuredPlacementTransfer? cellTransfer;
                bool captured = allowMeasuredVectorPlacement
                    ? sourceRow[column].TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
                        preparedRow[column], expectedSourceWidth, out cellTransfer)
                    : sourceRow[column].TryCaptureTableCellMeasuredPlacementTransfer(
                        preparedRow[column], expectedSourceWidth, out cellTransfer);
                if (!captured || cellTransfer is null)
                {
                    return false;
                }

                cells[cellIndex] = new CellFence(false, row, column, sourceRow[column], preparedRow[column], cellTransfer);
                cellTransfers[cellIndex++] = cellTransfer;
            }
        }

        if (cellIndex != cellCount)
            return false;

        var alignmentValues = (CellAlignment[])_columnAlignments.Clone();
        transfer = new MeasuredPlacementTransfer(
            this,
            prepared,
            expectedRootWidth,
            rows,
            cells,
            cellTransfers,
            alignmentValues,
            headerPadding,
            bodyPadding);
        return true;
    }

    private bool IsSourceTransferEligible(
        float expectedRootWidth,
        Thickness headerPadding,
        Thickness bodyPadding)
    {
        if (IsDirty || !_hasArrangement || _colWidths is null || _rowHeights is null ||
            _colWidths.Length != _colCount || _rowHeights.Length != RowCount ||
            _rowBottomEdges.Length != RowCount || _arrangedWidth != expectedRootWidth ||
            Bounds.Width != expectedRootWidth || Bounds.X != _arrangedX || Bounds.Y != _arrangedY ||
            !IsFiniteNonnegativeForClone(Bounds) || !IsFiniteThicknessForClone(Margin) ||
            !double.IsFinite(CollapsedTopMargin) ||
            !float.IsFinite(_arrangedX) || !float.IsFinite(_arrangedY) ||
            !float.IsFinite(_contentWidth) || !float.IsFinite(_viewportWidth) ||
            !double.IsFinite(_horizontalOffset) ||
            _horizontalOffset < 0 ||
            _horizontalOffset > Math.Max(0, _contentWidth - _viewportWidth) ||
            _columnAlignments.Length > _colCount)
        {
            return false;
        }

        for (int column = 0; column < _colWidths.Length; column++)
        {
            if (!float.IsFinite(_colWidths[column]) || _colWidths[column] <= 0)
                return false;
        }

        for (int row = 0; row < _rowHeights.Length; row++)
        {
            if (!float.IsFinite(_rowHeights[row]) || _rowHeights[row] <= 0)
                return false;
        }

        return MeasuredPlacementTransfer.IsValidMeasuredGeometry(
            this, expectedRootWidth, headerPadding, bodyPadding);
    }

    private bool TryValidateCloneBeforeMeasure(TableBox prepared)
    {
        if (prepared._headerCells.Length != _headerCells.Length ||
            prepared._bodyCells.Length != _bodyCells.Length ||
            prepared.IsDirty == false || prepared._hasArrangement ||
            prepared._colWidths is not null || prepared._rowHeights is not null ||
            prepared._rowBottomEdges.Length != 0 || prepared._contentWidth != 0 ||
            prepared._viewportWidth != 0 || prepared._arrangedX != 0 ||
            prepared._arrangedY != 0 || prepared._arrangedWidth != 0 ||
            !MeasuredPlacementTransfer.SameRect(prepared.Bounds, default) ||
            prepared._horizontalOffset != _horizontalOffset ||
            !ReferenceEquals(prepared._context.SourceMap, _context.SourceMap) ||
            prepared._colCount != _colCount ||
            !SamePlacementMetadataForClone(this, prepared) || !SameMarginsForClone(this, prepared) ||
            prepared._columnAlignments.Length != _columnAlignments.Length)
        {
            return false;
        }

        for (int index = 0; index < _columnAlignments.Length; index++)
        {
            if (_columnAlignments[index] != prepared._columnAlignments[index])
                return false;
        }

        int count = 0;
        for (int rowIndex = 0; rowIndex < _headerCells.Length; rowIndex++)
        {
            InlineContainerBox[]? sourceRow = _headerCells[rowIndex];
            InlineContainerBox[]? preparedRow = prepared._headerCells[rowIndex];
            if (sourceRow is null || preparedRow is null || ReferenceEquals(sourceRow, preparedRow) ||
                sourceRow.Length != preparedRow.Length)
                return false;
            for (int column = 0; column < sourceRow.Length; column++)
            {
                if (sourceRow[column] is null || preparedRow[column] is null ||
                    ReferenceEquals(sourceRow[column], preparedRow[column]))
                    return false;
                count++;
            }
        }

        for (int rowIndex = 0; rowIndex < _bodyCells.Length; rowIndex++)
        {
            InlineContainerBox[]? sourceRow = _bodyCells[rowIndex];
            InlineContainerBox[]? preparedRow = prepared._bodyCells[rowIndex];
            if (sourceRow is null || preparedRow is null || ReferenceEquals(sourceRow, preparedRow) ||
                sourceRow.Length != preparedRow.Length)
                return false;
            for (int column = 0; column < sourceRow.Length; column++)
            {
                if (sourceRow[column] is null || preparedRow[column] is null ||
                    ReferenceEquals(sourceRow[column], preparedRow[column]))
                    return false;
                count++;
            }
        }

        return count > 0 && count <= MaximumPrivateRasterPlacementCells;
    }

    private bool TryPreflightCellTransferBudget(
        TableBox prepared,
        int cellCount,
        Thickness headerPadding,
        Thickness bodyPadding,
        bool allowMeasuredVectorPlacement)
    {
        const int MaximumTableImagePairs = ImageBox.MaximumMeasuredPlacementTransferBatchCount;
        if (cellCount + 1 > PrivateRasterPlacementTree.MaximumNodes)
            return false;

        int totalRuns = 0;
        int totalImages = 0;
        int totalTextLength = 0;
        long totalStyleKeyEntries = 0;
        var sourceImages = new HashSet<ImageBox>(MaximumTableImagePairs, ReferenceEqualityComparer.Instance);
        var preparedImages = new HashSet<ImageBox>(MaximumTableImagePairs, ReferenceEqualityComparer.Instance);

        for (int row = 0; row < _headerCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = _headerCells[row];
            InlineContainerBox[] preparedRow = prepared._headerCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                if (!TryAccumulateCellTransferBudget(
                        sourceRow[column], preparedRow[column],
                        GetCellMeasureWidth(_colWidths!, column, headerPadding), prepared._context,
                        ref totalRuns, ref totalImages, ref totalTextLength, ref totalStyleKeyEntries,
                        sourceImages, preparedImages, allowMeasuredVectorPlacement))
                {
                    return false;
                }
            }
        }

        for (int row = 0; row < _bodyCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = _bodyCells[row];
            InlineContainerBox[] preparedRow = prepared._bodyCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                if (!TryAccumulateCellTransferBudget(
                        sourceRow[column], preparedRow[column],
                        GetCellMeasureWidth(_colWidths!, column, bodyPadding), prepared._context,
                        ref totalRuns, ref totalImages, ref totalTextLength, ref totalStyleKeyEntries,
                        sourceImages, preparedImages, allowMeasuredVectorPlacement))
                {
                    return false;
                }
            }
        }

        return totalRuns <= InlineContainerBox.MaximumPrivateRasterPlacementRuns &&
            totalImages == sourceImages.Count && totalImages == preparedImages.Count &&
            totalTextLength <= InlineContainerBox.MaximumPrivateRasterPlacementTextLength &&
            totalStyleKeyEntries <= InlineContainerBox.MaximumPrivateRasterPlacementStyleKeyCopyEntries;
    }

    private static bool TryAccumulateCellTransferBudget(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedSourceWidth,
        MarkdownLayoutContext preparedContext,
        ref int totalRuns,
        ref int totalImages,
        ref int totalTextLength,
        ref long totalStyleKeyEntries,
        HashSet<ImageBox> sourceImages,
        HashSet<ImageBox> preparedImages,
        bool allowMeasuredVectorPlacement)
    {
        if (!float.IsFinite(expectedSourceWidth) || expectedSourceWidth <= 0)
            return false;

        int runCount;
        int imageCount;
        long styleKeyEntries;
        bool countsValid = allowMeasuredVectorPlacement
            ? source.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                preparedContext, expectedSourceWidth, out runCount, out imageCount, out styleKeyEntries)
            : source.TryGetPrivateRasterPlacementCounts(
                preparedContext, expectedSourceWidth, out runCount, out imageCount, out styleKeyEntries,
                allowUnmeasuredTextOnly: false,
                allowIdleImagePlacements: false,
                allowCodeForegroundSpans: false);
        if (!countsValid)
            return false;

        int foregroundSpanCount = source.PrivateRasterPlacementForegroundSpanCount;
        int cellRunCount = runCount + foregroundSpanCount;
        int textLength = source.PrivateRasterPlacementTextLength;
        if (cellRunCount < runCount || imageCount < 0 || textLength < 0 ||
            cellRunCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns - totalRuns ||
            imageCount > ImageBox.MaximumMeasuredPlacementTransferBatchCount - totalImages ||
            textLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength - totalTextLength ||
            !PrivateRasterPlacementTree.TryAccumulateStyleKeyCopyEntries(
                ref totalStyleKeyEntries, styleKeyEntries) ||
            source.Runs.Count != prepared.Runs.Count)
        {
            return false;
        }

        for (int index = 0; index < source.Runs.Count; index++)
        {
            InlineRun sourceRun = source.Runs[index];
            InlineRun preparedRun = prepared.Runs[index];
            if (sourceRun is InlineImageRun sourceImageRun)
            {
                if (preparedRun is not InlineImageRun preparedImageRun ||
                    !sourceImages.Add(sourceImageRun.Image) ||
                    !preparedImages.Add(preparedImageRun.Image) ||
                    ReferenceEquals(sourceImageRun.Image, preparedImageRun.Image) ||
                    sourceImages.Contains(preparedImageRun.Image) ||
                    preparedImages.Contains(sourceImageRun.Image))
                {
                    return false;
                }
            }
            else if (preparedRun is InlineImageRun)
            {
                return false;
            }
        }

        totalRuns += cellRunCount;
        totalImages += imageCount;
        totalTextLength += textLength;
        return true;
    }

    private static bool IsSourceCellAtTablePlacement(
        TableBox table,
        InlineContainerBox cell,
        bool isHeader,
        int rowIndex,
        int columnIndex,
        Thickness padding)
    {
        if (!IsFiniteNonnegativeForClone(cell.Bounds))
            return false;
        InlineContainerBox[][] rows = isHeader ? table._headerCells : table._bodyCells;
        if (rowIndex < 0 || rowIndex >= rows.Length || columnIndex < 0 ||
            columnIndex >= rows[rowIndex].Length || table._colWidths is null ||
            table._rowHeights is null)
        {
            return false;
        }

        int logicalRow = isHeader ? rowIndex : table._headerCells.Length + rowIndex;
        float expectedWidth = GetCellMeasureWidth(table._colWidths, columnIndex, padding);
        bool rtl = table._context.FlowDirection == FlowDirection.RightToLeft;
        float innerWidth = Math.Max(
            1f,
            table._arrangedWidth - (float)(table.Margin.Left + table.Margin.Right));
        float contentOrigin = rtl
            ? table._arrangedX + (float)table.Margin.Left + innerWidth - table._contentWidth + (float)table._horizontalOffset
            : table._arrangedX + (float)table.Margin.Left - (float)table._horizontalOffset;
        float expectedX = contentOrigin + GetColumnStartFor(table._colWidths, columnIndex, rtl) + (float)padding.Left;
        float expectedY = table._arrangedY + (float)table.Margin.Top;
        for (int row = 0; row < logicalRow; row++)
            expectedY += table._rowHeights[row];
        expectedY += (float)padding.Top;
        float paddedCellHeight = (float)cell.Bounds.Height + (float)(padding.Top + padding.Bottom);
        return cell.Bounds.X == expectedX && cell.Bounds.Y == expectedY &&
            cell.Bounds.Width == expectedWidth && float.IsFinite(paddedCellHeight) &&
            paddedCellHeight >= 0 && paddedCellHeight <= table._rowHeights[logicalRow];
    }

    private static float GetCellMeasureWidth(float[] widths, int column, Thickness padding)
        => Math.Max(1f, widths[column] - (float)(padding.Left + padding.Right));

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

    private static bool SamePlacementMetadataForClone(TableBox source, TableBox prepared)
        => source.BlockIndex == prepared.BlockIndex &&
           source.SafeHtmlInlineFlowScopeId == prepared.SafeHtmlInlineFlowScopeId;

    private static bool SameMarginsForClone(TableBox source, TableBox prepared)
        => SameThicknessForClone(source.Margin, prepared.Margin) &&
           source.CollapsedTopMargin == prepared.CollapsedTopMargin &&
           source.SuppressTopMargin == prepared.SuppressTopMargin &&
           source.SuppressBottomMargin == prepared.SuppressBottomMargin;

    private static bool SameThicknessForClone(Thickness left, Thickness right)
        => left.Left == right.Left && left.Top == right.Top &&
           left.Right == right.Right && left.Bottom == right.Bottom;

    private static bool IsFiniteThicknessForClone(Thickness value)
        => double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
           double.IsFinite(value.Right) && double.IsFinite(value.Bottom);

    private static bool IsFiniteNonnegativeForClone(Rect rect)
        => double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
           double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
           rect.Width >= 0 && rect.Height >= 0;

    internal readonly struct RowFence(
        bool IsHeader,
        int RowIndex,
        InlineContainerBox[] SourceRow,
        InlineContainerBox[] PreparedRow)
    {
        public bool IsHeader { get; init; } = IsHeader;
        public int RowIndex { get; init; } = RowIndex;
        public InlineContainerBox[] SourceRow { get; init; } = SourceRow;
        public InlineContainerBox[] PreparedRow { get; init; } = PreparedRow;
    }

    internal readonly struct CellFence(
        bool IsHeader,
        int RowIndex,
        int ColumnIndex,
        InlineContainerBox Source,
        InlineContainerBox Prepared,
        InlineContainerBox.MeasuredPlacementTransfer Transfer)
    {
        public bool IsHeader { get; init; } = IsHeader;
        public int RowIndex { get; init; } = RowIndex;
        public int ColumnIndex { get; init; } = ColumnIndex;
        public InlineContainerBox Source { get; init; } = Source;
        public InlineContainerBox Prepared { get; init; } = Prepared;
        public InlineContainerBox.MeasuredPlacementTransfer Transfer { get; init; } = Transfer;
    }
}
