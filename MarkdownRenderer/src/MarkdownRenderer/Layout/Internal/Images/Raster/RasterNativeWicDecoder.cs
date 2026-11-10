using System;
using System.IO;
using System.Threading;
using MarkdownRenderer.Images.Internal.Raster;

namespace MarkdownRenderer.Layout.Internal.Images.Raster;

/// <summary>
/// Synchronous MTA WIC path for a source whose host budget and geometry have
/// already been validated. Call only from a non-cancelable Task.Run delegate;
/// cancellation is checked cooperatively between native calls and while
/// remapping output. All COM creation, use, final release, and apartment
/// balancing happen on that same worker thread.
/// </summary>
internal static class RasterNativeWicDecoder
{
    internal const string CachePartition = "\u001Fnative-wic-v1";
    internal const string GifCachePartition = "\u001Fgif-native-first-frame-v1";

    internal static bool IsQualifiedPlan(in RasterImageDecodePlan plan)
    {
        if (plan.Format is not ("PNG" or "BMP" or "JPEG" or "WEBP") ||
            plan.Orientation is < 1 or > 8 ||
            plan.SourceWidth <= 0 || plan.SourceHeight <= 0 ||
            plan.SourceWidth > RasterImageResourceBudget.MaxDimension ||
            plan.SourceHeight > RasterImageResourceBudget.MaxDimension ||
            plan.IntrinsicWidth <= 0 || plan.IntrinsicHeight <= 0 ||
            plan.OutputWidth <= 0 || plan.OutputHeight <= 0 ||
            plan.DecodeWidth <= 0 || plan.DecodeHeight <= 0)
        {
            return false;
        }

        // PNG and WebP non-identity EXIF parity is not yet part of this
        // production allowlist. Let the established WinRT path own those.
        if ((plan.Format is "PNG" or "WEBP") && plan.Orientation != 1)
            return false;

        bool swapsAxes = plan.Orientation >= 5;
        long sourcePixels = (long)plan.SourceWidth * plan.SourceHeight;
        long outputPixels = (long)plan.OutputWidth * plan.OutputHeight;
        if (sourcePixels <= 0 || sourcePixels > RasterImageResourceBudget.MaxDownsampleSourcePixels ||
            outputPixels <= 0 || outputPixels > RasterImageResourceBudget.MaxPixelsPerFrame ||
            plan.SourcePixels != sourcePixels || plan.OutputPixels != outputPixels ||
            plan.IntrinsicWidth != (swapsAxes ? plan.SourceHeight : plan.SourceWidth) ||
            plan.IntrinsicHeight != (swapsAxes ? plan.SourceWidth : plan.SourceHeight) ||
            plan.DecodeWidth != (swapsAxes ? plan.OutputHeight : plan.OutputWidth) ||
            plan.DecodeHeight != (swapsAxes ? plan.OutputWidth : plan.OutputHeight) ||
            plan.DecodeWidth > plan.SourceWidth || plan.DecodeHeight > plan.SourceHeight)
        {
            return false;
        }

        return (long)plan.DecodeWidth * plan.DecodeHeight == outputPixels;
    }

    /// <summary>
    /// Decodes one exact, previously admitted source/plan. This method consumes
    /// <paramref name="processDecodeCredit"/> on every path: successful output
    /// transfers it to the returned pixel lease; safe failures refund it; an
    /// uncertain native teardown leaves it rooted by the published retirement
    /// record and never returns a partially decoded surface.
    /// </summary>
    internal static RasterDecodedPixelLease Decode(
        byte[] source,
        RasterImageDecodePlan plan,
        IDisposable processDecodeCredit,
        NativeResourceRetirement retirement,
        CancellationToken cancellationToken) =>
        Decode(source, plan, processDecodeCredit, retirement, default, cancellationToken);

    /// <inheritdoc cref="Decode(byte[], RasterImageDecodePlan, IDisposable, NativeResourceRetirement, CancellationToken)"/>
    /// <param name="source">The admitted encoded source.</param>
    /// <param name="plan">The validated decode plan for <paramref name="source"/>.</param>
    /// <param name="processDecodeCredit">The process decode credit this call consumes.</param>
    /// <param name="retirement">The owner that keeps an uncertain native teardown.</param>
    /// <param name="options">The pixel-copy strategy and decode observer.</param>
    /// <param name="cancellationToken">Cancels between native calls.</param>
    internal static RasterDecodedPixelLease Decode(
        byte[] source,
        RasterImageDecodePlan plan,
        IDisposable processDecodeCredit,
        NativeResourceRetirement retirement,
        RasterNativeWicDecodeOptions options,
        CancellationToken cancellationToken) =>
        DecodeCore(source, plan, default, isGif: false, processDecodeCredit, retirement, options, cancellationToken);

