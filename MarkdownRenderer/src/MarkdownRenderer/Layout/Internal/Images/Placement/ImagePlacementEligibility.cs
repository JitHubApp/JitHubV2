using System;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg;

namespace MarkdownRenderer.Layout.Internal.Images.Placement;

/// <summary>
/// Decides whether an image box's state may be copied into a private
/// placement: a completed raster, a completed whole SVG bitmap with its
/// metadata fence, or a never-started descriptor, and whether a placement
/// copied earlier is still current.
/// </summary>
/// <remarks>
/// Context identity is required for every placement. State eligibility is
/// checked separately; no deferred document owner or old-generation work is
/// carried into a private clone. Members ending in NoLock run under the
/// image-work lock; the unstarted-descriptor check also holds the SVG host
/// deferral lock, the same order the load activation uses.
/// </remarks>
internal sealed class ImagePlacementEligibility
{
    private readonly ImageBox _box;
    private readonly ImageWorkLifetime _lifetime;
    private readonly ImageLoadState _load;
    private readonly ImagePresentation _presentation;
    private readonly SvgImageState _svg;

    internal ImagePlacementEligibility(
        ImageBox box,
        ImageWorkLifetime lifetime,
        ImageLoadState load,
        ImagePresentation presentation,
        SvgImageState svg)
    {
        _box = box;
        _lifetime = lifetime;
        _load = load;
        _presentation = presentation;
        _svg = svg;
    }

    private bool HasDataSource =>
        _box.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        (_box.PreResolutionCacheKey?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ?? false) ||
        _box.BuiltInSource is { Kind: MarkdownBuiltInImageSourceKind.Data };

    /// <summary>True when a placement built for <paramref name="target"/> may share this box's resources.</summary>
    internal bool IsContextCompatible(MarkdownLayoutContext target)
    {
        MarkdownLayoutContext context = _box.Context;
        return ReferenceEquals(context.ResourceCreator, target.ResourceCreator) &&
               ReferenceEquals(context.ThemeSnapshot, target.ThemeSnapshot) &&
               ReferenceEquals(context.Registry, target.Registry) &&
               ReferenceEquals(context.Dispatcher, target.Dispatcher) &&
               ReferenceEquals(context.ImageResolver, target.ImageResolver) &&
               ReferenceEquals(context.SvgRenderer, target.SvgRenderer) &&
               ReferenceEquals(context.PerformanceSession, target.PerformanceSession) &&
               ReferenceEquals(context.ImagePreparationOwner, target.ImagePreparationOwner) &&
               ReferenceEquals(context.ImageDocumentSource, target.ImageDocumentSource) &&
               ReferenceEquals(context.SourceMap, target.SourceMap) &&
               context.FlowDirection == target.FlowDirection &&
               context.RasterizationScale == target.RasterizationScale &&
               context.AllowThirdPartyRemoteImages == target.AllowThirdPartyRemoteImages &&
               string.Equals(context.Language, target.Language, StringComparison.Ordinal) &&
               string.Equals(context.ImageDocumentPath, target.ImageDocumentPath, StringComparison.Ordinal) &&
               Equals(context.ImageBaseUri, target.ImageBaseUri);
    }

    /// <summary>True while the box shows a completed, idle raster from a non-data source.</summary>
    internal bool IsCompletedRasterEligibleNoLock()
        => !_lifetime.IsDisposed &&
           !_lifetime.IsSuspended &&
           !_lifetime.IsFinalizing &&
           !_lifetime.IsRetirementCompletedNoLock &&
           _lifetime.WorkCountNoLock == 0 &&
           !_load.IsSvg &&
           !_box.OutputDeferral.IsBitmapOutputDeferred &&
           !_load.IsFailed &&
           _load.IsStarted &&
           !_lifetime.LifetimeCancellation.IsCancellationRequested &&
           !HasDataSource;

    /// <summary>
    /// Final commit fence of a completed raster placement. The candidate
    /// lease keeps the immutable bitmap alive; this rejects source
    /// replacement, suspension or disposal since candidate creation.
    /// </summary>
    internal bool IsCurrentCompletedRaster(SharedCanvasBitmapCache.Lease expectedSourceLease)
    {
        ArgumentNullException.ThrowIfNull(expectedSourceLease);
        lock (_lifetime.Sync)
        {
            return IsCompletedRasterEligibleNoLock() &&
                ReferenceEquals(_presentation.Lease, expectedSourceLease) &&
                _presentation.Bitmap is not null;
        }
    }

