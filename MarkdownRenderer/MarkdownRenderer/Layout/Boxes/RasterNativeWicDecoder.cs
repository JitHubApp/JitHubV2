using System;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A bounded premultiplied CPU raster whose process-decode credit remains
/// owned until the caller finishes GPU upload. The backing array is internal
/// and must not be retained after disposal.
/// </summary>
internal sealed class RasterDecodedPixelLease : IDisposable
{
    private byte[]? _pixels;
    private IDisposable? _processDecodeCredit;

    internal RasterDecodedPixelLease(int width, int height, byte[] pixels, IDisposable processDecodeCredit)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
        _processDecodeCredit = processDecodeCredit;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal byte[] Pixels => Volatile.Read(ref _pixels)
        ?? throw new ObjectDisposedException(nameof(RasterDecodedPixelLease));

    public void Dispose()
    {
        byte[]? pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
            Array.Clear(pixels);
        Interlocked.Exchange(ref _processDecodeCredit, null)?.Dispose();
    }
}

#if MARKDOWN_LAYOUT_DIAGNOSTICS
internal readonly record struct RasterNativeWicDiagnosticSnapshot(long NativeStarted, long PixelLeaseCompleted);

internal static class RasterNativeWicDiagnostics
{
    private static long s_nativeStarted;
    private static long s_pixelLeaseCompleted;

    internal static RasterNativeWicDiagnosticSnapshot GetSnapshot() =>
        new(Interlocked.Read(ref s_nativeStarted), Interlocked.Read(ref s_pixelLeaseCompleted));

    internal static void RecordNativeStarted() => Interlocked.Increment(ref s_nativeStarted);
    internal static void RecordPixelLeaseCompleted() => Interlocked.Increment(ref s_pixelLeaseCompleted);
}
#endif

/// <summary>
/// Synchronous MTA WIC path for a source whose host budget and geometry have
/// already been validated. Call only from a non-cancelable Task.Run delegate;
/// cancellation is checked cooperatively between native calls and while
/// remapping output. All COM creation, use, final release, and apartment
/// balancing happen on that same worker thread.
/// </summary>
internal static partial class RasterNativeWicDecoder
{
    internal const string CachePartition = "\u001Fnative-wic-v1";
    internal const string GifCachePartition = "\u001Fgif-native-first-frame-v1";
    private const int MaximumColorContexts = 16;
    private const int MaximumOwnedNativePointers = 64;
    private static readonly Guid IidWicColorContext = RasterNativeWicInterop.IidWicColorContext;
    private static readonly Guid IidWicBitmapSource = RasterNativeWicInterop.IidWicBitmapSource;

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
        CancellationToken cancellationToken) =>
        DecodeCore(source, plan, default, isGif: false, processDecodeCredit, cancellationToken,
            diagnosticCopyBandRows: 0, useDiagnosticCopyBands: false);

    internal static RasterDecodedPixelLease DecodeGifFirstFrame(
        byte[] source,
        RasterGifFirstFrameDecodePlan plan,
        IDisposable processDecodeCredit,
        CancellationToken cancellationToken) =>
        DecodeCore(source, default, plan, isGif: true, processDecodeCredit, cancellationToken,
            diagnosticCopyBandRows: 0, useDiagnosticCopyBands: false);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private const int MaximumDiagnosticCopyBandRows = 1_024;

    /// <summary>
    /// Runs the ordinary owned-WIC path, replacing only the final converter's
    /// full CopyPixels call with bounded output-row requests for diagnostics.
    /// This does not participate in shipping decode selection.
    /// </summary>
    internal static RasterDecodedPixelLease DecodeWithCopyBandsForDiagnostics(
        byte[] source,
        RasterImageDecodePlan plan,
        IDisposable processDecodeCredit,
        int copyBandRows,
        CancellationToken cancellationToken) =>
        DecodeCore(source, plan, default, isGif: false, processDecodeCredit, cancellationToken,
            copyBandRows, useDiagnosticCopyBands: true);

    internal static RasterDecodedPixelLease DecodeGifFirstFrameWithCopyBandsForDiagnostics(
        byte[] source,
        RasterGifFirstFrameDecodePlan plan,
        IDisposable processDecodeCredit,
        int copyBandRows,
        CancellationToken cancellationToken) =>
        DecodeCore(source, default, plan, isGif: true, processDecodeCredit, cancellationToken,
            copyBandRows, useDiagnosticCopyBands: true);
