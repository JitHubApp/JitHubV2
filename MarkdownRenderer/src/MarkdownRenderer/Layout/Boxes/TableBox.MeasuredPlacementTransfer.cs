using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class TableBox
{

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
        out TableMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedRootWidth, out transfer, allowMeasuredVectorPlacement: true);

    private bool TryCaptureMeasuredPlacementTransferCore(
        TableBox prepared,
        float expectedRootWidth,
        out TableMeasuredPlacementTransfer? transfer,
        bool allowMeasuredVectorPlacement)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        transfer = null;
        if (!float.IsFinite(expectedRootWidth) || expectedRootWidth <= 0 ||
            SafeHtmlInlineFlowScopeId is not null ||
            prepared.SafeHtmlInlineFlowScopeId != SafeHtmlInlineFlowScopeId ||
            prepared.BlockIndex != BlockIndex ||
            !TablePrivatePlacementCloner.HasCompatiblePlacementContext(_context, prepared._context) ||
            !TablePrivatePlacementCloner.TryGetPrivatePlacementCellCount(this, out int cellCount) ||
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
        var rows = new TableMeasuredRowFence[rowCount];
        int rowIndex = 0;
        for (int index = 0; index < _headerCells.Length; index++)
            rows[rowIndex++] = new TableMeasuredRowFence(true, index, _headerCells[index], prepared._headerCells[index]);
        for (int index = 0; index < _bodyCells.Length; index++)
            rows[rowIndex++] = new TableMeasuredRowFence(false, index, _bodyCells[index], prepared._bodyCells[index]);

        var cells = new TableMeasuredCellFence[cellCount];
        var cellTransfers = new InlineMeasuredPlacementTransfer[cellCount];
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
                InlineMeasuredPlacementTransfer? cellTransfer;
                bool captured = allowMeasuredVectorPlacement
                    ? sourceRow[column].TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
                        preparedRow[column], expectedSourceWidth, out cellTransfer)
                    : sourceRow[column].TryCaptureTableCellMeasuredPlacementTransfer(
                        preparedRow[column], expectedSourceWidth, out cellTransfer);
                if (!captured || cellTransfer is null)
                {
                    return false;
                }

                cells[cellIndex] = new TableMeasuredCellFence(true, row, column, sourceRow[column], preparedRow[column], cellTransfer);
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
                InlineMeasuredPlacementTransfer? cellTransfer;
                bool captured = allowMeasuredVectorPlacement
                    ? sourceRow[column].TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
                        preparedRow[column], expectedSourceWidth, out cellTransfer)
                    : sourceRow[column].TryCaptureTableCellMeasuredPlacementTransfer(
                        preparedRow[column], expectedSourceWidth, out cellTransfer);
                if (!captured || cellTransfer is null)
                {
                    return false;
                }

                cells[cellIndex] = new TableMeasuredCellFence(false, row, column, sourceRow[column], preparedRow[column], cellTransfer);
                cellTransfers[cellIndex++] = cellTransfer;
            }
        }

        if (cellIndex != cellCount)
            return false;

        var alignmentValues = (CellAlignment[])_columnAlignments.Clone();
        transfer = new TableMeasuredPlacementTransfer(
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

        return TableMeasuredPlacementTransfer.IsValidMeasuredGeometry(
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
            !TableMeasuredPlacementTransfer.SameRect(prepared.Bounds, default) ||
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
}
