using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Raster;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;
using SvgPreflight = (Windows.Foundation.Size Intrinsic, string? Title, string? Desc, string ContentHash, string SecurityPartitionHash);
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.MarkdownSvgPhysicalViewport PhysicalViewport);

namespace MarkdownRenderer.Layout.Internal.Images.Loading;

/// <summary>
/// An image box's side of its source loader: the load state the raster and
/// SVG paths read and update, the presentation of a cached, preview or
/// decoded raster, and the SVG publication, failure and document-use
/// operations they call.
/// </summary>
/// <remarks>
/// Raster results are presented on the UI thread by the loader. A decode
/// result that only fills geometry the layout already reserved exactly is
/// published without relayout.
/// </remarks>
internal sealed class ImageSourceLoadHost : IImageSourceLoadHost
{
    private readonly ImageBox _box;

    internal ImageSourceLoadHost(ImageBox box) => _box = box;

    private ImageWorkLifetime Lifetime => _box.Lifetime;

    private ImageLoadState Load => _box.LoadState;

    private ImagePresentation Presentation => _box.Presentation;

    bool IImageSourceLoadHost.IsSvg
    {
        get => Load.IsSvg;
        set => Load.IsSvg = value;
    }

    string? IImageSourceLoadHost.ActiveCacheKey
    {
        get => Load.ActiveCacheKey;
        set => Load.ActiveCacheKey = value;
    }

    MarkdownBuiltInImageSource? IImageSourceLoadHost.BuiltInSource => _box.BuiltInSource;

    void IImageSourceLoadHost.TrackDetachedImageWork(Task detachedOperation, bool failRetirementOnFault) =>
        Lifetime.TrackDetachedWork(detachedOperation, failRetirementOnFault);

    void IImageSourceLoadHost.PresentCachedRasterBitmap(SharedCanvasBitmapCache.Lease cachedLease)
    {
        if (Lifetime.IsDisposed)
        {
            Lifetime.TryCleanup(cachedLease.Dispose);
            return;
        }

        Presentation.Replace(cachedLease.Bitmap, cachedLease);
        _box.RaiseLoadCompleted(layoutInvalidated: true);
    }

    bool IImageSourceLoadHost.TryPublishCachedSvgSource(
        SvgCacheEntry entry, string resolvedCacheKey, BitmapOutputWorkContext? expectedWork) =>
        _box.SvgCachedPublisher.IsCacheEntryCurrent(entry) && _box.SvgCachedPublisher.TryPublish(
            layoutInvalidated: true,
            sourceEntry: entry,
            resolvedCacheKey: resolvedCacheKey,
            expectedWork: expectedWork);

    void IImageSourceLoadHost.PresentSvgDataUriFailure() => _box.FailurePublisher.PresentSvgDataUriFailure();

    bool IImageLoadHost.IsDisposed => Lifetime.IsDisposed;

    bool IImageLoadHost.TryBeginImageWork(out ImageWorkLease? work) => Lifetime.TryBeginWork(out work);

    void IImageLoadHost.TryImageCleanup(Action cleanup) => Lifetime.TryCleanup(cleanup);

    void IImageLoadHost.RecordImageRetirementFailure(Exception exception) => Lifetime.RecordFailure(exception);

    void IImageLoadHost.PublishOnUiThread(Action publish, Action? onDropped) =>
        _box.PublishOnUiThread(publish, onDropped);

    void IImageLoadHost.ReportUnavailable(
        MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgFailureReason) =>
        _box.FailurePublisher.ReportUnavailable(reason, svgFailureReason);

    void IImageLoadHost.PublishFailure(string cacheKey, bool cachePermanently) =>
        _box.FailurePublisher.PublishFailure(cacheKey, cachePermanently);

    void IImageLoadHost.ResetLoadAfterCancellation()
    {
        if (!Lifetime.IsDisposed)
            Load.IsStarted = false;
    }

    double IImageRasterLoadHost.RasterSizingAvailableWidth =>
        _box.MeasureCustody.AvailableWidth - _box.Margin.Left - _box.Margin.Right;

