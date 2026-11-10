using System;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal.Raster;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// Admits one image box's decoded and cached bitmap outputs against its scope's
/// bitmap output governor. A denial for still-current work is recorded with
/// the box's <see cref="ImageBitmapOutputDeferral"/>, which retries the load
/// once capacity returns; a successful claim retires that deferral.
/// </summary>
internal sealed class ImageBitmapOutputAdmission
{
    private readonly IImageBitmapOutputHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputDeferral _deferral;

    internal ImageBitmapOutputAdmission(
        IImageBitmapOutputHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        ImageBitmapOutputDeferral deferral)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(deferral);
        _host = host;
        _context = context;
        _resources = resources;
        _deferral = deferral;
    }

    internal BitmapOutputWorkContext CaptureBitmapOutputWorkContext(
        CancellationToken token,
        bool isExistingSvgTileOutput = false,
        MarkdownSvgRenderDemand? expectedDemand = null,
        int? expectedSvgGeneration = null)
    {
        MarkdownSvgRenderDemand? demand = _host.LoadDemand;
        int loadGeneration = _host.LoadDemandGeneration;
        int svgGeneration = _host.SvgRenderGeneration;
        bool valid = !token.IsCancellationRequested &&
            (expectedDemand is null || ReferenceEquals(expectedDemand, demand)) &&
            (!expectedSvgGeneration.HasValue || expectedSvgGeneration.Value == svgGeneration);
        return new BitmapOutputWorkContext(
            demand,
            loadGeneration,
            svgGeneration,
            token,
            isExistingSvgTileOutput,
            valid);
    }

    internal void RequestOversizedCachedBitmapTrimIfNeeded(SharedCanvasBitmapCache.Lease lease)
    {
        if (_deferral.IsBitmapOutputParticipationEnabled &&
            lease.PixelWeightBytes > _resources.BitmapOutput.MaximumSingleOutputBytes)
        {
            _resources.BitmapCache.RequestBitmapOutputPressureTrim();
        }
    }

    internal bool TryReserveBitmapOutput(
        long bytes,
        out BitmapOutputAdmission.Reservation? reservation,
        bool isExistingSvgTileOutput = false,
        BitmapOutputWorkContext? expectedWork = null)
    {
        reservation = null;
        if (!_deferral.IsBitmapOutputParticipationEnabled)
            return true;

        BitmapOutputAdmission admission = _resources.BitmapOutput;
        if (bytes <= 0 || bytes > admission.MaximumSingleOutputBytes)
            return false;

        if (expectedWork is { } initialWork &&
            (initialWork.IsExistingSvgTileOutput != isExistingSvgTileOutput ||
             !initialWork.IsCurrent(_host)))
        {
            return false;
        }

        GetBitmapOutputDemand(
            isExistingSvgTileOutput,
            expectedWork,
            out bool isVisible,
            out bool isReplacement,
            out MarkdownSvgRenderDemand? demand,
            out int demandGeneration,
            out int svgGeneration);

        if (admission.TryReserve(bytes, isVisible, isReplacement, out reservation, out long version))
        {
            if (expectedWork is { } work)
            {
                if (!work.IsCurrent(_host) ||
                    !_deferral.CompleteBitmapOutputClaim(work))
                {
                    reservation!.Dispose();
                    reservation = null;
                    return false;
                }
            }
            return true;
        }

        if (expectedWork is { } deniedWork && deniedWork.IsCurrent(_host))
        {
            _deferral.RecordBitmapOutputDeferral(
                bytes,
                version,
                deniedWork,
                isVisible,
                isReplacement,
                needsCacheTrim: true);
        }
        return false;
    }

    internal bool TryAdmitCachedBitmapOutput(
        SharedCanvasBitmapCache.Lease lease,
        BitmapOutputWorkContext? expectedWork = null)
        => TryAdmitCachedBitmapOutput(
            lease,
            isExistingSvgTileOutput: false,
            expectedWork: expectedWork);

    internal bool TryAcquireAdmittedSvgBitmap(
        CanvasDevice device,
        string identity,
        out SharedCanvasBitmapCache.Lease? lease,
        bool isExistingSvgTileOutput = false,
        BitmapOutputWorkContext? expectedWork = null)
    {
        if (!_resources.BitmapCache.TryAcquire(device, identity, out lease) || lease is null)
            return false;

        if (TryAdmitCachedBitmapOutput(lease, isExistingSvgTileOutput, expectedWork))
            return true;

        _host.TryImageCleanup(lease.Dispose);
        lease = null;
        return false;
    }

    internal bool TryAdmitCachedBitmapOutput(
        SharedCanvasBitmapCache.Lease lease,
        bool isExistingSvgTileOutput,
        BitmapOutputWorkContext? expectedWork = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_deferral.IsBitmapOutputParticipationEnabled)
            return true;

        if (expectedWork is { } initialWork &&
            (initialWork.IsExistingSvgTileOutput != isExistingSvgTileOutput ||
             !initialWork.IsCurrent(_host)))
        {
            return false;
        }

        BitmapOutputAdmission admission = _resources.BitmapOutput;
        GetBitmapOutputDemand(
            isExistingSvgTileOutput,
            expectedWork,
            out bool isVisible,
            out bool isReplacement,
            out _,
            out _,
            out _);

        if (lease.IsBitmapOutputAdmitted)
        {
            if (expectedWork is { } admittedWork)
                return admittedWork.IsCurrent(_host) && _deferral.CompleteBitmapOutputClaim(admittedWork);
            return true;
        }

        if (lease.TryAdmitBitmapOutput(isVisible, isReplacement, out long requiredBytes, out long version))
        {
            if (expectedWork is { } admittedWork)
            {
                if (!admittedWork.IsCurrent(_host) ||
                    !_deferral.CompleteBitmapOutputClaim(admittedWork))
                {
                    return false;
                }
            }
            return true;
        }

        if (requiredBytes > admission.MaximumSingleOutputBytes)
            _resources.BitmapCache.RequestBitmapOutputPressureTrim();

        if (expectedWork is { } deniedWork && deniedWork.IsCurrent(_host) &&
            deniedWork.Demand is not null && requiredBytes > 0 &&
            requiredBytes <= admission.MaximumSingleOutputBytes)
        {
            _deferral.RecordBitmapOutputDeferral(
                requiredBytes,
                version,
                deniedWork,
                isVisible,
                isReplacement,
                needsCacheTrim: true);
        }

        return false;
    }

    internal bool TryAcquireSourceSizedRasterBitmap(
        string cacheKey, out SharedCanvasBitmapCache.Lease? lease,
        BitmapOutputWorkContext? expectedWork = null)
    {
        MarkdownPerformanceOptions? options = _context.PerformanceSession?.Options;
        lease = null;
        // A source-sized entry may have been created by an eager consumer.
        // Its cache identity grants reuse of the source, not permission to
        // bypass this consumer's physical-size planning or output ceiling.
        // Exact display/static-preview variants remain reusable in the decode
        // path, without starting a decoder or taking a preparation slot.
        if (options?.UseDisplaySizedRasterDecode == true ||
            !_resources.BitmapCache.TryAcquire(_context.ResourceCreator.Device, cacheKey, out lease))
        {
            return false;
        }

        bool withinPixelBudget = options is null || lease!.IsWithinPixelBudget(
            options.ReleaseOffscreenImageResources
                ? Math.Min(options.MaxRasterOutputPixels, _resources.BitmapOutput.MaximumSingleOutputBytes / 4)
                : options.MaxRasterOutputPixels);
        if (withinPixelBudget &&
            (options is null || TryAdmitCachedBitmapOutput(lease!, expectedWork: expectedWork)))
            return true;

        if (!withinPixelBudget)
            RequestOversizedCachedBitmapTrimIfNeeded(lease!);

        _host.TryImageCleanup(lease!.Dispose);
        lease = null;
        return false;
    }

    internal bool TryAcquireRasterBitmap(
        CanvasDevice device,
        string key,
        RasterImageSizingRequest sizingRequest,
        bool transformed,
        out SharedCanvasBitmapCache.Lease? lease,
        BitmapOutputWorkContext? expectedWork = null)
    {
        if (!_resources.BitmapCache.TryAcquire(device, key, out lease))
            return false;
        bool withinPixelBudget = lease!.IsWithinPixelBudget(sizingRequest.MaximumPixels);
        bool matchesSizingRequest = withinPixelBudget &&
            (!transformed || lease.MatchesSizingRequest(sizingRequest));
        if (withinPixelBudget && matchesSizingRequest &&
            TryAdmitCachedBitmapOutput(lease, expectedWork: expectedWork))
            return true;

        RequestOversizedCachedBitmapTrimIfNeeded(lease);

        _host.TryImageCleanup(lease.Dispose);
        lease = null;
        return false;
    }

    internal void ReleasePendingRasterOutputs(
        ref SharedCanvasBitmapCache.Lease? bitmapLease,
        ref CanvasBitmap? ownedBitmap)
    {
        if (bitmapLease is { } lease)
            _host.TryImageCleanup(lease.Dispose);
        bitmapLease = null;
        if (ownedBitmap is { } bitmap)
            _host.TryImageCleanup(bitmap.Dispose);
        ownedBitmap = null;
    }

    private void GetBitmapOutputDemand(
        bool isExistingSvgTileOutput,
        BitmapOutputWorkContext? expectedWork,
        out bool isVisible,
        out bool isReplacement,
        out MarkdownSvgRenderDemand? demand,
        out int demandGeneration,
        out int svgGeneration)
    {
        demand = expectedWork.HasValue
            ? expectedWork.Value.Demand
            : _host.LoadDemand;
        demandGeneration = expectedWork.HasValue
            ? expectedWork.Value.LoadGeneration
            : _host.LoadDemandGeneration;
        svgGeneration = expectedWork.HasValue
            ? expectedWork.Value.SvgGeneration
            : _host.SvgRenderGeneration;
        isVisible = demand?.Priority == MarkdownSvgRenderPriority.Visible ||
            _host.IsViewportVisible;
        isReplacement = _host.HasRetainedBitmapOutput(isExistingSvgTileOutput);
    }

    internal void ReleaseAdmittedBitmap(
        ref CanvasBitmap? bitmap,
        ref BitmapOutputAdmission.Reservation? reservation)
    {
        CanvasBitmap? owned = bitmap;
        BitmapOutputAdmission.Reservation? charge = reservation;
        bitmap = null;
        reservation = null;

        bool cleanupSucceeded = true;
        if (owned is not null)
        {
            try { owned.Dispose(); }
            catch (Exception exception)
            {
                cleanupSucceeded = false;
                _host.RecordImageRetirementFailure(exception);
            }
        }

        if (charge is null)
            return;
        if (!cleanupSucceeded || (owned is null && charge.IsCommitted))
        {
            charge.MarkUncertain();
            return;
        }

        if (owned is not null && charge.IsCommitted)
            charge.ReleaseAfterSuccessfulCleanup();
        else if (owned is null && !charge.IsCommitted)
            charge.Dispose();
        else
            charge.MarkUncertain();
    }
}
