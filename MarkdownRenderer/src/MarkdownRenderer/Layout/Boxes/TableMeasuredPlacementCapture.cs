using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Captures a table measured-placement transfer between a published table
/// and its private clone: checks the source is eligible, validates the
/// unmeasured clone, bounds the per-cell transfer budget and captures every
/// cell transfer.
/// </summary>
internal static class TableMeasuredPlacementCapture
{
    internal static bool TryCaptureMeasuredPlacementTransfer(
        TableBox table,
        TableBox prepared,
        float expectedRootWidth,
        out TableMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            table,
            prepared, expectedRootWidth, out transfer, allowMeasuredVectorPlacement: false);

    internal static bool TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
        TableBox table,
        TableBox prepared,
        float expectedRootWidth,
        out TableMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            table,
            prepared, expectedRootWidth, out transfer, allowMeasuredVectorPlacement: true);

    private static bool TryCaptureMeasuredPlacementTransferCore(
        TableBox table,
        TableBox prepared,
        float expectedRootWidth,
        out TableMeasuredPlacementTransfer? transfer,
        bool allowMeasuredVectorPlacement)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        transfer = null;
        if (!float.IsFinite(expectedRootWidth) || expectedRootWidth <= 0 ||
            table.SafeHtmlInlineFlowScopeId is not null ||
            prepared.SafeHtmlInlineFlowScopeId != table.SafeHtmlInlineFlowScopeId ||
            prepared.BlockIndex != table.BlockIndex ||
            !TablePrivatePlacementCloner.HasCompatiblePlacementContext(table.Context, prepared.Context) ||
            !TablePrivatePlacementCloner.TryGetPrivatePlacementCellCount(table, out int cellCount) ||
            cellCount <= 0 || cellCount > TableBox.MaximumPrivateRasterPlacementCells ||
            table.RowCount <= 0 || table.RowCount > TableBox.MaximumPrivateRasterPlacementCells ||
            table.ColumnCount <= 0 || table.ColumnCount > TableBox.MaximumPrivateRasterPlacementCells)
        {
            return false;
        }

        Thickness headerPadding = TableBox.EffectiveCellPadding(table.GetHeaderStyle());
        Thickness bodyPadding = TableBox.EffectiveCellPadding(table.GetBodyStyle());
        if (!IsFiniteThicknessForClone(headerPadding) || !IsFiniteThicknessForClone(bodyPadding) ||
            headerPadding.Left < 0 || headerPadding.Right < 0 ||
            headerPadding.Top < 0 || headerPadding.Bottom < 0 ||
            bodyPadding.Left < 0 || bodyPadding.Right < 0 ||
            bodyPadding.Top < 0 || bodyPadding.Bottom < 0)
        {
            return false;
        }

        if (!IsSourceTransferEligible(table, expectedRootWidth, headerPadding, bodyPadding) ||
            !TryValidateCloneBeforeMeasure(table, prepared) ||
            ReferenceEquals(prepared.ColumnAlignments, table.ColumnAlignments))
        {
            return false;
        }

        if (!TryPreflightCellTransferBudget(
                table,
                prepared, cellCount, headerPadding, bodyPadding, allowMeasuredVectorPlacement))
            return false;

        int rowCount = table.HeaderCells.Length + table.BodyCells.Length;
        var rows = new TableMeasuredRowFence[rowCount];
        int rowIndex = 0;
        for (int index = 0; index < table.HeaderCells.Length; index++)
            rows[rowIndex++] = new TableMeasuredRowFence(true, index, table.HeaderCells[index], prepared.HeaderCells[index]);
        for (int index = 0; index < table.BodyCells.Length; index++)
            rows[rowIndex++] = new TableMeasuredRowFence(false, index, table.BodyCells[index], prepared.BodyCells[index]);

        var cells = new TableMeasuredCellFence[cellCount];
        var cellTransfers = new InlineMeasuredPlacementTransfer[cellCount];
        int cellIndex = 0;
        for (int row = 0; row < table.HeaderCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = table.HeaderCells[row];
            InlineContainerBox[] preparedRow = prepared.HeaderCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                float expectedSourceWidth = GetCellMeasureWidth(table.ColumnWidths!, column, headerPadding);
                if (!IsSourceCellAtTablePlacement(table, sourceRow[column], true, row, column, headerPadding))
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

        for (int row = 0; row < table.BodyCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = table.BodyCells[row];
            InlineContainerBox[] preparedRow = prepared.BodyCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                float expectedSourceWidth = GetCellMeasureWidth(table.ColumnWidths!, column, bodyPadding);
                if (!IsSourceCellAtTablePlacement(table, sourceRow[column], false, row, column, bodyPadding))
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

        var alignmentValues = (TableBox.CellAlignment[])table.ColumnAlignments.Clone();
        transfer = new TableMeasuredPlacementTransfer(
            table,
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

    private static bool IsSourceTransferEligible(
        TableBox table,
        float expectedRootWidth,
        Thickness headerPadding,
        Thickness bodyPadding)
    {
        if (table.IsDirty || !table.HasArrangement || table.ColumnWidths is null || table.RowHeights is null ||
            table.ColumnWidths.Length != table.ColumnCount || table.RowHeights.Length != table.RowCount ||
            table.RowBottomEdges.Length != table.RowCount || table.ArrangedWidth != expectedRootWidth ||
            table.Bounds.Width != expectedRootWidth || table.Bounds.X != table.ArrangedX || table.Bounds.Y != table.ArrangedY ||
            !IsFiniteNonnegativeForClone(table.Bounds) || !IsFiniteThicknessForClone(table.Margin) ||
            !double.IsFinite(table.CollapsedTopMargin) ||
            !float.IsFinite(table.ArrangedX) || !float.IsFinite(table.ArrangedY) ||
            !float.IsFinite(table.ContentWidth) || !float.IsFinite(table.ViewportWidth) ||
            !double.IsFinite(table.HorizontalOffset) ||
            table.HorizontalOffset < 0 ||
            table.HorizontalOffset > Math.Max(0, table.ContentWidth - table.ViewportWidth) ||
            table.ColumnAlignments.Length > table.ColumnCount)
        {
            return false;
        }

        for (int column = 0; column < table.ColumnWidths.Length; column++)
        {
            if (!float.IsFinite(table.ColumnWidths[column]) || table.ColumnWidths[column] <= 0)
                return false;
        }

        for (int row = 0; row < table.RowHeights.Length; row++)
        {
            if (!float.IsFinite(table.RowHeights[row]) || table.RowHeights[row] <= 0)
                return false;
        }

        return TableMeasuredPlacementTransfer.IsValidMeasuredGeometry(
            table, expectedRootWidth, headerPadding, bodyPadding);
    }

    private static bool TryValidateCloneBeforeMeasure(TableBox table, TableBox prepared)
    {
        if (prepared.HeaderCells.Length != table.HeaderCells.Length ||
            prepared.BodyCells.Length != table.BodyCells.Length ||
            prepared.IsDirty == false || prepared.HasArrangement ||
            prepared.ColumnWidths is not null || prepared.RowHeights is not null ||
            prepared.RowBottomEdges.Length != 0 || prepared.ContentWidth != 0 ||
            prepared.ViewportWidth != 0 || prepared.ArrangedX != 0 ||
            prepared.ArrangedY != 0 || prepared.ArrangedWidth != 0 ||
            !TableMeasuredPlacementTransfer.SameRect(prepared.Bounds, default) ||
            prepared.HorizontalOffset != table.HorizontalOffset ||
            !ReferenceEquals(prepared.Context.SourceMap, table.Context.SourceMap) ||
            prepared.ColumnCount != table.ColumnCount ||
            !SamePlacementMetadataForClone(table, prepared) || !SameMarginsForClone(table, prepared) ||
            prepared.ColumnAlignments.Length != table.ColumnAlignments.Length)
        {
            return false;
        }

        for (int index = 0; index < table.ColumnAlignments.Length; index++)
        {
            if (table.ColumnAlignments[index] != prepared.ColumnAlignments[index])
                return false;
        }

        int count = 0;
        for (int rowIndex = 0; rowIndex < table.HeaderCells.Length; rowIndex++)
        {
            InlineContainerBox[]? sourceRow = table.HeaderCells[rowIndex];
            InlineContainerBox[]? preparedRow = prepared.HeaderCells[rowIndex];
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

        for (int rowIndex = 0; rowIndex < table.BodyCells.Length; rowIndex++)
        {
            InlineContainerBox[]? sourceRow = table.BodyCells[rowIndex];
            InlineContainerBox[]? preparedRow = prepared.BodyCells[rowIndex];
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

        return count > 0 && count <= TableBox.MaximumPrivateRasterPlacementCells;
    }

    private static bool TryPreflightCellTransferBudget(
        TableBox table,
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

        for (int row = 0; row < table.HeaderCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = table.HeaderCells[row];
            InlineContainerBox[] preparedRow = prepared.HeaderCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                if (!TryAccumulateCellTransferBudget(
                        sourceRow[column], preparedRow[column],
                        GetCellMeasureWidth(table.ColumnWidths!, column, headerPadding), prepared.Context,
                        ref totalRuns, ref totalImages, ref totalTextLength, ref totalStyleKeyEntries,
                        sourceImages, preparedImages, allowMeasuredVectorPlacement))
                {
                    return false;
                }
            }
        }

        for (int row = 0; row < table.BodyCells.Length; row++)
        {
            InlineContainerBox[] sourceRow = table.BodyCells[row];
            InlineContainerBox[] preparedRow = prepared.BodyCells[row];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                if (!TryAccumulateCellTransferBudget(
                        sourceRow[column], preparedRow[column],
                        GetCellMeasureWidth(table.ColumnWidths!, column, bodyPadding), prepared.Context,
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
        InlineContainerBox[][] rows = isHeader ? table.HeaderCells : table.BodyCells;
        if (rowIndex < 0 || rowIndex >= rows.Length || columnIndex < 0 ||
            columnIndex >= rows[rowIndex].Length || table.ColumnWidths is null ||
            table.RowHeights is null)
        {
            return false;
        }

        int logicalRow = isHeader ? rowIndex : table.HeaderCells.Length + rowIndex;
        float expectedWidth = GetCellMeasureWidth(table.ColumnWidths, columnIndex, padding);
        bool rtl = table.Context.FlowDirection == FlowDirection.RightToLeft;
        float innerWidth = Math.Max(
            1f,
            table.ArrangedWidth - (float)(table.Margin.Left + table.Margin.Right));
        float contentOrigin = rtl
            ? table.ArrangedX + (float)table.Margin.Left + innerWidth - table.ContentWidth + (float)table.HorizontalOffset
            : table.ArrangedX + (float)table.Margin.Left - (float)table.HorizontalOffset;
        float expectedX = contentOrigin + GetColumnStartFor(table.ColumnWidths, columnIndex, rtl) + (float)padding.Left;
        float expectedY = table.ArrangedY + (float)table.Margin.Top;
        for (int row = 0; row < logicalRow; row++)
            expectedY += table.RowHeights[row];
        expectedY += (float)padding.Top;
        float paddedCellHeight = (float)cell.Bounds.Height + (float)(padding.Top + padding.Bottom);
        return cell.Bounds.X == expectedX && cell.Bounds.Y == expectedY &&
            cell.Bounds.Width == expectedWidth && float.IsFinite(paddedCellHeight) &&
            paddedCellHeight >= 0 && paddedCellHeight <= table.RowHeights[logicalRow];
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
