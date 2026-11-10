using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal.Raster;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Layout.Internal.Images.Raster;

/// <summary>
/// Decodes an admitted raster cache miss into a device bitmap through one of
/// four paths: the portable raster provider, owned WIC (static or GIF first
/// frame), or the WinRT decoder (bounded transform or direct load). Each path
/// reserves its exact bitmap output first and records its preparation stages
/// in the load's <see cref="RasterLoadState"/>.
/// </summary>
internal sealed class RasterBitmapDecoder
{
    private static readonly long RasterDecoderMaximumWorkingBytes = IntPtr.Size == 4
        ? 32L * 1024 * 1024
        : 64L * 1024 * 1024;

    private readonly MarkdownLayoutContext _context;
    private readonly ImageResourceScope _resources;
    private readonly ImageBitmapOutputAdmission _outputAdmission;

    internal RasterBitmapDecoder(
        MarkdownLayoutContext context,
        ImageResourceScope resources,
        ImageBitmapOutputAdmission outputAdmission)
    {
        _context = context;
        _resources = resources;
        _outputAdmission = outputAdmission;
    }

    internal static long GetProcessAdmissionWeight(int sourceLength, RasterLoadPlan plan)
    {
        RasterImageBudgetResult budget = plan.Budget;
        long sourcePixels = (long)budget.Width * budget.Height;
        long outputPixels = plan.PortableRasterDecode || plan.NativeDecode ? plan.NativePlan.OutputPixels : plan.NativeGifDecode
            ? plan.NativeGifPlan.CanvasOutputPixels : plan.TransformedDecode
            ? plan.SizingRequest.GetMaximumOrientedOutputPixels(
                budget.Width, budget.Height, maySwapAxes: budget.FirstFrame is null)
            : sourcePixels;
        // Owned-WIC weights include a conservative source-scratch
        // allowance informed by oversized WebP measurements. This
        // coordinates concurrent work; it is not a native peak cap.
        return plan.PortableRasterDecode
            ? RasterDecodeAdmissionEstimate.ForPortableProvider(
                sourceLength,
                RasterDecoderMaximumWorkingBytes,
                checked(outputPixels * 4))
            : plan.NativeDecode
            ? RasterDecodeAdmissionEstimate.ForOwnedStaticWic(sourceLength, plan.NativePlan)
            : plan.NativeGifDecode
                ? RasterDecodeAdmissionEstimate.ForOwnedGifWic(sourceLength, plan.NativeGifPlan)
                : checked(
                    sourceLength +
                    checked(sourcePixels * 4) +
                    checked(outputPixels * (plan.ComposeGifCanvas ? 12 : 8)));
    }

    /// <summary>Decodes through the plan's path.</summary>
    /// <returns><see langword="false"/> when the path's bitmap output was denied.</returns>
    internal Task<bool> DecodeAsync(
        byte[] bytes,
        string cacheKey,
        RasterLoadPlan plan,
        RasterLoadState state,
        long stageStarted,
        CancellationToken decodeToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        if (plan.PortableRasterDecode)
            return DecodeWithProviderAsync(bytes, plan, state, decodeToken, bitmapOutputWork);
        if (plan.NativeDecode)
            return DecodeWithOwnedWicAsync(bytes, plan, state, stageStarted, decodeToken, bitmapOutputWork);
        if (plan.NativeGifDecode)
            return DecodeGifWithOwnedWicAsync(bytes, plan, state, stageStarted, decodeToken, bitmapOutputWork);
        return DecodeWithWinRtAsync(bytes, cacheKey, plan, state, stageStarted, decodeToken, bitmapOutputWork);
    }

    private async Task<bool> DecodeWithProviderAsync(
        byte[] bytes,
        RasterLoadPlan plan,
        RasterLoadState state,
        CancellationToken decodeToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        RasterImageDecodePlan nativePlan = plan.NativePlan;
        long outputBytes = checked(nativePlan.OutputPixels * 4);
        if (!_outputAdmission.TryReserveBitmapOutput(outputBytes,
                out state.BitmapReservation, expectedWork: bitmapOutputWork))
            return false;

        decodeToken.ThrowIfCancellationRequested();
        MarkdownRasterDecodePriority priority = bitmapOutputWork.Demand?.Priority ==
            MarkdownSvgRenderPriority.Overscan
            ? MarkdownRasterDecodePriority.Overscan
            : MarkdownRasterDecodePriority.Visible;
        MarkdownRasterDecodeRequest request = new(
            bytes,
            plan.RasterFormat,
            nativePlan.OutputWidth,
            nativePlan.OutputHeight,
            RasterDecoderMaximumWorkingBytes,
            MarkdownRasterPixelFormat.Bgra8Premultiplied,
            priority);
        long stageStarted = state.StartStage();
        MarkdownRaster decodedRaster;
        try
        {
            decodedRaster = await plan.RasterDecoder!.DecodeAsync(request, decodeToken).ConfigureAwait(false);
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
            state.CacheRasterDecodeFailure = true;
            throw;
        }
        state.RecordStage(MarkdownRasterPreparationStage.RasterProviderDecode, stageStarted);

        if (decodedRaster is null)
            throw new InvalidDataException("The raster provider returned no owned pixel buffer.");

        using (decodedRaster)
            UploadProviderRaster(decodedRaster, nativePlan, outputBytes, state, decodeToken);
        return true;
    }

