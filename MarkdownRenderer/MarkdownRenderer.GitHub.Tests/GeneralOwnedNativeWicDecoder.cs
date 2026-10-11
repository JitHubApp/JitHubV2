using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using MarkdownRenderer.Layout.Boxes;

using WicAbi = MarkdownRenderer.GitHub.Tests.OwnedNativeWicLifetimeQualificationTests;

namespace MarkdownRenderer.GitHub.Tests;

internal readonly record struct NativeWicRetirementInfo(
    int AdoptedOwnedInterfaceCount,
    int SuccessfullyFinalReleasedOwnedInterfaceCount,
    int OutstandingQueryInterfaceReferences,
    bool SourcePinReleased,
    bool GraphRetired,
    bool Quarantined,
    bool ApartmentInitializationBalanced);

internal readonly record struct NativeWicPreview(
    int Width,
    int Height,
    byte[] Pixels,
    int ColorContextCount,
    NativeWicRetirementInfo Retirement);

internal enum NativeWicDecodeCheckpointPoint
{
    PixelsCopied,
    BeforeOwnedInterfaceFinalRelease,
}

internal enum NativeWicOwnedInterfaceKind
{
    Converter,
    Scaler,
    ColorTransform,
    PreScaleConverter,
    DestinationColorContext,
    SourceColorContext,
    Frame,
    Decoder,
    Stream,
    Factory,
}

internal sealed class NativeWicDecodeTestHook
{
    private Action? _action;

    internal NativeWicDecodeTestHook(
        NativeWicDecodeCheckpointPoint point,
        NativeWicOwnedInterfaceKind? ownedInterface,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Point = point;
        OwnedInterface = ownedInterface;
        _action = action;
    }

    internal NativeWicDecodeCheckpointPoint Point { get; }
    internal NativeWicOwnedInterfaceKind? OwnedInterface { get; }
    internal void InvokeOnce() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

/// <summary>Bounded scalar custody diagnostics for failures after graph creation.</summary>
internal sealed class NativeWicDecodeFailure : Exception
{
    internal NativeWicDecodeFailure(Exception cause, NativeWicRetirementInfo retirement)
        : base("Native WIC decoding failed after creating its owned graph.", cause)
    {
        Retirement = retirement;
    }

    internal NativeWicRetirementInfo Retirement { get; }
}

/// <summary>
/// Test-only synchronous WIC preview decoder. Call from one MTA worker thread;
/// all interface calls and final releases stay on that thread. This does not
/// replace the renderer's WinRT decoder and is not a general codec-fidelity claim.
/// </summary>
internal static partial class GeneralOwnedNativeWicDecoder
{
    private const int MaximumColorContexts = 16;
    private const uint ClsctxInprocServer = 0x1;

    private static readonly object QuarantineGate = new();
    private static OwnedWicGraph? QuarantinedGraphsHead;

    private static readonly Guid ClsidWicImagingFactory2 = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
    private static readonly Guid IidWicImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid IidIStream = new("0000000c-0000-0000-c000-000000000046");
    private static readonly Guid IidWicBitmapSource = new("00000120-a8f2-4877-ba0a-fd2b6645fb94");
    private static readonly Guid IidWicColorContext = new("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d");
    private static readonly Guid WicPixelFormat32bppPbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    private const int WicDecodeMetadataCacheOnDemand = 0;
    private const int WicBitmapInterpolationModeFant = 3;
    private const int WicBitmapDitherTypeNone = 0;
    private const int WicBitmapPaletteTypeCustom = 0;
    private const int WicColorContextTypeProfile = 1;
    private const int WicColorContextTypeExifColorSpace = 2;
    private const int WicUnsupportedOperation = unchecked((int)0x88982F81);