    /// <summary>
    /// Revalidates a completed placement after the caller read the
    /// provider's inexpensive cache generation outside all image and layout
    /// locks. Text-free SVGs intentionally do not depend on that generation,
    /// matching the SVG cache policy.
    /// </summary>
    internal bool IsCurrentCompletedPlacement(
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? svgMetadataFence,
        long currentProviderCacheGeneration)
    {
        ArgumentNullException.ThrowIfNull(expectedSourceLease);
        if (svgMetadataFence is not { } fence)
            return IsCurrentCompletedRaster(expectedSourceLease);

        if (fence.Info.HasText &&
            fence.ProviderCacheGeneration != currentProviderCacheGeneration)
        {
            return false;
        }

        return IsCompletedSvgLocallyCurrent(expectedSourceLease, fence);
    }

    /// <summary>
    /// Final UI-commit fence for a descriptor-only clone. A load attempt is
    /// monotonic for the box even when a queued activation later resets its
    /// started flag; the zero demand generation therefore rejects any source
    /// that began resolving while the candidate was being measured.
    /// </summary>
    internal bool IsCurrentIdlePlacement(int expectedLoadDemandGeneration)
    {
        if (expectedLoadDemandGeneration != 0 ||
            !AreAuxiliaryStatesClear() ||
            !_box.OutputDeferral.GetState().IsPristine)
        {
            return false;
        }

        lock (_lifetime.Sync)
        {
            lock (_box.SvgHostDeferral.Sync)
            {
                return IsUnstartedEligibleNoLock(expectedLoadDemandGeneration);
            }
        }
    }

    /// <summary>
    /// True while the box never started, presented, accepted or failed
    /// anything. The caller holds the image-work lock, then the SVG host
    /// deferral lock.
    /// </summary>
    internal bool IsUnstartedEligibleNoLock(int expectedGeneration)
    {
        return expectedGeneration == 0 &&
            !_lifetime.IsDisposed && !_lifetime.IsSuspended &&
            !_lifetime.IsFinalizing && !_lifetime.IsRetirementCompletedNoLock &&
            !_lifetime.HasRetirementBarrierNoLock &&
            _lifetime.WorkCountNoLock == 0 &&
            !_load.IsStarted && !_load.IsFailed &&
            _load.DemandGeneration == expectedGeneration &&
            _load.Demand is null &&
            !_lifetime.LifetimeCancellation.IsCancellationRequested &&
            !_lifetime.HasResidencyCancellationNoLock &&
            _presentation.Bitmap is null && _presentation.Lease is null &&
            !_presentation.HasPresentedImage &&
            _presentation.RasterIntrinsicSize.Width == 0 && _presentation.RasterIntrinsicSize.Height == 0 &&
            _svg.IsPristineNoLock &&
            _box.SvgDocuments.Current is null &&
            !_box.SvgTileCache.HasRenderedTile &&
            !_box.SvgTileCache.PressureDemandSignatureKnown &&
            !HasDataSource;
    }

    /// <summary>True while the SVG tile cache and the SVG host deferral hold nothing.</summary>
    internal bool AreAuxiliaryStatesClear()
    {
        if (!_box.SvgTileCache.IsClear())
            return false;

        return _box.SvgHostDeferral.IsClear();
    }

    /// <summary>
    /// Captures the presented lease and metadata fence of a completed whole
    /// SVG bitmap, then proves them current outside the image-work lock.
    /// </summary>
    internal bool TryCaptureCompletedSvg(
        out SharedCanvasBitmapCache.Lease? sourceLease,
        out CompletedSvgPlacementMetadataFence fence)
    {
        sourceLease = null;
        fence = default;
        if (_box.OutputDeferral.IsBitmapOutputDeferred)
            return false;

        lock (_lifetime.Sync)
        {
            if (!TryCaptureCompletedSvgNoLock(out sourceLease, out fence))
                return false;
        }

        return IsCompletedSvgLocallyCurrent(sourceLease!, fence);
    }

    private bool TryCaptureCompletedSvgNoLock(
        out SharedCanvasBitmapCache.Lease? sourceLease,
        out CompletedSvgPlacementMetadataFence fence)
    {
        sourceLease = null;
        fence = default;

        if (!IsCompletedSvgCoreEligibleNoLock() ||
            _svg.SourceIdentity is not { } sourceIdentity ||
            _svg.SourceByteLength <= 0 ||
            _svg.ContentHash is not { Length: > 0 } contentHash ||
            _svg.SecurityPartitionHash is not { Length: > 0 } securityPartitionHash ||
            _svg.Info is not { } info ||
            _svg.BitmapWidthPixels <= 0 ||
            _svg.BitmapHeightPixels <= 0 ||
            !IsFinitePositive(_svg.IntrinsicSize.Width) ||
            !IsFinitePositive(_svg.IntrinsicSize.Height))
        {
            return false;
        }

        SharedCanvasBitmapCache.Lease? lease = _presentation.Lease;
        if (lease is null || _presentation.Bitmap is null)
            return false;

        sourceLease = lease;
        fence = new CompletedSvgPlacementMetadataFence(
            sourceIdentity,
            _svg.SourceByteLength,
            _svg.AnonymousPartitionKey,
            _box.PreResolutionCacheKey,
            _svg.IntrinsicSize,
            _svg.Title,
            _svg.Description,
            contentHash,
            securityPartitionHash,
            info,
            _load.ActiveCacheKey,
            _svg.RendererIdentity,
            _svg.ProviderCacheGeneration,
            _svg.RenderGeneration,
            _load.DemandGeneration,
            _svg.BitmapWidthPixels,
            _svg.BitmapHeightPixels,
            _svg.PhysicalViewport,
            _presentation.PhysicalViewport);
        return true;
    }