    void IImageRasterLoadHost.PresentRasterPreview(SharedCanvasBitmapCache.Lease cachedPreview)
    {
        if (Presentation.Bitmap is not null)
        {
            Lifetime.TryCleanup(cachedPreview.Dispose);
            return;
        }

        Size previewIntrinsic = cachedPreview.IntrinsicSize!.Value;
        Presentation.RasterIntrinsicSize = previewIntrinsic;
        Presentation.Replace(cachedPreview.Bitmap, cachedPreview);
        _box.RaiseLoadCompleted(layoutInvalidated: true);
    }

    void IImageRasterLoadHost.PresentRasterLoad(in RasterLoadCompletion completion)
    {
        if (Lifetime.IsDisposed)
        {
            if (completion.Lease is { } retiredLease)
                Lifetime.TryCleanup(retiredLease.Dispose);
            if (completion.OwnedBitmap is { } retiredBitmap)
                Lifetime.TryCleanup(retiredBitmap.Dispose);
            return;
        }

        bool exactReservedGeometry = completion.TransformedDecode && !completion.Failed && !completion.DeviceLost &&
            completion.Lease is not null &&
            _box.Context.PerformanceSession is { IsDisposed: false } session &&
            session.Options.PrefetchDocumentImages &&
            session.Options.UseDisplaySizedRasterDecode &&
            _box.HasExactReservedRasterGeometry(completion.IntrinsicSize);
        bool layoutChanged = (Presentation.Bitmap is null && !exactReservedGeometry) ||
            (completion.TransformedDecode &&
             !exactReservedGeometry &&
             (Presentation.RasterIntrinsicSize.Width != completion.IntrinsicSize.Width ||
              Presentation.RasterIntrinsicSize.Height != completion.IntrinsicSize.Height));
        if (completion.Failed && Presentation.Bitmap is null)
        {
            Load.IsFailed = true;
            Load.IsFailureCarryable = completion.CacheRasterDecodeFailure;
            if (completion.CacheRasterDecodeFailure)
                _box.Resources.Failures.RememberFailedRasterDecode(completion.CacheKey, completion.FailureFormatKey, completion.RasterDecodeFailureIdentity);
        }
        else if (completion.DeviceLost)
        {
            Load.IsStarted = false;
        }
        else if (completion.Lease is not null)
        {
            if (completion.TransformedDecode)
                Presentation.RasterIntrinsicSize = completion.IntrinsicSize;
            Presentation.Replace(completion.Lease.Bitmap, completion.Lease);
        }

        _box.RaiseLoadCompleted(layoutInvalidated: layoutChanged);
    }

    long ISvgLoadHost.SvgHostVisualDemandEpoch => _box.Svg.VisualDemandEpoch;

    int ISvgLoadHost.AdvanceSvgRenderGeneration() => _box.Svg.AdvanceRenderGeneration();

    string ISvgLoadHost.GetSvgSourceSecurityPartitionKey(string resolvedCacheKey) =>
        _box.Svg.GetSecurityPartitionKey(resolvedCacheKey);

    bool ISvgLoadHost.IsKnownSvgSource(byte[] rawBytes, string securityPartitionHash) =>
        _box.Svg.IsKnownSource(rawBytes, securityPartitionHash);

    bool ISvgLoadHost.TryGetKnownSvgPreflight(
        byte[] rawBytes, string securityPartitionHash, Size intrinsicHint, out SvgPreflight preflight) =>
        _box.Svg.TryGetKnownPreflight(rawBytes, securityPartitionHash, intrinsicHint, out preflight);

    void ISvgLoadHost.PublishSvgFailure(
        MarkdownSvgException exception, string failureCacheKey, bool cachePermanently, int renderGeneration) =>
        _box.FailurePublisher.PublishSvgFailure(exception, failureCacheKey, cachePermanently, renderGeneration);

    void ISvgLoadHost.PublishSvgFailure(
        MarkdownSvgFailureReason reason, string description, string failureCacheKey,
        bool cachePermanently, int renderGeneration) =>
        _box.FailurePublisher.PublishSvgFailure(reason, description, failureCacheKey, cachePermanently, renderGeneration);

    MarkdownSvgColor ISvgLoadHost.GetSemanticSvgColor() => _box.SvgRasterPlanner.GetSemanticColor();

    MarkdownSvgColorScheme ISvgLoadHost.GetSvgColorScheme() => _box.SvgRasterPlanner.GetColorScheme();

