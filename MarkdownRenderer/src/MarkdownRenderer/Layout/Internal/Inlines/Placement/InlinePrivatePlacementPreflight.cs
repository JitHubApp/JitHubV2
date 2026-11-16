using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Inlines.Placement;

/// <summary>
/// Allocation-free checks that decide whether an inline container can be
/// copied into a private placement candidate, and how many runs, images and
/// style-key entries the copy needs.
/// </summary>
internal static class InlinePrivatePlacementPreflight
{
    /// <summary>
    /// Whether a candidate built in <paramref name="targetContext"/> shares every
    /// resource, policy and document identity of <paramref name="sourceContext"/>
    /// that a copied run or its measured layout depends on.
    /// </summary>
    /// <param name="sourceContext">The published source box's context.</param>
    /// <param name="targetContext">The private candidate's context.</param>
    internal static bool ArePrivateRasterPlacementContextsCompatible(
        MarkdownLayoutContext sourceContext,
        MarkdownLayoutContext targetContext)
        => ReferenceEquals(sourceContext.ResourceCreator, targetContext.ResourceCreator) &&
           ReferenceEquals(sourceContext.ThemeSnapshot, targetContext.ThemeSnapshot) &&
           ReferenceEquals(sourceContext.Registry, targetContext.Registry) &&
           ReferenceEquals(sourceContext.Dispatcher, targetContext.Dispatcher) &&
           ReferenceEquals(sourceContext.ImageResolver, targetContext.ImageResolver) &&
           ReferenceEquals(sourceContext.SvgRenderer, targetContext.SvgRenderer) &&
           ReferenceEquals(sourceContext.PerformanceSession, targetContext.PerformanceSession) &&
           ReferenceEquals(sourceContext.ImagePreparationOwner, targetContext.ImagePreparationOwner) &&
           ReferenceEquals(sourceContext.ImageDocumentSource, targetContext.ImageDocumentSource) &&
           ReferenceEquals(sourceContext.SourceMap, targetContext.SourceMap) &&
           sourceContext.FlowDirection == targetContext.FlowDirection &&
           sourceContext.RasterizationScale == targetContext.RasterizationScale &&
           sourceContext.AllowThirdPartyRemoteImages == targetContext.AllowThirdPartyRemoteImages &&
           string.Equals(sourceContext.Language, targetContext.Language, StringComparison.Ordinal) &&
           string.Equals(sourceContext.ImageDocumentPath, targetContext.ImageDocumentPath, StringComparison.Ordinal) &&
           Equals(sourceContext.ImageBaseUri, targetContext.ImageBaseUri) &&
           ReferenceEquals(sourceContext.StringProvider, targetContext.StringProvider) &&
           ReferenceEquals(sourceContext.CommandProvider, targetContext.CommandProvider) &&
           ReferenceEquals(sourceContext.DisclosureStates, targetContext.DisclosureStates);

    internal static bool TryGetPrivateRasterPlacementCounts(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false)
        => TryGetPrivateRasterPlacementCounts(
            box,
            targetContext, availableWidth, out runCount, out imageCount, out _, allowUnmeasuredTextOnly, allowIdleImagePlacements);

    internal static bool TryGetPrivateRasterPlacementCounts(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false)
        => TryGetPrivateRasterPlacementCountsCore(
            box,
            targetContext, availableWidth, out runCount, out imageCount, out styleKeyCopyEntries,
            allowUnmeasuredTextOnly, allowIdleImagePlacements, allowCodeForegroundSpans,
            allowMeasuredVectorPlacement: false);