    /// <summary>
    /// Decodes one validated static raster into final-orientation PBGRA pixels.
    /// For EXIF orientations 5–8, outputWidth/outputHeight are the dimensions
    /// after orientation, and the raw Fant scaler dimensions are reversed.
    /// </summary>
    internal static NativeWicPreview Decode(
        byte[] source,
        int expectedWidth,
        int expectedHeight,
        int outputWidth,
        int outputHeight,
        ushort orientation,
        CancellationToken cancellationToken = default,
        NativeWicDecodeTestHook? testHook = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        RasterImageBudgetResult header = RasterImageResourceBudget.Validate(source);
        if ((!header.Accepted && !header.CanRenderStaticPreview) ||
            header.FrameCount != 1 ||
            header.Format is not ("PNG" or "BMP" or "JPEG" or "WEBP"))
            throw new InvalidDataException(header.Reason ?? "Native WIC preview requires one admitted static image frame.");
        if (expectedWidth <= 0 || expectedHeight <= 0 ||
            expectedWidth > RasterImageResourceBudget.MaxDimension ||
            expectedHeight > RasterImageResourceBudget.MaxDimension ||
            expectedWidth != header.Width || expectedHeight != header.Height ||
            checked((long)expectedWidth * expectedHeight) > RasterImageResourceBudget.MaxDownsampleSourcePixels)
        {
            throw new InvalidDataException("The expected native WIC source dimensions do not match the admitted header.");
        }
        if (outputWidth <= 0 || outputHeight <= 0 ||
            outputWidth > RasterImageResourceBudget.MaxDimension ||
            outputHeight > RasterImageResourceBudget.MaxDimension ||
            checked((long)outputWidth * outputHeight) > RasterImageResourceBudget.MaxPixelsPerFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(outputWidth),
                "The oriented native WIC output dimensions exceed the raster limits.");
        }
        if (orientation is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(orientation), "EXIF orientation must be between 1 and 8.");

        cancellationToken.ThrowIfCancellationRequested();
        var graph = new OwnedWicGraph(
            source, header.Format!, expectedWidth, expectedHeight, outputWidth, outputHeight, orientation, testHook);
        byte[]? scaledPixels = null;
        Exception? decodeFailure = null;
        Exception? retirementFailure = null;
        try
        {
            graph.InitializeApartment();
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
            scaledPixels = graph.CopyPixels();
            graph.NotifyPixelsCopied();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception failure)
        {
            decodeFailure = failure;
        }

        try
        {
            graph.Dispose();
        }
        catch (Exception failure)
        {
            retirementFailure = failure;
        }
        finally
        {
            // Balance only this graph's successful initialization on the same
            // synchronous worker, including quarantine paths. This does not
            // release, retry, or make a quarantined native object usable again.
            graph.BalanceApartmentInitialization();
        }

        if (decodeFailure is not null || retirementFailure is not null)
        {
            if (scaledPixels is not null)
                Array.Clear(scaledPixels);

            Exception cause = decodeFailure is not null && retirementFailure is not null
                ? new AggregateException(
                    "Native WIC decoding and owned-graph retirement both failed.",
                    decodeFailure,
                    retirementFailure)
                : decodeFailure ?? retirementFailure!;
            throw new NativeWicDecodeFailure(cause, graph.GetRetirement());
        }

        byte[] rawPixels = scaledPixels
            ?? throw new InvalidOperationException("Native WIC completed without a pixel buffer.");
        byte[] orientedPixels;
        if (orientation == 1)
        {
            orientedPixels = rawPixels;
        }
        else
        {
            try
            {
                orientedPixels = ApplyOrientation(
                    rawPixels,
                    graph.RawOutputWidth,
                    graph.RawOutputHeight,
                    outputWidth,
                    outputHeight,
                    orientation,
                    cancellationToken);
            }
            finally
            {
                // Orientation owns a separate, bounded destination array. Do
                // not leave the intermediate alive after remapping completes.
                Array.Clear(rawPixels);
            }
        }

        return new NativeWicPreview(
            outputWidth,
            outputHeight,
            orientedPixels,
            graph.FrameColorContextCount,
            graph.GetRetirement());
    }

