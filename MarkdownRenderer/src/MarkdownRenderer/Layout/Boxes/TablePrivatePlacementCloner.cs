using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Builds an unmeasured private placement copy of a table around cell roots
/// cloned by the private-placement visitor, after proving the cell map is
/// an exact one-to-one copy of this table.
/// </summary>
internal static class TablePrivatePlacementCloner
{
    internal static bool TryCreatePrivateRasterPlacementClone(
        TableBox table,
        MarkdownLayoutContext targetContext,
        IReadOnlyDictionary<InlineContainerBox, InlineContainerBox> clonedCells,
        out TableBox? clone)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(clonedCells);
        clone = null;
        targetContext.CancellationToken.ThrowIfCancellationRequested();

        if (!HasCompatiblePlacementContext(table.Context, targetContext) ||
            !TryGetPrivatePlacementCellCount(table, out int cellCount) ||
            cellCount == 0 || clonedCells.Count != cellCount)
            return false;

        var sourceCells = new HashSet<InlineContainerBox>(
            cellCount,
            ReferenceEqualityComparer.Instance);
        foreach (InlineContainerBox[]? row in table.HeaderCells)
        {
            if (row is null || !AddSourceCells(row, sourceCells, targetContext))
                return false;
        }
        foreach (InlineContainerBox[]? row in table.BodyCells)
        {
            if (row is null || !AddSourceCells(row, sourceCells, targetContext))
                return false;
        }