    internal static RasterDecodedPixelLease DecodeGifFirstFrame(
        byte[] source,
        RasterGifFirstFrameDecodePlan plan,
        IDisposable processDecodeCredit,
        NativeResourceRetirement retirement,
        CancellationToken cancellationToken) =>
        DecodeGifFirstFrame(source, plan, processDecodeCredit, retirement, default, cancellationToken);

    internal static RasterDecodedPixelLease DecodeGifFirstFrame(
        byte[] source,
        RasterGifFirstFrameDecodePlan plan,
        IDisposable processDecodeCredit,
        NativeResourceRetirement retirement,
        RasterNativeWicDecodeOptions options,
        CancellationToken cancellationToken) =>
        DecodeCore(source, default, plan, isGif: true, processDecodeCredit, retirement, options, cancellationToken);

    private static RasterDecodedPixelLease DecodeCore(
        byte[] source, RasterImageDecodePlan staticPlan, RasterGifFirstFrameDecodePlan gifPlan,
        bool isGif, IDisposable processDecodeCredit, NativeResourceRetirement retirement,
        RasterNativeWicDecodeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processDecodeCredit);
        ArgumentNullException.ThrowIfNull(retirement);
        IDisposable? untransferredProcessCredit = processDecodeCredit;
        RasterDecodedPixelLease? pixelLease = null;
        RasterNativeWicGraph? graph = null;
        byte[]? outputPixels = null;
        byte[]? rawPixels = null;
        bool graphOwnsLease = false;
        try
        {
            ThrowIfOutsideAdmissionContract(source, staticPlan, gifPlan, isGif, options, cancellationToken);
            RasterNativeWicDecodeGeometry plan = isGif
                ? new RasterNativeWicDecodeGeometry(gifPlan) : new RasterNativeWicDecodeGeometry(staticPlan);
            cancellationToken.ThrowIfCancellationRequested();
            int outputLength = checked(checked(plan.OutputWidth * plan.OutputHeight) * 4);
            int rawLength = checked(checked(plan.DecodeWidth * plan.DecodeHeight) * 4);
            rawPixels = GC.AllocateUninitializedArray<byte>(rawLength);
            outputPixels = plan.Orientation == 1
                ? rawPixels
                : GC.AllocateUninitializedArray<byte>(outputLength);
            pixelLease = new RasterDecodedPixelLease(
                plan.OutputWidth, plan.OutputHeight, outputPixels!, untransferredProcessCredit);
            untransferredProcessCredit = null;

            // Reserve durable custody and root the complete managed owner graph
            // before the first native factory can allocate an interface.
            NativeResourceRetirementRecord retirementRecord =
                retirement.Reserve(resourceGroupCount: 0, temporarySlotCount: 1);
            graph = new RasterNativeWicGraph(source, plan, rawPixels!, pixelLease!, retirementRecord);
            retirementRecord.SetTemporary(0, graph, retryable: false);
            graphOwnsLease = true;

            DecodeThroughGraph(graph, options, cancellationToken);

            // Source pixels are no longer referenced by any live WIC object
            // after the owned graph is fully retired. Keep output admission in
            // the independent returned CPU lease until upload completes.
            graph.Dispose();
            graph.BalanceApartmentInitialization();

            if (plan.Orientation != 1)
            {
                ApplyOrientation(
                    rawPixels!,
                    pixelLease!.Pixels,
                    plan.DecodeWidth,
                    plan.DecodeHeight,
                    plan.Orientation,
                    cancellationToken);
                Array.Clear(rawPixels);
                rawPixels = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            options.Diagnostics?.RecordPixelLeaseCompleted();
            RasterDecodedPixelLease completed = pixelLease
                ?? throw new InvalidOperationException("The native WIC pixel lease was already transferred.");
            pixelLease = null;
            return completed;
        }
        catch
        {
            RetireAfterFailure(graph, graphOwnsLease, ref pixelLease, ref rawPixels, ref outputPixels);
            throw;
        }
        finally
        {
            // Ownership transfers into RasterDecodedPixelLease before this
            // point. If construction failed earlier, the lease itself refunds
            // process credit; if it was never created, refund the original.
            pixelLease?.Dispose();
            untransferredProcessCredit?.Dispose();
        }
    }