    /// <summary>
    /// True while the completed SVG still matches <paramref name="expected"/>
    /// and no auxiliary state appeared. The image, tile and host locks are
    /// never nested: the image generation is rechecked after the auxiliary
    /// snapshots so a source replacement or newly admitted work cannot cross
    /// the metadata fence.
    /// </summary>
    internal bool IsCompletedSvgLocallyCurrent(
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence expected)
    {
        if (_box.OutputDeferral.IsBitmapOutputDeferred)
            return false;

        lock (_lifetime.Sync)
        {
            if (!CompletedSvgMetadataMatchesNoLock(expectedSourceLease, expected))
                return false;
        }

        if (!AreAuxiliaryStatesClear() || _box.OutputDeferral.IsBitmapOutputDeferred)
            return false;

        lock (_lifetime.Sync)
        {
            return !_box.OutputDeferral.IsBitmapOutputDeferred &&
                   CompletedSvgMetadataMatchesNoLock(expectedSourceLease, expected);
        }
    }

    /// <summary>True while the completed SVG's lease and every accepted value equal <paramref name="expected"/>.</summary>
    internal bool CompletedSvgMetadataMatchesNoLock(
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence expected)
    {
        return IsCompletedSvgCoreEligibleNoLock() &&
               ReferenceEquals(_presentation.Lease, expectedSourceLease) &&
               ReferenceEquals(_svg.SourceIdentity, expected.SourceIdentity) &&
               _svg.SourceByteLength == expected.SourceByteLength &&
               string.Equals(_svg.AnonymousPartitionKey, expected.AnonymousPartitionKey, StringComparison.Ordinal) &&
               string.Equals(_box.PreResolutionCacheKey, expected.PreResolutionCacheKey, StringComparison.Ordinal) &&
               _svg.IntrinsicSize.Equals(expected.IntrinsicSize) &&
               string.Equals(_svg.Title, expected.Title, StringComparison.Ordinal) &&
               string.Equals(_svg.Description, expected.Description, StringComparison.Ordinal) &&
               string.Equals(_svg.ContentHash, expected.ContentHash, StringComparison.Ordinal) &&
               string.Equals(_svg.SecurityPartitionHash, expected.SecurityPartitionHash, StringComparison.Ordinal) &&
               ReferenceEquals(_svg.Info, expected.Info) &&
               string.Equals(_load.ActiveCacheKey, expected.ActiveCacheKey, StringComparison.Ordinal) &&
               _svg.RendererIdentity == expected.RendererIdentity &&
               _svg.ProviderCacheGeneration == expected.ProviderCacheGeneration &&
               _svg.RenderGeneration == expected.RenderGeneration &&
               _load.DemandGeneration == expected.LoadDemandGeneration &&
               _svg.BitmapWidthPixels == expected.BitmapWidthPixels &&
               _svg.BitmapHeightPixels == expected.BitmapHeightPixels &&
               Nullable.Equals(_svg.PhysicalViewport, expected.PhysicalViewport) &&
               Nullable.Equals(_presentation.PhysicalViewport, expected.BitmapPhysicalViewport) &&
               _presentation.Bitmap is not null;
    }

    private bool IsCompletedSvgCoreEligibleNoLock()
    {
        return !_lifetime.IsDisposed &&
               !_lifetime.IsSuspended &&
               !_lifetime.IsFinalizing &&
               !_lifetime.IsRetirementCompletedNoLock &&
               _lifetime.WorkCountNoLock == 0 &&
               _load.IsSvg &&
               !_load.IsFailed &&
               _load.IsStarted &&
               _presentation.HasPresentedImage &&
               !_lifetime.LifetimeCancellation.IsCancellationRequested &&
               _box.SvgDocuments.Current is null &&
               _svg.RendererIdentity > 0 &&
               _svg.AcceptedPlan is null &&
               _svg.ParkingPlan is null &&
               !_svg.UsesTiles &&
               !_box.SvgTileCache.HasRenderedTile &&
               !_svg.IsPressureParked &&
               !_box.SvgTileCache.PressureDemandSignatureKnown &&
               _svg.FailureReason is null &&
               _svg.FailureDescription is null &&
               !HasDataSource;
    }

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;
}