    bool ISvgLoadHost.TryPublishContentCachedSvg(
        string contentCacheKey, string resolvedCacheKey, string? builtInSourceAliasKey,
        bool layoutInvalidated, int renderGeneration, BitmapOutputWorkContext bitmapOutputWork) =>
        _box.SvgCachedPublisher.TryPublishContentCached(
            contentCacheKey, resolvedCacheKey, builtInSourceAliasKey,
            layoutInvalidated, renderGeneration, bitmapOutputWork);

    SvgDocumentUseLease? ISvgLoadHost.TryAcquireCandidateSvgDocumentUse(
        IMarkdownSvgDocument document, out bool ownershipConflict) =>
        _box.SvgDocuments.TryAcquireCandidateUse(document, out ownershipConflict);

    bool ISvgLoadHost.IsSvgProviderCurrent(
        IMarkdownSvgRenderer renderer, long providerCacheGeneration, bool hasText) =>
        _box.SvgCachedPublisher.IsProviderCurrent(renderer, providerCacheGeneration, hasText);

    void ISvgLoadHost.PublishSvgGenerationInvalidated(int renderGeneration) =>
        _box.FailurePublisher.PublishSvgGenerationInvalidated(renderGeneration);

    SvgRasterPlan ISvgLoadHost.PickRasterDimensions(Size intrinsic) =>
        _box.SvgRasterPlanner.PickRasterDimensions(intrinsic);

    string ISvgLoadHost.BuildSvgBitmapIdentity(
        string contentHash, string securityPartitionHash, MarkdownSvgDocumentInfo info, int width, int height,
        MarkdownSvgColorScheme colorScheme, MarkdownSvgColor semanticColor, long rendererIdentity,
        long providerCacheGeneration, MarkdownSvgPhysicalViewport physicalViewport) =>
        _box.SvgRasterPlanner.BuildBitmapIdentity(
            contentHash, securityPartitionHash, info, width, height, colorScheme, semanticColor,
            rendererIdentity, providerCacheGeneration, physicalViewport);

    void ISvgLoadHost.HandleSvgWorkFailure(
        Exception exception, CancellationToken cancellationToken, CancellationToken sessionLifetimeToken,
        string resolvedCacheKey, string svgFailureCacheKey, int renderGeneration) =>
        _box.FailurePublisher.HandleSvgWorkFailure(
            exception, cancellationToken, sessionLifetimeToken, resolvedCacheKey, svgFailureCacheKey, renderGeneration);

    void ISvgLoadHost.RetireUncommittedSvgDocument(IMarkdownSvgDocument document) =>
        _box.SvgDocuments.RetireUncommitted(document);

    (Action Publish, Action? OnDropped) ISvgLoadHost.CreateTiledSvgPublication(
        SvgPublicationMetadata metadata, IMarkdownSvgDocument openedDocument,
        SvgDocumentUseLease candidateUse, long publishRequestedAt) =>
        _box.SvgPublication.CreateTiledPublication(metadata, openedDocument, candidateUse, publishRequestedAt);

    (Action Publish, Action? OnDropped) ISvgLoadHost.CreateBitmapSvgPublication(
        SvgPublicationMetadata metadata, SharedCanvasBitmapCache.Lease publishedLease, long publishRequestedAt) =>
        _box.SvgPublication.CreateBitmapPublication(metadata, publishedLease, publishRequestedAt);

    Task<MarkdownSvgRaster> ISvgLoadHost.RenderSvgDocumentAsync(
        IMarkdownSvgDocument document, MarkdownSvgRenderRequest request, CancellationToken cancellationToken,
        SvgDocumentUseLease parentUse, MarkdownSvgRenderDemand? demand) =>
        _box.SvgDocuments.RenderAsync(document, request, cancellationToken, parentUse, demand);

    void ISvgLoadHost.ValidateSvgRaster(MarkdownSvgRaster raster, int expectedWidth, int expectedHeight) =>
        SvgRasterUpload.Validate(raster, expectedWidth, expectedHeight);

    CanvasBitmap ISvgLoadHost.CreateBitmapFromSvgRaster(
        MarkdownSvgRaster raster, long diagnosticWorkId, long diagnosticSourceBytes,
        long diagnosticTargetPixels, BitmapOutputAdmission.Reservation? bitmapReservation) =>
        SvgRasterUpload.CreateBitmap(
            _box.Context.ResourceCreator, raster, diagnosticWorkId, diagnosticSourceBytes, diagnosticTargetPixels,
            bitmapReservation);
}