    internal static bool TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries)
        => TryGetPrivateRasterPlacementCountsCore(
            box,
            targetContext, availableWidth, out runCount, out imageCount, out styleKeyCopyEntries,
            allowUnmeasuredTextOnly: false, allowIdleImagePlacements: false,
            allowCodeForegroundSpans: false, allowMeasuredVectorPlacement: true);

    private static bool TryGetPrivateRasterPlacementCountsCore(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries,
        bool allowUnmeasuredTextOnly,
        bool allowIdleImagePlacements,
        bool allowCodeForegroundSpans,
        bool allowMeasuredVectorPlacement)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        runCount = 0;
        imageCount = 0;
        styleKeyCopyEntries = 0;
        targetContext.CancellationToken.ThrowIfCancellationRequested();
        if (!IsEligibleForPrivateInlinePlacementClone(
                box,
                targetContext, availableWidth, allowUnmeasuredTextOnly, allowIdleImagePlacements,
                allowCodeForegroundSpans, allowMeasuredVectorPlacement) ||
            !TryGetPrivateRasterPlacementShapeCounts(box, allowUnmeasuredTextOnly,
                out int candidateRunCount, out int candidateImageCount, out long candidateStyleKeyCopyEntries,
                allowIdleImagePlacements, allowCodeForegroundSpans, allowMeasuredVectorPlacement) ||
            (box.TextLayout is null && candidateImageCount != 0 && !allowIdleImagePlacements))
            return false;

        // Prove the complete source shape before any earlier root can acquire
        // candidate bitmap leases. The clone and final commit repeat these
        // lifecycle checks because image work can start after this inspection.
        if (box.InlineImageEntries is { } imageEntries)
            foreach (var entry in imageEntries)
                if (!entry.Run.TryClassifyPrivateImagePlacement(
                        targetContext, allowIdleImagePlacements, out _, out _))
                    return false;

        runCount = candidateRunCount;
        imageCount = candidateImageCount;
        styleKeyCopyEntries = candidateStyleKeyCopyEntries;
        return true;
    }

    internal static bool TryGetPrivateRasterPlacementStyleKeyCopyEntries(InlineContainerBox box, out long entries)
    {
        entries = 0;
        if (box.RunList.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns ||
            !box.HasWellFormedEffectiveRunAliases ||
            !box.HasWellFormedResolvedLinkInlineStyleCache ||
            !TryAccumulateStyleKeyCopyEntries(ref entries, box.StyleContextKeys) ||
            !TryAccumulateStyleKeyCopyEntries(ref entries, box.StyleAliasKeys))
            return false;

        for (int index = 0; index < box.RunList.Count; index++)
        {
            InlineRun run = box.RunList[index];
            string[]? aliasOverride = box.GetEffectiveRunAliasOverride(index);
            if (run is null ||
                !TryAccumulateStyleKeyCopyEntries(ref entries, run.StyleAliases) ||
                !TryAccumulateStyleKeyCopyEntries(ref entries, run.StyleModifierKeys) ||
                !TryAccumulateStyleKeyCopyEntries(ref entries, run.ScriptStyleAncestryKeys) ||
                run is LinkRun linkRun && linkRun.InlineStyleMap is { } inlineStyleMap &&
                    (!inlineStyleMap.IsValidForTextLength(run.TextLength) ||
                     !TryAccumulateStyleKeyCopyEntryCount(ref entries, inlineStyleMap.StyleKeyEntryCount)) ||
                aliasOverride is not null &&
                    !TryAccumulateStyleKeyCopyEntries(ref entries, aliasOverride))
                return false;
        }

        return true;
    }

    private static bool TryAccumulateStyleKeyCopyEntries(
        ref long total,
        IReadOnlyList<string>? keys)
    {
        if (keys is null)
            return false;

        int count = keys.Count;
        if (count < 0)
            return false;
        return TryAccumulateStyleKeyCopyEntryCount(ref total, count);
    }

    private static bool TryAccumulateStyleKeyCopyEntryCount(ref long total, long count)
    {
        if (count < 0)
            return false;
        total = total > long.MaxValue - count ? long.MaxValue : total + count;
        return total <= InlineContainerBox.MaximumPrivateRasterPlacementStyleKeyCopyEntries;
    }

    private static bool TryGetPrivateRasterPlacementShapeCounts(
        InlineContainerBox box,
        bool allowUnmeasuredTextOnly,
        out int runCount,
        out int imageCount,
        out long styleKeyCopyEntries,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false,
        bool allowMeasuredVectorPlacement = false)
    {
        runCount = 0;
        imageCount = 0;
        styleKeyCopyEntries = 0;
        if (!IsWithinContainerShapeLimits(box, allowCodeForegroundSpans, ref styleKeyCopyEntries))
            return false;

        // Empty table cells have no logical inline runs but still own mutable
        // native layout state and must be privately reconstructed with their
        // table candidate. Admit only the exact clean empty shape; malformed
        // partial indexes, atomic runs, or stale text buffers remain rejected.
        if (box.RunList.Count == 0)
            return IsCleanEmptyShape(box);

        IReadOnlyList<(InlineImageRun Run, int Offset)> imageEntries = box.InlineImageEntries is { } entries
            ? entries
            : Array.Empty<(InlineImageRun Run, int Offset)>();
        int imageOrdinal = 0;
        int vectorOrdinal = 0;
        long offset = 0;
        for (int runIndex = 0; runIndex < box.RunList.Count; runIndex++)
        {
            InlineRun run = box.RunList[runIndex];
            if (!IsAdmissibleRun(
                    box,
                    run,
                    runIndex,
                    imageEntries,
                    allowMeasuredVectorPlacement,
                    offset,
                    ref styleKeyCopyEntries,
                    ref imageOrdinal,
                    ref vectorOrdinal))
            {
                return false;
            }

            offset += run.TextLength;
            if (offset > int.MaxValue)
                return false;
        }

        bool unmeasuredDirtyText = allowUnmeasuredTextOnly && box.TextLayout is null && box.IsBufferDirty;
        if (imageOrdinal != imageEntries.Count || vectorOrdinal != box.VectorSceneRunCount || offset != box.RunTextLength ||
            (!unmeasuredDirtyText && offset != box.LayoutBuffer.Length))
            return false;

        if (allowMeasuredVectorPlacement && box.PendingSceneGeometry.Length != 0 &&
            !PendingSceneGeometryMatchesRuns(box, vectorOrdinal))
            return false;

        if (box.TextLayout is null && imageOrdinal != 0 &&
            !UnmeasuredImagesArePristine(box, imageEntries, allowIdleImagePlacements))
            return false;

        runCount = box.RunList.Count;
        imageCount = imageOrdinal;
        return true;
    }

    // One run at its logical index with consistent text lengths: a text or
    // non-disclosure link run, an admissible image, or (when allowed) an
    // admissible measured vector scene. Accumulates its style-key copies and
    // advances the image or vector ordinal it consumes.
    private static bool IsAdmissibleRun(
        InlineContainerBox box,
        InlineRun run,
        int runIndex,
        IReadOnlyList<(InlineImageRun Run, int Offset)> imageEntries,
        bool allowMeasuredVectorPlacement,
        long offset,
        ref long styleKeyCopyEntries,
        ref int imageOrdinal,
        ref int vectorOrdinal)
    {
        if (run is null || run.InlineIndex != runIndex || run.RenderedLength != run.TextLength ||
            run.LayoutTextLength != run.TextLength)
            return false;

        if (!TryAccumulateRunStyleKeyCopyEntries(box, run, runIndex, ref styleKeyCopyEntries))
            return false;

        if (PrivateTextRunPlacement.IsSupported(run) || run is LinkRun)
            return run is not LinkRun disclosureLink || (!disclosureLink.IsDisclosure && !disclosureLink.IsExpanded);
        if (run is InlineImageRun imageRun)
            return IsAdmissibleImageRun(box, imageEntries, imageRun, ref imageOrdinal, offset);
        if (allowMeasuredVectorPlacement && run is InlineVectorSceneRun vectorRun)
        {
            if (!IsAdmissibleVectorRun(vectorRun))
                return false;
            vectorOrdinal++;
            return true;
        }
        return false;
    }

    // The container-level size bounds and cache shape, accumulating its own
    // style-key copies before any run is inspected.
    private static bool IsWithinContainerShapeLimits(
        InlineContainerBox box,
        bool allowCodeForegroundSpans,
        ref long styleKeyCopyEntries)
        => !(box.RunList.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns ||
            box.RunTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength ||
            box.InlineImageEntries is { Count: > InlineContainerBox.MaximumPrivateRasterPlacementImages } ||
            !box.HasWellFormedEffectiveRunAliases || !box.HasWellFormedResolvedLinkInlineStyleCache ||
            box.ResolvedRunStyleCount != box.RunList.Count ||
            (box.InlineImageEntries?.Count ?? 0) > box.RunList.Count ||
            !CanCopyPrivateCodeForegroundSpans(box, allowCodeForegroundSpans) ||
            !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, box.StyleContextKeys) ||
            !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, box.StyleAliasKeys));

    private static bool IsCleanEmptyShape(InlineContainerBox box)
        => !(box.RunTextLength != 0 || box.LayoutBuffer.Length != 0 || box.InlineImageEntries is not null ||
            box.InlineEmbedEntries is not null || box.HasAtomicInlineObject ||
            box.VectorSceneRunCount != 0 || box.HasDeferredSceneMarker ||
            box.ForegroundSpans.Count != 0 ||
            (box.TextLayout is null && box.SelectionLayout is not null));

    private static bool TryAccumulateRunStyleKeyCopyEntries(
        InlineContainerBox box,
        InlineRun run,
        int runIndex,
        ref long styleKeyCopyEntries)
    {
        string[]? aliasOverride = box.GetEffectiveRunAliasOverride(runIndex);
        return !(!TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, run.StyleAliases) ||
            !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, run.StyleModifierKeys) ||
            !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, run.ScriptStyleAncestryKeys) ||
            run is LinkRun linkRun && linkRun.InlineStyleMap is { } inlineStyleMap &&
                (!inlineStyleMap.IsValidForTextLength(run.TextLength) ||
                 !TryAccumulateStyleKeyCopyEntryCount(
                     ref styleKeyCopyEntries,
                     inlineStyleMap.StyleKeyEntryCount)) ||
            aliasOverride is not null &&
                !TryAccumulateStyleKeyCopyEntries(ref styleKeyCopyEntries, aliasOverride));
    }

    private static bool IsAdmissibleImageRun(
        InlineContainerBox box,
        IReadOnlyList<(InlineImageRun Run, int Offset)> imageEntries,
        InlineImageRun imageRun,
        ref int imageOrdinal,
        long offset)
    {
        if (imageOrdinal >= imageEntries.Count)
            return false;
        var entry = imageEntries[imageOrdinal++];
        if (!ReferenceEquals(entry.Run, imageRun) || offset > int.MaxValue || entry.Offset != (int)offset ||
            imageRun.Image.BlockIndex != box.BlockIndex)
            return false;

        // Bounding this duplicate check at 64 keeps it allocation-free
        // while rejecting aliases before acquiring any clone leases.
        for (int priorIndex = 0; priorIndex < imageOrdinal - 1; priorIndex++)
        {
            if (ReferenceEquals(imageEntries[priorIndex].Run.Image, imageRun.Image))
                return false;
        }

        return true;
    }

    // Read only renderer-owned identity and scalar state. A placeholder or
    // another measurement-only run cannot become a publishable drawing
    // through this private recipe.
    private static bool IsAdmissibleVectorRun(InlineVectorSceneRun vectorRun)
        => !(vectorRun.IsDisposed || vectorRun.IsMeasuredPlacementOnly ||
            vectorRun.DeferredSceneMarker is not null ||
            !float.IsFinite(vectorRun.DesiredWidth) || vectorRun.DesiredWidth <= 0 ||
            !float.IsFinite(vectorRun.DesiredHeight) || vectorRun.DesiredHeight <= 0 ||
            !float.IsFinite(vectorRun.DesiredBaseline) || vectorRun.DesiredBaseline < 0 ||
            vectorRun.DesiredBaseline > vectorRun.DesiredHeight ||
            !double.IsFinite(vectorRun.LocalBounds.X) || !double.IsFinite(vectorRun.LocalBounds.Y) ||
            !double.IsFinite(vectorRun.LocalBounds.Width) || vectorRun.LocalBounds.Width < 0 ||
            !double.IsFinite(vectorRun.LocalBounds.Height) || vectorRun.LocalBounds.Height < 0);

    private static bool PendingSceneGeometryMatchesRuns(InlineContainerBox box, int vectorOrdinal)
    {
        if (box.PendingSceneGeometry.Length != vectorOrdinal || box.CurrentSceneGeometryGeneration == 0 ||
            box.PendingSceneGeometryGeneration !=
                (box.UsesProgressiveNativeSceneResidency ? box.CurrentSceneGeometryGeneration : 0))
            return false;
        int geometryIndex = 0;
        foreach (InlineRun run in box.RunList)
            if (run is InlineVectorSceneRun vectorRun &&
                !ReferenceEquals(box.PendingSceneGeometry[geometryIndex++].Run, vectorRun))
                return false;
        return true;
    }

    // Unmeasured images can carry only pristine descriptors, never an
    // active load, accepted payload, or shared mutable source owner.
    private static bool UnmeasuredImagesArePristine(
        InlineContainerBox box,
        IReadOnlyList<(InlineImageRun Run, int Offset)> imageEntries,
        bool allowIdleImagePlacements)
    {
        if (!allowIdleImagePlacements)
            return false;
        foreach (var entry in imageEntries)
            if (!ReferenceEquals(entry.Run.Image.Context, box.Context) ||
                !entry.Run.Image.IsCurrentIdleImagePlacement(0))
                return false;
        return true;
    }

    private static bool IsEligibleForPrivateInlinePlacementClone(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        bool allowUnmeasuredTextOnly,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false,
        bool allowMeasuredVectorPlacement = false)
    {
        bool unmeasuredTextOnly = allowUnmeasuredTextOnly && box.TextLayout is null &&
            box.SelectionLayout is null && (box.InlineImageEntries is null || allowIdleImagePlacements) && box.InlineEmbedEntries is null &&
            (!box.HasAtomicInlineObject || (allowIdleImagePlacements && box.InlineImageEntries is not null)) &&
            box.VectorSceneRunCount == 0 && !box.HasDeferredSceneMarker &&
            box.PendingSceneGeometry.Length == 0 && CanCopyPrivateCodeForegroundSpans(box, allowCodeForegroundSpans);
        bool cleanEmptyCell = box.RunList.Count == 0 && box.RunTextLength == 0 &&
            box.LayoutBuffer.Length == 0 && box.InlineImageEntries is null &&
            box.InlineEmbedEntries is null && !box.HasAtomicInlineObject;
        bool zeroLengthBuiltInTextTableCell = IsZeroLengthBuiltInTextTableCell(box);
        if ((!unmeasuredTextOnly &&
                (box.TextLayout is null || box.IsBufferDirty ||
                 (box.LayoutBuffer.Length == 0 && !cleanEmptyCell && !zeroLengthBuiltInTextTableCell) ||
                 box.ResolvedContainerStyle is null)) ||
            !float.IsFinite(availableWidth) || availableWidth <= 0 ||
            box.InlineEmbedEntries is not null ||
            (!allowMeasuredVectorPlacement && box.VectorSceneRunCount != 0) ||
            box.HasDeferredSceneMarker ||
            (!allowMeasuredVectorPlacement && box.PendingSceneGeometry.Length != 0) ||
            !CanCopyPrivateCodeForegroundSpans(box, allowCodeForegroundSpans) ||
            !ArePrivateRasterPlacementContextsCompatible(box.Context, targetContext))
            return false;

        return true;
    }

    private static bool IsZeroLengthBuiltInTextTableCell(InlineContainerBox box)
    {
        if (box.ElementKey is not (MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell) ||
            box.RunList.Count == 0 || box.RunList.Count > InlineContainerBox.MaximumPrivateRasterPlacementRuns ||
            box.RunTextLength != 0 || box.LayoutBuffer.Length != 0 ||
            box.InlineImageEntries is not null || box.InlineEmbedEntries is not null || box.HasAtomicInlineObject ||
            box.VectorSceneRunCount != 0 || box.HasDeferredSceneMarker ||
            box.PendingSceneGeometry.Length != 0 || box.ForegroundSpans.Count != 0)
        {
            return false;
        }

        foreach (InlineRun run in box.RunList)
        {
            if (run is null || !PrivateTextRunPlacement.IsSupported(run) ||
                run.TextLength != 0 || run.RenderedLength != 0 || run.LayoutTextLength != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanCopyPrivateCodeForegroundSpans(InlineContainerBox box, bool allowCodeForegroundSpans)
    {
        if (box.ForegroundSpans.Count == 0)
            return true;

        return allowCodeForegroundSpans &&
            box.ElementKey == MarkdownElementKeys.CodeBlock &&
            box.ForegroundSpans.Count <= InlineContainerBox.MaximumPrivateRasterPlacementRuns;
    }
}
