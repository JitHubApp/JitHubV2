using System;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Creates an image box's private placements for a candidate layout: a
/// completed raster or whole SVG bitmap behind an independent lease, or a
/// never-started descriptor.
/// </summary>
/// <remarks>
/// A candidate gets its own current-generation build context and never
/// reads or forwards the source box's old build token. No source bytes,
/// asynchronous work, SVG documents or tiles, or old generation cancellation
/// state cross the boundary; the caller keeps the candidate lease until
/// construction succeeds.
/// </remarks>
internal sealed class ImagePlacementCloner
{
    private readonly ImageBox _box;
    private readonly ImagePlacementEligibility _eligibility;

    internal ImagePlacementCloner(ImageBox box, ImagePlacementEligibility eligibility)
    {
        _box = box;
        _eligibility = eligibility;
    }

    private ImageWorkLifetime Lifetime => _box.Lifetime;

    private ImagePresentation Presentation => _box.Presentation;

    private bool IsTargetUsable(MarkdownLayoutContext targetContext) =>
        _eligibility.IsContextCompatible(targetContext) &&
        !targetContext.ImageCancellationToken.IsCancellationRequested &&
        targetContext.PerformanceSession is not IMarkdownPerformanceSessionInternal { IsDisposed: true };

    /// <summary>Creates a candidate-only clone of the completed raster.</summary>
    internal bool TryCreateCompletedRasterClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        placement = null;
        expectedSourceLease = null;
        if (!IsTargetUsable(targetContext))
            return false;

        targetContext.CancellationToken.ThrowIfCancellationRequested();

        SharedCanvasBitmapCache.Lease? sourceLease;
        lock (Lifetime.Sync)
        {
            if (!_eligibility.IsCompletedRasterEligibleNoLock())
                return false;
            sourceLease = Presentation.Lease;
            if (sourceLease is null || Presentation.Bitmap is null)
                return false;
        }

        SharedCanvasBitmapCache.Lease? candidateLease = sourceLease.TryClone();
        if (candidateLease is null)
            return false;

