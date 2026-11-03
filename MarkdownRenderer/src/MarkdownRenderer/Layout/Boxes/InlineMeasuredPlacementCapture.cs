using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Captures an inline measured-placement transfer between a published
/// inline box and its private measured clone, and holds the shared fences
/// that validate it: supported roots, compatible inputs, run and key
/// identity, and native-layout custody.
/// </summary>
internal static class InlineMeasuredPlacementCapture
{
    /// <summary>
    /// Captures a measured Body/Heading source and the exact private clone
    /// created from it. The clone must be measured and arranged outside the
    /// source gate before <see cref="InlineMeasuredPlacementTransfer.IsCurrentAndPrepared"/>
    /// succeeds. Unsupported roots fail closed.
    /// </summary>
    internal static bool TryCaptureMeasuredPlacementTransfer(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            box,
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer);

    internal static bool TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            box,
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            allowMeasuredVectorPlacement: true);

    /// <summary>
    /// Root-local image-completion variant for a table cell. The candidate is
    /// measurement-only and its vector geometry is remapped to stable source
    /// runs before the placement transaction commits.
    /// </summary>
    internal static bool TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedSourceWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            box,
            prepared, expectedSourceWidth, isTableCellTransfer: true, out transfer,
            allowMeasuredVectorPlacement: true);

    /// <summary>
    /// Captures a table-cell source and its private clone before the candidate
    /// table is measured. The source remains fenced at its exact current cell
    /// width; the prepared cell may later be measured at a different width as
    /// the candidate table rebalances its columns.
    /// </summary>
    internal static bool TryCaptureTableCellMeasuredPlacementTransfer(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedSourceWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            box,
            prepared, expectedSourceWidth, isTableCellTransfer: true, out transfer);

    /// <summary>
    /// Captures a measured image-free Body, Heading, or ListMarker leaf inside
    /// an ordinary StackBox/ListItemBox placement tree. Both the published
    /// source and the completed private leaf remain fenced at the same exact
    /// width; unlike a table cell this mode never permits column rebalancing.
    /// </summary>
    internal static bool TryCaptureContainerLeafMeasuredPlacementTransfer(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            box,
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            isContainerLeafTransfer: true);

    /// <summary>
    /// Root-local image-completion variant for a vector-bearing leaf in an
    /// ordinary stack/list placement tree. It never publishes its measured-only
    /// candidate runs.
    /// </summary>
    internal static bool TryCaptureContainerLeafMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            box,
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            isContainerLeafTransfer: true, allowMeasuredVectorPlacement: true);

    private static bool TryCaptureMeasuredPlacementTransferCore(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool isTableCellTransfer,
        out InlineMeasuredPlacementTransfer? transfer,
        bool isContainerLeafTransfer = false,
        bool allowMeasuredVectorPlacement = false)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        transfer = null;
        bool rootsSupported = isTableCellTransfer
            ? IsSupportedTableCellMeasuredPlacementRoot(box, allowMeasuredVectorPlacement) &&
                IsSupportedTableCellMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? IsSupportedContainerLeafMeasuredPlacementRoot(box, allowMeasuredVectorPlacement) &&
                IsSupportedContainerLeafMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : IsSupportedMeasuredPlacementRoot(box, allowMeasuredVectorPlacement) &&
                IsSupportedMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement);
        bool measuredSource = isTableCellTransfer
            ? IsMeasuredSourceForTableCellTransfer(box, expectedAvailableWidth, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? IsMeasuredSourceForContainerLeafTransfer(box, expectedAvailableWidth, allowMeasuredVectorPlacement)
            : IsMeasuredSourceForTransfer(box, expectedAvailableWidth, allowMeasuredVectorPlacement);
        int runCount = 0;
        int imageCount = 0;
        bool placementCountsValid = allowMeasuredVectorPlacement
            ? box.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                prepared.Context, expectedAvailableWidth, out runCount,
                out imageCount, out _)
            : box.TryGetPrivateRasterPlacementCounts(
                prepared.Context, expectedAvailableWidth, out runCount,
                out imageCount, out _, allowUnmeasuredTextOnly: false,
                allowIdleImagePlacements: false, allowCodeForegroundSpans: false);
        if (ReferenceEquals(box, prepared) ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0f ||
            !rootsSupported ||
            !InlinePrivatePlacementPreflight.ArePrivateRasterPlacementContextsCompatible(box.Context, prepared.Context) ||
            !box.RawPlacementMargin.Equals(prepared.RawPlacementMargin) ||
            !placementCountsValid ||
            (!isTableCellTransfer && !isContainerLeafTransfer && imageCount == 0) ||
            (isContainerLeafTransfer && imageCount != 0) ||
            imageCount > InlineContainerBox.MaximumPrivateRasterPlacementImages ||
            (!isTableCellTransfer && !isContainerLeafTransfer && runCount == 0) ||
            runCount > InlineContainerBox.MaximumPrivateRasterPlacementRuns ||
            !measuredSource ||
            !IsUnmeasuredPrivateCloneOf(
                box, prepared, expectedAvailableWidth,
                allowImageFree: isTableCellTransfer || isContainerLeafTransfer,
                allowMeasuredVectorPlacement: allowMeasuredVectorPlacement) ||
            !NativeLayoutsAreDistinct(box) || !NativeLayoutsAreDistinct(prepared))
        {
            return false;
        }

        var runStates = new InlineMeasuredRunState[runCount];
        int offset = 0;
        int imageOrdinal = 0;
        var seenSourceImages = new ImageBox[imageCount];
        var seenPreparedImages = new ImageBox[imageCount];
        for (int index = 0; index < runCount; index++)
        {
            InlineRun sourceRun = box.RunList[index];
            InlineRun preparedRun = prepared.RunList[index];
            if (!InlineMeasuredRunState.TryCreate(
                    sourceRun, preparedRun, index, offset,
                    allowMeasuredVectorPlacement, box.ResolvedContainerStyle!.FontSize,
                    out runStates[index]))
                return false;

            if (runStates[index].SourceImageRun is { } sourceImageRun &&
                runStates[index].PreparedImageRun is { } preparedImageRun)
            {
                for (int prior = 0; prior < imageOrdinal; prior++)
                {
                    if (ReferenceEquals(seenSourceImages[prior], sourceImageRun.Image) ||
                        ReferenceEquals(seenPreparedImages[prior], preparedImageRun.Image) ||
                        ReferenceEquals(seenSourceImages[prior], preparedImageRun.Image) ||
                        ReferenceEquals(seenPreparedImages[prior], sourceImageRun.Image))
                    {
                        return false;
                    }
                }
                seenSourceImages[imageOrdinal] = sourceImageRun.Image;
                seenPreparedImages[imageOrdinal] = preparedImageRun.Image;
                imageOrdinal++;
            }

            offset += sourceRun.TextLength;
        }

        if (imageOrdinal != imageCount || offset != box.RunTextLength ||
            (box.InlineImageEntries?.Count ?? 0) != imageCount ||
            (prepared.InlineImageEntries?.Count ?? 0) != imageCount ||
            ((box.InlineImageEntries is null) != (prepared.InlineImageEntries is null)))
        {
            return false;
        }

        imageOrdinal = 0;
        foreach (InlineMeasuredRunState run in runStates)
        {
            if (run.SourceImageRun is null || run.PreparedImageRun is null)
                continue;
            if (box.InlineImageEntries is null || prepared.InlineImageEntries is null)
                return false;
            if (!run.MatchesImageEntries(
                    box.InlineImageEntries[imageOrdinal], prepared.InlineImageEntries[imageOrdinal]))
            {
                return false;
            }
            imageOrdinal++;
        }

        var effectiveAliases = new IReadOnlyList<string>[runCount];
        var resolvedStyles = new ElementStyle?[runCount];
        for (int index = 0; index < runCount; index++)
        {
            string[] sourceAliases = box.GetEffectiveRunAliases(index);
            string[] preparedAliases = prepared.GetEffectiveRunAliases(index);
            if (ReferenceEquals(sourceAliases, preparedAliases) ||
                !MatchesKeys(sourceAliases, preparedAliases) ||
                !ReferenceEquals(box.GetCachedRunStyle(index), prepared.GetCachedRunStyle(index)))
            {
                return false;
            }
            effectiveAliases[index] = preparedAliases;
            resolvedStyles[index] = box.GetCachedRunStyle(index);
        }

        if (ReferenceEquals(box.StyleContextKeys, prepared.StyleContextKeys) ||
            ReferenceEquals(box.StyleAliasKeys, prepared.StyleAliasKeys) ||
            !MatchesKeys(box.StyleContextKeys, prepared.StyleContextKeys) ||
            !MatchesKeys(box.StyleAliasKeys, prepared.StyleAliasKeys))
        {
            return false;
        }

        transfer = new InlineMeasuredPlacementTransfer(
            box,
            prepared,
            expectedAvailableWidth,
            runStates,
            prepared.StyleContextKeys,
            prepared.StyleAliasKeys,
            effectiveAliases,
            resolvedStyles,
            isTableCellTransfer,
            isContainerLeafTransfer,
            allowMeasuredVectorPlacement);
        return true;
    }

    private static bool IsSupportedMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey == MarkdownElementKeys.Body || InlineRunStyleResolver.IsHeadingElement(box.ElementKey)) &&
        box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box.InlineEmbedEntries is null &&
        (allowMeasuredVectorPlacement || box.VectorSceneRunCount == 0) &&
        !box.HasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box.PendingSceneGeometry.Length == 0) &&
        box.ForegroundSpans.Count == 0;

    private static bool IsSupportedTableCellMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey is MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell) &&
        box.BlockIndex > 0 && box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box.InlineEmbedEntries is null &&
        (allowMeasuredVectorPlacement || box.VectorSceneRunCount == 0) &&
        !box.HasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box.PendingSceneGeometry.Length == 0) &&
        box.ForegroundSpans.Count == 0;

    private static bool IsSupportedContainerLeafMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey is MarkdownElementKeys.Body or
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or
            MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or
            MarkdownElementKeys.ListMarker) &&
        box.BlockIndex > 0 && box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box.InlineEmbedEntries is null &&
        (allowMeasuredVectorPlacement || box.VectorSceneRunCount == 0) &&
        !box.HasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box.PendingSceneGeometry.Length == 0) &&
        box.ForegroundSpans.Count == 0;

    internal static bool AreCompatibleMeasuredRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false) =>
        float.IsFinite(expectedAvailableWidth) && expectedAvailableWidth > 0f &&
        IsSupportedMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    internal static bool AreCompatibleTableCellRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        bool allowMeasuredVectorPlacement = false) =>
        IsSupportedTableCellMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedTableCellMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    internal static bool AreCompatibleContainerLeafRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        bool allowMeasuredVectorPlacement = false) =>
        IsSupportedContainerLeafMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedContainerLeafMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    private static bool AreCompatiblePlacementRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared) =>
        InlinePrivatePlacementPreflight.ArePrivateRasterPlacementContextsCompatible(source.Context, prepared.Context) &&
        source.BlockIndex == prepared.BlockIndex &&
        string.Equals(source.ElementKey, prepared.ElementKey, StringComparison.Ordinal) &&
        string.Equals(source.CodeLanguage, prepared.CodeLanguage, StringComparison.Ordinal) &&
        string.Equals(source.StyleState, prepared.StyleState, StringComparison.Ordinal) &&
        source.CodeBlockTextOffset == prepared.CodeBlockTextOffset &&
        source.CodeBlockTextLength == prepared.CodeBlockTextLength &&
        source.WordWrapping == prepared.WordWrapping &&
        source.TextAlignment == prepared.TextAlignment &&
        source.DrawContainerChrome == prepared.DrawContainerChrome &&
        source.UseContainerPadding == prepared.UseContainerPadding &&
        source.UseContainerMargin == prepared.UseContainerMargin &&
        source.SafeHtmlInlineFlowScopeId == prepared.SafeHtmlInlineFlowScopeId &&
        source.Margin.Equals(prepared.Margin) &&
        source.CollapsedTopMargin == prepared.CollapsedTopMargin &&
        source.SuppressTopMargin == prepared.SuppressTopMargin &&
        source.SuppressBottomMargin == prepared.SuppressBottomMargin &&
        source.UsesProgressiveNativeSceneResidency == prepared.UsesProgressiveNativeSceneResidency &&
        source.HasAtomicInlineObject == prepared.HasAtomicInlineObject &&
        source.IsBufferDirty == prepared.IsBufferDirty &&
        source.RunTextLength == prepared.RunTextLength &&
        ReferenceEquals(source.LayoutBuffer, prepared.LayoutBuffer) &&
        ReferenceEquals(source.ResolvedContainerStyle, prepared.ResolvedContainerStyle) &&
        MatchesKeys(source.StyleContextKeys, prepared.StyleContextKeys) &&
        MatchesKeys(source.StyleAliasKeys, prepared.StyleAliasKeys);

    private static bool IsMeasuredSourceForTransfer(
        InlineContainerBox box,
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        bool expectedAtomic = (box.InlineImageEntries?.Count ?? 0) > 0 ||
            allowMeasuredVectorPlacement && box.VectorSceneRunCount > 0;
        if (box.IsDirty || box.TextLayout is null || box.IsBufferDirty || box.ResolvedContainerStyle is null ||
            !float.IsFinite(box.LastMeasuredWidth) || box.LastMeasuredWidth <= 0f ||
            Math.Abs(box.LastMeasuredWidth - expectedAvailableWidth) > 0.5f ||
            !IsFiniteNonnegative(box.Bounds) || box.Bounds.Width != expectedAvailableWidth ||
            box.RunList.Count == 0 || box.RunTextLength != box.LayoutBuffer.Length ||
            box.RunTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength ||
            !box.HasWellFormedEffectiveRunAliases || box.ResolvedRunStyleCount != box.RunList.Count ||
            box.HasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(box.Margin) || !double.IsFinite(box.CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    private static bool IsMeasuredSourceForTableCellTransfer(
        InlineContainerBox box,
        float expectedSourceWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = box.InlineImageEntries?.Count ?? 0;
        bool expectedAtomic = imageCount > 0 ||
            allowMeasuredVectorPlacement && box.VectorSceneRunCount > 0;
        if (box.IsDirty || box.TextLayout is null || box.IsBufferDirty || box.ResolvedContainerStyle is null ||
            !float.IsFinite(expectedSourceWidth) || expectedSourceWidth <= 0f ||
            !float.IsFinite(box.LastMeasuredWidth) || box.LastMeasuredWidth != expectedSourceWidth ||
            !IsFiniteNonnegative(box.Bounds) || box.Bounds.Width != expectedSourceWidth ||
            box.RunTextLength != box.LayoutBuffer.Length ||
            box.RunTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength ||
            !box.HasWellFormedEffectiveRunAliases || box.ResolvedRunStyleCount != box.RunList.Count ||
            imageCount > InlineContainerBox.MaximumPrivateRasterPlacementImages ||
            box.HasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(box.Margin) || !double.IsFinite(box.CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    private static bool IsMeasuredSourceForContainerLeafTransfer(
        InlineContainerBox box,
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = box.InlineImageEntries?.Count ?? 0;
        bool expectedAtomic = allowMeasuredVectorPlacement && box.VectorSceneRunCount > 0;
        if (box.IsDirty || box.TextLayout is null || box.IsBufferDirty || box.ResolvedContainerStyle is null ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0f ||
            !float.IsFinite(box.LastMeasuredWidth) || box.LastMeasuredWidth != expectedAvailableWidth ||
            !IsFiniteNonnegative(box.Bounds) || box.Bounds.Width != expectedAvailableWidth ||
            box.RunTextLength != box.LayoutBuffer.Length ||
            box.RunTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength ||
            !box.HasWellFormedEffectiveRunAliases || box.ResolvedRunStyleCount != box.RunList.Count ||
            imageCount != 0 || box.InlineImageEntries is not null || box.HasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(box.RawPlacementMargin) || !IsFiniteThickness(box.Margin) ||
            !double.IsFinite(box.CollapsedTopMargin))
        {
            return false;
        }

        // Empty paragraphs may have no runs, but only the established clean
        // empty native-layout shape is admitted. Zero-length runs remain
        // restricted to the existing table-cell path.
        return box.RunList.Count != 0 ||
            (box.LayoutBuffer.Length == 0 && box.RunTextLength == 0 && box.InlineEmbedEntries is null &&
             box.VectorSceneRunCount == 0 && !box.HasDeferredSceneMarker &&
             box.PendingSceneGeometry.Length == 0 && box.ForegroundSpans.Count == 0);
    }

    private static bool IsUnmeasuredPrivateCloneOf(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool allowImageFree = false,
        bool allowMeasuredVectorPlacement = false)
    {
        bool imageIndexShapeMatches = allowImageFree
            ? (prepared.InlineImageEntries?.Count ?? 0) == (source.InlineImageEntries?.Count ?? 0) &&
                ((prepared.InlineImageEntries is null) == (source.InlineImageEntries is null))
            : prepared.InlineImageEntries is { Count: > 0 } &&
                prepared.InlineImageEntries.Count == source.InlineImageEntries?.Count;
        return prepared.IsDirty && prepared.TextLayout is null && prepared.SelectionLayout is null &&
            prepared.TextLayoutRetirementRecord is null &&
            prepared.SelectionLayoutRetirementRecord is null &&
            prepared.CachedLineMetrics is null && !prepared.HasReadLineMetrics &&
            !prepared.IsBufferDirty && ReferenceEquals(prepared.LayoutBuffer, source.LayoutBuffer) &&
            prepared.RunTextLength == source.RunTextLength &&
            prepared.RunList.Count == source.RunList.Count && imageIndexShapeMatches &&
            prepared.VectorSceneRunCount == source.VectorSceneRunCount &&
            (allowMeasuredVectorPlacement || source.VectorSceneRunCount == 0) &&
            !source.HasDeferredSceneMarker && !prepared.HasDeferredSceneMarker &&
            prepared.CurrentSceneGeometryGeneration == 0 &&
            prepared.PendingSceneGeometryGeneration == 0 &&
            prepared.PendingSceneGeometry.Length == 0 &&
            prepared.LastMeasuredWidth == source.LastMeasuredWidth &&
            ReferenceEquals(prepared.ResolvedContainerStyle, source.ResolvedContainerStyle) &&
            prepared.Bounds.Width == 0 && prepared.Bounds.Height == 0 &&
            float.IsFinite(expectedAvailableWidth);
    }

    internal static bool MatchesRuns(
        InlineContainerBox source,
        InlineContainerBox prepared,
        InlineMeasuredRunState[] runs,
        IReadOnlyList<string>[] effectiveAliases,
        ElementStyle?[] sourceResolvedStyles,
        bool allowMeasuredVectorPlacement)
    {
        int sourceImageCount = source.InlineImageEntries?.Count ?? 0;
        int preparedImageCount = prepared.InlineImageEntries?.Count ?? 0;
        int vectorCount = 0;
        foreach (InlineMeasuredRunState run in runs)
            if (run.SourceVectorRun is not null)
                vectorCount++;
        if (source.RunList.Count != runs.Length || prepared.RunList.Count != runs.Length ||
            source.RunTextLength != prepared.RunTextLength ||
            sourceImageCount != preparedImageCount ||
            ((source.InlineImageEntries is null) != (prepared.InlineImageEntries is null)) ||
            !source.HasWellFormedEffectiveRunAliases ||
            !prepared.HasWellFormedEffectiveRunAliases ||
            source.ResolvedRunStyleCount != runs.Length ||
            prepared.ResolvedRunStyleCount != runs.Length ||
            source.ForegroundSpans.Count != 0 || prepared.ForegroundSpans.Count != 0 ||
            source.InlineEmbedEntries is not null || prepared.InlineEmbedEntries is not null ||
            source.HasDeferredSceneMarker || prepared.HasDeferredSceneMarker ||
            source.VectorSceneRunCount != vectorCount || prepared.VectorSceneRunCount != vectorCount ||
            (!allowMeasuredVectorPlacement && vectorCount != 0) ||
            source.HasAtomicInlineObject != prepared.HasAtomicInlineObject)
        {
            return false;
        }

        for (int index = 0; index < runs.Length; index++)
        {
            InlineMeasuredRunState run = runs[index];
            if (!ReferenceEquals(source.RunList[index], run.SourceRun) ||
                !ReferenceEquals(prepared.RunList[index], run.PreparedRun) ||
                !run.MatchesSourceAndPrepared() ||
                !MatchesKeys(source.GetEffectiveRunAliases(index), effectiveAliases[index]) ||
                !MatchesKeys(prepared.GetEffectiveRunAliases(index), effectiveAliases[index]) ||
                !ReferenceEquals(source.GetCachedRunStyle(index), sourceResolvedStyles[index]))
            {
                return false;
            }
        }

        int imageOrdinal = 0;
        foreach (InlineMeasuredRunState run in runs)
        {
            if (run.SourceImageRun is null || run.PreparedImageRun is null)
                continue;
            if (source.InlineImageEntries is null || prepared.InlineImageEntries is null ||
                imageOrdinal >= sourceImageCount ||
                !run.MatchesImageEntries(
                    source.InlineImageEntries[imageOrdinal],
                    prepared.InlineImageEntries[imageOrdinal]))
            {
                return false;
            }
            imageOrdinal++;
        }
        return imageOrdinal == sourceImageCount;
    }

    internal static bool MatchesContainerKeys(
        InlineContainerBox box,
        IReadOnlyList<string> contextKeys,
        IReadOnlyList<string> aliasKeys) =>
        MatchesKeys(box.StyleContextKeys, contextKeys) &&
        MatchesKeys(box.StyleAliasKeys, aliasKeys);

    private static bool MatchesKeys(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static bool NativeLayoutsAreDistinct(InlineContainerBox box) =>
        HasValidNativeLayoutCustody(box);

    internal static bool HasValidNativeLayoutCustody(InlineContainerBox box) =>
        (box.TextLayout is null) == (box.TextLayoutRetirementRecord is null) &&
        (box.SelectionLayout is null) == (box.SelectionLayoutRetirementRecord is null) &&
        (box.TextLayout is null || box.SelectionLayout is null ||
            !ReferenceEquals(box.TextLayout, box.SelectionLayout)) &&
        (box.TextLayoutRetirementRecord is null || box.SelectionLayoutRetirementRecord is null ||
            !ReferenceEquals(box.TextLayoutRetirementRecord, box.SelectionLayoutRetirementRecord));

    internal static bool Aliases(CanvasTextLayout? first, CanvasTextLayout? second) =>
        first is not null && ReferenceEquals(first, second);

    internal static bool Aliases(
        NativeResourceRetirementRecord? first,
        NativeResourceRetirementRecord? second) =>
        first is not null && ReferenceEquals(first, second);

    internal static bool SameRect(Rect first, Rect second) =>
        first.X == second.X && first.Y == second.Y &&
        first.Width == second.Width && first.Height == second.Height;

    internal static bool IsFiniteNonnegative(Rect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && bounds.Width >= 0 &&
        double.IsFinite(bounds.Height) && bounds.Height >= 0;

    internal static bool HasUsableMeasuredVectorRegions(in InlineSceneGeometry geometry)
    {
        if (geometry.RegionCount <= 0)
            return false;

        for (int index = 0; index < geometry.RegionCount; index++)
        {
            Rect region = geometry.GetRegionBounds(index);
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
                !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
                !double.IsFinite(region.Right) || !double.IsFinite(region.Bottom) ||
                region.Width < 0d || region.Height < 0d)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteThickness(Thickness thickness) =>
        double.IsFinite(thickness.Left) && double.IsFinite(thickness.Top) &&
        double.IsFinite(thickness.Right) && double.IsFinite(thickness.Bottom);
}
