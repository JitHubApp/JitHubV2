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
                InlineRun sourceRun = box.RunList[index];
                if (PrivateTextRunPlacement.IsSupported(sourceRun))
                {
                    copiedRuns[index] = PrivateTextRunPlacement.Clone(sourceRun) ??
                        throw new InvalidOperationException("A supported built-in text run omitted its private placement recipe.");
                }
                else if (sourceRun is LinkRun linkRun)
                {
                    copiedRuns[index] = linkRun.CloneForPrivateRasterPlacement();
                }
                else if (allowMeasuredVectorPlacement && sourceRun is InlineVectorSceneRun vectorRun)
                {
                    copiedRuns[index] = vectorRun.CloneForMeasuredPlacement();
                }
                else if (sourceRun is InlineImageRun imageRun &&
                         TryCreateImageRunPlacement(imageRun, targetContext, allowIdleImagePlacements,
                             out InlineImageRun? copiedImageRun,
                             out RasterPlacementSourceFence sourceFence) && copiedImageRun is not null)
                {
                    copiedRuns[index] = copiedImageRun;
                    unownedImageClones.Add(copiedImageRun.Image);
                    sourceFences.Add(sourceFence);
                }
                else
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
            int offset = 0;
            for (int index = 0; index < copiedRuns.Length; index++)
            {
                targetContext.CancellationToken.ThrowIfCancellationRequested();
                InlineRun copiedRun = copiedRuns[index];
                privateCandidate.AddPrivatePlacementRun(copiedRun);
                if (copiedRun is InlineImageRun copiedImage)
                    unownedImageClones.Remove(copiedImage.Image);
                privateCandidate.RecordPrivatePlacementRunState(box, index, copiedRun, offset, runCount);
                offset += copiedRun.TextLength;
            }
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
        ImageBox.CompletedSvgPlacementMetadataFence? metadata;
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
