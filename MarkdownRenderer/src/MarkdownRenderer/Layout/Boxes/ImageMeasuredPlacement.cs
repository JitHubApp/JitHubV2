using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// An image box's measured placement as a transferable unit: its measured
/// geometry, bounds and native placeholder and caption layouts with their
/// retirement records, the fences that prove them unchanged, and the checks
/// that a privately measured clone may hand them to this box.
/// </summary>
/// <remarks>
/// The checks and the swap use only references and scalars: no native API
/// or host callback runs under the placement locks. Members ending in NoLock
/// run with this box's and the other box's image-work and measure locks
/// held, as acquired by <see cref="ImageMeasuredPlacementBatch"/>.
/// </remarks>
internal sealed class ImageMeasuredPlacement
{
    private readonly ImageBox _box;
    private readonly ImageMeasureCustody _measure;

    internal ImageMeasuredPlacement(ImageBox box, ImageMeasureCustody measure)
    {
        _box = box;
        _measure = measure;
    }

    /// <summary>
    /// Captures the image's inline measurement scalars under its placement
    /// lock. These values can be compared with the owning inline run while
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
            Rect bounds = _box.Bounds;
            ImagePresentation presentation = _box.Presentation;
            if (_box.Lifetime.IsDisposed || !_measure.IsInlineLayout || _measure.CheckpointActive ||
                _measure.OriginalPlaceholder is not null ||
                _measure.OriginalCaption is not null ||
                !presentation.HasPresentedImage || presentation.Bitmap is null || presentation.Lease is null ||
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
    /// Compares image-owned native layouts by identity only, to prove that a
    /// private inline container's layouts are not aliased by its images.
    /// </summary>
    internal bool HasNativePlacementLayoutAlias(CanvasTextLayout? candidate)
    {
        lock (_measure.Sync)
        {
            return NativeLayoutsAlias(_measure.Placeholder, candidate) ||
                   NativeLayoutsAlias(_measure.Caption, candidate);
        }
    }

    /// <summary>
    /// Adds the native layouts and their records to preallocated identity
    /// sets. Each must have one cleanup owner across every image and inline
    /// container in the batch; false on a duplicate.
    /// </summary>
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

    /// <summary>Captures the native layouts and records of a settled placement.</summary>
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
                !_box.Lifetime.IsDisposed && !_measure.CheckpointActive &&
                _measure.OriginalPlaceholder is null && _measure.OriginalCaption is null &&
                _measure.HasValidNativeLayoutCustodyNoLock());
            return fence.IsValid;
        }
    }

    /// <summary>True while the native layouts and records are those <paramref name="fence"/> captured.</summary>
    internal bool IsCurrentNativeLayoutPlacementFenceNoLock(NativeLayoutPlacementFence fence) =>
        fence.IsValid &&
        !_box.Lifetime.IsDisposed &&
        !_measure.CheckpointActive &&
        _measure.OriginalPlaceholder is null &&
        _measure.OriginalCaption is null &&
        fence.Generation == _measure.NativeLayoutGeneration &&
        ReferenceEquals(fence.Placeholder, _measure.Placeholder) &&
        ReferenceEquals(fence.PlaceholderRetirementRecord, _measure.PlaceholderRecord) &&
        ReferenceEquals(fence.Caption, _measure.Caption) &&
        ReferenceEquals(fence.CaptionRetirementRecord, _measure.CaptionRecord) &&
        _measure.HasValidNativeLayoutCustodyNoLock();

    /// <summary>
    /// True when <paramref name="prepared"/>, a privately measured clone of
    /// this box at the same available width, may hand its placement to it.
    /// </summary>
    internal bool IsTransferSourceEligibleNoLock(
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? expectedSvgMetadataFence,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth,
        bool expectedInlineLayout)
        => IsTransferSourceEligibleForWidthsNoLock(prepared, expectedSourceLease,
            expectedSvgMetadataFence, currentProviderCacheGeneration,
            expectedAvailableWidth, expectedAvailableWidth, expectedInlineLayout);

    /// <summary>
    /// True when <paramref name="prepared"/> may hand its placement to this
    /// box: both are coherent at their expected widths, present the same
    /// bitmap under distinct leases, own disjoint native layouts, have the
    /// same placement inputs, and remain the same completed raster or match
    /// the same SVG metadata fence.
    /// </summary>
    internal bool IsTransferSourceEligibleForWidthsNoLock(
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? expectedSvgMetadataFence,
        long currentProviderCacheGeneration,
        float expectedAvailableWidth,
        float expectedPreparedAvailableWidth,
        bool expectedInlineLayout)
    {
        SharedCanvasBitmapCache.Lease? preparedLease = prepared.Presentation.Lease;
        if (!HasTransferableMeasurementsNoLock(
                prepared, expectedAvailableWidth, expectedPreparedAvailableWidth, expectedInlineLayout) ||
            !HasTransferableBitmapNoLock(prepared, expectedSourceLease, preparedLease) ||
            !HasMatchingPlacementInputs(prepared))
        {
            return false;
        }

        ImagePlacementEligibility eligibility = _box.PlacementEligibility;
        ImagePlacementEligibility preparedEligibility = prepared.PlacementEligibility;
        if (expectedSvgMetadataFence is { } svgFence)
        {
            return _box.IsSvg && prepared.IsSvg &&
                   (!svgFence.Info.HasText ||
                    svgFence.ProviderCacheGeneration == currentProviderCacheGeneration) &&
                   eligibility.CompletedSvgMetadataMatchesNoLock(expectedSourceLease, svgFence) &&
                   preparedEligibility.CompletedSvgMetadataMatchesNoLock(preparedLease!, svgFence);
        }

        return !_box.IsSvg && !prepared.IsSvg &&
               eligibility.IsCompletedRasterEligibleNoLock() &&
               preparedEligibility.IsCompletedRasterEligibleNoLock() &&
               ReferenceEquals(_box.Presentation.Lease, expectedSourceLease);
    }

    private bool HasTransferableMeasurementsNoLock(
        ImageBox prepared,
        float expectedAvailableWidth,
        float expectedPreparedAvailableWidth,
        bool expectedInlineLayout)
    {
        ImageMeasureCustody preparedMeasure = prepared.MeasureCustody;
        return _measure.HasValidNativeLayoutCustodyNoLock() &&
               preparedMeasure.HasValidNativeLayoutCustodyNoLock() &&
               !_box.Context.ImageCancellationToken.IsCancellationRequested &&
               !prepared.Context.ImageCancellationToken.IsCancellationRequested &&
               !prepared.Context.CancellationToken.IsCancellationRequested &&
               (expectedInlineLayout || (!_box.IsDirty && !prepared.IsDirty)) &&
               _measure.IsInlineLayout == expectedInlineLayout &&
               preparedMeasure.IsInlineLayout == expectedInlineLayout &&
               !_measure.CheckpointActive && !preparedMeasure.CheckpointActive &&
               (!expectedInlineLayout ||
                (_measure.OriginalPlaceholder is null &&
                 _measure.OriginalCaption is null &&
                 preparedMeasure.OriginalPlaceholder is null &&
                 preparedMeasure.OriginalCaption is null)) &&
               HasCoherentMeasuredPlacementNoLock(expectedAvailableWidth, expectedInlineLayout) &&
               prepared.MeasuredPlacement.HasCoherentMeasuredPlacementNoLock(
                   expectedPreparedAvailableWidth, expectedInlineLayout);
    }

    private bool HasTransferableBitmapNoLock(
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        SharedCanvasBitmapCache.Lease? preparedLease)
    {
        ImagePresentation presentation = _box.Presentation;
        ImageMeasureCustody preparedMeasure = prepared.MeasureCustody;
        return presentation.HasPresentedImage && prepared.Presentation.HasPresentedImage &&
               ReferenceEquals(presentation.Lease, expectedSourceLease) &&
               ReferenceEquals(presentation.Bitmap, prepared.Presentation.Bitmap) &&
               preparedLease is not null &&
               !ReferenceEquals(presentation.Lease, preparedLease) &&
               !NativeLayoutsAlias(_measure.Placeholder, preparedMeasure.Placeholder) &&
               !NativeLayoutsAlias(_measure.Caption, preparedMeasure.Caption) &&
               !NativeLayoutsAlias(_measure.Placeholder, preparedMeasure.Caption) &&
               !NativeLayoutsAlias(_measure.Caption, preparedMeasure.Placeholder);
    }

    private bool HasMatchingPlacementInputs(ImageBox other)
    {
        if (_box.BuiltInSource.HasValue != other.BuiltInSource.HasValue)
        {
            return false;
        }

        if (_box.BuiltInSource is { } builtInSource &&
            other.BuiltInSource is { } otherBuiltInSource &&
            (!ReferenceEquals(builtInSource.Uri, otherBuiltInSource.Uri) ||
             !ReferenceEquals(builtInSource.CacheKey, otherBuiltInSource.CacheKey) ||
             builtInSource.Kind != otherBuiltInSource.Kind))
        {
            return false;
        }

        return _box.PlacementEligibility.IsContextCompatible(other.Context) &&
               ReferenceEquals(_box.Url, other.Url) &&
               ReferenceEquals(_box.Alt, other.Alt) &&
               RequestedSizeMatches(_box.RequestedWidth, other.RequestedWidth) &&
               RequestedSizeMatches(_box.RequestedHeight, other.RequestedHeight) &&
               string.Equals(_box.PreResolutionCacheKey, other.PreResolutionCacheKey, StringComparison.Ordinal) &&
               string.Equals(_box.LoadState.ActiveCacheKey, other.LoadState.ActiveCacheKey, StringComparison.Ordinal) &&
               _box.ContentAlignment == other.ContentAlignment &&
               _box.MinimumParagraphLineHeight == other.MinimumParagraphLineHeight &&
               _box.BlockIndex == other.BlockIndex &&
               _box.SafeHtmlInlineFlowScopeId == other.SafeHtmlInlineFlowScopeId &&
               _box.Margin.Left == other.Margin.Left &&
               _box.Margin.Top == other.Margin.Top &&
               _box.Margin.Right == other.Margin.Right &&
               _box.Margin.Bottom == other.Margin.Bottom &&
               _box.HasSamePlacementMargins(other) &&
               _box.Presentation.RasterIntrinsicSize.Equals(other.Presentation.RasterIntrinsicSize) &&
               _box.Svg.IntrinsicSize.Equals(other.Svg.IntrinsicSize);
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

    /// <summary>True while the measured geometry, bounds and caption agree at <paramref name="expectedAvailableWidth"/>.</summary>
    internal bool HasCoherentMeasuredPlacementNoLock(float expectedAvailableWidth, bool expectedInlineLayout)
    {
        if (_measure.IsInlineLayout != expectedInlineLayout)
            return false;

        Rect bounds = _box.Bounds;
        if (expectedInlineLayout)
        {
            return float.IsFinite(_measure.AvailableWidth) && _measure.AvailableWidth == expectedAvailableWidth &&
                   float.IsFinite(_measure.ImageWidth) && _measure.ImageWidth > 0f &&
                   float.IsFinite(_measure.ImageHeight) && _measure.ImageHeight > 0f &&
                   float.IsFinite(_measure.CaptionHeight) && _measure.CaptionHeight == 0f && _measure.Caption is null &&
                   _measure.CaptionRecord is null &&
                   _measure.OriginalPlaceholder is null &&
                   _measure.OriginalCaption is null &&
                   double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                   double.IsFinite(bounds.Width) && bounds.Width == _measure.ImageWidth &&
                   double.IsFinite(bounds.Height) && bounds.Height == _measure.ImageHeight;
        }

        float expectedBoundsHeight =
            Math.Max(_measure.ImageHeight, _box.ParagraphLineHeight) +
            _measure.CaptionHeight +
            (float)(_box.Margin.Top + _box.Margin.Bottom);
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

        if (string.IsNullOrEmpty(_box.Alt))
            return _measure.Caption is null && _measure.CaptionHeight == 0;

        return _measure.Caption is not null && _measure.CaptionHeight > 0;
    }

    /// <summary>
    /// Swaps the measured geometry, bounds, native layouts and records with
    /// <paramref name="prepared"/>, which becomes the sole cleanup owner of
    /// the old placement. Plain reference and scalar swaps only.
    /// </summary>
    internal void SwapWithNoLock(ImageBox prepared)
    {
        ImageMeasureCustody other = prepared.MeasureCustody;
        (_measure.AvailableWidth, other.AvailableWidth) = (other.AvailableWidth, _measure.AvailableWidth);
        (_measure.ImageWidth, other.ImageWidth) = (other.ImageWidth, _measure.ImageWidth);
        (_measure.ImageHeight, other.ImageHeight) = (other.ImageHeight, _measure.ImageHeight);
        (_measure.CaptionHeight, other.CaptionHeight) = (other.CaptionHeight, _measure.CaptionHeight);
        Rect sourceBounds = _box.Bounds;
        _box.SetMeasuredBounds(prepared.Bounds);
        prepared.SetMeasuredBounds(sourceBounds);
        (_measure.Placeholder, other.Placeholder) = (other.Placeholder, _measure.Placeholder);
        (_measure.PlaceholderRecord, other.PlaceholderRecord) = (other.PlaceholderRecord, _measure.PlaceholderRecord);
        (_measure.Caption, other.Caption) = (other.Caption, _measure.Caption);
        (_measure.CaptionRecord, other.CaptionRecord) = (other.CaptionRecord, _measure.CaptionRecord);
        _measure.AdvanceNativeLayoutGenerationNoLock();
        other.AdvanceNativeLayoutGenerationNoLock();
    }

    /// <summary>True when either box's placeholder or caption is the other's.</summary>
    internal static bool NativePlacementLayoutsAlias(ImageBox first, ImageBox second)
    {
        ImageMeasureCustody a = first.MeasureCustody;
        ImageMeasureCustody b = second.MeasureCustody;
        return NativeLayoutsAlias(a.Placeholder, b.Placeholder) ||
               NativeLayoutsAlias(a.Placeholder, b.Caption) ||
               NativeLayoutsAlias(a.Caption, b.Placeholder) ||
               NativeLayoutsAlias(a.Caption, b.Caption);
    }

    /// <summary>True when either box's placeholder or caption record is the other's.</summary>
    internal static bool NativePlacementRetirementRecordsAlias(ImageBox first, ImageBox second)
    {
        ImageMeasureCustody a = first.MeasureCustody;
        ImageMeasureCustody b = second.MeasureCustody;
        return NativeRetirementRecordsAlias(a.PlaceholderRecord, b.PlaceholderRecord) ||
               NativeRetirementRecordsAlias(a.PlaceholderRecord, b.CaptionRecord) ||
               NativeRetirementRecordsAlias(a.CaptionRecord, b.PlaceholderRecord) ||
               NativeRetirementRecordsAlias(a.CaptionRecord, b.CaptionRecord);
    }

    private static bool NativeLayoutsAlias(CanvasTextLayout? first, CanvasTextLayout? second) =>
        first is not null && ReferenceEquals(first, second);

    private static bool NativeRetirementRecordsAlias(
        NativeResourceRetirementRecord? first,
        NativeResourceRetirementRecord? second) =>
        first is not null && ReferenceEquals(first, second);

    private static bool AddNativeLayoutOwner(
        CanvasTextLayout? layout,
        HashSet<CanvasTextLayout> owners) =>
        layout is null || owners.Add(layout);

    private static bool AddRetirementOwner(
        NativeResourceRetirementRecord? record,
        HashSet<NativeResourceRetirementRecord> owners) =>
        record is null || owners.Add(record);
}