    private static void ThrowIfOutsideAdmissionContract(
        byte[] source,
        RasterImageDecodePlan staticPlan,
        RasterGifFirstFrameDecodePlan gifPlan,
        bool isGif,
        RasterNativeWicDecodeOptions options,
        CancellationToken cancellationToken)
    {
        if (options.CopyBandRows is < 0 or > RasterNativeWicDecodeOptions.MaximumCopyBandRows)
            throw new ArgumentOutOfRangeException(nameof(options), "The copy band row count is outside the supported range.");
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length == 0 || source.Length > RasterImageResourceBudget.MaxInputBytes ||
            (isGif ? !gifPlan.MatchesValidatedBudget(RasterImageResourceBudget.Validate(source))
                : !IsQualifiedPlan(staticPlan)))
        {
            throw new InvalidDataException("The source or decode plan is outside the native WIC admission contract.");
        }
    }

    // Builds the WIC graph in its fixed order and copies the pixels,
    // checking cancellation between native calls.
    private static void DecodeThroughGraph(
        RasterNativeWicGraph graph,
        RasterNativeWicDecodeOptions options,
        CancellationToken cancellationToken)
    {
        graph.InitializeApartment();
        options.Diagnostics?.RecordNativeStarted();
        cancellationToken.ThrowIfCancellationRequested();
        graph.CreateFactory();
        cancellationToken.ThrowIfCancellationRequested();
        graph.CreateBorrowingStream();
        cancellationToken.ThrowIfCancellationRequested();
        graph.CreateDecoder();
        cancellationToken.ThrowIfCancellationRequested();
        graph.ReadFrameAndSourceMetadata();
        cancellationToken.ThrowIfCancellationRequested();
        graph.CreateColorManagedTransform(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        graph.CreatePreScalePremultipliedConverterIfNeeded();
        cancellationToken.ThrowIfCancellationRequested();
        graph.CreateFantScaler();
        cancellationToken.ThrowIfCancellationRequested();
        graph.CreatePremultipliedConverter();
        cancellationToken.ThrowIfCancellationRequested();
        if (options.CopyBandRows > 0)
            graph.CopyPixelsInBands(options.CopyBandRows, cancellationToken);
        else
            graph.CopyPixels();
        cancellationToken.ThrowIfCancellationRequested();
    }

    // Runs on the failure path before the exception is rethrown. A
    // quarantined graph keeps its custody; the caller's finally then sees the
    // lease, pixel buffers and credit it must no longer touch as null.
    private static void RetireAfterFailure(
        RasterNativeWicGraph? graph,
        bool graphOwnsLease,
        ref RasterDecodedPixelLease? pixelLease,
        ref byte[]? rawPixels,
        ref byte[]? outputPixels)
    {
        // CopyPixels has synchronously returned before this catch. WIC
        // never owns these caller-provided output arrays; erase partial
        // pixels even if native retirement must retain their custody.
        if (rawPixels is not null)
            Array.Clear(rawPixels);
        if (outputPixels is not null && !ReferenceEquals(outputPixels, rawPixels))
            Array.Clear(outputPixels);
        bool retainedByQuarantine = false;
        if (graph is not null)
        {
            try
            {
                graph.Dispose();
            }
            catch
            {
                retainedByQuarantine = graph.IsQuarantined;
            }
            finally
            {
                graph.BalanceApartmentInitialization();
            }
        }

        if (retainedByQuarantine || graphOwnsLease && graph?.IsQuarantined == true)
        {
            // The retirement registry now roots graph -> pixel lease ->
            // process credit and graph -> pinned source. Never clear or
            // release any object still reachable from that custody root.
            // Its caller-owned pixel storage has already been erased.
            pixelLease = null;
            rawPixels = null;
            outputPixels = null;
        }
        else
        {
            if (rawPixels is not null &&
                (pixelLease is null || !ReferenceEquals(rawPixels, pixelLease.Pixels)))
            {
                Array.Clear(rawPixels);
            }
            pixelLease?.Dispose();
            if (pixelLease is null && outputPixels is not null &&
                (rawPixels is null || !ReferenceEquals(rawPixels, outputPixels)))
            {
                Array.Clear(outputPixels);
            }
        }
    }

    private static void ApplyOrientation(
        byte[] source,
        byte[] destination,
        int rawWidth,
        int rawHeight,
        int orientation,
        CancellationToken cancellationToken)
    {
        if (source.Length != checked(checked(rawWidth * rawHeight) * 4) ||
            destination.Length != checked(checked(rawHeight * rawWidth) * 4))
        {
            throw new InvalidDataException("The bounded WIC pixel arrays do not match the planned orientation geometry.");
        }

        int orientedWidth = orientation >= 5 ? rawHeight : rawWidth;
        for (int y = 0; y < rawHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < rawWidth; x++)
            {
                (int outputX, int outputY) = orientation switch
                {
                    2 => (rawWidth - 1 - x, y),
                    3 => (rawWidth - 1 - x, rawHeight - 1 - y),
                    4 => (x, rawHeight - 1 - y),
                    5 => (y, x),
                    6 => (rawHeight - 1 - y, x),
                    7 => (rawHeight - 1 - y, rawWidth - 1 - x),
                    8 => (y, rawWidth - 1 - x),
                    _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
                };
                int sourceOffset = checked((checked(y * rawWidth) + x) * 4);
                int destinationOffset = checked((checked(outputY * orientedWidth) + outputX) * 4);
                source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