#endif

    private static RasterDecodedPixelLease DecodeCore(
        byte[] source, RasterImageDecodePlan staticPlan, RasterGifFirstFrameDecodePlan gifPlan,
        bool isGif, IDisposable processDecodeCredit, CancellationToken cancellationToken,
        int diagnosticCopyBandRows, bool useDiagnosticCopyBands)
    {
        ArgumentNullException.ThrowIfNull(processDecodeCredit);
        IDisposable? untransferredProcessCredit = processDecodeCredit;
        RasterDecodedPixelLease? pixelLease = null;
        OwnedWicGraph? graph = null;
        byte[]? outputPixels = null;
        byte[]? rawPixels = null;
        bool graphOwnsLease = false;
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (useDiagnosticCopyBands &&
                (diagnosticCopyBandRows < 1 || diagnosticCopyBandRows > MaximumDiagnosticCopyBandRows))
            {
                throw new ArgumentOutOfRangeException(nameof(diagnosticCopyBandRows));
            }
#else
            _ = diagnosticCopyBandRows;
            _ = useDiagnosticCopyBands;
#endif
            ArgumentNullException.ThrowIfNull(source);
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length == 0 || source.Length > RasterImageResourceBudget.MaxInputBytes ||
                (isGif ? !gifPlan.MatchesValidatedBudget(RasterImageResourceBudget.Validate(source))
                    : !IsQualifiedPlan(staticPlan)))
            {
                throw new InvalidDataException("The source or decode plan is outside the native WIC admission contract.");
            }

            NativeDecodeGeometry plan = isGif
                ? new NativeDecodeGeometry(gifPlan)
                : new NativeDecodeGeometry(staticPlan);
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
                NativeResourceRetirement.Shared.Reserve(resourceGroupCount: 0, temporarySlotCount: 1);
            graph = new OwnedWicGraph(source, plan, rawPixels!, pixelLease!, retirementRecord);
            retirementRecord.SetTemporary(0, graph, retryable: false);
            graphOwnsLease = true;

            graph.InitializeApartment();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnostics.RecordNativeStarted();
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (useDiagnosticCopyBands)
                graph.CopyPixelsInBands(diagnosticCopyBandRows, cancellationToken);
            else
