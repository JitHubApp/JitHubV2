using System;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Internal.Images.Placement;

/// <summary>
/// Atomically adopts a bounded batch of privately measured image placements.
/// The caller holds the owning snapshots' exclusive layout gates. Every
/// identity and work fence is checked before the first placement is
/// swapped, so a stale final pair leaves every earlier pair untouched.
/// </summary>
/// <remarks>
/// Lock order: the scope's measured-placement transfer gate, then every
/// pair's image-work locks in batch order (source before prepared), then
/// every pair's measure locks in the same order. The scope's gate serializes
/// overlapping pair sequences. Live admission state is rechecked only after
/// every lock is held; it is short-lived and never calls back into image
/// lifecycle locks.
/// </remarks>
internal static class ImageMeasuredPlacementBatch
{
    /// <summary>Adopts <paramref name="transfers"/>, or none of them.</summary>
    internal static bool TryTransfer(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        if (transfers.IsEmpty ||
            transfers.Length > ImageMeasuredPlacementTransfer.MaximumBatchCount ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0)
        {
            return false;
        }

        // Validate every argument, context, cancellation token, provider
        // metadata requirement, and live output waiter before taking pair locks.
        // The quadratic identity check is bounded to at most 32 entries and
        // avoids temporary HashSet allocations in this commit path.
        ImageResourceScope? batchResources = transfers[0].Source?.Resources;
        for (int index = 0; index < transfers.Length; index++)
        {
            if (!IsPairAdmissible(transfers[index], batchResources, expectedAvailableWidth) ||
                SharesBoxWithEarlierPair(transfers, index))
            {
                return false;
            }
        }

        lock (batchResources!.MeasuredPlacementTransferSync)
        {
            return TryAcquireImageWorkLocks(
                transfers, 0, currentProviderCacheGeneration, expectedAvailableWidth);
        }
    }

