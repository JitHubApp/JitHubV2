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
    internal const int MaximumMeasuredPlacementTransferBatchCount = 32;

    // Only placement swaps are serialized here. Image work and native
    // construction remain outside this gate; it prevents overlapping batches
    // from acquiring disjoint image pairs in opposite orders.
    private static readonly object s_measuredPlacementTransferSync = new();

    internal readonly struct NativeLayoutPlacementFence(
        CanvasTextLayout? Placeholder,
        NativeResourceRetirementRecord? PlaceholderRetirementRecord,
        CanvasTextLayout? Caption,
        NativeResourceRetirementRecord? CaptionRetirementRecord,
        int Generation,
        bool IsValid)
    {
        public CanvasTextLayout? Placeholder { get; init; } = Placeholder;
        public NativeResourceRetirementRecord? PlaceholderRetirementRecord { get; init; } = PlaceholderRetirementRecord;
        public CanvasTextLayout? Caption { get; init; } = Caption;
        public NativeResourceRetirementRecord? CaptionRetirementRecord { get; init; } = CaptionRetirementRecord;
        public int Generation { get; init; } = Generation;
        public bool IsValid { get; init; } = IsValid;
    }

    internal readonly struct MeasuredPlacementTransfer
    {
        internal ImageBox Source { get; init; }
        internal ImageBox Prepared { get; init; }
        internal SharedCanvasBitmapCache.Lease ExpectedSourceLease { get; init; }
        internal CompletedSvgPlacementMetadataFence? ExpectedSvgMetadataFence { get; init; }
        internal bool ExpectedInlineLayout { get; init; }
        internal float ExpectedAvailableWidth { get; init; }
        internal float ExpectedPreparedAvailableWidth { get; init; }
        internal NativeLayoutPlacementFence ExpectedSourceNativeLayouts { get; init; }
        internal NativeLayoutPlacementFence? ExpectedPreparedNativeLayouts { get; init; }

        internal MeasuredPlacementTransfer(
            ImageBox Source,
            ImageBox Prepared,
            SharedCanvasBitmapCache.Lease ExpectedSourceLease,
            CompletedSvgPlacementMetadataFence? ExpectedSvgMetadataFence,
            bool ExpectedInlineLayout = false,
            float ExpectedAvailableWidth = 0f,
            float ExpectedPreparedAvailableWidth = 0f)
        {
            this.Source = Source;
            this.Prepared = Prepared;
            this.ExpectedSourceLease = ExpectedSourceLease;
            this.ExpectedSvgMetadataFence = ExpectedSvgMetadataFence;
            this.ExpectedInlineLayout = ExpectedInlineLayout;
            this.ExpectedAvailableWidth = ExpectedAvailableWidth;
            this.ExpectedPreparedAvailableWidth = ExpectedPreparedAvailableWidth;
            ExpectedSourceNativeLayouts = Source is not null &&
                Source.TryCaptureNativeLayoutPlacementFence(out var fence)
                ? fence
                : default;
            ExpectedPreparedNativeLayouts = Prepared is not null &&
                Prepared.TryCaptureNativeLayoutPlacementFence(out var preparedFence)
                ? preparedFence
                : null;
        }
    }

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
        lock (_inlineMeasureSync)
        {
            availableWidth = 0f;
            imageWidth = 0f;
            imageHeight = 0f;
            Rect bounds = Bounds;
            if (_disposed || !_isInlineLayout || _inlineMeasureCheckpointActive ||
                _inlineMeasureOriginalPlaceholder is not null ||
                _inlineMeasureOriginalCaption is not null ||
                !_hasPresentedImage || _bitmap is null || _bitmapLease is null ||
                !float.IsFinite(_availableWidth) || _availableWidth <= 0f ||
                !float.IsFinite(_imageWidth) || _imageWidth <= 0f ||
                !float.IsFinite(_imageHeight) || _imageHeight <= 0f ||
                !float.IsFinite(_captionHeight) || _captionHeight != 0f || _caption is not null ||
                !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || bounds.Width != _imageWidth ||
                !double.IsFinite(bounds.Height) || bounds.Height != _imageHeight)
            {
                return false;
            }

            availableWidth = _availableWidth;
            imageWidth = _imageWidth;
            imageHeight = _imageHeight;
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
        lock (_inlineMeasureSync)
        {
            return NativeLayoutsAlias(_placeholder, candidate) ||
                   NativeLayoutsAlias(_caption, candidate);
        }
    }

    // The caller preallocates a reference-identity set outside the snapshot
    // gate. Each native layout must have one cleanup owner across every cell
    // and image in the batch, including aliases within one image owner.
    internal bool TryAddNativePlacementLayoutOwners(HashSet<CanvasTextLayout> owners)
    {
        lock (_inlineMeasureSync)
        {
            return (_placeholder is null || owners.Add(_placeholder)) &&
                   (_caption is null || owners.Add(_caption));
        }
    }

    // Root image publication preallocates both sets before taking the final
    // snapshot gate. Records move with their native layouts and must be unique
    // across every image and inline container in the batch.
    internal bool TryAddNativePlacementOwners(
        HashSet<CanvasTextLayout> layoutOwners,
        HashSet<NativeResourceRetirementRecord> retirementOwners)
    {
        lock (_inlineMeasureSync)
        {
            return HasValidNativeLayoutCustodyNoLock() &&
                AddNativeLayoutOwner(_placeholder, layoutOwners) &&
                AddNativeLayoutOwner(_caption, layoutOwners) &&
                AddRetirementOwner(_placeholderRetirementRecord, retirementOwners) &&
                AddRetirementOwner(_captionRetirementRecord, retirementOwners);
        }
    }

    internal bool TryCaptureNativeLayoutPlacementFence(out NativeLayoutPlacementFence fence)
    {
        lock (_inlineMeasureSync)
        {
            fence = new NativeLayoutPlacementFence(
                _placeholder,
                _placeholderRetirementRecord,
                _caption,
                _captionRetirementRecord,
                _nativeLayoutGeneration,
                !_disposed && !_inlineMeasureCheckpointActive &&
                _inlineMeasureOriginalPlaceholder is null && _inlineMeasureOriginalCaption is null &&
                HasValidNativeLayoutCustodyNoLock());
            return fence.IsValid;
        }
    }

    internal bool IsCurrentNativeLayoutPlacementFenceNoLock(NativeLayoutPlacementFence fence) =>
        fence.IsValid &&
        !_disposed &&
        !_inlineMeasureCheckpointActive &&
        _inlineMeasureOriginalPlaceholder is null &&
        _inlineMeasureOriginalCaption is null &&
        fence.Generation == _nativeLayoutGeneration &&
        ReferenceEquals(fence.Placeholder, _placeholder) &&
        ReferenceEquals(fence.PlaceholderRetirementRecord, _placeholderRetirementRecord) &&
        ReferenceEquals(fence.Caption, _caption) &&
        ReferenceEquals(fence.CaptionRetirementRecord, _captionRetirementRecord) &&
        HasValidNativeLayoutCustodyNoLock();

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
        var transfer = new MeasuredPlacementTransfer(
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
        ReadOnlySpan<MeasuredPlacementTransfer> transfers,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        if (transfers.IsEmpty ||
            transfers.Length > MaximumMeasuredPlacementTransferBatchCount ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0)
        {
            return false;
        }

        // Validate every argument, context, cancellation token, provider
        // metadata requirement, and live output waiter before taking pair locks.
        // The quadratic identity check is bounded to at most 32 entries and
        // avoids temporary HashSet allocations in this commit path.
        for (int index = 0; index < transfers.Length; index++)
        {
            MeasuredPlacementTransfer transfer = transfers[index];
            ImageBox? source = transfer.Source;
            ImageBox? prepared = transfer.Prepared;
            SharedCanvasBitmapCache.Lease? expectedLease = transfer.ExpectedSourceLease;
            float pairExpectedWidth = ResolveExpectedAvailableWidth(transfer, expectedAvailableWidth);
            float preparedExpectedWidth = ResolveExpectedPreparedAvailableWidth(transfer, pairExpectedWidth);
            if (source is null || prepared is null || expectedLease is null ||
                ReferenceEquals(source, prepared) ||
                !float.IsFinite(pairExpectedWidth) || pairExpectedWidth <= 0f ||
                !float.IsFinite(preparedExpectedWidth) || preparedExpectedWidth <= 0f ||
                (!transfer.ExpectedInlineLayout && preparedExpectedWidth != pairExpectedWidth) ||
                !source.IsRasterPlacementContextCompatible(prepared.Context) ||
                source.Context.ImageCancellationToken.IsCancellationRequested ||
                prepared.Context.ImageCancellationToken.IsCancellationRequested ||
                prepared.Context.CancellationToken.IsCancellationRequested ||
                source.Context.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true } ||
                prepared.Context.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true } ||
                !source.HasNoLiveBitmapOutputPlacementWork() ||
                !prepared.HasNoLiveBitmapOutputPlacementWork())
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
                MeasuredPlacementTransfer earlier = transfers[previous];
                if (ReferenceEquals(source, earlier.Source) ||
                    ReferenceEquals(source, earlier.Prepared) ||
                    ReferenceEquals(prepared, earlier.Source) ||
                    ReferenceEquals(prepared, earlier.Prepared))
                {
                    return false;
                }
            }
        }

        lock (s_measuredPlacementTransferSync)
        {
            // Every batch uses the same source→prepared ordering within a pair,
            // and the static gate serializes overlapping pair sequences.
            return TryAcquireSvgTransferLocks(
                transfers, 0, currentProviderCacheGeneration, expectedAvailableWidth);
        }
    }

    private static bool TryAcquireSvgTransferLocks(
        ReadOnlySpan<MeasuredPlacementTransfer> transfers,
        int index,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth)
    {
        if (index == transfers.Length)
        {
            return TryAcquireInlineTransferLocks(
                transfers, 0, currentProviderCacheGeneration, expectedAvailableWidth);
        }

        MeasuredPlacementTransfer transfer = transfers[index];
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
        ReadOnlySpan<MeasuredPlacementTransfer> transfers,
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
                MeasuredPlacementTransfer validatedTransfer = transfers[check];
                for (int previous = 0; previous < check; previous++)
                {
                    MeasuredPlacementTransfer earlierTransfer = transfers[previous];
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
                    !validatedTransfer.Source.HasNoLiveBitmapOutputPlacementWork() ||
                    !validatedTransfer.Prepared.HasNoLiveBitmapOutputPlacementWork() ||
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
                MeasuredPlacementTransfer committedTransfer = transfers[commit];
                ImageBox committedSource = committedTransfer.Source;
                ImageBox committedPrepared = committedTransfer.Prepared;
                (committedSource._availableWidth, committedPrepared._availableWidth) =
                    (committedPrepared._availableWidth, committedSource._availableWidth);
                (committedSource._imageWidth, committedPrepared._imageWidth) =
                    (committedPrepared._imageWidth, committedSource._imageWidth);
                (committedSource._imageHeight, committedPrepared._imageHeight) =
                    (committedPrepared._imageHeight, committedSource._imageHeight);
                (committedSource._captionHeight, committedPrepared._captionHeight) =
                    (committedPrepared._captionHeight, committedSource._captionHeight);
                (committedSource.Bounds, committedPrepared.Bounds) =
                    (committedPrepared.Bounds, committedSource.Bounds);
                (committedSource._placeholder, committedPrepared._placeholder) =
                    (committedPrepared._placeholder, committedSource._placeholder);
                (committedSource._placeholderRetirementRecord, committedPrepared._placeholderRetirementRecord) =
                    (committedPrepared._placeholderRetirementRecord, committedSource._placeholderRetirementRecord);
                (committedSource._caption, committedPrepared._caption) =
                    (committedPrepared._caption, committedSource._caption);
                (committedSource._captionRetirementRecord, committedPrepared._captionRetirementRecord) =
                    (committedPrepared._captionRetirementRecord, committedSource._captionRetirementRecord);
                committedSource.AdvanceNativeLayoutGenerationNoLock();
                committedPrepared.AdvanceNativeLayoutGenerationNoLock();
            }
            return true;
        }

        MeasuredPlacementTransfer transfer = transfers[index];
        ImageBox source = transfer.Source;
        ImageBox prepared = transfer.Prepared;
        lock (source._inlineMeasureSync)
        lock (prepared._inlineMeasureSync)
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
        if (!HasValidNativeLayoutCustodyNoLock() ||
            !prepared.HasValidNativeLayoutCustodyNoLock() ||
            Context.ImageCancellationToken.IsCancellationRequested ||
            prepared.Context.ImageCancellationToken.IsCancellationRequested ||
            prepared.Context.CancellationToken.IsCancellationRequested ||
            (!expectedInlineLayout && (IsDirty || prepared.IsDirty)) ||
            _isInlineLayout != expectedInlineLayout ||
            prepared._isInlineLayout != expectedInlineLayout ||
            _inlineMeasureCheckpointActive || prepared._inlineMeasureCheckpointActive ||
            (expectedInlineLayout &&
             (_inlineMeasureOriginalPlaceholder is not null ||
              _inlineMeasureOriginalCaption is not null ||
              prepared._inlineMeasureOriginalPlaceholder is not null ||
              prepared._inlineMeasureOriginalCaption is not null)) ||
            !HasCoherentMeasuredPlacementNoLock(expectedAvailableWidth, expectedInlineLayout) ||
            !prepared.HasCoherentMeasuredPlacementNoLock(expectedPreparedAvailableWidth, expectedInlineLayout) ||
            !_hasPresentedImage || !prepared._hasPresentedImage ||
            !ReferenceEquals(Volatile.Read(ref _bitmapLease), expectedSourceLease) ||
            !ReferenceEquals(_bitmap, prepared._bitmap) ||
            preparedLease is null ||
            ReferenceEquals(_bitmapLease, preparedLease) ||
            NativeLayoutsAlias(_placeholder, prepared._placeholder) ||
            NativeLayoutsAlias(_caption, prepared._caption) ||
            NativeLayoutsAlias(_placeholder, prepared._caption) ||
            NativeLayoutsAlias(_caption, prepared._placeholder) ||
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

    private bool HasNoLiveBitmapOutputPlacementWork()
    {
        lock (_bitmapOutputSync)
        {
            // Successful admission leaves monotonic waiter epochs and cached
            // visibility/tier hints behind. They are historical bookkeeping,
            // not pending ownership. A private placement transfer only needs
            // to exclude live waiters, registrations, deferrals, and retries.
            return _bitmapOutputWaiter is null &&
                _bitmapOutputWaiterOwner is null &&
                _bitmapOutputImageCancellation.Equals(default(CancellationTokenRegistration)) &&
                _bitmapOutputSessionCancellation.Equals(default(CancellationTokenRegistration)) &&
                _bitmapOutputDemand is null &&
                _bitmapOutputRequiredBytes == 0 &&
                _bitmapOutputCapacityVersion == 0 &&
                _bitmapOutputWaiterSlotVersion == 0 &&
                _bitmapOutputDemandGeneration == 0 &&
                _bitmapOutputSvgGeneration == 0 &&
                _bitmapOutputDeferred == 0 &&
                _bitmapOutputNeedsTrim == 0 &&
                _bitmapOutputRegistering == 0 &&
                _bitmapOutputRegistrationNeeded == 0 &&
                _bitmapOutputCapacityReady == 0 &&
                _bitmapOutputRetryQueued == 0 &&
                _bitmapOutputRetryInProgress == 0 &&
                _bitmapOutputRetryOfferPending == 0 &&
                _bitmapOutputRegistrationFailed == 0 &&
                _bitmapOutputCancellationRegistered == 0;
        }
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
        if (_isInlineLayout != expectedInlineLayout)
            return false;

        if (expectedInlineLayout)
        {
            Rect inlineBounds = Bounds;
            return float.IsFinite(_availableWidth) && _availableWidth == expectedAvailableWidth &&
                   float.IsFinite(_imageWidth) && _imageWidth > 0f &&
                   float.IsFinite(_imageHeight) && _imageHeight > 0f &&
                   float.IsFinite(_captionHeight) && _captionHeight == 0f && _caption is null &&
                   _captionRetirementRecord is null &&
                   _inlineMeasureOriginalPlaceholder is null &&
                   _inlineMeasureOriginalCaption is null &&
                   double.IsFinite(inlineBounds.X) && double.IsFinite(inlineBounds.Y) &&
                   double.IsFinite(inlineBounds.Width) && inlineBounds.Width == _imageWidth &&
                   double.IsFinite(inlineBounds.Height) && inlineBounds.Height == _imageHeight;
        }

        Rect bounds = Bounds;
        float expectedBoundsHeight =
            Math.Max(_imageHeight, GetParagraphLineHeight()) +
            _captionHeight +
            (float)(Margin.Top + Margin.Bottom);
        if (!float.IsFinite(_availableWidth) || _availableWidth != expectedAvailableWidth ||
            !float.IsFinite(_imageWidth) || _imageWidth <= 0 ||
            !float.IsFinite(_imageHeight) || _imageHeight <= 0 ||
            !float.IsFinite(_captionHeight) || _captionHeight < 0 ||
            !float.IsFinite(expectedBoundsHeight) ||
            !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || bounds.Width != expectedAvailableWidth ||
            !double.IsFinite(bounds.Height) || bounds.Height != expectedBoundsHeight)
        {
            return false;
        }

        if (string.IsNullOrEmpty(Alt))
            return _caption is null && _captionHeight == 0;

        return _caption is not null && _captionHeight > 0;
    }

    private static float ResolveExpectedAvailableWidth(
        MeasuredPlacementTransfer transfer,
        float fallbackWidth) =>
        transfer.ExpectedAvailableWidth == 0f
            ? fallbackWidth
            : transfer.ExpectedAvailableWidth;

    private static float ResolveExpectedPreparedAvailableWidth(
        MeasuredPlacementTransfer transfer,
        float sourceWidth) =>
        transfer.ExpectedPreparedAvailableWidth == 0f
            ? sourceWidth
            : transfer.ExpectedPreparedAvailableWidth;

    private static bool NativeLayoutsAlias(CanvasTextLayout? first, CanvasTextLayout? second) =>
        first is not null && ReferenceEquals(first, second);

    private static bool NativePlacementLayoutsAlias(ImageBox first, ImageBox second) =>
        NativeLayoutsAlias(first._placeholder, second._placeholder) ||
        NativeLayoutsAlias(first._placeholder, second._caption) ||
        NativeLayoutsAlias(first._caption, second._placeholder) ||
        NativeLayoutsAlias(first._caption, second._caption);

    private static bool NativePlacementRetirementRecordsAlias(ImageBox first, ImageBox second) =>
        NativeRetirementRecordsAlias(first._placeholderRetirementRecord, second._placeholderRetirementRecord) ||
        NativeRetirementRecordsAlias(first._placeholderRetirementRecord, second._captionRetirementRecord) ||
        NativeRetirementRecordsAlias(first._captionRetirementRecord, second._placeholderRetirementRecord) ||
        NativeRetirementRecordsAlias(first._captionRetirementRecord, second._captionRetirementRecord);

    private static bool NativeRetirementRecordsAlias(
        NativeResourceRetirementRecord? first,
        NativeResourceRetirementRecord? second) =>
        first is not null && ReferenceEquals(first, second);
}
