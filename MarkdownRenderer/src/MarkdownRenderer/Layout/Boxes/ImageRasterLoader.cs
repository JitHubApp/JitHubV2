using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Decodes an image box's admitted raster bytes into a device bitmap: budget
/// validation, the decode plan, cache lookups, preparation admission, the
/// portable-provider, owned-WIC, GIF and WinRT decode paths, and the
/// UI-thread handoff of the completed lease. Presentation state stays with the
/// box, which applies the completion through <see cref="IImageRasterLoadHost"/>.
/// </summary>
internal sealed class ImageRasterLoader
{
    private static readonly long RasterDecoderMaximumWorkingBytes = IntPtr.Size == 4
        ? 32L * 1024 * 1024
        : 64L * 1024 * 1024;

    private readonly IImageRasterLoadHost _host;
    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputAdmission _outputAdmission;
    private readonly ImageBitmapOutputDeferral _outputDeferral;
    private readonly SafeHtmlLength? _requestedWidth;
    private readonly SafeHtmlLength? _requestedHeight;

    internal ImageRasterLoader(
        IImageRasterLoadHost host,
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        ImageBitmapOutputAdmission outputAdmission,
        ImageBitmapOutputDeferral outputDeferral,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight)
    {
        _host = host;
        _context = context;
        _resources = resources;
        _outputAdmission = outputAdmission;
        _outputDeferral = outputDeferral;
        _requestedWidth = requestedWidth;
        _requestedHeight = requestedHeight;
    }

    internal Task LoadAsync(
        byte[] bytes, string cacheKey, BitmapOutputWorkContext bitmapOutputWork)
    {
        // A session source-cache hit can complete synchronously inside the
        // viewport's EnsureLoading call. Keep header validation, preview-cache
        // search, WIC decode, and GPU upload off that UI callback even then.
        return _context.Dispatcher?.HasThreadAccess == true
            ? Task.Run(() => LoadCoreAsync(bytes, cacheKey, bitmapOutputWork), CancellationToken.None)
            : LoadCoreAsync(bytes, cacheKey, bitmapOutputWork);
    }

