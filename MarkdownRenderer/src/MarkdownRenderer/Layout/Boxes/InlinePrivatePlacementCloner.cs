using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Builds unmeasured private placement candidates of an inline container:
/// copies its runs, clones completed raster images with their source
/// identity fences, and discards partial work on failure.
/// </summary>
internal static class InlinePrivatePlacementCloner
{
    /// <summary>
    /// Builds a private, unmeasured candidate for a bounded inline sequence
    /// containing at least one completed raster. The caller must separately
    /// prove top-level/suffix ownership and rebuild semantic projections.
    /// Source image/lease outputs are borrowed identity fences; candidate
    /// cleanup owns only cloned ImageBoxes until the caller measures it.
    /// </summary>
    internal static bool TryCreatePrivateRasterImageCandidates(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        candidate = null;
        expectedSources = null;
        if (!box.TryGetPrivateRasterPlacementCounts(
                targetContext, availableWidth, out int runCount, out int imageCount) || imageCount == 0)
            return false;

        return TryCreatePrivateInlinePlacementCloneCore(
            box,
            targetContext, runCount, imageCount, out candidate, out expectedSources);
    }

    internal static bool TryCreatePrivateInlinePlacementClone(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources,
        bool allowUnmeasuredTextOnly = false,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        candidate = null;
        expectedSources = null;
        if (!box.TryGetPrivateRasterPlacementCounts(
                targetContext, availableWidth, out int runCount, out int imageCount, out _,
                allowUnmeasuredTextOnly, allowIdleImagePlacements, allowCodeForegroundSpans))
            return false;

        return TryCreatePrivateInlinePlacementCloneCore(
            box,
            targetContext, runCount, imageCount, out candidate, out expectedSources,
            allowIdleImagePlacements, allowCodeForegroundSpans);
    }

    internal static bool TryCreatePrivateInlinePlacementCloneWithMeasuredVectorPlacement(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        float availableWidth,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources)
    {
        candidate = null;
        expectedSources = null;
        if (!box.TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                targetContext, availableWidth, out int runCount, out int imageCount, out _))
            return false;