    private void UploadProviderRaster(
        MarkdownRaster decodedRaster,
        RasterImageDecodePlan nativePlan,
        long outputBytes,
        RasterLoadState state,
        CancellationToken decodeToken)
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
        if (state.BitmapReservation is not null && !state.BitmapReservation.Commit())
            throw new InvalidOperationException("The raster output reservation is no longer active.");
        long stageStarted = state.StartStage();
        byte[] uploadPixels = decodedRaster.Pixels.ToArray();
        try
        {
            state.OwnedBitmap = CanvasBitmap.CreateFromBytes(
                _context.ResourceCreator, uploadPixels, nativePlan.OutputWidth,
                nativePlan.OutputHeight, DirectXPixelFormat.B8G8R8A8UIntNormalized,
                96, CanvasAlphaMode.Premultiplied);
        }
        finally
        {
            Array.Clear(uploadPixels, 0, uploadPixels.Length);
        }
        state.RecordStage(MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
    }

    private async Task<bool> DecodeWithOwnedWicAsync(
        byte[] bytes,
        RasterLoadPlan plan,
        RasterLoadState state,
        long stageStarted,
        CancellationToken decodeToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        RasterImageDecodePlan nativePlan = plan.NativePlan;
        // Admit exact GPU output before any codec factory. The
        // process estimate also covers overlapping raw/oriented
        // CPU buffers and remains held through final upload.
        if (!_outputAdmission.TryReserveBitmapOutput(checked(nativePlan.OutputPixels * 4),
                out state.BitmapReservation, expectedWork: bitmapOutputWork))
            return false;
        IDisposable nativeCredit = state.ProcessDecodeSlot!;
        var nativeOptions = new RasterNativeWicDecodeOptions(_resources.NativeWic);
        NativeResourceRetirement nativeRetirement = _context.RenderResources.NativeRetirement;
        Task<RasterDecodedPixelLease> nativeTask = Task.Run(
            () => RasterNativeWicDecoder.Decode(
                        bytes, nativePlan, nativeCredit, nativeRetirement, nativeOptions, decodeToken),
            CancellationToken.None);
        // Decode consumes credit on every invocation path. If
        // scheduling itself throws, the caller still owns it.
        state.ProcessDecodeSlot = null;
        using RasterDecodedPixelLease pixels = await nativeTask.ConfigureAwait(false);
        state.RecordStage(MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
        decodeToken.ThrowIfCancellationRequested();
        stageStarted = state.StartStage();
        if (state.BitmapReservation is not null && !state.BitmapReservation.Commit())
            throw new InvalidOperationException("The raster output reservation is no longer active.");
        state.OwnedBitmap = CanvasBitmap.CreateFromBytes(
            _context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        state.RecordStage(MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
        // Pixel lease disposal clears the CPU buffer and refunds
        // process credit only after upload; no BitmapDecoder RCW
        // or codec graph remains alive on the success path.
        return true;
    }

    private async Task<bool> DecodeGifWithOwnedWicAsync(
        byte[] bytes,
        RasterLoadPlan plan,
        RasterLoadState state,
        long stageStarted,
        CancellationToken decodeToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        RasterGifFirstFrameDecodePlan nativeGifPlan = plan.NativeGifPlan;
        bool composeGifCanvas = plan.ComposeGifCanvas;
        long frameBytes = checked(nativeGifPlan.FrameOutputPixels * 4);
        long canvasBytes = checked(nativeGifPlan.CanvasOutputPixels * 4);
        if (!_outputAdmission.TryReserveBitmapOutput(composeGifCanvas ? canvasBytes + frameBytes : frameBytes,
                out state.BitmapReservation, expectedWork: bitmapOutputWork))
            return false;
        // Reserve and split before the codec or either GPU
        // plane exists. A failed frame disposal must retain its
        // own charge without preventing healthy canvas cleanup.
        BitmapOutputAdmission.Reservation? frameReservation = composeGifCanvas
            ? state.BitmapReservation?.Split(frameBytes) : null;
        CanvasBitmap? decodedFrame = null;
        try
        {
            IDisposable nativeCredit = state.ProcessDecodeSlot!;
            var nativeOptions = new RasterNativeWicDecodeOptions(_resources.NativeWic);
            NativeResourceRetirement nativeRetirement = _context.RenderResources.NativeRetirement;
            Task<RasterDecodedPixelLease> nativeTask = Task.Run(
                () => RasterNativeWicDecoder.DecodeGifFirstFrame(
                    bytes, nativeGifPlan, nativeCredit, nativeRetirement, nativeOptions, decodeToken),
                CancellationToken.None);
            state.ProcessDecodeSlot = null;
            using RasterDecodedPixelLease pixels = await nativeTask.ConfigureAwait(false);
            state.RecordStage(MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
            decodeToken.ThrowIfCancellationRequested();
            stageStarted = state.StartStage();
            if (composeGifCanvas)
            {
                if (frameReservation is not null && !frameReservation.Commit())
                    throw new InvalidOperationException("The GIF frame reservation is no longer active.");
                decodedFrame = CanvasBitmap.CreateFromBytes(
                    _context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                if (state.BitmapReservation is not null && !state.BitmapReservation.Commit())
                    throw new InvalidOperationException("The GIF canvas reservation is no longer active.");
                ComposeGifCanvasBitmap(decodedFrame, plan.Budget.FirstFrame!.Value,
                    plan.Budget.Width, plan.Budget.Height, state.RasterSize, ref state.OwnedBitmap);
            }
            else
            {
                if (state.BitmapReservation is not null && !state.BitmapReservation.Commit())
                    throw new InvalidOperationException("The GIF output reservation is no longer active.");
                state.OwnedBitmap = CanvasBitmap.CreateFromBytes(
                    _context.ResourceCreator, pixels.Pixels, pixels.Width, pixels.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            }
            decodeToken.ThrowIfCancellationRequested();
            state.RecordStage(MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
        }
        finally
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref decodedFrame, ref frameReservation);
        }

        return true;
    }

    private async Task<bool> DecodeWithWinRtAsync(
        byte[] bytes,
        string cacheKey,
        RasterLoadPlan plan,
        RasterLoadState state,
        long stageStarted,
        CancellationToken decodeToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        bool transformedDecode = plan.TransformedDecode;
        // Borrow the admitted source array rather than duplicating
        // up to 32 MiB into an InMemoryRandomAccessStream. Keep both
        // wrappers alive until the native decode/upload completes.
        if (transformedDecode)
        {
            // Reserve durable custody before either WinRT factory.
            // Awaiting a projected operation does not close it;
            // failed terminal cleanup must retain its admission.
            state.WicDecodeOwner = WicDecodeOperationOwner.Create(_context.RenderResources.NativeRetirement);
            state.WicDecodeOwner.AdoptProcessDecodeSlot(ref state.ProcessDecodeSlot);
        }
        using MemoryStream? sourceStream = transformedDecode ? null : new(bytes, writable: false);
        using IRandomAccessStream? directStream = sourceStream?.AsRandomAccessStream();
        IRandomAccessStream stream = state.WicDecodeOwner?.OpenSourceStream(bytes) ?? directStream!;
        decodeToken.ThrowIfCancellationRequested();
        state.RecordStage(MarkdownRasterPreparationStage.SourceStreamOpen, stageStarted);
        if (transformedDecode)
        {
            return await DecodeTransformedWithWinRtAsync(
                    stream, cacheKey, plan, state, decodeToken, bitmapOutputWork)
                .ConfigureAwait(false);
        }

        stageStarted = state.StartStage();
        state.OwnedBitmap = await CanvasBitmap.LoadAsync(_context.ResourceCreator, stream);
        state.RecordStage(MarkdownRasterPreparationStage.Win2DDirectLoad, stageStarted);
        return true;
    }

    private async Task<bool> DecodeTransformedWithWinRtAsync(
        IRandomAccessStream stream,
        string cacheKey,
        RasterLoadPlan plan,
        RasterLoadState state,
        CancellationToken decodeToken,
        BitmapOutputWorkContext bitmapOutputWork)
    {
        RasterImageBudgetResult budget = plan.Budget;
        bool composeGifCanvas = plan.ComposeGifCanvas;
        WicDecodeOperationOwner wicDecodeOwner = state.WicDecodeOwner!;
        long stageStarted = state.StartStage();
        BitmapDecoder decoder = await wicDecodeOwner.CreateDecoderOperation(stream);
        state.RecordStage(MarkdownRasterPreparationStage.DecoderCreation, stageStarted);
        decodeToken.ThrowIfCancellationRequested();
        (BitmapTransform transform, bool swapDecodeAxes) = PlanWinRtTransform(decoder, plan, state);
        (int Width, int Height) rasterSize = state.RasterSize;
        long canvasBytes = checked((long)rasterSize.Width * rasterSize.Height * 4);
        long frameBytes = checked((long)transform.ScaledWidth * transform.ScaledHeight * 4);
        if (!_outputAdmission.TryReserveBitmapOutput(composeGifCanvas ? canvasBytes + frameBytes : frameBytes,
                out state.BitmapReservation, expectedWork: bitmapOutputWork))
            return false;
        // Split before either output is born. The temporary
        // frame's charge survives independently if its native
        // disposal fails; the canvas may still retire normally.
        BitmapOutputAdmission.Reservation? frameReservation = composeGifCanvas
            ? state.BitmapReservation?.Split(frameBytes) : null;
        CanvasBitmap? decodedFrame = null;
        try
        {
            stageStarted = state.StartStage();
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
            state.RecordStage(MarkdownRasterPreparationStage.WicPixelDecode, stageStarted);
            decodeToken.ThrowIfCancellationRequested();
            ThrowIfFrameMismatchesTransform(firstFrame, transform, swapDecodeAxes, plan.SizingRequest.MaximumPixels);
            stageStarted = state.StartStage();
            if (composeGifCanvas)
            {
                frameReservation?.Commit();
                decodedFrame = CanvasBitmap.CreateFromSoftwareBitmap(
                    _context.ResourceCreator, firstFrame);
                state.BitmapReservation?.Commit();
                ComposeGifCanvasBitmap(decodedFrame, budget.FirstFrame!.Value,
                    budget.Width, budget.Height, rasterSize, ref state.OwnedBitmap);
                decodeToken.ThrowIfCancellationRequested();
            }
            else
            {
                state.BitmapReservation?.Commit();
                state.OwnedBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_context.ResourceCreator, firstFrame);
            }
            state.RecordStage(MarkdownRasterPreparationStage.Win2DBitmapUpload, stageStarted);
            if (budget.CanRenderStaticPreview)
                ReportStaticPreview(cacheKey, budget, rasterSize);
        }
        finally
        {
            _outputAdmission.ReleaseAdmittedBitmap(ref decodedFrame, ref frameReservation);
        }

        return true;
    }

    private static void ThrowIfFrameMismatchesTransform(
        SoftwareBitmap frame,
        BitmapTransform transform,
        bool swapDecodeAxes,
        long maximumPixels)
    {
        uint expectedPixelWidth = swapDecodeAxes ? transform.ScaledHeight : transform.ScaledWidth;
        uint expectedPixelHeight = swapDecodeAxes ? transform.ScaledWidth : transform.ScaledHeight;
        if (frame.PixelWidth != expectedPixelWidth ||
            frame.PixelHeight != expectedPixelHeight ||
            checked((long)frame.PixelWidth * frame.PixelHeight) > maximumPixels)
        {
            throw new InvalidDataException("The decoded frame does not match the bounded raster transform.");
        }
    }

    private static void ReportStaticPreview(
        string cacheKey,
        RasterImageBudgetResult budget,
        (int Width, int Height) rasterSize) =>
        MarkdownDiagnostics.WriteLine(
            $"[ImageBox] rendered a bounded first-frame preview for {cacheKey}; " +
            $"source={budget.Width}x{budget.Height}, " +
            $"preview={rasterSize.Width}x{rasterSize.Height}, frames={budget.FrameCount}.");

    /// <summary>
    /// Validates the WinRT decoder against the header, records the oriented
    /// intrinsic size and output geometry in <paramref name="state"/>, and
    /// returns the bounded transform.
    /// </summary>
    private static (BitmapTransform Transform, bool SwapDecodeAxes) PlanWinRtTransform(
        BitmapDecoder decoder, RasterLoadPlan plan, RasterLoadState state)
    {
        RasterImageBudgetResult budget = plan.Budget;
        bool composeGifCanvas = plan.ComposeGifCanvas;
        int firstFrameWidth = budget.FirstFrame?.Width ?? budget.Width;
        int firstFrameHeight = budget.FirstFrame?.Height ?? budget.Height;
        if (decoder.PixelWidth != firstFrameWidth || decoder.PixelHeight != firstFrameHeight)
        {
            throw new InvalidDataException("The decoded raster dimensions do not match its validated header.");
        }

        // GIF descriptors are subrectangles. Their frame axes
        // validate WIC, while the logical screen owns aspect,
        // offset and transparent padding in the document.
        state.IntrinsicSize = budget.FirstFrame is not null
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
            var orientedSize = plan.SizingRequest.GetSize(
                checked((int)decoder.OrientedPixelWidth),
                checked((int)decoder.OrientedPixelHeight));
            state.RasterSize = (orientedSize.Height, orientedSize.Width);
        }
        (int Width, int Height) rasterSize = state.RasterSize;
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
        return (transform, swapDecodeAxes);
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
}