    private static bool IsPairAdmissible(
        ImageMeasuredPlacementTransfer transfer,
        ImageResourceScope? batchResources,
        float expectedAvailableWidth)
    {
        ImageBox? source = transfer.Source;
        ImageBox? prepared = transfer.Prepared;
        float pairExpectedWidth = ResolveExpectedAvailableWidth(transfer, expectedAvailableWidth);
        float preparedExpectedWidth = ResolveExpectedPreparedAvailableWidth(transfer, pairExpectedWidth);
        if (source is null || prepared is null || transfer.ExpectedSourceLease is null ||
            ReferenceEquals(source, prepared) ||
            !ReferenceEquals(source.Resources, batchResources) ||
            !ReferenceEquals(prepared.Resources, batchResources) ||
            !float.IsFinite(pairExpectedWidth) || pairExpectedWidth <= 0f ||
            !float.IsFinite(preparedExpectedWidth) || preparedExpectedWidth <= 0f ||
            (!transfer.ExpectedInlineLayout && preparedExpectedWidth != pairExpectedWidth) ||
            !source.PlacementEligibility.IsContextCompatible(prepared.Context) ||
            source.Context.ImageCancellationToken.IsCancellationRequested ||
            prepared.Context.ImageCancellationToken.IsCancellationRequested ||
            prepared.Context.CancellationToken.IsCancellationRequested ||
            source.Context.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true } ||
            prepared.Context.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true } ||
            !source.OutputDeferral.GetState().HasNoLiveWork ||
            !prepared.OutputDeferral.GetState().HasNoLiveWork)
        {
            return false;
        }

        return !transfer.ExpectedSvgMetadataFence.HasValue ||
               (source.Context.SvgRenderer is not null && prepared.Context.SvgRenderer is not null &&
                source.PlacementEligibility.AreAuxiliaryStatesClear() &&
                prepared.PlacementEligibility.AreAuxiliaryStatesClear());
    }

    private static bool SharesBoxWithEarlierPair(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        int index)
    {
        ImageBox source = transfers[index].Source;
        ImageBox prepared = transfers[index].Prepared;
        for (int previous = 0; previous < index; previous++)
        {
            ImageMeasuredPlacementTransfer earlier = transfers[previous];
            if (ReferenceEquals(source, earlier.Source) ||
                ReferenceEquals(source, earlier.Prepared) ||
                ReferenceEquals(prepared, earlier.Source) ||
                ReferenceEquals(prepared, earlier.Prepared))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAcquireImageWorkLocks(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        int index,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        if (index == transfers.Length)
        {
            return TryAcquireMeasureLocks(
                transfers, 0, currentProviderCacheGeneration, expectedAvailableWidth);
        }

        ImageMeasuredPlacementTransfer transfer = transfers[index];
        lock (transfer.Source.Lifetime.Sync)
        lock (transfer.Prepared.Lifetime.Sync)
        {
            return TryAcquireImageWorkLocks(
                transfers, index + 1, currentProviderCacheGeneration, expectedAvailableWidth);
        }
    }

    private static bool TryAcquireMeasureLocks(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        int index,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        if (index == transfers.Length)
        {
            if (!IsLockedBatchValid(transfers, currentProviderCacheGeneration, expectedAvailableWidth))
                return false;

            // All validation is complete. These assignments are plain
            // reference/scalar swaps and cannot invoke native or host code.
            foreach (ImageMeasuredPlacementTransfer committedTransfer in transfers)
                committedTransfer.Source.MeasuredPlacement.SwapWithNoLock(committedTransfer.Prepared);
            return true;
        }

        ImageMeasuredPlacementTransfer transfer = transfers[index];
        lock (transfer.Source.MeasureCustody.Sync)
        lock (transfer.Prepared.MeasureCustody.Sync)
        {
            return TryAcquireMeasureLocks(
                transfers, index + 1, currentProviderCacheGeneration, expectedAvailableWidth);
        }
    }

    // Every lifecycle and placement lock is held: recheck native layout
    // ownership across the batch, each pair's fences, live output work, and
    // transfer eligibility.
    private static bool IsLockedBatchValid(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        for (int check = 0; check < transfers.Length; check++)
        {
            ImageMeasuredPlacementTransfer validatedTransfer = transfers[check];
            for (int previous = 0; previous < check; previous++)
            {
                if (SharesNativeOwner(validatedTransfer, transfers[previous]))
                    return false;
            }

            if (ImageMeasuredPlacement.NativePlacementLayoutsAlias(validatedTransfer.Source, validatedTransfer.Prepared) ||
                ImageMeasuredPlacement.NativePlacementRetirementRecordsAlias(validatedTransfer.Source, validatedTransfer.Prepared) ||
                !IsLockedPairCurrent(validatedTransfer, currentProviderCacheGeneration, expectedAvailableWidth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SharesNativeOwner(
        ImageMeasuredPlacementTransfer transfer,
        ImageMeasuredPlacementTransfer earlier) =>
        ImageMeasuredPlacement.NativePlacementLayoutsAlias(transfer.Source, earlier.Source) ||
        ImageMeasuredPlacement.NativePlacementLayoutsAlias(transfer.Source, earlier.Prepared) ||
        ImageMeasuredPlacement.NativePlacementLayoutsAlias(transfer.Prepared, earlier.Source) ||
        ImageMeasuredPlacement.NativePlacementLayoutsAlias(transfer.Prepared, earlier.Prepared) ||
        ImageMeasuredPlacement.NativePlacementRetirementRecordsAlias(transfer.Source, earlier.Source) ||
        ImageMeasuredPlacement.NativePlacementRetirementRecordsAlias(transfer.Source, earlier.Prepared) ||
        ImageMeasuredPlacement.NativePlacementRetirementRecordsAlias(transfer.Prepared, earlier.Source) ||
        ImageMeasuredPlacement.NativePlacementRetirementRecordsAlias(transfer.Prepared, earlier.Prepared);

    private static bool IsLockedPairCurrent(
        ImageMeasuredPlacementTransfer transfer,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        float sourceWidth = ResolveExpectedAvailableWidth(transfer, expectedAvailableWidth);
        return transfer.ExpectedSourceNativeLayouts.IsValid &&
               transfer.ExpectedPreparedNativeLayouts is { } preparedNativeFence &&
               transfer.Source.MeasuredPlacement.IsCurrentNativeLayoutPlacementFenceNoLock(
                   transfer.ExpectedSourceNativeLayouts) &&
               transfer.Prepared.MeasuredPlacement.IsCurrentNativeLayoutPlacementFenceNoLock(preparedNativeFence) &&
               transfer.Source.OutputDeferral.GetState().HasNoLiveWork &&
               transfer.Prepared.OutputDeferral.GetState().HasNoLiveWork &&
               transfer.Source.MeasuredPlacement.IsTransferSourceEligibleForWidthsNoLock(
                   transfer.Prepared,
                   transfer.ExpectedSourceLease,
                   transfer.ExpectedSvgMetadataFence,
                   currentProviderCacheGeneration,
                   sourceWidth,
                   ResolveExpectedPreparedAvailableWidth(transfer, sourceWidth),
                   transfer.ExpectedInlineLayout);
    }

    private static float ResolveExpectedAvailableWidth(
        ImageMeasuredPlacementTransfer transfer,
        float fallbackWidth) =>
        transfer.ExpectedAvailableWidth == 0f
            ? fallbackWidth
            : transfer.ExpectedAvailableWidth;

    private static float ResolveExpectedPreparedAvailableWidth(
        ImageMeasuredPlacementTransfer transfer,
        float sourceWidth) =>
        transfer.ExpectedPreparedAvailableWidth == 0f
            ? sourceWidth
            : transfer.ExpectedPreparedAvailableWidth;
}
