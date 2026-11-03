using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class ImageBox
{
    /// <summary>
    /// Captures the exact width used by a completed inline image's atomic
    /// measurement. Callers must still retain the image identity and validate
    /// its lease and enclosing paragraph revision at publication time.
    /// </summary>
    internal bool TryGetCompletedInlinePlacementWidth(out float availableWidth)
    {
        return TryGetCompletedInlinePlacementState(out availableWidth, out _, out _);
    }

    /// <summary>
    /// Captures the image's inline measurement scalars under its placement
    /// lock. These values can be compared with the owning InlineImageRun while
    /// the caller holds the enclosing snapshot gate; no native APIs are used.
    /// </summary>
    internal bool TryGetCompletedInlinePlacementState(
        out float availableWidth,
        out float imageWidth,
        out float imageHeight)
    {
        lock (_measure.Sync)
        {
            availableWidth = 0f;
            imageWidth = 0f;
            imageHeight = 0f;
            Rect bounds = Bounds;
            if (_disposed || !_measure.IsInlineLayout || _measure.CheckpointActive ||
                _measure.OriginalPlaceholder is not null ||
                _measure.OriginalCaption is not null ||
                !_hasPresentedImage || _bitmap is null || _bitmapLease is null ||
                !float.IsFinite(_measure.AvailableWidth) || _measure.AvailableWidth <= 0f ||
                !float.IsFinite(_measure.ImageWidth) || _measure.ImageWidth <= 0f ||
                !float.IsFinite(_measure.ImageHeight) || _measure.ImageHeight <= 0f ||
                !float.IsFinite(_measure.CaptionHeight) || _measure.CaptionHeight != 0f || _measure.Caption is not null ||
                !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || bounds.Width != _measure.ImageWidth ||
                !double.IsFinite(bounds.Height) || bounds.Height != _measure.ImageHeight)
            {
                return false;
            }

            availableWidth = _measure.AvailableWidth;
            imageWidth = _measure.ImageWidth;
            imageHeight = _measure.ImageHeight;
            return true;
        }
    }

    /// <summary>
    /// Compares image-owned native layouts by identity only. The caller may
    /// use this when proving that a private inline container's layouts are
    /// not aliased by its source/prepared images.
    /// </summary>
    internal bool HasNativePlacementLayoutAlias(CanvasTextLayout? candidate)
    {
        lock (_measure.Sync)
        {
            return NativeLayoutsAlias(_measure.Placeholder, candidate) ||
                   NativeLayoutsAlias(_measure.Caption, candidate);
        }
    }

    // The caller preallocates a reference-identity set outside the snapshot
    // gate. Each native layout must have one cleanup owner across every cell
    // and image in the batch, including aliases within one image owner.
    internal bool TryAddNativePlacementLayoutOwners(HashSet<CanvasTextLayout> owners)
    {
        lock (_measure.Sync)
        {
            return (_measure.Placeholder is null || owners.Add(_measure.Placeholder)) &&
                   (_measure.Caption is null || owners.Add(_measure.Caption));
        }
    }

    // Root image publication preallocates both sets before taking the final
    // snapshot gate. Records move with their native layouts and must be unique
    // across every image and inline container in the batch.
    internal bool TryAddNativePlacementOwners(
        HashSet<CanvasTextLayout> layoutOwners,
        HashSet<NativeResourceRetirementRecord> retirementOwners)
    {
        lock (_measure.Sync)
        {
            return _measure.HasValidNativeLayoutCustodyNoLock() &&
                AddNativeLayoutOwner(_measure.Placeholder, layoutOwners) &&
                AddNativeLayoutOwner(_measure.Caption, layoutOwners) &&
                AddRetirementOwner(_measure.PlaceholderRecord, retirementOwners) &&
                AddRetirementOwner(_measure.CaptionRecord, retirementOwners);
        }
    }

    internal bool TryCaptureNativeLayoutPlacementFence(out NativeLayoutPlacementFence fence)
    {
        lock (_measure.Sync)
        {
            fence = new NativeLayoutPlacementFence(
                _measure.Placeholder,
                _measure.PlaceholderRecord,
                _measure.Caption,
                _measure.CaptionRecord,
                _measure.NativeLayoutGeneration,
                !_disposed && !_measure.CheckpointActive &&
                _measure.OriginalPlaceholder is null && _measure.OriginalCaption is null &&
                _measure.HasValidNativeLayoutCustodyNoLock());
            return fence.IsValid;
        }
    }

    internal bool IsCurrentNativeLayoutPlacementFenceNoLock(NativeLayoutPlacementFence fence) =>
        fence.IsValid &&
        !_disposed &&
        !_measure.CheckpointActive &&
        _measure.OriginalPlaceholder is null &&
        _measure.OriginalCaption is null &&
        fence.Generation == _measure.NativeLayoutGeneration &&
        ReferenceEquals(fence.Placeholder, _measure.Placeholder) &&
        ReferenceEquals(fence.PlaceholderRetirementRecord, _measure.PlaceholderRecord) &&
        ReferenceEquals(fence.Caption, _measure.Caption) &&
        ReferenceEquals(fence.CaptionRetirementRecord, _measure.CaptionRecord) &&
        _measure.HasValidNativeLayoutCustodyNoLock();

    private static bool AddNativeLayoutOwner(
        CanvasTextLayout? layout,
        HashSet<CanvasTextLayout> owners) =>
        layout is null || owners.Add(layout);

    private static bool AddRetirementOwner(
        NativeResourceRetirementRecord? record,
        HashSet<NativeResourceRetirementRecord> owners) =>
        record is null || owners.Add(record);

    /// <summary>
    /// Moves only the measured placement owned by <paramref name="prepared"/>
    /// onto this source image. The source image keeps its bitmap/cache lease,
    /// SVG metadata, active work identity, cancellation lifetime, and event
    /// subscribers. The prepared image receives the old placement layouts so
    /// its normal snapshot retirement remains their sole cleanup owner.
    ///
    /// The caller must hold the source snapshot's exclusive layout gate and
    /// must have completed all callback-capable provider reads before calling.
    /// This method performs no native/API calls and invokes no callbacks.
    /// </summary>
    internal bool TryTransferMeasuredPlacementFromCompletedClone(
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? expectedSvgMetadataFence,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expectedSourceLease);
        var transfer = new ImageMeasuredPlacementTransfer(
            this, prepared, expectedSourceLease, expectedSvgMetadataFence);
        return TryTransferMeasuredPlacementsFromCompletedClones(
            MemoryMarshal.CreateReadOnlySpan(ref transfer, 1),
            currentProviderCacheGeneration,
            expectedAvailableWidth);
    }

    /// <summary>
    /// Atomically adopts a bounded set of privately measured image placements.
    /// The caller holds the owning snapshots' exclusive layout gates. All
    /// identities and work fences are checked before the first placement field
    /// is swapped, so a stale final pair leaves every earlier pair untouched.
    /// </summary>
    internal static bool TryTransferMeasuredPlacementsFromCompletedClones(
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
        ImageResourceScope? batchResources = transfers[0].Source?._resources;
        for (int index = 0; index < transfers.Length; index++)
        {
            ImageMeasuredPlacementTransfer transfer = transfers[index];
            ImageBox? source = transfer.Source;
            ImageBox? prepared = transfer.Prepared;
            SharedCanvasBitmapCache.Lease? expectedLease = transfer.ExpectedSourceLease;
            float pairExpectedWidth = ResolveExpectedAvailableWidth(transfer, expectedAvailableWidth);
            float preparedExpectedWidth = ResolveExpectedPreparedAvailableWidth(transfer, pairExpectedWidth);
            if (source is null || prepared is null || expectedLease is null ||
                ReferenceEquals(source, prepared) ||
                !ReferenceEquals(source._resources, batchResources) ||
                !ReferenceEquals(prepared._resources, batchResources) ||
                !float.IsFinite(pairExpectedWidth) || pairExpectedWidth <= 0f ||
                !float.IsFinite(preparedExpectedWidth) || preparedExpectedWidth <= 0f ||
                (!transfer.ExpectedInlineLayout && preparedExpectedWidth != pairExpectedWidth) ||
                !source.IsRasterPlacementContextCompatible(prepared.Context) ||
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

            if (transfer.ExpectedSvgMetadataFence.HasValue &&
                (source.Context.SvgRenderer is null || prepared.Context.SvgRenderer is null ||
                 !source.AreSvgPlacementAuxiliaryStatesClear() ||
                 !prepared.AreSvgPlacementAuxiliaryStatesClear()))
            {
                return false;
            }

            for (int previous = 0; previous < index; previous++)
            {
                ImageMeasuredPlacementTransfer earlier = transfers[previous];
                if (ReferenceEquals(source, earlier.Source) ||
                    ReferenceEquals(source, earlier.Prepared) ||
                    ReferenceEquals(prepared, earlier.Source) ||
                    ReferenceEquals(prepared, earlier.Prepared))
                {
                    return false;
                }
            }
        }

        lock (batchResources!.MeasuredPlacementTransferSync)
        {
            // Every batch uses the same source→prepared ordering within a pair,
            // and the scope's gate serializes overlapping pair sequences.
            return TryAcquireSvgTransferLocks(
                transfers, 0, currentProviderCacheGeneration, expectedAvailableWidth);
        }
    }

    private static bool TryAcquireSvgTransferLocks(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        int index,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        if (index == transfers.Length)
        {
            return TryAcquireInlineTransferLocks(
                transfers, 0, currentProviderCacheGeneration, expectedAvailableWidth);
        }

        ImageMeasuredPlacementTransfer transfer = transfers[index];
        ImageBox source = transfer.Source;
        ImageBox prepared = transfer.Prepared;
        lock (source._svgWorkSync)
        lock (prepared._svgWorkSync)
        {
            return TryAcquireSvgTransferLocks(
                transfers, index + 1, currentProviderCacheGeneration, expectedAvailableWidth);
        }
    }

    private static bool TryAcquireInlineTransferLocks(
        ReadOnlySpan<ImageMeasuredPlacementTransfer> transfers,
        int index,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        if (index == transfers.Length)
        {
            // Recheck all live admission state only after all lifecycle and
            // placement locks are held. Admission state is short-lived and
            // does not call back into image lifecycle locks.
            for (int check = 0; check < transfers.Length; check++)
            {
                ImageMeasuredPlacementTransfer validatedTransfer = transfers[check];
                for (int previous = 0; previous < check; previous++)
                {
                    ImageMeasuredPlacementTransfer earlierTransfer = transfers[previous];
                    if (NativePlacementLayoutsAlias(validatedTransfer.Source, earlierTransfer.Source) ||
                        NativePlacementLayoutsAlias(validatedTransfer.Source, earlierTransfer.Prepared) ||
                        NativePlacementLayoutsAlias(validatedTransfer.Prepared, earlierTransfer.Source) ||
                        NativePlacementLayoutsAlias(validatedTransfer.Prepared, earlierTransfer.Prepared) ||
                        NativePlacementRetirementRecordsAlias(validatedTransfer.Source, earlierTransfer.Source) ||
                        NativePlacementRetirementRecordsAlias(validatedTransfer.Source, earlierTransfer.Prepared) ||
                        NativePlacementRetirementRecordsAlias(validatedTransfer.Prepared, earlierTransfer.Source) ||
                        NativePlacementRetirementRecordsAlias(validatedTransfer.Prepared, earlierTransfer.Prepared))
                    {
                        return false;
                    }
                }

                if (NativePlacementLayoutsAlias(validatedTransfer.Source, validatedTransfer.Prepared) ||
                    NativePlacementRetirementRecordsAlias(validatedTransfer.Source, validatedTransfer.Prepared))
                {
                    return false;
                }

                if (!validatedTransfer.ExpectedSourceNativeLayouts.IsValid ||
                    validatedTransfer.ExpectedPreparedNativeLayouts is not { } preparedNativeFence ||
                    !validatedTransfer.Source.IsCurrentNativeLayoutPlacementFenceNoLock(
                        validatedTransfer.ExpectedSourceNativeLayouts) ||
                    !validatedTransfer.Prepared.IsCurrentNativeLayoutPlacementFenceNoLock(preparedNativeFence) ||
                    !validatedTransfer.Source.OutputDeferral.GetState().HasNoLiveWork ||
                    !validatedTransfer.Prepared.OutputDeferral.GetState().HasNoLiveWork ||
                    !validatedTransfer.Source.IsTransferSourceEligibleForWidthsNoLock(
                        validatedTransfer.Prepared,
                        validatedTransfer.ExpectedSourceLease,
                        validatedTransfer.ExpectedSvgMetadataFence,
                        currentProviderCacheGeneration,
                        ResolveExpectedAvailableWidth(validatedTransfer, expectedAvailableWidth),
                        ResolveExpectedPreparedAvailableWidth(validatedTransfer,
                            ResolveExpectedAvailableWidth(validatedTransfer, expectedAvailableWidth)),
                        validatedTransfer.ExpectedInlineLayout))
                {
                    return false;
                }
            }

            // All validation is complete. These assignments are plain
            // reference/scalar swaps and cannot invoke native or host code.
            for (int commit = 0; commit < transfers.Length; commit++)
            {
                ImageMeasuredPlacementTransfer committedTransfer = transfers[commit];
                ImageBox committedSource = committedTransfer.Source;
                ImageBox committedPrepared = committedTransfer.Prepared;
                (committedSource._measure.AvailableWidth, committedPrepared._measure.AvailableWidth) =
                    (committedPrepared._measure.AvailableWidth, committedSource._measure.AvailableWidth);
                (committedSource._measure.ImageWidth, committedPrepared._measure.ImageWidth) =
                    (committedPrepared._measure.ImageWidth, committedSource._measure.ImageWidth);
                (committedSource._measure.ImageHeight, committedPrepared._measure.ImageHeight) =
                    (committedPrepared._measure.ImageHeight, committedSource._measure.ImageHeight);
                (committedSource._measure.CaptionHeight, committedPrepared._measure.CaptionHeight) =
                    (committedPrepared._measure.CaptionHeight, committedSource._measure.CaptionHeight);
                (committedSource.Bounds, committedPrepared.Bounds) =
                    (committedPrepared.Bounds, committedSource.Bounds);
                (committedSource._measure.Placeholder, committedPrepared._measure.Placeholder) =
                    (committedPrepared._measure.Placeholder, committedSource._measure.Placeholder);
                (committedSource._measure.PlaceholderRecord, committedPrepared._measure.PlaceholderRecord) =
                    (committedPrepared._measure.PlaceholderRecord, committedSource._measure.PlaceholderRecord);
                (committedSource._measure.Caption, committedPrepared._measure.Caption) =
                    (committedPrepared._measure.Caption, committedSource._measure.Caption);
                (committedSource._measure.CaptionRecord, committedPrepared._measure.CaptionRecord) =
                    (committedPrepared._measure.CaptionRecord, committedSource._measure.CaptionRecord);
                committedSource._measure.AdvanceNativeLayoutGenerationNoLock();
                committedPrepared._measure.AdvanceNativeLayoutGenerationNoLock();
            }
            return true;
        }

        ImageMeasuredPlacementTransfer transfer = transfers[index];
        ImageBox source = transfer.Source;
        ImageBox prepared = transfer.Prepared;
        lock (source._measure.Sync)
        lock (prepared._measure.Sync)
        {
            return TryAcquireInlineTransferLocks(
                transfers, index + 1, currentProviderCacheGeneration, expectedAvailableWidth);
        }
    }

    private bool IsTransferSourceEligibleNoLock(
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? expectedSvgMetadataFence,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth,
        bool expectedInlineLayout)
        => IsTransferSourceEligibleForWidthsNoLock(prepared, expectedSourceLease,
            expectedSvgMetadataFence, currentProviderCacheGeneration,
            expectedAvailableWidth, expectedAvailableWidth, expectedInlineLayout);

    private bool IsTransferSourceEligibleForWidthsNoLock(
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? expectedSvgMetadataFence,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth,
        float expectedPreparedAvailableWidth,
        bool expectedInlineLayout)
    {
        SharedCanvasBitmapCache.Lease? preparedLease = prepared._bitmapLease;
        if (!_measure.HasValidNativeLayoutCustodyNoLock() ||
            !prepared._measure.HasValidNativeLayoutCustodyNoLock() ||
            Context.ImageCancellationToken.IsCancellationRequested ||
            prepared.Context.ImageCancellationToken.IsCancellationRequested ||
            prepared.Context.CancellationToken.IsCancellationRequested ||
            (!expectedInlineLayout && (IsDirty || prepared.IsDirty)) ||
            _measure.IsInlineLayout != expectedInlineLayout ||
            prepared._measure.IsInlineLayout != expectedInlineLayout ||
            _measure.CheckpointActive || prepared._measure.CheckpointActive ||
            (expectedInlineLayout &&
             (_measure.OriginalPlaceholder is not null ||
              _measure.OriginalCaption is not null ||
              prepared._measure.OriginalPlaceholder is not null ||
              prepared._measure.OriginalCaption is not null)) ||
            !HasCoherentMeasuredPlacementNoLock(expectedAvailableWidth, expectedInlineLayout) ||
            !prepared.HasCoherentMeasuredPlacementNoLock(expectedPreparedAvailableWidth, expectedInlineLayout) ||
            !_hasPresentedImage || !prepared._hasPresentedImage ||
            !ReferenceEquals(Volatile.Read(ref _bitmapLease), expectedSourceLease) ||
            !ReferenceEquals(_bitmap, prepared._bitmap) ||
            preparedLease is null ||
            ReferenceEquals(_bitmapLease, preparedLease) ||
            NativeLayoutsAlias(_measure.Placeholder, prepared._measure.Placeholder) ||
            NativeLayoutsAlias(_measure.Caption, prepared._measure.Caption) ||
            NativeLayoutsAlias(_measure.Placeholder, prepared._measure.Caption) ||
            NativeLayoutsAlias(_measure.Caption, prepared._measure.Placeholder) ||
            !HasMatchingPlacementInputs(prepared))
        {
            return false;
        }

        if (expectedSvgMetadataFence is { } svgFence)
        {
            if (!_isSvg || !prepared._isSvg ||
                (svgFence.Info.HasText &&
                 svgFence.ProviderCacheGeneration != currentProviderCacheGeneration) ||
                !CompletedSvgPlacementMetadataMatchesNoLock(expectedSourceLease, svgFence) ||
                !prepared.CompletedSvgPlacementMetadataMatchesNoLock(
                    preparedLease,
                    svgFence))
            {
                return false;
            }
        }
        else if (_isSvg || prepared._isSvg ||
                 !IsCompletedRasterPlacementEligibleNoLock() ||
                 !prepared.IsCompletedRasterPlacementEligibleNoLock() ||
                 !ReferenceEquals(_bitmapLease, expectedSourceLease))
        {
            return false;
        }

        return true;
    }

    private bool HasMatchingPlacementInputs(ImageBox other)
    {
        if (_builtInSource.HasValue != other._builtInSource.HasValue)
        {
            return false;
        }

        if (_builtInSource is { } builtInSource &&
            other._builtInSource is { } otherBuiltInSource &&
            (!ReferenceEquals(builtInSource.Uri, otherBuiltInSource.Uri) ||
             !ReferenceEquals(builtInSource.CacheKey, otherBuiltInSource.CacheKey) ||
             builtInSource.Kind != otherBuiltInSource.Kind))
        {
            return false;
        }

        return IsRasterPlacementContextCompatible(other.Context) &&
               ReferenceEquals(Url, other.Url) &&
               ReferenceEquals(Alt, other.Alt) &&
               RequestedSizeMatches(_requestedWidth, other._requestedWidth) &&
               RequestedSizeMatches(_requestedHeight, other._requestedHeight) &&
               string.Equals(_preResolutionCacheKey, other._preResolutionCacheKey, StringComparison.Ordinal) &&
               string.Equals(_activeCacheKey, other._activeCacheKey, StringComparison.Ordinal) &&
               ContentAlignment == other.ContentAlignment &&
               MinimumParagraphLineHeight == other.MinimumParagraphLineHeight &&
               BlockIndex == other.BlockIndex &&
               SafeHtmlInlineFlowScopeId == other.SafeHtmlInlineFlowScopeId &&
               Margin.Left == other.Margin.Left &&
               Margin.Top == other.Margin.Top &&
               Margin.Right == other.Margin.Right &&
               Margin.Bottom == other.Margin.Bottom &&
               RawPlacementMargin.Equals(other.RawPlacementMargin) &&
               CollapsedTopMargin == other.CollapsedTopMargin &&
               SuppressTopMargin == other.SuppressTopMargin &&
               SuppressBottomMargin == other.SuppressBottomMargin &&
               _rasterIntrinsicSize.Equals(other._rasterIntrinsicSize) &&
               _svgIntrinsicSize.Equals(other._svgIntrinsicSize);
    }

    private static bool RequestedSizeMatches(SafeHtmlLength? left, SafeHtmlLength? right)
    {
        if (left.HasValue != right.HasValue)
            return false;
        if (!left.HasValue)
            return true;

        SafeHtmlLength leftValue = left.Value;
        SafeHtmlLength rightValue = right.GetValueOrDefault();
        return leftValue.Value == rightValue.Value &&
               leftValue.IsPercent == rightValue.IsPercent;
    }

    private bool HasCoherentMeasuredPlacementNoLock(float expectedAvailableWidth, bool expectedInlineLayout)
    {
        if (_measure.IsInlineLayout != expectedInlineLayout)
            return false;

        if (expectedInlineLayout)
        {
            Rect inlineBounds = Bounds;
            return float.IsFinite(_measure.AvailableWidth) && _measure.AvailableWidth == expectedAvailableWidth &&
                   float.IsFinite(_measure.ImageWidth) && _measure.ImageWidth > 0f &&
                   float.IsFinite(_measure.ImageHeight) && _measure.ImageHeight > 0f &&
                   float.IsFinite(_measure.CaptionHeight) && _measure.CaptionHeight == 0f && _measure.Caption is null &&
                   _measure.CaptionRecord is null &&
                   _measure.OriginalPlaceholder is null &&
                   _measure.OriginalCaption is null &&
                   double.IsFinite(inlineBounds.X) && double.IsFinite(inlineBounds.Y) &&
                   double.IsFinite(inlineBounds.Width) && inlineBounds.Width == _measure.ImageWidth &&
                   double.IsFinite(inlineBounds.Height) && inlineBounds.Height == _measure.ImageHeight;
        }

        Rect bounds = Bounds;
        float expectedBoundsHeight =
            Math.Max(_measure.ImageHeight, GetParagraphLineHeight()) +
            _measure.CaptionHeight +
            (float)(Margin.Top + Margin.Bottom);
        if (!float.IsFinite(_measure.AvailableWidth) || _measure.AvailableWidth != expectedAvailableWidth ||
            !float.IsFinite(_measure.ImageWidth) || _measure.ImageWidth <= 0 ||
            !float.IsFinite(_measure.ImageHeight) || _measure.ImageHeight <= 0 ||
            !float.IsFinite(_measure.CaptionHeight) || _measure.CaptionHeight < 0 ||
            !float.IsFinite(expectedBoundsHeight) ||
            !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width != expectedAvailableWidth ||
            !double.IsFinite(bounds.Height) || bounds.Height != expectedBoundsHeight)
        {
            return false;
        }

        if (string.IsNullOrEmpty(Alt))
            return _measure.Caption is null && _measure.CaptionHeight == 0;

        return _measure.Caption is not null && _measure.CaptionHeight > 0;
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

    private static bool NativeLayoutsAlias(CanvasTextLayout? first, CanvasTextLayout? second) =>
        first is not null && ReferenceEquals(first, second);

    private static bool NativePlacementLayoutsAlias(ImageBox first, ImageBox second) =>
        NativeLayoutsAlias(first._measure.Placeholder, second._measure.Placeholder) ||
        NativeLayoutsAlias(first._measure.Placeholder, second._measure.Caption) ||
        NativeLayoutsAlias(first._measure.Caption, second._measure.Placeholder) ||
        NativeLayoutsAlias(first._measure.Caption, second._measure.Caption);

    private static bool NativePlacementRetirementRecordsAlias(ImageBox first, ImageBox second) =>
        NativeRetirementRecordsAlias(first._measure.PlaceholderRecord, second._measure.PlaceholderRecord) ||
        NativeRetirementRecordsAlias(first._measure.PlaceholderRecord, second._measure.CaptionRecord) ||
        NativeRetirementRecordsAlias(first._measure.CaptionRecord, second._measure.PlaceholderRecord) ||
        NativeRetirementRecordsAlias(first._measure.CaptionRecord, second._measure.CaptionRecord);

    private static bool NativeRetirementRecordsAlias(
        NativeResourceRetirementRecord? first,
        NativeResourceRetirementRecord? second) =>
        first is not null && ReferenceEquals(first, second);
}