    private async Task LoadCoreAsync(
        byte[] bytes, string cacheKey, BitmapOutputWorkContext bitmapOutputWork)
    {
        if (!_host.TryBeginImageWork(out ImageWorkLease? admittedWork) || admittedWork is null)
            return;
        using ImageWorkLease imageWork = admittedWork;
        CancellationToken imageCancellationToken = imageWork.Token;
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(bytes);
        if (!budget.Accepted && !budget.CanRenderStaticPreview)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] raster rejected before CanvasBitmap decode for {cacheKey}: {budget.Reason}");
            _host.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
            _host.PublishFailure(cacheKey);
            return;
        }
        string failureFormatKey = budget.Format ?? string.Empty;

        MarkdownPerformanceOptions? rasterOptions = _context.PerformanceSession?.Options;
        bool boundedBitmapOutput = rasterOptions?.ReleaseOffscreenImageResources == true;
        bool displaySized = rasterOptions?.UseDisplaySizedRasterDecode == true;
        bool sessionPixelCapApplies = _context.PerformanceSession is { } performanceSession &&
            (long)budget.Width * budget.Height > performanceSession.Options.MaxRasterOutputPixels;
        bool composeGifCanvas = budget.FirstFrame is { } gifFrame &&
            (gifFrame.Left != 0 || gifFrame.Top != 0 ||
             gifFrame.Width != budget.Width || gifFrame.Height != budget.Height);
        IMarkdownRasterDecoder? rasterDecoder = _context.RasterDecoder;
        long rasterDecoderIdentity = rasterDecoder is null ? 0 : _resources.ProviderIdentities.GetRasterDecoderIdentity(rasterDecoder);
        bool rasterProviderSupportsFormat = false;
        MarkdownRasterFormat rasterFormat = default;
        bool hasStaticProviderFormat = budget.FrameCount == 1 && budget.FirstFrame is null &&
            TryGetMarkdownRasterFormat(budget.Format, out rasterFormat);
        if (hasStaticProviderFormat && rasterDecoder is not null)
        {
            try
            {
                rasterProviderSupportsFormat = rasterDecoder.SupportsFormat(rasterFormat);
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[ImageBox] raster decoder capability check failed for {cacheKey}: {exception.GetType().Name}.");
                _host.ReportUnavailable(MarkdownImageUnavailableReason.Unavailable);
                _host.PublishFailure(cacheKey: string.Empty);
                return;
            }
        }
        // A registered portable provider receives exact physical display
        // geometry even when display-sized previews are not enabled for other
        // image formats. Formats without a valid static plan stay on the
        // existing platform decode path.
        bool legacySizingPolicyApplies = displaySized || budget.CanRenderStaticPreview || sessionPixelCapApplies;
        bool sizingPolicyApplies = legacySizingPolicyApplies ||
            rasterProviderSupportsFormat;
        // Participating consumers always use WIC's explicit bounded transform.
        // The shared budget reserves both the GIF canvas and its temporary
        // frame; legacy consumers retain their existing eager decode behavior.
        bool transformedDecode = sizingPolicyApplies || composeGifCanvas || boundedBitmapOutput;
        long maximumOutputPixels = Math.Min(RasterImageResourceBudget.MaxPixelsPerFrame,
            rasterOptions?.MaxRasterOutputPixels ?? RasterImageResourceBudget.MaxPixelsPerFrame);
        if (boundedBitmapOutput)
            maximumOutputPixels = Math.Min(maximumOutputPixels,
                _resources.BitmapOutput.MaximumSingleOutputBytes / (composeGifCanvas ? 8 : 4));
        var sizingRequest = new RasterImageSizingRequest(
            sizingPolicyApplies ? _host.RasterSizingAvailableWidth : budget.Width,
            sizingPolicyApplies ? _context.RasterizationScale : 1d,
            sizingPolicyApplies ? _requestedWidth : null,
            sizingPolicyApplies ? _requestedHeight : null,
            maximumOutputPixels);
        (int Width, int Height) rasterSize = transformedDecode
            ? sizingRequest.GetSize(budget.Width, budget.Height)
            : default;
        RasterImageDecodePlan nativePlan = default;
        bool nativePlanAvailable = transformedDecode &&
            RasterImageDecodePlan.TryCreate(bytes, budget, sizingRequest, out nativePlan, imageCancellationToken);
        bool portableRasterDecode = rasterProviderSupportsFormat && nativePlanAvailable;
        // Capability alone must not alter legacy behavior. If an otherwise
        // eligible format cannot produce the host's validated static plan,
        // retry planning with the caller's pre-existing transform policy and
        // leave decode selection to the established platform path.
        if (rasterProviderSupportsFormat && !portableRasterDecode && !legacySizingPolicyApplies)
        {
            sizingPolicyApplies = false;
            transformedDecode = composeGifCanvas || boundedBitmapOutput;
            sizingRequest = new RasterImageSizingRequest(
                budget.Width,
                1d,
                null,
                null,
                maximumOutputPixels);
            rasterSize = transformedDecode
                ? sizingRequest.GetSize(budget.Width, budget.Height)
                : default;
            nativePlan = default;
            nativePlanAvailable = transformedDecode &&
                RasterImageDecodePlan.TryCreate(bytes, budget, sizingRequest, out nativePlan, imageCancellationToken);
        }
        bool nativeDecode = nativePlanAvailable && !portableRasterDecode &&
            RasterNativeWicDecoder.IsQualifiedPlan(nativePlan);
        if (nativeDecode)
            rasterSize = (nativePlan.OutputWidth, nativePlan.OutputHeight);
        else if (portableRasterDecode)
            rasterSize = (nativePlan.OutputWidth, nativePlan.OutputHeight);
        RasterGifFirstFrameDecodePlan nativeGifPlan = default;
        bool nativeGifDecode = transformedDecode && budget.Format == "GIF" &&
            RasterGifFirstFrameDecodePlan.TryCreate(bytes, budget, sizingRequest,
                out nativeGifPlan, imageCancellationToken);
        if (nativeGifDecode)
            rasterSize = (nativeGifPlan.OutputWidth, nativeGifPlan.OutputHeight);
        long rasterDecodeFailureIdentity = portableRasterDecode ? rasterDecoderIdentity : 0;
        if (_resources.Failures.HasFailedRasterDecode(cacheKey, failureFormatKey, rasterDecodeFailureIdentity))
        {
            _host.PublishFailure(cacheKey: string.Empty, cachePermanently: false);
            return;
        }
        // A partial GIF is the full logical canvas, not the WIC first-frame
        // bitmap. Partition it before the sizing grammar so old raw entries
        // and preview candidates cannot substitute incompatible geometry.
        string rasterSourceIdentity = composeGifCanvas && cacheKey.Length > 0
            ? cacheKey + "\u001Fgif-canvas-v1" : cacheKey;
        // Owned WIC variants use final orientation, unlike older provisional
        // raw-axis variants. Never substitute those cache entries on promotion.
        if (nativeDecode && rasterSourceIdentity.Length > 0)
            rasterSourceIdentity += RasterNativeWicDecoder.CachePartition;
        if (nativeGifDecode && rasterSourceIdentity.Length > 0)
            rasterSourceIdentity += RasterNativeWicDecoder.GifCachePartition;
        if (portableRasterDecode && rasterSourceIdentity.Length > 0 && rasterDecoder is not null)
        {
            rasterSourceIdentity += "\u001Fraster-decoder-v1:" +
                _resources.ProviderIdentities.GetRasterDecoderIdentity(rasterDecoder).ToString(CultureInfo.InvariantCulture);
        }
        string cacheFamily = transformedDecode && cacheKey.Length > 0
            ? sizingRequest.GetCacheFamily(rasterSourceIdentity, displaySized) : cacheKey;
        string bitmapCacheKey = transformedDecode && cacheKey.Length > 0
            ? sizingRequest.GetCacheKey(cacheFamily, rasterSize)
            : cacheKey;

        CanvasBitmap? ownedBitmap = null;
        BitmapOutputAdmission.Reservation? bitmapReservation = null;
        SharedCanvasBitmapCache.Lease? bitmapLease = null;
        IDisposable? processDecodeSlot = null;
        WicDecodeOperationOwner? wicDecodeOwner = null;
        IDisposable? preparationSlot = null;
        IDisposable? rasterWork = null;
        IMarkdownPerformanceSessionInternal? rasterSession = _context.PerformanceSession;
        Size intrinsicSize = new(budget.Width, budget.Height);
        if (nativeDecode || portableRasterDecode)
            intrinsicSize = new Size(nativePlan.IntrinsicWidth, nativePlan.IntrinsicHeight);
        SemaphoreSlim? decodeGate = null;
        bool decodeGateHeld = false;
        bool failed = false;
        bool deviceLost = false;
        bool cacheRasterDecodeFailure = false;
        try
        {
            imageCancellationToken.ThrowIfCancellationRequested();
            CanvasDevice device = _context.ResourceCreator.Device;
            if (!string.IsNullOrEmpty(bitmapCacheKey) &&
                _outputAdmission.TryAcquireRasterBitmap(device, bitmapCacheKey, sizingRequest, transformedDecode, out bitmapLease,
                    bitmapOutputWork))
            {
                // Warm cache hit. No stream, decoder, or GPU allocation.
            }
            else
            {
                rasterWork = rasterSession?.TryBeginRasterWork();
                if (rasterSession is not null && rasterWork is null)
                    throw new OperationCanceledException(rasterSession.LifetimeToken);
                using CancellationTokenSource? decodeCancellation = rasterSession is null ? null :
                    CancellationTokenSource.CreateLinkedTokenSource(imageCancellationToken, rasterSession.LifetimeToken);
                CancellationToken decodeToken = decodeCancellation?.Token ?? imageCancellationToken;
                decodeToken.ThrowIfCancellationRequested();
                // A resize or DPI transition may need a larger raster than an
                // image already uploaded for this source. Keep that cached
                // bitmap visible while the exact replacement is decoded. An
                // exact cache hit above never incurs this variant search.
                if (displaySized &&
                    rasterOptions?.UseCachedRasterPreview == true &&
                    cacheKey.Length > 0 &&
                    (long)rasterSize.Width * rasterSize.Height >= 65_536 &&
                    _resources.BitmapCache.TryAcquireBestRasterPreview(
                        device,
                        cacheFamily,
                        sizingRequest,
                        out SharedCanvasBitmapCache.Lease? previewLease) &&
                    previewLease is not null)
                {
                    SharedCanvasBitmapCache.Lease cachedPreview = previewLease;
                    if (!_outputAdmission.TryAdmitCachedBitmapOutput(cachedPreview, expectedWork: bitmapOutputWork))
                    {
                        _host.TryImageCleanup(cachedPreview.Dispose);
                        if (_outputDeferral.IsBitmapOutputDeferred)
                            return;
                    }
                    else
                    {
                        _host.PublishOnUiThread(
                            () => _host.PresentRasterPreview(cachedPreview), cachedPreview.Dispose);
                    }
                }

                if (!string.IsNullOrEmpty(bitmapCacheKey))
                {
                    decodeGate = _resources.BitmapCache.GetDecodeGate(device, bitmapCacheKey);
                    await decodeGate.WaitAsync(decodeToken).ConfigureAwait(false);
                    decodeGateHeld = true;

                    // Another request may have populated the cache while this
                    // request waited on the fixed-size keyed decode gate.
                    _ = _outputAdmission.TryAcquireRasterBitmap(device, bitmapCacheKey, sizingRequest, transformedDecode, out bitmapLease,
                        bitmapOutputWork);
                }

                if (bitmapLease is null)
                {
                    if (_outputDeferral.IsBitmapOutputDeferred)
                        return;
                    long decodeStarted = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
                        ? Stopwatch.GetTimestamp()
                        : 0;
                    long rasterPreparationId = rasterSession is { IsDisposed: false }
                        ? rasterSession.BeginRasterPreparation(bytes.Length, budget.Width, budget.Height)
                        : 0;
                    long admissionStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                    preparationSlot = rasterSession is { IsDisposed: false } &&
                        _context.EffectiveImagePreparationOwner is { } documentOwner
                        ? await rasterSession.EnterCpuPreparationAsync(
                                documentOwner,
                                exclusive: sessionPixelCapApplies || budget.CanRenderStaticPreview,
                                decodeToken)
                            .ConfigureAwait(false)
                        : null;
                    // Keyed deduplication and optional session limits are not
                    // aggregate codec limits. All ordinary-raster cache misses
                    // share this admission, including empty keys and controls
                    // with no performance session. Native decode cannot be
                    // canceled by this token: hold the lease through its await,
                    // bitmap upload, and output cleanup, never in a callback.
                    long sourcePixels = (long)budget.Width * budget.Height;
                    long outputPixels = portableRasterDecode || nativeDecode ? nativePlan.OutputPixels : nativeGifDecode
                        ? nativeGifPlan.CanvasOutputPixels : transformedDecode
                        ? sizingRequest.GetMaximumOrientedOutputPixels(
                            budget.Width, budget.Height, maySwapAxes: budget.FirstFrame is null)
                        : sourcePixels;
                    // Owned-WIC weights include a conservative source-scratch
                    // allowance informed by oversized WebP measurements. This
                    // coordinates concurrent work; it is not a native peak cap.
                    long processAdmissionWeight = portableRasterDecode
                        ? RasterDecodeAdmissionEstimate.ForPortableProvider(
                            bytes.Length,
                            RasterDecoderMaximumWorkingBytes,
                            checked(outputPixels * 4))
                        : nativeDecode
                        ? RasterDecodeAdmissionEstimate.ForOwnedStaticWic(bytes.Length, nativePlan)
                        : nativeGifDecode
                            ? RasterDecodeAdmissionEstimate.ForOwnedGifWic(bytes.Length, nativeGifPlan)
                            : checked(
                                bytes.Length +
                                checked(sourcePixels * 4) +
                                checked(outputPixels * (composeGifCanvas ? 12 : 8)));
                    processDecodeSlot = await _resources.Preparation.EnterAsync(
                        _context.EffectiveImagePreparationOwner ?? _context,
                        processAdmissionWeight,
                        exclusive: sessionPixelCapApplies || budget.CanRenderStaticPreview,
                        decodeToken).ConfigureAwait(false);
                    RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                        MarkdownRasterPreparationStage.PreparationAdmission, admissionStarted);
                    long stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                    if (portableRasterDecode)
                    {
                        long outputBytes = checked(nativePlan.OutputPixels * 4);
                        if (!_outputAdmission.TryReserveBitmapOutput(outputBytes,
                                out bitmapReservation, expectedWork: bitmapOutputWork))
                            return;

                        decodeToken.ThrowIfCancellationRequested();
                        MarkdownRasterDecodePriority priority = bitmapOutputWork.Demand?.Priority ==
                            MarkdownSvgRenderPriority.Overscan
                            ? MarkdownRasterDecodePriority.Overscan
                            : MarkdownRasterDecodePriority.Visible;
                        MarkdownRasterDecodeRequest request = new(
                            bytes,
                            rasterFormat,
                            nativePlan.OutputWidth,
                            nativePlan.OutputHeight,
                            RasterDecoderMaximumWorkingBytes,
                            MarkdownRasterPixelFormat.Bgra8Premultiplied,
                            priority);
                        stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                        MarkdownRaster decodedRaster;
                        try
                        {
                            decodedRaster = await rasterDecoder!.DecodeAsync(request, decodeToken).ConfigureAwait(false);
                        }
                        catch (MarkdownRasterException exception) when (
                            exception.Reason == MarkdownRasterFailureReason.Canceled)
                        {
                            throw new OperationCanceledException(
                                "The static raster decoder canceled its active request.", exception, decodeToken);
                        }
                        catch (MarkdownRasterException exception) when (
                            exception.Reason == MarkdownRasterFailureReason.UnsupportedContent)
                        {
                            // This typed result describes the exact encoded
                            // source/container, unlike resource limits,
                            // timeouts, worker failures, and capability errors.
                            cacheRasterDecodeFailure = true;
                            throw;
                        }
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.RasterProviderDecode, stageStarted);

                        if (decodedRaster is null)
                            throw new InvalidDataException("The raster provider returned no owned pixel buffer.");

                        using (decodedRaster)
                        {
                            if (decodedRaster.WidthPixels != nativePlan.OutputWidth ||
                                decodedRaster.HeightPixels != nativePlan.OutputHeight ||
                                decodedRaster.StrideBytes != checked(nativePlan.OutputWidth * 4) ||
                                decodedRaster.LengthBytes != checked((int)outputBytes) ||
                                decodedRaster.PixelFormat != MarkdownRasterPixelFormat.Bgra8Premultiplied)
                            {
                                throw new InvalidDataException(
                                    "The raster provider returned pixels outside the admitted output contract.");
                            }

                            decodeToken.ThrowIfCancellationRequested();
                            if (bitmapReservation is not null && !bitmapReservation.Commit())
                                throw new InvalidOperationException("The raster output reservation is no longer active.");
                            stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                            byte[] uploadPixels = decodedRaster.Pixels.ToArray();
                            try
                            {
                                ownedBitmap = CanvasBitmap.CreateFromBytes(
                                    _context.ResourceCreator, uploadPixels, nativePlan.OutputWidth,
                                    nativePlan.OutputHeight, DirectXPixelFormat.B8G8R8A8UIntNormalized,
                                    96, CanvasAlphaMode.Premultiplied);
                            }
                            finally
                            {
                                Array.Clear(uploadPixels, 0, uploadPixels.Length);
                            }
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
                        }
                    }
                    else if (nativeDecode)
                    {
                        // Admit exact GPU output before any codec factory. The
                        // process estimate also covers overlapping raw/oriented
                        // CPU buffers and remains held through final upload.
                        if (!_outputAdmission.TryReserveBitmapOutput(checked(nativePlan.OutputPixels * 4),
                                out bitmapReservation, expectedWork: bitmapOutputWork))
                            return;
                        IDisposable nativeCredit = processDecodeSlot!;
                        var nativeOptions = new RasterNativeWicDecodeOptions(_resources.NativeWic);
                        Task<RasterDecodedPixelLease> nativeTask = Task.Run(
                            () => RasterNativeWicDecoder.Decode(
                                        bytes, nativePlan, nativeCredit, nativeOptions, decodeToken),
                            CancellationToken.None);
                        // Decode consumes credit on every invocation path. If
                        // scheduling itself throws, the caller still owns it.
                        processDecodeSlot = null;
                        using RasterDecodedPixelLease pixels = await nativeTask.ConfigureAwait(false);
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
                        decodeToken.ThrowIfCancellationRequested();
                        stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                        if (bitmapReservation is not null && !bitmapReservation.Commit())
                            throw new InvalidOperationException("The raster output reservation is no longer active.");
                        ownedBitmap = CanvasBitmap.CreateFromBytes(
                            _context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
                        // Pixel lease disposal clears the CPU buffer and refunds
                        // process credit only after upload; no BitmapDecoder RCW
                        // or codec graph remains alive on the success path.
                    }
                    else if (nativeGifDecode)
                    {
                        long frameBytes = checked(nativeGifPlan.FrameOutputPixels * 4);
                        long canvasBytes = checked(nativeGifPlan.CanvasOutputPixels * 4);
                        if (!_outputAdmission.TryReserveBitmapOutput(composeGifCanvas ? canvasBytes + frameBytes : frameBytes,
                                out bitmapReservation, expectedWork: bitmapOutputWork))
                            return;
                        // Reserve and split before the codec or either GPU
                        // plane exists. A failed frame disposal must retain its
                        // own charge without preventing healthy canvas cleanup.
                        BitmapOutputAdmission.Reservation? frameReservation = composeGifCanvas
                            ? bitmapReservation?.Split(frameBytes) : null;
                        CanvasBitmap? decodedFrame = null;
                        try
                        {
                            IDisposable nativeCredit = processDecodeSlot!;
                            var nativeOptions = new RasterNativeWicDecodeOptions(_resources.NativeWic);
                            Task<RasterDecodedPixelLease> nativeTask = Task.Run(
                                () => RasterNativeWicDecoder.DecodeGifFirstFrame(
                                    bytes, nativeGifPlan, nativeCredit, nativeOptions, decodeToken),
                                CancellationToken.None);
                            processDecodeSlot = null;
                            using RasterDecodedPixelLease pixels = await nativeTask.ConfigureAwait(false);
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
                            decodeToken.ThrowIfCancellationRequested();
                            stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                            if (composeGifCanvas)
                            {
                                if (frameReservation is not null && !frameReservation.Commit())
                                    throw new InvalidOperationException("The GIF frame reservation is no longer active.");
                                decodedFrame = CanvasBitmap.CreateFromBytes(
                                    _context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                                if (bitmapReservation is not null && !bitmapReservation.Commit())
                                    throw new InvalidOperationException("The GIF canvas reservation is no longer active.");
                                ComposeGifCanvasBitmap(decodedFrame, budget.FirstFrame!.Value,
                                    budget.Width, budget.Height, rasterSize, ref ownedBitmap);
                            }
                            else
                            {
                                if (bitmapReservation is not null && !bitmapReservation.Commit())
                                    throw new InvalidOperationException("The GIF output reservation is no longer active.");
                                ownedBitmap = CanvasBitmap.CreateFromBytes(
                                    _context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                            }
                            decodeToken.ThrowIfCancellationRequested();
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
                        }
                        finally
                        {
                            _outputAdmission.ReleaseAdmittedBitmap(ref decodedFrame, ref frameReservation);
                        }
                    }
                    else
                    {
                        // Borrow the admitted source array rather than duplicating
                        // up to 32 MiB into an InMemoryRandomAccessStream. Keep both
                        // wrappers alive until the native decode/upload completes.
                        if (transformedDecode)
                        {
                            // Reserve durable custody before either WinRT factory.
                            // Awaiting a projected operation does not close it;
                            // failed terminal cleanup must retain its admission.
                            wicDecodeOwner = WicDecodeOperationOwner.Create();
                            wicDecodeOwner.AdoptProcessDecodeSlot(ref processDecodeSlot);
                        }
                        using MemoryStream? sourceStream = transformedDecode ? null : new(bytes, writable: false);
                        using IRandomAccessStream? directStream = sourceStream?.AsRandomAccessStream();
                        IRandomAccessStream stream = wicDecodeOwner?.OpenSourceStream(bytes) ?? directStream!;
                        decodeToken.ThrowIfCancellationRequested();
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.SourceStreamOpen, stageStarted);
                        if (transformedDecode)
                        {
                            stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                            BitmapDecoder decoder = await wicDecodeOwner!.CreateDecoderOperation(stream);
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.DecoderCreation, stageStarted);
                            decodeToken.ThrowIfCancellationRequested();
                            int firstFrameWidth = budget.FirstFrame?.Width ?? budget.Width;
                            int firstFrameHeight = budget.FirstFrame?.Height ?? budget.Height;
                            if (decoder.PixelWidth != firstFrameWidth || decoder.PixelHeight != firstFrameHeight)
                            {
                                throw new InvalidDataException("The decoded raster dimensions do not match its validated header.");
                            }

                            // GIF descriptors are subrectangles. Their frame axes
                            // validate WIC, while the logical screen owns aspect,
                            // offset and transparent padding in the document.
                            intrinsicSize = budget.FirstFrame is not null
                                ? new Size(budget.Width, budget.Height)
                                : new Size(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                            bool swapDecodeAxes = budget.FirstFrame is null &&
                                (decoder.OrientedPixelWidth != decoder.PixelWidth ||
                                 decoder.OrientedPixelHeight != decoder.PixelHeight);
                            if (swapDecodeAxes)
                            {
                                // WIC applies scaling before EXIF rotation. Plan the
                                // displayed dimensions against the oriented aspect,
                                // then swap the transform axes back to source space.
                                var orientedSize = sizingRequest.GetSize(
                                    checked((int)decoder.OrientedPixelWidth),
                                    checked((int)decoder.OrientedPixelHeight));
                                rasterSize = (orientedSize.Height, orientedSize.Width);
                            }
                            var transform = new BitmapTransform
                            {
                                ScaledWidth = checked((uint)(composeGifCanvas
                                    ? Math.Max(1, (int)Math.Ceiling(firstFrameWidth * (double)rasterSize.Width / budget.Width))
                                    : rasterSize.Width)),
                                ScaledHeight = checked((uint)(composeGifCanvas
                                    ? Math.Max(1, (int)Math.Ceiling(firstFrameHeight * (double)rasterSize.Height / budget.Height))
                                    : rasterSize.Height)),
                                InterpolationMode = BitmapInterpolationMode.Fant,
                            };
                            long canvasBytes = checked((long)rasterSize.Width * rasterSize.Height * 4);
                            long frameBytes = checked((long)transform.ScaledWidth * transform.ScaledHeight * 4);
                            if (!_outputAdmission.TryReserveBitmapOutput(composeGifCanvas ? canvasBytes + frameBytes : frameBytes,
                                    out bitmapReservation, expectedWork: bitmapOutputWork))
                                return;
                            // Split before either output is born. The temporary
                            // frame's charge survives independently if its native
                            // disposal fails; the canvas may still retire normally.
                            BitmapOutputAdmission.Reservation? frameReservation = composeGifCanvas
                                ? bitmapReservation?.Split(frameBytes) : null;
                            CanvasBitmap? decodedFrame = null;
                            try
                            {
                                stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                                SoftwareBitmap firstFrame = await wicDecodeOwner.CreateSoftwareBitmapOperation(
                                    decoder,
                                    BitmapPixelFormat.Bgra8,
                                    BitmapAlphaMode.Premultiplied,
                                    transform,
                                    ExifOrientationMode.RespectExifOrientation,
                                    ColorManagementMode.ColorManageToSRgb);
                                // The operation owner now retains the result until
                                // upload and real terminal cleanup have completed.
                                wicDecodeOwner.AdoptSoftwareBitmap(firstFrame);
                                RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                    MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
                                decodeToken.ThrowIfCancellationRequested();
                                uint expectedPixelWidth = swapDecodeAxes ? transform.ScaledHeight : transform.ScaledWidth;
                                uint expectedPixelHeight = swapDecodeAxes ? transform.ScaledWidth : transform.ScaledHeight;
                                if (firstFrame.PixelWidth != expectedPixelWidth ||
                                    firstFrame.PixelHeight != expectedPixelHeight ||
                                    checked((long)firstFrame.PixelWidth * firstFrame.PixelHeight) > sizingRequest.MaximumPixels)
                                {
                                    throw new InvalidDataException("The decoded frame does not match the bounded raster transform.");
                                }
                                stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                                if (composeGifCanvas)
                                {
                                    frameReservation?.Commit();
                                    decodedFrame = CanvasBitmap.CreateFromSoftwareBitmap(
                                        _context.ResourceCreator, firstFrame);
                                    bitmapReservation?.Commit();
                                    ComposeGifCanvasBitmap(decodedFrame, budget.FirstFrame!.Value,
                                        budget.Width, budget.Height, rasterSize, ref ownedBitmap);
                                    decodeToken.ThrowIfCancellationRequested();
                                }
                                else
                                {
                                    bitmapReservation?.Commit();
                                    ownedBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_context.ResourceCreator, firstFrame);
                                }
                                RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                    MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
                                if (budget.CanRenderStaticPreview)
                                {
                                    MarkdownDiagnostics.WriteLine(
                                        $"[ImageBox] rendered a bounded first-frame preview for {cacheKey}; " +
                                        $"source={budget.Width}x{budget.Height}, " +
                                        $"preview={rasterSize.Width}x{rasterSize.Height}, frames={budget.FrameCount}.");
                                }
                            }
                            finally
                            {
                                _outputAdmission.ReleaseAdmittedBitmap(ref decodedFrame, ref frameReservation);
                            }
                        }
                        else
                        {
                            stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                            ownedBitmap = await CanvasBitmap.LoadAsync(_context.ResourceCreator, stream);
                            RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                                MarkdownRasterPreparationStage.Win2DDirectLoad, stageStarted);
                        }
                    }

                    decodeToken.ThrowIfCancellationRequested();
                    var outputSize = ownedBitmap.SizeInPixels;
                    if (outputSize.Width == 0 || outputSize.Height == 0 ||
                        outputSize.Width > RasterImageResourceBudget.MaxDimension ||
                        outputSize.Height > RasterImageResourceBudget.MaxDimension ||
                        checked((long)outputSize.Width * outputSize.Height) > sizingRequest.MaximumPixels)
                    {
                        throw new InvalidDataException("The decoded raster output exceeds the consumer's pixel budget.");
                    }
                    if (!string.IsNullOrEmpty(bitmapCacheKey))
                    {
                        stageStarted = rasterPreparationId != 0 ? Stopwatch.GetTimestamp() : 0;
                        bitmapLease = _resources.BitmapCache.StoreAndAcquire(
                            device, bitmapCacheKey, ownedBitmap,
                            transformedDecode ? intrinsicSize : ownedBitmap.Size,
                            bitmapReservation: bitmapReservation);
                        ownedBitmap = null; // lease now owns the decoded handle
                        bitmapReservation = null;
                        RecordRasterPreparationStage(rasterSession, rasterPreparationId,
                            MarkdownRasterPreparationStage.CachePublication, stageStarted);
                    }
                    else
                    {
                        // Unkeyed payloads acquire lifetime accounting on the
                        // preparation worker too. Publication only swaps the
                        // completed lease, without querying native dimensions.
                        bitmapLease = _resources.BitmapCache.CreateUncachedLease(
                            ownedBitmap, checked((int)outputSize.Width), checked((int)outputSize.Height),
                            transformedDecode ? intrinsicSize : ownedBitmap.Size,
                            bitmapReservation: bitmapReservation);
                        ownedBitmap = null;
                        bitmapReservation = null;
                    }
                    if (decodeStarted != 0)
                    {
                        MarkdownPerformanceEventSource.Log.ResourceWork(
                            2,
                            Stopwatch.GetTimestamp() - decodeStarted,
                            (long)(transformedDecode ? rasterSize.Width : budget.Width) *
                                (transformedDecode ? rasterSize.Height : budget.Height));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            _outputAdmission.ReleasePendingRasterOutputs(ref bitmapLease, ref ownedBitmap);
            _host.ResetLoadAfterCancellation();
            return;
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[ImageBox] resolved bitmap load deferred after graphics device loss for {cacheKey}: {ex.Message}");
            _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            _outputAdmission.ReleasePendingRasterOutputs(ref bitmapLease, ref ownedBitmap);
            deviceLost = true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[ImageBox] resolved bitmap load failed for {cacheKey}: {ex.Message}");
            _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            _outputAdmission.ReleasePendingRasterOutputs(ref bitmapLease, ref ownedBitmap);
            failed = true;
        }
        finally
        {
            bytes = Array.Empty<byte>();
            _outputAdmission.ReleaseAdmittedBitmap(ref ownedBitmap, ref bitmapReservation);
            if (wicDecodeOwner is not null)
                _host.TryImageCleanup(wicDecodeOwner.Retire);
            if (processDecodeSlot is not null)
                _host.TryImageCleanup(processDecodeSlot.Dispose);
            if (decodeGateHeld)
                _host.TryImageCleanup(() => decodeGate!.Release());
            if (preparationSlot is not null)
                _host.TryImageCleanup(preparationSlot.Dispose);
            if (rasterWork is not null)
                _host.TryImageCleanup(rasterWork.Dispose);
        }

        SharedCanvasBitmapCache.Lease? publishedLease = bitmapLease;
        CanvasBitmap? publishedOwnedBitmap = ownedBitmap;
        if (transformedDecode && publishedLease?.IntrinsicSize is { } cachedIntrinsicSize)
            intrinsicSize = cachedIntrinsicSize;

        var completion = new RasterLoadCompletion(
            publishedLease, publishedOwnedBitmap, intrinsicSize, transformedDecode, failed, deviceLost,
            cacheRasterDecodeFailure, cacheKey, failureFormatKey, rasterDecodeFailureIdentity);
        _host.PublishOnUiThread(() => _host.PresentRasterLoad(completion),
        onDropped: () =>
        {
            if (publishedLease is { } droppedLease)
                _host.TryImageCleanup(droppedLease.Dispose);
            if (publishedOwnedBitmap is { } droppedBitmap)
                _host.TryImageCleanup(droppedBitmap.Dispose);
        });
    }

    private void ComposeGifCanvasBitmap(
        CanvasBitmap decodedFrame, (int Left, int Top, int Width, int Height) frameRect,
        int canvasWidth, int canvasHeight, (int Width, int Height) rasterSize,
        [System.Diagnostics.CodeAnalysis.NotNull] ref CanvasBitmap? ownedCanvas)
    {
        // Destination edges stay fractional: rounding a small frame up to
        // whole pixels incorrectly fills its padding at low resolutions.
        var destination = new Rect(
            frameRect.Left * (double)rasterSize.Width / canvasWidth,
            frameRect.Top * (double)rasterSize.Height / canvasHeight,
            frameRect.Width * (double)rasterSize.Width / canvasWidth,
            frameRect.Height * (double)rasterSize.Height / canvasHeight);
        // Publish ownership to the caller before drawing, so its established
        // failure/retirement path also owns a canvas whose draw call fails.
        ownedCanvas = new CanvasRenderTarget(_context.ResourceCreator, rasterSize.Width, rasterSize.Height, 96);
        using CanvasDrawingSession drawing = ((CanvasRenderTarget)ownedCanvas).CreateDrawingSession();
        drawing.Clear(Color.FromArgb(0, 0, 0, 0));
        drawing.DrawImage(decodedFrame, destination,
            new Rect(0, 0, decodedFrame.Size.Width, decodedFrame.Size.Height),
            1f, CanvasImageInterpolation.Linear);
    }

    private static bool TryGetMarkdownRasterFormat(string? format, out MarkdownRasterFormat rasterFormat)
    {
        rasterFormat = format switch
        {
            "PNG" => MarkdownRasterFormat.Png,
            "JPEG" => MarkdownRasterFormat.Jpeg,
            "BMP" => MarkdownRasterFormat.Bmp,
            "WEBP" => MarkdownRasterFormat.WebP,
            _ => default,
        };
        return format is "PNG" or "JPEG" or "BMP" or "WEBP";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordRasterPreparationStage(
        IMarkdownPerformanceSessionInternal? session,
        long preparationId,
        MarkdownRasterPreparationStage stage,
        long startedAt)
    {
        if (preparationId != 0)
            session!.RecordRasterPreparationStage(
                preparationId, stage, Stopwatch.GetTimestamp() - startedAt);
    }
}
