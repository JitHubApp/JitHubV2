using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Captures an inline measured-placement transfer between a published
/// inline box and its private measured clone, and holds the shared fences
/// that validate it: run and key identity, native-layout custody and finite
/// geometry. The root fences live in <see cref="InlineMeasuredPlacementRootAdmission"/>.
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
        if (!TryAdmitMeasuredPlacementPair(
                box, prepared, expectedAvailableWidth, isTableCellTransfer,
                isContainerLeafTransfer, allowMeasuredVectorPlacement,
                out int runCount, out int imageCount))
        {
            return false;
        }

        if (!TryCaptureRunStates(
                box, prepared, runCount, imageCount, allowMeasuredVectorPlacement,
                out InlineMeasuredRunState[] runStates) ||
            !RunImagesMatchEntries(box, prepared, runStates) ||
            !TryCaptureRunStyles(
                box, prepared, runCount,
                out IReadOnlyList<string>[] effectiveAliases,
                out ElementStyle?[] resolvedStyles))
        {
            return false;
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

    // The source and candidate must be distinct supported roots in
    // compatible contexts, with a measured source and an unmeasured private
    // clone whose run and image counts fit this transfer kind.
    private static bool TryAdmitMeasuredPlacementPair(
        InlineContainerBox box,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool isTableCellTransfer,
        bool isContainerLeafTransfer,
        bool allowMeasuredVectorPlacement,
        out int runCount,
        out int imageCount)
    {
        bool rootsSupported = isTableCellTransfer
            ? InlineMeasuredPlacementRootAdmission.IsSupportedTableCellMeasuredPlacementRoot(box, allowMeasuredVectorPlacement) &&
                InlineMeasuredPlacementRootAdmission.IsSupportedTableCellMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? InlineMeasuredPlacementRootAdmission.IsSupportedContainerLeafMeasuredPlacementRoot(box, allowMeasuredVectorPlacement) &&
                InlineMeasuredPlacementRootAdmission.IsSupportedContainerLeafMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : InlineMeasuredPlacementRootAdmission.IsSupportedMeasuredPlacementRoot(box, allowMeasuredVectorPlacement) &&
                InlineMeasuredPlacementRootAdmission.IsSupportedMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement);
        bool measuredSource = isTableCellTransfer
            ? InlineMeasuredPlacementRootAdmission.IsMeasuredSourceForTableCellTransfer(box, expectedAvailableWidth, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? InlineMeasuredPlacementRootAdmission.IsMeasuredSourceForContainerLeafTransfer(box, expectedAvailableWidth, allowMeasuredVectorPlacement)
            : InlineMeasuredPlacementRootAdmission.IsMeasuredSourceForTransfer(box, expectedAvailableWidth, allowMeasuredVectorPlacement);
        runCount = 0;
        imageCount = 0;
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
            !InlineMeasuredPlacementRootAdmission.IsUnmeasuredPrivateCloneOf(
                box, prepared, expectedAvailableWidth,
                allowImageFree: isTableCellTransfer || isContainerLeafTransfer,
                allowMeasuredVectorPlacement: allowMeasuredVectorPlacement) ||
            !NativeLayoutsAreDistinct(box) || !NativeLayoutsAreDistinct(prepared))
        {
            return false;
        }

        return true;
    }

    // Captures each source/prepared run pair in order, rejecting any image
    // box shared between runs or between the source and the candidate.
    private static bool TryCaptureRunStates(
        InlineContainerBox box,
        InlineContainerBox prepared,
        int runCount,
        int imageCount,
        bool allowMeasuredVectorPlacement,
        out InlineMeasuredRunState[] runStates)
    {
        runStates = new InlineMeasuredRunState[runCount];
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

        return imageOrdinal == imageCount && offset == box.RunTextLength &&
            (box.InlineImageEntries?.Count ?? 0) == imageCount &&
            (prepared.InlineImageEntries?.Count ?? 0) == imageCount &&
            ((box.InlineImageEntries is null) == (prepared.InlineImageEntries is null));
    }

    private static bool RunImagesMatchEntries(
        InlineContainerBox box,
        InlineContainerBox prepared,
        InlineMeasuredRunState[] runStates)
    {
        int imageOrdinal = 0;
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

        return true;
    }

    // The candidate's effective aliases must be equal-valued private copies,
    // and both boxes must share the same resolved run style instance.
    private static bool TryCaptureRunStyles(
        InlineContainerBox box,
        InlineContainerBox prepared,
        int runCount,
        out IReadOnlyList<string>[] effectiveAliases,
        out ElementStyle?[] resolvedStyles)
    {
        effectiveAliases = new IReadOnlyList<string>[runCount];
        resolvedStyles = new ElementStyle?[runCount];
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

        return true;
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

    internal static bool MatchesKeys(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
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

    internal static bool IsFiniteThickness(Thickness thickness) =>
        double.IsFinite(thickness.Left) && double.IsFinite(thickness.Top) &&
        double.IsFinite(thickness.Right) && double.IsFinite(thickness.Bottom);
}