    private static byte[] ApplyOrientation(
        byte[] scaledPixels,
        int rawWidth,
        int rawHeight,
        int outputWidth,
        int outputHeight,
        ushort orientation,
        CancellationToken cancellationToken)
    {
        if (scaledPixels.Length != checked(checked(rawWidth * rawHeight) * 4))
            throw new InvalidDataException("The scaled WIC surface does not match its bounded dimensions.");

        var output = new byte[checked(checked(outputWidth * outputHeight) * 4)];
        try
        {
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
                    int destinationOffset = checked((checked(outputY * outputWidth) + outputX) * 4);
                    scaledPixels.AsSpan(sourceOffset, 4).CopyTo(output.AsSpan(destinationOffset, 4));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return output;
        }
        catch
        {
            Array.Clear(output);
            throw;
        }
    }

    private sealed unsafe class OwnedWicGraph : IDisposable
    {
        private readonly byte[] _source;
        private readonly string _format;
        private readonly int _expectedWidth;
        private readonly int _expectedHeight;
        private readonly int _rawOutputWidth;
        private readonly int _rawOutputHeight;
        private NativeWicDecodeTestHook? _testHook;
        private readonly StrategyBasedComWrappers _wrappers = new();
        private readonly ComObject?[] _sourceColorContextOwners = new ComObject?[MaximumColorContexts];
        private readonly WicAbi.IWICColorContext?[] _sourceColorContexts =
            new WicAbi.IWICColorContext?[MaximumColorContexts];
        private readonly nint[] _sourceColorContextPointers = new nint[MaximumColorContexts];

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
        private WicAbi.IWICImagingFactory? _factory;
        private WicAbi.IWICStream? _stream;
        private WicAbi.IWICBitmapDecoder? _decoder;
        private WicAbi.IWICBitmapFrameDecode? _frame;
        private WicAbi.IWICColorContext? _destinationColorContext;
        private WicAbi.IWICColorTransform? _colorTransform;
        private WicAbi.IWICFormatConverter? _preScaleConverter;
        private WicAbi.IWICBitmapScaler? _scaler;
        private WicAbi.IWICFormatConverter? _converter;
        private int _sourceWidth;
        private int _sourceHeight;
        private int _frameColorContextCount;
        private int _sourceColorContextCount;
        private int _ownedSourceColorContextCount;
        private int _adoptedOwnedInterfaceCount;
        private int _successfullyFinalReleasedOwnedInterfaceCount;
        private int _outstandingQueryInterfaceReferences;
        private bool _apartmentInitialized;
        private bool _sourcePinReleased;
        private bool _quarantined;
        private bool _retired;
        private OwnedWicGraph? _nextQuarantinedGraph;

        internal OwnedWicGraph(
            byte[] source,
            string format,
            int expectedWidth,
            int expectedHeight,
            int outputWidth,
            int outputHeight,
            ushort orientation,
            NativeWicDecodeTestHook? testHook)
        {
            _source = source;
            _format = format;
            _testHook = testHook;
            _expectedWidth = expectedWidth;
            _expectedHeight = expectedHeight;
            bool swapsAxes = orientation is >= 5 and <= 8;
            _rawOutputWidth = swapsAxes ? outputHeight : outputWidth;
            _rawOutputHeight = swapsAxes ? outputWidth : outputHeight;
            _sourcePin = GCHandle.Alloc(source, GCHandleType.Pinned);
        }

        internal int FrameColorContextCount => _frameColorContextCount;
        internal int RawOutputWidth => _rawOutputWidth;
        internal int RawOutputHeight => _rawOutputHeight;

        internal void NotifyPixelsCopied() => InvokeTestHook(NativeWicDecodeCheckpointPoint.PixelsCopied);