#endif
                graph.CopyPixels();
            cancellationToken.ThrowIfCancellationRequested();

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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            RasterNativeWicDiagnostics.RecordPixelLeaseCompleted();
#endif
            RasterDecodedPixelLease completed = pixelLease
                ?? throw new InvalidOperationException("The native WIC pixel lease was already transferred.");
            pixelLease = null;
            return completed;
        }
        catch
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
            throw;
        }
        finally
        {
            // Ownership transfers into RasterDecodedPixelLease before this
            // point. If construction failed earlier, the lease itself refunds
            // process credit; if it was never created, refund the original.
            if (pixelLease is not null)
                pixelLease.Dispose();
            untransferredProcessCredit?.Dispose();
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

    // Share native ownership, color management and retirement, not the public
    // planning contracts. GIF's returned buffer is only its descriptor frame.
    private readonly struct NativeDecodeGeometry
    {
        internal NativeDecodeGeometry(RasterImageDecodePlan plan)
        {
            SourceWidth = plan.SourceWidth;
            SourceHeight = plan.SourceHeight;
            Format = plan.Format;
            FrameCount = 1;
            Orientation = plan.Orientation;
            OutputWidth = plan.OutputWidth;
            OutputHeight = plan.OutputHeight;
            DecodeWidth = plan.DecodeWidth;
            DecodeHeight = plan.DecodeHeight;
        }

        internal NativeDecodeGeometry(RasterGifFirstFrameDecodePlan plan)
        {
            SourceWidth = plan.FrameWidth;
            SourceHeight = plan.FrameHeight;
            Format = "GIF";
            FrameCount = plan.FrameCount;
            Orientation = 1;
            OutputWidth = DecodeWidth = plan.DecodeWidth;
            OutputHeight = DecodeHeight = plan.DecodeHeight;
        }

        internal readonly int SourceWidth, SourceHeight, FrameCount, Orientation;
        internal readonly int OutputWidth, OutputHeight, DecodeWidth, DecodeHeight;
        internal readonly string Format;
    }

    private sealed unsafe class OwnedWicGraph : IDisposable
    {
        private readonly byte[] _source;
        private readonly NativeDecodeGeometry _plan;
        private readonly byte[] _rawPixels;
        private readonly RasterDecodedPixelLease _pixelLease;
        private readonly NativeResourceRetirementRecord _retirementRecord;
        private readonly StrategyBasedComWrappers _wrappers = new();
        private readonly ComObject?[] _sourceColorContextOwners = new ComObject?[MaximumColorContexts];
        private readonly RasterNativeWicInterop.IWICColorContext?[] _sourceColorContexts =
            new RasterNativeWicInterop.IWICColorContext?[MaximumColorContexts];
        private readonly nint[] _sourceColorContextPointers = new nint[MaximumColorContexts];
        private readonly nint[] _ownedNativePointers = new nint[MaximumOwnedNativePointers];

        private GCHandle _sourcePin;
        private ComObject? _factoryObject;
        private ComObject? _streamObject;
        private ComObject? _decoderObject;
        private ComObject? _frameObject;
        private ComObject? _destinationColorContextObject;
        private ComObject? _colorTransformObject;
        private ComObject? _preScaleConverterObject;
        private ComObject? _scalerObject;
        private ComObject? _converterObject;
        private RasterNativeWicInterop.IWICImagingFactory? _factory;
        private RasterNativeWicInterop.IWICStream? _stream;
        private RasterNativeWicInterop.IWICBitmapDecoder? _decoder;
        private RasterNativeWicInterop.IWICBitmapFrameDecode? _frame;
        private RasterNativeWicInterop.IWICColorContext? _destinationColorContext;
        private RasterNativeWicInterop.IWICColorTransform? _colorTransform;
        private RasterNativeWicInterop.IWICFormatConverter? _preScaleConverter;
        private RasterNativeWicInterop.IWICBitmapScaler? _scaler;
        private RasterNativeWicInterop.IWICFormatConverter? _converter;
        private int _sourceWidth;
        private int _sourceHeight;
        private int _frameColorContextCount;
        private bool _colorTransformActive;
        private int _sourceColorContextCount;
        private int _ownedSourceColorContextCount;
        private int _outstandingNativePointerReferences;
        private nint _overflowNativePointer;
        private bool _apartmentInitialized;
        private bool _quarantined;
        private bool _retired;

        internal OwnedWicGraph(
            byte[] source,
            NativeDecodeGeometry plan,
            byte[] rawPixels,
            RasterDecodedPixelLease pixelLease,
            NativeResourceRetirementRecord retirementRecord)
        {
            _source = source;
            _plan = plan;
            _rawPixels = rawPixels;
            _pixelLease = pixelLease;
            _retirementRecord = retirementRecord;
            _sourcePin = GCHandle.Alloc(source, GCHandleType.Pinned);
        }

        internal bool IsQuarantined => _quarantined;

        internal void InitializeApartment()
        {
            int hr = NativeMethods.CoInitializeEx(0, coInitialize: 0);
            if (hr < 0)
                ThrowForHResult(hr, "CoInitializeEx(COINIT_MULTITHREADED)");
            _apartmentInitialized = true;
        }

        internal void CreateFactory()
        {
            nint native = 0;
            Guid classId = RasterNativeWicInterop.ClsidWicImagingFactory2;
            Guid interfaceId = RasterNativeWicInterop.IidWicImagingFactory;
            int hr = NativeMethods.CoCreateInstance(
                &classId, 0, RasterNativeWicInterop.ClsctxInprocServer, &interfaceId, &native);
            if (native != 0)
                _factory = Adopt<RasterNativeWicInterop.IWICImagingFactory>(ref native, ref _factoryObject);
            ThrowForHResult(hr, "CoCreateInstance(CLSID_WICImagingFactory2)");
            if (_factory is null)
                throw new InvalidOperationException("CoCreateInstance returned no WIC factory.");
        }

        internal void CreateBorrowingStream()
        {
            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateStream(&native);
            if (native != 0)
                _stream = Adopt<RasterNativeWicInterop.IWICStream>(ref native, ref _streamObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateStream");
            RasterNativeWicInterop.IWICStream stream = Require(_stream, "stream");
            byte* bytes = (byte*)_sourcePin.AddrOfPinnedObject();
            hr = stream.InitializeFromMemory(bytes, checked((uint)_source.Length));
            ThrowForHResult(hr, "IWICStream.InitializeFromMemory");
        }

        internal void CreateDecoder()
        {
            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nint streamPointer = QueryOwnedInterface(Require(_streamObject, "stream owner"), RasterNativeWicInterop.IidIStream);
            nint native = 0;
            int hr;
            try
            {
                hr = factory.CreateDecoderFromStream(
                    streamPointer, 0, RasterNativeWicInterop.DecodeMetadataCacheOnDemand, &native);
                if (native != 0)
                    _decoder = Adopt<RasterNativeWicInterop.IWICBitmapDecoder>(ref native, ref _decoderObject);
            }
            finally
            {
                ReleaseOwnedPointer(ref streamPointer);
            }
            ThrowForHResult(hr, "IWICImagingFactory.CreateDecoderFromStream");
            if (_decoder is null)
                throw new InvalidOperationException("WIC returned no bitmap decoder.");
        }

        internal void ReadFrameAndSourceMetadata()
        {
            RasterNativeWicInterop.IWICBitmapDecoder decoder = Require(_decoder, "decoder");
            uint frameCount = 0;
            int hr = decoder.GetFrameCount(&frameCount);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetFrameCount");
            if (frameCount != _plan.FrameCount)
                throw new InvalidDataException("WIC frame count differs from the exact admitted raster header.");

            Guid containerFormat = Guid.Empty;
            hr = decoder.GetContainerFormat(&containerFormat);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetContainerFormat");
            Guid expectedContainer = _plan.Format switch
            {
                "PNG" => RasterNativeWicInterop.ContainerFormatPng,
                "BMP" => RasterNativeWicInterop.ContainerFormatBmp,
                "JPEG" => RasterNativeWicInterop.ContainerFormatJpeg,
                "WEBP" => RasterNativeWicInterop.ContainerFormatWebp,
                "GIF" => RasterNativeWicInterop.ContainerFormatGif,
                _ => Guid.Empty,
            };
            if (expectedContainer == Guid.Empty || containerFormat != expectedContainer)
                throw new InvalidDataException("WIC selected a container different from the exact admitted raster format.");

            nint native = 0;
            hr = decoder.GetFrame(0, &native);
            if (native != 0)
                _frame = Adopt<RasterNativeWicInterop.IWICBitmapFrameDecode>(ref native, ref _frameObject);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetFrame");
            RasterNativeWicInterop.IWICBitmapFrameDecode frame = Require(_frame, "frame");
            uint width = 0;
            uint height = 0;
            hr = frame.GetSize(&width, &height);
            ThrowForHResult(hr, "IWICBitmapSource.GetSize");
            _sourceWidth = checked((int)width);
            _sourceHeight = checked((int)height);
            if (_sourceWidth != _plan.SourceWidth || _sourceHeight != _plan.SourceHeight)
                throw new InvalidDataException("WIC decoded dimensions differ from the exact admitted raster header.");

            Guid sourcePixelFormat = Guid.Empty;
            hr = frame.GetPixelFormat(&sourcePixelFormat);
            ThrowForHResult(hr, "IWICBitmapSource.GetPixelFormat");
            if (sourcePixelFormat == Guid.Empty)
                throw new InvalidDataException("WIC returned an empty source pixel format identifier.");

            uint colorContextCount = 0;
            hr = frame.GetColorContexts(0, null, &colorContextCount);
            if (hr == RasterNativeWicInterop.WicUnsupportedOperation &&
                (_plan.Format is "BMP" or "GIF"))
            {
                colorContextCount = 0;
            }
            else
            {
                ThrowForHResult(hr, "IWICBitmapFrameDecode.GetColorContexts(count)");
            }
            if (colorContextCount > MaximumColorContexts)
                throw new InvalidDataException("WIC source has more color contexts than the fixed metadata bound.");
            _frameColorContextCount = checked((int)colorContextCount);
        }

        internal void CreateColorManagedTransform(CancellationToken cancellationToken)
        {
            if (_frameColorContextCount == 0)
                return;

            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            RasterNativeWicInterop.IWICBitmapFrameDecode frame = Require(_frame, "frame");
            CreateDestinationColorContext(factory);
            ReadSourceColorContexts(frame);
            Exception? firstUnusableContext = null;
            for (int index = 0; index < _sourceColorContextCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RasterNativeWicInterop.IWICColorContext candidate = _sourceColorContexts[index]
                    ?? throw new InvalidOperationException("A bounded source color-context slot was not initialized.");
                uint type = 0;
                int hr = candidate.GetType(&type);
                if (hr < 0)
                {
                    firstUnusableContext ??= CreateHResultException(hr, "IWICColorContext.GetType");
                    continue;
                }
                if (type is not (RasterNativeWicInterop.ColorContextTypeProfile or RasterNativeWicInterop.ColorContextTypeExifColorSpace))
                    continue;
                if (TryInitializeColorTransformFor(candidate, out Exception? unusableContext))
                {
                    _colorTransformActive = true;
                    return;
                }
                firstUnusableContext ??= unusableContext;
            }

            // P3-012: no embedded profile is usable (for example a v2.1
            // header with v4-only tag types, which the color system rejects
            // as ERROR_INVALID_PROFILE). Browsers ignore such a profile and
            // show the pixels as sRGB, so decode untagged instead of failing.
            _ = firstUnusableContext;
            _colorTransformActive = false;
        }

        internal void CreatePreScalePremultipliedConverterIfNeeded()
        {
            if (_colorTransformActive)
                return;
            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateFormatConverter(&native);
            if (native != 0)
                _preScaleConverter = Adopt<RasterNativeWicInterop.IWICFormatConverter>(ref native, ref _preScaleConverterObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateFormatConverter(pre-scale PBGRA)");
            RasterNativeWicInterop.IWICFormatConverter converter = Require(_preScaleConverter, "pre-scale format converter");
            Guid targetFormat = RasterNativeWicInterop.WicPixelFormat32bppPbgra;
            nint sourcePointer = QueryOwnedInterface(Require(_frameObject, "frame owner"), IidWicBitmapSource);
            try
            {
                hr = converter.Initialize(
                    sourcePointer, &targetFormat, RasterNativeWicInterop.BitmapDitherTypeNone,
                    0, 0, RasterNativeWicInterop.BitmapPaletteTypeCustom);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICFormatConverter.Initialize(pre-scale PBGRA)");
        }

        private void CreateDestinationColorContext(RasterNativeWicInterop.IWICImagingFactory factory)
        {
            nint native = 0;
            int hr = factory.CreateColorContext(&native);
            if (native != 0)
                _destinationColorContext = Adopt<RasterNativeWicInterop.IWICColorContext>(ref native, ref _destinationColorContextObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorContext(destination)");
            hr = Require(_destinationColorContext, "destination color context").InitializeFromExifColorSpace(1);
            ThrowForHResult(hr, "IWICColorContext.InitializeFromExifColorSpace(sRGB destination)");
        }

        private void ReadSourceColorContexts(RasterNativeWicInterop.IWICBitmapFrameDecode frame)
        {
            int capacity = _frameColorContextCount;
            for (int index = 0; index < capacity; index++)
                _ = CreateAndStoreSourceColorContext();
            try
            {
                for (int index = 0; index < capacity; index++)
                {
                    ComObject owner = _sourceColorContextOwners[index]
                        ?? throw new InvalidOperationException("A source color-context owner was not recorded.");
                    _sourceColorContextPointers[index] = QueryOwnedInterface(owner, IidWicColorContext);
                }
                uint actualCount = 0;
                fixed (nint* contexts = _sourceColorContextPointers)
                {
                    int hr = frame.GetColorContexts(checked((uint)capacity), contexts, &actualCount);
                    ThrowForHResult(hr, "IWICBitmapFrameDecode.GetColorContexts(profiles)");
                }
                if (actualCount == 0 || actualCount > capacity)
                    throw new InvalidDataException("WIC initialized an invalid number of source color contexts.");
                _sourceColorContextCount = checked((int)actualCount);
            }
            finally
            {
                for (int index = 0; index < capacity; index++)
                    ReleaseOwnedPointer(ref _sourceColorContextPointers[index]);
            }
        }

        private RasterNativeWicInterop.IWICColorContext CreateAndStoreSourceColorContext()
        {
            if (_ownedSourceColorContextCount >= MaximumColorContexts)
                throw new InvalidOperationException("The bounded source color-context array is full.");
            int slot = _ownedSourceColorContextCount++;
            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateColorContext(&native);
            RasterNativeWicInterop.IWICColorContext? typed = null;
            if (native != 0)
            {
                typed = Adopt<RasterNativeWicInterop.IWICColorContext>(ref native, ref _sourceColorContextOwners[slot]);
                _sourceColorContexts[slot] = typed;
            }
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorContext(source)");
            if (typed is null || _sourceColorContextOwners[slot] is null)
                throw new InvalidOperationException("WIC returned no source color context owner.");
            return typed;
        }

        private bool TryInitializeColorTransformFor(
            RasterNativeWicInterop.IWICColorContext sourceContext,
            out Exception? unusableContext)
        {
            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateColorTransformer(&native);
            if (native != 0)
                _colorTransform = Adopt<RasterNativeWicInterop.IWICColorTransform>(ref native, ref _colorTransformObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorTransformer");
            RasterNativeWicInterop.IWICColorTransform transform = Require(_colorTransform, "color transform");
            Guid destinationFormat = RasterNativeWicInterop.WicPixelFormat32bppPbgra;
            nint framePointer = 0;
            nint sourceContextPointer = 0;
            nint destinationContextPointer = 0;
            int initializeHr = int.MinValue;
            try
            {
                framePointer = QueryOwnedInterface(Require(_frameObject, "frame owner"), IidWicBitmapSource);
                sourceContextPointer = QueryOwnedInterface(
                    Require(GetOwner(sourceContext), "source color-context owner"), IidWicColorContext);
                destinationContextPointer = QueryOwnedInterface(
                    Require(_destinationColorContextObject, "destination color-context owner"), IidWicColorContext);
                initializeHr = transform.Initialize(framePointer, sourceContextPointer, destinationContextPointer, &destinationFormat);
            }
            finally
            {
                ReleaseOwnedPointer(ref destinationContextPointer);
                ReleaseOwnedPointer(ref sourceContextPointer);
                ReleaseOwnedPointer(ref framePointer);
            }
            if (initializeHr >= 0)
            {
                unusableContext = null;
                return true;
            }
            unusableContext = CreateHResultException(initializeHr, "IWICColorTransform.Initialize");
            if (!TryFinalRelease(ref _colorTransformObject, ref _colorTransform))
                QuarantineAndThrow("A rejected WIC color transform could not be safely released.");
            return false;
        }

        private ComObject? GetOwner(RasterNativeWicInterop.IWICColorContext context)
        {
            if (ReferenceEquals(context, _destinationColorContext))
                return _destinationColorContextObject;
            for (int index = 0; index < _ownedSourceColorContextCount; index++)
            {
                if (ReferenceEquals(context, _sourceColorContexts[index]))
                    return _sourceColorContextOwners[index];
            }
            return null;
        }

        internal void CreateFantScaler()
        {
            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateBitmapScaler(&native);
            if (native != 0)
                _scaler = Adopt<RasterNativeWicInterop.IWICBitmapScaler>(ref native, ref _scalerObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateBitmapScaler");
            RasterNativeWicInterop.IWICBitmapScaler scaler = Require(_scaler, "scaler");
            ComObject sourceOwner = _colorTransformActive
                ? Require(_colorTransformObject, "color transform owner")
                : Require(_preScaleConverterObject, "pre-scale converter owner");
            nint sourcePointer = QueryOwnedInterface(sourceOwner, IidWicBitmapSource);
            try
            {
                hr = scaler.Initialize(
                    sourcePointer,
                    checked((uint)_plan.DecodeWidth),
                    checked((uint)_plan.DecodeHeight),
                    RasterNativeWicInterop.BitmapInterpolationModeFant);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICBitmapScaler.Initialize(PBGRA Fant)");
        }

        internal void CreatePremultipliedConverter()
        {
            RasterNativeWicInterop.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateFormatConverter(&native);
            if (native != 0)
                _converter = Adopt<RasterNativeWicInterop.IWICFormatConverter>(ref native, ref _converterObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateFormatConverter");
            RasterNativeWicInterop.IWICFormatConverter converter = Require(_converter, "converter");
            Guid targetFormat = RasterNativeWicInterop.WicPixelFormat32bppPbgra;
            nint sourcePointer = QueryOwnedInterface(Require(_scalerObject, "scaler owner"), IidWicBitmapSource);
            try
            {
                hr = converter.Initialize(
                    sourcePointer, &targetFormat, RasterNativeWicInterop.BitmapDitherTypeNone,
                    0, 0, RasterNativeWicInterop.BitmapPaletteTypeCustom);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICFormatConverter.Initialize(final PBGRA)");
        }

        internal void CopyPixels()
        {
            RasterNativeWicInterop.IWICFormatConverter converter = Require(_converter, "converter");
            uint stride = checked((uint)_plan.DecodeWidth * 4);
            uint bufferSize = checked((uint)_rawPixels.Length);
            fixed (byte* destination = _rawPixels)
            {
                int hr = converter.CopyPixels(rect: 0, stride, bufferSize, destination);
                ThrowForHResult(hr, "IWICBitmapSource.CopyPixels(final PBGRA)");
            }
        }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
        internal void CopyPixelsInBands(int copyBandRows, CancellationToken cancellationToken)
        {
            if (copyBandRows is < 1 or > MaximumDiagnosticCopyBandRows)
                throw new ArgumentOutOfRangeException(nameof(copyBandRows));

            RasterNativeWicInterop.IWICFormatConverter converter = Require(_converter, "converter");
            uint stride = checked((uint)_plan.DecodeWidth * 4);
            fixed (byte* destination = _rawPixels)
            {
                for (int y = 0; y < _plan.DecodeHeight;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int rows = Math.Min(copyBandRows, _plan.DecodeHeight - y);
                    var rectangle = new DiagnosticWicRect(0, y, _plan.DecodeWidth, rows);
                    uint bandBufferSize = checked(stride * (uint)rows);
                    int destinationOffset = checked(y * (int)stride);
                    byte* bandDestination = destination + destinationOffset;
                    int hr = converter.CopyPixels(
                        (nint)(&rectangle), stride, bandBufferSize, bandDestination);
                    ThrowForHResult(hr, "IWICBitmapSource.CopyPixels(final PBGRA row band)");
                    y = checked(y + rows);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DiagnosticWicRect
        {
            internal int X;
            internal int Y;
            internal int Width;
            internal int Height;

            internal DiagnosticWicRect(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }
#endif

        public void Dispose()
        {
            if (_retired || _quarantined)
                return;
            if (_outstandingNativePointerReferences != 0)
                QuarantineAndThrow("Cannot retire a WIC graph with outstanding native interface references.");

            if (!TryFinalRelease(ref _converterObject, ref _converter) ||
                !TryFinalRelease(ref _scalerObject, ref _scaler) ||
                !TryFinalRelease(ref _colorTransformObject, ref _colorTransform) ||
                !TryFinalRelease(ref _preScaleConverterObject, ref _preScaleConverter) ||
                !TryFinalRelease(ref _destinationColorContextObject, ref _destinationColorContext))
            {
                QuarantineAndThrow("A downstream WIC interface could not be retired.");
            }

            for (int index = _ownedSourceColorContextCount - 1; index >= 0; index--)
            {
                if (!TryFinalRelease(ref _sourceColorContextOwners[index], ref _sourceColorContexts[index]))
                    QuarantineAndThrow("A WIC source color context could not be retired.");
            }

            if (!TryFinalRelease(ref _frameObject, ref _frame) ||
                !TryFinalRelease(ref _decoderObject, ref _decoder) ||
                !TryFinalRelease(ref _streamObject, ref _stream) ||
                !TryFinalRelease(ref _factoryObject, ref _factory))
            {
                QuarantineAndThrow("An upstream WIC interface could not be retired.");
            }

            if (_sourcePin.IsAllocated)
            {
                try
                {
                    _sourcePin.Free();
                }
                catch
                {
                    QuarantineAndThrow("The pinned raster source could not be released exactly once.");
                }
            }
            _retired = true;
            _retirementRecord.SetTemporary(0, resource: null, retryable: false);
        }

        internal void BalanceApartmentInitialization()
        {
            if (!_apartmentInitialized)
                return;
            NativeMethods.CoUninitialize();
            _apartmentInitialized = false;
        }

        private T Adopt<T>(ref nint native, ref ComObject? slot)
            where T : class
        {
            if (native == 0)
                throw new InvalidOperationException("WIC returned a null COM interface pointer.");
            // Record the factory-owned reference before wrapper creation or
            // any other operation that can allocate or fail.
            TrackNativePointer(native);
            if (slot is not null)
                throw new InvalidOperationException("A WIC graph slot was assigned twice.");
            nint ownedResultReference = native;
            slot = (ComObject)_wrappers.GetOrCreateObjectForComInstance(
                ownedResultReference, CreateObjectFlags.UniqueInstance);
            T typed = (T)(object)slot;
            ReleaseOwnedPointer(ref native);
            return typed;
        }

        private bool TryFinalRelease<T>(ref ComObject? owner, ref T? typed)
            where T : class
        {
            if (owner is null)
            {
                typed = null;
                return true;
            }
            try
            {
                owner.FinalRelease();
                owner = null;
                typed = null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private nint QueryOwnedInterface(ComObject owner, Guid iid)
        {
            int instanceSlot = ReserveNativePointerSlot();
            nint instance = 0;
            bool found;
            try
            {
                found = ComWrappers.TryGetComInstance(owner, out instance);
            }
            catch
            {
                if (instance != 0)
                    StoreReservedNativePointer(instanceSlot, instance);
                else
                    _ownedNativePointers[instanceSlot] = 0;
                throw;
            }
            if (instance != 0)
                StoreReservedNativePointer(instanceSlot, instance);
            else
                _ownedNativePointers[instanceSlot] = 0;
            if (!found || instance == 0)
            {
                if (instance != 0)
                    ReleaseOwnedPointer(ref instance);
                throw new InvalidOperationException("The unique WIC wrapper no longer exposes its COM instance.");
            }

            int queriedSlot = ReserveNativePointerSlot();
            nint queried = 0;
            try
            {
                int hr = Marshal.QueryInterface(instance, in iid, out queried);
                if (queried != 0)
                    StoreReservedNativePointer(queriedSlot, queried);
                else
                    _ownedNativePointers[queriedSlot] = 0;
                if (hr < 0)
                    ThrowForHResult(hr, "QueryInterface(WIC interface)");
                if (queried == 0)
                    throw new InvalidOperationException("QueryInterface returned a null WIC interface pointer.");
                return queried;
            }
            catch
            {
                // A failing QI is allowed to have written an owned pointer.
                // Keep such output in the preallocated owner graph; retirement
                // will quarantine rather than guess whether it is releasable.
                if (_ownedNativePointers[queriedSlot] == -1)
                {
                    if (queried != 0)
                        StoreReservedNativePointer(queriedSlot, queried);
                    else
                        _ownedNativePointers[queriedSlot] = 0;
                }
                throw;
            }
            finally
            {
                // TryGetComInstance itself returns an owned reference. If its
                // release fails the tracked slot remains in this graph and
                // prevents unsafe retirement of its dependencies.
                ReleaseOwnedPointerAt(instanceSlot);
                instance = 0;
            }
        }

        private void ReleaseOwnedPointer(ref nint pointer)
        {
            nint value = pointer;
            if (value == 0)
                return;
            int slot = FindNativePointerSlot(value);
            if (slot < 0)
                throw new InvalidOperationException("A native WIC reference was not present in its bounded custody slots.");
            ReleaseOwnedPointerAt(slot);
            pointer = 0;
        }

        private int ReserveNativePointerSlot()
        {
            for (int index = 0; index < _ownedNativePointers.Length; index++)
            {
                if (_ownedNativePointers[index] == 0)
                {
                    // A sentinel prevents a reentrant COM call from claiming
                    // a slot reserved for a pointer about to be returned.
                    _ownedNativePointers[index] = -1;
                    return index;
                }
            }
            QuarantineAndThrow("The fixed WIC native-reference custody table is exhausted.");
            throw new InvalidOperationException("Unreachable after native-reference custody exhaustion.");
        }

        private void TrackNativePointer(nint pointer)
        {
            for (int index = 0; index < _ownedNativePointers.Length; index++)
            {
                if (_ownedNativePointers[index] == 0)
                {
                    _ownedNativePointers[index] = pointer;
                    _outstandingNativePointerReferences++;
                    return;
                }
            }
            if (_overflowNativePointer == 0)
            {
                // This defensive custody slot is unreachable under the fixed
                // color-context and graph bounds, but preserves an unexpected
                // native output before publishing a terminal quarantine.
                _overflowNativePointer = pointer;
                _outstandingNativePointerReferences++;
            }
            QuarantineAndThrow("The fixed WIC native-reference custody table is exhausted.");
        }

        private void StoreReservedNativePointer(int slot, nint pointer)
        {
            if (pointer == 0)
            {
                _ownedNativePointers[slot] = 0;
                return;
            }
            if (_ownedNativePointers[slot] != -1)
            {
                if (_overflowNativePointer == 0)
                {
                    _overflowNativePointer = pointer;
                    _outstandingNativePointerReferences++;
                }
                QuarantineAndThrow("A reserved WIC native-reference slot changed state.");
            }
            _ownedNativePointers[slot] = pointer;
            _outstandingNativePointerReferences++;
        }

        private int FindNativePointerSlot(nint pointer)
        {
            for (int index = 0; index < _ownedNativePointers.Length; index++)
            {
                if (_ownedNativePointers[index] == pointer)
                    return index;
            }
            return -1;
        }

        private void ReleaseOwnedPointerAt(int slot)
        {
            nint pointer = _ownedNativePointers[slot];
            if (pointer == 0 || pointer == -1)
                return;
            // Do not clear the custody slot or decrement the count until the
            // owned reference has actually been released.
            Marshal.Release(pointer);
            _ownedNativePointers[slot] = 0;
            _outstandingNativePointerReferences--;
        }

        private void QuarantineAndThrow(string message)
        {
            _quarantined = true;
            _retirementRecord.StopSingleSlotRetries();
            _retirementRecord.Publish();
            throw new InvalidOperationException(message + " Its source, process credit, and uncertain WIC graph remain quarantined.");
        }

        private static T Require<T>(T? value, string description) where T : class =>
            value ?? throw new InvalidOperationException($"The WIC {description} was not created.");

        private static Exception CreateHResultException(int hr, string operation) =>
            new COMException($"{operation} failed with HRESULT 0x{hr:X8}.", hr);

        private static void ThrowForHResult(int hr, string operation)
        {
            if (hr < 0)
                throw CreateHResultException(hr, operation);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx")]
        internal static partial int CoInitializeEx(nint reserved, uint coInitialize);

        [LibraryImport("ole32.dll", EntryPoint = "CoUninitialize")]
        internal static partial void CoUninitialize();

        [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
        internal static unsafe partial int CoCreateInstance(
            Guid* classId,
            nint outer,
            uint classContext,
            Guid* interfaceId,
            nint* instance);
    }
}
