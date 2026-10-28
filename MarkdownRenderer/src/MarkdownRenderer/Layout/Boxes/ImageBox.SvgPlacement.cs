using System;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class ImageBox
{
    /// <summary>
    /// Immutable accepted-SVG state used only to validate a private bitmap
    /// placement. The source identity is weak; this value never owns source bytes,
    /// a provider/document, tile plan, work owner, or cancellation state.
    /// </summary>
    internal readonly struct CompletedSvgPlacementMetadataFence
    {
        internal CompletedSvgPlacementMetadataFence(
            WeakReference<byte[]> sourceIdentity,
            long sourceByteLength,
            string? anonymousPartitionKey,
            string? preResolutionCacheKey,
            Size intrinsicSize,
            string? title,
            string? description,
            string contentHash,
            string securityPartitionHash,
            MarkdownSvgDocumentInfo info,
            string? activeCacheKey,
            long rendererIdentity,
            long providerCacheGeneration,
            int renderGeneration,
            int loadDemandGeneration,
            int bitmapWidthPixels,
            int bitmapHeightPixels,
            MarkdownSvgPhysicalViewport? physicalViewport,
            MarkdownSvgPhysicalViewport? bitmapPhysicalViewport)
        {
            SourceIdentity = sourceIdentity;
            SourceByteLength = sourceByteLength;
            AnonymousPartitionKey = anonymousPartitionKey;
            PreResolutionCacheKey = preResolutionCacheKey;
            IntrinsicSize = intrinsicSize;
            Title = title;
            Description = description;
            ContentHash = contentHash;
            SecurityPartitionHash = securityPartitionHash;
            Info = info;
            ActiveCacheKey = activeCacheKey;
            RendererIdentity = rendererIdentity;
            ProviderCacheGeneration = providerCacheGeneration;
            RenderGeneration = renderGeneration;
            LoadDemandGeneration = loadDemandGeneration;
            BitmapWidthPixels = bitmapWidthPixels;
            BitmapHeightPixels = bitmapHeightPixels;
            PhysicalViewport = physicalViewport;
            BitmapPhysicalViewport = bitmapPhysicalViewport;
        }

        internal WeakReference<byte[]> SourceIdentity { get; }
        internal long SourceByteLength { get; }
        internal string? AnonymousPartitionKey { get; }
        internal string? PreResolutionCacheKey { get; }
        internal Size IntrinsicSize { get; }
        internal string? Title { get; }
        internal string? Description { get; }
        internal string ContentHash { get; }
        internal string SecurityPartitionHash { get; }
        internal MarkdownSvgDocumentInfo Info { get; }
        internal string? ActiveCacheKey { get; }
        internal long RendererIdentity { get; }
        internal long ProviderCacheGeneration { get; }
        internal int RenderGeneration { get; }
        internal int LoadDemandGeneration { get; }
        internal int BitmapWidthPixels { get; }
        internal int BitmapHeightPixels { get; }
        internal MarkdownSvgPhysicalViewport? PhysicalViewport { get; }
        internal MarkdownSvgPhysicalViewport? BitmapPhysicalViewport { get; }
    }

    /// <summary>
    /// Creates the existing completed-raster placement or, for a strictly
    /// completed full SVG bitmap, returns its immutable metadata fence too.
    /// The original overload remains the raster-only contract for callers that
    /// do not participate in SVG identity validation.
    /// </summary>
    internal bool TryCreateCompletedRasterPlacementClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence)
    {
        svgMetadataFence = null;
        if (TryCreateCompletedRasterPlacementClone(
                targetContext,
                out placement,
                out expectedSourceLease))
        {
            return true;
        }

        placement = null;
        expectedSourceLease = null;
        return TryCreateCompletedSvgPlacementClone(
            targetContext,
            out placement,
            out expectedSourceLease,
            out svgMetadataFence);
    }

    /// <summary>
    /// Revalidates an SVG placement locally after the caller has read the
    /// provider's inexpensive cache generation outside all image/layout locks.
    /// Text-free SVGs intentionally do not depend on that generation, matching
    /// the existing SVG cache policy.
    /// </summary>
    internal bool IsCurrentCompletedRasterPlacement(
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence? svgMetadataFence,
        long currentProviderCacheGeneration)
    {
        ArgumentNullException.ThrowIfNull(expectedSourceLease);
        if (svgMetadataFence is not { } fence)
            return IsCurrentCompletedRasterPlacement(expectedSourceLease);

        if (fence.Info.HasText &&
            fence.ProviderCacheGeneration != currentProviderCacheGeneration)
        {
            return false;
        }

        return IsCompletedSvgPlacementLocallyCurrent(expectedSourceLease, fence);
    }

    private bool TryCreateCompletedSvgPlacementClone(
        MarkdownLayoutContext targetContext,
        out ImageBox? placement,
        out SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out CompletedSvgPlacementMetadataFence? svgMetadataFence)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        placement = null;
        expectedSourceLease = null;
        svgMetadataFence = null;

        if (!IsRasterPlacementContextCompatible(targetContext) ||
            targetContext.ImageCancellationToken.IsCancellationRequested ||
            targetContext.PerformanceSession is IMarkdownPerformanceSessionInternal { IsDisposed: true } ||
            Context.SvgRenderer is null)
        {
            return false;
        }

        targetContext.CancellationToken.ThrowIfCancellationRequested();

        if (!TryCaptureCompletedSvgPlacement(
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
            if (!IsCompletedSvgPlacementLocallyCurrent(sourceLease, fence))
                return false;

            placement = new ImageBox(targetContext, this, candidateLease, fence);
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

    private bool TryCaptureCompletedSvgPlacement(
        out SharedCanvasBitmapCache.Lease? sourceLease,
        out CompletedSvgPlacementMetadataFence fence)
    {
        sourceLease = null;
        fence = default;
        if (IsBitmapOutputDeferred)
            return false;

        lock (_svgWorkSync)
        {
            if (!TryCaptureCompletedSvgPlacementNoLock(out sourceLease, out fence))
                return false;
        }

        return IsCompletedSvgPlacementLocallyCurrent(sourceLease!, fence);
    }

    private bool TryCaptureCompletedSvgPlacementNoLock(
        out SharedCanvasBitmapCache.Lease? sourceLease,
        out CompletedSvgPlacementMetadataFence fence)
    {
        sourceLease = null;
        fence = default;

        if (!IsCompletedSvgPlacementCoreEligibleNoLock() ||
            _svgSourceIdentity is not { } sourceIdentity ||
            _svgSourceByteLength <= 0 ||
            _svgContentHash is not { Length: > 0 } contentHash ||
            _svgSecurityPartitionHash is not { Length: > 0 } securityPartitionHash ||
            _svgInfo is not { } info ||
            _svgBitmapWidthPixels <= 0 ||
            _svgBitmapHeightPixels <= 0 ||
            !IsFinitePositive(_svgIntrinsicSize.Width) ||
            !IsFinitePositive(_svgIntrinsicSize.Height))
        {
            return false;
        }

        SharedCanvasBitmapCache.Lease? lease = Volatile.Read(ref _bitmapLease);
        if (lease is null || _bitmap is null)
            return false;

        sourceLease = lease;
        fence = new CompletedSvgPlacementMetadataFence(
            sourceIdentity,
            _svgSourceByteLength,
            Volatile.Read(ref _svgAnonymousPartitionKey),
            _preResolutionCacheKey,
            _svgIntrinsicSize,
            _svgTitle,
            _svgDesc,
            contentHash,
            securityPartitionHash,
            info,
            Volatile.Read(ref _activeCacheKey),
            _svgRendererIdentity,
            _svgProviderCacheGeneration,
            Volatile.Read(ref _svgRenderGeneration),
            Volatile.Read(ref _svgLoadDemandGeneration),
            _svgBitmapWidthPixels,
            _svgBitmapHeightPixels,
            _svgPhysicalViewport,
            _bitmapPhysicalViewport);
        return true;
    }

    private bool IsCompletedSvgPlacementLocallyCurrent(
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence expected)
    {
        if (IsBitmapOutputDeferred)
            return false;

        lock (_svgWorkSync)
        {
            if (!CompletedSvgPlacementMetadataMatchesNoLock(expectedSourceLease, expected))
                return false;
        }

        if (!AreSvgPlacementAuxiliaryStatesClear() || IsBitmapOutputDeferred)
            return false;

        // Do not nest the image, tile, and host locks. Recheck the image
        // generation after the auxiliary snapshots so a source replacement or
        // newly admitted work cannot cross the metadata fence.
        lock (_svgWorkSync)
        {
            return !IsBitmapOutputDeferred &&
                   CompletedSvgPlacementMetadataMatchesNoLock(expectedSourceLease, expected);
        }
    }

    private bool CompletedSvgPlacementMetadataMatchesNoLock(
        SharedCanvasBitmapCache.Lease expectedSourceLease,
        CompletedSvgPlacementMetadataFence expected)
    {
        return IsCompletedSvgPlacementCoreEligibleNoLock() &&
               ReferenceEquals(Volatile.Read(ref _bitmapLease), expectedSourceLease) &&
               ReferenceEquals(_svgSourceIdentity, expected.SourceIdentity) &&
               _svgSourceByteLength == expected.SourceByteLength &&
               string.Equals(_svgAnonymousPartitionKey, expected.AnonymousPartitionKey, StringComparison.Ordinal) &&
               string.Equals(_preResolutionCacheKey, expected.PreResolutionCacheKey, StringComparison.Ordinal) &&
               _svgIntrinsicSize.Equals(expected.IntrinsicSize) &&
               string.Equals(_svgTitle, expected.Title, StringComparison.Ordinal) &&
               string.Equals(_svgDesc, expected.Description, StringComparison.Ordinal) &&
               string.Equals(_svgContentHash, expected.ContentHash, StringComparison.Ordinal) &&
               string.Equals(_svgSecurityPartitionHash, expected.SecurityPartitionHash, StringComparison.Ordinal) &&
               ReferenceEquals(_svgInfo, expected.Info) &&
               string.Equals(_activeCacheKey, expected.ActiveCacheKey, StringComparison.Ordinal) &&
               _svgRendererIdentity == expected.RendererIdentity &&
               _svgProviderCacheGeneration == expected.ProviderCacheGeneration &&
               Volatile.Read(ref _svgRenderGeneration) == expected.RenderGeneration &&
               Volatile.Read(ref _svgLoadDemandGeneration) == expected.LoadDemandGeneration &&
               _svgBitmapWidthPixels == expected.BitmapWidthPixels &&
               _svgBitmapHeightPixels == expected.BitmapHeightPixels &&
               Nullable.Equals(_svgPhysicalViewport, expected.PhysicalViewport) &&
               Nullable.Equals(_bitmapPhysicalViewport, expected.BitmapPhysicalViewport) &&
               _bitmap is not null;
    }

    private bool IsCompletedSvgPlacementCoreEligibleNoLock()
    {
        return !_disposed &&
               !_resourcesSuspended &&
               !_imageRetirementFinalizing &&
               !_imageRetirementCompleted &&
               _svgWorkCount == 0 &&
               _isSvg &&
               !_loadFailed &&
               _loadStarted &&
               _hasPresentedImage &&
               !_imageLifetimeCancellation.IsCancellationRequested &&
               _svgDocument is null &&
               _svgRendererIdentity > 0 &&
               Volatile.Read(ref _svgTileRenderPlan) is null &&
               Volatile.Read(ref _svgParkingPlan) is null &&
               !_svgUsesTiles &&
               !_svgHasRenderedTile &&
               !_svgPressureParked &&
               !_svgPressureDemandSignatureKnown &&
               _svgFailureReason is null &&
               _svgFailureDescription is null &&
               !Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
               !(_preResolutionCacheKey?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ?? false) &&
               _builtInSource is not { Kind: MarkdownBuiltInImageSourceKind.Data };
    }

    private bool AreSvgPlacementAuxiliaryStatesClear()
    {
        lock (_svgTileLock)
        {
            if (_svgTileCacheTransitionToken is not null ||
                _svgTiles.Count != 0 ||
                _svgTilesInFlight.Count != 0 ||
                _svgTileWorkOwners.Count != 0 ||
                _svgFallbackTiles is not null ||
                _svgFallbackIdentityPrefix is not null ||
                _svgFallbackWidthPixels != 0 ||
                _svgFallbackHeightPixels != 0 ||
                _svgFallbackPhysicalViewport is not null ||
                _svgTileCacheIdentityPrefix is not null ||
                _svgTileCacheWidthPixels != 0 ||
                _svgTileCacheHeightPixels != 0 ||
                _svgTileCachePhysicalViewport is not null ||
                _svgHasRenderedTile)
            {
                return false;
            }
        }

        lock (_svgHostDeferredSync)
        {
            return _svgHostDeferred == SvgHostDeferredNone &&
                   _svgHostDeferredWaiter is null &&
                   _svgHostDeferredWaiterIdentity is null &&
                   _svgHostDeferredRegistrationAttempt is null &&
                   _svgHostDeferredDenialIdentity is null &&
                   _svgHostDeferredAdmission is null &&
                   !_svgHostDeferredCapacityReady;
        }
    }

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;
}