        private void InvokeTestHook(
            NativeWicDecodeCheckpointPoint point,
            NativeWicOwnedInterfaceKind? ownedInterface = null)
        {
            NativeWicDecodeTestHook? hook = _testHook;
            if (hook is null || hook.Point != point || hook.OwnedInterface != ownedInterface)
                return;

            // Consume before invoking user test code so a thrown hook is never
            // retained or replayed by quarantine/retirement paths.
            _testHook = null;
            hook.InvokeOnce();
        }

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
            Guid classId = ClsidWicImagingFactory2;
            Guid interfaceId = IidWicImagingFactory;
            int hr = NativeMethods.CoCreateInstance(
                &classId, 0, ClsctxInprocServer, &interfaceId, &native);
            if (native != 0)
                _factory = Adopt<WicAbi.IWICImagingFactory>(ref native, ref _factoryObject);
            ThrowForHResult(hr, "CoCreateInstance(CLSID_WICImagingFactory2)");
            if (_factory is null)
                throw new InvalidOperationException("CoCreateInstance returned no WIC factory.");
        }

        internal void CreateBorrowingStream()
        {
            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateStream(&native);
            if (native != 0)
                _stream = Adopt<WicAbi.IWICStream>(ref native, ref _streamObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateStream");
            WicAbi.IWICStream stream = Require(_stream, "stream");
            byte* bytes = (byte*)_sourcePin.AddrOfPinnedObject();
            hr = stream.InitializeFromMemory(bytes, checked((uint)_source.Length));
            ThrowForHResult(hr, "IWICStream.InitializeFromMemory");
        }

        internal void CreateDecoder()
        {
            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            nint streamPointer = QueryOwnedInterface(Require(_streamObject, "stream owner"), IidIStream);
            nint native = 0;
            int hr;
            try
            {
                hr = factory.CreateDecoderFromStream(
                    streamPointer, 0, WicDecodeMetadataCacheOnDemand, &native);
                if (native != 0)
                    _decoder = Adopt<WicAbi.IWICBitmapDecoder>(ref native, ref _decoderObject);
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
            WicAbi.IWICBitmapDecoder decoder = Require(_decoder, "decoder");
            uint frameCount = 0;
            int hr = decoder.GetFrameCount(&frameCount);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetFrameCount");
            if (frameCount != 1)
                throw new InvalidDataException($"Native WIC requires exactly one static frame; decoder reported {frameCount}.");

            Guid containerFormat = Guid.Empty;
            hr = decoder.GetContainerFormat(&containerFormat);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetContainerFormat");
            if (containerFormat == Guid.Empty)
                throw new InvalidDataException("Native WIC returned an empty container format identifier.");

            nint native = 0;
            hr = decoder.GetFrame(0, &native);
            if (native != 0)
                _frame = Adopt<WicAbi.IWICBitmapFrameDecode>(ref native, ref _frameObject);
            ThrowForHResult(hr, "IWICBitmapDecoder.GetFrame");
            WicAbi.IWICBitmapFrameDecode frame = Require(_frame, "frame");
            uint width = 0;
            uint height = 0;
            hr = frame.GetSize(&width, &height);
            ThrowForHResult(hr, "IWICBitmapSource.GetSize");
            _sourceWidth = checked((int)width);
            _sourceHeight = checked((int)height);
            if (_sourceWidth != _expectedWidth || _sourceHeight != _expectedHeight)
            {
                throw new InvalidDataException(
                    $"Native WIC decoded {_sourceWidth}x{_sourceHeight}; expected {_expectedWidth}x{_expectedHeight}.");
            }

            Guid sourcePixelFormat = Guid.Empty;
            hr = frame.GetPixelFormat(&sourcePixelFormat);
            ThrowForHResult(hr, "IWICBitmapSource.GetPixelFormat");
            if (sourcePixelFormat == Guid.Empty)
                throw new InvalidDataException("Native WIC returned an empty source pixel format identifier.");

            uint colorContextCount = 0;
            hr = frame.GetColorContexts(0, null, &colorContextCount);
            if (hr == WicUnsupportedOperation && string.Equals(_format, "BMP", StringComparison.Ordinal))
            {
                // WIC's BMP decoder reports this optional metadata query as
                // unsupported when the frame has no authored color context.
                colorContextCount = 0;
            }
            else
            {
                ThrowForHResult(hr, "IWICBitmapFrameDecode.GetColorContexts(count)");
            }
            if (colorContextCount > MaximumColorContexts)
            {
                throw new InvalidDataException(
                    $"Native WIC source reports {colorContextCount} color contexts; the bounded limit is {MaximumColorContexts}.");
            }
            _frameColorContextCount = checked((int)colorContextCount);
        }

        internal void CreateColorManagedTransform(CancellationToken cancellationToken)
        {
            if (_frameColorContextCount == 0)
            {
                // Do not synthesize an sRGB-to-sRGB CMM for unprofiled input:
                // it quantizes samples compared with the stock WinRT path.
                return;
            }

            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            WicAbi.IWICBitmapFrameDecode frame = Require(_frame, "frame");
            CreateDestinationColorContext(factory);
            ReadSourceColorContexts(frame);
            Exception? firstUnusableContext = null;
            for (int index = 0; index < _sourceColorContextCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WicAbi.IWICColorContext candidate = _sourceColorContexts[index]
                    ?? throw new InvalidOperationException("A WIC source color-context slot was not initialized.");
                uint type = 0;
                int hr = candidate.GetType(&type);
                if (hr < 0)
                {
                    firstUnusableContext ??= CreateHResultException(hr, "IWICColorContext.GetType");
                    continue;
                }
                if (type is not (WicColorContextTypeProfile or WicColorContextTypeExifColorSpace))
                    continue;

                if (TryInitializeColorTransformFor(candidate, out Exception? unusableContext))
                    return;
                firstUnusableContext ??= unusableContext;
            }

            throw new InvalidDataException(
                "The admitted image contains color metadata, but none of its bounded source contexts can initialize a WIC color transform.",
                firstUnusableContext);
        }

        internal void CreatePreScalePremultipliedConverterIfNeeded()
        {
            if (_frameColorContextCount != 0)
                return;

            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateFormatConverter(&native);
            if (native != 0)
                _preScaleConverter = Adopt<WicAbi.IWICFormatConverter>(ref native, ref _preScaleConverterObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateFormatConverter(pre-scale PBGRA)");
            WicAbi.IWICFormatConverter converter = Require(_preScaleConverter, "pre-scale format converter");
            Guid targetFormat = WicPixelFormat32bppPbgra;
            nint sourcePointer = QueryOwnedInterface(Require(_frameObject, "frame owner"), IidWicBitmapSource);
            try
            {
                hr = converter.Initialize(
                    sourcePointer,
                    &targetFormat,
                    WicBitmapDitherTypeNone,
                    0,
                    0,
                    WicBitmapPaletteTypeCustom);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICFormatConverter.Initialize(pre-scale PBGRA)");
        }

        private void CreateDestinationColorContext(WicAbi.IWICImagingFactory factory)
        {
            nint native = 0;
            int hr = factory.CreateColorContext(&native);
            if (native != 0)
                _destinationColorContext = Adopt<WicAbi.IWICColorContext>(ref native, ref _destinationColorContextObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorContext(destination)");
            hr = Require(_destinationColorContext, "destination color context")
                .InitializeFromExifColorSpace(1);
            ThrowForHResult(hr, "IWICColorContext.InitializeFromExifColorSpace(sRGB destination)");
        }

        private void ReadSourceColorContexts(WicAbi.IWICBitmapFrameDecode frame)
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
                {
                    throw new InvalidDataException(
                        $"WIC initialized {actualCount} source contexts after reporting {capacity}.");
                }
                _sourceColorContextCount = checked((int)actualCount);
            }
            finally
            {
                for (int index = 0; index < capacity; index++)
                    ReleaseOwnedPointer(ref _sourceColorContextPointers[index]);
            }
        }

        private WicAbi.IWICColorContext CreateAndStoreSourceColorContext()
        {
            if (_ownedSourceColorContextCount >= MaximumColorContexts)
                throw new InvalidOperationException("The bounded source color-context array is full.");

            int slot = _ownedSourceColorContextCount++;
            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateColorContext(&native);
            WicAbi.IWICColorContext? typed = null;
            if (native != 0)
            {
                typed = Adopt<WicAbi.IWICColorContext>(ref native, ref _sourceColorContextOwners[slot]);
                _sourceColorContexts[slot] = typed;
            }
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorContext(source)");
            if (typed is null)
                throw new InvalidOperationException("WIC returned no source color context.");
            if (_sourceColorContextOwners[slot] is null)
                throw new InvalidOperationException("The source color-context owner was not recorded.");

            return typed;
        }

        private void InitializeColorTransformFor(WicAbi.IWICColorContext sourceContext)
        {
            if (TryInitializeColorTransformFor(sourceContext, out Exception? failure))
                return;
            throw failure ?? new InvalidDataException("The default WIC source color context is unusable.");
        }

        private bool TryInitializeColorTransformFor(
            WicAbi.IWICColorContext sourceContext,
            out Exception? unusableContext)
        {
            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateColorTransformer(&native);
            if (native != 0)
                _colorTransform = Adopt<WicAbi.IWICColorTransform>(ref native, ref _colorTransformObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateColorTransformer");
            WicAbi.IWICColorTransform transform = Require(_colorTransform, "color transform");

            Guid destinationFormat = WicPixelFormat32bppPbgra;
            nint framePointer = 0;
            nint sourceContextPointer = 0;
            nint destinationContextPointer = 0;
            int initializeHr = int.MinValue;
            try
            {
                framePointer = QueryOwnedInterface(Require(_frameObject, "frame owner"), IidWicBitmapSource);
                sourceContextPointer = QueryOwnedInterface(
                    Require(GetOwner(sourceContext), "source color context owner"), IidWicColorContext);
                destinationContextPointer = QueryOwnedInterface(
                    Require(_destinationColorContextObject, "destination color context owner"), IidWicColorContext);
                initializeHr = transform.Initialize(
                    framePointer,
                    sourceContextPointer,
                    destinationContextPointer,
                    &destinationFormat);
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
            if (!TryFinalRelease(
                    NativeWicOwnedInterfaceKind.ColorTransform,
                    ref _colorTransformObject,
                    ref _colorTransform,
                    out Exception? releaseFailure))
                QuarantineAndThrow(
                    "A rejected WIC color transform could not be released; its graph and source pin remain quarantined.",
                    releaseFailure!);
            return false;
        }

        private ComObject? GetOwner(WicAbi.IWICColorContext context)
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
            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateBitmapScaler(&native);
            if (native != 0)
                _scaler = Adopt<WicAbi.IWICBitmapScaler>(ref native, ref _scalerObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateBitmapScaler");
            WicAbi.IWICBitmapScaler scaler = Require(_scaler, "scaler");
            ComObject sourceOwner = _frameColorContextCount == 0
                ? Require(_preScaleConverterObject, "pre-scale format converter owner")
                : Require(_colorTransformObject, "color transform owner");
            nint sourcePointer = QueryOwnedInterface(sourceOwner, IidWicBitmapSource);
            try
            {
                hr = scaler.Initialize(
                    sourcePointer,
                    checked((uint)_rawOutputWidth),
                    checked((uint)_rawOutputHeight),
                    WicBitmapInterpolationModeFant);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICBitmapScaler.Initialize(PBGRA Fant)");
        }

        internal void CreatePremultipliedConverter()
        {
            WicAbi.IWICImagingFactory factory = Require(_factory, "factory");
            nint native = 0;
            int hr = factory.CreateFormatConverter(&native);
            if (native != 0)
                _converter = Adopt<WicAbi.IWICFormatConverter>(ref native, ref _converterObject);
            ThrowForHResult(hr, "IWICImagingFactory.CreateFormatConverter");
            WicAbi.IWICFormatConverter converter = Require(_converter, "converter");
            Guid targetFormat = WicPixelFormat32bppPbgra;
            ComObject sourceOwner = Require(_scalerObject, "scaler owner");
            nint sourcePointer = QueryOwnedInterface(sourceOwner, IidWicBitmapSource);
            try
            {
                hr = converter.Initialize(
                    sourcePointer,
                    &targetFormat,
                    WicBitmapDitherTypeNone,
                    0,
                    0,
                    WicBitmapPaletteTypeCustom);
            }
            finally
            {
                ReleaseOwnedPointer(ref sourcePointer);
            }
            ThrowForHResult(hr, "IWICFormatConverter.Initialize(final PBGRA)");
        }

        internal byte[] CopyPixels()
        {
            WicAbi.IWICFormatConverter converter = Require(_converter, "converter");
            int expectedByteCount = checked(checked(_rawOutputWidth * _rawOutputHeight) * 4);
            var pixels = new byte[expectedByteCount];
            fixed (byte* destination = pixels)
            {
                int hr = converter.CopyPixels(
                    rect: 0,
                    stride: checked((uint)_rawOutputWidth * 4),
                    bufferSize: checked((uint)pixels.Length),
                    buffer: destination);
                ThrowForHResult(hr, "IWICBitmapSource.CopyPixels(final PBGRA)");
            }
            return pixels;
        }

        internal NativeWicRetirementInfo GetRetirement() => new(
            _adoptedOwnedInterfaceCount,
            _successfullyFinalReleasedOwnedInterfaceCount,
            _outstandingQueryInterfaceReferences,
            _sourcePinReleased,
            _retired,
            IsRetainedByQuarantineRoot(),
            !_apartmentInitialized);

        public void Dispose()
        {
            if (_retired || _quarantined)
                return;

            if (_outstandingQueryInterfaceReferences != 0)
            {
                QuarantineAndThrow(
                    $"Cannot retire a WIC graph with {_outstandingQueryInterfaceReferences} outstanding QueryInterface references.");
            }

            if (!TryFinalRelease(NativeWicOwnedInterfaceKind.Converter, ref _converterObject, ref _converter, out Exception? failure) ||
                !TryFinalRelease(NativeWicOwnedInterfaceKind.Scaler, ref _scalerObject, ref _scaler, out failure) ||
                !TryFinalRelease(NativeWicOwnedInterfaceKind.ColorTransform, ref _colorTransformObject, ref _colorTransform, out failure) ||
                !TryFinalRelease(NativeWicOwnedInterfaceKind.PreScaleConverter, ref _preScaleConverterObject, ref _preScaleConverter, out failure) ||
                !TryFinalRelease(NativeWicOwnedInterfaceKind.DestinationColorContext, ref _destinationColorContextObject, ref _destinationColorContext, out failure))
            {
                QuarantineAndThrow("A unique downstream WIC interface could not be retired.", failure!);
            }

            for (int index = _ownedSourceColorContextCount - 1; index >= 0; index--)
            {
                if (!TryFinalRelease(
                        NativeWicOwnedInterfaceKind.SourceColorContext,
                        ref _sourceColorContextOwners[index],
                        ref _sourceColorContexts[index],
                        out failure))
                {
                    QuarantineAndThrow("A unique WIC source color context could not be retired.", failure!);
                }
            }

            if (!TryFinalRelease(NativeWicOwnedInterfaceKind.Frame, ref _frameObject, ref _frame, out failure) ||
                !TryFinalRelease(NativeWicOwnedInterfaceKind.Decoder, ref _decoderObject, ref _decoder, out failure) ||
                !TryFinalRelease(NativeWicOwnedInterfaceKind.Stream, ref _streamObject, ref _stream, out failure) ||
                !TryFinalRelease(NativeWicOwnedInterfaceKind.Factory, ref _factoryObject, ref _factory, out failure))
            {
                QuarantineAndThrow("A unique upstream WIC interface could not be retired.", failure!);
            }

            if (_sourcePin.IsAllocated)
            {
                _sourcePin.Free();
                _sourcePinReleased = true;
            }
            _retired = true;
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
            if (slot is not null)
                throw new InvalidOperationException("A WIC graph slot was assigned twice.");

            nint ownedResultReference = native;
            ComObject? unrecordedWrapper = null;
            try
            {
                unrecordedWrapper = (ComObject)_wrappers.GetOrCreateObjectForComInstance(
                    ownedResultReference,
                    CreateObjectFlags.UniqueInstance);
                slot = unrecordedWrapper;
                unrecordedWrapper = null;
                _adoptedOwnedInterfaceCount++;

                T typed = (T)(object)slot!;
                Marshal.Release(ownedResultReference);
                native = 0;
                return typed;
            }
            catch
            {
                if (unrecordedWrapper is not null)
                    unrecordedWrapper.FinalRelease();
                if (native != 0)
                {
                    Marshal.Release(native);
                    native = 0;
                }
                throw;
            }
        }

        private bool TryFinalRelease<T>(
            NativeWicOwnedInterfaceKind interfaceKind,
            ref ComObject? owner,
            ref T? typed,
            out Exception? failure)
            where T : class
        {
            failure = null;
            if (owner is null)
            {
                typed = null;
                return true;
            }

            try
            {
                InvokeTestHook(
                    NativeWicDecodeCheckpointPoint.BeforeOwnedInterfaceFinalRelease,
                    interfaceKind);
                owner.FinalRelease();
                owner = null;
                typed = null;
                _successfullyFinalReleasedOwnedInterfaceCount++;
                return true;
            }
            catch (Exception exception)
            {
                failure = exception;
                return false;
            }
        }

        private nint QueryOwnedInterface(ComObject owner, Guid iid)
        {
            bool found = ComWrappers.TryGetComInstance(owner, out nint instance);
            if (!found || instance == 0)
            {
                if (instance != 0)
                    Marshal.Release(instance);
                throw new InvalidOperationException("The unique WIC wrapper no longer exposes its COM instance.");
            }

            try
            {
                int hr = Marshal.QueryInterface(instance, in iid, out nint queried);
                if (hr < 0)
                {
                    if (queried != 0)
                        Marshal.Release(queried);
                    ThrowForHResult(hr, $"QueryInterface({iid})");
                }
                if (queried == 0)
                    throw new InvalidOperationException($"QueryInterface({iid}) returned a null interface pointer.");
                _outstandingQueryInterfaceReferences++;
                return queried;
            }
            finally
            {
                // TryGetComInstance itself returns an owned reference.
                Marshal.Release(instance);
            }
        }

        private void ReleaseOwnedPointer(ref nint pointer)
        {
            nint value = pointer;
            pointer = 0;
            if (value == 0)
                return;
            Marshal.Release(value);
            _outstandingQueryInterfaceReferences--;
        }

        private void QuarantineAndThrow(string message, Exception? failure = null)
        {
            _quarantined = true;
            _testHook = null;
            lock (QuarantineGate)
            {
                _nextQuarantinedGraph = QuarantinedGraphsHead;
                QuarantinedGraphsHead = this;
            }
            throw new InvalidOperationException(
                message + " Its dependency graph and pinned source are retained for the test process lifetime.",
                failure);
        }

        private bool IsRetainedByQuarantineRoot()
        {
            if (!_quarantined)
                return false;

            lock (QuarantineGate)
            {
                for (OwnedWicGraph? graph = QuarantinedGraphsHead;
                     graph is not null;
                     graph = graph._nextQuarantinedGraph)
                {
                    if (ReferenceEquals(graph, this))
                        return true;
                }
            }

            return false;
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