        var cloneValues = new HashSet<InlineContainerBox>(
            cellCount,
            ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<InlineContainerBox, InlineContainerBox> pair in clonedCells)
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            if (pair.Key is null || pair.Value is null ||
                !sourceCells.Contains(pair.Key) || sourceCells.Contains(pair.Value) ||
                !ReferenceEquals(pair.Value.Context, targetContext) ||
                !IsEquivalentInlineCellClone(pair.Key, pair.Value) ||
                !cloneValues.Add(pair.Value))
                return false;
        }
        if (cloneValues.Count != cellCount)
            return false;

        if (!TryCloneRows(table.HeaderCells, clonedCells, targetContext, out InlineContainerBox[][]? header) ||
            !TryCloneRows(table.BodyCells, clonedCells, targetContext, out InlineContainerBox[][]? body) ||
            header is null || body is null)
            return false;

        targetContext.CancellationToken.ThrowIfCancellationRequested();
        clone = table.CreatePrivatePlacementShell(targetContext, header, body);
        return true;
    }

    internal static bool TryGetPrivatePlacementCellCount(TableBox table, out int cellCount)
    {
        cellCount = 0;
        for (int rowIndex = 0; rowIndex < table.HeaderCells.Length; rowIndex++)
        {
            InlineContainerBox[]? row = table.HeaderCells[rowIndex];
            if (row is null || row.Length > TableBox.MaximumPrivateRasterPlacementCells - cellCount)
                return false;
            cellCount += row.Length;
        }

        for (int rowIndex = 0; rowIndex < table.BodyCells.Length; rowIndex++)
        {
            InlineContainerBox[]? row = table.BodyCells[rowIndex];
            if (row is null || row.Length > TableBox.MaximumPrivateRasterPlacementCells - cellCount)
                return false;
            cellCount += row.Length;
        }

        return true;
    }

    private static bool AddSourceCells(
        InlineContainerBox[] row,
        HashSet<InlineContainerBox> sourceCells,
        MarkdownLayoutContext targetContext)
    {
        for (int column = 0; column < row.Length; column++)
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            InlineContainerBox? cell = row[column];
            if (cell is null || !sourceCells.Add(cell))
                return false;
        }

        return true;
    }

    private static bool TryCloneRows(
        InlineContainerBox[][] sourceRows,
        IReadOnlyDictionary<InlineContainerBox, InlineContainerBox> clonedCells,
        MarkdownLayoutContext targetContext,
        out InlineContainerBox[][]? clonedRows)
    {
        clonedRows = new InlineContainerBox[sourceRows.Length][];
        for (int rowIndex = 0; rowIndex < sourceRows.Length; rowIndex++)
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            InlineContainerBox[]? sourceRow = sourceRows[rowIndex];
            if (sourceRow is null)
            {
                clonedRows = null;
                return false;
            }

            var clonedRow = new InlineContainerBox[sourceRow.Length];
            for (int column = 0; column < sourceRow.Length; column++)
            {
                targetContext.CancellationToken.ThrowIfCancellationRequested();
                if (sourceRow[column] is not { } sourceCell ||
                    !clonedCells.TryGetValue(sourceCell, out InlineContainerBox? clonedCell) ||
                    clonedCell is null)
                {
                    clonedRows = null;
                    return false;
                }

                clonedRow[column] = clonedCell;
            }

            clonedRows[rowIndex] = clonedRow;
        }

        return true;
    }

    internal static bool HasCompatiblePlacementContext(
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

    private static bool IsEquivalentInlineCellClone(
        InlineContainerBox source,
        InlineContainerBox candidate)
    {
        if (source.BlockIndex != candidate.BlockIndex ||
            !string.Equals(source.ElementKey, candidate.ElementKey, StringComparison.Ordinal) ||
            source.SafeHtmlInlineFlowScopeId != candidate.SafeHtmlInlineFlowScopeId ||
            source.Runs.Count != candidate.Runs.Count)
            return false;

        for (int index = 0; index < source.Runs.Count; index++)
        {
            InlineRun originalRun = source.Runs[index];
            InlineRun clonedRun = candidate.Runs[index];
            if (ReferenceEquals(originalRun, clonedRun) ||
                originalRun.GetType() != clonedRun.GetType() ||
                originalRun.InlineIndex != clonedRun.InlineIndex ||
                originalRun.RenderedLength != clonedRun.RenderedLength ||
                !originalRun.SourceSpan.Equals(clonedRun.SourceSpan) ||
                !HaveEquivalentRunText(originalRun, clonedRun) ||
                !string.Equals(originalRun.ElementKey, clonedRun.ElementKey, StringComparison.Ordinal) ||
                !string.Equals(originalRun.SemanticHeadingKey, clonedRun.SemanticHeadingKey, StringComparison.Ordinal) ||
                !SameStrings(originalRun.StyleAliases, clonedRun.StyleAliases) ||
                !SameStrings(originalRun.StyleModifierKeys, clonedRun.StyleModifierKeys) ||
                !SameStrings(originalRun.ScriptStyleAncestryKeys, clonedRun.ScriptStyleAncestryKeys) ||
                originalRun.SafeHtmlScriptRoleIsRunElement != clonedRun.SafeHtmlScriptRoleIsRunElement)
                return false;

            if (originalRun is InlineImageRun originalImage && clonedRun is InlineImageRun clonedImage &&
                (ReferenceEquals(originalImage.Image, clonedImage.Image) ||
                 !string.Equals(originalImage.Url, clonedImage.Url, StringComparison.Ordinal) ||
                 !string.Equals(originalImage.Title, clonedImage.Title, StringComparison.Ordinal) ||
                 !string.Equals(originalImage.LinkUrl, clonedImage.LinkUrl, StringComparison.Ordinal) ||
                 !string.Equals(originalImage.LinkTitle, clonedImage.LinkTitle, StringComparison.Ordinal) ||
                 !string.Equals(originalImage.AltText, clonedImage.AltText, StringComparison.Ordinal) ||
                 originalImage.DesiredWidth != clonedImage.DesiredWidth ||
                 originalImage.DesiredHeight != clonedImage.DesiredHeight))
                return false;

            if (originalRun is LinkRun originalLink && clonedRun is LinkRun clonedLink &&
                (!string.Equals(originalLink.Url, clonedLink.Url, StringComparison.Ordinal) ||
                 !string.Equals(originalLink.Title, clonedLink.Title, StringComparison.Ordinal) ||
                 !string.Equals(originalLink.AccessibilityName, clonedLink.AccessibilityName, StringComparison.Ordinal) ||
                 !string.Equals(originalLink.DisclosureId, clonedLink.DisclosureId, StringComparison.Ordinal) ||
                 originalLink.IsExpanded != clonedLink.IsExpanded ||
                 originalLink.IsSuperscript != clonedLink.IsSuperscript))
                return false;
        }

        return true;
    }

    private static bool HaveEquivalentRunText(InlineRun left, InlineRun right)
    {
        if (left is SourceSliceTextRun leftSlice && right is SourceSliceTextRun rightSlice)
        {
            return ReferenceEquals(leftSlice.SourceText, rightSlice.SourceText) &&
                leftSlice.SourceStart == rightSlice.SourceStart &&
                leftSlice.SliceLength == rightSlice.SliceLength;
        }

        return string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
            string.Equals(left.LayoutText, right.LayoutText, StringComparison.Ordinal);
    }

    private static bool SameStrings(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