        return TryCreatePrivateInlinePlacementCloneCore(
            box,
            targetContext, runCount, imageCount, out candidate, out expectedSources,
            allowMeasuredVectorPlacement: true);
    }

    private static bool TryCreatePrivateInlinePlacementCloneCore(
        InlineContainerBox box,
        MarkdownLayoutContext targetContext,
        int runCount,
        int imageCount,
        out InlineContainerBox? candidate,
        out RasterPlacementSourceFence[]? expectedSources,
        bool allowIdleImagePlacements = false,
        bool allowCodeForegroundSpans = false,
        bool allowMeasuredVectorPlacement = false)
    {
        candidate = null;
        expectedSources = null;
        var copiedRuns = new InlineRun[runCount];
        var sourceFences = new List<RasterPlacementSourceFence>(imageCount);
        var unownedImageClones = new List<ImageBox>(imageCount);
        InlineContainerBox? privateCandidate = null;
        try
        {
            for (int index = 0; index < runCount; index++)
            {
                targetContext.CancellationToken.ThrowIfCancellationRequested();
                if (!TryCopyRun(
                        box.RunList[index],
                        targetContext,
                        allowIdleImagePlacements,
                        allowMeasuredVectorPlacement,
                        unownedImageClones,
                        sourceFences,
                        out copiedRuns[index]))
                {
                    Exception? cleanupFailure = DiscardPrivateRasterCloneConstruction(
                        targetContext, privateCandidate, unownedImageClones);
                    privateCandidate = null;
                    unownedImageClones.Clear();
                    if (cleanupFailure is not null)
                        throw cleanupFailure;
                    return false;
                }

                targetContext.CancellationToken.ThrowIfCancellationRequested();
            }

            privateCandidate = box.CreatePrivatePlacementShell(targetContext, allowCodeForegroundSpans);
            int offset = AdoptCopiedRuns(box, privateCandidate, targetContext, copiedRuns, unownedImageClones);
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            privateCandidate.SetPrivatePlacementRunTextLength(offset);

            if (unownedImageClones.Count != 0 || sourceFences.Count != imageCount)
                throw new InvalidOperationException("The private inline candidate did not take ownership of every cloned image.");

            expectedSources = sourceFences.ToArray();
            candidate = privateCandidate;
            privateCandidate = null;
            return true;
        }
        catch (Exception constructionFailure)
        {
            Exception? cleanupFailure = DiscardPrivateRasterCloneConstruction(
                targetContext, privateCandidate, unownedImageClones);
            if (cleanupFailure is not null)
                TryAttachCleanupFailure(constructionFailure, cleanupFailure);
            throw;
        }
    }

    // Copies one source run for the private candidate. A cloned image is
    // recorded as unowned, with its source fence, before this returns, so a
    // later failure can retire it. Returns false for an unsupported run.
    private static bool TryCopyRun(
        InlineRun sourceRun,
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        bool allowMeasuredVectorPlacement,
        List<ImageBox> unownedImageClones,
        List<RasterPlacementSourceFence> sourceFences,
        out InlineRun copiedRun)
    {
        if (PrivateTextRunPlacement.IsSupported(sourceRun))
        {
            copiedRun = PrivateTextRunPlacement.Clone(sourceRun) ??
                throw new InvalidOperationException("A supported built-in text run omitted its private placement recipe.");
        }
        else if (sourceRun is LinkRun linkRun)
        {
            copiedRun = linkRun.CloneForPrivateRasterPlacement();
        }
        else if (allowMeasuredVectorPlacement && sourceRun is InlineVectorSceneRun vectorRun)
        {
            copiedRun = vectorRun.CloneForMeasuredPlacement();
        }
        else if (sourceRun is InlineImageRun imageRun &&
                 TryCreateImageRunPlacement(imageRun, targetContext, allowIdleImagePlacements,
                     out InlineImageRun? copiedImageRun,
                     out RasterPlacementSourceFence sourceFence) && copiedImageRun is not null)
        {
            copiedRun = copiedImageRun;
            unownedImageClones.Add(copiedImageRun.Image);
            sourceFences.Add(sourceFence);
        }
        else
        {
            copiedRun = null!;
            return false;
        }

        return true;
    }

    // Moves each copied run into the candidate in order; an image leaves the
    // unowned list only once the candidate holds its run.
    private static int AdoptCopiedRuns(
        InlineContainerBox box,
        InlineContainerBox privateCandidate,
        MarkdownLayoutContext targetContext,
        InlineRun[] copiedRuns,
        List<ImageBox> unownedImageClones)
    {
        int offset = 0;
        for (int index = 0; index < copiedRuns.Length; index++)
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            InlineRun copiedRun = copiedRuns[index];
            privateCandidate.AddPrivatePlacementRun(copiedRun);
            if (copiedRun is InlineImageRun copiedImage)
                unownedImageClones.Remove(copiedImage.Image);
            privateCandidate.RecordPrivatePlacementRunState(box, index, copiedRun, offset, copiedRuns.Length);
            offset += copiedRun.TextLength;
        }

        return offset;
    }

    private static bool TryCreateImageRunPlacement(
        InlineImageRun source,
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        out InlineImageRun? clone,
        out RasterPlacementSourceFence fence)
    {
        fence = default;
        ImageBox? image;
        SharedCanvasBitmapCache.Lease? lease;
        CompletedSvgPlacementMetadataFence? metadata;
        bool isIdle = false;
        int idleGeneration = 0;
        bool created = allowIdleImagePlacements
            ? source.TryCreatePrivateImagePlacementClone(targetContext, out clone, out image,
                out lease, out metadata, out isIdle, out idleGeneration)
            : source.TryCreatePrivateRasterPlacementClone(targetContext, out clone, out image,
                out lease, out metadata);
        if (!created || clone is null)
        {
            if (clone is not null)
                targetContext.DiscardConstructionBlock(clone.Image);
            clone = null;
            return false;
        }
        if (image is null || (isIdle ? lease is not null || metadata is not null : lease is null))
        {
            var failure = new InvalidOperationException("A private image clone omitted its source identity fence.");
            try { targetContext.DiscardConstructionBlock(clone.Image); }
            catch (Exception cleanupFailure) { TryAttachCleanupFailure(failure, cleanupFailure); }
            clone = null;
            throw failure;
        }
        fence = new RasterPlacementSourceFence(image, lease, metadata, isIdle, idleGeneration);
        return true;
    }

    private static Exception? DiscardPrivateRasterCloneConstruction(
        MarkdownLayoutContext targetContext,
        InlineContainerBox? privateCandidate,
        List<ImageBox> unownedImageClones)
    {
        Exception? firstFailure = null;
        if (privateCandidate is not null)
        {
            try
            {
                targetContext.DiscardConstructionBlock(privateCandidate);
            }
            catch (Exception exception)
            {
                firstFailure = exception;
            }
        }

        foreach (ImageBox image in unownedImageClones)
        {
            try
            {
                targetContext.DiscardConstructionBlock(image);
            }
            catch (Exception exception)
            {
                if (firstFailure is null)
                    firstFailure = exception;
                else
                {
                    try { firstFailure.Data["MarkdownRenderer.PrivateRasterAdditionalCleanupFailure"] = exception; }
                    catch { /* Cleanup diagnostics must not mask the primary cleanup failure. */ }
                }
            }
        }

        return firstFailure;
    }

    private static void TryAttachCleanupFailure(Exception primaryFailure, Exception cleanupFailure)
    {
        try
        {
            primaryFailure.Data["MarkdownRenderer.PrivateRasterCandidateCleanupFailure"] = cleanupFailure;
        }
        catch
        {
            // Cleanup diagnostics must not mask the candidate failure.
        }
    }
}