        try
        {
            bool stillCurrent;
            lock (Lifetime.Sync)
            {
                stillCurrent = _eligibility.IsCompletedRasterEligibleNoLock() &&
                    ReferenceEquals(Presentation.Lease, sourceLease) &&
                    Presentation.Bitmap is not null;
            }
            if (!stillCurrent)
                return false;

            placement = ImageBox.CreateCompletedPlacement(targetContext, _box, candidateLease, svgMetadataFence: null);
            candidateLease = null;
            expectedSourceLease = sourceLease;
            return true;
        }
        finally
        {
            candidateLease?.Dispose();
        }
    }

    /// <summary>
    /// Creates the completed-raster placement or, for a strictly completed
    /// whole SVG bitmap, the SVG placement and its immutable metadata fence.
    /// </summary>
    internal bool TryCreateCompletedClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence)
    {
        svgMetadataFence = null;
        if (TryCreateCompletedRasterClone(
                targetContext,
                out placement,
                out expectedSourceLease))
        {
            return true;
        }

        placement = null;
        expectedSourceLease = null;
        return TryCreateCompletedSvgClone(
            targetContext,
            out placement,
            out expectedSourceLease,
            out svgMetadataFence);
    }

    private bool TryCreateCompletedSvgClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        placement = null;
        expectedSourceLease = null;
        svgMetadataFence = null;

        if (!IsTargetUsable(targetContext) || _box.Context.SvgRenderer is null)
            return false;

        targetContext.CancellationToken.ThrowIfCancellationRequested();

        if (!_eligibility.TryCaptureCompletedSvg(
                out SharedCanvasBitmapCache.Lease? sourceLease,
                out CompletedSvgPlacementMetadataFence fence) ||
            sourceLease is null)
        {
            return false;
        }

        SharedCanvasBitmapCache.Lease? candidateLease = sourceLease.TryClone();
        if (candidateLease is null)
            return false;

        try
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            if (!_eligibility.IsCompletedSvgLocallyCurrent(sourceLease, fence))
                return false;

            placement = ImageBox.CreateCompletedPlacement(targetContext, _box, candidateLease, fence);
            candidateLease = null;
            expectedSourceLease = sourceLease;
            svgMetadataFence = fence;
            return true;
        }
        finally
        {
            candidateLease?.Dispose();
        }
    }

    /// <summary>
    /// Creates a private placement from either a completed immutable bitmap
    /// or a never-started descriptor. The latter is deliberately much
    /// narrower than a pending-load clone: it carries no source task, bytes,
    /// lease, callback, or old cancellation state, and it cannot invoke a
    /// resolver or cache lookup while the source snapshot is being captured.
    /// </summary>
    internal bool TryCreatePrivateClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        placement = null;
        expectedSourceLease = null;
        svgMetadataFence = null;
        isIdleDescriptor = false;
        expectedIdleLoadDemandGeneration = 0;

        if (!IsTargetUsable(targetContext))
            return false;

        targetContext.CancellationToken.ThrowIfCancellationRequested();

        // Preserve the established completed bitmap/SVG paths and their exact
        // lease/metadata fences. Only if there is no completed representation
        // do we consider a resource-free, never-started descriptor.
        if (TryCreateCompletedClone(
                targetContext,
                out ImageBox? completedPlacement,
                out SharedCanvasBitmapCache.Lease? completedLease,
                out CompletedSvgPlacementMetadataFence? completedSvgFence))
        {
            if (completedPlacement is null || completedLease is null)
            {
                if (completedPlacement is not null)
                    targetContext.DiscardConstructionBlock(completedPlacement);
                return false;
            }

            placement = completedPlacement;
            expectedSourceLease = completedLease;
            svgMetadataFence = completedSvgFence;
            return true;
        }

        if (!TryCreateIdleClone(targetContext, out placement))
            return false;

        isIdleDescriptor = true;
        expectedIdleLoadDemandGeneration = 0;
        return true;
    }

    // First observes auxiliary ownership outside the lifecycle locks. The
    // generation/work gate is the authoritative race fence; the second
    // observation prevents a stale waiter/retirement state from being
    // mistaken for a virgin descriptor.
    private bool TryCreateIdleClone(MarkdownLayoutContext targetContext, out ImageBox? placement)
    {
        placement = null;
        if (!_eligibility.AreAuxiliaryStatesClear() ||
            !_box.OutputDeferral.GetState().IsPristine)
        {
            return false;
        }

        ImageBox? idlePlacement = null;
        try
        {
            lock (Lifetime.Sync)
            {
                lock (_box.SvgHostDeferral.Sync)
                {
                    if (!_eligibility.IsUnstartedEligibleNoLock(expectedGeneration: 0))
                        return false;

                    // This private constructor copies only immutable descriptor
                    // values and scalar placement inputs. In particular it never
                    // enters the ordinary constructor's synchronous cache probe.
                    idlePlacement = ImageBox.CreateIdlePlacement(targetContext, _box);
                }
            }

            // Keep construction ownership until every post-construction fence
            // has passed. Cancellation here must still retire the fresh linked
            // lifetime source rather than strand it in the candidate ledger.
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            if (!_eligibility.AreAuxiliaryStatesClear() ||
                !_box.OutputDeferral.GetState().IsPristine ||
                !_eligibility.IsCurrentIdlePlacement(expectedLoadDemandGeneration: 0))
            {
                return false;
            }

            placement = idlePlacement;
            idlePlacement = null;
            return true;
        }
        finally
        {
            if (idlePlacement is not null)
                targetContext.DiscardConstructionBlock(idlePlacement);
        }
    }

    /// <summary>
    /// Allocation-free first-pass classification for a private placement. It
    /// uses the same completed-image and pristine-idle gates as the clone
    /// factory, but never clones a bitmap lease or starts resolver or
    /// provider work.
    /// </summary>
    internal bool TryClassifyPrivate(
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        isIdleDescriptor = false;
        expectedIdleLoadDemandGeneration = 0;

        if (!_eligibility.IsContextCompatible(targetContext) ||
            targetContext.CancellationToken.IsCancellationRequested ||
            targetContext.ImageCancellationToken.IsCancellationRequested ||
            targetContext.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true })
        {
            return false;
        }

        // Do not acquire a lease here. This source snapshot already owns its
        // bitmap reference; the later clone operation repeats these fences and
        // obtains its independent lease only after whole-tree preflight passes.
        lock (Lifetime.Sync)
        {
            if (_eligibility.IsCompletedRasterEligibleNoLock() &&
                Presentation.Lease is not null &&
                Presentation.Bitmap is not null)
            {
                return true;
            }
        }

        // The full-SVG classifier performs only immutable metadata/reference
        // checks. It does not query the provider generation or create a lease.
        if (_box.Context.SvgRenderer is not null &&
            _eligibility.TryCaptureCompletedSvg(
                out SharedCanvasBitmapCache.Lease? completedSvgLease,
                out _) &&
            completedSvgLease is not null)
        {
            return true;
        }

        if (!allowIdleImagePlacements ||
            !_eligibility.AreAuxiliaryStatesClear() ||
            !_box.OutputDeferral.GetState().IsPristine)
        {
            return false;
        }

        lock (Lifetime.Sync)
        {
            lock (_box.SvgHostDeferral.Sync)
            {
                if (!_eligibility.IsUnstartedEligibleNoLock(expectedGeneration: 0))
                    return false;
            }
        }

        // Cancellation can race the lifecycle-lock inspection. Fail closed so
        // the tree preflight never approves a candidate already being abandoned.
        if (targetContext.CancellationToken.IsCancellationRequested ||
            targetContext.ImageCancellationToken.IsCancellationRequested)
        {
            return false;
        }

        isIdleDescriptor = true;
        expectedIdleLoadDemandGeneration = 0;
        return true;
    }
}
