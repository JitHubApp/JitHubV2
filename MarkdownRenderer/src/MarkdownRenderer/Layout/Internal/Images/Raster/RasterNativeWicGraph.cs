using System;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using MarkdownRenderer.Layout.Internal.RenderResources;

namespace MarkdownRenderer.Layout.Internal.Images.Raster;

/// <summary>
/// The owned WIC object graph of one synchronous decode: the factory,
/// stream, decoder, frame, color contexts and transform, scaler and format
/// converters, and the native pointers they hand out, released on dispose.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because every stage adopts its COM
/// objects and borrowed pointers into this graph's slots and pointer table,
/// and one Dispose must release them all in reverse construction order,
/// quarantining the whole graph if any release or outstanding pointer fails.
/// </remarks>
internal sealed unsafe class RasterNativeWicGraph : IDisposable
{
    private const int MaximumColorContexts = 16;
    private const int MaximumOwnedNativePointers = 64;
    private static readonly Guid IidWicColorContext = RasterNativeWicInterop.IidWicColorContext;
    private static readonly Guid IidWicBitmapSource = RasterNativeWicInterop.IidWicBitmapSource;

    private readonly byte[] _source;
    private readonly RasterNativeWicDecodeGeometry _plan;
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

    internal RasterNativeWicGraph(
        byte[] source,
        RasterNativeWicDecodeGeometry plan,
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
        int hr = RasterNativeWicInterop.CoInitializeEx(0, coInitialize: 0);
        if (hr < 0)
            ThrowForHResult(hr, "CoInitializeEx(COINIT_MULTITHREADED)");
        _apartmentInitialized = true;
    }

    internal void CreateFactory()
    {
        nint native = 0;
        Guid classId = RasterNativeWicInterop.ClsidWicImagingFactory2;
        Guid interfaceId = RasterNativeWicInterop.IidWicImagingFactory;
        int hr = RasterNativeWicInterop.CoCreateInstance(
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

        // No embedded profile is usable (for example a v2.1
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

    internal void CopyPixelsInBands(int copyBandRows, CancellationToken cancellationToken)
    {
        if (copyBandRows is < 1 or > RasterNativeWicDecodeOptions.MaximumCopyBandRows)
            throw new ArgumentOutOfRangeException(nameof(copyBandRows));

        RasterNativeWicInterop.IWICFormatConverter converter = Require(_converter, "converter");
        uint stride = checked((uint)_plan.DecodeWidth * 4);
        fixed (byte* destination = _rawPixels)
        {
            for (int y = 0; y < _plan.DecodeHeight;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rows = Math.Min(copyBandRows, _plan.DecodeHeight - y);
                var rectangle = new WicRect(0, y, _plan.DecodeWidth, rows);
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
    private struct WicRect
    {
        internal int X;
        internal int Y;
        internal int Width;
        internal int Height;

        internal WicRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

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
        RasterNativeWicInterop.CoUninitialize();
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
